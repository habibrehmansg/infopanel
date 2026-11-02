using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// Starts helpers in this process: the helper's own serve loop (<see cref="HelperHost"/>) over a tree, on a real pipe of
/// the test's own, and the engine's real client on it. A helper's end is its serve loop's.
/// </summary>
internal sealed class TreeLauncher(ISensorTree tree, TimeProvider? time = null) : ILibreHelperLauncher
{
    private int _starts;

    public int Starts => Volatile.Read(ref _starts);

    public List<Task<HelperExit>> Runs { get; } = [];

    public ISensorTree Tree { get; set; } = tree;

    public async Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
    {
        Interlocked.Increment(ref _starts);
        var (pipe, run) = HelperTestKit.StartHelper(Tree);
        lock (Runs)
            Runs.Add(run);
        var client = await SensorHelperClient.ConnectAsync(pipe, HelperTestKit.Patience, time, observer, cancel);
        return new LibreHelperConnection(client, ExitOf(run));
    }

    private static async Task<int?> ExitOf(Task<HelperExit> run) => (int)await run;

    /// <summary>Waits for every helper started so far to end.</summary>
    public async Task AllEnded()
    {
        Task[] runs;
        lock (Runs)
            runs = [.. Runs];
        await Task.WhenAll(runs).WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
    }
}

/// <summary>A tree that says the driver is <paramref name="driver"/> in its hello, otherwise <paramref name="inner"/>.</summary>
internal sealed class DriverTree(FakeTree inner, string driver) : ISensorTree
{
    public bool Elevated => inner.Elevated;

    public string Driver => driver;

    public void Configure(bool storage, int storageInterval) => inner.Configure(storage, storageInterval);

    public TreeSnapshot Update() => inner.Update();

    public void Dispose() => inner.Dispose();
}

/// <summary>A helper whose every answer the test sets, and whose end the test causes.</summary>
internal sealed class ScriptedHelper : ILibreHelper
{
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HelloAnswer Hello { get; set; } = new(HelperProtocol.Version, "2.0.0-test", false, "unknown");

    public CatalogueAnswer Catalogue { get; set; } = new(1,
        [new HelperHardware("/amdcpu/0", "CPU", "Cpu", null)],
        [new HelperSensor("/amdcpu/0/load/0", "CPU Total", "Load", 0, "%", "/amdcpu/0")]);

    /// <summary>What each sample does: answers, or throws.</summary>
    public Func<HelperSample> Sample { get; set; } = () => throw new InvalidOperationException("no sample set");

    /// <summary>What each catalogue request after the start's does (the catalogue when unset).</summary>
    public Func<CancellationToken, Task<CatalogueAnswer>>? NextCatalogue { get; set; }

    /// <summary>What each sample does, given the request's cancellation (<see cref="Sample"/> when unset).</summary>
    public Func<CancellationToken, Task<HelperSample>>? SampleCall { get; set; }

    public int Catalogues { get; private set; }

    /// <summary>A request that is answered only by its cancellation; <paramref name="asked"/> is set once it waits.</summary>
    public static async Task<T> Hang<T>(ManualResetEventSlim asked, CancellationToken cancel)
    {
        asked.Set();
        await Task.Delay(Timeout.Infinite, cancel);
        throw new InvalidOperationException("unreachable");
    }

    public bool Disposed { get; private set; }

    public Task<int?> Exited => _exited.Task;

    /// <summary>The helper's process ends with <paramref name="code"/>, the engine not having asked.</summary>
    public void Exit(int code) => _exited.TrySetResult(code);

    public Task<HelloAnswer> HelloAsync(CancellationToken cancel) => Task.FromResult(Hello);

    public Task<CatalogueAnswer> CatalogueAsync(CancellationToken cancel) =>
        ++Catalogues > 1 && NextCatalogue is { } next ? next(cancel) : Task.FromResult(Catalogue);

    public Task<HelperSample> SampleAsync(CancellationToken cancel)
    {
        if (SampleCall is { } call)
            return call(cancel);
        try
        {
            return Task.FromResult(Sample());
        }
        catch (Exception ex)
        {
            return Task.FromException<HelperSample>(ex);
        }
    }

