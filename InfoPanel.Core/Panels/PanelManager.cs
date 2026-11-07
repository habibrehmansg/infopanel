using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Serilog;

namespace InfoPanel.Panels;

/// <summary>What the manager knows of a saved panel now (for the API later, and tests).</summary>
public sealed record PanelStatus(SavedPanel Saved, bool Connected, string State, string? Reason, BeadaPanelInfo? Info, double? Fps, double? FrameTimeMs);

/// <summary>What the manager is given of the engine: profiles, the render service, the frame rate setting.</summary>
public sealed record PanelHost(
    Func<string, bool> ProfileReady,
    Func<IFrameSink, bool> Attach,
    Action<IFrameSink> Detach,
    Action<IFrameSink> RequestChanged,
    Action<IFrameSink> Refresh,
    Func<int> FrameRate);

/// <summary>
/// The USB panels: detection every second and the sessions they want, on one thread of its own (detection passes,
/// sessions' ends and profile changes are queued to it; a timer's pass is dropped while one is queued or running). The
/// only lock is the set of running panels the stop asks, so the stop reaches them whatever the thread is doing.
/// <list type="bullet">
/// <item>Each pass lists the USB devices (what the OS has cached, no I/O to them). A BeadaPanel at a bus and address not
/// seen before is identified (its info query, 1000 ms timeouts, one at a time); a busy one is tried again on the next
/// pass, logged once; an unknown model is logged once and left alone while it stays plugged in.</item>
/// <item>An identified panel is matched to a saved one by its serial (saved panels sharing it by serial and saved port
/// together), else (a saved panel without one) by its USB port; else, or when panels sharing a serial still can't be
/// told apart, it is saved as new, off and without a profile. A matched panel's new port or model is saved.</item>
/// <item>A saved panel runs while it is connected, enabled, and its profile exists and is ready; otherwise it is
/// stopped, its screen off, its settings kept. A session that fails is started again on the next pass, its reason kept
/// and logged once. A profile whose frames stop (deleted, or it can't be drawn) turns the panel off until the profiles
/// change (a change while it was running counts too: a profile deleted and made again is tried).</item>
/// </list>
/// </summary>
public sealed class PanelManager : IAsyncDisposable
{
    /// <summary>How often the USB devices are listed.</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(1);

    private readonly IUsbBackend _backend;
    private readonly PanelStore _store;
    private readonly PanelHost _host;
    private readonly TimeProvider _time;
    private readonly PanelRunnerOptions _runnerOptions;
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly List<SavedPanel> _devices;
    private readonly Dictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<(byte Bus, byte Address), Seen> _seen = [];
    private readonly TaskCompletionSource _threadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _liveLock = new();
    private readonly HashSet<PanelRunner> _live = []; // runners started and not yet ended, for the stop (under _liveLock)
    private Thread? _thread;
    private ITimer? _timer;
    private volatile bool _stopping; // set under _liveLock; read anywhere
    private int _passQueued; // 1 while a timer's pass is queued or running: further ticks are dropped
    private long _profilesGeneration; // how many times the profiles changed (the manager's thread)

