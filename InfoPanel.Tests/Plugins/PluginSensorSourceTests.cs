using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugins sensor source: what hosts list, under 1.x's keys, with statuses that follow the host, and bounds on what a
// host may send.
public class PluginSensorSourceTests
{
    private const string Folder = "InfoPanel.Fixture";

    [Fact]
    public void Keys_are_1x_s_with_and_without_an_ephemeral_container_and_groups_are_plugin_and_container()
    {
        var source = Running(Folder, Plugin("weather", "Weather",
            Container("city", "City", eph: false, Sensor("temp", "Temperature", 21, "°C")),
            Container("now", "Now", eph: true, Text("summary", "Summary", "Sunny"))));

        var sensors = source.GetSensors();

        Assert.Equal(["plugin:/weather/city/temp", "plugin:/weather/summary"], sensors.Select(s => s.Key.ToString()).ToArray());
        Assert.Equal(["Weather › City", "Weather › Now"], sensors.Select(s => s.Group).ToArray());
        Assert.All(sensors, s => Assert.Equal(PluginSensorSource.SourceId, s.SourceId));
        Assert.Equal(["°C", ""], sensors.Select(s => s.Unit).ToArray());
    }

    [Fact]
    public void Numbers_texts_and_tables_read_as_the_scene_runtime_reads_them()
    {
        var table = new TableValueDto
        {
            Columns = ["Name", "Load"],
            DefaultFormat = "0:150|1:80",
            Rows =
            [
                [new TableCellDto { Type = "text", TextValue = "alpha" }, new TableCellDto { Type = "sensor", SensorName = "load", SensorUnit = "%", SensorValue = new SensorValueDto { Value = 12.5f, Unit = "%" } }],
            ],
        };
        var source = Running(Folder, Plugin("p", "P", Container("c", "C", eph: false,
            Sensor("n", "N", 3, "V", min: 1, max: 5, avg: 2),
            Text("t", "T", "hello"),
            new EntryDto { Id = "tbl", Name = "Tbl", Type = "table", TableValue = table })));
        var hub = new SensorHub([source]);
        var inputs = new SensorSceneInputs(hub, new SensorHistory(hub, TimeProvider.System));

        var number = inputs.Read("plugin:/p/c/n", "number");
        Assert.Equal((SceneInputStatus.Live, 3d, 1d, 5d, 2d, "V"), (number.Status, number.Now, number.Min, number.Max, number.Avg, number.Unit));
        Assert.Equal("hello", inputs.Read("plugin:/p/c/t", "text").Text);
        var read = inputs.Read("plugin:/p/c/tbl", "table");
        Assert.Equal(SceneInputStatus.Live, read.Status);
        Assert.Equal(["Name", "Load"], read.Table.Columns);
        Assert.Equal(["alpha", "12.5%"], read.Table.Rows.Single());
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read("plugin:/p/c/t", "number").Status);
    }

    [Fact]
    public void Updates_change_the_readings_of_the_entries_the_host_listed()
    {
        var source = Running(Folder, Plugin("p", "P",
            Container("c", "C", eph: false, Sensor("n", "N", 3, "V")),
            Container("e", "E", eph: true, Sensor("m", "M", 4, "V"))));

        source.Update(Folder,
        [
            new SensorUpdateBatchDto
            {
                PluginId = "p",
                Updates =
                [
                    new EntryUpdateDto { ContainerId = "c", EntryId = "n", Type = "sensor", SensorValue = new SensorValueDto { Value = 30, Unit = "V" } },
                    new EntryUpdateDto { ContainerId = "e", EntryId = "m", Type = "sensor", SensorValue = new SensorValueDto { Value = float.NaN, ValueMax = float.PositiveInfinity, Unit = "V" } },
                    new EntryUpdateDto { ContainerId = "c", EntryId = "unlisted", Type = "sensor", SensorValue = new SensorValueDto { Value = 1 } },
                ],
            },
        ]);

        Assert.True(source.TryRead(SensorKey.Plugin("/p/c/n"), out var n));
        Assert.Equal(30, n.ValueNow);
        // Non-finite numbers read as 0.
        Assert.True(source.TryRead(SensorKey.Plugin("/p/m"), out var m));
        Assert.Equal((0d, 0d), (m.ValueNow, m.ValueMax));
        Assert.False(source.Handles(SensorKey.Plugin("/q/c/n")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(SensorKey.Plugin("/p/c/unlisted")));
    }

    [Fact]
    public void Statuses_follow_the_host_live_while_running_stale_while_restarting_unavailable_when_failed_or_stopped()
    {
        var source = Running(Folder, Plugin("p", "P", Container("c", "C", eph: false, Sensor("n", "N", 3, "V"))));
        var key = SensorKey.Plugin("/p/c/n");

        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        source.SetState(Folder, PluginHostState.Restarting);
        Assert.Equal(SensorStatus.Stale, source.GetStatus(key));
        Assert.True(source.TryRead(key, out var kept));
        Assert.Equal(3, kept.ValueNow);
        source.SetState(Folder, PluginHostState.Failed);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
        Assert.False(source.TryRead(key, out _));
        source.SetState(Folder, PluginHostState.Stopped);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
        // Still listed, and still this source's: an unavailable sensor, not a key no source knows.
        Assert.True(source.Handles(key));
        Assert.Single(source.GetSensors());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("a/b", "'/'")]
    [InlineData("infopanel.macos", "infopanel.")]
    [InlineData("InfoPanel.Mock", "infopanel.")]
    public void A_plugin_id_that_would_break_keys_or_take_the_engines_prefix_is_refused(string id, string reason)
    {
        var source = new PluginSensorSource();

        var refused = source.Load(Folder, [new PluginMetadataDto { Id = id, Name = "X" }], new Dictionary<string, List<ContainerDto>>
        {
            [id] = [Container("c", "C", eph: false, Sensor("n", "N", 1, ""))],
        });

        Assert.Contains(reason, Assert.Single(refused).Reason);
        Assert.Empty(source.GetSensors());
    }

    [Fact]
    public void A_plugin_id_over_64_characters_is_refused()
    {
        var id = new string('a', 65);
        var refused = new PluginSensorSource().Load(Folder, [new PluginMetadataDto { Id = id, Name = "X" }], new Dictionary<string, List<ContainerDto>>());

        Assert.Contains("64", Assert.Single(refused).Reason);
    }

    [Fact]
    public void A_second_package_offering_a_loaded_plugin_id_is_refused_so_keys_stay_unique()
    {
        var source = Running(Folder, Plugin("p", "P", Container("c", "C", eph: false, Sensor("n", "N", 1, ""))));

        var refused = source.Load("InfoPanel.Other", [new PluginMetadataDto { Id = "p", Name = "P" }], new Dictionary<string, List<ContainerDto>>
        {
            ["p"] = [Container("c", "C", eph: false, Sensor("n", "N", 99, ""))],
        });

        Assert.Contains(Folder, Assert.Single(refused).Reason);
        Assert.True(source.TryRead(SensorKey.Plugin("/p/c/n"), out var reading));
        Assert.Equal(1, reading.ValueNow);
    }

    [Fact]
    public void A_host_listing_20000_entries_keeps_16384_with_one_warning()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = new PluginSensorSource(logger: logger);
        var entries = Enumerable.Range(0, 20_000).Select(i => Sensor($"s{i}", $"S{i}", i, "")).ToArray();

        source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = "P" }],
            new Dictionary<string, List<ContainerDto>> { ["p"] = [Container("c", "C", eph: false, entries)] });

        Assert.Equal(16_384, source.GetSensors().Count);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void Texts_names_and_tables_past_their_bounds_are_cut_with_one_warning_per_entry()
    {
        var logger = new CountingLogger<PluginSensorSource>();
        var source = new PluginSensorSource(logger: logger);
        var bigTable = new TableValueDto
        {
            Columns = [.. Enumerable.Range(0, 70).Select(i => $"c{i}")],
            Rows = [.. Enumerable.Range(0, 1200).Select(_ => Enumerable.Range(0, 70).Select(_ => new TableCellDto { Type = "text", TextValue = new string('y', 2000) }).ToList())],
        };
        source.Load(Folder, [new PluginMetadataDto { Id = "p", Name = new string('P', 300) }], new Dictionary<string, List<ContainerDto>>
        {
            ["p"] =
            [
                Container("c", "C", eph: false,
                    Text("t", new string('n', 300), new string('x', 5000)),
                    new EntryDto { Id = "tbl", Name = "Tbl", Type = "table", TableValue = bigTable }),
            ],
        });
        source.SetState(Folder, PluginHostState.Running);
        // The same text again: still one warning for the entry.
        source.Update(Folder, [new SensorUpdateBatchDto { PluginId = "p", Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = "t", Type = "text", TextValue = new string('z', 5000) }] }]);

        var text = source.GetSensors().Single(s => s.Key.Id == "/p/c/t");
        Assert.Equal(256, text.Name.Length);
        Assert.Equal(256, text.Group.Length);
        Assert.True(source.TryRead(text.Key, out var t));
        Assert.Equal(4096, t.ValueText!.Length);
        Assert.True(source.TryRead(SensorKey.Plugin("/p/c/tbl"), out var tbl));
        Assert.Equal((1000, 64), (tbl.ValueTable!.Rows.Count, tbl.ValueTable.Columns.Count));
        Assert.Equal(1024, tbl.ValueTable.Rows[0][0].ToString()!.Length);
        Assert.Equal(2, logger.Warnings);
    }

    [Fact]
    public void A_container_or_entry_id_over_256_characters_or_a_key_over_512_refuses_the_plugins_catalogue()
    {
        var source = new PluginSensorSource();
        var plugins = new[]
        {
            Plugin("long-container", "A", Container(new string('c', 257), "C", eph: false, Sensor("s", "S", 1, ""))),
            Plugin("long-entry", "B", Container("c", "C", eph: false, Sensor(new string('e', 257), "S", 1, ""))),
            // Each part within 256, but plugin:/long-key/{256}/{256} is over the 512 a document's sensor key may be.
            Plugin("long-key", "K", Container(new string('c', 256), "C", eph: false, Sensor(new string('e', 256), "S", 1, ""))),
            // An ephemeral container's id isn't in its keys, but it is still bounded.
            Plugin("long-ephemeral", "E", Container(new string('c', 257), "C", eph: true, Sensor("s", "S", 1, ""))),
            Plugin("fine", "F", Container(new string('c', 256), "C", eph: false, Sensor(new string('e', 200), "S", 1, ""))),
        };

        var refused = source.Load(Folder, [.. plugins.Select(p => p.Item1)], plugins.ToDictionary(p => p.Item1.Id, p => p.Item2));

        Assert.Equal(["long-container", "long-entry", "long-key", "long-ephemeral"], refused.Select(r => r.PluginId).ToArray());
        Assert.Contains("container id longer than 256", refused[0].Reason);
        Assert.Contains("entry id longer than 256", refused[1].Reason);
        Assert.Contains("key longer than 512", refused[2].Reason);
        Assert.Contains("container id longer than 256", refused[3].Reason);
        // The reason names the bound, not the id.
        Assert.All(refused, r => Assert.True(r.Reason.Length < 200, r.Reason));
        var only = Assert.Single(source.GetSensors());
        Assert.Equal($"plugin:/fine/{new string('c', 256)}/{new string('e', 200)}", only.Key.ToString());
        // A refused plugin's id isn't taken: its keys are left to the hub.
        Assert.False(source.Handles(SensorKey.Plugin("/long-entry/c/s")));
    }

    [Fact]
    public void Repaired_table_column_names_never_collide()
    {
        var source = Running(Folder, Plugin("p", "P", Container("c", "C", eph: false, new EntryDto
        {
            Id = "tbl",
            Name = "Tbl",
            Type = "table",
            TableValue = new TableValueDto
            {
                Columns = ["x#2", "x", "x", "", ""],
                Rows = [[.. Enumerable.Range(0, 5).Select(i => new TableCellDto { Type = "text", TextValue = $"v{i}" })]],
            },
        })));

        Assert.True(source.TryRead(SensorKey.Plugin("/p/c/tbl"), out var reading));
        var columns = reading.ValueTable!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToArray();
        Assert.Equal(5, columns.Length);
        Assert.Equal(columns.Length, columns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["x#2", "x"], columns[..2]);
        Assert.Equal("v4", reading.ValueTable.Rows[0][4].ToString());
    }

    private static PluginSensorSource Running(string folder, (PluginMetadataDto Meta, List<ContainerDto> Containers) plugin)
    {
        var source = new PluginSensorSource();
        Assert.Empty(source.Load(folder, [plugin.Meta], new Dictionary<string, List<ContainerDto>> { [plugin.Meta.Id] = plugin.Containers }));
        source.SetState(folder, PluginHostState.Running);
        return source;
    }

    private static (PluginMetadataDto, List<ContainerDto>) Plugin(string id, string name, params ContainerDto[] containers) =>
        (new PluginMetadataDto { Id = id, Name = name }, [.. containers]);

    private static ContainerDto Container(string id, string name, bool eph, params EntryDto[] entries) =>
        new() { Id = id, Name = name, IsEphemeralPath = eph, Entries = [.. entries] };

    private static EntryDto Sensor(string id, string name, float value, string unit, float? min = null, float? max = null, float? avg = null) =>
        new()
        {
            Id = id,
            Name = name,
            Type = "sensor",
            SensorValue = new SensorValueDto { Value = value, ValueMin = min ?? value, ValueMax = max ?? value, ValueAvg = avg ?? value, Unit = unit },
        };

    private static EntryDto Text(string id, string name, string text) => new() { Id = id, Name = name, Type = "text", TextValue = text };

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warnings);
        }
    }
}
