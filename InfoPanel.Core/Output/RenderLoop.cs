using InfoPanel.Documents;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Output;

/// <summary>
/// Renders one profile for its sinks, created and disposed by <see cref="RenderService"/>, and running only while it
/// has sinks. It holds the profile's <see cref="SceneRuntime"/>.
/// <list type="bullet">
/// <item><b>When.</b> A <see cref="PeriodicTimer"/> (over the service's <see cref="TimeProvider"/>) at
/// 1000 / max(sink fps) ms, and early on <see cref="ProfileDocument.Changed"/>, a new sink or a changed request,
/// but at most once every <see cref="MinInterval"/>.</item>
/// <item><b>Which sinks.</b> A sink is due when it is new, its request changed, or 1000 / its fps ms (less half the
/// loop's period, to absorb timer jitter) have passed since its last frame. No due sink: no tick at all.</item>
/// <item><b>A tick.</b> The document's <see cref="ProfileDocument.Current"/> generation (read without its lock) is
/// loaded into the runtime when it is a new one; the runtime ticks once (elapsed time since the loop's first tick), and
/// that one <see cref="TickSnapshot"/> is drawn for each group of due sinks with the same
/// <see cref="FrameScale.Normalize"/>d scale, into the group's reused bitmap, with the stats overlay when the profile
/// asks for it; its bounds are measured once. Each group's pixels and the bounds are hashed (XxHash3). A due sink gets
/// the group's frame unless it already has a frame with that hash and revision (new and changed sinks always get it).
/// One <see cref="RenderedFrame"/> per group, shared by its sinks, PNG-encoded at most once.</item>
/// <item>Every scale, the bounds and thumbnails of a tick (<see cref="RenderService.RenderOnce"/>) come from the same
/// snapshot, so they agree pixel for pixel; tick numbers only go up.</item>
/// <item>A profile too large to draw within <see cref="FrameScale.MaxPixels"/> at any scale
/// (<see cref="FrameScale.Normalize"/> null; none within the document format's limits) is not drawn: its sinks are
/// closed with <see cref="FrameSinkClosedReason.Unrenderable"/>.</item>
/// </list>
/// </summary>
public sealed class RenderLoop
{
    public const int DefaultFps = 30;
    public const int MaxFps = 60;

    /// <summary>The shortest time between two ticks, however often the document changes.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(16);

    private static readonly ILogger Logger = Log.ForContext<RenderLoop>();

    private readonly RenderService _service;
    private readonly SceneRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly StatsOverlay _stats = new();
    private readonly Lock _sinkLock = new();
    private readonly List<AttachedSink> _sinks = [];
    private readonly Lock _tickLock = new();
    private readonly Dictionary<float, Surface> _surfaces = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _completion = Task.CompletedTask;
    private bool _started;
    private bool _shutDown;
    private string? _lastError;
    private Generation? _loaded;
    private long? _firstTick;

    internal RenderLoop(RenderService service, ProfileDocument document, SceneRuntime runtime, TimeProvider time)
    {
        _service = service;
        Document = document;
        _runtime = runtime;
        _time = time;
    }

    public string ProfileId => Document.Id;

    public ProfileDocument Document { get; }

    public int SinkCount
    {
        get { lock (_sinkLock) return _sinks.Count; }
    }

    /// <summary>The timer period for the current sinks: 1000 / max(sink fps) ms.</summary>
    public TimeSpan Period
    {
        get
        {
            lock (_sinkLock)
                return PeriodFor(_sinks);
        }
    }

    /// <summary>A request's frame rate: <see cref="DefaultFps"/> when 0 or less, at most <see cref="MaxFps"/>.</summary>
    public static int EffectiveFps(FrameRequest request) => request.MaxFps <= 0 ? DefaultFps : Math.Min(request.MaxFps, MaxFps);

    internal void Add(AttachedSink sink)
    {
        lock (_sinkLock)
            _sinks.Add(sink);
    }

    /// <summary>Removes the sink; returns how many are left.</summary>
    internal int Remove(AttachedSink sink)
    {
        lock (_sinkLock)
        {
            _sinks.Remove(sink);
            return _sinks.Count;
        }
    }

    internal AttachedSink[] Sinks()
    {
        lock (_sinkLock)
            return [.. _sinks];
    }

    /// <summary>Starts ticking on the thread pool once <paramref name="previous"/> (an earlier loop of the profile) has stopped.</summary>
    internal void Start(Task previous)
    {
        if (_stop.IsCancellationRequested)
            return; // detached before it started: Stop already shut it down

        _started = true;
        _completion = Task.Run(() => RunAsync(previous, _stop.Token));
    }