    public PanelManager(IUsbBackend backend, PanelStore store, PanelHost host, TimeProvider time, ILogger logger, PanelRunnerOptions? runnerOptions = null)
    {
        if (store.Problem != null)
            throw new ArgumentException("A store the engine can't hold runs no panels.", nameof(store));
        _backend = backend;
        _store = store;
        _host = host;
        _time = time;
        _logger = logger;
        _runnerOptions = runnerOptions ?? new PanelRunnerOptions();
        _devices = [.. store.Devices];
    }

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
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "USB panels: a pass failed");
                }
            }
        }
        finally
        {
            _threadEnded.TrySetResult();
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
                answer.TrySetResult(ask());
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
    /// turning it off by stopping the session (screen off). Null when there is no such panel.
    /// </summary>
    public Task<SavedPanel?> UpdateAsync(string id, Func<SavedPanel, SavedPanel> change) => Ask(() =>
    {
        var index = _devices.FindIndex(d => d.Id == id);
        if (index < 0)
            return null;
        var before = _devices[index];
        var after = change(before) with { Id = before.Id, Family = before.Family, Kept = before.Kept };
        _devices[index] = after;
        Save();
        var runtime = RuntimeOf(id);
        if (runtime.Runner is { } runner && !runtime.Stopping)
        {
            if (after.Enabled != before.Enabled)
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

    /// <summary>The saved panels and their state now.</summary>
    public Task<IReadOnlyList<PanelStatus>> ListAsync() => Ask<IReadOnlyList<PanelStatus>>(() => [.. _devices.Select(StatusOf)]);

    private PanelStatus StatusOf(SavedPanel saved)
    {
        var runtime = RuntimeOf(saved.Id);
        var runner = runtime.Runner;
        var state = runtime.Usb == null ? "notConnected"
            : runner is { Running: true } ? "running"
            : runner != null ? "starting"
            : runtime.Reason != null && Wanted(saved, runtime) ? "failed"
            : "off";
        return new PanelStatus(saved, runtime.Usb != null, state, runner is { Running: true } ? null : runtime.Reason, runtime.Info,
            runner is { Running: true } ? runner.Fps : null, runner is { Running: true } ? runner.FrameTimeMs : null);
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
        if (_stopping)
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

        var panels = listed.Where(d => d.VendorId == BeadaPanelProtocol.VendorId && d.ProductId == BeadaPanelProtocol.ProductId).ToList();

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

        foreach (var usb in panels)
        {
            if (_stopping)
                return; // the stop doesn't wait for the rest of the panels' queries
            if (_seen.TryGetValue((usb.Bus, usb.Address), out var seen) && (seen.DeviceId != null || seen.Ignored))
                continue;
            Identify(usb, seen);
        }

        foreach (var saved in _devices)
            Reconcile(saved);
    }

    private void Identify(UsbDeviceId usb, Seen? seen)
    {
        var identity = BeadaPanelSession.Identify(_backend, usb);
        if (identity.Status != UsbStatus.Ok || identity.Info is not { } info)
        {
            if (identity.Error == BeadaPanelInfoError.UnknownModel)
            {
                _seen[(usb.Bus, usb.Address)] = new Seen(usb) { Ignored = true };
                _logger.Information("A BeadaPanel at {Port} reports model code {Code}, which InfoPanel doesn't know: it isn't driven", usb.PortPath, identity.ModelCode);
                return;
            }
            if (seen == null)
            {
                _seen[(usb.Bus, usb.Address)] = new Seen(usb);
                _logger.Information("A BeadaPanel at {Port} couldn't be identified ({Status}{Error}): tried again every second",
                    usb.PortPath, identity.Status, identity.Error == BeadaPanelInfoError.None ? "" : $", {identity.Error}");
            }
            return;
        }

        var serial = info.Serial.Length > 0 ? info.Serial : identity.UsbSerial is { Length: > 0 } s && s.All(char.IsAsciiLetterOrDigit) ? s : null;
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
                .Where(i => _devices[i].Family == SavedPanel.BeadaPanelFamily && _devices[i].Serial == serial)
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
            index = _devices.FindIndex(d => d.Family == SavedPanel.BeadaPanelFamily && d.Serial == null && d.Port == usb.PortPath && !claimed.Contains(d.Id));

        SavedPanel saved;
        if (index < 0)
        {
            saved = new SavedPanel(Guid.NewGuid().ToString("D"), SavedPanel.BeadaPanelFamily, info.Model.Id, serial, usb.PortPath);
            _devices.Add(saved);
            Save();
            if (ambiguous)
                _logger.Warning("BeadaPanel {Model} at {Port} reports serial {Serial}, which more than one saved panel has, none of them last seen at this port: saved as a new panel, off ({Id})",
                    info.Model.Id, usb.PortPath, serial, saved.Id);
            else if (heldElsewhere)
                _logger.Warning("BeadaPanel {Model} at {Port} reports serial {Serial}, which a panel already connected has: saved as a new panel, off ({Id})",
                    info.Model.Id, usb.PortPath, serial, saved.Id);
            else
                _logger.Information("BeadaPanel {Model} found, serial {Serial}, at {Port}: saved, off ({Id})", info.Model.Id, serial ?? "none", usb.PortPath, saved.Id);
        }
        else
        {
            saved = _devices[index];
            if (saved.Port != usb.PortPath || saved.Model != info.Model.Id || (saved.Serial == null && serial != null))
            {
                saved = saved with { Port = usb.PortPath, Model = info.Model.Id, Serial = saved.Serial ?? serial };
                _devices[index] = saved;
                Save();
            }
            _logger.Information("BeadaPanel {Model} connected at {Port} ({Id}, {State})", info.Model.Id, usb.PortPath, saved.Id, saved.Enabled ? "on" : "off");
        }

        _seen[(usb.Bus, usb.Address)] = new Seen(usb) { DeviceId = saved.Id };
        var runtime = RuntimeOf(saved.Id);
        runtime.Usb = usb;
        runtime.Info = info;
        runtime.Reason = null;
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
        if (!wanted || runtime.Usb is not { } usb)
            return;

        var started = new PanelRunner(_backend, usb, Label(saved.Id), saved.ProfileId!, saved.Brightness, saved.Rotation,
            sink => _host.Attach(sink), sink => _host.Detach(sink), sink => _host.RequestChanged(sink), sink => _host.Refresh(sink), _host.FrameRate,
            _time, _runnerOptions, _logger);
        lock (_liveLock)
        {
            if (_stopping)
                return; // the stop has asked every runner it will wait for
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
        var runtime = RuntimeOf(id);
        if (runtime.Runner != runner)
            return;
        runtime.Runner = null;
        runtime.Stopping = false;
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
    }

    private string Label(string id)
    {
        var saved = _devices.Find(d => d.Id == id);
        return saved == null ? id : $"BeadaPanel {saved.Model ?? "?"} ({id[..8]})";
    }

    /// <summary>
    /// Stops detection and every panel, each turning its screen off, waiting at most <paramref name="bound"/> from the call
    /// for all of them and the manager's thread together (3 s). Running panels are asked to stop at once, whatever the
    /// manager's thread is doing; a detection pass under way stops before its next panel's query. A panel still writing
    /// at the bound is left to its thread. True when every panel's thread and the manager's thread ended.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan bound)
    {
        var started = TimeProvider.System.GetTimestamp();
        _timer?.Dispose();
        List<PanelRunner> runners;
        lock (_liveLock)
        {
            _stopping = true;
            runners = [.. _live];
        }
        foreach (var runner in runners)
            runner.RequestStop();
        _queue.CompleteAdding(); // what is queued still runs (a pass returns at once); nothing more is taken
        if (_thread == null)
            _threadEnded.TrySetResult(); // never started: nothing to wait for

        var all = Task.WhenAll(runners.Select(r => r.Ended).Append(_threadEnded.Task));
        var left = bound - TimeProvider.System.GetElapsedTime(started);
        var ended = left > TimeSpan.Zero && await Task.WhenAny(all, Task.Delay(left, TimeProvider.System)).ConfigureAwait(false) == all;
        if (!ended)
            _logger.Warning("USB panels: {Count} didn't turn off within {Bound} s{Thread}", runners.Count(r => !r.Ended.IsCompleted), bound.TotalSeconds,
                _threadEnded.Task.IsCompleted ? "" : " (a panel's query was still under way)");
        return ended;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_queue.IsAddingCompleted)
            await StopAsync(BeadaPanelSession.ScreenOffBound).ConfigureAwait(false);
    }

    private sealed class Runtime
    {
        public UsbDeviceId? Usb;
        public BeadaPanelInfo? Info;
        public PanelRunner? Runner;
        public bool Stopping;
        public string? Reason;
        public string? ClosedProfile;
        public long ClosedGeneration;
        public long StartedGeneration;
    }

    private sealed class Seen(UsbDeviceId usb)
    {
        public UsbDeviceId Usb { get; } = usb;
        public string? DeviceId { get; init; }
        public bool Ignored { get; init; }
    }
}
