using System.Data;
using System.Diagnostics;
using System.Globalization;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A tick that reads a table input whose source hands over a new table every tick (the worst case: a source changes it at
/// most once a round), through a table node of 10 rows and a few cells. Today's reading of a 100-row table (every cell's
/// text of the first 100 rows made when the table is first read) against the reading of tables as data: each row made
/// when a tick first reads it, at 100 and at 4,096 rows. Prints p50/p95; asserts loose bounds. Measure with a Release build:
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class SceneTableReadingPerformanceTests(ITestOutputHelper output)
{
    private static readonly SensorKey Key = SensorKey.Plugin("/perf/processes");

    private static DataTable Processes(int rows, int tick)
    {
        var table = new DataTable();
        table.Columns.Add("Process", typeof(object));
        table.Columns.Add("CPU", typeof(object));
        table.Columns.Add("Memory", typeof(object));
        table.Columns.Add("Threads", typeof(object));
        for (var r = 0; r < rows; r++)
            table.Rows.Add(new PluginText("name", $"process {r}"), new PluginSensor("cpu", (tick * 7 + r * 13) % 1000 / 10f, "%"),
                new PluginSensor("memory", r * 64.5f, "MB"), new PluginSensor("threads", r % 40, ""));
        return table;
    }

    /// <summary>How the engine read a table until tables were read as data: its first 100 rows' text, all made at once.</summary>
    private sealed class EagerTables(ISensorReader sensors) : ISceneInputSource
    {
        public SceneReading Read(string key, string inputType)
        {
            sensors.TryRead(Key, out var reading);
            var table = reading.ValueTable!;
            var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
            var rows = new List<IReadOnlyList<string>>(Math.Min(table.Rows.Count, 100));
            foreach (DataRow row in table.Rows)
            {
                if (rows.Count == 100) break;
                rows.Add(row.ItemArray.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "").ToArray());
            }
            return new SceneReading { Status = SceneInputStatus.Live, Table = new SceneTable(columns, rows) };
        }

        public IReadOnlyList<double> History(string key) => [];
    }

    private const string TableNode = "{'kind': 'table', 'input': 't', 'y': 40, 'rows': 10, 'rowHeight': 14,"
        + " 'columns': [{'column': 0, 'w': 90}, {'column': 'CPU', 'w': 50, 'align': 'end'}], 'font': {'family': 'Inter', 'size': 11}, 'fill': '#e5e7eb'}";

    private const string CellsNode = "{'kind': 'text', 'font': {'family': 'Inter', 'size': 11}, 'fill': '#e5e7eb', 'text': {'op': 'concat', 'args': ["
        + "{'op': 'format', 'value': {'op': 'input', 'name': 't', 'field': 'rows'}}, ' ',"
        + "{'op': 'cell', 'input': 't', 'row': 0, 'column': 'Process'}, ' ',"
        + "{'op': 'cell', 'input': 't', 'row': 50, 'column': 'CPU'}, ' ',"
        + "{'op': 'format', 'value': {'op': 'cell', 'input': 't', 'row': {'op': 'sub', 'args': [{'op': 'input', 'name': 't', 'field': 'rows'}, 1]}, 'column': 'Memory', 'field': 'number'}, 'decimals': 1}]}}";

    private List<double> Ticks(ISceneInputSource inputs, FakeSensorReader sensors, int rows, bool cells)
    {
        var nodes = cells ? TableNode + ", " + CellsNode : TableNode;
        var scene = SceneKit.Json("{'format': 'infopanel.scene/1', 'inputs': {'t': {'type': 'table'}}, 'nodes': [" + nodes + "]}");
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(scene, 240, 200, bindings: new Dictionary<string, string> { ["t"] = Key.ToString() }));
        var times = new List<double>();
        const int warmup = 50, samples = 400;
        for (var i = 0; i < warmup + samples; i++)
        {
            sensors.Readings[Key] = new SensorReading(Processes(rows, i), "", "");
            var t0 = Stopwatch.GetTimestamp();
            using var tick = runtime.Tick(i / 30.0, SceneKit.Now);
            var t1 = Stopwatch.GetTimestamp();
            Assert.DoesNotContain(tick.Bounds, b => b.Problem is not null);
            if (i >= warmup) times.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
        }
        return times;
    }

    [Fact]
    public void A_tick_reading_a_new_table_is_not_slower_at_100_rows_and_bounded_at_4096()
    {
        var sensors = new FakeSensorReader();
        var today = ScenePerformanceTests.Percentiles(Ticks(new EagerTables(sensors), sensors, 100, cells: false));
        var asData100 = ScenePerformanceTests.Percentiles(Ticks(new SensorSceneInputs(sensors, new FakeSensorHistory()), sensors, 100, cells: false));
        var cells100 = ScenePerformanceTests.Percentiles(Ticks(new SensorSceneInputs(sensors, new FakeSensorHistory()), sensors, 100, cells: true));
        var cells4096 = ScenePerformanceTests.Percentiles(Ticks(new SensorSceneInputs(sensors, new FakeSensorHistory()), sensors, 4096, cells: true));
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        foreach (var (name, (p50, p95, _)) in new[]
                 {
                     ("today, 100 rows, a table node", today), ("as data, 100 rows, a table node", asData100),
                     ("as data, 100 rows, a table node and four cells", cells100), ("as data, 4,096 rows, a table node and four cells", cells4096),
                 })
            output.WriteLine($"{build}: tick reading a new table each tick, {name}: p50 {p50:0.000} ms, p95 {p95:0.000} ms");

        // Reading as data costs no more than today's reading of 100 rows (the rows a table node draws are made as before,
        // the others never), within a margin for noise; 4,096 rows cost what the rows read cost, far inside the tick's
        // 2 ms target (spec/docs/limits.md, "Performance targets").
        Assert.InRange(asData100.P50, 0, today.P50 * 1.5 + 0.05);
        Assert.InRange(cells4096.P50, 0, cells100.P50 * 2 + 0.1);
        Assert.InRange(cells4096.P95, 0, 2.0);
    }
}
