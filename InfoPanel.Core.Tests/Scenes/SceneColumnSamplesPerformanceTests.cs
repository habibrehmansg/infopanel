using System.Data;
using System.Diagnostics;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A tick of the frame-time trace (frame-times.json: 600 bars and their 99th percentile) over a table's column whose
/// source hands over a new table every tick (the worst case: every row made afresh), against the same trace over a
/// number input's 600-sample history (what a graph costs), and the limit's case, 4,096 rows of four columns, drawn and
/// sorted whole. Prints p50/p95 and the bytes a tick allocates; asserts loose bounds. Measure with a Release build:
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class SceneColumnSamplesPerformanceTests(ITestOutputHelper output)
{
    private static readonly SensorKey Key = SensorKey.Plugin("/perf/frames");

    /// <summary>Frame times as a plugin hands them over: <paramref name="rows"/> rows of a frame label, ms, fps and a flag.</summary>
    private static DataTable Frames(int rows, int tick)
    {
        var table = new DataTable();
        table.Columns.Add("frame", typeof(object));
        table.Columns.Add("ms", typeof(object));
        table.Columns.Add("fps", typeof(object));
        table.Columns.Add("dropped", typeof(object));
        for (var r = 0; r < rows; r++)
        {
            var ms = 6 + (tick * 7 + r * 13) % 100 / 10f;
            table.Rows.Add(new PluginText("frame", "#" + (tick + r)), new PluginSensor("ms", ms, "ms"), new PluginSensor("fps", 1000 / ms, ""), new PluginText("dropped", ms > 14 ? "yes" : "no"));
        }
        return table;
    }

    private static string Trace(int count, bool column) =>
        "{'format': 'infopanel.scene/1', 'inputs': {'t': {'type': '" + (column ? "table" : "number") + "'}}, "
        + "'values': {'p99': {'op': 'history', 'input': 't', " + (column ? "'column': 'ms', " : "") + "'fn': 'percentile', 'count': " + count + ", 'p': 99}}, "
        + "'nodes': [{'kind': 'series', 'input': 't', " + (column ? "'column': 'ms', " : "") + "'w': 300, 'h': 80, 'min': 0, 'max': 33.4, 'mode': 'bars', 'pitch': " + (300.0 / count).ToString(System.Globalization.CultureInfo.InvariantCulture) + ", 'count': " + count + ", 'fill': '#38bdf8'},"
        + " {'kind': 'line', 'x1': 0, 'x2': 300, 'y1': {'op': 'value', 'name': 'p99'}, 'y2': {'op': 'value', 'name': 'p99'}, 'stroke': {'paint': '#ef4444', 'width': 1}}]}";

    /// <summary>A history that is a new list every tick, as the engine's is after each sample.</summary>
    private sealed class Histories(int count) : ISceneInputSource
    {
        public int Tick;

        public SceneReading Read(string key, string inputType) => new() { Status = SceneInputStatus.Live, Now = 7 };

        public IReadOnlyList<double> History(string key) => [.. Enumerable.Range(0, count).Select(r => 6 + (Tick * 7 + r * 13) % 100 / 10.0)];
    }

    private (List<double> Ms, List<double> Bytes) Ticks(int rows, int count, bool column)
    {
        var sensors = new FakeSensorReader();
        var histories = new Histories(count);
        ISceneInputSource inputs = column ? new SensorSceneInputs(sensors, new FakeSensorHistory()) : histories;
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(SceneKit.Json(Trace(count, column)), 320, 100, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));
        var times = new List<double>();
        var bytes = new List<double>();
        const int warmup = 50, samples = 300;
        for (var i = 0; i < warmup + samples; i++)
        {
            sensors.Readings[Key] = new SensorReading(Frames(rows, i), "", "");
            histories.Tick = i;
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            using var tick = runtime.Tick(i / 30.0, SceneKit.Now);
            var t1 = Stopwatch.GetTimestamp();
            var b1 = GC.GetAllocatedBytesForCurrentThread();
            Assert.DoesNotContain(tick.Bounds, b => b.Problem is not null);
            if (i < warmup) continue;
            times.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            bytes.Add(b1 - b0);
        }
        return (times, bytes);
    }

    [Fact]
    public void A_trace_over_a_new_600_row_table_each_tick_stays_near_a_graphs_tick_and_4096_rows_within_bounds()
    {
        var history = Ticks(0, 600, column: false);
        var column = Ticks(600, 600, column: true);
        var limit = Ticks(4096, 4096, column: true);
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        var results = new[] { ("a 600-sample history", history), ("a 600-row table's column", column), ("a 4,096-row table's column", limit) }
            .Select(r => (r.Item1, Ms: ScenePerformanceTests.Percentiles(r.Item2.Ms), Bytes: ScenePerformanceTests.Percentiles(r.Item2.Bytes))).ToArray();
        foreach (var (name, ms, bytes) in results)
            output.WriteLine($"{build}: the trace over {name}, new each tick: p50 {ms.P50:0.000} ms, p95 {ms.P95:0.000} ms; allocated p50 {bytes.P50 / 1024:0} KiB");

        // A column read makes the rows it reads (their texts and numbers) once per table: a trace over a table that is
        // new every tick costs a few times a graph's tick, and the limit's 4,096 rows of four columns, read and sorted
        // whole, what those rows cost. Medians, against the same run's graph, so the bounds hold in any build and on a
        // shared machine (a Release run's numbers are in the report).
        var (h, c, l) = (results[0].Ms, results[1].Ms, results[2].Ms);
        Assert.InRange(c.P50, 0, h.P50 * 10 + 0.5);
        Assert.InRange(l.P50, 0, c.P50 * 10 + 2);
        // And a loose ceiling on each median, so that the three can't regress together: over ten times a Debug run's
        // median on a loaded machine (a median of 300 ticks, which a few slow ones don't move).
        Assert.InRange(h.P50, 0, 10);
        Assert.InRange(c.P50, 0, 25);
        Assert.InRange(l.P50, 0, 60);
    }
}
