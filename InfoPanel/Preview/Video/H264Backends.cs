using Microsoft.Extensions.Logging;

namespace InfoPanel.Preview.Video;

/// <summary>
/// The run's H.264 encoders, best first, and what this run has learned about each: whether its probe passed (once, the
/// first time a stream reaches it, on a background task), the sizes it can't take, and whether it was given up for the
/// run (three quarantined sessions, or faulty starts at three different sizes with no good start between them). A
/// stream takes the first backend that is enabled, probed available and not given up; with none left, its frames are
/// PNG with <c>encoder.unavailable</c>.
/// </summary>
internal sealed class H264Backends
{
    /// <summary>Quarantined sessions that give a backend up for the run.</summary>
    public const int QuarantinesToGiveUp = 3;

    /// <summary>Faulty starts at different sizes, with no good start between them, that give a backend up for the run.</summary>
    public const int FaultySizesToGiveUp = 3;

    /// <summary>
    /// How long the engine's stop waits for running probes and live streams once it has ended them (each kills and
    /// collects its processes; a stream retires its session within <see cref="Quarantine.Bound"/>).
    /// </summary>
    public static readonly TimeSpan StopBound = Quarantine.Bound + TimeSpan.FromSeconds(1);

    /// <summary>How long the engine's stop then waits for the processes still running (killed then) to be collected (<see cref="IH264Backend.ReapAsync"/>).</summary>
    public static readonly TimeSpan ReapBound = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<IH264Backend, Record> _records = [];
    private readonly HashSet<VideoStream> _streams = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _stopped;

    private sealed class Record
    {
        public Task<H264Probe>? Probe;
        public bool GivenUp;
        public int Quarantines;
        public readonly HashSet<(int, int)> FaultySizes = [];
        public readonly HashSet<(int, int)> Unsupported = [];
    }

    public H264Backends(IReadOnlyList<IH264Backend> chain, ILogger logger)
    {
        Chain = chain;
        _logger = logger;
        foreach (var backend in chain)
            _records[backend] = new Record();
    }

    /// <summary>The enabled backends, best first.</summary>
    public IReadOnlyList<IH264Backend> Chain { get; }

    /// <summary>The <c>--h264</c> values: every backend, hardware only, software only, or none.</summary>
    public static readonly string[] Modes = ["auto", "hardware", "software", "off"];

    /// <summary>
    /// This OS's backends, best first, as <c>--h264</c> enables them: its hardware encoder (VideoToolbox on macOS; none
    /// yet elsewhere), then the software one, ffmpeg found by <paramref name="software"/> (the engine always passes it;
    /// null leaves the software encoder out).
    /// </summary>
    public static H264Backends ForThisComputer(string mode, ILogger logger, FfmpegDiscovery? software = null)
    {
        var all = new List<IH264Backend>();
        if (OperatingSystem.IsMacOS())
            all.Add(new VideoToolboxH264(logger));
        if (software != null)
            all.Add(new FfmpegH264(software, logger));
        return new H264Backends(Enabled(all, mode), logger);
    }

    /// <summary>The backends <paramref name="mode"/> enables, in order.</summary>
    public static IReadOnlyList<IH264Backend> Enabled(IEnumerable<IH264Backend> all, string mode) => mode switch
    {
        "off" => [],
        "hardware" => [.. all.Where(b => b.Kind == H264Kind.Hardware)],
        "software" => [.. all.Where(b => b.Kind == H264Kind.Software)],
        _ => [.. all],
    };

    /// <summary>
    /// Whether the backend works here: probed on a background task, the result cached and logged; a busy answer isn't
    /// cached (the next call, after the stream's backoff, probes again).
    /// </summary>
    public Task<H264Probe> ProbeAsync(IH264Backend backend)
    {
        lock (_lock)
        {
            var record = _records[backend];
            if (record.Probe is { IsCompletedSuccessfully: true, Result: H264Probe.Busy })
                record.Probe = null;
            // Once the engine stops, nothing new is probed.
            if (_stopped && record.Probe == null)
                return Task.FromResult(H264Probe.Unavailable);
            return record.Probe ??= Task.Run(() => RunProbe(backend));
        }
    }

    /// <summary>
    /// A stream of this run, which the engine's stop ends and waits for (<see cref="StopAsync"/>); false once the engine
    /// is stopping, when the stream ends at once. Its loop calls <see cref="Untrack"/> as it ends.
    /// </summary>
    internal bool Track(VideoStream stream)
    {
        lock (_lock)
        {
            if (_stopped)
                return false;
            _streams.Add(stream);
            return true;
        }
    }

    internal void Untrack(VideoStream stream)
    {
        lock (_lock)
            _streams.Remove(stream);
    }

