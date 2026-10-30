using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins;

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
    private readonly List<string> _errors = [];

    private PluginHostConnection? _connection;
    // Bumped by every launch, restart and stop: work for an older generation finds it stale and drops what it holds.
    private int _generation;
    private bool _stopping = true;
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

    public PluginHostState State { get; private set; } = PluginHostState.Stopped;

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

    /// <summary>The package's newest errors (from its plugins, its loads, its host's failures), oldest first.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
                return [.. _errors];
        }
    }

    /// <summary>Starts the host, the restart policy afresh. Nothing when it is already running or starting.</summary>
    public Task StartAsync()
    {
        lock (_lock)
        {
            if (!_stopping)
                return _pending;
            _stopping = false;
            _lifetime = new CancellationTokenSource();
            _windowStart = null;
            _restarts = 0;
            Reason = null;
            SetState(PluginHostState.Starting);
            var generation = ++_generation;
            var token = _lifetime.Token;
            return _pending = Task.Run(() => LaunchAsync(generation, token));
        }
    }

    /// <summary>
    /// Stops the host on purpose (never restarted): it is asked to shut down within <paramref name="within"/>, then
    /// killed and waited for (<see cref="PluginHostTimeouts.KillWait"/>); a start or restart in flight is cancelled.
    /// </summary>
    public async Task StopAsync(TimeSpan within)
    {
        PluginHostConnection? connection;
        Task pending;
        CancellationTokenSource lifetime;
        lock (_lock)
        {
            _stopping = true;
            _generation++;
            connection = _connection;
            _connection = null;
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

    private async Task WatchAsync(int generation, PluginHostConnection connection)
    {
        var reason = await connection.Ended.ConfigureAwait(false);
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

            if (_restarts >= _settings.Timeouts.MaxRestarts)
            {
                _generation++;
                Reason = reason;
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
            SetState(PluginHostState.Failed);
            Error($"not started: {reason}");
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

    private void SetState(PluginHostState state)
    {
        State = state;
        _source.SetState(_package.Folder, state);
    }

    /// <summary>Logs an error of the package's as a warning (Settings › Diagnostics shows it) and keeps it.</summary>
    private void Error(string message, string? pluginId = null)
    {
        message = message.Length > _settings.Limits.ErrorChars ? message[.._settings.Limits.ErrorChars] : message;
        if (pluginId == null)
            _logger.LogWarning("plugin {Folder}: {Message}", _package.Folder, message);
        else
            _logger.LogWarning("plugin {Folder}/{PluginId}: {Message}", _package.Folder, pluginId, message);

        lock (_errors)
        {
            _errors.Add(pluginId == null ? message : $"{pluginId}: {message}");
            if (_errors.Count > _settings.Limits.ErrorsKept)
                _errors.RemoveAt(0);
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
                foreach (var refusal in runner._source.Load(runner._package.Folder, plugins ?? [], containersByPluginId ?? []))
                    runner.Error($"not loaded: {refusal.Reason}", refusal.PluginId);
                Loaded.TrySetResult();
            }
            finally
            {
                budget.Handled();
            }
        }

        public void OnSensorUpdate(List<SensorUpdateBatchDto> updates)
        {
            try
            {
                if (Current && updates != null)
                    runner._source.Update(runner._package.Folder, updates);
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

        public void OnPerformanceUpdate(List<PluginPerformanceDto> performances) => budget.Handled();

        /// <summary>The host sent a call the engine doesn't take (dropped as it was read, never counted); said once a connection.</summary>
        public void Ignored(string method)
        {
            if (Current)
                runner.Error($"its host sent a call the engine doesn't take, {method} (another method, or arguments it can't read); it and any more like it are ignored");
        }

        public void OnImageResize(string pluginId, ImageDescriptorDto descriptor) => budget.Handled();
    }
}
