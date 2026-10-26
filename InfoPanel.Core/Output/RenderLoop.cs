using InfoPanel.Drawing;
using InfoPanel.Editing;
using Serilog;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Output;

/// <summary>
/// Renders one profile for its sinks, created and disposed by <see cref="RenderService"/>, and running only while it
/// has sinks.
/// <list type="bullet">
/// <item><b>When.</b> A <see cref="PeriodicTimer"/> (over the service's <see cref="TimeProvider"/>) at
/// 1000 / max(sink fps) ms, and early on <see cref="ProfileDocument.Changed"/>, a new sink or a changed request,
/// but at most once every <see cref="MinInterval"/>.</item>
/// <item><b>Which sinks.</b> A sink is due when it is new, its request changed, or 1000 / its fps ms (less half the
/// loop's period, to absorb timer jitter) have passed since its last frame. No due sink: no tick at all.</item>
/// <item><b>A tick.</b> <c>state.Tick++</c>; due sinks are grouped by <see cref="FrameScale.Normalize"/>d scale;
/// under the document's <c>SyncRoot</c> each group is drawn into its reused bitmap and
/// <see cref="ProfileRenderer.MeasureBounds"/> runs once (profile coordinates, shared by every group); outside the
/// lock each group's pixels and the bounds are hashed (XxHash3). A due sink gets the group's frame unless it already
/// has a frame with that hash and revision (new and changed sinks always get it). One <see cref="RenderedFrame"/> per
/// group, shared by its sinks, PNG-encoded at most once.</item>
/// <item>Several groups, thumbnails (<see cref="RenderService.RenderOnce"/>) and bounds of one tick all draw at the
/// same <see cref="ProfileRenderState.Tick"/>, so animation state advances once per tick, and read the same time,
/// sensor readings and history (frozen per tick by <see cref="ProfileRenderState"/>).</item>
/// <item>A profile too large to draw within <see cref="FrameScale.MaxPixels"/> at any scale
/// (<see cref="FrameScale.Normalize"/> null) is not drawn: its sinks are closed with
/// <see cref="FrameSinkClosedReason.Unrenderable"/>.</item>
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
    private readonly ProfileRenderer _renderer;
    private readonly ProfileRenderState _state;
    private readonly TimeProvider _time;
    private readonly RenderOptions _options;
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

    internal RenderLoop(RenderService service, ProfileDocument document, ProfileRenderer renderer, ProfileRenderState state,
        TimeProvider time, RenderOptions options)
    {
        _service = service;
        Document = document;
        _renderer = renderer;
        _state = state;
        _time = time;
        _options = options;
    }

    public Guid ProfileId => Document.Id;

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

    /// <summary>Stops ticking and releases the bitmaps; the task completes when the loop has stopped.</summary>
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
            Logger.Error(ex, "The render loop of profile {ProfileGuid} stopped on an error", ProfileId);
        }
        finally
        {
            Document.Changed -= OnDocumentChanged;
            ShutDown();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Saving changes neither pixels nor revision.
        if (e.Change != DocumentChange.Saved)
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
                // Drawing skips items that throw, so this is something else (out of memory, a disposed native object).
                if (ex.Message != _lastError)
                    Logger.Error(ex, "A render tick of profile {ProfileGuid} failed", ProfileId);
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
        List<(float Scale, Surface Surface, List<AttachedSink> Sinks)> groups = [];
        IReadOnlyList<ItemBounds> bounds = [];
        long tick = 0, revision = 0;
        int profileWidth, profileHeight;
        bool unrenderable;

        lock (Document.SyncRoot)
        {
            var profile = Document.Profile;
            profileWidth = Math.Max(1, profile.Width);
            profileHeight = Math.Max(1, profile.Height);

            // No scale fits the pixel cap (only a profile far over Workspace.MaxDimension): nothing is drawn.
            unrenderable = FrameScale.Normalize(1, profileWidth, profileHeight) == null;
            if (!unrenderable)
            {
                tick = ++_state.Tick;
                revision = Document.Revision;

                foreach (var group in due.GroupBy(d => FrameScale.Normalize(d.Request.Scale, profileWidth, profileHeight)!.Value))
                {
                    var surface = GetSurface(group.Key, profileWidth, profileHeight);
                    surface.Canvas.Clear(SKColors.Transparent);
                    _renderer.Draw(surface.Canvas, profile, Document.Items, _state, _options, group.Key);
                    surface.Canvas.Flush();
                    groups.Add((group.Key, surface, group.Select(d => d.Entry).ToList()));
                }

                if (includeBounds)
                    bounds = _renderer.MeasureBounds(profile, Document.Items, _state);
            }
        }

        if (unrenderable)
        {
            Logger.Warning("Profile {ProfileGuid} is {Width}×{Height}, too large to render within {MaxPixels} pixels at any scale; closing its sinks",
                ProfileId, profileWidth, profileHeight, FrameScale.MaxPixels);
            DropUnusedSurfaces([], profileWidth, profileHeight);
            _service.CloseUnrenderable(this);
            return 0;
        }

        DropUnusedSurfaces(requests, profileWidth, profileHeight);
        return Deliver(groups, bounds, tick, revision, profileWidth, profileHeight, now);
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
                Revision = revision,
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
                        Logger.Error(ex, "A frame sink of profile {ProfileGuid} failed", ProfileId);
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

    /// <summary>The bounds as bytes for the content hash: id, x, y, w, h, rotation (little-endian) and the group flag.</summary>
    private static byte[] BoundsBytes(IReadOnlyList<ItemBounds> bounds)
    {
        const int size = 16 + 5 * 4 + 1;
        var bytes = new byte[bounds.Count * size];
        var span = bytes.AsSpan();
        foreach (var b in bounds)
        {
            b.Id.TryWriteBytes(span);
            BinaryPrimitives.WriteSingleLittleEndian(span[16..], b.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[20..], b.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span[24..], b.W);
            BinaryPrimitives.WriteSingleLittleEndian(span[28..], b.H);
            BinaryPrimitives.WriteSingleLittleEndian(span[32..], b.Rotation);
            span[36] = b.IsGroup ? (byte)1 : (byte)0;
            span = span[size..];
        }

        return bytes;
    }

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
