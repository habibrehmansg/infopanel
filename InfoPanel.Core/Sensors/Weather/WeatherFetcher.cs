using InfoPanel.Service;
using Serilog;
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Weather;

/// <summary>
/// The answer whose values the weather source shows: the answer, when it came (the time provider's timestamp; the age of
/// the values, which a stale answer after it never renews), the place asked for (null: from the network address), and
/// whether they read stale (the answer was stale, or a stale one came since).
/// </summary>
public sealed record WeatherHeld(WeatherAnswer Answer, long FetchedAt, string? Place, bool Stale = false);

/// <summary>
/// The weather source's task (docs/2.0/weather.md): asks the configured service for the weather, through
/// <see cref="ServiceClient"/> and its rules (only to the service's address, the credential as a bearer token, no
/// redirect followed, no credential no request, a 401 refusing every request until a client provides another,
/// <c>--offline</c> asking only a service on this computer), when <see cref="WeatherSchedule"/> says, and keeps the
/// last answer (<see cref="Latest"/>), so the source's poll only reads it and never waits on the network. One request at
/// a time; each bounded at <see cref="RequestTimeout"/>, its answer read to at most <see cref="WeatherAnswer.MaxBytes"/>.
/// A change of the place, of the credential or of the service address asks at once; so does turning it on. It asks only
/// while a weather value is in use, read within <see cref="UseWindow"/> (<see cref="NoteUse"/>): with no reader nothing is
/// asked, the answer held ages as it does, and a request that falls due (or that a change made due) waits for the next
/// reader, then is made at once; one not yet due waits for its time. Turned off,
/// it cancels a request under way, forgets the answer and asks nothing. The credential is never read here, nor logged;
/// the place is never logged.
/// </summary>
public sealed class WeatherFetcher : IDisposable
{
    /// <summary>The service's weather request, on the service's origin.</summary>
    public const string Path = "/svc/v1/weather";

    /// <summary>Each request's bound, reading the answer included.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A weather value read within this long is in use (the same 2 minutes as the Network source's public address): the service is asked only then.</summary>
    public static readonly TimeSpan UseWindow = TimeSpan.FromMinutes(2);

    private const long NeverUsed = long.MinValue;

    private static readonly SourceReason Disabled = new("disabled", "Turned off in the engine's settings (sources.weather.enabled)");
    private static readonly SourceReason NoService = new("noService", "The engine has no service address to ask for the weather");
    private static readonly SourceReason NoCredential = new("noCredential", "Waiting for a client to provide the service credential");
    private static readonly SourceReason Offline = new("offline", "The engine runs offline (--offline) and the service isn't on this computer");
    private static readonly SourceReason CredentialRejected = new("credentialRejected", "The service refused the credential; a client must provide a new one");
    private static readonly SourceReason PlaceNotFound = new("placeNotFound", "The service found no place by the name set (sources.weather.place)");
    private static readonly SourceReason LocationUnknown = new("locationUnknown", "The service couldn't tell where this computer is; set a place (sources.weather.place)");
    private static readonly SourceReason RateLimited = new("rateLimited", "The service asked the engine to wait: too many requests");
    private static readonly SourceReason ServiceUnavailable = new("serviceUnavailable", "The service couldn't be reached, or didn't answer with the weather");

    private readonly ServiceClient? _client;
    private readonly ServiceCredentials? _credentials;
    private readonly TimeProvider _time;
    private readonly Func<double> _jitter;
    private readonly ILogger _log;
    private readonly WeatherSchedule _schedule = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    // Under _lock: the settings, the change count (a change of place or of on/off cancels a request under way and drops
    // what it brings), whether a request is due at once, when the next is due (null: only after a change), the state.
    private bool _enabled = true;
    private string? _place;
    private long _generation;
    private bool _pending = true;
    private long? _dueAt;
    private SourceState _state = SourceState.Starting;
    private CancellationTokenSource _changeCts = new();
    private ServiceStatus? _seen;

    // When a weather value was last read (the time provider's timestamp), or NeverUsed.
    private long _lastUse = NeverUsed;
    // 1 while the task waits for want of a reader (set before it reads _lastUse), so the next read wakes it.
    private int _noReader;
    private volatile WeatherHeld? _held;
    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private long _requests;
    private long _attempts;
    private long _waits;
    private volatile bool _waiting;

