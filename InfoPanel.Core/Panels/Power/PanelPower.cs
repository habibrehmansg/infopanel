using System;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.BeadaPanel;
using Serilog;

namespace InfoPanel.Panels.Power;

/// <summary>
/// The panels' sleep and wake (InfoPanel 1.4's): before the computer sleeps every panel turns its screen off, at most
/// <see cref="BeadaPanelSession.ScreenOffBound"/>, and none starts; once it is awake, <see cref="ResumeDelay"/> later, a
/// detection pass starts the panels that want to run. A sleep again before then keeps them dark. Sleep and wake are
/// ordered as they arrive: a sleep's work that only runs after a later wake (a starved thread pool) turns nothing off. After
/// <see cref="Dispose"/> (the engine stopping: the source unregistered before the panels stop) nothing is delivered,
/// and a wake already queued on the manager's thread does nothing.
/// </summary>
public sealed class PanelPower(IPowerEvents source, PanelManager manager, TimeProvider time, ILogger logger) : IDisposable
{
    /// <summary>How long after the wake the panels are looked for (InfoPanel 1.4 waited 1 s).</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _order = new();
    private long _events; // the power events, numbered as they arrive on the source's thread (under _order)
    private long _lastSleep, _lastWake; // the latest of each's number (under _order)
    private bool _started;

    /// <summary>Registers with the source; false (logged) when the platform refused it, and panels just don't follow sleep.</summary>
    public bool Start()
    {
        if (source.Start(new PowerHandlers(SleepArriving, Resumed)))
        {
            _started = true;
            logger.Information("USB panels: sleep and wake through {Source}", source.Name);
            return true;
        }
        logger.Warning("USB panels: {Source} couldn't be registered; panels stay on while the computer sleeps", source.Name);
        return false;
    }

    private long Arrived(bool sleep)
    {
        lock (_order)
        {
            var number = ++_events;
            if (sleep)
                _lastSleep = number;
            else
                _lastWake = number;
            return number;
        }
    }

    /// <summary>A will-sleep arrived: numbered now, on the source's thread; its work may run later, off it.</summary>
    private Func<Task<bool>> SleepArriving()
    {
        if (_disposed.IsCancellationRequested)
            return () => Task.FromResult(true);
        var sleep = Arrived(sleep: true);
        return () => Suspend(sleep);
    }

    private Task<bool> Suspend(long sleep)
    {
        if (_disposed.IsCancellationRequested)
            return Task.FromResult(true);
        // Awake again since this sleep arrived (its work waited for a thread past the source's bound): nothing to turn
        // off (checked under the manager's sleep lock, as the wake's pass checks its own).
        return manager.SuspendAsync(BeadaPanelSession.ScreenOffBound, () =>
        {
            lock (_order)
                return _lastWake < sleep;
        });
    }

    private void Resumed()
    {
        if (_disposed.IsCancellationRequested)
            return;
        _ = WakeAsync(Arrived(sleep: false));
    }

    private async Task WakeAsync(long wake)
    {
        try
        {
            await Task.Delay(ResumeDelay, time, _disposed.Token).ConfigureAwait(false);
            // Asleep again since this wake, or disposed since: nothing (checked on the manager's thread, where the wake
            // may have waited in the queue).
            await manager.ResumeAsync(() =>
            {
                lock (_order)
                    return !_disposed.IsCancellationRequested && _lastSleep < wake;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // disposed, or the manager stopped
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "USB panels: the wake's pass failed");
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested)
            return;
        _disposed.Cancel();
        source.Dispose();
        if (_started)
            logger.Information("USB panels: {Source} unregistered", source.Name);
    }
}
