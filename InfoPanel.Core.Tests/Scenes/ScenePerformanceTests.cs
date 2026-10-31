using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Tick and draw times for a document like the reference of spec/docs/limits.md (1920 × 480, 30 items: donuts, a graph,
/// bars, histograms, text, a clock, gauges), with every input changing every tick. Prints p50/p95/p99; asserts only
/// loose bounds, so a slow test machine doesn't fail it. Measure with a Release build:
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class ScenePerformanceTests(ITestOutputHelper output)
{
    internal static JsonElement ReferenceDocument()
    {
        var items = new JsonArray();
        var n = 0;
        void Add(string example, double x, double y, double? w = null, double? h = null)
        {
            var scene = JsonNode.Parse(SceneKit.Example("scene/valid/" + example).GetRawText())!;
            var item = new JsonObject
            {
                ["kind"] = "scene",
                ["id"] = $"i{n}",
                ["placement"] = new JsonObject { ["x"] = x, ["y"] = y },
                ["scene"] = scene,
            };
            if (w is not null || h is not null)
            {
                var layout = new JsonObject();
                if (w is { } ww) layout["w"] = ww;
                if (h is { } hh) layout["h"] = hh;
                item["layout"] = layout;
            }
            if (scene["inputs"] is JsonObject inputs)
            {
                var bindings = new JsonObject();
                foreach (var (name, _) in inputs) bindings[name] = new JsonObject { ["key"] = $"perf:{n % 6}:{name}" };
                item["bindings"] = bindings;
            }
            items.Add(item);
            n++;
        }
        for (var i = 0; i < 4; i++) Add("donut.json", 20 + i * 140, 20);
        Add("graph.json", 600, 20);
        Add("clock.json", 880, 20);
        for (var i = 0; i < 6; i++) Add("bar.json", 1120, 20 + i * 30, 240, 20);
        for (var i = 0; i < 2; i++) Add("histogram.json", 1400 + i * 260, 20, 240, 80);
        for (var i = 0; i < 8; i++) Add("sensor-text.json", 20 + i * 180, 300);
        Add("text-layout.json", 1400, 120);
        Add("table.json", 1620, 120);
        for (var i = 0; i < 2; i++) Add("peak-hold.json", 1100 + i * 40, 230);
        for (var i = 0; i < 2; i++) Add("gauge-sequence.json", 600 + i * 150, 120);
        for (var i = 0; i < 2; i++) Add("shapes.json", 900 + i * 220, 250);
        var doc = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = "3dd19c0e-675a-4591-b096-83417853861f",
            ["profile"] = new JsonObject { ["name"] = "Wide bar", ["width"] = 1920, ["height"] = 480, ["background"] = "#0b1220ff" },
            ["items"] = items,
        };
        return JsonSerializer.SerializeToElement(doc);
    }

    internal static void Feed(FakeSceneInputs inputs, int tick)
    {
        for (var k = 0; k < 6; k++)
        {
            var v = 50 + 45 * Math.Sin(tick / 9.0 + k);
            foreach (var name in new[] { "v", "load" }) inputs.Number($"perf:{k}:{name}", Math.Round(v, 2), "%", 0, 100, 50);
            inputs.Text($"perf:{k}:song", "Now playing: a long song title " + (tick / 30));
            inputs.Text($"perf:{k}:art", "art");
            inputs.Histories[$"perf:{k}:v"] = inputs.Histories[$"perf:{k}:load"] =
                Enumerable.Range(tick, 240).Select(i => 50 + 40 * Math.Sin(i / 7.0)).ToArray();
            inputs.Readings[$"perf:{k}:procs"] = new SceneReading
            {
                Status = SceneInputStatus.Live,
                Table = new SceneTable(["Name", "CPU", "Mem"], Enumerable.Range(0, 8).Select(r =>
                    (IReadOnlyList<string>)[$"process {r}", ((tick * 7 + r * 13) % 100 / 10.0).ToString("0.0"), $"{r * 64} MB"]).ToArray()),
            };
        }
    }

    internal static (double P50, double P95, double P99) Percentiles(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return (At(0.50), At(0.95), At(0.99));
    }

    [Fact]
    public void Reference_document_meets_the_budgets()
    {
        var inputs = new FakeSceneInputs();
        using var media = SceneKit.SyncMedia();
        using var runtime = new SceneRuntime(SceneKit.Options(inputs, media));
        runtime.Load(ReferenceDocument());

        using var one = new SKBitmap(new SKImageInfo(1920, 480, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var two = new SKBitmap(new SKImageInfo(3840, 960, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var c1 = new SKCanvas(one);
        using var c2 = new SKCanvas(two);
        List<double> ticks = [], draw1 = [], draw2 = [], bounds = [];
        const int warmup = 30, samples = 300;
        for (var i = 0; i < warmup + samples; i++)
        {
            Feed(inputs, i);
            var t0 = Stopwatch.GetTimestamp();
            using var s = runtime.Tick(i / 30.0, SceneKit.Now.AddSeconds(i / 30.0));
            var t1 = Stopwatch.GetTimestamp();
            s.Draw(c1, 1);
            c1.Flush();
            var t2 = Stopwatch.GetTimestamp();
            s.Draw(c2, 2);
            c2.Flush();
            var t3 = Stopwatch.GetTimestamp();
            _ = s.Bounds;
            var t4 = Stopwatch.GetTimestamp();
            Assert.DoesNotContain(s.Bounds, b => b.Problem is not null);
            if (i < warmup) continue;
            ticks.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            draw1.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            draw2.Add(Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
            bounds.Add(Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds);
        }
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        foreach (var (name, list) in new[] { ("tick", ticks), ("draw scale 1", draw1), ("draw scale 2", draw2), ("bounds", bounds) })
        {
            var (p50, p95, p99) = Percentiles(list);
            output.WriteLine($"{build} {name}: p50 {p50:0.00} ms, p95 {p95:0.00} ms, p99 {p99:0.00} ms");
        }
        // Loose bounds (10× the budgets of spec/docs/limits.md), so the test holds on slow machines and in Debug.
        Assert.True(Percentiles(ticks).P95 < 20, "tick p95");
        Assert.True(Percentiles(draw1).P95 < 40, "draw scale 1 p95");
        Assert.True(Percentiles(draw2).P95 < 80, "draw scale 2 p95");
    }

    [Fact]
    public void A_full_canvas_raster_from_scale_1_to_4()
    {
        // An 800 × 480 picture over the whole canvas, as a background: above scale 1 it is the canvas's pixels enlarged.
        // Logged, not asserted.
        using var photo = new SKBitmap(new SKImageInfo(800, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(photo))
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(800, 480),
                   [new SKColor(10, 20, 60), new SKColor(200, 120, 40), new SKColor(20, 160, 140)], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
            canvas.DrawRect(0, 0, 800, 480, paint);
        var png = GoldenImage.EncodePng(photo);
        var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var assets = FolderAssets.Examples();
        assets.Override = h => h == hash ? png : null;
        using var media = SceneKit.SyncMedia(assets);
        var doc = SceneKit.Doc(SceneKit.Json("{'format': 'infopanel.scene/1', 'nodes': [{'kind': 'media', 'w': 800, 'h': 480, 'source': {'asset': '" + hash + "'}}]}"), 800, 480);
        using var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options(new FakeSceneInputs(), media));
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        foreach (var scale in new[] { 1f, 2f, 2.5f, 3f, 4f })
        {
            using var frame = new SKBitmap(new SKImageInfo((int)(800 * scale), (int)(480 * scale), SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            using var canvas = new SKCanvas(frame);
            List<double> draws = [];
            for (var i = 0; i < 25; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                snapshot.Draw(canvas, scale);
                canvas.Flush();
                if (i >= 5) draws.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            var (p50, p95, _) = Percentiles(draws);
            output.WriteLine($"{build} full-canvas raster, draw scale {scale}: p50 {p50:0.00} ms, p95 {p95:0.00} ms");
        }
    }

    [Fact]
    public void Twenty_rasters_on_a_wide_canvas_at_scale_4()
    {
        // 1920 × 480 with a full-canvas photo and 19 gauges (160 × 160 pictures with transparent corners, every third
        // turned, at sub-pixel places, the last partly off the canvas), drawn at scale 4. The rasters share one picture of
        // the canvas a frame, each clearing and drawing only what it covers, so a frame allocates one canvas of pixels
        // however many rasters it has. Logs time and allocations per frame; asserts the pictures, and loose time and
        // managed bounds.
        static byte[] Png(int w, int h, Action<SKCanvas> draw)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                draw(canvas);
            }
            return GoldenImage.EncodePng(bitmap);
        }
        var photo = Png(1920, 480, canvas =>
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1920, 480),
                [new SKColor(10, 20, 60), new SKColor(200, 120, 40), new SKColor(20, 160, 140)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 1920, 480, paint);
        });
        var gauge = Png(160, 160, canvas =>
        {
            using var shader = SKShader.CreateSweepGradient(new SKPoint(80, 80), [SKColors.Lime, SKColors.Yellow, SKColors.Red, SKColors.Lime]);
            using var ring = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 18 };
            canvas.DrawCircle(80, 80, 66, ring);
            using var face = new SKPaint { Color = new SKColor(15, 23, 42, 220), IsAntialias = true };
            canvas.DrawCircle(80, 80, 56, face);
        });
        static string Hash(byte[] png) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var pngs = new Dictionary<string, byte[]> { [Hash(photo)] = photo, [Hash(gauge)] = gauge };
        var assets = FolderAssets.Examples();
        assets.Override = h => pngs.GetValueOrDefault(h);
        using var media = SceneKit.SyncMedia(assets);
        List<string> nodes = [$"{{'kind': 'media', 'w': 1920, 'h': 480, 'source': {{'asset': '{Hash(photo)}'}}}}"];
        for (var k = 0; k < 19; k++)
        {
            var (x, y) = (15.3 + k * 100.7, 40.6 + k % 3 * 110.2);
            var node = FormattableString.Invariant($"{{'kind': 'media', 'x': {x:0.0}, 'y': {y:0.0}, 'source': {{'asset': '{Hash(gauge)}'}}}}");
            nodes.Add(k % 3 == 0
                ? FormattableString.Invariant($"{{'kind': 'group', 'transform': [{{'type': 'rotate', 'angle': {15 + k}, 'cx': {x + 80:0.0}, 'cy': {y + 80:0.0}}}], 'children': [{node}]}}")
                : node);
        }
        var doc = SceneKit.Doc(SceneKit.Json("{'format': 'infopanel.scene/1', 'nodes': [" + string.Join(",", nodes) + "]}"), 1920, 480);
        using var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options(new FakeSceneInputs(), media));
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        const float scale = 4;
        using var frame = new SKBitmap(new SKImageInfo(1920 * 4, 480 * 4, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(frame);
        List<double> draws = [];
        long pictures = 0, pictureBytes = 0, largestFrameBytes = 0, managed = 0;
        SceneDrawer.PictureProbe = info =>
        {
            pictures++;
            pictureBytes += info.BytesSize64;
        };
        try
        {
            for (var i = 0; i < 25; i++)
            {
                pictures = pictureBytes = 0;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                snapshot.Draw(canvas, scale);
                canvas.Flush();
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (i < 5) continue;
                draws.Add(ms);
                managed = Math.Max(managed, GC.GetAllocatedBytesForCurrentThread() - allocated);
                largestFrameBytes = Math.Max(largestFrameBytes, pictureBytes);
                Assert.Equal(1, pictures);
            }
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
        }
        var (p50, p95, _) = Percentiles(draws);
        output.WriteLine($"{build} 20 rasters on 1920 × 480, draw scale {scale}: p50 {p50:0.00} ms, p95 {p95:0.00} ms; "
            + $"pictures {largestFrameBytes:N0} bytes a frame, managed {managed:N0} bytes a frame");
        Assert.InRange(largestFrameBytes, 1, 1920L * 480 * 4);
        Assert.True(managed < 1 << 20, $"{managed:N0} managed bytes a frame");
        // Loose: the watchdog counts a scale-4 draw as a sixteenth, so 50 ms a canvas pixel is 800 ms.
        Assert.True(p95 < 400, "draw scale 4 p95");
    }

    [Fact]
    public void Twenty_rasters_on_a_wide_canvas_between_quarter_steps()
    {
        // 1920 × 480 with a full-canvas photo and 19 gauges (160 × 160 pictures with transparent corners, every third
        // turned, at sub-pixel places, the last partly off the canvas), drawn at 3.3, which isn't a multiple of 0.25:
        // each raster's canvas pixels are enlarged in memory by the rule. The rasters share one picture of the canvas and
        // one frame-size enlargement a frame. Logs time and allocations per frame; asserts the pictures and enlargements,
        // and loose time and managed bounds.
        static string Png(int w, int h, Action<SKCanvas> draw, Dictionary<string, byte[]> pngs)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                draw(canvas);
            }
            var png = GoldenImage.EncodePng(bitmap);
            var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
            pngs[hash] = png;
            return hash;
        }
        var pngs = new Dictionary<string, byte[]>();
        var photo = Png(1920, 480, canvas =>
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1920, 480),
                [new SKColor(10, 20, 60), new SKColor(200, 120, 40), new SKColor(20, 160, 140)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 1920, 480, paint);
        }, pngs);
        var gauge = Png(160, 160, canvas =>
        {
            using var shader = SKShader.CreateSweepGradient(new SKPoint(80, 80), [SKColors.Lime, SKColors.Yellow, SKColors.Red, SKColors.Lime]);
            using var ring = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 18 };
            canvas.DrawCircle(80, 80, 66, ring);
            using var face = new SKPaint { Color = new SKColor(15, 23, 42, 220), IsAntialias = true };
            canvas.DrawCircle(80, 80, 56, face);
        }, pngs);
        var assets = FolderAssets.Examples();
        assets.Override = h => pngs.GetValueOrDefault(h);
        using var media = SceneKit.SyncMedia(assets);
        List<string> nodes = [$"{{'kind': 'media', 'w': 1920, 'h': 480, 'source': {{'asset': '{photo}'}}}}"];
        for (var k = 0; k < 19; k++)
        {
            var (x, y) = (15.3 + k * 100.7, 40.6 + k % 3 * 110.2);
            var node = FormattableString.Invariant($"{{'kind': 'media', 'x': {x:0.0}, 'y': {y:0.0}, 'source': {{'asset': '{gauge}'}}}}");
            nodes.Add(k % 3 == 0
                ? FormattableString.Invariant($"{{'kind': 'group', 'transform': [{{'type': 'rotate', 'angle': {15 + k}, 'cx': {x + 80:0.0}, 'cy': {y + 80:0.0}}}], 'children': [{node}]}}")
                : node);
        }
        var doc = SceneKit.Doc(SceneKit.Json("{'format': 'infopanel.scene/1', 'nodes': [" + string.Join(",", nodes) + "]}"), 1920, 480);
        using var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options(new FakeSceneInputs(), media));
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        const float scale = 3.3f;
        var (fw, fh) = ((int)Math.Round(1920 * scale), (int)Math.Round(480 * scale));
        using var frame = new SKBitmap(new SKImageInfo(fw, fh, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(frame);
        List<double> draws = [];
        long pictures = 0, enlargements = 0, enlargedBytes = 0, largestEnlargedBytes = 0, managed = 0;
        SceneDrawer.PictureProbe = _ => pictures++;
        SceneDrawer.EnlargementProbe = info =>
        {
            enlargements++;
            enlargedBytes += info.BytesSize64;
        };
        try
        {
            for (var i = 0; i < 25; i++)
            {
                pictures = enlargements = enlargedBytes = 0;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                snapshot.Draw(canvas, scale);
                canvas.Flush();
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (i < 5) continue;
                draws.Add(ms);
                managed = Math.Max(managed, GC.GetAllocatedBytesForCurrentThread() - allocated);
                largestEnlargedBytes = Math.Max(largestEnlargedBytes, enlargedBytes);
                Assert.Equal(1, pictures);
                Assert.Equal(1, enlargements);
            }
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
            SceneDrawer.EnlargementProbe = null;
        }
        var (p50, p95, _) = Percentiles(draws);
        output.WriteLine($"{build} 20 rasters on 1920 × 480, draw scale {scale}: p50 {p50:0.00} ms, p95 {p95:0.00} ms; "
            + $"enlargements {largestEnlargedBytes:N0} bytes a frame, managed {managed:N0} bytes a frame");
        Assert.InRange(largestEnlargedBytes, 1, (long)fw * fh * 4);
        Assert.True(managed < 1 << 20, $"{managed:N0} managed bytes a frame");
        // Loose: the watchdog counts a draw at 3.3 as a 10.9th, so 50 ms a canvas pixel is 544 ms.
        Assert.True(p95 < 400, "draw scale 3.3 p95");
    }
}
