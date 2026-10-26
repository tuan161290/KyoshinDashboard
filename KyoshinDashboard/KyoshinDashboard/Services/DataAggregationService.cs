using KyoshinDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Services
{
    /// <summary>
    /// Background service that periodically aggregates StatusLogs into DailyStatistic records.
    /// Runs every 5 minutes to keep daily stats up-to-date.
    /// Also cleans up old StatusLogs/ErrorLogs (DataSettings:RetentionDays) and finalizes completed days.
    /// </summary>
    public class DataAggregationService : BackgroundService
    {
        private readonly ILogger<DataAggregationService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly int _retentionDays;

        public DataAggregationService(ILogger<DataAggregationService> logger,
            IServiceProvider serviceProvider, IConfiguration config)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _retentionDays = config.GetValue("DataSettings:RetentionDays", 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 30s after startup to let the system stabilize
            await Task.Delay(30_000, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await AggregateToday();
                    await FinalizePastDays();
                    await CleanupOldLogs();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Aggregation error");
                }

                // Run every 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        /// <summary>Aggregate today's StatusLogs into DailyStatistic (live update)</summary>
        private async Task AggregateToday()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();

            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var logs = await db.StatusLogs
                .Where(l => l.StartTime >= today && l.StartTime < tomorrow)
                .ToListAsync();

            for (int machineId = 1; machineId <= MachineMonitorService.TOTAL_MACHINES; machineId++)
            {
                var machineLogs = logs.Where(l => l.MachineId == machineId).ToList();
                if (machineLogs.Count == 0) continue;

                var stat = await db.DailyStatistics
                    .FirstOrDefaultAsync(d => d.MachineId == machineId && d.Date == today);

                if (stat == null)
                {
                    stat = new DailyStatistic { MachineId = machineId, Date = today };
                    db.DailyStatistics.Add(stat);
                }

                // Reset and recalculate
                stat.GreenSeconds = stat.YellowSeconds = stat.RedSeconds = stat.OffSeconds = 0;
                stat.OutErrorCount = stat.TrashErrorCount = stat.OtherErrorCount = 0;
                stat.Shift1GreenSec = stat.Shift1YellowSec = stat.Shift1RedSec = 0;
                stat.Shift2GreenSec = stat.Shift2YellowSec = stat.Shift2RedSec = 0;
                stat.Shift3GreenSec = stat.Shift3YellowSec = stat.Shift3RedSec = 0;

                foreach (var log in machineLogs)
                {
                    var endTime = log.EndTime ?? DateTime.Now;
                    var duration = (endTime - log.StartTime).TotalSeconds;
                    if (duration < 0) duration = 0;

                    var shift = MachineMonitorService.GetCurrentShift(log.StartTime);

                    switch (log.Status)
                    {
                        case MachineStatus.Green:
                            stat.GreenSeconds += duration;
                            if (shift == ShiftType.Shift1) stat.Shift1GreenSec += duration;
                            else if (shift == ShiftType.Shift2) stat.Shift2GreenSec += duration;
                            else stat.Shift3GreenSec += duration;
                            break;
                        case MachineStatus.Yellow:
                            stat.YellowSeconds += duration;
                            if (shift == ShiftType.Shift1) stat.Shift1YellowSec += duration;
                            else if (shift == ShiftType.Shift2) stat.Shift2YellowSec += duration;
                            else stat.Shift3YellowSec += duration;
                            break;
                        case MachineStatus.Red:
                            stat.RedSeconds += duration;
                            if (shift == ShiftType.Shift1) stat.Shift1RedSec += duration;
                            else if (shift == ShiftType.Shift2) stat.Shift2RedSec += duration;
                            else stat.Shift3RedSec += duration;
                            break;
                        default:
                            stat.OffSeconds += duration;
                            break;
                    }
                }

                // Count errors from ErrorLogs table
                var errLogs = await db.ErrorLogs
                    .Where(e => e.MachineId == machineId && e.StartTime >= today && e.StartTime < tomorrow)
                    .ToListAsync();
                stat.OutErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Out);
                stat.TrashErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Trash);
                stat.OtherErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Other);
            }

            await db.SaveChangesAsync();
        }

        /// <summary>Finalize past days that haven't been marked as complete</summary>
        private async Task FinalizePastDays()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();

            var yesterday = DateTime.Today.AddDays(-1);

            // Find unfinalized records older than today
            var unfinalized = await db.DailyStatistics
                .Where(d => d.Date < DateTime.Today && !d.IsFinalized)
                .ToListAsync();

            foreach (var stat in unfinalized)
            {
                stat.IsFinalized = true;
            }

            // Also aggregate any past days that don't have DailyStatistic records yet
            var existingDates = await db.DailyStatistics
                .Where(d => d.Date >= DateTime.Today.AddDays(-7))
                .Select(d => new { d.MachineId, d.Date })
                .ToListAsync();

            // Times are stored as integers (see KyoshinDbContext): take the date in C#, not in SQL
            var logDates = (await db.StatusLogs
                .Where(l => l.StartTime >= DateTime.Today.AddDays(-7) && l.StartTime < DateTime.Today)
                .Select(l => new { l.MachineId, l.StartTime })
                .ToListAsync())
                .Select(l => new { l.MachineId, Date = l.StartTime.Date })
                .Distinct()
                .ToList();

            foreach (var missing in logDates.Where(ld =>
                !existingDates.Any(ed => ed.MachineId == ld.MachineId && ed.Date == ld.Date)))
            {
                var dayStart = missing.Date;
                var dayEnd = dayStart.AddDays(1);

                var dayLogs = await db.StatusLogs
                    .Where(l => l.MachineId == missing.MachineId &&
                                l.StartTime >= dayStart && l.StartTime < dayEnd)
                    .ToListAsync();

                var stat = new DailyStatistic
                {
                    MachineId = missing.MachineId,
                    Date = missing.Date,
                    IsFinalized = true
                };

                foreach (var log in dayLogs)
                {
                    var endTime = log.EndTime ?? dayEnd;
                    var duration = (endTime - log.StartTime).TotalSeconds;
                    if (duration < 0) duration = 0;

                    switch (log.Status)
                    {
                        case MachineStatus.Green: stat.GreenSeconds += duration; break;
                        case MachineStatus.Yellow: stat.YellowSeconds += duration; break;
                        case MachineStatus.Red: stat.RedSeconds += duration; break;
                        default: stat.OffSeconds += duration; break;
                    }

                }

                // Count errors from ErrorLogs
                var errLogs = await db.ErrorLogs
                    .Where(e => e.MachineId == missing.MachineId && e.StartTime >= dayStart && e.StartTime < dayEnd)
                    .ToListAsync();
                stat.OutErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Out);
                stat.TrashErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Trash);
                stat.OtherErrorCount = errLogs.Count(e => e.ErrorType == ErrorType.Other);

                db.DailyStatistics.Add(stat);
            }

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Delete StatusLogs and ErrorLogs that ended before the retention period (DataSettings:RetentionDays,
        /// 0 = keep everything, the default: production data is kept). Week/month statistics stay in DailyStatistics.
        /// </summary>
        private async Task CleanupOldLogs()
        {
            if (_retentionDays <= 0) return;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();

            var cutoff = DateTime.Today.AddDays(-_retentionDays);

            // Keep the logs of any day whose statistics are not final yet
            var firstOpenDay = await db.DailyStatistics
                .Where(d => !d.IsFinalized)
                .MinAsync(d => (DateTime?)d.Date);
            if (firstOpenDay < cutoff) cutoff = firstOpenDay.Value;

            // Only finished segments: a machine still in the same state (EndTime null) keeps its open log
            var statusDeleted = await db.StatusLogs
                .Where(l => l.StartTime < cutoff && l.EndTime != null && l.EndTime < cutoff)
                .ExecuteDeleteAsync();
            var errorDeleted = await db.ErrorLogs
                .Where(e => e.StartTime < cutoff && e.EndTime != null && e.EndTime < cutoff)
                .ExecuteDeleteAsync();

            if (statusDeleted + errorDeleted > 0)
                _logger.LogInformation("Cleaned up {Status} status logs and {Errors} error logs that ended before {Date}",
                    statusDeleted, errorDeleted, cutoff.ToString("yyyy-MM-dd"));
        }
    }
}
