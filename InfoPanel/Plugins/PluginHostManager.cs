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

    /// <summary>The user's on and off, <c>{data}/plugin-state.json</c> (<see cref="PluginStateFile"/>); null keeps them for the run only.</summary>
    public string? StateFile { get; init; }

    /// <summary>The operating system packages run on, as a package's <c>Platforms</c> names it: <c>windows</c>, <c>macos</c>, <c>linux</c> (else <c>other</c>).</summary>
    public string Platform { get; init; } = PluginPlatforms.Current;

    /// <summary>How the plugin state file is written (tests give a storage fault); null: <see cref="InfoPanel.Documents.AtomicFile"/>.</summary>
    internal Action<string, byte[], Action>? StateWriter { get; init; }
}

/// <summary>How turning a package on or off, or reloading it, went.</summary>
public enum PluginSwitch
{
    Done,

    /// <summary>No package in that folder.</summary>
    NotFound,

    /// <summary>It doesn't run on this operating system.</summary>
    Unsupported,

    /// <summary>A reload of a package that is turned off.</summary>
    NotRunning,

    /// <summary>The plugin state file can't be changed this run (<see cref="PluginStateUnavailableException"/>).</summary>
    StateUnavailable,

    /// <summary>The choice couldn't be written; nothing changed.</summary>
    WriteFailed,
}

