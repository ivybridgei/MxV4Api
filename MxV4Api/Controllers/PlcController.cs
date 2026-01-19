using Microsoft.AspNetCore.Mvc;
using MxV4Api.Services;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlcController : ControllerBase
    {
        private readonly MxService _mxService;

        public PlcController(MxService mxService)
        {
            _mxService = mxService;
        }

        // 测试读取单个字
        // GET: api/plc/read/D100
        [HttpGet("read/{device}")]
        public async Task<IActionResult> Read(string device)
        {
            try
            {
                int value = await _mxService.ReadDeviceAsync(device);
                return Ok(new { device, value, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // 测试读取字符串 (模拟读取 SN)
        // GET: api/plc/read-string/D4220/10
        [HttpGet("read-string/{device}/{length}")]
        public async Task<IActionResult> ReadString(string device, int length)
        {
            try
            {
                string result = await _mxService.ReadStringAsync(device, length);
                return Ok(new { device, length, result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // 写值
        // POST: api/plc/write
        [HttpPost("write")]
        public async Task<IActionResult> Write([FromBody] WriteRequest req)
        {
            try
            {
                await _mxService.WriteDeviceAsync(req.Device, req.Value);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
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