    /// <param name="client">How the requests go to the service; null: the engine has no service (<c>noService</c>).</param>
    /// <param name="credentials">The credential's state, followed so a change asks at once; null follows nothing.</param>
    /// <param name="jitter">A random number from 0 to 1 for each answer's jitter; <see cref="Random.Shared"/> by default.</param>
    public WeatherFetcher(ServiceClient? client, ServiceCredentials? credentials, TimeProvider? time = null, Func<double>? jitter = null, ILogger? log = null)
    {
        _client = client;
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _log = (log ?? Log.Logger).ForContext<WeatherFetcher>();
        if (_credentials != null)
        {
            _seen = _credentials.Status;
            _credentials.Changed += CredentialsChanged;
        }
    }

    /// <summary>The answer held, or null (none yet, forgotten when the place changed or it was turned off).</summary>
    public WeatherHeld? Latest => _held;

    /// <summary>
    /// What the task last found: <see cref="SourceState.Starting"/> before its first request; <c>running</c> after an
    /// answer; <c>off</c> turned off (<c>disabled</c>), without a service address (<c>noService</c>) or a credential
    /// (<c>noCredential</c>); else <c>unavailable</c>, with why.
    /// </summary>
    public SourceState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Requests sent to the service (an answer came, or the request failed after it was made). For tests and diagnostics.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Requests the task tried, sent or refused before sending. For tests.</summary>
    internal long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>How many times the task has begun waiting. For tests.</summary>
    internal long Waits => Interlocked.Read(ref _waits);

    /// <summary>Whether the task waits for a change or its time (no request under way). For tests.</summary>
    internal bool IsWaiting => _waiting;

    /// <summary>Called on the task once it has taken a request that is due, before it makes it. For tests.</summary>
    internal Action? AfterTakeDue { get; set; }

    /// <summary>Called in <see cref="NoteUse"/> once it has read the time, before it records the read. For tests.</summary>
    internal Action? AfterUseCaptured { get; set; }

    /// <summary>
    /// The settings: on or off, and the place to ask for (null: from the network address). Turning it off cancels a
    /// request under way and forgets the answer; turning it on, or a new place (which forgets the answer, for another
    /// place), asks at once. The same values again change nothing.
    /// </summary>
    public void Configure(bool enabled, string? place)
    {
        CancellationTokenSource before;
        lock (_lock)
        {
            if (enabled == _enabled && string.Equals(place, _place, StringComparison.Ordinal))
                return;
            var wasEnabled = _enabled;
            _enabled = enabled;
            _place = place;
            _generation++;
            _held = null;
            _schedule.Reset();
            // A request under way is for the settings before: cancelled (outside the lock), and what it brings is dropped.
            before = _changeCts;
            _changeCts = new CancellationTokenSource();
            _dueAt = null;
            _pending = enabled;
            if (!enabled)
                _state = SourceState.OffBecause(Disabled);
            else if (!wasEnabled)
                _state = SourceState.Starting;
        }

        // Not disposed: a request may still link to its token (it holds no timer).
        before.Cancel();
        Wake();
    }

    /// <summary>
    /// A weather value was read (a shown profile's tick, a graph's draw, a client's value read; <see cref="WeatherSource"/>
    /// calls this as <c>NetworkSource</c> does <c>PublicIpLookup.NoteUse</c>). When that brings the weather into use (not
    /// read within <see cref="UseWindow"/> before), or the task waits for want of a reader (whatever time this read took,
    /// one just inside the window included), the task looks at its schedule at once: a request due, or pending since the
    /// last change, is made then. Takes no lock and allocates nothing.
    /// </summary>
    public void NoteUse()
    {
        var now = _time.GetTimestamp();
        AfterUseCaptured?.Invoke();
        // The read is recorded before the task's flag is read, and the task sets its flag before it reads the last read
        // (both full fences): either the task sees this read, or this read sees the flag and wakes it.
        var last = Interlocked.Exchange(ref _lastUse, now);
        var waitsForAReader = Volatile.Read(ref _noReader) != 0 && Interlocked.Exchange(ref _noReader, 0) != 0;
        if (waitsForAReader || last == NeverUsed || _time.GetElapsedTime(last, now) > UseWindow)
            Wake();
    }

    /// <summary>Whether a weather value was read within <see cref="UseWindow"/> of <paramref name="now"/>.</summary>
    private bool InUse(long now)
    {
        var last = Interlocked.Read(ref _lastUse);
        return last != NeverUsed && _time.GetElapsedTime(last, now) <= UseWindow;
    }

