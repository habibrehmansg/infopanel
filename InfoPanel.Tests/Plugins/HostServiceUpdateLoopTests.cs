using System.Buffers;
using System.Data;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugin host's update loop sends what changed to the client and counts it as sent only once the send completed.
// Each test drives a HostService holding one initialised plugin with one sensor, connected through a JsonRpc whose
// transport the test controls: every write completes, throws or waits on a gate, and each OnSensorUpdate is recorded.
public class HostServiceUpdateLoopTests
{
    // Generous: every wait here ends on a signal long before it, unless what it waits for never happens.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_value_whose_send_completed_is_not_sent_again()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var host = await HostWith(temp);
        var transport = new TestTransport();
        Connect(host, transport);

        await Cycle(host);
        await Cycle(host);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal(("stub", "temp", 21f), sent);
    }

    [Fact]
    public async Task A_value_whose_send_failed_is_sent_again_on_the_next_cycle()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var host = await HostWith(temp);
        var transport = new TestTransport(WriteOutcome.Throw);
        Connect(host, transport);

        await Cycle(host);
        await Cycle(host);
        await Cycle(host);

        Assert.Equal([("stub", "temp", 21f), ("stub", "temp", 21f)], transport.Sent);
    }

    [Fact]
    public async Task Nothing_is_marked_sent_before_a_client_connects()
    {
        var temp = new PluginSensor("temp", "Temperature", 21);
        var host = await HostWith(temp);
        await Cycle(host);

        var transport = new TestTransport();
        Connect(host, transport);
        await Cycle(host);
        await Cycle(host);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal(("stub", "temp", 21f), sent);
    }

    // A client that is connected but stops reading leaves the send pending for good: shutdown must not wait for it.
    [Fact]
    public async Task Shutdown_does_not_wait_for_a_send_that_never_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var temp = new PluginSensor("temp", "Temperature", 21);
        var host = await HostWith(temp);
        var transport = new TestTransport(WriteOutcome.Block);
        var jsonRpc = Connect(host, transport);
        var shutdownRequested = false;
        host.OnShutdownRequested = () =>
        {
            shutdownRequested = true;
            jsonRpc.Dispose();
        };

        try
        {
            host.StartLoops();
            await transport.WriteStarted.WaitAsync(Bound, ct);

            await host.ShutdownAsync().WaitAsync(Bound, ct);

            Assert.True(shutdownRequested);
        }
        finally
        {
            transport.OpenGate();
        }
    }

    // A table the engine would cut to 1,000 rows, whose 20,000 rows of 10 cells of 64 characters (about 19 MB as JSON) pass the 16 MiB
    // the engine reads in one message: the host cuts it before sending, so no message passes the engine's limit.
    [Fact]
    public async Task A_20000_row_table_is_cut_to_the_engines_rows_and_no_message_passes_its_limit()
    {
        var host = await HostWith(Table("processes", rows: 20_000, columns: 10, chars: 64));
        var transport = new TestTransport();
        Connect(host, transport);

        await Cycle(host);

        AllWithinTheEnginesLimit(transport);
        Assert.Equal([("processes", 1_000)], transport.Tables);
    }

    // 1,000 rows of 64 cells of 1 KiB is within the engine's table limits but 64 MB: the host sends the rows that fit.
    [Fact]
    public async Task A_table_within_the_engines_limits_but_larger_than_a_message_is_cut_to_what_fits()
    {
        var host = await HostWith(Table("wide", rows: 1_000, columns: 64, chars: 1_024));
        var transport = new TestTransport();
        Connect(host, transport);

        await Cycle(host);

        AllWithinTheEnginesLimit(transport);
        var (id, rows) = Assert.Single(transport.Tables);
        Assert.Equal("wide", id);
        Assert.InRange(rows, 1, 999);
    }

    // Six tables of 1,000 rows of 3 cells of 1,000 characters, about 3 MB each, are within every limit alone but 19 MB
    // together: they all go, whole, across messages within the limit, as fast as the host's wire allows (its messages of
    // readings at most 8 MiB at once, then 2 MiB a second: IpcLimits), the rest waiting for later cycles.
    [Fact]
    public async Task Changes_larger_than_a_message_together_are_sent_whole_across_messages_as_the_wire_allows()
    {
        var tables = Enumerable.Range(0, 6).Select(i => Table($"t{i}", rows: 1_000, columns: 3, chars: 1_000)).ToArray();
        var time = new InfoPanel.Tests.Desktop.ManualTime(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var host = await HostWith(time, tables);
        var transport = new TestTransport();
        Connect(host, transport);

        await Cycle(host);
        Assert.InRange(transport.Tables.Count, 1, tables.Length - 1);
        for (var cycle = 0; cycle < 150 && transport.Tables.Count < tables.Length; cycle++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Cycle(host);
        }

        AllWithinTheEnginesLimit(transport);
        Assert.Equal(tables.Select(t => (t.Id, 1_000)), transport.Tables);
    }

    // One text of 20 million characters (a plugin putting a log file in a text) passes the 16 MiB the engine reads in one
    // message: the host cuts it to the engine's 4,096 characters before sending, so no message passes the limit.
    [Fact]
    public async Task A_text_longer_than_a_message_is_cut_to_the_engines_characters_and_no_message_passes_its_limit()
    {
        var host = await HostWith(new PluginText("log", "Log", new string('x', 20_000_000)));
        var transport = new TestTransport();
        Connect(host, transport);

        await Cycle(host);

        AllWithinTheEnginesLimit(transport);
        Assert.Equal([("log", PluginLimits.Default.TextChars)], transport.Texts);
    }

    private static void AllWithinTheEnginesLimit(TestTransport transport)
    {
        Assert.NotEmpty(transport.MessageBytes);
        Assert.All(transport.MessageBytes, bytes => Assert.InRange(bytes, 1, PluginLimits.Default.MessageBytes));
    }

    /// <summary>A table of <paramref name="rows"/> × <paramref name="columns"/> text cells, each <paramref name="chars"/> characters.</summary>
    private static PluginTable Table(string id, int rows, int columns, int chars)
    {
        var data = new DataTable();
        for (var c = 0; c < columns; c++)
            data.Columns.Add($"c{c}", typeof(PluginText));
        for (var r = 0; r < rows; r++)
        {
            var cells = new object[columns];
            for (var c = 0; c < columns; c++)
                cells[c] = new PluginText("", $"r{r}c{c}".PadRight(chars, 'x')[..chars]);
            data.Rows.Add(cells);
        }
        return new PluginTable(id, id, data, "");
    }

    private static Task Cycle(HostService host) => host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);

    private static Task<HostService> HostWith(params IPluginData[] entries) => HostWith(TimeProvider.System, entries);

    private static async Task<HostService> HostWith(TimeProvider time, params IPluginData[] entries)
    {
        var host = new HostService("plugin.dll", null, null, time);
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), new StubPlugin(entries));
        await wrapper.Initialize();
        host.AddInitializedWrapper(wrapper);
        return host;
    }

    private static JsonRpc Connect(HostService host, TestTransport transport)
    {
        var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);
        return jsonRpc;
    }

    private enum WriteOutcome { Complete, Throw, Block }

    /// <summary>
    /// A JsonRpc transport whose sensor update writes follow a script (then complete). Each OnSensorUpdate write is
    /// recorded when it starts, even one that then throws or blocks: its sensors as (plugin, entry, value), its tables as
    /// (entry, rows), its texts as (entry, characters), and its size as the JSON the host's formatter writes. Reads wait for cancellation: the client never
    /// sends anything.
    /// </summary>
    private sealed class TestTransport(params WriteOutcome[] script) : IJsonRpcMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<(string, string, float)> _sent = [];
        private readonly List<(string, int)> _tables = [];
        private readonly List<(string, int)> _texts = [];
        private readonly List<long> _messageBytes = [];
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();

        public Task WriteStarted => _writeStarted.Task;

        public List<(string, string, float)> Sent
        {
            get { lock (_lock) return [.. _sent]; }
        }

        public List<(string Id, int Rows)> Tables
        {
            get { lock (_lock) return [.. _tables]; }
        }

        public List<(string Id, int Chars)> Texts
        {
            get { lock (_lock) return [.. _texts]; }
        }

        public List<long> MessageBytes
        {
            get { lock (_lock) return [.. _messageBytes]; }
        }

        public void OpenGate() => _gate.TrySetResult();

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public async ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is not JsonRpcRequest { Method: nameof(IPluginClientCallback.OnSensorUpdate) } request) return;

            WriteOutcome outcome;
            lock (_lock)
            {
                // The loop reuses its batch list, so the values are copied as the write starts.
                var batches = (IEnumerable<SensorUpdateBatchDto>)request.ArgumentsList![0]!;
                foreach (var batch in batches)
                {
                    foreach (var update in batch.Updates)
                    {
                        if (update.TableValue is { } table)
                            _tables.Add((update.EntryId, table.Rows.Count));
                        else if (update.TextValue is { } text)
                            _texts.Add((update.EntryId, text.Length));
                        else
                            _sent.Add((batch.PluginId, update.EntryId, update.SensorValue!.Value));
                    }
                }

                var json = new ArrayBufferWriter<byte>();
                Formatter.Serialize(json, message);
                _messageBytes.Add(json.WrittenCount);

                outcome = _writes < script.Length ? script[_writes] : WriteOutcome.Complete;
                _writes++;
            }

            _writeStarted.TrySetResult();

            switch (outcome)
            {
                case WriteOutcome.Throw:
                    throw new IOException("The write failed.");
                case WriteOutcome.Block:
                    await _gate.Task.WaitAsync(cancellationToken);
                    break;
            }
        }
    }

    private sealed class StubPlugin(IPluginData[] entries) : IPlugin
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        // Updated by the host's loop (Update), so no plugin task runs.
        public TimeSpan UpdateInterval => TimeSpan.Zero;
        public void Initialize() { }

        public void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("group");
            container.Entries.AddRange(entries);
            containers.Add(container);
        }

        public void Update() { }
        public Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Close() { }
    }
}
