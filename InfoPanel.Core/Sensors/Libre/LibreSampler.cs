using InfoPanel.SensorHelper;
using InfoPanel.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// The Libre source's sampler: it runs the sensor helper and reads it, one round a poll, all on the poll thread. A
/// change to the settings is heard at once (<see cref="SettingsChanged"/>): off ends the helper's request in flight (a
/// discovery, a catalogue fetch or a sample that hangs), and the round stops the helper then and there. Each round also
/// applies <c>sources.libre.*</c> as they are now (the settings store's current values): off stops the helper (the
/// engine asked, so it isn't restarted), lists no sensors and says <c>off</c> · <c>disabled</c>; on starts it, and off
/// then on again since the last round starts a new one; a start that completes while it is off is stopped before it is
/// used (an Off heard while it starts ends the start, however soon after the round read the settings); Off, and
/// storage off, withdraw their sensors (every one; the storage hardware's; those still listed stale after their hardware
/// went by itself too), which the polled source unlists at once (hardware that goes by itself is stale first); a storage change is sent as <c>configure</c>, which reopens
/// LibreHardwareMonitor's computer as 1.4.x restarted its monitor, and the helper's next sample then comes under a new
/// catalogue version, which is fetched. Then it samples: a value is the reading, null a failed read (never 0); the
/// engine keeps the minimum, maximum and average (1.4.x's average was always 0). A hardware the catalogue no longer
/// lists goes stale, then unavailable (<see cref="PolledSensorSource"/>).
/// <para>
/// <b>Lifetime.</b> The first discovery starts the helper (its start and LibreHardwareMonitor's open may outlast
/// start-up's wait). A round fails when its sample isn't answered within <see cref="HelperProtocol.SampleTimeout"/> or
/// LibreHardwareMonitor failed it (<c>treeFailed</c>); <see cref="FailedRoundsBeforeRestart"/> failed rounds in a row
/// restart the helper. A sample unanswered in time gives the helper up at once: its client closes, since an answer that
/// comes later would put the pipe out of step, so that round restarts it too. So does any end of the helper the engine
/// didn't ask for (exit 4, a failed open; exit 0, a parent it didn't recognise; a crash) and anything that breaks the
/// protocol. Each restart waits <see cref="BackoffOf"/> (2, 4 … 60 s, from the loss) while the source is
/// <c>unavailable</c> · <c>helperFailed</c> and its sensors stale; an answered sample (under a new catalogue too) resets
/// the wait and the failed rounds, and a catalogue it then fails to fetch fails that round's readings. A helper the build
/// doesn't carry is the same state, logged once.
/// </para>
/// <para>
/// <b>The PawnIO driver.</b> The helper reads the driver's state as it starts and says it in <c>hello</c>; the source's
/// state follows it (<see cref="StateOf"/>). Given the registry (<c>driverRegistry</c>, on Windows), the sampler reads
/// the same value itself right before it starts each helper (so a change while the helper starts is one the next read
/// sees) and then every <see cref="LibreSource.DriverCheckInterval"/> on the source's clock, whatever the settings: when
/// it differs from what it read before the running helper started (a driver installed, updated or removed outside the
/// engine), that helper is stopped (the engine asked) and a new one started in the same round, once, with no backoff (it
/// isn't a failure). <see cref="DriverChanged"/> (an install that finished) does the same at the next round, whatever the
/// registry says. Either, with no helper running (waiting out a restart's backoff), starts one in that round, the wait
/// cleared.
/// </para>
/// <para>
/// <b>An install's tail.</b> From <see cref="DriverChanged"/> on, <see cref="DriverSettling"/> is true until the engine
/// has read the driver again and the helper started after that read has said hello (its state is then the driver's), or
/// no such helper will: that start failed or was interrupted, Libre is off at the round, or the sampler closed (the source
/// stopped). Each of those reads the registry first, so the driver it leaves is never the one from before the install.
/// The installs asked and the installs settled are counts under one lock: <see cref="DriverChanged"/> counts one on the
/// installer's thread, and only the poll thread settles them (a start settles the count it took as it began, so an
/// install that ends while a helper starts is settled by the next one). Nothing waits for it: the helper's own start,
/// with its deadlines, bounds it.
/// </para>
/// </summary>
public sealed class LibreSampler : ISensorSampler
{
    /// <summary>
    /// Hears the helper's answers (each as its JSON, through the client: <see cref="SensorHelperClient.IObserver"/>),
    /// the end of each round, and the sampler's close: what a recording is made of. Called on the poll thread; one that
    /// throws is logged once and called no more.
    /// </summary>
    public interface IObserver : SensorHelperClient.IObserver
    {
        void RoundEnded();

