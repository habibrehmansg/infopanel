using Serilog;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// Installs the PawnIO driver from the installer bundled with the engine (<c>POST /api/sensors/sources/libre/driver</c>):
/// <see cref="BundledVersion"/>'s <c>PawnIO_setup.exe</c>, at one fixed place beside the engine (<see cref="DefaultPath"/>),
/// never committed to the repository: a Windows package carries it. The file counts only when its SHA-256 is
/// <see cref="BundledSha256"/>; a missing file, another hash or an operating system other than Windows means no bundled
/// installer (<see cref="Available"/> null, and <see cref="Start"/> refuses). Nothing a client sends chooses a path or an
/// argument.
/// <para>
/// An install checks the hash again right before the launch, holding the file open for reading and shared for reading
/// only, so it can't be written, renamed or deleted, nor any folder on its way renamed, until the launch returns. A file
/// that is itself a link (a reparse point) is refused. The launch names the file the open handle resolves to (its final
/// path, <c>GetFinalPathNameByHandle</c>, with no links in it), never the fixed path again, so the file run is the file
/// hashed even when a folder on the fixed path is a junction retargeted meanwhile; a final path Windows' normalisation
/// would change (a name ending in a dot or a space, say) or too long for the shell isn't launched, and the install is
/// <c>failed</c> (<see cref="LaunchPathOf"/>). It runs with the fixed
/// <see cref="Arguments"/> through the shell with the <see cref="Verb"/> verb (the installer asks for administrator rights
/// itself, so Windows asks once), and is waited for until it ends, however long that is, never killed (it runs elevated,
/// and its prompt may sit open): one install at a time, so another is refused until then. The engine stopping abandons
/// the wait: no outcome is published and nothing called back after <see cref="Dispose"/>. Exit 0 is <c>installed</c>: the Libre source reads the driver again and starts a new
/// helper. 3010 and 1641 (since 2.2.0 the installer exits with Windows' error codes) are <c>restartNeeded</c>; the prompt
/// declined (error 1223 from the launch) is <c>declined</c>, and changes nothing; any other end is <c>failed</c>, with the
/// exit code when it exited (none when it couldn't be started). Every outcome is logged. The engine never uninstalls
/// PawnIO.
/// </para>
/// </summary>
public sealed class PawnIoInstaller : IDisposable
{
    /// <summary>The PawnIO release the engine installs.</summary>
    public const string BundledVersion = "2.2.0";

    /// <summary>Its installer's SHA-256 (3,410,960 bytes, signed by namazso), as its release publishes it.</summary>
    public const string BundledSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

    /// <summary>The installer's arguments, always these: install, with no window of its own.</summary>
    public const string Arguments = "-install -silent";

    /// <summary>The shell verb the installer is run with: Windows asks for administrator rights.</summary>
    public const string Verb = "runas";

    /// <summary>Windows' ERROR_CANCELLED: the prompt was declined.</summary>
    public const int Declined = 1223;

    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED.</summary>
    public const int RebootRequired = 3010;

    /// <summary>ERROR_SUCCESS_REBOOT_INITIATED.</summary>
    public const int RebootInitiated = 1641;

    /// <summary>The bundled installer's place: <c>pawnio/PawnIO_setup.exe</c> in the engine's own folder.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO_setup.exe");

    private readonly string _path;
    private readonly IPawnIoLauncher _launcher;
    private readonly Action _installed;
    private readonly bool _supported;
    private readonly string _sha256;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Func<FileStream, string> _finalPath;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private bool _installing;
    private bool _disposed;
    private PawnIoOutcome? _last;
    private Task? _run;
    private (long Length, DateTime Written, bool Matches)? _checked;
    private bool _wrongLogged;
    private bool _linkLogged;

    /// <param name="path">The installer's place (<see cref="DefaultPath"/>).</param>
    /// <param name="launcher">Starts it (<see cref="ShellPawnIoLauncher"/>; a fake in tests).</param>
    /// <param name="installed">Called after an install that exited 0: the Libre source's <see cref="LibreSource.DriverChanged"/>.</param>
    /// <param name="supported">Whether the engine may install it here: Windows, not a replay.</param>
    /// <param name="time">The clock an install's length is logged by.</param>
    /// <param name="sha256">The installer's SHA-256 (<see cref="BundledSha256"/>; the tests' own file's in tests).</param>
    /// <param name="finalPath">
    /// The path the open file resolves to: on Windows its final path (<see cref="PawnIoFinalPath.Of"/>); elsewhere, where
    /// no install runs, the path it was opened by; the tests' own in tests.
    /// </param>
    public PawnIoInstaller(string path, IPawnIoLauncher launcher, Action installed, bool supported, TimeProvider? time = null, ILogger? log = null,
        string sha256 = BundledSha256, Func<FileStream, string>? finalPath = null)
    {
        _path = path;
        _launcher = launcher;
        _installed = installed;
        _supported = supported;
        _sha256 = sha256;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<PawnIoInstaller>();
        _finalPath = finalPath ?? FinalPathOf;
    }

