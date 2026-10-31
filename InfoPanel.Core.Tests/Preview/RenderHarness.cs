using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Models;
using InfoPanel.Spec;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// A workspace over a temp data folder with one profile, and a <see cref="RenderService"/> whose loops are driven by hand
/// (<see cref="RenderServiceOptions.AutoTick"/> false) over a manual clock and fake sensors.
/// </summary>
internal sealed class RenderHarness : IAsyncDisposable
{
    public const string ProfileIdOf = "5f0c8a2e-3b7d-4e19-9a6c-1d2e3f405162";
    public static readonly SensorKey Load = SensorKey.Plugin("/test/load");

    private readonly TempDirectory _temp = new();

    public RenderHarness(JsonObject document, bool autoTick = false, TimeProvider? time = null, TimeProvider? drawTime = null)
    {
        Time = time ?? new ManualClock();
        var paths = new DataPaths(_temp.Path);
        var documents = new DocumentStore(paths);
        var blobs = new BlobStore(paths);
        documents.Open();
        blobs.Open();
        Workspace = new Workspace(documents, blobs, new WorkspaceOptions
        {
            Instance = DocKit.Instance,
            Time = Time,
            HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
        });

        var created = Workspace.Create(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = ProfileIdOf, ["document"] = document }));
        Assert.True(created.Entry is not null, string.Join("; ", created.Invalid?.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}") ?? []));
        ProfileId = ProfileIdOf;
        SetLoad(50);
        Service = new RenderService(Workspace, Scenes(Workspace, Sensors, History, drawTime), Time, new RenderServiceOptions(autoTick));
    }

    /// <summary>The runtime options the engine uses, over fake sensors, with the zone and locale pinned.</summary>
    public static SceneRuntimeOptions Scenes(Workspace workspace, ISensorReader sensors, ISensorHistory history, TimeProvider? drawTime = null) => new()
    {
        DrawTime = drawTime ?? TimeProvider.System,
        Inputs = new SensorSceneInputs(sensors, history),
        Limits = workspace.Limits,
        Support = workspace.Support,
        LocalZone = TimeZoneInfo.Utc,
        Locale = RegionalFormat.Invariant,
    };

    public TimeProvider Time { get; }

    public ManualClock Clock => (ManualClock)Time;

    public FakeSensorReader Sensors { get; } = new();

    public FakeSensorHistory History { get; } = new();

    public Workspace Workspace { get; }

    public RenderService Service { get; }

    public string ProfileId { get; }

    public ProfileDocument Document => Workspace.Open(ProfileId)!;

    public void SetLoad(double value) => Sensors.Readings[Load] = new SensorReading(0, 100, value, value, "%");

    public FakeSink Attach(float scale = 1, int maxFps = 30, bool includeBounds = true)
    {
        var sink = new FakeSink(ProfileId, new FrameRequest(scale, maxFps, includeBounds));
        Assert.True(Service.Attach(sink));
        return sink;
    }

    /// <summary>Advances the clock by <paramref name="ms"/> and runs one tick.</summary>
    public int Tick(int ms = 34)
    {
        Clock.Advance(ms);
        return Service.Tick(ProfileId);
    }

    /// <summary>Applies a commit of this workspace's instance, based on the current revision.</summary>
    public CommitResult Commit(params JsonObject[] ops)
    {
        var doc = Document;
        var body = new JsonObject
        {
            ["opId"] = Guid.NewGuid().ToString("N"),
            ["instance"] = doc.Instance,
            ["baseRev"] = doc.Rev,
            ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
        };
        var outcome = doc.Commit(JsonSerializer.SerializeToUtf8Bytes(body));
        Assert.True(outcome.Result is not null, DocKit.Describe(outcome));
        return outcome.Result;
    }

    /// <summary>A scene: a bar <c>2 × load</c> wide and 30 high (a number input <c>v</c>), red.</summary>
    public static JsonObject BarScene() => JsonNode.Parse("""
        {"format": "infopanel.scene/1", "inputs": {"v": {"type": "number"}},
         "nodes": [{"kind": "rect", "w": {"op": "mul", "args": [{"op": "input", "name": "v"}, 2]}, "h": 30, "fill": "#ff0000"}]}
        """)!.AsObject();

    /// <summary>A scene that animates: a bar that grows 100 pixels a second of elapsed time (a state cell), up to 280.</summary>
    public static JsonObject GrowingScene() => JsonNode.Parse("""
        {"format": "infopanel.scene/1",
         "state": {"t": {"init": 0, "next": {"op": "add", "args": [{"op": "state", "name": "t"}, {"op": "dt"}]}}},
         "nodes": [{"kind": "rect", "w": {"op": "min", "args": [{"op": "mul", "args": [{"op": "state", "name": "t"}, 100]}, 280]}, "h": 20, "fill": "#ffffff"}]}
        """)!.AsObject();

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> document on black with <paramref name="items"/>.</summary>
    public static JsonObject Doc(int width, int height, params JsonObject[] items) => new()
    {
        ["format"] = "infopanel.document/1",
        ["id"] = ProfileIdOf,
        ["profile"] = new JsonObject { ["name"] = "Bars", ["width"] = width, ["height"] = height, ["background"] = "#000000" },
        ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
    };

    /// <summary>A 300×100 document: on black, a bar <c>bar</c> bound to <see cref="Load"/> at (10, 10) and a still 40×40 <c>shape</c>.</summary>
    public static JsonObject BarDocument(int width = 300, int height = 100) => Doc(width, height,
        DocKit.Scene("bar", 10, 10, BarScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = Load.ToString() } } }),
        DocKit.Scene("shape", 220, 50, DocKit.Rect("#00ff00", 40, 40)));

    /// <summary>A 300×100 document with one growing bar <c>grow</c> at (10, 40).</summary>
    public static JsonObject GrowingDocument() => Doc(300, 100, DocKit.Scene("grow", 10, 40, GrowingScene()));

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Workspace.Dispose();
        _temp.Dispose();
    }
}

