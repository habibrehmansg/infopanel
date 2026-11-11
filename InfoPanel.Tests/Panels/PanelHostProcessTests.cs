using System.IO.Pipes;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using InfoPanel.Spec;
using Serilog.Events;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// Panel hosts (one process a package, <c>--panel-host</c>) under the panel manager, over the fake USB backend: a host
/// starts with the first device its families name and stops after the last; a host that ends unasked is started again
/// after its delay, its panel with it, while a BeadaPanel's session runs on untouched; after three restarts in a minute
/// the package fails and its panels are failed with the <c>driver</c> reason; on quit the sessions close within the
/// 3 s bound, and a host past it is killed. The hosts here are serve loops in this process, ended as a crash ends a
/// process, except the last test's: the virtual package in a real host process.
/// </summary>
public sealed class PanelHostProcessTests : IAsyncDisposable
{
    private const string BeadaSerial = "934072005c00481401044f2420751fd6";
    private const string VirtualId = "0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60";
    private const string BeadaId = "1f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60";

    private static readonly PanelPackageFamily Family = new("virtual", "Virtual panel", [new PanelMatch(0x1209, 0x0001)],
        [new PanelPackageModel("4x2", "Virtual 4 x 2", 4, 2, null)]);

    private readonly string _folder = Directory.CreateTempSubdirectory("panel-hosts-").FullName;
    private readonly FakeUsbBackend _backend = new();
    private readonly List<LogEvent> _events;
    private readonly Serilog.ILogger _logger;
    private readonly InProcessLauncher _launcher;
    private readonly List<PanelSink> _sinks = [];
    private PanelManager? _manager;
    private PanelHostRunner? _runner;
    private long _frameId;

    public PanelHostProcessTests()
    {
        (_logger, _events) = PanelStoreTests.Logged();
        _launcher = new InProcessLauncher(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        if (_runner != null)
            await _runner.DisposeAsync();
        _launcher.Release();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a host process's log still open on Windows: left for the temp folder's own clean-up
        }
    }

    /// <summary>The log so far, taken under the sink's lock (hosts log from their own threads).</summary>
    private List<LogEvent> Events()
    {
        lock (_events)
            return [.. _events];
    }

    private string Log => string.Join("\n", Events().Select(e => e.RenderMessage()));

    private FakeUsbDevice PlugVirtual() => _backend.Add(new FakeUsbDevice(new UsbDeviceId(0x1209, 0x0001, 0, 9, "0-9")));

