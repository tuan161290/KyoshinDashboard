using ClosedXML.Excel;
using KyoshinDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Services
{
    /// <summary>Status seconds of one machine on one production day (06:00 → 06:00), chosen shifts only.</summary>
    public record ReportDay(DateTime Date, double GreenSec, double YellowSec, double RedSec, double OffSec);

    public record ReportMachine(int MachineId, List<ReportDay> Days)
    {
        public string Name => $"M{MachineId}";
    }

    public record ReportData(DateTime From, DateTime To, List<ShiftType> Shifts, List<ReportMachine> Machines);

    /// <summary>
    /// The "Báo cáo" report (page /Report, API report and report-excel): per machine and production day,
    /// time in each status inside the chosen shifts. Rates use <see cref="ProductionRate"/>.
    /// </summary>
    public static class ReportService
    {
        public const int MAX_DAYS = 93;

        public static async Task<ReportData> BuildAsync(KyoshinDbContext db, DateTime from, DateTime to,
            List<int> machineIds, List<ShiftType> shifts)
        {
            var rangeStart = from.AddHours(6);
            var rangeEnd = to.AddDays(1).AddHours(6);
            var now = DateTime.Now;

            var logs = await db.StatusLogs
                .Where(l => machineIds.Contains(l.MachineId) && l.StartTime < rangeEnd && (l.EndTime == null || l.EndTime > rangeStart))
                .ToListAsync();

            // seconds[machine][day] = { green, yellow, red, off }
            var seconds = new Dictionary<int, SortedDictionary<DateTime, double[]>>();
            foreach (var log in logs)
            {
                var logStart = log.StartTime < rangeStart ? rangeStart : log.StartTime;
                var logEnd = log.EndTime ?? now;
                if (logEnd > rangeEnd) logEnd = rangeEnd;
                if (logEnd <= logStart) continue;

                int slot = log.Status switch
                {
                    MachineStatus.Green => 0,
                    MachineStatus.Yellow => 1,
                    MachineStatus.Red => 2,
                    _ => 3
                };
                var days = seconds.TryGetValue(log.MachineId, out var d) ? d : seconds[log.MachineId] = new();

                // Production days this segment touches, then the chosen shifts of each day
                for (var day = MachineMonitorService.GetShiftDay(logStart); day <= MachineMonitorService.GetShiftDay(logEnd) && day <= to; day = day.AddDays(1))
                {
                    foreach (var shift in shifts)
                    {
                        var (shiftStart, shiftEnd) = MachineMonitorService.GetShiftBoundaries(day, shift);
                        var overlap = ((logEnd < shiftEnd ? logEnd : shiftEnd) - (logStart > shiftStart ? logStart : shiftStart)).TotalSeconds;
                        if (overlap <= 0) continue;
                        var sums = days.TryGetValue(day, out var s) ? s : days[day] = new double[4];
                        sums[slot] += overlap;
                    }
                }
            }

            var machines = machineIds.Select(id => new ReportMachine(id,
                seconds.TryGetValue(id, out var days)
                    ? days.Select(kv => new ReportDay(kv.Key, Math.Round(kv.Value[0]), Math.Round(kv.Value[1]),
                        Math.Round(kv.Value[2]), Math.Round(kv.Value[3]))).ToList()
                    : [])).ToList();
            return new ReportData(from, to, shifts, machines);
        }

        /// <summary>
        /// Excel file like the page: a summary sheet, then one sheet per machine with a row per day and an
        /// average row. <paramref name="breakMinutes"/> gives the break of a machine on a day (page edits or default).
        /// </summary>
        public static byte[] ToExcel(ReportData report, Func<int, DateTime, double> breakMinutes)
        {
            using var wb = new XLWorkbook();
            string shiftText = report.Shifts.Count == 3 ? "Cả ngày (Ca 1, 2, 3)" : string.Join(", ", report.Shifts.Select(s => $"Ca {(int)s}"));
            string period = $"Từ {report.From:dd/MM/yyyy} đến {report.To:dd/MM/yyyy} — {shiftText}";
            const string note = "Ngày sản xuất 06:00 → 06:00. Tỷ lệ % = thời gian ÷ (Chạy + Chờ + Dừng − nghỉ); thời gian Tắt máy không tính vào.";

            var computed = report.Machines.Select(m => (Machine: m, Rows: m.Days.Select(d => Compute(d, breakMinutes(m.MachineId, d.Date))).ToList())).ToList();

            // Summary sheet
            var sum = wb.Worksheets.Add("Tổng hợp");
            Title(sum, "BÁO CÁO HIỆU SUẤT — TỔNG HỢP", period, note);
            Header(sum, 5, "Máy", "Số ngày", "Chạy %", "Chờ %", "Dừng %", "Chạy (phút TB)", "Chờ (phút TB)", "Dừng (phút TB)", "Tắt (phút TB)", "Nghỉ (phút TB)");
            int r = 6;
            foreach (var (m, rows) in computed)
            {
                var a = Average(rows);
                sum.Cell(r, 1).Value = m.Name;
                sum.Cell(r, 2).Value = rows.Count;
                SetRate(sum.Cell(r, 3), a.Run); SetRate(sum.Cell(r, 4), a.Wait); SetRate(sum.Cell(r, 5), a.Stop);
                SetMin(sum.Cell(r, 6), a.RunMin); SetMin(sum.Cell(r, 7), a.WaitMin); SetMin(sum.Cell(r, 8), a.StopMin);
                SetMin(sum.Cell(r, 9), a.OffMin); SetMin(sum.Cell(r, 10), a.BreakMin);
                r++;
            }
            Finish(sum, 5, r - 1, 10);

            // One sheet per machine
            foreach (var (m, rows) in computed)
            {
                var ws = wb.Worksheets.Add(m.Name);
                Title(ws, $"BÁO CÁO HIỆU SUẤT — MÁY {m.Name}", period, note);
                Header(ws, 5, "Ngày", "Nghỉ (phút)", "Chạy %", "Chạy (phút)", "Chờ %", "Chờ (phút)", "Dừng %", "Dừng (phút)", "Tắt (phút)");
                r = 6;
                foreach (var row in rows)
                {
                    ws.Cell(r, 1).Value = row.Date;
                    ws.Cell(r, 1).Style.DateFormat.Format = "dd/MM/yyyy";
                    SetMin(ws.Cell(r, 2), row.BreakMin);
                    SetRate(ws.Cell(r, 3), row.Run); SetMin(ws.Cell(r, 4), row.RunMin);
                    SetRate(ws.Cell(r, 5), row.Wait); SetMin(ws.Cell(r, 6), row.WaitMin);
                    SetRate(ws.Cell(r, 7), row.Stop); SetMin(ws.Cell(r, 8), row.StopMin);
                    SetMin(ws.Cell(r, 9), row.OffMin);
                    r++;
                }
                if (rows.Count == 0)
                {
                    ws.Cell(r, 1).Value = "Không có dữ liệu trong khoảng đã chọn";
                    r++;
                }
                else
                {
                    var a = Average(rows);
                    ws.Cell(r, 1).Value = "Trung bình";
                    SetMin(ws.Cell(r, 2), a.BreakMin);
                    SetRate(ws.Cell(r, 3), a.Run); SetMin(ws.Cell(r, 4), a.RunMin);
                    SetRate(ws.Cell(r, 5), a.Wait); SetMin(ws.Cell(r, 6), a.WaitMin);
                    SetRate(ws.Cell(r, 7), a.Stop); SetMin(ws.Cell(r, 8), a.StopMin);
                    SetMin(ws.Cell(r, 9), a.OffMin);
                    ws.Range(r, 1, r, 9).Style.Font.Bold = true;
                    ws.Range(r, 1, r, 9).Style.Border.TopBorder = XLBorderStyleValues.Medium;
                    r++;
                }
                Finish(ws, 5, r - 1, 9);
            }

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return stream.ToArray();
        }

        // Rates are null when the base (Run + Wait + Stop − break) is empty
        private record Row(DateTime Date, double BreakMin, double? Run, double? Wait, double? Stop,
            double RunMin, double WaitMin, double StopMin, double OffMin);

        private static Row Compute(ReportDay d, double breakMin)
        {
            // Not rounded here (the cell format shows 1 decimal), so averages match the page
            double rateBase = ProductionRate.Base(d.GreenSec, d.YellowSec, d.RedSec, breakMin * 60);
            double? Rate(double sec) => rateBase > 0 ? sec / rateBase * 100 : null;
            return new Row(d.Date, breakMin, Rate(d.GreenSec), Rate(d.YellowSec), Rate(d.RedSec),
                Math.Round(d.GreenSec / 60), Math.Round(d.YellowSec / 60), Math.Round(d.RedSec / 60), Math.Round(d.OffSec / 60));
        }

        // Same as the page: rates averaged over the days that have a rate, minutes over all days
        private static Row Average(List<Row> rows)
        {
            var rated = rows.Where(x => x.Run != null).ToList();
            double? AvgRate(Func<Row, double?> f) => rated.Count > 0 ? rated.Average(x => f(x)!.Value) : null;
            double AvgMin(Func<Row, double> f) => rows.Count > 0 ? Math.Round(rows.Average(f)) : 0;
            return new Row(default, AvgMin(x => x.BreakMin), AvgRate(x => x.Run), AvgRate(x => x.Wait), AvgRate(x => x.Stop),
                AvgMin(x => x.RunMin), AvgMin(x => x.WaitMin), AvgMin(x => x.StopMin), AvgMin(x => x.OffMin));
        }

        private static void SetRate(IXLCell cell, double? value)
        {
            if (value == null) { cell.Value = "—"; cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; return; }
            cell.Value = value.Value;
            cell.Style.NumberFormat.Format = "0.0";
        }

        private static void SetMin(IXLCell cell, double value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = "0";
        }

        private static void Title(IXLWorksheet ws, string title, string period, string note)
        {
            ws.Cell(1, 1).Value = title;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Value = period;
            ws.Cell(3, 1).Value = note;
            ws.Cell(3, 1).Style.Font.Italic = true;
            ws.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;
        }

        private static void Header(IXLWorksheet ws, int row, params string[] names)
        {
            for (int i = 0; i < names.Length; i++) ws.Cell(row, i + 1).Value = names[i];
            var range = ws.Range(row, 1, row, names.Length);
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.WrapText = true;
        }

        private static void Finish(IXLWorksheet ws, int headerRow, int lastRow, int lastCol)
        {
            var table = ws.Range(headerRow, 1, Math.Max(lastRow, headerRow), lastCol);
            table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Columns(1, lastCol).Width = 13;
            ws.SheetView.FreezeRows(headerRow);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.FitToPages(1, 0);
        }
    }
}
