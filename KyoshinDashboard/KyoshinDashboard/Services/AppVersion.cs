using System.Reflection;

namespace KyoshinDashboard.Services
{
    /// <summary>
    /// Version of this build: the git tag of a GitHub release (e.g. "1.2.0"), "0.0.0-local" for a local build.
    /// The installed package also has it in C:\Kyoshin\version.txt, which update.cmd compares with the latest release.
    /// </summary>
    public static class AppVersion
    {
        public static string Current { get; } =
            (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
            .Split('+')[0]; // the SDK appends "+<commit>"
    }
}
