using System.Collections.Concurrent;

namespace MxV4Api.Services
{
    public class MxService : IDisposable
    {
        private readonly ConcurrentDictionary<int, StationAgent> _agents = new();
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MxService> _logger;
        private readonly IConfiguration _config;

        public MxService(ILoggerFactory loggerFactory, IConfiguration config)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<MxService>();
            _config = config;
        }

        /// <summary>
        /// 获取或创建代理
        /// </summary>
        public StationAgent GetAgent(int stationId)
        {
            return _agents.GetOrAdd(stationId, id =>
            {
                // 从配置读取心跳参数，读不到则用默认值
                string hbDevice = _config.GetValue<string>("PlcSettings:HeartbeatDevice") ?? "D0";
                int hbInterval = _config.GetValue<int>("PlcSettings:HeartbeatIntervalMs", 30000);

				_logger.LogDebug($"创建站点 {id} 代理");
				return new StationAgent(id, hbDevice, hbInterval, _loggerFactory);
            });
        }

        /// <summary>
        /// 启动时预热：自动连接配置的站点 (异步版本)
        /// </summary>
        // 【关键修改 5】改为 Async 方法，实现错峰启动
        public async Task PreWarmAsync()
        {
            var stations = _config.GetSection("PlcSettings:PreWarmStations").Get<int[]>();
            if (stations != null && stations.Length > 0)
            {
                _logger.LogInformation($"开始预热站点: {string.Join(", ", stations)}");
                foreach (var id in stations)
                {
                    _logger.LogInformation($"正在启动 Station {id}...");
                    
                    // 触发代理创建和线程启动
                    GetAgent(id);

                    // 【关键修改 6】强制等待 1 秒
                    // 给上一个站点充足的时间去 new ActUtlType 和 Open
                    // 配合 StationAgent 里的锁，双重保险
                    await Task.Delay(1000);
                }
                _logger.LogInformation("所有站点预热请求已下发。");
            }
            else
            {
                _logger.LogInformation("未配置预热站点。");
            }
        }

        public void Dispose()
        {
            foreach (var agent in _agents.Values)
            {
                agent.Dispose();
            }
            _agents.Clear();
        }
    }
}