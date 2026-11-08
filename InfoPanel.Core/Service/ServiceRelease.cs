using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

namespace InfoPanel.Service;

/// <summary>
/// Asks the configured service, once, to forget a credential the engine replaced (spec/docs/api.md, "The configured
/// service"): <c>DELETE /svc/v1/device</c> with that credential as <c>Authorization: Bearer</c>, through
/// <see cref="ServiceClient"/> and its rules, to the address it was kept for. Started after the new credential is kept and
/// never awaited by the request that provided it; one attempt by the engine (the connection layer may send it again, within
/// the same bound, when a connection closes before any answer), dropped after <see cref="Bound"/>, nothing stored (a
/// restart forgets it). It is dropped the moment it may no longer be sent: the credential is provided again, the address
/// changes (checked under the store's lock right before sending, and a request still being made is cancelled), or the
/// engine stops: no release starts after that, the engine's stop waits for those running within <see cref="StopWait"/>,
/// and one still running past it starts no request and logs nothing. A request handed to the connection before a drop can't
/// be recalled. Whatever the service answers, nothing changes in the
/// engine (a 401 rejects nothing: the credential isn't kept). One log line says how it went, never a credential, its
/// fingerprint or the service's text.
/// </summary>
public sealed class ServiceRelease : IAsyncDisposable
{
    /// <summary>The longest the request may take, its answer's headers included.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>The longest the engine's stop waits for the releases still running, once they are cancelled.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    /// <summary>The service's path that forgets the client whose credential asks.</summary>
    internal const string Path = "/svc/v1/device";

    /// <summary>Why a release was dropped: the engine stops.</summary>
    internal const string Stopping = "the engine stops";

    private readonly ServiceClient _client;
    private readonly TimeSpan _bound;
    private readonly TimeSpan _stopWait;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop;
    private readonly Lock _lock = new();

    /// <summary>The releases running; under <see cref="_lock"/>.</summary>
    private readonly HashSet<Task> _running = [];

    /// <summary>
    /// Under <see cref="_lock"/>: <see cref="DisposeAsync"/> has returned. A release still running past its wait starts no
    /// request (its token is cancelled) and logs nothing from then on.
    /// </summary>
    private bool _disposed;

    /// <param name="client">The client the request goes through.</param>
    /// <param name="stopping">The engine stops (the host's <c>ApplicationStopping</c>): every release is dropped.</param>
    /// <param name="log">Where its one line goes.</param>
    public ServiceRelease(ServiceClient client, CancellationToken stopping, ILogger? log = null)
        : this(client, Bound, TimeProvider.System, stopping, log)
    {
    }

    /// <summary>With another bound, clock and stop wait: the tests' seam.</summary>
    internal ServiceRelease(ServiceClient client, TimeSpan bound, TimeProvider time, CancellationToken stopping, ILogger? log,
        TimeSpan? stopWait = null)
    {
        _client = client;
        _bound = bound;
        _stopWait = stopWait ?? StopWait;
        _time = time;
        _log = log ?? Log.ForContext<ServiceRelease>();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    /// <summary>
    /// Starts the request for <paramref name="replaced"/> in the background and returns at once; nothing when it is null,
    /// or once the engine stops. The task returned never faults; it ends once the request has, and is only for tests to
    /// wait on.
    /// </summary>
    public Task Start(ReplacedServiceCredential? replaced)
    {
        if (replaced is null)
            return Task.CompletedTask;
        Task? task = null;
        lock (_lock)
        {
            if (!_stop.IsCancellationRequested)
            {
                task = Task.Run(() => RunAsync(replaced));
                _running.Add(task);
            }
        }

        if (task is null)
        {
            replaced.Drop(Stopping);
            _client.Released(replaced);
            return Task.CompletedTask;
        }

        // Outside the lock: a release already ended runs this at once.
        _ = task.ContinueWith(t =>
        {
            lock (_lock)
                _running.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>The reason a request that got no answer within <paramref name="bound"/> was dropped.</summary>
    internal static string NoAnswer(TimeSpan bound) => string.Create(CultureInfo.InvariantCulture, $"no answer in {bound.TotalSeconds:0.###} s");

    private async Task RunAsync(ReplacedServiceCredential replaced)
    {
        const string Couldnt = "Couldn't tell the configured service to forget the credential this engine replaced ({Reason:l})";
        using var timeout = new CancellationTokenSource(_bound, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, replaced.Dropped, _stop.Token);
        try
        {
            using var answer = await _client.SendWithAsync(replaced, HttpMethod.Delete, Path, linked.Token).ConfigureAwait(false);
            if (answer.Refused is { } refused)
            {
                Say(LogEventLevel.Warning, Couldnt, refused switch
                {
                    ServiceRefusal.Offline => "not sent: offline",
                    ServiceRefusal.OtherOrigin => "not sent: the address changed",
                    _ => "not sent: " + refused,
                });
            }
            else if (answer.Response!.IsSuccessStatusCode)
            {
                Say(LogEventLevel.Information, "Told the configured service to forget the credential this engine replaced");
            }
            else if (answer.Response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Say(LogEventLevel.Information, "The configured service no longer knew the credential this engine replaced");
            }
            else
            {
                Say(LogEventLevel.Warning, Couldnt, ((int)answer.Response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (OperationCanceledException) when (replaced.Reason is not null || _stop.IsCancellationRequested)
        {
            if (_stop.IsCancellationRequested)
                replaced.Drop(Stopping);
            Say(LogEventLevel.Warning, Couldnt, "dropped: " + replaced.Reason);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Say(LogEventLevel.Warning, Couldnt, NoAnswer(_bound));
        }
        catch (Exception ex)
        {
            // Its type only: an exception's text may carry the service's.
            Say(LogEventLevel.Warning, Couldnt, ex.GetType().Name);
        }
        finally
        {
            _client.Released(replaced);
        }
    }

    /// <summary>
    /// The release's one line, unless <see cref="DisposeAsync"/> has returned: checked and written under
    /// <see cref="_lock"/>, so no line follows it.
    /// </summary>
    private void Say(LogEventLevel level, string template, string? reason = null)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (reason is null)
                _log.Write(level, template);
            else
                _log.Write(level, template, reason);
        }
    }

    /// <summary>
    /// The engine stops: every release is dropped, none starts after this, and this waits for those running, at most
    /// <see cref="StopWait"/>; once it returns, one still running starts no request and logs nothing. Called by the host
    /// when it disposes its services, before the client's own disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_lock)
        {
            _ = _stop.CancelAsync();
            running = [.. _running];
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(_stopWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warning("The engine stops while a request to the configured service to forget a replaced credential still runs");
        }
        finally
        {
            lock (_lock)
                _disposed = true;
        }
    }
}
