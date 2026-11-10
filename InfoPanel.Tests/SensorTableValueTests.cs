using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;
using InfoPanel.Tests.Import;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// A source whose sensors read fixed states, listed in the order given: a test's numbers, texts and tables.
/// </summary>
internal sealed class FixedSensorSource(string id, params (SensorKey Key, string Name, SensorState State)[] sensors) : ISensorSource, ISensorStatusSource
{
    public string Id => id;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<SensorInfo> GetSensors() => [.. sensors.Select(s => new SensorInfo(s.Key, id, "Fixed", s.Name, s.State.Reading.Unit ?? ""))];

    public bool Handles(SensorKey key) => sensors.Any(s => s.Key == key);

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    public SensorState ReadState(SensorKey key) => sensors.Single(s => s.Key == key).State;

    /// <summary>
    /// A test engine with <paramref name="sources"/> beside its own (the mock's). Its requests run in the test's execution
    /// context, so a culture the test sets is the one they format with.
    /// </summary>
    public static WebApplicationFactory<Program> Engine(EngineFactory factory, params ISensorSource[] sources) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Configure<TestServerOptions>(o => o.PreserveExecutionContext = true);
            foreach (var source in sources)
                s.AddSingleton(source);
        }));

    /// <summary>A client of <paramref name="engine"/> with the session token.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> engine)
    {
        var client = engine.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        return client;
    }
}