    /// <summary>Asks for a tick now (still at most once every <see cref="MinInterval"/>).</summary>
    internal void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The snapshot of the loop's latest tick, retained for the caller; null before the first tick or once stopped.</summary>
    internal TickSnapshot? LatestSnapshot()
    {
        try
        {
            return _runtime.Current;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Stops ticking and releases the bitmaps and the runtime; the task completes when the loop has stopped.</summary>
    internal Task Stop()
    {
        _stop.Cancel();

        // A hand-driven loop has no task of its own; shut it down on the pool, so a caller holding the service
        // lock never waits for a tick in progress (whose sink might be calling into the service).
        if (!_started)
            _completion = Task.Run(ShutDown);

        return _completion;
    }

    private async Task RunAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The previous loop logged its own failure.
        }

        Document.Changed += OnDocumentChanged;
        try
        {
            using var timer = new PeriodicTimer(Period, _time);
            Task<bool>? timerTick = null;
            Task? wake = null;
            long? last = null;

            while (!ct.IsCancellationRequested)
            {
                if (last is long lastTimestamp)
                {
                    var since = _time.GetElapsedTime(lastTimestamp);
                    if (since < MinInterval)
                        await Task.Delay(MinInterval - since, _time, ct).ConfigureAwait(false);
                }

                last = _time.GetTimestamp();
                RunTick();

                var period = Period;
                if (timer.Period != period)
                    timer.Period = period;

                timerTick ??= timer.WaitForNextTickAsync(ct).AsTask();
                wake ??= _wake.WaitAsync(ct);
                var woken = await Task.WhenAny(timerTick, wake).ConfigureAwait(false);
                if (woken == timerTick)
                {
                    if (!await timerTick.ConfigureAwait(false))
                        break;
                    timerTick = null;
                }
                else
                {
                    await wake.ConfigureAwait(false);
                    wake = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The render loop of profile {ProfileId} stopped on an error", ProfileId);
        }
        finally
        {
            Document.Changed -= OnDocumentChanged;
            ShutDown();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Saving and history eviction change neither pixels nor revision.
        if (e.Change is not (DocumentChange.Saved or DocumentChange.History))
            Wake();
    }

    private void ShutDown()
    {
        lock (_tickLock)
        {
            if (_shutDown)
                return;

            _shutDown = true;
            foreach (var surface in _surfaces.Values)
                surface.Dispose();
            _surfaces.Clear();
            _runtime.Dispose();
        }
    }

    /// <summary>
    /// One tick (see the class summary). Called by the loop's own task, or by <see cref="RenderService.Tick"/> when
    /// ticks are driven by hand. Returns the number of frames delivered (one per sink that got one).
    /// </summary>
    internal int RunTick()
    {
        lock (_tickLock)
        {
            if (_shutDown)
                return 0;

            try
            {
                return Tick();
            }
            catch (Exception ex)
            {
                // The runtime draws a failing item as a placeholder, so this is something else (out of memory, a disposed native object).
                if (ex.Message != _lastError)
                    Logger.Error(ex, "A render tick of profile {ProfileId} failed", ProfileId);
                _lastError = ex.Message;
                return 0;
            }
        }
    }

    private int Tick()
    {
        AttachedSink[] sinks;
        lock (_sinkLock)
            sinks = [.. _sinks];

        if (sinks.Length == 0)
            return 0;

        var now = _time.GetTimestamp();
        var requests = new FrameRequest?[sinks.Length];
        for (var i = 0; i < sinks.Length; i++)
        {
            try
            {
                requests[i] = sinks[i].Sink.Request;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A frame sink's request could not be read");
            }
        }

        var period = PeriodFor(requests.OfType<FrameRequest>());
        var due = new List<(AttachedSink Entry, FrameRequest Request)>();
        for (var i = 0; i < sinks.Length; i++)
        {
            if (requests[i] is not FrameRequest request)
                continue;

            var entry = sinks[i];
            if (!Equals(request, entry.Request))
            {
                entry.Request = request;
                entry.Fresh = true;
            }

            var interval = TimeSpan.FromSeconds(1.0 / EffectiveFps(request));
            if (entry.Fresh || _time.GetElapsedTime(entry.LastSent, now) >= interval - period / 2)
                due.Add((entry, request));
        }

        if (due.Count == 0)
            return 0;

        var includeBounds = due.Exists(d => d.Request.IncludeBounds);
        var generation = Document.Current;
        var profile = generation.Document.Profile;
        var profileWidth = Math.Max(1, profile.Width);
        var profileHeight = Math.Max(1, profile.Height);

        // No scale fits the pixel cap (no canvas within the document format's limits): nothing is drawn.
        if (FrameScale.Normalize(1, profileWidth, profileHeight) == null)
        {
            Logger.Warning("Profile {ProfileId} is {Width}×{Height}, too large to render within {MaxPixels} pixels at any scale; closing its sinks",
                ProfileId, profileWidth, profileHeight, FrameScale.MaxPixels);
            DropUnusedSurfaces([], profileWidth, profileHeight);
            _service.CloseUnrenderable(this);
            return 0;
        }

        if (!ReferenceEquals(generation, _loaded))
        {
            _runtime.Load(generation);
            _loaded = generation;
        }

        _firstTick ??= now;
        var tickStarted = _time.GetTimestamp();
        using var snapshot = _runtime.Tick(_time.GetElapsedTime(_firstTick.Value, now).TotalSeconds, _time.GetUtcNow());
        List<(float Scale, Surface Surface, List<AttachedSink> Sinks)> groups = [];
        (string Left, string Right)? overlay = profile.StatsOverlay == true ? _stats.Text(profile.Name) : null;
        foreach (var group in due.GroupBy(d => FrameScale.Normalize(d.Request.Scale, profileWidth, profileHeight)!.Value))
        {
            var surface = GetSurface(group.Key, profileWidth, profileHeight);
            surface.Canvas.Clear(SKColors.Transparent);
            snapshot.Draw(surface.Canvas, group.Key);
            if (overlay != null)
                StatsOverlay.Draw(surface.Canvas, group.Key, profileWidth, overlay.Value);
            surface.Canvas.Flush();
            groups.Add((group.Key, surface, group.Select(d => d.Entry).ToList()));
        }

        IReadOnlyList<ItemBounds> bounds = includeBounds ? snapshot.Bounds : [];
        _stats.Record(_time.GetElapsedTime(tickStarted), _time.GetTimestamp(), _time);
        DropUnusedSurfaces(requests, profileWidth, profileHeight);
        return Deliver(groups, bounds, snapshot.Tick, snapshot.Rev, profileWidth, profileHeight, now);
    }

    /// <summary>Hashes each group and hands its frame to the group's sinks that don't already have that content.</summary>
    private int Deliver(List<(float Scale, Surface Surface, List<AttachedSink> Sinks)> groups, IReadOnlyList<ItemBounds> bounds,
        long tick, long revision, int profileWidth, int profileHeight, long now)
    {
        var boundsBytes = BoundsBytes(bounds);
        var delivered = 0;
        foreach (var (scale, surface, groupSinks) in groups)
        {
            var hasher = new XxHash3();
            hasher.Append(surface.Bitmap.GetPixelSpan());
            hasher.Append(boundsBytes);
            var hash = hasher.GetCurrentHashAsUInt64();

            var recipients = groupSinks.Where(s => s.Fresh || s.LastHash != hash || s.LastRevision != revision).ToList();
            if (recipients.Count == 0)
                continue;

            var frame = new RenderedFrame(SKImage.FromPixels(surface.Bitmap.PeekPixels()))
            {
                ProfileId = ProfileId,
                Rev = revision,
                FrameId = _service.NextFrameId(),
                Tick = tick,
                Scale = scale,
                PixelWidth = surface.Bitmap.Width,
                PixelHeight = surface.Bitmap.Height,
                ProfileWidth = profileWidth,
                ProfileHeight = profileHeight,
                Bounds = bounds,
                ContentHash = hash,
            };

            try
            {
                foreach (var sink in recipients)
                {
                    sink.Fresh = false;
                    sink.LastSent = now;
                    sink.LastHash = hash;
                    sink.LastRevision = revision;
                    try
                    {
                        if (sink.Deliver(frame))
                            delivered++;
                    }
                    catch (Exception ex)
                    {
                        delivered++;
                        Logger.Error(ex, "A frame sink of profile {ProfileId} failed", ProfileId);
                    }
                }
            }
            finally
            {
                frame.Close();
            }
        }

        return delivered;
    }

    private Surface GetSurface(float scale, int profileWidth, int profileHeight)
    {
        var (width, height) = FrameScale.PixelSize(scale, profileWidth, profileHeight);
        if (_surfaces.TryGetValue(scale, out var surface))
        {
            if (surface.Bitmap.Width == width && surface.Bitmap.Height == height)
                return surface;

            surface.Dispose();
        }

        surface = new Surface(width, height);
        _surfaces[scale] = surface;
        return surface;
    }

    /// <summary>Releases the bitmaps of scales no sink asks for any more.</summary>
    private void DropUnusedSurfaces(FrameRequest?[] requests, int profileWidth, int profileHeight)
    {
        var used = requests.OfType<FrameRequest>().Select(r => FrameScale.Normalize(r.Scale, profileWidth, profileHeight)).ToHashSet();
        foreach (var scale in _surfaces.Keys.Where(s => !used.Contains(s)).ToList())
        {
            _surfaces[scale].Dispose();
            _surfaces.Remove(scale);
        }
    }

    private static TimeSpan PeriodFor(IEnumerable<AttachedSink> sinks)
    {
        var requests = new List<FrameRequest>();
        foreach (var sink in sinks)
        {
            try
            {
                requests.Add(sink.Sink.Request);
            }
            catch (Exception)
            {
                // Logged by the tick.
            }
        }

        return PeriodFor(requests);
    }

    private static TimeSpan PeriodFor(IEnumerable<FrameRequest> requests)
    {
        var fps = requests.Select(EffectiveFps).DefaultIfEmpty(DefaultFps).Max();
        return TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
    }

    /// <summary>The bounds as bytes for the content hash: their JSON, as the frame header carries them.</summary>
    private static byte[] BoundsBytes(IReadOnlyList<ItemBounds> bounds) =>
        bounds.Count == 0 ? [] : JsonSerializer.SerializeToUtf8Bytes(bounds, SpecJson.Options);

    /// <summary>A reused Rgba8888 premultiplied bitmap and its canvas, one per scale.</summary>
    private sealed class Surface : IDisposable
    {
        public Surface(int width, int height)
        {
            Bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            Canvas = new SKCanvas(Bitmap);
        }

        public SKBitmap Bitmap { get; }

        public SKCanvas Canvas { get; }

        public void Dispose()
        {
            Canvas.Dispose();
            Bitmap.Dispose();
        }
    }
}

/// <summary>
/// A sink attached to a <see cref="RenderLoop"/>: its scheduling state (render thread only) and the gate that orders
/// its <see cref="IFrameSink.OnFrame"/> and <see cref="IFrameSink.OnClosed"/> calls. The gate is held only to flip
/// flags, never during a sink call, so closing never waits for a sink. A close that arrives while
/// <see cref="IFrameSink.OnFrame"/> runs is delivered by the render thread right after it returns.
/// </summary>
internal sealed class AttachedSink(IFrameSink sink)
{
    private static readonly ILogger Logger = Log.ForContext<AttachedSink>();

    private readonly Lock _gate = new();
    private bool _delivering;
    private bool _stopped;
    private FrameSinkClosedReason? _pendingClose;

    public IFrameSink Sink { get; } = sink;

    /// <summary>The request last seen; a different one makes the sink fresh again.</summary>
    public FrameRequest? Request;

    /// <summary>New or changed: gets the next frame whatever it holds.</summary>
    public bool Fresh = true;

    public long LastSent;
    public ulong LastHash;
    public long LastRevision;

    /// <summary>
    /// Calls <see cref="IFrameSink.OnFrame"/> unless the sink was detached or closed; returns whether it did. A close
    /// that came in meanwhile is delivered here, after <c>OnFrame</c> returned. Exceptions from the sink propagate.
    /// </summary>
    public bool Deliver(RenderedFrame frame)
    {
        lock (_gate)
        {
            if (_stopped)
                return false;
            _delivering = true;
        }

        try
        {
            Sink.OnFrame(frame);
        }
        finally
        {
            FrameSinkClosedReason? reason;
            lock (_gate)
            {
                _delivering = false;
                reason = _pendingClose;
                _pendingClose = null;
            }

            if (reason is { } closed)
                NotifyClosed(closed);
        }

        return true;
    }

    /// <summary>The owner detached it: no <c>OnFrame</c> starts from now on (one under way may finish); no <c>OnClosed</c>.</summary>
    public void Detach()
    {
        lock (_gate)
            _stopped = true;
    }

    /// <summary>
    /// The service detached it: no <c>OnFrame</c> starts from now on, and <see cref="IFrameSink.OnClosed"/> is called
    /// once, now or, if an <c>OnFrame</c> is under way, by the render thread when it returns. Does nothing if the sink
    /// was already detached or closed.
    /// </summary>
    public void Close(FrameSinkClosedReason reason)
    {
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            if (_delivering)
            {
                _pendingClose = reason;
                return;
            }
        }

        NotifyClosed(reason);
    }

    private void NotifyClosed(FrameSinkClosedReason reason)
    {
        try
        {
            Sink.OnClosed(reason);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A frame sink failed while closing");
        }
    }
}