    private FakeUsbDevice PlugBeada() => _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, 4, "0-4"))
    {
        Reply = PanelReplies.Info(2, 5, 21, 1920, 480, BeadaSerial),
        UsbSerial = BeadaSerial,
    });

    private static PanelPackage Package(string directory) =>
        new("InfoPanel.Panels.Virtual", directory, Path.Combine(directory, "InfoPanel.Panels.Virtual.dll"), Bundled: true, "1.0", [Family]);

    private PanelHostRunner Runner(IPanelHostLauncher launcher, PanelPackage? package = null, PanelHostTimeouts? timeouts = null)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "slots"));
        // The runner's own fake clock: a host's restart delay passes at once, and the restart window counts those delays.
        _runner = new PanelHostRunner(package ?? Package(Path.Combine(_folder, "InfoPanel.Panels.Virtual")), launcher, Path.Combine(_folder, "slots"),
            Path.Combine(_folder, "logs"), () => "information", on: true, new FakeUsbClock(), _logger,
            timeouts ?? new PanelHostTimeouts { Restart = TimeSpan.FromMilliseconds(200) });
        return _runner;
    }

    private PanelManager Manager(PanelHostRunner runner, params SavedPanel[] saved)
    {
        var store = PanelStore.Read(Path.Combine(_folder, PanelStore.FileName), _logger);
        if (saved.Length > 0)
            store.Save(saved);
        var host = new PanelHost(_ => true, sink => { lock (_sinks) _sinks.Add((PanelSink)sink); return true; }, sink => { lock (_sinks) _sinks.Remove((PanelSink)sink); },
            _ => { }, _ => { }, () => 15);
        // No passes of its own: each check the tests make runs one (Step), so nothing waits for a timer's pass.
        _manager = new PanelManager(_backend, store, host, TimeProvider.System, _logger, new PanelRunnerOptions { KeepAlive = TimeSpan.FromMilliseconds(50) },
            drivers: [.. PanelManager.BuiltInDrivers(_backend, _backend.Clock), new HostedPanelDriver(runner.Families.Single(f => f.Id == "virtual"), runner, _logger)]);
        _manager.Start(passes: false);
        return _manager;
    }

    private static SavedPanel Virtual(bool enabled = true) => new(VirtualId, "virtual", "4x2", "VIRTUAL09", "0-9", enabled, enabled ? "p-1" : null);

    // Profile p-2, which gets no frames: its session runs, writing nothing, while the virtual panel's does.
    private static SavedPanel Beada() => new(BeadaId, SavedPanel.BeadaPanelFamily, "8", BeadaSerial, "0-4", true, "p-2", Encoding: PanelEncoding.Raw);

    private static readonly System.Reflection.ConstructorInfo FrameConstructor = typeof(RenderedFrame).GetConstructor(
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(SKImage)])!;

    private static readonly System.Reflection.MethodInfo FrameClose = typeof(RenderedFrame).GetMethod("Close",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static void Init(RenderedFrame frame, string property, object value) => typeof(RenderedFrame).GetProperty(property)!.SetValue(frame, value);

    /// <summary>An 8 × 2 frame to every sink of profile p-1, as a render loop hands them over.</summary>
    private void Deliver()
    {
        List<PanelSink> sinks;
        lock (_sinks)
            sinks = [.. _sinks.Where(s => s.ProfileId == "p-1")];
        foreach (var sink in sinks)
        {
            var frameId = Interlocked.Increment(ref _frameId);
            using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
            pixels.Erase(SKColors.SteelBlue);
            using var image = SKImage.FromBitmap(pixels);
            // A render loop's frame (its constructor and close are the engine's own, reached here by reflection).
            var frame = (RenderedFrame)FrameConstructor.Invoke([image]);
            Init(frame, "ProfileId", "p-1");
            Init(frame, "Rev", 1L);
            Init(frame, "FrameId", frameId);
            Init(frame, "Tick", frameId);
            Init(frame, "Scale", 1f);
            Init(frame, "PixelWidth", 8);
            Init(frame, "PixelHeight", 2);
            Init(frame, "ProfileWidth", 8);
            Init(frame, "ProfileHeight", 2);
            Init(frame, "Bounds", (IReadOnlyList<ItemBounds>)[]);
            Init(frame, "ContentHash", (ulong)frameId);
            sink.OnFrame(frame);
            FrameClose.Invoke(frame, null);
        }
    }

    /// <summary>One step of the engine as the test drives it: a frame to each sink, then one of the manager's passes (none while no manager runs).</summary>
    private async Task Step()
    {
        if (_manager == null)
            return;
        Deliver();
        await _manager.RunPassAsync();
    }

    /// <summary>
    /// Steps until the panel <paramref name="id"/> is as <paramref name="wanted"/>; between steps it gives the hosts' own
    /// work (their pipes and processes) a moment. No pass comes but these.
    /// </summary>
    private async Task<PanelStatus> Until(string id, Func<PanelStatus, bool> wanted, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            await Step();
            var status = (await _manager!.ListAsync()).FirstOrDefault(s => s.Saved.Id == id);
            if (status != null && wanted(status))
                return status;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}: {status}\nThe log:\n{Log}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Steps until <paramref name="done"/>, as <see cref="Until(string, Func{PanelStatus, bool}, string, int)"/>.</summary>
    private async Task Until(Func<bool> done, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            await Step();
            if (done())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{Log}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_host_starts_with_the_first_device_its_families_name_and_stops_after_the_last_leaves()
    {
        var runner = Runner(_launcher);
        Manager(runner);
        await _manager!.RunPassAsync();
        Assert.Equal(0, _launcher.Starts); // no device: no host

        var device = PlugVirtual();
        IReadOnlyList<PanelStatus> found = [];
        await Until(() => (found = _manager.ListAsync().GetAwaiter().GetResult()).Count > 0, "the virtual panel was never found");
        var panel = Assert.Single(found);
        Assert.Equal(("virtual", "4x2", "0-9", "off"), (panel.Saved.Family, panel.Saved.Model, panel.Saved.Port, panel.State));
        Assert.Equal(1, _launcher.Starts);
        Assert.Equal("running", runner.State);

        _backend.Unplug(device);
        await Until(() => _launcher.Running == 0, "the host never stopped after its last device left");
        Assert.Equal("idle", runner.State);
        await _manager.RunPassAsync();
        Assert.Equal(1, _launcher.Starts);
        Assert.Contains(Events(), e => e.RenderMessage().Contains("driver virtual, the panel package InfoPanel.Panels.Virtual"));
    }

    [Fact]
    public async Task A_host_that_ends_unasked_restarts_its_panel_while_a_BeadaPanel_session_runs_on()
    {
        var beada = PlugBeada();
        PlugVirtual();
        var runner = Runner(_launcher);
        Manager(runner, Virtual(), Beada());
        await Until(VirtualId, s => s.State == "running", "the virtual panel never ran");
        await Until(BeadaId, s => s.State == "running", "the BeadaPanel never ran");
        var beadaOpens = beada.Calls.Count(c => c == "open");

        _launcher.Crash(); // the host process dies

        await Until(() => _launcher.Starts == 2, "the host was never started again");
        // The panel's session on the dead host reads "running" until its runner's next write finds the host gone; only a
        // second session's start means the panel was started again on the new host.
        await Until(() => _launcher.Sessions == 2, "the virtual panel was never started again on the new host");
        await Until(VirtualId, s => s.State == "running" && _launcher.Running == 1, "the virtual panel never ran on the new host");
        Assert.Equal(2, _launcher.Sessions);
        var bead = await Until(BeadaId, s => s.State == "running", "the BeadaPanel stopped");
        Assert.Equal(beadaOpens, beada.Calls.Count(c => c == "open")); // its session never restarted
        Assert.DoesNotContain(Events(), e => e.RenderMessage().Contains("BeadaPanel 8") && e.RenderMessage().Contains("stopped after"));
        Assert.Contains(Events(), e => e.RenderMessage().Contains("InfoPanel.Panels.Virtual's host ended") && e.RenderMessage().Contains("started again in 0.2 s"));
    }

    [Fact]
    public async Task After_three_restarts_in_a_minute_the_package_fails_and_its_panel_is_failed_with_the_driver_reason()
    {
        PlugVirtual();
        var runner = Runner(_launcher);
        Manager(runner, Virtual());
        for (var start = 1; start <= 4; start++)
        {
            await Until(() => _launcher.Starts == start && _launcher.Running == 1, $"host {start} never ran");
            _launcher.Crash();
        }

        var failed = await Until(VirtualId, s => s.State == "failed" && s.ReasonCode == "driver", "the panel never failed with the driver reason");
        Assert.Equal("failed", runner.State);
        Assert.StartsWith("its driver failed: its host kept ending (3 restarts within 60 s)", failed.Reason);
        Assert.Equal(new PanelDriverProblem(false, failed.Reason!), runner.Problem);
        for (var pass = 0; pass < 3; pass++)
            await Step(); // the device is still there: given up, no pass starts it again
        Assert.Equal(4, _launcher.Starts);
    }

    [Fact]
    public async Task On_quit_the_sessions_close_within_the_bound_and_a_host_past_it_is_killed()
    {
        PlugVirtual();
        _launcher.HangClose = true;
        var runner = Runner(_launcher);
        Manager(runner, Virtual());
        await Until(VirtualId, s => s.State == "running", "the virtual panel never ran");

        // The engine's stop: the panels within 3 s, then the hosts within what is left of them.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _manager!.StopAsync(TimeSpan.FromSeconds(3));
        var left = TimeSpan.FromSeconds(3) - clock.Elapsed;
        await runner.StopAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero);

        // Within the bound, with slack for a loaded machine: it never waited for the hung close. The hung host was ended
        // (killed: it may still hold the device), and no host runs after the stop. Which ended it races, both within the
        // bound: the session's close past its budget, or the hosts' stop past what was left of it (the close's call then
        // fails with the host's end and logs nothing; the session's own warning is HostedPanelTests' to check).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the stop took {clock.Elapsed.TotalMilliseconds} ms\nThe log:\n{Log}");
        Assert.InRange(_launcher.Killed, 1, 2); // the hung host; and a host started again meanwhile, when the stop came after it
        Assert.Equal(0, _launcher.Running);
        Assert.Contains(Events(), e => e.RenderMessage() is var m
            && (m.Contains("InfoPanel.Panels.Virtual couldn't close its session") && m.Contains("its host is ended")
                || m.Contains("InfoPanel.Panels.Virtual's host didn't stop within") && m.Contains("killed")));
    }

    [Fact]
    public async Task A_host_that_doesnt_end_at_the_stops_shutdown_is_killed_past_the_bound()
    {
        PlugVirtual();
        _launcher.HangClose = true;
        var runner = Runner(_launcher);
        Manager(runner, Virtual());
        await Until(VirtualId, s => s.State == "running", "the virtual panel never ran");

        // The host is asked to close its sessions and end; its session's close never returns, so it never ends.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await runner.StopAsync(TimeSpan.FromSeconds(1));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"the stop took {clock.Elapsed.TotalMilliseconds} ms\nThe log:\n{Log}");
        Assert.Equal((1, 1, 0), (_launcher.Starts, _launcher.Killed, _launcher.Running));
        Assert.Contains(Events(), e => e.RenderMessage().Contains("InfoPanel.Panels.Virtual's host didn't stop within") && e.RenderMessage().Contains("killed"));
    }

    [Fact]
    public async Task A_stop_waits_for_a_host_start_under_way_and_ends_what_it_started()
    {
        var gated = new GatedLauncher(_launcher);
        var runner = Runner(gated);
        var connecting = Task.Run(() => runner.Connect(CancellationToken.None), TestContext.Current.CancellationToken);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The engine stops while the host's process is being started.
        var stopping = runner.StopAsync(TimeSpan.FromSeconds(3));
        Assert.False(stopping.IsCompleted, "the stop returned while a host was being started");
        gated.Open();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The host it started is ended by the time the stop returns, and none is started after it.
        Assert.Equal((1, 0, 1), (_launcher.Starts, _launcher.Running, _launcher.Killed));
        await Assert.ThrowsAsync<PanelDriverException>(() => connecting);
        Assert.Equal("closed", runner.State);
        Assert.Throws<PanelDriverException>(() => runner.Connect(CancellationToken.None));
        Assert.Equal(1, _launcher.Starts);
    }

    [Fact]
    public async Task A_host_start_held_past_the_stops_bound_creates_no_process_after_the_stop_returned()
    {
        var gated = new GatedLauncher(_launcher);
        var runner = Runner(gated);
        var connecting = Task.Run(() => runner.Connect(CancellationToken.None), TestContext.Current.CancellationToken);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The host's start is held past the stop's bound and the second a kill is given: the stop returns first.
        await runner.StopAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        gated.Open();
        await gated.Left.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The start went on once let go, after the stop: it created no host.
        Assert.Equal((0, 0), (_launcher.Starts, _launcher.Running));
        await Assert.ThrowsAsync<PanelDriverException>(() => connecting);
        Assert.Equal("closed", runner.State);
    }

    [Fact]
    public async Task The_virtual_package_runs_in_a_panel_host_process_of_its_own_and_is_started_again_when_killed()
    {
        var package = VirtualPackage.CopyTo(_folder, "log frames.txt");
        PlugVirtual();
        var launcher = new PanelProcessLauncher();
        var runner = Runner(launcher, Package(package), new PanelHostTimeouts { Restart = TimeSpan.FromMilliseconds(200) });
        Manager(runner, Virtual());
        await Until(VirtualId, s => s.State == "running", "the virtual panel never ran in its host", 60);
        var frames = Path.Combine(package, "frames.txt");
        // A frame, not only the start, reached this host process (the driver logs its start before any write).
        await Until(() => WroteAfterOpen(frames, 0), "no frame reached the host process", 30);
        var first = runner.ProcessId;
        Assert.NotNull(first);
        Assert.NotEqual(Environment.ProcessId, first);

        using (var killed = System.Diagnostics.Process.GetProcessById(first!.Value))
        {
            killed.Kill();
            await killed.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var before = VirtualPackage.Lines(frames).Count; // the first host's lines: it is gone
        await Until(() => runner.ProcessId is { } id && id != first, "the host process was never started again", 60);
        await Until(VirtualId, s => s.State == "running", "the virtual panel never ran on the new host process", 60);
        // The new host process started the panel again and was written to as well.
        await Until(() => WroteAfterOpen(frames, before), "no frame reached the new host process", 30);

        Assert.True(await _manager!.StopAsync(TimeSpan.FromSeconds(3)), $"the panel didn't stop within the bound\nThe log:\n{Log}");
        await runner.StopAsync(TimeSpan.FromSeconds(3));
        Assert.Null(runner.ProcessId);
        Assert.Contains(VirtualPackage.Lines(frames), l => l.StartsWith("close screenOff", StringComparison.Ordinal));
    }

    /// <summary>Whether the virtual driver's log, from line <paramref name="from"/>, has a start followed by a frame.</summary>
    private static bool WroteAfterOpen(string log, int from) =>
        File.Exists(log) && VirtualPackage.Lines(log).Skip(from).SkipWhile(l => !l.StartsWith("open ", StringComparison.Ordinal)).Any(l => l.StartsWith("write ", StringComparison.Ordinal));

    [Fact]
    public void The_virtual_driver_s_log_is_read_while_its_host_appends_to_it()
    {
        // The host's driver appends as File.AppendAllLines does (write access, sharing read only); a read with
        // File.ReadAllLines would ask for read sharing only, which Windows refuses while that append holds the file.
        var log = Path.Combine(_folder, "frames.txt");
        File.WriteAllLines(log, ["open brightness 100 rotation 0 format any"]);
        using var appending = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
        appending.Write("write 1 Rgb565Le 307200\n"u8);
        appending.Flush();

        Assert.Equal(["open brightness 100 rotation 0 format any", "write 1 Rgb565Le 307200"], VirtualPackage.Lines(log));
    }

    [Fact]
    public async Task A_host_whose_drivers_arent_its_packages_families_fails_the_package_and_its_panel_with_the_driver_reason()
    {
        PlugVirtual();
        _launcher.Family = "other";
        var runner = Runner(_launcher);
        Manager(runner, Virtual());

        var failed = await Until(VirtualId, s => s.State == "failed" && s.ReasonCode == "driver", "the panel never failed with the driver reason");
        Assert.Equal("failed", runner.State);
        Assert.Equal("its driver failed: its host's drivers (other) aren't the families its panel.json declares (virtual)", failed.Reason);
        Assert.Equal((1, 0), (_launcher.Starts, _launcher.Running)); // ended, and not started again
    }

    [Fact]
    public async Task A_host_whose_hello_lists_a_family_without_an_id_fails_the_package_and_its_panel_with_the_driver_reason()
    {
        PlugVirtual();
        _launcher.Family = null!; // its hello: families [null]
        var runner = Runner(_launcher);
        Manager(runner, Virtual());

        var failed = await Until(VirtualId, s => s.State == "failed" && s.ReasonCode == "driver", "the panel never failed with the driver reason");
        Assert.Equal("failed", runner.State);
        Assert.Equal("its driver failed: its host's hello is malformed: a family without an id", failed.Reason);
        Assert.Equal((1, 0), (_launcher.Starts, _launcher.Running)); // ended, and not started again
    }

    [Fact]
    public void A_package_whose_DLL_has_no_driver_for_a_family_its_panel_json_declares_isnt_loaded()
    {
        var package = VirtualPackage.CopyTo(Path.Combine(_folder, "incomplete"));
        var manifest = File.ReadAllText(Path.Combine(package, PanelPackages.ManifestName));
        File.WriteAllText(Path.Combine(package, PanelPackages.ManifestName), manifest.Replace("""
            "families": [
            """, """
            "families": [
                { "id": "ghost", "name": "Ghost panel", "match": [{ "usb": "1209:0002" }] },
            """));

        var refused = Assert.Throws<InvalidDataException>(() => PanelHostEntry.Load(package, VirtualPackage.Folder, _logger));
        Assert.Equal("no driver in InfoPanel.Panels.Virtual.dll is for the family ghost its panel.json declares", refused.Message);
    }

    [Fact]
    public void A_slot_folder_that_is_a_link_is_refused_and_nothing_where_it_points_is_swept()
    {
        var run = Path.Combine(_folder, "run");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_folder, "elsewhere")).FullName;
        var victim = Path.Combine(elsewhere, "someone-elses.slots");
        File.WriteAllText(victim, "kept");
        Directory.CreateDirectory(run);
        var root = Path.Combine(_folder, "data");
        Directory.CreateSymbolicLink(Path.Combine(run, "panels-" + InfoPanel.Hosting.LocalChannel.HashOf(root)), elsewhere);

        Assert.ThrowsAny<IOException>(() => PanelService.SlotDirectory(run, root, _logger));
        Assert.True(File.Exists(victim), "a file where the link points was swept");
    }

    [Fact]
    public async Task A_host_whose_driver_hangs_in_its_constructor_ends_once_the_engine_is_gone()
    {
        // The engine's stand-in: a process this test can end (a panel host waiting for a pipe no one serves).
        var launcher = new PanelProcessLauncher();
        var stand = Path.Combine(_folder, "stand-in");
        using var engine = launcher.Start(new PanelHostCommand("ippn-" + Guid.NewGuid().ToString("N")[..16], VirtualPackage.CopyTo(stand), Path.Combine(_folder, "logs"),
            "information", Environment.ProcessId, PanelHostEntry.StartTimeOf(Environment.ProcessId)!.Value), new PanelHostLaunch());
        var package = VirtualPackage.CopyTo(Path.Combine(_folder, "hung"), "log frames.txt", "hang-constructor");
        using var host = launcher.Start(new PanelHostCommand("ippn-" + Guid.NewGuid().ToString("N")[..16], package, Path.Combine(_folder, "logs"), "information", engine.Id,
            PanelHostEntry.StartTimeOf(engine.Id)!.Value), new PanelHostLaunch());
        try
        {
            var frames = Path.Combine(package, "frames.txt");
            await Until(() => File.Exists(frames) && VirtualPackage.Lines(frames).Contains("constructing"), "the hung driver was never made", 30);

            engine.Kill();
            await engine.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await host.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            host.Kill();
            engine.Kill();
        }
    }

    [Fact]
    public async Task A_host_given_the_engines_id_with_another_start_time_ends_before_its_package_loads()
    {
        // This test's process stands in for one that took the engine's id after the engine ended: its id, another start.
        var package = VirtualPackage.CopyTo(Path.Combine(_folder, "reused"), "log frames.txt", "hang-constructor");
        var start = PanelHostEntry.StartTimeOf(Environment.ProcessId)!.Value;
        using var host = new PanelProcessLauncher().Start(new PanelHostCommand("ippn-" + Guid.NewGuid().ToString("N")[..16], package, Path.Combine(_folder, "logs"),
            "information", Environment.ProcessId, start + 1), new PanelHostLaunch());
        try
        {
            await host.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(3, host.ExitCode); // the engine is gone
            Assert.False(File.Exists(Path.Combine(package, "frames.txt")), "the package's driver was made");
        }
        finally
        {
            host.Kill();
        }
    }

    [Theory]
    [InlineData("4242 (InfoPanel) S 1 4242 4242 0 -1 4194560 100 0 0 0 5 3 0 0 20 0 9 0 123456 1000 10", 123456L)]
    [InlineData("4242 (a ) b) (c) S 1 4242 4242 0 -1 4194560 100 0 0 0 5 3 0 0 20 0 9 0 98765 1000 10", 98765L)]
    [InlineData("4242 (short) S 1 4242", null)]
    [InlineData("no name", null)]
    public void On_Linux_a_processs_start_is_the_kernels_starttime_read_after_its_name(string stat, long? ticks) =>
        Assert.Equal(ticks, PanelHostEntry.LinuxStartTicks(stat));

    /// <summary>A launcher whose start waits, once <see cref="Entered"/>, until the test opens it; then the inner launcher's (<see cref="Left"/> once it returned or threw).</summary>
    private sealed class GatedLauncher(IPanelHostLauncher inner) : IPanelHostLauncher
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _left = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(false);

        public Task Entered => _entered.Task;

        public Task Left => _left.Task;

        public void Open() => _gate.Set();

        public IPanelHostProcess Start(PanelHostCommand command, PanelHostLaunch launch)
        {
            _entered.TrySetResult();
            _gate.Wait(TimeSpan.FromSeconds(30));
            try
            {
                return inner.Start(command, launch);
            }
            finally
            {
                _left.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Panel hosts as serve loops in this process over the runner's pipe: <see cref="Crash"/> ends the running one as a
    /// dead process would (its pipe closed, its sessions gone); with <see cref="HangClose"/> its sessions' close never returns.
    /// </summary>
    private sealed class InProcessLauncher(Serilog.ILogger logger) : IPanelHostLauncher
    {
        private readonly List<Hosted> _hosts = [];
        private readonly ManualResetEventSlim _release = new(false);
        private int _starts;
        private int _sessions;
        private int _killed;

        public bool HangClose { get; set; }

        /// <summary>The family its hosts' driver is for (a package declaring another gets a host whose drivers aren't its families).</summary>
        public string Family { get; set; } = "virtual";

        public int Starts => Volatile.Read(ref _starts);

        public int Sessions => Volatile.Read(ref _sessions);

        public int Killed => Volatile.Read(ref _killed);

        public int Running
        {
            get
            {
                lock (_hosts)
                    return _hosts.Count(h => !h.Exited.IsCompleted);
            }
        }

        public void Release() => _release.Set();

        public IPanelHostProcess Start(PanelHostCommand command, PanelHostLaunch launch) => launch.Create(() =>
        {
            Interlocked.Increment(ref _starts); // a host made, as a process created
            var host = new Hosted(this, command.PipeName, logger);
            lock (_hosts)
                _hosts.Add(host);
            return host;
        });

        public void Crash()
        {
            lock (_hosts)
                foreach (var host in _hosts.Where(h => !h.Exited.IsCompleted))
                    host.End();
        }

        private sealed class Hosted : IPanelHostProcess
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly NamedPipeClientStream _pipe;

            private readonly InProcessLauncher _launcher;

            public Hosted(InProcessLauncher launcher, string pipeName, Serilog.ILogger logger)
            {
                _launcher = launcher;
                _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var driver = new CountingDriver(launcher);
                var hangs = launcher.HangClose; // a host whose session hangs stays alive until it is killed, as a process held by its driver would
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _pipe.ConnectAsync(_stop.Token);
                        await new PanelHostServer([driver], logger).ServeAsync(_pipe, _stop.Token);
                    }
                    catch (Exception)
                    {
                        // ended as a crash ends a process
                    }
                    if (!hangs)
                        _exited.TrySetResult();
                });
            }

            private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Exited => _exited.Task;

            public int Id => 0;

            public int? ExitCode => Exited.IsCompleted ? 0 : null;

            public string ErrorTail => "";

            public void End()
            {
                _stop.Cancel();
                _pipe.Dispose();
                _exited.TrySetResult(); // a process killed is gone at once
            }

            public void Kill()
            {
                if (Exited.IsCompleted)
                    return;
                Interlocked.Increment(ref _launcher._killed);
                End();
            }

            public void Dispose()
            {
            }

            /// <summary>A virtual 4 × 2 panel: every probe is its, every write is taken.</summary>
            private sealed class CountingDriver(InProcessLauncher launcher) : IPanelDriver
            {
                public string Family { get; } = launcher.Family;

                public IReadOnlyList<PanelMatch> Matches { get; } = [new PanelMatch(0x1209, 0x0001)];

                public PanelProbe Probe(PanelCandidate device, CancellationToken stop) =>
                    new(true, new PanelIdentity("4x2", 4, 2, "VIRTUAL09", "1"), PanelDeviceStatus.Ok);

                public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
                {
                    Interlocked.Increment(ref launcher._sessions);
                    return new PanelOpen(new Session(launcher), PanelDeviceStatus.Ok, "");
                }
            }

            private sealed class Session(InProcessLauncher launcher) : IPanelSession
            {
                public PanelCapabilities Capabilities { get; } = new(4, 2, [PanelFrameFormat.Rgb565Le], MaxFps: 30);

                public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop) => new(PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(1));

                public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

                public void Close(bool screenOff, TimeSpan budget)
                {
                    if (launcher.HangClose)
                        launcher._release.Wait(); // until the test ends: the host never answers the close
                }

                public void Dispose()
                {
                }
            }
        }
    }
}

