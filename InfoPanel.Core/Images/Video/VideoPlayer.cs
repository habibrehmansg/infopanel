using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Spec;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Images.Video;

/// <summary>How videos are decoded (spec/docs/smart-nodes.md, "Video files"; limits.md, process budgets).</summary>
public sealed class VideoOptions
{
    /// <summary>The decoder; null: no video plays (every lookup fails, and the engine doesn't list <c>media.video</c>).</summary>
    public FFmpeg? Decoder { get; init; }

    /// <summary>Decode sessions open at once, each one video at one size (a floor of the process budgets: 8).</summary>
    public int MaxSessions { get; init; } = 8;

    /// <summary>A session no tick has asked for this long is closed.</summary>
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What open decoders may hold between them, estimated per session from its coded size and codec
    /// (<see cref="VideoPlayer.WorkingSet"/>): 768 MiB, so one video within the media limits always fits alone. A session
    /// that would take them past it waits as one past <see cref="MaxSessions"/> does.
    /// </summary>
    public long WorkingBytes { get; init; } = 768L * 1024 * 1024;

    /// <summary>Past <see cref="MaxSessions"/> or <see cref="WorkingBytes"/>, a session no tick has asked for this long is closed to make room.</summary>
    public TimeSpan RoomAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Frames a session decodes ahead of the time asked for.</summary>
    public int Ahead { get; init; } = 3;

    /// <summary>Tests: told each video's hash and the address FFmpeg is given (always none), just before FFmpeg reads the file.</summary>
    public Action<string, string?>? Opening { get; init; }
}

/// <summary>Where a player's decoded frames count: the media cache's byte budget.</summary>
internal interface IFrameBudget
{
    void Charge(long bytes);

    void Uncharge(long bytes);
}

