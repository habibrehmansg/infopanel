using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Preview.Video;
using InfoPanel.Tests.Desktop;
using InfoPanel.Tests.Preview.Video;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// A <c>png</c> subscription is paced by its frames' encode time: while encoding at the asked rate would take more than
/// the budget's share of the render thread, it is sent fewer frames a second, and it gets its rate back when the cost
/// falls. Driven by hand on the engine host with a manual clock; every tick draws new pixels (the bar grows with time).
/// </summary>
public sealed class PngPacingTests : IAsyncLifetime
{
    /// <summary>A sixtieth of a second, rounded up to a whole tick.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromTicks((TimeSpan.TicksPerSecond + 59) / 60);

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly string _id = Guid.NewGuid().ToString();
    private WebApplicationFactory<Program>? _factory;

    /// <summary>A bar that grows with time: every tick has new pixels.</summary>
    private static JsonObject Growing() => JsonNode.Parse("""
        {"format": "infopanel.scene/1",
         "state": {"t": {"init": 0, "next": {"op": "add", "args": [{"op": "state", "name": "t"}, {"op": "dt"}]}}},
         "nodes": [{"kind": "rect", "w": {"op": "mul", "args": [{"op": "state", "name": "t"}, 10]}, "h": 20, "fill": "#ffffff"}]}
        """)!.AsObject();

