using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Sensors;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The manager's packages as a client sees them: the user's on and off kept in plugin-state.json, PluginInfo.ini and its
// platforms, reload, the one-id-one-package rule, errors, CPU and memory, and actions and settings through the host as it
// runs. Fake hosts (FakePluginHosts) on real pipes, a clock that moves only when the test moves it.
public sealed class PluginManagerTests : IDisposable
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero));
    private readonly PluginSensorSource _source = new();
    private readonly string _root = PluginFixtures.NewTempRoot("ip-pmgr-");
    private TimeSpan _now;

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    private string Bundled => Path.Combine(_root, "bundled");

    private string Installed => Path.Combine(_root, "data", "plugins");

    private string StateFile => Path.Combine(_root, "data", "plugin-state.json");

    // ---- plugin-state.json ----

    [Fact]
    public async Task An_off_package_isnt_started_at_the_next_start()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, """{"format":"infopanel.plugin-state/1","off":["infopanel.b"]}""");
        var launcher = Launcher();
        await using var manager = Manager(launcher);

        _ = manager.StartAllAsync();

        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Running(a);
        Assert.True(manager.IsOn(a));
        Assert.False(manager.IsOn(b));
        await Eventually.Stays(() => launcher.Started.Count == 1 && b.State == PluginHostState.Stopped, TimeSpan.FromMilliseconds(200), "the off package isn't started");
        Assert.EndsWith("InfoPanel.A.dll", launcher.Started[0].Command.PluginDll);
    }

    [Fact]
    public async Task A_crash_and_a_give_up_never_turn_a_package_off()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using (var manager = Manager(launcher))
        {
            _ = manager.StartAllAsync();
            var a = manager.Find("InfoPanel.A")!;
            await Running(a);

            for (var restart = 1; restart <= 3; restart++)
            {
                launcher.Started[^1].Exit(1, $"crash {restart}");
                await Eventually.Until(() => a.State == PluginHostState.Restarting, "the host is restarting");
                await Armed(_now + TimeSpan.FromSeconds(2));
                await Advance(TimeSpan.FromSeconds(2));
                await Eventually.Until(() => launcher.Started.Count == restart + 1, $"restart {restart} starts a host");
                await Running(a);
            }

            launcher.Started[^1].Exit(1, "crash 4");
            await Eventually.Until(() => a.State == PluginHostState.Failed, "the package is given up on");
            Assert.True(manager.IsOn(a));
            Assert.False(File.Exists(StateFile));
        }

        // Nor does the engine's stop with the package failed.
        Assert.False(File.Exists(StateFile));

        // The next start runs it again.
        var next = Launcher();
        await using var again = Manager(next);
        _ = again.StartAllAsync();
        await Running(again.Find("InfoPanel.A")!);
        Assert.False(File.Exists(StateFile));
    }

    // ---- PluginInfo.ini's platforms ----

    [Fact]
    public async Task A_package_that_doesnt_name_this_platform_is_unsupported_starts_no_host_and_is_never_turned_off()
    {
        Package(Bundled, "InfoPanel.A", "[PluginInfo]\nName=Windows only\nPlatforms=windows\n");
        Package(Bundled, "InfoPanel.B", "[PluginInfo]\nPlatforms=MacOS, linux\n");
        var launcher = Launcher(Hosts("a", "b"));
        await using (var manager = Manager(launcher, platform: "macos"))
        {
            _ = manager.StartAllAsync();

            var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
            await Running(b);
            Assert.False(manager.IsSupported(a));
            Assert.True(manager.IsSupported(b));
            Assert.True(manager.IsOn(a));
            await Eventually.Stays(() => launcher.Started.Count == 1 && a.State == PluginHostState.Stopped, TimeSpan.FromMilliseconds(200), "no host starts for the unsupported package");
            Assert.EndsWith("InfoPanel.B.dll", launcher.Started[0].Command.PluginDll);
        }

        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public async Task The_bundled_Extras_are_unsupported_off_Windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Extras run on Windows.");
        PluginFixtures.Copy("InfoPanel.Extras", Bundled);
        var launcher = Launcher();
        await using var manager = Manager(launcher, platform: PluginPlatforms.Current);

        _ = manager.StartAllAsync();

        var extras = Assert.Single(manager.Runners);
        Assert.False(manager.IsSupported(extras));
        await Eventually.Stays(() => launcher.Started.Count == 0, TimeSpan.FromMilliseconds(200), "no host starts for Extras");
    }

    // ---- one plugin id, one package ----

    [Fact]
    public async Task A_later_package_that_loads_an_id_first_loses_it_to_the_earlier_one_when_that_loads()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher(c => new FakeHost { PluginId = "dup", InitializeGate = c.PluginDll.Contains("InfoPanel.A.") ? gate.Task : null });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);

        // B loads first and takes the id.
        await Running(b);
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));
        var bHost = launcher.Started.Single(p => p.Command.PluginDll.Contains("InfoPanel.B."));

        // Then A, earlier in the list, loads it: A keeps it, B is stopped and fails.
        gate.SetResult();
        await Running(a);
        await Eventually.Until(() => b.State == PluginHostState.Failed, "the later package fails");
        Assert.Equal("duplicateId", b.ReasonCode);
        Assert.Contains("dup", b.Reason);
        Assert.Contains("InfoPanel.A", b.Reason);
        await Eventually.Until(() => bHost.HasExited, "the later package's host is stopped");
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));
        Assert.Equal(PluginHostState.Running, a.State);
        Assert.True(manager.IsOn(b));
        Assert.False(File.Exists(StateFile));
        // Not restarted.
        await Advance(TimeSpan.FromSeconds(10));
        await Eventually.Stays(() => launcher.Started.Count == 2, TimeSpan.FromMilliseconds(200), "no restart of the later package");
    }

    [Fact]
    public async Task A_later_package_that_loads_an_id_the_earlier_one_has_fails_and_reloading_it_tries_again()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher(c => new FakeHost { PluginId = "dup", InitializeGate = c.PluginDll.Contains("InfoPanel.B.") ? gate.Task : null });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Running(a);

        gate.SetResult();
        await Eventually.Until(() => b.State == PluginHostState.Failed, "the later package fails");
        Assert.Equal("duplicateId", b.ReasonCode);
        Assert.Contains("InfoPanel.A", b.Reason);
        Assert.Equal(PluginHostState.Running, a.State);
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));
        Assert.Contains(b.ErrorLog, e => e.Plugin == null && e.Message.Contains("dup", StringComparison.Ordinal));

        // Reloading it tries again (and fails again while A has the id); with A off, it runs.
        Assert.Equal(PluginSwitch.Done, await manager.ReloadAsync("InfoPanel.B"));
        await Eventually.Until(() => launcher.Started.Count == 3 && b.State == PluginHostState.Failed, "the reload fails again");
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(PluginSwitch.Done, await manager.ReloadAsync("InfoPanel.B"));
        await Running(b);
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));

        // Turning A on again stops B the same way.
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: true));
        await Running(a);
        await Eventually.Until(() => b.State == PluginHostState.Failed && b.ReasonCode == "duplicateId", "the later package fails again");
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":[]}""", File.ReadAllText(StateFile));
    }

    // ---- on, off and reload ----

    [Fact]
    public async Task Turning_a_package_off_writes_it_then_stops_its_host_and_on_starts_it()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("infopanel.a", on: false));
        Assert.Equal(PluginHostState.Stopped, a.State);
        Assert.Equal(1, launcher.Started[0].ShutdownsAsked);
        Assert.False(manager.IsOn(a));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""", File.ReadAllText(StateFile));
        Assert.Equal(SensorStatus.Unavailable, _source.GetStatus(Key));
        Assert.Equal(PluginSwitch.NotRunning, await manager.ReloadAsync("InfoPanel.A"));

        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: true));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":[]}""", File.ReadAllText(StateFile));
        await Running(a);
        Assert.Equal(2, launcher.Started.Count);
        Assert.Equal(PluginSwitch.NotFound, await manager.SetOnAsync("InfoPanel.None", on: true));
    }

    [Fact]
    public async Task A_choice_that_cant_be_written_changes_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A read-only folder is a Unix test.");
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);
        var data = Path.GetDirectoryName(StateFile)!;
        Directory.CreateDirectory(data);
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(PluginSwitch.WriteFailed, await manager.SetOnAsync("InfoPanel.A", on: false));
        }
        finally
        {
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(PluginHostState.Running, a.State);
        Assert.True(manager.IsOn(a));
        Assert.Equal(0, launcher.Started[0].ShutdownsAsked);
    }

    [Fact]
    public async Task A_choice_written_whose_folder_couldnt_be_flushed_is_in_effect_and_the_next_change_writes_it_again()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        var (fail, writes) = (true, 0);
        var options = Options("macos") with
        {
            StateWriter = (path, bytes, replaced) =>
            {
                writes++;
                InfoPanel.Documents.AtomicFile.Write(path, bytes, null, replaced);
                if (fail)
                    throw new IOException("the folder couldn't be flushed");
            },
        };
        await using var manager = new PluginHostManager(options, launcher, _source, _time, NullLoggerFactory.Instance);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        // The file was replaced, only flushing its folder failed: the disk says off, and so does the engine.
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""", File.ReadAllText(StateFile));
        Assert.False(manager.IsOn(a));
        Assert.Equal(PluginHostState.Stopped, a.State);

        // The same choice again writes it again, so the flush is retried.
        fail = false;
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(2, writes);
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(2, writes);

        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: true));
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":[]}""", File.ReadAllText(StateFile));
        await Running(a);
    }

    [Fact]
    public async Task A_choice_written_whose_folder_couldnt_be_flushed_nor_the_file_read_back_is_in_effect()
    {
        if (OperatingSystem.IsWindows())
            return; // The unreadable file is made with a Unix mode.
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        var (fail, writes) = (true, 0);
        var options = Options("macos") with
        {
            StateWriter = (path, bytes, replaced) =>
            {
                writes++;
                InfoPanel.Documents.AtomicFile.Write(path, bytes, null, replaced);
                if (fail)
                {
                    // The file is replaced; then the storage fails: its folder isn't flushed and the file can't be read.
                    File.SetUnixFileMode(path, UnixFileMode.None);
                    throw new IOException("the folder couldn't be flushed");
                }
            },
        };
        await using var manager = new PluginHostManager(options, launcher, _source, _time, NullLoggerFactory.Instance);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        // The file was replaced: the disk says off, and so does the engine, though the file couldn't be read back.
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.False(manager.IsOn(a));
        Assert.Equal(PluginHostState.Stopped, a.State);
        File.SetUnixFileMode(StateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal("""{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""", File.ReadAllText(StateFile));

        // The same choice again writes it again, so the flush is retried.
        fail = false;
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(2, writes);
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task A_choice_whose_write_fails_before_the_file_is_replaced_changes_nothing()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        var options = Options("macos") with { StateWriter = (_, _, _) => throw new IOException("disk full") };
        await using var manager = new PluginHostManager(options, launcher, _source, _time, NullLoggerFactory.Instance);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        Assert.Equal(PluginSwitch.WriteFailed, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.True(manager.IsOn(a));
        Assert.Equal(PluginHostState.Running, a.State);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public async Task A_state_file_the_engine_cant_hold_runs_every_package_and_refuses_on_and_off()
    {
        Package(Bundled, "InfoPanel.A", "[PluginInfo]\nPlatforms=windows\n");
        Package(Bundled, "InfoPanel.B");
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, """{"format":"infopanel.plugin-state/9","off":["InfoPanel.B"]}""");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var b = manager.Find("InfoPanel.B")!;
        await Running(b);

        Assert.Equal(PluginSwitch.StateUnavailable, await manager.SetOnAsync("InfoPanel.B", on: false));
        Assert.Equal(PluginHostState.Running, b.State);
        Assert.Equal(PluginSwitch.Unsupported, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(PluginSwitch.Unsupported, await manager.ReloadAsync("InfoPanel.A"));
        Assert.Equal("""{"format":"infopanel.plugin-state/9","off":["InfoPanel.B"]}""", File.ReadAllText(StateFile));
    }

    [Fact]
    public async Task Reload_stops_the_host_and_starts_it_afresh_with_its_restarts_counted_anew()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);
        for (var restart = 1; restart <= 3; restart++)
        {
            launcher.Started[^1].Exit(1, $"crash {restart}");
            await Eventually.Until(() => a.State == PluginHostState.Restarting, "the host is restarting");
            await Armed(_now + TimeSpan.FromSeconds(2));
            await Advance(TimeSpan.FromSeconds(2));
            await Eventually.Until(() => launcher.Started.Count == restart + 1, $"restart {restart} starts a host");
            await Running(a);
        }

        Assert.Equal(PluginSwitch.Done, await manager.ReloadAsync("InfoPanel.A"));
        Assert.Equal(1, launcher.Started[3].ShutdownsAsked);
        await Running(a);
        Assert.Equal(5, launcher.Started.Count);

        // Three restarts in the same minute are allowed again.
        launcher.Started[^1].Exit(1, "crash after the reload");
        await Eventually.Until(() => a.State == PluginHostState.Restarting, "the reloaded host restarts rather than failing");
    }

    // ---- a package's lifecycle, one step at a time, bound to its host's generation ----

    [Fact]
    public async Task Shutdown_waits_for_a_reload_under_way_and_no_host_starts_after_it()
    {
        Package(Bundled, "InfoPanel.A");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = Launcher(() => new FakeHost { ShutdownGate = gate.Task });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        // The reload is under way, its host asked to shut down; then the engine stops.
        var reload = manager.ReloadAsync("InfoPanel.A");
        await Eventually.Until(() => launcher.Started[0].ShutdownsAsked == 1, "the reload asks the host to shut down");
        var stopping = manager.StopAllAsync();
        await Eventually.Stays(() => !stopping.IsCompleted, TimeSpan.FromMilliseconds(300), "shutdown waits for the reload under way");

        gate.SetResult();
        await reload.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(PluginHostState.Stopped, a.State);
        await Eventually.Stays(() => launcher.Started.Count == 1, TimeSpan.FromMilliseconds(200), "no host starts after shutdown");
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: true));
        await Eventually.Stays(() => launcher.Started.Count == 1, TimeSpan.FromMilliseconds(200), "turning it on after shutdown starts no host");
    }

    [Fact]
    public async Task A_start_that_comes_after_shutdown_stopped_a_package_starts_no_host()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        await manager.StopAllAsync();
        // A switch that found the engine running before shutdown latched starts the package only now.
        await a.StartAsync();
        await Eventually.Stays(() => launcher.Started.Count == 1 && a.State == PluginHostState.Stopped, TimeSpan.FromMilliseconds(300), "no host starts after shutdown");
    }

    [Fact]
    public async Task A_duplicate_refusal_during_a_restart_delay_cancels_the_restart_before_it_launches_a_host()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, """{"format":"infopanel.plugin-state/1","off":["InfoPanel.A"]}""");
        var launcher = new FakeLauncher(_ => new FakeHost { PluginId = "dup" });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Running(b);

        // B crashes and waits out its restart delay; A, earlier in the list, is turned on and takes the id.
        launcher.Started[0].Exit(1, "crash");
        await Eventually.Until(() => b.State == PluginHostState.Restarting, "B is restarting");
        await Armed(_now + TimeSpan.FromSeconds(2));
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: true));
        await Running(a);
        await Eventually.Until(() => b.State == PluginHostState.Failed && b.ReasonCode == "duplicateId", "B fails with duplicateId");
        var started = launcher.Started.Count;

        await Advance(TimeSpan.FromSeconds(2));
        await Eventually.Stays(() => launcher.Started.Count == started, TimeSpan.FromMilliseconds(300), "B's abandoned restart launches no host");
        Assert.Equal(PluginHostState.Failed, b.State);
        Assert.Equal(PluginHostState.Running, a.State);
    }

    [Fact]
    public async Task Two_claims_of_one_id_at_once_leave_it_to_one_package()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        // Both hosts stay starting: neither has listed its plugins yet.
        var never = new TaskCompletionSource();
        var launcher = new FakeLauncher(_ => new FakeHost { PluginId = "dup", InitializeGate = never.Task });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Eventually.Until(() => launcher.Started.Count == 2, "both hosts are launched");

        // A's claim is decided; before its sensors are taken, B's claim of the same id comes: B can't have it.
        Assert.Null(manager.Claim(a, a.Generation, ["dup"]));
        Assert.Equal(["dup"], a.ClaimedIds);
        Assert.Contains("InfoPanel.A", manager.Claim(b, b.Generation, ["dup"]));
        Assert.Empty(b.ClaimedIds);
        Assert.Equal(PluginHostState.Starting, a.State);
    }

    [Fact]
    public async Task A_loaded_callback_of_a_host_since_turned_off_neither_claims_nor_publishes()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, """{"format":"infopanel.plugin-state/1","off":["InfoPanel.B"]}""");
        var launcher = new FakeLauncher(_ => new FakeHost { PluginId = "dup" });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Running(a);
        var stale = a.Generation;

        // A is turned off, then the later B started: B has the id.
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.A", on: false));
        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync("InfoPanel.B", on: true));
        await Running(b);
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));

        // A's loaded callback, found current before A was turned off, goes on only now.
        var host = new FakeHost { PluginId = "dup" };
        a.TakeLoaded(stale, [host.Metadata], host.Containers);

        Assert.Equal(PluginHostState.Running, b.State);
        Assert.Null(b.ReasonCode);
        Assert.Equal(["dup"], b.ClaimedIds);
        Assert.Empty(a.ClaimedIds);
        Assert.Equal(PluginHostState.Stopped, a.State);
        Assert.Empty(_source.PluginIdsOf("InfoPanel.A"));
        Assert.Equal(SensorStatus.Live, _source.GetStatus(DupKey));
    }

    [Fact]
    public async Task A_settings_change_read_from_one_host_is_never_applied_to_the_next()
    {
        Package(Bundled, "InfoPanel.A");
        var narrow = Settings();
        narrow[1].MaxValue = 10;
        var hosts = new Queue<FakeHost>([new FakeHost { Settings = Settings() }, new FakeHost { Settings = narrow }]);
        var launcher = Launcher(() => hosts.Dequeue());
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        // 50 is checked against the first host's count (0–100); the package is reloaded before it is applied.
        a.AfterSettingsRead = async () =>
        {
            a.AfterSettingsRead = null;
            Assert.Equal(PluginSwitch.Done, await manager.ReloadAsync("InfoPanel.A"));
            await Running(a);
        };
        var (outcome, _, _) = await a.ApplySettingAsync("fake", "count", Json("50"));

        Assert.Equal(PluginCall.NotRunning, outcome);
        Assert.Equal(2, launcher.Started.Count);
        Assert.Empty(launcher.Started[0].Host.Applied);
        Assert.Empty(launcher.Started[1].Host.Applied);
    }

    // ---- errors, CPU and memory ----

    [Fact]
    public async Task Errors_are_kept_newest_first_with_their_time_and_plugin_twenty_at_most()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);
        var host = launcher.Started[0].Host;

        await host.SendErrorAsync("first");
        await Eventually.Until(() => a.ErrorLog.Count == 1, "the error is kept");
        Assert.Equal(new PluginErrorEntry(_time.GetUtcNow(), "fake", "first"), a.ErrorLog[0]);

        await Advance(TimeSpan.FromMilliseconds(1500));
        for (var i = 0; i < 24; i++)
            await host.SendErrorAsync($"error {i}");
        await Eventually.Until(() => a.ErrorLog.Count == 20 && a.ErrorLog[0].Message == "error 23", "the newest 20 are kept");
        Assert.Equal("error 4", a.ErrorLog[^1].Message);
        Assert.Equal(_time.GetUtcNow(), a.ErrorLog[0].Time);

        // The package's own (its host ending): no plugin.
        launcher.Started[0].Exit(1, "crash");
        await Eventually.Until(() => a.ErrorLog[0].Plugin == null && a.ErrorLog[0].Message.Contains("crash", StringComparison.Ordinal), "the host's end is the package's error");
    }

    [Fact]
    public async Task CPU_is_the_hosts_report_clamped_and_memory_the_engines_read_both_only_while_it_runs()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher();
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);
        var host = launcher.Started[0].Host;

        Assert.Null(a.Cpu);
        Assert.Equal(42L * 1024 * 1024, a.Memory);
        await host.SendPerformanceAsync(12.5);
        await Eventually.Until(() => a.Cpu == 12.5, "the host's report is the CPU");
        await host.SendPerformanceAsync(250);
        await Eventually.Until(() => a.Cpu == 100, "clamped at 100");
        await host.SendPerformanceAsync(-3);
        await Eventually.Until(() => a.Cpu == 0, "clamped at 0");

        await manager.SetOnAsync("InfoPanel.A", on: false);
        Assert.Null(a.Cpu);
        Assert.Null(a.Memory);
    }

    // ---- actions ----

    [Fact]
    public async Task An_action_goes_to_the_host_running_then_and_only_one_its_plugin_lists()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher(() => new FakeHost { Actions = ["Refresh"] });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        Assert.Equal(PluginCall.Done, await a.RunActionAsync("fake", "Refresh"));
        await Eventually.Until(() => launcher.Started[0].Host.ActionsRun.Count == 1, "the host runs it");
        Assert.Equal(("fake", "Refresh"), launcher.Started[0].Host.ActionsRun[0]);
        Assert.Equal(PluginCall.NotFound, await a.RunActionAsync("fake", "ToString"));
        Assert.Equal(PluginCall.NotFound, await a.RunActionAsync("other", "Refresh"));
        Assert.Equal([new PluginActionInfo("Refresh", "Do Refresh")], a.Plugins.Single().Actions);

        // After a restart, through the new host.
        launcher.Started[0].Exit(1, "crash");
        await Eventually.Until(() => a.State == PluginHostState.Restarting, "the host restarts");
        Assert.Equal(PluginCall.NotRunning, await a.RunActionAsync("fake", "Refresh"));
        await Armed(_now + TimeSpan.FromSeconds(2));
        await Advance(TimeSpan.FromSeconds(2));
        await Eventually.Until(() => launcher.Started.Count == 2, "a new host starts");
        await Running(a);
        Assert.Equal(PluginCall.Done, await a.RunActionAsync("fake", "Refresh"));
        await Eventually.Until(() => launcher.Started[1].Host.ActionsRun.Count == 1, "the new host runs it");
        Assert.Single(launcher.Started[0].Host.ActionsRun);
    }

    // ---- settings ----

    private static List<PluginConfigPropertyDto> Settings() =>
    [
        new() { Key = "label", DisplayName = "Label", Type = "String", Value = "fixture" },
        new() { Key = "count", DisplayName = "Count", Type = "Integer", Value = 3L, MinValue = 0, MaxValue = 100, Step = 1 },
        new() { Key = "ratio", DisplayName = "Ratio", Description = "A share.", Type = "Double", Value = 0.5, MinValue = 0, MaxValue = 1, Step = 0.1 },
        new() { Key = "enabled", DisplayName = "Enabled", Type = "Boolean", Value = true },
        new() { Key = "mode", DisplayName = "Mode", Type = "Choice", Value = "a", Options = ["a", "b"] },
        new() { Key = "wide", DisplayName = "Wide", Type = "Integer", Value = 0L },
    ];

    [Fact]
    public async Task Settings_are_asked_of_the_host_and_a_change_is_checked_against_the_setting_before_it_is_applied()
    {
        Package(Bundled, "InfoPanel.A");
        var launcher = Launcher(() => new FakeHost { Settings = Settings() });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);
        var host = launcher.Started[0].Host;

        var (got, settings) = await a.GetSettingsAsync("fake");
        Assert.Equal(PluginCall.Done, got);
        Assert.Equal(["label", "count", "ratio", "enabled", "mode", "wide"], settings!.Select(s => s.Key));
        Assert.Equal(["string", "integer", "number", "boolean", "choice", "integer"], settings!.Select(s => s.Type));
        Assert.Equal((0d, 100d, 1d), (settings![1].Min!.Value, settings[1].Max!.Value, settings[1].Step!.Value));
        Assert.Equal(3, settings[1].Value!.Value.GetInt32());
        Assert.Equal("A share.", settings[2].Description);
        Assert.Equal(["a", "b"], settings[4].Options);

        foreach (var (key, value) in new (string, string)[]
        {
            ("count", "101"), ("count", "-1"), ("count", "2.5"), ("count", "\"3\""), ("wide", "2147483648"), ("wide", "-2147483649"),
            ("ratio", "1.5"), ("ratio", "true"), ("ratio", "1e400"), ("enabled", "1"), ("enabled", "\"true\""),
            ("mode", "\"c\""), ("mode", "1"), ("label", "7"), ("label", "\"" + new string('x', 4097) + "\""),
        })
        {
            var (outcome, problem, _) = await a.ApplySettingAsync("fake", key, Json(value));
            Assert.True(outcome == PluginCall.Invalid, $"{key} = {value[..Math.Min(20, value.Length)]}: {outcome}");
            // The message says what the setting takes, never the value given.
            Assert.StartsWith($"The setting {key} takes", problem);
            Assert.DoesNotContain("xxxx", problem!);
        }
        Assert.Empty(host.Applied);

        var (applied, _, after) = await a.ApplySettingAsync("fake", "count", Json("7"));
        Assert.Equal(PluginCall.Done, applied);
        Assert.Equal(7, after!.Single(s => s.Key == "count").Value!.Value.GetInt32());
        Assert.Equal(("fake", "count"), (host.Applied[0].Plugin, host.Applied[0].Key));
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "count", Json("8.0"))).Outcome);
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "wide", Json("-2147483648"))).Outcome);
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "ratio", Json("1"))).Outcome);
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "mode", Json("\"b\""))).Outcome);
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "enabled", Json("false"))).Outcome);
        Assert.Equal(PluginCall.Done, (await a.ApplySettingAsync("fake", "label", Json("\"" + new string('x', 4096) + "\""))).Outcome);
        Assert.Equal(PluginCall.NotFound, (await a.ApplySettingAsync("fake", "nope", Json("1"))).Outcome);
        Assert.Equal(PluginCall.NotFound, (await a.ApplySettingAsync("other", "count", Json("1"))).Outcome);
        Assert.Equal(PluginCall.NotFound, (await a.GetSettingsAsync("other")).Outcome);
    }

    [Fact]
    public async Task A_plugin_without_settings_has_none_to_read_and_a_host_that_doesnt_answer_is_given_10_seconds()
    {
        Package(Bundled, "InfoPanel.A");
        Package(Bundled, "InfoPanel.B");
        var launcher = new FakeLauncher(c => c.PluginDll.Contains("InfoPanel.A.")
            ? new FakeHost { PluginId = "plain" }
            : new FakeHost { PluginId = "slow", Settings = Settings(), SettingsHang = true });
        await using var manager = Manager(launcher);
        _ = manager.StartAllAsync();
        var (a, b) = (manager.Find("InfoPanel.A")!, manager.Find("InfoPanel.B")!);
        await Running(a);
        await Running(b);

        Assert.Equal(PluginCall.NotFound, (await a.GetSettingsAsync("plain")).Outcome);

        var asking = b.GetSettingsAsync("slow");
        await Armed(_now + TimeSpan.FromSeconds(10));
        await Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(1));
        Assert.False(asking.IsCompleted);
        await Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(PluginCall.NoAnswer, (await asking.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_settings_change_never_logs_its_value()
    {
        Package(Bundled, "InfoPanel.A");
        var log = new CapturingLoggerProvider();
        var launcher = Launcher(() => new FakeHost { Settings = Settings() });
        await using var manager = new PluginHostManager(Options("macos"), launcher, _source, _time, new Microsoft.Extensions.Logging.LoggerFactory([log]));
        _ = manager.StartAllAsync();
        var a = manager.Find("InfoPanel.A")!;
        await Running(a);

        await a.ApplySettingAsync("fake", "label", Json("\"s3cret-value\""));
        await a.ApplySettingAsync("fake", "count", Json("424242"));
        await a.ApplySettingAsync("fake", "mode", Json("\"s3cret-choice\""));

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("s3cret", StringComparison.Ordinal) || l.Contains("424242", StringComparison.Ordinal));
        Assert.DoesNotContain(a.ErrorLog, e => e.Message.Contains("s3cret", StringComparison.Ordinal));
    }

    private static readonly SensorKey DupKey = SensorKey.Plugin("/dup/c/v");
    private static readonly SensorKey Key = SensorKey.Plugin("/fake/c/v");

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Every log line, formatted, whatever its level.</summary>
    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._lines)
                    provider._lines.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
            }
        }
    }

    // ---- helpers ----

    /// <summary>Hosts whose plugin ids are <paramref name="ids"/>, one each in the order they start, then the last again.</summary>
    private static Func<FakeHost> Hosts(params string[] ids)
    {
        var next = 0;
        return () => new FakeHost { PluginId = ids[Math.Min(Interlocked.Increment(ref next) - 1, ids.Length - 1)] };
    }


    private static FakeLauncher Launcher(Func<FakeHost>? hosts = null) => new(hosts ?? (() => new FakeHost()));

    /// <summary>A package folder <paramref name="name"/> in <paramref name="parent"/>, with a DLL file (the hosts are fakes) and <paramref name="ini"/> as its PluginInfo.ini.</summary>
    private static string Package(string parent, string name, string? ini = null)
    {
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".dll"), "not loaded: the hosts are fakes");
        if (ini != null)
            File.WriteAllText(Path.Combine(folder, "PluginInfo.ini"), ini);
        return folder;
    }

    private PluginHostManager Manager(FakeLauncher launcher, string platform = "macos") =>
        new(Options(platform), launcher, _source, _time, NullLoggerFactory.Instance);

    private PluginOptions Options(string platform) => new()
    {
        BundledDirectory = Bundled,
        InstalledDirectory = Installed,
        StateFile = StateFile,
        Platform = platform,
        Host = new PluginHostSettings
        {
            SettingsDirectory = Path.Combine(_root, "data", "plugin-settings"),
            LogsDirectory = Path.Combine(_root, "data", "logs", "plugins"),
        },
    };

    private static Task Running(PluginHostRunner runner) =>
        Eventually.Until(() => runner.State == PluginHostState.Running, $"{runner.Package.Folder}'s host runs");

    private Task Armed(TimeSpan at) => Eventually.Until(() => _time.HasTimerDueAt(at), $"a timer due at {at} is armed");

    private async Task Advance(TimeSpan by)
    {
        _time.Advance(by);
        _now += by;
        await Task.Yield();
    }
}
