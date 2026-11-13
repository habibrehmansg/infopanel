using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Plugins;
using InfoPanel.Settings;
using Sentry;
using Sentry.Extensibility;

namespace InfoPanel.CrashReporting;

/// <summary>What crash reports need from the engine (<see cref="CrashReports.Start"/>).</summary>
public sealed record CrashReportsOptions
{
    /// <summary>Where reports go: the build's crash-report address (<see cref="CrashReports.BuiltInDsn"/>); none sends nothing.</summary>
    public required string? Dsn { get; init; }

    public required DataPaths Paths { get; init; }

    /// <summary><c>infopanel-engine@{version}+{build}</c>.</summary>
    public required string Release { get; init; }

    /// <summary><c>production</c>, <c>beta</c>, <c>ci</c> or <c>development</c> (<see cref="CrashReports.EnvironmentOf"/>).</summary>
    public required string Environment { get; init; }

    /// <summary>
    /// The values free text loses besides the user's and computer's names and folders (<see cref="CrashReports.EngineNames"/>):
    /// read when a report is made (at most <see cref="CrashReports.NamesWait"/>), every name read during the run kept.
    /// </summary>
    public Func<IEnumerable<KnownValue>>? KnownValues { get; init; }

    /// <summary>The user's and computer's names and folders the scrubber knows; by default this computer's (<see cref="CrashReports.ThisComputer"/>).</summary>
    public IReadOnlyList<KnownValue>? Fixed { get; init; }

    /// <summary>Tests: where envelopes go in place of the network.</summary>
    public ITransport? Transport { get; init; }

    /// <summary>Tests: whether the network is there, in place of the SDK's check (which connects to the address's host).</summary>
    public INetworkStatusListener? NetworkStatus { get; init; }

    public Serilog.ILogger Logger { get; init; } = Serilog.Core.Logger.None;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The longest stopping the reports takes by default: the library's 2 s to send what is queued, and a second more.</summary>
    public static readonly TimeSpan DefaultStopDeadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest stopping the reports (turned off, or the engine stopping) may take: past it, their sending is retired,
    /// so nothing they hold is ever sent, and the engine goes on.
    /// </summary>
    public TimeSpan StopDeadline { get; init; } = DefaultStopDeadline;
}

/// <summary>
/// Crash reports (docs/2.0/crash-reports.md): in a build that names a crash-report address (the assembly metadata
/// <see cref="DsnMetadata"/>, from the MSBuild property <c>InfoPanelCrashReportsDsn</c>), while the setting
/// <c>telemetry.crashReports</c> is on (the default), the engine sends Sentry an event for each unhandled exception and
/// unobserved task exception (the SDK's own hooks), each error it logs with an exception (<see cref="ReportLogged"/>, at
/// most one per exception type and top InfoPanel frame an hour and <see cref="MaxLoggedPerRun"/> a run; never a plugin
/// host's), a plugin host's unexpected exit (<see cref="PluginHostExited"/>, without its name) and, at the next start, a
/// run that ended without shutting down (<c>runtime.json</c> left behind). Every event is rebuilt from an allowlist and
/// scrubbed (<see cref="CrashScrubber"/>); its breadcrumbs are log templates (<see cref="TemplateBreadcrumbSink"/>).
/// Sessions count crash-free users by a random id per install, kept in <c>crash-reports.json</c> and made only when
/// reports first start. One rule holds the switch: nothing queued is sent unless reporting was on when it was queued and
/// has been on since. It rests on the consent epoch (<see cref="TelemetrySettings.CrashReportsEpoch"/>), a random id the
/// settings write that turns reporting on makes and the one that turns it off removes: the SDK keeps its queue and session
/// in <c>crash-reports/{epoch}/</c> and each start reads and writes only its own epoch's folder, so turned off and on
/// again it starts on a new, empty folder, and nothing an earlier consent queued (whether or not its folder could be
/// deleted or read, and whatever a late write of the old start put there) is ever read again. Old epochs' folders are
/// deleted at start and at each change of the settings, best effort: one that can't be is never read. Off at start: the
/// SDK is never started and no id is made. Turned off: the SDK's sending is retired (<see cref="SendGeneration"/>: nothing
/// more leaves, the send in flight is abandoned) and it is stopped unflushed. A build without an address (a build from
/// source, or <c>--offline</c>) never starts the SDK, but deletes old folders all the same. Stopping ends within
/// <see cref="CrashReportsOptions.StopDeadline"/>; past it, the sending is retired. A clean stop ends the session as
/// exited, a fatal one as crashed, then sends what is queued within 2 s. No failure of the reports (a folder it can't
/// write, the SDK's own) ever stops the engine: they are off for the run, with one warning. Settings read before the
/// store opens (bad arguments, a data folder in use) are never sent. No free text leaves the engine
/// (<see cref="CrashScrubber"/>).
/// </summary>
public sealed class CrashReports : IDisposable
{
    /// <summary>The assembly metadata a release build carries its crash-report address in.</summary>
    public const string DsnMetadata = "InfoPanel.CrashReportsDsn";