/// <summary>
/// A table sensor's value in <c>GET /api/sensors/values</c>: its <c>table</c>, with typed columns, its first 10 rows as a
/// scene's table input reads them, its row count and the layout its source suggests; System Info's tables, a plugin's
/// (columns of any type, typed by their cells) and a 1.x table imported onto System Info.
/// </summary>
public sealed class SensorTableValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TopCpuUsage = "infopanel:/system-info/processes/top-cpu-usage";

    /// <summary>
    /// A system of <paramref name="count"/> processes, <c>app-01</c> on, each using CPU time and memory: 10 MB a number, so
    /// every Memory cell reads in megabytes (under 1,024 MB).
    /// </summary>
    private sealed class FakeProcesses(int count) : IProcessSampler
    {
        private int _round;

        public int ProcessorCount => 4;
        public TimeSpan Uptime() => TimeSpan.FromHours(1);
        public int ProcessCount() => count;
        public int? ThreadCount() => null;
        public int? HandleCount() => null;
        public double? CpuUsage() => 10;
        public double? CpuUtility() => null;
        public long MemoryUsage() => 2L * 1024 * 1024 * 1024;
        public long MemoryCompression() => 0;

        public IReadOnlyList<ProcessReading> Processes()
        {
            var round = Interlocked.Increment(ref _round);
            return [.. Enumerable.Range(1, count).Select(i => new ProcessReading(i, $"app-{i:00}", TimeSpan.FromMilliseconds(i * round), i * 10L * 1024 * 1024, 1))];
        }
    }

    /// <summary>System Info over <paramref name="processes"/>, polled every 100 ms (its tables read from its second round).</summary>
    private static PolledSensorSource SystemInfo(int processes) =>
        SystemInfoSource.Create(new FakeProcesses(processes), options: new PolledSensorSourceOptions { Interval = TimeSpan.FromMilliseconds(100) });

    /// <summary>The value of <paramref name="key"/>, once it has one (within 20 s).</summary>
    private static async Task<JsonElement> ValueOf(HttpClient client, string key)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var values = JsonDocument.Parse(await client.GetStringAsync("/api/sensors/values", Ct)).RootElement;
            foreach (var value in values.EnumerateArray())
            {
                if (value.GetProperty("key").GetString() == key)
                    return value.Clone();
            }

            Assert.True(DateTime.UtcNow < deadline, $"{key} has no value after 20 s");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task SystemInfosTopCpuUsage_CarriesItsTable_TypedColumns_TheFirst10Rows_AllRowsCounted_AndItsFormat()
    {
        using var source = SystemInfo(12);
        using var factory = new EngineFactory();
        using var engine = FixedSensorSource.Engine(factory, source);
        using var client = FixedSensorSource.Client(engine);

        var value = await ValueOf(client, TopCpuUsage);

        Assert.True(ReadEndpointTests.Valid(value, "sensorValue"));
        var table = value.GetProperty("table");
        Assert.Equal(
            """[{"name":"Process Name","type":"text","unit":""},{"name":"Usage","type":"number","unit":"%"},{"name":"Utility","type":"number","unit":"%"},{"name":"Memory","type":"number","unit":" MB"}]""",
            table.GetProperty("columns").GetRawText());
        // The first 10 of the 12 rows, in the source's order (the text is its first row's), a cell per column; the cells'
        // text is the source's (not held here).
        var rows = table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToList()).ToList();
        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal(4, row.Count));
        Assert.Equal(10, rows.Select(r => r[0]).Distinct().Count());
        Assert.All(rows, row => Assert.Matches("^app-[01][0-9]$", row[0]));
        Assert.StartsWith(rows[0][0] + ", ", value.GetProperty("text").GetString());
        Assert.Equal(12, table.GetProperty("rowCount").GetInt32());
        Assert.Equal("0:200|1:60|2:70|3:100", table.GetProperty("format").GetString());
    }

    /// <summary>
    /// <paramref name="name"/> as the current culture until disposed: a plugin number's cell text is its own
    /// <c>ToString()</c>, in the culture it runs in, so a test that holds its decimal point pins the culture.
    /// </summary>
    private static IDisposable Culture(string name)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = name == "" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
        return new Restore(() => CultureInfo.CurrentCulture = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>A cell that counts how often it is formatted.</summary>
    private sealed class CountedCell(int[] formatted) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider)
        {
            formatted[0]++;
            return "cell";
        }

        public override string ToString() => ToString(null, null);
    }

    [Fact]
    public void AValuesTable_FormatsOnlyTheRowsItCarries()
    {
        // 100 rows of 3 columns: the value carries 10 rows, so 30 cells are formatted, not 300.
        var formatted = new int[1];
        var table = new DataTable();
        foreach (var name in new[] { "A", "B", "C" })
            table.Columns.Add(name, typeof(object));
        for (var i = 0; i < 100; i++)
            table.Rows.Add(new CountedCell(formatted), new CountedCell(formatted), new CountedCell(formatted));

        var value = InfoPanel.Spec.SensorTable.Of(table, null);

        Assert.Equal(10, value.Rows.Count);
        Assert.Equal(100, value.RowCount);
        Assert.Equal(30, formatted[0]);
    }

    [Fact]
    public async Task UnderACultureWithADecimalComma_TheValuesCells_AreTheScenesTableInputsCells()
    {
        using var culture = Culture("de-DE");
        var table = new DataTable();
        table.Columns.Add("Name", typeof(object));
        table.Columns.Add("Power", typeof(object));
        table.Columns.Add("Ratio", typeof(object));
        table.Rows.Add(new PluginText("", "", "Pump"), new PluginSensor("", "", 21.5f, "W"), 2.5d);
        table.Rows.Add(new PluginText("", "", "Fan"), new PluginSensor("", "", 30f, "W"), 0.25d);
        var key = SensorKey.Plugin("/fixed/german-table");
        var source = new FixedSensorSource("fixed",
            (key, "German table", new SensorState(SensorStatus.Live, SensorKind.Table, new SensorReading(table, null!, "Pump, 21,5W, 2.5"))));
        using var factory = new EngineFactory();
        using var engine = FixedSensorSource.Engine(factory, source);
        using var client = FixedSensorSource.Client(engine);

        var value = await ValueOf(client, key.ToString());
        var rows = value.GetProperty("table").GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToList()).ToList();
        var scene = new InfoPanel.Scenes.SensorSceneInputs(
            engine.Services.GetRequiredService<ISensorReader>(), engine.Services.GetRequiredService<ISensorHistory>()).Read(key.ToString(), "table");

        // The culture reached the request: a plugin number reads with its decimal comma, as the scene reads it.
        Assert.Equal("21,5W", rows[0][1]);
        Assert.Equal(scene.Table.Rows.Select(r => r.ToList()).ToList(), rows);
    }

    [Fact]
    public async Task ATablesColumn_IsANumberWhenItsCellsArePluginNumbers_ElseText_AndAnEmptyTableKeepsItsColumnsDeclaredTypes()
    {
        using var culture = Culture("");
        // A plugin's table, as the plugin host gives it: columns of any type, each cell a plugin text or number (or, from
        // another source, any value: text, formatted as a scene reads it). No format.
        var plugin = new DataTable();
        plugin.Columns.Add("Name", typeof(object));
        plugin.Columns.Add("Temp", typeof(object));
        plugin.Columns.Add("Ratio", typeof(object));
        plugin.Rows.Add(new PluginText("", "", "Pump"), new PluginSensor("", "", 21.5f, "W"), 2.5d);
        plugin.Rows.Add(new PluginText("", "", "Fan"), new PluginSensor("", "", 30f, "W"), 0.25d);
        // An empty table whose columns say their types.
        var empty = new DataTable();
        empty.Columns.Add("Process", typeof(PluginText));
        empty.Columns.Add("CPU", typeof(PluginSensor));
        var pluginKey = SensorKey.Plugin("/fixed/plugin-table");
        var emptyKey = SensorKey.Plugin("/fixed/empty-table");
        var source = new FixedSensorSource("fixed",
            (pluginKey, "Plugin table", new SensorState(SensorStatus.Live, SensorKind.Table, new SensorReading(plugin, null!, "Pump, 21.5W, 2.5"))),
            (emptyKey, "Empty table", new SensorState(SensorStatus.Stale, SensorKind.Table, new SensorReading(empty, "0:100|1:50", ""))));
        using var factory = new EngineFactory();
        using var engine = FixedSensorSource.Engine(factory, source);
        using var client = FixedSensorSource.Client(engine);

        var value = await ValueOf(client, pluginKey.ToString());
        Assert.True(ReadEndpointTests.Valid(value, "sensorValue"));
        Assert.Equal(
            """{"columns":[{"name":"Name","type":"text","unit":""},{"name":"Temp","type":"number","unit":"W"},{"name":"Ratio","type":"text","unit":""}],"rows":[["Pump","21.5W","2.5"],["Fan","30.0W","0.25"]],"rowCount":2,"format":""}""",
            value.GetProperty("table").GetRawText());

        var none = await ValueOf(client, emptyKey.ToString());
        Assert.True(ReadEndpointTests.Valid(none, "sensorValue"));
        Assert.Equal(
            """{"columns":[{"name":"Process","type":"text","unit":""},{"name":"CPU","type":"number","unit":""}],"rows":[],"rowCount":0,"format":"0:100|1:50"}""",
            none.GetProperty("table").GetRawText());
    }

    [Fact]
    public async Task ANumberColumnWhoseCellsDifferInUnit_IsANumberWithoutAUnit()
    {
        using var culture = Culture("");
        // System Info's columns, a process's memory in gigabytes from 1,024 MB on: the Memory column's cells have two units.
        var mixed = new DataTable();
        mixed.Columns.Add("Process Name", typeof(PluginText));
        mixed.Columns.Add("Usage", typeof(PluginSensor));
        mixed.Columns.Add("Memory", typeof(PluginSensor));
        mixed.Rows.Add(new PluginText("Process Name", "big"), new PluginSensor("Usage", 12.5f, "%"), new PluginSensor("Memory", 1.2f, " GB"));
        mixed.Rows.Add(new PluginText("Process Name", "small"), new PluginSensor("Usage", 3f, "%"), new PluginSensor("Memory", 845.3f, " MB"));
        var key = SensorKey.Plugin("/fixed/mixed-units");
        var source = new FixedSensorSource("fixed",
            (key, "Mixed units", new SensorState(SensorStatus.Live, SensorKind.Table, new SensorReading(mixed, "0:200|1:60|2:100", "big, 12.5%, 1.2 GB"))));
        using var factory = new EngineFactory();
        using var engine = FixedSensorSource.Engine(factory, source);
        using var client = FixedSensorSource.Client(engine);

        var value = await ValueOf(client, key.ToString());

        Assert.True(ReadEndpointTests.Valid(value, "sensorValue"));
        Assert.Equal(
            """{"columns":[{"name":"Process Name","type":"text","unit":""},{"name":"Usage","type":"number","unit":"%"},{"name":"Memory","type":"number","unit":""}],"rows":[["big","12.5%","1.2 GB"],["small","3.0%","845.3 MB"]],"rowCount":2,"format":"0:200|1:60|2:100"}""",
            value.GetProperty("table").GetRawText());
    }

    // ---- The 1.x import ----

    [Fact]
    public async Task A1xTableOnTheSystemInfoPlugin_ImportsOntoSystemInfo_WhoseValueCarriesThe1xTablesFormat()
    {
        using var source = SystemInfo(3);
        using var factory = new EngineFactory();
        using var engine = FixedSensorSource.Engine(factory, source);
        using var client = FixedSensorSource.Client(engine);
        var archive = V1Fixture.V1Archive(V1Fixture.Items(
            """<DisplayItem xsi:type="TableSensorDisplayItem"><Name>Top CPU</Name><SensorName>Top CPU Usage</SensorName><SensorType>Plugin</SensorType><PluginSensorId>/system-info-plugin/processes/top-cpu-usage</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId><TableFormat>0:200|1:60|2:70|3:100</TableFormat></DisplayItem>"""));

        using var response = await client.PostAsync("/api/import/v1/archive", V1Fixture.Bytes(archive), Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var item = Assert.Single(envelope.GetProperty("items").EnumerateArray());
        Assert.Equal("table", item.GetProperty("kind").GetString());
        var key = item.GetProperty("meta").GetProperty("sensor").GetProperty("key").GetString()!;
        Assert.Equal(TopCpuUsage, key);
        Assert.Contains(envelope.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "import.sensor.translated");

        // The translated key's value: the table the 1.x item's own format lays out, with the 1.x plugin's default format.
        var table = (await ValueOf(client, key)).GetProperty("table");
        Assert.Equal("0:200|1:60|2:70|3:100", table.GetProperty("format").GetString());
        Assert.Equal(["Process Name", "Usage", "Utility", "Memory"], table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(3, table.GetProperty("rowCount").GetInt32());
    }
}
