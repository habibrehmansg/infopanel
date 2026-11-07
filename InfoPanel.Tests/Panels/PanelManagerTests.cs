using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// The panel manager over the fake USB backend, with a host that attaches sinks without rendering: detection, matching
/// by serial then port, saving new panels off, the sessions each saved panel wants, and their ends.
/// </summary>
public sealed class PanelManagerTests : IAsyncDisposable
{
    private const string Serial = "934072005c00481401044f2420751fd6";

    private readonly string _folder = Directory.CreateTempSubdirectory("panels-manager-").FullName;
    private readonly FakeUsbBackend _backend = new();
    private readonly HashSet<string> _ready = ["p-1", "p-2"];
    private readonly List<IFrameSink> _attached = [];
    private readonly List<LogEvent> _events;
    private readonly Serilog.ILogger _logger;
    private PanelManager? _manager;

    public PanelManagerTests() => (_logger, _events) = PanelStoreTests.Logged();

    private string StoreFile => Path.Combine(_folder, PanelStore.FileName);

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A BeadaPanel 8 as the owner's reports itself: model code 21, PanelLink 2, platform 5, 1920 × 480.</summary>
    private static byte[] Reply(string serial = Serial, byte modelCode = 21) =>
        PanelReplies.Info(2, 5, modelCode, 1920, 480, serial);

