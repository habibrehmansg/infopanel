using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Sdk;
using InfoPanel.SensorHelper;
using Serilog;
using Serilog.Events;

namespace InfoPanel.Panels;

/// <summary>
/// The engine executable's panel host mode: <c>InfoPanel --panel-host --pipe &lt;name&gt; --package &lt;dir&gt;
/// --log-dir &lt;dir&gt; --log-level &lt;level&gt; --engine-pid &lt;pid&gt; --engine-start &lt;its start time&gt;</c>, one
/// process a panel package, run as the engine's user, never elevated. Before any package code loads it watches the
/// engine's process (its id and start time) and ends once that is gone (<see cref="WatchEngine"/>). It connects to the engine's current-user pipe, reads the package's <c>panel.json</c>, loads
/// <c>{folder}.dll</c> in a load context of its own (the panel SDK shared with the engine's), makes each public
/// <see cref="IPanelDriver"/> with a public constructor that takes nothing and whose family the manifest declares, and
/// serves the engine (<see cref="PanelHostServer"/>) until a shutdown or the pipe's end. It writes only its log
/// (<c>{log-dir}/{folder}-{yyyyMMdd}.log</c>, daily, three kept, 10 MiB a file). When the pipe ends without a shutdown
/// its sessions get <see cref="PanelHostServer.CloseGrace"/> to close; then it ends through the operating system's own
/// exit, whatever threads a driver left.
/// </summary>
public static class PanelHostEntry
{
    public const string Flag = "--panel-host";

    /// <summary>How long the host waits for the engine's end of the pipe.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Whether <paramref name="args"/> start the panel host mode (<see cref="Flag"/> first).</summary>
    public static bool IsHostMode(string[] args) => args.Length > 0 && args[0] == Flag;

