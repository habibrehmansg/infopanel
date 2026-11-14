using System.Text.Json;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Spec;
using StreamJsonRpc;

namespace InfoPanel.Plugins;

/// <summary>An error of a package's: when the engine got it, the plugin it came from (null: the package's own), and what it says.</summary>
public sealed record PluginErrorEntry(DateTimeOffset Time, string? Plugin, string Message);

/// <summary>An action a plugin lists: its method, and its name for people.</summary>
public sealed record PluginActionInfo(string Method, string Name);

/// <summary>A plugin a package's host loaded this run (never a setting's value).</summary>
public sealed record PluginLoaded(string Id, string Name, string Description, IReadOnlyList<PluginActionInfo> Actions, bool Configurable);

/// <summary>How a call of a package's plugin went.</summary>
public enum PluginCall
{
    Done,

    /// <summary>The running host lists no such plugin, or the plugin no such action, settings or key.</summary>
    NotFound,

    /// <summary>No host runs.</summary>
    NotRunning,

    /// <summary>The host didn't answer within <see cref="PluginHostTimeouts.Call"/>, or failed to.</summary>
    NoAnswer,

    /// <summary>The setting doesn't take the value.</summary>
    Invalid,

    /// <summary>The action isn't enabled now (its state says why): it wasn't run.</summary>
    Disabled,
}

/// <summary>Where the engine writes for its plugin hosts, and how it starts them.</summary>
public sealed record PluginHostSettings
{
    /// <summary>Each package's settings go in <c>{SettingsDirectory}/{folder}/</c>.</summary>
    public required string SettingsDirectory { get; init; }

    /// <summary>The hosts' logs, <c>{LogsDirectory}/{folder}-{yyyyMMdd}.log</c>.</summary>
    public required string LogsDirectory { get; init; }

    /// <summary>The hosts' log level (<c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c>), read at each start.</summary>
    public Func<string> LogLevel { get; init; } = () => "information";

    public PluginHostTimeouts Timeouts { get; init; } = PluginHostTimeouts.Default;

    public PluginLimits Limits { get; init; } = PluginLimits.Default;

    /// <summary>
    /// Why a host mustn't be given this folder to write in (it is the 1.x folder's: <c>V1Folder.FindWriteProblem</c>), or
    /// null; asked of each package's settings folder and of the log folder before each start.
    /// </summary>
    public Func<string, string?> WriteCheck { get; init; } = _ => null;
}

/// <summary>
/// Runs one package's host, as 1.4.x did: started and connected (<see cref="PluginHostConnection.OpenAsync"/>); an
/// unexpected exit, disconnect or failed start restarts it after <see cref="PluginHostTimeouts.RestartDelay"/>, at most
/// <see cref="PluginHostTimeouts.MaxRestarts"/> times in <see cref="PluginHostTimeouts.RestartWindow"/> (counted from the
/// window's first failure), after which the package is <see cref="PluginHostState.Failed"/> with the reason until it is
/// started again. An intentional stop never restarts: the host is asked to shut down within a bound, then killed. Before
/// each start, the folders the host will write in are checked (<see cref="WriteProblem"/>): one it mustn't be given fails
/// the package at once, with no host started and no restart.
/// </summary>
internal sealed class PluginHostRunner : IAsyncDisposable
{
    private readonly PluginPackage _package;
    private readonly PluginHostSettings _settings;
    private readonly IPluginProcessLauncher _launcher;
    private readonly PluginSensorSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    // Oldest first.
    private readonly List<PluginErrorEntry> _errors = [];
    private IReadOnlyList<PluginLoaded> _plugins = [];
    private IReadOnlyList<string> _claimed = [];
    private double? _cpu;

    private PluginHostConnection? _connection;
    // Bumped by every launch, restart and stop: work for an older generation finds it stale and drops what it holds.
    private int _generation;
    private bool _stopping = true;
    // Latched by the engine's stop: nothing starts the host after it.
    private bool _closed;
    private CancellationTokenSource _lifetime = new();
    private Task _pending = Task.CompletedTask;
    private DateTimeOffset? _windowStart;
    private int _restarts;

    public PluginHostRunner(PluginPackage package, PluginHostSettings settings, IPluginProcessLauncher launcher, PluginSensorSource source,
        TimeProvider time, ILogger logger)
    {
        _package = package;
        _settings = settings;
        _launcher = launcher;
        _source = source;
        _time = time;
        _logger = logger;
    }