        void Stopped()
        {
        }
    }

    /// <summary>Failed rounds in a row (a sample LibreHardwareMonitor failed) that restart the helper.</summary>
    public const int FailedRoundsBeforeRestart = 3;

    /// <summary>The longest wait before a restart.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILibreHelperLauncher _launcher;
    private readonly Func<LibreSettings> _settings;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private readonly ILogger _log;
    private IObserver? _observer;

    private ILibreHelper? _helper;
    private volatile HelloAnswer? _hello;
    private volatile LibreCatalogue _catalogue = LibreCatalogue.Empty;
    private long _version;
    private (bool Storage, int Interval) _applied;
    private int _failedRounds;
    private int _restarts;
    private long _lostAt;
    private TimeSpan _wait;
    private bool _waiting;
    private bool _discovered;
    private bool _missingLogged;
    private bool _failedLogged;
    private volatile SourceState? _state;

    // The helper's generation: its requests end when it is cancelled (by the source's stop, or by Off heard from another
    // thread). Swapped on the poll thread, cancelled from any, both under _gate.
    private readonly Lock _gate = new();
    private CancellationTokenSource? _generation;
    private bool _offHeard;

    // The PawnIO driver as the engine reads it (null without a registry, or before the first read), when it was read, and
    // as it was read right before the running helper (or the last start) started; a restart asked from another thread.
    private readonly IPawnIoRegistry? _driverRegistry;
    private volatile PawnIoReading? _driverRead;
    private long _driverReadAt;
    private PawnIoReading? _driverAtStart;

    // Installs that ended well (DriverChanged, any thread) and those the poll thread has settled, under _installGate; once
    // the sampler is closed nothing more is counted. _installsTaken: the count the poll thread last acted on.
    private readonly Lock _installGate = new();
    private long _installsAsked;
    private long _installsSettled;
    private bool _closed;
    private long _installsTaken;
    // The helper's elevation changed (its task set up or removed): asked from any thread, taken by the poll thread.
    private long _helperChangesAsked;
    private long _helperChangesTaken;

    // Sensors dropped on purpose (Off; storage off) since the polled source last took them.
    private readonly HashSet<string> _withdrawn = new(StringComparer.Ordinal);

    // Every sensor the polled source still lists, with its hardware type: one whose hardware went by itself is still
    // listed (stale, until it is unavailable, then unlisted: Unlisted lets it go), so a withdrawal names it too.
    private readonly Dictionary<string, string> _listed = new(StringComparer.Ordinal);

    /// <param name="launcher">Starts a helper (its process, a recording's, a test's).</param>
    /// <param name="settings">The <c>sources.libre</c> settings now (the settings store's current ones).</param>
    /// <param name="stopping">Cancelled when the source stops: a request in flight ends at once.</param>
    /// <param name="observer">A recording, when one is made.</param>
    /// <param name="driverRegistry">Where the engine reads the PawnIO driver's version itself (Windows); none elsewhere.</param>
    public LibreSampler(ILibreHelperLauncher launcher, Func<LibreSettings> settings, TimeProvider? time = null, CancellationToken stopping = default,
        IObserver? observer = null, ILogger? log = null, IPawnIoRegistry? driverRegistry = null)
    {
        _driverRegistry = driverRegistry;
        _launcher = launcher;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _stopping = stopping;
        _observer = observer;
        _log = (log ?? Log.Logger).ForContext<LibreSampler>();
    }

