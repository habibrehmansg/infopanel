using System.Data;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Loader;
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
        // The first read (the change hash) succeeds and the second (the conversion) throws. The hash must not be
        // recorded before the conversion succeeds, or the next cycle sees "no change" and the table is never sent.
        var table = new FlakyTable("table", [null, new InvalidOperationException("Collection was modified.")]);
        var wrapper = Wrap(table);
        var manager = new SensorSnapshotManager();

        Assert.Empty(manager.GetChangedEntries(wrapper));

        var retried = Assert.Single(manager.GetChangedEntries(wrapper));
        Assert.Equal("table", retried.EntryId);
    }

    [Fact]
    public void A_sensor_that_throws_is_still_a_plugin_failure_that_reaches_the_caller()
    {
        var wrapper = Wrap(new ThrowingSensor());

        Assert.Throws<InvalidOperationException>(() => new SensorSnapshotManager().GetChangedEntries(wrapper));
    }

    private static PluginWrapper Wrap(params IPluginData[] entries)
    {
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), new StubPlugin());
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

    private sealed class ThrowingSensor : IPluginSensor
    {
        public string Id => "bad";
        public string Name => "Bad";
        public string? Unit => null;
        public float Value { get => throw new InvalidOperationException("plugin failure"); set { } }
    }

    private sealed class StubPlugin : IPlugin
    {
        public string Id => "stub";
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
