using System.Collections.Concurrent;
using System.Text;
using ActUtlTypeLib; // 引用 COM 组件命名空间

namespace MxV4Api.Services
{
    public class MxService : IDisposable, IHostedService
    {
        // 任务队列：存放具体的 PLC 操作委托
        private readonly BlockingCollection<Action<ActUtlType>> _taskQueue;
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;
        private readonly ILogger<MxService> _logger;

        // 配置：站号
        private readonly int _stationNumber;

        public MxService(IConfiguration config, ILogger<MxService> logger)
        {
            _logger = logger;
            _taskQueue = new BlockingCollection<Action<ActUtlType>>();
            _cts = new CancellationTokenSource();

            // 从配置文件读取站号，默认 1
            _stationNumber = config.GetValue<int>("PlcSettings:StationNumber", 1);

            // ⚠️ 核心：创建 STA 线程
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "MX_V4_STA_Thread"
            };
            _workerThread.SetApartmentState(ApartmentState.STA); // 必须是 STA
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("启动 PLC 通信线程...");
            _workerThread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("停止 PLC 通信线程...");
            _taskQueue.CompleteAdding();
            _cts.Cancel();
            return Task.CompletedTask;
        }

        // ==========================================
        //  对外公开的异步 API 方法
        // ==========================================

        // 1. 读取单个字 (short)
        public Task<int> ReadDeviceAsync(string device)
        {
            var tcs = new TaskCompletionSource<int>();
            EnqueueTask(plc =>
            {
                int value;
                int ret = plc.GetDevice(device, out value);
                if (ret == 0) tcs.SetResult(value);
                else tcs.SetException(new Exception($"PLC Error 0x{ret:X}"));
            }, tcs);
            return tcs.Task;
        }

        // 2. 批量读取 (ReadBlock) - 返回 short[]
        public Task<short[]> ReadBlockAsync(string device, int size)
        {
            var tcs = new TaskCompletionSource<short[]>();
            EnqueueTask(plc =>
            {
                short[] data = new short[size];
                // 注意：MX v4 API 可能是 ReadDeviceBlock 或 ReadDeviceBlock2，根据你的旧代码是用 ReadDeviceBlock2
                int ret = plc.ReadDeviceBlock2(device, size, out data[0]);
                if (ret == 0) tcs.SetResult(data);
                else tcs.SetException(new Exception($"PLC Error 0x{ret:X}"));
            }, tcs);
            return tcs.Task;
        }

        // 3. 写入单个字
        public Task WriteDeviceAsync(string device, int value)
        {
            var tcs = new TaskCompletionSource<bool>();
            EnqueueTask(plc =>
            {
                int ret = plc.SetDevice(device, value);
                if (ret == 0) tcs.SetResult(true);
                else tcs.SetException(new Exception($"PLC Error 0x{ret:X}"));
            }, tcs);
            return tcs.Task;
        }

        // 4. 读取字符串 (复用你旧代码的逻辑)
        public async Task<string> ReadStringAsync(string device, int length)
        {
            short[] data = await ReadBlockAsync(device, length);
            return ShortsToAscii(data);
        }

        // ==========================================
        //  内部逻辑
        // ==========================================

        private void EnqueueTask<T>(Action<ActUtlType> action, TaskCompletionSource<T> tcs)
        {
            if (_cts.IsCancellationRequested || _taskQueue.IsAddingCompleted)
            {
                tcs.SetException(new InvalidOperationException("Service is stopping"));
                return;
            }

            _taskQueue.Add(plc =>
            {
                try
                {
                    action(plc);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }

        // 唯一的 STA 线程循环
        private void WorkerLoop()
        {
            ActUtlType plc = null;
            try
            {
                _logger.LogInformation($"[STA线程] 初始化 MX Component, 站号: {_stationNumber}");

                // COM 对象必须在 STA 线程内创建
                plc = new ActUtlType();
                plc.ActLogicalStationNumber = _stationNumber;

                // 简单的自动连接逻辑
                int ret = plc.Open();
                if (ret == 0)
                    _logger.LogInformation("[STA线程] PLC 连接成功");
                else
                    _logger.LogError($"[STA线程] PLC 连接失败: 0x{ret:X}");

                // 消费队列
                foreach (var action in _taskQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        // 可以在这里加一个简单的重连检测 (Watchdog)
                        // logic...

                        action(plc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[STA线程] 执行指令异常");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "[STA线程] 致命错误，线程退出");
            }
            finally
            {
                if (plc != null)
                {
                    plc.Close();
                    // 释放 COM
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(plc);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _workerThread?.Join(1000);
        }

        // 复用你原来的 ASCII 转换逻辑
        private string ShortsToAscii(short[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder(data.Length * 2);
            foreach (short s_val in data)
            {
                byte lowByte = (byte)(s_val & 0xFF);
                byte highByte = (byte)((s_val >> 8) & 0xFF);
                if (lowByte == 0) break;
                sb.Append((char)lowByte);
                if (highByte == 0) break;
                sb.Append((char)highByte);
            }
            return sb.ToString().Trim();
        }
    }
}