/// <summary>
/// The engine's plugin hosts: one <see cref="PluginHostRunner"/> per package found at start
/// (<see cref="PluginPackages.Find"/>). <see cref="StartAllAsync"/> starts those the user hasn't turned off
/// (<see cref="PluginStateFile"/>) and that run on this operating system (their <c>PluginInfo.ini</c>'s <c>Platforms</c>);
/// <see cref="StopAllAsync"/> gives every host <see cref="PluginHostTimeouts.EngineStop"/> in all to shut down, in
/// parallel, then kills the rest. A stop latches: a start that comes after it (the engine stopped before its queued start
/// ran) starts nothing. One plugin id, one package (<see cref="Claim"/>): of two packages whose hosts load an id, the one
/// earlier in the order found keeps it, whichever loaded first.
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
    // The one-id rule's decisions, one at a time.
    private readonly Lock _claims = new();
    // On, off and reload, one at a time.
    private readonly SemaphoreSlim _switching = new(1, 1);
    private IReadOnlyList<PluginHostRunner> _runners = [];
    private PluginStateFile _state = PluginStateFile.Read(null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
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
            _state = PluginStateFile.Read(_options.StateFile, _logger);
            if (_options.StateWriter is { } writer)
                _state.Writer = writer;
            var hostLogger = _loggers.CreateLogger("InfoPanel.Plugins.PluginHost");
            var runners = packages
                .Select((p, order) => new PluginHostRunner(p, _options.Host, _launcher, _source, _time, hostLogger) { Order = order, Claim = Claim })
                .ToList();
            _runners = runners;

            // Only the packages the user hasn't turned off, and that run here.
            foreach (var r in runners.Where(r => !IsSupported(r)))
                _logger.LogInformation("The plugin {Folder} isn't started: it runs only on {Platforms}", r.Package.Folder, string.Join(", ", r.Package.Info.Platforms));
            var starting = runners.Where(r => !_state.IsOff(r.Package.Folder) && IsSupported(r)).ToList();
            _logger.LogInformation("Starting {Count} of {All} plugin hosts", starting.Count, runners.Count);
            // Started under the lock (each start only schedules its launch): a stop waits for this, then stops them all.
            return Task.WhenAll(starting.Select(r => r.StartAsync()).ToList());
        }
    }

    /// <summary>
    /// Latches stopping (no start after it starts anything), then stops every host in parallel within
    /// <see cref="PluginHostTimeouts.EngineStop"/> in all and kills the rest, closing each package so that nothing starts
    /// it again; a launch in flight is cancelled. Then waits for a turn on, off or reload under way to finish.
    /// </summary>
    public async Task StopAllAsync()
    {
        IReadOnlyList<PluginHostRunner> runners;
        lock (_lock)
        {
            _stopping = true;
            runners = _runners;
        }

        if (runners.Count > 0)
            await Task.WhenAll(runners.Select(r => r.StopAsync(_options.Host.Timeouts.EngineStop, close: true))).ConfigureAwait(false);
        await _switching.WaitAsync().ConfigureAwait(false);
        _switching.Release();
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);

    /// <summary>The package in <paramref name="folder"/> (its name compared ignoring case), or null.</summary>
    internal PluginHostRunner? Find(string folder) =>
        Runners.FirstOrDefault(r => string.Equals(r.Package.Folder, folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="runner"/>'s package runs on this operating system (its <c>Platforms</c>); an unsupported one never starts.</summary>
    internal bool IsSupported(PluginHostRunner runner) => runner.Package.Info.RunsOn(_options.Platform);

    /// <summary>The user's choice for <paramref name="runner"/>'s package: on unless turned off.</summary>
    internal bool IsOn(PluginHostRunner runner) => !State.IsOff(runner.Package.Folder);

    private PluginStateFile State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    private bool Stopping
    {
        get
        {
            lock (_lock)
                return _stopping;
        }
    }

    /// <summary>
    /// The one-id-one-package rule, asked as <paramref name="runner"/>'s host of <paramref name="generation"/> lists
    /// <paramref name="ids"/>: null when it may take them, reserved for it in the same step (every later package whose
    /// running host has one of them is stopped, failed with <c>duplicateId</c>), else why not (a package earlier in the
    /// order has one, or the generation is no longer current: it then takes nothing and fails no one).
    /// </summary>
    internal string? Claim(PluginHostRunner runner, int generation, IReadOnlyList<string> ids)
    {
        lock (_claims)
        {
            var others = Runners.Where(r => r != runner).Select(r => (Runner: r, Shared: r.ClaimedIds.Intersect(ids, StringComparer.Ordinal).FirstOrDefault()))
                .Where(o => o.Shared != null)
                .ToList();
            if (others.Where(o => o.Runner.Order < runner.Order).OrderBy(o => o.Runner.Order).FirstOrDefault() is { Runner: { } earlier, Shared: { } id })
                return $"its plugin {id} is provided by the plugin package {earlier.Package.Folder}, earlier in the list";
            if (!runner.Reserve(generation, ids))
                return PluginHostRunner.Superseded;
            foreach (var (later, shared) in others)
                later.FailDuplicate(null, $"its plugin {shared} is provided by the plugin package {runner.Package.Folder}, earlier in the list");
            return null;
        }
    }

    /// <summary>
    /// Records the user's choice for the package in <paramref name="folder"/>, then stops its host (within
    /// <see cref="PluginHostTimeouts.Stop"/>, then killed) or starts it (a failed one afresh). The choice is written first:
    /// a write that fails changes nothing.
    /// </summary>
    internal async Task<PluginSwitch> SetOnAsync(string folder, bool on)
    {
        if (Find(folder) is not { } runner)
            return PluginSwitch.NotFound;
        if (!IsSupported(runner))
            return PluginSwitch.Unsupported;

        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                State.Set(runner.Package.Folder, on);
            }
            catch (PluginStateUnavailableException)
            {
                return PluginSwitch.StateUnavailable;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && State.IsOff(runner.Package.Folder) == !on)
            {
                // The file was replaced, only flushing its folder failed: it holds the choice, which the next start
                // reads, so the runtime follows it; the next change writes the file again.
                _logger.LogError(ex, "The plugin {Folder} is turned {OnOff}, but the plugin state file's folder couldn't be flushed", runner.Package.Folder, on ? "on" : "off");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The plugin {Folder} couldn't be turned {OnOff}: the plugin state file couldn't be written", runner.Package.Folder, on ? "on" : "off");
                return PluginSwitch.WriteFailed;
            }

            if (!on)
            {
                await runner.StopAsync(_options.Host.Timeouts.Stop).ConfigureAwait(false);
                return PluginSwitch.Done;
            }

            if (runner.State == PluginHostState.Failed)
                await runner.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            if (runner.State == PluginHostState.Stopped && !Stopping)
                _ = runner.StartAsync();
            return PluginSwitch.Done;
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Stops the package's host (within <see cref="PluginHostTimeouts.Stop"/>, then killed) and starts it afresh, its restarts counted anew.</summary>
    internal async Task<PluginSwitch> ReloadAsync(string folder)
    {
        if (Find(folder) is not { } runner)
            return PluginSwitch.NotFound;
        if (!IsSupported(runner))
            return PluginSwitch.Unsupported;

        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsOn(runner))
                return PluginSwitch.NotRunning;
            await runner.StopAsync(_options.Host.Timeouts.Stop).ConfigureAwait(false);
            if (!Stopping)
                _ = runner.StartAsync();
            return PluginSwitch.Done;
        }
        finally
        {
            _switching.Release();
        }
    }
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
    /// come first; off under <c>--no-plugins</c>), the hosts' manager, and the service that starts them once the engine listens.
    /// </summary>
    public static IServiceCollection AddPlugins(this IServiceCollection services, EngineOptions options, DataPaths paths, SettingsStore settings)
    {
        // The 1.x folder start-up checked the data folder against: what a host is given to write in is checked against it too.
        var v1Root = options.V1Dir ?? V1Folder.DefaultRoot();
        services.AddSingleton(sp => new PluginSensorSource(
            logger: sp.GetService<ILogger<PluginSensorSource>>(),
            enabled: sp.GetRequiredService<PluginOptions>().Enabled));
        services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<PluginSensorSource>());
        services.AddSingleton<IPluginProcessLauncher, PluginProcessLauncher>();
        services.AddSingleton(new PluginOptions
        {
            Enabled = !options.NoPlugins,
            BundledDirectory = options.BundledPlugins ?? Path.Combine(AppContext.BaseDirectory, "plugins"),
            InstalledDirectory = paths.PluginsDirectory,
            StateFile = Path.Combine(paths.Root, PluginStateFile.FileName),
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