/// <summary>Records what it receives; copies the pixels and PNG while the frame is valid.</summary>
internal sealed class FakeSink(string profileId, FrameRequest request) : IFrameSink
{
    private readonly Lock _lock = new();
    private readonly List<Received> _frames = [];
    private readonly List<FrameSinkClosedReason> _closed = [];

    public string ProfileId { get; } = profileId;

    public FrameRequest Request { get; set; } = request;

    public bool Throws { get; set; }

    public bool KeepPng { get; set; }

    public IReadOnlyList<Received> Frames
    {
        get { lock (_lock) return [.. _frames]; }
    }

    public IReadOnlyList<FrameSinkClosedReason> Closed
    {
        get { lock (_lock) return [.. _closed]; }
    }

    public void OnFrame(RenderedFrame frame)
    {
        var pixels = frame.Image.PeekPixels().GetPixelSpan().ToArray();
        var png = KeepPng ? frame.GetPng() : default;
        lock (_lock)
            _frames.Add(new Received(frame, pixels, png));

        if (Throws)
            throw new InvalidOperationException("sink failure");
    }

    public void OnClosed(FrameSinkClosedReason reason)
    {
        lock (_lock)
            _closed.Add(reason);
    }

    public sealed record Received(RenderedFrame Frame, byte[] Pixels, ReadOnlyMemory<byte> Png);
}

/// <summary>Blocks inside <see cref="OnFrame"/> until <see cref="Release"/> is set; records what happens to it, in order.</summary>
internal sealed class BlockingSink(string profileId, FrameRequest request) : IFrameSink
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];

    public string ProfileId { get; } = profileId;

    public FrameRequest Request { get; } = request;

    public ManualResetEventSlim Entered { get; } = new();

    public ManualResetEventSlim Release { get; } = new();

    public IReadOnlyList<string> Events
    {
        get { lock (_lock) return [.. _events]; }
    }

    public void OnFrame(RenderedFrame frame)
    {
        Add("frame");
        Entered.Set();
        Release.Wait(TimeSpan.FromSeconds(10));
        Add("frame returned");
    }

    public void OnClosed(FrameSinkClosedReason reason) => Add("closed " + reason);

    private void Add(string e)
    {
        lock (_lock)
            _events.Add(e);
    }
}
