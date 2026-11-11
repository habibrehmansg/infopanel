using InfoPanel.Documents;
using InfoPanel.Output.Gpu;
using InfoPanel.Scenes;
using Serilog;
using SkiaSharp;
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
public sealed record RenderServiceOptions(bool AutoTick = true)
{
    /// <summary>Makes the GPU <see cref="RenderService.Gpu"/> draws with when it is turned on; null: this computer's (<see cref="GpuRendering.ForThisComputer"/>).</summary>
    public Func<IGpuDevice>? GpuDevice { get; init; }
}

/// <summary>
/// The engine's renderer of live profiles: one <see cref="RenderLoop"/> per profile that has sinks, created on the
/// first <see cref="Attach"/> and disposed when the last sink detaches, each with its own <see cref="SceneRuntime"/>
/// (made with <see cref="SceneOptions"/>), and thumbnails from a running loop's latest tick snapshot. Thread-safe.
/// <list type="bullet">
/// <item>A profile with simulated sinks (<see cref="Attach(IFrameSink, ProfileDocument, bool)"/>) has a second loop for
/// them, with made-up values, listed apart: thumbnails, inspections and the latest snapshot only ever read the first.
/// Deletes, unrenderable profiles and shutdown close its sinks as the first's.</item>
/// <item>A deleted profile (seen on <see cref="Workspace.ProfilesChanged"/>) stops its loop, and each of its sinks
/// gets <see cref="IFrameSink.OnClosed"/> with <see cref="FrameSinkClosedReason.ProfileDeleted"/>.</item>
/// <item><see cref="DisposeAsync"/> (host shutdown) stops every loop and closes the sinks with
/// <see cref="FrameSinkClosedReason.Shutdown"/>.</item>
/// <item>Closing never waits for a sink: <see cref="IFrameSink.OnClosed"/> is called at once, or, when the sink's
/// <see cref="IFrameSink.OnFrame"/> is under way, by the render thread right after it returns; no <c>OnFrame</c>
/// starts once the sink is closed (see <see cref="IFrameSink"/>).</item>
/// <item>A loop draws one <see cref="ProfileDocument"/> instance (the workspace's lifetime rule): attaching for a
/// document that is no longer its profile's is refused, and a loop of a deleted document still listed when the id's new
/// document is attached is stopped first, its sinks closed as deleted.</item>
/// <item>Lock order: the service lock, then the workspace's; never a document's lock under the service lock.</item>
/// </list>
/// </summary>
public sealed class RenderService : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<RenderService>();

    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly RenderServiceOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RenderLoop> _loops = new(StringComparer.Ordinal);

    /// <summary>Simulated sinks' loops, apart from <see cref="_loops"/>, which every read of a profile's frames uses.</summary>
    private readonly Dictionary<string, RenderLoop> _simulatedLoops = new(StringComparer.Ordinal);

    private readonly Dictionary<IFrameSink, (RenderLoop Loop, AttachedSink Entry)> _sinkLoops = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Id, bool Simulated), Task> _stopping = [];
    private long _frameId;
    private bool _disposed;

    /// <param name="scenes">
    /// What every loop's runtime and every one-off thumbnail is made with (inputs, media, limits, support); its limits
    /// and support must be the workspace's, so a generation is never checked again when it is loaded.
    /// </param>
    public RenderService(Workspace workspace, SceneRuntimeOptions scenes, TimeProvider time, RenderServiceOptions? options = null)
    {
        _workspace = workspace;
        SceneOptions = scenes;
        _time = time;
        _options = options ?? new RenderServiceOptions();
        Gpu = new GpuRendering(_options.GpuDevice, time);
        _workspace.ProfilesChanged += OnProfilesChanged;
    }

    /// <summary>What every loop's runtime is made with.</summary>
    public SceneRuntimeOptions SceneOptions { get; }

    /// <summary>
    /// Drawing with the GPU (the <c>rendering.gpu</c> setting, which the engine applies here): every loop's frames and
    /// <see cref="RenderOnce"/>'s thumbnails draw through it. Disposed with the service.
    /// </summary>
    public GpuRendering Gpu { get; }

    internal TimeProvider Time => _time;

    /// <summary>The number of profiles being rendered (each has at least one sink); simulated loops aren't counted.</summary>
    public int LoopCount
    {
        get { lock (_lock) return _loops.Count; }
    }

    /// <summary>True while a loop renders the profile (not its simulated loop: <see cref="IsSimulating"/>).</summary>
    public bool IsRendering(string profileId)
    {
        lock (_lock) return _loops.ContainsKey(profileId);
    }

    /// <summary>True while the profile's simulated loop renders it (it has a simulated sink).</summary>
    public bool IsSimulating(string profileId)
    {
        lock (_lock) return _simulatedLoops.ContainsKey(profileId);
    }

    /// <summary>
    /// What a simulated loop's runtime is made with: <paramref name="live"/>'s options, with inputs of its own that make up
    /// every value over <paramref name="live"/>'s (<see cref="SimulatedSceneInputs"/>), and video decode sessions of its
    /// own, so its clock never moves another loop's videos (closed when the loop ends).
    /// </summary>
    internal static SceneRuntimeOptions SimulatedOptions(SceneRuntimeOptions live, TimeProvider time) => new()
    {
        Inputs = new SimulatedSceneInputs(live.Inputs, time, live.Limits),
        Media = live.Media,
        VideoOwner = new object(),
        Providers = live.Providers,
        Limits = live.Limits,
        Support = live.Support,
        LocalZone = live.LocalZone,
        Locale = live.Locale,
        Logger = live.Logger,
        LayoutsPerTick = live.LayoutsPerTick,
        ItemBudgetMs = live.ItemBudgetMs,
        DrawTime = live.DrawTime,
        SuspendFor = live.SuspendFor,
        ProcessBudget = live.ProcessBudget,
    };

    internal long NextFrameId() => Interlocked.Increment(ref _frameId);

    /// <summary>
    /// Starts sending the profile's frames to <paramref name="sink"/>; it gets the next frame whatever it holds.
    /// False when there is no such profile.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sink is already attached.</exception>
    /// <remarks>False too when the profile can't be opened (<c>unsupported</c> or <c>unreadable</c>).</remarks>
    public bool Attach(IFrameSink sink) => _workspace.Open(sink.ProfileId) is { } doc && Attach(sink, doc);

    /// <summary>
    /// Starts sending <paramref name="document"/>'s frames to <paramref name="sink"/>, for a caller that holds the
    /// document (it follows that instance's events too). False when the document is no longer its profile's (deleted,
    /// the id perhaps created again since): nothing is attached, and no other loop is touched.
    /// </summary>
    /// <param name="simulated">
    /// True: the sink gets the profile's simulated loop, a loop of its own drawn with made-up values
    /// (<see cref="SimulatedSceneInputs"/>), shared by the profile's simulated sinks and stopped with the last. No other
    /// read here (<see cref="RenderOnce"/>, <see cref="Inspect"/>, <see cref="LatestSnapshot"/>, <see cref="IsRendering"/>,
    /// <see cref="LoopCount"/>) sees it.
    /// </param>
    /// <exception cref="InvalidOperationException">The sink is already attached.</exception>
    public bool Attach(IFrameSink sink, ProfileDocument document, bool simulated = false)
    {
        RenderLoop loop;
        Task? previous = null;
        List<AttachedSink> retired = [];
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sinkLoops.ContainsKey(sink))
                throw new InvalidOperationException("The sink is already attached.");

            // Checked under the service lock: a delete's ProfilesChanged runs after the document left its slot, so either
            // it finds this loop listed, or this check finds the document gone.
            if (!_workspace.IsOpen(document))
                return false;

            var loops = simulated ? _simulatedLoops : _loops;
            if (!loops.TryGetValue(document.Id, out loop!) || loop.Document != document)
            {
                // A listed loop of an earlier document of this id was deleted, its ProfilesChanged not handled yet.
                if (loop != null)
                    retired = TakeSinks(loop);
                previous = _stopping.GetValueOrDefault((document.Id, simulated)) ?? Task.CompletedTask;
                var options = simulated ? SimulatedOptions(SceneOptions, _time) : SceneOptions;
                loop = new RenderLoop(this, document, new SceneRuntime(options), _time);
                loops[document.Id] = loop;
            }

            var entry = new AttachedSink(sink);
            loop.Add(entry);
            _sinkLoops[sink] = (loop, entry);
        }

        Close(retired, FrameSinkClosedReason.ProfileDeleted);
        if (previous != null && _options.AutoTick)
            loop.Start(previous);
        loop.Wake();
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
    /// Makes the sink fresh, so it gets the loop's next frame whatever it holds, and wakes its loop: for a sink whose
    /// output needs a new frame though its <see cref="IFrameSink.Request"/> didn't change (a video stream's keyframe).
    /// Does nothing if it isn't attached.
    /// </summary>
    public void Refresh(IFrameSink sink)
    {
        RenderLoop? loop = null;
        lock (_lock)
        {
            if (_sinkLoops.TryGetValue(sink, out var attached))
            {
                Volatile.Write(ref attached.Entry.Fresh, true);
                loop = attached.Loop;
            }
        }

        loop?.Wake();
    }

    /// <summary>
    /// Runs one tick of the profile's loop now, and of its simulated loop, on the calling thread, and returns the number
    /// of frames delivered (0 when nothing is rendering the profile). For tests and hand-driven loops
    /// (<see cref="RenderServiceOptions.AutoTick"/> false).
    /// </summary>
    public int Tick(string profileId)
    {
        RenderLoop? loop, simulated;
        lock (_lock)
        {
            _loops.TryGetValue(profileId, out loop);
            _simulatedLoops.TryGetValue(profileId, out simulated);
        }
        return (loop?.RunTick() ?? 0) + (simulated?.RunTick() ?? 0);
    }

    /// <summary>
    /// A PNG thumbnail <paramref name="width"/> pixels wide (<see cref="Thumbnail.Size"/>; spec/docs/api.md,
    /// "Thumbnails"), drawn through <see cref="Gpu"/> as the loops' frames are. While a loop renders the profile it is
    /// drawn from the tick snapshot the loop last drew, without advancing it, so at the profile's width it is the loop's
    /// frame (without the stats overlay, which only live outputs draw); otherwise from a one-off snapshot of the current
    /// generation (state at init, elapsed 0).
    /// </summary>
    public byte[] RenderOnce(ProfileDocument document, int width)
    {
        RenderLoop? loop;
        lock (_lock)
            _loops.TryGetValue(document.Id, out loop);

        using var snapshot = loop?.LatestSnapshot() ?? OneOff(document.Current);
        return Thumbnail.RenderPng(snapshot, width, Gpu);
    }

    /// <summary>
    /// Hands <paramref name="inspect"/> one tick snapshot and the document generation it was drawn from, and releases the
    /// snapshot after: one tick for a picture and whatever is measured from it (its bounds, its text, the same frame drawn
    /// without text), with the document's content of that same generation (its items' names and kinds), so they all
    /// agree. The generation is the document's current one: the live loop's latest tick when it shows that generation,
    /// else a one-off of it (a change the loop hasn't ticked yet is drawn and measured as it is now, Codex p2 #6). The
    /// one-off goes through the process budget as a live tick does, holding its reservation until the inspection is
    /// done, and draws what doesn't fit as budget placeholders (Codex p2-2 #2): the loop's runtime admits it when the
    /// profile has a loop (<see cref="RenderLoop.Inspection"/>), a runtime of its own otherwise.
    /// </summary>
    public T Inspect<T>(ProfileDocument document, Func<TickSnapshot, Generation, T> inspect)
    {
        RenderLoop? loop;
        lock (_lock)
            _loops.TryGetValue(document.Id, out loop);

        if (loop?.Inspection(_time.GetUtcNow()) is { } shown)
        {
            using (shown.Snapshot)
                return inspect(shown.Snapshot, shown.Generation);
        }

        var generation = document.Current;
        using var runtime = new SceneRuntime(SceneOptions);
        runtime.Load(generation);
        using var snapshot = runtime.Snapshot(_time.GetUtcNow(), admit: true);
        return inspect(snapshot, generation);
    }

    /// <summary>The snapshot of the latest tick of the profile's loop, retained for the caller; null when no loop has ticked.</summary>
    public TickSnapshot? LatestSnapshot(string profileId)
    {
        RenderLoop? loop;
        lock (_lock)
            _loops.TryGetValue(profileId, out loop);
        return loop?.LatestSnapshot();
    }

    private TickSnapshot OneOff(Generation generation)
    {
        using var runtime = new SceneRuntime(SceneOptions);
        runtime.Load(generation);
        return runtime.Snapshot(_time.GetUtcNow());
    }

    /// <summary>
    /// Stops every loop, closes their sinks (<see cref="FrameSinkClosedReason.Shutdown"/>), waits for the loops to stop and
    /// releases the GPU (<see cref="Gpu"/>).
    /// </summary>
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
            foreach (var loop in _loops.Values.Concat(_simulatedLoops.Values).ToList())
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

        Gpu.Dispose();
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e) => CloseDeleted();

    /// <summary>Stops the loops (simulated ones too) of profiles that are no longer open in the workspace and closes their sinks.</summary>
    private void CloseDeleted()
    {
        var closed = new List<AttachedSink>();
        lock (_lock)
        {
            foreach (var loop in _loops.Values.Concat(_simulatedLoops.Values).ToList())
            {
                if (_workspace.IsOpen(loop.Document))
                    continue;

                closed.AddRange(TakeSinks(loop));
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
            if (MapOf(loop) == null)
                return;

            closed = TakeSinks(loop);
        }

        Close(closed, FrameSinkClosedReason.Unrenderable);
    }

    /// <summary>Under the service lock: the map that lists <paramref name="loop"/>, live or simulated; null when neither does.</summary>
    private Dictionary<string, RenderLoop>? MapOf(RenderLoop loop)
    {
        if (_loops.TryGetValue(loop.ProfileId, out var live) && live == loop)
            return _loops;
        if (_simulatedLoops.TryGetValue(loop.ProfileId, out var simulated) && simulated == loop)
            return _simulatedLoops;
        return null;
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
        var map = MapOf(loop);
        map?.Remove(loop.ProfileId);

        // A new loop of the profile waits for the one it follows, of its own kind, to stop.
        var id = (loop.ProfileId, map == _simulatedLoops);
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