    /// <summary>Runs the host and ends the process with its exit code: 0 after a shutdown or the pipe's end, 1 on a fatal error, 2 for bad arguments, 3 once the engine is gone.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var code = await RunHostAsync(args).ConfigureAwait(false);
        ExitNow(code); // a driver's threads and exit handlers can't hold the process
        return code;
    }

    private static async Task<int> RunHostAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--pipe" or "--package" or "--log-dir" or "--log-level" or "--engine-pid" or "--engine-start") || values.ContainsKey(args[i]))
            {
                Console.Error.WriteLine("InfoPanel panel host: usage: --panel-host --pipe <name> --package <dir> --log-dir <dir> [--log-level <level>] --engine-pid <pid> --engine-start <its start time>");
                return 2;
            }
            values[args[i]] = args[i + 1];
        }
        if (!values.TryGetValue("--pipe", out var pipeName) || !values.TryGetValue("--package", out var package) || !values.TryGetValue("--log-dir", out var logDir)
            || !Enum.TryParse<LogEventLevel>(values.GetValueOrDefault("--log-level", "information"), ignoreCase: true, out var level)
            || !int.TryParse(values.GetValueOrDefault("--engine-pid"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var enginePid)
            || !long.TryParse(values.GetValueOrDefault("--engine-start"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var engineStart))
        {
            Console.Error.WriteLine("InfoPanel panel host: --pipe, --package, --log-dir, --engine-pid and --engine-start are needed; --log-level is debug, information, warning or error.");
            return 2;
        }

        var directory = Path.GetFullPath(package);
        var folder = Path.GetFileName(directory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(Path.Combine(Path.GetFullPath(logDir), folder + "-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 3,
                fileSizeLimitBytes: 10 * 1024 * 1024)
            .CreateLogger();
        Usb.IUsbBackend? usb = null;
        try
        {
            WatchEngine(enginePid, engineStart);
            // The device seam the package's drivers get: libusb when it loads (else USB answers notSupported), HID, serial.
            if (Usb.LibUsb.LoadAndLog(Usb.LibUsb.DefaultCandidates(), Log.Logger) is { } library)
                usb = Usb.LibUsbBackend.Create(library, out _);
            var devices = new Devices.EnginePanelDevices(usb, new Devices.HidSharpSystem(Log.Logger), new Devices.SerialPorts(Log.Logger), Log.Logger);
            var drivers = Load(directory, folder, Log.Logger, devices);
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = new CancellationTokenSource(ConnectTimeout))
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            Log.Information("Panel host for {Folder:l} connected: {Families:l}", folder, string.Join(", ", drivers.Select(d => d.Family)));
            await new PanelHostServer(drivers, Log.Logger).ServeAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Panel host fatal error");
            Console.Error.WriteLine($"InfoPanel panel host: {ex.Message}");
            return 1;
        }
        finally
        {
            usb?.Dispose();
            Log.Information("Panel host stopped");
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Started before any package code loads: a thread that ends the host (exit code 3) once the engine's process is gone,
    /// whatever a driver does meanwhile (a constructor that never returns, a thread that holds the process). The process
    /// <paramref name="pid"/> is the engine only when it started at <paramref name="start"/> (<see cref="StartTimeOf"/>):
    /// one that took the engine's id after it ended is taken as the engine gone. Once the pipe is connected its end ends
    /// the host too (the serve loop's).
    /// </summary>
    private static void WatchEngine(int pid, long start)
    {
        var engine = HelperParent.Open(pid);
        if (engine == null || (OperatingSystem.IsLinux() ? LinuxStartTicks(pid) : engine.StartTime) != start)
        {
            engine?.Dispose();
            EngineGone(pid);
            return;
        }
        new Thread(() =>
        {
            engine.Exited.Wait();
            EngineGone(pid);
        })
        { IsBackground = true, Name = "Panel host: the engine's watch" }.Start();
    }

    /// <summary>
    /// The start time of the process <paramref name="pid"/>, or null when it isn't running or can't be read: what the
    /// engine passes its hosts (<c>--engine-start</c>) and a host compares, read as the sensor helper reads it
    /// (<see cref="HelperParent"/>); on Linux the kernel's own start in clock ticks since boot (<c>/proc/{pid}/stat</c>),
    /// since .NET's reading there moves with the clock between readings.
    /// </summary>
    public static long? StartTimeOf(int pid)
    {
        if (OperatingSystem.IsLinux())
            return LinuxStartTicks(pid);
        using var process = HelperParent.Open(pid);
        return process?.StartTime;
    }

    private static long? LinuxStartTicks(int pid)
    {
        try
        {
            return LinuxStartTicks(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The 22nd field (starttime) of a <c>/proc/{pid}/stat</c> line, read after the name's closing bracket (a name may hold spaces and brackets).</summary>
    internal static long? LinuxStartTicks(string stat)
    {
        var end = stat.LastIndexOf(')');
        if (end < 0)
            return null;
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries); // from the 3rd, state
        return fields.Length > 22 - 3 && long.TryParse(fields[22 - 3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : null;
    }

    private static void EngineGone(int pid)
    {
        Log.Warning("The engine (process {Pid}) is gone, so the panel host ends", pid);
        Log.CloseAndFlush();
        ExitNow(3);
    }

    /// <summary>
    /// The package's drivers: its manifest's families, each made from the DLL's public driver for it, through its public
    /// constructor that takes the device seam (given <paramref name="devices"/>, the host's), else the one that takes nothing.
    /// </summary>
    /// <exception cref="InvalidDataException">Its manifest can't be used, or a family it declares has no driver.</exception>
    internal static IReadOnlyList<IPanelDriver> Load(string directory, string folder, Serilog.ILogger logger, IPanelDevices? devices = null)
    {
        var manifest = PanelPackages.ReadManifest(directory, out var error) ?? throw new InvalidDataException($"its {PanelPackages.ManifestName} {error}");
        if (PanelPackages.SdkProblem(manifest.Sdk) is { } sdk)
            throw new InvalidDataException(sdk);
        var dll = Path.Combine(directory, folder + ".dll");
        var assembly = new PanelPackageLoadContext(dll).LoadFromAssemblyPath(dll);
        var drivers = new List<IPanelDriver>();
        foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPanelDriver).IsAssignableFrom(t)))
        {
            var withDevices = devices != null ? type.GetConstructor([typeof(IPanelDevices)]) : null;
            if (withDevices == null && type.GetConstructor(Type.EmptyTypes) == null)
            {
                logger.Warning("{Type:l} isn't loaded: a driver has a public constructor that takes the device seam (IPanelDevices) or nothing", type.FullName);
                continue;
            }
            var driver = withDevices != null ? (IPanelDriver)withDevices.Invoke([devices]) : (IPanelDriver)Activator.CreateInstance(type)!;
            if (manifest.Families.All(f => f.Id != driver.Family) || drivers.Any(d => d.Family == driver.Family))
            {
                logger.Warning("{Type:l} isn't loaded: its family {Family:l} isn't one panel.json declares once", type.FullName, driver.Family);
                continue;
            }
            drivers.Add(driver);
        }
        // A family declared and not driven would read as ready while every probe of it fails: the package isn't loaded.
        if (manifest.Families.FirstOrDefault(f => drivers.All(d => d.Family != f.Id)) is { } missing)
            throw new InvalidDataException($"no driver in {Path.GetFileName(dll)} is for the family {missing.Id} its {PanelPackages.ManifestName} declares");
        return drivers;
    }

    /// <summary>A package's own load context: its dependencies beside it, the panel SDK (and the framework) the engine's.</summary>
    private sealed class PanelPackageLoadContext(string dll) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(dll), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(dll);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IPanelDriver).Assembly.GetName().Name)
                return null; // the engine's, so IPanelDriver is one type on both sides
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>Ends the process at once, through the operating system: no managed exit handler or finaliser runs.</summary>
    private static void ExitNow(int code)
    {
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
        else
            SysExit(code);
        Environment.FailFast("The panel host couldn't end itself.");
    }

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void SysExit(int status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
