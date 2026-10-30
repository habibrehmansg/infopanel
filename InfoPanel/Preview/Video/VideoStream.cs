using System.Collections.Concurrent;
using System.Collections.Immutable;
using InfoPanel.Output;
using InfoPanel.Spec;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace InfoPanel.Preview.Video;

/// <summary>A video frame ready to send, with the stream generation its pixels were offered in.</summary>
internal sealed record EncodedFrame(FrameHeader Header, ReadOnlyMemory<byte> Payload, int Generation);

/// <summary>Frames a subscription drew and didn't send since the last one it sent (the frame header's <c>skipped</c>).</summary>
internal sealed class SkipCounter
{
    private long _value;

    public void Add(long count = 1) => Interlocked.Add(ref _value, count);

    /// <summary>The count so far, reset to 0 (the send loop, as it sends a frame).</summary>
    public long Take() => Interlocked.Exchange(ref _value, 0);

    public long Peek => Interlocked.Read(ref _value);
}

/// <summary>
/// One <c>h264</c> subscription's video: the render thread offers each frame (<see cref="Route"/>, then
/// <see cref="Offer"/>), one loop task encodes them, and the socket's send loop takes them in order (<see cref="TryTake"/>).
/// <list type="bullet">
/// <item><b>Drop before encoding, never after.</b> <see cref="Offer"/> copies the pixels into a one-frame raw slot where
/// the newest wins; the loop submits a frame only once it has reserved its slot in the ordered output queue
/// (<see cref="OutputSlots{T}"/>, <see cref="QueueSlots"/>) and the encoder holds fewer than <see cref="MaxInFlight"/>,
/// so a slow socket backs up to the raw slot and every encoded frame is sent.</item>
/// <item><b>One outcome per submission.</b> Each accepted submission's slot is owned by its pending entry and given back
/// in a <c>finally</c> unless its frame was queued; outcomes must come in submission order, and an unknown or
/// out-of-order one is a failure of the session.</item>
/// <item><b>Generations.</b> Each raw frame carries the stream's generation; routing a frame to PNG and failing a
/// session start a new one, and the send side drops a frame of an older generation, so nothing of an aborted session
/// or of video before a PNG frame is sent after it. Every frame is forced to be a keyframe until the session has queued
/// one in the frame's generation (a keyframe the encoder drops stays owed, and a fresh frame is asked for).</item>
/// <item><b>The watchdog.</b> While frames are in the encoder the loop's wait has a deadline, <see cref="StallTimeout"/>
/// after the last outcome, with or without new input; a stall aborts the session and counts a failure.</item>
/// <item><b>Bounded retirement.</b> A session is never drained: every retirement (a failure, a size change, the end of
/// the subscription) aborts it and waits at most <see cref="Quarantine.Bound"/> for its teardown (<see cref="Quarantine"/>).
/// <see cref="Dispose"/> returns at once.</item>
/// <item><b>Failures</b> (each counted per backend): a refused frame, an encoder error, a stall, an unknown or
/// out-of-order outcome, a frame that can't be built, an avcC that can't be made to say "no reordering". The session is
/// replaced; three in <see cref="FailureWindow"/>, a faulty start or a bad avcC give the backend up for the stream,
/// and with none left the frames are PNG with <c>encoder.failed</c>. A start the backend calls unsupported or busy
/// isn't a failure: the next backend takes that size, or PNG with <c>encoder.size</c>; a busy one is tried again after
/// a backoff from <see cref="BusyFirst"/> to <see cref="BusyMax"/>.</item>
/// </list>
/// </summary>
internal sealed class VideoStream : IDisposable
{
    public const string Unavailable = "encoder.unavailable";
    public const string Failed = "encoder.failed";
    public const string Size = "encoder.size";

    /// <summary>The video size: each side at most this, and at most <see cref="MaxPixels"/>; larger frames are PNG.</summary>
    public const int MaxSide = 4096;

