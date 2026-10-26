using KyoshinDashboard.Models;
using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Controllers
{
    public class HistoryController : Controller
    {
        private readonly KyoshinDbContext _db;

        public HistoryController(KyoshinDbContext db) => _db = db;

        public IActionResult Index(int? machineId, DateTime? date)
        {
            ViewBag.MachineId = machineId ?? 1;
            ViewBag.Date = (date ?? MachineMonitorService.GetShiftDay(DateTime.Now)).ToString("yyyy-MM-dd");
            return View();
        }
    }

    public class StatisticsController : Controller
    {
        private readonly KyoshinDbContext _db;

        public StatisticsController(KyoshinDbContext db) => _db = db;

        public IActionResult Index(DateTime? date, int? shift)
        {
            ViewBag.Date = (date ?? MachineMonitorService.GetShiftDay(DateTime.Now)).ToString("yyyy-MM-dd");
            ViewBag.Shift = shift ?? (int)MachineMonitorService.GetCurrentShift(DateTime.Now);
            return View();
        }
    }
}
