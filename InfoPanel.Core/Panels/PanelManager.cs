using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Serilog;

namespace InfoPanel.Panels;

/// <summary>
/// What the manager knows of a saved panel now (the API's entry, and tests). <see cref="Jpeg"/>: whether a running panel
/// streams JPEG. <see cref="ReasonCode"/>: the failure's code (<c>busy</c>, <c>access</c>, <c>notSupported</c>,
/// <c>timeout</c>, <c>io</c>), or <c>settling</c> while a start waits out <see cref="PanelManager.JpegSettle"/>.
/// </summary>
public sealed record PanelStatus(SavedPanel Saved, bool Connected, string State, string? Reason, BeadaPanelInfo? Info, double? Fps, double? FrameTimeMs, bool? Jpeg = null,
    string? ReasonCode = null);

/// <summary>
/// What <see cref="PanelManager.SaveAsync"/> did: the panel's state as saved, or, when nothing was saved, the
/// <see cref="Existing"/> saved panel's id (the same 1.x device, or the same family and serial).
/// </summary>
public sealed record PanelSave(PanelStatus? Saved, string? Existing);

/// <summary>What the manager is given of the engine: profiles, the render service, the frame rate setting.</summary>
public sealed record PanelHost(
    Func<string, bool> ProfileReady,
    Func<IFrameSink, bool> Attach,
    Action<IFrameSink> Detach,
    Action<IFrameSink> RequestChanged,
    Action<IFrameSink> Refresh,
    Func<int> FrameRate);

/// <summary>A client's change to the saved panels that the panels file couldn't take: nothing changed, live or saved.</summary>
public sealed class PanelsNotSavedException(Exception inner) : Exception("The panels file couldn't be written; nothing changed.", inner);

/// <summary>
/// The USB panels: detection every second and the sessions they want, on one thread of its own (detection passes,
/// sessions' ends and profile changes are queued to it; a timer's pass is dropped while one is queued or running). The
/// only lock is the set of running panels the stop asks, so the stop reaches them whatever the thread is doing.
/// <list type="bullet">
/// <item>Each pass lists the USB devices (what the OS has cached, no I/O to them). A device some driver's matches name
/// (<see cref="IPanelDriver"/>; the built-in list, <see cref="BuiltInDrivers"/>, is BeadaPanel's), at a bus and address
/// not seen before, is offered to those drivers in their order, one device at a time, and is the first's whose probe says
/// "mine" (BeadaPanel's: its info query, 1000 ms timeouts) until it is unplugged; one a probe couldn't ask (busy, a
/// probe that threw or passed its <see cref="ProbeDeadline"/>; the stop and the sleep cut a probe short too) is offered
/// again on the next pass, logged once a change; one every driver passes on (a BeadaPanel of an unknown model) is logged
/// once and left alone while it stays plugged in. A device no driver names is never opened.</item>
/// <item>An identified panel is matched to a saved one by its serial (saved panels sharing it by serial and saved port
/// together), else (a saved panel without one) by its USB port; else, or when panels sharing a serial still can't be
/// told apart, it is saved as new, off and without a profile. A matched panel's new port or model is saved.</item>
/// <item>A saved panel runs while it is connected, enabled, and its profile exists and is ready; otherwise it is
/// stopped, its screen off, its settings kept. A session that fails is started again on the next pass, its reason kept
/// and logged once. A profile whose frames stop (deleted, or it can't be drawn) turns the panel off until the profiles
/// change (a change while it was running counts too: a profile deleted and made again is tried).</item>
/// <item>Sleep and wake (<see cref="Power.PanelPower"/>): <see cref="SuspendAsync"/> turns every panel off and holds
/// passes and starts until <see cref="ResumeAsync"/>, which runs a pass.</item>
/// </list>
/// </summary>
public sealed class PanelManager : IAsyncDisposable
{
    /// <summary>How often the USB devices are listed.</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(1);

    /// <summary>The most identities forgotten panels are remembered by in a run (<see cref="ForgetAsync"/>): past it no saved panel adopts one.</summary>
    public const int MaxForgotten = 256;

    /// <summary>How long a driver's probe is given (real time, as the USB transfers' own timeouts).</summary>
    public static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(5);

    /// <summary>The probe's deadline this manager gives (tests: a shorter one); <see cref="ProbeDeadline"/> unless set.</summary>
    internal TimeSpan ProbeTimeout { get; init; } = ProbeDeadline;

    /// <summary>Tests: how much later than <see cref="ProbeTimeout"/> the manager acts on the deadline (a delayed timer, a slow thread); zero unless set.</summary>
    internal TimeSpan ProbeDeadlineDelay { get; init; }

    private readonly IUsbBackend _backend;
    private readonly IReadOnlyList<IPanelDriver> _drivers;
    private readonly PanelStore _store;
    private readonly PanelHost _host;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan>? _sinceBoot; // a monotonic clock that counts sleep (PowerEvents.SinceBoot); null: _time does
    private readonly PanelRunnerOptions _runnerOptions;
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly List<SavedPanel> _devices;
    private readonly Dictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<(byte Bus, byte Address), Seen> _seen = [];
    private readonly Dictionary<UsbDeviceId, long> _forgottenJpegEnds = []; // a forgotten panel's JPEG end, kept while it stays plugged in
    private readonly HashSet<string> _forgotten = new(StringComparer.Ordinal); // the forgotten panels' identities this run (IdentitiesOf), at most MaxForgotten: never adopted
    private bool _adoptionOff; // an identity couldn't be remembered (_forgotten is full): no saved panel adopts one for the rest of the run
    // Set when the manager's thread ends, to the probes it left running (_probesLeft then): done once they have all ended.
    private readonly TaskCompletionSource<Task> _threadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _liveLock = new();
    private readonly HashSet<PanelRunner> _live = []; // runners started and not yet ended, for the stop (under _liveLock)
    private Thread? _thread;
    private ITimer? _timer;
    private volatile bool _stopping; // set under _liveLock; read anywhere
    private volatile bool _suspended; // the computer is going to sleep or asleep (SuspendAsync to ResumeAsync); set under _liveLock
    private (long Timestamp, TimeSpan SinceBoot) _asleepFrom; // when SuspendAsync came, on both clocks (under _liveLock)
    private CancellationTokenSource _probeStop = new(); // cancelled by the stop and the sleep, a new one at the wake (under _liveLock)
    private readonly Dictionary<(byte Bus, byte Address), Task> _probesLeft = []; // probes left running past their deadline, by device (this thread)
    private int _passQueued; // 1 while a timer's pass is queued or running: further ticks are dropped
    private long _profilesGeneration; // how many times the profiles changed (the manager's thread)
    private long _version; // how many times the list changed this run (written on the manager's thread)
    private IReadOnlyList<PanelStatus> _published; // the list as the version last counted it (the manager's thread)
    private long? _statsCountedAt; // when a change of the stats alone was last counted

