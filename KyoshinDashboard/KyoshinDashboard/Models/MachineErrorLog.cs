using System.ComponentModel.DataAnnotations;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Independent error log — each ON→OFF cycle = one record.
    /// Completely decoupled from MachineStatusLog.
    /// </summary>
    public class MachineErrorLog
    {
        [Key]
        public long Id { get; set; }

        public int MachineId { get; set; }

        public ErrorType ErrorType { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        /// <summary>Duration in seconds</summary>
        public double DurationSeconds { get; set; }

        public ShiftType Shift { get; set; }
    }
}
