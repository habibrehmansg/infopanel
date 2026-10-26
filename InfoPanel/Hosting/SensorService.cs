using InfoPanel.Sensors;

namespace InfoPanel.Hosting;

/// <summary>
/// Runs the sensors: starts every source, then steps the hub's simulated keys and samples history every second
/// until shutdown, when it stops the sources.
/// </summary>
public sealed class SensorService(SensorHub hub, SensorHistory history, TimeProvider time, ILogger<SensorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var source in hub.Sources)
        {
            await source.StartAsync(stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Sensor source {Source} started with {Count} sensors", source.Id, source.GetSensors().Count);
        }

        try
        {
            await Task.WhenAll(hub.RunAsync(time, stoppingToken), history.RunAsync(stoppingToken)).ConfigureAwait(false);
        }
        finally
        {
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
