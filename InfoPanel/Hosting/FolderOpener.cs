using System.Diagnostics;

namespace InfoPanel.Hosting;

/// <summary>
/// Opens a folder of the engine's in the file manager of the computer it runs on (<c>POST /api/folders/{folder}/open</c>).
/// The seam tests replace, so no test opens a file manager.
/// </summary>
public interface IFolderOpener
{
    /// <summary>Asks the system's file manager to open <paramref name="folder"/>; throws when it can't.</summary>
    void Open(string folder);
}

/// <summary>
/// The system's file manager (1.4.x opens its folders with <c>explorer.exe</c>): <c>open</c> on macOS,
/// <c>explorer.exe</c> on Windows, <c>xdg-open</c> elsewhere, each given the folder as its one argument and never through a
/// shell. A folder that isn't there is refused before anything starts; the engine doesn't wait for the file manager.
/// Repeats start nothing: a folder started less than <see cref="Repeat"/> ago (a double click) isn't started again, and
/// while <see cref="MaxStarting"/> file managers haven't exited (a stuck one) no other starts; the request is answered as
/// done all the same.
/// </summary>
public sealed class FolderOpener : IFolderOpener
{
    /// <summary>Which file manager a platform has.</summary>
    public enum Platform { MacOS, Windows, Linux }

    /// <summary>At most this many file managers starting at once (started and not yet exited).</summary>
    public const int MaxStarting = 4;

    /// <summary>A folder started less than this long ago isn't started again.</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromSeconds(1);

    private readonly Func<ProcessStartInfo, Task> _start;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _lastStart = new(StringComparer.Ordinal);
    private int _starting;

    /// <param name="start">Starts a command; its task completes when the process exits (the engine never waits on it).</param>
    public FolderOpener(Func<ProcessStartInfo, Task> start, TimeProvider time, ILogger? logger = null)
    {
        _start = start;
        _time = time;
        _logger = logger;
    }

    public FolderOpener(ILogger? logger = null) : this(StartProcess, TimeProvider.System, logger) { }

    /// <summary>The platform the engine runs on; Linux for every other Unix, whose desktops open folders with <c>xdg-open</c>.</summary>
    public static Platform Current =>
        OperatingSystem.IsMacOS() ? Platform.MacOS : OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;

    /// <summary>The command that opens <paramref name="folder"/> on <paramref name="platform"/>.</summary>
    public static ProcessStartInfo CommandFor(Platform platform, string folder)
    {
        var program = platform switch
        {
            Platform.MacOS => "open",
            Platform.Windows => "explorer.exe",
            _ => "xdg-open",
        };
        var command = new ProcessStartInfo(program) { UseShellExecute = false };
        command.ArgumentList.Add(folder);
        return command;
    }

    public void Open(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder {folder} isn't there.");

        long? before;
        long now;
        lock (_lock)
        {
            now = _time.GetTimestamp();
            if (_lastStart.TryGetValue(folder, out var last) && _time.GetElapsedTime(last, now) < Repeat)
                return;
            if (_starting >= MaxStarting)
            {
                _logger?.LogWarning("Not opening the folder {Folder}: {Count} file managers started earlier haven't exited yet", folder, _starting);
                return;
            }

            before = _lastStart.TryGetValue(folder, out var previous) ? previous : null;
            _lastStart[folder] = now;
            _starting++;
        }

        Task exited;
        try
        {
            exited = _start(CommandFor(Current, folder));
        }
        catch
        {
            // Nothing started: it holds no place, and the next request tries again. The folder's time is given back only if
            // it is still this request's own reservation: a later request (a second on) that started the folder keeps its time.
            lock (_lock)
            {
                _starting--;
                if (_lastStart.TryGetValue(folder, out var current) && current == now)
                {
                    if (before is { } previous)
                        _lastStart[folder] = previous;
                    else
                        _lastStart.Remove(folder);
                }
            }

            throw;
        }

        exited.ContinueWith(t =>
        {
            _ = t.Exception; // a file manager that ended in an error frees its place too; the error is no one's to report
            lock (_lock)
                _starting--;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts <paramref name="command"/>; the task completes when it exits, and nothing waits on it.</summary>
    private static Task StartProcess(ProcessStartInfo command)
    {
        var process = Process.Start(command);
        if (process == null)
            return Task.CompletedTask;
        return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
    }
}
