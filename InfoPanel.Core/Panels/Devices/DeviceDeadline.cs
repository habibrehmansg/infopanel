using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Devices;

/// <summary>A call's one deadline: the caller's timeout, counted from the call's start, spent by every step it takes.</summary>
internal readonly struct DeviceDeadline
{
    private readonly long _start;
    private readonly int _timeoutMs;
    private readonly Func<int>? _remaining;

    public DeviceDeadline(int timeoutMs)
    {
        _start = Stopwatch.GetTimestamp();
        _timeoutMs = timeoutMs;
    }

    /// <summary>The tests' deadline, whose time left is what <paramref name="remaining"/> answers each time it is asked.</summary>
    internal DeviceDeadline(Func<int> remaining)
        : this(0) => _remaining = remaining;

    /// <summary>The milliseconds left, 0 once it has passed.</summary>
    public int RemainingMs => _remaining is { } scripted ? Math.Max(0, scripted())
        : (int)Math.Max(0, Math.Ceiling(_timeoutMs - Stopwatch.GetElapsedTime(_start).TotalMilliseconds));

    public bool Passed => RemainingMs <= 0;
}

/// <summary>
/// An open that keeps to its caller's deadline and token: the open runs on a thread of its own with a token that ends at the
/// deadline or with the caller's (each step looks at it before it reaches the device); the caller waits only what is
/// left. An open that ends late is disposed once it ends, never handed out: past the deadline the call answers
/// <see cref="PanelDeviceStatus.Timeout"/>, and once the caller's token is cancelled it throws.
/// </summary>
internal static class DeviceOpen
{
    public static PanelDeviceStatus Run<T>(int timeoutMs, CancellationToken cancellationToken, Func<CancellationToken, (PanelDeviceStatus Status, T? Handle)> open, out T? handle)
        where T : class, IDisposable
    {
        handle = null;
        cancellationToken.ThrowIfCancellationRequested();
        var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(timeoutMs);
        var token = expiry.Token;
        // On a thread of its own, so an open starts at once however busy the thread pool is (opens are rare: one a session).
        var done = new TaskCompletionSource<(PanelDeviceStatus Status, T? Handle)>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(open(token));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Panel device open" };
        thread.Start();
        var opening = done.Task;
        try
        {
            // A timed wait, not the expiry's token: its timer runs on the thread pool, which a busy process can hold up.
            if (!opening.Wait(timeoutMs, cancellationToken))
            {
                expiry.Cancel();
                DisposeLate(opening, expiry);
                return PanelDeviceStatus.Timeout;
            }
        }
        catch (OperationCanceledException)
        {
            expiry.Cancel();
            DisposeLate(opening, expiry);
            cancellationToken.ThrowIfCancellationRequested();
            return PanelDeviceStatus.Timeout;
        }
        catch (AggregateException)
        {
            expiry.Dispose();
            return PanelDeviceStatus.Io; // an open never throws for the device's sake
        }
        expiry.Dispose();
        var (status, opened) = opening.Result;
        if (cancellationToken.IsCancellationRequested)
        {
            opened?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (status != PanelDeviceStatus.Ok || opened is null)
        {
            opened?.Dispose();
            return status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status;
        }
        handle = opened;
        return PanelDeviceStatus.Ok;
    }

    private static void DisposeLate<T>(Task<(PanelDeviceStatus Status, T? Handle)> opening, CancellationTokenSource expiry) where T : class, IDisposable =>
        _ = opening.ContinueWith(t =>
        {
            expiry.Dispose();
            if (t.Status == TaskStatus.RanToCompletion)
                t.Result.Handle?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
