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
    private TaskCompletionSource _wake = NewWake();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lookups;
    private long _waits;
    private volatile bool _waiting;
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

    /// <summary>The family's last result: <see cref="AddressFamily.InterNetwork"/> or <see cref="AddressFamily.InterNetworkV6"/>.</summary>
    public Result Latest(AddressFamily family)
    {
        lock (_lock)
            return family == AddressFamily.InterNetworkV6 ? _v6 : _v4;
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

    /// <summary>Cancels a lookup in flight and waits for the task to end.</summary>
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

        // The task ends at the cancellation; a request in flight ends with the client.
        _client.Dispose();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A new wake before the schedule is read: a cause noted after the read completes it, so none is missed.
                var wake = NewWake();
                Volatile.Write(ref _wake, wake);
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
    }

    /// <summary>One lookup, both families together; the results kept, the schedule told.</summary>
    internal async Task LookUpAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);
        var v4 = FetchAsync(IPv4Endpoint, AddressFamily.InterNetwork, ct);
        var v6 = FetchAsync(IPv6Endpoint, AddressFamily.InterNetworkV6, ct);
        await Task.WhenAll(v4, v6).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        bool answered;
        lock (_lock)
        {
            var before4 = _v4.Answered;
            var before6 = _v6.Answered;
            _v4 = Next(_v4, v4.Result);
            _v6 = Next(_v6, v6.Result);
            answered = (_v4.Answered || _v6.Answered) && (_v4.Answered || !before4) && (_v6.Answered || !before6);
        }

        _schedule.LookedUp(_time.GetTimestamp(), answered);
        _log.Debug("Public IP: looked up; IPv4 {V4}, IPv6 {V6}", v4.Result != null ? "answered" : "no answer", v6.Result != null ? "answered" : "no answer");
        if (!answered && !_failing)
            _log.Warning("Public IP: the lookup failed; the public address readings go stale, then unavailable, until it answers (next in {Wait})", _schedule.Backoff);
        else if (answered && _failing)
            _log.Information("Public IP: the lookup answers again");
        _failing = !answered;
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
