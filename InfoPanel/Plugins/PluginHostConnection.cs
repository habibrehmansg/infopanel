using System.IO.Pipes;
using System.Security.Cryptography;
using InfoPanel.Plugins.Ipc;
using NonConcurrentSynchronizationContext = Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext;
using StreamJsonRpc;

namespace InfoPanel.Plugins;

/// <summary>The waits and the restart policy of plugin hosts (1.4.x's).</summary>
public sealed record PluginHostTimeouts
{
    public static PluginHostTimeouts Default { get; } = new();

    /// <summary>How long a started host has to connect to its pipe.</summary>
    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long its <c>InitializeAsync</c> may take.</summary>
    public TimeSpan Initialize { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the engine waits for <c>OnPluginsLoaded</c> after that, then goes on without its entries.</summary>
    public TimeSpan Loaded { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The pause before a host that ended unexpectedly is started again.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The window, from the first failure, in which at most <see cref="MaxRestarts"/> restarts are made.</summary>
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxRestarts { get; init; } = 3;

    /// <summary>How long one package's host gets to shut down before it is killed.</summary>
    public TimeSpan Stop { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a killed host is waited for.</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long all hosts together get to shut down when the engine stops, before the rest are killed.</summary>
    public TimeSpan EngineStop { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// One connected plugin host: its process, the engine's end of its pipe (the server, current user only) and the
/// JSON-RPC connection over it, reading through <see cref="MessageLimitStream"/>. <see cref="Ended"/> completes, with
/// the reason, when the process exits or the connection ends.
/// </summary>
internal sealed class PluginHostConnection : IAsyncDisposable
{
    private readonly IPluginProcess _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly TimeProvider _time;

    private readonly Task<string> _disconnected;

    private PluginHostConnection(IPluginProcess process, NamedPipeServerStream pipe, JsonRpc rpc, Task<string> disconnected, TimeProvider time,
        IReadOnlyList<PluginMetadataDto> plugins)
    {
        _process = process;
        _pipe = pipe;
        _rpc = rpc;
        _disconnected = disconnected;
        _time = time;
        Plugins = plugins;
        Ended = WatchAsync();
    }

    public IPluginProcess Process => _process;

    public JsonRpc Rpc => _rpc;

    /// <summary>What <c>InitializeAsync</c> answered.</summary>
    public IReadOnlyList<PluginMetadataDto> Plugins { get; }

    /// <summary>Completes with why, when the host exits or its connection ends.</summary>
    public Task<string> Ended { get; }

    /// <summary>
    /// A pipe name short enough that the Unix socket path it becomes (<c>{temp}/CoreFxPipe_{name}</c>) stays under
    /// macOS's 104 bytes: <c>ipph-</c> and 16 hex digits.
    /// </summary>
    public static string NewPipeName() => "ipph-" + RandomNumberGenerator.GetHexString(16, lowercase: true);

    /// <summary>
    /// Starts a host for <paramref name="package"/> and connects it (what it sends counted against
    /// <paramref name="budget"/>, which <paramref name="callback"/> releases as it handles each call; the first call it
    /// sends that the engine doesn't take goes to <paramref name="ignored"/>): the pipe, the process, then within
    /// <see cref="PluginHostTimeouts.Connect"/> its connection, within <see cref="PluginHostTimeouts.Initialize"/> its
    /// <c>InitializeAsync</c>, and within <see cref="PluginHostTimeouts.Loaded"/> its <c>OnPluginsLoaded</c> (without it,
    /// the engine goes on with <c>InitializeAsync</c>'s answer and no entries). A wait that runs out, a process that exits
    /// or a connection that ends kills the host and throws <see cref="PluginHostException"/> with the reason.
    /// </summary>
    public static async Task<PluginHostConnection> OpenAsync(
        PluginPackage package,
        Func<string, PluginHostCommand> command,
        IPluginProcessLauncher launcher,
        IPluginClientCallback callback,
        PendingBudget budget,
        Action<string> ignored,
        Task loaded,
        PluginHostTimeouts timeouts,
        PluginLimits limits,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        var pipeName = NewPipeName();
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        IPluginProcess? process = null;
        JsonRpc? rpc = null;
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            process = launcher.Start(command(pipeName));

            using (var connect = new Deadline(timeouts.Connect, time, ct))
            {
                var connected = pipe.WaitForConnectionAsync(connect.Token);
                await Race(connected, process, "connecting", connect, ct, timeouts.Connect).ConfigureAwait(false);
            }

            rpc = new JsonRpc(new BudgetedMessageHandler(pipe, new MessageLimitStream(pipe, limits.MessageBytes, budget), new JsonMessageFormatter(), budget, ignored))
            {
                // The host's calls in the order it sent them: its loaded list before its updates.
                SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false),
            };
            rpc.AddLocalRpcTarget<IPluginClientCallback>(callback, null);
            rpc.Disconnected += (_, e) => disconnected.TrySetResult(
                e.Exception?.GetBaseException() is { } error ? $"{e.Description} ({error.Message})" : e.Description);
            rpc.StartListening();

            List<PluginMetadataDto> plugins;
            using (var init = new Deadline(timeouts.Initialize, time, ct))
            {
                var initialized = rpc.InvokeWithCancellationAsync<List<PluginMetadataDto>>(nameof(IPluginHostService.InitializeAsync), null, init.Token);
                await Race(initialized, process, "initialising", init, ct, timeouts.Initialize, disconnected.Task).ConfigureAwait(false);
                plugins = await initialized.ConfigureAwait(false) ?? [];
            }

            try
            {
                await loaded.WaitAsync(timeouts.Loaded, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("plugin {Folder}: its host didn't list its plugins' entries within {Wait}; going on without them", package.Folder, timeouts.Loaded);
            }

            return new PluginHostConnection(process, pipe, rpc, disconnected.Task, time, plugins);
        }
        catch
        {
            rpc?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (process != null)
            {
                await KillAsync(process, timeouts.KillWait, time, logger, package.Folder).ConfigureAwait(false);
                process.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Asks the host to shut down within <paramref name="within"/> and waits for it to exit in that time; then kills it
    /// (and its children) and waits <paramref name="killWait"/> for that. Never throws.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan within, TimeSpan killWait, ILogger logger, string folder)
    {
        if (within > TimeSpan.Zero && !_process.HasExited)
        {
            // The host exits once its plugins have stopped: the wait is for that exit, bounded on the engine's clock. The
            // call's answer isn't waited for (its cancellation wouldn't end it until the host answered, and a plugin that
            // hangs never lets it).
            using var deadline = new CancellationTokenSource();
            try
            {
                Observe(_rpc.InvokeAsync(nameof(IPluginHostService.ShutdownAsync)));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or IOException)
            {
                // Its connection had ended: it is killed below unless it exits in time.
            }
            await Task.WhenAny(_process.Exited, Task.Delay(within, _time, deadline.Token)).ConfigureAwait(false);
            await deadline.CancelAsync().ConfigureAwait(false);
        }

        if (!_process.HasExited)
            await KillAsync(_process, killWait, _time, logger, folder).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    /// <summary>Kills the process and its children and waits up to <paramref name="wait"/> for it.</summary>
    public static async Task KillAsync(IPluginProcess process, TimeSpan wait, TimeProvider time, ILogger logger, string folder)
    {
        if (process.HasExited)
            return;
        process.Kill();
        try
        {
            await process.Exited.WaitAsync(wait, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("plugin {Folder}: its host (process {Pid}) didn't exit within {Wait} of being killed", folder, process.Id, wait);
        }
    }

    private async Task<string> WatchAsync()
    {
        var first = await Task.WhenAny(_process.Exited, _disconnected).ConfigureAwait(false);
        // A host that exits closes its pipe first: give its exit a moment (wall clock: only the reason depends on it), so
        // the reason is the exit code and what it said rather than the closed pipe.
        if (first != _process.Exited)
            await Task.WhenAny(_process.Exited, Task.Delay(ExitGrace)).ConfigureAwait(false);
        if (_process.HasExited)
            return ExitReason(_process);
        return $"the connection to its host ended: {await _disconnected.ConfigureAwait(false)}";
    }

    /// <summary>How long a closed connection waits for its host's exit, to tell the reason.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Marks a task's exception observed: the race above may leave it behind.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>Why a host process ended: its exit code, and the end of its stderr after an abnormal exit.</summary>
    public static string ExitReason(IPluginProcess process)
    {
        var code = process.ExitCode;
        var why = code is { } c ? $"its host exited with code {c}" : "its host exited";
        var tail = process.ErrorTail.Trim();
        return code is not 0 && tail.Length > 0 ? $"{why}: {tail}" : why;
    }

    /// <summary>
    /// Waits for <paramref name="step"/>, unless the process exits, the connection ends or the deadline passes first
    /// (a JSON-RPC call's cancellation doesn't end it until the host answers, so the deadline is raced, not passed).
    /// </summary>
    private static async Task Race(Task step, IPluginProcess process, string what, Deadline deadline, CancellationToken ct,
        TimeSpan wait, Task? connection = null)
    {
        Observe(step);
        var expired = Task.Delay(Timeout.Infinite, deadline.Token);
        Task[] tasks = connection == null ? [step, process.Exited, expired] : [step, process.Exited, connection, expired];
        var first = await Task.WhenAny(tasks).ConfigureAwait(false);
        if (first == expired)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginHostException($"its host didn't finish {what} within {wait}");
        }
        if (first == step)
        {
            try
            {
                await step.ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new PluginHostException($"its host didn't finish {what} within {wait}");
            }
            catch (Exception ex) when (ex is ConnectionLostException or IOException or RemoteInvocationException)
            {
                throw new PluginHostException(process.HasExited ? ExitReason(process) : $"its host failed while {what}: {ex.Message}");
            }
        }

        ct.ThrowIfCancellationRequested();
        if (first == process.Exited || process.HasExited)
            throw new PluginHostException(ExitReason(process));
        throw new PluginHostException($"the connection to its host ended while {what}");
    }
}

/// <summary>
/// A wait on the engine's clock (so a test's clock moves it) that also ends when <c>ct</c> is cancelled: its
/// <see cref="Token"/> is cancelled by whichever comes first; <see cref="IsCancellationRequested"/> says whether the time ran out.
/// </summary>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _linked;

    public Deadline(TimeSpan wait, TimeProvider time, CancellationToken ct)
    {
        _timeout = new CancellationTokenSource(wait, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _timeout.Token);
    }

    public CancellationToken Token => _linked.Token;

    /// <summary>Whether the time ran out.</summary>
    public bool IsCancellationRequested => _timeout.IsCancellationRequested;

    public void Dispose()
    {
        _linked.Dispose();
        _timeout.Dispose();
    }
}

/// <summary>A plugin host that couldn't be started or connected, with the reason.</summary>
public sealed class PluginHostException(string message) : Exception(message);