    /// <summary>Starts the task (a no-op when it runs).</summary>
    public void Start()
    {
        _lifecycle.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is { IsCompleted: false })
                return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Cancels a request under way and waits for the task to end.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop is not { } loop)
                return;
            await _cts!.CancelAsync().ConfigureAwait(false);
            await loop.WaitAsync(ct).ConfigureAwait(false);
            _loop = null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        _lifecycle.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
        }
        finally
        {
            _lifecycle.Release();
        }

        if (_credentials != null)
            _credentials.Changed -= CredentialsChanged;
    }

    // The credential or the address changed (provided, removed, rejected, another address): when what the engine would
    // send changed, a request is due at once (refused before it is sent when there is nothing to send).
    private void CredentialsChanged()
    {
        var status = _credentials!.Status;
        lock (_lock)
        {
            if (_seen is { } seen && seen.Address == status.Address && seen.Credential == status.Credential && seen.Fingerprint == status.Fingerprint)
                return;
            _seen = status;
            if (!_enabled)
                return;
            _schedule.Reset();
            _pending = true;
        }

        Wake();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    /// <summary>A request taken: the settings it is for, as they were when it was taken, and their change's token.</summary>
    private readonly record struct Taken(long Generation, string? Place, CancellationToken Changed);

    /// <summary>
    /// The request due now, taken with the settings it is for in one step (a change after it is the next request's), or
    /// the wait until one is due (null: none until a change).
    /// </summary>
    private (Taken? Request, TimeSpan? Wait) TakeDue()
    {
        lock (_lock)
        {
            if (!_enabled)
                return (null, null);
            var now = _time.GetTimestamp();
            // Nothing is asked while nothing reads a weather value: the due request, and what a change made due, wait
            // for a reader. The flag is set first, so a read recorded after the check below wakes the task (NoteUse).
            Interlocked.Exchange(ref _noReader, 1);
            if (!InUse(now))
                return (null, null);
            Volatile.Write(ref _noReader, 0);
            if (_pending || (_dueAt is { } at && now >= at))
                return (TakeLocked(), null);

            return (null, _dueAt is { } due ? _time.GetElapsedTime(now, due) : null);
        }
    }

    /// <summary>Under <see cref="_lock"/>, turned on: consumes the request due and captures what it is for.</summary>
    private Taken TakeLocked()
    {
        _pending = false;
        _dueAt = null;
        return new Taken(_generation, _place, _changeCts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A new wake before the schedule is read: a change made after the read completes it, so none is missed.
                var wake = NewWake();
                Volatile.Write(ref _wake, wake);
                var (request, wait) = TakeDue();
                if (request is { } taken)
                {
                    AfterTakeDue?.Invoke();
                    await FetchAsync(taken, ct).ConfigureAwait(false);
                    continue;
                }

                Interlocked.Increment(ref _waits);
                _waiting = true;
                try
                {
                    await (wait is { } w ? wake.Task.WaitAsync(w, _time, ct) : wake.Task.WaitAsync(ct)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
                finally
                {
                    _waiting = false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Weather: the task failed");
        }
    }

    /// <summary>What one request came to: the state, an answer to hold (or none), and the wait until the next (null: until a change).</summary>
    private readonly record struct Outcome(SourceState State, WeatherAnswer? Answer, TimeSpan? Wait);

    /// <summary>One request now, whether or not one is due (it takes the one due, if any). For tests.</summary>
    internal Task FetchOnceAsync(CancellationToken ct)
    {
        Taken taken;
        lock (_lock)
        {
            if (!_enabled)
                return Task.CompletedTask;
            taken = TakeLocked();
        }

        return FetchAsync(taken, ct);
    }

    /// <summary>
    /// The request <paramref name="taken"/>, its outcome kept: the state, the answer, when the next is due. Dropped when
    /// the place changed or it was turned off since it was taken. Throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    private async Task FetchAsync(Taken taken, CancellationToken ct)
    {
        var (generation, place, changed) = taken;
        Interlocked.Increment(ref _attempts);
        Outcome outcome;
        using (var timeout = new CancellationTokenSource(RequestTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, changed, timeout.Token))
        {
            try
            {
                outcome = await RequestAsync(place, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || changed.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unreachable, refused, timed out, cut off: the service didn't answer.
                lock (_lock)
                    outcome = new Outcome(SourceState.UnavailableBecause(ServiceUnavailable), null, _schedule.Failed());
            }
        }

        SourceState before;
        lock (_lock)
        {
            // Turned off, or another place, since it was sent: what it brought is for no one.
            if (generation != _generation)
                return;
            before = _state;
            _state = outcome.State;
            if (outcome.Answer is { } answer)
                _held = HoldLocked(answer, place);
            _dueAt = outcome.Wait is { } wait ? _time.GetTimestamp() + Ticks(wait) : null;
        }

        if (before != outcome.State)
            LogState(outcome.State, outcome.Wait);
    }

    /// <summary>
    /// Under <see cref="_lock"/>: what an <paramref name="answer"/> leaves held. A live one is held, its age from now. A
    /// stale one (the service couldn't refresh it) never renews the age of the values shown: values held stay, read stale
    /// from now on; with none held (for this place, since it was turned on), it is held, read stale, its age from now.
    /// </summary>
    private WeatherHeld HoldLocked(WeatherAnswer answer, string? place)
    {
        if (!answer.Stale || _held is not { } held)
            return new WeatherHeld(answer, _time.GetTimestamp(), place, answer.Stale);
        return held.Stale ? held : held with { Stale = true };
    }

    private long Ticks(TimeSpan wait) => (long)(wait.Ticks * (double)_time.TimestampFrequency / TimeSpan.TicksPerSecond);

    private async Task<Outcome> RequestAsync(string? place, CancellationToken ct)
    {
        if (_client == null)
            return new Outcome(SourceState.OffBecause(NoService), null, null);

        var path = place is null ? Path : Path + "?place=" + Uri.EscapeDataString(place);
        using var answer = await _client.SendAsync(HttpMethod.Get, path, ct: ct).ConfigureAwait(false);
        if (answer.Refused is { } refused)
        {
            return new Outcome(refused switch
            {
                ServiceRefusal.NoService => SourceState.OffBecause(NoService),
                ServiceRefusal.NoCredential => SourceState.OffBecause(NoCredential),
                ServiceRefusal.Rejected => SourceState.UnavailableBecause(CredentialRejected),
                ServiceRefusal.Offline => SourceState.UnavailableBecause(Offline),
                _ => SourceState.UnavailableBecause(NoService),
            }, null, null);
        }

        Interlocked.Increment(ref _requests);
        var response = answer.Response!;
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var body = await ReadAsync(response, ct).ConfigureAwait(false);
                if (body is not null && WeatherAnswer.Parse(body) is { } parsed)
                {
                    lock (_lock)
                        return new Outcome(SourceState.Running, parsed, _schedule.Answered(_time.GetUtcNow(), parsed.Expires, _jitter()));
                }

                break;
            case HttpStatusCode.Unauthorized:
                // ServiceClient has marked the credential rejected: nothing is sent until a client provides another.
                return new Outcome(SourceState.UnavailableBecause(CredentialRejected), null, null);
            case HttpStatusCode.TooManyRequests:
                return new Outcome(SourceState.UnavailableBecause(RateLimited), null, WeatherSchedule.Limited(answer.RetryAfter));
            case HttpStatusCode.BadRequest:
                lock (_lock)
                    return new Outcome(SourceState.UnavailableBecause(PlaceNotFound), null, _schedule.Failed());
            case HttpStatusCode.UnprocessableEntity:
                var reason = await ProblemCodeAsync(response, ct).ConfigureAwait(false) switch
                {
                    "placeNotFound" => PlaceNotFound,
                    "locationUnknown" => LocationUnknown,
                    _ => ServiceUnavailable,
                };
                lock (_lock)
                    return new Outcome(SourceState.UnavailableBecause(reason), null, _schedule.Failed());
        }

        lock (_lock)
            return new Outcome(SourceState.UnavailableBecause(ServiceUnavailable), null, _schedule.Failed(answer.RetryAfter));
    }

    /// <summary>The answer's body, or null when it is longer than <see cref="WeatherAnswer.MaxBytes"/> (refused unread past it).</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > WeatherAnswer.MaxBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[WeatherAnswer.MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return read > WeatherAnswer.MaxBytes ? null : buffer[..read];
    }

    /// <summary>A problem answer's <c>code</c>, or null.</summary>
    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (await ReadAsync(response, ct).ConfigureAwait(false) is not { } body)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = WeatherAnswer.MaxDepth });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void LogState(SourceState state, TimeSpan? wait)
    {
        if (state.Kind == SourceStateKind.Running)
            _log.Information("Weather: the service answers");
        else if (state.Reason is { } reason && wait is { } next)
            _log.Warning("Weather: {Reason} ({Code}); asking again in {Wait}", reason.Message, reason.Code, next);
        else if (state.Reason is { } why)
            _log.Information("Weather: {Reason} ({Code})", why.Message, why.Code);
    }
}