    private static string FinalPathOf(FileStream file) => OperatingSystem.IsWindows() ? PawnIoFinalPath.Of(file.SafeFileHandle) : file.Name;

    /// <summary>
    /// <see cref="BundledVersion"/> while the bundled installer is there with its hash (checked again only when the file's
    /// size or time changes); null when there is none.
    /// </summary>
    public string? Available
    {
        get
        {
            if (!_supported)
                return null;
            try
            {
                var file = new FileInfo(_path);
                if (!file.Exists || IsLink(file))
                    return null;
                var (length, written) = (file.Length, file.LastWriteTimeUtc);
                lock (_gate)
                {
                    if (_checked is { } known && known.Length == length && known.Written == written)
                        return known.Matches ? BundledVersion : null;
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var matches = Matches(stream);
                lock (_gate)
                    _checked = (length, written, matches);
                return matches ? BundledVersion : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Whether an install runs now.</summary>
    public bool Installing
    {
        get
        {
            lock (_gate)
                return _installing;
        }
    }

    /// <summary>The last install's outcome since the engine started; null before the first ends.</summary>
    public PawnIoOutcome? LastInstall
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    /// <summary>The install running now, or the last one (for tests).</summary>
    internal Task? Run
    {
        get
        {
            lock (_gate)
                return _run;
        }
    }

    /// <summary>
    /// Starts an install: <see cref="PawnIoStart.Busy"/> while one runs (until its installer ends, however long),
    /// <see cref="PawnIoStart.NoInstaller"/> without a bundled installer (none here, no file, a link, or another hash),
    /// else <see cref="PawnIoStart.Started"/>; the launch and the wait run on their own, and the outcome is
    /// <see cref="LastInstall"/> once it ends.
    /// </summary>
    public PawnIoStart Start()
    {
        if (!_supported)
            return PawnIoStart.NoInstaller;
        lock (_gate)
        {
            if (_installing)
                return PawnIoStart.Busy;
            _installing = true;
        }

        (FileStream File, string? LaunchPath)? opened = null;
        try
        {
            opened = OpenChecked();
        }
        finally
        {
            if (opened is null)
            {
                lock (_gate)
                    _installing = false;
            }
        }

        if (opened is not { } checkedFile)
            return PawnIoStart.NoInstaller;

        _log.Information("PawnIO: installing {Version} from {Path}; Windows asks for administrator rights", BundledVersion, checkedFile.LaunchPath ?? _path);
        var started = _time.GetTimestamp();
        var run = Task.Run(() => InstallAsync(checkedFile.File, checkedFile.LaunchPath, started));
        lock (_gate)
            _run = run;
        return PawnIoStart.Started;
    }

    /// <summary>
    /// The installer, open for reading only and shared for reading only, when it isn't a link and its hash is the pinned
    /// one; with the path to launch it by, the one the open handle resolves to (<see cref="LaunchPathOf"/>; null when it
    /// can't be launched safely).
    /// </summary>
    private (FileStream File, string? LaunchPath)? OpenChecked()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("PawnIO: no installer to run at {Path} ({Message})", _path, ex.Message);
            return null;
        }

        try
        {
            if (IsLink(new FileInfo(_path)))
            {
                LogLinkOnce();
                stream.Dispose();
                return null;
            }

            // Held open for reading only, the file can't change: what this check finds is what Available says from now on.
            var matches = Matches(stream);
            lock (_gate)
                _checked = (stream.Length, File.GetLastWriteTimeUtc(_path), matches);
            if (!matches)
            {
                stream.Dispose();
                return null;
            }

            return (stream, LaunchPathOf(_finalPath(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Warning("PawnIO: the installer at {Path} couldn't be checked ({Message}); it isn't run", _path, ex.Message);
            stream.Dispose();
            return null;
        }
    }

    /// <summary>Whether the installer's own entry is a link (a symbolic link, a junction: any reparse point).</summary>
    private static bool IsLink(FileInfo file) => (file.Attributes & FileAttributes.ReparsePoint) != 0;

    private void LogLinkOnce()
    {
        lock (_gate)
        {
            if (_linkLogged)
                return;
            _linkLogged = true;
        }

        _log.Warning("PawnIO: the installer at {Path} is a link, not a file; it isn't offered or run", _path);
    }

    /// <summary>
    /// The path the shell launches the installer by: the open handle's final path without Windows' <c>\\?\</c> prefix
    /// (<c>\\?\UNC\</c> as <c>\\</c>), only when that names the very same file: the rest fits in <c>MAX_PATH</c>, which
    /// the shell's launch takes, and Windows' normalisation, which the prefix turned off, changes nothing in it (no name
    /// ends in a dot or a space, none is empty, and on Windows <see cref="Path.GetFullPath(string)"/> gives it back as it
    /// is). Otherwise null: it isn't launched at all, since a launch by the <c>\\?\</c> path itself is untried.
    /// </summary>
    public static string? LaunchPathOf(string finalPath)
    {
        const int MaxPath = 260; // with the terminating null
        var plain = finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..]
            : finalPath;
        return plain.Length < MaxPath && NormalisedAsItIs(plain) ? plain : null;
    }

    /// <summary>
    /// Whether Windows' normalisation leaves <paramref name="path"/> (<c>C:\…</c> or <c>\\server\share\…</c>) as it is: it
    /// drops a name's trailing dots and spaces (<c>verified.</c> would be <c>verified</c>, another folder), folds empty
    /// names and resolves <c>.</c> and <c>..</c>.
    /// </summary>
    private static bool NormalisedAsItIs(string path)
    {
        var names = path.StartsWith(@"\\", StringComparison.Ordinal) ? path[2..] : path;
        foreach (var name in names.Split('\\'))
        {
            if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' '))
                return false;
        }

        return !OperatingSystem.IsWindows() || string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine stops: an install's wait is abandoned, its installer left running (never killed); from now on no outcome
    /// is published and nothing is called back, whenever the launch or the installer ends.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _stopping.Cancel();
    }

    /// <summary>Whether the stream's SHA-256 is the pinned one; another is logged once.</summary>
    private bool Matches(FileStream stream)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (string.Equals(hash, _sha256, StringComparison.Ordinal))
            return true;
        lock (_gate)
        {
            if (_wrongLogged)
                return false;
            _wrongLogged = true;
        }

        _log.Warning("PawnIO: the installer at {Path} has SHA-256 {Hash}, not {Expected}; it isn't offered or run", _path, hash, _sha256);
        return false;
    }

    private async Task InstallAsync(FileStream file, string? launchPath, long started)
    {
        PawnIoOutcome outcome;
        try
        {
            outcome = await LaunchAndWaitAsync(file, launchPath, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _log.Information("PawnIO: the engine stops; the installer is left running, and its end isn't waited for");
            lock (_gate)
                _installing = false;
            return;
        }
        catch (Exception ex)
        {
            outcome = new PawnIoOutcome(PawnIoOutcome.Failed, null);
            _log.Error(ex, "PawnIO: the install failed");
        }

        bool disposed;
        lock (_gate)
        {
            _installing = false;
            disposed = _disposed;
            if (!disposed)
            {
                _last = outcome;
                // Under the lock, so nothing is called back once Dispose has returned (the callback only marks the
                // driver changed).
                if (outcome.Outcome == PawnIoOutcome.Installed)
                    _installed();
            }
        }

        if (disposed)
        {
            _log.Information("PawnIO: the engine stops; the installer's end ({Outcome}) isn't taken", outcome.Outcome);
            return;
        }

        _log.Information("PawnIO: the install ended {Outcome} after {Seconds:0} s", outcome.Outcome, _time.GetElapsedTime(started).TotalSeconds);

        switch (outcome.Outcome)
        {
            case PawnIoOutcome.Installed:
                _log.Information("PawnIO: installed; the driver is read again and the sensor helper started again");
                break;
            case PawnIoOutcome.RestartNeeded:
                _log.Information("PawnIO: installed; Windows must restart to finish");
                break;
            case PawnIoOutcome.DeclinedOutcome:
                _log.Information("PawnIO: Windows' prompt was declined; nothing changed");
                break;
            default:
                if (outcome.Code is { } code)
                    _log.Warning("PawnIO: the installer stopped with exit code {Code}", code);
                break;
        }
    }

    /// <summary>
    /// Launches the installer by its open handle's path (the file held open until the launch returns) and waits for it to
    /// exit, until the engine stops; a path that can't be launched safely (null) is failed, with nothing launched.
    /// </summary>
    private async Task<PawnIoOutcome> LaunchAndWaitAsync(FileStream file, string? launchPath, CancellationToken stopping)
    {
        if (launchPath is null)
        {
            await file.DisposeAsync().ConfigureAwait(false);
            _log.Warning("PawnIO: the installer's path ({Path}) can't be launched safely; it isn't run", file.Name);
            return new PawnIoOutcome(PawnIoOutcome.Failed, null);
        }

        IPawnIoProcess? process;
        try
        {
            try
            {
                process = _launcher.Start(new ProcessStartInfo(launchPath)
                {
                    Arguments = Arguments,
                    UseShellExecute = true,
                    Verb = Verb,
                    WorkingDirectory = Path.GetDirectoryName(launchPath) ?? "",
                });
            }
            finally
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Declined)
        {
            return new PawnIoOutcome(PawnIoOutcome.DeclinedOutcome, null);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "PawnIO: the installer couldn't be started");
            return new PawnIoOutcome(PawnIoOutcome.Failed, null);
        }

        if (process is null)
        {
            _log.Warning("PawnIO: the installer couldn't be started (no process)");
            return new PawnIoOutcome(PawnIoOutcome.Failed, null);
        }

        using (process)
        {
            await process.WaitForExitAsync(stopping).ConfigureAwait(false);
            var code = process.ExitCode;
            _log.Information("PawnIO: the installer exited with code {Code}", code);
            return code switch
            {
                0 => new PawnIoOutcome(PawnIoOutcome.Installed, null),
                RebootRequired or RebootInitiated => new PawnIoOutcome(PawnIoOutcome.RestartNeeded, null),
                _ => new PawnIoOutcome(PawnIoOutcome.Failed, code),
            };
        }
    }
}

/// <summary>What <see cref="PawnIoInstaller.Start"/> did.</summary>
public enum PawnIoStart
{
    Started,
    Busy,
    NoInstaller,
}

/// <summary>
/// How an install ended: <see cref="Installed"/>, <see cref="RestartNeeded"/>, <see cref="DeclinedOutcome"/> or
/// <see cref="Failed"/>, which has the installer's exit code when it exited (none when it couldn't be started).
/// </summary>
public sealed record PawnIoOutcome(string Outcome, int? Code)
{
    public const string Installed = "installed";
    public const string RestartNeeded = "restartNeeded";
    public const string DeclinedOutcome = "declined";
    public const string Failed = "failed";
}

/// <summary>Starts the installer: the shell (<see cref="ShellPawnIoLauncher"/>), a fake in tests.</summary>
public interface IPawnIoLauncher
{
    /// <summary>
    /// Starts <paramref name="start"/> and returns its process (null when none was started); throws
    /// <see cref="Win32Exception"/> 1223 when Windows' prompt is declined.
    /// </summary>
    IPawnIoProcess? Start(ProcessStartInfo start);
}

/// <summary>A started installer: waited for and its exit code read. Never killed.</summary>
public interface IPawnIoProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken cancel);

    int ExitCode { get; }
}