    /// <summary>
    /// The engine's stop: cancels the probes still running (each kills and collects what it started), ends every live
    /// stream (each retires its session: its process killed and collected), and waits for them all at most
    /// <see cref="StopBound"/>; then each backend kills what it started that still runs (a quarantined or abandoned
    /// session's) and waits at most <see cref="ReapBound"/> for it to be collected. No probe, stream or process starts
    /// after it. Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await EndAsync(ct);
        try
        {
            await Task.WhenAll(Chain.Select(b => b.ReapAsync(ReapBound, ct)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EndAsync(CancellationToken ct)
    {
        Task[] running;
        VideoStream[] streams;
        lock (_lock)
        {
            _stopped = true;
            running = [.. _records.Values.Select(r => r.Probe).OfType<Task>().Where(p => !p.IsCompleted)];
            streams = [.. _streams];
        }

        _stopping.Cancel();
        foreach (var stream in streams)
            stream.Dispose();
        try
        {
            await Task.WhenAll([.. running, .. streams.Select(s => s.Completion)]).WaitAsync(StopBound, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("An H.264 encoder probe or stream didn't end within {Ms} ms of the engine's stop", (int)StopBound.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private H264Probe RunProbe(IH264Backend backend)
    {
        H264Probe found;
        try
        {
            // A session the probe gave up waiting for counts as quarantined, as a stream's does.
            found = backend.Probe(_stopping.Token, teardown => Quarantine.Abandon(teardown, backend, this, _logger));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("H.264 encoder {Backend} ({Kind}) failed its probe: {Message}", backend.Name, Word(backend.Kind), ex.Message);
            return H264Probe.Unavailable;
        }

        if (found == H264Probe.Available)
            _logger.LogInformation("H.264 encoder found: {Kind} {Backend}", Word(backend.Kind), backend.Name);
        else if (found == H264Probe.Busy)
            _logger.LogInformation("H.264 encoder {Backend} ({Kind}) is busy; asked again later", backend.Name, Word(backend.Kind));
        else
            _logger.LogInformation("H.264 encoder {Backend} ({Kind}) isn't available here", backend.Name, Word(backend.Kind));
        return found;
    }

    /// <summary>
    /// Whether no backend can ever serve a stream in this run: none enabled, every probe done and failed, or every one
    /// given up. A probe not yet run counts as maybe.
    /// </summary>
    public bool NoneLeft
    {
        get
        {
            lock (_lock)
            {
                foreach (var record in _records.Values)
                {
                    if (record.GivenUp)
                        continue;
                    // A busy probe counts as maybe too: it is asked again.
                    if (record.Probe is not { IsCompleted: true } probe || (probe.IsCompletedSuccessfully && probe.Result != H264Probe.Unavailable))
                        return false;
                }

                return true;
            }
        }
    }

    public bool IsGivenUp(IH264Backend backend)
    {
        lock (_lock)
            return _records[backend].GivenUp;
    }

    public bool IsUnsupported(IH264Backend backend, int width, int height)
    {
        lock (_lock)
            return _records[backend].Unsupported.Contains((width, height));
    }

    /// <summary>The backend can't take this size: skipped for it for the rest of the run; not a fault.</summary>
    public void RecordUnsupported(IH264Backend backend, int width, int height, string why)
    {
        lock (_lock)
        {
            if (!_records[backend].Unsupported.Add((width, height)))
                return;
        }

        _logger.LogInformation("H.264 encoder {Backend} can't take {Width}x{Height} ({Why}); frames of that size use the next encoder, or PNG",
            backend.Name, width, height, why);
    }

    public void RecordGoodStart(IH264Backend backend)
    {
        lock (_lock)
            _records[backend].FaultySizes.Clear();
    }

    /// <summary>A start that failed for an unknown reason; at three sizes with no good start between them the backend is given up for the run.</summary>
    public void RecordFaultyStart(IH264Backend backend, int width, int height)
    {
        lock (_lock)
        {
            var record = _records[backend];
            record.FaultySizes.Add((width, height));
            if (record.FaultySizes.Count < FaultySizesToGiveUp || record.GivenUp)
                return;
            record.GivenUp = true;
        }

        _logger.LogError("H.264 encoder {Backend} failed to start at {Count} sizes; given up for this run", backend.Name, FaultySizesToGiveUp);
    }

    /// <summary>A session that didn't tear down within the bound (deliberately leaked); three give the backend up for the run.</summary>
    public void RecordQuarantine(IH264Backend backend)
    {
        int count;
        lock (_lock)
        {
            var record = _records[backend];
            count = ++record.Quarantines;
            if (count < QuarantinesToGiveUp || record.GivenUp)
                return;
            record.GivenUp = true;
        }

        _logger.LogError("H.264 encoder {Backend} left {Count} sessions that never tore down; given up for this run", backend.Name, count);
    }

    /// <summary>Quarantined sessions of the backend so far (tests, logs).</summary>
    public int Quarantines(IH264Backend backend)
    {
        lock (_lock)
            return _records[backend].Quarantines;
    }

    public static string Word(H264Kind kind) => kind == H264Kind.Hardware ? "hardware" : "software";
}

/// <summary>
/// The engine's stop for the run's H.264 encoders: a probe still running (an ffmpeg child) is cancelled and every live
/// stream ended, and the stop waits, within <see cref="H264Backends.StopBound"/>, until they have killed and collected
/// what they started. A socket's own close may come later than the engine's exit, so the stop doesn't rely on it.
/// </summary>
internal sealed class H264Shutdown(H264Backends backends) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => backends.StopAsync(cancellationToken);
}
