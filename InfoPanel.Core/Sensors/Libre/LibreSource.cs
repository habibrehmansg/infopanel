using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// The Libre source (<c>libre</c>): LibreHardwareMonitor's sensors, read by the sensor helper the engine starts
/// (<see cref="LibreSampler"/>), every 2 s as 1.4.x, answering every <c>libre:</c> key (1.4.x's
/// <c>libre:{Identifier}</c>; one this computer lacks reads unavailable). A <see cref="PolledSensorSource"/> whose stop
/// first ends any request to the helper in flight, so a helper that hangs never holds up the engine's stop; its sampler
/// then stops the helper. Its state: <c>running</c>; <c>limited</c> · <c>noDriver</c> while the helper says the driver
/// is missing; <c>off</c> · <c>disabled</c> while <c>sources.libre.enabled</c> is off; <c>unavailable</c> ·
/// <c>helperFailed</c> while the helper restarts or the build doesn't carry it. The engine tells it of each change to
/// the stored settings (<see cref="SettingsChanged"/>), so Off stops the helper at once, even while a request hangs.
/// </summary>
public sealed class LibreSource : ISensorSource, ISensorStatusSource, ISensorSourceState, IDisposable
{
    public const string SourceId = "libre";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>The driver state the helper's <c>hello</c> gives when the driver isn't installed.</summary>
    public const string DriverMissing = "missing";

    public static readonly SourceReason Disabled = new("disabled", "Libre Hardware Monitor is turned off in the settings");

    public static readonly SourceReason NoDriver = new("noDriver",
        "The PawnIO driver isn't installed, so the CPU's temperatures, clocks and power and the board's fans and voltages aren't read");

    public static readonly SourceReason HelperFailed = new("helperFailed", "The sensor helper stopped; it is started again");

    /// <summary><see cref="HelperFailed"/>'s code, for a build that doesn't carry the helper.</summary>
    public static readonly SourceReason HelperMissing = new("helperFailed", "The sensor helper isn't installed with the engine");

    private readonly PolledSensorSource _source;
    private readonly CancellationTokenSource _stopping = new();
    private volatile LibreSampler? _sampler;

    /// <param name="launcher">Starts the helper: its process (<see cref="LibreHelperProcess"/>), a recording's, a test's.</param>
    /// <param name="settings">The <c>sources.libre</c> settings now, read each round and on <see cref="SettingsChanged"/>.</param>
    public LibreSource(ILibreHelperLauncher launcher, Func<LibreSettings> settings, TimeProvider? time = null, PolledSensorSourceOptions? options = null,
        LibreSampler.IObserver? observer = null, Serilog.ILogger? log = null)
    {
        _source = new PolledSensorSource(SourceId, () => _sampler = new LibreSampler(launcher, settings, time, _stopping.Token, observer, log),
            options ?? new PolledSensorSourceOptions { Interval = Interval }, time, SensorKeySpace.Of(SensorType.Libre), log);
    }

    /// <summary>The helper's process beside the engine (<see cref="LibreHelperProcess.DefaultPath"/>).</summary>
    public static LibreSource CreateWindows(Func<LibreSettings> settings, TimeProvider? time = null, LibreSampler.IObserver? observer = null) =>
        new(new LibreHelperProcess(LibreHelperProcess.DefaultPath, time: time), settings, time, observer: observer);

    public string Id => SourceId;

    /// <summary>The polled source underneath (its polls, for tests).</summary>
    internal PolledSensorSource Polled => _source;

    /// <summary>The sampler while the source runs (for tests).</summary>
    internal LibreSampler? Sampler => _sampler;

    /// <summary>The sensors with their hardware and sensor types, as last fetched (empty before the first start).</summary>
    public IReadOnlyList<LibreSensor> Catalogue => _sampler?.Catalogue.Sensors ?? [];

    public SourceState State => _source.State;

    /// <summary>
    /// The stored settings changed (the settings store's <c>Changed</c>; any thread, quick): Off ends the helper's request
    /// in flight, so the helper stops at once rather than after a hung discovery, catalogue fetch or sample.
    /// </summary>
    public void SettingsChanged() => _sampler?.SettingsChanged();

    public bool Handles(SensorKey key) => _source.Handles(key);

    public IReadOnlyList<SensorInfo> GetSensors() => _source.GetSensors();

    public bool TryRead(SensorKey key, out SensorReading reading) => _source.TryRead(key, out reading);

    public SensorStatus GetStatus(SensorKey key) => _source.GetStatus(key);

    public SensorState ReadState(SensorKey key) => _source.ReadState(key);

    /// <summary>
    /// Starts the helper (its first discovery) and polling. Cancelling <paramref name="ct"/> (the engine stopping while
    /// the helper still starts) ends the start's request in flight, as a stop does.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await using var _ = ct.Register(() => _stopping.Cancel());
        await _source.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ends the helper's request in flight, then stops polling (the sampler stops the helper as the loop ends).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _source.StopAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _source.Dispose();
    }
}
