using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KyoshinDashboard.Models
{
    public class KyoshinDbContext : DbContext
    {
        public KyoshinDbContext(DbContextOptions<KyoshinDbContext> options) : base(options) { }

        public DbSet<MachineStatusLog> StatusLogs { get; set; } = null!;
        public DbSet<MachineErrorLog> ErrorLogs { get; set; } = null!;
        public DbSet<ShiftStatistic> ShiftStatistics { get; set; } = null!;
        public DbSet<DailyStatistic> DailyStatistics { get; set; } = null!;
        public DbSet<AgentCursor> AgentCursors { get; set; } = null!;

        /// <summary>
        /// Log times are stored as INTEGER: milliseconds from 1970-01-01 00:00 on the same (factory local) clock,
        /// about 6 bytes instead of a 27-character text. Read in SQL with datetime(StartTime / 1000, 'unixepoch').
        /// </summary>
        public static readonly ValueConverter<DateTime, long> TimeAsMilliseconds = new(
            v => (v.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
            v => new DateTime(DateTime.UnixEpoch.Ticks + v * TimeSpan.TicksPerMillisecond));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MachineStatusLog>(entity =>
            {
                entity.Property(e => e.StartTime).HasConversion(TimeAsMilliseconds);
                entity.Property(e => e.EndTime).HasConversion(TimeAsMilliseconds);
                entity.HasIndex(e => e.MachineId);
                entity.HasIndex(e => e.StartTime);
                entity.HasIndex(e => new { e.MachineId, e.StartTime });
            });

            modelBuilder.Entity<MachineErrorLog>(entity =>
            {
                entity.Property(e => e.StartTime).HasConversion(TimeAsMilliseconds);
                entity.Property(e => e.EndTime).HasConversion(TimeAsMilliseconds);
                entity.HasIndex(e => e.MachineId);
                entity.HasIndex(e => e.StartTime);
                entity.HasIndex(e => new { e.MachineId, e.ErrorType, e.StartTime });
            });

            modelBuilder.Entity<ShiftStatistic>(entity =>
            {
                entity.HasIndex(e => new { e.MachineId, e.Date, e.Shift }).IsUnique();
            });

            modelBuilder.Entity<DailyStatistic>(entity =>
            {
                entity.HasIndex(e => new { e.MachineId, e.Date }).IsUnique();
                entity.HasIndex(e => e.Date);
            });
        }
    }
}