    /// <param name="sinceBoot">
    /// A monotonic clock that keeps counting while the computer sleeps (<see cref="Power.PowerEvents.SinceBoot"/>), for
    /// the time asleep when <paramref name="time"/>'s timestamps stop then (macOS's do); null when they don't.
    /// </param>
    /// <param name="drivers">The panel drivers, in the order a device is offered to them; null: <see cref="BuiltInDrivers"/>.</param>
    public PanelManager(IUsbBackend backend, PanelStore store, PanelHost host, TimeProvider time, ILogger logger, PanelRunnerOptions? runnerOptions = null,
        Func<TimeSpan>? sinceBoot = null, IReadOnlyList<IPanelDriver>? drivers = null)
    {
        _drivers = drivers ?? BuiltInDrivers(backend, time);
        _sinceBoot = sinceBoot;
        if (store.Problem != null)
            throw new ArgumentException("A store the engine can't hold runs no panels.", nameof(store));
        _backend = backend;
        _store = store;
        _host = host;
        _time = time;
        _logger = logger;
        _runnerOptions = runnerOptions ?? new PanelRunnerOptions();
        _devices = [.. store.Devices];
        _published = [.. _devices.Select(StatusOf)];
    }

    /// <summary>The drivers built into the engine, in the order a device is offered to them: BeadaPanel's.</summary>
    public static IReadOnlyList<IPanelDriver> BuiltInDrivers(IUsbBackend backend, TimeProvider time) => [new BeadaPanelDriver(backend, time)];

    /// <summary>A listed USB device as a driver is offered it.</summary>
    internal static PanelCandidate CandidateOf(UsbDeviceId usb) => new(usb.VendorId, usb.ProductId, usb.Bus, usb.Address, usb.PortPath);

    /// <summary>The drivers whose matches name <paramref name="usb"/>'s ids, in their order.</summary>
    private IEnumerable<IPanelDriver> DriversOf(UsbDeviceId usb) =>
        _drivers.Where(d => d.Matches.Any(m => m.VendorId == usb.VendorId && m.ProductId == usb.ProductId));

    /// <summary>A family's name in the log: BeadaPanel's as it is written, any other's id.</summary>
    private static string NameOf(string family) => family == SavedPanel.BeadaPanelFamily ? "BeadaPanel" : family;

    /// <summary>How many times the saved panels and their state changed this run: 0 at start (<see cref="Changed"/>).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Raised on the manager's thread with the new <see cref="Version"/> after a change to what <see cref="ListAsync"/>
    /// answers; a change of the running panels' stats alone counts at most once a second.
    /// </summary>
    public event Action<long>? Changed;

    /// <summary>How often a change of the stats alone counts as a change of the list, at most.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    /// <summary>Starts the manager's thread; with <paramref name="passes"/>, a pass every <see cref="PassInterval"/> (the first at once).</summary>
    public void Start(bool passes = true)
    {
        _thread = new Thread(Work) { IsBackground = true, Name = "Panels" };
        _thread.Start();
        if (passes)
            _timer = _time.CreateTimer(_ => TimerPass(), null, TimeSpan.Zero, PassInterval);
    }

    /// <summary>A timer's pass, unless one is still queued or running (a slow pass doesn't pile more up behind it).</summary>
    private void TimerPass()
    {
        if (Interlocked.CompareExchange(ref _passQueued, 1, 0) != 0)
            return;
        Post(() =>
        {
            try
            {
                Pass();
            }
            finally
            {
                Volatile.Write(ref _passQueued, 0);
            }
        });
    }

