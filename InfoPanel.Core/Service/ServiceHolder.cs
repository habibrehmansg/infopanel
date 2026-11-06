using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Service;

/// <summary>
/// Who holds the service credential, as the menu shows it: <see cref="Held"/>, the engine holds a credential the service
/// hasn't refused; <see cref="Name"/>, its holder's name (else email) as the service last gave it for that same
/// credential, ready to show, or null while it isn't known; <see cref="Email"/>, the holder's email shown beside that
/// name, ready to show, or null when the name is the email itself or there is none.
/// </summary>
public readonly record struct ServiceHolderState(bool Held, string? Name, string? Email = null);

/// <summary>
/// Asks the configured service who holds the credential (docs/2.0/desktop-shell.md, "The menu"):
/// <c>GET /svc/v1/holder</c> through <see cref="ServiceClient"/> and its rules (only to the service's address, the
/// credential as a bearer token, no redirect followed, no credential no request, a 401 refusing every request until a
/// client provides another, <c>--offline</c> asking only a service on this computer), answered
/// <c>{"name": string | null, "email": string}</c>. It asks once started, when the credential or the service address
/// changes, and again every <see cref="Interval"/> after an answer while one is held. After a failed ask (unreachable, a
/// 5xx, a malformed answer) it asks again sooner, as the weather source does: after <see cref="FirstRetry"/>, doubling
/// with each failure in a row, or after the failure's <c>Retry-After</c> when that is longer, never later than
/// <see cref="Interval"/>; and at once when the computer's network changes (<see cref="NetworkChanged"/>). An answer, a
/// change of the credential or the address, or a network change starts the retries again at <see cref="FirstRetry"/>. A
/// failure of the task itself is logged and the next change asks again. One request at a time, each bounded at
/// <see cref="RequestTimeout"/>, its answer read to at most <see cref="MaxBytes"/>. The name (else the email) and the
/// email beside it are kept in memory only, tied to the credential's generation they were asked with, so an answer that
/// comes after the credential was replaced or removed is never shown for another; a failure keeps what is known for that
/// same credential. Never stored, never logged; nor is the credential, which isn't read here.
/// What the menu reads (<see cref="Current"/>) is published on each change, so reading it never waits for the credential's
/// store.
/// </summary>
public sealed class ServiceHolder : IDisposable
{
    /// <summary>The service's holder request, on the service's origin.</summary>
    public const string Path = "/svc/v1/holder";

    /// <summary>Each request's bound, reading the answer included.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long after an answer it asks again while the credential is held; the longest wait after a failure.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>The wait after the first failed ask in a row, doubled after each further one, up to <see cref="Interval"/>.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);

    /// <summary>The longest answer read, in bytes; a longer one is malformed.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The most characters (what reads as one, an emoji included) the name shows; past it, its first ones and "…".</summary>
    public const int MaxNameLength = 40;

    private readonly ServiceClient? _client;
    private readonly ServiceCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    // What the menu reads, written only by the one publisher at a time (_publishing), read without a lock, so a menu never
    // waits for the store. Publications asked for (_requested) and the publisher's signal after each one (_published).
    // No lock of the holder is ever held while the publisher reads the credential's state: a change's notification can
    // come under the credential's lock (a change of address holds it through the settings' notification).
    private volatile Snapshot _shown;
    private long _requested;
    private int _publishing;
    private TaskCompletionSource _published = NewWake();

    // Under _lock: an ask is due at once (at start, after a change); when the next is due (null: only after a change); the
    // name and email known and the credential's generation they were asked with; what the last ask came to (for the log,
    // and a network change after a failure); the failed asks in a row since an answer or a change.
    private bool _pending = true;
    private long? _dueAt;
    private (long Generation, string Name, string? Email)? _known;
    private Kind? _last;
    private int _failures;

    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private long _requests;
    private long _asks;
    private volatile bool _waiting;

    /// <param name="client">How the requests go to the service; null: the engine has no service, and asks nothing.</param>
    /// <param name="credentials">The credential, followed so a change asks at once; never read here.</param>
    public ServiceHolder(ServiceClient? client, ServiceCredentials credentials, TimeProvider? time = null, ILogger? log = null)
    {
        _client = client;
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<ServiceHolder>();
        // Read here, before anything else can publish; then followed, and published again for a change made in between.
        _shown = Read(0);
        _credentials.Changed += CredentialChanged;
        RequestPublish(here: true);
    }

