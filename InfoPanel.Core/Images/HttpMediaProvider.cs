using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Spec;
using Serilog;

namespace InfoPanel.Images;

/// <summary>The <see cref="HttpMediaProvider"/>'s budgets (spec/docs/limits.md, process budgets) and seams.</summary>
public sealed class HttpMediaProviderOptions
{
    /// <summary>The clock idling, refresh, retries and the time limits run on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The media limits an answer is held to (<c>mediaBytes</c> and the header's).</summary>
    public SpecLimits Limits { get; init; } = SpecLimits.Default;

    /// <summary>The <c>media.localNetwork</c> setting, read for every connection.</summary>
    public Func<bool> LocalNetwork { get; init; } = () => true;

    /// <summary>Connect only to this computer (an engine started offline: tests).</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>Resolves a host name to its addresses (the system's resolver).</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;

    /// <summary>Opens a TCP connection to an address the rules allowed (a socket).</summary>
    public Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Connect { get; init; } = HttpMediaProvider.ConnectSocketAsync;

    /// <summary>The <c>User-Agent</c> sent: the engine's name and version.</summary>
    public string UserAgent { get; init; } = "InfoPanel";

    /// <summary>A reference no tick asked for in this long is dropped.</summary>
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The whole answer, from the request to its last byte, every redirect included.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Fetches at once across the engine (one per address).</summary>
    public int MaxRequests { get; init; } = 4;

    public int MaxRedirects { get; init; } = 5;

    /// <summary>The longest address taken, in characters.</summary>
    public int MaxAddressLength { get; init; } = 2048;
}

/// <summary>
/// The <c>http</c> media provider (spec/docs/providers.md): web images by address. One fetcher per address, shared by
/// every item showing it, alive while ticks ask for it and dropped <see cref="HttpMediaProviderOptions.IdleAfter"/> after
/// the last ask; <see cref="Get"/> never blocks. Every connection resolves its host here and connects only to an address
/// <see cref="AddressRules"/> allowed (<see cref="SocketsHttpHandler.ConnectCallback"/>), so redirects and names whose
/// answer changes are held to the rules. Fetched bytes are inspected as assets are, kept in memory only
/// (<see cref="FetchedMediaStore"/>) and decoded by the engine's <see cref="MediaSampleCache"/>.
/// </summary>
public sealed class HttpMediaProvider : IMediaProvider, IDisposable
{
    public const string Name = "http";

    private static readonly ILogger Logger = Log.ForContext<HttpMediaProvider>();

    private readonly MediaSampleCache _cache;
    private readonly FetchedMediaStore _store;
    private readonly HttpMediaProviderOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Fetcher> _fetchers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _client;
    private readonly long _start;
    private long _lastSweep;
    private int _active;
    private int _peak;
    private int _waiting;
    private int _started;
    private volatile bool _disposed;

