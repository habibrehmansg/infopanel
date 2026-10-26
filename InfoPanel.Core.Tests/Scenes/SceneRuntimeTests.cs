using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Images;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The tick model (spec/docs/identity.md): state cells in elapsed time at any tick rate, state keyed by identity, one
/// snapshot shared by every scale, thumbnail and sink, inputs read once per tick, asynchronous media arriving mid-tick.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneRuntimeTests
{
    private static JsonElement BarDoc(double x = 10, string key = "test:v", string? scene = null, string id = "item")
    {
        var doc = SceneKit.Doc(scene is null ? SceneKit.Example("scene/valid/bar.json") : SceneKit.Json(scene), 260, 40, x, 10, 240, 20,
            bindings: new Dictionary<string, string> { ["v"] = key });
        if (id == "item") return doc;
        var node = JsonNode.Parse(doc.GetRawText())!;
        node["items"]![0]!["id"] = id;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Ticks <paramref name="runtime"/> from 0 to <paramref name="seconds"/> at <paramref name="fps"/>, with <paramref name="before"/> run before each tick.</summary>
    private static void Run(SceneRuntime runtime, double fps, double seconds, Action<double>? before = null)
    {
        var n = (int)Math.Round(seconds * fps);
        for (var k = 0; k <= n; k++)
        {
            var t = k / fps;
            before?.Invoke(t);
            runtime.Tick(t, SceneKit.Now).Dispose();
        }
    }

    private static double CellAfter(JsonElement doc, string cell, double fps, double seconds, Action<FakeSceneInputs, double> inputs)
    {
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(doc);
        Run(runtime, fps, seconds, t => inputs(fake, t));
        return runtime.CellValue("item", cell)!.Value;
    }

    [Fact]
    public void Smoothing_follows_elapsed_time_not_ticks()
    {
        // v steps from 0 to 100 after the first tick; shown = 1 − exp(−t / 0.25) at any frame rate.
        static void Step(FakeSceneInputs f, double t) => f.Number("test:v", t == 0 ? 0 : 100);
        var at30 = CellAfter(BarDoc(), "shown", 30, 1, Step);
        var at60 = CellAfter(BarDoc(), "shown", 60, 1, Step);
        var at7 = CellAfter(BarDoc(), "shown", 7, 1, Step);
        var exact = 1 - Math.Exp(-1 / 0.25);
        Assert.Equal(exact, at30, 9);
        Assert.Equal(exact, at60, 9);
        Assert.Equal(exact, at7, 9);
    }

    [Fact]
    public void Rate_limits_and_peak_holds_follow_elapsed_time()
    {
        var gauge = SceneKit.Doc(SceneKit.Example("scene/valid/gauge-sequence.json"), 148, 148);
        static void Full(FakeSceneInputs f, double t) => f.Number("test:v", t == 0 ? 0 : 100);
        Assert.Equal(1.6, CellAfter(gauge, "frame", 30, 0.2, Full), 9);
        Assert.Equal(1.6, CellAfter(gauge, "frame", 60, 0.2, Full), 9);
        Assert.Equal(1.6, CellAfter(gauge, "frame", 5, 0.2, Full), 9);

        // Peak at 100 % until 0.5 s, then the level drops to 0: the peak holds 1.5 s, then falls 0.4 a second.
        var peak = SceneKit.Doc(SceneKit.Example("scene/valid/peak-hold.json"), 44, 180);
        static void Drop(FakeSceneInputs f, double t) => f.Number("test:v", t <= 0.5 ? 100 : 0);
        var p30 = CellAfter(peak, "peak", 30, 3, Drop);
        var p60 = CellAfter(peak, "peak", 60, 3, Drop);
        var expected = 1 - 0.4 * (3 - 0.5 - 1.5);
        Assert.Equal(expected, p30, 1);
        Assert.Equal(expected, p60, 1);
        Assert.True(Math.Abs(p30 - p60) <= 0.4 / 30 + 1e-9, $"{p30} vs {p60}");
    }

    [Fact]
    public void Animation_is_a_function_of_elapsed_time()
    {
        SKBitmap At(double fps)
        {
            var fake = new FakeSceneInputs();
            fake.Text("test:song", "A song title that is far too long for the marquee box");
            using var runtime = new SceneRuntime(SceneKit.Options(fake));
            var text = SceneKit.Doc(SceneKit.Example("scene/valid/text-layout.json"), 220, 180, 10, 10);
            runtime.Load(text);
            Run(runtime, fps, 1.5);
            using var s = runtime.Current!;
            return s.Render();
        }
        using var a = At(30);
        using var b = At(60);
        using var c = At(4);
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, 0));
        Assert.Equal(0, SceneKit.DifferingPixels(a, c, 0));
    }

    [Fact]
    public void One_snapshot_draws_the_same_for_every_sink_scale_and_thumbnail()
    {
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 64, "%");
        using var media = SceneKit.SyncMedia();
        using var runtime = new SceneRuntime(SceneKit.Options(fake, media));
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/shapes.json"), 220, 180, 5, 5));
        runtime.Tick(0, SceneKit.Now).Dispose();
        using var snapshot = runtime.Tick(1.25, SceneKit.Now);
        var generation = snapshot.Generation;
        var tick = snapshot.Tick;

        using var sinkA = snapshot.Render(1);
        using var sinkB = snapshot.Render(1);
        using var scale2 = snapshot.Render(2);
        using var thumbnail = runtime.Current!.Render(1);
        var bounds1 = snapshot.Bounds;
        Assert.Equal(0, SceneKit.DifferingPixels(sinkA, sinkB, 0));
        Assert.Equal(0, SceneKit.DifferingPixels(sinkA, thumbnail, 0));
        Assert.Same(bounds1, snapshot.Bounds);

        // Scale 2 is the same evaluation at twice the resolution: downsampled, it matches scale 1 closely.
        using var down = scale2.Resize(new SKImageInfo(220, 180, scale2.ColorType, scale2.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var differing = SceneKit.DifferingPixels(sinkA, down, 24);
        Assert.True(differing < 220 * 180 / 50, $"{differing} pixels differ between scale 1 and scale 2 downsampled");

        // Drawing never advances anything.
        using var again = runtime.Current!;
        Assert.Equal(tick, again.Tick);
        Assert.Equal(generation, again.Generation);
    }

    [Fact]
    public void A_snapshot_draws_the_same_from_several_threads_at_once()
    {
        var fake = new FakeSceneInputs();
        using var media = SceneKit.SyncMedia();
        using var runtime = new SceneRuntime(SceneKit.Options(fake, media));
        runtime.Load(ScenePerformanceTests.ReferenceDocument());
        ScenePerformanceTests.Feed(fake, 3);
        using var s = runtime.Tick(0, SceneKit.Now);
        using var serial = s.Render(1);
        var parallel = new SKBitmap[8];
        Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => parallel[i] = s.Render(i % 2 == 0 ? 1 : 0.5f));
        foreach (var (bmp, i) in parallel.Select((b, i) => (b, i)))
        {
            if (i % 2 == 0) Assert.Equal(0, SceneKit.DifferingPixels(serial, bmp, 0));
            bmp.Dispose();
        }
    }

    [Fact]
    public void Scale_two_text_layout_and_bounds_are_the_scale_one_layout()
    {
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 71.25, "°C");
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/sensor-text.json"), 200, 60, 10, 6));
        using var s = runtime.Tick(0, SceneKit.Now);
        var b = s.Bounds.Single();
        using var one = s.Render(1);
        using var two = s.Render(2);
        // The text's ink sits at the same place at both scales: compare the first lit column.
        static int FirstLit(SKBitmap bmp, int factor)
        {
            for (var x = 0; x < bmp.Width; x++)
                for (var y = 0; y < bmp.Height; y++)
                    if (bmp.GetPixel(x, y).Red > 200) return x / factor;
            return -1;
        }
        Assert.InRange(Math.Abs(FirstLit(one, 1) - FirstLit(two, 2)), 0, 1);
        Assert.Equal(10, b.Frame.X, 0);
    }

    [Fact]
    public void Inputs_are_read_once_per_key_per_tick()
    {
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 10);
        var one = JsonNode.Parse(BarDoc().GetRawText())!;
        var item = one["items"]![0]!.DeepClone();
        item["id"] = "second";
        one["items"]!.AsArray().Add(item);
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(JsonSerializer.SerializeToElement(one));
        runtime.Tick(0, SceneKit.Now).Dispose();
        Assert.Equal(1, fake.Reads);
        Assert.Equal(1, fake.HistoryReads);
        using (var s = runtime.Current!)
        {
            s.Render(1).Dispose();
            s.Render(2).Dispose();
            _ = s.Bounds;
        }
        Assert.Equal(1, fake.Reads);
        runtime.Tick(0.1, SceneKit.Now).Dispose();
        Assert.Equal(2, fake.Reads);
    }

    [Fact]
    public void State_is_kept_by_identity_and_starts_again_when_its_definition_changes()
    {
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(BarDoc());
        fake.Number("test:v", 0);
        runtime.Tick(0, SceneKit.Now).Dispose();
        fake.Number("test:v", 100);
        runtime.Tick(0.25, SceneKit.Now).Dispose();
        var smoothed = runtime.CellValue("item", "shown")!.Value;
        Assert.InRange(smoothed, 0.6, 0.65);

        // Moved and rebound (to a key with the same reading): kept, and it keeps smoothing from where it was.
        fake.Number("test:other", 100);
        runtime.Load(BarDoc(x: 50, key: "test:other"));
        runtime.Tick(0.5, SceneKit.Now).Dispose();
        Assert.Equal(1 - Math.Exp(-2), runtime.CellValue("item", "shown")!.Value, 9);

        // A new scene with the same cell (same name and definition) but other nodes: kept.
        var bar = SceneKit.Example("scene/valid/bar.json").GetRawText();
        var restyled = bar.Replace("#1f2937", "#000000");
        runtime.Load(BarDoc(key: "test:other", scene: restyled));
        runtime.Tick(0.75, SceneKit.Now).Dispose();
        Assert.Equal(1 - Math.Exp(-3), runtime.CellValue("item", "shown")!.Value, 9);

        // The cell's definition changes (another time constant): it starts again at init (the target, 1).
        var changed = bar.Replace("0.25", "0.5");
        fake.Number("test:other", 30);
        runtime.Load(BarDoc(key: "test:other", scene: changed));
        runtime.Tick(1.0, SceneKit.Now).Dispose();
        Assert.Equal(0.3, runtime.CellValue("item", "shown")!.Value, 9);
        Assert.Equal(1, runtime.CellCount("item"));

        // Hidden: not evaluated, state kept; shown again, it continues with dt over the hidden time.
        var hidden = JsonNode.Parse(BarDoc(key: "test:other", scene: changed).GetRawText())!;
        hidden["items"]![0]!["visible"] = false;
        runtime.Load(JsonSerializer.SerializeToElement(hidden));
        runtime.Tick(1.5, SceneKit.Now).Dispose();
        Assert.Equal(0.3, runtime.CellValue("item", "shown")!.Value, 9);

        // Removed: dropped.
        runtime.Load(BarDoc(key: "test:other", scene: changed, id: "another"));
        runtime.Tick(2.0, SceneKit.Now).Dispose();
        Assert.Null(runtime.CellValue("item", "shown"));
    }

    [Fact]
    public void Removing_an_item_drops_its_state_even_when_it_comes_back_before_a_tick()
    {
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(BarDoc());
        fake.Number("test:v", 0);
        runtime.Tick(0, SceneKit.Now).Dispose();
        fake.Number("test:v", 100);
        runtime.Tick(0.25, SceneKit.Now).Dispose();
        Assert.InRange(runtime.CellValue("item", "shown")!.Value, 0.6, 0.65);

        // Removed, then the removal undone, with no tick between: the cell starts again at init (the target, 1).
        runtime.Load(BarDoc(id: "another"));
        Assert.Null(runtime.CellValue("item", "shown"));
        runtime.Load(BarDoc());
        runtime.Tick(0.5, SceneKit.Now).Dispose();
        Assert.Equal(1, runtime.CellValue("item", "shown")!.Value, 9);
    }

    [Fact]
    public void Definition_changes_drop_old_cells_even_when_the_item_never_evaluates()
    {
        // The dash is over budget, so every tick fails after the cells are made.
        var points = string.Join(",", Enumerable.Range(0, 2000).Select(i => $"[{i % 2 * 1000},{i}]"));
        string Scene(int init) => "{\"format\":\"infopanel.scene/1\",\"state\":{\"c\":{\"init\":" + init
            + ",\"next\":{\"op\":\"add\",\"args\":[{\"op\":\"state\",\"name\":\"c\"},1]}}},\"nodes\":[{\"kind\":\"rect\",\"w\":{\"op\":\"state\",\"name\":\"c\"},\"h\":1},"
            + "{\"kind\":\"polyline\",\"points\":[" + points + "],\"stroke\":{\"paint\":\"#ffffff\",\"width\":1,\"dash\":[0.001,0.001]}}]}";
        using var runtime = new SceneRuntime(SceneKit.Options());
        for (var generation = 0; generation < 20; generation++)
        {
            runtime.Load(SceneKit.Doc(SceneKit.Json(Scene(generation)), 100, 100));
            using var s = runtime.Tick(generation * 0.1, SceneKit.Now);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
            Assert.Equal(1, runtime.CellCount("item"));
        }
    }

    [Fact]
    public void A_one_off_snapshot_shows_cells_at_init_and_advances_nothing()
    {
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 0);
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(BarDoc());
        runtime.Tick(0, SceneKit.Now).Dispose();
        fake.Number("test:v", 100);
        using (var once = runtime.Snapshot(SceneKit.Now))
        {
            Assert.Equal(0, once.Clock.Elapsed);
            using var bmp = once.Render();
            // init is the target: the bar is full (its right end is lit with the gradient's red end).
            Assert.True(bmp.GetPixel(245, 20).Red > 150);
        }
        Assert.Equal(0, runtime.CellValue("item", "shown"));
    }

    [Fact]
    public void Media_arriving_mid_tick_changes_nothing_until_the_next_tick()
    {
        var assets = new GatedAssets(FolderAssets.Examples());
        using var media = new MediaSampleCache(assets, new MediaSampleCacheOptions { Workers = 1 });
        var decoded = new ManualResetEventSlim();
        media.FrameDecoded += _ => decoded.Set();
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','nodes':[{'kind':'media','w':96,'h':64,'source':{'asset':'sha256:2e6bf049cc4791922aabe0704b3d6e978102348ec52e63bc29a7574281450be1'}}]}");
        using var runtime = new SceneRuntime(SceneKit.Options(media: media));
        runtime.Load(SceneKit.Doc(scene, 100, 70));

        var first = runtime.Tick(0, SceneKit.Now);
        Assert.Contains(first.Diagnostics, d => d.Code == "media.loading");
        using var before = first.Render();
        assets.Gate.Set();
        Assert.True(decoded.Wait(TimeSpan.FromSeconds(10)), "the asset was not decoded");
        using var after = first.Render();
        Assert.Equal(0, SceneKit.DifferingPixels(before, after, 0));
        Assert.Equal(0, first.SampleCount);

        var second = runtime.Tick(0.1, SceneKit.Now);
        using var next = second.Render();
        Assert.True(SceneKit.DifferingPixels(before, next, 0) > 1000);
        Assert.Equal(1, second.SampleCount);
        first.Dispose();

        // The snapshot owns its frame: it still draws after the cache has shut down, until it is released.
        media.Dispose();
        using (var late = second.Render())
            Assert.Equal(0, SceneKit.DifferingPixels(next, late, 0));
        second.Dispose();
        Assert.Equal(0, media.Bytes);
    }

    [Fact]
    public void Animated_media_plays_by_elapsed_time()
    {
        using var media = SceneKit.SyncMedia();
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','nodes':[{'kind':'media','source':{'asset':'sha256:e796ee98bcccd3a77b80d6c394435d1c460c3684c96c542b073150f0ba6eb354'}}]}");
        using var runtime = new SceneRuntime(SceneKit.Options(media: media));
        runtime.Load(SceneKit.Doc(scene, 48, 32));
        var frames = new List<SKBitmap>();
        var times = Enumerable.Range(0, 60).Select(i => i * 0.1).ToArray();
        foreach (var t in times)
            using (var s = runtime.Tick(t, SceneKit.Now)) frames.Add(s.Render());
        // The animation changes over time and repeats (loop): some later frame differs from the first, and the
        // frame at a time is the same however the ticks got there.
        Assert.Contains(frames.Skip(1), f => SceneKit.DifferingPixels(frames[0], f, 0) > 0);
        using var again = new SceneRuntime(SceneKit.Options(media: media));
        again.Load(SceneKit.Doc(scene, 48, 32));
        using (var s = again.Tick(times[37], SceneKit.Now))
        using (var direct = s.Render())
            Assert.Equal(0, SceneKit.DifferingPixels(frames[37], direct, 0));
        foreach (var f in frames) f.Dispose();
    }

    [Fact]
    public void The_process_budget_admits_documents_while_they_fit()
    {
        var budget = new SceneProcessBudget(4);
        var options = new SceneRuntimeOptions { ProcessBudget = budget, LocalZone = TimeZoneInfo.Utc };
        using var a = new SceneRuntime(options);
        using var b = new SceneRuntime(options);
        a.Load(BarDoc());
        b.Load(BarDoc());
        using (var s = a.Tick(0, SceneKit.Now)) Assert.Null(s.Bounds.Single().Problem);
        using (var s = b.Tick(0, SceneKit.Now))
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.process");
        }
        a.Dispose();
        using (var s = b.Tick(0.1, SceneKit.Now)) Assert.Null(s.Bounds.Single().Problem);
        Assert.Equal(3, budget.Used);
    }

    private sealed class GatedAssets(IMediaAssetStore inner) : IMediaAssetStore
    {
        public ManualResetEventSlim Gate { get; } = new();

        public byte[]? Read(string hash)
        {
            Gate.Wait(TimeSpan.FromSeconds(10));
            return inner.Read(hash);
        }
    }
}