    /// <summary>
    /// Who holds the credential now: none held or refused, not held; held, its holder's name when known for it. Read
    /// without waiting for any lock (the menu reads it on the UI thread while a client's credential may be being written).
    /// </summary>
    public ServiceHolderState Current => _shown.State;

    /// <summary>
    /// What <see cref="Current"/> reads: the credential's state and generation, what shows for them, and the publications
    /// asked for that it covers (<see cref="Requested"/>: every one asked for up to it was asked before it was read).
    /// </summary>
    private sealed record Snapshot(ServiceCredentialState Credential, long Generation, ServiceHolderState State, long Requested);

    /// <summary>
    /// The credential's state now and the name known for its generation, as a snapshot covering <paramref name="requested"/>.
    /// The credential's lock is taken with no lock of the holder held, and the holder's own after it is let go.
    /// </summary>
    private Snapshot Read(long requested)
    {
        var (state, generation) = _credentials.StateAndGeneration;
        (long, string Name, string? Email)? shown;
        lock (_lock)
            shown = _known is { } known && known.Generation == generation ? known : null;
        return new Snapshot(state, generation,
            state == ServiceCredentialState.Present ? new ServiceHolderState(true, shown?.Name, shown?.Email) : new ServiceHolderState(false, null), requested);
    }

    /// <summary>
    /// Asks for what <see cref="Current"/> reads to be published again, after a change in effect now; returns the request's
    /// number. One publisher at a time publishes every request made: <paramref name="here"/>, this thread when none is
    /// publishing; else a thread of the pool. It never waits: a publisher running already publishes this request too.
    /// </summary>
    private long RequestPublish(bool here)
    {
        var request = Interlocked.Increment(ref _requested);
        if (Interlocked.CompareExchange(ref _publishing, 1, 0) == 0)
        {
            if (here)
                Pump();
            else
                ThreadPool.UnsafeQueueUserWorkItem(static holder => holder.PumpOnPool(), this, preferLocal: false);
        }

        return request;
    }

    /// <summary>
    /// The publisher (holding <see cref="_publishing"/>): publishes until every request made is covered, each publication
    /// reading the credential afresh after the last, so what shows is never older than what showed before it.
    /// </summary>
    private void Pump()
    {
        do
        {
            try
            {
                long upTo;
                while ((upTo = Interlocked.Read(ref _requested)) > _shown.Requested)
                {
                    _shown = Read(upTo);
                    Signal();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _publishing, 0);
                Signal();
            }

            // A request made while this publisher was letting go found it publishing: publish it, unless another has.
        } while (Interlocked.Read(ref _requested) > _shown.Requested && Interlocked.CompareExchange(ref _publishing, 1, 0) == 0);
    }

    private void PumpOnPool()
    {
        try
        {
            Pump();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Showing who holds the service credential failed; shown again at the next change");
        }
    }

    private void Signal() => Interlocked.Exchange(ref _published, NewWake()).TrySetResult();

    /// <summary>
    /// Asks for a publication and waits until one covering it is in <see cref="Current"/>: from the task only, which holds
    /// no lock, so it can wait for a publisher that waits for the credential's lock.
    /// </summary>
    private async Task PublishedAsync(CancellationToken ct)
    {
        var request = RequestPublish(here: true);
        while (_shown.Requested < request)
        {
            var signal = Volatile.Read(ref _published);
            if (Interlocked.CompareExchange(ref _publishing, 1, 0) == 0)
            {
                Pump();
                continue;
            }

            if (_shown.Requested < request)
                await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Requests sent to the service (an answer came, or the request failed after it was made). For tests and diagnostics.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Asks made, sent or refused before sending. For tests.</summary>
    internal long Asks => Interlocked.Read(ref _asks);

    /// <summary>Whether the task waits for a change or its time (no ask under way). For tests.</summary>
    internal bool IsWaiting => _waiting;

    /// <summary>Starts the task, which asks at once (a no-op when it runs).</summary>
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

        _credentials.Changed -= CredentialChanged;
    }

    /// <summary>
    /// What a name or email shows as: its control characters (C0, DEL, C1) and line or paragraph separators dropped, white
    /// space at its ends trimmed, past <see cref="MaxNameLength"/> characters its first ones and "…"; the name, else the
    /// email; null when neither has anything to show.
    /// </summary>
    internal static string? Shown(string? name, string email)
    {
        var shown = Clean(name);
        if (shown.Length == 0)
            shown = Clean(email);
        return Shortened(shown);
    }

    /// <summary>
    /// The email as it shows beside the name, cleaned and shortened as <see cref="Shown"/> does; null when there is no name
    /// to show (the email shows in its place) or the email has nothing to show.
    /// </summary>
    internal static string? ShownBeside(string? name, string email) => Clean(name).Length == 0 ? null : Shortened(Clean(email));

    private static string? Shortened(string shown)
    {
        if (shown.Length == 0)
            return null;
        var info = new StringInfo(shown);
        return info.LengthInTextElements <= MaxNameLength ? shown : info.SubstringByTextElements(0, MaxNameLength - 1) + "…";
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                kept.Append(c);
        }

        return kept.ToString().Trim();
    }

