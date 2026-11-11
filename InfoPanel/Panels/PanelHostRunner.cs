using System.Diagnostics;
using System.IO.Pipes;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Sdk;
using InfoPanel.Plugins;

namespace InfoPanel.Panels;

/// <summary>What a panel host process is started with (the engine executable's panel host mode, <see cref="PanelHostEntry"/>).</summary>
/// <remarks><paramref name="EngineStart"/> is the engine's process start time (<see cref="PanelHostEntry.StartTimeOf"/>), so the host never takes a process that reused the engine's id for it.</remarks>
public sealed record PanelHostCommand(string PipeName, string PackageDirectory, string LogDirectory, string LogLevel, int EnginePid, long EngineStart)
{
    /// <summary>The arguments after the executable: <c>--panel-host --pipe … --package … --log-dir … --log-level … --engine-pid … --engine-start …</c>.</summary>
    public IReadOnlyList<string> Arguments =>
        [PanelHostEntry.Flag, "--pipe", PipeName, "--package", PackageDirectory, "--log-dir", LogDirectory, "--log-level", LogLevel,
            "--engine-pid", EnginePid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--engine-start", EngineStart.ToString(System.Globalization.CultureInfo.InvariantCulture)];
}

/// <summary>Starts panel host processes (tests give the runner one of their own).</summary>
public interface IPanelHostLauncher
{
    /// <summary>Starts a host for <paramref name="command"/>, its process created only through <paramref name="launch"/>'s <see cref="PanelHostLaunch.Create{T}"/>.</summary>
    IPanelHostProcess Start(PanelHostCommand command, PanelHostLaunch launch);
}

/// <summary>
/// A host start's handoff with the runner's stop: the launcher creates the process only through <see cref="Create{T}"/>,
/// which refuses once the start is cancelled; <see cref="Cancel"/> returns once no creation is under way, so after it
/// this start can create no process.
/// </summary>
public sealed class PanelHostLaunch
{
    private readonly Lock _lock = new();
    private bool _cancelled;

    /// <summary>Runs <paramref name="create"/> (the process's creation) unless the start was cancelled first.</summary>
    /// <exception cref="OperationCanceledException">The start was cancelled: nothing is created.</exception>
    public T Create<T>(Func<T> create)
    {
        lock (_lock)
        {
            if (_cancelled)
                throw new OperationCanceledException("the panel host's start was cancelled: the engine is stopping");
            return create();
        }
    }

    /// <summary>Cancels the start: waits for a creation under way, and refuses any after.</summary>
    public void Cancel()
    {
        lock (_lock)
            _cancelled = true;
    }
}

/// <summary>A running panel host process.</summary>
public interface IPanelHostProcess : IDisposable
{
    int Id { get; }

    /// <summary>The exit code once it has exited.</summary>
    int? ExitCode { get; }

    /// <summary>Completes when the process has exited.</summary>
    Task Exited { get; }

    /// <summary>The end of what it wrote to stderr, at most 4 KiB.</summary>
    string ErrorTail { get; }

