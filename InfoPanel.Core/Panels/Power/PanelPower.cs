using System;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.BeadaPanel;
using Serilog;

namespace InfoPanel.Panels.Power;

/// <summary>
/// The panels' sleep and wake (InfoPanel 1.4's): before the computer sleeps every panel turns its screen off, at most
/// <see cref="BeadaPanelSession.ScreenOffBound"/>, and none starts; once it is awake, <see cref="ResumeDelay"/> later, a
/// detection pass starts the panels that want to run. A sleep again before then keeps them dark. After
/// <see cref="Dispose"/> (the engine stopping: the source unregistered before the panels stop) nothing is delivered,
/// and a wake already queued on the manager's thread does nothing.
/// </summary>
public sealed class PanelPower(IPowerEvents source, PanelManager manager, TimeProvider time, ILogger logger) : IDisposable
{
    /// <summary>How long after the wake the panels are looked for (InfoPanel 1.4 waited 1 s).</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _disposed = new();
    private long _sleeps; // how many sleeps came: a wake's pass is dropped when another came since
    private bool _started;

    /// <summary>Registers with the source; false (logged) when the platform refused it, and panels just don't follow sleep.</summary>
    public bool Start()
    {
        if (source.Start(new PowerHandlers(Suspending, Resumed)))
        {
            _started = true;
            logger.Information("USB panels: sleep and wake through {Source}", source.Name);
            return true;
        }
        logger.Warning("USB panels: {Source} couldn't be registered; panels stay on while the computer sleeps", source.Name);
        return false;
    }

    private Task<bool> Suspending()
    {
        if (_disposed.IsCancellationRequested)
            return Task.FromResult(true);
        Interlocked.Increment(ref _sleeps);
        return manager.SuspendAsync(BeadaPanelSession.ScreenOffBound);
    }

    private void Resumed()
    {
        if (_disposed.IsCancellationRequested)
            return;
        var sleeps = Interlocked.Read(ref _sleeps);
        _ = WakeAsync(sleeps);
    }

    private async Task WakeAsync(long sleeps)
    {
        try
        {
            await Task.Delay(ResumeDelay, time, _disposed.Token).ConfigureAwait(false);
            // Asleep again since this wake, or disposed since: nothing (checked on the manager's thread, where the wake
            // may have waited in the queue).
            await manager.ResumeAsync(() => !_disposed.IsCancellationRequested && Interlocked.Read(ref _sleeps) == sleeps).ConfigureAwait(false);
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