    private void Work()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                    Publish();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "USB panels: a pass failed");
                }
            }
        }
        finally
        {
            // No probe starts once this thread has ended (they start only on it), so these are all that may still run.
            _threadEnded.TrySetResult(Task.WhenAll(_probesLeft.Values.Select(p => p.ContinueWith(static _ => { }, TaskScheduler.Default))));
        }
    }

    private bool Post(Action action)
    {
        try
        {
            _queue.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // stopped
        }
    }

    /// <summary>Runs <paramref name="ask"/> on the manager's thread; cancelled once the manager has stopped.</summary>
    private Task<T> Ask<T>(Func<T> ask)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = Post(() =>
        {
            try
            {
                var result = ask();
                Publish(); // counted before the caller has the answer, so a list it reads next has the version of its change
                answer.TrySetResult(result);
            }
            catch (Exception ex)
            {
                answer.TrySetException(ex);
            }
        });
        if (!posted)
            answer.TrySetCanceled();
        return answer.Task;
    }

    /// <summary>Runs a detection pass now, on the manager's thread.</summary>
    public Task RunPassAsync() => Ask(() =>
    {
        Pass();
        return true;
    });

    /// <summary>The profiles changed (created, deleted, their state): a pass, and a panel turned off by its profile may run again.</summary>
    public void ProfilesChanged() => Post(() =>
    {
        _profilesGeneration++;
        Pass();
    });

    /// <summary>The frame rate setting changed: every running panel's loop is told.</summary>
    public void FrameRateChanged() => Post(() =>
    {
        foreach (var runtime in _runtime.Values)
            if (runtime.Runner?.Sink is { } sink)
                _host.RequestChanged(sink);
    });

    /// <summary>
    /// Changes a saved panel's settings and saves them; applied at once: brightness between frames, rotation from the
    /// next frame, another profile by moving the panel's sink to it (the session kept, the screen not turned off), and
    /// turning it off by stopping the session (screen off); another encoding stops the session too, and the next pass
    /// starts it in the new one. Null when there is no such panel.
    /// </summary>
    /// <exception cref="PanelsNotSavedException">The panels file couldn't be written: nothing changed.</exception>
    public Task<SavedPanel?> UpdateAsync(string id, Func<SavedPanel, SavedPanel> change) => Ask(() =>
    {
        var index = _devices.FindIndex(d => d.Id == id);
        if (index < 0)
            return null;
        var before = _devices[index];
        var after = change(before) with { Id = before.Id, Family = before.Family, Kept = before.Kept };
        List<SavedPanel> next = [.. _devices];
        next[index] = after;
        SaveChange(next);
        _devices[index] = after;
        var runtime = RuntimeOf(id);
        if (runtime.Runner is { } runner && !runtime.Stopping)
        {
            if (after.Enabled != before.Enabled || after.Encoding != before.Encoding)
            {
                runtime.Stopping = true;
                runner.RequestStop();
            }
            else
            {
                runner.Brightness = after.Brightness;
                if (after.Rotation != before.Rotation)
                    runner.Rotation = after.Rotation;
                if (after.ProfileId is { } profile && profile != before.ProfileId && Wanted(after, runtime))
                    runner.ProfileId = profile; // else the reconcile below stops it
            }
        }
        Reconcile(after);
        return (SavedPanel?)after;
    });

    /// <summary>
    /// Forgets a saved panel: its session stopped with its screen off, its settings removed and saved. A connected panel
    /// is identified again once its session has ended, and saved as new (off, without a profile): for the rest of the
    /// run no saved panel adopts it (<see cref="Adopt"/>). False when there is no such panel.
    /// </summary>
    /// <exception cref="PanelsNotSavedException">The panels file couldn't be written: nothing changed.</exception>
    public Task<bool> ForgetAsync(string id) => Ask(() =>
    {
        var index = _devices.FindIndex(d => d.Id == id);
        if (index < 0)
            return false;
        List<SavedPanel> next = [.. _devices];
        next.RemoveAt(index);
        SaveChange(next);
        Remember(IdentitiesOf(_devices[index].Serial, _devices[index].Port));
        _devices.RemoveAt(index);
        var runtime = RuntimeOf(id);
        if (runtime.Runner is { } runner)
        {
            runtime.Forgotten = true; // its USB device is left alone until the session ends (Ended)
            if (!runtime.Stopping)
            {
                runtime.Stopping = true;
                runner.RequestStop();
            }
        }
        else
            Release(id);
        return true;
    });

    /// <summary>
    /// Saves a panel that need not be plugged in (a client's), as <see cref="SaveAsync"/> does; null when a saved panel
    /// is the same 1.x device or has its family and serial.
    /// </summary>
    /// <exception cref="PanelsNotSavedException">The panels file couldn't be written: nothing changed.</exception>
    public async Task<PanelStatus?> AddAsync(SavedPanel panel) => (await SaveAsync(panel)).Saved;

    /// <summary>
    /// Saves a panel that need not be plugged in (a client's), after the saved ones, written before it is listed, and
    /// answers its state as saved (in the same turn of the manager's thread, so a change queued meanwhile can't come
    /// between); it is matched to a panel plugged in as every saved panel is, and, without a serial, as
    /// <see cref="Adopt"/> says. Nothing is saved when a saved panel is the same 1.x device (<see
    /// cref="PanelV1Identity.SameDevice"/>) or has its family and serial: the answer names that panel, the first with the
    /// 1.x device, else the one with the serial.
    /// </summary>
    /// <exception cref="PanelsNotSavedException">The panels file couldn't be written: nothing changed.</exception>
    public Task<PanelSave> SaveAsync(SavedPanel panel) => Ask(() =>
    {
        var existing = (panel.V1 is { } v1 ? _devices.FirstOrDefault(d => d.V1?.SameDevice(v1.DeviceId) == true) : null)
            ?? (panel.Serial != null ? _devices.FirstOrDefault(d => d.Family == panel.Family && d.Serial == panel.Serial) : null);
        if (existing != null)
            return new PanelSave(null, existing.Id);
        List<SavedPanel> next = [.. _devices, panel];
        SaveChange(next);
        _devices.Add(panel);
        _logger.Information("Panel {Panel}: saved by a client, serial {Serial}{FromV1}", Label(panel.Id), panel.Serial ?? "none",
            panel.V1 != null ? ", from a 1.x device" : "");
        return new PanelSave(StatusOf(panel), null);
    });

    /// <summary>
    /// A forgotten panel's runtime and its USB device dropped: the next pass identifies the device as new. The panel's
    /// <see cref="JpegSettle"/> is the panel's own, not its settings': kept for the device found again.
    /// </summary>
    private void Release(string id)
    {
        if (_runtime.Remove(id, out var runtime) && runtime.Usb is { } usb && runtime.JpegEndedAt is { } ended)
            _forgottenJpegEnds[usb] = ended;
        foreach (var (key, seen) in _seen.ToList())
            if (seen.DeviceId == id)
                _seen.Remove(key);
    }

    /// <summary>
    /// After each of the manager's actions: when the list it answers changed, <see cref="Version"/> goes up and
    /// <see cref="Changed"/> is raised; a change of the stats alone only once <see cref="StatsInterval"/> has passed since
    /// the last one counted (it counts later, after another action, when it is still there).
    /// </summary>
    private void Publish()
    {
        List<PanelStatus> now = [.. _devices.Select(StatusOf)];
        if (now.SequenceEqual(_published))
            return;
        if (now.Select(WithoutStats).SequenceEqual(_published.Select(WithoutStats)))
        {
            if (_statsCountedAt is { } counted && _time.GetElapsedTime(counted) < StatsInterval)
                return;
            _statsCountedAt = _time.GetTimestamp();
        }
        _published = now;
        var version = Interlocked.Increment(ref _version);
        Changed?.Invoke(version);
    }

    private static PanelStatus WithoutStats(PanelStatus status) => status with { Fps = null, FrameTimeMs = null };

    /// <summary>The saved panels and their state now.</summary>
    public Task<IReadOnlyList<PanelStatus>> ListAsync() => Ask<IReadOnlyList<PanelStatus>>(() => [.. _devices.Select(StatusOf)]);

    private PanelStatus StatusOf(SavedPanel saved)
    {
        var runtime = RuntimeOf(saved.Id);
        var runner = runtime.Runner;
        // Asleep, a panel without a session is off with no reason (a failure is kept for the wake's start).
        var asleep = _suspended && runtime.Usb != null && runner == null;
        var waiting = !asleep && runtime.Usb != null && runner == null && Wanted(saved, runtime) && SettleLeft(runtime) != null;
        var state = runtime.Usb == null ? "notConnected"
            : runner is { Running: true } ? "running"
            : runner != null ? "starting"
            : asleep ? "off"
            : waiting ? "waiting"
            : runtime.Reason != null && Wanted(saved, runtime) ? "failed"
            : "off";
        var reason = runner is { Running: true } || asleep ? null
            : waiting ? $"the panel takes no new stream until {JpegSettle.TotalSeconds:0} s after its JPEG stream ended"
            : runtime.Reason;
        var code = reason == null ? null : waiting ? "settling" : runtime.ReasonCode;
        return new PanelStatus(saved, runtime.Usb != null, state, reason, runtime.Info,
            runner is { Running: true } ? runner.Fps : null, runner is { Running: true } ? runner.FrameTimeMs : null,
            runner is { Running: true } ? runner.Jpeg : null, code);
    }

    /// <summary>
    /// How long a panel is given after a JPEG session ends before a new session starts: a T113 BeadaPanel takes no
    /// transfer for 20 to 30 s after a JPEG stream ends (measured on a BeadaPanel 8, whether the stream ends with the type 6
    /// end tag or a PanelLinkReset, and whatever the next stream is), so a start sooner fails. Only this engine's own
    /// sessions are counted; a session that never got past its start tag counts too once it was JPEG.
    /// </summary>
    public static readonly TimeSpan JpegSettle = TimeSpan.FromSeconds(30);

    /// <summary>What is left of <see cref="JpegSettle"/> since the panel's last JPEG session ended; null when nothing is.</summary>
    private TimeSpan? SettleLeft(Runtime runtime)
    {
        if (runtime.JpegEndedAt is not { } ended)
            return null;
        var left = JpegSettle - _time.GetElapsedTime(ended);
        return left > TimeSpan.Zero ? left : null;
    }

    private Runtime RuntimeOf(string id)
    {
        if (!_runtime.TryGetValue(id, out var runtime))
            _runtime[id] = runtime = new Runtime();
        return runtime;
    }

    /// <summary>The manager's thread: detection, then every saved panel's session to match what it wants.</summary>
    private void Pass()
    {
        if (_stopping || _suspended)
            return;

        IReadOnlyList<UsbDeviceId> listed;
        try
        {
            listed = _backend.ListDevices();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "USB panels: the USB devices couldn't be listed");
            return;
        }

        // Only the devices some driver's matches name: no other is ever opened.
        var panels = listed.Where(d => DriversOf(d).Any()).ToList();
        foreach (var gone in _forgottenJpegEnds.Keys.Where(u => !panels.Contains(u)).ToList())
            _forgottenJpegEnds.Remove(gone); // unplugged: it starts afresh

        // Gone: no longer listed at that bus and address (or another device is there now).
        foreach (var (key, seen) in _seen.ToList())
        {
            if (panels.Contains(seen.Usb))
                continue;
            _seen.Remove(key);
            if (seen.DeviceId is { } id && _runtime.TryGetValue(id, out var runtime) && runtime.Usb == seen.Usb)
            {
                runtime.Usb = null;
                _logger.Information("Panel {Panel}: unplugged", Label(id));
                runtime.Runner?.RequestStop();
            }
        }

        var unmatched = new List<Unmatched>();
        foreach (var usb in panels)
        {
            if (_stopping || _suspended)
                return; // the stop and the sleep don't wait for the rest of the panels' queries (those found are found again)
            if (_seen.TryGetValue((usb.Bus, usb.Address), out var seen) && (seen.DeviceId != null || seen.Ignored))
                continue;
            if (Identify(usb, seen) is { } panel)
                unmatched.Add(panel);
        }
        Adopt(unmatched);

        foreach (var saved in _devices)
            Reconcile(saved);
    }

    /// <summary>
    /// A panel identified in this pass that matches no saved panel, while a saved one might take it (<see cref="Adopt"/>):
    /// <paramref name="Ambiguous"/> or <paramref name="HeldElsewhere"/> when its serial clashes, so it is new whatever.
    /// </summary>
    private sealed record Unmatched(UsbDeviceId Usb, IPanelDriver Driver, PanelIdentity Identity, bool Ambiguous, bool HeldElsewhere)
    {
        public string? Serial => Identity.Serial;
    }

    /// <summary>
    /// Every identity a panel has for <see cref="_forgotten"/>: its serial (when known) and its port (when it has had
    /// one), both, because a later pass may read only one of them: a serial that couldn't be read now, or one that
    /// couldn't be then. None for a saved panel never plugged in, without a serial.
    /// </summary>
    private static List<string> IdentitiesOf(string? serial, string? port)
    {
        List<string> identities = [];
        if (serial != null)
            identities.Add("serial:" + serial);
        if (port != null)
            identities.Add("port:" + port);
        return identities;
    }

    /// <summary>
    /// Remembers a forgotten panel by every identity it had, up to <see cref="MaxForgotten"/> in all. One that doesn't
    /// fit is not remembered, and nothing already remembered is dropped: adoption stops for the rest of the run instead,
    /// logged once.
    /// </summary>
    private void Remember(List<string> identities)
    {
        var fresh = identities.Where(i => !_forgotten.Contains(i)).ToList();
        if (_forgotten.Count + fresh.Count <= MaxForgotten)
        {
            _forgotten.UnionWith(fresh);
            return;
        }
        if (!_adoptionOff)
        {
            _adoptionOff = true;
            _logger.Information("Adoption is off until the engine restarts: {Count} forgotten panel identities are remembered, the most there is room for", _forgotten.Count);
        }
    }

    /// <summary>
    /// A saved panel of <paramref name="family"/> and <paramref name="model"/> that has never been plugged in and has no
    /// serial (a client saved it so, from what another program knew of it): it may take a panel that matches no saved one
    /// (<see cref="Adopt"/>).
    /// </summary>
    private List<int> NeverSeen(string family, string model) =>
        [.. Enumerable.Range(0, _devices.Count).Where(i => _devices[i] is { Serial: null, Port: null } d && d.Family == family && d.Model == model)];

    /// <summary>
    /// The panels found in this pass that match no saved one (serial clashes included), while a saved panel without a
    /// serial that has never been plugged in might take them: such a saved panel takes one only when that can't be
    /// mistaken: it is the only such saved panel of the model, the panel is the only one of its model found unmatched in
    /// the pass (every one counted, a serial clash too), its serial doesn't clash, it isn't a panel forgotten this run
    /// (<see cref="ForgetAsync"/>: its serial or its port is one a forgotten panel had), and the memory of those isn't
    /// full (<see cref="MaxForgotten"/>: then none is adopted). Otherwise each is saved as new and the saved one stays not connected.
    /// </summary>
    private void Adopt(List<Unmatched> unmatched)
    {
        foreach (var panel in unmatched)
        {
            var family = panel.Driver.Family;
            var model = panel.Identity.Model;
            var name = NameOf(family);
            var candidates = NeverSeen(family, model);
            var found = unmatched.Count(p => p.Driver.Family == family && p.Identity.Model == model);
            var clashes = panel.Ambiguous || panel.HeldElsewhere;
            var forgotten = IdentitiesOf(panel.Serial, panel.Usb.PortPath).Any(_forgotten.Contains);
            var index = candidates.Count == 1 && found == 1 && !clashes && !forgotten && !_adoptionOff ? candidates[0] : -1;
            if (index < 0 && !clashes)
            {
                if (forgotten)
                    _logger.Information("{Family:l} {Model} at {Port}: forgotten this run, so no saved panel takes it", name, model, panel.Usb.PortPath);
                else if (_adoptionOff)
                    _logger.Information("{Family:l} {Model} at {Port}: no saved panel takes a panel any more (the forgotten panels' memory is full)", name, model, panel.Usb.PortPath);
                else
                    _logger.Information("{Family:l} {Model} at {Port}: {Count} saved panels of its model without a serial that were never plugged in, and {Found} found: none takes it",
                        name, model, panel.Usb.PortPath, candidates.Count, found);
            }
            Claim(panel.Usb, panel.Driver, panel.Identity, index, panel.Ambiguous, panel.HeldElsewhere, adopted: index >= 0);
        }
    }

    /// <summary>
    /// One driver's probe of <paramref name="device"/>, its token cancelled by the stop, the sleep, and its deadline
    /// (<see cref="ProbeTimeout"/>, real time). A probe that throws, or that hasn't answered by its deadline, is a failure
    /// of that device alone (status <see cref="PanelDeviceStatus.Io"/> or <see cref="PanelDeviceStatus.Timeout"/>), as one
    /// that couldn't ask it is. <c>Failure</c>: what the failure was (null: none), for its log line once a change;
    /// <c>Error</c>: the driver's exception.
    /// </summary>
    private (PanelProbe Probe, string? Failure, Exception? Error) ProbeOf(IPanelDriver driver, PanelCandidate device)
    {
        var key = (device.Bus, device.Address);
        if (_probesLeft.TryGetValue(key, out var left))
        {
            if (!left.IsCompleted)
                return (new PanelProbe(false, null, PanelDeviceStatus.Timeout, "its last probe hasn't ended"), "deadline", null);
            _probesLeft.Remove(key);
        }

        // The probe runs on the thread pool; this thread enforces its deadline by a bounded wait, so no cancellation runs
        // on a timer thread, and the driver's cancellation callbacks run guarded (CancelProbe).
        var stop = _probeStop.Token; // replaced only on this thread (the wake)
        var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var cut = source.Token.WaitHandle; // set by the stop, the sleep and the deadline's cancel
        var started = TimeProvider.System.GetTimestamp(); // the clock of StopAsync's bound
        var probing = Task.Run(() => driver.Probe(device, source.Token));
        var bound = ProbeTimeout == Timeout.InfiniteTimeSpan ? ProbeTimeout : ProbeTimeout + ProbeDeadlineDelay;
        WaitHandle.WaitAny([((IAsyncResult)probing).AsyncWaitHandle, cut], bound);
        // An answer counts only if it came within the deadline, whenever this thread got to look (a delayed thread).
        var inTime = probing.IsCompleted
            && (ProbeTimeout == Timeout.InfiniteTimeSpan || TimeProvider.System.GetElapsedTime(started) <= ProbeTimeout);
        if (!probing.IsCompleted)
        {
            CancelProbe(source, probing);
            ((IAsyncResult)probing).AsyncWaitHandle.WaitOne(ProbeGrace); // a probe that heeds its token ends before the device is offered on
            if (!probing.IsCompleted)
                _probesLeft[key] = probing; // not offered again until it ends
        }
        else
            source.Dispose();

        var timeout = (new PanelProbe(false, null, PanelDeviceStatus.Timeout,
            stop.IsCancellationRequested ? "cut short" : $"no answer within {ProbeTimeout.TotalSeconds:0.#} s"), "deadline", (Exception?)null);
        if (!inTime)
            return timeout;
        if (probing.Exception?.InnerException is { } ex)
            return ex is OperationCanceledException && stop.IsCancellationRequested ? timeout
                : (new PanelProbe(false, null, PanelDeviceStatus.Io, $"its driver failed: {ex.Message}"), $"threw {ex.GetType().FullName}", ex);
        if (probing.Result is not { } probe)
            return (new PanelProbe(false, null, PanelDeviceStatus.Io, "its driver failed: its probe answered nothing"), $"threw {typeof(InvalidOperationException).FullName}", null);
        return (probe, probe.Status == PanelDeviceStatus.Ok ? null : $"{probe.Status}: {probe.Message}", null);
    }

    /// <summary>How long a probe cancelled by its deadline, the stop or the sleep is given to end before the manager moves on.</summary>
    private static readonly TimeSpan ProbeGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cancels a probe's token off the manager's thread, guarded: a driver's cancellation callback that throws is logged,
    /// one that blocks holds up only its pool thread. The probe's source goes once the probe has ended.
    /// </summary>
    private void CancelProbe(CancellationTokenSource source, Task probing)
    {
        _ = Task.Run(() =>
        {
            Cancel(source);
            probing.ContinueWith(t =>
            {
                _ = t.Exception; // observed: a cancelled probe's failure is no failure of the engine's
                source.Dispose();
            }, TaskScheduler.Default);
        });
    }

    /// <summary>Cancels <paramref name="source"/>, a driver's callback that throws logged (the stop, the sleep, a deadline).</summary>
    private void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (Exception ex) // AggregateException: the callbacks' failures
        {
            _logger.Warning(ex, "USB panels: a driver's probe failed when it was cancelled");
        }
    }

    /// <summary>
    /// Offers a device to the drivers whose matches name it, in their order, until one's probe says "mine" (a probe that
    /// couldn't ask it, threw or passed its deadline ends the offer until the next pass); then matches the panel found to
    /// a saved one, or saves it as new; null once that is done (or no driver took it). A panel that matches none while a saved panel without a serial
    /// that was never plugged in might take it is answered instead, for <see cref="Adopt"/>.
    /// </summary>
    private Unmatched? Identify(UsbDeviceId usb, Seen? seen)
    {
        var device = CandidateOf(usb);
        IPanelDriver? driver = null;
        PanelIdentity? identity = null;
        List<string> passedOn = [];
        foreach (var offered in DriversOf(usb))
        {
            var (probe, failure, error) = ProbeOf(offered, device);
            if (_stopping || _suspended)
                return null; // cut short: the wake's pass offers the device again (the stop ends the passes)
            if (probe.Status != PanelDeviceStatus.Ok)
            {
                // A failure of this device alone: offered again on the next pass, logged once a change.
                if (seen == null)
                    _seen[(usb.Bus, usb.Address)] = seen = new Seen(usb);
                if (seen.Failure != failure)
                {
                    seen.Failure = failure;
                    _logger.Write(error == null ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Warning, error,
                        "A {Family:l} at {Port} couldn't be identified ({Why:l}): tried again every second",
                        NameOf(offered.Family), usb.PortPath, probe.Message ?? probe.Status.ToString());
                }
                return null;
            }
            if (probe.Mine && probe.Identity is { } mine)
            {
                (driver, identity) = (offered, mine);
                break;
            }
            passedOn.Add(probe.Message ?? $"not {offered.Family}'s");
        }
        if (driver == null || identity == null)
        {
            _seen[(usb.Bus, usb.Address)] = new Seen(usb) { Ignored = true };
            _logger.Information("The USB device {Ids:l} at {Port} isn't driven while it stays plugged in: {Why:l}",
                $"{usb.VendorId:x4}:{usb.ProductId:x4}", usb.PortPath, string.Join("; ", passedOn));
            return null;
        }
        _logger.Information("The USB device {Ids:l} at {Port}: driver {Driver:l}, built in", $"{usb.VendorId:x4}:{usb.ProductId:x4}", usb.PortPath, driver.Family);

        var family = driver.Family;
        var serial = identity.Serial;
        var claimed = _runtime.Where(r => r.Value.Usb != null).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var index = -1;
        var ambiguous = false;
        var heldElsewhere = false;
        if (serial != null)
        {
            // By serial, when one saved panel has it. When saved panels share it (panels that report one serial), whether
            // it is shared is decided over every saved panel, matched or not, and only a unique saved port matches; a
            // panel that still isn't told apart is saved as new. A unique serial whose panel is already connected
            // elsewhere is another panel reporting it: matched by port as a panel without a serial is, else new.
            var bySerial = Enumerable.Range(0, _devices.Count)
                .Where(i => _devices[i].Family == family && _devices[i].Serial == serial)
                .ToList();
            if (bySerial.Count == 1)
            {
                heldElsewhere = claimed.Contains(_devices[bySerial[0]].Id);
                index = heldElsewhere ? -1 : bySerial[0];
            }
            else if (bySerial.Count > 1)
            {
                var byPort = bySerial.Where(i => _devices[i].Port == usb.PortPath && !claimed.Contains(_devices[i].Id)).ToList();
                index = byPort.Count == 1 ? byPort[0] : -1;
                ambiguous = index < 0;
            }
        }
        if (index < 0 && !ambiguous)
            index = _devices.FindIndex(d => d.Family == family && d.Serial == null && d.Port == usb.PortPath && !claimed.Contains(d.Id));

        if (index < 0 && NeverSeen(family, identity.Model).Count > 0)
            return new Unmatched(usb, driver, identity, ambiguous, heldElsewhere); // counted by Adopt, then claimed
        Claim(usb, driver, identity, index, ambiguous, heldElsewhere, adopted: false);
        return null;
    }

    /// <summary>
    /// The panel <paramref name="driver"/> identified at <paramref name="usb"/> taken as the saved panel at
    /// <paramref name="index"/> (its new port, model and serial saved), or, at -1, saved as new, off and without a profile.
    /// </summary>
    private void Claim(UsbDeviceId usb, IPanelDriver driver, PanelIdentity identity, int index, bool ambiguous, bool heldElsewhere, bool adopted)
    {
        var name = NameOf(driver.Family);
        var model = identity.Model;
        var serial = identity.Serial;
        SavedPanel saved;
        if (index < 0)
        {
            saved = new SavedPanel(Guid.NewGuid().ToString("D"), driver.Family, model, serial, usb.PortPath);
            _devices.Add(saved);
            Save();
            if (ambiguous)
                _logger.Warning("{Family:l} {Model} at {Port} reports serial {Serial}, which more than one saved panel has, none of them last seen at this port: saved as a new panel, off ({Id})",
                    name, model, usb.PortPath, serial, saved.Id);
            else if (heldElsewhere)
                _logger.Warning("{Family:l} {Model} at {Port} reports serial {Serial}, which a panel already connected has: saved as a new panel, off ({Id})",
                    name, model, usb.PortPath, serial, saved.Id);
            else
                _logger.Information("{Family:l} {Model} found, serial {Serial}, at {Port}: saved, off ({Id})", name, model, serial ?? "none", usb.PortPath, saved.Id);
        }
        else
        {
            saved = _devices[index];
            if (saved.Port != usb.PortPath || saved.Model != model || (saved.Serial == null && serial != null))
            {
                saved = saved with { Port = usb.PortPath, Model = model, Serial = saved.Serial ?? serial };
                _devices[index] = saved;
                Save();
            }
            if (adopted)
                _logger.Information("{Family:l} {Model} at {Port}, serial {Serial}: taken as the saved panel {Id}, saved without a serial and never plugged in before",
                    name, model, usb.PortPath, serial ?? "none", saved.Id);
            _logger.Information("{Family:l} {Model} connected at {Port} ({Id}, {State})", name, model, usb.PortPath, saved.Id, saved.Enabled ? "on" : "off");
        }

        _seen[(usb.Bus, usb.Address)] = new Seen(usb) { DeviceId = saved.Id };
        var runtime = RuntimeOf(saved.Id);
        runtime.Usb = usb;
        runtime.Driver = driver;
        runtime.Identity = identity;
        runtime.Info = (driver as BeadaPanelDriver)?.InfoOf(identity);
        runtime.Reason = null;
        if (_forgottenJpegEnds.Remove(usb, out var ended) && !(runtime.JpegEndedAt > ended))
            runtime.JpegEndedAt = ended;
    }

    private void Save()
    {
        try
        {
            _store.Save(_devices);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "USB panels: the panels file couldn't be written; the change holds for this run");
        }
    }

    /// <summary>
    /// A client's change, written before it is applied: one the file can't take changes nothing. One the file took whose
    /// folder couldn't be flushed afterwards is applied, as the file holds it (logged), so the file and the run agree.
    /// </summary>
    /// <exception cref="PanelsNotSavedException">The file couldn't be written.</exception>
    private void SaveChange(List<SavedPanel> next)
    {
        try
        {
            _store.Save(next);
        }
        catch (PanelsNotFlushedException ex)
        {
            _logger.Warning(ex, "USB panels: the panels file holds the change, but its folder couldn't be flushed; the change is made");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Error(ex, "USB panels: the panels file couldn't be written; the change wasn't made");
            throw new PanelsNotSavedException(ex);
        }
    }

    /// <summary>
    /// Connected, enabled, and its profile ready, unless that profile's frames stopped and the profiles haven't changed
    /// since its runner started (a profile deleted and made again is a change, so its new document is tried).
    /// </summary>
    private bool Wanted(SavedPanel saved, Runtime runtime) =>
        runtime.Usb != null && saved.Enabled && saved.ProfileId is { } profile
        && !(runtime.ClosedProfile == profile && runtime.ClosedGeneration == _profilesGeneration)
        && _host.ProfileReady(profile);

    private void Reconcile(SavedPanel saved)
    {
        var runtime = RuntimeOf(saved.Id);
        var wanted = Wanted(saved, runtime);
        if (runtime.Runner is { } runner)
        {
            if (runner.Running)
                runtime.Reason = null; // started again since it failed
            if (!wanted && !runtime.Stopping)
            {
                runtime.Stopping = true;
                runner.RequestStop();
            }
            return;
        }
        if (!wanted || runtime.Usb is not { } usb || runtime.Driver is not { } driver || runtime.Identity is not { } identity)
            return;

        // After a JPEG stream the panel takes no new one for a while: the start waits for it (the next passes try again).
        if (SettleLeft(runtime) is { } left)
        {
            if (!runtime.Settling)
                _logger.Information("Panel {Panel}: starting again in {Seconds:0} s, once the panel takes a stream after its JPEG one", Label(saved.Id), Math.Ceiling(left.TotalSeconds));
            runtime.Settling = true;
            return;
        }
        runtime.Settling = false;

        // A panel that refused the JPEG start once this run is raw until the engine restarts, whatever its setting.
        var encoding = runtime.JpegRefused ? PanelEncoding.Raw : saved.Encoding;
        var started = new PanelRunner(driver, CandidateOf(usb), identity, Label(saved.Id), saved.ProfileId!, saved.Brightness, saved.Rotation,
            sink => _host.Attach(sink), sink => _host.Detach(sink), sink => _host.RequestChanged(sink), sink => _host.Refresh(sink), _host.FrameRate,
            _time, _runnerOptions, _logger, encoding);
        lock (_liveLock)
        {
            if (_stopping || _suspended)
                return; // the stop (or the sleep) has asked every runner it will wait for
            _live.Add(started);
        }
        runtime.Runner = started;
        runtime.Stopping = false;
        runtime.StartedGeneration = _profilesGeneration;
        started.Ended.ContinueWith(t =>
        {
            lock (_liveLock)
                _live.Remove(started);
            Post(() => Ended(saved.Id, started, t.Result));
        }, TaskContinuationOptions.ExecuteSynchronously);
        started.Start();
    }

    private void Ended(string id, PanelRunner runner, PanelRunEnd end)
    {
        if (!_runtime.TryGetValue(id, out var runtime) || runtime.Runner != runner)
            return;
        runtime.Runner = null;
        runtime.Stopping = false;
        if (runner.Jpeg)
            runtime.JpegEndedAt = _time.GetTimestamp();
        if (runtime.Forgotten)
        {
            Release(id);
            return;
        }
        if (runner.JpegRefused != null)
            runtime.JpegRefused = true;
        if (end.SinkClosed is { } closed)
        {
            // Held against this profile only while the profiles are as they were when the runner started.
            runtime.ClosedProfile = runner.ProfileId;
            runtime.ClosedGeneration = runtime.StartedGeneration;
            _logger.Information("Panel {Panel}: profile {ProfileId} stopped giving frames ({Reason}): screen off until the profiles change", Label(id), runner.ProfileId, closed);
            return;
        }
        if (!end.Failed)
        {
            runtime.Reason = null;
            return;
        }
        if (runtime.Reason != end.Message)
            _logger.Information("Panel {Panel}: {Message}; tried again on the next pass", Label(id), end.Message);
        runtime.Reason = end.Message;
        runtime.ReasonCode = end.Status switch
        {
            UsbStatus.Busy => "busy",
            UsbStatus.Access => "access",
            UsbStatus.NotSupported => "notSupported",
            UsbStatus.Timeout => "timeout",
            _ => "io",
        };
    }

    private string Label(string id)
    {
        var saved = _devices.Find(d => d.Id == id);
        return saved == null ? id : $"{NameOf(saved.Family)} {saved.Model ?? "?"} ({id[..8]})";
    }

    /// <summary>
    /// Stops detection and every panel, each turning its screen off, waiting at most <paramref name="bound"/> from the call
    /// for all of them and the manager's thread together (3 s). Running panels are asked to stop at once, whatever the
    /// manager's thread is doing; a detection pass under way stops before its next panel's query, and a probe's token is
    /// cancelled (a driver waiting on its device stops waiting). A panel still writing, or a probe still under way (one
    /// its grace left behind, a driver blocked in its device), at the bound is left to its thread. True when every
    /// panel's thread, the manager's thread and every probe it started ended; false keeps the USB backend in use, so
    /// the caller mustn't dispose it (a probe inside it would hold that disposal for ever).
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan bound)
    {
        var started = TimeProvider.System.GetTimestamp();
        _timer?.Dispose();
        List<PanelRunner> runners;
        CancellationTokenSource probeStop;
        lock (_liveLock)
        {
            _stopping = true;
            runners = [.. _live];
            probeStop = _probeStop;
        }
        foreach (var runner in runners)
            runner.RequestStop();
        // Then a probe under way stops waiting on its device: its callbacks off this path, guarded.
        _ = Task.Run(() => Cancel(probeStop));
        _queue.CompleteAdding(); // what is queued still runs (a pass returns at once); nothing more is taken
        if (_thread == null)
            _threadEnded.TrySetResult(Task.CompletedTask); // never started: nothing to wait for

        var probesEnded = _threadEnded.Task.Unwrap(); // the manager's thread, then the probes it left running
        var all = Task.WhenAll(runners.Select(r => r.Ended).Append(probesEnded));
        var left = bound - TimeProvider.System.GetElapsedTime(started);
        var ended = left > TimeSpan.Zero && await Task.WhenAny(all, Task.Delay(left, TimeProvider.System)).ConfigureAwait(false) == all;
        if (!ended)
            _logger.Warning("USB panels: {Count} didn't turn off within {Bound} s{Thread}", runners.Count(r => !r.Ended.IsCompleted), bound.TotalSeconds,
                !_threadEnded.Task.IsCompleted ? " (a panel's query was still under way)"
                : !probesEnded.IsCompleted ? " (a driver's probe was still under way; the USB backend is kept)" : "");
        return ended;
    }

    /// <summary>
    /// The computer is going to sleep: every running panel is asked to stop at once, whatever the manager's thread is
    /// doing, each turning its screen off; until <see cref="ResumeAsync"/> no pass runs (one under way stops before its
    /// next panel's query, its probe's token cancelled; the wake's pass offers that device again) and no panel starts. Waits at most <paramref name="bound"/> from the call for the panels; a
    /// panel still writing then is left to its thread. True when every panel asked has ended. <paramref name="stillAsleep"/>,
    /// asked with the sleep's lock held: false (a wake came since the sleep) does nothing.
    /// </summary>
    public async Task<bool> SuspendAsync(TimeSpan bound, Func<bool>? stillAsleep = null)
    {
        var started = TimeProvider.System.GetTimestamp();
        List<PanelRunner> runners;
        CancellationTokenSource probeStop;
        lock (_liveLock)
        {
            if (_stopping)
                return true; // the stop has them
            if (stillAsleep?.Invoke() == false)
            {
                _logger.Information("USB panels: the computer woke before the sleep's turn-off ran; nothing turned off");
                return true;
            }
            if (!_suspended && _sinceBoot != null)
                _asleepFrom = (_time.GetTimestamp(), _sinceBoot());
            _suspended = true;
            runners = [.. _live];
            probeStop = _probeStop;
        }
        foreach (var runner in runners)
            runner.RequestStop();
        // Then a probe under way stops waiting on its device (the wake's pass offers the device again): off this path, guarded.
        _ = Task.Run(() => Cancel(probeStop));
        _logger.Information("USB panels: the computer is going to sleep; {Count} turned off", runners.Count);
        if (runners.Count == 0)
            return true;

        var all = Task.WhenAll(runners.Select(r => r.Ended));
        var left = bound - TimeProvider.System.GetElapsedTime(started);
        var ended = left > TimeSpan.Zero && await Task.WhenAny(all, Task.Delay(left, TimeProvider.System)).ConfigureAwait(false) == all;
        if (!ended)
            _logger.Warning("USB panels: {Count} didn't turn off within {Bound} s; the computer sleeps anyway", runners.Count(r => !r.Ended.IsCompleted), bound.TotalSeconds);
        return ended;
    }

    /// <summary>
    /// The computer is awake: passes run again and one runs now, starting the panels that want to run. The time asleep
    /// counts toward a panel's <see cref="JpegSettle"/> (the manager's clock may stop while the computer sleeps, as
    /// macOS's does; the boot clock it was given doesn't, and a wall-clock correction moves neither). A wake without a
    /// sleep is just a pass. <paramref name="stillAwake"/>, asked
    /// with the sleep's lock held: false (a sleep came since the wake) does nothing.
    /// </summary>
    public Task ResumeAsync(Func<bool>? stillAwake = null) => Ask(() =>
    {
        lock (_liveLock)
        {
            if (_stopping || stillAwake?.Invoke() == false)
                return false;
            if (_suspended)
            {
                _suspended = false;
                // The sleep's cancelled token goes: probes from now on get one only the stop or the next sleep cancels.
                // The old one isn't disposed: the sleep's cancel may still be running its callbacks on the pool.
                _probeStop = new CancellationTokenSource();
                // The time the boot clock counted and the manager's didn't: the time asleep, when the manager's stops then.
                var uncounted = _sinceBoot == null ? TimeSpan.Zero
                    : _sinceBoot() - _asleepFrom.SinceBoot - _time.GetElapsedTime(_asleepFrom.Timestamp);
                if (uncounted > TimeSpan.Zero)
                {
                    var ticks = (long)(uncounted.TotalSeconds * _time.TimestampFrequency);
                    foreach (var runtime in _runtime.Values)
                        if (runtime.JpegEndedAt is { } ended)
                            runtime.JpegEndedAt = ended - ticks;
                    foreach (var (usb, ended) in _forgottenJpegEnds.ToList())
                        _forgottenJpegEnds[usb] = ended - ticks;
                }
                _logger.Information("USB panels: the computer is awake; looking for panels");
            }
        }
        Pass();
        return true;
    });

    public async ValueTask DisposeAsync()
    {
        if (!_queue.IsAddingCompleted)
            await StopAsync(BeadaPanelSession.ScreenOffBound).ConfigureAwait(false);
    }

    private sealed class Runtime
    {
        public UsbDeviceId? Usb;
        public IPanelDriver? Driver; // the driver whose probe took the device, with Usb
        public PanelIdentity? Identity; // what its probe found
        public BeadaPanelInfo? Info; // a BeadaPanel's info reply, for the API
        public PanelRunner? Runner;
        public bool Stopping;
        public string? Reason;
        public string? ReasonCode; // Reason's code, for the API
        public bool Forgotten; // forgotten while its session ran: released when it ends
        public string? ClosedProfile;
        public long ClosedGeneration;
        public long StartedGeneration;
        public bool JpegRefused; // the panel didn't take a JPEG start this run: raw from now on
        public long? JpegEndedAt; // when its last JPEG session ended (the manager's clock), for JpegSettle
        public bool Settling; // a start is waiting out JpegSettle (logged once)
    }

    private sealed class Seen(UsbDeviceId usb)
    {
        public UsbDeviceId Usb { get; } = usb;
        public string? DeviceId { get; init; }
        public bool Ignored { get; init; }
        public string? Failure { get; set; } // its probe's last failure, logged once a change (the manager's thread)
    }
}
