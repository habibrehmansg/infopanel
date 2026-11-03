using InfoPanel.SensorHelper;
using Serilog;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// Chooses the sensor helper each start (Windows): when <paramref name="taskUser"/> names a user (the engine runs
/// unelevated and that user's task runs this install's helper: <see cref="LibreElevation.TaskUser"/>), the helper the
/// task runs as SYSTEM; otherwise, and whenever that fails, the engine's own child (<paramref name="child"/>, elevated
/// exactly when the engine is). The task's helper: the task is started (<see cref="ILibreElevationSystem.RunTask"/>),
/// its pipe (<see cref="HelperTask.PipeName"/>) opened without <see cref="PipeOptions.CurrentUserOnly"/> (its server is
/// SYSTEM) and identification only, and its owner checked before anything is sent: only a pipe SYSTEM owns is the
/// helper's (a user's own process can't create one; it can't open a SYSTEM process to read its image path either). A
/// pipe that doesn't come within <paramref name="runAgainEvery"/> has the task started again: the helper a restart
/// replaces may still be ending (its pipe gone, its process cleaning up), and the task's <c>IgnoreNew</c> drops a start
/// while it runs. Its helper is the one only once it answered the engine's hello: one that drops the engine after the
/// owner check (refused, closed, no answer) serves nothing (<see cref="NotServing"/>). All within
/// <see cref="ConnectTimeout"/>; a wrong owner, a refused start, a helper that didn't serve or the time running out falls
/// back to the child, logged with why and told to <paramref name="fellBack"/> (<see cref="LastFallback"/>; null after a
/// start that didn't fall back), and so does a <paramref name="taskUser"/> that throws (whether the task is set up
/// couldn't be read: <see cref="TaskUnreadable"/>, not taken for no task). So does the start after
/// <see cref="UnservedBeforeFallback"/> task's helpers in a row were lost before answering a sample (each said hello,
/// then the sampler gave it up as failed: <see cref="ILibreHelper.GivenUp"/>), once: the child has its turn, and the
/// start after tries the task again. A task's helper the engine stopped on purpose before a sample (Libre off, a restart
/// it asked for) neither counts nor resets the count (Codex W5.2 r4 2: two quick toggles sent every start to the child).
/// </summary>
public sealed class LibreHelperChooser(ILibreHelperLauncher child, Func<string?> taskUser, ILibreElevationSystem system, TimeSpan? connectTimeout = null,
    Func<string, string>? pipeName = null, TimeProvider? time = null, ILogger? log = null, TimeSpan? runAgainEvery = null, Action<LibreHelperFallback?>? fellBack = null) : ILibreHelperLauncher
{
    /// <summary>How long the task's helper has to serve its pipe, owner checked, before the child is started instead.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a start of the task has to bring its pipe before the task is started again (within <see cref="ConnectTimeout"/>).</summary>
    public static readonly TimeSpan RunAgainEvery = TimeSpan.FromSeconds(1);

    /// <summary>The task's helper didn't serve the engine: its pipe didn't come within <see cref="ConnectTimeout"/>, it dropped the engine before answering its hello, or <see cref="UnservedBeforeFallback"/> in a row were lost before a sample.</summary>
    public const string NotServing = "notServing";

    /// <summary>Task's helpers in a row given up as failed before answering a sample, after which a start goes to the child.</summary>
    public const int UnservedBeforeFallback = 2;

    /// <summary>The pipe of the task's name isn't SYSTEM's (a squatter's, or its owner can't be told).</summary>
    public const string NotSystem = "notSystem";

    /// <summary>Windows refused to start the task.</summary>
    public const string StartRefused = "startRefused";

    /// <summary>Whether the task is set up couldn't be read: not taken for no task, since it may be (Codex r2a 2).</summary>
    public const string TaskUnreadable = "taskUnreadable";

    private readonly TimeSpan _connectTimeout = connectTimeout ?? ConnectTimeout;
    private readonly TimeSpan _runAgainEvery = runAgainEvery ?? RunAgainEvery;
    private readonly Func<string, string> _pipeName = pipeName ?? HelperTask.PipeName;
    private readonly ILogger _log = (log ?? Log.Logger).ForContext<LibreHelperChooser>();
    private volatile LibreHelperFallback? _lastFallback;
    private TaskHelper? _lastTaskHelper;
    private int _unserved;

    /// <summary>Why the last start that tried the task's helper fell back to the child; null when it didn't (or none tried).</summary>
    public LibreHelperFallback? LastFallback => _lastFallback;

    private void FellBack(LibreHelperFallback? why)
    {
        _lastFallback = why;
        fellBack?.Invoke(why);
    }

    public async Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel)
    {
        string? sid;
        try
        {
            sid = taskUser();
        }
        catch (Exception ex)
        {
            // Not "no task": it may be set up, so the child serving is a fallback, said until a start gets the task's helper.
            _log.Warning(ex, "Libre: whether the sensor helper's task is set up couldn't be read; the helper starts without administrator rights");
            var why = "its task couldn't be read: " + ex.Message;
            FellBack(new LibreHelperFallback(TaskUnreadable, why[..Math.Min(why.Length, LibreHelperFallback.MaxMessage)]));
            return await child.StartAsync(observer, cancel).ConfigureAwait(false);
        }

        if (sid is null)
        {
            FellBack(null);
            return await child.StartAsync(observer, cancel).ConfigureAwait(false);
        }

        // The task's helper the last start handed on, ended (this is a restart): one that answered a sample resets the
        // count, one given up as failed before that counts, and one stopped on purpose before that does neither.
        if (Interlocked.Exchange(ref _lastTaskHelper, null) is { } last)
        {
            if (last.Served)
                _unserved = 0;
            else if (last.Failed)
                _unserved++;
        }

        if (_unserved >= UnservedBeforeFallback)
        {
            var why = $"its helper ended {_unserved} starts in a row before serving a sample";
            _unserved = 0;
            FellBack(new LibreHelperFallback(NotServing, why));
            _log.Warning("Libre: the sensor helper's task didn't serve ({Why}); the helper starts without administrator rights", why);
            return await child.StartAsync(observer, cancel).ConfigureAwait(false);
        }

        try
        {
            var helper = await StartTaskAsync(sid, observer, cancel).ConfigureAwait(false);
            _lastTaskHelper = helper;
            FellBack(null);
            _log.Information("Libre: the sensor helper runs through its task, as SYSTEM");
            return helper;
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            var code = ex switch
            {
                TimeoutException or NotServingException => NotServing,
                NotSystemException => NotSystem,
                _ => StartRefused,
            };
            FellBack(new LibreHelperFallback(code, ex.Message is { Length: > 0 } why ? why[..Math.Min(why.Length, LibreHelperFallback.MaxMessage)] : code));
            _log.Warning("Libre: the sensor helper's task didn't serve ({Why}); the helper starts without administrator rights", ex.Message);
        }

        return await child.StartAsync(observer, cancel).ConfigureAwait(false);
    }

    private async Task<TaskHelper> StartTaskAsync(string sid, SensorHelperClient.IObserver? observer, CancellationToken cancel)
    {
        using var deadline = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
        NamedPipeClientStream? pipe = null;
        try
        {
            while (true)
            {
                await Task.Run(() => system.RunTask(sid), CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
                pipe = new NamedPipeClientStream(".", _pipeName(sid), PipeDirection.InOut, PipeOptions.Asynchronous,
                    OperatingSystem.IsWindows() ? TokenImpersonationLevel.Identification : TokenImpersonationLevel.None);
                try
                {
                    await pipe.ConnectAsync((int)Math.Max(1, _runAgainEvery.TotalMilliseconds), linked.Token).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    // No pipe yet: the start may have been dropped while the helper it replaces ends. Start it again.
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = null;
                }
            }

            var owner = system.PipeOwner(pipe);
            if (!string.Equals(owner, HelperTask.SystemSid, StringComparison.Ordinal))
                throw new NotSystemException($"its pipe is owned by {owner ?? "someone Windows can't name"}, not SYSTEM");
            var client = new SensorHelperClient(pipe, time, observer);
            pipe = null;
            HelloAnswer hello;
            try
            {
                hello = await client.HelloAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException)
                    throw;
                throw new NotServingException($"its helper didn't serve the engine: {ex.Message}");
            }

            return new TaskHelper(client, hello);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"nothing served its pipe within {_connectTimeout.TotalSeconds:0} s");
        }
        finally
        {
            if (pipe is not null)
                await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NotSystemException(string message) : IOException(message);

    private sealed class NotServingException(string message) : IOException(message);

    /// <summary>
    /// The task's helper: its process isn't the engine's to watch or end; closing the pipe ends it. Its hello was answered
    /// when it started (<paramref name="hello"/>, the first <see cref="HelloAsync"/>'s answer); <see cref="Served"/> once
    /// it answered a sample, <see cref="Failed"/> once the sampler gave it up (<see cref="GivenUp"/>).
    /// </summary>
    private sealed class TaskHelper(SensorHelperClient client, HelloAnswer hello) : LibreHelperConnection(client, new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously).Task)
    {
        private int _disposed;
        private int _helloTaken;
        private volatile bool _served;
        private volatile bool _failed;

        public bool Served => _served;

        public bool Failed => _failed;

        public override void GivenUp() => _failed = true;

        public override Task<HelloAnswer> HelloAsync(CancellationToken cancel) =>
            Interlocked.Exchange(ref _helloTaken, 1) == 0 ? Task.FromResult(hello) : base.HelloAsync(cancel);

        public override async Task<HelperSample> SampleAsync(CancellationToken cancel)
        {
            var sample = await base.SampleAsync(cancel).ConfigureAwait(false);
            _served = true;
            return sample;
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await Client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Why a start of the sensor helper fell back from the task's helper to the engine's own: a code (<see cref="LibreHelperChooser.NotServing"/>, <see cref="LibreHelperChooser.NotSystem"/>, <see cref="LibreHelperChooser.StartRefused"/>, <see cref="LibreHelperChooser.TaskUnreadable"/>) and the same in English.</summary>
public sealed record LibreHelperFallback(string Code, string Message)
{
    /// <summary>The longest message the API carries (a source's reason's).</summary>
    public const int MaxMessage = 256;
}