    /// <summary>Kills the process; nothing when it has exited.</summary>
    void Kill();
}

/// <summary>A panel host's bounds and restart policy (tests shorten the delay): started again after <see cref="Restart"/>, at most <see cref="MaxRestarts"/> times in <see cref="RestartWindow"/>.</summary>
public sealed record PanelHostTimeouts
{
    public TimeSpan Restart { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxRestarts { get; init; } = 3;

    /// <summary>How long a host is given to connect to its pipe and answer its hello.</summary>
    public TimeSpan Start { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a host stopped because no device of it is plugged in is given to end before it is killed.</summary>
    public TimeSpan Stop { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Starts panel hosts as the engine's own executable in its panel host mode, as the plugin hosts are
/// (<see cref="PluginProcessLauncher.EngineCommand"/>): run as the user who runs the engine, never elevated; stdout
/// dropped (the host logs to its file), stderr's last 4 KiB kept for the reason a host failed.
/// </summary>
public sealed class PanelProcessLauncher : IPanelHostLauncher
{
    public IPanelHostProcess Start(PanelHostCommand command, PanelHostLaunch launch)
    {
        var (file, prefix) = PluginProcessLauncher.EngineCommand();
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in prefix.Concat(command.Arguments))
            start.ArgumentList.Add(arg);
        var process = launch.Create(() => Process.Start(start)) ?? throw new InvalidOperationException($"The panel host {file} didn't start.");
        return new HostProcess(process);
    }

    private sealed class HostProcess : IPanelHostProcess
    {
        private const int TailBytes = 4096;
        private readonly Process _process;
        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;

        public HostProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            _ = Drain(process.StandardOutput.BaseStream, keep: false);
            var stderr = Drain(process.StandardError.BaseStream, keep: true);
            Exited = ExitAsync(stderr);
        }

        public int Id { get; }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public Task Exited { get; }

        public string ErrorTail
        {
            get
            {
                lock (_tail)
                    return System.Text.Encoding.UTF8.GetString(_tail, 0, _tailLength);
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // it has exited
            }
        }

        public void Dispose() => _process.Dispose();

        private async Task ExitAsync(Task stderr)
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAny(stderr, Task.Delay(500)).ConfigureAwait(false);
        }

        private Task Drain(Stream stream, bool keep) => Task.Run(async () =>
        {
            var buffer = new byte[TailBytes];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    if (keep)
                        lock (_tail)
                        {
                            var drop = Math.Max(0, _tailLength + read - TailBytes);
                            if (read >= TailBytes)
                            {
                                buffer.AsSpan(read - TailBytes, TailBytes).CopyTo(_tail);
                                _tailLength = TailBytes;
                                continue;
                            }
                            if (drop > 0)
                            {
                                _tail.AsSpan(drop, _tailLength - drop).CopyTo(_tail);
                                _tailLength -= drop;
                            }
                            buffer.AsSpan(0, read).CopyTo(_tail.AsSpan(_tailLength));
                            _tailLength += read;
                        }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });
    }
}

/// <summary>
/// One panel package's host (<see cref="IPanelHostLink"/>): a process of its own, the engine executable in its panel host
/// mode, connected over a current-user pipe, started when a probe or a start first needs it (a device its families name
/// is plugged in) and stopped once none is and no session is open. A host that ends unasked is started again after
/// <see cref="PanelHostTimeouts.Restart"/> while it is wanted, at most <see cref="PanelHostTimeouts.MaxRestarts"/> times
/// in <see cref="PanelHostTimeouts.RestartWindow"/>; then the package fails with why, and its panels are failed with the
/// <c>driver</c> reason until the engine restarts. A package turned off never starts a host. The engine's stop asks the
/// host to close its sessions and end, and kills it past the bound.
/// </summary>
public sealed class PanelHostRunner : IPanelHostLink, IAsyncDisposable
{
    private enum HostState { Idle, Starting, Running, Restarting, Off, Failed, Closed }

    /// <summary>This engine's process start time, as its hosts check it (<see cref="PanelHostEntry.StartTimeOf"/>).</summary>
    private static readonly Lazy<long> EngineStart = new(() =>
        PanelHostEntry.StartTimeOf(Environment.ProcessId) ?? throw new InvalidOperationException("this process's start time can't be read"));

    private readonly PanelPackage _package;
    private readonly IPanelHostLauncher _launcher;
    private readonly string _logDirectory;
    private readonly Func<string> _logLevel;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger _logger;
    private readonly PanelHostTimeouts _timeouts;
    private readonly Lock _lock = new();
    private readonly Queue<long> _restarts = new(); // when each restart was decided, in the window
    private readonly List<(Task Task, PanelHostLaunch Handoff)> _launches = []; // the hosts' starts under way, for the stop to wait for and cancel
    private HostState _state;
    private int _generation; // a host's start; a later one makes an earlier one's ends stale
    private TaskCompletionSource<PanelHostConnection>? _ready; // what Connect waits for: the host starting or started
    private PanelHostConnection? _connection;
    private IPanelHostProcess? _process;
    private bool _present;
    private int _sessions;
    private string? _failure;

    public PanelHostRunner(PanelPackage package, IPanelHostLauncher launcher, string slotDirectory, string logDirectory, Func<string> logLevel, bool on,
        TimeProvider time, Serilog.ILogger logger, PanelHostTimeouts? timeouts = null)
    {
        _package = package;
        _launcher = launcher;
        SlotDirectory = slotDirectory;
        _logDirectory = logDirectory;
        _logLevel = logLevel;
        _time = time;
        _logger = logger;
        _timeouts = timeouts ?? new PanelHostTimeouts();
        _state = on ? HostState.Idle : HostState.Off;
        if (package.Problem is { } problem)
            (_state, _failure) = (HostState.Failed, problem);
    }

    public string Folder => _package.Folder;

    public string SlotDirectory { get; }

    public PanelPackage Package => _package;

    public IReadOnlyList<PanelPackageFamily> Families => _package.Families;

