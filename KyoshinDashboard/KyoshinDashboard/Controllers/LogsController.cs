using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace KyoshinDashboard.Controllers
{
    /// <summary>Nhật ký hệ thống: the Kyoshin entries of the Windows event log and log files (SystemLogService).</summary>
    public class LogsController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        /// <summary>GET /api/logs?hours=24 — entries of the last hours (newest first) and the log files.</summary>
        [HttpGet("/api/logs")]
        public IActionResult Get(int hours = 24)
        {
            hours = Math.Clamp(hours, 1, 24 * 90);
            var (entries, truncated) = SystemLogService.Read(TimeSpan.FromHours(hours));
            return Json(new
            {
                available = SystemLogService.Available,
                truncated,
                maxEntries = SystemLogService.MAX_ENTRIES,
                entries = entries.Select(e => new
                {
                    time = e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    source = e.Source,
                    level = e.Level,
                    category = e.Category,
                    message = e.Message
                }),
                files = SystemLogService.ReadFiles()
            });
        }
    }
}
