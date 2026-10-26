using InfoPanel.Sensors;

namespace InfoPanel.Hosting;

/// <param name="StartupWait">How long start-up waits for the sources' discovery before the server starts.</param>
public sealed record SensorServiceOptions(TimeSpan StartupWait)
{
    public static readonly SensorServiceOptions Default = new(TimeSpan.FromSeconds(5));
}

/// <summary>
/// Runs the sensors. Start-up (which runs before the server listens) starts every source and waits up to
/// <see cref="SensorServiceOptions.StartupWait"/> for their discovery, so the first request sees the full catalogue; a
/// source still starting after that is logged and finishes in the background. Then the service steps the hub's simulated
/// keys and samples history every second until shutdown, when it stops the sources.
/// </summary>
public sealed class SensorService(
    SensorHub hub,
    SensorHistory history,
    TimeProvider time,
    ILogger<SensorService> logger,
    SensorServiceOptions? options = null) : BackgroundService
{
    private readonly SensorServiceOptions _options = options ?? SensorServiceOptions.Default;
    private readonly CancellationTokenSource _stopping = new();
    private Task[] _starts = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var sources = hub.Sources.ToArray();
        // Task.Run: a source that blocks before its first await can't hold up the others or the bound.
        _starts = sources.Select(s => Task.Run(() => StartSourceAsync(s, _stopping.Token))).ToArray();
        try
        {
            // Wall-clock bound, not the injected clock: a manual test clock must not turn it into a wait forever.
            // The starts never fault (StartSourceAsync logs), so this ends on completion, timeout or cancellation.
            await Task.WhenAll(_starts).WaitAsync(_options.StartupWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var pending = sources.Where((_, i) => !_starts[i].IsCompleted).Select(s => s.Id);
            logger.LogWarning("Sensor discovery took longer than {Wait}; serving while {Sources} still start",
                _options.StartupWait, string.Join(", ", pending));
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // Cancels like BackgroundService.Dispose but leaves the token source undisposed (it has no timer): the host
    // may stop this service after disposing it.
    public override void Dispose()
    {
        _stopping.Cancel();
        base.Dispose();
    }

    private async Task StartSourceAsync(ISensorSource source, CancellationToken ct)
    {
        try
        {
            await source.StartAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Sensor source {Source} started with {Count} sensors", source.Id, source.GetSensors().Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sensor source {Source} failed to start", source.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(hub.RunAsync(time, stoppingToken), history.RunAsync(stoppingToken)).ConfigureAwait(false);
        }
        finally
        {
            // A source still starting has seen the cancellation; stop it once its start has returned.
            await Task.WhenAll(_starts).ConfigureAwait(false);
            foreach (var source in hub.Sources)
            {
                try
                {
                    await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Sensor source {Source} did not stop cleanly", source.Id);
                }
            }
        }
    }
}
