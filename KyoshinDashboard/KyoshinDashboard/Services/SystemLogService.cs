using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text;

namespace KyoshinDashboard.Services
{
    /// <summary>
    /// Kyoshin entries of the Windows event log for the Nhật ký page (/Logs): what the agent and the server logged
    /// (Application log, sources KyoshinAgent / KyoshinDashboard / KyoshinServer, or ".NET Runtime" when started from
    /// run-console.cmd), Kyoshin crashes (Application Error, .NET Runtime) and the services' records in the System log
    /// (Service Control Manager). Plus the end of the agent's crash log and of update.log.
    /// The server runs on the same PC as the agent, so this is the whole picture of the factory side.
    /// </summary>
    public static class SystemLogService
    {
        public const int MAX_ENTRIES = 3000;
        private const int MAX_SCANNED_PER_LOG = 30000;   // other programs' records looked at to find Kyoshin ones
        private const int MAX_FILE_BYTES = 64 * 1024;

        /// <summary>
        /// Source: "agent" or "server"; Level: "error", "warning" or "info". Category: the logger name, or for a record
        /// written by Windows (service stopped, crash) the Windows source, e.g. "Service Control Manager".
        /// </summary>
        public record Entry(DateTime Time, string Source, string Level, string Category, string Message);

        // How Windows' own records name the agent and the server: exe, service name, display name (install-service.cmd)
        private static readonly string[] AgentNames = { "KyoshinAgent", "Kyoshin Agent" };
        private static readonly string[] ServerNames = { "KyoshinDashboard", "KyoshinServer", "Kyoshin Server", "Kyoshin Dashboard" };

        public record LogFile(string Name, string Path, string Text);

        public static bool Available => OperatingSystem.IsWindows();

        /// <summary>Entries of the last <paramref name="period"/>, newest first; Truncated = more existed than were read.</summary>
        public static (List<Entry> Entries, bool Truncated) Read(TimeSpan period)
        {
            var entries = new List<Entry>();
            if (!OperatingSystem.IsWindows())
                return (entries, false);

            long ms = (long)period.TotalMilliseconds;
            string time = $"TimeCreated[timediff(@SystemTime) <= {ms}]";
            bool truncated = ReadLog("Application",
                $"*[System[(Provider[@Name='KyoshinAgent'] or Provider[@Name='KyoshinDashboard'] or Provider[@Name='KyoshinServer'] " +
                $"or Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error']) and {time}]]", entries);
            truncated |= ReadLog("System", $"*[System[Provider[@Name='Service Control Manager'] and {time}]]", entries);

            entries.Sort((a, b) => b.Time.CompareTo(a.Time));
            if (entries.Count > MAX_ENTRIES)
            {
                entries.RemoveRange(MAX_ENTRIES, entries.Count - MAX_ENTRIES);
                truncated = true;
            }
            return (entries, truncated);
        }

        [SupportedOSPlatform("windows")]
        private static bool ReadLog(string logName, string xpath, List<Entry> entries)
        {
            int scanned = 0, kept = 0;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true });
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        var entry = ToEntry(record);
                        if (entry != null)
                        {
                            entries.Add(entry);
                            if (++kept >= MAX_ENTRIES)
                                return true;
                        }
                    }
                    if (++scanned >= MAX_SCANNED_PER_LOG)
                        return true;
                }
            }
            catch (EventLogException)
            {
                // log missing or not readable by this account
            }
            catch (UnauthorizedAccessException)
            {
            }
            return false;
        }

        [SupportedOSPlatform("windows")]
        private static Entry? ToEntry(EventRecord record)
        {
            string provider = record.ProviderName ?? "";
            bool ours = provider is "KyoshinAgent" or "KyoshinDashboard" or "KyoshinServer";
            string text = (ours ? JoinProperties(record, "\n") : Describe(record)).Replace("\r\n", "\n").Trim();
            bool aboutAgent = AgentNames.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (!ours && !aboutAgent && !ServerNames.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return null;

            // Microsoft.Extensions.Logging writes "Category: X\nEventId: N\n\nmessage"
            string category = "";
            string message = text;
            if (text.StartsWith("Category: "))
            {
                int lineEnd = text.IndexOf('\n');
                category = (lineEnd < 0 ? text[10..] : text[10..lineEnd]).Trim();
                int body = text.IndexOf("\n\n", StringComparison.Ordinal);
                message = body < 0 ? "" : text[(body + 2)..].Trim();
            }

            string source =
                category.StartsWith("KyoshinAgent") || provider == "KyoshinAgent" ? "agent" :
                category.StartsWith("KyoshinDashboard") || ours ? "server" :
                aboutAgent ? "agent" : "server";
            if (category.Length == 0 && !ours)
                category = provider;

            string level = record.Level switch
            {
                1 or 2 => "error",   // critical, error
                3 => "warning",
                _ => "info"
            };

            return new Entry(record.TimeCreated ?? DateTime.MinValue, source, level, category, message);
        }

        // Windows' own text for the event (Application Error, Service Control Manager ...), else its raw values
        [SupportedOSPlatform("windows")]
        private static string Describe(EventRecord record)
        {
            try
            {
                string? text = record.FormatDescription();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch (EventLogException)
            {
            }
            return JoinProperties(record, " | ");
        }

        [SupportedOSPlatform("windows")]
        private static string JoinProperties(EventRecord record, string separator) =>
            string.Join(separator, record.Properties.Select(p => p.Value?.ToString() ?? ""));

        /// <summary>
        /// The agent's crash log and update.log (the end of each), when they exist. Install layout: C:\Kyoshin\KyoshinServer
        /// (this app), C:\Kyoshin\KyoshinAgent, C:\Kyoshin\update.log; run from the zip: Server\ and Agent\ side by side.
        /// </summary>
        public static List<LogFile> ReadFiles()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var files = new List<LogFile>();
            foreach (var agentFolder in new[] { "KyoshinAgent", "Agent" })
            {
                var crashLog = ReadFile("agent-crash.log", Path.Combine(root, agentFolder, "agent-crash.log"));
                if (crashLog != null)
                {
                    files.Add(crashLog);
                    break;
                }
            }
            var updateLog = ReadFile("update.log", Path.Combine(root, "update.log"));
            if (updateLog != null)
                files.Add(updateLog);
            return files;
        }

        private static LogFile? ReadFile(string name, string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, stream.Length - MAX_FILE_BYTES);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                if (start > 0)
                    text = "…" + text[(text.IndexOf('\n') + 1)..];  // starts mid-line: drop the partial line
                return new LogFile(name, path, text);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
