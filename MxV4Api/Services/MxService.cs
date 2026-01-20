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

                _logger.LogInformation($"创建站点 {id} 代理 (心跳: {hbDevice}, {hbInterval}ms)");
                return new StationAgent(id, hbDevice, hbInterval, _loggerFactory);
            });
        }

        /// <summary>
        /// 启动时预热：自动连接配置的站点
        /// </summary>
        public void PreWarm()
        {
            var stations = _config.GetSection("PlcSettings:PreWarmStations").Get<int[]>();
            if (stations != null && stations.Length > 0)
            {
                _logger.LogInformation($"开始预热站点: {string.Join(", ", stations)}");
                foreach (var id in stations)
                {
                    // 调用 GetAgent 会自动触发线程创建和连接
                    GetAgent(id);
                }
            }
            else
            {
                _logger.LogInformation("未配置预热站点，跳过预热。");
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