    public const string UncleanExitMessage = "The previous run ended unexpectedly (a crash, a forced quit or a power loss)";
    public const string PluginHostExitMessage = "A plugin host exited unexpectedly";

    /// <summary>The most logged errors one run reports.</summary>
    public const int MaxLoggedPerRun = 20;

    /// <summary>One logged error per exception type and top InfoPanel frame in this time.</summary>
    public static readonly TimeSpan LoggedWindow = TimeSpan.FromHours(1);

    /// <summary>The SDK's folder in the data folder (its queue and its installation id, seeded with ours).</summary>
    public const string FolderName = DataPaths.CrashReportsFolderName;

    private static CrashReports? s_current;

    private readonly CrashReportsOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Type, string? Frame), DateTimeOffset> _sentLogged = [];
    private readonly Dictionary<string, KnownValue> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _engineTexts = new(StringComparer.Ordinal)
    {
        [UncleanExitMessage] = true,
        [PluginHostExitMessage] = true,
    };

    private Task? _reading;
    private SettingsStore? _settings;
    private IDisposable? _sdk;
    private SentryOptions? _sdkOptions;
    private SendGeneration? _generation;
    private string? _installId;
    private string? _epoch;
    private bool _sweepWarned;
    private int _loggedThisRun;
    private bool _uncleanExit;
    private bool _uncleanReported;
    private bool _disposed;

    /// <summary>The longest a report waits for the names it removes (<see cref="CrashReportsOptions.KnownValues"/>); past it, those read before.</summary>
    public static readonly TimeSpan NamesWait = TimeSpan.FromMilliseconds(500);

    /// <summary>The most log templates kept as the engine's own texts (<see cref="IsEngineText"/>); later ones are sent as no text.</summary>
    private const int MaxEngineTexts = 4096;

    private CrashReports(CrashReportsOptions options) => _options = options;

    /// <summary>The reports' folder in the data folder: one folder in it per consent epoch.</summary>
    private string Folder => Path.Combine(_options.Paths.Root, FolderName);

    /// <summary>The folder the SDK keeps its queue, session and copy of the id in under consent <paramref name="epoch"/>.</summary>
    public static string EpochFolderOf(DataPaths paths, string epoch) => Path.Combine(paths.Root, FolderName, epoch);

    /// <summary>The consent epoch the SDK runs under now, or null while it doesn't run.</summary>
    public string? Epoch => Volatile.Read(ref _epoch);

    /// <summary>The reports this engine started, while they exist (a plugin host's exit reaches them here).</summary>
    public static CrashReports? Current => Volatile.Read(ref s_current);

    /// <summary>Whether the SDK runs now: a crash-report address, and the setting on.</summary>
    public bool IsOn => Volatile.Read(ref _sdk) != null;

    /// <summary>Whether this build names a crash-report address: without one the SDK never starts, though the switch is followed (turned off, the queue is dropped).</summary>
    public bool HasAddress => !string.IsNullOrWhiteSpace(_options.Dsn);

    /// <summary>The id this install's reports carry, once made; null before reports first start.</summary>
    public string? InstallId => _installId;

    /// <summary>The crash-report address <paramref name="assembly"/> (the engine's, by default) was built with, or null: a build from source has none.</summary>
    public static string? BuiltInDsn(Assembly? assembly = null) =>
        (assembly ?? typeof(CrashReports).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == DsnMetadata)?.Value is { Length: > 0 } dsn && !string.IsNullOrWhiteSpace(dsn) ? dsn.Trim() : null;

    /// <summary>The environment a version is released to: a bare version <c>production</c>, <c>-beta.N</c> <c>beta</c>, <c>-dev.ci.N</c> <c>ci</c>, else <c>development</c>.</summary>
    public static string EnvironmentOf(string version)
    {
        var core = version.Split('+')[0];
        if (!core.Contains('-', StringComparison.Ordinal))
            return "production";
        if (core.Contains("-beta.", StringComparison.Ordinal))
            return "beta";
        return core.Contains("-dev.ci.", StringComparison.Ordinal) ? "ci" : "development";
    }

    /// <summary>This computer's names and folders, as the scrubber's labels: the user, the computer, the home, data and 1.x folders.</summary>
    public static IReadOnlyList<KnownValue> ThisComputer(DataPaths paths, string? v1Folder) =>
        ThisComputer(paths, v1Folder, System.Environment.UserName, System.Environment.MachineName,
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

    /// <summary>The scrubber's labels for a computer <paramref name="computer"/>, its user <paramref name="user"/> and their folders.</summary>
    public static IReadOnlyList<KnownValue> ThisComputer(DataPaths paths, string? v1Folder, string user, string computer, string? home)
    {
        var known = new List<KnownValue>
        {
            new(paths.Root, "<data folder>"),
            new(user, "<user>"),
            new(computer, "<computer>"),
        };
        if (v1Folder != null)
            known.Add(new(v1Folder, "<1.x folder>"));
        if (home is { Length: > 1 })
            known.Add(new(home, "~"));
        return known;
    }

    /// <summary>
    /// The names the reports remove besides the user's and computer's, as the engine has them now: profiles'; plugin
    /// packages' folders and names and their plugins' ids and names; saved panels' serials and 1.x device ids and locations
    /// (<c>devices.json</c>); sensors' and their groups' names of 3 characters or more (shorter ones would turn up inside
    /// ordinary words). Families' and models' names are products', not the user's: kept.
    /// </summary>
    public static IEnumerable<KnownValue> EngineNames(Workspace workspace, DataPaths paths, IServiceProvider? services)
    {
        var names = new List<KnownValue>();
        foreach (var entry in workspace.List())
            Add(entry.Name, "<profile>");

        if (services?.GetService(typeof(PluginHostManager)) is PluginHostManager plugins)
        {
            foreach (var runner in plugins.Runners)
            {
                Add(runner.Package.Folder, "<plugin>");
                Add(runner.Package.Info.Name, "<plugin>");
                foreach (var plugin in runner.Plugins)
                {
                    Add(plugin.Id, "<plugin>");
                    Add(plugin.Name, "<plugin>");
                }
            }
        }

        foreach (var panel in Panels.PanelStore.Read(paths.DevicesFile, Serilog.Core.Logger.None).Devices)
        {
            Add(panel.Serial, "<device>");
            Add(panel.V1?.DeviceId, "<device>");
            Add(panel.V1?.DeviceLocation, "<device>");
        }

        if (services?.GetService(typeof(Sensors.SensorHub)) is Sensors.SensorHub hub)
        {
            foreach (var sensor in hub.GetSensors())
            {
                if (sensor.Name.Length >= 3)
                    Add(sensor.Name, "<sensor>");
                if (sensor.Group.Length >= 3)
                    Add(sensor.Group, "<sensor>");
            }
        }

        return names;

        void Add(string? value, string label)
        {
            if (!string.IsNullOrWhiteSpace(value))
                names.Add(new(value, label));
        }
    }

    /// <summary>The SDK's own folder in <paramref name="folder"/> for <paramref name="dsn"/>: <c>Sentry/{SHA-1 of the address}</c>.</summary>
    public static string SdkFolderOf(string folder, string dsn) =>
        Path.Combine(folder, "Sentry", Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(dsn))));

    /// <summary>
    /// Starts crash reports as the settings say, and follows their changes, in every build: one that names no crash-report
    /// address never starts the SDK or sends anything, but deletes old epochs' folders. Reads <c>runtime.json</c> first:
    /// one left behind (the engine holds its data folder's lock, so no other engine wrote it) is a previous run that didn't
    /// shut down.
    /// </summary>
    public static CrashReports Start(CrashReportsOptions options, SettingsStore settings, TemplateBreadcrumbSink? sink = null)
    {
        var reports = new CrashReports(options) { _settings = settings };
        if (!reports.HasAddress)
        {
            options.Logger.Information("Crash reports: none in this build (it names no crash-report address)");
        }
        else
        {
            try
            {
                reports._uncleanExit = RegularFile.StateOf(options.Paths.RuntimeFile) != PathState.Absent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not known: not reported.
            }
        }

        Volatile.Write(ref s_current, reports);
        if (sink != null)
            sink.Reports = reports;
        // Under the store's lock, as each change's handler runs: no change comes between the first look and the handler.
        settings.Read(snapshot =>
        {
            settings.Changed += reports.OnSettingsChanged;
            reports.Follow(snapshot.Settings.Telemetry);
            return true;
        });
        return reports;
    }

    /// <summary>Under the store's lock (its <see cref="SettingsStore.Changed"/>), in the changes' order.</summary>
    private void OnSettingsChanged(SettingsSnapshot snapshot) => Follow(snapshot.Settings.Telemetry);

    /// <summary>
    /// Follows the switch and its consent epoch, under the settings store's lock. Off: the SDK stopped when it runs. On: the
    /// SDK started on its epoch's folder (a build with an address), or, running on an earlier epoch's, stopped as turned
    /// off and started on the new one. Then every other epoch's folder is deleted, best effort.
    /// </summary>
    private void Follow(TelemetrySettings telemetry)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!telemetry.CrashReports)
            {
                if (IsOn)
                    Close(turnedOff: true);
            }
            else if (HasAddress && (!IsOn || (telemetry.CrashReportsEpoch is { } epoch && epoch != _epoch)))
            {
                // A new consent: the start before it is retired, unflushed, and never read again.
                if (IsOn)
                    Close(turnedOff: true);
                Contained(() => Open(telemetry.CrashReportsEpoch), "start");
            }

            Sweep(_epoch ?? (telemetry.CrashReports ? telemetry.CrashReportsEpoch : null));
        }
    }

    /// <summary>Runs <paramref name="action"/>; a failure turns the reports off for the run, with one warning, and never reaches the caller.</summary>
    private void Contained(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try
            {
                // A start that failed part way: its sending retired, its transport stopped.
                if (Volatile.Read(ref _sdk) == null && _sdkOptions != null)
                {
                    _generation?.Retire();
                    (_sdkOptions.Transport as IDisposable)?.Dispose();
                    _sdkOptions = null;
                    _generation = null;
                    Volatile.Write(ref _epoch, null);
                    SentrySdk.Close();
                }

                _options.Logger.Warning(ex, "Crash reports: couldn't {What}; none are sent until they are turned on again", what);
            }
            catch (Exception)
            {
                // Nothing more to do: reports stay off.
            }
        }
    }

    /// <summary>
    /// Starts the SDK on consent <paramref name="epoch"/>'s folder. With none (reports on since the install, or settings
    /// that lost it), one is kept in the settings first; when it can't be, this run's reports are kept in a folder of the
    /// run's own, never read by a later start.
    /// </summary>
    private void Open(string? epoch)
    {
        if (epoch == null)
        {
            try
            {
                epoch = _settings?.CrashReportsEpoch();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _options.Logger.Warning("Crash reports: their consent couldn't be kept in the settings, so this run's are kept for this run only ({Reason})", ex.GetType().Name);
            }

            epoch ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        _installId ??= InstallIdFor(_options.Paths);
        var folder = EpochFolderOf(_options.Paths, epoch);
        // A link as the reports' folder or the epoch's would lead the SDK to a queue elsewhere, never this consent's.
        if (IsLink(Folder) || IsLink(folder))
        {
            _options.Logger.Warning("Crash reports: their folder in the data folder is a link, so none are sent this run");
            return;
        }

        if (_installId == null || !Seed(folder, _options.Dsn!, _installId))
        {
            _options.Logger.Warning("Crash reports: the install's id couldn't be kept in the data folder, so none are sent this run");
            return;
        }

        var installId = _installId;
        var generation = new SendGeneration();
        var options = new SentryOptions
        {
            Dsn = _options.Dsn,
            Debug = false,
            SendDefaultPii = false,
            IsEnvironmentUser = false,
            AttachStacktrace = true,
            ReportAssembliesMode = ReportAssembliesMode.None,
            AutoSessionTracking = true,
            IsGlobalModeEnabled = true,
            MaxBreadcrumbs = 50,
            Release = _options.Release,
            Environment = _options.Environment,
            // The SDK's queue and its installation id (which sessions count users by) in the data folder, the id seeded with
            // ours: without a file of its own the SDK would take the MAC address.
            CacheDirectoryPath = folder,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            InitCacheFlushTimeout = TimeSpan.Zero,
            SendClientReports = false,
            CrashedLastRun = () => _uncleanExit,
        };
        options.DisableDiagnosticSourceIntegration();
        options.SetBeforeSend((@event, _) => generation.Live ? CrashScrubber.Scrub(@event, installId, KnownNow(), IsEngineText) : null);
        // Only the log's template breadcrumbs: none of the SDK's own (it would add HTTP and others).
        options.SetBeforeBreadcrumb(crumb => crumb.Type == TemplateBreadcrumbSink.BreadcrumbType ? crumb : null);
        // Every send behind this start's generation: retired, nothing more leaves and the send in flight is abandoned.
        if (_options.Transport != null)
            options.Transport = new GatedTransport(_options.Transport, generation);
        else
            options.CreateHttpMessageHandler = () => new GatedHandler(generation);
        if (_options.NetworkStatus != null)
            options.NetworkStatusListener = _options.NetworkStatus;

        _generation = generation;
        _sdkOptions = options;
        var sdk = SentrySdk.Init(options);
        SentrySdk.ConfigureScope(scope =>
        {
            scope.User = new SentryUser { Id = installId };
            scope.SetTag("process", "engine");
            scope.SetTag("os", OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux");
        });
        Volatile.Write(ref _epoch, epoch);
        Volatile.Write(ref _sdk, sdk);
        _options.Logger.Information("Crash reports: on ({Environment})", _options.Environment);

        if (_uncleanExit && !_uncleanReported)
        {
            _uncleanReported = true;
            SentrySdk.CaptureMessage(UncleanExitMessage, SentryLevel.Error);
        }
    }

    /// <summary>
    /// Stops the SDK, within <see cref="CrashReportsOptions.StopDeadline"/>. Turned off: its sending is retired first
    /// (nothing more is sent, the send in flight abandoned), then it stops without flushing; its epoch's folder is never
    /// read again (<see cref="Follow"/> deletes it). The engine stopping: the session ends as exited
    /// (<paramref name="fatal"/>: as crashed), and what is queued is sent within the SDK's 2 s. Past the deadline the
    /// sending is retired and the engine goes on.
    /// </summary>
    private void Close(bool turnedOff, bool fatal = false)
    {
        var sdk = Interlocked.Exchange(ref _sdk, null);
        if (sdk == null)
            return;
        var options = _sdkOptions;
        var generation = _generation;
        _sdkOptions = null;
        _generation = null;
        Volatile.Write(ref _epoch, null);
        if (turnedOff)
        {
            generation?.Retire();
            Contained(() => Within(() =>
            {
                // Nothing flushed: the queue is dropped, not sent. The transport (the SDK's caching one, its worker) stops first.
                if (options != null)
                    options.ShutdownTimeout = TimeSpan.Zero;
                (options?.Transport as IDisposable)?.Dispose();
                sdk.Dispose();
            }, generation), "stop when turned off");
        }
        else
        {
            // A clean stop: the session ends (else the next start would take it for an abnormal end), then what is queued
            // is sent within the shutdown timeout, then the transport's worker stops.
            Contained(() => Within(() =>
            {
                SentrySdk.EndSession(fatal ? SessionEndStatus.Crashed : SessionEndStatus.Exited);
                sdk.Dispose();
                (options?.Transport as IDisposable)?.Dispose();
            }, generation), "stop");
            generation?.Retire();
        }

        _options.Logger.Information("Crash reports: off ({Why})", turnedOff ? "turned off" : fatal ? "the engine stopped on an error" : "the engine stopped");
    }

    /// <summary>
    /// Runs <paramref name="stop"/>, waiting at most <see cref="CrashReportsOptions.StopDeadline"/>: past it,
    /// <paramref name="generation"/> is retired, so nothing it holds is ever sent, and the stop goes on alone.
    /// </summary>
    private void Within(Action stop, SendGeneration? generation)
    {
        var stopping = Task.Run(stop);
        bool done;
        try
        {
            done = stopping.Wait(_options.StopDeadline);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
            throw;
        }

        if (done)
            return;
        generation?.Retire();
        _ = stopping.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _options.Logger.Warning("Crash reports: stopping took over {Seconds} s; what they still held is never sent", _options.StopDeadline.TotalSeconds);
    }

    /// <summary>The most entries of the reports' folder one sweep looks at; the rest wait for the next.</summary>
    internal const int MaxSweptEntries = 100;

    /// <summary>
    /// Deletes every entry of the reports' folder but <paramref name="keep"/>'s (the consent epoch in effect), best effort:
    /// at most <see cref="MaxSweptEntries"/> a sweep, a link removed and never followed. What can't be deleted stays, and
    /// is never read: no start reads a folder but its own epoch's. Logged once a run as a warning, then as debug.
    /// </summary>
    private void Sweep(string? keep)
    {
        var failed = 0;
        Exception? first = null;
        try
        {
            var folder = new DirectoryInfo(Folder);
            if (folder.LinkTarget != null || !folder.Exists)
                return;
            foreach (var entry in folder.EnumerateFileSystemInfos().Take(MaxSweptEntries))
            {
                // Every case variant of the epoch stays: on a case-insensitive filesystem it is the active folder.
                if (string.Equals(entry.Name, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (entry is DirectoryInfo directory && directory.LinkTarget == null)
                        directory.Delete(recursive: true);
                    else if (entry is DirectoryInfo { LinkTarget: not null } && OperatingSystem.IsWindows())
                        Directory.Delete(entry.FullName);
                    else
                        File.Delete(entry.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    first ??= ex;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failed++;
            first ??= ex;
        }

        if (failed == 0)
            return;
        if (!_sweepWarned)
        {
            _sweepWarned = true;
            _options.Logger.Warning("Crash reports: {Count} folders of reports from before they were last turned on couldn't be deleted ({Reason}); they are never sent", failed, first!.GetType().Name);
        }
        else
        {
            _options.Logger.Debug("Crash reports: {Count} folders of reports from before they were last turned on couldn't be deleted", failed);
        }
    }

    /// <summary>
    /// The values free text loses now: the fixed ones, then every name read this run, read again first (on another thread,
    /// waiting at most <see cref="NamesWait"/>). A read that fails leaves the names read before; one that takes too long
    /// is kept when it ends, for the reports after.
    /// </summary>
    private IReadOnlyList<KnownValue> KnownNow()
    {
        if (_options.KnownValues is { } read)
        {
            Task reading;
            lock (_names)
            {
                if (_reading is not { IsCompleted: false })
                    _reading = Task.Run(() => Remember(read));
                reading = _reading;
            }

            reading.Wait(NamesWait);
        }

        lock (_names)
            return [.. _options.Fixed ?? [], .. _names.Values];
    }

    /// <summary>Reads the names and keeps them with those read before; a read that fails keeps those.</summary>
    private void Remember(Func<IEnumerable<KnownValue>> read)
    {
        List<KnownValue> names;
        try
        {
            names = [.. read()];
        }
        catch (Exception)
        {
            return;
        }

        lock (_names)
        {
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name.Value))
                    _names.TryAdd(name.Value, name);
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> is the engine's own: a message it reports (<see cref="UncleanExitMessage"/>, <see cref="PluginHostExitMessage"/>) or a template it logged.</summary>
    private bool IsEngineText(string text) => _engineTexts.ContainsKey(text);

    /// <summary>Keeps a log template as the engine's own text, which an event and a breadcrumb may then carry.</summary>
    private void EngineText(string? template)
    {
        if (template != null && _engineTexts.Count < MaxEngineTexts)
            _engineTexts.TryAdd(template, true);
    }

    /// <summary>Adds a breadcrumb while reports are on.</summary>
    internal void AddBreadcrumb(Breadcrumb breadcrumb)
    {
        if (!IsOn)
            return;
        EngineText(breadcrumb.Message);
        SentrySdk.AddBreadcrumb(breadcrumb);
    }

    /// <summary>
    /// An error the engine logged with <paramref name="exception"/>: reported at level error, at most once per exception
    /// type and top InfoPanel frame in <see cref="LoggedWindow"/> and <see cref="MaxLoggedPerRun"/> a run. Never one from
    /// the plugin hosts (their messages carry a plugin's own text) or a <see cref="PluginHostException"/>, and never the
    /// crash log's, whose exceptions the SDK reports itself.
    /// </summary>
    internal void ReportLogged(Exception exception, string template, string? source, bool fatal)
    {
        if (!IsOn)
            return;
        // A failed request is logged by ASP.NET (Microsoft.*) and a fatal start by the entry point (no source): both count.
        if (source is not null && (source.StartsWith("InfoPanel.Plugins", StringComparison.Ordinal) || source == typeof(Hosting.CrashLogging).FullName
            || source.StartsWith(typeof(CrashReports).Namespace!, StringComparison.Ordinal)))
            return;
        if (Chain(exception).Any(e => e is PluginHostException))
            return;

        var key = (exception.GetType().FullName ?? exception.GetType().Name, TopFrame(exception));
        var now = _options.Time.GetUtcNow();
        lock (_sentLogged)
        {
            if (_loggedThisRun >= MaxLoggedPerRun || (_sentLogged.TryGetValue(key, out var last) && now - last < LoggedWindow))
                return;
            _sentLogged[key] = now;
            _loggedThisRun++;
        }

        EngineText(template);
        SentrySdk.CaptureEvent(new SentryEvent(exception)
        {
            Level = fatal ? SentryLevel.Fatal : SentryLevel.Error,
            Message = new SentryMessage { Message = template },
        });
    }

    /// <summary>A plugin host's process ended without the engine asking (the plugin hosts' supervisor): one warning, without the plugin's name, path or id.</summary>
    public static void PluginHostExited(int? exitCode, TimeSpan ran, int restarts)
    {
        if (Current is not { IsOn: true })
            return;
        var @event = new SentryEvent { Level = SentryLevel.Warning, Message = new SentryMessage { Message = PluginHostExitMessage } };
        @event.SetTag("exit_code", exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
        @event.SetTag("ran", RanBucket(ran));
        @event.SetTag("restarts", restarts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SentrySdk.CaptureEvent(@event);
    }

    /// <summary>How long a host ran, in buckets.</summary>
    internal static string RanBucket(TimeSpan ran) => ran.TotalSeconds switch
    {
        < 10 => "under 10 s",
        < 60 => "10 s to 1 min",
        < 600 => "1 to 10 min",
        < 3600 => "10 min to 1 h",
        _ => "over 1 h",
    };

    /// <summary>Waits up to <paramref name="timeout"/> for queued reports to be sent (tests, and before a crash test exits).</summary>
    public void Flush(TimeSpan timeout)
    {
        if (IsOn)
            SentrySdk.Flush(timeout);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var seen = 0;
        var queue = new Queue<Exception>([exception]);
        while (queue.Count > 0 && seen++ < 64)
        {
            var next = queue.Dequeue();
            yield return next;
            if (next is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    queue.Enqueue(inner);
            }
            else if (next.InnerException != null)
            {
                queue.Enqueue(next.InnerException);
            }
        }
    }

    /// <summary>The first InfoPanel method in the exception's stack trace, as <c>Type.Method</c>, or null.</summary>
    private static string? TopFrame(Exception exception)
    {
        foreach (var frame in new StackTrace(exception, false).GetFrames())
        {
            if (frame.GetMethod() is { DeclaringType: { } type } method && (type.Namespace ?? "").StartsWith("InfoPanel", StringComparison.Ordinal))
                return $"{type.FullName}.{method.Name}";
        }

        return null;
    }

    // ---- the install's id ----------------------------------------------------------------------------------------------

    /// <summary>The data folder's <c>crash-reports.json</c>.</summary>
    public static string FileOf(DataPaths paths) => Path.Combine(paths.Root, DataPaths.CrashReportsFileName);

    /// <summary>
    /// The install's id in <c>crash-reports.json</c> (<c>{"installId": "…"}</c>): read, or made once (a random UUID, never
    /// anything of the computer's) and written. Null when it can't be read or written: no reports then.
    /// </summary>
    internal static string? InstallIdFor(DataPaths paths)
    {
        var file = FileOf(paths);
        try
        {
            if (RegularFile.StateOf(file) != PathState.Absent)
            {
                var stored = JsonNode.Parse(RegularFile.ReadAtMost(file, 4096))?["installId"]?.GetValue<string>();
                return Guid.TryParse(stored, out var id) ? id.ToString("D") : null;
            }

            var made = Guid.NewGuid().ToString("D");
            AtomicFile.Write(file, Encoding.UTF8.GetBytes(new JsonObject { ["installId"] = made }.ToJsonString()), paths.Root);
            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a symbolic link, a junction or another reparse point (absent: no).</summary>
    private static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>
    /// Gives the SDK our id as its installation id, the one its sessions count users by: it keeps it in
    /// <c>{folder}/Sentry/{SHA-1 of the address}/.installation</c> and reads it from there (else it makes one, or takes the
    /// MAC address when it can't write). True once the file holds <paramref name="installId"/>.
    /// </summary>
    internal static bool Seed(string folder, string dsn, string installId)
    {
        try
        {
            var directory = SdkFolderOf(folder, dsn);
            var file = Path.Combine(directory, ".installation");
            Directory.CreateDirectory(directory);
            if (RegularFile.StateOf(file) != PathState.Absent && Encoding.UTF8.GetString(RegularFile.ReadAtMost(file, 256)) == installId)
                return true;
            File.WriteAllText(file, installId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The engine stopping cleanly: the session ends as exited.</summary>
    public void Dispose() => Stop(fatal: false);

    /// <summary>
    /// The engine stopping: the session ends as exited, or, <paramref name="fatal"/> (the engine stopped on an error it
    /// couldn't go on from, such as a port it couldn't listen on), as crashed; then what is queued is sent, all within
    /// <see cref="CrashReportsOptions.StopDeadline"/>. Once only.
    /// </summary>
    public void Stop(bool fatal)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_settings != null)
                _settings.Changed -= OnSettingsChanged;
            Close(turnedOff: false, fatal);
        }

        Interlocked.CompareExchange(ref s_current, null, this);
    }
}
