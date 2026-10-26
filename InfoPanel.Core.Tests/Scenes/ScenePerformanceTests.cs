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
}
