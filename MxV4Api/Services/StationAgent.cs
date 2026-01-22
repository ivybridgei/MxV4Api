using System.Collections.Concurrent;
using ActUtlTypeLib;

namespace MxV4Api.Services
{
    public class StationAgent : IDisposable
    {
        // 【关键修改 1】全局静态锁
        // 所有的 StationAgent 实例共享这把锁
        // 作用：确保同一时刻全系统只有一个线程在进行 COM 的创建或连接操作
        private static readonly SemaphoreSlim _globalSetupLock = new SemaphoreSlim(1, 1);

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
                // 【关键修改 2】初始化时申请全局锁
                // 必须锁住 new 和 Open，因为 MX v4 驱动在这两步不是线程安全的
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
                    // 【关键修改 3】初始化完成后立即释放锁
                    // 让其他站点可以开始初始化。此时本站点已经拿到句柄，后续读写不需要锁。
                    _globalSetupLock.Release();
                    _logger.LogInformation("初始化完成，释放锁。");
                }

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
                        _logger.LogWarning($"检测到异常 ({ex.Message})，准备重连...");
                        
                        // 【关键修改 4】重连逻辑也需要加锁
                        // 因为重连涉及到 Close 和 Open，这也属于驱动敏感操作
                        PerformSafeReconnect(ref plc);
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

        // 安全重连方法
        private void PerformSafeReconnect(ref ActUtlType plc)
        {
            try
            {
                // 关闭不需要锁，先关了再说
                try { plc.Close(); } catch { }
                
                // 冷却时间，避免在驱动崩溃时疯狂抢锁
                Thread.Sleep(2000);

                _logger.LogInformation("重连: 等待全局锁...");
                _globalSetupLock.Wait(); // 申请锁
                try
                {
                    _logger.LogInformation("重连: 获取锁成功，执行 Open...");
                    
                    // 甚至可以考虑在这里重新 new 一个对象，防止旧对象内部状态损坏
                    // 但通常 Close/Open 就够了。如果这里还是不行，可以尝试释放 COM 再 new。
                    
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