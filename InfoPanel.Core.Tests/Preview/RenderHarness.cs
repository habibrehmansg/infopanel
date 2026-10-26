using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// A workspace with one profile and a <see cref="RenderService"/> whose loops are driven by hand
/// (<see cref="RenderServiceOptions.AutoTick"/> false) over a manual clock and fake sensors.
/// </summary>
internal sealed class RenderHarness : IAsyncDisposable
{
    public static readonly SensorKey Load = SensorKey.Plugin("/test/load");

    private readonly TempDirectory _temp = new();

    public RenderHarness(Profile profile, IEnumerable<DisplayItem> items, bool autoTick = false, TimeProvider? time = null, RenderOptions? options = null)
    {
        Time = time ?? new ManualClock();
        var store = new ProfileStore(new DataPaths(_temp.Path));
        store.AddProfile(profile, items);
        Workspace = new Workspace(store, time: Time);
        ProfileId = profile.Guid;
        Sensors.Readings[Load] = new SensorReading(0, 100, 0, 0, "%");
        Renderer = new ProfileRenderer(Sensors, History, new FakeImageSource(), new FakeAssetLocator(_temp.Path), Time);
        States = new ProfileRenderStates();
        Service = new RenderService(Workspace, Renderer, States, new ThumbnailRenderer(Renderer, States), Time,
            new RenderServiceOptions(autoTick, options));
    }

    public TimeProvider Time { get; }

    public ManualClock Clock => (ManualClock)Time;

    public FakeSensorReader Sensors { get; } = new();

    public FakeSensorHistory History { get; } = new();

    public Workspace Workspace { get; }

    public ProfileRenderer Renderer { get; }

    public ProfileRenderStates States { get; }

    public RenderService Service { get; }

    public Guid ProfileId { get; }

    public ProfileDocument Document => Workspace.Open(ProfileId)!;

    public ProfileRenderState State => Service.GetState(ProfileId);

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

    /// <summary>A 300×100 profile: a black background, a bar bound to <see cref="Load"/> and a still shape.</summary>
    public static (Profile Profile, List<DisplayItem> Items) BarProfile(int width = 300, int height = 100)
    {
        var profile = new Profile("Bars", width, height) { BackgroundColor = "#FF000000" };
        var bar = new BarDisplayItem("Bar", profile)
        {
            X = 10, Y = 10, Width = 200, Height = 30, Color = "#FFFF0000", BackgroundColor = "#FF00FF00", Gradient = false, Frame = false,
            SensorType = SensorType.Plugin, PluginSensorId = Load.Id,
        };
        var shape = new ShapeDisplayItem("Shape", profile) { X = 220, Y = 50, Width = 40, Height = 40, ShowGradient = false }; // no gradient animation
        return (profile, [bar, shape]);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        _temp.Dispose();
    }
}

/// <summary>Records what it receives; copies the pixels and PNG while the frame is valid.</summary>
internal sealed class FakeSink(Guid profileId, FrameRequest request) : IFrameSink
{
    private readonly Lock _lock = new();
    private readonly List<Received> _frames = [];
    private readonly List<FrameSinkClosedReason> _closed = [];

    public Guid ProfileId { get; } = profileId;

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
internal sealed class BlockingSink(Guid profileId, FrameRequest request) : IFrameSink
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];

    public Guid ProfileId { get; } = profileId;

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
