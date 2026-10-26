using System.ComponentModel.DataAnnotations;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Aggregated statistics per machine per shift per day
    /// </summary>
    public class ShiftStatistic
    {
        [Key]
        public long Id { get; set; }

        public int MachineId { get; set; }
        public DateTime Date { get; set; }
        public ShiftType Shift { get; set; }

        /// <summary>Total seconds in Green status</summary>
        public double GreenSeconds { get; set; }

        /// <summary>Total seconds in Yellow status</summary>
        public double YellowSeconds { get; set; }

        /// <summary>Total seconds in Red status</summary>
        public double RedSeconds { get; set; }

        /// <summary>Total seconds in Off status</summary>
        public double OffSeconds { get; set; }

        public int OutErrorCount { get; set; }
        public int TrashErrorCount { get; set; }
        public int OtherErrorCount { get; set; }

        /// <summary>Efficiency = Run ÷ (Run + Wait + Stop), see ProductionRate</summary>
        public double Efficiency => ProductionRate.Efficiency(GreenSeconds, YellowSeconds, RedSeconds);
    }
}
