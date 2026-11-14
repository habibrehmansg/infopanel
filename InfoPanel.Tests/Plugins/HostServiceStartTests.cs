using System.Buffers;
using System.IO.MemoryMappedFiles;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Graphics;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A plugin whose start fails after the host has initialised and loaded it is stopped and dropped: the host's update cycle
// no longer updates it or sends its entries, its own task is stopped and it is closed before its image buffers are
// released. The host tests run the host in this process on a copy of the test plugin, load it without starting the loops,
// and run one update cycle themselves, connected through a transport that records each OnSensorUpdate; the others drive
// a stub plugin directly. The catalogue the start sends is one message: it stays within the engine's limit.
public sealed class HostServiceStartTests : IDisposable
{
    // Generous: every wait here ends on a signal long before it, unless what it waits for never happens.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string _root = PluginFixtures.NewTempRoot("ip-st-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task A_plugin_whose_settings_read_fails_at_its_start_is_not_updated_or_sent()
    {
        // Under throw-settings, reading the settings throws once a setting has been applied, and the start applies the
        // stored label: the start's own settings read fails after the plugin's Initialize and Load have run.
        var package = PluginFixtures.CopyFixture(Path.Combine(_root, "plugins"), "throw-settings");
        var settings = Path.Combine(_root, "settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, PluginFixtures.FixturePluginId + ".json"), """{"label": "stored"}""");
        var host = new HostService(PluginFixtures.DllOf(package), settings);
        var transport = new RecordingTransport();
        using var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);

        try
        {
            var listed = await host.LoadPluginsAsync();
            await host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);

            Assert.DoesNotContain(listed, p => p.Id == PluginFixtures.FixturePluginId);
            // Its Update() would have counted the counter up from 0, and the cycle would have sent its entries.
            Assert.Empty(transport.Sent.Where(s => s.PluginId == PluginFixtures.FixturePluginId));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // 4,500 texts of 4,096 characters (about 18.7 MB as JSON) are each within the engine's limits but together pass the
    // 16 MiB it reads in one message: the catalogue lists every text, those past its budget without their values (the
    // first update sends them), and stays within the limit.
    [Fact]
    public async Task A_catalogue_larger_than_a_message_lists_its_texts_without_values_and_stays_within_the_limit()
    {
        var package = PluginFixtures.CopyFixture(Path.Combine(_root, "plugins"), "texts=4500");
        var host = new HostService(PluginFixtures.DllOf(package));
        var transport = new RecordingTransport();
        using var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);

        try
        {
            await host.LoadPluginsAsync();
            var listed = await transport.Listed.WaitAsync(Bound, TestContext.Current.CancellationToken);

            Assert.InRange(listed.Bytes, 1, PluginLimits.Default.MessageBytes);
            Assert.Equal(4_500, listed.Texts.Count);
            Assert.Contains(listed.Texts, t => t == null);
            Assert.All(listed.Texts, t => Assert.InRange(t?.Length ?? 0, 0, PluginLimits.Default.TextChars));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // 1,024 tables with no rows, each of 64 columns with names of 256 characters, are each within the engine's limits (as
    // are 1,024 entries) but their headers alone pass the 16 MiB it reads in one message: past its budget a table is
    // listed without its value (the first update sends it), and the catalogue stays within the limit.
    [Fact]
    public async Task A_catalogue_of_tables_whose_headers_pass_a_message_lists_some_without_their_value_and_stays_within_the_limit()
    {
        var package = PluginFixtures.CopyFixture(Path.Combine(_root, "plugins"), "empty-tables=1024");
        var host = new HostService(PluginFixtures.DllOf(package));
        var transport = new RecordingTransport();
        using var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);

        try
        {
            await host.LoadPluginsAsync();
            var listed = await transport.Listed.WaitAsync(Bound, TestContext.Current.CancellationToken);

            Assert.InRange(listed.Bytes, 1, PluginLimits.Default.MessageBytes);
            Assert.Equal(1_024, listed.Tables.Count);
            Assert.Contains(listed.Tables, t => t == null);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // The wrapper's Initialize runs the plugin's Initialize and Load and starts nothing: the plugin's own task starts only
    // when the host starts it (Start), once the plugin's start is done.
    [Fact]
    public async Task A_plugins_own_task_starts_only_when_the_host_starts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = new StubPlugin(TimeSpan.FromMilliseconds(1));
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), plugin);

        try
        {
            await wrapper.Initialize();
            Assert.True(wrapper.IsLoaded);
            Assert.False(wrapper.IsRunning, "Initialize started the plugin's own task");

            await wrapper.Start();
            Assert.True(wrapper.IsRunning);
            await plugin.Entered.Task.WaitAsync(Bound, ct);
        }
        finally
        {
            plugin.Release.TrySetResult();
            await wrapper.StopAsync();
        }
    }

    // The host applies a plugin's saved settings before its first update: its own task starts once they are applied. The
    // plugin's ApplyConfig waits (up to a bound) for an update to begin, so an update that could begin before the saved
    // settings are applied does, and sees the setting as it was; the host's order never lets one.
    [Fact]
    public async Task A_plugins_first_update_comes_after_its_saved_settings_are_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Path.Combine(_root, "settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "configurable.json"), """{"on": true}""");
        var host = new HostService(Path.Combine(_root, "plugin.dll"), settings);
        var plugin = new ConfigurablePlugin();

        try
        {
            await host.StartPluginsAsync([plugin], null);

            Assert.True(plugin.Applied);
            Assert.True(await plugin.FirstUpdateSaw.Task.WaitAsync(Bound, ct), "the first update came before the saved settings were applied");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // A plugin without an interval has no task of its own, but its Initialize or Load may have started something (a
    // timer, a connection): stopping it closes it, once.
    [Fact]
    public async Task Stopping_a_plugin_without_an_interval_closes_it_once()
    {
        var plugin = new StubPlugin(TimeSpan.Zero);
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), plugin);

        await wrapper.Initialize();
        await wrapper.StopAsync();
        await wrapper.StopAsync();

        Assert.Equal(1, plugin.Closes);
    }

    // A dropped plugin with an interval may be drawing into its image buffer on its own task: the task is stopped, and
    // the plugin closed, before the buffer's mapping is released, never the other way round.
    [Fact]
    public async Task A_dropped_plugins_own_task_is_stopped_before_its_image_buffers_are_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new HostService("plugin.dll");
        var descriptor = new PluginDescriptor("plugin.dll", null);
        var plugin = new StubPlugin(TimeSpan.FromMilliseconds(1));
        var wrapper = new PluginWrapper(descriptor, plugin);
        var size = PluginImageWriter.ComputeMmfSize(4, 2);
        using var mmf = MemoryMappedFile.CreateNew(null, size);
        var view = mmf.CreateViewAccessor(0, size);
        var writer = new PluginImageWriter("test-image", mmf, view, 4, 2);

        await wrapper.Initialize();
        await wrapper.Start();
        await plugin.Entered.Task.WaitAsync(Bound, ct);
        var drop = host.DropAsync(descriptor, wrapper, [writer]);

        try
        {
            Assert.False(view.SafeMemoryMappedViewHandle.IsClosed, "the image buffer was released while the plugin was drawing");
            Assert.Equal(0, plugin.Closes);
        }
        finally
        {
            plugin.Release.TrySetResult();
            await drop.WaitAsync(Bound, ct);
        }

        Assert.True(view.SafeMemoryMappedViewHandle.IsClosed, "the image buffer is released");
        Assert.Equal(1, plugin.Closes);
    }

    /// <summary>
    /// A plugin with no entries that counts its Close calls. With an interval, its UpdateAsync signals
    /// <see cref="Entered"/> and then waits for <see cref="Release"/> without the token, as a draw under way would.
    /// </summary>
    private sealed class StubPlugin(TimeSpan interval) : IPlugin
    {
        private int _closes;

        public int Closes => Volatile.Read(ref _closes);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "stub";
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => interval;
        public void Initialize() { }
        public void Load(List<IPluginContainer> containers) { }
        public void Update() { }

        public async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
        }

        public void Close() => Interlocked.Increment(ref _closes);
    }

    /// <summary>
    /// A plugin with one setting, <c>on</c> (off by default), and an interval: its first update says whether it saw the
    /// setting on. Its ApplyConfig waits up to two seconds for an update to begin before it applies the value.
    /// </summary>
    private sealed class ConfigurablePlugin : IPlugin, IPluginConfigurable
    {
        private readonly TaskCompletionSource _updating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _on;

        public TaskCompletionSource<bool> FirstUpdateSaw { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Applied { get; private set; }

        public string Id => "configurable";
        public string Name => "Configurable";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(1);
        public void Initialize() { }
        public void Load(List<IPluginContainer> containers) { }
        public void Update() { }

        public Task UpdateAsync(CancellationToken cancellationToken)
        {
            // Read before the signal that releases a waiting ApplyConfig.
            var on = _on;
            FirstUpdateSaw.TrySetResult(on);
            _updating.TrySetResult();
            return Task.CompletedTask;
        }

        public void Close() { }

        public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
            [new PluginConfigProperty { Key = "on", DisplayName = "On", Type = PluginConfigType.Boolean, Value = _on }];

        public void ApplyConfig(string key, object? value)
        {
            _updating.Task.Wait(TimeSpan.FromSeconds(2));
            _on = (bool)value!;
            Applied = true;
        }
    }

    /// <summary>
    /// A JsonRpc transport that records each OnSensorUpdate's entries as (plugin, entry, value), and the OnPluginsLoaded
    /// write: its size, as the JSON the host's formatter writes, and the values of the <c>texts</c> container's entries and of
    /// the <c>empty-tables</c> container's tables it lists. It ignores the rest.
    /// </summary>
    private sealed class RecordingTransport : IJsonRpcMessageHandler
    {
        private readonly List<(string PluginId, string EntryId, string Value)> _sent = [];
        private readonly TaskCompletionSource<(long Bytes, List<string?> Texts, List<TableValueDto?> Tables)> _listed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();

        public List<(string PluginId, string EntryId, string Value)> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        /// <summary>The catalogue's size and its <c>texts</c> entries' values, once it was written.</summary>
        public Task<(long Bytes, List<string?> Texts, List<TableValueDto?> Tables)> Listed => _listed.Task;

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnPluginsLoaded) } loaded)
            {
                try
                {
                    // The arguments are read before serialising, which replaces them with JSON tokens.
                    var containers = (Dictionary<string, List<ContainerDto>>)loaded.ArgumentsList![1]!;
                    var texts = containers.Values.SelectMany(c => c).Where(c => c.Id == "texts")
                        .SelectMany(c => c.Entries).Select(e => e.TextValue).ToList();
                    var tables = containers.Values.SelectMany(c => c).Where(c => c.Id == "empty-tables")
                        .SelectMany(c => c.Entries).Select(e => e.TableValue).ToList();
                    var json = new ArrayBufferWriter<byte>();
                    Formatter.Serialize(json, message);
                    _listed.TrySetResult((json.WrittenCount, texts, tables));
                }
                catch (Exception ex)
                {
                    _listed.TrySetException(ex);
                }
            }
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnSensorUpdate) } request)
            {
                lock (_sent)
                {
                    foreach (var batch in (IEnumerable<SensorUpdateBatchDto>)request.ArgumentsList![0]!)
                    {
                        foreach (var update in batch.Updates)
                            _sent.Add((batch.PluginId, update.EntryId, update.SensorValue?.Value.ToString() ?? update.TextValue ?? update.Type));
                    }
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
