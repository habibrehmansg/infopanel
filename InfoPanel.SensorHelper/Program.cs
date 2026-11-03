using System.Reflection;

namespace InfoPanel.SensorHelper;

/// <summary>
/// <c>InfoPanel.SensorHelper --pipe &lt;name&gt; --parent &lt;engine's process id&gt; --parent-start &lt;its start
/// time&gt;</c>: serves LibreHardwareMonitor's sensors to the engine on that pipe and ends its own process
/// (<see cref="HelperProcess"/>) when the engine closes the pipe or its process ends (0, as when the process named isn't
/// the engine that started), after refusing what it was sent (3), or when LibreHardwareMonitor's computer fails to open
/// (4). A command line it doesn't take exits 2.
/// <para>
/// Its task (<see cref="HelperTask"/>, <see cref="HelperTaskCommands"/>): <c>--register --user &lt;SID&gt;</c> and
/// <c>--unregister --user &lt;SID&gt;</c>, run elevated, create and remove that user's task, run as SYSTEM;
/// <c>--serve --user &lt;SID&gt;</c> is the task's action. Their exit codes are <see cref="HelperTask"/>'s.
/// </para>
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

        if (HelperTaskCommandLine.IsTaskCommand(args))
        {
            var serve = new HelperServeOptions { CreatePipe = (name, sid) => WindowsHelperPipe.Create(name, sid), Client = WindowsHelperPipe.ClientOf };
            return HelperTaskCommands.RunAsync(args, new WindowsHelperTaskHost(), () => new LibreTree(), Version(), serve, Console.Error);
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