    public HttpMediaProvider(MediaSampleCache cache, FetchedMediaStore store, HttpMediaProviderOptions? options = null)
    {
        _cache = cache;
        _store = store;
        _options = options ?? new HttpMediaProviderOptions();
        _start = _options.Time.GetTimestamp();
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxRequests));
        _client = Client(null);
    }

    /// <summary>
    /// A client as every fetch makes its requests: no cookies, proxy, redirects followed by the handler or decompression,
    /// each connection through <see cref="ConnectAsync"/>; with <paramref name="credentials"/>, the one reference's that
    /// carries them (a client of its own, made and disposed with its fetch, so no other reference ever sends them).
    /// </summary>
    private HttpClient Client(ICredentials? credentials) => new(new SocketsHttpHandler
    {
        UseCookies = false,
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        Credentials = credentials,
        PreAuthenticate = false,
        ConnectCallback = ConnectAsync,
        MaxResponseHeadersLength = 64,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Raised after each fetch finished (answered, failed or refused), with the address; tests wait on it.</summary>
    public event Action<string>? Answered;

    /// <summary>Addresses with a fetcher.</summary>
    public int FetcherCount
    {
        get
        {
            lock (_lock) return _fetchers.Count;
        }
    }

    /// <summary>The most fetches that were under way at once.</summary>
    internal int PeakRequests => Volatile.Read(ref _peak);

    /// <summary>Fetches started.</summary>
    internal int FetchesStarted
    {
        get
        {
            lock (_lock) return _started;
        }
    }

    /// <summary>Tests: raised when a fetch has to wait for a slot, with how many wait.</summary>
    internal event Action<int>? Waiting;

    /// <summary>
    /// Tests: awaited when a fetch has its outcome and before it is published, with the address and whether it brought
    /// an image's bytes.
    /// </summary>
    internal Func<string, bool, Task>? Publishing;

    private long Now() => (long)_options.Time.GetElapsedTime(_start).TotalMilliseconds;

    public ProvidedMedia Get(in MediaRequest request)
    {
        if (_disposed) return new ProvidedMedia(null, new MediaStatus("media.provider", "The provider is shut down."));
        if (!TryAddress(request.Reference, _options.MaxAddressLength, out var address, out var uri, out var credential))
            return new ProvidedMedia(null, new MediaStatus("media.provider", "Not a web address."));

        Fetcher? fetcher;
        string? key;
        string? pending;
        MediaStatus? status;
        List<Fetcher>? dropped = null;
        try
        {
            lock (_lock)
            {
                var now = Now();
                dropped = Sweep(now);
                if (!_fetchers.TryGetValue(address, out fetcher))
                {
                    // A one-off render starts nothing: it shows what a live tick has fetched, else the placeholder.
                    if (!request.Live) return new ProvidedMedia(null, new MediaStatus("media.loading", "Not loaded yet."));
                    fetcher = new Fetcher(address, uri, credential, now);
                    _fetchers[address] = fetcher;
                }
                if (request.Live)
                {
                    fetcher.LastAsked = now;
                    fetcher.Asked(request.Refresh, now, _options.IdleAfter);
                    if (!fetcher.Busy && now >= fetcher.DueAt(now, _options.IdleAfter))
                    {
                        fetcher.Busy = true;
                        _started++;
                        var started = fetcher;
                        _ = Task.Run(() => FetchAsync(started));
                    }
                }
                key = fetcher.Key;
                pending = fetcher.Pending;
                status = fetcher.Status;
            }
        }
        finally
        {
            // Outside the lock: stopping a fetch may run what waits on it.
            if (dropped is not null) foreach (var f in dropped) f.Stop();
        }

        // A newer image fetched: it replaces the one shown only once it is decoded, and one that can't be decoded never does.
        if (pending is not null)
        {
            var next = _cache.Get(pending, request.Seconds, request.Playback);
            if (next.Sample is { } decoded)
            {
                lock (_lock)
                {
                    if (fetcher.Pending == pending)
                    {
                        fetcher.Key = pending;
                        fetcher.Pending = null;
                        // Its fetch's all-clear, unless a later fetch answered meanwhile: that one's outcome stands.
                        if (fetcher.PendingFetch == fetcher.Fetches)
                        {
                            fetcher.Status = null;
                            fetcher.Failures = 0;
                        }
                    }
                    status = fetcher.Status;
                }
                return new ProvidedMedia(decoded, status);
            }
            if (next.State == MediaState.Failed)
            {
                lock (_lock)
                {
                    if (fetcher.Pending == pending)
                    {
                        fetcher.Pending = null;
                        Undecodable(fetcher, pending, next.Problem);
                    }
                    status = fetcher.Status;
                }
                if (!_store.Contains(pending)) _cache.Retry(pending);
            }
        }

        if (key is null) return new ProvidedMedia(null, status ?? new MediaStatus("media.loading", "Loading."));
        var lookup = _cache.Get(key, request.Seconds, request.Playback);
        if (lookup.Sample is { } sample) return new ProvidedMedia(sample, status);
        if (lookup.State == MediaState.Failed)
        {
            lock (_lock)
            {
                if (fetcher.Key == key)
                {
                    fetcher.Key = null;
                    Undecodable(fetcher, key, lookup.Problem);
                }
                status = fetcher.Status;
            }
            if (!_store.Contains(key)) _cache.Retry(key);
            return new ProvidedMedia(null, status ?? new MediaStatus("media.loading", "Loading."));
        }
        return new ProvidedMedia(null, status ?? new MediaStatus("media.loading", "Decoding."));
    }

    /// <summary>
    /// The media cache couldn't decode <paramref name="key"/>, the fetcher's: when the store dropped its bytes, they are
    /// fetched again at once; otherwise the bytes can't be decoded, which is the fetcher's status (<paramref name="problem"/>,
    /// the cache's "code: message"), and it is fetched again on the schedule of any failure (refresh, or the retries).
    /// Under the lock.
    /// </summary>
    private void Undecodable(Fetcher fetcher, string key, string? problem)
    {
        fetcher.Validators = default;
        if (!_store.Contains(key))
        {
            fetcher.Refetch = true;
            return;
        }
        var colon = problem?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        fetcher.Status = colon > 0
            ? new MediaStatus(problem![..colon], problem[(colon + 2)..])
            : new MediaStatus("media.malformed", problem ?? "The image could not be decoded.");
        fetcher.Failures++;
    }

    /// <summary>
    /// Another item's ask sharing this tick's answer for the reference: its refresh counts as asked, so the shortest among
    /// the items applies from the next tick's ask. Starts nothing.
    /// </summary>
    public void Asked(in MediaRequest request)
    {
        if (_disposed || !request.Live || !TryAddress(request.Reference, _options.MaxAddressLength, out var address, out _, out _)) return;
        lock (_lock)
        {
            if (_fetchers.TryGetValue(address, out var fetcher)) fetcher.Asked(request.Refresh, Now(), _options.IdleAfter);
        }
    }

    /// <summary>
    /// Drops every fetcher, so each reference is fetched again on the next ask (the <c>media.localNetwork</c> setting
    /// changed: what was allowed may not be now, and the other way round). Fetches under way are stopped, and what they
    /// bring back isn't kept.
    /// </summary>
    public void Reset()
    {
        List<Fetcher> dropped;
        lock (_lock)
        {
            dropped = [.. _fetchers.Values];
            foreach (var f in dropped) f.Dropped = true;
            _fetchers.Clear();
        }
        foreach (var f in dropped) f.Stop();
    }

    /// <summary>
    /// Drops fetchers no tick asked for in <see cref="HttpMediaProviderOptions.IdleAfter"/>; at most once a second. Under
    /// the lock: the caller stops what it answers (<see cref="Fetcher.Stop"/>) once it has let the lock go.
    /// </summary>
    private List<Fetcher>? Sweep(long now)
    {
        if (now - _lastSweep < 1000 && _lastSweep != 0) return null;
        _lastSweep = now;
        var idle = (long)_options.IdleAfter.TotalMilliseconds;
        List<Fetcher>? dropped = null;
        foreach (var (address, f) in _fetchers.Where(p => now - p.Value.LastAsked >= idle).ToList())
        {
            f.Dropped = true;
            _fetchers.Remove(address);
            (dropped ??= []).Add(f);
        }
        return dropped;
    }

    private async Task FetchAsync(Fetcher fetcher)
    {
        Validators conditional;
        lock (_lock) conditional = fetcher.Key is null && fetcher.Pending is null ? default : fetcher.Validators;
        Outcome outcome;
        var waited = false;
        // Stopped when the provider shuts down or the fetcher is dropped (no tick asks for it, or the setting changed).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, fetcher.Stopped);
        try
        {
            if (!_slots.Wait(0))
            {
                // Every slot is taken: this fetch waits for one.
                Waiting?.Invoke(Interlocked.Increment(ref _waiting));
                try
                {
                    await _slots.WaitAsync(stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _waiting);
                }
            }
            waited = true;
            bool dropped;
            // Dropped while it waited: it never starts.
            lock (_lock) dropped = fetcher.Dropped;
            if (dropped)
            {
                outcome = Outcome.Failed("media.provider", "Dropped.");
            }
            else
            {
                var active = Interlocked.Increment(ref _active);
                InterlockedMax(ref _peak, active);
                try
                {
                    outcome = await FetchOnceAsync(fetcher, conditional, stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            outcome = Outcome.Failed("media.provider", "The provider is shut down.");
        }
        catch (OperationCanceledException) when (fetcher.Stopped.IsCancellationRequested)
        {
            outcome = Outcome.Failed("media.provider", "Dropped.");
        }
        catch (Exception e)
        {
            Logger.Warning(e, "Fetching a web image failed");
            outcome = Outcome.Failed("media.provider", "The image couldn't be fetched.");
        }
        finally
        {
            if (waited) _slots.Release();
        }

        if (Publishing is { } publishing) await publishing(fetcher.Address, outcome.Bytes is not null).ConfigureAwait(false);
        string? fresh = null;
        lock (_lock)
        {
            var now = Now();
            fetcher.Busy = false;
            fetcher.AnsweredAt = now;
            fetcher.Refetch = false;
            // A dropped fetcher's outcome is nobody's: nothing it fetched is kept.
            if (fetcher.Dropped)
            {
            }
            else if (outcome.Bytes is { } bytes)
            {
                // Shown once decoded (Get); the bytes are decoded afresh, so one that failed before is tried again.
                var key = _store.Put(bytes);
                fetcher.Fetches++;
                fetcher.Validators = outcome.Validators;
                fetcher.Status = null;
                if (key == fetcher.Key)
                {
                    fetcher.Pending = null;
                    fetcher.Failures = 0;
                }
                else
                {
                    fetcher.Pending = key;
                    fetcher.PendingFetch = fetcher.Fetches;
                    fresh = key;
                }
            }
            else if (outcome.NotModified)
            {
                fetcher.Fetches++;
                fetcher.Status = null;
                fetcher.Failures = 0;
                if (outcome.Validators.Any) fetcher.Validators = outcome.Validators;
            }
            else
            {
                // The last good image stays; the status says why the newest couldn't be had.
                fetcher.Fetches++;
                fetcher.Status = outcome.Status;
                fetcher.Failures++;
            }
        }
        if (fresh is not null) _cache.Retry(fresh);
        Answered?.Invoke(fetcher.Address);
    }

    /// <summary>One fetch of the fetcher's address, following redirects, within the answer's time limit.</summary>
    private async Task<Outcome> FetchOnceAsync(Fetcher fetcher, Validators conditional, CancellationToken stop)
    {
        using var timeout = new CancellationTokenSource(_options.AnswerTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stop);
        var token = linked.Token;
        var uri = fetcher.Uri;
        // The reference's own user info, in a client of this fetch's: released with it, and never another reference's.
        using var own = fetcher.Credential is { } credential ? Client(new AddressCredential(fetcher.Uri, credential)) : null;
        var client = own ?? _client;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                request.Headers.UserAgent.TryParseAdd(_options.UserAgent);
                // A new connection for every request, so each is made, and checked, under the rules in effect now.
                request.Headers.ConnectionClose = true;
                if (hop == 0 && conditional.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                    request.Headers.IfNoneMatch.Add(tag);
                if (hop == 0 && conditional.LastModified is { } modified)
                    request.Headers.IfModifiedSince = modified;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= _options.MaxRedirects)
                        return Outcome.Failed("media.provider", $"The server redirected more than {_options.MaxRedirects} times.");
                    if (response.Headers.Location is not { } location
                        || !TryRedirect(uri, location, _options.MaxAddressLength, out var next))
                        return Outcome.Failed("media.provider", "The server redirected to something that isn't a web address.");
                    uri = next;
                    continue;
                }
                if (code == 304 && hop == 0 && conditional.Any)
                    return Outcome.Unchanged(ValidatorsOf(response));
                if (code != 200)
                    return Outcome.Failed("media.provider", $"The server answered {code}.");

                var max = _options.Limits.MediaBytes;
                if (response.Content.Headers.ContentLength is { } length && length > max)
                    return Outcome.Failed("limit.mediaBytes", $"The image is over {max} bytes (mediaBytes).");
                var bytes = await ReadBoundedAsync(response.Content, max, token).ConfigureAwait(false);
                if (bytes is null)
                    return Outcome.Failed("limit.mediaBytes", $"The image is over {max} bytes (mediaBytes).");
                var check = MediaValidator.Check(bytes, _options.Limits);
                if (check.Info is null || check.Errors.Count > 0)
                {
                    var error = check.Errors.FirstOrDefault();
                    return Outcome.Failed(error?.Code ?? "media.format", error?.Message ?? "Not a supported image.");
                }
                return Outcome.Fetched(bytes, ValidatorsOf(response));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Outcome.Failed("media.provider", $"The server took longer than {_options.AnswerTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e) when (Refused(e) is { } refused)
        {
            return Outcome.Failed("media.address", refused.Message);
        }
        catch (HttpRequestException e) when (ConnectTimedOut(e))
        {
            return Outcome.Failed("media.provider", $"The server didn't answer within {_options.ConnectTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            return Outcome.Failed("media.provider", $"Couldn't connect: {Reason(e)}");
        }
        catch (IOException e)
        {
            return Outcome.Failed("media.provider", $"The answer broke off: {e.Message}");
        }
    }

    /// <summary>The body, counted as it is read: null as soon as it is over <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long max, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static Validators ValidatorsOf(HttpResponseMessage response) =>
        new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);

    /// <summary>
    /// The handler's connect step: resolves the host here (<c>localhost</c> and the names under it are this computer's
    /// addresses, never looked up), and connects to the first of its addresses the rules allow, within the connect time
    /// limit. A host none of whose addresses is allowed is refused, naming the first.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
        try
        {
            var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
            var isLiteral = IPAddress.TryParse(literal, out var ip);
            // Offline, a name other than this computer's isn't even looked up: the lookup would reach the network.
            if (!isLiteral && _options.LoopbackOnly && !IsThisComputer(host))
                throw new AddressRefusedException($"{host} is outside this computer, and the engine runs offline.");
            var addresses = isLiteral ? [ip!]
                : IsThisComputer(host) ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
                : await _options.Resolve(host, linked.Token).ConfigureAwait(false);
            var localNetwork = _options.LocalNetwork();
            string? refusal = null;
            foreach (var address in addresses)
            {
                var why = AddressRules.Refusal(address, localNetwork, _options.LoopbackOnly);
                if (why is null)
                    return await _options.Connect(new IPEndPoint(address, context.DnsEndPoint.Port), linked.Token).ConfigureAwait(false);
                refusal ??= why;
            }
            throw new AddressRefusedException(refusal ?? $"{host} has no address.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new ConnectTimeoutException();
        }
    }

    /// <summary><c>localhost</c> and the names under it (RFC 6761), which name this computer: 127.0.0.1 and ::1.</summary>
    private static bool IsThisComputer(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A TCP connection to <paramref name="endpoint"/>.</summary>
    public static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static AddressRefusedException? Refused(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is AddressRefusedException refused) return refused;
        return null;
    }

    private static bool ConnectTimedOut(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is ConnectTimeoutException) return true;
        return false;
    }

    private static string Reason(HttpRequestException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is SocketException socket) return socket.SocketErrorCode == SocketError.HostNotFound ? "the server's name wasn't found." : socket.Message;
        return e.Message;
    }

    /// <summary>
    /// Whether <paramref name="reference"/> is an address the provider takes: absolute <c>http</c> or <c>https</c>, trimmed,
    /// within <paramref name="maxLength"/> characters, with a host that has no <c>%</c>. <paramref name="address"/> is the
    /// trimmed reference (a fetcher's key), <paramref name="uri"/> it without its user info, which becomes
    /// <paramref name="credential"/>.
    /// </summary>
    internal static bool TryAddress(string reference, int maxLength, out string address, out Uri uri, out NetworkCredential? credential)
    {
        address = reference.Trim();
        uri = null!;
        credential = null;
        if (address.Length is 0 || address.Length > maxLength) return false;
        if (HostHasPercent(address) || !Uri.TryCreate(address, UriKind.Absolute, out var parsed) || !IsWebAddress(parsed)) return false;
        if (parsed.UserInfo.Length > 0)
        {
            var info = parsed.UserInfo;
            var colon = info.IndexOf(':');
            var user = Uri.UnescapeDataString(colon < 0 ? info : info[..colon]);
            var password = colon < 0 ? "" : Uri.UnescapeDataString(info[(colon + 1)..]);
            credential = new NetworkCredential(user, password);
            parsed = new UriBuilder(parsed) { UserName = "", Password = "" }.Uri;
        }
        uri = parsed;
        return true;
    }

    private static bool IsWebAddress(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0 && !uri.Host.Contains('%');

    /// <summary>Whether the host of an absolute address, as written, has a <c>%</c> (an escape, or an IPv6 zone).</summary>
    private static bool HostHasPercent(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;
        var rest = address.AsSpan(scheme + 3);
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        return (at < 0 ? authority : authority[(at + 1)..]).Contains('%');
    }

    /// <summary>A redirect's target: <paramref name="location"/> against <paramref name="from"/>, a web address without user info.</summary>
    private static bool TryRedirect(Uri from, Uri location, int maxLength, out Uri next)
    {
        next = location.IsAbsoluteUri ? location : new Uri(from, location);
        return (!location.IsAbsoluteUri || !HostHasPercent(location.OriginalString))
            && IsWebAddress(next) && next.UserInfo.Length == 0 && next.AbsoluteUri.Length <= maxLength;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _client.Dispose();
    }

    private readonly record struct Validators(string? ETag, DateTimeOffset? LastModified)
    {
        public bool Any => ETag is not null || LastModified is not null;
    }

    private sealed record Outcome(byte[]? Bytes, bool NotModified, Validators Validators, MediaStatus? Status)
    {
        public static Outcome Fetched(byte[] bytes, Validators validators) => new(bytes, false, validators, null);

        public static Outcome Unchanged(Validators validators) => new(null, true, validators, null);

        public static Outcome Failed(string code, string message) => new(null, false, default, new MediaStatus(code, message));
    }

    /// <summary>One address's state: its newest image, when to fetch next, why the last fetch failed. Under the provider's lock.</summary>
    private sealed class Fetcher(string address, Uri uri, NetworkCredential? credential, long created)
    {
        public readonly string Address = address;
        public readonly Uri Uri = uri;
        public readonly NetworkCredential? Credential = credential;
        private readonly CancellationTokenSource _stopped = new();
        public long LastAsked = created;
        public long? AnsweredAt;
        public bool Busy;
        public bool Dropped;
        public bool Refetch;
        /// <summary>The image shown: the newest that was decoded.</summary>
        public string? Key;

        /// <summary>A newer image fetched and not decoded yet: it becomes <see cref="Key"/> once it is.</summary>
        public string? Pending;

        /// <summary>Fetches answered (not dropped), counted as each is published.</summary>
        public long Fetches;

        /// <summary>
        /// The answered fetch <see cref="Pending"/> came from: while it is the latest, the image's showing clears the
        /// status; after a later fetch answered, that one's outcome stands.
        /// </summary>
        public long PendingFetch;
        public Validators Validators;
        public MediaStatus? Status;
        public int Failures;

        /// <summary>Cancelled when the fetcher is dropped: its fetch under way stops, and one waiting for a slot never starts.</summary>
        public CancellationToken Stopped => _stopped.Token;

        /// <summary>
        /// Stops a dropped fetcher's fetch (no tick asks for it any more, or the setting changed): outside the provider's
        /// lock, since what the cancellation runs may take it.
        /// </summary>
        public void Stop() => _stopped.Cancel();

        // The refreshes asked for recently, each with when it was last asked: the shortest applies.
        private readonly Dictionary<int, long> _refreshes = new();

        public void Asked(int refresh, long now, TimeSpan idle)
        {
            if (refresh > 0) _refreshes[refresh] = now;
            var window = (long)Math.Min(idle.TotalMilliseconds, 5000);
            foreach (var r in _refreshes.Where(p => now - p.Value > window).Select(p => p.Key).ToList()) _refreshes.Remove(r);
        }

        /// <summary>
        /// When the next fetch may start: now for the first one; else the shortest refresh after the last answer, or
        /// with none, never after an image (until it idles out) and 5, 10, 20 … 300 s after a failure.
        /// </summary>
        public long DueAt(long now, TimeSpan idle)
        {
            if (AnsweredAt is not { } answered || Refetch) return now;
            if (_refreshes.Count > 0) return answered + _refreshes.Keys.Min() * 1000L;
            if (Status is null && (Key ?? Pending) is not null) return long.MaxValue;
            var backoff = Math.Min(300, 5 * (1L << Math.Min(Math.Max(Failures - 1, 0), 6)));
            return answered + backoff * 1000L;
        }
    }

    /// <summary>A reference's user info as credentials, sent only to its address (Basic or Digest, as its server asks).</summary>
    private sealed class AddressCredential(Uri address, NetworkCredential credential) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            authType is "Basic" or "Digest" && uri.AbsoluteUri == address.AbsoluteUri ? credential : null;
    }

    /// <summary>A connection the address rules refused; its message names the address and why.</summary>
    private sealed class AddressRefusedException(string message) : Exception(message);

    private sealed class ConnectTimeoutException() : Exception("The connection timed out.");
}
