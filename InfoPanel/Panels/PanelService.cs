using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
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
}

/// <summary>
/// The engine's USB panels (docs/2.0/panels.md): off with <c>--no-panels</c>; otherwise <c>devices.json</c> is read
/// (a file the engine can't hold turns panels off for the run) and libusb-1.0 is loaded (not found: panels are
/// unsupported, one log line), then <see cref="PanelManager"/> runs. Its stop turns every panel's screen off, at most
/// 3 s in all, before the render service is disposed (it is registered after it, so it stops first).
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

    /// <summary>The running manager, or null when panels are off or unsupported this run.</summary>
    public PanelManager? Manager => _manager;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.NoPanels)
            return Task.CompletedTask;

        var store = PanelStore.Read(paths.DevicesFile, logger);
        if (store.Problem != null)
            return Task.CompletedTask;

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
        _manager = new PanelManager(_backend, store, host, time, logger, _options.Runner);
        workspace.ProfilesChanged += OnProfilesChanged;
        settings.Changed += OnSettingsChanged;
        _manager.Start();
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
        // A panel still writing past the bound keeps the backend; the process is ending.
        if (await manager.StopAsync(BeadaPanelSession.ScreenOffBound).ConfigureAwait(false))
            _backend?.Dispose();
    }
}
