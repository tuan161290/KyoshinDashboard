using System.ComponentModel.DataAnnotations;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Pre-aggregated daily statistics per machine.
    /// Used for fast week/month chart queries instead of scanning raw StatusLogs.
    /// One row per machine per day = max 48 rows/day.
    /// </summary>
    public class DailyStatistic
    {
        [Key]
        public long Id { get; set; }

        public int MachineId { get; set; }
        public DateTime Date { get; set; }

        // Time in each status (seconds)
        public double GreenSeconds { get; set; }
        public double YellowSeconds { get; set; }
        public double RedSeconds { get; set; }
        public double OffSeconds { get; set; }

        // Error counts for the day
        public int OutErrorCount { get; set; }
        public int TrashErrorCount { get; set; }
        public int OtherErrorCount { get; set; }

        // Per-shift breakdown
        public double Shift1GreenSec { get; set; }
        public double Shift1YellowSec { get; set; }
        public double Shift1RedSec { get; set; }
        public double Shift2GreenSec { get; set; }
        public double Shift2YellowSec { get; set; }
        public double Shift2RedSec { get; set; }
        public double Shift3GreenSec { get; set; }
        public double Shift3YellowSec { get; set; }
        public double Shift3RedSec { get; set; }

        /// <summary>Efficiency = Run ÷ (Run + Wait + Stop), see ProductionRate</summary>
        public double Efficiency => ProductionRate.Efficiency(GreenSeconds, YellowSeconds, RedSeconds);

        /// <summary>Whether this record has been finalized (day is complete)</summary>
        public bool IsFinalized { get; set; }
    }
}
