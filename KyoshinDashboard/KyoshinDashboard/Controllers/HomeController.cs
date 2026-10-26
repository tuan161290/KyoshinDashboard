using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace KyoshinDashboard.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            var states = MachineMonitorService.GetAllStates();
            return View(states);
        }

        public IActionResult IoMonitor()
        {
            var states = MachineMonitorService.GetAllStates();
            return View(states);
        }

        public IActionResult Error()
        {
            return View();
        }
    }
}
