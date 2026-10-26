using InfoPanel.Drawing;
using InfoPanel.Editing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Output;

/// <param name="AutoTick">
/// True (the engine): each loop ticks on its own timer. False: loops never tick by themselves and
/// <see cref="RenderService.Tick"/> drives them, for deterministic tests.
/// </param>
/// <param name="Render">The options every loop draws with; <see cref="RenderOptions.Default"/> when null.</param>
public sealed record RenderServiceOptions(bool AutoTick = true, RenderOptions? Render = null);

/// <summary>
/// The engine's renderer of live profiles: one <see cref="RenderLoop"/> per profile that has sinks, created on the
/// first <see cref="Attach"/> and disposed when the last sink detaches, and thumbnails that share the loops'
/// <see cref="ProfileRenderState"/>s. Thread-safe.
/// <list type="bullet">
/// <item>A deleted profile (seen on <see cref="Workspace.ProfilesChanged"/>) stops its loop, and each of its sinks
/// gets <see cref="IFrameSink.OnClosed"/> with <see cref="FrameSinkClosedReason.ProfileDeleted"/>.</item>
/// <item><see cref="DisposeAsync"/> (host shutdown) stops every loop and closes the sinks with
/// <see cref="FrameSinkClosedReason.Shutdown"/>.</item>
/// <item>Closing never waits for a sink: <see cref="IFrameSink.OnClosed"/> is called at once, or, when the sink's
/// <see cref="IFrameSink.OnFrame"/> is under way, by the render thread right after it returns; no <c>OnFrame</c>
/// starts once the sink is closed (see <see cref="IFrameSink"/>).</item>
/// <item>Lock order: the service lock, then the workspace's; never a document's lock under the service lock.</item>
/// </list>
/// </summary>
public sealed class RenderService : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<RenderService>();

    private readonly Workspace _workspace;
    private readonly ProfileRenderer _renderer;
    private readonly ProfileRenderStates _states;
    private readonly ThumbnailRenderer _thumbnails;
    private readonly TimeProvider _time;
    private readonly RenderServiceOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, RenderLoop> _loops = [];
    private readonly Dictionary<IFrameSink, (RenderLoop Loop, AttachedSink Entry)> _sinkLoops = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, Task> _stopping = [];
    private long _frameId;
    private bool _disposed;

    public RenderService(Workspace workspace, ProfileRenderer renderer, ProfileRenderStates states, ThumbnailRenderer thumbnails,
        TimeProvider time, RenderServiceOptions? options = null)
    {
        _workspace = workspace;
        _renderer = renderer;
        _states = states;
        _thumbnails = thumbnails;
        _time = time;
        _options = options ?? new RenderServiceOptions();
        _workspace.ProfilesChanged += OnProfilesChanged;
    }

    /// <summary>What every loop draws with (the panel path must use the same options to match the preview).</summary>
    public RenderOptions Options => _options.Render ?? RenderOptions.Default;

    /// <summary>The number of profiles being rendered (each has at least one sink).</summary>
    public int LoopCount
    {
        get { lock (_lock) return _loops.Count; }
    }

    /// <summary>True while a loop renders the profile.</summary>
    public bool IsRendering(Guid profileId)
    {
        lock (_lock) return _loops.ContainsKey(profileId);
    }

    /// <summary>The render state the profile's loops and thumbnails share.</summary>
    public ProfileRenderState GetState(Guid profileId) => _states.Get(profileId);

    internal long NextFrameId() => Interlocked.Increment(ref _frameId);

    /// <summary>
    /// Starts sending the profile's frames to <paramref name="sink"/>; it gets the next frame whatever it holds.
    /// False when there is no such profile.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sink is already attached.</exception>
    /// <exception cref="System.IO.InvalidDataException">The profile's items file can't be read.</exception>
    public bool Attach(IFrameSink sink)
    {
        var doc = _workspace.Open(sink.ProfileId);
        if (doc == null)
            return false;

        RenderLoop loop;
        Task? previous = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sinkLoops.ContainsKey(sink))
                throw new InvalidOperationException("The sink is already attached.");

            if (!_loops.TryGetValue(doc.Id, out loop!) || loop.Document != doc)
            {
                previous = _stopping.GetValueOrDefault(doc.Id) ?? Task.CompletedTask;
                loop = new RenderLoop(this, doc, _renderer, _states.Get(doc.Id), _time, Options);
                _loops[doc.Id] = loop;
            }

            var entry = new AttachedSink(sink);
            loop.Add(entry);
            _sinkLoops[sink] = (loop, entry);
        }

        if (previous != null && _options.AutoTick)
            loop.Start(previous);
        loop.Wake();

        // Deleted between Open and here: the ProfilesChanged handler may have run before the loop was listed.
        if (!_workspace.IsOpen(doc))
            CloseDeleted();

        return true;
    }

    /// <summary>
    /// Stops sending frames to the sink: no <see cref="IFrameSink.OnFrame"/> starts after this returns (one already
    /// under way may still finish), and <see cref="IFrameSink.OnClosed"/> is not called. Does nothing if it isn't attached.
    /// </summary>
    public void Detach(IFrameSink sink)
    {
        lock (_lock)
        {
            if (!_sinkLoops.Remove(sink, out var attached))
                return;

            attached.Entry.Detach();
            if (attached.Loop.Remove(attached.Entry) == 0)
                StopLoop(attached.Loop);
        }
    }

    /// <summary>Tells the sink's loop its <see cref="IFrameSink.Request"/> changed, so it renders now rather than on its next timer tick.</summary>
    public void RequestChanged(IFrameSink sink)
    {
        RenderLoop? loop = null;
        lock (_lock)
        {
            if (_sinkLoops.TryGetValue(sink, out var attached))
                loop = attached.Loop;
        }

        loop?.Wake();
    }

    /// <summary>
    /// Runs one tick of the profile's loop now, on the calling thread, and returns the number of frames delivered
    /// (0 when nothing is rendering the profile). For tests and hand-driven loops (<see cref="RenderServiceOptions.AutoTick"/> false).
    /// </summary>
    public int Tick(Guid profileId)
    {
        RenderLoop? loop;
        lock (_lock)
            _loops.TryGetValue(profileId, out loop);
        return loop?.RunTick() ?? 0;
    }

    /// <summary>
    /// A PNG thumbnail <paramref name="width"/> pixels wide (<see cref="ThumbnailRenderer"/>). While a loop renders the
    /// profile it is drawn at the loop's current tick, without advancing it and with the loop's
    /// <see cref="RenderOptions"/> (smoothing included), so at the profile's width it is the loop's frame pixel for
    /// pixel and the loop's next frame is unchanged; otherwise it advances the profile's tick, so clocks show the
    /// current time, and draws values at their target (<see cref="ThumbnailRenderer.Options"/>).
    /// </summary>
    public byte[] RenderOnce(ProfileDocument document, int width)
    {
        RenderLoop? loop;
        lock (_lock)
            _loops.TryGetValue(document.Id, out loop);
        return loop != null
            ? _thumbnails.RenderPng(document, width, advanceTick: false, loop.Options)
            : _thumbnails.RenderPng(document, width, advanceTick: true);
    }

    /// <summary>Stops every loop, closes their sinks (<see cref="FrameSinkClosedReason.Shutdown"/>) and waits for the loops to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        List<AttachedSink> sinks;
        List<Task> stopped;
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _workspace.ProfilesChanged -= OnProfilesChanged;
            sinks = [.. _sinkLoops.Values.Select(p => p.Entry)];
            _sinkLoops.Clear();
            foreach (var loop in _loops.Values.ToList())
                StopLoop(loop);
            stopped = [.. _stopping.Values];
        }

        Close(sinks, FrameSinkClosedReason.Shutdown);

        try
        {
            await Task.WhenAll(stopped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "A render loop did not stop cleanly");
        }
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e) => CloseDeleted();

    /// <summary>Stops the loops of profiles that are no longer open in the workspace and closes their sinks.</summary>
    private void CloseDeleted()
    {
        var closed = new List<AttachedSink>();
        lock (_lock)
        {
            foreach (var loop in _loops.Values.ToList())
            {
                if (_workspace.IsOpen(loop.Document))
                    continue;

                closed.AddRange(TakeSinks(loop));
                _states.Remove(loop.ProfileId);
            }
        }

        if (closed.Count > 0)
            Logger.Information("Stopped rendering a deleted profile; closed {Count} sink(s)", closed.Count);
        Close(closed, FrameSinkClosedReason.ProfileDeleted);
    }

    /// <summary>
    /// Called by <paramref name="loop"/>'s tick when its profile can't be drawn within <see cref="FrameScale.MaxPixels"/>:
    /// stops the loop and closes its sinks with <see cref="FrameSinkClosedReason.Unrenderable"/>.
    /// </summary>
    internal void CloseUnrenderable(RenderLoop loop)
    {
        List<AttachedSink> closed;
        lock (_lock)
        {
            if (!_loops.TryGetValue(loop.ProfileId, out var listed) || listed != loop)
                return;

            closed = TakeSinks(loop);
        }

        Close(closed, FrameSinkClosedReason.Unrenderable);
    }

    /// <summary>Under the service lock: detaches every sink of <paramref name="loop"/>, stops it and returns the sinks to close.</summary>
    private List<AttachedSink> TakeSinks(RenderLoop loop)
    {
        var sinks = loop.Sinks().ToList();
        foreach (var entry in sinks)
        {
            _sinkLoops.Remove(entry.Sink);
            loop.Remove(entry);
        }

        StopLoop(loop);
        return sinks;
    }

    /// <summary>Under the service lock.</summary>
    private void StopLoop(RenderLoop loop)
    {
        if (_loops.TryGetValue(loop.ProfileId, out var listed) && listed == loop)
            _loops.Remove(loop.ProfileId);

        var id = loop.ProfileId;
        var stopped = loop.Stop();
        _stopping[id] = stopped;
        stopped.ContinueWith(task =>
        {
            lock (_lock)
            {
                if (_stopping.TryGetValue(id, out var current) && current == task)
                    _stopping.Remove(id);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Outside the service lock: closes each sink (<see cref="AttachedSink.Close"/> never waits for a sink).</summary>
    private static void Close(IEnumerable<AttachedSink> sinks, FrameSinkClosedReason reason)
    {
        foreach (var sink in sinks)
            sink.Close(reason);
    }
}
