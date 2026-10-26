using Kyoshin.Plc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace KyoshinAgent
{
    /// <summary>
    /// Local store-and-forward queue (SQLite file next to the exe).
    /// Every machine state change is written here first, so nothing is lost while the server or
    /// the Internet is down; the uploader deletes rows only after the server has confirmed them.
    /// Seq is AUTOINCREMENT, so it never repeats even after rows are deleted.
    /// The file also keeps the AgentId — deleting it starts a new id (and a new Seq range on the server).
    /// </summary>
    public sealed class OutboxStore
    {
        private readonly string _connectionString;
        private readonly object _lock = new();

        public string AgentId { get; }
        public string FilePath { get; }

        public OutboxStore(IHostEnvironment env, IOptions<AgentOptions> options)
        {
            FilePath = Path.Combine(env.ContentRootPath, options.Value.DataFile);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath }.ToString();

            using var conn = Open();
            Execute(conn, "PRAGMA journal_mode=WAL;");
            Execute(conn, """
                CREATE TABLE IF NOT EXISTS Meta (
                    Key   TEXT PRIMARY KEY,
                    Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Outbox (
                    Seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                    MachineId  INTEGER NOT NULL,
                    Status     TEXT    NOT NULL,
                    OutError   INTEGER NOT NULL,
                    TrashError INTEGER NOT NULL,
                    OtherError INTEGER NOT NULL,
                    Time       TEXT    NOT NULL);
                """);
            AgentId = GetOrCreateAgentId(conn);
        }

        /// <summary>Queue the new state of each changed machine with the time of its change (one transaction).</summary>
        public void Enqueue(IReadOnlyCollection<(MachineReading Reading, DateTimeOffset Time)> changes)
        {
            lock (_lock)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Outbox (MachineId, Status, OutError, TrashError, OtherError, Time)
                    VALUES ($m, $s, $o, $t, $x, $time);
                    """;
                var pM = cmd.Parameters.Add("$m", SqliteType.Integer);
                var pS = cmd.Parameters.Add("$s", SqliteType.Text);
                var pO = cmd.Parameters.Add("$o", SqliteType.Integer);
                var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
                var pX = cmd.Parameters.Add("$x", SqliteType.Integer);
                var pTime = cmd.Parameters.Add("$time", SqliteType.Text);

                foreach (var (r, time) in changes)
                {
                    pM.Value = r.MachineId;
                    pS.Value = r.Status.ToString();
                    pO.Value = r.OutError ? 1 : 0;
                    pT.Value = r.TrashError ? 1 : 0;
                    pX.Value = r.OtherError ? 1 : 0;
                    pTime.Value = time.ToString("o");
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>Oldest queued events, in Seq order.</summary>
        public List<IngestEvent> Peek(int max)
        {
            lock (_lock)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT Seq, MachineId, Status, OutError, TrashError, OtherError, Time
                    FROM Outbox ORDER BY Seq LIMIT $max;
                    """;
                cmd.Parameters.AddWithValue("$max", max);

                var events = new List<IngestEvent>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new IngestEvent
                    {
                        Seq = reader.GetInt64(0),
                        MachineId = reader.GetInt32(1),
                        Status = reader.GetString(2),
                        OutError = reader.GetInt64(3) != 0,
                        TrashError = reader.GetInt64(4) != 0,
                        OtherError = reader.GetInt64(5) != 0,
                        Time = DateTimeOffset.Parse(reader.GetString(6))
                    });
                }
                return events;
            }
        }

        /// <summary>Delete events the server has confirmed.</summary>
        public void RemoveUpTo(long seq)
        {
            lock (_lock)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Outbox WHERE Seq <= $seq;";
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.ExecuteNonQuery();
            }
        }

        public long Count()
        {
            lock (_lock)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Outbox;";
                return (long)cmd.ExecuteScalar()!;
            }
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private static void Execute(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static string GetOrCreateAgentId(SqliteConnection conn)
        {
            using var get = conn.CreateCommand();
            get.CommandText = "SELECT Value FROM Meta WHERE Key = 'AgentId';";
            if (get.ExecuteScalar() is string existing)
                return existing;

            string id = Guid.NewGuid().ToString("N");
            using var set = conn.CreateCommand();
            set.CommandText = "INSERT INTO Meta (Key, Value) VALUES ('AgentId', $id);";
            set.Parameters.AddWithValue("$id", id);
            set.ExecuteNonQuery();
            return id;
        }
    }
}
