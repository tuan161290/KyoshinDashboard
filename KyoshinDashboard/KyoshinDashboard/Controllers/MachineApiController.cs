using System.Globalization;
using KyoshinDashboard.Models;
using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MachineApiController : ControllerBase
    {
        private readonly KyoshinDbContext _db;

        public MachineApiController(KyoshinDbContext db) => _db = db;

        /// <summary>GET /api/machineapi/states - Current states of all machines</summary>
        [HttpGet("states")]
        public IActionResult GetStates()
        {
            var states = MachineMonitorService.GetAllStates();
            return Ok(states.Select(MachineMonitorService.ToClientPayload));
        }

        /// <summary>GET /api/machineapi/history?machineId=1&date=2026-03-02</summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory(int machineId, DateTime? date)
        {
            var targetDate = date ?? DateTime.Today;
            var startOfDay = targetDate.Date;
            var endOfDay = startOfDay.AddDays(1);

            var logs = await _db.StatusLogs
                .Where(l => l.MachineId == machineId && l.StartTime >= startOfDay && l.StartTime < endOfDay)
                .OrderBy(l => l.StartTime)
                .Select(l => new
                {
                    Status = l.Status.ToString(),
                    StartTime = l.StartTime.ToString("HH:mm:ss"),
                    EndTime = l.EndTime.HasValue ? l.EndTime.Value.ToString("HH:mm:ss") : "Now",
                    l.DurationSeconds,
                    Shift = l.Shift.ToString()
                })
                .ToListAsync();

            return Ok(logs);
        }

        /// <summary>
        /// GET /api/machineapi/chart-data?machineId=1&from=2026-03-02T06:00:00&to=2026-03-03T06:00:00 (factory local time),
        /// or &date=2026-03-02 for that calendar day. Segment start/end are full local timestamps ("yyyy-MM-ddTHH:mm:ss"),
        /// so a range across midnight (shift 3) comes out in the right order.
        /// </summary>
        [HttpGet("chart-data")]
        public async Task<IActionResult> GetChartData(int machineId, DateTime? date, DateTime? from, DateTime? to)
        {
            var rangeStart = from ?? (date ?? DateTime.Today).Date;
            var rangeEnd = to ?? rangeStart.AddDays(1);
            if (rangeEnd <= rangeStart || rangeEnd - rangeStart > TimeSpan.FromDays(7))
                return BadRequest("'to' must be after 'from', at most 7 days");

            var logs = await _db.StatusLogs
                .Where(l => l.MachineId == machineId && l.StartTime < rangeEnd && (l.EndTime == null || l.EndTime > rangeStart))
                .OrderBy(l => l.StartTime)
                .ToListAsync();

            // Status timeline data — clamp to the range, cap open-ended logs
            var statusTimeline = new List<object>();
            for (int idx = 0; idx < logs.Count; idx++)
            {
                var l = logs[idx];
                var clampedStart = l.StartTime < rangeStart ? rangeStart : l.StartTime;
                var endTime = l.EndTime ?? (idx < logs.Count - 1 ? logs[idx + 1].StartTime : DateTime.Now);
                var clampedEnd = endTime > rangeEnd ? rangeEnd : endTime;
                if (clampedEnd <= clampedStart) continue;
                statusTimeline.Add(new
                {
                    start = FormatChartTime(clampedStart),
                    end = FormatChartTime(clampedEnd),
                    status = l.Status.ToString(),
                    color = l.Status switch
                    {
                        MachineStatus.Green => "#22c55e",
                        MachineStatus.Yellow => "#eab308",
                        MachineStatus.Red => "#ef4444",
                        _ => "#6b7280"
                    },
                    duration = (clampedEnd - clampedStart).TotalSeconds
                });
            }

            // Error ON/OFF timelines from separate ErrorLogs table
            var errorLogs = await _db.ErrorLogs
                .Where(e => e.MachineId == machineId && e.StartTime < rangeEnd && (e.EndTime == null || e.EndTime > rangeStart))
                .OrderBy(e => e.StartTime)
                .ToListAsync();

            var errorTimeline = new List<object>();
            foreach (var e in errorLogs)
            {
                var clampedStart = e.StartTime < rangeStart ? rangeStart : e.StartTime;
                var endTime = e.EndTime ?? DateTime.Now;
                var clampedEnd = endTime > rangeEnd ? rangeEnd : endTime;

                // Skip very short error events (< 3 seconds) to reduce chart noise
                if ((clampedEnd - clampedStart).TotalSeconds < 3) continue;

                errorTimeline.Add(new
                {
                    start = FormatChartTime(clampedStart),
                    end = FormatChartTime(clampedEnd),
                    errorType = e.ErrorType.ToString(),
                    outError = e.ErrorType == ErrorType.Out ? 1 : 0,
                    trashError = e.ErrorType == ErrorType.Trash ? 1 : 0,
                    otherError = e.ErrorType == ErrorType.Other ? 1 : 0
                });
            }

            return Ok(new { statusTimeline, errorTimeline });
        }

        // Factory local time without an offset: the browser reads it as local time
        private static string FormatChartTime(DateTime time) =>
            time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>GET /api/machineapi/shift-stats?date=2026-03-02&shift=1</summary>
        [HttpGet("shift-stats")]
        public async Task<IActionResult> GetShiftStats(DateTime? date, int? shift)
        {
            var targetDate = date ?? MachineMonitorService.GetShiftDay(DateTime.Now);
            var shiftType = shift.HasValue ? (ShiftType)shift.Value : MachineMonitorService.GetCurrentShift(DateTime.Now);

            // Calculate shift time boundaries
            var (shiftStart, shiftEnd) = MachineMonitorService.GetShiftBoundaries(targetDate, shiftType);

            var statusLogs = await _db.StatusLogs
                .Where(l => l.StartTime < shiftEnd && (l.EndTime == null || l.EndTime > shiftStart))
                .ToListAsync();

            var errorLogs = await _db.ErrorLogs
                .Where(e => e.StartTime < shiftEnd && (e.EndTime == null || e.EndTime > shiftStart))
                .ToListAsync();

            var stats = Enumerable.Range(1, MachineMonitorService.TOTAL_MACHINES).Select(machineId =>
            {
                var machineLogs = statusLogs.Where(l => l.MachineId == machineId).ToList();
                double greenSec = 0, yellowSec = 0, redSec = 0, offSec = 0;

                foreach (var log in machineLogs)
                {
                    var start = log.StartTime < shiftStart ? shiftStart : log.StartTime;
                    var end = (log.EndTime ?? DateTime.Now) > shiftEnd ? shiftEnd : (log.EndTime ?? DateTime.Now);
                    var dur = (end - start).TotalSeconds;
                    if (dur < 0) dur = 0;

                    switch (log.Status)
                    {
                        case MachineStatus.Green: greenSec += dur; break;
                        case MachineStatus.Yellow: yellowSec += dur; break;
                        case MachineStatus.Red: redSec += dur; break;
                        default: offSec += dur; break;
                    }
                }

                // Count errors from ErrorLogs table
                var machineErrors = errorLogs.Where(e => e.MachineId == machineId).ToList();
                double outSec = 0, trashSec = 0, otherSec = 0;
                foreach (var e in machineErrors)
                {
                    var start = e.StartTime < shiftStart ? shiftStart : e.StartTime;
                    var end = (e.EndTime ?? DateTime.Now) > shiftEnd ? shiftEnd : (e.EndTime ?? DateTime.Now);
                    var dur = (end - start).TotalSeconds;
                    if (dur < 0) dur = 0;
                    switch (e.ErrorType)
                    {
                        case ErrorType.Out: outSec += dur; break;
                        case ErrorType.Trash: trashSec += dur; break;
                        case ErrorType.Other: otherSec += dur; break;
                    }
                }

                return new
                {
                    machineId,
                    name = $"M{machineId}",
                    greenSec = Math.Round(greenSec),
                    yellowSec = Math.Round(yellowSec),
                    redSec = Math.Round(redSec),
                    offSec = Math.Round(offSec),
                    efficiency = ProductionRate.Efficiency(greenSec, yellowSec, redSec),
                    outSec = Math.Round(outSec),
                    trashSec = Math.Round(trashSec),
                    otherSec = Math.Round(otherSec),
                    outCount = machineErrors.Count(e => e.ErrorType == ErrorType.Out),
                    trashCount = machineErrors.Count(e => e.ErrorType == ErrorType.Trash),
                    otherCount = machineErrors.Count(e => e.ErrorType == ErrorType.Other)
                };
            }).ToList();

            return Ok(stats);
        }

        /// <summary>
        /// GET /api/machineapi/report?from=2026-10-01&amp;to=2026-10-07&amp;machines=1,2,5&amp;shifts=1,3
        /// Run/Wait/Stop/Off seconds per machine per production day (06:00 → 06:00), counting only the chosen
        /// shifts (none = all). Machines none = all. Days without any data are left out. The page computes the
        /// rates (ProductionRate formula) so the break time can be changed per day without asking again.
        /// </summary>
        [HttpGet("report")]
        public async Task<IActionResult> GetReport(DateTime from, DateTime to, string? machines, string? shifts)
        {
            if (!TryReportRange(ref from, ref to, out var error))
                return BadRequest(error);

            var report = await ReportService.BuildAsync(_db, from, to,
                ToMachineIds(ParseIdList(machines, 1, MachineMonitorService.TOTAL_MACHINES)),
                ToShifts(ParseIdList(shifts, 1, 3)));

            return Ok(new
            {
                from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                shifts = report.Shifts.Select(s => (int)s),
                machines = report.Machines.Select(m => new
                {
                    machineId = m.MachineId,
                    name = m.Name,
                    days = m.Days.Select(d => new
                    {
                        date = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        greenSec = d.GreenSec,
                        yellowSec = d.YellowSec,
                        redSec = d.RedSec,
                        offSec = d.OffSec
                    })
                })
            });
        }

        /// <summary>Body of POST report-excel: the report filters plus the break minutes shown on the page.</summary>
        public class ReportExcelRequest
        {
            public DateTime From { get; set; }
            public DateTime To { get; set; }
            public List<int> Machines { get; set; } = new();
            public List<int> Shifts { get; set; } = new();
            public double DefaultBreak { get; set; }
            /// <summary>Break minutes edited per day: key "machineId|yyyy-MM-dd".</summary>
            public Dictionary<string, double> Breaks { get; set; } = new();
        }

        /// <summary>POST /api/machineapi/report-excel — the report page as an .xlsx file (summary + one sheet per machine).</summary>
        [HttpPost("report-excel")]
        public async Task<IActionResult> GetReportExcel([FromBody] ReportExcelRequest request)
        {
            DateTime from = request.From, to = request.To;
            if (!TryReportRange(ref from, ref to, out var error))
                return BadRequest(error);

            var report = await ReportService.BuildAsync(_db, from, to,
                ToMachineIds(request.Machines.Where(id => id >= 1 && id <= MachineMonitorService.TOTAL_MACHINES).Distinct().Order().ToList()),
                ToShifts(request.Shifts.Where(s => s >= 1 && s <= 3).Distinct().Order().ToList()));

            double defaultBreak = Math.Max(0, request.DefaultBreak);
            var file = ReportService.ToExcel(report, (machineId, day) =>
                request.Breaks.TryGetValue($"{machineId}|{day:yyyy-MM-dd}", out var b) ? Math.Max(0, b) : defaultBreak);

            return File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"BaoCao_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.xlsx");
        }

        private static bool TryReportRange(ref DateTime from, ref DateTime to, out string error)
        {
            from = from.Date;
            to = to.Date;
            error = $"Khoảng ngày không hợp lệ (tối đa {ReportService.MAX_DAYS} ngày)";
            return to >= from && (to - from).TotalDays < ReportService.MAX_DAYS;
        }

        private static List<int> ToMachineIds(List<int> ids) =>
            ids.Count > 0 ? ids : Enumerable.Range(1, MachineMonitorService.TOTAL_MACHINES).ToList();

        private static List<ShiftType> ToShifts(List<int> shifts) =>
            shifts.Count > 0 ? shifts.Select(s => (ShiftType)s).ToList() : [ShiftType.Shift1, ShiftType.Shift2, ShiftType.Shift3];

        // "1,2,5" → [1, 2, 5] within [min, max], duplicates and junk ignored
        private static List<int> ParseIdList(string? list, int min, int max) =>
            (list ?? "").Split(',', ';', ' ')
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .Where(v => v >= min && v <= max)
                .Distinct().Order().ToList();

        /// <summary>
        /// GET /api/machineapi/period-stats?period=day|week|month&date=2026-03-02
        /// Uses pre-aggregated DailyStatistic for fast queries.
        /// </summary>
        [HttpGet("period-stats")]
        public async Task<IActionResult> GetPeriodStats(string period = "day", DateTime? date = null)
        {
            var endDate = (date ?? DateTime.Today);
            DateTime startDate;
            switch (period.ToLower())
            {
                case "week": startDate = endDate.AddDays(-6); break;
                case "month": startDate = endDate.AddDays(-29); break;
                default: startDate = endDate; break; // day
            }

            var dailyStats = await _db.DailyStatistics
                .Where(d => d.Date >= startDate && d.Date <= endDate)
                .OrderBy(d => d.Date)
                .ToListAsync();

            // Group by day for trend charts
            var dateRange = Enumerable.Range(0, (endDate - startDate).Days + 1)
                .Select(offset => startDate.AddDays(offset)).ToList();

            // Per-day aggregated data (all machines combined)
            var dailyTrend = dateRange.Select(d =>
            {
                var dayStats = dailyStats.Where(s => s.Date == d).ToList();
                return new
                {
                    date = d.ToString("dd/MM"),
                    greenSec = Math.Round(dayStats.Sum(s => s.GreenSeconds)),
                    yellowSec = Math.Round(dayStats.Sum(s => s.YellowSeconds)),
                    redSec = Math.Round(dayStats.Sum(s => s.RedSeconds)),
                    offSec = Math.Round(dayStats.Sum(s => s.OffSeconds)),
                    outErrors = dayStats.Sum(s => s.OutErrorCount),
                    trashErrors = dayStats.Sum(s => s.TrashErrorCount),
                    otherErrors = dayStats.Sum(s => s.OtherErrorCount),
                    // Average over the machines that had a lamp on that day (a machine powered off all day has no rate)
                    efficiency = dayStats.Any(s => s.GreenSeconds + s.YellowSeconds + s.RedSeconds > 0)
                        ? Math.Round(dayStats.Where(s => s.GreenSeconds + s.YellowSeconds + s.RedSeconds > 0)
                            .Average(s => s.Efficiency), 1) : 0.0
                };
            }).ToList();

            // Per-machine summary (aggregated over entire period)
            var machineSummary = Enumerable.Range(1, MachineMonitorService.TOTAL_MACHINES).Select(machineId =>
            {
                var ms = dailyStats.Where(s => s.MachineId == machineId).ToList();
                double greenSec = ms.Sum(s => s.GreenSeconds);
                double yellowSec = ms.Sum(s => s.YellowSeconds);
                double redSec = ms.Sum(s => s.RedSeconds);
                double offSec = ms.Sum(s => s.OffSeconds);

                return new
                {
                    machineId,
                    name = $"M{machineId}",
                    greenSec = Math.Round(greenSec),
                    yellowSec = Math.Round(yellowSec),
                    redSec = Math.Round(redSec),
                    offSec = Math.Round(offSec),
                    efficiency = ProductionRate.Efficiency(greenSec, yellowSec, redSec),
                    outCount = ms.Sum(s => s.OutErrorCount),
                    trashCount = ms.Sum(s => s.TrashErrorCount),
                    otherCount = ms.Sum(s => s.OtherErrorCount)
                };
            }).ToList();

            return Ok(new { period, startDate = startDate.ToString("yyyy-MM-dd"),
                endDate = endDate.ToString("yyyy-MM-dd"), dailyTrend, machineSummary });
        }

        /// <summary>GET /api/machineapi/agent — factory link status (online, last seen, PLC mode)</summary>
        [HttpGet("agent")]
        public IActionResult GetAgentStatus()
        {
            return Ok(MachineMonitorService.GetAgentStatusPayload());
        }
    }
}
