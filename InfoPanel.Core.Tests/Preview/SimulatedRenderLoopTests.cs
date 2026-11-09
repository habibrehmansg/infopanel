using System.Reflection;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Output;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// Simulated subscriptions on the render service: a loop of their own per profile, with made-up numbers, that no other
/// output sees.
/// </summary>
public class SimulatedRenderLoopTests
{
    /// <summary>A key the fake sensors never have.</summary>
    private static readonly SensorKey Missing = SensorKey.Plugin("/test/missing");

    private const int Width = 300, Height = 100;

    /// <summary>
    /// A bar <c>2 × v</c> wide and 30 high in red at (0, 0), and below it at (0, 40) a 100 × 50 graph of the same key in
    /// green, a bar a pixel, so its leftmost bar is drawn only when the history holds 101 values.
    /// </summary>
    private static JsonObject Document()
    {
        var binding = new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = Missing.ToString() } } };
        var graph = JsonNode.Parse("""
            {"format": "infopanel.scene/1", "inputs": {"v": {"type": "number"}},
             "nodes": [{"kind": "series", "input": "v", "w": 100, "h": 50, "min": 0, "max": 100, "mode": "bars", "pitch": 1, "barWidth": 1, "fill": "#00ff00"}]}
            """)!.AsObject();
        return RenderHarness.Doc(Width, Height,
            DocKit.Scene("bar", 0, 0, RenderHarness.BarScene(), binding),
            DocKit.Scene("graph", 0, 40, graph, binding));
    }

    private static RenderHarness Harness() => new(Document());

    private static FakeSink Simulated(RenderHarness h, int maxFps = 30)
    {
        var sink = new FakeSink(h.ProfileId, new FrameRequest(1, maxFps));
        Assert.True(h.Service.Attach(sink, h.Document, simulated: true));
        return sink;
    }

    private static int Count(ReadOnlySpan<byte> rgba, int x0, int y0, int x1, int y1, Func<byte, byte, byte, bool> match)
    {
        var count = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * Width + x) * 4;
                if (match(rgba[i], rgba[i + 1], rgba[i + 2]))
                    count++;
            }
        return count;
    }

    private static int Red(ReadOnlySpan<byte> rgba) => Count(rgba, 0, 0, Width, 30, (r, g, b) => r > 200 && g < 50 && b < 50);

    private static int Green(ReadOnlySpan<byte> rgba, int x0 = 0, int x1 = 100) => Count(rgba, x0, 40, x1, 90, (r, g, b) => g > 200 && r < 50 && b < 50);

    private static byte[] Thumbnail(RenderHarness h)
    {
        using var bitmap = SKBitmap.Decode(h.Service.RenderOnce(h.Document, Width));
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        return rgba.GetPixelSpan().ToArray();
    }

    [Fact]
    public async Task A_window_style_sink_and_the_thumbnail_stay_live_while_a_simulated_subscription_runs()
    {
        await using var h = Harness();
        var window = h.Attach();
        var simulated = Simulated(h);
        Assert.True(h.Service.IsRendering(h.ProfileId));
        Assert.True(h.Service.IsSimulating(h.ProfileId));

        for (var i = 0; i < 4; i++)
        {
            h.Clock.Advance(1000);
            h.Service.Tick(h.ProfileId);
        }

        // Live, the key reads unavailable: no bar, no graph.
        Assert.All(window.Frames, f => Assert.Equal(0, Red(f.Pixels)));
        Assert.All(window.Frames, f => Assert.Equal(0, Green(f.Pixels)));
        Assert.Equal(0, Red(Thumbnail(h)));
        Assert.Equal(0, Green(Thumbnail(h)));
        using (var latest = h.Service.LatestSnapshot(h.ProfileId))
            Assert.NotNull(latest);

        // Simulated, it moves: a bar 40 to 160 wide to start with, and the graph's frames differ.
        Assert.InRange(Red(simulated.Frames[0].Pixels), 40 * 30, 160 * 30);
        Assert.True(simulated.Frames.Count >= 4);
        Assert.True(simulated.Frames.Select(f => Red(f.Pixels)).Distinct().Count() >= 3, "The bar moves.");
        Assert.All(simulated.Frames, f => Assert.True(Green(f.Pixels) > 0));
    }

    [Fact]
    public async Task A_graphs_history_is_full_from_the_first_simulated_frame()
    {
        await using var h = Harness();
        var simulated = Simulated(h);

        h.Service.Tick(h.ProfileId);

        // The leftmost bar is the 101st newest value.
        Assert.True(Green(simulated.Frames[0].Pixels, 0, 1) > 0, "The graph is full.");
    }

    [Fact]
    public async Task With_only_a_simulated_subscription_the_thumbnail_the_inspection_and_the_latest_snapshot_see_no_loop()
    {
        await using var h = Harness();
        var simulated = Simulated(h);
        h.Service.Tick(h.ProfileId);
        Assert.Single(simulated.Frames);
        Assert.True(Red(simulated.Frames[0].Pixels) > 0);

        Assert.False(h.Service.IsRendering(h.ProfileId));
        Assert.Equal(0, h.Service.LoopCount);
        Assert.Null(h.Service.LatestSnapshot(h.ProfileId));
        Assert.Equal(0, Red(Thumbnail(h)));
        var inspected = h.Service.Inspect(h.Document, (snapshot, _) =>
        {
            using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            snapshot.Draw(canvas, 1);
            return bitmap.GetPixelSpan().ToArray();
        });
        Assert.Equal(0, Red(inspected));
    }

    [Fact]
    public async Task The_simulated_loop_ends_with_its_last_subscription_and_the_live_one_goes_on()
    {
        await using var h = Harness();
        var window = h.Attach();
        var first = Simulated(h);
        var second = Simulated(h);
        h.Service.Tick(h.ProfileId);
        Assert.Equal(first.Frames[0].Frame.Tick, second.Frames[0].Frame.Tick);
        Assert.Equal(first.Frames[0].Pixels, second.Frames[0].Pixels);

        h.Service.Detach(first);
        Assert.True(h.Service.IsSimulating(h.ProfileId));
        h.Service.Detach(second);
        Assert.False(h.Service.IsSimulating(h.ProfileId));
        Assert.True(h.Service.IsRendering(h.ProfileId));

        var frames = window.Frames.Count;
        h.Clock.Advance(1000);
        h.Commit(DocKit.Place("bar", 1, 0));
        h.Service.Tick(h.ProfileId);
        Assert.Equal(frames + 1, window.Frames.Count);

        // A new simulated subscription starts a new loop, from its own first tick.
        var third = Simulated(h);
        Assert.True(h.Service.IsSimulating(h.ProfileId));
        h.Service.Detach(window);
        Assert.False(h.Service.IsRendering(h.ProfileId));
        Assert.True(h.Service.IsSimulating(h.ProfileId));
        h.Service.Tick(h.ProfileId);
        Assert.Single(third.Frames);
        h.Service.Detach(third);
        Assert.False(h.Service.IsSimulating(h.ProfileId));
    }

    [Fact]
    public async Task Deleting_the_profile_closes_its_simulated_subscriptions_too()
    {
        await using var h = Harness();
        var window = h.Attach();
        var simulated = Simulated(h);
        h.Service.Tick(h.ProfileId);

        Assert.True(h.Workspace.DeleteWithItsTag(h.ProfileId));

        Assert.Equal([FrameSinkClosedReason.ProfileDeleted], window.Closed);
        Assert.Equal([FrameSinkClosedReason.ProfileDeleted], simulated.Closed);
        Assert.False(h.Service.IsRendering(h.ProfileId));
        Assert.False(h.Service.IsSimulating(h.ProfileId));
    }

    [Fact]
    public async Task Shutting_down_closes_the_simulated_subscriptions()
    {
        var h = Harness();
        var simulated = Simulated(h);
        h.Service.Tick(h.ProfileId);

        await h.DisposeAsync();

        Assert.Equal([FrameSinkClosedReason.Shutdown], simulated.Closed);
        Assert.False(h.Service.IsSimulating(h.ProfileId), "The simulated loop is stopped.");
    }

    /// <summary>The fixture frame drawn at the centre of a 64 × 48 video at (0, 0).</summary>
    private static int VideoFrame(FakeSink.Received frame)
    {
        var i = (24 * Width + 32) * 4;
        return VideoFixtures.FrameOf(new SKColor(frame.Pixels[i], frame.Pixels[i + 1], frame.Pixels[i + 2]));
    }

    [Fact]
    public async Task A_simulated_loop_plays_videos_on_decode_sessions_of_its_own_released_when_it_ends()
    {
        var clip = VideoFixtures.Read("clip-h264.mp4");
        var hash = InfoPanel.Documents.BlobStore.IdOf(clip);
        var scene = JsonNode.Parse($$$"""
            {"format": "infopanel.scene/1", "requires": ["media.video"],
             "nodes": [{"kind": "media", "playback": "once", "source": {"video": "{{{hash}}}"}}]}
            """)!.AsObject();
        await using var h = new RenderHarness(RenderHarness.Doc(Width, Height, DocKit.Scene("video", 0, 0, scene)));
        var assets = new InfoPanel.Core.Tests.Scenes.MemoryAssets();
        assets.Assets[hash] = clip;
        // As in a render loop: each session decodes on a thread of its own, a few frames ahead of the time asked for.
        using var media = new InfoPanel.Images.MediaSampleCache(assets, new InfoPanel.Images.MediaSampleCacheOptions
        {
            Time = h.Time,
            Video = new InfoPanel.Images.Video.VideoOptions { Decoder = FFmpegForTests.Library },
        });
        var scenes = RenderHarness.Scenes(h.Workspace, h.Sensors, h.History);
        await using var service = new RenderService(h.Workspace, new SceneRuntimeOptions
        {
            Inputs = scenes.Inputs, Media = media, Limits = scenes.Limits, Support = InfoPanel.Spec.EngineSupport.All,
            LocalZone = scenes.LocalZone, Locale = scenes.Locale,
        }, h.Time, new RenderServiceOptions(false));

        var window = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.True(service.Attach(window, h.Document));
        int Tick()
        {
            h.Clock.Advance(34);
            service.Tick(h.ProfileId);
            return VideoFrame(window.Frames[^1]);
        }

        // Played once, the video holds its last frame, 9, from 0.9 s on.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Tick() != 9 && DateTime.UtcNow < deadline)
            Thread.Sleep(2);
        Assert.Equal(9, VideoFrame(window.Frames[^1]));
        Assert.Equal(1, media.VideoSessions);

        // A simulated subscription starts its own clock at 0: the window's video goes on as it was.
        var simulated = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.True(service.Attach(simulated, h.Document, simulated: true));
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(9, Tick());
            Thread.Sleep(2);
        }
        Assert.Equal(2, media.VideoSessions);
        Assert.NotEmpty(simulated.Frames);

        // Its loop ended, its session is closed at once (not when idle), and the window's plays on.
        service.Detach(simulated);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (media.VideoSessions != 1 && DateTime.UtcNow < deadline)
            Thread.Sleep(2);
        Assert.Equal(1, media.VideoSessions);
        Assert.Equal(9, Tick());
    }

    [Fact]
    public void A_simulated_loops_runtime_takes_every_option_of_the_services_but_its_inputs_and_video_sessions()
    {
        // Every option a runtime is made with, so a simulated loop draws as the live one does but for its numbers and
        // the clock its videos play on.
        var properties = typeof(SceneRuntimeOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(
            ["DrawTime", "Inputs", "ItemBudgetMs", "LayoutsPerTick", "Limits", "LocalZone", "Locale", "Logger", "Media", "ProcessBudget", "Providers", "Support", "SuspendFor", "VideoOwner"],
            properties.Select(p => p.Name).Order(StringComparer.Ordinal));

        var live = new SceneRuntimeOptions
        {
            Inputs = NoSceneInputs.Instance,
            Providers = new Dictionary<string, InfoPanel.Images.IMediaProvider>(),
            Limits = InfoPanel.Spec.SpecLimits.Default,
            Support = InfoPanel.Spec.EngineSupport.All,
            LocalZone = TimeZoneInfo.Utc,
            Locale = RegionalFormat.Invariant,
            Logger = Serilog.Core.Logger.None,
            LayoutsPerTick = 17,
            ItemBudgetMs = 23,
            DrawTime = TimeProvider.System,
            SuspendFor = TimeSpan.FromSeconds(29),
            ProcessBudget = new SceneProcessBudget(31),
        };
        var simulated = RenderService.SimulatedOptions(live, TimeProvider.System);

        Assert.IsType<SimulatedSceneInputs>(simulated.Inputs);
        Assert.Null(live.VideoOwner);
        Assert.NotNull(simulated.VideoOwner);
        Assert.NotSame(simulated.VideoOwner, RenderService.SimulatedOptions(live, TimeProvider.System).VideoOwner);
        foreach (var property in properties.Where(p => p.Name is not ("Inputs" or "VideoOwner")))
            Assert.True(Equals(property.GetValue(live), property.GetValue(simulated)), property.Name);
    }
}