    // The credential or the address changed (provided, removed, refused, another address): an ask is due at once, and what
    // the menu reads is published again, never here: this can be raised under the credential's lock (a change of address
    // holds it through the settings' notification), while a publisher waits for that lock. A request under way finishes;
    // what it brings is for the credential it was sent with.
    private void CredentialChanged()
    {
        lock (_lock)
        {
            _pending = true;
            _failures = 0;
        }

        Volatile.Read(ref _wake).TrySetResult();
        RequestPublish(here: false);
    }

    /// <summary>
    /// The computer's network changed (the engine follows the system's address changes): after a failed ask, one is due at
    /// once and the retries start again at <see cref="FirstRetry"/>; otherwise nothing changes. Answers whether it asks.
    /// </summary>
    public bool NetworkChanged()
    {
        lock (_lock)
        {
            if (_last != Kind.Failed || _dueAt is null)
                return false;
            _pending = true;
            _failures = 0;
        }

        Volatile.Read(ref _wake).TrySetResult();
        return true;
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether an ask is due now (consumed), or the wait until one is (null: none until a change).</summary>
    private (bool Due, TimeSpan? Wait) TakeDue()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (_pending || (_dueAt is { } at && now >= at))
            {
                _pending = false;
                _dueAt = null;
                return (true, null);
            }

            return (false, _dueAt is { } due ? _time.GetElapsedTime(now, due) : null);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // A new wake before the schedule is read: a change made after the read completes it, so none is missed.
            var wake = NewWake();
            Volatile.Write(ref _wake, wake);
            try
            {
                var (due, wait) = TakeDue();
                if (due)
                {
                    await AskAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await WaitAsync(wake, wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never the end of the task: what failed is logged, and the next change of the credential or the address
                // asks again (a change made meanwhile has completed this wake already).
                _log.Error(ex, "Asking the configured service for the credential's holder failed; asking again when the credential or the service address changes");
                try
                {
                    await WaitAsync(wake, null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Waits for a change (<paramref name="wake"/>), or <paramref name="wait"/> when one is given.</summary>
    private async Task WaitAsync(TaskCompletionSource wake, TimeSpan? wait, CancellationToken ct)
    {
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

    /// <summary>What an ask came to.</summary>
    private enum Kind
    {
        /// <summary>The service named the holder.</summary>
        Named,

        /// <summary>No request made: no service, no credential, refused before, or offline.</summary>
        NotAsked,

        /// <summary>The service refused the credential (401).</summary>
        Refused,

        /// <summary>Unreachable, timed out, another status, or a malformed answer.</summary>
        Failed,
    }

    /// <summary>One ask, its outcome kept for the credential's generation it was sent with. A request's failure is an outcome: it throws when <paramref name="ct"/> is cancelled, or when the ask itself can't be made.</summary>
    private async Task AskAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _asks);
        // Read before the request takes the credential: the generation only grows, so when it is the same once the answer
        // is in, the request was sent with this one; when it moved, the answer is kept under this one and shown for none.
        var (_, generation) = _credentials.StateAndGeneration;
        Outcome outcome;
        using (var timeout = new CancellationTokenSource(RequestTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                outcome = await RequestAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unreachable, refused, timed out, cut off: the service didn't answer.
                outcome = new Outcome(Kind.Failed);
            }
        }

        bool again;
        var wait = Interval;
        lock (_lock)
        {
            switch (outcome.Kind)
            {
                case Kind.Named:
                    // A late answer never replaces what a later credential's answer brought.
                    if (_known is not { } known || known.Generation <= generation)
                        _known = (generation, outcome.Name!, outcome.Email);
                    _failures = 0;
                    _dueAt = _time.GetTimestamp() + Ticks(Interval);
                    break;
                case Kind.Failed:
                    // Soon, then less often: the retry's wait, or the service's Retry-After when longer, never past the
                    // interval (a service's wait of days would hold the next ask past what a wait can be).
                    wait = RetryWait(++_failures, outcome.RetryAfter);
                    _dueAt = _time.GetTimestamp() + Ticks(wait);
                    break;
                default:
                    // Nothing held to ask for, or refused: nothing kept, nothing asked until a change.
                    _known = null;
                    _failures = 0;
                    _dueAt = null;
                    break;
            }

            again = _last == outcome.Kind;
            _last = outcome.Kind;
        }

        await PublishedAsync(ct).ConfigureAwait(false);
        if (again)
            return;

        // Never the name, the email or the credential: what the ask came to only.
        switch (outcome.Kind)
        {
            case Kind.Named:
                _log.Information("The configured service named the credential's holder");
                break;
            case Kind.Refused:
                _log.Information("The configured service refused the credential when asked for its holder");
                break;
            case Kind.Failed:
                _log.Warning("The configured service didn't say who holds the credential; asking again in {Wait}", wait);
                break;
        }
    }

    /// <summary>
    /// The wait after <paramref name="failures"/> failed asks in a row: <see cref="FirstRetry"/>, doubled after each further
    /// one, or <paramref name="retryAfter"/> (the service's least wait) when it is longer; never past <see cref="Interval"/>.
    /// </summary>
    internal static TimeSpan RetryWait(int failures, TimeSpan? retryAfter)
    {
        var doublings = Math.Min(Math.Max(failures - 1, 0), 30);
        var wait = TimeSpan.FromTicks(Math.Min(FirstRetry.Ticks * (1L << doublings), Interval.Ticks));
        if (retryAfter is { } asked && asked > wait)
            wait = asked;
        return wait > Interval ? Interval : wait;
    }

    private long Ticks(TimeSpan wait) => (long)(wait.Ticks * (double)_time.TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>What an ask came to: the name and the email beside it (<see cref="Kind.Named"/>), the service's <c>Retry-After</c> (<see cref="Kind.Failed"/>).</summary>
    private readonly record struct Outcome(Kind Kind, string? Name = null, string? Email = null, TimeSpan? RetryAfter = null);

    private async Task<Outcome> RequestAsync(CancellationToken ct)
    {
        if (_client == null)
            return new Outcome(Kind.NotAsked);

        using var answer = await _client.SendAsync(HttpMethod.Get, Path, ct: ct).ConfigureAwait(false);
        if (answer.Refused is not null)
            return new Outcome(Kind.NotAsked);

        Interlocked.Increment(ref _requests);
        var response = answer.Response!;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new Outcome(Kind.Refused);
        if (response.StatusCode == HttpStatusCode.OK && await ReadAsync(response, ct).ConfigureAwait(false) is { } body && Parse(body) is { } holder)
            return new Outcome(Kind.Named, holder.Name, holder.Email);
        return new Outcome(Kind.Failed, RetryAfter: answer.RetryAfter);
    }

    /// <summary>
    /// The name to show and the email beside it from an answer <c>{"name": string | null, "email": string}</c> (a name
    /// left out reads as null), or null when it is malformed: not that object, the email not a string, or nothing to show.
    /// </summary>
    private static (string Name, string? Email)? Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String)
                return null;
            string? name = null;
            if (root.TryGetProperty("name", out var named))
            {
                if (named.ValueKind == JsonValueKind.String)
                    name = named.GetString();
                else if (named.ValueKind != JsonValueKind.Null)
                    return null;
            }

            var address = email.GetString()!;
            return Shown(name, address) is { } shown ? (shown, ShownBeside(name, address)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The answer's body, or null when it is longer than <see cref="MaxBytes"/> (refused unread past it).</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return read > MaxBytes ? null : buffer[..read];
    }
}