/// <summary>Starts the installer through the shell (<see cref="Process.Start(ProcessStartInfo)"/>).</summary>
public sealed class ShellPawnIoLauncher : IPawnIoLauncher
{
    public IPawnIoProcess? Start(ProcessStartInfo start) => Process.Start(start) is { } process ? new Started(process) : null;

    private sealed class Started(Process process) : IPawnIoProcess
    {
        public Task WaitForExitAsync(CancellationToken cancel) => process.WaitForExitAsync(cancel);

        public int ExitCode => process.ExitCode;

        public void Dispose() => process.Dispose();
    }
}

/// <summary>The final path of an open file on Windows: where its handle leads, every link on the way resolved.</summary>
[SupportedOSPlatform("windows")]
public static partial class PawnIoFinalPath
{
    /// <summary>
    /// <c>GetFinalPathNameByHandleW</c> with <c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: <c>\\?\C:\…</c> or
    /// <c>\\?\UNC\server\share\…</c>. Throws <see cref="Win32Exception"/> when Windows can't say.
    /// </summary>
    public static unsafe string Of(SafeFileHandle file)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
                length = GetFinalPathNameByHandle(file, chars, (uint)buffer.Length, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[length]; // the length it needs, its terminating null included
        }
    }

    // DWORD GetFinalPathNameByHandleW(HANDLE hFile, LPWSTR lpszFilePath, DWORD cchFilePath, DWORD dwFlags)
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