    private FakeUsbDevice Plug(byte address, string port, string serial = Serial, byte modelCode = 21) =>
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, address, port))
        {
            Reply = Reply(serial, modelCode),
            UsbSerial = serial,
        });

    private PanelManager Manager(params SavedPanel[] saved)
    {
        var store = PanelStore.Read(StoreFile, _logger);
        if (saved.Length > 0)
            store.Save(saved);
        var host = new PanelHost(
            ProfileReady: id => { lock (_ready) return _ready.Contains(id); },
            Attach: sink => { lock (_attached) _attached.Add(sink); return true; },
            Detach: sink => { lock (_attached) _attached.Remove(sink); },
            RequestChanged: _ => { },
            Refresh: _ => { },
            FrameRate: () => 15);
        _manager = new PanelManager(_backend, store, host, _backend.Clock, _logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan });
        _manager.Start(passes: false);
        return _manager;
    }

    private static async Task<PanelStatus> Until(PanelManager manager, Func<PanelStatus, bool> wanted, int index = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var list = await manager.ListAsync();
            if (list.Count > index && wanted(list[index]))
                return list[index];
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"the panel never got there: {(list.Count > index ? list[index] : "none listed")}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Raw: these tests hold the raw stream's transcript (its start, black frame and end); <see cref="PanelJpegManagerTests"/> runs Auto.</summary>
    private static SavedPanel Saved(string? serial = Serial, string port = "0-1", bool enabled = true, string? profile = "p-1") =>
        new("0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", SavedPanel.BeadaPanelFamily, "8", serial, port, enabled, profile, Encoding: PanelEncoding.Raw);

    private static bool EndsWithScreenOff(FakeUsbDevice device)
    {
        var t = device.Transfers;
        return t.Length >= 3 && t[^3].Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]))
            && t[^2].Data.Length == 1920 * 480 * 2 && t[^2].Data.All(b => b == 0) && t[^1].Endpoint == BeadaPanelProtocol.PanelOut && t[^1].Data.Length == 270;
    }

    [Fact]
    public async Task A_plugged_panel_is_saved_off_without_a_profile()
    {
        Plug(4, "0-1");
        var manager = Manager();

        await manager.RunPassAsync();

        var status = Assert.Single(await manager.ListAsync());
        Assert.Equal(("off", true, "8", Serial, "0-1", false, (string?)null),
            (status.State, status.Connected, status.Saved.Model, status.Saved.Serial, status.Saved.Port, status.Saved.Enabled, status.Saved.ProfileId));
        Assert.Contains("\"serial\":\"" + Serial + "\"", File.ReadAllText(StoreFile));
        Assert.Contains(_events, e => e.RenderMessage().Contains("BeadaPanel \"8\" found") && e.RenderMessage().Contains("saved, off"));
        Assert.Empty(_attached);
    }

    [Fact]
    public async Task Enabled_with_a_ready_profile_runs_and_turned_off_its_screen_goes_off()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());

        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running");
        Assert.Equal("p-1", Assert.Single(_attached).ProfileId);
        Assert.Equal(1920, running.Info!.Width);

        await manager.UpdateAsync(running.Saved.Id, s => s with { Enabled = false });
        await Until(manager, s => s.State == "off");
        Assert.True(EndsWithScreenOff(device));
        Assert.Empty(_attached);
        Assert.Equal(0, device.OpenHandles);
        Assert.Contains("\"enabled\":false", File.ReadAllText(StoreFile));
    }

    [Fact]
    public async Task Unplugged_it_is_kept_and_replugged_at_another_port_it_is_matched_by_serial()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        var id = (await Until(manager, s => s.State == "running")).Saved.Id;

        _backend.Unplug(device);
        await manager.RunPassAsync();
        var gone = await Until(manager, s => s.State == "notConnected");
        Assert.Equal((id, true, "p-1"), (gone.Saved.Id, gone.Saved.Enabled, gone.Saved.ProfileId));

        Plug(9, "0-2");
        await manager.RunPassAsync();
        var back = await Until(manager, s => s.State == "running");
        Assert.Equal((id, "0-2"), (back.Saved.Id, back.Saved.Port));
        Assert.Single(await manager.ListAsync());
        Assert.Contains("\"port\":\"0-2\"", File.ReadAllText(StoreFile));
    }

    [Fact]
    public async Task A_panel_without_a_serial_is_matched_by_its_port()
    {
        var device = Plug(4, "1-2.3", serial: "");
        device.UsbSerial = null;
        var manager = Manager(Saved(serial: null, port: "1-2.3"));

        await manager.RunPassAsync();

        var status = await Until(manager, s => s.State == "running");
        Assert.Equal("0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", status.Saved.Id);
        Assert.Single(await manager.ListAsync());
    }

    [Fact]
    public async Task A_busy_panel_is_tried_again_every_pass_and_logged_once()
    {
        var device = Plug(4, "0-1");
        device.ClaimResult = UsbStatus.Busy;
        var manager = Manager();

        await manager.RunPassAsync();
        await manager.RunPassAsync();
        Assert.Empty(await manager.ListAsync());
        Assert.Empty(device.Transfers);
        Assert.Single(_events, e => e.RenderMessage().Contains("couldn't be identified"));

        device.ClaimResult = UsbStatus.Ok;
        await manager.RunPassAsync();
        Assert.Equal("off", Assert.Single(await manager.ListAsync()).State);
    }

    [Fact]
    public async Task An_unknown_model_is_logged_once_and_left_alone()
    {
        var device = Plug(4, "0-1", modelCode: 1);
        var manager = Manager();

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        Assert.Empty(await manager.ListAsync());
        Assert.Single(_events, e => e.RenderMessage().Contains("model code 1"));
        Assert.Equal(2, device.Transfers.Length); // one query and its reply
    }

    [Fact]
    public async Task Two_panels_at_once_are_two_saved_panels()
    {
        Plug(4, "0-1");
        Plug(5, "0-2", serial: "A1B2C3");
        var manager = Manager();

        await manager.RunPassAsync();

        Assert.Equal([Serial, "A1B2C3"], (await manager.ListAsync()).Select(s => s.Saved.Serial));
    }

    [Fact]
    public async Task A_failed_start_keeps_its_reason_and_is_tried_again_on_the_next_pass()
    {
        var device = Plug(4, "0-1");
        device.FailWrite(5, UsbStatus.Io); // writes: identify's query, the start's query, reset, backlight, then the stream's start
        var manager = Manager(Saved());

        await manager.RunPassAsync();
        var failed = await Until(manager, s => s.State == "failed");
        Assert.Contains("Io", failed.Reason);
        Assert.True(EndsWithScreenOff(device));

        await manager.RunPassAsync();
        var running = await Until(manager, s => s.State == "running");
        Assert.Null(running.Reason);
    }

    [Fact]
    public async Task A_deleted_profile_turns_the_screen_off_keeps_the_profile_and_waits_for_it()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running");
        var sink = Assert.Single(_attached);

        // The render service closes the sink (the profile was deleted), and it is no longer listed.
        lock (_ready)
            _ready.Remove("p-1");
        sink.OnClosed(FrameSinkClosedReason.ProfileDeleted);
        var off = await Until(manager, s => s.State == "off");
        Assert.Equal("p-1", off.Saved.ProfileId);
        Assert.True(EndsWithScreenOff(device));

        // It comes back with the profile.
        lock (_ready)
            _ready.Add("p-1");
        manager.ProfilesChanged();
        await Until(manager, s => s.State == "running");
    }

    [Fact]
    public async Task Brightness_changes_between_frames_without_a_restart()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        var id = (await Until(manager, s => s.State == "running")).Saved.Id;
        var opens = device.Calls.Count(c => c == "open");

        await manager.UpdateAsync(id, s => s with { Brightness = 40 });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!device.Transfers.Any(t => t.Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [40]))))
        {
            Assert.True(DateTime.UtcNow < deadline, "the brightness was never sent");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal(opens, device.Calls.Count(c => c == "open"));
        Assert.Equal("running", (await manager.ListAsync())[0].State);
    }

    [Fact]
    public void A_store_the_engine_cant_hold_runs_no_manager()
    {
        File.WriteAllText(StoreFile, "not json");
        var store = PanelStore.Read(StoreFile, _logger);
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 15);
        Assert.Throws<ArgumentException>(() => new PanelManager(_backend, store, host, _backend.Clock, _logger));
    }

    [Fact]
    public async Task Two_panels_with_one_serial_keep_their_own_settings_when_listed_in_the_other_order()
    {
        const string Second = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d";
        // Listed in the other order this time: the panel at 0-2 first.
        Plug(5, "0-2", serial: "S");
        Plug(4, "0-1", serial: "S");
        var manager = Manager(Saved(serial: "S", port: "0-1", enabled: false), Saved(serial: "S", port: "0-2", enabled: false, profile: "p-2") with { Id = Second });

        await manager.RunPassAsync();

        var list = await manager.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(("0-1", "p-1", true), (list[0].Saved.Port, list[0].Saved.ProfileId, list[0].Connected));
        Assert.Equal((Second, "0-2", "p-2", true), (list[1].Saved.Id, list[1].Saved.Port, list[1].Saved.ProfileId, list[1].Connected));
    }

    [Fact]
    public async Task A_serial_two_saved_panels_share_at_ports_neither_had_is_listed_as_new()
    {
        const string Second = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d";
        Plug(4, "0-3", serial: "S");
        var manager = Manager(Saved(serial: "S", port: "0-1", enabled: false), Saved(serial: "S", port: "0-2", enabled: false, profile: "p-2") with { Id = Second });

        await manager.RunPassAsync();

        var list = await manager.ListAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal(("0-1", false), (list[0].Saved.Port, list[0].Connected));
        Assert.Equal(("0-2", false), (list[1].Saved.Port, list[1].Connected));
        Assert.Equal(("0-3", true, (string?)null), (list[2].Saved.Port, list[2].Connected, list[2].Saved.ProfileId));
        Assert.Single(_events, e => e.RenderMessage().Contains("more than one saved panel"));
    }

    [Fact]
    public async Task A_third_panel_with_a_shared_serial_is_new_after_another_matched_not_the_one_left()
    {
        const string Second = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d";
        // A (0-1, on, p-1) and B (0-2) share serial S. B is listed first and matched; then a third S panel at 0-3, a
        // port neither had: it mustn't take A's settings because A is the only S panel left unmatched.
        Plug(5, "0-2", serial: "S");
        Plug(6, "0-3", serial: "S");
        var manager = Manager(Saved(serial: "S", port: "0-1", enabled: true), Saved(serial: "S", port: "0-2", enabled: false, profile: "p-2") with { Id = Second });

        await manager.RunPassAsync();

        var list = await manager.ListAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal(("0-1", true, "p-1", false), (list[0].Saved.Port, list[0].Saved.Enabled, list[0].Saved.ProfileId, list[0].Connected));
        Assert.Equal((Second, "0-2", true), (list[1].Saved.Id, list[1].Saved.Port, list[1].Connected));
        Assert.Equal(("0-3", true, false, (string?)null), (list[2].Saved.Port, list[2].Connected, list[2].Saved.Enabled, list[2].Saved.ProfileId));
        Assert.Single(_events, e => e.RenderMessage().Contains("more than one saved panel"));
    }

    [Fact]
    public async Task A_profile_deleted_and_made_again_while_the_screen_goes_off_runs_again()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running");
        var sink = Assert.Single(_attached);

        // The screen-off's first write is held until the profile is back.
        using var held = new ManualResetEventSlim(false);
        using var holding = new ManualResetEventSlim(false);
        device.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0])))
            {
                holding.Set();
                held.Wait(TimeSpan.FromSeconds(10));
            }
            return null;
        };
        lock (_ready)
            _ready.Remove("p-1");
        sink.OnClosed(FrameSinkClosedReason.ProfileDeleted);
        manager.ProfilesChanged();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)), "the screen-off never started");

        lock (_ready)
            _ready.Add("p-1");
        manager.ProfilesChanged();
        await manager.RunPassAsync();
        held.Set();

        await Until(manager, s => s.State == "off");
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running");
    }

    [Fact]
    public async Task Changing_the_profile_moves_the_panel_without_turning_its_screen_off()
    {
        var device = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        var id = (await Until(manager, s => s.State == "running")).Saved.Id;
        var opens = device.Calls.Count(c => c == "open");

        await manager.UpdateAsync(id, s => s with { ProfileId = "p-2" });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (_attached)
                if (_attached is [{ ProfileId: "p-2" }])
                    break;
            Assert.True(DateTime.UtcNow < deadline, "the panel never showed p-2");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal("running", (await manager.ListAsync())[0].State);
        Assert.Equal(opens, device.Calls.Count(c => c == "open"));
        Assert.DoesNotContain(device.Transfers, t => t.Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0])));
    }

    [Fact]
    public async Task Stop_reaches_running_panels_at_once_while_a_pass_waits_on_panels_that_dont_answer()
    {
        var running = Plug(4, "0-1");
        var manager = Manager(Saved());
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running");

        // Three panels whose info query takes its whole 1000 ms timeout (real time), each.
        using var querying = new ManualResetEventSlim(false);
        for (byte address = 5; address <= 7; address++)
        {
            var slow = Plug(address, $"0-{address}", serial: $"SLOW{address}");
            slow.OnWrite = write =>
            {
                querying.Set();
                Thread.Sleep(write.TimeoutMs);
                return UsbStatus.Timeout;
            };
        }
        var screenOffAt = TimeSpan.MinValue;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        running.OnWrite = write =>
        {
            if (screenOffAt == TimeSpan.MinValue && write.Data.AsSpan().SequenceEqual(BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0])))
                screenOffAt = clock.Elapsed;
            return null;
        };
        var pass = manager.RunPassAsync();
        Assert.True(querying.Wait(TimeSpan.FromSeconds(10)), "the slow query never started");

        var stopStarted = clock.Elapsed;
        var ended = await manager.StopAsync(TimeSpan.FromSeconds(3));
        var stopTook = clock.Elapsed - stopStarted;

        Assert.True(ended);
        Assert.True(EndsWithScreenOff(running));
        Assert.True(screenOffAt - stopStarted < TimeSpan.FromMilliseconds(500), $"the screen-off started {(screenOffAt - stopStarted).TotalMilliseconds} ms after the stop");
        Assert.True(stopTook < TimeSpan.FromSeconds(3), $"the stop took {stopTook.TotalMilliseconds} ms");
        await pass.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_turns_every_panel_off_within_its_bound()
    {
        var one = Plug(4, "0-1");
        var two = Plug(5, "0-2", serial: "A1B2C3");
        var manager = Manager(Saved(), Saved(serial: "A1B2C3", port: "0-2") with { Id = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d", ProfileId = "p-2" });
        await manager.RunPassAsync();
        await Until(manager, s => s.State == "running", 0);
        await Until(manager, s => s.State == "running", 1);

        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        Assert.True(EndsWithScreenOff(one));
        Assert.True(EndsWithScreenOff(two));
        Assert.Equal(0, one.OpenHandles + two.OpenHandles);
    }
}