    public const long MaxPixels = 4096L * 2304;
    public const int MaxInFlight = 2;
    public const int QueueSlots = 8;
    public const long HardwareBitrate = 2_000_000;
    public const long SoftwareBitrate = 8_000_000;
    public const int FailuresToGiveUp = 3;
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BusyFirst = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BusyMax = TimeSpan.FromSeconds(30);

    private sealed record RawFrame(byte[] Pixels, int Width, int Height, FrameHeader Header, int Generation);

    private sealed record Pending(long Id, FrameHeader Header, OutputSlots<EncodedFrame>.Reservation Reservation, int Generation, long Submitted,
        long KeyframeAsked, bool Forced);

    /// <summary>One encoder session and its submissions, by id; a late outcome finds it dead and is dropped.</summary>
    private sealed class Session(IH264Backend backend, int index, int width, int height)
    {
        public readonly IH264Backend Backend = backend;
        public readonly int Index = index;
        public readonly int Width = width;
        public readonly int Height = height;
        public IH264Session Encoder = null!;
        public readonly Lock Gate = new();
        public readonly SortedDictionary<long, Pending> Pending = [];
        public long NextId;

        /// <summary>The generation a keyframe of this session was queued in (under <see cref="Gate"/>): until then every frame is forced to be one.</summary>
        public int KeyedGeneration = -1;
        public bool Dead;
        public string? Failure;
        public bool FailureGivesUp;
        public int Fps;
        public int RefusedFps;
        public (byte[] Input, string Codec, string Description)? Avcc;
    }

    private readonly H264Backends _backends;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action _wake;
    private readonly Action _refresh;
    private readonly SkipCounter _skipped;
    private readonly OutputSlots<EncodedFrame> _slots = new(QueueSlots);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly Lock _rawLock = new();
    private RawFrame? _raw;
    private int _generation;
    private int _disposed;

    // Published by the loop, read by the render thread (Route).
    private volatile string? _fallback;
    private long _retryAt;
    private ImmutableHashSet<(int, int)> _pngSizes = [];

    // Render thread only.
    private bool _routedVideo;

    // Set by the render thread as frames leave video, taken by the loop (which retires the session).
    private int _leftVideo;

    // Any thread.
    private volatile int _fps;
    private long _keyframeAsked;
    private long _keyframeAnswered;

    // Loop only.
    private Session? _session;
    private long _lastProgress;
    private long _lastProgressAt;
    private readonly Dictionary<IH264Backend, Queue<long>> _failures = [];
    private readonly HashSet<IH264Backend> _givenUp = [];
    private readonly Dictionary<IH264Backend, Busy> _busy = [];

    /// <summary>A backend that said it was busy: tried again from <see cref="Until"/>; <see cref="Announced"/> once a fresh frame was asked for then.</summary>
    private sealed class Busy(long until, TimeSpan backoff)
    {
        public long Until = until;
        public TimeSpan Backoff = backoff;
        public bool Announced;
    }

    /// <summary>Tests: called as an encoded frame is built, before it is queued (a throw is a failure of the session).</summary>
    internal Action<long>? BeforeCommit;

    public VideoStream(H264Backends backends, int fps, Action wake, Action refresh, SkipCounter skipped, ILogger logger, TimeProvider time)
    {
        _backends = backends;
        _fps = Math.Clamp(fps, 1, 60);
        _wake = wake;
        _refresh = refresh;
        _skipped = skipped;
        _logger = logger;
        _time = time;
        _lastProgressAt = time.GetTimestamp();
        if (backends.NoneLeft)
            _fallback = Unavailable;
        Completion = Task.Run(RunAsync);
    }

    /// <summary>Ends once the loop has stopped and retired its session (within <see cref="Quarantine.Bound"/> of <see cref="Dispose"/>).</summary>
    public Task Completion { get; }

    /// <summary>Frames queued to send plus reservations (tests).</summary>
    internal int Occupied => _slots.Occupied;

    /// <summary>Frames queued to send (tests).</summary>
    internal int Queued => _slots.Queued;

    /// <summary>Frames in the current session's encoder (tests).</summary>
    internal int InFlight => _session?.Encoder.InFlight ?? 0;