    public Task<ConfigureAnswer> ConfigureAsync(bool storage, int storageInterval, CancellationToken cancel) =>
        Task.FromResult(new ConfigureAnswer(storage, storageInterval));

    public static HelperSample Answer(long version, params float?[] values) =>
        new(new SampleAnswer(version, values, values, values), false);

    /// <summary>A sample under a catalogue version the engine hasn't fetched yet.</summary>
    public static HelperSample Changed(long version, params float?[] values) =>
        new(new SampleAnswer(version, values, values, values), true);

    public static HelperErrorException TreeFailed() => new(ErrorAnswer.TreeFailed, "LibreHardwareMonitor threw.");

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _exited.TrySetResult(null);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Starts a new <see cref="ScriptedHelper"/> each time (or throws what <see cref="Refuse"/> says), keeping each.</summary>
internal sealed class ScriptedLauncher(Action<ScriptedHelper>? setUp = null) : ILibreHelperLauncher
{
    public List<ScriptedHelper> Helpers { get; } = [];

    public Func<Exception>? Refuse { get; set; }

    public int Starts { get; private set; }

    public ScriptedHelper Last => Helpers[^1];

    public Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
    {
        Starts++;
        if (Refuse?.Invoke() is { } refusal)
            return Task.FromException<ILibreHelper>(refusal);
        var helper = new ScriptedHelper();
        setUp?.Invoke(helper);
        Helpers.Add(helper);
        return Task.FromResult<ILibreHelper>(helper);
    }
}

/// <summary>A Libre source on a manual clock, polled by the test, its settings set by the test, its log kept.</summary>
internal sealed class LibreRig : IDisposable
{
    public readonly TimerTimeProvider Time = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
    public readonly LibreSource Source;
    public readonly List<LogEvent> Log = [];
    private LibreSettings _settings = LibreSettings.Default;

    /// <summary>The settings now; setting them is a change to the stored settings, which the source hears of at once.</summary>
    public LibreSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            Notify();
        }
    }

    /// <summary>Sets the settings without the change being heard of (only the source's own reads of them see it).</summary>
    public void SetQuietly(LibreSettings settings) => _settings = settings;

    /// <summary>
    /// Runs once, right after the source's next read of the settings has taken them (that read still sees them as they
    /// were): a change made here arrives between the source's read and what it does next.
    /// </summary>
    public Action? AfterNextRead { get; set; }

    private LibreSettings Read()
    {
        var settings = _settings;
        var after = AfterNextRead;
        AfterNextRead = null;
        after?.Invoke();
        return settings;
    }

    private void Notify() => Source.SettingsChanged();

    public LibreRig(Func<TimeProvider, ILibreHelperLauncher> launcher, LibreSampler.IObserver? observer = null)
        : this((time, _) => launcher(time), observer)
    {
    }

    /// <param name="launcher">Given the rig's clock and its log.</param>
    public LibreRig(Func<TimeProvider, Serilog.ILogger, ILibreHelperLauncher> launcher, LibreSampler.IObserver? observer = null)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Sink(Log)).CreateLogger();
        Source = new LibreSource(launcher(Time, log), Read, Time, observer: observer, log: log);
    }

    public PolledSensorSource Polled => Source.Polled;

    public void Open() => Polled.Open();

    public void Poll() => Polled.Poll();

    /// <summary>Moves the clock by <paramref name="by"/>, then polls.</summary>
    public void Poll(TimeSpan by)
    {
        Time.Advance(by);
        Poll();
    }

    public SourceState State => Source.State;

    public IReadOnlyList<string> Keys => [.. Source.GetSensors().Select(s => s.Key.ToString())];

    public SensorStatus Status(string id) => Source.GetStatus(SensorKey.Libre(id));

    public double? Now(string id) => Source.TryRead(SensorKey.Libre(id), out var reading) ? reading.ValueNow : null;

    public void Dispose() => Source.Dispose();

    private sealed class Sink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
