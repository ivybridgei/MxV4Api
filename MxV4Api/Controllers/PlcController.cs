using System.Text;
using Microsoft.AspNetCore.Mvc;
using MxV4Api.Services;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/plc")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class PlcController : ControllerBase
    {
        private readonly MxService _mxManager;
        private readonly ILogger<PlcController> _logger;

        public PlcController(MxService mxManager, ILogger<PlcController> logger)
        {
            _mxManager = mxManager;
            _logger = logger;
        }

        private StationAgent GetAgent(int stationId) => _mxManager.GetAgent(stationId);

        [HttpGet("{stationId}/read/{device}/{length?}")]
        public async Task<IActionResult> Read(int stationId, string device, int length = 1)
        {
            if (length < 1) length = 1;
            try
            {
                var agent = GetAgent(stationId);
                int[] data = await agent.ReadBlockAsync(device, length);

                return Ok(new
                {
                    station = stationId,
                    device,
                    length,
                    data = data,
                    t = DateTime.Now.Ticks
                });
            }
            catch (Exception ex)
            {
                return HandleError(ex, stationId, "Read", $"{device} (Len: {length})");
            }
        }

        [HttpGet("{stationId}/read-string/{device}/{length}")]
        public async Task<IActionResult> ReadString(int stationId, string device, int length)
        {
            if (length < 1) length = 1;
            try
            {
                var agent = GetAgent(stationId);
                int[] rawData = await agent.ReadBlockAsync(device, length);
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
                return HandleError(ex, stationId, "ReadString", $"{device} (Len: {length})");
            }
        }

        [HttpPost("{stationId}/write")]
        public async Task<IActionResult> Write(int stationId, [FromBody] WriteRequest req)
        {
            try
            {
                var agent = GetAgent(stationId);
                await agent.WriteDeviceAsync(req.Device, req.Value);
                return Ok(new { success = true, station = stationId });
            }
            catch (Exception ex)
            {
                return HandleError(ex, stationId, "Write", $"{req.Device} = {req.Value}");
            }
        }

        private IActionResult HandleError(Exception ex, int stationId, string action, string detail)
        {
            _logger.LogError($"[API请求失败] 站点: {stationId} | 动作: {action} | 目标: {detail} | 原因: {ex.Message}");

            if (ex.Message.Contains("Busy") || ex.Message.Contains("Queue Full") || ex.Message.Contains("处于断开状态"))
            {
                return StatusCode(503, new { error = "Station Offline or Busy", detail = ex.Message });
            }

            return StatusCode(500, new { error = ex.Message });
        }

        private string IntsToAscii(int[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder(data.Length * 2);
            foreach (int val in data)
            {
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