    /// <summary>
    /// Render thread: null when this frame goes to the encoder (<see cref="Offer"/>), else why it is a PNG frame:
    /// <see cref="Size"/> (larger than the video size, or no backend takes its size), <see cref="Unavailable"/> or
    /// <see cref="Failed"/>. Leaving video starts a new generation, so no queued video follows the PNG frame.
    /// </summary>
    public string? Route(int width, int height)
    {
        var fallback = width > MaxSide || height > MaxSide || (long)width * height > MaxPixels
            ? Size
            : _fallback
              ?? (Interlocked.Read(ref _retryAt) is var retry and > 0 && _time.GetTimestamp() < retry ? Failed : null)
              ?? (Volatile.Read(ref _pngSizes).Contains((width, height)) ? Size : null);

        if (fallback == null)
        {
            _routedVideo = true;
            return null;
        }

        if (_routedVideo)
        {
            // Leaving video: no queued video follows the PNG frame, and the session is retired (PNG frames hold no
            // encoder); video resumes in a new session, with a keyframe.
            _routedVideo = false;
            NewGeneration();
            Volatile.Write(ref _leftVideo, 1);
            Kick();
        }

        return fallback;
    }

    /// <summary>Render thread, inside <c>OnFrame</c>: copies the frame's pixels into the raw slot (the newest wins).</summary>
    public void Offer(RenderedFrame frame, bool includeBounds)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var pixels = Rent(width * height * 4);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* to = pixels)
            {
                if (!frame.Image.ReadPixels(info, (IntPtr)to, width * 4, 0, 0))
                {
                    Return(pixels);
                    _skipped.Add();
                    return;
                }
            }
        }

        Offer(new RawFrame(pixels, width, height, FrameHeaders.From(frame, includeBounds), Volatile.Read(ref _generation)));
    }

    /// <summary>Tests: offers packed RGBA pixels as a frame with <paramref name="header"/>.</summary>
    internal void Offer(byte[] pixels, FrameHeader header) =>
        Offer(new RawFrame(pixels, header.PixelWidth, header.PixelHeight, header, Volatile.Read(ref _generation)));

    private void Offer(RawFrame raw)
    {
        RawFrame? replaced;
        lock (_rawLock)
        {
            replaced = _raw;
            _raw = raw;
        }

        if (replaced != null)
        {
            Return(replaced.Pixels);
            _skipped.Add();
        }

        Kick();
    }

    /// <summary>The client lost its decoder's state: the next video frame is a keyframe (a fresh frame is asked for).</summary>
    public void RequestKeyframe()
    {
        Interlocked.Increment(ref _keyframeAsked);
        Kick();
        _refresh();
    }

    /// <summary>The subscription's frame rate changed (<c>view</c>'s <c>maxFps</c>): the live session is asked for it.</summary>
    public void SetFrameRate(int fps)
    {
        _fps = Math.Clamp(fps, 1, 60);
        Kick();
    }

    /// <summary>The next encoded frame of the current generation, in order (send loop only); older ones are dropped.</summary>
    public bool TryTake(out EncodedFrame frame)
    {
        while (_slots.TryTake(out frame))
        {
            Kick();
            if (frame.Generation == Volatile.Read(ref _generation))
                return true;
            _skipped.Add();
        }

        return false;
    }

    /// <summary>Stops the loop; returns at once. The session is retired within <see cref="Quarantine.Bound"/> (<see cref="Completion"/>).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        NewGeneration();
    }

    private void NewGeneration()
    {
        Interlocked.Increment(ref _generation);
        RawFrame? dropped;
        lock (_rawLock)
        {
            dropped = _raw;
            _raw = null;
        }

        if (dropped != null)
        {
            Return(dropped.Pixels);
            _skipped.Add();
        }
    }

    private void Kick()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private byte[] Rent(int size)
    {
        while (_pool.TryDequeue(out var buffer))
        {
            if (buffer.Length == size)
                return buffer;
        }

        return new byte[size];
    }

    private void Return(byte[] buffer)
    {
        if (_pool.Count < 2)
            _pool.Enqueue(buffer);
    }

    // ---- the loop ----

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await WaitAsync();
                if (_stop.IsCancellationRequested)
                    break;
                await StepAsync();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The H.264 stream failed; its frames are PNG until the subscription ends");
            _fallback = Failed;
            NewGeneration();
            _refresh();
        }
        finally
        {
            if (_session is { } session)
                await RetireAsync(session, "the subscription ended");
            lock (_rawLock)
                _raw = null;
        }
    }

    private async Task WaitAsync()
    {
        long? deadline = null;
        if (_session is { } session && session.Encoder.InFlight > 0)
            deadline = _lastProgressAt + (long)(StallTimeout.TotalSeconds * _time.TimestampFrequency);
        var retry = Interlocked.Read(ref _retryAt);
        if (retry > 0)
            deadline = deadline is { } d ? Math.Min(d, retry) : retry;
        foreach (var busy in _busy.Values)
        {
            if (!busy.Announced)
                deadline = deadline is { } d ? Math.Min(d, busy.Until) : busy.Until;
        }

        if (deadline is not { } at)
        {
            await _signal.WaitAsync(_stop.Token);
            return;
        }

        var delay = _time.GetElapsedTime(_time.GetTimestamp(), at);
        if (delay <= TimeSpan.Zero)
            return;
        using var timeout = new CancellationTokenSource(delay, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeout.Token);
        try
        {
            await _signal.WaitAsync(either.Token);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            // The deadline.
        }
    }

    private async Task StepAsync()
    {
        if (Interlocked.Exchange(ref _leftVideo, 0) != 0 && _session is { } left)
            await RetireAsync(left, "the frames are PNG now");

        var now = _time.GetTimestamp();
        if (_session is { } session)
        {
            string? failure;
            bool givesUp;
            lock (session.Gate)
            {
                failure = session.Failure;
                givesUp = session.FailureGivesUp;
            }

            var progress = session.Encoder.Progress;
            if (failure != null)
            {
                await FailAsync(session, failure, givesUp);
            }
            else if (progress != _lastProgress || session.Encoder.InFlight == 0)
            {
                _lastProgress = progress;
                _lastProgressAt = now;
            }
            else if (_time.GetElapsedTime(_lastProgressAt, now) >= StallTimeout)
            {
                await FailAsync(session, $"no output for {(int)StallTimeout.TotalMilliseconds} ms with {session.Encoder.InFlight} frames in it", false);
            }
        }

        // A busy backend's backoff over: frames go to the encoder again (a fresh one is asked for, for a still panel).
        var retry = Interlocked.Read(ref _retryAt);
        var ask = false;
        if (retry > 0 && now >= retry)
        {
            Interlocked.Exchange(ref _retryAt, 0);
            ask = true;
        }

        foreach (var busy in _busy.Values)
        {
            if (!busy.Announced && now >= busy.Until)
            {
                busy.Announced = true;
                ask = true;
            }
        }

        if (ask)
            _refresh();

        if (_session is { } live && live.Fps != _fps && live.RefusedFps != _fps)
        {
            var fps = _fps;
            if (live.Encoder.TrySetFrameRate(fps))
            {
                live.Fps = fps;
                live.RefusedFps = 0;
            }
            else
            {
                // Remembered for this value only: another requested rate is asked again.
                live.RefusedFps = fps;
                _logger.LogInformation("H.264 encoder {Backend} refused the frame rate {Fps}; it keeps {Current}", live.Backend.Name, fps, live.Fps);
            }
        }

        await SubmitAsync();
    }

    private async Task SubmitAsync()
    {
        if (_session is { } current && current.Encoder.InFlight >= MaxInFlight)
            return;
        lock (_rawLock)
        {
            if (_raw == null)
                return;
        }

        var reservation = _slots.TryReserve();
        if (reservation == null)
            return; // The queue is full: frames drop at the raw slot until the socket takes some.

        RawFrame? raw;
        lock (_rawLock)
        {
            raw = _raw;
            _raw = null;
        }

        if (raw == null)
        {
            reservation.Dispose();
            return;
        }

        try
        {
            if (raw.Generation != Volatile.Read(ref _generation))
            {
                _skipped.Add();
                return;
            }

            var session = await SessionForAsync(raw.Width, raw.Height);
            if (session == null || raw.Generation != Volatile.Read(ref _generation))
            {
                _skipped.Add();
                return;
            }

            // A keyframe is owed until one is queued: for the generation (a new session, PNG frames, a failure) and for
            // the client's request. A dropped one stays owed, so no delta follows it.
            var asked = Interlocked.Read(ref _keyframeAsked);
            var id = session.NextId++;
            var started = _time.GetTimestamp();
            bool force;
            lock (session.Gate)
            {
                force = session.KeyedGeneration != raw.Generation || Interlocked.Read(ref _keyframeAnswered) < asked;
                session.Pending[id] = new Pending(id, raw.Header, reservation, raw.Generation, started, asked, force);
            }
            if (session.Encoder.InFlight == 0)
                _lastProgressAt = started;

            H264Submit result;
            string? problem;
            try
            {
                result = session.Encoder.Submit(raw.Pixels, raw.Width, raw.Height, id, force, out problem);
            }
            catch (Exception ex)
            {
                result = H264Submit.Refused;
                problem = ex.Message;
            }

            if (result == H264Submit.Accepted)
            {
                reservation = null; // Owned by its pending entry now.
                return;
            }

            lock (session.Gate)
                session.Pending.Remove(id);
            _skipped.Add();
            if (result == H264Submit.Refused)
            {
                lock (session.Gate)
                    session.Failure ??= "it refused a frame: " + problem;
                Kick();
            }
        }
        finally
        {
            reservation?.Dispose();
            Return(raw.Pixels);
        }
    }

    /// <summary>The session for frames of this size: the current one, else the first backend that starts one; null when none does.</summary>
    private async Task<Session?> SessionForAsync(int width, int height)
    {
        var now = _time.GetTimestamp();
        if (_session is { } current)
        {
            // A better backend that was busy may be free now.
            var better = _busy.Any(b => b.Value.Until <= now && _backends.Chain.Take(current.Index).Contains(b.Key));
            if (current.Width == width && current.Height == height && !better)
                return current;
            await RetireAsync(current, better ? "a better encoder may be free" : $"the frame is now {width}x{height}");
        }

        var sawUnsupported = false;
        var sawBusy = false;
        for (var i = 0; i < _backends.Chain.Count; i++)
        {
            var backend = _backends.Chain[i];
            if (_givenUp.Contains(backend) || _backends.IsGivenUp(backend))
                continue;
            if (_busy.TryGetValue(backend, out var busy) && now < busy.Until)
            {
                sawBusy = true;
                continue;
            }

            var probe = await _backends.ProbeAsync(backend).WaitAsync(_stop.Token);
            if (probe == H264Probe.Busy)
            {
                // Its probe found no session free: transient, so the backoff applies, as for a busy start.
                Backoff(backend, now, "its probe found no session free");
                sawBusy = true;
                continue;
            }

            if (probe != H264Probe.Available)
                continue;
            if (_backends.IsUnsupported(backend, width, height))
            {
                sawUnsupported = true;
                continue;
            }

            var session = new Session(backend, i, width, height) { Fps = _fps };
            try
            {
                var bitrate = backend.Kind == H264Kind.Hardware ? HardwareBitrate : SoftwareBitrate;
                session.Encoder = backend.Start(new H264Config(width, height, _fps, bitrate), outcome => OnOutcome(session, outcome));
            }
            catch (H264StartException ex) when (ex.Problem == H264StartProblem.Unsupported)
            {
                WatchTeardown(ex, backend);
                _backends.RecordUnsupported(backend, width, height, ex.Message);
                sawUnsupported = true;
                continue;
            }
            catch (H264StartException ex) when (ex.Problem == H264StartProblem.Busy)
            {
                WatchTeardown(ex, backend);
                Backoff(backend, now, ex.Message);
                sawBusy = true;
                continue;
            }
            catch (Exception ex)
            {
                WatchTeardown(ex, backend);
                // A faulty start: given up for this stream at once (and counted for the run, by size).
                _logger.LogWarning("H.264 encoder {Backend} failed to start at {Width}x{Height}: {Message}; given up for this stream",
                    backend.Name, width, height, ex.Message);
                _givenUp.Add(backend);
                _backends.RecordFaultyStart(backend, width, height);
                continue;
            }

            // What the start let go on the way (a retry's first session) is watched now: a wedged one counts while this runs.
            foreach (var released in session.Encoder.Released)
                _ = Quarantine.WatchAsync(released, backend, _backends, _time, _logger);
            _busy.Remove(backend);
            _backends.RecordGoodStart(backend);
            _session = session;
            _lastProgress = 0;
            _lastProgressAt = now;
            _logger.LogInformation("H.264 stream started: {Kind} {Backend}, {Width}x{Height} at {Fps} frames a second", H264Backends.Word(backend.Kind),
                backend.Name, width, height, _fps);
            return session;
        }

        // None: PNG frames, saying why.
        if (sawBusy)
            Interlocked.Exchange(ref _retryAt, _busy.Values.Where(b => b.Until > now).Min(b => b.Until));
        else if (sawUnsupported)
            Volatile.Write(ref _pngSizes, _pngSizes.Add((width, height)));
        else
            _fallback = _backends.NoneLeft ? Unavailable : Failed;
        NewGeneration();
        _refresh();
        return null;
    }

    /// <summary>
    /// A failed start's teardown of what it had created (a native session's invalidation can block): watched off this
    /// loop, so the next backend starts at once, and counted as quarantined if it doesn't end within the bound.
    /// </summary>
    private void WatchTeardown(Exception refusal, IH264Backend backend)
    {
        if (refusal is H264StartException { Teardown: { } teardown })
            _ = Quarantine.WatchAsync(teardown, backend, _backends, _time, _logger);
    }

    /// <summary>A busy backend: tried again after a backoff from <see cref="BusyFirst"/>, doubling to <see cref="BusyMax"/>; not counted.</summary>
    private void Backoff(IH264Backend backend, long now, string why)
    {
        var backoff = _busy.TryGetValue(backend, out var last) ? TimeSpan.FromTicks(Math.Min(last.Backoff.Ticks * 2, BusyMax.Ticks)) : BusyFirst;
        _busy[backend] = new Busy(now + (long)(backoff.TotalSeconds * _time.TimestampFrequency), backoff);
        _logger.LogInformation("H.264 encoder {Backend} is busy ({Message}); trying it again in {Seconds} s", backend.Name, why, backoff.TotalSeconds);
    }

    /// <summary>A failure of the session's backend in this stream: replaced, counted, and given up at the third in the window.</summary>
    private async Task FailAsync(Session session, string why, bool givesUp)
    {
        var backend = session.Backend;
        _logger.LogWarning("H.264 encoder {Backend} failed in a stream ({Why}); a new session follows", backend.Name, why);
        await RetireAsync(session, "it failed");
        NewGeneration();

        var now = _time.GetTimestamp();
        if (!_failures.TryGetValue(backend, out var times))
            _failures[backend] = times = new Queue<long>();
        while (times.Count > 0 && _time.GetElapsedTime(times.Peek(), now) > FailureWindow)
            times.Dequeue();
        times.Enqueue(now);
        if ((givesUp || times.Count >= FailuresToGiveUp) && _givenUp.Add(backend))
            _logger.LogWarning("H.264 encoder {Backend} given up for this stream ({Count} failures in {Seconds} s{Why})", backend.Name, times.Count,
                FailureWindow.TotalSeconds, givesUp ? ", one that can't be retried" : "");

        _refresh();
    }

    /// <summary>Ends the session without draining it: every pending frame's slot back, the teardown waited for at most the bound.</summary>
    private async Task RetireAsync(Session session, string why)
    {
        if (_session == session)
            _session = null;

        List<Pending> pending;
        lock (session.Gate)
        {
            session.Dead = true;
            pending = [.. session.Pending.Values];
            session.Pending.Clear();
        }

        foreach (var entry in pending)
        {
            entry.Reservation.Dispose();
            _skipped.Add();
        }

        _logger.LogDebug("H.264 session of {Backend} ended ({Why})", session.Backend.Name, why);
        await Quarantine.RetireAsync(session.Encoder, session.Backend, _backends, _time, _logger);
    }

    // ---- outcomes (the encoder's thread) ----

    private void OnOutcome(Session session, H264Outcome outcome)
    {
        Pending? pending;
        bool broken;
        lock (session.Gate)
        {
            if (session.Dead)
                return;
            if (!session.Pending.Remove(outcome.FrameId, out pending))
            {
                session.Failure ??= $"an outcome for frame {outcome.FrameId}, which it doesn't have";
                Kick();
                return;
            }

            if (session.Pending.Count > 0 && session.Pending.Keys.First() < outcome.FrameId)
                session.Failure ??= $"frame {outcome.FrameId} came before frame {session.Pending.Keys.First()}";
            if (outcome.Kind == H264OutcomeKind.Failed)
                session.Failure ??= outcome.Problem ?? "it failed a frame";
            broken = session.Failure != null;
        }

        try
        {
            using (pending.Reservation)
            {
                if (broken || outcome.Kind != H264OutcomeKind.Encoded)
                {
                    // Nothing of a session after its failure is sent: a later frame may refer to the one it lost.
                    _skipped.Add();
                    // A dropped keyframe stays owed (the next frame is forced again); a still panel needs a fresh frame for it.
                    if (!broken && outcome.Kind == H264OutcomeKind.Dropped && pending.Forced)
                        _refresh();
                    return;
                }

                BeforeCommit?.Invoke(outcome.FrameId);
                var frame = Build(session, pending, outcome);
                pending.Reservation.Commit(frame);
                if (outcome.Keyframe)
                {
                    lock (session.Gate)
                        session.KeyedGeneration = Math.Max(session.KeyedGeneration, pending.Generation);
                    InterlockedMax(ref _keyframeAnswered, pending.KeyframeAsked);
                }

                _wake();
            }
        }
        catch (Exception ex)
        {
            lock (session.Gate)
            {
                session.Failure ??= "a frame couldn't be built: " + ex.Message;
                if (ex is H264AvccException)
                    session.FailureGivesUp = true;
            }

            _skipped.Add();
        }
        finally
        {
            Kick();
        }
    }

    private EncodedFrame Build(Session session, Pending pending, H264Outcome outcome)
    {
        if (outcome.Data is not { Length: > 0 } data)
            throw new InvalidDataException("an encoded frame without data");
        var header = pending.Header with
        {
            Encoding = "h264",
            Keyframe = outcome.Keyframe,
            EncodeMs = Math.Round(_time.GetElapsedTime(pending.Submitted).TotalMilliseconds, 2),
        };

        if (outcome.Keyframe)
        {
            var (codec, description) = Describe(session, outcome.Avcc ?? throw new H264AvccException("a keyframe without its avcC"));
            header = header with { Codec = codec, Description = description, Encoder = H264Backends.Word(session.Backend.Kind) };
        }

        return new EncodedFrame(header, data, pending.Generation);
    }

    /// <summary>The keyframe's codec string and description: the avcC with its SPS rewritten to say "no reordering", cached per session.</summary>
    private static (string Codec, string Description) Describe(Session session, byte[] avcc)
    {
        if (session.Avcc is { } cached && cached.Input.AsSpan().SequenceEqual(avcc))
            return (cached.Codec, cached.Description);

        var rewritten = H264Sps.RewriteForStream(avcc, out var problem);
        if (problem != null)
            throw new H264AvccException(problem);
        var codec = $"avc1.{rewritten[1]:X2}{rewritten[2]:X2}{rewritten[3]:X2}";
        var description = Convert.ToBase64String(rewritten);
        session.Avcc = (avcc.ToArray(), codec, description);
        return (codec, description);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        var seen = Interlocked.Read(ref target);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
                return;
            seen = previous;
        }
    }
}

/// <summary>An avcC the stream can't send: the backend can't be relied on to say "no reordering", so it is given up for the stream.</summary>
internal sealed class H264AvccException(string message) : Exception(message);
