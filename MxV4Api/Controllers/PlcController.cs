using System.Text;
using Microsoft.AspNetCore.Mvc;
using MxV4Api.Services;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/plc")] // 基础路由
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class PlcController : ControllerBase
    {
        private readonly MxService _mxManager;

        public PlcController(MxService mxManager)
        {
            _mxManager = mxManager;
        }

        // 辅助：强制短连接
        private void ForceShortConnection() => Response.Headers.Append("Connection", "close");

        // 辅助：获取代理
        private StationAgent GetAgent(int stationId) => _mxManager.GetAgent(stationId);

        // ============================================================
        // 1. 读取数值 (支持单个或批量)
        // URL: GET /api/plc/{stationId}/read/{device}/{length?}
        // length 默认为 1
        // ============================================================
        [HttpGet("{stationId}/read/{device}/{length?}")]
        public async Task<IActionResult> Read(int stationId, string device, int length = 1)
        {
            ForceShortConnection();
            if (length < 1) length = 1;

            try
            {
                // 获取对应站点的代理（如果没有会自动创建线程）
                var agent = GetAgent(stationId);

                // 统一返回数组，方便前端处理
                int[] data = await agent.ReadBlockAsync(device, length);

                return Ok(new
                {
                    station = stationId,
                    device,
                    length,
                    data = data, // 数组格式 [123, 456, ...]
                    t = DateTime.Now.Ticks
                });
            }
            catch (Exception ex)
            {
                return HandleError(ex);
            }
        }

        // ============================================================
        // 2. 读取字符串 (自动 ASCII 解码)
        // URL: GET /api/plc/{stationId}/read-string/{device}/{length}
        // ============================================================
        [HttpGet("{stationId}/read-string/{device}/{length}")]
        public async Task<IActionResult> ReadString(int stationId, string device, int length)
        {
            ForceShortConnection();
            if (length < 1) length = 1;

            try
            {
                var agent = GetAgent(stationId);
                int[] rawData = await agent.ReadBlockAsync(device, length);

                // 转换逻辑：int[] -> string
                string result = IntsToAscii(rawData);

                return Ok(new
                {
                    station = stationId,
                    device,
                    length,
                    result,
                    t = DateTime.Now.Ticks
                });
            }
            catch (Exception ex)
            {
                return HandleError(ex);
            }
        }

        // ============================================================
        // 3. 写入数值
        // URL: POST /api/plc/{stationId}/write
        // Body: { "device": "D100", "value": 123 }
        // ============================================================
        [HttpPost("{stationId}/write")]
        public async Task<IActionResult> Write(int stationId, [FromBody] WriteRequest req)
        {
            ForceShortConnection();
            try
            {
                var agent = GetAgent(stationId);
                await agent.WriteDeviceAsync(req.Device, req.Value);
                return Ok(new { success = true, station = stationId });
            }
            catch (Exception ex)
            {
                return HandleError(ex);
            }
        }

        // 统一错误处理
        private IActionResult HandleError(Exception ex)
        {
            // 如果是队列满或超时
            if (ex.Message.Contains("Busy") || ex.Message.Contains("Queue Full"))
                return StatusCode(503, new { error = "Station Busy" });

            return StatusCode(500, new { error = ex.Message });
        }

        // 辅助：Int/Short 转 ASCII 字符串
        private string IntsToAscii(int[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder(data.Length * 2);
            foreach (int val in data)
            {
                // MX Component ReadDeviceBlock2 读出来的是 short 强转的 int
                // 低位在前，高位在后
                byte lowByte = (byte)(val & 0xFF);
                byte highByte = (byte)((val >> 8) & 0xFF);

                if (lowByte == 0) break;
                sb.Append((char)lowByte);

                if (highByte == 0) break;
                sb.Append((char)highByte);
            }
            return sb.ToString().Trim();
        }
    }

    public class WriteRequest
    {
        public string Device { get; set; }
        public int Value { get; set; }
    }
}