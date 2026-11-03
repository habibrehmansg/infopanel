using InfoPanel.SensorHelper;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>What the Libre source's elevation needs of Windows (<see cref="WindowsLibreElevationSystem"/>; fakes in tests).</summary>
public interface ILibreElevationSystem
{
    /// <summary>Whether the engine itself runs with administrator rights.</summary>
    bool EngineElevated { get; }

    /// <summary>The SID of the user the engine runs as; null when it can't be told.</summary>
    string? UserSid { get; }

    /// <summary>Program Files from the known-folder API (<c>FOLDERID_ProgramFiles</c>); null when it can't be told.</summary>
    string? ProgramFiles { get; }

    /// <summary>The final path of an open file: every link on the way resolved (<see cref="PawnIoFinalPath.Of"/> on Windows).</summary>
    string FinalPathOf(FileStream file);

    /// <summary>The program and arguments of <paramref name="sid"/>'s helper task (<see cref="HelperTask.PathOf"/>), null when it has none.</summary>
    (string Path, string Arguments)? TaskAction(string sid);

    /// <summary>Starts <paramref name="sid"/>'s helper task (<c>schtasks /Run</c>); throws when Windows refuses.</summary>
    void RunTask(string sid);

    /// <summary>The SID that owns the pipe <paramref name="pipe"/> is connected to (<c>GetSecurityInfo</c> on its handle); null when it can't be told.</summary>
    string? PipeOwner(NamedPipeClientStream pipe);

    /// <summary>Windows' system folder (<c>GetSystemDirectory</c>, never an environment variable), where its command processor is.</summary>
    string SystemDirectory => Environment.SystemDirectory;
}

/// <summary>
/// "Read all sensors": the Libre source's elevation (<c>POST</c> and <c>DELETE /api/sensors/sources/libre/elevation</c>).
/// Unelevated, LibreHardwareMonitor reads fewer sensors even with the PawnIO driver; this sets up a task that runs the
/// sensor helper as SYSTEM for the engine's user (<see cref="HelperTask"/>), so the engine, still unelevated, reads every
/// sensor from then on with no prompt. Setting it up runs this install's own helper with <c>--register --user &lt;the
/// engine's user SID&gt;</c> through Windows' command processor, run by the shell with <c>runas</c> (Windows asks once;
/// for a standard user an administrator approves it), which clears .NET's variables first
/// (<see cref="CommandProcessorArguments"/>), and turning it off runs <c>--unregister</c> the same way. The launch is W5.1's
/// (<see cref="PawnIoInstaller"/>): the helper held open for reading and shared for reading only until the launch
/// returns, a link refused, launched by the path its open handle resolves to (<see cref="PawnIoInstaller.LaunchPathOf"/>),
/// and only when that path is under Program Files: a helper anywhere else is <c>unavailable</c> · <c>notInstalled</c>,
/// and nothing is offered (a user-writable helper run by a SYSTEM task would be a way to administrator rights; the helper
/// checks the same itself). One change at a time, waited for until it ends, never killed; the engine stopping abandons
/// the wait. Exit 0 is <c>on</c> (or <c>off</c>) and starts a new sensor helper (<see cref="LibreSource.HelperChanged"/>);
/// 1223 from the launch, Windows' prompt declined, is <c>declined</c>; anything else <c>failed</c>, with the exit code
/// when it exited.
/// <para>
/// Its state: <c>unavailable</c> · <c>notWindows</c> without Windows (another OS, a replay), · <c>engineElevated</c> when
/// the engine runs as administrator (its own child reads everything), · <c>notInstalled</c> when the helper isn't under
/// Program Files; <c>settingUp</c> while a change runs; <c>on</c> while the user's task exists and runs this install's
/// helper with <c>--serve --user &lt;SID&gt;</c>; else <c>off</c>. The task is read at most every
/// <see cref="TaskCheckInterval"/>, and again after each change.
/// </para>
/// </summary>
public sealed class LibreElevation : IDisposable
{
    public const string Off = "off";
    public const string On = "on";
    public const string SettingUp = "settingUp";
    public const string Unavailable = "unavailable";

    public const string NotInstalled = "notInstalled";
    public const string NotWindows = "notWindows";
    public const string EngineElevated = "engineElevated";

