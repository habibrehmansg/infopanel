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

    /// <summary>Streams open at once across the engine.</summary>
    public int MaxStreams { get; init; } = 16;

    /// <summary>Streams open at once among those one item asked for (in <see cref="IdleAfter"/>).</summary>
    public int MaxStreamsPerItem { get; init; } = 4;

    /// <summary>
    /// The bytes the open streams hold between them: each one's newest part waiting for a tick and the part it is reading.
    /// </summary>
    public long StreamBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>A stream no live tick asked for in this long is idle: the first to close when a limit is reached.</summary>
    public TimeSpan StreamIdleAfter { get; init; } = TimeSpan.FromSeconds(2);

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
    private readonly ITimer _expiry;
    private readonly long _start;
    /// <summary>When the sweep last ran; null until it has (a clock at zero is a time like any other).</summary>
    private long? _lastSweep;

    /// <summary>Tests: <see cref="Fetcher.Visits"/> of the fetchers dropped.</summary>
    private long _askerVisits;

    /// <summary>The bytes the streams hold: each fetcher's part waiting for a tick and each open stream's part being read.</summary>
    private long _streamBytes;
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
        // References no tick asks for expire on their own, with no lookup to notice them (a stream would read on).
        _expiry = _options.Time.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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

    /// <summary>Tests: the entries of the items asking for each address visited, dropped fetchers' included.</summary>
    internal long AskerVisits
    {
        get
        {
            lock (_lock) return _askerVisits + _fetchers.Values.Sum(f => f.Visits);
        }
    }

    /// <summary>Tests: the items noted as asking for <paramref name="reference"/>, asked lately or not.</summary>
    internal int AskersOf(string reference)
    {
        if (!TryAddress(reference, _options.MaxAddressLength, out var address, out _, out _)) return 0;
        lock (_lock) return _fetchers.TryGetValue(address, out var f) ? f.AskerCount : 0;
    }

    /// <summary>The bytes the streams hold between them (<see cref="HttpMediaProviderOptions.StreamBytes"/>).</summary>
    internal long StreamBytesHeld
    {
        get
        {
            lock (_lock) return _streamBytes;
        }
    }

    /// <summary>Tests: raised when a stream's part has been read whole, with the address.</summary>
    internal event Action<string>? PartReceived;

    /// <summary>Tests: raised when a fetch has to wait for a slot, with how many wait.</summary>
    internal event Action<int>? Waiting;

    /// <summary>
    /// Tests: awaited when a fetch has its outcome and before it is published, with the address and whether it brought
    /// an image's bytes.
    /// </summary>
    internal Func<string, bool, Task>? Publishing;

    private long Now() => (long)_options.Time.GetElapsedTime(_start).TotalMilliseconds;

    /// <summary>
    /// <see cref="Get(in MediaRequest)"/> by an item in a tick: every ask of the tick for an address shows what its first
    /// live ask chose (so a stream's part is taken once a tick, whatever the time and playback of the asks), and the item
    /// counts among those asking for the address (for the streams one item may keep open).
    /// </summary>
    public ProvidedMedia Get(in MediaRequest request, in MediaAsk ask) => Answer(request, ask.Tick, ask.Item);

    public ProvidedMedia Get(in MediaRequest request) => Answer(request, null, null);

    private ProvidedMedia Answer(in MediaRequest request, MediaTick? tick, object? item)
    {
        if (_disposed) return new ProvidedMedia(null, new MediaStatus("media.provider", "The provider is shut down."));
        if (!TryAddress(request.Reference, _options.MaxAddressLength, out var address, out var uri, out var credential))
            return new ProvidedMedia(null, new MediaStatus("media.provider", "Not a web address."));

        Fetcher? fetcher;
        string? key;
        string? pending;
        MediaStatus? status;
        (byte[] Part, long Fetch)? part = null;
        var chose = false;
        Choice? chosen = null;
        List<Fetcher>? dropped = null;
        List<OpenStream>? closed = null;
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
                // An item joining a stream other items opened: it counts among the item's streams.
                if (request.Live && item is not null && Joining(fetcher, item, now, ref closed) is { } over)
                    return new ProvidedMedia(null, over);
                // What the tick's first live ask for the address chose, which every later ask in the tick shows.
                chose = tick is not null && tick.TryShown(fetcher, out chosen);
                if (request.Live)
                {
                    fetcher.LastAsked = now;
                    if (item is not null) fetcher.Asker(item, now);
                    fetcher.Asked(request.Refresh, now, _options.IdleAfter);
                    if (!fetcher.Busy && now >= fetcher.DueAt(now, _options.IdleAfter))
                    {
                        fetcher.Busy = true;
                        _started++;
                        var started = fetcher;
                        _ = Task.Run(() => FetchAsync(started));
                    }
                    if (!chose) part = NextPart(fetcher);
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
            CancelClosed(closed);
        }

        if (chose && chosen is not null) return Chosen(chosen, request);
        var answer = Present(fetcher, request, part, key, pending, status, out var shown);
        // What this ask was answered, a picture or none: every later ask of the tick shows the same.
        if (tick is not null && request.Live)
            tick.Show(fetcher, new Choice(shown, answer.Sample, answer.Status, request.Seconds, request.Playback));
        return answer;
    }

    /// <summary>
    /// What the tick's first live ask for the fetcher was answered (<paramref name="chosen"/>), at this ask's time and
    /// playback: nothing is taken, decoded anew or replaced. No picture then is none now, though a decode finished
    /// meanwhile; a picture then is its media's frame at this ask's time, else that same picture; and a time and playback
    /// the tick answered already are answered the same picture, though a frame decoded meanwhile. The status is the first
    /// ask's.
    /// </summary>
    private ProvidedMedia Chosen(Choice chosen, in MediaRequest request)
    {
        if (chosen.Sample is not { } first || chosen.Key is not { } key) return new ProvidedMedia(null, chosen.Status);
        var at = (request.Seconds, request.Playback);
        lock (chosen.Answers)
        {
            // A picture let go for good meanwhile (no tick holds it) is looked up again.
            if (chosen.Answers.TryGetValue(at, out var answered) && answered?.Retain() is { } same)
                return new ProvidedMedia(same, chosen.Status);
            var sample = _cache.Get(key, request.Seconds, request.Playback).Sample ?? first.Retain();
            chosen.Answers[at] = sample;
            return sample is not null
                ? new ProvidedMedia(sample, chosen.Status)
                : new ProvidedMedia(null, chosen.Status ?? new MediaStatus("media.loading", "Decoding."));
        }
    }

    /// <summary>
    /// What a fetcher shows for an ask, given what the lock read (<paramref name="key"/>, <paramref name="pending"/>,
    /// <paramref name="status"/>) and the stream <paramref name="part"/> it took: a newer image once decoded, else the one
    /// shown. <paramref name="shown"/> is the media answered (or being decoded), null for none.
    /// </summary>
    private ProvidedMedia Present(Fetcher fetcher, in MediaRequest request, (byte[] Part, long Fetch)? part, string? key, string? pending,
        MediaStatus? status, out string? shown)
    {
        shown = null;
        // A stream's newest part, at most one a tick: it is decoded as a newer image fetched is.
        if (part is { } taken)
        {
            pending = TakePart(fetcher, taken.Part, taken.Fetch) ?? pending;
            lock (_lock) status = fetcher.Status;
        }

        // A newer image fetched: it replaces the one shown only once it is decoded, and one that can't be decoded never does.
        if (pending is not null)
        {
            var next = _cache.Get(pending, request.Seconds, request.Playback);
            if (next.Sample is { } decoded)
            {
                string? replaced = null;
                (byte[] Part, long Fetch)? after = null;
                lock (_lock)
                {
                    if (fetcher.Pending == pending)
                    {
                        // A stream keeps only the picture it shows: the one before goes, unless another address shows it.
                        if (fetcher.Stream && fetcher.Key is { } old && old != pending
                            && !_fetchers.Values.Any(f => f != fetcher && (f.Key == old || f.Pending == old)))
                            replaced = old;
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
                    // A stream's next part starts decoding now, when this tick took none: it shows from a later tick.
                    if (part is null && request.Live) after = NextPart(fetcher);
                }
                if (replaced is not null)
                {
                    _store.Remove(replaced);
                    _cache.Forget(replaced);
                }
                if (after is { } queued && TakePart(fetcher, queued.Part, queued.Fetch) is { } following)
                    _cache.Get(following, request.Seconds, request.Playback).Sample?.Release();
                shown = pending;
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
        if (lookup.Sample is { } sample)
        {
            shown = key;
            return new ProvidedMedia(sample, status);
        }
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
        shown = key;
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
    /// Another ask sharing this tick's answer for the reference (another node of the item): its refresh counts as asked, so
    /// the shortest among them applies from the next tick's ask. Starts nothing.
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
            foreach (var f in dropped) Drop(f);
            _fetchers.Clear();
        }
        foreach (var f in dropped) f.Stop();
    }

    /// <summary>
    /// The timer's sweep (<see cref="Sweep"/>), once a second: a reference no tick asked for expires whether or not any
    /// lookup comes, and its fetch under way, a stream's blocked read included, is stopped.
    /// </summary>
    private void Expire()
    {
        if (_disposed) return;
        List<Fetcher>? dropped;
        lock (_lock) dropped = Sweep(Now());
        if (dropped is not null) foreach (var f in dropped) f.Stop();
    }

    /// <summary>Marks <paramref name="f"/> dropped and gives back the stream bytes it holds. Under the lock.</summary>
    private void Drop(Fetcher f)
    {
        f.Dropped = true;
        _askerVisits += f.Visits;
        _streamBytes -= f.PartBytes;
        f.PartBytes = 0;
        f.Part = null;
        if (f.Open is { } open) Release(open);
    }

    /// <summary>Gives back what an open stream's part being read holds; it charges nothing more. Under the lock.</summary>
    private void Release(OpenStream stream)
    {
        stream.Released = true;
        _streamBytes -= stream.Reading;
        stream.Reading = 0;
    }

    /// <summary>
    /// Drops fetchers no tick asked for in <see cref="HttpMediaProviderOptions.IdleAfter"/>, and forgets the items that
    /// didn't ask for those kept in that time; at most once a second. Under the lock: the caller stops what it answers
    /// (<see cref="Fetcher.Stop"/>) once it has let the lock go.
    /// </summary>
    private List<Fetcher>? Sweep(long now)
    {
        if (_lastSweep is { } last && now - last < 1000) return null;
        _lastSweep = now;
        var idle = (long)_options.IdleAfter.TotalMilliseconds;
        List<Fetcher>? dropped = null;
        foreach (var (address, f) in _fetchers.ToList())
        {
            if (now - f.LastAsked < idle)
            {
                f.ForgetAskers(now - idle);
                continue;
            }
            Drop(f);
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
        var slot = new Slot(this);
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
            slot.Taken();
            bool dropped;
            // Dropped while it waited: it never starts.
            lock (_lock) dropped = fetcher.Dropped;
            outcome = dropped
                ? Outcome.Failed("media.provider", "Dropped.")
                : await FetchOnceAsync(fetcher, conditional, slot.Active(), stop.Token).ConfigureAwait(false);
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
            slot.Release();
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
            else if (outcome.Closed)
            {
                // A stream closed to keep within the streams' limits while no tick asked for it: no failure, and it opens
                // again as soon as a tick does.
                fetcher.Refetch = true;
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

    /// <summary>
    /// One fetch of the fetcher's address, following redirects, within the answer's time limit; or, when it answers with a
    /// stream, the stream until it ends (<see cref="StreamAsync"/>), which leaves its request <paramref name="slot"/> to
    /// other fetches once it starts.
    /// </summary>
    private async Task<Outcome> FetchOnceAsync(Fetcher fetcher, Validators conditional, Slot slot, CancellationToken stop)
    {
        using var timeout = new CancellationTokenSource(_options.AnswerTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stop);
        var token = linked.Token;
        var uri = fetcher.Uri;
        var streaming = false;
        // The reference's own user info, in a client of this fetch's: released with it, and never another reference's.
        using var own = fetcher.Credential is { } credential ? Client(new AddressCredential(fetcher.Uri, credential)) : null;
        var client = own ?? _client;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // Pictures, and the cameras' streams (some answer only a request that accepts their stream).
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("multipart/x-mixed-replace"));
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

                if (response.Content.Headers.ContentType is { } type
                    && string.Equals(type.MediaType, "multipart/x-mixed-replace", StringComparison.OrdinalIgnoreCase))
                {
                    if (MultipartStream.BoundaryOf(type) is not { } boundary)
                        return Outcome.Failed("media.provider", "The stream has no boundary.");
                    streaming = true;
                    slot.Release();
                    return await StreamAsync(fetcher, response, boundary, timeout, token).ConfigureAwait(false);
                }

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
            return Outcome.Failed("media.provider", streaming
                ? $"The stream sent nothing for {_options.AnswerTimeout.TotalSeconds:0} s."
                : $"The server took longer than {_options.AnswerTimeout.TotalSeconds:0} s.");
        }
        catch (MultipartStream.Problem problem)
        {
            return Outcome.Failed(problem.Code, problem.Message);
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
            return Outcome.Failed("media.provider", streaming ? $"The stream broke off: {e.Message}" : $"The answer broke off: {e.Message}");
        }
    }

    /// <summary>
    /// A <c>multipart/x-mixed-replace</c> answer (an MJPEG camera): read part by part while ticks ask for it, each part
    /// within <c>mediaBytes</c> and the newest kept for the next tick to take (<see cref="TakePart"/>), until it ends, breaks,
    /// or sends no part for the answer's time limit. It ends as a failed fetch: the last picture stays, and it is fetched
    /// again on the schedule of any failure (the refresh, or the retries).
    /// </summary>
    private async Task<Outcome> StreamAsync(Fetcher fetcher, HttpResponseMessage response, string boundary, CancellationTokenSource timeout, CancellationToken token)
    {
        using var close = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stream = new OpenStream(close);
        List<OpenStream>? closed = null;
        try
        {
            lock (_lock)
            {
                if (fetcher.Dropped) return Outcome.Failed("media.provider", "Dropped.");
                if (Admit(fetcher, Now(), ref closed) is { } refused) return refused;
                fetcher.Stream = true;
                fetcher.Open = stream;
            }
        }
        finally
        {
            CancelClosed(closed);
        }
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(close.Token).ConfigureAwait(false);
            var parts = new MultipartStream(body, boundary, _options.Limits.MediaBytes, bytes => Charge(fetcher, stream, bytes));
            while (true)
            {
                // Each part has the answer's time limit, from the one before.
                timeout.CancelAfter(_options.AnswerTimeout);
                var part = await parts.ReadPartAsync(close.Token).ConfigureAwait(false);
                if (part is null) return Outcome.Failed("media.provider", "The stream ended.");
                lock (_lock)
                {
                    // Closed or dropped meanwhile: the part is nobody's.
                    if (stream.Released) continue;
                    // The part read whole is now the one waiting for a tick, in place of any before it.
                    _streamBytes -= stream.Reading + fetcher.PartBytes;
                    stream.Reading = 0;
                    fetcher.Part = part;
                    fetcher.PartBytes = part.Length;
                    _streamBytes += part.Length;
                    fetcher.PartFetch = fetcher.Fetches;
                }
                PartReceived?.Invoke(fetcher.Address);
            }
        }
        catch (OperationCanceledException) when (stream.Closed && !token.IsCancellationRequested)
        {
            return Outcome.ClosedIdle;
        }
        finally
        {
            lock (_lock)
            {
                if (!stream.Released) Release(stream);
                if (fetcher.Open == stream) fetcher.Open = null;
            }
        }
    }

    /// <summary>
    /// Whether a new stream of <paramref name="fetcher"/> may open (spec/docs/providers.md, "Streams"): within the streams
    /// open at once among those each item asking for it asked for, then across the engine. Past a limit, the idle stream
    /// asked for longest ago closes (into <paramref name="closed"/>, cancelled once the lock is let go). An item with
    /// none idle is refused the stream alone: it is no longer among those asking, so its next ask joins as any other
    /// item's does (<see cref="Joining"/>), while the stream opens for the items with room. Refused for every item, or
    /// with none idle across the engine, the new one is refused. Under the lock.
    /// </summary>
    private Outcome? Admit(Fetcher fetcher, long now, ref List<OpenStream>? closed)
    {
        var open = _fetchers.Values.Where(f => f != fetcher && f.Open is { Released: false }).ToList();
        var recent = now - (long)_options.IdleAfter.TotalMilliseconds;
        var askers = fetcher.AskersSince(recent);
        List<object>? full = null;
        string? over = null;
        foreach (var item in askers)
        {
            if (RoomFor(item, open, now, ref closed) is not { } why) continue;
            (full ??= []).Add(item);
            over = why;
        }
        if (full is not null)
        {
            if (full.Count == askers.Count) return Outcome.Failed("media.budget", over!);
            foreach (var item in full) fetcher.ForgetAsker(item);
        }
        while (open.Count + 1 > Math.Max(1, _options.MaxStreams))
        {
            if (Idlest(open, now) is not { } idle)
                return Outcome.Failed("media.budget", $"Too many streams: {_options.MaxStreams} are open.");
            CloseStream(idle, ref closed);
            open.Remove(idle);
        }
        return null;
    }

    /// <summary>
    /// Room for one more stream among those <paramref name="item"/> asked for (in <see cref="HttpMediaProviderOptions.IdleAfter"/>),
    /// of the other <paramref name="open"/> streams: past the limit, its idle stream asked for longest ago closes (into
    /// <paramref name="closed"/>, and out of <paramref name="open"/>); with none idle, why there is no room. Under the lock.
    /// </summary>
    private string? RoomFor(object item, List<Fetcher> open, long now, ref List<OpenStream>? closed)
    {
        var recent = now - (long)_options.IdleAfter.TotalMilliseconds;
        var its = open.Where(f => f.AskedBy(item, recent)).ToList();
        while (its.Count + 1 > Math.Max(1, _options.MaxStreamsPerItem))
        {
            if (Idlest(its, now) is not { } idle) return $"Too many streams for one item: {_options.MaxStreamsPerItem} are open.";
            CloseStream(idle, ref closed);
            its.Remove(idle);
            open.Remove(idle);
        }
        return null;
    }

    /// <summary>
    /// When <paramref name="item"/> asks for an open stream it hasn't asked for lately (one other items opened): the stream
    /// counts among its own, so room is made for it as for one it opens (<see cref="Admit"/>); with none, its ask is
    /// answered <c>media.budget</c> and the stream stays open for the others. Null when it may join. Under the lock.
    /// </summary>
    private MediaStatus? Joining(Fetcher fetcher, object item, long now, ref List<OpenStream>? closed)
    {
        if (fetcher.Open is not { Released: false } || fetcher.AskedBy(item, now - (long)_options.IdleAfter.TotalMilliseconds)) return null;
        var open = _fetchers.Values.Where(f => f != fetcher && f.Open is { Released: false }).ToList();
        return RoomFor(item, open, now, ref closed) is { } over ? new MediaStatus("media.budget", over) : null;
    }

    /// <summary>The fetcher among <paramref name="candidates"/> no live tick asked for in the streams' idle time, asked for longest ago.</summary>
    private Fetcher? Idlest(IEnumerable<Fetcher> candidates, long now)
    {
        var idle = (long)_options.StreamIdleAfter.TotalMilliseconds;
        return candidates.Where(f => now - f.LastAsked >= idle).MinBy(f => f.LastAsked);
    }

    /// <summary>
    /// Counts <paramref name="bytes"/> more of the part <paramref name="stream"/> is reading against the streams' budget
    /// (<see cref="HttpMediaProviderOptions.StreamBytes"/>). Past it, idle streams close, the one asked for longest ago
    /// first, and the parts they hold go; with none idle left, the reading stream ends (<c>media.budget</c>).
    /// </summary>
    private void Charge(Fetcher fetcher, OpenStream stream, long bytes)
    {
        List<OpenStream>? closed = null;
        try
        {
            lock (_lock)
            {
                // Closed or dropped: its read is being stopped, and what it reads is nobody's.
                if (stream.Released) return;
                var max = _options.StreamBytes;
                while (_streamBytes + bytes > max)
                {
                    var holding = _fetchers.Values.Where(f => f != fetcher && (f.PartBytes > 0 || f.Open is { Reading: > 0 }));
                    if (Idlest(holding, Now()) is not { } idle)
                        throw new MultipartStream.Problem("media.budget", $"The streams are over their budget of {max} bytes.");
                    CloseStream(idle, ref closed);
                }
                _streamBytes += bytes;
                stream.Reading += bytes;
            }
        }
        finally
        {
            CancelClosed(closed);
        }
    }

    /// <summary>
    /// Closes <paramref name="fetcher"/>'s stream for the streams' limits: the part it holds goes, and its open stream, if
    /// any, stops reading (cancelled once the lock is let go, through <paramref name="closed"/>). The fetcher and the
    /// picture it shows stay. Under the lock.
    /// </summary>
    private void CloseStream(Fetcher fetcher, ref List<OpenStream>? closed)
    {
        _streamBytes -= fetcher.PartBytes;
        fetcher.PartBytes = 0;
        fetcher.Part = null;
        if (fetcher.Open is not { Released: false } open) return;
        open.Closed = true;
        Release(open);
        (closed ??= []).Add(open);
    }

    private static void CancelClosed(List<OpenStream>? closed)
    {
        if (closed is null) return;
        foreach (var s in closed) s.Cancel();
    }

    /// <summary>
    /// A stream's newest part, taken by a tick: inspected as an asset is and kept in the store, to be shown once decoded
    /// (it becomes the fetcher's pending image), and null when there is nothing new to decode. A part that isn't a JPEG is
    /// skipped; its refusal is the fetcher's status until a later part is shown, while its stream runs.
    /// </summary>
    private string? TakePart(Fetcher fetcher, byte[] part, long fetch)
    {
        var check = MediaValidator.Check(part, _options.Limits);
        MediaStatus? refused = null;
        if (check.Info is null || check.Errors.Count > 0)
        {
            var error = check.Errors.FirstOrDefault();
            refused = new MediaStatus(error?.Code ?? "media.format", error?.Message ?? "A part of the stream isn't a JPEG image.");
        }
        else if (check.Info.Format != MediaFormat.Jpeg)
        {
            refused = new MediaStatus("media.format", "A part of the stream isn't a JPEG image.");
        }
        if (refused is not null)
        {
            lock (_lock)
            {
                if (!fetcher.Dropped && fetch == fetcher.Fetches) fetcher.Status = refused;
            }
            return null;
        }
        var key = _store.Put(part, out var added);
        string? taken = null;
        lock (_lock)
        {
            if (fetcher.Dropped || fetcher.Pending is not null)
            {
            }
            else if (key == fetcher.Key)
            {
                if (fetch == fetcher.Fetches)
                {
                    fetcher.Status = null;
                    fetcher.Failures = 0;
                }
            }
            else
            {
                fetcher.Pending = key;
                fetcher.PendingFetch = fetch;
                taken = key;
            }
        }
        // Its bytes are back, or are new: a lookup that failed, or that read the store before they were there, is
        // forgotten, as a finite image fetched is (a picture decoded already is left alone).
        if (added || taken is not null) _cache.Retry(key);
        return taken;
    }

    /// <summary>
    /// The fetcher's newest stream part, when a live tick may take one: nothing else is waiting to be decoded. It leaves
    /// the streams' budget for the fetched store's. Under the lock; the caller takes it with <see cref="TakePart"/> once
    /// it has let the lock go.
    /// </summary>
    private (byte[] Part, long Fetch)? NextPart(Fetcher fetcher)
    {
        if (fetcher.Pending is not null || fetcher.Part is not { } part) return null;
        fetcher.Part = null;
        _streamBytes -= fetcher.PartBytes;
        fetcher.PartBytes = 0;
        return (part, fetcher.PartFetch);
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

    /// <summary>The handler's connect step: <see cref="AddressRules.ConnectAsync"/>, within the connect time limit.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
        try
        {
            return await AddressRules.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, _options.Resolve, _options.Connect,
                _options.LocalNetwork(), _options.LoopbackOnly, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new ConnectTimeoutException();
        }
    }

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
        _expiry.Dispose();
        _stop.Cancel();
        _client.Dispose();
    }

    private readonly record struct Validators(string? ETag, DateTimeOffset? LastModified)
    {
        public bool Any => ETag is not null || LastModified is not null;
    }

    /// <summary>
    /// A tick's first live answer for a fetcher: the media shown (<see cref="Key"/>, null for none), the picture answered
    /// (null for none: still decoding, or nothing to show) and the status; and the picture the tick answered at each time
    /// and playback asked (<see cref="Answers"/>, under its own lock), so asks alike are answered alike. The pictures
    /// aren't held: a later ask retains one only while its first holder still does.
    /// </summary>
    private sealed class Choice(string? key, MediaSample? sample, MediaStatus? status, double seconds, MediaPlayback playback)
    {
        public readonly string? Key = key;
        public readonly MediaSample? Sample = sample;
        public readonly MediaStatus? Status = status;
        public readonly Dictionary<(double Seconds, MediaPlayback Playback), MediaSample?> Answers = new() { [(seconds, playback)] = sample };
    }

    private sealed record Outcome(byte[]? Bytes, bool NotModified, Validators Validators, MediaStatus? Status, bool Closed = false)
    {
        public static Outcome Fetched(byte[] bytes, Validators validators) => new(bytes, false, validators, null);

        public static Outcome Unchanged(Validators validators) => new(null, true, validators, null);

        public static Outcome Failed(string code, string message) => new(null, false, default, new MediaStatus(code, message));

        /// <summary>A stream closed for the streams' limits while idle (<see cref="CloseStream"/>).</summary>
        public static readonly Outcome ClosedIdle = new(null, false, default, null, Closed: true);
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

        /// <summary>Whether the address answered with a stream: each image shown replaces the one before, which isn't kept.</summary>
        public bool Stream;

        /// <summary>A stream's newest part, read whole and not yet taken by a tick: a newer one replaces it.</summary>
        public byte[]? Part;

        /// <summary>The <see cref="Fetches"/> count when <see cref="Part"/> was read: the stream it came from.</summary>
        public long PartFetch;

        /// <summary><see cref="Part"/>'s bytes, counted in the streams' budget.</summary>
        public long PartBytes;

        /// <summary>The stream reading now, if any.</summary>
        public OpenStream? Open;

        // The items that asked for the address in live ticks (MediaAsk), each with when it last did.
        private readonly Dictionary<object, long> _askers = new(ReferenceEqualityComparer.Instance);

        /// <summary>Tests: the entries of <see cref="_askers"/> visited, each noted or looked at.</summary>
        public long Visits;

        /// <summary>The items noted, asked lately or not.</summary>
        public int AskerCount => _askers.Count;

        private IEnumerable<KeyValuePair<object, long>> Visit()
        {
            foreach (var p in _askers)
            {
                Visits++;
                yield return p;
            }
        }

        /// <summary>
        /// Notes <paramref name="item"/>'s live ask, looking at no other item: every item of a tick notes itself, so the
        /// items gone are forgotten by the sweep (<see cref="ForgetAskers"/>), not here.
        /// </summary>
        public void Asker(object item, long now)
        {
            _askers[item] = now;
            Visits++;
        }

        /// <summary>Forgets <paramref name="item"/>'s asks: its next ask is the first.</summary>
        public void ForgetAsker(object item) => _askers.Remove(item);

        /// <summary>Forgets the items that last asked before <paramref name="since"/>.</summary>
        public void ForgetAskers(long since)
        {
            List<object>? gone = null;
            foreach (var p in Visit())
                if (p.Value < since) (gone ??= []).Add(p.Key);
            if (gone is not null) foreach (var item in gone) _askers.Remove(item);
        }

        /// <summary>The items that asked for the address since <paramref name="since"/>.</summary>
        public List<object> AskersSince(long since) => [.. Visit().Where(p => p.Value >= since).Select(p => p.Key)];

        /// <summary>Whether <paramref name="item"/> asked for the address since <paramref name="since"/>.</summary>
        public bool AskedBy(object item, long since) => _askers.TryGetValue(item, out var at) && at >= since;

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

    /// <summary>
    /// A fetch's hold on one of the request slots (<see cref="HttpMediaProviderOptions.MaxRequests"/>) and its count among
    /// the fetches under way: given back when the fetch ends, or as soon as its answer turns out to be a stream.
    /// </summary>
    private sealed class Slot(HttpMediaProvider provider)
    {
        private int _held; // 0 none; 1 a slot; 2 a slot, and counted under way

        public void Taken() => _held = 1;

        public Slot Active()
        {
            _held = 2;
            InterlockedMax(ref provider._peak, Interlocked.Increment(ref provider._active));
            return this;
        }

        public void Release()
        {
            var held = Interlocked.Exchange(ref _held, 0);
            if (held == 2) Interlocked.Decrement(ref provider._active);
            if (held >= 1) provider._slots.Release();
        }
    }

    /// <summary>A stream reading, as the streams' limits see it. Its fields are under the provider's lock.</summary>
    private sealed class OpenStream(CancellationTokenSource close)
    {
        /// <summary>The bytes of the part being read, counted in the streams' budget.</summary>
        public long Reading;

        /// <summary>It counts for nothing any more (closed, dropped or ended): it charges nothing more.</summary>
        public bool Released;

        /// <summary>Closed for the streams' limits (<see cref="CloseStream"/>), not ended by its own answer.</summary>
        public volatile bool Closed;

        /// <summary>Stops its read; outside the provider's lock, since what the cancellation runs may take it.</summary>
        public void Cancel()
        {
            try
            {
                close.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }
    }

    /// <summary>A reference's user info as credentials, sent only to its address (Basic or Digest, as its server asks).</summary>
    private sealed class AddressCredential(Uri address, NetworkCredential credential) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            authType is "Basic" or "Digest" && uri.AbsoluteUri == address.AbsoluteUri ? credential : null;
    }

    private sealed class ConnectTimeoutException() : Exception("The connection timed out.");
}
