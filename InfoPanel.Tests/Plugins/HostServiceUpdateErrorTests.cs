using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A plugin's failing updates are logged by the host and reported to the engine as the package's errors, once per streak
// of failures: a plugin whose device is gone doesn't look healthy, and one that fails every cycle doesn't flood either.
// Each test drives a HostService holding one initialised plugin, with a log of its own and connected through a transport
// that records each OnPluginError; the host's update cycle is run by the test.
public class HostServiceUpdateErrorTests
{
    // Generous: every wait here ends on a signal long before it, unless what it waits for never happens.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_Update_that_keeps_throwing_is_logged_and_reported_once_per_streak()
    {
        var plugin = new ThrowingPlugin(TimeSpan.Zero) { Failing = true };
        var (host, _, log, errors) = await HostWith(plugin);

        // A streak of three failing cycles, one that updates, then a new streak of two.
        await Cycle(host);
        await Cycle(host);
        await Cycle(host);
        plugin.Failing = false;
        await Cycle(host);
        plugin.Failing = true;
        await Cycle(host);
        await Cycle(host);

        Assert.Equal(6, plugin.Updates);
        Assert.Equal(2, errors.Errors.Count);
        Assert.All(errors.Errors, e =>
        {
            Assert.Equal("stub", e.PluginId);
            Assert.Contains(nameof(InvalidOperationException), e.Message);
        });
        Assert.Equal(2, log.Events.Count(e => e.Level == LogEventLevel.Warning && e.Exception is InvalidOperationException));
    }

    [Fact]
    public async Task An_UpdateAsync_that_throws_on_the_plugins_own_task_is_logged_and_reported_once_per_streak()
    {
        var plugin = new ThrowingPlugin(TimeSpan.FromMilliseconds(1)) { Failing = true };
        var (host, wrapper, log, errors) = await HostWith(plugin);

        try
        {
            // The plugin's task runs each UpdateAsync after the last one ended: once the second has started, the first
            // has thrown and been handled.
            await plugin.SecondUpdateStarted.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await Cycle(host);
            await Cycle(host);

            var error = Assert.Single(errors.Errors);
            Assert.Equal("stub", error.PluginId);
            Assert.Contains(nameof(InvalidOperationException), error.Message);
            Assert.Single(log.Events, e => e.Level == LogEventLevel.Warning && e.Exception is InvalidOperationException);
        }
        finally
        {
            await wrapper.StopAsync();
        }
    }

    private static Task Cycle(HostService host) => host.RunUpdateCycleAsync([], TestContext.Current.CancellationToken);

    private static async Task<(HostService Host, PluginWrapper Wrapper, CollectingSink Log, ErrorTransport Errors)> HostWith(IPlugin plugin)
    {
        var log = new CollectingSink();
        var host = new HostService("plugin.dll", logger: new LoggerConfiguration().WriteTo.Sink(log).CreateLogger());
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), plugin);
        await wrapper.Initialize();
        host.AddInitializedWrapper(wrapper);

        var transport = new ErrorTransport();
        var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);
        return (host, wrapper, log, transport);
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

    /// <summary>A JsonRpc transport that records each OnPluginError as (plugin, message) when its write starts, and ignores the rest.</summary>
    private sealed class ErrorTransport : IJsonRpcMessageHandler
    {
        private readonly List<(string PluginId, string Message)> _errors = [];

        public bool CanRead => true;
        public bool CanWrite => true;
        public IJsonRpcMessageFormatter Formatter { get; } = new JsonMessageFormatter();

        public List<(string PluginId, string Message)> Errors
        {
            get { lock (_errors) return [.. _errors]; }
        }

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is JsonRpcRequest { Method: nameof(IPluginClientCallback.OnPluginError) } request)
            {
                lock (_errors)
                    _errors.Add(((string)request.ArgumentsList![0]!, (string)request.ArgumentsList[1]!));
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A plugin with one sensor whose Update (with no interval) or UpdateAsync (on its own task, with one) throws while
    /// <see cref="Failing"/> is set.
    /// </summary>
    private sealed class ThrowingPlugin(TimeSpan interval) : IPlugin
    {
        private readonly PluginSensor _sensor = new("temp", "Temperature", 21);
        private readonly TaskCompletionSource _secondUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _updates;

        public volatile bool Failing;

        public int Updates => Volatile.Read(ref _updates);
        public Task SecondUpdateStarted => _secondUpdate.Task;

        public string Id => "stub";
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => interval;
        public void Initialize() { }

        public void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("group");
            container.Entries.Add(_sensor);
            containers.Add(container);
        }

        public void Update() => Step();

        public Task UpdateAsync(CancellationToken cancellationToken)
        {
            Step();
            return Task.CompletedTask;
        }

        private void Step()
        {
            if (Interlocked.Increment(ref _updates) == 2)
                _secondUpdate.TrySetResult();
            if (Failing)
                throw new InvalidOperationException("the device is gone");
            _sensor.Value += 1;
        }

        public void Close() { }
    }
}
