using System.Collections.Concurrent;
using ActUtlTypeLib;

namespace MxV4Api.Services
{
    public class StationAgent : IDisposable
    {
        private readonly int _stationId;
        private readonly string _heartbeatDevice;
        private readonly int _heartbeatInterval;
        private readonly ILogger _logger;

        // 队列容量 10
        private readonly BlockingCollection<Action<ActUtlType>> _taskQueue;
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;

        public StationAgent(int stationId, string heartbeatDevice, int heartbeatInterval, ILoggerFactory loggerFactory)
        {
            _stationId = stationId;
            _heartbeatDevice = heartbeatDevice;
            _heartbeatInterval = heartbeatInterval;
            _logger = loggerFactory.CreateLogger($"Station_{stationId}");

            _taskQueue = new BlockingCollection<Action<ActUtlType>>(boundedCapacity: 10);
            _cts = new CancellationTokenSource();

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"STA_Station_{stationId}"
            };
            _workerThread.SetApartmentState(ApartmentState.STA);
            _workerThread.Start();
        }

        // ================== 公共方法 ==================

        public Task<int[]> ReadBlockAsync(string device, int length)
        {
            var tcs = new TaskCompletionSource<int[]>();
            EnqueueTask(plc =>
            {
                short[] data = new short[length];
                int ret = plc.ReadDeviceBlock2(device, length, out data[0]);
                if (ret == 0) tcs.SetResult(Array.ConvertAll(data, x => (int)x));
                else throw new Exception($"Error 0x{ret:X}");
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
                else throw new Exception($"Error 0x{ret:X}");
            }, tcs);
            return tcs.Task;
        }

        // ================== 队列封装 ==================

        private void EnqueueTask<T>(Action<ActUtlType> action, TaskCompletionSource<T> tcs)
        {
            if (_cts.IsCancellationRequested || _taskQueue.IsAddingCompleted)
            {
                tcs.SetException(new InvalidOperationException("Station Stopping"));
                return;
            }

            // 尝试入队 (500ms 超时)
            if (!_taskQueue.TryAdd(plc =>
            {
                try
                {
                    action(plc); // 执行业务逻辑
                }
                catch (Exception ex)
                {
                    // 1. 告诉前端 API 报错了
                    tcs.SetException(new Exception($"Station {_stationId}: {ex.Message}"));

                    // 2. 【关键】再次抛出异常，让 WorkerLoop 捕获到，从而触发重连
                    throw;
                }
            }, 500))
            {
                tcs.SetException(new Exception($"Station {_stationId} Busy (Queue Full)"));
            }
        }

        // ================== 核心循环 (含心跳与重连) ==================

        private void WorkerLoop()
        {
            ActUtlType plc = null;
            try
            {
                _logger.LogInformation("初始化 COM 组件...");
                plc = new ActUtlType();
                plc.ActLogicalStationNumber = _stationId;

                // 首次连接
                int ret = plc.Open();
                if (ret != 0) _logger.LogError($"连接失败: 0x{ret:X}");
                else _logger.LogInformation("连接成功");

                // 使用 While 循环配合 TryTake 实现空闲检测
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        // 尝试取任务，如果在 HeartbeatInterval 时间内取到了，就执行任务
                        // 如果超时没取到，TryTake 返回 false，进入 else 分支执行心跳
                        if (_taskQueue.TryTake(out var action, _heartbeatInterval, _cts.Token))
                        {
                            // 执行具体任务 (Read/Write)
                            // 如果任务内部 throw 了异常，会跳到下方的 catch
                            action(plc);
                        }
                        else
                        {
                            // === 空闲心跳逻辑 ===
                            // _logger.LogDebug($"发送心跳检测 ({_heartbeatDevice})...");
                            int hbVal;
                            int hbRet = plc.GetDevice(_heartbeatDevice, out hbVal);
                            if (hbRet != 0)
                            {
                                throw new Exception($"心跳失败 0x{hbRet:X}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // 正常退出
                    }
                    catch (Exception ex)
                    {
                        // === 遇错即重连策略 ===
                        _logger.LogWarning($"检测到异常 ({ex.Message})，正在执行重连...");

                        try
                        {
                            plc.Close();
                            // 稍微冷却一下，防止死循环刷屏
                            Thread.Sleep(500);

                            int reRet = plc.Open();
                            if (reRet == 0) _logger.LogInformation("重连成功");
                            else _logger.LogError($"重连失败: 0x{reRet:X}");
                        }
                        catch (Exception fatal)
                        {
                            _logger.LogError($"重连过程发生致命错误: {fatal.Message}");
                        }
                    }
                }
            }
            finally
            {
                if (plc != null)
                {
                    try { plc.Close(); } catch { }
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(plc);
                }
            }
        }

        public void Dispose()
        {
            _taskQueue.CompleteAdding();
            _cts.Cancel();
        }
    }
}