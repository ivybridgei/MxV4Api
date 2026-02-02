using System.Collections; // 用于 BitArray
using System.Collections.Concurrent;
using ActUtlTypeLib;
using System.Runtime.InteropServices;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

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

        // 【新增】连续失败计数器
        private int _continuousFailures = 0;

        public StationAgent(int stationId, string heartbeatDevice, int heartbeatInterval, ILoggerFactory loggerFactory)
        {
            _stationId = stationId;
            _heartbeatDevice = heartbeatDevice;
            _heartbeatInterval = heartbeatInterval;
            _logger = loggerFactory.CreateLogger($"Station_{stationId}");

            _taskQueue = new BlockingCollection<Action<ActUtlType>>(boundedCapacity: 100);
            _cts = new CancellationTokenSource();

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"STA_Station_{stationId}"
            };
            _workerThread.SetApartmentState(ApartmentState.STA);
            _workerThread.Start();
        }

        // =============================================================
        //  读取入口：自动区分 位元件 vs 字元件
        // =============================================================
        public Task<int[]> ReadBlockAsync(string deviceStr, int length)
        {
            var tcs = new TaskCompletionSource<int[]>();

            // 1. 解析地址信息
            var devInfo = DeviceUtility.ParseDevice(deviceStr);

            // 2. 判断是否为位元件 (M, X, Y...)
            if (DeviceUtility.IsBitDevice(devInfo.Head))
            {
                // ==== 位元件特殊处理逻辑 ====
                EnqueueTask(plc =>
                {
                    // A. 计算对齐
                    // MX Component 要求批量读位时，建议以 16 的倍数起始，否则可能报错或错位
                    // 例如: 请求 M3, 长度 1
                    // 实际地址 = 3
                    // 对齐地址 = 3 - (3 % 16) = 0 (即 M0)
                    // 偏移量(Offset) = 3
                    int offset = devInfo.Address % 16;
                    int alignedAddress = devInfo.Address - offset;

                    // B. 计算需要读取的“字(Word)”数量
                    // 我们需要的总位数为: 偏移量 + 请求长度
                    // 例如: 偏移3 + 长度1 = 需要覆盖索引 0~3 的范围，至少读1个字(16位)
                    // 例如: 偏移15 + 长度2 = 需要覆盖 15,16，跨越了两个字，需要读2个字
                    int totalBitsNeeded = offset + length;
                    int wordsToRead = (int)Math.Ceiling(totalBitsNeeded / 16.0);

                    // C. 构建对齐后的读取地址 (如 M0)
                    string alignedDeviceStr = DeviceUtility.BuildDeviceStr(devInfo.Head, alignedAddress, devInfo.IsHex);

                    // D. 执行读取 (读回来的是 short 数组，每个 short 包含 16 个位)
                    short[] rawWords = new short[wordsToRead];
                    int ret = plc.ReadDeviceBlock2(alignedDeviceStr, wordsToRead, out rawWords[0]);

                    if (ret != 0) throw new Exception($"ReadBit Error 0x{ret:X}");

                    // E. 数据解包 (Unpacking) & 裁剪 (Slicing)
                    var resultArray = new int[length];
                    int resultIndex = 0;

                    // 遍历读取到的每一个字
                    for (int w = 0; w < wordsToRead; w++)
                    {
                        // 获取当前字的 16 个位
                        // 注意：BitArray 转换后 index 0 是低位 (Bit 0)
                        var bits = new BitArray(BitConverter.GetBytes(rawWords[w]));

                        for (int b = 0; b < 16; b++)
                        {
                            // 计算当前位的全局索引 (基于 alignedAddress)
                            int currentBitGlobalIndex = (w * 16) + b;

                            // 如果当前位在我们需要的范围内 (Offset ~ Offset + Length)
                            if (currentBitGlobalIndex >= offset && currentBitGlobalIndex < (offset + length))
                            {
                                resultArray[resultIndex] = bits[b] ? 1 : 0;
                                resultIndex++;
                            }
                        }
                    }

                    tcs.SetResult(resultArray);

                }, tcs);
            }
            else
            {
                // ==== 字元件 (D, W, ZR...) 原有逻辑 ====
                EnqueueTask(plc =>
                {
                    short[] data = new short[length];
                    int ret = plc.ReadDeviceBlock2(deviceStr, length, out data[0]);

                    if (ret == 0)
                        tcs.SetResult(Array.ConvertAll(data, x => (int)x));
                    else
                        throw new Exception($"ReadWord Error 0x{ret:X}");
                }, tcs);
            }

            return tcs.Task;
        }

        // ... WriteDeviceAsync 和 EnqueueTask 保持不变 (略) ...
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
                try { action(plc); } catch (Exception ex) { tcs.SetException(new Exception($"Station {_stationId}: {ex.Message}")); throw; }
            }, 3000))
            {
                tcs.SetException(new Exception($"Station {_stationId} Busy (Queue Full > 100)"));
            }
        }

        // ================== WorkerLoop (包含资源释放优化) ==================
        private void WorkerLoop()
        {
            ActUtlType plc = null;
            try
            {
                Thread.Sleep(1000);
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
                        if (plc == null)
                        {
                            _logger.LogWarning("检测到 PLC 实例丢失，尝试重建...");
                            PerformSafeReconnect(ref plc);

                            if (plc == null)
                            {
                                Thread.Sleep(5000);
                                continue;
                            }
                        }

                        if (_taskQueue.TryTake(out var action, _heartbeatInterval, _cts.Token))
                        {
                            action(plc);
                        }
                        else
                        {
                        // === 空闲心跳逻辑 ===
                        // 【优化】心跳成功不记录日志，避免刷屏
                        int hbVal;
                        int hbRet = plc.GetDevice(_heartbeatDevice, out hbVal);
                        if (hbRet != 0) throw new Exception($"心跳失败 0x{hbRet:X}");

                        // 心跳成功，重置失败计数
                        _continuousFailures = 0;

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
                if (plc != null)
                {
                    try { plc.Close(); } catch { }
                    try { Marshal.FinalReleaseComObject(plc); } catch { }
                    plc = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        private void PerformSafeReconnect(ref ActUtlType plc)
        {
            try
            {
                _logger.LogWarning("开始执行深度重连流程...");

                // 【新增】每进入一次重连流程，计数器+1
                // 只有在心跳成功或 Open 成功 (视策略而定) 时才清零
                // 这里我们选择在 Open 成功时清零，或者在 WorkerLoop 心跳成功时清零
                // 考虑到 0xF0000003 往往连 Open 都过不去，我们在这里累加
                _continuousFailures++;

                // 如果连续失败次数过多，说明当前进程的 COM 环境已彻底损坏 (僵尸进程)
                // 必须自杀，让守护进程 (Guardian) 重启我们
                if (_continuousFailures >= 5)
                {
                    _logger.LogCritical($"连续失败次数 ({_continuousFailures}) 达到阈值。判定 COM 组件处于不可恢复的死锁状态。");
                    _logger.LogCritical(">>> 正在执行进程自杀 (Suicide)，等待守护进程重启...");
                    
                    // 确保日志落盘
                    Serilog.Log.CloseAndFlush();

                    
                    // 强制退出码 1，告知 Guardian 发生了错误
                    Environment.Exit(1);
                }

                if (plc != null)
                {
                    try
                    {
                        plc.Close();
                    }
                    catch { }

                    try
                    {
                        Marshal.FinalReleaseComObject(plc);
                    }
                    catch { }

                    plc = null;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                Thread.Sleep(3000);

                _logger.LogInformation("重连: 等待全局锁...");
                _globalSetupLock.Wait();
                try
                {
                    _logger.LogInformation("重连: 获取锁成功，正在创建全新 COM 实例...");

                    plc = new ActUtlType();
                    plc.ActLogicalStationNumber = _stationId;

                    int ret = plc.Open();
                    if (ret == 0)
                    {
                        _logger.LogInformation("✅ 重连成功 (新实例)");
                        // 重连成功，重置计数器
                        _continuousFailures = 0;
                    }
                    else
                    {
                        _logger.LogError($"❌ 重连失败: 0x{ret:X}");

                        try { Marshal.FinalReleaseComObject(plc); } catch { }
                        plc = null;
                        
                        // 注意：这里 Open 失败也会导致下一次循环再次调用 PerformSafeReconnect，从而增加计数
                    }
                }
                finally
                {
                    _globalSetupLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"重连过程发生致命错误: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _taskQueue.CompleteAdding();
            _cts.Cancel();
        }
    }
}