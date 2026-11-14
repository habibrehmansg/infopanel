using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Xunit;
using ManualClock = InfoPanel.Core.Tests.Editing.ManualTimeProvider;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// What a tick allocates for its inputs: a source's history and table readings are used as handed over, not copied or
/// converted again every tick. Allocation counts on the ticking thread, not timings.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneInputAllocationTests(ITestOutputHelper output)
{
    private const int Numbers = 30;
    private const int Ticks = 1_000;

    /// <summary>A scene with <paramref name="numbers"/> number inputs (<c>n0</c>…) and, with <paramref name="table"/>, a table input <c>t</c>.</summary>
    private static JsonElement Scene(int numbers, bool table)
    {
        var inputs = new StringBuilder();
        for (var i = 0; i < numbers; i++) inputs.Append($"'n{i}': {{'type': 'number'}},");
        if (table) inputs.Append("'t': {'type': 'table'},");
        var nodes = "{'kind': 'rect', 'w': {'op': 'input', 'name': 'n0'}, 'h': 10, 'fill': '#22c55e'}";
        if (table)
            nodes += ", {'kind': 'table', 'input': 't', 'y': 12, 'rows': 4, 'rowHeight': 16, 'columns': [{'column': 0, 'w': 60}, {'column': 1, 'w': 60}],"
                + " 'font': {'family': 'Inter', 'size': 12}, 'fill': '#e5e7eb'}";
        return SceneKit.Json($"{{'format': 'infopanel.scene/1', 'inputs': {{{inputs.ToString().TrimEnd(',')}}}, 'nodes': [{nodes}]}}");
    }

    /// <summary>Bytes allocated on this thread by <paramref name="ticks"/> ticks, after a few to warm up.</summary>
    private static long Allocated(SceneRuntime runtime, int ticks)
    {
        for (var k = 0; k < 5; k++) runtime.Tick(k / 30.0, SceneKit.Now).Dispose();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < ticks; k++) runtime.Tick((k + 5) / 30.0, SceneKit.Now).Dispose();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void A_full_history_is_not_copied_every_tick()
    {
        var fake = new FakeSceneInputs();
        var full = Enumerable.Range(0, 4_096).Select(i => (double)i).ToArray();
        var bindings = new Dictionary<string, string>();
        for (var i = 0; i < Numbers; i++)
        {
            fake.Number($"test:n{i}", 50);
            fake.Histories[$"test:n{i}"] = full;
            bindings[$"n{i}"] = $"test:n{i}";
        }
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        runtime.Load(SceneKit.Doc(Scene(Numbers, table: false), 200, 40, bindings: bindings));

        var bytes = Allocated(runtime, Ticks);
        output.WriteLine($"{Ticks} ticks, {Numbers} number inputs with full histories: {bytes:N0} B ({bytes / Ticks:N0} B a tick)");
        // A copy of each history is 4,096 × 8 B: 30 a tick would be about 983 MB over the run.
        Assert.InRange(bytes, 0, 30_000_000);
    }

    [Fact]
    public void A_simulated_history_is_copied_once_a_step_not_once_a_read()
    {
        var clock = new ManualClock();
        var simulated = new SimulatedSceneInputs(new SensorSceneInputs(new FakeSensorReader(), new FakeSensorHistory()), clock);
        var key = SensorKey.Plugin("/test/load").ToString();
        simulated.Read(key, "number");
        clock.Advance(1000);
        var first = simulated.History(key);
        var copied = simulated.Copied;

        for (var k = 0; k < Ticks; k++) Assert.Equal(first, simulated.History(key));
        var perRead = simulated.Copied - copied;
        output.WriteLine($"{Ticks} reads of a {first.Count}-value simulated history in one step: {perRead:N0} values copied");
        Assert.InRange(perRead, 0, first.Count);

        // The next step's value is in the next read.
        clock.Advance(1000);
        var next = simulated.History(key);
        Assert.Equal(first.Count + 1, next.Count);
        Assert.Equal(first, next.Take(first.Count));
    }

    /// <summary>A 100 × 5 table of numbers, as a process list reads.</summary>
    private static DataTable Processes(double offset = 0)
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        for (var c = 1; c < 5; c++) table.Columns.Add($"C{c}", typeof(double));
        for (var r = 0; r < 100; r++) table.Rows.Add($"process {r}", r + offset + 0.5, r * 2.25, r * 1024.0, r / 3.0);
        return table;
    }

    /// <summary>Records the table each table read hands the runtime.</summary>
    private sealed class TableReads(ISceneInputSource inner) : ISceneInputSource
    {
        public readonly List<SceneTable> Tables = [];

        public SceneReading Read(string key, string inputType)
        {
            var reading = inner.Read(key, inputType);
            if (inputType == "table") Tables.Add(reading.Table);
            return reading;
        }

        public IReadOnlyList<double> History(string key) => inner.History(key);
    }

    [Fact]
    public void A_table_reading_is_converted_once_per_table_not_once_per_tick()
    {
        var sensors = new FakeSensorReader();
        var key = SensorKey.Plugin("/test/processes");
        sensors.Readings[key] = new SensorReading(Processes(), "", "processes");
        var reads = new TableReads(new SensorSceneInputs(sensors, new FakeSensorHistory()));
        using var runtime = new SceneRuntime(SceneKit.Options(reads));
        runtime.Load(SceneKit.Doc(Scene(1, table: true), 200, 100,
            bindings: new Dictionary<string, string> { ["n0"] = SensorKey.Plugin("/test/n0").ToString(), ["t"] = key.ToString() }));

        var bytes = Allocated(runtime, Ticks);
        var conversions = reads.Tables.Skip(5).Distinct(ReferenceEqualityComparer.Instance).Count();
        output.WriteLine($"{Ticks} ticks of one unchanged 100 × 5 table: {conversions} conversions, {bytes:N0} B ({bytes / Ticks:N0} B a tick)");
        Assert.Equal(1, conversions);

        // A new table is the next tick's reading.
        sensors.Readings[key] = new SensorReading(Processes(offset: 1000), "", "processes");
        runtime.Tick(10_000, SceneKit.Now).Dispose();
        var latest = reads.Tables[^1];
        Assert.NotSame(reads.Tables[^2], latest);
        Assert.Equal("1000.5", latest.Rows[0][1]);
        Assert.Equal(100, latest.Rows.Count);
    }

    [Fact]
    public void A_table_no_longer_read_is_collectable()
    {
        var sensors = new FakeSensorReader();
        var key = SensorKey.Plugin("/test/processes");
        var number = SensorKey.Plugin("/test/n0").ToString();
        var inputs = new SensorSceneInputs(sensors, new FakeSensorHistory());
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(Scene(1, table: true), 200, 100,
            bindings: new Dictionary<string, string> { ["n0"] = number, ["t"] = key.ToString() }));
        var table = ReadTable(runtime, sensors, key);

        // Its binding goes, and the source drops its reading.
        runtime.Load(SceneKit.Doc(Scene(1, table: false), 200, 100, bindings: new Dictionary<string, string> { ["n0"] = number }));
        runtime.Tick(20, SceneKit.Now).Dispose();
        sensors.Readings.Remove(key);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(table.IsAlive, "a table no sensor holds and no binding reads is still reachable");
        GC.KeepAlive(inputs);
    }

    /// <summary>Hands the source a table, ticks a few times so it is read, and returns a weak reference to it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReadTable(SceneRuntime runtime, FakeSensorReader sensors, SensorKey key)
    {
        var table = Processes();
        sensors.Readings[key] = new SensorReading(table, "", "processes");
        for (var k = 0; k < 3; k++)
        {
            using var tick = runtime.Tick(k, SceneKit.Now);
        }
        return new WeakReference(table);
    }

    [Fact]
    public void Readers_of_a_new_table_at_once_all_get_one_conversion()
    {
        const int Readers = 8;
        var sensors = new FakeSensorReader();
        var key = SensorKey.Plugin("/test/processes");
        sensors.Readings[key] = new SensorReading(Processes(), "", "processes");
        var inputs = new SensorSceneInputs(sensors, new FakeSensorHistory());
        var text = key.ToString();
        inputs.Read(text, "number"); // its key parsed first: the race is the conversion's

        var results = new SceneTable[Readers];
        using var start = new Barrier(Readers);
        var threads = Enumerable.Range(0, Readers).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            results[i] = inputs.Read(text, "table").Table;
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(100, results[0].Rows.Count);
        Assert.All(results, table => Assert.Same(results[0], table));
    }

    /// <summary>
    /// What one tick allocates with the engine's sensors: 30 number inputs with full histories and a table input, none
    /// changing. No history is copied, the table isn't converted again, and no binding's key is parsed again (each parse
    /// makes strings of its own).
    /// </summary>
    [Fact]
    public void A_tick_of_unchanged_sensors_allocates_a_bounded_amount()
    {
        var sensors = new FakeSensorReader();
        var history = new FakeSensorHistory();
        var full = Enumerable.Range(0, 4_096).Select(i => (double)i).ToArray();
        var bindings = new Dictionary<string, string>();
        for (var i = 0; i < Numbers; i++)
        {
            var key = SensorKey.Plugin($"/test/sensor/n{i}");
            sensors.Readings[key] = new SensorReading(0, 100, 50, 50, "%");
            history.Samples[key] = full;
            bindings[$"n{i}"] = key.ToString();
        }
        var table = SensorKey.Plugin("/test/processes");
        sensors.Readings[table] = new SensorReading(Processes(), "", "processes");
        bindings["t"] = table.ToString();
        var inputs = new SensorSceneInputs(sensors, history);
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(Scene(Numbers, table: true), 200, 100, bindings: bindings));

        var bytes = Allocated(runtime, Ticks);
        output.WriteLine($"{Ticks} ticks, {Numbers} number inputs and a table: {bytes:N0} B ({bytes / Ticks:N0} B a tick)");

        // The sensors' side alone: one tick's reads, without the runtime.
        var keys = bindings.Values.ToArray();
        foreach (var key in keys) inputs.Read(key, "number");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < Ticks; k++)
            foreach (var key in keys)
            {
                inputs.Read(key, key == bindings["t"] ? "table" : "number");
                if (key != bindings["t"]) inputs.History(key);
            }
        var reads = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{Ticks} rounds of the sensors' reads alone: {reads:N0} B ({reads / Ticks:N0} B a round)");

        // A number's reading is one object (about 100 B); a key parsed again makes about 150 B of strings each time.
        Assert.InRange(reads / Ticks, 0, (Numbers + 1) * 120);
        // The rest of a tick (its snapshot, the evaluator's state, each input's snap) is about 18 KB; at the pinned commit
        // a copy of each full history alone was about 983 KB a tick, the table's conversion about 43 KB and the parses about 6 KB.
        Assert.InRange(bytes / Ticks, 0, 20_000);
    }
}