    /// <summary>How long a reading of the user's task stands for the state (the helper's start reads it afresh).</summary>
    public static readonly TimeSpan TaskCheckInterval = TimeSpan.FromSeconds(10);

    private readonly string _helperPath;
    private readonly ILibreElevationSystem? _system;
    private readonly IPawnIoLauncher _launcher;
    private readonly Action _helperChanged;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private bool _changing;
    private bool _disposed;
    private LibreElevationChange? _last;
    private Task? _run;
    private (string Sid, bool Names, long At)? _task;
    // Counts the changes ended: a reading of the task begun before one ended is stale, and isn't kept.
    private long _changesEnded;
    private bool _queryLogged;
    private LibreHelperFallback? _fallback;

    /// <param name="helperPath">This install's sensor helper (<see cref="LibreHelperProcess.DefaultPath"/>).</param>
    /// <param name="system">Windows (<see cref="WindowsLibreElevationSystem"/>); null where there is none to set up (another OS, a replay).</param>
    /// <param name="launcher">Starts the helper elevated (<see cref="ShellPawnIoLauncher"/>; a fake in tests).</param>
    /// <param name="helperChanged">Called after a change that exited 0: <see cref="LibreSource.HelperChanged"/>.</param>
    public LibreElevation(string helperPath, ILibreElevationSystem? system, IPawnIoLauncher launcher, Action helperChanged, TimeProvider? time = null, ILogger? log = null)
    {
        _helperPath = helperPath;
        _system = system;
        _launcher = launcher;
        _helperChanged = helperChanged;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<LibreElevation>();
    }

    /// <summary>Windows, as the helper's start asks it (<see cref="LibreHelperChooser"/>); null without.</summary>
    public ILibreElevationSystem? System => _system;

    /// <summary>The last change's outcome since the engine started; null before the first ends.</summary>
    public LibreElevationChange? LastChange
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    /// <summary>The change running now, or the last one (for tests).</summary>
    internal Task? Run
    {
        get
        {
            lock (_gate)
                return _run;
        }
    }

    /// <summary>
    /// The last start of the sensor helper fell back from the task's helper to the engine's own, and why
    /// (<see cref="LibreHelperChooser"/>); null when it didn't. Reported while the state is <c>on</c>.
    /// </summary>
    public void HelperFellBack(LibreHelperFallback? why)
    {
        lock (_gate)
            _fallback = why;
    }

    /// <summary>The state now, with why when <c>unavailable</c>, the last change, and with <c>on</c> why the helper running isn't the task's, when it isn't.</summary>
    public LibreElevationReport Report()
    {
        var last = LastChange;
        if (_system is not { } system)
            return new LibreElevationReport(Unavailable, NotWindows, last);
        lock (_gate)
        {
            if (_changing)
                return new LibreElevationReport(SettingUp, null, last);
        }

        if (system.EngineElevated)
            return new LibreElevationReport(Unavailable, EngineElevated, last);
        if (InstalledPath() is not { } path)
            return new LibreElevationReport(Unavailable, NotInstalled, last);
        if (!TaskNames(path, fresh: false))
            return new LibreElevationReport(Off, null, last);
        lock (_gate)
            return new LibreElevationReport(On, null, last, _fallback);
    }

    /// <summary>
    /// The SID whose task the helper's start should run: the engine's user, when the engine runs unelevated, its helper
    /// is installed under Program Files, and that user's task runs this very helper with <c>--serve --user &lt;SID&gt;</c>
    /// (read now). Null otherwise: the helper starts as the engine's child. Throws what reading the task threw: whether
    /// it is set up isn't known (<see cref="LibreHelperChooser.TaskUnreadable"/>).
    /// </summary>
    public string? TaskUser()
    {
        if (_system is not { } system || system.EngineElevated || system.UserSid is not { } sid)
            return null;
        if (InstalledPath() is not { } path)
            return null;
        return TaskNames(path, fresh: true) ? sid : null;
    }

