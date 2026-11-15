using System.Diagnostics;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A 600-bar series coloured by a smooth ramp of four stops (every bar its own colour) against the same series with one
/// <c>fill</c>, over a history that is a new list every tick: the tick and the draw of each, timed apart. Prints p50/p95;
/// asserts loose bounds on the medians.
/// Measure with a Release build:
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class SceneSeriesRampPerformanceTests(ITestOutputHelper output)
{
    private static string Series(string mode, bool ramp) =>
        "{'format': 'infopanel.scene/1', 'inputs': {'v': {'type': 'number'}}, 'nodes': [{'kind': 'series', 'input': 'v', 'w': 300, 'h': 80, "
        + "'min': 0, 'max': 100, 'mode': '" + mode + "', 'pitch': 0.5, 'count': 600, "
        + (ramp
            ? "'ramp': [{'at': 0, 'color': '#38bdf8'}, {'at': 0.5, 'color': '#22c55e'}, {'at': 0.8, 'color': '#f59e0b'}, {'at': 1, 'color': '#ef4444'}]"
            : "'fill': '#38bdf8'")
        + "}]}";

    /// <summary>A history that is a new list every tick, as the engine's is after each sample.</summary>
    private sealed class Histories : ISceneInputSource
    {
        public int Tick;

        public SceneReading Read(string key, string inputType) => new() { Status = SceneInputStatus.Live, Now = 50 };

        public IReadOnlyList<double> History(string key) => [.. Enumerable.Range(0, 600).Select(r => (Tick * 7 + r * 13) % 1000 / 10.0)];
    }

    private static (List<double> Tick, List<double> Draw) Run(string mode, bool ramp)
    {
        var histories = new Histories();
        using var runtime = new SceneRuntime(SceneKit.Options(histories));
        runtime.Load(SceneKit.Doc(SceneKit.Json(Series(mode, ramp)), 320, 100, 10, 10));
        using var bitmap = new SKBitmap(new SKImageInfo(320, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var ticks = new List<double>();
        var draws = new List<double>();
        const int warmup = 50, samples = 300;
        for (var i = 0; i < warmup + samples; i++)
        {
            histories.Tick = i;
            var t0 = Stopwatch.GetTimestamp();
            using var tick = runtime.Tick(i / 30.0, SceneKit.Now);
            var t1 = Stopwatch.GetTimestamp();
            canvas.Clear(SKColors.Black);
            tick.Draw(canvas, 1);
            canvas.Flush();
            var t2 = Stopwatch.GetTimestamp();
            Assert.DoesNotContain(tick.Bounds, b => b.Problem is not null);
            if (i < warmup) continue;
            ticks.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            draws.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        }
        return (ticks, draws);
    }

    [Fact]
    public void A_600_bar_series_coloured_by_a_ramp_against_one_fill()
    {
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        var medians = new Dictionary<(string, bool), double>();
        foreach (var (mode, ramp) in new[] { ("bars", false), ("bars", true), ("cells", false), ("cells", true) })
        {
            var (tick, draw) = Run(mode, ramp);
            var (t, d) = (ScenePerformanceTests.Percentiles(tick), ScenePerformanceTests.Percentiles(draw));
            output.WriteLine($"{build}: 600 {mode} {(ramp ? "with a ramp" : "with one fill")}: tick p50 {t.P50:0.000} ms, p95 {t.P95:0.000} ms; draw p50 {d.P50:0.000} ms, p95 {d.P95:0.000} ms");
            medians[(mode, ramp)] = t.P50 + d.P50;
        }

        // A colour a sample costs a little more than one fill (a Release run: about 1.4 times, the tick's colours and
        // their paint's colour changes; the numbers are in the report): medians, against the same run's one fill, so the
        // bound holds in any build and on a shared machine, and a loose ceiling so that both can't regress together.
        foreach (var mode in new[] { "bars", "cells" })
        {
            Assert.InRange(medians[(mode, true)], 0, medians[(mode, false)] * 3 + 0.5);
            Assert.InRange(medians[(mode, true)], 0, 15);
        }
    }
}