    /// <summary>The wait before the <paramref name="restart"/>th restart in a row (from 1): 2, 4, 8, 16, 32, then 60 s.</summary>
    public static TimeSpan BackoffOf(int restart) =>
        restart >= 6 ? MaxBackoff : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 2 << Math.Max(0, restart - 1)));

    /// <summary>The catalogue as last fetched, with the hardware and sensor types; read from any thread.</summary>
    public LibreCatalogue Catalogue => _catalogue;

    public long CatalogueVersion => _version;

    public SourceState? State => _state;

    /// <summary>Whether a helper runs now. For tests.</summary>
    internal bool HelperRunning => _helper is not null;

    /// <summary>How many sensor ids the sampler keeps as listed (to withdraw them). For tests, on the poll thread.</summary>
    internal int ListedCount => _listed.Count;

    /// <summary>The helper's hello, while one runs.</summary>
    public HelloAnswer? Hello => _hello;

    /// <summary>
    /// The PawnIO driver now, from any thread: <c>notLoaded</c> when the running helper says so; else what the engine
    /// last read from the registry (Windows); else what the running helper said (a recording's); else <c>unknown</c>.
    /// The version is the registry's, when the engine reads it.
    /// </summary>
    public PawnIoReading Driver
    {
        get
        {
            var hello = _hello;
            var read = _driverRead;
            if (hello?.Driver == PawnIoDriver.NotLoaded)
                return new PawnIoReading(PawnIoDriver.NotLoaded, read?.Version);
            if (read is not null)
                return read;
            return hello?.Driver is PawnIoDriver.Missing or PawnIoDriver.Outdated or PawnIoDriver.Installed
                ? new PawnIoReading(hello.Driver, null)
                : new PawnIoReading(PawnIoDriver.Unknown, null);
        }
    }

    /// <summary>
    /// The driver was installed (from any thread): at the next round the registry is read again and the running helper,
    /// if any, is stopped and a new one started, once, with no backoff; <see cref="DriverSettling"/> until its hello. On a
    /// closed sampler (the source stopped) no round will: the registry is read now instead.
    /// </summary>
    public void DriverChanged()
    {
        lock (_installGate)
        {
            if (!_closed)
            {
                _installsAsked++;
                return;
            }
        }

        ReadDriver();
    }

    /// <summary>
    /// The helper's task was set up or removed (from any thread: <see cref="LibreElevation"/>): at the next round the
    /// running helper, if any, is stopped and a new one started, once, with no backoff, so the launcher chooses again
    /// (<see cref="LibreHelperChooser"/>); with none running (waiting out a restart's backoff), one starts in that round.
    /// </summary>
    public void HelperChanged() => Interlocked.Increment(ref _helperChangesAsked);

    /// <summary>Whether the helper's elevation changed since the last start (the poll thread), taking the change.</summary>
    private string? TakeHelperChange()
    {
        var asked = Interlocked.Read(ref _helperChangesAsked);
        if (asked == _helperChangesTaken)
            return null;
        _helperChangesTaken = asked;
        return "the sensor helper's administrator rights changed";
    }

    /// <summary>
    /// Whether an install that ended well hasn't been settled yet (any thread): the driver hasn't been read again with a
    /// helper started for it that said hello, and nothing has ended that wait (Libre off, a failed start, the sampler
    /// closed). Read before <see cref="Driver"/>: once this is false, <see cref="Driver"/> is the one read after the install.
    /// </summary>
    public bool DriverSettling
    {
        get
        {
            lock (_installGate)
                return _installsSettled != _installsAsked;
        }
    }

    private long InstallsAsked()
    {
        lock (_installGate)
            return _installsAsked;
    }

    /// <summary>Installs up to <paramref name="asked"/> are settled (the poll thread, after the registry was read for them).</summary>
    private void Settle(long asked)
    {
        lock (_installGate)
            _installsSettled = Math.Max(_installsSettled, asked);
    }

    public IReadOnlyCollection<string> TakeWithdrawn()
    {
        if (_withdrawn.Count == 0)
            return [];
        string[] ids = [.. _withdrawn];
        _withdrawn.Clear();
        return ids;
    }

    public void Unlisted(IReadOnlyCollection<string> ids)
    {
        // An id the catalogue lists again (fetched after the polled source last discovered) stays: it is listed next.
        var now = new HashSet<string>(_catalogue.Sensors.Select(s => s.Identifier), StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!now.Contains(id))
                _listed.Remove(id);
        }
    }

    /// <summary>
    /// The stored settings changed (from any thread, quickly: the settings store's lock is held). Off ends the helper's
    /// request in flight at once, so the poll thread stops the helper then and there, and the next round stops it if none
    /// was; the round itself reads the settings again either way.
    /// </summary>
    public void SettingsChanged()
    {
        if (_settings().Enabled)
            return;
        lock (_gate)
        {
            _offHeard = true;
            // The token is marked at once; its callbacks (the request's own) run on the thread pool, not here.
            _ = _generation?.CancelAsync();
        }
    }

    /// <summary>The helper's requests' token: its generation's, else the source's stop.</summary>
    private CancellationToken Token => _generation?.Token ?? _stopping;

    /// <summary>Whether a request ended because the source stops or Off was heard.</summary>
    private bool Interrupted => _stopping.IsCancellationRequested || (_generation?.IsCancellationRequested ?? false);

    /// <summary>
    /// A new generation for a helper about to start, before it is asked anything. An Off heard since the caller read the
    /// settings cancelled the generation before this one, so the settings are read again once this one is published
    /// (outside the gate: <see cref="SettingsChanged"/> takes it under the settings store's lock): off now cancels it, and
    /// an Off heard after that cancels it itself.
    /// </summary>
    private void NewGeneration()
    {
        CancellationTokenSource? old;
        CancellationTokenSource generation;
        lock (_gate)
        {
            old = _generation;
            _generation = generation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            _offHeard = false;
        }

        old?.Dispose();
        if (!_settings().Enabled)
            generation.Cancel();
    }

    private bool TakeOffHeard()
    {
        lock (_gate)
        {
            var heard = _offHeard;
            _offHeard = false;
            return heard;
        }
    }

    /// <summary>A request ended because the source stops (nothing more to do) or Off was heard: the helper stops now.</summary>
    private void Interrupt(ILibreHelper? starting = null)
    {
        Stop(starting);
        if (!_stopping.IsCancellationRequested)
            TurnOff();
    }

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_discovered)
        {
            _discovered = true;
            var wanted = _settings();
            if (!wanted.Enabled)
                TurnOff();
            else
                Start(wanted);
        }

        return _catalogue.Definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        try
        {
            Round(samples);
        }
        finally
        {
            Tell(o => o.RoundEnded());
        }
    }

    private void Round(Span<SensorSample> samples)
    {
        var wanted = _settings();
        if (!wanted.Enabled)
        {
            // Off: no helper will say hello for an install, so it is settled now, the registry read for it first.
            var asked = InstallsAsked();
            CheckDriver(asked);
            TurnOff();
            _installsTaken = asked;
            Settle(asked);
            _helperChangesTaken = Interlocked.Read(ref _helperChangesAsked);
            Fail(samples);
            return;
        }

        // Turned off and on again since the last round: the off is honoured (the helper stopped), then a new one starts.
        if (TakeOffHeard())
            TurnOff();

        // The driver changed since the running helper read it (or an install finished), or the helper's administrator
        // rights did: a new helper reads it, now.
        var helperChanged = TakeHelperChange();
        var driverChanged = CheckDriver(InstallsAsked()) ?? helperChanged;
        if (driverChanged is { } why && _helper is not null)
        {
            Restart(wanted, why);
            Fail(samples);
            return;
        }

        if (_helper is null)
        {
            // Waiting out a restart's backoff, or the start failed: this round has nothing to read. A driver that changed
            // meanwhile clears the wait: a helper starts now, to read it.
            if (driverChanged is not null && _waiting)
                _log.Information("Libre: {Why}; the sensor helper is started now, not after its wait", driverChanged);
            if (driverChanged is not null || !_waiting || _time.GetElapsedTime(_lostAt) >= _wait)
                Start(wanted);
            Fail(samples);
            return;
        }

        if (_helper.Exited.IsCompleted)
        {
            Lost($"it ended (exit code {Describe(_helper.Exited)}) though the engine didn't stop it");
            Fail(samples);
            return;
        }

        if ((wanted.Storage, wanted.StorageInterval) != _applied && !Configure(wanted))
        {
            Fail(samples);
            return;
        }

        HelperSample sample;
        try
        {
            sample = Wait(_helper.SampleAsync(Token));
        }
        catch (Exception ex) when (Interrupted && ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException)
        {
            Interrupt();
            Fail(samples);
            return;
        }
        catch (HelperErrorException ex) when (ex.Code is ErrorAnswer.TreeFailed or "tooLarge")
        {
            // LibreHardwareMonitor failed this read; the helper's pipe is still in step.
            Fail(samples);
            _failedRounds++;
            if (!_failedLogged)
                _log.Warning("Libre: the sensor helper failed a read ({Code}: {Message})", ex.Code, ex.Message);
            _failedLogged = true;
            if (_failedRounds >= FailedRoundsBeforeRestart)
                Lost($"{_failedRounds} rounds in a row failed");
            return;
        }
        catch (Exception ex) when (ex is TimeoutException or HelperProtocolException or ObjectDisposedException or System.IO.IOException)
        {
            Fail(samples);
            Lost(ex is TimeoutException ? $"a sample went unanswered for {HelperProtocol.SampleTimeout.TotalSeconds:0} s" : ex.Message);
            return;
        }

        // The helper answered: a success, whatever the catalogue (three failed rounds are three in a row).
        Answered();
        if (sample.CatalogueChanged)
        {
            // The values are for a catalogue the engine doesn't have yet: fetch it; this round's values are skipped, and
            // fail when the fetch does (the helper lost, its readings no longer live).
            if (!FetchCatalogue())
                Fail(samples);
            return;
        }

        var values = sample.Answer.Value;
        for (var i = 0; i < samples.Length && i < values.Count; i++)
        {
            if (values[i] is { } value)
                samples[i].Set(value);
            else
                samples[i].Outcome = SampleOutcome.Failed;
        }

        _state = StateOf(_hello);
    }

    private void Answered()
    {
        _failedRounds = 0;
        _failedLogged = false;
        _restarts = 0;
    }

    /// <summary>Starts a helper, says hello, configures it when the settings aren't its defaults, and fetches its catalogue.</summary>
    private void Start(LibreSettings wanted)
    {
        _waiting = false;
        NewGeneration();
        // The installs this start answers for: those ended well before the registry is read just below. However it ends
        // (its hello, a failure, an interruption), they are settled then; one that ends later is the next start's.
        var installs = InstallsAsked();
        _installsTaken = installs;
        // So is a change of the helper's administrator rights asked before it: the launcher chooses now.
        _helperChangesTaken = Interlocked.Read(ref _helperChangesAsked);
        // The engine's own reading before the helper reads it: a change while it starts differs from this at the next read.
        // Taken at every start, one that fails too, so a change is acted on once.
        _driverAtStart = ReadDriver();
        try
        {
            StartHelper(wanted);
        }
        finally
        {
            Settle(installs);
        }
    }

    private void StartHelper(LibreSettings wanted)
    {
        ILibreHelper? helper = null;
        try
        {
            helper = Wait(_launcher.StartAsync(_observer, Token));
            var hello = Wait(helper.HelloAsync(Token));
            if (wanted.Storage != HelperProtocol.DefaultStorage || wanted.StorageInterval != HelperProtocol.DefaultStorageInterval)
                Wait(helper.ConfigureAsync(wanted.Storage, wanted.StorageInterval, Token));
            var catalogue = Wait(helper.CatalogueAsync(Token));
            if (!_settings().Enabled || Interrupted)
            {
                // Turned off while it started: stopped before it is used.
                Interrupt(helper);
                return;
            }

            _helper = helper;
            _hello = hello;
            _applied = (wanted.Storage, wanted.StorageInterval);
            _failedRounds = 0;
            _missingLogged = false;
            SetCatalogue(catalogue);
            _state = StateOf(hello);
            _log.Information("Libre: the sensor helper {Version} reads {Count} sensors (elevated: {Elevated}, driver: {Driver})",
                hello.Helper, catalogue.Sensors.Count, hello.Elevated, hello.Driver);
        }
        catch (Exception ex) when (Interrupted && ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException)
        {
            Interrupt(helper);
        }
        catch (LibreHelperMissingException ex)
        {
            if (!_missingLogged)
                _log.Warning("Libre: no sensor helper to start ({Message}); Libre's sensors aren't read", ex.Message);
            _missingLogged = true;
            Backoff(LibreSource.HelperMissing, null);
        }
        catch (Exception ex)
        {
            helper?.GivenUp();
            Stop(helper);
            Backoff(LibreSource.HelperFailed, $"it didn't start ({ex.Message})");
        }
    }

    private bool Configure(LibreSettings wanted)
    {
        try
        {
            Wait(_helper!.ConfigureAsync(wanted.Storage, wanted.StorageInterval, Token));
            _applied = (wanted.Storage, wanted.StorageInterval);
            _log.Information("Libre: storage {Storage}, read every {Interval} s", wanted.Storage ? "on" : "off", wanted.StorageInterval);
            return true;
        }
        catch (Exception ex) when (Interrupted && ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException)
        {
            Interrupt();
            return false;
        }
        catch (Exception ex)
        {
            // A computer that fails to reopen ends the helper; anything else broke the protocol. Either way, restart.
            Lost($"configuring it failed ({ex.Message})");
            return false;
        }
    }

    /// <summary>Fetches the helper's new catalogue; false when it couldn't (the helper lost, stopped or turned off).</summary>
    private bool FetchCatalogue()
    {
        try
        {
            SetCatalogue(Wait(_helper!.CatalogueAsync(Token)));
            _state = StateOf(_hello);
            return true;
        }
        catch (Exception ex) when (Interrupted && ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException)
        {
            Interrupt();
            return false;
        }
        catch (Exception ex)
        {
            Lost($"fetching its catalogue failed ({ex.Message})");
            return false;
        }
    }

    private void SetCatalogue(CatalogueAnswer answer)
    {
        _catalogue = LibreCatalogue.Of(answer);
        _version++;
        foreach (var sensor in _catalogue.Sensors)
            _listed[sensor.Identifier] = sensor.HardwareType;
        if (_applied.Storage)
            return;

        // Storage turned off: every drive's sensor still listed that this catalogue leaves out is withdrawn, those of a
        // drive that went by itself before too (any other hardware that went did so by itself).
        var now = new HashSet<string>(_catalogue.Sensors.Select(s => s.Identifier), StringComparer.Ordinal);
        foreach (var (id, hardwareType) in _listed.ToArray())
        {
            if (hardwareType == StorageHardware && !now.Contains(id))
            {
                _withdrawn.Add(id);
                _listed.Remove(id);
            }
        }
    }

    /// <summary>LibreHardwareMonitor's hardware type of a drive, what storage off leaves out.</summary>
    private const string StorageHardware = "Storage";

    /// <summary>Off in the settings: the helper stopped (not a failure: no restart), no sensors.</summary>
    private void TurnOff()
    {
        if (_helper is { } helper)
        {
            _helper = null;
            Stop(helper);
            _log.Information("Libre: turned off; the sensor helper stopped");
        }

        _hello = null;
        _waiting = false;
        _restarts = 0;
        _failedRounds = 0;
        TakeOffHeard();
        if (_listed.Count > 0 || _catalogue.Count > 0)
        {
            // Off on purpose: every sensor still listed withdrawn (those of hardware that went by itself too, even with
            // none in the catalogue now), unlisted at once.
            foreach (var id in _listed.Keys)
                _withdrawn.Add(id);
            foreach (var sensor in _catalogue.Sensors)
                _withdrawn.Add(sensor.Identifier);
            _listed.Clear();
            _catalogue = LibreCatalogue.Empty;
            _version++;
        }

        _state = SourceState.OffBecause(LibreSource.Disabled);
    }

    /// <summary>The helper is lost: given up as failed (<see cref="ILibreHelper.GivenUp"/>), stopped, and started again after its backoff.</summary>
    private void Lost(string why)
    {
        var helper = _helper;
        _helper = null;
        _hello = null;
        _failedRounds = 0;
        helper?.GivenUp();
        Stop(helper);
        Backoff(LibreSource.HelperFailed, why);
    }

    private void Backoff(SourceReason reason, string? why)
    {
        _restarts++;
        _wait = BackoffOf(_restarts);
        _lostAt = _time.GetTimestamp();
        _waiting = true;
        _state = SourceState.UnavailableBecause(reason);
        if (why is null)
            return;
        if (_restarts == 1)
            _log.Warning("Libre: the sensor helper is restarted in {Wait} s: {Why}", _wait.TotalSeconds, why);
        else
            _log.Information("Libre: the sensor helper is restarted in {Wait} s (restart {Restart} in a row): {Why}", _wait.TotalSeconds, _restarts, why);
    }

    /// <summary>
    /// The state the helper's hello gives: the driver <c>missing</c> limited · <c>noDriver</c>, <c>outdated</c> limited ·
    /// <c>driverOutdated</c>, <c>notLoaded</c> limited · <c>driverNotLoaded</c> when the helper runs as administrator and
    /// · <c>notElevated</c> when it doesn't; anything else (<c>installed</c>, <c>unknown</c>) running.
    /// </summary>
    private static SourceState StateOf(HelloAnswer? hello) => hello?.Driver switch
    {
        PawnIoDriver.Missing => new SourceState(SourceStateKind.Limited, LibreSource.NoDriver),
        PawnIoDriver.Outdated => new SourceState(SourceStateKind.Limited, LibreSource.DriverOutdated),
        PawnIoDriver.NotLoaded => new SourceState(SourceStateKind.Limited, hello.Elevated ? LibreSource.DriverNotLoaded : LibreSource.NotElevated),
        _ => SourceState.Running,
    };

    /// <summary>Reads the driver from the registry now (none without one), keeping it as the engine's last reading.</summary>
    private PawnIoReading? ReadDriver()
    {
        if (_driverRegistry is null)
            return null;
        var read = PawnIoDriver.Read(_driverRegistry);
        _driverRead = read;
        _driverReadAt = _time.GetTimestamp();
        return read;
    }

    /// <summary>
    /// Reads the driver when an install finished since the last start took the count (<paramref name="asked"/> is the
    /// count now) or <see cref="LibreSource.DriverCheckInterval"/> passed since the last read, and says why the running
    /// helper should start again: an install finished, or the driver isn't what it was as that helper started. Null for
    /// nothing to do.
    /// </summary>
    private string? CheckDriver(long asked)
    {
        if (asked != _installsTaken)
        {
            ReadDriver();
            return "the PawnIO driver was installed";
        }

        if (_driverRegistry is null || (_driverRead is not null && _time.GetElapsedTime(_driverReadAt) < LibreSource.DriverCheckInterval))
            return null;
        var read = ReadDriver();
        return _driverAtStart is { } was && read is not null && read != was
            ? $"the PawnIO driver changed ({Describe(was)} to {Describe(read)})"
            : null;
    }

    private static string Describe(PawnIoReading driver) => driver.Version is { } version ? $"{driver.State} {version}" : driver.State;

    /// <summary>The engine stops the running helper (it asked: not a failure, no backoff) and starts a new one at once.</summary>
    private void Restart(LibreSettings wanted, string why)
    {
        var helper = _helper;
        _helper = null;
        _hello = null;
        Stop(helper);
        _log.Information("Libre: {Why}; the sensor helper is started again", why);
        Start(wanted);
    }

    private static void Fail(Span<SensorSample> samples)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i].Outcome = SampleOutcome.Failed;
    }

    private static string Describe(Task<int?> exited) =>
        exited.IsCompletedSuccessfully && exited.Result is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";

    /// <summary>The helper's requests are asynchronous; the poll thread waits for each (each has its own deadline).</summary>
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private void Stop(ILibreHelper? helper)
    {
        if (helper is null)
            return;
        try
        {
            helper.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Libre: stopping the sensor helper failed");
        }
    }

    private void Tell(Action<IObserver> what)
    {
        if (_observer is not { } observer)
            return;
        try
        {
            what(observer);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Libre: its observer (a recording) failed and is let go; reading goes on");
            _observer = null;
        }
    }

    /// <summary>Stops the helper (the source stopped: the engine asked, so it isn't restarted) and tells the observer.</summary>
    public void Dispose()
    {
        var helper = _helper;
        _helper = null;
        // A retired helper no longer speaks for the driver, as when it's turned off, lost or restarted.
        _hello = null;
        Stop(helper);
        // No round runs from now on: an install not settled is, with the registry as it is now; a later one reads it itself.
        bool settling;
        lock (_installGate)
        {
            _closed = true;
            settling = _installsSettled != _installsAsked;
        }

        if (settling)
            ReadDriver();
        lock (_installGate)
            _installsSettled = _installsAsked;
        Tell(o => o.Stopped());
        lock (_gate)
        {
            _generation?.Dispose();
            _generation = null;
        }
    }
}