    /// <summary>
    /// Starts setting the task up (<paramref name="on"/>) or removing it: <see cref="ElevationStart.Busy"/> while a change
    /// runs, <see cref="ElevationStart.Unavailable"/> when the state is <c>unavailable</c> (nothing to set up here),
    /// else <see cref="ElevationStart.Started"/>; the launch and its wait run on their own, the outcome is
    /// <see cref="LastChange"/> once it ends.
    /// </summary>
    public ElevationStart Start(bool on)
    {
        if (_system is not { } system)
            return ElevationStart.Unavailable;
        lock (_gate)
        {
            if (_changing)
                return ElevationStart.Busy;
            _changing = true;
        }

        (FileStream File, string LaunchPath, string Sid)? opened = null;
        try
        {
            if (!system.EngineElevated && system.UserSid is { } sid && OpenInstalled() is { } file)
                opened = (file.File, file.LaunchPath, sid);
        }
        finally
        {
            if (opened is null)
            {
                lock (_gate)
                    _changing = false;
            }
        }

        if (opened is not { } checkedFile)
            return ElevationStart.Unavailable;

        var arguments = on ? HelperTask.RegisterArguments(checkedFile.Sid) : HelperTask.UnregisterArguments(checkedFile.Sid);
        _log.Information("Libre: {What} the sensor helper's task for {Sid}; Windows asks for administrator rights", on ? "setting up" : "removing", checkedFile.Sid);
        var started = _time.GetTimestamp();
        var run = Task.Run(() => ChangeAsync(on, checkedFile.File, checkedFile.LaunchPath, arguments, started));
        lock (_gate)
            _run = run;
        return ElevationStart.Started;
    }

    /// <summary>The helper's final path when it is installed under Program Files (checked on its open file), else null.</summary>
    private string? InstalledPath()
    {
        if (OpenInstalled() is not { } opened)
            return null;
        opened.File.Dispose();
        return opened.LaunchPath;
    }

