using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Power;
using InfoPanel.Panels.Usb;
using InfoPanel.Persistence;
using InfoPanel.Settings;

namespace InfoPanel.Panels;

/// <summary>
/// Where the panel service finds USB (tests replace it): the libusb candidates in their order, or a backend outright.
/// </summary>
public sealed record PanelServiceOptions
{
    /// <summary>The places libusb-1.0 is looked for (<see cref="LibUsb.DefaultCandidates"/>).</summary>
    public Func<IReadOnlyList<string>> LibraryCandidates { get; init; } = LibUsb.DefaultCandidates;

    /// <summary>A backend to use instead of libusb (tests: the fake).</summary>
    public Func<IUsbBackend>? Backend { get; init; }

    public PanelRunnerOptions Runner { get; init; } = new();

    /// <summary>The computer's sleep and wake (tests: a fake, or none); the default is the platform's (<see cref="PanelService.PlatformPower"/>).</summary>
    public Func<Serilog.ILogger, IPowerEvents?> Power { get; init; } = PanelService.PlatformPower;

    /// <summary>The monotonic clock that counts sleep, for the time asleep (<see cref="PowerEvents.SinceBoot"/>; tests: their own).</summary>
    public Func<TimeSpan>? SinceBoot { get; init; } = PowerEvents.SinceBoot;

    /// <summary>How <c>devices.json</c> is written (tests give a storage fault); null: <see cref="PanelStore.Writer"/>'s own.</summary>
    internal Action<string, byte[], Action>? StoreWriter { get; init; }

    /// <summary>How panel hosts are started (tests: their own); the engine executable in its panel host mode by default.</summary>
    public IPanelHostLauncher HostLauncher { get; init; } = new PanelProcessLauncher();
}

/// <summary>
/// A panel family the engine knows (the API's <c>deviceList.families</c>): built in, or declared by a panel package
/// (<see cref="Runner"/>, null for a built-in one), with its state: <c>ready</c>, <c>off</c> (its package turned off)
/// or <c>failed</c> (<see cref="ReasonCode"/> and <see cref="Reason"/> say why).
/// </summary>
public sealed record PanelFamilyInfo(string Id, string Name, PanelHostRunner? Runner, IReadOnlyList<PanelPackageModel> Models, IReadOnlyList<string> Transports)
{
    /// <summary>The settings its panels take (its panel.json's); none for a built-in family.</summary>
    public IReadOnlyList<PanelFamilySetting> Settings { get; init; } = [];

    /// <summary><c>builtIn</c>, or the package's folder name.</summary>
    public string Driver => Runner?.Folder ?? "builtIn";

    public string State => Runner?.Problem switch
    {
        null => "ready",
        { Off: true } => "off",
        _ => "failed",
    };

    public string? ReasonCode => State == "failed" ? Runner!.Package.ProblemCode ?? "gaveUp" : null;

    public string? Reason => State == "failed" ? Runner!.Problem!.Message : null;
}

