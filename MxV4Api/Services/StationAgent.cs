using System.Collections; // 用于 BitArray
using System.Collections.Concurrent;
using ActUtlTypeLib;
using System.Runtime.InteropServices;

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
            }, 500))
            {
                tcs.SetException(new Exception($"Station {_stationId} Busy (Queue Full)"));
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