/// <summary>The virtual panel package as the solution builds it (InfoPanel.Panels.Virtual), copied for a test with its mode.</summary>
internal static class VirtualPackage
{
    public const string Folder = "InfoPanel.Panels.Virtual";

    /// <summary>The build's output: the repository's InfoPanel.Panels.Virtual/bin/{configuration}/net10.0, the configuration this test runs in.</summary>
    public static string Built
    {
        get
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var repository = output.Parent.Parent!.Parent!.Parent!.FullName;
            var built = Path.Combine(repository, Folder, "bin", configuration, output.Name);
            if (!File.Exists(Path.Combine(built, Folder + ".dll")))
                throw new InvalidOperationException($"The virtual panel package isn't built at {built}: build the solution (InfoPanel.slnx) first.");
            return built;
        }
    }

    /// <summary>
    /// The lines of a log the host process appends to while it is read: opened sharing read, write and delete, as an
    /// append on Windows may hold it, so the read never fails on a sharing violation.
    /// </summary>
    public static IReadOnlyList<string> Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>Copies the package into <paramref name="parent"/>/InfoPanel.Panels.Virtual with <paramref name="mode"/> as its virtual-mode.txt; returns the folder.</summary>
    public static string CopyTo(string parent, params string[] mode)
    {
        var source = Built;
        var target = Path.Combine(parent, Folder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        if (mode.Length > 0)
            File.WriteAllLines(Path.Combine(target, "virtual-mode.txt"), mode);
        return target;
    }
}
