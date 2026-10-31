using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace InfoPanel.Preview.Video;

/// <summary>
/// Where the software H.264 encoder's ffmpeg is: <see cref="Configured"/> (<c>--ffmpeg</c>) alone when given; otherwise
/// the first executable <c>ffmpeg</c> (<c>ffmpeg.exe</c> on Windows) in the fully qualified folders of
/// <see cref="SearchPath"/> (<c>PATH</c>; a relative entry would be looked up against whatever the current folder is,
/// so it is skipped), then in <see cref="WellKnown"/>: Homebrew's and <c>/usr/local/bin</c>, which an app started from
/// the Dock or Finder doesn't have on its <c>PATH</c>.
/// </summary>
internal sealed record FfmpegDiscovery(string? Configured, string? SearchPath, IReadOnlyList<string> WellKnown)
{
    /// <summary>The folders looked in after <c>PATH</c>: none on Windows.</summary>
    public static IReadOnlyList<string> WellKnownDirectories { get; } = OperatingSystem.IsWindows() ? [] : ["/opt/homebrew/bin", "/usr/local/bin"];

    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>This process's: <paramref name="configured"/> (<c>--ffmpeg</c>), its <c>PATH</c> and <see cref="WellKnownDirectories"/>.</summary>
    public static FfmpegDiscovery ForThisComputer(string? configured) =>
        new(configured, Environment.GetEnvironmentVariable("PATH"), WellKnownDirectories);

    /// <summary>Exactly this file (tests).</summary>
    public static FfmpegDiscovery At(string path) => new(path, null, []);