    /// <summary>
    /// <c>idle</c> (on, no host needed now), <c>starting</c>, <c>running</c>, <c>restarting</c>, <c>off</c> (turned off),
    /// <c>failed</c> (<see cref="Problem"/> says why) or <c>closed</c> (the engine is stopping).
    /// </summary>
    public string State
    {
        get
        {
            lock (_lock)
                return _state.ToString().ToLowerInvariant();
        }
    }

    /// <summary>Its host process's id while one runs.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_lock)
                return _process?.Id is { } id && _state is HostState.Running or HostState.Starting ? id : null;
        }
    }

    public PanelDriverProblem? Problem
    {
        get
        {
            lock (_lock)
                return _state switch
                {
                    HostState.Off => new PanelDriverProblem(true, $"its driver, the panel package {Folder}, is turned off"),
                    HostState.Failed => new PanelDriverProblem(false, $"its driver failed: {_failure}"),
                    _ => null,
                };
        }
    }

    public PanelHostConnection Connect(CancellationToken stop)
    {
        for (var attempt = 0; ; attempt++)
        {
            Task<PanelHostConnection> ready;
            lock (_lock)
            {
                if (Problem is { } problem)
                    throw new PanelDriverException(problem.Message);
                if (_state == HostState.Closed)
                    throw new PanelDriverException("its driver's host is stopping with the engine");
                if (_ready == null)
                {
                    _ready = new TaskCompletionSource<PanelHostConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                    LaunchLocked();
                }
                ready = _ready.Task;
            }
            try
            {
                ready.Wait(stop);
            }
            catch (AggregateException ex) when (ex.InnerException is PanelDriverException driver)
            {
                throw new PanelDriverException(driver.Message);
            }
            var connection = ready.Result;
            if (!connection.IsClosed || attempt >= 2)
                return connection;
        }
    }

    public void SessionOpened()
    {
        lock (_lock)
            _sessions++;
    }

    public void SessionClosed()
    {
        lock (_lock)
        {
            _sessions = Math.Max(0, _sessions - 1);
            StopIfUnwantedLocked();
        }
    }

    public void SetPresent(bool present)
    {
        lock (_lock)
        {
            _present = present;
            StopIfUnwantedLocked();
        }
    }

    /// <summary>A host no device and no session needs any more is asked to end (killed past <see cref="PanelHostTimeouts.Stop"/>).</summary>
    private void StopIfUnwantedLocked()
    {
        if (_present || _sessions > 0 || _state is not (HostState.Starting or HostState.Running or HostState.Restarting))
            return;
        _logger.Information("The panel package {Folder:l}'s host is stopped: no device of it is plugged in", Folder);
        var (connection, process) = DetachLocked(HostState.Idle, "its host stopped: no device of it is plugged in");
        _ = Task.Run(() => EndAsync(connection, process, _timeouts.Stop));
    }

    /// <summary>The host's connection and process taken from the runner, which is <paramref name="state"/> now; a start waited for fails with <paramref name="why"/>.</summary>
    private (PanelHostConnection?, IPanelHostProcess?) DetachLocked(HostState state, string why)
    {
        _generation++;
        _state = state;
        var taken = (_connection, _process);
        (_connection, _process) = (null, null);
        if (_ready is { Task.IsCompleted: false } waiting)
            waiting.TrySetException(new PanelDriverException(why));
        _ready = null;
        return taken;
    }

    private void LaunchLocked()
    {
        var generation = ++_generation;
        _state = HostState.Starting;
        var ready = _ready!;
        var handoff = new PanelHostLaunch();
        _launches.RemoveAll(l => l.Task.IsCompleted);
        _launches.Add((Task.Run(() => LaunchAsync(generation, ready, handoff)), handoff));
    }

    /// <summary>Whether <paramref name="generation"/> is still the runner's: a stop, or a later start, makes it stale.</summary>
    private bool Current(int generation)
    {
        lock (_lock)
            return generation == _generation;
    }

