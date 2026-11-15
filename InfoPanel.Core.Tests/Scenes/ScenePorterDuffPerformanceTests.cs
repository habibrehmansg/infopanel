using System.Diagnostics;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Output.Gpu;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The Porter-Duff example (a faded marquee and a tinted picture: two parents drawn as layers, each with an operator's
/// layer inside) against the same scene without its blends (no layer), ticked and drawn on the CPU, and drawn on this
/// computer's GPU where there is one. Prints p50/p95; asserts loose bounds on the CPU medians.
/// Measure with a Release build:
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class ScenePorterDuffPerformanceTests(ITestOutputHelper output)
{
    private const int Width = 220, Height = 70;

    private static string Scene(bool operators)
    {
        var scene = JsonNode.Parse(SceneKit.Example("scene/valid/blend-porter-duff.json").GetRawText())!;
        if (!operators)
            foreach (var top in scene["nodes"]!.AsArray())
                foreach (var child in top!["children"]!.AsArray())
                    child!.AsObject().Remove("blend");
        return scene.ToJsonString();
    }

    private sealed class Title : ISceneInputSource
    {
        public SceneReading Read(string key, string inputType) =>
            new() { Status = SceneInputStatus.Live, Text = "Now playing: a song title that is much too long for its box" };

        public IReadOnlyList<double> History(string key) => [];
    }

    private static (List<double> Tick, List<double> Draw, List<double> Gpu) Run(bool operators, GpuRendering? gpu)
    {
        using var media = SceneKit.SyncMedia();
        using var runtime = new SceneRuntime(SceneKit.Options(new Title(), media));
        runtime.Load(SceneKit.Doc(SceneKit.Json(Scene(operators)), Width, Height, 10, 10));
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var onGpu = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var (ticks, draws, gpus) = (new List<double>(), new List<double>(), new List<double>());
        const int warmup = 50, samples = 300;
        var layers = 0;
        for (var i = 0; i < warmup + samples; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            using var tick = runtime.Tick(i / 30.0, SceneKit.Now);
            var t1 = Stopwatch.GetTimestamp();
            var before = SceneDrawer.LayersMade;
            SceneDrawer.CountLayers = true;
            try
            {
                canvas.Clear(SKColors.Black);
                tick.Draw(canvas, 1);
                canvas.Flush();
            }
            finally
            {
                SceneDrawer.CountLayers = false;
            }
            var t2 = Stopwatch.GetTimestamp();
            layers += SceneDrawer.LayersMade - before;
            Assert.DoesNotContain(tick.Bounds, b => b.Problem is not null);
            var t3 = Stopwatch.GetTimestamp();
            if (gpu is not null)
            {
                Assert.NotEqual(GpuRendering.CpuBackend, gpu.Draw(onGpu, c =>
                {
                    c.Clear(SKColors.Black);
                    tick.Draw(c, 1);
                }));
            }
            var t4 = Stopwatch.GetTimestamp();
            if (i < warmup) continue;
            ticks.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            draws.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            if (gpu is not null) gpus.Add(Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds);
        }
        // Four layers a CPU draw with the operators (each parent's and each operator's), none without.
        Assert.Equal(operators ? 4 : 0, layers / (warmup + samples));
        return (ticks, draws, gpus);
    }

    [Fact]
    public void The_example_with_its_operators_against_the_same_scene_without_them()
    {
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        GpuRendering? gpu = null;
        if (!OperatingSystem.IsLinux())
        {
            gpu = new GpuRendering();
            if (gpu.Apply(true).Fallback is { } why)
            {
                output.WriteLine($"No GPU: {why.Code}");
                gpu.Dispose();
                gpu = null;
            }
        }
        try
        {
            var medians = new Dictionary<bool, double>();
            foreach (var operators in new[] { false, true })
            {
                var (tick, draw, onGpu) = Run(operators, gpu);
                var (t, d) = (ScenePerformanceTests.Percentiles(tick), ScenePerformanceTests.Percentiles(draw));
                var what = operators ? "with its operators (4 layers)" : "without them (no layer)";
                output.WriteLine($"{build}: the example {what}: tick p50 {t.P50:0.000} ms, p95 {t.P95:0.000} ms; CPU draw p50 {d.P50:0.000} ms, p95 {d.P95:0.000} ms");
                if (onGpu.Count > 0)
                {
                    var g = ScenePerformanceTests.Percentiles(onGpu);
                    output.WriteLine($"{build}: the example {what}: GPU draw with read-back p50 {g.P50:0.000} ms, p95 {g.P95:0.000} ms");
                }
                medians[operators] = t.P50 + d.P50;
            }
            // Loose: the layers may cost a few times the plain draw of so small a scene, never a frame's worth.
            Assert.True(medians[true] <= 3 * medians[false] + 0.5, $"with the operators {medians[true]:0.000} ms, without {medians[false]:0.000} ms");
            Assert.True(medians[true] <= 15, $"with the operators {medians[true]:0.000} ms");
        }
        finally
        {
            gpu?.Dispose();
        }
    }
}
