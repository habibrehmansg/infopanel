using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Spec;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Scenes;

public sealed class SceneRuntimeOptions
{
    public ISceneInputSource Inputs { get; init; } = NoSceneInputs.Instance;

    /// <summary>Decoded media; null draws every asset as missing.</summary>
    public MediaSampleCache? Media { get; init; }

    /// <summary>Media providers by name (capability <c>media.provider</c>).</summary>
    public IReadOnlyDictionary<string, IMediaProvider> Providers { get; init; } = new Dictionary<string, IMediaProvider>();

    public SpecLimits Limits { get; init; } = SpecLimits.Default;

    public EngineSupport Support { get; init; } = EngineSupport.All;

    public TimeZoneInfo? LocalZone { get; init; }

    public CultureInfo? Locale { get; init; }

    /// <summary>Text lines (of layouts made, not cached) a document may lay out per tick; an item that would exceed it is drawn as over budget for that tick.</summary>
    public int LayoutsPerTick { get; init; } = 4_000;

    /// <summary>An item whose evaluation or drawing takes longer than this, three times running, is suspended.</summary>
    public double ItemBudgetMs { get; init; } = 50;

    /// <summary>How long a suspended item is drawn as over budget before it is tried again.</summary>
    public TimeSpan SuspendFor { get; init; } = TimeSpan.FromSeconds(5);

    public SceneProcessBudget ProcessBudget { get; init; } = SceneProcessBudget.Shared;
}

/// <summary>
/// Scene nodes rendered at once (spec/docs/limits.md, process budgets: 100,000 across the process). A document is
/// admitted while it fits; one that doesn't is drawn as over budget until others stop.
/// </summary>
public sealed class SceneProcessBudget(int maxNodes)
{
    public const int DefaultMaxNodes = 100_000;

    /// <summary>The process's budget, shared by every runtime that isn't given another.</summary>
    public static SceneProcessBudget Shared { get; } = new(DefaultMaxNodes);

    private int _used;

    public int MaxNodes { get; } = maxNodes;

    public int Used => Volatile.Read(ref _used);

    internal bool TryReserve(int nodes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _used);
            if (current + nodes > MaxNodes) return false;
            if (Interlocked.CompareExchange(ref _used, current + nodes, current) == current) return true;
        }
    }

    internal void Release(int nodes) => Interlocked.Add(ref _used, -nodes);
}

