using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Power;
using InfoPanel.Panels.Usb;
using Xunit;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels.Power;

/// <summary>
/// Sleep and wake (InfoPanel 1.4's: suspend stops every panel, resume waits 1 s and starts them, session end and quit
/// stop them) through a fake power source, over the fake USB backend: will-sleep turns every panel's screen off before
/// the sleep is allowed, at most 3 s; powered-on runs a detection pass 1 s later (a manual clock); quit turns each panel
/// off once; a sleep during a pass and a wake without a sleep do no harm, nor does a sleep's work that runs after the wake.
/// </summary>
public sealed class PanelPowerTests : IAsyncDisposable
{
    private const string SerialA = "934072005c00481401044f2420751fd6";
    private const string SerialB = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string IdA = "0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60";
    private const string IdB = "1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d";

    private static readonly byte[] BacklightOff = BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]);

    private readonly string _folder = Directory.CreateTempSubdirectory("panels-power-").FullName;
    private readonly FakeUsbBackend _backend = new();
    private readonly FakePowerEvents _source = new();
    private readonly ManualClock _resumeClock = new();
    private readonly ManualResetEventSlim _release = new(false); // lets a hanging write go at the end
    private readonly KeptLog _log = new();
    private PanelManager? _manager;
    private PanelPower? _power;

    public async ValueTask DisposeAsync()
    {
        _release.Set();
        _power?.Dispose();
        if (_manager != null)
            await _manager.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly byte[] RawStart =
        BeadaPanelProtocol.PanelLink(PanelLinkType.StartMediaStream, "video/x-raw, format=RGB16, width=1920, height=480, framerate=0/1"u8);

    /// <summary>A BeadaPanel 8 (model code 21, PanelLink 2, platform 5, 1920 × 480): JPEG when saved <c>auto</c>, else raw.</summary>
    private FakeUsbDevice Plug(byte address, string port, string serial) =>
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 0, address, port))
        {
            Reply = V.InfoReply(2, 5, 21, 1920, 480, serial),
            UsbSerial = serial,
        });

    private static SavedPanel Saved(string id, string serial, string port, PanelEncoding encoding = PanelEncoding.Raw) =>
        new(id, SavedPanel.BeadaPanelFamily, "8", serial, port, true, "p-1", Encoding: encoding);

    private PanelManager Manager(SleepingClock? clock, params SavedPanel[] saved)
    {
        var store = PanelStore.Read(Path.Combine(_folder, PanelStore.FileName), _log.Logger);
        if (saved.Length > 0)
            store.Save(saved);
        var host = new PanelHost(_ => true, _ => true, _ => { }, _ => { }, _ => { }, () => 15);
        _manager = new PanelManager(_backend, store, host, clock ?? (TimeProvider)_backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, clock == null ? null : clock.SinceBoot);
        _manager.Start(passes: false);
        _power = new PanelPower(_source, _manager, _resumeClock, _log.Logger);
        Assert.True(_power.Start());
        return _manager;
    }

    private PanelManager Manager(params SavedPanel[] saved) => Manager(null, saved);

    private async Task<IReadOnlyList<PanelStatus>> Until(PanelManager manager, Func<IReadOnlyList<PanelStatus>, bool> wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var list = await manager.ListAsync();
            if (wanted(list))
                return list;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"the panels never got there: {string.Join("; ", list)}\nThe log:\n{_log}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static bool All(IReadOnlyList<PanelStatus> list, int count, string state) => list.Count == count && list.All(s => s.State == state);

    private static int ScreenOffs(FakeUsbDevice device) => device.Transfers.Count(t => t.Data.AsSpan().SequenceEqual(BacklightOff));

    /// <summary>Raw sessions started: their stream's start tag.</summary>
    private static int Starts(FakeUsbDevice device) => device.Transfers.Count(t => t.Data.AsSpan().SequenceEqual(RawStart));

    /// <summary>The raw stream's stop as its last three transfers: backlight 0, a black frame, the stream's end.</summary>
    private static bool EndsWithScreenOff(FakeUsbDevice device)
    {
        var t = device.Transfers;
        return t.Length >= 3 && t[^3].Data.AsSpan().SequenceEqual(BacklightOff)
            && t[^2].Data.Length == 1920 * 480 * 2 && t[^2].Data.All(b => b == 0) && t[^1].Endpoint == BeadaPanelProtocol.PanelOut;
    }

    [Fact]
    public async Task Will_sleep_turns_every_panel_off_before_it_allows_the_sleep_and_they_stay_off_while_asleep()
    {
        var a = Plug(4, "0-1", SerialA);
        var b = Plug(5, "0-2", SerialB);
        var manager = Manager(Saved(IdA, SerialA, "0-1"), Saved(IdB, SerialB, "0-2"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 2, "running"));

        var (finished, waited) = await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);

        // At the moment the sleep is allowed, both screens are already off and both panels let go.
        Assert.True(EndsWithScreenOff(a) && EndsWithScreenOff(b) && a.OpenHandles == 0 && b.OpenHandles == 0);
        Assert.True(finished);
        Assert.True(waited < PowerEvents.SuspendBound, $"waited {waited}");

        // A pass while asleep (the manager's timer still ticks until the computer sleeps) starts nothing again.
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 2, "off"));
        Assert.Equal((1, 1, 1, 1), (Starts(a), Starts(b), ScreenOffs(a), ScreenOffs(b)));
    }

    [Fact]
    public async Task A_panel_whose_screen_off_hangs_still_lets_the_computer_sleep_at_3_s()
    {
        var a = Plug(4, "0-1", SerialA);
        var hanging = Plug(5, "0-2", SerialB);
        var manager = Manager(Saved(IdA, SerialA, "0-1"), Saved(IdB, SerialB, "0-2"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 2, "running"));
        hanging.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(BacklightOff))
                _release.Wait(); // the screen-off's first write never returns (until the test ends)
            return null;
        };

        var (finished, waited) = await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);

        Assert.False(finished);
        Assert.Equal(TimeSpan.FromSeconds(3), PowerEvents.SuspendBound); // the bound itself: the real-time check is tolerant
        Assert.InRange(waited, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(5)); // 3 s, written out
        Assert.True(EndsWithScreenOff(a)); // the other panel went dark as usual
        Assert.Equal(1, ScreenOffs(hanging)); // the hanging one was asked, and is left to its thread
    }

    [Fact]
    public async Task On_wake_a_pass_runs_1_s_later_and_not_before()
    {
        var a = Plug(4, "0-1", SerialA);
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        await Until(manager, l => All(l, 1, "off"));
        var b = Plug(5, "0-2", SerialB); // plugged in while asleep

        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromMilliseconds(999)); // 1 s, written out: not the constant under test
        var early = await manager.ListAsync();
        Assert.True(All(early, 1, "off"), string.Join("; ", early));
        Assert.Empty(b.Transfers);

        _resumeClock.Advance(TimeSpan.FromMilliseconds(1));
        var awake = await Until(manager, l => l.Count == 2 && l[0].State == "running" && l[1].State == "off");
        Assert.Equal((SerialB, false), (awake[1].Saved.Serial, awake[1].Saved.Enabled)); // the new panel found by the pass
        Assert.Equal(2, Starts(a));
    }

    [Fact]
    public async Task Quit_unregisters_the_source_and_turns_every_panel_off_once()
    {
        var a = Plug(4, "0-1", SerialA);
        var b = Plug(5, "0-2", SerialB);
        var manager = Manager(Saved(IdA, SerialA, "0-1"), Saved(IdB, SerialB, "0-2"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 2, "running"));

        _power!.Dispose(); // the host's stop: the source first, then the panels
        Assert.True(_source.Disposed);
        Assert.True(await manager.StopAsync(BeadaPanelSession.ScreenOffBound));

        Assert.True(EndsWithScreenOff(a) && EndsWithScreenOff(b));
        Assert.Equal((1, 1), (ScreenOffs(a), ScreenOffs(b)));
    }

    [Fact]
    public async Task A_quit_while_asleep_turns_nothing_off_again_and_a_late_wake_does_nothing()
    {
        var a = Plug(4, "0-1", SerialA);
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        await Until(manager, l => All(l, 1, "off"));

        _power!.Dispose();
        Assert.True(await manager.StopAsync(BeadaPanelSession.ScreenOffBound));
        _source.Wake(); // delivered after the quit: ignored
        _resumeClock.Advance(PanelPower.ResumeDelay);

        Assert.True(_source.Disposed);
        Assert.Equal((1, 1), (Starts(a), ScreenOffs(a)));
    }

    [Fact]
    public async Task A_sleep_during_a_pass_is_allowed_at_once_and_the_pass_queries_and_starts_nothing_more()
    {
        var entered = new ManualResetEventSlim(false);
        var a = Plug(4, "0-1", SerialA);
        var b = Plug(5, "0-2", SerialB);
        a.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(V.GetPanelInfo))
            {
                entered.Set();
                _release.Wait(); // the pass is held in the first panel's query
            }
            return null;
        };
        var manager = Manager(Saved(IdA, SerialA, "0-1"), Saved(IdB, SerialB, "0-2"));
        var pass = manager.RunPassAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var (finished, waited) = await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        Assert.True(finished);
        Assert.True(waited < TimeSpan.FromSeconds(1), $"waited {waited}"); // not held behind the pass
        Assert.False(pass.IsCompleted);

        _release.Set();
        await pass;
        var list = await manager.ListAsync();
        Assert.Empty(b.Transfers); // the next panel's query isn't sent
        Assert.Equal(0, Starts(a)); // a's query was under way and ends; no session starts
        Assert.DoesNotContain(list, s => s.State is "running" or "starting");
    }

    [Fact]
    public async Task A_wake_without_a_sleep_runs_a_pass_and_leaves_running_panels_alone()
    {
        var a = Plug(4, "0-1", SerialA);
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        Plug(5, "0-2", SerialB);

        _source.Wake();
        _resumeClock.Advance(PanelPower.ResumeDelay);

        await Until(manager, l => l.Count == 2 && l[0].State == "running" && l[1].State == "off");
        Assert.Equal((1, 0), (Starts(a), ScreenOffs(a)));
    }

    [Fact]
    public async Task A_sleep_within_a_second_of_waking_keeps_the_panels_dark()
    {
        var a = Plug(4, "0-1", SerialA);
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);

        _source.Wake();
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken); // asleep again before the wake's pass
        _resumeClock.Advance(PanelPower.ResumeDelay);
        await manager.RunPassAsync();

        Assert.True(All(await manager.ListAsync(), 1, "off"));
        Assert.Equal((1, 1), (Starts(a), ScreenOffs(a)));
    }

    [Fact]
    public async Task A_sleeps_work_that_runs_only_after_the_wake_turns_nothing_off_and_the_wake_still_looks_for_panels()
    {
        var a = Plug(4, "0-1", SerialA);
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The thread pool is starved at will-sleep: the source lets the computer sleep at its bound, the work not yet run.
        var (finished, work) = await Task.Run(() => _source.SleepHeld(held.Task, TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);
        Assert.False(finished);
        Plug(5, "0-2", SerialB); // plugged in while asleep
        _source.Wake();
        held.SetResult(); // the sleep's work runs only now, after the wake
        await work;
        _resumeClock.Advance(PanelPower.ResumeDelay);

        // The wake's pass ran (it found the new panel) and the old sleep turned nothing off.
        var awake = await Until(manager, l => l.Count == 2 && l[0].State == "running" && l[1].State == "off");
        Assert.Equal(SerialB, awake[1].Saved.Serial);
        Assert.Equal((1, 0), (Starts(a), ScreenOffs(a)));
    }

    /// <summary>
    /// A panel streaming JPEG takes no new stream until 30 s after its stream ended; the computer's time asleep counts
    /// toward it (the manager's clock, macOS's uptime, stops while asleep), so after a long sleep it is back 1 s after the wake.
    /// </summary>
    [Fact]
    public async Task A_jpeg_panel_counts_the_time_asleep_toward_its_wait_after_a_jpeg_stream()
    {
        var clock = new SleepingClock(_backend.Clock);
        Plug(4, "0-1", SerialA);
        var manager = Manager(clock, Saved(IdA, SerialA, "0-1", PanelEncoding.Auto));
        await manager.RunPassAsync();
        Assert.True((await Until(manager, l => All(l, 1, "running")))[0].Jpeg);
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        await Until(manager, l => All(l, 1, "off"));

        clock.SleepFor(TimeSpan.FromMinutes(1));
        _source.Wake();
        _resumeClock.Advance(PanelPower.ResumeDelay);

        Assert.True((await Until(manager, l => All(l, 1, "running")))[0].Jpeg);
    }

    [Fact]
    public async Task After_a_short_sleep_a_jpeg_panel_waits_out_the_rest_of_its_30_s()
    {
        var clock = new SleepingClock(_backend.Clock);
        Plug(4, "0-1", SerialA);
        var manager = Manager(clock, Saved(IdA, SerialA, "0-1", PanelEncoding.Auto));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        await Until(manager, l => All(l, 1, "off"));

        clock.SleepFor(TimeSpan.FromSeconds(10));
        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1));

        await Until(manager, l => All(l, 1, "waiting"));
        _backend.Clock.Advance(TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(1)); // 30 s in all, written out
        await manager.RunPassAsync();
        Assert.True(All(await manager.ListAsync(), 1, "waiting"));
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
    }

    /// <summary>A JPEG panel asleep, its stream ended at the sleep (the wake's pass not yet run).</summary>
    private async Task<(PanelManager, SleepingClock)> AsleepAfterJpeg()
    {
        var clock = new SleepingClock(_backend.Clock);
        Plug(4, "0-1", SerialA);
        var manager = Manager(clock, Saved(IdA, SerialA, "0-1", PanelEncoding.Auto));
        await manager.RunPassAsync();
        Assert.True((await Until(manager, l => All(l, 1, "running")))[0].Jpeg);
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        await Until(manager, l => All(l, 1, "off"));
        return (manager, clock);
    }

    [Fact]
    public async Task A_wall_clock_set_forward_while_asleep_is_not_time_asleep()
    {
        var (manager, clock) = await AsleepAfterJpeg();

        clock.ShiftWall(TimeSpan.FromHours(1)); // a clock correction, no sleep
        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1));

        await Until(manager, l => All(l, 1, "waiting"));
    }

    [Fact]
    public async Task A_wall_clock_set_back_while_asleep_still_counts_the_time_asleep()
    {
        var (manager, clock) = await AsleepAfterJpeg();

        clock.SleepFor(TimeSpan.FromMinutes(1));
        clock.ShiftWall(-TimeSpan.FromHours(1));
        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1));

        Assert.True((await Until(manager, l => All(l, 1, "running")))[0].Jpeg);
    }

    [Fact]
    public async Task A_manager_clock_that_counts_sleep_itself_has_it_added_once()
    {
        var (manager, _) = await AsleepAfterJpeg();

        _backend.Clock.Advance(TimeSpan.FromSeconds(10)); // the timestamps went on while asleep, and so did the boot clock
        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1));

        await Until(manager, l => All(l, 1, "waiting"));
        _backend.Clock.Advance(TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(1));
        await manager.RunPassAsync();
        Assert.True(All(await manager.ListAsync(), 1, "waiting"));
        _backend.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await manager.RunPassAsync();
        await Until(manager, l => All(l, 1, "running"));
    }

    [Fact]
    public async Task A_wake_queued_behind_the_managers_thread_does_nothing_once_disposed()
    {
        var entered = new ManualResetEventSlim(false);
        var a = Plug(4, "0-1", SerialA);
        var b = Plug(5, "0-2", SerialB);
        a.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(V.GetPanelInfo) && !entered.IsSet)
            {
                entered.Set();
                _release.Wait(); // the manager's thread is held in a pass's first query
            }
            return null;
        };
        var manager = Manager(Saved(IdA, SerialA, "0-1"), Saved(IdB, SerialB, "0-2"));
        var pass = manager.RunPassAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);

        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1)); // the wake's pass is queued behind the held one
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _power!.Dispose(); // the engine stopping
        _release.Set();
        await pass;
        var list = await manager.ListAsync(); // after the queued wake

        Assert.Empty(b.Transfers); // no detection pass
        Assert.Equal(0, Starts(a)); // no panel started
        Assert.DoesNotContain(list, s => s.State is "running" or "starting");
    }

    [Fact]
    public async Task A_failed_panel_is_off_with_no_reason_while_asleep_and_tried_again_at_the_wake()
    {
        var a = Plug(4, "0-1", SerialA);
        var failing = true;
        a.OnWrite = write => failing && write.Data.AsSpan().SequenceEqual(RawStart) ? UsbStatus.Io : null;
        var manager = Manager(Saved(IdA, SerialA, "0-1"));
        await manager.RunPassAsync();
        Assert.NotNull((await Until(manager, l => All(l, 1, "failed")))[0].Reason);

        await Task.Run(_source.Sleep, TestContext.Current.CancellationToken);
        var asleep = await Until(manager, l => l.Count == 1 && l[0].State != "failed");

        Assert.Equal(("off", null), (asleep[0].State, asleep[0].Reason));
        failing = false;
        _source.Wake();
        _resumeClock.Advance(TimeSpan.FromSeconds(1));
        await Until(manager, l => All(l, 1, "running"));
    }
}
