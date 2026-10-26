using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// One-time upgrade of a database created before log times were stored as INTEGER
    /// (see <see cref="KyoshinDbContext.TimeAsMilliseconds"/>): rebuilds StatusLogs and ErrorLogs with the
    /// current schema and converts the old text times. Nothing happens on a new or already converted database.
    /// </summary>
    public static class LogTimeMigration
    {
        // Text "yyyy-MM-dd HH:mm:ss.fffffff" -> milliseconds from 1970-01-01 on the same clock (no time zone shift)
        private const string ToMs = "CAST(ROUND((julianday({0}) - 2440587.5) * 86400000.0) AS INTEGER)";

        public static void Run(KyoshinDbContext db, ILogger logger)
        {
            var tables = new[] { "StatusLogs", "ErrorLogs" }.Where(t => IsTextTime(db, t)).ToList();
            if (tables.Count == 0) return;

            // CREATE TABLE / CREATE INDEX statements of the current model, one per table
            var script = db.Database.GenerateCreateScript()
                .Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            using (var tx = db.Database.BeginTransaction())
            {
                foreach (var table in tables)
                {
                    var createTable = script.Single(s => s.StartsWith($"CREATE TABLE \"{table}\""));
                    var createIndexes = script.Where(s => s.Contains($" ON \"{table}\" ("));
                    var columns = db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToList();
                    var select = columns.Select(c => c is "StartTime" or "EndTime"
                        ? string.Format(ToMs, $"\"{c}\"")
                        : $"\"{c}\"");

                    db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" RENAME TO \"{table}_old\"");
                    db.Database.ExecuteSqlRaw(createTable);
                    int rows = db.Database.ExecuteSqlRaw(
                        $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) " +
                        $"SELECT {string.Join(", ", select)} FROM \"{table}_old\"");
                    db.Database.ExecuteSqlRaw($"DROP TABLE \"{table}_old\"");
                    foreach (var index in createIndexes) db.Database.ExecuteSqlRaw(index);
                    logger.LogInformation("Converted {Rows} rows of {Table} to integer times", rows, table);
                }
                tx.Commit();
            }

            db.Database.ExecuteSqlRaw("VACUUM");
        }

        private static bool IsTextTime(KyoshinDbContext db, string table) =>
            db.Database.SqlQueryRaw<string>(
                $"SELECT type AS Value FROM pragma_table_info('{table}') WHERE name = 'StartTime'")
                .AsEnumerable().FirstOrDefault()?.Equals("TEXT", StringComparison.OrdinalIgnoreCase) == true;
    }
}