/// <summary>
/// The engine's USB panels (docs/2.0/panels.md): off with <c>--no-panels</c>; otherwise <c>devices.json</c> is read
/// (a file the engine can't hold turns panels off for the run) and libusb-1.0 is loaded (not found: panels are
/// unsupported, one log line), then <see cref="PanelManager"/> runs, with <see cref="PanelPower"/> over the platform's
/// sleep and wake (macOS: <see cref="MacPowerEvents"/>; none elsewhere yet). Its stop (quit, and a session's end, which
/// stops the engine) unregisters from sleep and wake first, then turns every panel's screen off, at most 3 s in all,
/// before the render service is disposed (it is registered after it, so it stops first).
/// </summary>
public sealed class PanelService(
    EngineOptions options,
    DataPaths paths,
    Workspace workspace,
    RenderService render,
    SettingsStore settings,
    TimeProvider time,
    Serilog.ILogger logger,
    PanelServiceOptions? panelOptions = null) : IHostedService
{
    private readonly PanelServiceOptions _options = panelOptions ?? new PanelServiceOptions();
    private IUsbBackend? _backend;
    private PanelManager? _manager;
    private PanelPower? _power;
    private IReadOnlyList<PanelHostRunner> _hosts = [];

    /// <summary>The families the engine knows this run: the built-in ones, then each panel package's (on, off or failed); none while panels are off.</summary>
    public IReadOnlyList<PanelFamilyInfo> Families { get; private set; } = [];

    /// <summary>The built-in family, BeadaPanel's, as the families list gives it.</summary>
    internal static PanelFamilyInfo BuiltInFamily { get; } = new(SavedPanel.BeadaPanelFamily, "BeadaPanel", null,
        [.. BeadaPanelModel.All.Select(m => new PanelPackageModel(m.Id, m.Id, m.Width, m.Height, m.V1Model))], ["usb"]);

    /// <summary>The panel packages' hosts, one a package found at start (none with <c>--no-plugins</c>).</summary>
    public IReadOnlyList<PanelHostRunner> Hosts => _hosts;

    /// <summary>This platform's sleep and wake: macOS's IOKit power notifications; none on other platforms yet.</summary>
    public static IPowerEvents? PlatformPower(Serilog.ILogger logger) => OperatingSystem.IsMacOS() ? new MacPowerEvents(logger) : null;

    /// <summary>The running manager, or null when panels are off or unsupported this run.</summary>
    public PanelManager? Manager => _manager;

    /// <summary>
    /// Why there is no manager this run (the API's <c>reason</c>): <c>off</c> (<c>--no-panels</c>), <c>store</c>
    /// (<c>devices.json</c> can't be held) or <c>library</c> (no libusb); null once the manager runs.
    /// </summary>
    public string? Unsupported { get; private set; } = "off";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.NoPanels)
            return Task.CompletedTask;

        var store = PanelStore.Read(paths.DevicesFile, logger);
        if (store.Problem != null)
        {
            Unsupported = "store";
            return Task.CompletedTask;
        }
        if (_options.StoreWriter is { } writer)
            store.Writer = writer;
        Unsupported = "library";

        if (_options.Backend is { } make)
            _backend = make();
        else
        {
            if (LibUsb.LoadAndLog(_options.LibraryCandidates(), logger) is not { } library)
                return Task.CompletedTask;
            _backend = LibUsbBackend.Create(library, out var why);
            if (_backend == null)
            {
                logger.Information("USB panels are unsupported: libusb didn't start ({Reason})", why);
                return Task.CompletedTask;
            }
        }

        var host = new PanelHost(
            ProfileReady: id => workspace.Entry(id)?.Status == "ok",
            Attach: sink =>
            {
                try
                {
                    return render.Attach(sink);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            },
            Detach: render.Detach,
            RequestChanged: render.RequestChanged,
            Refresh: render.Refresh,
            FrameRate: () => settings.Current.Settings.FrameRate);
        var hosted = StartPackages();
        _manager = new PanelManager(_backend, store, host, time, logger, _options.Runner, _options.SinceBoot,
            [.. PanelManager.BuiltInDrivers(_backend, time), .. hosted]);
        workspace.ProfilesChanged += OnProfilesChanged;
        settings.Changed += OnSettingsChanged;
        _manager.Start();
        if (_options.Power(logger) is { } source)
        {
            _power = new PanelPower(source, _manager, time, logger);
            if (!_power.Start())
            {
                _power.Dispose();
                _power = null;
            }
        }
        Unsupported = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The panel packages (<see cref="PanelPackages.Find"/>: bundled beside the engine, then in the data folder's
    /// <c>plugins/</c>; none with <c>--no-plugins</c>), a host runner each (off when the user turned the package off in
    /// <c>plugin-state.json</c>, failed when the engine can't run it), and a proxy driver for each family they declare;
    /// the slot files go in a folder of the run folder private to this data folder, swept now.
    /// </summary>
    private List<HostedPanelDriver> StartPackages()
    {
        List<PanelFamilyInfo> families = [BuiltInFamily];
        List<HostedPanelDriver> drivers = [];
        if (options.NoPlugins)
        {
            Families = families;
            return drivers;
        }
        var packages = PanelPackages.Find(options.BundledPlugins ?? Path.Combine(AppContext.BaseDirectory, "plugins"), paths.PluginsDirectory,
            [SavedPanel.BeadaPanelFamily], logger);
        string? slots = null;
        if (packages.Count > 0)
        {
            try
            {
                slots = SlotDirectory(options.RunDir ?? LocalChannel.DefaultRunDirectory(), paths.Root, logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // a LocalChannelException too
            {
                logger.Warning("No panel package runs: the panels' slot folder in the run folder can't be made ({Error:l})", ex.Message);
            }
        }
        if (slots != null)
        {
            var state = Plugins.PluginStateFile.Read(Path.Combine(paths.Root, Plugins.PluginStateFile.FileName), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            List<PanelHostRunner> hosts = [];
            foreach (var package in packages.Where(p => p.ProblemCode != "duplicateId"))
            {
                var on = !state.IsOff(package.Folder);
                var runner = new PanelHostRunner(package, _options.HostLauncher, slots, paths.PluginLogsDirectory,
                    () => settings.Current.Settings.LogLevel.ToString().ToLowerInvariant(), on, time, logger);
                hosts.Add(runner);
                foreach (var family in package.Families)
                {
                    drivers.Add(new HostedPanelDriver(family, runner, logger));
                    families.Add(new PanelFamilyInfo(family.Id, family.Name, runner, family.Models,
                        [.. family.Matches.Select(m => PanelPackages.NameOf(m.Transport)).Distinct()]) { Settings = family.Settings });
                }
                if (!on)
                    logger.Information("The panel package {Folder:l} is turned off: its families' panels stay off", package.Folder);
            }
            _hosts = hosts;
        }
        Families = families;
        return drivers;
    }

    /// <summary>
    /// The run folder's slot folder for the data folder <paramref name="root"/> (<c>{run}/panels-{hash}</c>), its leftovers
    /// swept. It is checked as the run folder is (<see cref="LocalChannel.CreateRunDirectory"/>) before anything in it is
    /// swept or made: a link, anything but a folder, or another user's folder is refused; a new one is made this user's
    /// alone (Unix: 0700; Windows: a protected access list granting only this user), and an existing one made so.
    /// </summary>
    /// <exception cref="IOException">It can't be made, or isn't this user's private folder.</exception>
    internal static string SlotDirectory(string run, string root, Serilog.ILogger logger)
    {
        LocalChannel.CreateRunDirectory(run);
        var slots = Path.Combine(run, "panels-" + LocalChannel.HashOf(root));
        LocalChannel.CreateRunDirectory(slots);
        PanelSlotFile.Sweep(slots, logger);
        return slots;
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e) => _manager?.ProfilesChanged();

    private void OnSettingsChanged(SettingsSnapshot snapshot) => _manager?.FrameRateChanged();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_manager is not { } manager)
            return;
        workspace.ProfilesChanged -= OnProfilesChanged;
        settings.Changed -= OnSettingsChanged;
        // One deadline for both: the source's disposal (which waits for a will-sleep under way) and the panels' stop,
        // which gets only what is left.
        var started = TimeProvider.System.GetTimestamp();
        _power?.Dispose(); // no sleep or wake reaches the panels once they stop
        var left = BeadaPanelSession.ScreenOffBound - TimeProvider.System.GetElapsedTime(started);
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        // A panel still writing past the bound keeps the backend; the process is ending.
        var stopped = await manager.StopAsync(left).ConfigureAwait(false);
        // Then the panel hosts, in what is left of the bound: each closes its sessions and ends, or is killed.
        left = BeadaPanelSession.ScreenOffBound - TimeProvider.System.GetElapsedTime(started);
        await Task.WhenAll(_hosts.Select(h => h.StopAsync(left < TimeSpan.Zero ? TimeSpan.Zero : left))).ConfigureAwait(false);
        if (stopped)
            _backend?.Dispose();
    }
}
