extern alias PluginHost;

using InfoPanel.Plugins;
using PluginHost::InfoPanel.Plugins.Host;
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

    private static Task Cycle(HostService host) => host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);

    private static async Task<HostService> HostWith(params IPluginData[] entries)
    {
        var host = new HostService("plugin.dll");
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
    /// recorded as (plugin, entry, value) when it starts, even one that then throws or blocks. Reads wait for
    /// cancellation: the client never sends anything.
    /// </summary>
    private sealed class TestTransport(params WriteOutcome[] script) : IJsonRpcMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<(string, string, float)> _sent = [];
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
                        _sent.Add((batch.PluginId, update.EntryId, update.SensorValue!.Value));
                    }
                }

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
