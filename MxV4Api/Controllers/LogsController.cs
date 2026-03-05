using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.IO;
using System.Collections.Generic;

namespace MxV4Api.Controllers
{
    [ApiController]
    [Route("api/logs")]
    public class LogsController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetTodayLogs([FromQuery] int lines = 100)
        {
            try
            {
                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"log-{dateStr}.txt");

                if (!System.IO.File.Exists(logPath))
                {
                    return Content($"[提示] 今日日志文件尚未生成: {logPath}");
                }

                var fileInfo = new FileInfo(logPath);
                // 【安全检查】如果日志大于 20MB，直接提示太大，防止 OOM
                if (fileInfo.Length > 20 * 1024 * 1024)
                {
                    return Content($"[警告] 日志文件过大 ({fileInfo.Length / 1024 / 1024} MB)，请直接去服务器 logs 目录查看，避免浏览器崩溃。");
                }

                // 使用队列只保留最后 N 行，内存占用极小
                var queue = new Queue<string>(lines);

                // FileShare.ReadWrite 允许在写入时读取
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (queue.Count >= lines) queue.Dequeue();
                        queue.Enqueue(line);
                    }
                }

                string content = string.Join(Environment.NewLine, queue);
                return Content(content, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"读取日志出错: {ex.Message}");
            }
        }
    }
}