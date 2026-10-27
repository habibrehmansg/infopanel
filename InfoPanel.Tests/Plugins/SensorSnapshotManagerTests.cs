extern alias PluginHost;

using System.Data;
using InfoPanel.Plugins;
using PluginHost::InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugin host's change detection reads plugin tables while the plugin may still be writing them. A table that
// changes mid-read must cost only its own entry for that cycle, never the other entries' updates.
public class SensorSnapshotManagerTests
{
    private const string Group = "group";

    [Fact]
    public void A_table_that_changes_during_the_read_does_not_drop_the_plugins_other_updates()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var label = new PluginText("label", "Label", "hello");
        var table = new FlakyTable("table", [new InvalidOperationException("Collection was modified; enumeration operation may not execute.")]);
        var wrapper = Wrap(temp, table, label);

        var updates = new SensorSnapshotManager().GetChangedEntries(wrapper);

        Assert.Equal(["temp", "label"], updates.Select(u => u.EntryId).ToArray());
    }

    [Fact]
    public void A_table_whose_rows_are_removed_during_the_read_does_not_drop_the_plugins_other_updates()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var label = new PluginText("label", "Label", "hello");
        var table = new FlakyTable("table", [new RowNotInTableException("This row has been removed from a table and does not have any data.")]);
        var wrapper = Wrap(temp, table, label);

        var updates = new SensorSnapshotManager().GetChangedEntries(wrapper);

        Assert.Equal(["temp", "label"], updates.Select(u => u.EntryId).ToArray());
    }

    [Fact]
    public void A_table_skipped_for_one_cycle_is_sent_on_the_next()
    {
        var table = new FlakyTable("table", [new InvalidOperationException("Collection was modified.")]);
        var wrapper = Wrap(table);
        var manager = new SensorSnapshotManager();

        Assert.Empty(manager.GetChangedEntries(wrapper));

        var retried = Assert.Single(manager.GetChangedEntries(wrapper));
        Assert.Equal("table", retried.EntryId);
        Assert.Equal("table", retried.Type);
    }

    [Fact]
    public void A_table_whose_conversion_fails_is_sent_on_the_next_cycle()
    {
        // The conversion is the table's one read per cycle. When it throws on a changed table nothing is recorded,
        // or the next cycle would see "no change" and the change would never be sent.
        var table = new ScriptedTable("table", ["A", null], then: "B");
        var wrapper = Wrap(table);
        var manager = new SensorSnapshotManager();
        Assert.Equal("A", Assert.Single(Deliver(manager, wrapper)).TableValue!.Rows.Single().Single().TextValue);

        Assert.Empty(Deliver(manager, wrapper));

        var retried = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal("table", retried.EntryId);
        Assert.Equal("B", retried.TableValue!.Rows.Single().Single().TextValue);
    }

    [Fact]
    public void A_sensor_that_throws_is_still_a_plugin_failure_that_reaches_the_caller()
    {
        var wrapper = Wrap(new ThrowingSensor());

        Assert.Throws<InvalidOperationException>(() => new SensorSnapshotManager().GetChangedEntries(wrapper));
    }

    // A DataTable is not thread-safe. Removing and adding columns while the host reads the table makes the read throw
    // more than "Collection was modified": a column removed between finding it and reading its storage throws
    // NullReferenceException from row[i], and a column index past the shrunk collection throws
    // IndexOutOfRangeException. Each is scripted here, so the interleaving is the same on every run.
    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    public void A_table_whose_columns_change_during_the_read_does_not_drop_the_plugins_other_updates(Type failure)
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var label = new PluginText("label", "Label", "hello");
        var table = new FlakyTable("table", [(Exception)Activator.CreateInstance(failure)!]);
        var wrapper = Wrap(temp, table, label);
        var manager = new SensorSnapshotManager();

        Assert.Equal(["temp", "label"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
        Assert.Equal(["table"], Deliver(manager, wrapper).Select(u => u.EntryId).ToArray());
    }

    [Fact]
    public void A_cycle_that_fails_part_way_does_not_suppress_the_values_it_had_already_read()
    {
        // The cycle reads temp, then a plugin failure aborts it, so temp's value never reaches the client. The next
        // cycle must still send it: a value counts as sent only once the host has delivered it.
        var temp = new PluginSensor("temp", "Temperature", 21);
        var wrapper = Wrap(temp, new FailOnceSensor());
        var manager = new SensorSnapshotManager();

        Assert.Throws<InvalidOperationException>(() => Deliver(manager, wrapper));

        Assert.Contains("temp", Deliver(manager, wrapper).Select(u => u.EntryId));
    }

    [Fact]
    public void An_update_the_host_did_not_deliver_is_sent_again_on_the_next_cycle()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var wrapper = Wrap(temp);
        var manager = new SensorSnapshotManager();

        Assert.Single(manager.GetChangedEntries(wrapper)); // read, never delivered

        var resent = Assert.Single(manager.GetChangedEntries(wrapper));
        Assert.Equal(21, resent.SensorValue!.Value);
    }

    [Fact]
    public void A_delivered_value_is_not_sent_again_until_it_changes()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var label = new PluginText("label", "Label", "hello");
        var wrapper = Wrap(temp, label);
        var manager = new SensorSnapshotManager();

        Assert.Equal(2, Deliver(manager, wrapper).Count);
        Assert.Empty(Deliver(manager, wrapper));

        temp.Value = 22;
        var changed = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal("temp", changed.EntryId);
        Assert.Empty(Deliver(manager, wrapper));
    }

    // A plugin changes its table between the host's reads within one cycle: A, then B, then A from then on. Whatever
    // the host sends, once the table stays at A the client must end up with A.
    [Fact]
    public void A_table_that_changes_between_reads_ends_up_delivered_as_it_now_is()
    {
        var table = new ScriptedTable("table", ["A", "B"], then: "A");
        var wrapper = Wrap(table);
        var manager = new SensorSnapshotManager();

        TableValueDto? delivered = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            foreach (var update in Deliver(manager, wrapper)) delivered = update.TableValue;
        }

        Assert.NotNull(delivered);
        Assert.Equal("A", delivered.Rows.Single().Single().TextValue);
    }

    [Fact]
    public void A_sensor_that_changes_between_reads_ends_up_delivered_as_it_now_is()
    {
        var sensor = new ScriptedSensor("sensor", [1, 2], then: 1);
        var wrapper = Wrap(sensor);
        var manager = new SensorSnapshotManager();

        float? delivered = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            foreach (var update in Deliver(manager, wrapper)) delivered = update.SensorValue!.Value;
        }

        Assert.Equal(1, delivered);
    }

    [Fact]
    public void A_table_change_the_payload_carries_is_an_update()
    {
        // A cell's unit, a sensor cell's name and a column's name are all in the payload, so a change to any of them
        // alone is an update.
        var cell = new PluginSensor("rain", "Precipitation", 0, "mm/h");
        var data = new DataTable();
        data.Columns.Add("value", typeof(object));
        data.Rows.Add(cell);
        var table = new PluginTable("table", "Table", data, "");
        var wrapper = Wrap(table);
        var manager = new SensorSnapshotManager();
        Assert.Single(Deliver(manager, wrapper));

        cell.Unit = "in/h";
        var unitChanged = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal("in/h", unitChanged.TableValue!.Rows.Single().Single().SensorUnit);

        data.Columns[0].ColumnName = "amount";
        var columnRenamed = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(["amount"], columnRenamed.TableValue!.Columns);

        // Another sensor with the same value and unit, under another name.
        data.Rows[0][0] = new PluginSensor("rainfall", "Rainfall", 0, "in/h");
        var renamed = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal("Rainfall", renamed.TableValue!.Rows.Single().Single().SensorName);

        Assert.Empty(Deliver(manager, wrapper));
    }

    [Fact]
    public void A_table_whose_read_fails_part_way_through_its_cells_is_sent_whole_on_the_next_cycle()
    {
        // The table itself reads fine; its second cell's getter throws once, after the first cell was converted. The
        // cycle records nothing for the table, the next sends it whole, and the one after has nothing to send.
        var data = new DataTable();
        data.Columns.Add("value", typeof(object));
        data.Rows.Add(new PluginText("first", "First", "a"));
        data.Rows.Add(new FailOnceSensor());
        var wrapper = Wrap(new PluginTable("table", "Table", data, ""));
        var manager = new SensorSnapshotManager();

        Assert.Empty(Deliver(manager, wrapper));

        var table = Assert.Single(Deliver(manager, wrapper)).TableValue!;
        Assert.Equal("a", table.Rows[0].Single().TextValue);
        Assert.Equal(1, table.Rows[1].Single().SensorValue!.Value);

        Assert.Empty(Deliver(manager, wrapper));
    }

    [Fact]
    public void A_sensor_whose_unit_alone_changes_is_an_update()
    {
        // Precipitation reads 0 in dry weather whichever measurement system is chosen; switching it must still send
        // the new unit.
        var rain = new PluginSensor("rain", "Precipitation", 0, "mm/h");
        var wrapper = Wrap(rain);
        var manager = new SensorSnapshotManager();
        Assert.Single(Deliver(manager, wrapper));

        rain.Unit = "in/h";

        var update = Assert.Single(Deliver(manager, wrapper));
        Assert.Equal(0, update.SensorValue!.Value);
        Assert.Equal("in/h", update.SensorValue.Unit);
        Assert.Empty(Deliver(manager, wrapper));
    }

    // One host loads every plugin in a DLL and they share one manager. Two of them may use the same container and
    // entry ids: each plugin's entry keeps its own delivered value, so one plugin's update never hides another's.
    [Fact]
    public void Two_plugins_with_the_same_container_and_entry_ids_keep_separate_delivered_values()
    {
        // Sensors that report only a value (no min, max or average), so A's reading of 2 equals B's.
        var tempA = new ValueSensor("temp", 1);
        var tempB = new ValueSensor("temp", 2);
        var a = WrapAs("plugin-a", tempA);
        var b = WrapAs("plugin-b", tempB);
        var manager = new SensorSnapshotManager();

        // One cycle as the update loop runs it: every plugin is read, then every batch is confirmed delivered.
        var sentA = manager.GetChangedEntries(a);
        var sentB = manager.GetChangedEntries(b);
        manager.MarkSent(a.Id, sentA);
        manager.MarkSent(b.Id, sentB);
        Assert.Equal(1, Assert.Single(sentA).SensorValue!.Value);
        Assert.Equal(2, Assert.Single(sentB).SensorValue!.Value);

        tempA.Value = 2;

        var nextA = manager.GetChangedEntries(a);
        var nextB = manager.GetChangedEntries(b);
        manager.MarkSent(a.Id, nextA);
        manager.MarkSent(b.Id, nextB);
        Assert.Equal(2, Assert.Single(nextA).SensorValue!.Value);
        Assert.Empty(nextB);
    }

    // A table whose read keeps failing (here a cell getter that throws NullReferenceException) is skipped every cycle so
    // the plugin's other entries still go out. That must not be silent: one warning once the streak is long enough.
    [Fact]
    public void A_table_that_keeps_failing_to_read_is_reported_once()
    {
        const int threshold = SensorSnapshotManager.SkipWarningCycles;
        var events = new CollectingSink();
        var manager = new SensorSnapshotManager(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(events).CreateLogger());
        var failure = new NullReferenceException();
        var wrapper = Wrap(new FlakyTable("table", Enumerable.Repeat<Exception?>(failure, 3 * threshold).ToArray()));

        for (var cycle = 1; cycle < threshold; cycle++) Assert.Empty(Deliver(manager, wrapper));
        Assert.Empty(events.Events);

        Assert.Empty(Deliver(manager, wrapper));
        var warning = Assert.Single(events.Events);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Same(failure, warning.Exception);

        for (var cycle = threshold; cycle < 2 * threshold; cycle++) Assert.Empty(Deliver(manager, wrapper));
        Assert.Single(events.Events);
    }

    [Fact]
    public void A_good_read_ends_a_streak_of_failed_reads()
    {
        const int threshold = SensorSnapshotManager.SkipWarningCycles;
        var events = new CollectingSink();
        var manager = new SensorSnapshotManager(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(events).CreateLogger());
        var failures = Enumerable.Repeat<Exception?>(new NullReferenceException(), threshold - 1).ToArray();
        // Just short of the threshold, one good read, then just short again: neither streak is reported.
        var wrapper = Wrap(new FlakyTable("table", [.. failures, null, .. failures, null]));

        for (var cycle = 1; cycle < threshold; cycle++) Assert.Empty(Deliver(manager, wrapper));
        Assert.Equal("table", Assert.Single(Deliver(manager, wrapper)).EntryId);
        for (var cycle = 1; cycle < threshold; cycle++) Assert.Empty(Deliver(manager, wrapper));

        Assert.Empty(events.Events);
    }

    /// <summary>One host cycle that reaches the client: the changes are read, then confirmed delivered.</summary>
    private static List<EntryUpdateDto> Deliver(SensorSnapshotManager manager, PluginWrapper wrapper)
    {
        var updates = manager.GetChangedEntries(wrapper);
        manager.MarkSent(wrapper.Id, updates);
        return updates;
    }

    private static PluginWrapper Wrap(params IPluginData[] entries) => WrapAs("stub", entries);

    private static PluginWrapper WrapAs(string pluginId, params IPluginData[] entries)
    {
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), new StubPlugin(pluginId));
        var container = new PluginContainer(Group);
        container.Entries.AddRange(entries);
        wrapper.PluginContainers.Add(container);
        return wrapper;
    }

    /// <summary>
    /// A table whose <see cref="Value"/> reads fail as a plugin mutating it during a read does: each read takes the
    /// next scripted exception (null: a good read), and once the script is used up every read is good.
    /// </summary>
    private sealed class FlakyTable(string id, Exception?[] script) : IPluginTable
    {
        private int _reads;

        public string Id { get; } = id;
        public string Name { get; } = id;
        public string DefaultFormat { get; } = "";

        public DataTable Value
        {
            get
            {
                var failure = _reads < script.Length ? script[_reads] : null;
                _reads++;
                if (failure != null) throw failure;
                return Rows();
            }
            set => throw new NotSupportedException();
        }

        private static DataTable Rows()
        {
            var table = new DataTable();
            table.Columns.Add("name", typeof(object));
            table.Rows.Add(new PluginText("cell", "Cell", "value"));
            return table;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private sealed class ThrowingSensor : IPluginSensor
    {
        public string Id => "bad";
        public string Name => "Bad";
        public string? Unit => null;
        public float Value { get => throw new InvalidOperationException("plugin failure"); set { } }
    }

    /// <summary>
    /// A table that a plugin replaces between reads: each read of <see cref="Value"/> returns a one-cell table holding
    /// the next scripted text (null: the read throws "Collection was modified"), and once the script is used up,
    /// <c>then</c> on every read.
    /// </summary>
    private sealed class ScriptedTable(string id, string?[] script, string then) : IPluginTable
    {
        private int _reads;

        public string Id { get; } = id;
        public string Name { get; } = id;
        public string DefaultFormat { get; } = "";

        public DataTable Value
        {
            get
            {
                var text = _reads < script.Length ? script[_reads] : then;
                _reads++;
                if (text == null) throw new InvalidOperationException("Collection was modified.");
                var table = new DataTable();
                table.Columns.Add("name", typeof(object));
                table.Rows.Add(new PluginText("cell", "Cell", text));
                return table;
            }
            set => throw new NotSupportedException();
        }
    }

    /// <summary>A sensor that a plugin updates between reads: each read returns the next scripted value, then <c>then</c>.</summary>
    private sealed class ScriptedSensor(string id, float[] script, float then) : IPluginSensor
    {
        private int _reads;

        public string Id { get; } = id;
        public string Name { get; } = id;
        public string? Unit => null;

        public float Value
        {
            get
            {
                var value = _reads < script.Length ? script[_reads] : then;
                _reads++;
                return value;
            }
            set => throw new NotSupportedException();
        }
    }

    /// <summary>A sensor as a plugin may implement it directly: a value and nothing else.</summary>
    private sealed class ValueSensor(string id, float value) : IPluginSensor
    {
        public string Id { get; } = id;
        public string Name { get; } = id;
        public string? Unit => null;
        public float Value { get; set; } = value;
    }

    /// <summary>A sensor whose first read fails as a plugin failure does; every later read is good.</summary>
    private sealed class FailOnceSensor : IPluginSensor
    {
        private bool _failed;

        public string Id => "flaky";
        public string Name => "Flaky";
        public string? Unit => null;

        public float Value
        {
            get
            {
                if (_failed) return 1;
                _failed = true;
                throw new InvalidOperationException("plugin failure");
            }
            set { }
        }
    }

    private sealed class StubPlugin(string id) : IPlugin
    {
        public string Id => id;
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);
        public void Initialize() { }
        public void Load(List<IPluginContainer> containers) { }
        public void Update() { }
        public Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Close() { }
    }
}
