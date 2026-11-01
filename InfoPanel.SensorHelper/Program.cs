using System.Reflection;

namespace InfoPanel.SensorHelper;

/// <summary>
/// <c>InfoPanel.SensorHelper --pipe &lt;name&gt; --parent &lt;engine's process id&gt; --parent-start &lt;its start
/// time&gt;</c>: serves LibreHardwareMonitor's sensors to the engine on that pipe and ends its own process
/// (<see cref="HelperProcess"/>) when the engine closes the pipe or its process ends (0, as when the process named isn't
/// the engine that started), after refusing what it was sent (3), or when LibreHardwareMonitor's computer fails to open
/// (4). A command line it doesn't take exits 2.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The sensor helper reads LibreHardwareMonitor, which runs on Windows only.");
            return Task.FromResult(HelperProcess.ExitUsage);
        }

        return HelperProcess.RunAsync(args, () => new LibreTree(), Version());
    }

    private static string Version()
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