    public PluginPackage Package => _package;

    /// <summary>Its place in the order the packages were found (<see cref="PluginPackages.Find"/>): the earlier keeps a plugin id two provide.</summary>
    internal int Order { get; init; }

    /// <summary>
    /// The manager's one-id-one-package rule, asked when the host lists its plugins' ids: null to take them, else why the
    /// package can't (another package earlier in the list provides one). Null: no rule (a runner on its own).
    /// </summary>
    internal Func<PluginHostRunner, int, IReadOnlyList<string>, string?>? Claim { get; init; }

    /// <summary>The generation work is started for now (each launch, restart and stop bumps it).</summary>
    internal int Generation
    {
        get
        {
            lock (_lock)
                return _generation;
        }
    }

    /// <summary>The tests' barrier: awaited once a settings change has read the settings it checks the value against.</summary>
    internal Func<Task>? AfterSettingsRead { get; set; }

    /// <summary>
    /// Tests only: given a host's update call (its generation, and its hand-off to the source) once it was found current,
    /// to run the hand-off when it likes.
    /// </summary>
    internal Action<int, Action>? SourceUpdate { get; set; }

    /// <summary>The plugin ids its host took this run, while it is starting, running or restarting; else none.</summary>
    internal IReadOnlyList<string> ClaimedIds
    {
        get
        {
            lock (_lock)
                return _state is PluginHostState.Starting or PluginHostState.Running or PluginHostState.Restarting ? _claimed : [];
        }
    }

    /// <summary>Why it failed, for programs, while it is <see cref="PluginHostState.Failed"/>: <c>gaveUp</c>, <c>notStarted</c> or <c>duplicateId</c>.</summary>
    public string? ReasonCode { get; private set; }

    /// <summary>The plugins its host loaded this run, kept once it stops (none before it has run).</summary>
    public IReadOnlyList<PluginLoaded> Plugins
    {
        get
        {
            lock (_lock)
                return _plugins;
        }
    }

    /// <summary>
    /// Its host's processor use as the host last reported it, counted as the engine's own (<see cref="CpuShown"/>: per CPU
    /// off Windows, 0–100 × the CPUs; a share of the machine's on Windows, 0–100), while it runs; null before its first report.
    /// </summary>
    public double? Cpu
    {
        get
        {
            lock (_lock)
                return _state == PluginHostState.Running ? _cpu : null;
        }
    }

    /// <summary>The engine's own CPU rule (<see cref="InfoPanel.Sensors.SystemInfo.EngineSource"/>): the CPUs the OS counts, and whether its tool counts per CPU.</summary>
    private static readonly InfoPanel.Sensors.SystemInfo.IEngineProcess EngineCpu = InfoPanel.Sensors.SystemInfo.EngineSource.CurrentProcess();

    /// <summary>
    /// A host's report (its CPU time over the wall time, per CPU: 100 a busy CPU) as the engine shows its own CPU:
    /// <paramref name="perCpu"/> (off Windows, as Activity Monitor and <c>top</c>) as reported, within 0–100 ×
    /// <paramref name="processors"/>; else (Windows, as Task Manager) a share of every CPU's, the report over the CPUs,
    /// within 0–100.
    /// </summary>
    internal static double CpuShown(double reported, int processors, bool perCpu)
    {
        var cpus = Math.Max(1, processors);
        return perCpu ? Math.Clamp(reported, 0, 100d * cpus) : Math.Clamp(reported / cpus, 0, 100);
    }

    /// <summary>Its host's memory in bytes, read from the process, while it runs.</summary>
    public long? Memory
    {
        get
        {
            PluginHostConnection? connection;
            lock (_lock)
                connection = _state == PluginHostState.Running ? _connection : null;
            return connection?.Process.MemoryBytes;
        }
    }

    /// <summary>The package's state, published after its sensors' (<see cref="SetState"/>).</summary>
    public PluginHostState State => _state;

    private volatile PluginHostState _state = PluginHostState.Stopped;

    /// <summary>Why the package failed, while it is <see cref="PluginHostState.Failed"/>.</summary>
    public string? Reason { get; private set; }