    public ValueTask InitializeAsync()
    {
        var doc = TestDocs.Doc("Growing", 400, 100, TestDocs.Scene("grow", 0, 40, Growing()));
        doc["id"] = _id;
        var folder = Path.Combine(_base.DataDir, "profiles", _id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), doc.ToJsonString());
        File.WriteAllText(Path.Combine(_base.DataDir, "profiles.json"), $$"""{"order":["{{_id}}"]}""");
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new RenderServiceOptions(AutoTick: false));
        }));
        _ = _factory.Services;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        await _base.DisposeAsync();
    }

    private RenderService Render => _factory!.Services.GetRequiredService<RenderService>();

    /// <summary>
    /// Ticks the profile's loop at 60 Hz for <paramref name="seconds"/> of manual time and counts the PNG frames the sink was
    /// given. A 60 fps sink on the same loop sets its period, as its own timer would tick.
    /// </summary>
    private int Run(WebPreviewSink sink, double seconds)
    {
        var frames = 0;
        for (var tick = 0; tick < seconds * 60; tick++)
        {
            _clock.Advance(Step);
            Render.Tick(sink.ProfileId);
            while (sink.TryTakeFrame(out var frame))
            {
                frame.Sent();
                frames++;
            }
        }

        return frames;
    }

    /// <summary>Ticks the profile's loop at 60 Hz for <paramref name="seconds"/> of manual time; a session's send loop takes the frames.</summary>
    private void Tick(string profileId, double seconds)
    {
        for (var tick = 0; tick < seconds * 60; tick++)
        {
            _clock.Advance(Step);
            Render.Tick(profileId);
        }
    }

    private WebPreviewSink Attach(Func<double> encodeMs)
    {
        var sink = new WebPreviewSink(_id, new FrameRequest(1, 30), () => { }, (_, _) => { }) { EncodeMs = _ => encodeMs() };
        Assert.True(Render.Attach(new Probe(_id)));
        Assert.True(Render.Attach(sink));
        return sink;
    }

    /// <summary>A sink at 60 fps that keeps nothing.</summary>
    private sealed class Probe(string profileId) : IFrameSink
    {
        public string ProfileId => profileId;

        public FrameRequest Request { get; } = new(1, 60, IncludeBounds: false);

        public void OnFrame(RenderedFrame frame) { }
    }

    [Fact]
    public void SlowEncodes_LowerThePngRate_ToTheBudgetsShare()
    {
        var sink = Attach(() => 40);

        var frames = Run(sink, 10);
        TestContext.Current.TestOutputHelper?.WriteLine($"MEASURED {frames} frames in 10 s at 40 ms, paced MaxFps {sink.Request.MaxFps}");

        // 40 ms an encode at 30 fps would be 120 % of a core: at most 30 % of it is 7.5 encodes a second.
        Assert.True(frames <= 80, $"{frames} PNG frames encoded in 10 s at 40 ms an encode (30 fps asked)");
        Assert.True(frames >= 50, $"{frames} PNG frames: the rate is lowered to the budget, not to a trickle");
    }

    [Fact]
    public void CheapEncodes_KeepTheAskedRate_AndTheRateComesBack_WhenEncodesGetCheap()
    {
        var cost = 2.0;
        var sink = Attach(() => cost);
        var cheap = Run(sink, 2);
        cost = 40;
        Run(sink, 1);
        var slow = Run(sink, 2);
        cost = 2;
        Run(sink, 1);
        var back = Run(sink, 2);
        TestContext.Current.TestOutputHelper?.WriteLine($"MEASURED 2 s windows: {cheap} at 2 ms, {slow} at 40 ms, {back} at 2 ms again, MaxFps {sink.Request.MaxFps}");
        Assert.InRange(cheap, 58, 62);
        Assert.InRange(slow, 10, 20);
        Assert.InRange(back, 58, 62);
        Assert.Equal(30, sink.Request.MaxFps);
    }
    [Fact]
    public async Task AViewChangingOnlyTheScale_WhilePaced_KeepsTheAskedRate_AndThePace()
    {
        var socket = new FakeWebSocket();
        var session = new PreviewSession(socket, _factory!.Services.GetRequiredService<Workspace>(), Render, NullLogger.Instance);
        var run = session.RunAsync(CancellationToken.None, CancellationToken.None);
        Assert.True((await socket.NextAsync()).IsText("hello"));
        await socket.ReceiveAsync(new { type = "subscribe", profileId = _id, scale = 1, maxFps = 30 });
        await socket.NextAsync(m => m.IsText("doc"));
        var sink = session.CurrentSink!;
        sink.EncodeMs = _ => 40;
        Assert.True(Render.Attach(new Probe(_id)));

        Tick(_id, 2);
        var paced = sink.Request.MaxFps;
        Assert.True(paced < 30, $"paced to {paced} fps at 40 ms an encode");

        // Only the scale: the client's 30 fps stays asked, and the pace stays in force at the new scale.
        await socket.ReceiveAsync(new { type = "view", scale = 2 });
        Assert.Equal((2f, 30), (sink.Requested.Scale, sink.Requested.MaxFps));
        Assert.Equal((2f, paced), (sink.Request.Scale, sink.Request.MaxFps));

        // The same cost: neither changes.
        Tick(_id, 1);
        Assert.Equal((2f, 30), (sink.Requested.Scale, sink.Requested.MaxFps));
        Assert.Equal((2f, paced), (sink.Request.Scale, sink.Request.MaxFps));

        socket.ReceiveClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AnH264Subscription_BackToVideo_AfterSlowPngFrames_GetsItsAskedRateBack()
    {
        // 2100 pixels wide: at scale 2 wider than the video size (PNG frames), at scale 1 video.
        var id = TestDocs.Create(_factory!.Services.GetRequiredService<Workspace>(), TestDocs.Doc("Wide", 2100, 20, TestDocs.Scene("grow", 0, 0, Growing())));
        var backends = new H264Backends([new FakeH264Backend()], NullLogger.Instance);
        var sink = new WebPreviewSink(id, new FrameRequest(2, 30), () => { }, (_, _) => { }, WebPreviewSink.H264,
            (wake, refresh, skipped) => new VideoStream(backends, 30, wake, refresh, skipped, NullLogger.Instance, _clock))
        {
            EncodeMs = _ => 40,
        };
        Assert.True(Render.Attach(new Probe(id)));
        Assert.True(Render.Attach(sink));

        Run(sink, 2);
        Assert.True(sink.Request.MaxFps < 30, $"PNG frames at 40 ms an encode paced to {sink.Request.MaxFps} fps");

        // Within the video stream's grace (manual time), every frame goes to video, however fast its encoder starts.
        sink.Request = sink.Requested with { Scale = 1 };
        Render.RequestChanged(sink);
        Run(sink, 0.5);
        Assert.Equal(sink.Requested, sink.Request);
        Assert.Equal(30, sink.Request.MaxFps);

        sink.Retire();
        Render.Detach(sink);
    }
}
