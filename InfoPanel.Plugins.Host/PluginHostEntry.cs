using System.CommandLine;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using StreamJsonRpc;

namespace InfoPanel.Plugins.Host;

/// <summary>
/// The engine executable's plugin host mode: <c>InfoPanel --plugin-host --pipe &lt;name&gt; --plugin &lt;dll&gt;
/// --log-dir &lt;dir&gt; --settings-dir &lt;dir&gt; --log-level &lt;level&gt;</c>. It connects to the engine's pipe, loads
/// the plugin DLL and serves <see cref="HostService"/> until the engine shuts it down or the pipe ends. It writes only its
/// log (<c>{log-dir}/{plugin folder}-{yyyyMMdd}.log</c>, daily, three kept, 10 MiB a file, never to the console) and the
/// plugins' settings (<c>{settings-dir}/{plugin id}.json</c>). When the pipe ends without a shutdown (the engine
/// stopped or died) its plugins get 2 s to close; then the process exits, and one still alive 5 s after the pipe ended
/// ends itself, whatever threads or process-exit handlers a plugin left running: that end is the operating system's own
/// exit, which runs no managed code.
/// </summary>
public static class PluginHostEntry
{
    public const string Flag = "--plugin-host";

    /// <summary>How long plugins get to close when the pipe ends without a shutdown.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long after the pipe ended a host still alive ends itself.</summary>
    public static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(5);

    /// <summary>How long the host waits for the engine's end of the pipe.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public const long LogFileBytes = 10 * 1024 * 1024;
    public const int LogFilesKept = 3;

    /// <summary>Whether <paramref name="args"/> start the plugin host mode (<see cref="Flag"/> first).</summary>
    public static bool IsHostMode(string[] args) => args.Length > 0 && args[0] == Flag;

    /// <summary>
    /// Runs the host on the arguments after <see cref="Flag"/> and ends the process with its exit code: 0 after a
    /// shutdown or the pipe's end, 1 on a fatal error, 2 for bad arguments. Never returns normally.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var code = await RunHostAsync(args).ConfigureAwait(false);
        // A plugin's foreground threads would keep the process alive after Main returns, and its process-exit handlers run
        // inside Environment.Exit with no time limit: the deadline ends the process whatever they do.
        StartExitDeadline(code);
        Environment.Exit(code);
        return code;
    }

    private static async Task<int> RunHostAsync(string[] args)
    {
        var pipeOption = new Option<string>("--pipe") { Description = "The engine's pipe", Required = true };
        var pluginOption = new Option<string>("--plugin") { Description = "The plugin DLL", Required = true };
        var logDirOption = new Option<string>("--log-dir") { Description = "The folder for the host's log", Required = true };
        var settingsDirOption = new Option<string>("--settings-dir") { Description = "The folder for the plugins' settings", Required = true };
        var logLevelOption = new Option<string>("--log-level") { Description = "debug, information, warning or error", DefaultValueFactory = _ => "information" };

        var root = new RootCommand("InfoPanel plugin host");
        root.Options.Add(pipeOption);
        root.Options.Add(pluginOption);
        root.Options.Add(logDirOption);
        root.Options.Add(settingsDirOption);
        root.Options.Add(logLevelOption);

        var parsed = root.Parse(args.Length > 0 && args[0] == Flag ? args[1..] : args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors)
                Console.Error.WriteLine($"InfoPanel plugin host: {error.Message}");
            return 2;
        }

        if (!Enum.TryParse<LogEventLevel>(parsed.GetValue(logLevelOption), ignoreCase: true, out var level))
        {
            Console.Error.WriteLine("InfoPanel plugin host: --log-level must be debug, information, warning or error.");
            return 2;
        }

        var pluginPath = Path.GetFullPath(parsed.GetValue(pluginOption)!);
        var folder = Path.GetFileName(Path.GetDirectoryName(pluginPath)) is { Length: > 0 } f ? f : Path.GetFileNameWithoutExtension(pluginPath);
        var logDir = Path.GetFullPath(parsed.GetValue(logDirOption)!);
        var settingsDir = Path.GetFullPath(parsed.GetValue(settingsDirOption)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(Path.Combine(logDir, folder + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: LogFilesKept,
                fileSizeLimitBytes: LogFileBytes)
            .CreateLogger();

        try
        {
            return await ServeAsync(parsed.GetValue(pipeOption)!, pluginPath, settingsDir).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Plugin host fatal error");
            return 1;
        }
        finally
        {
            Log.Information("Plugin host stopped");
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ServeAsync(string pipeName, string pluginPath, string settingsDir)
    {
        Log.Information("Plugin host starting for {PluginPath}", pluginPath);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = new CancellationTokenSource(ConnectTimeout))
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        Log.Information("Connected to the engine");

        var hostService = new HostService(pluginPath, settingsDir);
        var shutDown = 0;
        using var jsonRpc = new JsonRpc(pipe, pipe);
        jsonRpc.AddLocalRpcTarget(hostService);
        hostService.SetJsonRpc(jsonRpc);
        hostService.OnShutdownRequested = () =>
        {
            Interlocked.Exchange(ref shutDown, 1);
            jsonRpc.Dispose();
        };
        jsonRpc.StartListening();

        try
        {
            await jsonRpc.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Information("The engine's pipe ended: {Reason}", ex.Message);
        }

        if (Volatile.Read(ref shutDown) == 1)
            return 0;

        // The engine went without a shutdown (it stopped or died): its plugins get a moment to close, and the process
        // ends by the deadline whatever a plugin does.
        StartExitDeadline(0);
        Log.Information("The engine's pipe ended without a shutdown; closing the plugins");
        try
        {
            // On the pool: a plugin that blocks before its first await can't hold this thread past the grace.
            await Task.Run(hostService.ShutdownAsync).WaitAsync(CloseGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning("The plugins didn't close within {Grace}; exiting", CloseGrace);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Closing the plugins failed; exiting");
        }

        return 0;
    }

    private static int _deadlineStarted;

    /// <summary>
    /// Ends the process with <paramref name="code"/> <see cref="ExitDeadline"/> from now, unless it has ended (once: a
    /// later call keeps the first deadline). The end is the operating system's (<see cref="ExitNow"/>), not
    /// <see cref="Environment.Exit"/>: that runs process-exit handlers, which a plugin can make hang, and a second call
    /// waits for the first.
    /// </summary>
    private static void StartExitDeadline(int code)
    {
        if (Interlocked.Exchange(ref _deadlineStarted, 1) == 1)
            return;
        var deadline = new Thread(() =>
        {
            Thread.Sleep(ExitDeadline);
            ExitNow(code);
        })
        { IsBackground = true, Name = "plugin-host-exit-deadline" };
        deadline.Start();
    }

    /// <summary>Ends the process at once, through the operating system: no managed exit handler or finaliser runs.</summary>
    internal static void ExitNow(int code)
    {
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
        else
            SysExit(code);
        // Neither returns; should one fail, the runtime's own immediate end.
        Environment.FailFast("The plugin host couldn't end itself.");
    }

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void SysExit(int status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