    /// <summary>
    /// The helper, open for reading and shared for reading only, with the path to launch it by (its open handle's final
    /// path, <see cref="PawnIoInstaller.LaunchPathOf"/>), when it isn't a link and that path is under Program Files.
    /// </summary>
    private (FileStream File, string LaunchPath)? OpenInstalled()
    {
        if (_system is not { } system)
            return null;
        FileStream stream;
        try
        {
            stream = new FileStream(_helperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            if ((new FileInfo(_helperPath).Attributes & FileAttributes.ReparsePoint) == 0
                && PawnIoInstaller.LaunchPathOf(system.FinalPathOf(stream)) is { } launchPath
                && HelperTask.IsUnder(launchPath, system.ProgramFiles)
                && Quotable(launchPath))
                return (stream, launchPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Not told where it is: not installed, as far as the engine can say.
        }

        stream.Dispose();
        return null;
    }

    /// <summary>Whether the user's task runs the helper at <paramref name="path"/> with its <c>--serve</c> arguments: read now when <paramref name="fresh"/> (a failed reading throws), else at most every <see cref="TaskCheckInterval"/> (a failed reading is no).</summary>
    private bool TaskNames(string path, bool fresh)
    {
        if (_system is not { } system || system.UserSid is not { } sid)
            return false;
        long changes;
        lock (_gate)
        {
            if (!fresh && _task is { } known && known.Sid == sid && _time.GetElapsedTime(known.At) < TaskCheckInterval)
                return known.Names;
            changes = _changesEnded;
        }

        bool names;
        try
        {
            names = system.TaskAction(sid) is { } action
                && string.Equals(action.Path, path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(action.Arguments, HelperTask.ServeArguments(sid), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            names = false;
            bool log;
            lock (_gate)
            {
                log = !_queryLogged;
                _queryLogged = true;
            }

            if (log)
                _log.Warning(ex, "Libre: the sensor helper's task couldn't be read; it counts as not set up");
            // The helper's start (fresh) isn't told "no task": it may be set up, and the child it starts is a fallback.
            if (fresh)
                throw;
        }

        lock (_gate)
        {
            if (_changesEnded == changes) // else a change ended meanwhile: this reading may be from before it
                _task = (sid, names, _time.GetTimestamp());
        }

        return names;
    }

    private async Task ChangeAsync(bool on, FileStream file, string launchPath, string arguments, long started)
    {
        LibreElevationChange outcome;
        try
        {
            outcome = await LaunchAndWaitAsync(on, file, launchPath, arguments, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _log.Information("Libre: the engine stops; the sensor helper's {What} is left running, and its end isn't waited for", on ? "set-up" : "removal");
            lock (_gate)
                _changing = false;
            return;
        }
        catch (Exception ex)
        {
            outcome = new LibreElevationChange(LibreElevationChange.Failed, null);
            _log.Error(ex, "Libre: the sensor helper's {What} failed", on ? "set-up" : "removal");
        }

        bool disposed;
        lock (_gate)
        {
            _changing = false;
            _task = null; // read again from now on
            _changesEnded++;
            disposed = _disposed;
            if (!disposed)
            {
                _last = outcome;
                // Under the lock, so the new helper is asked for before the state shows the change, and nothing is called
                // back once Dispose has returned (the callback only marks the helper changed).
                if (outcome.Outcome is LibreElevationChange.On or LibreElevationChange.Off)
                    _helperChanged();
            }
        }

        if (disposed)
            return;

        _log.Information("Libre: the sensor helper's {What} ended {Outcome} after {Seconds:0} s", on ? "set-up" : "removal", outcome.Outcome, _time.GetElapsedTime(started).TotalSeconds);
        if (outcome.Code is { } code)
            _log.Warning("Libre: the sensor helper stopped with exit code {Code}", code);
    }

    private async Task<LibreElevationChange> LaunchAndWaitAsync(bool on, FileStream file, string launchPath, string arguments, CancellationToken stopping)
    {
        IPawnIoProcess? process;
        try
        {
            try
            {
                // Windows paths: the helper's folder, the listing of the variables cleared in it, the command processor.
                var folder = launchPath[..Math.Max(0, launchPath.LastIndexOf('\\'))];
                var listing = folder + @"\elevation-" + Guid.NewGuid().ToString("N") + ".tmp";
                var command = CommandProcessorArguments(launchPath, arguments, listing)
                    ?? throw new IOException("The sensor helper's path can't be given to the command processor.");
                process = _launcher.Start(new ProcessStartInfo(_system!.SystemDirectory.TrimEnd('\\') + @"\cmd.exe")
                {
                    Arguments = command,
                    UseShellExecute = true,
                    Verb = PawnIoInstaller.Verb,
                    WorkingDirectory = folder,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            finally
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == PawnIoInstaller.Declined)
        {
            return new LibreElevationChange(LibreElevationChange.Declined, null);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Libre: the sensor helper couldn't be started elevated");
            return new LibreElevationChange(LibreElevationChange.Failed, null);
        }

        if (process is null)
            return new LibreElevationChange(LibreElevationChange.Failed, null);

        using (process)
        {
            await process.WaitForExitAsync(stopping).ConfigureAwait(false);
            var code = process.ExitCode;
            return code == HelperTask.ExitDone
                ? new LibreElevationChange(on ? LibreElevationChange.On : LibreElevationChange.Off, null)
                : new LibreElevationChange(LibreElevationChange.Failed, code);
        }
    }

    /// <summary>
    /// The prefixes of the variables the command processor clears before it runs the helper elevated (case aside): .NET's
    /// host and runtime read them before <c>Main</c>, where nothing in the helper can refuse them: a profiler
    /// (<c>CORECLR_ENABLE_PROFILING</c>, <c>CORECLR_PROFILER_PATH_64</c>), another runtime (<c>DOTNET_ROOT</c>), extra
    /// dependencies (<c>DOTNET_ADDITIONAL_DEPS</c>), the host's trace file (<c>COREHOST_TRACEFILE</c>), a diagnostics port
    /// and every runtime setting (<c>DOTNET_*</c>, <c>COMPlus_*</c>), and .NET Framework's profiler (<c>COR_*</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> ClearedPrefixes = ["DOTNET_", "COMPlus_", "CORECLR_", "COREHOST_", "COR_"];

    /// <summary>
    /// The command line of Windows' command processor (<c>cmd.exe</c>) that runs the helper at <paramref name="helperPath"/>
    /// with <paramref name="helperArguments"/> after clearing every variable whose name starts with one of
    /// <see cref="ClearedPrefixes"/>. An elevated process takes the environment Windows builds for the user who
    /// approved it, which that user's own programs write (<c>HKCU\Environment</c>), not the engine's; the command
    /// processor is no .NET program, and reads none of them. It runs without AutoRun commands (<c>/d</c>, which are the
    /// user's to write too), with its extensions on and delayed expansion off whatever the registry says; it lists the
    /// variables into <paramref name="listing"/> (in the helper's folder, under Program Files, where only administrators
    /// write) and reads that file back in the same process (a <c>for /f</c> over a command would start another command
    /// processor, with AutoRun), deletes it, then checks each prefix again (a variable the listing can't name, one whose
    /// name holds a line break): one variable left exits
    /// <see cref="HelperTask.ExitEnvironmentLeft"/> and the helper isn't run. Its exit code is the helper's. Null when a
    /// path holds a character the command line can't quote (<c>"</c>, <c>%</c> or a control character).
    /// </summary>
    public static string? CommandProcessorArguments(string helperPath, string helperArguments, string listing)
    {
        if (!Quotable(helperPath) || !Quotable(listing))
            return null;
        // Each query is "(set PREFIX)": SET matches the prefix as written, spaces after it included (all but the one an
        // operator's space gives), so "set PREFIX 1>nul" would query "PREFIX  " and match nothing.
        var list = string.Join(" & ", ClearedPrefixes.Select(prefix => $"(set {prefix})"));
        var checks = string.Join(" & ", ClearedPrefixes.Select(prefix => $"(set {prefix}) 1>nul 2>nul && exit {HelperTask.ExitEnvironmentLeft}"));
        var script = $"({list}) 1>\"{listing}\" 2>nul & (for /f \"usebackq delims==\" %v in (\"{listing}\") do @set \"%v=\") & del /f /q \"{listing}\" 1>nul 2>nul & {checks} & \"{helperPath}\" {helperArguments}";
        return "/d /e:on /v:off /s /c \"" + script + "\"";
    }

    /// <summary>Whether <paramref name="path"/> can stand between double quotes on the command processor's line as it is: no <c>"</c>, no <c>%</c> (expanded even there), no control character.</summary>
    public static bool Quotable(string path)
    {
        foreach (var c in path)
        {
            if (c is '"' or '%' || char.IsControl(c))
                return false;
        }

        return path.Length > 0;
    }

    /// <summary>The engine stops: a change's wait is abandoned (its process left running); no outcome is published and nothing called back after.</summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _stopping.Cancel();
    }
}

/// <summary>What <see cref="LibreElevation.Start"/> did.</summary>
public enum ElevationStart
{
    Started,
    Busy,
    Unavailable,
}

/// <summary>The elevation's state (<see cref="LibreElevation.Report"/>): <c>off</c>, <c>on</c>, <c>settingUp</c> or <c>unavailable</c> with its reason, the last change, and with <c>on</c> why the helper running is the engine's own, when it is.</summary>
public sealed record LibreElevationReport(string State, string? Reason, LibreElevationChange? LastChange, LibreHelperFallback? Fallback = null);

/// <summary>How a change ended: <see cref="On"/>, <see cref="Off"/>, <see cref="Declined"/> or <see cref="Failed"/>, which has the helper's exit code when it exited.</summary>
public sealed record LibreElevationChange(string Outcome, int? Code)
{
    public const string On = "on";
    public const string Off = "off";
    public const string Declined = "declined";
    public const string Failed = "failed";
}

/// <summary>The Libre source's elevation on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLibreElevationSystem : ILibreElevationSystem
{
    /// <summary>How long <c>schtasks /Run</c> may take before the start counts as failed.</summary>
    private static readonly TimeSpan RunWait = TimeSpan.FromSeconds(8);

    public bool EngineElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string? UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
    }

    public string? ProgramFiles => HelperWindows.ProgramFiles();

    public string FinalPathOf(FileStream file) => PawnIoFinalPath.Of(file.SafeFileHandle);

    public (string Path, string Arguments)? TaskAction(string sid) => WindowsTaskScheduler.ActionOf(HelperTask.PathOf(sid));

    public void RunTask(string sid)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/Run");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(HelperTask.PathOf(sid));
        using var process = Process.Start(info) ?? throw new IOException("schtasks didn't start.");
        _ = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunWait))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            throw new TimeoutException("schtasks /Run didn't end in time.");
        }

        if (process.ExitCode != 0)
            throw new IOException($"schtasks /Run exited with {process.ExitCode}: {(error.IsCompleted ? error.Result.Trim() : "")}");
    }

    public string? PipeOwner(NamedPipeClientStream pipe) => WindowsHelperPipe.OwnerOf(pipe.SafePipeHandle);
}
