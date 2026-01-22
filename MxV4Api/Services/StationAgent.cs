using System.Collections.Concurrent;
using ActUtlTypeLib;
using System.Runtime.InteropServices; // 必须引用

namespace MxV4Api.Services
{
    public class StationAgent : IDisposable
    {
        private static readonly SemaphoreSlim _globalSetupLock = new SemaphoreSlim(1, 1);

        private readonly int _stationId;
        private readonly string _heartbeatDevice;
        private readonly int _heartbeatInterval;
        private readonly ILogger _logger;

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

        public Task<int[]> ReadBlockAsync(string device, int length)
        {
            var tcs = new TaskCompletionSource<int[]>(); // 默认不可取消，安全
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

        private void EnqueueTask<T>(Action<ActUtlType> action, TaskCompletionSource<T> tcs)
        {
            if (_cts.IsCancellationRequested || _taskQueue.IsAddingCompleted)
            {
                tcs.SetException(new InvalidOperationException("Station Stopping"));
                return;
            }

            if (!_taskQueue.TryAdd(plc =>
            {
                try
                {
                    action(plc);
                }
                catch (Exception ex)
                {
                    tcs.SetException(new Exception($"Station {_stationId}: {ex.Message}"));
                    throw; // 抛出异常以触发 WorkerLoop 的重连逻辑
                }
            }, 500))
            {
                tcs.SetException(new Exception($"Station {_stationId} Busy (Queue Full)"));
            }
        }

        private void WorkerLoop()
        {
            ActUtlType plc = null;
            try
            {
                Thread.Sleep(1000); // 启动延时，避让 Session 0 初始化高峰
                _logger.LogInformation("等待驱动资源锁...");
                _globalSetupLock.Wait();

                try
                {
                    _logger.LogInformation("获取锁成功，开始初始化 COM...");
                    plc = new ActUtlType();
                    plc.ActLogicalStationNumber = _stationId;

                    int ret = plc.Open();
                    if (ret != 0) _logger.LogError($"连接失败: 0x{ret:X}");
                    else _logger.LogInformation("连接成功");
                }
                finally
                {
                    _globalSetupLock.Release();
                    _logger.LogInformation("初始化完成，释放锁。");
                }

                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (_taskQueue.TryTake(out var action, _heartbeatInterval, _cts.Token))
                        {
                            action(plc);
                        }
                        else
                        {
                            int hbVal;
                            int hbRet = plc.GetDevice(_heartbeatDevice, out hbVal);
                            if (hbRet != 0) throw new Exception($"心跳失败 0x{hbRet:X}");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"检测到异常 ({ex.Message})，准备重连...");
                        PerformSafeReconnect(ref plc);
                    }
                }
            }
            finally
            {
                // 【资源释放增强】
                if (plc != null)
                {
                    try { plc.Close(); } catch { }
                    try
                    {
                        Marshal.FinalReleaseComObject(plc); // 使用 FinalRelease 确保计数归零
                    }
                    catch { }
                    plc = null;

                    // 强制 GC，处理非托管 COM 内存泄漏
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        private void PerformSafeReconnect(ref ActUtlType plc)
        {
            try
            {
                try { plc.Close(); } catch { }
                Thread.Sleep(2000);

                _logger.LogInformation("重连: 等待全局锁...");
                _globalSetupLock.Wait();
                try
                {
                    _logger.LogInformation("重连: 获取锁成功，执行 Open...");
                    int ret = plc.Open();
                    if (ret == 0) _logger.LogInformation("重连成功");
                    else _logger.LogError($"重连失败: 0x{ret:X}");
                }
                finally
                {
                    _globalSetupLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"重连过程发生错误: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _taskQueue.CompleteAdding();
            _cts.Cancel();
        }
    }
}