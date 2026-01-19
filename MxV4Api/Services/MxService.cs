using System.Collections.Concurrent;
using System.Text;
using ActUtlTypeLib;

namespace MxV4Api.Services
{
    public class MxService : IDisposable, IHostedService
    {
        // 【关键修改 2】: 设置队列容量上限 (例如 10)。
        // 如果队列满了，说明 PLC 处理不过来，直接拒绝新请求，防止卡死。
        private readonly BlockingCollection<Action<ActUtlType>> _taskQueue;

        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;
        private readonly ILogger<MxService> _logger;
        private readonly int _stationNumber;

        public MxService(IConfiguration config, ILogger<MxService> logger)
        {
            _logger = logger;

            // 初始化限制容量的队列
            _taskQueue = new BlockingCollection<Action<ActUtlType>>(boundedCapacity: 10);

            _cts = new CancellationTokenSource();
            _stationNumber = config.GetValue<int>("PlcSettings:StationNumber", 1);

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "MX_V4_STA_Thread"
            };
            _workerThread.SetApartmentState(ApartmentState.STA);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _workerThread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _taskQueue.CompleteAdding();
            _cts.Cancel();
            return Task.CompletedTask;
        }

        // 通用入队逻辑
        private void EnqueueTask<T>(Action<ActUtlType> action, TaskCompletionSource<T> tcs)
        {
            if (_cts.IsCancellationRequested || _taskQueue.IsAddingCompleted)
            {
                tcs.SetException(new InvalidOperationException("Service Stopping"));
                return;
            }

            // 【关键修改 3】: 使用 TryAdd 而不是 Add
            // 如果队列满了(正在忙)，立即返回错误，不要让浏览器傻等
            if (!_taskQueue.TryAdd(plc =>
            {
                try { action(plc); } catch (Exception ex) { tcs.SetException(ex); }
            }, 500)) // 尝试等待 500ms 入队，进不去就放弃
            {
                tcs.SetException(new Exception("Server Busy (PLC Queue Full) - Please slow down"));
            }
        }

        // 对外 API (保持不变，只是内部调用了新的 EnqueueTask)
        public Task<int> ReadDeviceAsync(string device)
        {
            var tcs = new TaskCompletionSource<int>();
            EnqueueTask(plc =>
            {
                int value;
                int ret = plc.GetDevice(device, out value);
                if (ret == 0) tcs.SetResult(value);
                else throw new Exception($"PLC Read Error 0x{ret:X}"); // 抛出异常触发重连判断
            }, tcs);
            return tcs.Task;
        }

        public Task<short[]> ReadBlockAsync(string device, int size)
        {
            var tcs = new TaskCompletionSource<short[]>();
            EnqueueTask(plc =>
            {
                short[] data = new short[size];
                int ret = plc.ReadDeviceBlock2(device, size, out data[0]);
                if (ret == 0) tcs.SetResult(data);
                else throw new Exception($"PLC BlockRead Error 0x{ret:X}");
            }, tcs);
            return tcs.Task;
        }

        public Task WriteDeviceAsync(string device, int value)
        {
            var tcs = new TaskCompletionSource<bool>();
            EnqueueTask(plc =>
            {
                int ret = plc.SetDevice(device, value);
                if (ret == 0) tcs.SetResult(true);
                else throw new Exception($"PLC Write Error 0x{ret:X}");
            }, tcs);
            return tcs.Task;
        }

        public async Task<string> ReadStringAsync(string device, int length)
        {
            short[] data = await ReadBlockAsync(device, length);
            return ShortsToAscii(data);
        }

        // ================= STA 线程循环 (包含重连逻辑) =================
        private void WorkerLoop()
        {
            ActUtlType plc = null;
            try
            {
                plc = new ActUtlType();
                plc.ActLogicalStationNumber = _stationNumber;

                // 初始连接
                int ret = plc.Open();
                if (ret != 0) _logger.LogError($"初始连接失败: 0x{ret:X}");

                foreach (var action in _taskQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        action(plc);
                    }
                    catch (Exception ex)
                    {
                        // 【关键修改 4】: 简单的自动重连机制
                        // 如果捕获到 PLC 异常，尝试重置连接
                        _logger.LogWarning($"PLC 操作异常: {ex.Message}，尝试重置连接...");
                        try
                        {
                            plc.Close();
                            Thread.Sleep(500); // 冷却一下
                            plc.Open();
                        }
                        catch (Exception reEx)
                        {
                            _logger.LogError($"重连失败: {reEx.Message}");
                        }
                    }
                }
            }
            finally
            {
                if (plc != null)
                {
                    plc.Close();
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(plc);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _workerThread?.Join(1000);
        }

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