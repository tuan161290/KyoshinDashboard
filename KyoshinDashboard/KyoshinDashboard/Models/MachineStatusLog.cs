using System.ComponentModel.DataAnnotations;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Persisted log entry for each machine status change
    /// </summary>
    public class MachineStatusLog
    {
        [Key]
        public long Id { get; set; }

        public int MachineId { get; set; }

        public MachineStatus Status { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        /// <summary>Duration in seconds</summary>
        public double DurationSeconds { get; set; }

        public ShiftType Shift { get; set; }
    }
}
