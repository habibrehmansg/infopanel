using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Drives;
using InfoPanel.Sensors.Network;
using InfoPanel.Settings;

namespace InfoPanel.Hosting;

public static class BuiltInSourceServiceCollectionExtensions
{
    /// <summary>
    /// The engine's own sensor sources that read on every operating system: the clock (<c>infopanel.clock</c>), the
    /// drives (<c>infopanel.drives</c>, this computer's volumes, <see cref="DriveSource"/>) and the network
    /// (<c>infopanel.network</c>, its interfaces and, but under <c>--offline</c>, the public address, <see cref="NetworkSource"/>,
    /// its lookup following the settings' <c>sources.network.publicIp</c>),
    /// in that order. Registered
    /// after the platform's sources and before the plugins'. Not in mock mode or while a recording is replayed (whose
    /// sessions say mock): there their keys are what any key no source handles is (simulated in mock mode, else
    /// unavailable).
    /// </summary>
    public static IServiceCollection AddBuiltInSources(this IServiceCollection services, EngineOptions options)
    {
        if (options.Mock || options.ReplaySensors is not null)
            return services;
        services.AddSingleton<ISensorSource>(sp => new ClockSensorSource(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISensorSource>(sp => DriveSource.CreateSystem(sp.GetRequiredService<TimeProvider>()));
        // The public address lookup follows the engine's settings (sources.network.publicIp) when it has them.
        services.AddSingleton<ISensorSource>(sp => NetworkSource.CreateSystem(sp.GetRequiredService<TimeProvider>(), options.Offline, sp.GetService<SettingsStore>()));
        return services;
    }
}
