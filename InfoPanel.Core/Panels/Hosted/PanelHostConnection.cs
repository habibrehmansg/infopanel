using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Panels.Hosted;

/// <summary>
/// The engine's end of one panel host's pipe (<see cref="PanelHostWire"/>): requests go out one at a time, each with an
/// id; a reader matches the answers to them. A request never holds its caller: it waits for its turn and is written in
/// the background, within the deadline its caller gives, and a host that doesn't take one in that time (it stopped
/// reading) ends the connection, since half a message can't be followed by another. When the pipe ends (the host
/// stopped or died) every request waiting, and every one sent after, fails with <see cref="PanelDriverException"/>, and
/// <see cref="Closed"/> completes. A message out of bounds from the host ends the connection.
/// </summary>
public sealed class PanelHostConnection : IDisposable
{
    /// <summary>What a request through a connection whose host has ended fails with.</summary>
    public const string Ended = "its driver's host ended";

    /// <summary>The most requests kept waiting for their answers at once: a request past it ends the connection (its host is started again).</summary>
    public const int MaxOutstanding = 64;

    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PanelHostReply>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _postDeadline;
    private long _nextId;

    /// <summary>How long a posted request (<see cref="Post"/>) has for its turn and its writing by default.</summary>
    public static readonly TimeSpan PostDeadline = TimeSpan.FromSeconds(5);

    /// <param name="postDeadline">How long a posted request has for its turn and its writing (tests: a shorter one); <see cref="PostDeadline"/> unless set.</param>
    public PanelHostConnection(Stream stream, ILogger logger, TimeSpan? postDeadline = null)
    {
        _stream = stream;
        _logger = logger;
        _postDeadline = postDeadline ?? PostDeadline;
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Completes once the pipe has ended.</summary>
    public Task Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>The requests waiting for their answers now.</summary>
    internal int Outstanding => _pending.Count;

    /// <summary>
    /// Sends <paramref name="request"/> (its id set here) without waiting: the task completes with the host's answer, fails
    /// with <see cref="PanelDriverException"/> once the pipe has ended without one, or is cancelled when
    /// <paramref name="sending"/> is cancelled before the request was written (one cancelled while being written ends the
    /// connection). The answer is kept for whenever it comes.
    /// </summary>
    public Task<PanelHostReply> SendAsync(PanelHostRequest request, CancellationToken sending = default) => Send(request, sending, out _);

    /// <summary>
    /// Sends <paramref name="request"/> without waiting, its answer not wanted (one that comes is dropped): written within
    /// the connection's post deadline (and until <paramref name="sending"/>), like every request, so a host that doesn't
    /// take it never holds the pipe. A post is cleanup (a session's close, the host's shutdown): one not written by then,
    /// still waiting its turn or half written, ends the connection, so the host never keeps what it was to let go.
    /// </summary>
    public void Post(PanelHostRequest request, CancellationToken sending = default)
    {
        if (IsClosed)
            return;
        var limit = CancellationTokenSource.CreateLinkedTokenSource(sending);
        limit.CancelAfter(_postDeadline);
        _ = WriteAsync(request with { Id = Interlocked.Increment(ref _nextId) }, null, limit.Token)
            .ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), limit, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private Task<PanelHostReply> Send(PanelHostRequest request, CancellationToken sending, out long id)
    {
        id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<PanelHostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pending.Count >= MaxOutstanding && !IsClosed)
        {
            _logger.Warning("A panel host left {Count} requests unanswered, so its connection ends", _pending.Count);
            End();
        }
        _pending[id] = answer;
        if (IsClosed)
        {
            _pending.TryRemove(id, out _);
            answer.TrySetException(new PanelDriverException(Ended));
            return answer.Task;
        }
        _ = WriteAsync(request with { Id = id }, answer, sending);
        return answer.Task;
    }

