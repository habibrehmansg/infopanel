using InfoPanel.Hosting;
using InfoPanel.Import;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Settings;

namespace InfoPanel.Plugins;

/// <summary>What the engine's plugins are run with.</summary>
public sealed record PluginOptions
{
    /// <summary>False with <c>--no-plugins</c>: no host starts.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The bundled plugins' folder (beside the engine, read-only); null for none.</summary>
    public string? BundledDirectory { get; init; }

    /// <summary>The installed plugins' folder, the data folder's <c>plugins/</c>.</summary>
    public required string InstalledDirectory { get; init; }

    public required PluginHostSettings Host { get; init; }
}

/// <summary>
/// The engine's plugin hosts: one <see cref="PluginHostRunner"/> per package found at start
/// (<see cref="PluginPackages.Find"/>). <see cref="StartAllAsync"/> starts them together; <see cref="StopAllAsync"/>
/// gives every host <see cref="PluginHostTimeouts.EngineStop"/> in all to shut down, in parallel, then kills the rest.
/// A stop latches: a start that comes after it (the engine stopped before its queued start ran) starts nothing.
/// </summary>
public sealed class PluginHostManager : IAsyncDisposable
{
    private readonly PluginOptions _options;
    private readonly IPluginProcessLauncher _launcher;
    private readonly PluginSensorSource _source;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private IReadOnlyList<PluginHostRunner> _runners = [];
    private bool _started;
    private bool _stopping;

    public PluginHostManager(PluginOptions options, IPluginProcessLauncher launcher, PluginSensorSource source, TimeProvider time, ILoggerFactory loggers)
    {
        _options = options;
        _launcher = launcher;
        _source = source;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<PluginHostManager>();
    }

    public PluginOptions Options => _options;

    /// <summary>The packages' runners, in the order they were found.</summary>
    internal IReadOnlyList<PluginHostRunner> Runners
    {
        get
        {
            lock (_lock)
                return _runners;
        }
    }

    /// <summary>Finds the packages and starts a host for each (once; nothing when plugins are off or the engine is stopping).</summary>
    public Task StartAllAsync()
    {
        lock (_lock)
        {
            if (_started || _stopping || !_options.Enabled)
                return Task.CompletedTask;
            _started = true;
            var packages = PluginPackages.Find(_options.BundledDirectory, _options.InstalledDirectory, _logger);
            var hostLogger = _loggers.CreateLogger("InfoPanel.Plugins.PluginHost");
            var runners = packages
                .Select(p => new PluginHostRunner(p, _options.Host, _launcher, _source, _time, hostLogger))
                .ToList();
            _runners = runners;

            _logger.LogInformation("Starting {Count} plugin hosts", runners.Count);
            // Started under the lock (each start only schedules its launch): a stop waits for this, then stops them all.
            return Task.WhenAll(runners.Select(r => r.StartAsync()).ToList());
        }
    }

    /// <summary>
    /// Latches stopping (no start after it starts anything), then stops every host in parallel within
    /// <see cref="PluginHostTimeouts.EngineStop"/> in all and kills the rest; a launch in flight is cancelled.
    /// </summary>
    public async Task StopAllAsync()
    {
        IReadOnlyList<PluginHostRunner> runners;
        lock (_lock)
        {
            _stopping = true;
            runners = _runners;
        }

        if (runners.Count == 0)
            return;
        await Task.WhenAll(runners.Select(r => r.StopAsync(_options.Host.Timeouts.EngineStop))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}

/// <summary>
/// Starts the plugin hosts once the engine listens (never inside the sensors' start-up wait: a host may take 30 s, and
/// the built-in sources come first), and stops them when the engine stops.
/// </summary>
public sealed class PluginService(PluginHostManager manager, IHostApplicationLifetime lifetime, ILogger<PluginService> logger) : IHostedService
{
    private Task _starting = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!manager.Options.Enabled)
        {
            logger.LogInformation("Plugins are off (--no-plugins)");
            return Task.CompletedTask;
        }

        lifetime.ApplicationStarted.Register(() => _starting = Task.Run(manager.StartAllAsync));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Latches stopping first: a start still queued starts nothing, and one under way is stopped, before it is awaited.
        await manager.StopAllAsync().ConfigureAwait(false);
        try
        {
            await _starting.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Starting the plugin hosts failed");
        }
    }
}

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// The plugins: the <c>plugins</c> sensor source (registered after the sources already added, so the built-in ones
    /// come first), the hosts' manager, and the service that starts them once the engine listens.
    /// </summary>
    public static IServiceCollection AddPlugins(this IServiceCollection services, EngineOptions options, DataPaths paths, SettingsStore settings)
    {
        // The 1.x folder start-up checked the data folder against: what a host is given to write in is checked against it too.
        var v1Root = options.V1Dir ?? V1Folder.DefaultRoot();
        services.AddSingleton<PluginSensorSource>();
        services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<PluginSensorSource>());
        services.AddSingleton<IPluginProcessLauncher, PluginProcessLauncher>();
        services.AddSingleton(new PluginOptions
        {
            Enabled = !options.NoPlugins,
            BundledDirectory = options.BundledPlugins ?? Path.Combine(AppContext.BaseDirectory, "plugins"),
            InstalledDirectory = paths.PluginsDirectory,
            Host = new PluginHostSettings
            {
                SettingsDirectory = paths.PluginSettingsDirectory,
                LogsDirectory = paths.PluginLogsDirectory,
                LogLevel = () => settings.Current.Settings.LogLevel.ToString().ToLowerInvariant(),
                WriteCheck = path => V1Folder.FindWriteProblem(v1Root, paths.Root, path),
            },
        });
        services.AddSingleton<PluginHostManager>();
        services.AddHostedService<PluginService>();
        return services;
    }
}
