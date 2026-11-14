using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Plugins.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.Plugins;

/// <summary>What a plugin host process is started with (the engine executable's host mode, <see cref="PluginHostEntry"/>).</summary>
public sealed record PluginHostCommand(string PipeName, string PluginDll, string LogDirectory, string SettingsDirectory, string LogLevel)
{
    /// <summary>The arguments after the executable: <c>--plugin-host --pipe … --plugin … --log-dir … --settings-dir … --log-level …</c>.</summary>
    public IReadOnlyList<string> Arguments =>
    [
        PluginHostEntry.Flag, "--pipe", PipeName, "--plugin", PluginDll,
        "--log-dir", LogDirectory, "--settings-dir", SettingsDirectory, "--log-level", LogLevel,
    ];
}

/// <summary>Starts plugin host processes (tests give the manager one of their own).</summary>
public interface IPluginProcessLauncher
{
    IPluginProcess Start(PluginHostCommand command);
}

/// <summary>A running plugin host process.</summary>
public interface IPluginProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>The exit code once it has exited.</summary>
    int? ExitCode { get; }

    /// <summary>Completes when the process has exited.</summary>
    Task Exited { get; }

    /// <summary>The end of what it wrote to stderr, at most 4 KiB.</summary>
    string ErrorTail { get; }

    /// <summary>Kills the process and its children; nothing when it has exited.</summary>
    void Kill();

    /// <summary>
    /// Its memory now, in bytes, as the OS's own tool shows it, read as the engine's own is
    /// (<see cref="InfoPanel.Sensors.SystemInfo.ProcessMemory"/>: the physical footprint on macOS, the private working set
    /// on Windows, the resident set on Linux); null when it can't be read.
    /// </summary>
    long? MemoryBytes { get; }
}

/// <summary>
/// Starts plugin hosts as the engine's own executable in its host mode: <c>{BaseDirectory}/InfoPanel</c> (the app host),
/// else <c>dotnet {BaseDirectory}/InfoPanel.dll</c> when the engine itself runs through <c>dotnet</c>. Output and errors are
/// read as they come, in chunks of at most 4 KiB, never as lines (a host writing without a newline would make a line
/// reader hold all of it): stdout is dropped (the host logs to its file), and only stderr's last 4 KiB are kept, for the
/// reason a host failed.
/// </summary>
public sealed class PluginProcessLauncher(ILogger<PluginProcessLauncher>? logger = null) : IPluginProcessLauncher
{
    public IPluginProcess Start(PluginHostCommand command)
    {
        var (file, prefix) = EngineCommand();
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in prefix.Concat(command.Arguments))
            start.ArgumentList.Add(arg);