/// <summary>
/// Plays videos for the media cache (spec/docs/smart-nodes.md, "Video files"). Each video is probed once from its
/// stored bytes (header only) to learn its size; a decode session per video, size and playback mode then decodes it on
/// the tick clock: on its own thread, a few frames ahead of the time asked for (a render loop), or on the caller's thread
/// to exactly the frame asked for (<c>synchronous</c>: tests and one-off renders). The frame shown is the newest whose
/// time is at or before the time asked for, never one later. Frames count against the cache's budget while a session
/// holds them. At most <see cref="VideoOptions.MaxSessions"/> sessions are open, their decoders' estimated memory within
/// <see cref="VideoOptions.WorkingBytes"/>; a closed session's decoder stops at FFmpeg's next read. One no tick has asked for in
/// <see cref="VideoOptions.IdleAfter"/> is closed. Every session reads its video through the store's stream and runs the
/// probe on that stream again before FFmpeg sees a byte of it.
/// </summary>
internal sealed class VideoPlayer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<VideoPlayer>();

    /// <summary>A frame counts as on time up to this much after the time asked for (rounding of the container's times).</summary>
    private const double Epsilon = 1e-6;

    private readonly IMediaAssetStore _store;
    private readonly VideoOptions _options;
    private readonly SpecLimits _limits;
    private readonly bool _synchronous;
    private readonly TimeProvider _time;
    private readonly IFrameBudget _budget;
    private readonly long _start;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Probe> _probes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Hash, int Width, int Height, MediaPlayback Playback), Session> _sessions = new();
    private bool _disposed;

    public VideoPlayer(IMediaAssetStore store, VideoOptions options, SpecLimits limits, bool synchronous, TimeProvider time, IFrameBudget budget)
    {
        _store = store;
        _options = options;
        _limits = limits;
        _synchronous = synchronous;
        _time = time;
        _budget = budget;
        _start = time.GetTimestamp();
    }

    private long Now() => (long)_time.GetElapsedTime(_start).TotalMilliseconds;

    /// <summary>Open decode sessions.</summary>
    internal int Sessions
    {
        get
        {
            lock (_lock) return _sessions.Count;
        }
    }

    /// <summary>The estimated working set of the open sessions still decoding (a failed one holds no decoder).</summary>
    internal long WorkingBytes
    {
        get
        {
            lock (_lock) return WorkingLocked();
        }
    }

    private long WorkingLocked() => _sessions.Values.Where(s => !s.Failed).Sum(s => s.Working);

    /// <summary>
    /// What FFmpeg holds to decode a video, estimated from its header: the coded picture at 2 bytes a pixel (8-bit 4:2:0
    /// is 1.5) for each picture a decoder of its codec keeps (the most references it may hold, one per decoding thread,
    /// and the one being decoded). Intra-only codecs keep no references. A 4096 x 4096 H.264 or HEVC stream is about 660 MB.
    /// The picture is the largest the decoder may allocate (<see cref="VideoDecoder.PictureArea"/>): a stream whose
    /// pictures are larger than its container states fails to decode rather than outgrow this.
    /// </summary>
    internal static long WorkingSet(MediaInfo info)
    {
        var references = info.Codec switch
        {
            "h264" or "hevc" => 16,
            "vp9" or "av1" => 8,
            "vp8" => 3,
            "mpeg4" => 2,
            _ => 0, // mjpeg, prores
        };
        return VideoDecoder.PictureArea(info) * 2 * (references + VideoDecoder.Threads + 1);
    }

    /// <summary>What a video's probe found, once it has run: its header or why it can't play.</summary>
    private sealed class Probe
    {
        public volatile bool Done;
        public MediaInfo? Info;
        public string? Problem;
    }

    /// <summary>
    /// The frame of video <paramref name="hash"/> at <paramref name="seconds"/> into it (elapsed × speed), drawn in a box
    /// of <paramref name="boxWidth"/> x <paramref name="boxHeight"/> (null: the video's own size on that axis), retained
    /// for the caller. Never blocks unless the player is synchronous.
    /// </summary>
    public MediaLookup Get(string hash, double seconds, MediaPlayback playback, float? boxWidth, float? boxHeight)
    {
        if (_options.Decoder is null)
            return new MediaLookup(MediaState.Failed, null, null, "media.video: This engine has no video decoder.");
        if (_disposed)
            return new MediaLookup(MediaState.Failed, null, null, "The media cache is shut down.");
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;

        var probe = ProbeOf(hash);
        if (!probe.Done) return new MediaLookup(MediaState.Loading, null, null, null);
        if (probe.Info is not { } info) return new MediaLookup(MediaState.Failed, null, null, probe.Problem);

        var (width, height) = DecodeSize(info, boxWidth, boxHeight);
        Session session;
        lock (_lock)
        {
            var now = Now();
            SweepLocked(now);
            var key = (hash, width, height, playback);
            if (!_sessions.TryGetValue(key, out session!))
            {
                if (_sessions.Count >= Math.Max(1, _options.MaxSessions) && !MakeRoomLocked(now))
                    return new MediaLookup(MediaState.Failed, null, info,
                        $"media.budget: {_options.MaxSessions} videos are decoding already; this one waits for one of them to stop.");
                // The decoders' memory, estimated: past the budget a session not asked for lately makes room; one alone always fits.
                var working = WorkingSet(info);
                while (_sessions.Count > 0 && WorkingLocked() + working > _options.WorkingBytes)
                {
                    if (!MakeRoomLocked(now))
                        return new MediaLookup(MediaState.Failed, null, info,
                            $"media.budget: The videos decoding already leave no room for this one's decoder (about {working / (1024 * 1024)} MiB); it waits for one of them to stop.");
                }
                session = new Session(this, hash, info, width, height, playback, working);
                _sessions.Add(key, session);
                session.LastAsked = now;
                if (!_synchronous) session.Start();
            }
            session.LastAsked = now;
        }
        return session.Get(seconds);
    }

    /// <summary>The size a video is decoded at: twice its box, in steps of an eighth of its own size, never above it.</summary>
    internal static (int Width, int Height) DecodeSize(MediaInfo info, float? boxWidth, float? boxHeight)
    {
        double w = info.Width, h = info.Height;
        var bw = boxWidth ?? (boxHeight is { } bh0 ? bh0 * w / h : w);
        var bh = boxHeight ?? (boxWidth is { } bw0 ? bw0 * h / w : h);
        var scale = Math.Min(1, Math.Max(2 * bw / w, 2 * bh / h));
        if (!double.IsFinite(scale)) scale = 1;
        scale = Math.Clamp(Math.Ceiling(scale * 8) / 8, 0.125, 1);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    private Probe ProbeOf(string hash)
    {
        Probe? probe;
        lock (_lock)
        {
            if (!_probes.TryGetValue(hash, out probe))
            {
                probe = new Probe();
                _probes[hash] = probe;
                if (_probes.Count > 4096 && _probes.FirstOrDefault(p => p.Value != probe && p.Value.Done) is { Value: not null } old) _probes.Remove(old.Key);
                if (_synchronous) Run(hash, probe);
                else Task.Run(() => Run(hash, probe));
            }
        }
        return probe;
    }

    private void Run(string hash, Probe probe)
    {
        var (info, problem) = Inspect(hash, out var stream);
        stream?.Dispose();
        probe.Info = info;
        probe.Problem = problem;
        probe.Done = true;
    }

    /// <summary>
    /// The video's stream from the store, checked by the probe and the media limits before anything else reads it; the
    /// stream is the caller's (at position 0) when the check passed.
    /// </summary>
    private (MediaInfo? Info, string? Problem) Inspect(string hash, out Stream? stream)
    {
        stream = null;
        try
        {
            stream = _store.Open(hash);
            if (stream is null) return (null, $"media.missing: No video {hash}.");
            var check = MediaValidator.Check(stream, _limits);
            if (check.Errors.FirstOrDefault() is { } error) return (null, $"{error.Code}: {error.Message}");
            if (check.Info is not { IsVideo: true } info) return (null, "media.format: Not a video file.");
            stream.Position = 0;
            return (info, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"media.missing: {e.Message}");
        }
        finally
        {
            if (stream is not null && stream.CanSeek && stream.Position != 0)
            {
                stream.Dispose();
                stream = null;
            }
        }
    }

    /// <summary>
    /// Forgets what is known of <paramref name="hash"/> when it failed (its blob stored since, say), so the next lookup
    /// probes it again. A video that played is left alone: its bytes are the same.
    /// </summary>
    public void Retry(string hash)
    {
        lock (_lock)
        {
            if (_probes.TryGetValue(hash, out var probe) && probe.Done && probe.Info is null)
                _probes.Remove(hash);
            foreach (var (key, session) in _sessions.Where(s => s.Key.Hash == hash && s.Value.Failed).ToList())
            {
                _sessions.Remove(key);
                session.Close();
            }
        }
    }

    private void SweepLocked(long now)
    {
        var idle = (long)_options.IdleAfter.TotalMilliseconds;
        foreach (var (key, session) in _sessions.Where(s => now - s.Value.LastAsked > idle).ToList())
        {
            _sessions.Remove(key);
            session.Close();
        }
    }

    private bool MakeRoomLocked(long now)
    {
        var room = (long)_options.RoomAfter.TotalMilliseconds;
        var oldest = _sessions.Where(s => now - s.Value.LastAsked > room).OrderBy(s => s.Value.LastAsked).FirstOrDefault();
        if (oldest.Value is null) return false;
        _sessions.Remove(oldest.Key);
        oldest.Value.Close();
        return true;
    }

    public void Dispose()
    {
        List<Session> sessions;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions) session.Close();
        foreach (var session in sessions) session.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>A decoded frame a session holds: its time on the session's clock and its picture (one reference, counted in the budget).</summary>
    private sealed record Frame(double Seconds, MediaSample Sample);

    /// <summary>
    /// One video decoded at one size for one playback mode. In a render loop its own thread decodes ahead of the time
    /// asked for into a short queue; synchronous, each lookup decodes to its frame on the caller's thread. Times are on
    /// the session's clock: elapsed × speed seconds, the video's own time plus whole loops before it.
    /// </summary>
    private sealed class Session(VideoPlayer player, string hash, MediaInfo info, int width, int height, MediaPlayback playback, long working)
    {
        private readonly object _gate = new();

        /// <summary>Cancelled when the session closes: its decoder stops at FFmpeg's next read, mid-decode or mid-open.</summary>
        private readonly CancellationTokenSource _cancel = new();

        /// <summary>The decoder's estimated working set (<see cref="WorkingSet"/>).</summary>
        public long Working => working;
        private readonly List<Frame> _queue = [];
        private VideoDecoder? _decoder;
        private Thread? _thread;
        private Frame? _shown;
        private Frame? _first;
        private string? _problem;
        private bool _closed;
        private bool _opened;
        private double _wanted;
        private bool _reset;
        private bool _ended;

        /// <summary>
        /// The loop's length: the stream's own, learned at its first end (<see cref="_learned"/>). Until then the
        /// container's header stands in for a render loop's seeks only (it may be wrong either way); 0 while unknown.
        /// </summary>
        private double _duration = info.DurationSeconds;

        private bool _learned;

        // The latest frame time decoded (the video's own time), the stream's end so far; the interval to it from the frame
        // decoded just before it, when that one came before it in the stream (NaN when a seek landed on it); and the
        // frame decoded last since the last seek (NaN right after one): what the stream's last frame lasts is learned
        // only from two frames decoded in a row.
        private double _lastFrame = double.NaN, _lastInterval = double.NaN, _previous = double.NaN;

        // Synchronous: the decoded frame not shown yet (its time in the video), and the time of the last frame.
        private double? _pending;

        public long LastAsked;

        public bool Failed
        {
            get
            {
                lock (_gate) return _problem is not null;
            }
        }

        public void Start()
        {
            _thread = new Thread(Work) { IsBackground = true, Name = $"Video {hash[7..15]}" };
            _thread.Start();
        }

        public void Join(TimeSpan wait) => _thread?.Join(wait);

        public MediaLookup Get(double seconds)
        {
            if (player._synchronous) return GetNow(seconds);
            lock (_gate)
            {
                if (_problem is not null) return new MediaLookup(MediaState.Failed, null, info, _problem);
                if (playback == MediaPlayback.Still) seconds = 0;
                var newest = _queue.LastOrDefault()?.Seconds ?? _shown?.Seconds ?? 0;
                // Back in time (a new loop of the scene's clock, a slower speed), or far ahead of what is decoded: start over there.
                if (seconds < (_shown?.Seconds ?? 0) - Epsilon || (_opened && seconds > newest + 2 && !_ended))
                {
                    _reset = true;
                    foreach (var frame in _queue) Drop(frame);
                    _queue.Clear();
                    if (_shown is not null && _shown.Seconds > seconds + Epsilon)
                    {
                        Drop(_shown);
                        _shown = null;
                    }
                }
                _wanted = seconds;
                Monitor.PulseAll(_gate);

                // The newest decoded frame at or before the time asked for becomes the one shown.
                var index = _queue.FindLastIndex(f => f.Seconds <= seconds + Epsilon);
                if (index >= 0)
                {
                    for (var i = 0; i < index; i++) Drop(_queue[i]);
                    if (_shown is not null) Drop(_shown);
                    _shown = _queue[index];
                    _queue.RemoveRange(0, index + 1);
                }
                if (_shown is { } shown && shown.Seconds <= seconds + Epsilon)
                {
                    // A loop has ended and the next one's first frame isn't decoded yet: the first frame, kept, shows meanwhile.
                    // Before the decoder reaches the stream's end (a few frames ahead) the header's length stands in.
                    if (playback == MediaPlayback.Loop && _duration > 0 && _first is { } first)
                    {
                        var loopStart = Math.Floor(seconds / _duration) * _duration;
                        if (shown.Seconds < loopStart - Epsilon && loopStart + first.Seconds <= seconds + Epsilon)
                            return Ready(first);
                    }
                    return Ready(shown);
                }
                if (_first is { } kept && kept.Seconds <= seconds + Epsilon && (playback == MediaPlayback.Still || _shown is null))
                    return Ready(kept);
                return new MediaLookup(MediaState.Loading, null, info, null);
            }
        }

        private MediaLookup Ready(Frame frame) =>
            frame.Sample.Retain() is { } sample
                ? new MediaLookup(MediaState.Ready, sample, info, null)
                : new MediaLookup(MediaState.Loading, null, info, null);

        /// <summary>Releases a frame the session held, and its bytes from the budget (the first frame stays until the session closes).</summary>
        private void Drop(Frame frame)
        {
            if (ReferenceEquals(frame, _first)) return;
            player._budget.Uncharge(frame.Sample.Bytes);
            frame.Sample.Release();
        }

        private Frame Keep(double seconds, SKImage image)
        {
            var sample = MediaSample.FromImage(image, info.Width, info.Height);
            player._budget.Charge(sample.Bytes);
            return new Frame(seconds, sample);
        }

        /// <summary>Opens the decoder: the store's stream for the video, probed again, then handed to FFmpeg.</summary>
        private VideoDecoder Open()
        {
            var (checkedInfo, problem) = player.Inspect(hash, out var stream);
            if (checkedInfo is null || stream is null)
            {
                stream?.Dispose();
                var code = problem?.Split(':')[0] ?? MediaProbe.MalformedCode;
                throw new VideoException(code, problem?[(code.Length + 1)..].Trim() ?? "The video can't be read.");
            }
            return VideoDecoder.Open(player._options.Decoder!, stream, checkedInfo, width, height, player._limits,
                url => player._options.Opening?.Invoke(hash, url), _cancel.Token);
        }

        private void Fail(Exception e)
        {
            var code = e is VideoException v ? v.Code : MediaProbe.MalformedCode;
            Logger.Warning("Video {Hash} can't be played: {Code}: {Message}", hash, code, e.Message);
            lock (_gate) _problem = $"{code}: {e.Message}";
        }

        // ---- synchronous: decode to exactly the frame asked for, on the caller's thread --------------------------

        private MediaLookup GetNow(double seconds)
        {
            lock (_gate)
            {
                if (_problem is not null) return new MediaLookup(MediaState.Failed, null, info, _problem);
                try
                {
                    _decoder ??= Open();
                    // A loop wraps at the stream's own end, never the header's: until the end is known, the time asked for
                    // is decoded towards, and the end found on the way is where it wraps.
                    var target = playback switch
                    {
                        MediaPlayback.Still => 0,
                        MediaPlayback.Loop when _learned => seconds % _duration,
                        _ => seconds,
                    };
                    ShowAt(target);
                    if (playback == MediaPlayback.Loop && _ended && _learned && target >= _duration - Epsilon)
                        ShowAt(seconds % _duration); // the length was learned at the end: wrap now
                    return _shown is { } shown ? Ready(shown) : new MediaLookup(MediaState.Loading, null, info, null);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Fail(e);
                    return new MediaLookup(MediaState.Failed, null, info, _problem);
                }
            }
        }

        /// <summary>Shows the newest frame at or before <paramref name="target"/> (the video's own time).</summary>
        private void ShowAt(double target)
        {
            var decoder = _decoder!;
            if (_shown is { } shown)
            {
                // Still right: the shown frame is at or before the target, and the next one (when known) after it.
                if (shown.Seconds <= target + Epsilon && (_pending is { } next ? next > target + Epsilon : _ended))
                    return;
                // Behind the shown frame, or far ahead of it: start from the key frame before the target.
                if (target < shown.Seconds - Epsilon || target > shown.Seconds + 2)
                {
                    Seek(decoder, target);
                    _pending = null;
                    _ended = false;
                }
            }
            var held = false;
            if (_pending is { } waiting)
            {
                if (waiting > target + Epsilon) return;
                decoder.Hold(waiting);
                held = true;
                _pending = null;
            }
            while (true)
            {
                var next = decoder.Next();
                if (next is not { } at)
                {
                    _ended = true;
                    break;
                }
                Saw(at);
                if (at <= target + Epsilon)
                {
                    decoder.Hold(at);
                    held = true;
                    continue;
                }
                _pending = at;
                break;
            }
            if (held) Show(Keep(decoder.HeldSeconds, decoder.ConvertHeld()));
            if (_ended && playback == MediaPlayback.Loop && !_learned)
            {
                ReplayTheEnd(decoder);
                Learn();
            }
        }

        /// <summary>Goes back to the key frame at or before <paramref name="seconds"/>: the next frame decoded doesn't follow the last.</summary>
        private void Seek(VideoDecoder decoder, double seconds)
        {
            decoder.Seek(seconds);
            _previous = double.NaN;
        }

        /// <summary>Notes a decoded frame's time (the video's own), for the stream's end and what its last frame lasts.</summary>
        private void Saw(double at)
        {
            var interval = !double.IsNaN(_previous) && at > _previous + Epsilon ? at - _previous : double.NaN;
            if (double.IsNaN(_lastFrame) || at > _lastFrame + Epsilon)
            {
                _lastFrame = at;
                _lastInterval = interval;
            }
            else if (Math.Abs(at - _lastFrame) <= Epsilon && double.IsNaN(_lastInterval))
            {
                _lastInterval = interval;
            }
            _previous = at;
        }

        /// <summary>
        /// At the stream's end, before it is learned: when what the last frame lasts isn't known (a seek landed on the
        /// last frame), decodes the end again from the key frame before that frame, noting only its times. The decoder
        /// is at the stream's end again after. On the caller's decoding thread, outside the gate in a render loop.
        /// </summary>
        private void ReplayTheEnd(VideoDecoder decoder)
        {
            if (_learned || double.IsNaN(_lastFrame) || !double.IsNaN(_lastInterval) || _lastFrame <= Epsilon)
                return;
            try
            {
                Seek(decoder, _lastFrame - Epsilon);
            }
            catch (VideoException)
            {
                return; // a container that can't seek there: the header's frame interval stands in
            }
            while (decoder.Next() is { } at) Saw(at);
        }

        /// <summary>
        /// At the stream's end, once: the loop's length becomes the stream's own, its last frame's time plus what that
        /// frame lasts (the interval to it from the frame before it, decoded in a row; else the header's frame interval,
        /// else 0.04 s). Called under the gate.
        /// </summary>
        private void Learn()
        {
            if (_learned || double.IsNaN(_lastFrame))
                return;
            var interval = !double.IsNaN(_lastInterval) ? _lastInterval
                : info.Frames > 1 && info.DurationSeconds > 0 ? info.DurationSeconds / info.Frames
                : 0.04;
            _duration = _lastFrame + interval;
            _learned = true;
        }

        private void Show(Frame frame)
        {
            if (_shown is not null) Drop(_shown);
            _shown = frame;
            // The first frame is kept for the session's life: Drop passes it over, Close releases it.
            if (_first is null && frame.Seconds <= Epsilon)
                _first = frame;
        }

        // ---- a render loop's session: its own thread decodes ahead ---------------------------------------------

        private void Work()
        {
            try
            {
                var decoder = Open();
                lock (_gate)
                {
                    if (_closed)
                    {
                        decoder.Dispose();
                        return;
                    }
                    _decoder = decoder;
                }
                Decode(decoder);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                // Closed while it opened or decoded: it stopped at FFmpeg's next read.
            }
            catch (Exception e) when (e is VideoException or IOException or ObjectDisposedException)
            {
                Fail(e);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Video {Hash}'s decoder stopped", hash);
                Fail(e);
            }
            finally
            {
                lock (_gate)
                {
                    _decoder?.Dispose();
                    _decoder = null;
                }
            }
        }

        private void Decode(VideoDecoder decoder)
        {
            double loopStart = 0;
            var lastPush = Stopwatch.StartNew();
            var held = false;
            double heldAt = 0;

            // The first frame, kept for the session's life: shown at the start and while a loop seeks back.
            var firstAt = decoder.Next() ?? throw new VideoException(MediaProbe.MalformedCode, "The video has no frames.");
            var first = Keep(firstAt, decoder.Convert());
            lock (_gate)
            {
                if (_closed)
                {
                    // Closed while the frame was being kept: Close released what the session held, not this one.
                    Drop(first);
                    return;
                }
                _first = first;
                _queue.Add(first); // the same frame: shown first, then kept
                _opened = true;
                Saw(firstAt);
            }

            while (true)
            {
                double wanted;
                bool reset;
                lock (_gate)
                {
                    while (!_closed && !_reset && (playback == MediaPlayback.Still || _ended
                        || _queue.Count(f => f.Seconds > _wanted + Epsilon) >= player._options.Ahead))
                        Monitor.Wait(_gate, 100);
                    if (_closed) return;
                    wanted = _wanted;
                    reset = _reset;
                    _reset = false;
                }

                if (reset)
                {
                    loopStart = playback == MediaPlayback.Loop && _duration > 0 ? Math.Floor(wanted / _duration) * _duration : 0;
                    Seek(decoder, wanted - loopStart);
                    held = false;
                    lock (_gate) _ended = false;
                    continue;
                }

                var next = decoder.Next();
                if (next is not { } at)
                {
                    if (held)
                    {
                        Push(heldAt, decoder.ConvertHeld());
                        held = false;
                    }
                    // The first end: from here on the loop is the stream's own length, never the header's.
                    var learning = playback == MediaPlayback.Loop && !_learned; // only this thread learns
                    if (learning) ReplayTheEnd(decoder);
                    double length;
                    double? realign = null;
                    lock (_gate)
                    {
                        Learn();
                        length = _duration;
                        // Seeks so far took the header's length: on the stream's own, the time asked for may fall in
                        // another loop and elsewhere in the video. The frames queued are dropped (some were skipped,
                        // behind the clock, so their times can't just be moved) and the decoder seeks there.
                        if (learning && length > 0 && Math.Abs(Math.Floor(_wanted / length) * length - loopStart) > Epsilon)
                        {
                            foreach (var frame in _queue) Drop(frame);
                            _queue.Clear();
                            loopStart = Math.Floor(_wanted / length) * length;
                            realign = _wanted - loopStart;
                            _ended = false;
                        }
                    }
                    if (realign is { } into)
                    {
                        Seek(decoder, into);
                        continue;
                    }
                    if (playback == MediaPlayback.Loop && length > 0)
                    {
                        loopStart += length;
                        Seek(decoder, 0);
                    }
                    else
                    {
                        lock (_gate) _ended = true;
                    }
                    continue;
                }
                Saw(at); // this thread's alone in a render loop
                var time = loopStart + at;
                if (time <= wanted + Epsilon)
                {
                    // Behind the clock: keep only the newest such frame, unless the picture hasn't moved for a while.
                    decoder.Hold(time);
                    held = true;
                    heldAt = time;
                    if (lastPush.ElapsedMilliseconds < 50) continue;
                    Push(heldAt, decoder.ConvertHeld());
                    held = false;
                    lastPush.Restart();
                    continue;
                }
                if (held)
                {
                    Push(heldAt, decoder.ConvertHeld());
                    held = false;
                }
                Push(time, decoder.Convert());
                lastPush.Restart();
            }
        }

        private void Push(double seconds, SKImage image)
        {
            var frame = Keep(seconds, image);
            lock (_gate)
            {
                if (_closed)
                {
                    Drop(frame);
                    return;
                }
                _queue.Add(frame);
                _queue.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
            }
        }

        /// <summary>Stops the session: its thread ends (it closes the decoder) and every frame it holds is released.</summary>
        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _cancel.Cancel(); // a decode or open under way stops at FFmpeg's next read
                Monitor.PulseAll(_gate);
                foreach (var frame in _queue) Drop(frame);
                _queue.Clear();
                if (_shown is not null) Drop(_shown);
                _shown = null;
                if (_first is { } first)
                {
                    _first = null;
                    player._budget.Uncharge(first.Sample.Bytes);
                    first.Sample.Release();
                }
                if (_thread is null)
                {
                    _decoder?.Dispose();
                    _decoder = null;
                }
            }
        }
    }
}
