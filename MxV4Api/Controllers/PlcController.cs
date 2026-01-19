using Microsoft.AspNetCore.Mvc;
using MxV4Api.Services;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // 【关键修改 5】: 全局禁用浏览器缓存
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class PlcController : ControllerBase
    {
        private readonly MxService _mxService;

        public PlcController(MxService mxService)
        {
            _mxService = mxService;
        }

        // 辅助方法：添加强制断开 Header
        private void ForceShortConnection()
        {
            // 告诉浏览器：请求完立刻断开 TCP，不要 Keep-Alive
            // 这能模拟 PHP/Curl 的行为，减轻服务器压力
            Response.Headers.Append("Connection", "close");
        }

        [HttpGet("read/{device}")]
        public async Task<IActionResult> Read(string device)
        {
            ForceShortConnection();
            try
            {
                int value = await _mxService.ReadDeviceAsync(device);
                return Ok(new { device, value, t = DateTime.Now.Ticks });
            }
            catch (Exception ex)
            {
                // 如果是队列满了，返回 503 Service Unavailable
                if (ex.Message.Contains("Busy")) return StatusCode(503, new { error = "Server Busy" });
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("read-string/{device}/{length}")]
        public async Task<IActionResult> ReadString(string device, int length)
        {
            ForceShortConnection();
            try
            {
                string result = await _mxService.ReadStringAsync(device, length);
                return Ok(new { device, length, result });
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Busy")) return StatusCode(503, new { error = "Server Busy" });
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("write")]
        public async Task<IActionResult> Write([FromBody] WriteRequest req)
        {
            ForceShortConnection();
            try
            {
                await _mxService.WriteDeviceAsync(req.Device, req.Value);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Busy")) return StatusCode(503, new { error = "Server Busy" });
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class WriteRequest
    {
        public string Device { get; set; }
        public int Value { get; set; }
    }
}