    /// <summary>The executable found, or null.</summary>
    public string? Find()
    {
        if (Configured != null)
            return IsExecutable(Configured) ? Configured : null;
        foreach (var directory in Directories())
        {
            var candidate = Path.Combine(directory, ExecutableName);
            if (IsExecutable(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Where it looks, for the log.</summary>
    public string Describe() => Configured != null ? $"--ffmpeg {Configured}" : string.Join(", ", ["PATH", .. WellKnown]);

    private IEnumerable<string> Directories()
    {
        foreach (var entry in (SearchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (Path.IsPathFullyQualified(directory))
                yield return directory;
        }

        foreach (var directory in WellKnown)
            yield return directory;
    }

    /// <summary>A regular file (a link to one counts) that anyone may execute (on Windows: any file).</summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (OperatingSystem.IsWindows())
                return true;
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>A fault ffmpeg doesn't produce on demand, injected beneath the backend interface (tests only).</summary>
internal sealed class FfmpegFaults
{
    /// <summary>A session's output reader, once its reading has ended (the output closed, or its reads cancelled), waits for this: a reader that won't end.</summary>
    public ManualResetEventSlim? HoldReader { get; init; }
}

/// <summary>
/// The software H.264 encoder: the user's own ffmpeg (never shipped with the engine), run as a child process with fixed
/// arguments (<see cref="Arguments"/>, through <see cref="ProcessStartInfo.ArgumentList"/>, no shell), encoding with
/// libx264 at <see cref="VideoStream.SoftwareBitrate"/>: Baseline, no frame reordering (<c>-tune zerolatency</c>), the
/// BT.709 matrix into limited range, BT.709 written in the stream. Raw frames go in on its input, FLV comes out on its
/// output. Its arguments are fixed when it starts, so a forced keyframe or another frame rate is a new session
/// (<see cref="ChangesLive"/>), and a session whose output reader doesn't end within the quarantine bound gives it up
/// for the stream (<see cref="GivesUpWhenTeardownHangs"/>). It is found by <see cref="FfmpegDiscovery"/> and probed once
/// by a tiny encode with the production arguments, within <see cref="ProbeBound"/>.
/// </summary>
internal sealed class FfmpegH264(FfmpegDiscovery discovery, ILogger logger) : IH264Backend
{
    /// <summary>How long the probe (the version, then a 64 × 64 encode of two frames) may take, its processes killed after.</summary>
    public static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(5);

    /// <summary>The characters of <c>ffmpeg -version</c>'s output kept, for its first line; the rest is read and dropped.</summary>
    private const int VersionChars = 4096;

    private readonly Lock _lock = new();
    private readonly List<int> _processIds = [];
    private readonly List<int> _rates = [];

    /// <summary>Every process it started that hasn't exited yet, with its exit: owned until then, whatever became of its session (<see cref="ReapAsync"/>).</summary>
    private readonly Dictionary<Process, Task> _owned = [];

    private string? _executable;
    private bool _looked;
    private bool _reaped;

    public string Name => "ffmpeg";

    public H264Kind Kind => H264Kind.Software;

    public bool ChangesLive => false;

    public bool GivesUpWhenTeardownHangs => true;

    /// <summary>Tests: a fault beneath the interface.</summary>
    internal FfmpegFaults? Faults { get; init; }

    /// <summary>The ffmpeg found (looked for once), or null.</summary>
    public string? Executable
    {
        get
        {
            lock (_lock)
            {
                if (!_looked)
                {
                    _executable = discovery.Find();
                    _looked = true;
                }

                return _executable;
            }
        }
    }

    /// <summary>The version its probe read ("9.0.2"), or null before.</summary>
    public string? Version { get; private set; }

    /// <summary>Tests: the managed thread the last probe ran on.</summary>
    internal int ProbeThread { get; private set; }

    /// <summary>Tests: sessions started (the probe's not counted).</summary>
    internal int Starts
    {
        get
        {
            lock (_lock)
                return _processIds.Count;
        }
    }

    /// <summary>Tests: each started session's process id, in order.</summary>
    internal IReadOnlyList<int> ProcessIds
    {
        get
        {
            lock (_lock)
                return [.. _processIds];
        }
    }

    /// <summary>Tests: each started session's frame rate, in order.</summary>
    internal IReadOnlyList<int> Rates
    {
        get
        {
            lock (_lock)
                return [.. _rates];
        }
    }

    public H264Probe Probe() => Probe(CancellationToken.None, _ => { });

    /// <summary>
    /// The version, then a tiny encode with the production arguments. <paramref name="stopping"/> (the engine's stop)
    /// ends either at once: its process is killed and collected, its session torn down. Its session's teardown is waited
    /// for at most <see cref="Quarantine.Bound"/>: one that doesn't end fails the probe (nothing starts beside it) and is
    /// handed to <paramref name="abandoned"/>.
    /// </summary>
    public H264Probe Probe(CancellationToken stopping, Action<Task> abandoned)
    {
        ProbeThread = Environment.CurrentManagedThreadId;
        if (stopping.IsCancellationRequested)
            return H264Probe.Unavailable;
        var path = Executable;
        if (path == null)
        {
            logger.LogInformation("No ffmpeg found (looked at {Where}): the software H.264 encoder is unavailable. Install ffmpeg, or start the engine with --ffmpeg <path>",
                discovery.Describe());
            return H264Probe.Unavailable;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(ProbeBound.TotalSeconds * Stopwatch.Frequency);
        var version = ReadVersion(path, deadline, stopping);
        if (version == null || stopping.IsCancellationRequested)
            return H264Probe.Unavailable;

        // A tiny encode with the production arguments: `-h encoder=libx264` exits 0 for a build without it, and so may
        // anything else named ffmpeg. It must give an avcC the stream can send and an encoded keyframe.
        var first = new TaskCompletionSource<H264Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        FfmpegSession session;
        try
        {
            session = StartOwned(path, new H264Config(64, 64, 30, VideoStream.SoftwareBitrate), o => first.TrySetResult(o));
        }
        catch (H264StartException ex)
        {
            logger.LogInformation("ffmpeg at {Path} couldn't be started: {Message}; the software H.264 encoder is unavailable", path, ex.Message);
            return H264Probe.Unavailable;
        }

        var found = H264Probe.Unavailable;
        Task? leftBehind = null;
        try
        {
            var pixels = new byte[64 * 64 * 4];
            for (var id = 0; id < 2; id++)
            {
                Array.Fill(pixels, (byte)(id * 100));
                session.Submit(pixels, 64, 64, id, id == 0, out _);
            }

            if (!WaitOrStop(first.Task, Remaining(deadline), stopping))
            {
                if (stopping.IsCancellationRequested)
                    logger.LogInformation("ffmpeg's probe stopped with the engine");
                else
                    logger.LogWarning("ffmpeg at {Path} encoded nothing within {Seconds} s; the software H.264 encoder is unavailable", path, ProbeBound.TotalSeconds);
            }
            else if (Unusable(first.Task.Result) is { } problem)
                logger.LogInformation("ffmpeg {Version} at {Path} can't encode the preview's H.264 ({Problem}); the software H.264 encoder is unavailable",
                    version, path, problem);
            else
                found = H264Probe.Available;
        }
        finally
        {
            // Bounded, as every retirement: a teardown that doesn't end leaves the probe's process killed and its readers behind.
            var teardown = session.Abort();
            if (!Ended(teardown, Quarantine.Bound))
            {
                leftBehind = teardown;
                logger.LogWarning("ffmpeg at {Path}: the probe's session didn't tear down within {Ms} ms; the software H.264 encoder is unavailable", path,
                    (int)Quarantine.Bound.TotalMilliseconds);
                abandoned(teardown);
            }
        }

        if (leftBehind != null || found != H264Probe.Available)
            return H264Probe.Unavailable;
        Version = version;
        logger.LogInformation("Software H.264 encoder: ffmpeg {Version} at {Path} (libx264)", version, path);
        return H264Probe.Available;
    }

    /// <summary>Why the probe's first outcome isn't an encoded keyframe with an avcC the stream can send; null when it is.</summary>
    private static string? Unusable(H264Outcome outcome)
    {
        if (outcome.Kind != H264OutcomeKind.Encoded)
            return outcome.Problem ?? $"its first frame was {outcome.Kind}";
        if (!outcome.Keyframe)
            return "its first frame isn't a keyframe";
        if (outcome.Avcc == null)
            return "its first frame has no avcC";
        H264Sps.RewriteForStream(outcome.Avcc, out var problem);
        return problem == null ? null : "its avcC: " + problem;
    }

    /// <summary>Whether <paramref name="teardown"/> ended within <paramref name="bound"/> (a failed one ended).</summary>
    private static bool Ended(Task teardown, TimeSpan bound)
    {
        try
        {
            return teardown.Wait(bound);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>Whether <paramref name="task"/> ended within <paramref name="bound"/>; false at once when <paramref name="stopping"/> is cancelled.</summary>
    private static bool WaitOrStop(Task task, TimeSpan bound, CancellationToken stopping)
    {
        try
        {
            return task.Wait((int)Math.Ceiling(bound.TotalMilliseconds), stopping);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan Remaining(long deadline)
    {
        var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// The version from <c>ffmpeg -version</c> ("9.0.2", or "unknown" when it says none); null when it can't be run in
    /// time or <paramref name="stopping"/> is cancelled first, its process then killed and collected.
    /// </summary>
    private string? ReadVersion(string path, long deadline, CancellationToken stopping)
    {
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-version");
        Process process;
        lock (_lock)
        {
            if (_reaped)
                return null;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("no process");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                logger.LogInformation("ffmpeg at {Path} couldn't be run: {Message}; the software H.264 encoder is unavailable", path, ex.Message);
                return null;
            }

            Own(process);
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            cts.CancelAfter(Remaining(deadline));
            try
            {
                process.StandardInput.Close();
                // Both read to their end in bounded memory: the version's line from the first characters, the rest dropped.
                var output = BoundedOutput.HeadAsync(process.StandardOutput.BaseStream, VersionChars, cts.Token);
                var errors = BoundedOutput.HeadAsync(process.StandardError.BaseStream, 0, cts.Token);
                process.WaitForExitAsync(cts.Token).Wait(cts.Token);
                var line = output.Result.Split('\n', 2)[0].Trim();
                _ = errors.Result;
                const string prefix = "ffmpeg version ";
                return line.StartsWith(prefix, StringComparison.Ordinal) && line[prefix.Length..].Split(' ', 2)[0] is { Length: > 0 and <= 64 } v ? v : "unknown";
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException or IOException or InvalidOperationException)
            {
                FfmpegSession.Kill(process);
                try
                {
                    // Collected before the probe ends, so the engine's stop leaves no process behind.
                    process.WaitForExit(Quarantine.Bound);
                }
                catch (Exception wait) when (wait is InvalidOperationException or Win32Exception)
                {
                }

                if (stopping.IsCancellationRequested)
                    logger.LogInformation("ffmpeg's probe stopped with the engine; its ffmpeg -version killed");
                else
                    logger.LogWarning("ffmpeg at {Path} didn't answer -version within {Seconds} s; killed, and the software H.264 encoder is unavailable", path,
                        ProbeBound.TotalSeconds);
                return null;
            }
            finally
            {
                FfmpegSession.CloseStreams(process);
            }
        }
    }

    public IH264Session Start(H264Config config, Action<H264Outcome> output)
    {
        var path = Executable ?? throw new H264StartException(H264StartProblem.Fault, "no ffmpeg found");
        var session = StartOwned(path, config, output);
        lock (_lock)
        {
            _processIds.Add(session.ProcessId);
            _rates.Add(config.Fps);
        }

        return session;
    }

    /// <summary>A session whose process this backend owns until it exits; refused (busy, not counted) once the engine has stopped.</summary>
    private FfmpegSession StartOwned(string path, H264Config config, Action<H264Outcome> output)
    {
        lock (_lock)
        {
            if (_reaped)
                throw new H264StartException(H264StartProblem.Busy, "the engine is stopping");
            return FfmpegSession.Start(path, config, output, logger, Faults, Own);
        }
    }

    /// <summary>Under the lock: a process just started is owned until it exits, so the engine's stop can kill and collect it.</summary>
    private void Own(Process process)
    {
        var exited = process.WaitForExitAsync();
        _owned[process] = exited;
        _ = exited.ContinueWith(_ =>
        {
            lock (_lock)
                _owned.Remove(process);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// The engine's stop, after its probes and streams ended: every process this backend started that still runs (a
    /// quarantined or abandoned session's, or one nothing retired) is killed, and collected within <paramref name="bound"/>.
    /// Nothing starts after it.
    /// </summary>
    public async Task ReapAsync(TimeSpan bound, CancellationToken ct)
    {
        Task[] exits;
        lock (_lock)
        {
            _reaped = true;
            var running = _owned.Where(o => !o.Value.IsCompleted).ToList();
            foreach (var (process, _) in running)
                FfmpegSession.Kill(process);
            exits = [.. running.Select(o => o.Value)];
        }

        if (exits.Length == 0)
            return;
        try
        {
            await Task.WhenAll(exits).WaitAsync(bound, ct);
            logger.LogInformation("ffmpeg: {Count} processes still running at the engine's stop were killed and collected", exits.Length);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("ffmpeg: processes killed at the engine's stop weren't collected within {Ms} ms", (int)bound.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
        }
    }

    /// <summary>The command line for <paramref name="config"/>'s RGBA frames in, FLV of H.264 out.</summary>
    internal static List<string> Arguments(H264Config config)
    {
        string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        var bitrate = N(config.Bitrate);
        return
        [
            "-hide_banner", "-nostats", "-loglevel", "error",
            // Raw frames as they come: nothing to probe, and each frame's time is its number at the expected rate.
            "-probesize", "32", "-analyzeduration", "0",
            "-f", "rawvideo", "-pix_fmt", "rgb0", "-s", $"{N(config.Width)}x{N(config.Height)}", "-r", N(config.Fps), "-i", "pipe:0",
            // Odd sides padded in black; the BT.709 matrix into limited range (rgb0: the alpha byte is ignored, and the
            // frames are premultiplied, so transparency is black); the frames say BT.709 primaries, transfer and matrix
            // in limited range, which x264 writes into the SPS (output options alone don't reach it: the frames' own win).
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2:0:0:black,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,"
                + "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
            "-fps_mode", "passthrough",
            // Baseline and zero latency: no B-frames, no lookahead, each frame out as soon as it is in.
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline",
            "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", N(Math.Max(1, config.Bitrate / Math.Max(1, config.Fps))), "-g", "600",
            "-flush_packets", "1", "-f", "flv", "pipe:1",
        ];
    }
}

/// <summary>
/// One ffmpeg process (see <see cref="FfmpegH264"/>). <see cref="Submit"/> never waits: it copies the frame and queues it
/// for the session's writer, which writes it to ffmpeg's input. The reader parses ffmpeg's FLV output and reports each
/// access unit as the outcome of the oldest frame outstanding, its timestamp checked against that frame's; anything
/// else (an output for no frame, a timestamp out of step, a first frame that isn't a keyframe, a write that fails, the
/// output ending) fails the oldest frame outstanding, once, and the session refuses frames from then on.
/// <see cref="Abort"/> reports every outstanding frame aborted at once, and tears down off the caller's thread: it
/// cancels the writer's write (a write blocked on a full pipe included) and the readers' reads, kills the process, and
/// ends once the writer and both readers have ended, only then disposing the process.
/// </summary>
internal sealed class FfmpegSession : IH264Session
{
    /// <summary>ffmpeg's error lines logged per session at most; the rest are read and dropped.</summary>
    private const int MaxErrorLines = 20;

    /// <summary>The characters of an error line logged at most; the rest of it is read and dropped.</summary>
    private const int MaxErrorChars = 1024;

    /// <summary>Processes started and not yet torn down in this process (tests: restarts leak none).</summary>
    private static long _live;

    public static long Live => Interlocked.Read(ref _live);

    private readonly Process _process;
    private readonly Action<H264Outcome> _output;
    private readonly ILogger _logger;
    private readonly FfmpegFaults? _faults;
    private readonly int _frameBytes;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentQueue<byte[]> _buffers = new();
    private readonly TaskCompletionSource _teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private readonly Queue<(long Id, long Number)> _outstanding = new();
    private readonly Task _writer;
    private readonly Task _reader;
    private readonly Task _errors;
    private long _next;
    private long _progress;
    private long _written;
    private int _aborted;
    private string? _broken;

    private FfmpegSession(Process process, H264Config config, Action<H264Outcome> output, ILogger logger, FfmpegFaults? faults)
    {
        _process = process;
        _output = output;
        _logger = logger;
        _faults = faults;
        Config = config;
        _frameBytes = config.Width * config.Height * 4;
        ProcessId = process.Id;
        var ct = _cts.Token;
        _writer = Task.Run(() => WriteAsync(process.StandardInput.BaseStream, ct));
        _reader = Task.Run(() => ReadAsync(process.StandardOutput.BaseStream, ct));
        _errors = Task.Run(() => DrainErrorsAsync(process.StandardError.BaseStream, ct));
    }

    public H264Config Config { get; }

    public int ProcessId { get; }

    /// <summary>Frames written whole to ffmpeg's input (tests).</summary>
    public long FramesWritten => Interlocked.Read(ref _written);

    /// <summary>Starts ffmpeg for <paramref name="config"/>; throws <see cref="H264StartException"/> (a fault) when it can't.</summary>
    public static FfmpegSession Start(string path, H264Config config, Action<H264Outcome> output, ILogger logger, FfmpegFaults? faults,
        Action<Process>? started = null)
    {
        if (config.Width < 1 || config.Height < 1)
            throw new H264StartException(H264StartProblem.Unsupported, $"a {config.Width}x{config.Height} frame");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in FfmpegH264.Arguments(config))
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new H264StartException(H264StartProblem.Fault, $"ffmpeg at {path} couldn't be started: {ex.Message}");
        }

        started?.Invoke(process);
        Interlocked.Increment(ref _live);
        logger.LogDebug("ffmpeg started (pid {Pid}) for {Width}x{Height} at {Fps} frames a second", process.Id, config.Width, config.Height, config.Fps);
        return new FfmpegSession(process, config, output, logger, faults);
    }

    public IReadOnlyList<Task> Released => [];

    public int InFlight
    {
        get
        {
            lock (_lock)
                return _outstanding.Count;
        }
    }

    public long Progress => Interlocked.Read(ref _progress);

    /// <summary>Always false: ffmpeg's rate is one of its arguments (the stream starts a new session for another).</summary>
    public bool TrySetFrameRate(int fps) => false;

    /// <summary>
    /// Queues the frame for ffmpeg; never waits for the write. <paramref name="forceKeyframe"/> is ignored: the process's
    /// first frame is its keyframe (the stream starts a new session for another).
    /// </summary>
    public H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem)
    {
        problem = null;
        if (width != Config.Width || height != Config.Height || pixels.Length < _frameBytes)
        {
            problem = $"a {width}x{height} frame for a {Config.Width}x{Config.Height} session";
            return H264Submit.Refused;
        }

        var copy = Rent();
        pixels.AsSpan(0, _frameBytes).CopyTo(copy);
        lock (_lock)
        {
            if (Volatile.Read(ref _aborted) != 0)
                problem = "the session was aborted";
            else if (_broken != null)
                problem = _broken;
            if (problem != null)
            {
                _buffers.Enqueue(copy);
                return H264Submit.Refused;
            }

            _outstanding.Enqueue((frameId, _next++));
        }

        // Fails only once the session is aborted, which reported this frame aborted.
        if (!_input.Writer.TryWrite(copy))
            _buffers.Enqueue(copy);
        return H264Submit.Accepted;
    }

    private byte[] Rent()
    {
        while (_buffers.TryDequeue(out var buffer))
        {
            if (buffer.Length == _frameBytes)
                return buffer;
        }

        return new byte[_frameBytes];
    }

    private void Return(byte[] buffer)
    {
        if (_buffers.Count < 3)
            _buffers.Enqueue(buffer);
    }

    /// <summary>
    /// Ends the session without draining it: every outstanding frame is reported aborted now; the rest runs off this
    /// thread. The writer's write and the readers' reads are cancelled, the process is killed, and the returned task ends
    /// once the writer and both readers have ended (then the process is disposed). Idempotent.
    /// </summary>
    public Task Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return _teardown.Task;

        List<long> aborted;
        lock (_lock)
        {
            aborted = [.. _outstanding.Select(o => o.Id)];
            _outstanding.Clear();
        }

        foreach (var id in aborted)
            Report(new H264Outcome(id, H264OutcomeKind.Aborted));

        _input.Writer.TryComplete();
        _ = Task.Run(TeardownAsync);
        return _teardown.Task;
    }

    private async Task TeardownAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (AggregateException)
        {
        }

        Kill(_process);
        try
        {
            await Task.WhenAll(_writer, _reader, _errors);
        }
        catch (Exception)
        {
            // Each ends on its own terms; their failures were reported or don't matter after an abort.
        }

        try
        {
            // Killed: its exit is collected before the teardown ends (a process that can't be killed keeps it from ending).
            await _process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        CloseStreams(_process);
        _process.Dispose();
        _cts.Dispose();
        Interlocked.Decrement(ref _live);
        _teardown.TrySetResult();
    }

    /// <summary>
    /// Closes the process's three pipes; never throws. <see cref="Process.Dispose()"/> leaves a redirected stream once
    /// its property was read (it is the caller's then) to a finalizer, which holds three descriptors per process until
    /// a collection runs.
    /// </summary>
    internal static void CloseStreams(Process process)
    {
        foreach (var stream in new Func<Stream?>[] { () => process.StandardInput.BaseStream, () => process.StandardOutput.BaseStream, () => process.StandardError.BaseStream })
        {
            try
            {
                // A writer already closed has no stream left.
                stream()?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Kills the process alone (SIGKILL on Unix); never throws. ffmpeg starts no processes of its own, and a tree kill
    /// on Unix stops the process first (SIGSTOP) and walks every process on the computer before its SIGKILL: an engine
    /// that exits in between leaves the process stopped for good.
    /// </summary>
    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    private void Report(H264Outcome outcome)
    {
        try
        {
            _output(outcome);
        }
        catch (Exception)
        {
            // The stream's own failure; nothing of it reaches the reader.
        }
        finally
        {
            Interlocked.Increment(ref _progress);
        }
    }

    /// <summary>The session can't go on: the oldest outstanding frame fails (once), and later frames are refused.</summary>
    private void Fail(string why)
    {
        (long Id, long Number) head;
        lock (_lock)
        {
            if (Volatile.Read(ref _aborted) != 0 || _broken != null)
                return;
            _broken = why;
            if (!_outstanding.TryDequeue(out head))
                return;
        }

        Report(new H264Outcome(head.Id, H264OutcomeKind.Failed, Problem: why));
    }

    private string Exit()
    {
        try
        {
            return _process.HasExited ? "exit " + _process.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    // ---- the writer ----

    private async Task WriteAsync(Stream input, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _input.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await input.WriteAsync(frame, ct);
                    await input.FlushAsync(ct);
                    Interlocked.Increment(ref _written);
                }
                finally
                {
                    Return(frame);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Fail($"writing a frame to ffmpeg failed ({Exit()}): {ex.Message}");
        }
    }

    // ---- the readers ----

    private async Task ReadAsync(Stream output, CancellationToken ct)
    {
        string? failure;
        try
        {
            failure = await ReadFlvAsync(output, ct) ?? $"ffmpeg's output ended ({Exit()})";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            failure = "aborted";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            failure = $"reading ffmpeg's output failed ({Exit()}): {ex.Message}";
        }

        Fail(failure);
        if (_faults?.HoldReader is { } hold)
            await Task.Run(() => hold.Wait(), CancellationToken.None);
    }

    /// <summary>Reads FLV tags until the output ends (null) or something is wrong (what).</summary>
    private async Task<string?> ReadFlvAsync(Stream output, CancellationToken ct)
    {
        var header = new byte[13];
        if (!await ReadExactlyAsync(output, header, ct))
            return null;
        if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
            return "ffmpeg's output isn't FLV";

        var tag = new byte[11];
        byte[]? avcc = null;
        var first = true;
        while (await ReadExactlyAsync(output, tag, ct))
        {
            var size = (tag[1] << 16) | (tag[2] << 8) | tag[3];
            var timestamp = (long)(uint)((tag[7] << 24) | (tag[4] << 16) | (tag[5] << 8) | tag[6]);
            // The tag's data and the size of the tag after it.
            var data = new byte[size + 4];
            if (!await ReadExactlyAsync(output, data, ct))
                return "ffmpeg's output ended inside a tag";
            if (tag[0] != 9)
                continue; // Not video: the metadata.
            if (size < 5)
                return $"a video tag of {size} bytes";
            if ((data[0] & 0x80) != 0)
                return "an enhanced FLV video tag, which this reader doesn't take";
            if ((data[0] & 0x0f) != 7)
                return $"a video tag of codec {data[0] & 0x0f}, not H.264";

            var keyframe = data[0] >> 4 == 1;
            switch (data[1])
            {
                case 0:
                    // The sequence header: the avcC the next keyframe carries.
                    avcc = data[5..size];
                    break;
                case 1:
                    var compositionTime = (data[2] << 16) | (data[3] << 8) | data[4];
                    if (compositionTime != 0)
                        return $"a frame displayed {compositionTime} ms after it is decoded: ffmpeg reorders frames";
                    if (first && !keyframe)
                        return "ffmpeg's first frame isn't a keyframe";
                    if (keyframe && avcc == null)
                        return "a keyframe before its avcC";
                    if (Emit(timestamp, data[5..size], keyframe, avcc) is { } problem)
                        return problem;
                    first = false;
                    break;
            }
        }

        return null;
    }

    /// <summary>Reports an access unit as the outcome of the oldest outstanding frame, if its timestamp is that frame's.</summary>
    private string? Emit(long timestamp, byte[] unit, bool keyframe, byte[]? avcc)
    {
        (long Id, long Number) head;
        lock (_lock)
        {
            if (Volatile.Read(ref _aborted) != 0)
                return null;
            if (!_outstanding.TryPeek(out head))
                return $"an encoded frame at {timestamp} ms for no frame it was given";
            // FLV's milliseconds: the frame's number at the expected rate, rounded as ffmpeg rounds.
            var due = (long)Math.Round(head.Number * 1000.0 / Config.Fps, MidpointRounding.AwayFromZero);
            if (timestamp != due)
                return $"an encoded frame at {timestamp} ms where frame {head.Number} was due at {due} ms";
            _outstanding.Dequeue();
        }

        Report(new H264Outcome(head.Id, H264OutcomeKind.Encoded, unit, keyframe, keyframe ? avcc : null));
        return null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private async Task DrainErrorsAsync(Stream errors, CancellationToken ct)
    {
        try
        {
            await BoundedOutput.LinesAsync(errors, MaxErrorChars, MaxErrorLines,
                line => _logger.LogWarning("ffmpeg (pid {Pid}): {Line}", ProcessId, line), ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// A child's output read to its end in bounded memory, whatever it writes (a broken ffmpeg may write without end and
/// without a newline): at most <see cref="ChunkBytes"/> at a time, and of its text only what the caller keeps, the rest
/// read and dropped.
/// </summary>
internal static class BoundedOutput
{
    public const int ChunkBytes = 16 * 1024;

    /// <summary>
    /// Hands the first <paramref name="maxLines"/> lines to <paramref name="line"/>, each cut at <paramref name="maxChars"/>
    /// characters: a line that reaches it is handed over then, and the rest of it dropped up to its newline.
    /// </summary>
    public static async Task LinesAsync(Stream stream, int maxChars, int maxLines, Action<string> line, CancellationToken ct)
    {
        var bytes = new byte[ChunkBytes];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(ChunkBytes)];
        var decoder = Encoding.UTF8.GetDecoder();
        var current = new StringBuilder(maxChars);
        var skipping = false;
        var lines = 0;

        void Emit()
        {
            if (lines < maxLines)
            {
                lines++;
                line(current.ToString().TrimEnd('\r'));
            }

            current.Clear();
        }

        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (lines >= maxLines)
                continue;
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            for (var i = 0; i < count && lines < maxLines; i++)
            {
                if (chars[i] == '\n')
                {
                    if (!skipping)
                        Emit();
                    skipping = false;
                }
                else if (!skipping)
                {
                    current.Append(chars[i]);
                    if (current.Length >= maxChars)
                    {
                        Emit();
                        skipping = true;
                    }
                }
            }
        }

        if (current.Length > 0 && !skipping)
            Emit();
    }

    /// <summary>Its first <paramref name="maxChars"/> characters (none: only drained).</summary>
    public static async Task<string> HeadAsync(Stream stream, int maxChars, CancellationToken ct)
    {
        var bytes = new byte[ChunkBytes];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(ChunkBytes)];
        var decoder = Encoding.UTF8.GetDecoder();
        var head = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (head.Length >= maxChars)
                continue;
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            head.Append(chars, 0, Math.Min(count, maxChars - head.Length));
        }

        return head.ToString();
    }
}
