using Serilog;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Network;

/// <summary>
/// This network's public addresses, looked up from ipify (<see cref="IPv4Endpoint"/>, <see cref="IPv6Endpoint"/>) on
/// a task of its own, when <see cref="PublicIpSchedule"/> says (only while a public-address reading is in use), so the
/// network source's poll only reads the last result (<see cref="Latest"/>) and never waits on the network.
/// <para>
/// A lookup asks for both families together and never overlaps another. Each request is bounded at
/// <see cref="RequestTimeout"/>, goes through a handler of its own (no redirects, no cookies, the system's proxy, no
/// user agent or other header of ours; nothing drained), and its body is read to at most <see cref="MaxBodyBytes"/> bytes and kept only
/// when it is an address of the family asked (a captive portal's page, or an IPv6 answer to the IPv4 ask, is a
/// failure). A lookup fails when no family answers, or a family that answered at the last lookup doesn't (so a
/// network without IPv6 doesn't count as failing for want of it); one log line when lookups start failing, one when
/// they answer again. <see cref="StopAsync"/> cancels a lookup in flight and waits for the task to end.
/// </para>
/// <para>
/// A switch (<see cref="SetEnabled"/>, the setting <c>sources.network.publicIp</c>) turns it off and on while it runs:
/// off cancels a lookup in flight, ends the task and forgets the addresses, so nothing is asked and nothing kept until it
/// is on again (an answer that comes back as it turns off is dropped); on starts the task again (once the cancelled one
/// has ended, so two never run) with a lookup due at once, which it takes while a public-address reading is in use.
/// </para>
/// </summary>
public sealed class PublicIpLookup : IDisposable
{
    public static readonly Uri IPv4Endpoint = new("https://api.ipify.org/");
    public static readonly Uri IPv6Endpoint = new("https://api6.ipify.org/");

    /// <summary>Each request's bound, connecting and reading the body included.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A body longer than this is refused unread past it (an address is at most 45 characters).</summary>
    public const int MaxBodyBytes = 64;

    /// <summary>One family's last result: the last address it answered (null before any) and whether the latest lookup answered.</summary>
    public readonly record struct Result(string? Address, bool Answered);

    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly PublicIpSchedule _schedule;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Result _v4;
    private Result _v6;
    private bool _failing;
    private volatile bool _enabled = true;
    private long _generation;
    // Whether the owner started the task (Start, until StopAsync): the switch runs it only then.
    private bool _started;
    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lookups;
    private long _waits;
    private volatile bool _waiting;
    private int _running;
    private int _mostRunning;
    private bool _disposed;

    /// <param name="handler">Where the requests go: <see cref="CreateHandler"/>, or a test's. Disposed with the lookup.</param>
    public PublicIpLookup(HttpMessageHandler handler, TimeProvider? time = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<PublicIpLookup>();
        _schedule = new PublicIpSchedule(_time);
        // Our own bound per request (RequestTimeout, on the time provider), not HttpClient's.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The lookup over the system's network.</summary>
    public static PublicIpLookup CreateSystem(TimeProvider? time = null, ILogger? log = null) => new(CreateHandler(), time, log);

    /// <summary>
    /// The handler the lookup's requests go through: no redirects followed, no cookies kept or sent, the system's proxy,
    /// no decompression (so nothing asks for an encoding), connections bounded as the requests are, and no response
    /// drained: a body refused unread (past <see cref="MaxBodyBytes"/>, or behind another status than 200) closes its
    /// connection rather than being read out for reuse (the handler's default drains up to 1 MiB).
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseDrainSize = 0,
    };

    /// <summary>When lookups are due.</summary>
    public PublicIpSchedule Schedule => _schedule;

    /// <summary>Lookups taken since creation. For tests and diagnostics.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    /// <summary>How many times the task has begun waiting. For tests.</summary>
    internal long Waits => Interlocked.Read(ref _waits);

