using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Threading; // 需要 WPF 支持
using ActUtlTypeLib;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace MxV4Api.Services
{
    public class StationAgent : IDisposable
    {
        // 依然保留全局锁防 MX Component 底层冲突，但改为异步锁，防止死锁
        private static readonly SemaphoreSlim _globalSetupLock = new SemaphoreSlim(1, 1);

        private readonly int _stationId;
        private readonly string _heartbeatDevice;
        private readonly int _heartbeatInterval;
        private readonly ILogger _logger;

        private readonly Thread _workerThread;
        private Dispatcher _dispatcher; // 核心：替代 BlockingCollection
        private DispatcherTimer _heartbeatTimer; // 替代 TryTake 的超时参数

        private ActUtlType _plc;
        private bool _isDisposed;
        private int _continuousFailures = 0;

        // 用于等待线程初始化完成
        private readonly ManualResetEventSlim _initEvent = new(false);

        public StationAgent(int stationId, string heartbeatDevice, int heartbeatInterval, ILoggerFactory loggerFactory)
        {
            _stationId = stationId;
            _heartbeatDevice = heartbeatDevice;
            _heartbeatInterval = heartbeatInterval;
            _logger = loggerFactory.CreateLogger($"Station_{stationId}");

            // 1. 创建标准的 STA 线程
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"STA_Station_{stationId}"
            };
            _workerThread.SetApartmentState(ApartmentState.STA);
            _workerThread.Start();

            // 阻塞等待，直到后台线程的 Dispatcher 初始化成功
            _initEvent.Wait();
        }

        // ================== 核心：基于消息循环的工作线程 ==================
        private void WorkerLoop()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // 初始化 PLC 连接
            InitializePlcConnection();

            // 配置定时心跳机制（运行在当前 STA 线程的 Dispatcher 中，不阻塞）
            _heartbeatTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(_heartbeatInterval)
            };
            _heartbeatTimer.Tick += (s, e) => PerformHeartbeat();
            _heartbeatTimer.Start();

            // 通知外部构造函数，线程已就绪
            _initEvent.Set();

            // 🔥 核心魔法：启动 Windows 消息循环！
            // 这会挂起线程并处理事件，但不会阻塞底层 COM 消息，完美防止 ActiveX 死锁
            Dispatcher.Run();
        }

        private void InitializePlcConnection()
        {
            try
            {
                _logger.LogInformation($"[Station {_stationId}] 等待驱动资源锁...");
                _globalSetupLock.Wait();

                try
                {
                    _plc = new ActUtlType { ActLogicalStationNumber = _stationId };
                    int ret = _plc.Open();
                    if (ret != 0)
                    {
                        _logger.LogError($"[Station {_stationId}] 连接失败! ErrorCode: 0x{ret:X8}");
                        TriggerReconnect();
                    }
                    else
                    {
                        _logger.LogInformation($"[Station {_stationId}] 连接成功");
                        _continuousFailures = 0;
                    }
                }
                finally
                {
                    _globalSetupLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Station {_stationId}] 初始化异常: {ex.Message}");
            }
        }

        private void PerformHeartbeat()
        {
            if (_plc == null) return;
            try
            {
                int hbRet = _plc.GetDevice(_heartbeatDevice, out int hbVal);
                if (hbRet != 0) throw new Exception($"ErrorCode 0x{hbRet:X8}");
                _continuousFailures = 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Station {_stationId}] 心跳异常 ({ex.Message})，已触发断线重连机制...");
                TriggerReconnect();
            }
        }

        // ================== 异步任务派发 (完美替代 EnqueueTask) ==================
        private Task<T> DispatchTaskAsync<T>(Func<ActUtlType, T> action)
        {
            if (_isDisposed || _dispatcher.HasShutdownStarted)
                return Task.FromException<T>(new ObjectDisposedException($"[Station {_stationId}] 实例已释放/销毁"));

            var tcs = new TaskCompletionSource<T>();

            _dispatcher.InvokeAsync(() =>
            {
                if (_plc == null)
                {
                    tcs.SetException(new Exception($"[Station {_stationId}] 处于断开状态，后台正在努力重连中..."));
                    return;
                }

                try
                {
                    T result = action(_plc);
                    tcs.SetResult(result);
                    _continuousFailures = 0;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[Station {_stationId}] 通讯执行异常: {ex.Message}");
                    tcs.SetException(ex);
                    TriggerReconnect();
                }
            }, DispatcherPriority.Normal);

            return tcs.Task;
        }

        // ================== 业务方法 ==================
        public Task<int[]> ReadBlockAsync(string deviceStr, int length)
        {
            var devInfo = DeviceUtility.ParseDevice(deviceStr);

            if (DeviceUtility.IsBitDevice(devInfo.Head))
            {
                // 位元件逻辑，与你原本代码一致，仅改为通过 DispatchTaskAsync 调度
                return DispatchTaskAsync(plc =>
                {
                    int offset = devInfo.Address % 16;
                    int alignedAddress = devInfo.Address - offset;
                    int totalBitsNeeded = offset + length;
                    int wordsToRead = (int)Math.Ceiling(totalBitsNeeded / 16.0);
                    string alignedDeviceStr = DeviceUtility.BuildDeviceStr(devInfo.Head, alignedAddress, devInfo.IsHex);

                    short[] rawWords = new short[wordsToRead];
                    int ret = plc.ReadDeviceBlock2(alignedDeviceStr, wordsToRead, out rawWords[0]);
                    if (ret != 0) throw new Exception($"ReadBit Error 0x{ret:X}");

                    var resultArray = new int[length];
                    int resultIndex = 0;
                    for (int w = 0; w < wordsToRead; w++)
                    {
                        var bits = new BitArray(BitConverter.GetBytes(rawWords[w]));
                        for (int b = 0; b < 16; b++)
                        {
                            int currentBitGlobalIndex = (w * 16) + b;
                            if (currentBitGlobalIndex >= offset && currentBitGlobalIndex < (offset + length))
                            {
                                resultArray[resultIndex] = bits[b] ? 1 : 0;
                                resultIndex++;
                            }
                        }
                    }
                    return resultArray;
                });
            }
            else
            {
                // 字元件逻辑
                return DispatchTaskAsync(plc =>
                {
                    short[] data = new short[length];
                    int ret = plc.ReadDeviceBlock2(deviceStr, length, out data[0]);
                    if (ret != 0) throw new Exception($"ReadWord Error 0x{ret:X}");
                    return Array.ConvertAll(data, x => (int)x);
                });
            }
        }

        public Task WriteDeviceAsync(string device, int value)
        {
            return DispatchTaskAsync(plc =>
            {
                int ret = plc.SetDevice(device, value);
                if (ret != 0) throw new Exception($"Write Error 0x{ret:X}");
                return true;
            });
        }

        // ================== 安全重连与资源释放 ==================
        private void TriggerReconnect()
        {
            _continuousFailures++;
            if (_continuousFailures >= 5)
            {
                _logger.LogCritical($"[Station {_stationId}] 连续失败 {_continuousFailures} 次！判断为底层 COM 假死，强制丢弃对象执行硬复位。");
            }

            // 彻底释放旧的 COM 对象
            if (_plc != null)
            {
                try { _plc.Close(); } catch { }
                try { Marshal.FinalReleaseComObject(_plc); } catch { }
                _plc = null;
            }

            // 延迟重试，通过 DispatcherTimer 避免挂起消息循环
            var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            delayTimer.Tick += (s, e) =>
            {
                delayTimer.Stop();
                InitializePlcConnection(); // 重新走一次锁与初始化逻辑
            };
            delayTimer.Start();
        }

        public void Dispose()
        {
            _isDisposed = true;
            if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
            {
                // 必须在 STA 线程中释放 COM 对象
                _dispatcher.Invoke(() =>
                {
                    _heartbeatTimer?.Stop();
                    if (_plc != null)
                    {
                        try { _plc.Close(); } catch { }
                        try { Marshal.FinalReleaseComObject(_plc); } catch { }
                        _plc = null;
                    }
                    // 终止消息循环，安全退出后台线程
                    _dispatcher.InvokeShutdown();
                });
            }

            _workerThread.Join(2000);
            _initEvent.Dispose();
        }
    }
}