    private async Task LaunchAsync(int generation, TaskCompletionSource<PanelHostConnection> ready, PanelHostLaunch handoff)
    {
        if (!Current(generation))
            return; // stopped (or started again) before this start ran: nothing is started
        // Short, as the plugin hosts' are: on macOS and Linux the pipe is a socket in the temp folder, whose path is bounded.
        var pipeName = "ippn-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        NamedPipeServerStream? pipe = null;
        IPanelHostProcess? process = null;
        PanelHostConnection? connection = null;
        try
        {
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Directory.CreateDirectory(_logDirectory);
            process = _launcher.Start(new PanelHostCommand(pipeName, _package.Directory, _logDirectory, _logLevel(), Environment.ProcessId, EngineStart.Value), handoff);
            bool stale;
            lock (_lock)
            {
                stale = generation != _generation;
                if (!stale)
                    _process = process;
            }
            if (stale)
            {
                // Stopped while its process was being started: the process is ended before the stop (which waits for this) returns.
                process.Kill();
                await Task.WhenAny(process.Exited, Task.Delay(_timeouts.Stop)).ConfigureAwait(false);
                process.Dispose();
                pipe.Dispose();
                return;
            }

            using var deadline = new CancellationTokenSource(_timeouts.Start);
            var connecting = pipe.WaitForConnectionAsync(deadline.Token);
            if (await Task.WhenAny(connecting, process.Exited).ConfigureAwait(false) != connecting || !connecting.IsCompletedSuccessfully)
                throw new IOException(process.Exited.IsCompleted ? $"it exited ({process.ExitCode}) before it connected{Tail(process)}" : "it didn't connect in time");
            connection = new PanelHostConnection(pipe, _logger);
            var hello = connection.Call(new PanelHostRequest { Op = "hello" }, _timeouts.Start)
                ?? throw new IOException("it didn't answer its hello in time");
            // Its drivers must be the families its panel.json declares, each once: else they would read as ready while no
            // probe of them works. Starting it again changes nothing, so the package fails.
            var drivers = hello.Families ?? [];
            var refused = hello.Status != PanelDeviceStatus.Ok
                ? $"its host didn't start its drivers ({hello.Status}): {PanelHostWire.Cut(hello.Message)}"
                : drivers.Any(d => d is null)
                    ? "its host's hello is malformed: a family without an id"
                    : drivers.Count != _package.Families.Count || drivers.Distinct(StringComparer.Ordinal).Count() != drivers.Count
                        || _package.Families.Any(f => !drivers.Contains(f.Id, StringComparer.Ordinal))
                        ? $"its host's drivers ({string.Join(", ", drivers.Take(PanelPackages.MaxFamilies).Select(d => d is { Length: > 64 } ? d[..64] : d ?? ""))}) aren't the families its panel.json declares ({string.Join(", ", _package.Families.Select(f => f.Id))})"
                        : null;
            if (refused != null)
            {
                connection.Dispose();
                process.Kill();
                if (Refused(generation, refused))
                    process.Dispose();
                return;
            }
            lock (_lock)
            {
                if (generation != _generation)
                {
                    connection.Dispose();
                    process.Kill();
                    return;
                }
                _connection = connection;
                _state = HostState.Running;
            }
            _logger.Information("The panel package {Folder:l}'s host started (process {Pid}): {Families:l}", Folder, process.Id, string.Join(", ", hello.Families ?? []));
            ready.TrySetResult(connection);
            var ended = await Task.WhenAny(connection.Closed, process.Exited).ConfigureAwait(false);
            Ended(generation, ended == process.Exited ? $"exit {process.ExitCode?.ToString() ?? "?"}{Tail(process)}" : "its pipe ended");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PanelDriverException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            connection?.Dispose();
            pipe?.Dispose();
            process?.Kill();
            Ended(generation, $"it didn't start: {ex.Message}");
        }
    }

    private static string Tail(IPanelHostProcess process) =>
        process.ErrorTail is { Length: > 0 } tail ? $": {tail.Trim()[^Math.Min(tail.Trim().Length, 512)..]}" : "";

    /// <summary>A host of <paramref name="generation"/> that can't serve its package (<paramref name="why"/>): the package fails at once, for the run.</summary>
    private bool Refused(int generation, string why)
    {
        lock (_lock)
        {
            if (generation != _generation)
                return false; // asked to stop (which ends its process), or superseded
            _failure = why;
            _logger.Warning("The panel package {Folder:l} failed: {Why:l}", Folder, why);
            DetachLocked(HostState.Failed, $"its driver failed: {why}"); // its process is ended by the caller
            return true;
        }
    }

