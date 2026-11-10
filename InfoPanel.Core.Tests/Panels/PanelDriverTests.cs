using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// Panel families as drivers (<see cref="IPanelDriver"/>), over the fake USB backend: the manager offers a listed device
/// only to the drivers whose matches name it, in their order, and the first whose probe says "mine" owns it until it is
/// unplugged; a device no driver names is never opened; the runner drives the driver's <see cref="IPanelSession"/> and
/// its pacer learns from the device time each write reports, not from the time the call took.
/// </summary>
public sealed class PanelDriverTests : IAsyncDisposable
{
    private const ushort Vendor = 0x1d50;
    private const ushort Product = 0x61f0;

    private const ushort OtherVendor = 0x1d50;
    private const ushort OtherProduct = 0x61f1;

    private readonly FakeUsbBackend _backend = new();
    private readonly KeptLog _log = new();
    private readonly ManualResetEventSlim _release = new(false); // frees a probe the test left hanging (see Hang)
    private PanelManager? _manager;
    private volatile PanelSink? _sink;
    private long _frameId;

    public async ValueTask DisposeAsync()
    {
        _release.Set();
        if (_manager != null)
            await _manager.DisposeAsync();
        _release.Dispose();
    }

    private FakeUsbDevice Plug(ushort vendor, ushort product, byte address, string port) =>
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(vendor, product, 0, address, port)));

    private FakeDriver Driver(string family, bool mine = true, ushort vendor = Vendor, ushort product = Product) =>
        new(family, _backend.Clock, mine) { Matches = [new PanelMatch(vendor, product)] };

    private PanelManager Manager(params IPanelDriver[] drivers) => Manager(PanelManager.ProbeDeadline, drivers);

    /// <param name="probeDeadline">How long a probe is given (real time, as the manager's own bounds).</param>
    private PanelManager Manager(TimeSpan probeDeadline, params IPanelDriver[] drivers) => Manager(probeDeadline, TimeSpan.Zero, drivers);

    /// <param name="deadlineDelay">How much later the manager acts on the deadline (a delayed timer).</param>
    private PanelManager Manager(TimeSpan probeDeadline, TimeSpan deadlineDelay, params IPanelDriver[] drivers)
    {
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 60);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: drivers)
        {
            ProbeTimeout = probeDeadline,
            ProbeDeadlineDelay = deadlineDelay,
        };
        _manager.Start(passes: false);
        return _manager;
    }

    /// <summary>
    /// A probe that waits for a device reply that never comes: until its token is cancelled (then it throws, as a
    /// cancelled call does), or until the test ends. <paramref name="probing"/> is set once it waits; <paramref name="cancelled"/>
    /// completes when its token is cancelled.
    /// </summary>
    private PanelProbe Hang(CancellationToken stop, ManualResetEventSlim? probing = null, TaskCompletionSource? cancelled = null)
    {
        probing?.Set();
        WaitHandle.WaitAny([stop.WaitHandle, _release.WaitHandle]);
        if (stop.IsCancellationRequested)
            cancelled?.TrySetResult();
        stop.ThrowIfCancellationRequested();
        return new PanelProbe(false, null, PanelDeviceStatus.Timeout, "freed by the test's end");
    }

    private async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A new 8 × 2 frame to the panel's sink, as the render loop hands one over.</summary>
    private void Deliver()
    {
        if (_sink is not { } sink)
            return;
        var frameId = Interlocked.Increment(ref _frameId);
        using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        pixels.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(pixels);
        var frame = new RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = pixels.Width,
            PixelHeight = pixels.Height,
            ProfileWidth = pixels.Width,
            ProfileHeight = pixels.Height,
            Bounds = [],
            ContentHash = (ulong)frameId, // new pixels every frame
        };
        sink.OnFrame(frame);
        frame.Close();
    }

    [Fact]
    public async Task A_driver_whose_matches_name_a_device_gets_it_and_runs_it_paced_by_the_device_time_it_reports()
    {
        Plug(Vendor, Product, 7, "0-7");
        var driver = Driver("fakepanel");
        var manager = Manager(driver);

        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal(("fakepanel", "F1", "SERIAL1", "0-7", "off"), (found.Saved.Family, found.Saved.Model, found.Saved.Serial, found.Saved.Port, found.State));
        Assert.Equal(new PanelCandidate(Vendor, Product, 0, 7, "0-7"), Assert.Single(driver.Probed));
        Assert.Contains(_log.Lines, l => l.Contains("driver fakepanel"));

        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = 40 });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");
        var session = driver.Session!;
        Assert.Same(driver.Identity, driver.OpenedFor);
        Assert.Equal(40, driver.Options!.Brightness);

        // Each write takes 20 ms of the panel's thread, of which the device reports 7 ms as the transfer: the pacer's
        // first check (a second in) sees writes of 7 ms, and the thread's own rate from the 20 ms frames (0.85 × 50 fps).
        await Until(() =>
        {
            Deliver();
            return Task.FromResult(_log.Lines.Any(l => l.Contains(": pacing ")));
        }, "the pace was never checked");
        Assert.Contains(_log.Lines, l => l.Contains(": pacing 42 fps (writes 7 ms, unloaded 7 ms)"));
        Assert.All(session.Writes, w => Assert.Equal((PanelFrameFormat.Rgb565Le, 8 * 2 * 2, false), w));
        Assert.Equal(42, _sink!.PacedFps);

        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal((true, true), (session.ClosedWithScreenOff, session.Disposed));
        Assert.InRange(session.CloseBudget, TimeSpan.Zero, BeadaPanelSession.ScreenOffBound);
    }

    [Fact]
    public async Task A_device_no_driver_names_is_never_opened_and_never_listed()
    {
        // BeadaPanel's own ids among them: with no BeadaPanel driver in the list, the manager doesn't take them itself.
        var beadaIds = Plug(BeadaPanelProtocol.VendorId, BeadaPanelProtocol.ProductId, 4, "0-4");
        var other = Plug(0x046d, 0xc52b, 5, "0-5");
        var driver = Driver("fakepanel");
        var manager = Manager(driver);

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        Assert.Empty(await manager.ListAsync());
        Assert.Empty(beadaIds.Calls);
        Assert.Empty(other.Calls);
        Assert.Empty(driver.Probed);
    }

    [Fact]
    public async Task A_driver_whose_probe_says_not_mine_passes_the_device_on_to_the_next_driver_that_names_it()
    {
        Plug(Vendor, Product, 7, "0-7");
        var first = Driver("first", mine: false);
        var second = Driver("second");
        var unrelated = Driver("third", vendor: 0x0001, product: 0x0002);
        var manager = Manager(first, second, unrelated);

        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal("second", found.Saved.Family);
        Assert.Equal((1, 1, 0), (first.Probed.Count, second.Probed.Count, unrelated.Probed.Count));

        // The device is the second driver's while it stays plugged in: no driver is asked again.
        await manager.RunPassAsync();
        Assert.Equal((1, 1, 0), (first.Probed.Count, second.Probed.Count, unrelated.Probed.Count));
    }

    [Fact]
    public async Task A_device_every_driver_passes_on_is_left_alone_while_it_stays_plugged_in()
    {
        var device = Plug(Vendor, Product, 7, "0-7");
        var driver = Driver("fakepanel", mine: false);
        var manager = Manager(driver);

        await manager.RunPassAsync();
        await manager.RunPassAsync();

        Assert.Empty(await manager.ListAsync());
        Assert.Single(driver.Probed);
        Assert.Empty(device.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_session_whose_close_or_dispose_throws_still_ends_and_the_engine_runs_on(bool closeThrows, bool disposeThrows)
    {
        Plug(Vendor, Product, 7, "0-7");
        var driver = Driver("fakepanel");
        (driver.CloseThrows, driver.DisposeThrows) = (closeThrows, disposeThrows);
        var manager = Manager(driver);
        await manager.RunPassAsync();
        var id = Assert.Single(await manager.ListAsync()).Saved.Id;

        // Turned off: the session's stop throws, yet the runner ends and the manager lets the panel go.
        await manager.UpdateAsync(id, s => s with { Enabled = true, ProfileId = "p-1" });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");
        await manager.UpdateAsync(id, s => s with { Enabled = false });
        await Until(async () => (await manager.ListAsync())[0].State == "off", "the panel never ended");
        var first = driver.Sessions[0];
        Assert.Equal((true, true), (first.ClosedWithScreenOff, first.Disposed));
        Assert.Contains(_log.Lines, l => l.Contains(closeThrows ? "its screen off failed" : "letting the panel go failed"));

        // The engine runs on: the panel starts again on a new session, and the stop still ends every panel.
        await manager.UpdateAsync(id, s => s with { Enabled = true });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran again");
        Assert.Equal(2, driver.Sessions.Count);
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        Assert.True(driver.Sessions[1].Disposed);
    }

    [Fact]
    public async Task A_probe_waiting_on_a_device_is_cancelled_by_the_stop_which_ends_within_its_bound()
    {
        Plug(Vendor, Product, 7, "0-7");
        using var probing = new ManualResetEventSlim(false);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = Driver("fakepanel");
        driver.OnProbe = (_, stop) => Hang(stop, probing, cancelled);
        var manager = Manager(Timeout.InfiniteTimeSpan, driver); // no deadline: only the stop ends the probe

        var pass = manager.RunPassAsync();
        Assert.True(probing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the probe never started");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ended = await manager.StopAsync(TimeSpan.FromSeconds(3));

        Assert.True(ended, $"the stop didn't end the manager within its bound\nThe log:\n{_log}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the stop took {clock.Elapsed.TotalMilliseconds} ms");
        Assert.True(cancelled.Task.IsCompleted, "the probe's token wasn't cancelled");
        await pass.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_probe_waiting_on_a_device_is_cancelled_by_the_sleep_and_the_wake_probes_it_again()
    {
        Plug(Vendor, Product, 7, "0-7");
        using var probing = new ManualResetEventSlim(false);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = Driver("fakepanel");
        driver.OnProbe = (_, stop) => cancelled.Task.IsCompleted ? null : Hang(stop, probing, cancelled); // the wake's probe answers
        var manager = Manager(Timeout.InfiniteTimeSpan, driver);

        var pass = manager.RunPassAsync();
        Assert.True(probing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the probe never started");
        Assert.True(await manager.SuspendAsync(TimeSpan.FromSeconds(3)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await pass.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Empty(await manager.ListAsync()); // cut short by the sleep: neither found nor left alone

        await manager.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal(("fakepanel", "0-7"), (found.Saved.Family, found.Saved.Port));
        Assert.Equal(2, driver.Probed.Count);
    }

    [Fact]
    public async Task A_probe_past_its_deadline_fails_for_its_device_alone_and_is_tried_again()
    {
        Plug(Vendor, Product, 7, "0-7");
        Plug(OtherVendor, OtherProduct, 8, "0-8");
        var hung = Driver("hungpanel");
        hung.OnProbe = (_, stop) => Hang(stop);
        var other = Driver("otherpanel", vendor: OtherVendor, product: OtherProduct);
        var manager = Manager(TimeSpan.FromMilliseconds(100), hung, other);

        await manager.RunPassAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal(("otherpanel", "0-8"), (found.Saved.Family, found.Saved.Port));

        // A transient failure: offered again on the next pass, logged once.
        await manager.RunPassAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, hung.Probed.Count);
        Assert.Contains("no answer within 0.1 s", Assert.Single(_log.Lines, l => l.Contains("at \"0-7\" couldn't be identified")));
    }

    [Fact]
    public async Task A_probe_that_throws_fails_for_its_device_alone_logged_once_a_change()
    {
        Plug(Vendor, Product, 7, "0-7");
        Plug(OtherVendor, OtherProduct, 8, "0-8");
        var busy = false;
        var thrower = Driver("throwingpanel");
        thrower.OnProbe = (_, _) => busy ? new PanelProbe(false, null, PanelDeviceStatus.Busy, "held by another program") : throw new InvalidOperationException("the driver's bug");
        var other = Driver("otherpanel", vendor: OtherVendor, product: OtherProduct);
        var manager = Manager(thrower, other);

        await manager.RunPassAsync();
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal(("otherpanel", "0-8"), (found.Saved.Family, found.Saved.Port));
        Assert.Equal(2, thrower.Probed.Count);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("a pass failed"));
        Assert.Single(_log.Lines, l => l.Contains("at \"0-7\" couldn't be identified"));

        // Another failure is logged again, once.
        busy = true;
        await manager.RunPassAsync();
        await manager.RunPassAsync();
        Assert.Equal(4, thrower.Probed.Count);
        var lines = _log.Lines.Where(l => l.Contains("at \"0-7\" couldn't be identified")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("the driver's bug", lines[0]);
        Assert.Contains("held by another program", lines[1]);
    }

    [Fact]
    public async Task A_probe_whose_cancellation_throws_at_its_deadline_fails_for_its_device_alone_and_the_engine_runs_on()
    {
        Plug(Vendor, Product, 7, "0-7");
        Plug(OtherVendor, OtherProduct, 8, "0-8");
        var thrower = Driver("throwingpanel");
        thrower.OnProbe = (_, stop) =>
        {
            stop.Register(() => throw new IOException("cancel failed"));
            return Hang(stop);
        };
        var other = Driver("otherpanel", vendor: OtherVendor, product: OtherProduct);
        var manager = Manager(TimeSpan.FromMilliseconds(100), thrower, other);

        await manager.RunPassAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal(("otherpanel", "0-8"), (found.Saved.Family, found.Saved.Port));
        Assert.Contains("no answer within 0.1 s", Assert.Single(_log.Lines, l => l.Contains("at \"0-7\" couldn't be identified")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_probe_whose_cancellation_throws_holds_up_neither_the_stop_nor_the_sleep_of_the_running_panels(bool sleep)
    {
        Plug(Vendor, Product, 7, "0-7");
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>(); // what the stop or the sleep reached, in order
        var driver = Driver("fakepanel");
        driver.OnOpen = stop => stop.Register(() => order.Enqueue("panel asked to stop"));
        var thrower = Driver("throwingpanel", vendor: OtherVendor, product: OtherProduct);
        using var probing = new ManualResetEventSlim(false);
        thrower.OnProbe = (_, stop) =>
        {
            stop.Register(() => order.Enqueue("probe cancelled"));
            stop.Register(() => throw new IOException("cancel failed"));
            return Hang(stop, probing);
        };
        var manager = Manager(Timeout.InfiniteTimeSpan, driver, thrower); // no deadline: only the stop or the sleep ends the probe
        await manager.RunPassAsync();
        var id = Assert.Single(await manager.ListAsync()).Saved.Id;
        await manager.UpdateAsync(id, s => s with { Enabled = true, ProfileId = "p-1" });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");

        Plug(OtherVendor, OtherProduct, 8, "0-8");
        var pass = manager.RunPassAsync();
        Assert.True(probing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the probe never started");
        var ended = sleep ? await manager.SuspendAsync(TimeSpan.FromSeconds(3)) : await manager.StopAsync(TimeSpan.FromSeconds(3));

        Assert.True(ended, $"the running panel didn't end within the bound\nThe log:\n{_log}");
        var session = Assert.Single(driver.Sessions);
        Assert.Equal((true, true), (session.ClosedWithScreenOff, session.Disposed));
        await pass.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The running panel was asked to stop before the probe's token was cancelled (a callback may run after the probe woke).
        await Until(() => Task.FromResult(order.Count == 2), $"the stop reached {string.Join(", ", order)}");
        Assert.Equal(["panel asked to stop", "probe cancelled"], order.ToArray());
    }

    [Fact]
    public async Task A_probe_that_never_ends_makes_the_stop_answer_false_within_its_bound_so_the_backend_is_kept()
    {
        Plug(Vendor, Product, 7, "0-7");
        using var probing = new ManualResetEventSlim(false);
        var driver = Driver("blockedpanel");
        driver.OnProbe = (_, _) =>
        {
            probing.Set();
            _release.Wait(); // blocked in the device (a libusb_open under the backend's lock): its token unheeded, until the test ends
            return new PanelProbe(false, null, PanelDeviceStatus.Timeout, "freed by the test's end");
        };
        var manager = Manager(Timeout.InfiniteTimeSpan, driver); // no deadline: the stop comes while the probe is under way

        var pass = manager.RunPassAsync();
        Assert.True(probing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the probe never started");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ended = await manager.StopAsync(TimeSpan.FromSeconds(3));

        // False: the caller (PanelService.StopAsync) disposes the USB backend only on true, and a probe still inside the
        // backend would hold that disposal for ever.
        Assert.False(ended, $"the stop answered true with a probe still under way\nThe log:\n{_log}");
        // It waits out its whole bound (3 s), kept by a timer whose continuation needs a pool thread, which a full parallel
        // run can hold up by seconds: the slack says only that it didn't wait for the probe, which never ends before the test does.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the stop took {clock.Elapsed.TotalMilliseconds} ms");
        await pass.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // the manager's own thread moved on
        Assert.Contains(_log.Lines, l => l.Contains("a driver's probe was still under way"));
    }

    [Fact]
    public async Task A_mine_answered_after_its_deadline_is_refused_even_when_the_deadline_is_acted_on_late()
    {
        Plug(Vendor, Product, 7, "0-7");
        var late = Driver("latepanel");
        late.OnProbe = (_, _) =>
        {
            Thread.Sleep(600); // past the deadline, before the manager acts on it
            return null; // mine
        };
        var manager = Manager(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), late);

        await manager.RunPassAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(await manager.ListAsync());
        Assert.Contains("no answer within 0.2 s", Assert.Single(_log.Lines, l => l.Contains("at \"0-7\" couldn't be identified")));
    }

    /// <summary>
    /// A driver that says what it was asked: its probe answers <c>mine</c> or not (or <see cref="OnProbe"/>'s answer), its
    /// sessions are <see cref="FakeSession"/>s.
    /// </summary>
    private sealed class FakeDriver(string family, FakeUsbClock clock, bool mine) : IPanelDriver
    {
        private readonly List<PanelCandidate> _probed = [];
        private readonly List<FakeSession> _sessions = [];

        public string Family => family;

        public IReadOnlyList<PanelMatch> Matches { get; init; } = [];

        public PanelIdentity Identity { get; } = new("F1", 8, 2, "SERIAL1", "1.0");

        public IReadOnlyList<PanelCandidate> Probed
        {
            get
            {
                lock (_probed)
                    return [.. _probed];
            }
        }

        public PanelIdentity? OpenedFor { get; private set; }

        public PanelOpenOptions? Options { get; private set; }

        public FakeSession? Session { get; private set; }

        /// <summary>Every session opened, in order.</summary>
        public IReadOnlyList<FakeSession> Sessions
        {
            get
            {
                lock (_sessions)
                    return [.. _sessions];
            }
        }

        /// <summary>The probe's answer instead of <c>mine</c>'s; null from it: <c>mine</c>'s.</summary>
        public Func<PanelCandidate, CancellationToken, PanelProbe?>? OnProbe { get; set; }

        /// <summary>Its sessions' <see cref="IPanelSession.Close"/> and <see cref="IDisposable.Dispose"/> throw (after doing their part).</summary>
        public bool CloseThrows { get; set; }

        public bool DisposeThrows { get; set; }

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
        {
            lock (_probed)
                _probed.Add(device);
            if (OnProbe?.Invoke(device, stop) is { } answer)
                return answer;
            return mine ? new PanelProbe(true, Identity, PanelDeviceStatus.Ok) : new PanelProbe(false, null, PanelDeviceStatus.Ok, "not one of this driver's panels");
        }

        /// <summary>Called with the session's stop token at each <see cref="Open"/> (the runner's <c>RequestStop</c> cancels it).</summary>
        public Action<CancellationToken>? OnOpen { get; set; }

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
        {
            OnOpen?.Invoke(stop);
            OpenedFor = identity;
            Options = options;
            Session = new FakeSession(clock) { CloseThrows = CloseThrows, DisposeThrows = DisposeThrows };
            lock (_sessions)
                _sessions.Add(Session);
            return new PanelOpen(Session, PanelDeviceStatus.Ok, "");
        }
    }

    /// <summary>An 8 × 2 RGB565 panel whose every write takes 20 ms of the caller's time and reports 7 ms of device time.</summary>
    private sealed class FakeSession(FakeUsbClock clock) : IPanelSession
    {
        private readonly List<(PanelFrameFormat Format, int Length, bool Repeat)> _writes = [];

        public PanelCapabilities Capabilities { get; } = new(8, 2, [PanelFrameFormat.Rgb565Le], MaxFps: 60);

        public IReadOnlyList<(PanelFrameFormat Format, int Length, bool Repeat)> Writes
        {
            get
            {
                lock (_writes)
                    return [.. _writes];
            }
        }

        public bool ClosedWithScreenOff { get; private set; }

        public TimeSpan CloseBudget { get; private set; } = TimeSpan.MinValue;

        public bool Disposed { get; private set; }

        public bool CloseThrows { get; init; }

        public bool DisposeThrows { get; init; }

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            lock (_writes)
                _writes.Add((frame.Format, frame.Bytes.Length, frame.Repeat));
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return new PanelWrite(PanelDeviceStatus.Ok, TimeSpan.FromMilliseconds(7));
        }

        public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

        public void Close(bool screenOff, TimeSpan budget)
        {
            ClosedWithScreenOff = screenOff;
            CloseBudget = budget;
            if (CloseThrows)
                throw new InvalidOperationException("the screen-off's transfer failed");
        }

        /// <summary>A throwing dispose is slow too (100 ms); <see cref="Disposed"/> is set only once it has thrown.</summary>
        public void Dispose()
        {
            try
            {
                if (DisposeThrows)
                {
                    Thread.Sleep(100);
                    throw new InvalidOperationException("the device couldn't be let go");
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }
}
