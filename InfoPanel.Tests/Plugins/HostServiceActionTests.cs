using InfoPanel.Plugins;
using InfoPanel.Plugins.Host;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Tests.Desktop;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// A plugin's actions in its host: each action's state as the plugin describes it (IPluginActionStates), cut to its limits,
// or enabled with no state or confirmation for a plugin that doesn't (as every 1.x plugin); an action its state disables
// isn't run; one that runs does so on a task of its own and reads running until it ends; a state that throws or doesn't
// come within 5 seconds is disabled, reported once, and its action never runs; only a method listed as an action runs.
// Each test drives a HostService holding one initialised plugin, connected through a transport that records its errors.
public sealed class HostServiceActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_actions_state_is_as_its_plugin_describes_it_cut_to_its_limits()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);

        var connect = await host.DescribeActionAsync("stub", nameof(StatesPlugin.Connect), Ct);
        Assert.True(connect.Enabled);
        Assert.Equal(new string('s', 256), connect.State);
        // An empty title is the action's name; the text is kept as given, cut at 4,096 characters.
        Assert.Equal("Connect it", connect.Confirm!.Title);
        Assert.Equal(("line one\n\nline two" + new string('t', 5000))[..4096], connect.Confirm.Text);
        Assert.False(connect.Running);

        var off = await host.DescribeActionAsync("stub", nameof(StatesPlugin.Off), Ct);
        Assert.Equal((false, "Not possible here", null), (off.Enabled, off.State, off.Confirm));

        // Null, and an empty state line: enabled, no state, no confirmation.
        foreach (var method in new[] { nameof(StatesPlugin.Plain), nameof(StatesPlugin.Blank) })
        {
            var plain = await host.DescribeActionAsync("stub", method, Ct);
            Assert.Equal((true, null, null, false), (plain.Enabled, plain.State, plain.Confirm, plain.Running));
        }
    }

    [Fact]
    public async Task An_action_its_state_disables_isnt_run_and_one_enabled_runs_on_its_own_task_reading_running_until_it_ends()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);

        var refused = await host.InvokeActionAsync("stub", nameof(StatesPlugin.Off), Ct);
        Assert.Equal((false, false, "Not possible here"), (refused.Enabled, refused.Running, refused.State));

        // Hold runs until the test lets it go: the call that started it has answered, and its state reads running.
        var started = await host.InvokeActionAsync("stub", nameof(StatesPlugin.Hold), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(started is { Enabled: true, Running: true });
        await plugin.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Hold), Ct)).Running);
        Assert.False((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Plain), Ct)).Running);

        plugin.Release.SetResult();
        var deadline = DateTime.UtcNow + Eventually.Limit;
        while ((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Hold), Ct)).Running)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting until Hold has ended");
            await Task.Delay(10, Ct);
        }
        Assert.Equal([nameof(StatesPlugin.Hold)], plugin.Ran);
    }

    [Fact]
    public async Task A_plugin_without_action_states_has_every_action_enabled_with_no_state_or_confirmation_and_runs_it()
    {
        var plugin = new PlainPlugin();
        var (host, _) = await HostWith(plugin);

        var state = await host.DescribeActionAsync("plain", nameof(PlainPlugin.Mark), Ct);
        Assert.Equal((true, null, null, false), (state.Enabled, state.State, state.Confirm, state.Running));

        var run = await host.InvokeActionAsync("plain", nameof(PlainPlugin.Mark), Ct);
        Assert.True(run is { Enabled: true, Running: true, State: null, Confirm: null });
        await Eventually.Until(() => plugin.Marks == 1, "Mark has run");
    }

    [Fact]
    public async Task Only_a_method_the_plugin_lists_as_an_action_runs()
    {
        var plugin = new PlainPlugin();
        var (host, _) = await HostWith(plugin);

        foreach (var method in new[] { nameof(PlainPlugin.Unlisted), nameof(PlainPlugin.Close), "Nope" })
        {
            var refused = await host.InvokeActionAsync("plain", method, Ct);
            Assert.False(refused.Enabled);
            Assert.False((await host.DescribeActionAsync("plain", method, Ct)).Enabled);
        }
        Assert.False((await host.InvokeActionAsync("other", nameof(PlainPlugin.Mark), Ct)).Enabled);

        await Eventually.Stays(() => plugin.Marks == 0 && !plugin.UnlistedRan, TimeSpan.FromMilliseconds(200), "nothing ran");
    }

    [Fact]
    public async Task A_state_that_throws_is_disabled_reported_once_until_it_answers_and_its_action_isnt_run()
    {
        var plugin = new StatesPlugin();
        var (host, errors) = await HostWith(plugin);

        var failed = await host.InvokeActionAsync("stub", nameof(StatesPlugin.Throws), Ct);
        Assert.Equal((false, false), (failed.Enabled, failed.Running));
        Assert.Contains("the state is broken", failed.State);
        Assert.False((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Throws), Ct)).Enabled);
        var error = Assert.Single(errors.Errors);
        Assert.Equal("stub", error.PluginId);
        Assert.Contains("Throws", error.Message);

        // It answers once, then throws again: a new streak, reported again.
        plugin.ThrowsFixed = true;
        Assert.True((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Throws), Ct)).Enabled);
        plugin.ThrowsFixed = false;
        await host.DescribeActionAsync("stub", nameof(StatesPlugin.Throws), Ct);
        Assert.Equal(2, errors.Errors.Count);
        Assert.Empty(plugin.Ran);
    }

    [Fact]
    public async Task A_state_that_doesnt_come_within_5_seconds_is_disabled_and_its_action_never_runs()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin, time);

        var asking = host.InvokeActionAsync("stub", nameof(StatesPlugin.Hangs), Ct);
        await Eventually.Until(() => time.HasTimerDueAt(TimeSpan.FromSeconds(5)), "the host waits for the state");
        time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.False(asking.IsCompleted);
        time.Advance(TimeSpan.FromTicks(1));

        var state = await asking.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((false, false), (state.Enabled, state.Running));
        Assert.Contains("5 seconds", state.State);

        // The state comes late, enabled: the action still isn't run.
        plugin.HangsGate.SetResult();
        await Eventually.Stays(() => plugin.Ran.Count == 0, TimeSpan.FromMilliseconds(200), "the action isn't run");
    }

    [Fact]
    public async Task An_action_whose_call_is_cancelled_while_its_worker_is_delayed_is_answered_not_running_and_never_runs()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeActionStarts = async () =>
        {
            entered.TrySetResult();
            await delay.Task;
        };

        using var call = new CancellationTokenSource();
        var asking = host.InvokeActionAsync("stub", nameof(StatesPlugin.Plain), call.Token);
        await entered.Task.WaitAsync(Eventually.Limit, Ct);
        // Not answered before its worker has started it.
        Assert.False(asking.IsCompleted);

        // The engine's deadline passes (it cancels the call), then the worker goes on: the action isn't started.
        call.Cancel();
        delay.SetResult();
        var state = await asking.WaitAsync(Eventually.Limit, Ct);
        Assert.False(state.Running);
        await Eventually.Stays(() => plugin.Ran.Count == 0, TimeSpan.FromMilliseconds(200), "the action never runs");
        Assert.False((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Plain), Ct)).Running);
    }

    [Fact]
    public async Task An_action_whose_worker_is_delayed_past_the_start_limit_never_runs_though_its_call_isnt_cancelled()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin, time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeActionStarts = async () =>
        {
            entered.TrySetResult();
            await delay.Task;
        };

        // Its cancellation never reaches the host (a late or lost cancel): the host's own limit holds.
        var asking = host.InvokeActionAsync("stub", nameof(StatesPlugin.Plain), Ct);
        await entered.Task.WaitAsync(Eventually.Limit, Ct);
        time.Advance(HostService.StartLimit);
        delay.SetResult();
        var state = await asking.WaitAsync(Eventually.Limit, Ct);
        Assert.False(state.Running);
        await Eventually.Stays(() => plugin.Ran.Count == 0, TimeSpan.FromMilliseconds(200), "the action never runs");
    }

    [Fact]
    public async Task An_action_whose_state_comes_after_its_plugins_shutdown_began_never_starts()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);

        // The POST waits on the plugin's Describe while the package is turned off.
        var asking = host.InvokeActionAsync("stub", nameof(StatesPlugin.Gated), Ct);
        await plugin.GatedAsked.Task.WaitAsync(Eventually.Limit, Ct);
        var stopping = host.ShutdownAsync();
        await Eventually.Until(() => plugin.Closed, "the plugin is closing");

        // Its state comes, enabled, in time: the action still isn't started.
        plugin.GatedGate.SetResult();
        var state = await asking.WaitAsync(Eventually.Limit, Ct);
        Assert.False(state.Running);
        await stopping.WaitAsync(Eventually.Limit, Ct);
        await Eventually.Stays(() => plugin.Ran.Count == 0, TimeSpan.FromMilliseconds(200), "the action never runs");
    }

    [Fact]
    public async Task Shutdown_waits_for_the_actions_under_way_before_it_closes_the_plugins()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin, time);

        Assert.True((await host.InvokeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).Running);
        var stopping = host.ShutdownAsync();
        await Eventually.Until(() => time.HasTimerDueAt(HostService.ActionStopLimit), "shutdown waits for the action");
        Assert.False(plugin.Closed);

        // The action ends: the plugin is closed and shutdown ends, the clock never moved.
        plugin.ReleaseOneHold();
        await stopping.WaitAsync(Eventually.Limit, Ct);
        Assert.True(plugin.Closed);
        Assert.Equal(1, plugin.HoldsEnded);
    }

    [Fact]
    public async Task Shutdown_waits_for_an_action_under_way_only_up_to_its_limit()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin, time);

        Assert.True((await host.InvokeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).Running);
        var stopping = host.ShutdownAsync();
        await Eventually.Until(() => time.HasTimerDueAt(HostService.ActionStopLimit), "shutdown waits for the action");
        time.Advance(HostService.ActionStopLimit - TimeSpan.FromTicks(1));
        Assert.False(plugin.Closed);
        time.Advance(TimeSpan.FromTicks(1));

        await stopping.WaitAsync(Eventually.Limit, Ct);
        Assert.True(plugin.Closed);
        Assert.Equal(0, plugin.HoldsEnded);
        plugin.ReleaseOneHold();
    }

    [Fact]
    public async Task A_state_that_doesnt_come_is_asked_once_however_often_its_action_is_asked_about_until_it_ends()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var plugin = new StatesPlugin();
        var (host, errors) = await HostWith(plugin, time);

        // Three requests, each answered at 5 seconds as disabled, while the first Describe still hangs.
        for (var i = 1; i <= 3; i++)
        {
            var asking = i == 2
                ? host.InvokeActionAsync("stub", nameof(StatesPlugin.Hangs), Ct)
                : host.DescribeActionAsync("stub", nameof(StatesPlugin.Hangs), Ct);
            var at = HostService.DescribeLimit * i;
            await Eventually.Until(() => time.HasTimerDueAt(at), "the host waits for the state");
            time.Advance(HostService.DescribeLimit);
            var state = await asking.WaitAsync(Eventually.Limit, Ct);
            Assert.Equal((false, false), (state.Enabled, state.Running));
        }
        await Eventually.Stays(() => plugin.Described(nameof(StatesPlugin.Hangs)) == 1, TimeSpan.FromMilliseconds(200), "its state is asked once");
        Assert.Single(errors.Errors);

        // It ends: the next request has its answer.
        plugin.HangsGate.SetResult();
        Assert.True((await host.DescribeActionAsync("stub", nameof(StatesPlugin.Hangs), Ct).WaitAsync(Eventually.Limit, Ct)).Enabled);
        Assert.Empty(plugin.Ran);
    }

    [Fact]
    public async Task An_action_that_returns_a_task_reads_running_until_its_task_completes()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);

        Assert.True((await host.InvokeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).Running);
        await Eventually.Until(() => plugin.Ran.Contains(nameof(StatesPlugin.HoldAsync)), "HoldAsync has started");
        // Its method has returned its task, which hasn't completed: still running.
        var end = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
        while (DateTime.UtcNow < end)
        {
            Assert.True((await host.DescribeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).Running, "running while its task waits");
            await Task.Delay(10, Ct);
        }

        plugin.ReleaseOneHold();
        await UntilNotRunning(host, nameof(StatesPlugin.HoldAsync));
        Assert.Equal(1, plugin.HoldsEnded);
    }

    [Fact]
    public async Task Two_simultaneous_runs_of_one_action_both_run_and_it_reads_running_until_the_last_ends()
    {
        var plugin = new StatesPlugin();
        var (host, _) = await HostWith(plugin);

        var both = await Task.WhenAll(
            host.InvokeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct),
            host.InvokeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).WaitAsync(Eventually.Limit, Ct);
        Assert.All(both, state => Assert.True(state is { Enabled: true, Running: true }));
        await Eventually.Until(() => plugin.Ran.Count == 2, "both runs have started");

        plugin.ReleaseOneHold();
        await Eventually.Until(() => plugin.HoldsEnded == 1, "one run has ended");
        Assert.True((await host.DescribeActionAsync("stub", nameof(StatesPlugin.HoldAsync), Ct)).Running);

        plugin.ReleaseOneHold();
        await UntilNotRunning(host, nameof(StatesPlugin.HoldAsync));
        Assert.Equal(2, plugin.HoldsEnded);
        Assert.Equal([nameof(StatesPlugin.HoldAsync), nameof(StatesPlugin.HoldAsync)], plugin.Ran);
    }

    private static async Task UntilNotRunning(HostService host, string method)
    {
        var deadline = DateTime.UtcNow + Eventually.Limit;
        while ((await host.DescribeActionAsync("stub", method, Ct)).Running)
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting until {method} has ended");
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<(HostService Host, ErrorTransport Errors)> HostWith(IPlugin plugin, TimeProvider? time = null)
    {
        var host = new HostService("plugin.dll", null, null, time ?? TimeProvider.System);
        var wrapper = new PluginWrapper(new PluginDescriptor("plugin.dll", null), plugin);
        await wrapper.Initialize();
        host.AddInitializedWrapper(wrapper);

        var transport = new ErrorTransport();
        var jsonRpc = new JsonRpc(transport);
        jsonRpc.StartListening();
        host.SetJsonRpc(jsonRpc);
        return (host, transport);
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

    /// <summary>A plugin with no entries whose actions describe their states.</summary>
    private sealed class StatesPlugin : IPlugin, IPluginActionStates
    {
        private readonly List<string> _ran = [];
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HangsGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Holding => _holding.Task;
        public volatile bool ThrowsFixed;

        public List<string> Ran
        {
            get { lock (_ran) return [.. _ran]; }
        }

        private readonly Dictionary<string, int> _described = [];
        private readonly SemaphoreSlim _holdReleases = new(0);
        private int _holdsEnded;

        public TaskCompletionSource GatedAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GatedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Closed;

        /// <summary>How many times <paramref name="method"/>'s state was asked.</summary>
        public int Described(string method)
        {
            lock (_described)
                return _described.GetValueOrDefault(method);
        }

        /// <summary>The runs of <see cref="HoldAsync"/> that have ended.</summary>
        public int HoldsEnded => Volatile.Read(ref _holdsEnded);

        /// <summary>Lets one run of <see cref="HoldAsync"/> end (one waiting now, or the next).</summary>
        public void ReleaseOneHold() => _holdReleases.Release();

        public PluginActionState? Describe(string method)
        {
            lock (_described)
                _described[method] = _described.GetValueOrDefault(method) + 1;
            return DescribeOf(method);
        }

        private PluginActionState? DescribeOf(string method) => method switch
        {
            nameof(Gated) => GatedAsked.TrySetResult() && GatedGate.Task.Wait(TimeSpan.FromSeconds(30)) ? new PluginActionState() : null,
            nameof(Connect) => new PluginActionState
            {
                State = new string('s', 300),
                Confirm = new PluginActionConfirmation { Title = "", Text = "line one\n\nline two" + new string('t', 5000) },
            },
            nameof(Off) => new PluginActionState { Enabled = false, State = "Not possible here" },
            nameof(Blank) => new PluginActionState { State = "" },
            nameof(Throws) when !ThrowsFixed => throw new InvalidOperationException("the state is broken"),
            nameof(Hangs) => HangsGate.Task.Wait(TimeSpan.FromSeconds(30)) ? new PluginActionState() : null,
            _ => null,
        };

        private void Did(string method)
        {
            lock (_ran)
                _ran.Add(method);
        }

        [PluginAction("Connect it")]
        public void Connect() => Did(nameof(Connect));

        [PluginAction("Off")]
        public void Off() => Did(nameof(Off));

        [PluginAction("Plain")]
        public void Plain() => Did(nameof(Plain));

        [PluginAction("Blank")]
        public void Blank() => Did(nameof(Blank));

        [PluginAction("Throws")]
        public void Throws() => Did(nameof(Throws));

        [PluginAction("Hangs")]
        public void Hangs() => Did(nameof(Hangs));

        [PluginAction("Hold")]
        public void Hold()
        {
            Did(nameof(Hold));
            _holding.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(30));
        }

        [PluginAction("Gated")]
        public void Gated() => Did(nameof(Gated));

        /// <summary>Returns a task that completes once <see cref="ReleaseOneHold"/> lets it (at most 30 s).</summary>
        [PluginAction("Hold until released")]
        public async Task HoldAsync()
        {
            Did(nameof(HoldAsync));
            await _holdReleases.WaitAsync(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _holdsEnded);
        }

        public string Id => "stub";
        public string Name => "Stub";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => TimeSpan.Zero;
        public void Initialize() { }
        public void Load(List<IPluginContainer> containers) { }
        public void Update() { }
        public Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Close() => Closed = true;
    }

    /// <summary>A plugin without action states, as every 1.x plugin: an action, and a public method that isn't one.</summary>
    private sealed class PlainPlugin : IPlugin
    {
        private int _marks;

        public int Marks => Volatile.Read(ref _marks);
        public volatile bool UnlistedRan;

        [PluginAction("Mark")]
        public void Mark() => Interlocked.Increment(ref _marks);

        public void Unlisted() => UnlistedRan = true;

        public string Id => "plain";
        public string Name => "Plain";
        public string Description => "";
        public string? ConfigFilePath => null;
        public TimeSpan UpdateInterval => TimeSpan.Zero;
        public void Initialize() { }
        public void Load(List<IPluginContainer> containers) { }
        public void Update() { }
        public Task UpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Close() { }
    }
}