/// <summary>
/// Renders one document in the scene format: loads a generation (<see cref="Load"/>), then advances it tick by tick
/// (<see cref="Tick"/>), each tick producing one <see cref="TickSnapshot"/> that every output, scale, thumbnail and
/// bounds measurement of the tick shares. Holds the document's runtime state (state cells by item, cell, definition and
/// resolved type; text layouts). Ticks are serialised; snapshots may be drawn on any thread.
/// </summary>
public sealed class SceneRuntime : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SceneRuntime>();

    private readonly SceneRuntimeOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ItemRuntime> _items = new(StringComparer.Ordinal);
    private SceneDocumentModel? _model;
    private SceneChecker? _scenes;
    private TickSnapshot? _current;
    private long _generation;
    private long _tick;
    private int _reserved = -1;
    private bool _disposed;

    public SceneRuntime(SceneRuntimeOptions? options = null)
    {
        _options = options ?? new SceneRuntimeOptions();
    }

    /// <summary>The latest tick, retained by the runtime (thumbnails during a tick use it); null before the first tick.</summary>
    public TickSnapshot? Current
    {
        get
        {
            lock (_lock) return _current?.Retain();
        }
    }

    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Checks a document and loads it as the next generation (<see cref="Load(Generation)"/>), at the document's own
    /// <c>rev</c> (0 without one); scenes whose JSON is the same as in the previous document checked here are not checked
    /// or compiled again. Throws <see cref="SceneDocumentException"/> for a document that is unsupported or invalid as a
    /// whole.
    /// </summary>
    public DocumentCheck Load(JsonElement document)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var load = Check(document, null);
            Load(load.Generation!);
            return load.Check;
        }
    }

    /// <summary>
    /// Loads an already-checked generation as the next one. A generation checked with this runtime's limits and support
    /// keeps its checked and compiled scenes, reused by identity, so a generation that changed one item costs one item's
    /// work; one checked with others is checked again with this runtime's (its document as JSON), so what is drawn
    /// doesn't depend on how the generation was made. Items keep their state across generations (by id, cell name,
    /// definition and resolved type). Snapshots report the generation's <see cref="Generation.Rev"/>. Throws
    /// <see cref="SceneDocumentException"/> when the generation's document is unsupported or invalid as a whole here.
    /// </summary>
    public void Load(Generation generation)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!generation.Limits.Equals(_options.Limits) || !SceneChecker.SameSupport(generation.Support, _options.Support))
                generation = Check(SpecJson.Write(generation.Document), generation.Rev).Generation!;
            var model = SceneDocumentModel.Load(generation, _generation + 1);
            _generation = model.Generation;
            _model = model;
            Reconcile(model);
            if (_reserved >= 0)
            {
                _options.ProcessBudget.Release(_reserved);
                _reserved = -1;
            }
        }
    }

    /// <summary>
    /// Checks a document with this runtime's limits and support, reusing the scenes of the previous check by their JSON
    /// text; throws <see cref="SceneDocumentException"/> when there is no generation. Under the lock.
    /// </summary>
    private GenerationLoad Check(JsonElement document, long? rev)
    {
        var load = Documents.Generation.Load(document, rev, _options.Limits, _options.Support, _scenes);
        if (load.Generation is null) throw new SceneDocumentException(load.Check);
        _scenes = load.Scenes;
        return load;
    }

    /// <summary>
    /// Advances the document to <paramref name="elapsed"/> seconds since rendering started (monotonic) at wall-clock
    /// <paramref name="now"/>: reads every input once, evaluates values and state, lays out text, takes the media
    /// frames. The returned snapshot is the caller's (dispose it); the runtime keeps its own reference as <see cref="Current"/>.
    /// </summary>
    public TickSnapshot Tick(double elapsed, DateTimeOffset now)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var model = _model ?? throw new InvalidOperationException("No document is loaded.");
            if (_reserved < 0 && _options.ProcessBudget.TryReserve(model.NodeCount)) _reserved = model.NodeCount;
            var snapshot = Build(model, elapsed, now, advance: true, overProcessBudget: _reserved < 0);
            var previous = _current;
            _current = snapshot;
            previous?.Dispose();
            return snapshot.Retain();
        }
    }

    /// <summary>
    /// A one-off snapshot of the loaded document that advances nothing (spec/docs/identity.md): every state cell at
    /// init, elapsed 0, the current inputs and <paramref name="now"/>. For a thumbnail of a document no output renders.
    /// </summary>
    public TickSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var model = _model ?? throw new InvalidOperationException("No document is loaded.");
            return Build(model, 0, now, advance: false, overProcessBudget: false);
        }
    }

    /// <summary>Renders a document once, as a one-off snapshot (state at init, elapsed 0).</summary>
    public static TickSnapshot RenderOnce(JsonElement document, DateTimeOffset now, SceneRuntimeOptions? options = null)
    {
        using var runtime = new SceneRuntime(options);
        runtime.Load(document);
        return runtime.Snapshot(now);
    }

    private SceneClock Clock(double elapsed, DateTimeOffset now) =>
        new(SceneMath.Finite(elapsed), now, _options.LocalZone ?? TimeZoneInfo.Local, _options.Locale ?? CultureInfo.CurrentCulture);

    private TickSnapshot Build(SceneDocumentModel model, double elapsed, DateTimeOffset now, bool advance, bool overProcessBudget)
    {
        var clock = Clock(elapsed, now);
        var tick = advance ? ++_tick : _tick;
        var services = new TickServices
        {
            Media = _options.Media,
            Providers = _options.Providers,
            LayoutsLeft = _options.LayoutsPerTick,
            TextLength = _options.Limits.TextLength,
        };
        var readings = new Dictionary<(string, string), SceneReading>();
        var histories = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        var diagnostics = new List<SceneDiagnostic>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var nowTicks = Stopwatch.GetTimestamp();
        var largestScale = TickSnapshot.LargestScale(model.Width, model.Height);

        SnapItem? Visit(DocItem item, SKMatrix parent, double parentRotation)
        {
            if (!item.Visible) return null;
            var world = parent.PreConcat(item.Local);
            var rotation = parentRotation + item.Rotation;
            switch (item)
            {
                case DocGroup g:
                {
                    var children = new List<SnapItem>(g.Children.Length);
                    foreach (var c in g.Children)
                        if (Visit(c, world, rotation) is { } s) children.Add(s);
                    return new SnapGroup { Id = g.Id, World = world, Local = item.Local, Rotation = rotation, Children = children.ToArray() };
                }
                case DocScene d:
                {
                    present.Add(d.Id);
                    var snap = new SnapScene
                    {
                        Id = d.Id, World = world, Local = item.Local, Rotation = rotation, LayoutW = d.LayoutW, LayoutH = d.LayoutH,
                        PlaceholderBox = d.PlaceholderBox, Problem = d.Problem,
                    };
                    if (d.Scene is not { } scene) return snap;
                    if (overProcessBudget)
                    {
                        snap.Problem = "budget";
                        diagnostics.Add(new SceneDiagnostic(d.Id, "budget.process", $"Over the process budget of {_options.ProcessBudget.MaxNodes} scene nodes."));
                        return snap;
                    }
                    var rt = advance ? Runtime(d.Id) : new ItemRuntime(d.Id);
                    rt.Use(scene);
                    if (advance && Stopwatch.GetElapsedTime(rt.SuspendedAt, nowTicks) < _options.SuspendFor && rt.Suspended)
                    {
                        snap.Problem = "budget";
                        diagnostics.Add(new SceneDiagnostic(d.Id, "budget.time", "Suspended: it took too long to evaluate or draw."));
                        return snap;
                    }
                    rt.Suspended = false;
                    var diag = new ItemDiagnostics(d.Id);
                    var inputs = Inputs(scene, d, readings, histories);
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        var evaluator = new SceneEvaluator(scene, rt, services, inputs, clock, d.LayoutW ?? 0, d.LayoutH ?? 0, diag,
                            Math.Max(1, _options.Limits.ExprWork) * 2, largestScale, world, rotation);
                        snap.Nodes = evaluator.Run(advance);
                        snap.Runtime = advance ? rt : null;
                    }
                    catch (BudgetExceeded e)
                    {
                        snap.Problem = "budget";
                        diag.Add(e.Code, e.Message);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Logger.Error(e, "Item {Id} could not be evaluated", d.Id);
                        snap.Problem = "invalid";
                        diag.Add("runtime.error", e.Message);
                    }
                    if (advance) Watchdog(rt, tick, Stopwatch.GetElapsedTime(start).TotalMilliseconds, nowTicks);
                    diagnostics.AddRange(diag.Items);
                    return snap;
                }
                default:
                    return null;
            }
        }

        var roots = new List<SnapItem>(model.Items.Length);
        foreach (var item in model.Items)
            if (Visit(item, SKMatrix.Identity, 0) is { } s) roots.Add(s);

        return new TickSnapshot(model.Generation, model.Rev, tick, clock, model.Width, model.Height, model.Background, roots.ToArray(),
            services.Retained.ToArray(), services.Owned.ToArray(), diagnostics, _options.ItemBudgetMs);
    }

    private ItemRuntime Runtime(string id)
    {
        if (!_items.TryGetValue(id, out var rt))
        {
            rt = new ItemRuntime(id);
            _items[id] = rt;
        }
        return rt;
    }

    /// <summary>
    /// The watchdog (spec/docs/limits.md): a tick is slow when the item's evaluation or its slowest draw of that tick
    /// took longer than the budget; three slow ticks running suspend it.
    /// </summary>
    private void Watchdog(ItemRuntime rt, long tick, double ms, long now)
    {
        if (ms > _options.ItemBudgetMs) rt.MarkSlow(tick);
        if (rt.TakeSuspension(tick))
        {
            rt.Suspended = true;
            rt.SuspendedAt = now;
            Logger.Warning("Item {Id} is suspended for {Seconds} s: it took too long to evaluate or draw", rt.Id, _options.SuspendFor.TotalSeconds);
        }
    }

    /// <summary>
    /// Brings the runtime state in line with a newly loaded generation (spec/docs/identity.md), whether or not any
    /// tick evaluates it: a removed item's state is dropped at once (undoing the removal brings the item back with its
    /// cells at init), and an item keeps only the cells its scene still defines by name, definition and resolved
    /// type. Hidden items keep theirs; an item drawn as a placeholder has no cells.
    /// </summary>
    private void Reconcile(SceneDocumentModel model)
    {
        var scenes = new Dictionary<string, CompiledScene?>(StringComparer.Ordinal);
        void Collect(DocItem i)
        {
            if (i is DocScene d) scenes[d.Id] = d.Scene;
            else if (i is DocGroup g) foreach (var c in g.Children) Collect(c);
        }
        foreach (var i in model.Items) Collect(i);
        foreach (var (id, rt) in _items.ToList())
        {
            if (!scenes.TryGetValue(id, out var scene))
            {
                _items.Remove(id);
                continue;
            }
            if (rt.Cells.Count == 0) continue;
            HashSet<CellKey> defined = scene is null ? [] : scene.Cells.Select(c => c.Key).ToHashSet();
            foreach (var key in rt.Cells.Keys.Where(k => !defined.Contains(k)).ToList()) rt.Cells.Remove(key);
        }
    }

    private InputSnap[] Inputs(CompiledScene scene, DocScene item, Dictionary<(string, string), SceneReading> readings,
        Dictionary<string, IReadOnlyList<double>> histories)
    {
        var inputs = new InputSnap[scene.InputNames.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            if (i >= item.Bindings.Length || item.Bindings[i] is not { } binding)
            {
                inputs[i] = InputSnap.Unbound;
                continue;
            }
            var type = scene.InputTypes[i];
            if (!readings.TryGetValue((binding.Key, type), out var reading))
            {
                try
                {
                    reading = _options.Inputs.Read(binding.Key, type) ?? SceneReading.Unavailable;
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "Sensor {Key} could not be read", binding.Key);
                    reading = SceneReading.Unavailable;
                }
                readings[(binding.Key, type)] = reading;
            }
            IReadOnlyList<double> history = [];
            if (type == "number")
            {
                if (!histories.TryGetValue(binding.Key, out history!))
                {
                    try
                    {
                        var samples = _options.Inputs.History(binding.Key);
                        history = samples.Count > 4096 ? samples.Skip(samples.Count - 4096).ToArray() : samples.ToArray();
                    }
                    catch (Exception e)
                    {
                        Logger.Warning(e, "History of {Key} could not be read", binding.Key);
                        history = [];
                    }
                    histories[binding.Key] = history;
                }
            }
            inputs[i] = new InputSnap(reading, binding.Stat, history);
        }
        return inputs;
    }

    /// <summary>For tests: a state cell's current value (a number), by item id and cell name; null when it has none.</summary>
    internal double? CellValue(string itemId, string cell)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(itemId, out var rt)) return null;
            foreach (var (key, state) in rt.Cells)
                if (key.Name == cell) return state.Value.N;
            return null;
        }
    }

    /// <summary>For tests: the lines of the text layouts an item keeps between ticks (its text nodes' and table cells').</summary>
    internal int RetainedLines(string itemId)
    {
        lock (_lock)
            return _items.TryGetValue(itemId, out var rt)
                ? rt.Texts.Values.Sum(t => t.Layout.Lines.Length) + rt.TableCells.Values.Sum(t => t.Layout.Lines.Length)
                : 0;
    }

    internal int CellCount(string itemId)
    {
        lock (_lock) return _items.TryGetValue(itemId, out var rt) ? rt.Cells.Count : 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Dispose();
            _current = null;
            if (_reserved >= 0) _options.ProcessBudget.Release(_reserved);
            _reserved = -1;
            _items.Clear();
        }
    }
}
