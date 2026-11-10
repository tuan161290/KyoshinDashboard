using System.Diagnostics;
using System.Reflection;

namespace KyoshinAgent
{
    /// <summary>
    /// Writes exceptions that nothing caught to agent-crash.log next to the exe and to the Windows event log
    /// (source KyoshinAgent). Without it a crash leaves only "Application Error ... e0434352" in the event log,
    /// with no exception or stack trace.
    /// </summary>
    public static class CrashLog
    {
        public static void Register()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write("Agent crashed — unhandled exception", e.ExceptionObject, error: true);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("Unobserved task exception (the agent keeps running)", e.Exception, error: false);
                e.SetObserved();
            };
        }

        private static void Write(string title, object exception, bool error)
        {
            string version = (typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {title} (agent {version}){Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "agent-crash.log"), text);
            }
            catch (Exception)
            {
            }
            try
            {
                if (OperatingSystem.IsWindows())
                    EventLog.WriteEntry("KyoshinAgent", text.Length > 30000 ? text[..30000] : text,
                        error ? EventLogEntryType.Error : EventLogEntryType.Warning);
            }
            catch (Exception)
            {
            }
        }
    }
}