    /// <summary>Whether the task waits for a cause or its time (no lookup under way). For tests.</summary>
    internal bool IsWaiting => _waiting;

    /// <summary>How many tasks run now, and the most that ever ran at once. For tests.</summary>
    internal int Running => Volatile.Read(ref _running);

    internal int MostRunning => Volatile.Read(ref _mostRunning);

    /// <summary>Awaited by the task each time just before it takes a lookup that is due. For tests: a pause there.</summary>
    internal Func<Task>? BeforeTakeDue { get; set; }

    /// <summary>The family's last result: <see cref="AddressFamily.InterNetwork"/> or <see cref="AddressFamily.InterNetworkV6"/>.</summary>
    public Result Latest(AddressFamily family)
    {
        lock (_lock)
            return family == AddressFamily.InterNetworkV6 ? _v6 : _v4;
    }

    /// <summary>
    /// How many times lookups were turned off: a reader of <see cref="Latest"/> that saw another count holds a result
    /// that was forgotten since (<see cref="NetworkSampler"/>).
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Whether lookups are on (the setting <c>sources.network.publicIp</c>); on until <see cref="SetEnabled"/> turns them off.</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Turns lookups off or on, at once. Off: a lookup in flight is cancelled, the task ends without waiting for it here,
    /// and both families' results are forgotten. On: once started, the task runs again, with a lookup due at once (not
    /// after the 30 minutes or a back-off), taken while a public-address reading is in use. The same value again does
    /// nothing.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _lifecycle.Wait();
        try
        {
            if (_disposed || _enabled == enabled)
                return;
            _enabled = enabled;
            if (!enabled)
            {
                // Cancelled first, then forgotten: a lookup that ends meanwhile sees the cancellation under the lock and keeps nothing.
                _cts?.Cancel();
                lock (_lock)
                {
                    _v4 = default;
                    _v6 = default;
                    _failing = false;
                    Interlocked.Increment(ref _generation);
                }

                return;
            }

            if (_started)
                StartLoop(restart: true);
            else
                _schedule.Restart();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>A public-address reading was read: when that brings it into use, a lookup runs at once. Allocates nothing while it stays in use.</summary>
    public void NoteUse()
    {
        if (_schedule.NoteUse(_time.GetTimestamp()))
            Wake();
    }

    /// <summary>The network changed: while a public-address reading is in use, a lookup runs at once.</summary>
    public void NetworkChanged()
    {
        if (_schedule.NetworkChanged(_time.GetTimestamp()))
            Wake();
    }

    /// <summary>Starts the task (a no-op when it runs); while lookups are off, only once they are turned on.</summary>
    public void Start()
    {
        _lifecycle.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _started = true;
            if (_enabled)
                StartLoop(restart: false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Under _lifecycle: a task runs, after the one before it (cancelled by the switch) has ended, unless one runs already.
    // With restart (the switch turned on), the schedule starts afresh (PublicIpSchedule.Restart) only once the task before
    // has ended: that one, ending at its cancellation, can't take the lookup due at once and never ask it. And only while
    // lookups stay on since (the generation unchanged, the new task not cancelled): a later turn on restarts it again.
    private void StartLoop(bool restart)
    {
        if (_loop is { IsCompleted: false } && !_cts!.IsCancellationRequested)
            return;
        var before = _loop is { IsCompleted: false } ? _loop : null;
        if (before == null)
            _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        if (before == null)
        {
            if (restart)
                _schedule.Restart();
            _loop = Task.Run(() => RunAsync(token));
            return;
        }

        var generation = Generation;
        _loop = Task.Run(async () =>
        {
            await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (restart && Generation == generation && !token.IsCancellationRequested)
                _schedule.Restart();
            await RunAsync(token).ConfigureAwait(false);
        });
    }

    /// <summary>Cancels a lookup in flight and waits for the task to end.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _started = false;
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

        // The task ends at the cancellation; a request in flight ends with the client.
        _client.Dispose();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        var running = Interlocked.Increment(ref _running);
        for (var most = Volatile.Read(ref _mostRunning); running > most; most = Volatile.Read(ref _mostRunning))
        {
            if (Interlocked.CompareExchange(ref _mostRunning, running, most) == most)
                break;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A new wake before the schedule is read: a cause noted after the read completes it, so none is missed.
                var wake = NewWake();
                Volatile.Write(ref _wake, wake);
                if (BeforeTakeDue is { } pause)
                    await pause().ConfigureAwait(false);
                if (_schedule.TakeDue(_time.GetTimestamp()))
                {
                    await LookUpAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var wait = _schedule.WaitFrom(_time.GetTimestamp());
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
            _log.Error(ex, "Public IP: the lookup task failed");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    /// <summary>One lookup, both families together; the results kept, the schedule told.</summary>
    internal async Task LookUpAsync(CancellationToken ct)
    {
        // Nothing is asked while off, or once cancelled.
        if (!_enabled)
            return;
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _lookups);
        var v4 = FetchAsync(IPv4Endpoint, AddressFamily.InterNetwork, ct);
        var v6 = FetchAsync(IPv6Endpoint, AddressFamily.InterNetworkV6, ct);
        await Task.WhenAll(v4, v6).ConfigureAwait(false);

        bool answered, wasFailing;
        lock (_lock)
        {
            // Turned off (or stopped) since: the answer is dropped, so an address forgotten stays forgotten.
            ct.ThrowIfCancellationRequested();
            if (!_enabled)
                return;
            var before4 = _v4.Answered;
            var before6 = _v6.Answered;
            _v4 = Next(_v4, v4.Result);
            _v6 = Next(_v6, v6.Result);
            answered = (_v4.Answered || _v6.Answered) && (_v4.Answered || !before4) && (_v6.Answered || !before6);
            _schedule.LookedUp(_time.GetTimestamp(), answered);
            wasFailing = _failing;
            _failing = !answered;
        }

        _log.Debug("Public IP: looked up; IPv4 {V4}, IPv6 {V6}", v4.Result != null ? "answered" : "no answer", v6.Result != null ? "answered" : "no answer");
        if (!answered && !wasFailing)
            _log.Warning("Public IP: the lookup failed; the public address readings go stale, then unavailable, until it answers (next in {Wait})", _schedule.Backoff);
        else if (answered && wasFailing)
            _log.Information("Public IP: the lookup answers again");
    }

    // An address unchanged since the last answer is handed back as the same string.
    private static Result Next(Result last, string? address) =>
        address == null ? last with { Answered = false } : new Result(address == last.Address ? last.Address : address, true);

    /// <summary>The address <paramref name="endpoint"/> answers, of <paramref name="family"/>; null on any failure. Throws only when <paramref name="ct"/> is cancelled.</summary>
    internal async Task<string?> FetchAsync(Uri endpoint, AddressFamily family, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var buffer = new byte[MaxBodyBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(read), linked.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                read += n;
            }

            return read > MaxBodyBytes ? null : Parse(Encoding.ASCII.GetString(buffer, 0, read), family);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreachable, refused, timed out, a TLS failure: this family didn't answer.
            return null;
        }
    }

    /// <summary>
    /// The address in a body, when it is one of <paramref name="family"/> and nothing else (white space around it
    /// aside): IPv4 as four decimal parts, IPv6 without a zone; null otherwise.
    /// </summary>
    public static string? Parse(string body, AddressFamily family)
    {
        var text = body.Trim();
        if (text.Length == 0 || text.Contains('%') || !IPAddress.TryParse(text, out var address) || address.AddressFamily != family)
            return null;
        var canonical = address.ToString();
        // IPAddress also reads "1", "0x7f.1" and "010.0.0.1" as IPv4: only the dotted quad is an answer.
        if (family == AddressFamily.InterNetwork && canonical != text)
            return null;
        return canonical;
    }
}
