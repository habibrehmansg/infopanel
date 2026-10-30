using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The engine's side of a plugin host's life, as 1.4.x ran it: connect, initialise and loaded waits; an unexpected exit
// restarted after 2 s, at most three times in 60 s, then Failed with the reason; an intentional stop never restarted; a
// stop bounded, then a kill. Each test runs a host runner over fake hosts (FakePluginHosts) on real pipes, with a clock
// that moves only when the test moves it.
public sealed class PluginHostManagerTests : IDisposable
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginSensorSource _source = new();
    private readonly string _root = PluginFixtures.NewTempRoot("ip-hmgr-");
    private TimeSpan _now;

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task A_host_that_doesnt_connect_within_30_seconds_is_killed_and_restarted()
    {
        var launcher = new FakeLauncher(() => new FakeHost { NeverConnects = true });
        await using var runner = Runner(launcher);

        _ = runner.StartAsync();
        await Armed(TimeSpan.FromSeconds(30));
        await Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        Assert.False(launcher.Started[0].Killed);
        await Advance(TimeSpan.FromMilliseconds(1));

        await Eventually.Until(() => launcher.Started[0].Killed, "the host that didn't connect is killed");
        Assert.Equal(PluginHostState.Restarting, runner.State);
        Assert.Contains("connecting", runner.Errors[^1]);
    }

    [Fact]
    public async Task A_host_that_doesnt_initialise_within_30_seconds_is_killed()
    {
        var launcher = new FakeLauncher(() => new FakeHost { InitializeHangs = true });
        await using var runner = Runner(launcher);

        _ = runner.StartAsync();
        await Connected(launcher, 0);
        // The connect and initialise deadlines are both due at 30 s on this clock, and the host's side of the pipe
        // connects before the engine's connect deadline is gone. Once the engine has taken a call from the host it listens,
        // so its connect deadline is disposed; the one timer then due at 30 s is the initialise deadline.
        await launcher.Started[0].Host.SendErrorAsync("listening");
        await Eventually.Until(() => runner.Errors.Contains("fake: listening"), "the engine takes the host's calls");
        await Eventually.Until(() => _time.TimersDueAt(TimeSpan.FromSeconds(30)) == 1, "the initialise deadline is armed");
        await Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        Assert.False(launcher.Started[0].Killed);
        await Advance(TimeSpan.FromMilliseconds(1));

        await Eventually.Until(() => launcher.Started[0].Killed, "the host that didn't initialise is killed");
        Assert.Contains("initialising", runner.Errors[^1]);
    }

    [Fact]
    public async Task Without_its_loaded_list_within_10_seconds_the_host_runs_on_without_entries()
    {
        var launcher = new FakeLauncher(() => new FakeHost { NoLoaded = true });
        await using var runner = Runner(launcher);

        _ = runner.StartAsync();
        await Connected(launcher, 0);
        await Armed(TimeSpan.FromSeconds(10));
        await Advance(TimeSpan.FromSeconds(10));

        await Eventually.Until(() => runner.State == PluginHostState.Running, "the host runs");
        Assert.False(launcher.Started[0].Killed);
        Assert.Empty(_source.GetSensors());
    }

    [Fact]
    public async Task An_unexpected_exit_restarts_the_host_after_2_seconds_three_times_then_the_package_fails_with_the_reason()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);
        Assert.Equal(SensorStatus.Live, _source.GetStatus(Key));

        for (var restart = 1; restart <= 3; restart++)
        {
            launcher.Started[^1].Exit(1, $"crash {restart}");
            await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the host is restarting");
            // Its sensors keep their last values, stale, while it restarts.
            Assert.Equal(SensorStatus.Stale, _source.GetStatus(Key));
            await Armed(_now + TimeSpan.FromSeconds(2));
            await Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
            Assert.Equal(restart, launcher.Started.Count);
            await Advance(TimeSpan.FromMilliseconds(1));
            await Eventually.Until(() => launcher.Started.Count == restart + 1, $"restart {restart} starts a host");
            await Running(runner);
        }

        launcher.Started[^1].Exit(1, "crash 4");
        await Eventually.Until(() => runner.State == PluginHostState.Failed, "the package fails");
        Assert.Contains("crash 4", runner.Reason);
        Assert.Equal(SensorStatus.Unavailable, _source.GetStatus(Key));
        await Advance(TimeSpan.FromSeconds(10));
        await Eventually.Stays(() => launcher.Started.Count == 4, TimeSpan.FromMilliseconds(200), "no fifth host starts");
    }

    [Fact]
    public async Task The_restart_window_resets_60_seconds_after_its_first_failure()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);

        for (var restart = 1; restart <= 3; restart++)
        {
            launcher.Started[^1].Exit(1);
            await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the host is restarting");
            await Armed(_now + TimeSpan.FromSeconds(2));
            await Advance(TimeSpan.FromSeconds(2));
            await Eventually.Until(() => launcher.Started.Count == restart + 1, $"restart {restart} starts a host");
            await Running(runner);
        }

        // Past 60 s from the first failure: the next failure opens a new window.
        await Advance(TimeSpan.FromSeconds(60));
        launcher.Started[^1].Exit(1);
        await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the host is restarting again");
        await Armed(_now + TimeSpan.FromSeconds(2));
        await Advance(TimeSpan.FromSeconds(2));
        await Eventually.Until(() => launcher.Started.Count == 5, "a fifth host starts");
    }

    [Fact]
    public async Task An_intentional_stop_never_restarts_the_host()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);

        await runner.StopAsync(TimeSpan.FromSeconds(5));
        await Advance(TimeSpan.FromSeconds(10));

        await Eventually.Stays(() => runner.State == PluginHostState.Stopped && launcher.Started.Count == 1,
            TimeSpan.FromMilliseconds(300), "the stopped host stays stopped");
        Assert.Equal(1, launcher.Started[0].ShutdownsAsked);
        Assert.Equal(SensorStatus.Unavailable, _source.GetStatus(Key));
        // Its exit was no failure: nothing was counted against the restart policy or scheduled.
        Assert.Empty(runner.Errors);
    }

    [Fact]
    public async Task A_stop_gives_the_host_5_seconds_to_shut_down_then_kills_it()
    {
        var launcher = new FakeLauncher(() => new FakeHost { IgnoresShutdown = true });
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);

        var stop = runner.StopAsync(PluginHostTimeouts.Default.Stop);
        await Eventually.Until(() => launcher.Started[0].ShutdownsAsked == 1, "the host is asked to shut down");
        await Armed(_now + TimeSpan.FromSeconds(5));
        await Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
        Assert.False(launcher.Started[0].Killed);
        await Advance(TimeSpan.FromMilliseconds(1));

        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(launcher.Started[0].Killed);
        Assert.Equal(PluginHostState.Stopped, runner.State);
    }

    [Fact]
    public async Task When_the_engine_stops_every_host_gets_2_seconds_in_all_then_the_rest_are_killed()
    {
        var bundled = Path.Combine(_root, "bundled");
        foreach (var name in new[] { "InfoPanel.A", "InfoPanel.B" })
        {
            Directory.CreateDirectory(Path.Combine(bundled, name));
            File.WriteAllText(Path.Combine(bundled, name, name + ".dll"), "");
        }
        var hosts = 0;
        var launcher = new FakeLauncher(() => new FakeHost { IgnoresShutdown = true, PluginId = "p" + Interlocked.Increment(ref hosts) });
        await using var manager = new PluginHostManager(Options(bundled), launcher, _source, _time, NullLoggerFactory.Instance);

        _ = manager.StartAllAsync();
        await Eventually.Until(() => manager.Runners.Count == 2 && manager.Runners.All(r => r.State == PluginHostState.Running), "both hosts run");

        var stop = manager.StopAllAsync();
        await Eventually.Until(() => launcher.Started.All(p => p.ShutdownsAsked == 1), "both hosts are asked to shut down");
        await Eventually.Until(() => _time.TimersDueAt(_now + TimeSpan.FromSeconds(2)) == 2, "both shutdown deadlines are armed");
        await Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        Assert.DoesNotContain(launcher.Started, p => p.Killed);
        await Advance(TimeSpan.FromMilliseconds(1));

        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(launcher.Started, p => Assert.True(p.Killed));
    }

    [Fact]
    public async Task A_stop_that_comes_before_the_start_latches_so_no_host_starts_after_it()
    {
        var bundled = Path.Combine(_root, "bundled");
        foreach (var name in new[] { "InfoPanel.A", "InfoPanel.B" })
        {
            Directory.CreateDirectory(Path.Combine(bundled, name));
            File.WriteAllText(Path.Combine(bundled, name, name + ".dll"), "");
        }
        var hosts = 0;
        var launcher = new FakeLauncher(() => new FakeHost { PluginId = "p" + Interlocked.Increment(ref hosts) });
        await using var manager = new PluginHostManager(Options(bundled), launcher, _source, _time, NullLoggerFactory.Instance);

        // The engine stops before the start it queued has run.
        await manager.StopAllAsync();
        await manager.StartAllAsync();

        await Eventually.Stays(() => launcher.Started.Count == 0, TimeSpan.FromMilliseconds(300), "no host starts once the engine is stopping");
        Assert.All(manager.Runners, r => Assert.Equal(PluginHostState.Stopped, r.State));
    }

    [Fact]
    public async Task A_folder_the_host_would_write_in_that_the_check_refuses_fails_the_package_without_starting_a_host()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        var package = new PluginPackage("InfoPanel.Fake", _root, Path.Combine(_root, "InfoPanel.Fake.dll"), Bundled: true);
        var settings = Options(_root).Host with
        {
            WriteCheck = path => path.EndsWith("InfoPanel.Fake", StringComparison.Ordinal) ? "it is in the 1.x folder" : null,
        };
        await using var runner = new PluginHostRunner(package, settings, launcher, _source, _time, NullLogger.Instance);

        _ = runner.StartAsync();

        await Eventually.Until(() => runner.State == PluginHostState.Failed, "the package fails");
        Assert.Contains("it is in the 1.x folder", runner.Reason);
        await Advance(TimeSpan.FromSeconds(10));
        await Eventually.Stays(() => launcher.Started.Count == 0, TimeSpan.FromMilliseconds(200), "no host starts, then or later");
    }

    [Fact]
    public async Task A_log_folder_or_log_file_that_is_a_link_fails_the_package_without_starting_a_host()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var package = new PluginPackage("InfoPanel.Fake", _root, Path.Combine(_root, "InfoPanel.Fake.dll"), Bundled: true);

        // The log folder itself a link.
        var linkedLogs = Path.Combine(_root, "linked-logs");
        Directory.CreateSymbolicLink(linkedLogs, elsewhere);
        var launcher = new FakeLauncher(() => new FakeHost());
        await using (var runner = new PluginHostRunner(package, Options(_root).Host with { LogsDirectory = linkedLogs }, launcher, _source, _time, NullLogger.Instance))
        {
            _ = runner.StartAsync();
            await Eventually.Until(() => runner.State == PluginHostState.Failed, "the package fails on its linked log folder");
            Assert.Contains("is a link", runner.Reason);
        }

        // One of its log files a link (the host's log appends to it, through the link).
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        File.CreateSymbolicLink(Path.Combine(logs, "InfoPanel.Fake-20261031.log"), Path.Combine(elsewhere, "target.log"));
        await using (var runner = new PluginHostRunner(package, Options(_root).Host, launcher, _source, _time, NullLogger.Instance))
        {
            _ = runner.StartAsync();
            await Eventually.Until(() => runner.State == PluginHostState.Failed, "the package fails on its linked log file");
            Assert.Contains("is a link", runner.Reason);
        }

        Assert.Empty(launcher.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task A_message_over_16_MiB_closes_the_hosts_connection()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);

        // Its write may never finish: the engine stops reading and kills it.
        _ = launcher.Started[0].Host.SendHugeErrorAsync(17 * 1024 * 1024).ContinueWith(t => t.Exception, TaskScheduler.Default);

        await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the host's connection is closed and it restarts");
        Assert.True(launcher.Started[0].Killed);
        Assert.Contains("over the 16777216", runner.Errors[^1]);
    }

    [Fact]
    public async Task A_host_with_more_messages_waiting_than_the_engine_allows_is_disconnected()
    {
        var logger = new HoldingLogger("hold-the-engine");
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher, PluginLimits.Default with { PendingMessages = 8 }, logger);
        try
        {
            _ = runner.StartAsync();
            await Running(runner);
            var host = launcher.Started[0].Host;

            // The engine is busy with one message (its log holds it) while the host goes on sending valid ones.
            await host.SendErrorAsync("hold-the-engine");
            await logger.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            _ = Flood(host, count: 100, chars: 10);

            await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the flooding host's connection is closed and it restarts");
            Assert.True(launcher.Started[0].Killed);
            Assert.Contains(runner.Errors, e => e.Contains("more than 8 messages"));
        }
        finally
        {
            logger.Release.Set();
        }
    }

    [Fact]
    public async Task A_host_with_more_bytes_waiting_than_the_engine_allows_is_disconnected()
    {
        var logger = new HoldingLogger("hold-the-engine");
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher, PluginLimits.Default with { PendingBytes = 64 * 1024 }, logger);
        try
        {
            _ = runner.StartAsync();
            await Running(runner);
            var host = launcher.Started[0].Host;

            await host.SendErrorAsync("hold-the-engine");
            await logger.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            _ = Flood(host, count: 10, chars: 16 * 1024);

            await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the flooding host's connection is closed and it restarts");
            Assert.Contains(runner.Errors, e => e.Contains("more than 65536 bytes"));
        }
        finally
        {
            logger.Release.Set();
        }
    }

    [Fact]
    public async Task Messages_the_engine_has_handled_dont_count_against_the_budget()
    {
        var logger = new HoldingLogger("hold-the-engine");
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher, PluginLimits.Default with { PendingMessages = 8 }, logger);
        try
        {
            _ = runner.StartAsync();
            await Running(runner);
            var host = launcher.Started[0].Host;

            // Many more than 8 in all, but each handled before the next is sent.
            for (var i = 0; i < 40; i++)
            {
                await host.SendErrorAsync($"error {i}");
                await Eventually.Until(() => runner.Errors.Any(e => e.EndsWith($"error {i}", StringComparison.Ordinal)), $"error {i} is handled");
            }
            Assert.Equal(PluginHostState.Running, runner.State);

            // The budget still holds after them: 9 waiting while the engine is busy close the connection.
            await host.SendErrorAsync("hold-the-engine");
            await logger.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            _ = Flood(host, count: 100, chars: 10);
            await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the flooding host's connection is closed and it restarts");
            Assert.Contains(runner.Errors, e => e.Contains("more than 8 messages"));
        }
        finally
        {
            logger.Release.Set();
        }
    }

    [Fact]
    public async Task A_host_that_asks_for_a_reply_is_disconnected_and_never_answered()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);
        var host = launcher.Started[0].Host;

        // Paced calls with ids: each would leave a reply for the host to read. The channel takes notifications only, so
        // the first closes the connection and no reply is ever written.
        var asked = new List<Task>();
        for (var i = 0; i < 50 && runner.State == PluginHostState.Running; i++)
        {
            var call = host.AskAsync(nameof(IPluginClientCallback.OnPerformanceUpdate), new List<PluginPerformanceDto>());
            _ = call.ContinueWith(t => t.Exception, TaskScheduler.Default);
            asked.Add(call);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Eventually.Until(() => runner.State == PluginHostState.Restarting, "the host's connection is closed and it restarts");
        Assert.True(launcher.Started[0].Killed);
        Assert.Contains(runner.Errors, e => e.Contains("asked for a reply"));
        await Eventually.Until(() => asked.All(t => t.IsCompleted), "every call ends with the connection");
        Assert.DoesNotContain(asked, t => t.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Calls_the_engine_doesnt_take_are_ignored_and_never_count_against_the_budget()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        await using var runner = Runner(launcher);
        _ = runner.StartAsync();
        await Running(runner);
        var host = launcher.Started[0].Host;

        // One more than the budget's 1,024, one at a time: an unknown method, a missing argument, an argument too many,
        // and an argument of the wrong type. None reaches a callback.
        for (var i = 0; i < PluginLimits.Default.PendingMessages + 1; i++)
        {
            await ((i % 4) switch
            {
                0 => host.NotifyAsync("OnSomethingElse", i),
                1 => host.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), host.PluginId),
                2 => host.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), host.PluginId, "extra", i),
                _ => host.NotifyAsync(nameof(IPluginClientCallback.OnSensorUpdate), "not a list"),
            });
        }

        // Calls it takes still are, by position and by name.
        await host.SendErrorAsync("after them");
        await host.NotifyNamedAsync(nameof(IPluginClientCallback.OnPluginError), new { pluginId = host.PluginId, errorMessage = "by name" });
        await Eventually.Until(() => runner.Errors.Any(e => e.EndsWith("by name", StringComparison.Ordinal)), "the call by name is handled");
        Assert.Contains(runner.Errors, e => e.EndsWith("after them", StringComparison.Ordinal));
        Assert.Equal(PluginHostState.Running, runner.State);
        Assert.Single(launcher.Started);
        // Said once, not once a call.
        Assert.Single(runner.Errors, e => e.Contains("doesn't take"));
    }

    /// <summary>Sends <paramref name="count"/> plugin errors as fast as the pipe takes them; stops when the connection ends.</summary>
    private static Task Flood(FakeHost host, int count, int chars) => Task.Run(async () =>
    {
        for (var i = 0; i < count; i++)
            await host.SendErrorAsync(new string('f', chars));
    }).ContinueWith(t => t.Exception, TaskScheduler.Default);

    /// <summary>A log whose write of a message holding <c>marker</c> blocks until released: the engine stays busy with it.</summary>
    private sealed class HoldingLogger(string marker) : ILogger
    {
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains(marker, StringComparison.Ordinal))
                return;
            Holding.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void The_pipe_name_keeps_the_socket_path_under_macOS_s_104_bytes()
    {
        var name = PluginHostConnection.NewPipeName();

        Assert.Matches("^ipph-[0-9a-f]{16}$", name);
        // .NET puts a Unix pipe at {temp}/CoreFxPipe_{name}; macOS's longest per-user temp folder is about 50 bytes.
        Assert.True(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name).Length < 104);
        Assert.True(Path.Combine("/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/", "CoreFxPipe_" + name).Length < 104);
    }

    private static readonly SensorKey Key = SensorKey.Plugin("/fake/c/v");

    private PluginHostRunner Runner(FakeLauncher launcher) => Runner(launcher, PluginLimits.Default, NullLogger.Instance);

    private PluginHostRunner Runner(FakeLauncher launcher, PluginLimits limits, ILogger logger)
    {
        var package = new PluginPackage("InfoPanel.Fake", _root, Path.Combine(_root, "InfoPanel.Fake.dll"), Bundled: true);
        return new PluginHostRunner(package, Options(_root).Host with { Limits = limits }, launcher, _source, _time, logger);
    }

    private PluginOptions Options(string bundled) => new()
    {
        BundledDirectory = bundled,
        InstalledDirectory = Path.Combine(_root, "installed"),
        Host = new PluginHostSettings
        {
            SettingsDirectory = Path.Combine(_root, "settings"),
            LogsDirectory = Path.Combine(_root, "logs"),
        },
    };

    private async Task Running(PluginHostRunner runner) =>
        await Eventually.Until(() => runner.State == PluginHostState.Running, "the host runs");

    private static async Task Connected(FakeLauncher launcher, int index)
    {
        await Eventually.Until(() => launcher.Started.Count > index, "the host starts");
        await launcher.Started[index].Connected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Waits until a timer due <paramref name="at"/> after the clock's start is armed.</summary>
    private Task Armed(TimeSpan at) => Eventually.Until(() => _time.HasTimerDueAt(at), $"a timer due at {at} is armed");

    /// <summary>Moves the clock by <paramref name="by"/>.</summary>
    private async Task Advance(TimeSpan by)
    {
        _time.Advance(by);
        _now += by;
        await Task.Yield();
    }
}
