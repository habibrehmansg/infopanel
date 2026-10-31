using Microsoft.Extensions.Logging;

namespace InfoPanel.Preview.Video;

/// <summary>
/// Bounded retirement of encoder sessions. A session is never drained: <see cref="IH264Session.Abort"/> ends it at once
/// and tears it down off the caller's thread; the caller waits at most <see cref="Bound"/> for that teardown, then goes
/// on without it. A teardown that never ends leaks its session deliberately: it is logged as an error and counted
/// against its backend (<see cref="H264Backends.RecordQuarantine"/>), and three give the backend up for the run.
/// </summary>
internal static class Quarantine
{
    /// <summary>How long a retirement waits for a session's teardown.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    /// <summary>Teardowns still running past their bound in this process (tests, logs).</summary>
    private static long _leaked;

    public static long Leaked => Interlocked.Read(ref _leaked);

    /// <summary>Thread for a teardown that may block in native code: a background thread of its own, so it never holds a pool thread.</summary>
    public static Task RunOnThread(string name, Action teardown)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                teardown();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// Aborts <paramref name="session"/> and waits at most <see cref="Bound"/> (on <paramref name="time"/>) for its
    /// teardown; returns whether it ended in time. Never throws.
    /// </summary>
    public static async Task<bool> RetireAsync(IH264Session session, IH264Backend backend, H264Backends backends, TimeProvider time, ILogger logger,
        CancellationToken ct = default)
    {
        Task teardown;
        try
        {
            teardown = session.Abort();
        }
        catch (Exception ex)
        {
            logger.LogWarning("H.264 encoder {Backend} failed to abort a session: {Message}", backend.Name, ex.Message);
            return true;
        }

        return await WatchAsync(teardown, backend, backends, time, logger, ct);
    }

    /// <summary>
    /// Waits at most <see cref="Bound"/> for a teardown already running (a session's abort, or a failed start's, which
    /// hands its teardown back); one that doesn't end in time is logged and counted as quarantined. Never throws.
    /// </summary>
    public static async Task<bool> WatchAsync(Task teardown, IH264Backend backend, H264Backends backends, TimeProvider time, ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            await teardown.WaitAsync(Bound, time, ct);
            return true;
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning("H.264 encoder {Backend} failed to tear a session down: {Message}", backend.Name, ex.Message);
            return true;
        }

        Abandon(teardown, backend, backends, logger);
        return false;
    }

    /// <summary>
    /// A teardown already waited for past <see cref="Bound"/> (a probe's, which waits for its own session): left behind,
    /// logged and counted as quarantined, as <see cref="WatchAsync"/> does with one that doesn't end in time.
    /// </summary>
    public static void Abandon(Task teardown, IH264Backend backend, H264Backends backends, ILogger logger)
    {
        Interlocked.Increment(ref _leaked);
        _ = teardown.ContinueWith(_ => Interlocked.Decrement(ref _leaked), TaskScheduler.Default);
        logger.LogError("H.264 encoder {Backend}: a session didn't tear down within {Ms} ms; left behind (quarantined)", backend.Name,
            (int)Bound.TotalMilliseconds);
        backends.RecordQuarantine(backend);
    }
}