    /// <summary>The host's process id while one runs.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_lock)
                return _connection is { } c && !c.Process.HasExited ? c.Process.Id : null;
        }
    }

    /// <summary>The package's newest errors (from its plugins, its loads, its host's failures), oldest first, as text.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
                return [.. _errors.Select(e => e.Plugin == null ? e.Message : $"{e.Plugin}: {e.Message}")];
        }
    }

    /// <summary>The package's newest errors, newest first, each with its time and plugin.</summary>
    public IReadOnlyList<PluginErrorEntry> ErrorLog
    {
        get
        {
            lock (_errors)
                return [.. Enumerable.Reverse(_errors)];
        }
    }

    /// <summary>Starts the host, the restart policy afresh. Nothing when it is already running or starting, or the engine's stop closed it.</summary>
    public Task StartAsync()
    {
        lock (_lock)
        {
            if (_closed)
                return Task.CompletedTask;
            if (!_stopping)
                return _pending;
            _stopping = false;
            _lifetime = new CancellationTokenSource();
            _windowStart = null;
            _restarts = 0;
            Reason = null;
            ReasonCode = null;
            _cpu = null;
            SetState(PluginHostState.Starting);
            var generation = ++_generation;
            var token = _lifetime.Token;
            return _pending = Task.Run(() => LaunchAsync(generation, token));
        }
    }

    /// <summary>
    /// Stops the host on purpose (never restarted): it is asked to shut down within <paramref name="within"/>, then
    /// killed and waited for (<see cref="PluginHostTimeouts.KillWait"/>); a start or restart in flight is cancelled.
    /// <paramref name="close"/> (the engine's stop): no later <see cref="StartAsync"/> starts it.
    /// </summary>
    public async Task StopAsync(TimeSpan within, bool close = false)
    {
        PluginHostConnection? connection;
        Task pending;
        CancellationTokenSource lifetime;
        lock (_lock)
        {
            _stopping = true;
            _closed |= close;
            _generation++;
            connection = _connection;
            _connection = null;
            _claimed = [];
            _cpu = null;
            pending = _pending;
            lifetime = _lifetime;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        if (connection != null)
        {
            await connection.ShutdownAsync(within, _settings.Timeouts.KillWait, _logger, _package.Folder).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // A launch in flight sees the cancellation and kills what it started.
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
        }

        lock (_lock)
        {
            if (_stopping)
                SetState(PluginHostState.Stopped);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.Zero).ConfigureAwait(false);

    private async Task LaunchAsync(int generation, CancellationToken ct)
    {
        // A stale generation (stopped, refused, superseded) launches nothing.
        if (ct.IsCancellationRequested || !IsCurrent(generation))
            return;
        if (WriteProblem() is { } problem)
        {
            GiveUp(generation, problem);
            return;
        }

        var budget = PendingBudget.For(_settings.Limits);
        var callback = new Callback(this, generation, budget);
        PluginHostConnection connection;
        try
        {
            connection = await PluginHostConnection.OpenAsync(_package, Command, _launcher, callback, budget, callback.Ignored, callback.Loaded.Task,
                _settings.Timeouts, _settings.Limits, _time, _logger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            var reason = ex is PluginHostException ? ex.Message : $"its host couldn't be started: {ex.Message}";
            Failed(generation, reason, null);
            return;
        }

        lock (_lock)
        {
            if (generation == _generation && !_stopping)
            {
                _connection = connection;
                _cpu = null;
                if (!callback.Loaded.Task.IsCompleted)
                    _plugins = LoadedOf(connection.Plugins, null);
                SetState(PluginHostState.Running);
                _logger.LogInformation("plugin {Folder}: its host (process {Pid}) runs {Count} plugins", _package.Folder, connection.Process.Id, connection.Plugins.Count);
                _ = WatchAsync(generation, connection);
                return;
            }
        }

        // Stopped or superseded while it connected.
        await connection.ShutdownAsync(TimeSpan.Zero, _settings.Timeouts.KillWait, _logger, _package.Folder).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private bool IsCurrent(int generation)
    {
        lock (_lock)
            return generation == _generation && !_stopping;
    }

    private async Task WatchAsync(int generation, PluginHostConnection connection)
    {
        var running = _time.GetUtcNow();
        var reason = await connection.Ended.ConfigureAwait(false);
        // Ended without the engine asking: a crash report says so, without the plugin's name, path or id.
        if (IsCurrent(generation))
            CrashReporting.CrashReports.PluginHostExited(connection.Process.ExitCode, _time.GetUtcNow() - running, Volatile.Read(ref _restarts));
        Failed(generation, reason, connection);
    }

    /// <summary>The host of <paramref name="generation"/> ended or couldn't start: the restart policy decides what's next.</summary>
    private void Failed(int generation, string reason, PluginHostConnection? connection)
    {
        lock (_lock)
        {
            if (generation != _generation || _stopping)
                return;

            _connection = null;
            var now = _time.GetUtcNow();
            if (_windowStart is not { } start || now - start > _settings.Timeouts.RestartWindow)
            {
                _windowStart = now;
                _restarts = 0;
            }

            _cpu = null;
            if (_restarts >= _settings.Timeouts.MaxRestarts)
            {
                _generation++;
                Reason = reason;
                ReasonCode = "gaveUp";
                _claimed = [];
                SetState(PluginHostState.Failed);
                Error($"gave up after {_restarts} restarts in {_settings.Timeouts.RestartWindow.TotalSeconds:0} s: {reason}");
                _pending = Task.Run(() => DropAsync(connection));
                return;
            }

            _restarts++;
            // The error first: whoever sees the state sees why.
            Error($"{reason}; restarting it in {_settings.Timeouts.RestartDelay.TotalSeconds:0} s ({_restarts} of {_settings.Timeouts.MaxRestarts})");
            SetState(PluginHostState.Restarting);
            var next = ++_generation;
            var token = _lifetime.Token;
            _pending = Task.Run(() => RestartAsync(next, connection, token));
        }
    }

    /// <summary>The package fails at once with <paramref name="reason"/>: no restart (a start afresh checks again).</summary>
    private void GiveUp(int generation, string reason)
    {
        lock (_lock)
        {
            if (generation != _generation || _stopping)
                return;
            _generation++;
            Reason = reason;
            ReasonCode = "notStarted";
            _claimed = [];
            SetState(PluginHostState.Failed);
            Error($"not started: {reason}");
        }
    }

    /// <summary>
    /// The package loses a plugin id to a package earlier in the list (<paramref name="reason"/> names both): its host is
    /// stopped (within <see cref="PluginHostTimeouts.Stop"/>, then killed), it fails with <c>duplicateId</c>, and it isn't
    /// restarted; reloading it or turning it on tries again. Nothing when <paramref name="generation"/> (if given) is gone,
    /// or it is being stopped.
    /// </summary>
    internal void FailDuplicate(int? generation, string reason)
    {
        lock (_lock)
        {
            if (generation is { } g && g != _generation || _stopping || _state is PluginHostState.Failed or PluginHostState.Stopped)
                return;
            _generation++;
            var connection = _connection;
            _connection = null;
            _claimed = [];
            _cpu = null;
            Reason = reason;
            ReasonCode = "duplicateId";
            SetState(PluginHostState.Failed);
            Error($"stopped: {reason}");
            // The displaced generation's work (a launch, a restart waiting out its delay) is cancelled and kept in the
            // chain a later stop awaits.
            var pending = _pending;
            var lifetime = _lifetime;
            _pending = Task.Run(async () =>
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                try
                {
                    await pending.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                if (connection != null)
                {
                    await connection.ShutdownAsync(_settings.Timeouts.Stop, _settings.Timeouts.KillWait, _logger, _package.Folder).ConfigureAwait(false);
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }
    }

    /// <summary>
    /// Why the host can't be started on the folders it would write in, or null: its settings folder
    /// (<c>{settings}/{folder}</c>) and the log folder must not be links (the engine never writes through one, and a link
    /// deeper in the data folder isn't checked at start-up), nor be the 1.x folder's (<see cref="PluginHostSettings.WriteCheck"/>);
    /// and none of the package's log files may be a link, since the host's log appends to its file.
    /// </summary>
    internal string? WriteProblem()
    {
        var logs = _settings.LogsDirectory;
        foreach (var (what, path) in new[] { ("settings folder", Path.Combine(_settings.SettingsDirectory, _package.Folder)), ("log folder", logs) })
        {
            try
            {
                if (new DirectoryInfo(path).LinkTarget != null)
                    return $"its {what} {path} is a link, which the engine doesn't write through";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"its {what} {path} can't be checked: {ex.Message}";
            }

            if (_settings.WriteCheck(path) is { } problem)
                return $"its {what} can't be used: {problem}";
        }

        try
        {
            if (Directory.Exists(logs))
            {
                foreach (var file in new DirectoryInfo(logs).EnumerateFileSystemInfos(_package.Folder + "-*"))
                {
                    if (file.LinkTarget != null)
                        return $"its log file {file.FullName} is a link, which the engine doesn't write through";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"its log folder {logs} can't be read: {ex.Message}";
        }

        return null;
    }

    private async Task RestartAsync(int generation, PluginHostConnection? old, CancellationToken ct)
    {
        await DropAsync(old).ConfigureAwait(false);
        try
        {
            await Task.Delay(_settings.Timeouts.RestartDelay, _time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await LaunchAsync(generation, ct).ConfigureAwait(false);
    }

    private async Task DropAsync(PluginHostConnection? connection)
    {
        if (connection == null)
            return;
        await PluginHostConnection.KillAsync(connection.Process, _settings.Timeouts.KillWait, _time, _logger, _package.Folder).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private PluginHostCommand Command(string pipeName) => new(
        pipeName,
        _package.DllPath,
        _settings.LogsDirectory,
        Path.Combine(_settings.SettingsDirectory, _package.Folder),
        _settings.LogLevel());

    /// <summary>The sensors first: whoever sees the state sees its sensors' status (live, stale, unavailable) with it.</summary>
    private void SetState(PluginHostState state)
    {
        _source.SetState(_package.Folder, state);
        _state = state;
    }

    /// <summary>Logs an error of the package's as a warning (Settings › Diagnostics shows it) and keeps it.</summary>
    private void Error(string message, string? pluginId = null)
    {
        message = message.Length > _settings.Limits.ErrorChars ? message[.._settings.Limits.ErrorChars] : message;
        if (pluginId == null)
            _logger.LogWarning("plugin {Folder}: {Message}", _package.Folder, message);
        else
            _logger.LogWarning("plugin {Folder}/{PluginId}: {Message}", _package.Folder, pluginId, message);

        if (pluginId != null && pluginId.Length > _settings.Limits.IdChars)
            pluginId = pluginId[.._settings.Limits.IdChars];
        lock (_errors)
        {
            _errors.Add(new PluginErrorEntry(_time.GetUtcNow(), pluginId, message));
            if (_errors.Count > _settings.Limits.ErrorsKept)
                _errors.RemoveAt(0);
        }
    }

    /// <summary>The plugins of <paramref name="metadata"/> a package's host loaded (those in <paramref name="taken"/>, when given: the source took them), without their settings' values.</summary>
    private IReadOnlyList<PluginLoaded> LoadedOf(IEnumerable<PluginMetadataDto> metadata, IReadOnlySet<string>? taken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<PluginLoaded>();
        foreach (var plugin in metadata)
        {
            if (plugin?.Id is not { Length: > 0 } id || (taken != null && !taken.Contains(id)) || _source.IdProblem(id) != null || !seen.Add(id))
                continue;
            var actions = (plugin.Actions ?? [])
                .Where(a => a?.MethodName is { Length: > 0 and <= 256 })
                .Select(a => new PluginActionInfo(a.MethodName, Cut(a.DisplayName ?? "", _settings.Limits.NameChars)))
                .ToList();
            list.Add(new PluginLoaded(id, Cut(plugin.Name ?? "", _settings.Limits.NameChars), Cut(plugin.Description ?? "", _settings.Limits.TextChars), actions,
                plugin.IsConfigurable));
        }
        return list;
    }

    private static string Cut(string value, int chars) => value.Length <= chars ? value : value[..chars];

    /// <summary>The host's connection while it runs, and the plugin <paramref name="pluginId"/> it lists.</summary>
    private (PluginCall Outcome, PluginHostConnection? Connection, PluginLoaded? Plugin) Running(string pluginId)
    {
        lock (_lock)
        {
            if (_state != PluginHostState.Running || _connection is not { } connection)
                return (PluginCall.NotRunning, null, null);
            return _plugins.FirstOrDefault(p => p.Id == pluginId) is { } plugin
                ? (PluginCall.Done, connection, plugin)
                : (PluginCall.NotFound, null, null);
        }
    }

    /// <summary>
    /// Asks <paramref name="pluginId"/> to run <paramref name="method"/> through the host as it runs now, when the plugin
    /// lists that action (no other name is ever sent), within <see cref="PluginHostTimeouts.Call"/>: done once the host
    /// has started it, the action itself not waited for; <see cref="PluginCall.Disabled"/> when its state says it can't
    /// run now, and the host didn't run it.
    /// </summary>
    public async Task<PluginCall> RunActionAsync(string pluginId, string method)
    {
        var (outcome, connection, _) = RunningAction(pluginId, method);
        if (outcome != PluginCall.Done)
            return outcome;
        var (asked, state) = await CallAsync<ActionStateDto>(connection!, nameof(IPluginHostService.InvokeActionAsync), [pluginId, method]).ConfigureAwait(false);
        if (asked != PluginCall.Done)
            return asked;
        // Enabled but not started: the host's own limit for a start passed, or it is stopping; the action never runs.
        return state == null ? PluginCall.NoAnswer : !state.Enabled ? PluginCall.Disabled : state.Running ? PluginCall.Done : PluginCall.NoAnswer;
    }

    /// <summary>
    /// The action <paramref name="method"/>'s state, asked of the host as it runs now within
    /// <see cref="PluginHostTimeouts.Call"/>, when the plugin lists that action: whether it can run, its line and its
    /// confirmation as the plugin describes them (a plugin that doesn't: enabled, neither), cut to their limits, and
    /// whether a run of it is under way.
    /// </summary>
    public async Task<(PluginCall Outcome, PluginActionStateBody? State)> DescribeActionAsync(string pluginId, string method)
    {
        var (outcome, connection, action) = RunningAction(pluginId, method);
        if (outcome != PluginCall.Done)
            return (outcome, null);
        var (asked, state) = await CallAsync<ActionStateDto>(connection!, nameof(IPluginHostService.DescribeActionAsync), [pluginId, method]).ConfigureAwait(false);
        if (asked != PluginCall.Done)
            return (asked, null);
        if (state == null)
            return (PluginCall.NoAnswer, null);
        var confirm = state.Confirm is { } c
            ? new PluginActionConfirmBody
            {
                Title = Cut(string.IsNullOrEmpty(c.Title) ? (action!.Name.Length > 0 ? action.Name : method) : c.Title, PluginActionConfirmation.TitleChars),
                Text = Cut(c.Text ?? "", PluginActionConfirmation.TextChars),
            }
            : null;
        return (PluginCall.Done, new PluginActionStateBody
        {
            Enabled = state.Enabled,
            State = string.IsNullOrEmpty(state.State) ? null : Cut(state.State, PluginActionState.StateChars),
            Confirm = confirm,
            Running = state.Running,
        });
    }

    /// <summary>The host's connection while it runs, and the action <paramref name="method"/> of the plugin <paramref name="pluginId"/> it lists.</summary>
    private (PluginCall Outcome, PluginHostConnection? Connection, PluginActionInfo? Action) RunningAction(string pluginId, string method)
    {
        var (outcome, connection, plugin) = Running(pluginId);
        if (outcome != PluginCall.Done)
            return (outcome, null, null);
        return plugin!.Actions.FirstOrDefault(a => a.Method == method) is { } action
            ? (PluginCall.Done, connection, action)
            : (PluginCall.NotFound, null, null);
    }

    /// <summary>The plugin's settings, asked of its host as it runs, within <see cref="PluginHostTimeouts.Call"/>.</summary>
    public async Task<(PluginCall Outcome, IReadOnlyList<PluginSettingBody>? Settings)> GetSettingsAsync(string pluginId)
    {
        var (outcome, connection, settings) = await ReadSettingsAsync(pluginId).ConfigureAwait(false);
        return (outcome, settings);
    }

    /// <summary>The plugin's settings, asked of its host as it runs now, with the connection they were asked through.</summary>
    private async Task<(PluginCall Outcome, PluginHostConnection? Connection, IReadOnlyList<PluginSettingBody>? Settings)> ReadSettingsAsync(string pluginId)
    {
        var (outcome, connection, plugin) = Running(pluginId);
        if (outcome != PluginCall.Done)
            return (outcome, null, null);
        if (!plugin!.Configurable)
            return (PluginCall.NotFound, null, null);
        var (asked, properties) = await CallAsync<List<PluginConfigPropertyDto>>(connection!, nameof(IPluginHostService.GetConfigPropertiesAsync), [pluginId]).ConfigureAwait(false);
        return asked == PluginCall.Done ? (PluginCall.Done, connection, PluginSettings.Bodies(properties)) : (asked, null, null);
    }

    /// <summary>
    /// Changes one of the plugin's settings: its settings asked first, the value checked against the one named
    /// (<see cref="PluginSettings.Check"/>), then applied by the plugin; answered with every setting as the plugin answers
    /// them after it. The read, the check and the apply go through one connection: a host replaced in between (a reload,
    /// a restart) is never sent a value checked against another's settings, and the change answers
    /// <see cref="PluginCall.NotRunning"/>. Neither the value nor anything holding it is logged.
    /// </summary>
    public async Task<(PluginCall Outcome, string? Problem, IReadOnlyList<PluginSettingBody>? Settings)> ApplySettingAsync(string pluginId, string key, JsonElement value)
    {
        var (asked, connection, settings) = await ReadSettingsAsync(pluginId).ConfigureAwait(false);
        if (asked != PluginCall.Done)
            return (asked, null, null);
        if (settings!.FirstOrDefault(s => s.Key == key) is not { } setting)
            return (PluginCall.NotFound, null, null);
        var (send, problem) = PluginSettings.Check(setting, value);
        if (problem != null)
            return (PluginCall.Invalid, problem, null);
        if (AfterSettingsRead is { } barrier)
            await barrier().ConfigureAwait(false);

        lock (_lock)
        {
            if (_state != PluginHostState.Running || _connection != connection)
                return (PluginCall.NotRunning, null, null);
        }
        var (applied, after) = await CallAsync<List<PluginConfigPropertyDto>>(connection!, nameof(IPluginHostService.ApplyConfigAsync), [pluginId, key, send]).ConfigureAwait(false);
        return applied == PluginCall.Done ? (PluginCall.Done, null, PluginSettings.Bodies(after)) : (applied, null, null);
    }

    /// <summary>
    /// The plugins <paramref name="generation"/>'s host loaded: the manager's one-id rule decides first and reserves the ids
    /// in the same step (<see cref="Claim"/>, <see cref="Reserve"/>), then the source takes them, under the runner's lock
    /// and only while <paramref name="generation"/> is current. A stale generation neither claims nor publishes.
    /// </summary>
    internal void TakeLoaded(int generation, List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containers)
    {
        // One plugin id, one package: the manager's rule decides before the source takes any of them.
        var ids = plugins.Select(p => p?.Id).OfType<string>().Where(id => _source.IdProblem(id) == null).Distinct(StringComparer.Ordinal).ToList();
        var conflict = Claim != null ? Claim(this, generation, ids) : Reserve(generation, ids) ? null : Superseded;
        if (conflict != null)
        {
            // Nothing when the generation is stale.
            FailDuplicate(generation, conflict);
            return;
        }

        lock (_lock)
        {
            if (generation != _generation || _stopping)
                return;
            foreach (var refusal in _source.Load(_package.Folder, plugins, containers))
                Error($"not loaded: {refusal.Reason}", refusal.PluginId);
            _plugins = LoadedOf(plugins, _source.PluginIdsOf(_package.Folder));
        }
    }

    /// <summary>What <see cref="Claim"/> answers a generation that is no longer current.</summary>
    internal const string Superseded = "its host was stopped or replaced";

    /// <summary>Takes <paramref name="ids"/> as the host's while <paramref name="generation"/> is current; false when it is stale.</summary>
    internal bool Reserve(int generation, IReadOnlyList<string> ids)
    {
        lock (_lock)
        {
            if (generation != _generation || _stopping)
                return false;
            _claimed = ids;
            return true;
        }
    }

    /// <summary>A call of the host's, raced against <see cref="PluginHostTimeouts.Call"/> on the engine's clock and the connection's end (a call's cancellation doesn't end it until the host answers).</summary>
    private async Task<(PluginCall, T?)> CallAsync<T>(PluginHostConnection connection, string method, object?[] arguments)
    {
        using var deadline = new CancellationTokenSource(_settings.Timeouts.Call, _time);
        Task<T> call;
        try
        {
            call = connection.Rpc.InvokeWithCancellationAsync<T>(method, arguments, deadline.Token);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or IOException)
        {
            return (PluginCall.NotRunning, default);
        }
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        var expired = Task.Delay(Timeout.Infinite, deadline.Token);
        var first = await Task.WhenAny(call, expired, connection.Ended).ConfigureAwait(false);
        if (first == connection.Ended)
            return (PluginCall.NotRunning, default);
        if (first != call)
            return (PluginCall.NoAnswer, default);
        try
        {
            return (PluginCall.Done, await call.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionLostException or IOException or RemoteInvocationException or ObjectDisposedException)
        {
            return (PluginCall.NoAnswer, default);
        }
    }

    /// <summary>
    /// The engine's side of one host connection's calls: only the current generation's are taken. Each call, taken or
    /// not, releases its place in the connection's <see cref="PendingBudget"/> once handled.
    /// </summary>
    private sealed class Callback(PluginHostRunner runner, int generation, PendingBudget budget) : IPluginClientCallback
    {
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool Current
        {
            get
            {
                lock (runner._lock)
                    return runner._generation == generation && !runner._stopping;
            }
        }

        public void OnPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containersByPluginId)
        {
            try
            {
                if (!Current)
                    return;
                runner.TakeLoaded(generation, plugins ?? [], containersByPluginId ?? []);
                Loaded.TrySetResult();
            }
            finally
            {
                budget.Handled();
            }
        }

        /// <summary>
        /// Whether this connection is current, and the source's generation of the package read in the same step, under the
        /// runner's lock: a replacement's catalogue (which starts another) is loaded only under that lock once its own
        /// generation is current, so a generation read here is never the replacement's.
        /// </summary>
        private bool TryCurrent(out long epoch)
        {
            lock (runner._lock)
            {
                epoch = runner._source.EpochOf(runner._package.Folder);
                return runner._generation == generation && !runner._stopping;
            }
        }

        /// <summary>
        /// The host's changes, taken by the source only in the generation read when this connection was found current
        /// (<see cref="TryCurrent"/>): a call paused after that, while the host was stopped or replaced, takes nothing.
        /// </summary>
        public void OnSensorUpdate(List<SensorUpdateBatchDto> updates)
        {
            try
            {
                if (updates != null && TryCurrent(out var epoch))
                {
                    void HandOff() => runner._source.Update(runner._package.Folder, epoch, updates);
                    if (runner.SourceUpdate is { } hook)
                        hook(generation, HandOff);
                    else
                        HandOff();
                }
            }
            finally
            {
                budget.Handled();
            }
        }

        public void OnPluginError(string pluginId, string errorMessage)
        {
            try
            {
                if (Current)
                    runner.Error(errorMessage ?? "", pluginId ?? "");
            }
            finally
            {
                budget.Handled();
            }
        }

        /// <summary>The host's own report of its processor use (every plugin's is the process's): the highest finite one, shown as the engine's own CPU (<see cref="CpuShown"/>).</summary>
        public void OnPerformanceUpdate(List<PluginPerformanceDto> performances)
        {
            try
            {
                var cpu = (performances ?? []).Where(p => p != null && double.IsFinite(p.CpuPercent)).Select(p => p.CpuPercent).DefaultIfEmpty(double.NaN).Max();
                if (double.IsFinite(cpu))
                {
                    lock (runner._lock)
                    {
                        if (runner._generation == generation && !runner._stopping)
                            runner._cpu = CpuShown(cpu, EngineCpu.ProcessorCount, EngineCpu.CpuPerCpu);
                    }
                }
            }
            finally
            {
                budget.Handled();
            }
        }

        /// <summary>The host sent a call the engine doesn't take (dropped as it was read, never counted); said once a connection.</summary>
        public void Ignored(string method)
        {
            if (Current)
                runner.Error($"its host sent a call the engine doesn't take, {method} (another method, or arguments it can't read); it and any more like it are ignored");
        }

        public void OnImageResize(string pluginId, ImageDescriptorDto descriptor) => budget.Handled();
    }
}
