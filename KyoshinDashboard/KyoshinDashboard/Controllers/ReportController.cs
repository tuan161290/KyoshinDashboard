using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace KyoshinDashboard.Controllers
{
    /// <summary>
    /// Report page (as "Báo cáo" in the original Kyoshin app): date range, several machines, chosen shifts,
    /// one row per production day with Run/Wait/Stop % and minutes, Off minutes, break time and average.
    /// Data from GET /api/machineapi/report.
    /// </summary>
    public class ReportController : Controller
    {
        public IActionResult Index()
        {
            var today = MachineMonitorService.GetShiftDay(DateTime.Now);
            ViewBag.From = today.AddDays(-6).ToString("yyyy-MM-dd");
            ViewBag.To = today.ToString("yyyy-MM-dd");
            return View();
        }
    }
}