        var process = Process.Start(start) ?? throw new InvalidOperationException($"The plugin host {file} didn't start.");
        return new HostProcess(process, logger);
    }

    /// <summary>The engine executable and the arguments that come before the host's own.</summary>
    public static (string File, IReadOnlyList<string> Prefix) EngineCommand()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel");
        if (File.Exists(appHost))
            return (appHost, []);

        var processPath = Environment.ProcessPath ?? "dotnet";
        return (processPath, [Path.Combine(AppContext.BaseDirectory, "InfoPanel.dll")]);
    }

    internal sealed class HostProcess : IPluginProcess
    {
        private const int ChunkBytes = 4096;
        private const int TailBytes = 4096;

        /// <summary>How long an exited host's stderr is read on, so its last words are in the reason (wall clock).</summary>
        private static readonly TimeSpan StderrGrace = TimeSpan.FromMilliseconds(500);

        /// <summary>How long disposal waits for a disposed host's reads to end before it returns (wall clock).</summary>
        private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(1);

        /// <summary>How often, within <see cref="DrainWait"/>, disposal ends its reads again while one goes on (wall clock).</summary>
        private static readonly TimeSpan EndAgain = TimeSpan.FromMilliseconds(20);

        /// <summary>The longest pause between ends of reads that go on past <see cref="DrainWait"/> (wall clock).</summary>
        private static readonly TimeSpan EndAgainAtMost = TimeSpan.FromSeconds(1);

        private readonly Process _process;
        // Its output and errors, owned here: Process.Dispose doesn't close streams that were read.
        private readonly Stream _stdout;
        private readonly Stream _stderr;
        // Ends the reads waiting on them now. On Windows they are synchronous file streams over anonymous pipes, whose
        // read already waiting on a pipe that a process the host started still holds open ends neither by a token nor by
        // Dispose, and a read let in may not wait yet; cancelling the handle's I/O ends one waiting, and is done again
        // until the reads end, however long (a disposed stream's handle takes no more calls, so the streams are disposed
        // only once both reads have ended). Elsewhere the streams are disposed, which ends their reads.
        private readonly Action _endReads;
        // Taken by a read to check _stopping and start, and by disposal to set it and end the reads: each read either
        // starts before the stop, and is ended, or doesn't start.
        private readonly Lock _reading = new();
        private bool _stopping;
        private int _disposed;
        private readonly ILogger _logger;
        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;

        public HostProcess(Process process, ILogger? logger = null)
            : this(process, process.StandardOutput.BaseStream, process.StandardError.BaseStream, logger)
        {
        }

        private HostProcess(Process process, Stream stdout, Stream stderr, ILogger? logger)
            : this(process, stdout, stderr, EndReadsOf(stdout, stderr), logger)
        {
        }

        /// <summary>A host over <paramref name="stdout"/> and <paramref name="stderr"/>, whose waiting reads <paramref name="endReads"/> ends (the tests' seam).</summary>
        internal HostProcess(Process process, Stream stdout, Stream stderr, Action endReads, ILogger? logger)
        {
            _process = process;
            Id = process.Id;
            _stdout = stdout;
            _stderr = stderr;
            _endReads = endReads;
            _logger = logger ?? NullLogger.Instance;
            var stdoutRead = DrainAsync(_stdout, keep: false);
            var stderrRead = DrainAsync(_stderr, keep: true);
            Drained = Task.WhenAll(stdoutRead, stderrRead);
            Exited = ExitAsync(stderrRead);
        }

        private static Action EndReadsOf(Stream stdout, Stream stderr) =>
            OperatingSystem.IsWindows() ? CancelWindowsReadsOf(stdout, stderr) : () =>
            {
                stdout.Dispose();
                stderr.Dispose();
            };

        [SupportedOSPlatform("windows")]
        private static Action CancelWindowsReadsOf(Stream stdout, Stream stderr)
        {
            var (stdoutHandle, stderrHandle) = ((stdout as FileStream)?.SafeFileHandle, (stderr as FileStream)?.SafeFileHandle);
            return () =>
            {
                CancelReads(stdoutHandle);
                CancelReads(stderrHandle);
            };
        }

        [SupportedOSPlatform("windows")]
        private static void CancelReads(SafeFileHandle? handle)
        {
            if (handle == null || handle.IsClosed || handle.IsInvalid)
                return;
            try
            {
                // Nothing pending is no error worth a word (ERROR_NOT_FOUND).
                _ = HostPipeInterop.CancelIoEx(handle, 0);
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile: its reads have ended.
            }
        }

        /// <summary>Completes when the reads of its output and its errors have both ended.</summary>
        internal Task Drained { get; }

        public int Id { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? SafeExitCode() : null;

        public Task Exited { get; }

        public string ErrorTail
        {
            get
            {
                lock (_tail)
                    return System.Text.Encoding.UTF8.GetString(_tail, 0, _tailLength);
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It has exited, or can't be killed (gone already).
            }
        }

        public long? MemoryBytes
        {
            get
            {
                try
                {
                    if (_process.HasExited)
                        return null;
                    // 0 or less is a read that failed (exited meanwhile, refused, or Windows before 1809).
                    return InfoPanel.Sensors.SystemInfo.ProcessMemory.Of(_process) is var bytes and > 0 ? bytes : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Ends the reads of its output and errors (a process it started may still hold them open, and its reads would wait
        /// for that process): no read starts after this, those started are ended, again every <see cref="EndAgain"/> until
        /// they have, waiting at most <see cref="DrainWait"/>; then both streams are closed and the process disposed. Reads
        /// still going on then are logged, and ended again in the background, the pauses doubling up to
        /// <see cref="EndAgainAtMost"/>, until both have ended; only then are the streams closed and the process disposed.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            EndReads(stop: true);
            var waited = Stopwatch.StartNew();
            while (Task.WaitAny([Drained], EndAgain) < 0 && waited.Elapsed < DrainWait)
                EndReads(stop: false);
            if (Drained.IsCompleted)
            {
                Release();
                return;
            }
            _logger.LogWarning("plugin host (process {Pid}): its output reads didn't end within {Wait} of its disposal", Id, DrainWait);
            _ = EndReadsUntilDrainedAsync();
        }

        /// <summary>Ends the reads waiting now; with <paramref name="stop"/>, lets no read start after.</summary>
        private void EndReads(bool stop)
        {
            lock (_reading)
            {
                _stopping |= stop;
                _endReads();
            }
        }

        /// <summary>Ends the reads again, ever less often, until both have ended; then releases the streams and the process.</summary>
        private async Task EndReadsUntilDrainedAsync()
        {
            try
            {
                var again = EndAgain;
                while (await Task.WhenAny(Drained, Task.Delay(again)).ConfigureAwait(false) != Drained)
                {
                    EndReads(stop: false);
                    again = TimeSpan.FromTicks(Math.Min(again.Ticks * 2, EndAgainAtMost.Ticks));
                }
            }
            finally
            {
                Release();
            }
        }

        /// <summary>Closes both streams and disposes the process, once: by disposal, or by its background ends of reads.</summary>
        private void Release()
        {
            _stdout.Dispose();
            _stderr.Dispose();
            _process.Dispose();
        }

        private int? SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch (InvalidOperationException) { return null; }
        }

        private async Task ExitAsync(Task stderr)
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            // Its stderr to its end, briefly: a child that kept it open doesn't hold the exit.
            await Task.WhenAny(stderr, Task.Delay(StderrGrace)).ConfigureAwait(false);
        }

        /// <summary>Reads <paramref name="stream"/> to its end in fixed chunks, keeping stderr's last bytes when <paramref name="keep"/>.</summary>
        private Task DrainAsync(Stream stream, bool keep) => Task.Run(async () =>
        {
            var buffer = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    ValueTask<int> reading;
                    lock (_reading)
                    {
                        if (_stopping)
                            return;
                        reading = stream.ReadAsync(buffer);
                    }
                    var read = await reading.ConfigureAwait(false);
                    if (read == 0)
                        return;
                    if (keep)
                        Keep(buffer.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The process was disposed, its read cancelled, or its stream ended badly: nothing more to read.
            }
        });

        /// <summary>Appends <paramref name="chunk"/> to the tail, keeping its last <see cref="TailBytes"/>.</summary>
        private void Keep(ReadOnlySpan<byte> chunk)
        {
            lock (_tail)
            {
                if (chunk.Length >= TailBytes)
                {
                    chunk[^TailBytes..].CopyTo(_tail);
                    _tailLength = TailBytes;
                    return;
                }

                var drop = Math.Max(0, _tailLength + chunk.Length - TailBytes);
                if (drop > 0)
                {
                    _tail.AsSpan(drop, _tailLength - drop).CopyTo(_tail);
                    _tailLength -= drop;
                }
                chunk.CopyTo(_tail.AsSpan(_tailLength));
                _tailLength += chunk.Length;
            }
        }
    }
}

/// <summary>The kernel32 call that ends a plugin host's pending pipe reads on Windows.</summary>
[SupportedOSPlatform("windows")]
internal static partial class HostPipeInterop
{
    // BOOL CancelIoEx(HANDLE hFile, LPOVERLAPPED lpOverlapped): with no OVERLAPPED, every I/O pending on the handle, from
    // any thread.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CancelIoEx(SafeFileHandle file, nint overlapped);
}
