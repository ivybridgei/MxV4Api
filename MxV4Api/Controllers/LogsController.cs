using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/logs")]
    public class LogsController : ControllerBase
    {
        // 获取今天的日志内容
        // GET: api/logs
        [HttpGet]
        public IActionResult GetTodayLogs([FromQuery] int lines = 100)
        {
            try
            {
                // 1. 计算今天的日志文件名
                // Serilog 配置为 logs/log-yyyyMMdd.txt
                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"log-{dateStr}.txt");

                if (!System.IO.File.Exists(logPath))
                {
                    return Content($"[提示] 今日日志文件尚未生成: {logPath}");
                }

                // 2. 读取文件（使用 FileShare.ReadWrite 防止文件被占用无法读取）
                var logLines = new List<string>();
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        logLines.Add(line);
                    }
                }

                // 3. 只取最后 N 行
                int count = logLines.Count;
                var recentLogs = logLines.Skip(Math.Max(0, count - lines)).ToList();

                // 4. 拼接返回
                string content = string.Join(Environment.NewLine, recentLogs);
                return Content(content, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"读取日志出错: {ex.Message}");
            }
        }
    }
}