    /// <summary>Writes one request in its turn, until <paramref name="sending"/>: not yet written then, it is dropped (its answer cancelled); half written, the connection ends.</summary>
    private async Task WriteAsync(PanelHostRequest request, TaskCompletionSource<PanelHostReply>? answer, CancellationToken sending)
    {
        try
        {
            await _writing.WaitAsync(sending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Drop(request.Id, answer);
            return;
        }
        try
        {
            if (IsClosed)
                return; // its answer failed with the rest
            if (sending.IsCancellationRequested)
            {
                Drop(request.Id, answer);
                return;
            }
            await PanelHostWire.WriteAsync(_stream, request, sending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("A panel host didn't take a request in time (it stopped reading its pipe), so its connection ends");
            End();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException)
        {
            _logger.Debug(ex, "A panel host's pipe couldn't be written");
            End();
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>
    /// A request never written: its answer, when one is waited for, is cancelled. A post (a session's close or the host's
    /// shutdown: cleanup nothing else would do) ends the connection, so its host is started again without what it held.
    /// </summary>
    private void Drop(long id, TaskCompletionSource<PanelHostReply>? answer)
    {
        if (answer != null)
        {
            if (_pending.TryRemove(id, out _))
                answer.TrySetCanceled();
            return;
        }
        if (IsClosed)
            return;
        _logger.Warning("A panel host didn't take a cleanup request in time, so its connection ends");
        End();
    }

    /// <summary>
    /// <see cref="SendAsync"/>, waited for: the answer, or null when none came within <paramref name="deadline"/>, which
    /// covers the request's turn, its writing and the answer.
    /// </summary>
    /// <exception cref="PanelDriverException">The pipe ended without an answer.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was cancelled first.</exception>
    public PanelHostReply? Call(PanelHostRequest request, TimeSpan deadline, CancellationToken stop = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(stop);
        sending.CancelAfter(deadline);
        var answer = Send(request, sending.Token, out var id);
        try
        {
            var reply = Await(answer, deadline - Stopwatch.GetElapsedTime(started), stop);
            if (reply == null)
            {
                sending.Cancel(); // not yet written: dropped; half written: the connection ends
                Forget(id);
            }
            return reply;
        }
        catch (OperationCanceledException)
        {
            Forget(id);
            throw;
        }
    }

    /// <summary>A request whose answer is no longer waited for: a late one is dropped.</summary>
    private void Forget(long id)
    {
        if (_pending.TryRemove(id, out var answer))
            answer.TrySetCanceled();
    }

    /// <summary>
    /// Waits for <paramref name="answer"/> (from <see cref="SendAsync"/>) for at most <paramref name="wait"/>: the reply, or
    /// null when none came in time or the request was never written.
    /// </summary>
    /// <exception cref="PanelDriverException">The pipe ended without an answer.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was cancelled first.</exception>
    public static PanelHostReply? Await(Task<PanelHostReply> answer, TimeSpan wait, CancellationToken stop)
    {
        try
        {
            if (!answer.Wait(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, stop))
                return null;
        }
        catch (AggregateException ex) when (ex.InnerException is PanelDriverException driver)
        {
            throw new PanelDriverException(driver.Message);
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            stop.ThrowIfCancellationRequested();
            return null; // never written within its deadline
        }
        return answer.Result;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var reply = await PanelHostWire.ReadAsync<PanelHostReply>(_stream, _stop.Token).ConfigureAwait(false);
                if (reply == null)
                    break;
                if (_pending.TryRemove(reply.Id, out var answer))
                    answer.TrySetResult(reply);
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.Warning("A panel host sent a message the engine doesn't take, so its connection ends: {Why:l}", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the pipe ended
        }
        finally
        {
            End();
        }
    }

    private void End()
    {
        if (!_closed.TrySetResult())
            return;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var answer))
                answer.TrySetException(new PanelDriverException(Ended));
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends the connection: the host sees its pipe end.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        End();
    }
}