    /// <summary>A host of <paramref name="generation"/> ended (or didn't start): started again while wanted, within the policy; else the package fails.</summary>
    private void Ended(int generation, string why)
    {
        PanelHostConnection? connection;
        IPanelHostProcess? process;
        lock (_lock)
        {
            if (generation != _generation)
                return; // asked to stop, or superseded
            var wanted = _present || _sessions > 0;
            var now = _time.GetTimestamp();
            while (_restarts.Count > 0 && _time.GetElapsedTime(_restarts.Peek(), now) > _timeouts.RestartWindow)
                _restarts.Dequeue();
            if (!wanted)
            {
                _logger.Information("The panel package {Folder:l}'s host ended ({Why:l}); no device of it is plugged in", Folder, why);
                (connection, process) = DetachLocked(HostState.Idle, $"its driver's host ended: {why}");
            }
            else if (_restarts.Count >= _timeouts.MaxRestarts)
            {
                _failure = $"its host kept ending ({_timeouts.MaxRestarts} restarts within {_timeouts.RestartWindow.TotalSeconds:0} s); last: {why}";
                _logger.Warning("The panel package {Folder:l} failed: {Why:l}", Folder, _failure);
                (connection, process) = DetachLocked(HostState.Failed, $"its driver failed: {_failure}");
            }
            else
            {
                _restarts.Enqueue(now);
                _logger.Warning("The panel package {Folder:l}'s host ended ({Why:l}): started again in {Delay:0.#} s", Folder, why, _timeouts.Restart.TotalSeconds);
                // Its waiters keep waiting, now for the new host.
                var ready = _ready is { Task.IsCompleted: false } pending ? pending : new TaskCompletionSource<PanelHostConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                (connection, process) = (_connection, _process);
                (_connection, _process) = (null, null);
                _ready = ready;
                _state = HostState.Restarting;
                var restarting = ++_generation;
                _ = Task.Delay(_timeouts.Restart, _time).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (restarting != _generation || _state != HostState.Restarting)
                            return;
                        LaunchLocked();
                    }
                }, TaskScheduler.Default);
            }
        }
        connection?.Dispose();
        process?.Kill();
        process?.Dispose();
    }

    /// <summary>
    /// The engine's stop: no host starts after it; the running one is asked to close its sessions (screen off) and end
    /// within <paramref name="bound"/>, and is killed past it.
    /// </summary>
    public async Task StopAsync(TimeSpan bound)
    {
        var started = Stopwatch.GetTimestamp();
        PanelHostConnection? connection;
        IPanelHostProcess? process;
        (Task Task, PanelHostLaunch Handoff)[] launches;
        lock (_lock)
        {
            (connection, process) = DetachLocked(HostState.Closed, "its driver's host is stopping with the engine");
            launches = [.. _launches];
        }
        await EndAsync(connection, process, bound).ConfigureAwait(false);
        // A start under way when the stop came ends what it started (its generation is stale now): waited for, within
        // what is left of the bound and the second a kill is given.
        var left = bound - Stopwatch.GetElapsedTime(started);
        await Task.WhenAny(Task.WhenAll(launches.Select(l => l.Task)), Task.Delay((left > TimeSpan.Zero ? left : TimeSpan.Zero) + TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        // A start still held then (before its process is created) is cancelled: once a creation under way is done, none
        // can come after the stop returns.
        await Task.Run(() =>
        {
            foreach (var launch in launches)
                launch.Handoff.Cancel();
        }).ConfigureAwait(false);
    }

    /// <summary>Asks a host to close its sessions and end within <paramref name="bound"/>, then kills it.</summary>
    private async Task EndAsync(PanelHostConnection? connection, IPanelHostProcess? process, TimeSpan bound)
    {
        if (process == null)
        {
            connection?.Dispose();
            return;
        }
        var started = Stopwatch.GetTimestamp();
        // Sent in the background within the bound (a host that stopped reading can't hold the stop), then waited for; with
        // no time left, none is sent (the host is killed).
        using var sending = new CancellationTokenSource(bound > TimeSpan.Zero ? bound : TimeSpan.Zero);
        if (bound > TimeSpan.Zero)
            connection?.Post(new PanelHostRequest { Op = "shutdown", Ms = (int)Math.Max(0, bound.TotalMilliseconds - 500) }, sending.Token);
        var left = bound - Stopwatch.GetElapsedTime(started);
        var exited = left > TimeSpan.Zero && await Task.WhenAny(process.Exited, Task.Delay(left)).ConfigureAwait(false) == process.Exited;
        if (!exited && !process.Exited.IsCompleted)
        {
            _logger.Warning("The panel package {Folder:l}'s host didn't stop within {Bound:0.#} s: killed", Folder, bound.TotalSeconds);
            process.Kill();
            await Task.WhenAny(process.Exited, Task.Delay(1000)).ConfigureAwait(false);
        }
        connection?.Dispose();
        process.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync(_timeouts.Stop).ConfigureAwait(false);
}
