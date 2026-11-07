using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
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
        _manager = new PanelManager(_backend, store, host, time, logger, _options.Runner, _options.SinceBoot);
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
        if (await manager.StopAsync(left).ConfigureAwait(false))
            _backend?.Dispose();
    }
}
