using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using InfoPanel.Sensors.NowPlaying;

namespace InfoPanel.Images;

/// <summary>
/// The <c>now-playing</c> media provider (spec/docs/providers.md): the cover art of what this computer plays, as the
/// now-playing source read it (<see cref="NowPlayingArt"/>). A source's <c>ref</c> is the <c>art</c> reading's text, a
/// token naming one of the covers the source keeps; any other reference, and every reference in an engine without the
/// source, is answered nothing with <c>media.provider</c>, "No cover art.". It fetches nothing and connects nowhere: a
/// cover's bytes are the reader's, kept in memory in the fetched media's store and decoded by the engine's
/// <see cref="MediaSampleCache"/> as fetched media are, under the media limits. A cover a player gives as a web address
/// is asked of the web image provider (<c>http</c>) for that address, under its rules, as a private address (its fetch
/// logs neither it nor a message that could name it) and with no refresh.
/// <para>
/// The source's covers own their bytes: the store holds a cover's buffer itself (no copy) only while the source keeps
/// the cover, under a key of the provider's own (<see cref="KeyPrefix"/>, so nothing it removes is another provider's),
/// and a cover the source lets go of (pushed out by a newer one, or every cover when it stops reading) leaves the store
/// and the cache with it. A token no tick asked for in <see cref="IdleAfter"/> leaves them sooner (it is put back when
/// asked again); a one-off ask (a thumbnail, a held picture) is answered as a tick's ask is, decoding a cover it hasn't
/// yet, but keeps nothing alive. Admitting, dropping and expiring each run under one lock, so none undoes another; a
/// cover's bytes are taken from the source only under that lock (an ask paused on its way holds none), and an ask's
/// lookup, made after it admitted the cover, is kept only while that admission stands (else dropped, with the cache
/// entry it made).
/// </para>
/// <para>
/// What a cover's failure says is its code and a fixed text (<see cref="Failed"/>), never a message that could carry
/// what a player gave (an address's host, an SVG's own ids). It logs nothing, and its covers are decoded as private
/// media (a decode's failure logs neither the key nor the exception's message): a token names the cover of what plays.
/// </para>
/// </summary>
public sealed class NowPlayingProvider : IMediaProvider, IDisposable
{
    public const string Name = "now-playing";

    /// <summary>A token no tick asked for this long is dropped.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);

    /// <summary>What every key of its own in the store and the cache starts with; no other provider's key does.</summary>
    internal const string KeyPrefix = FetchedMediaStore.Prefix + "now-playing:";

    /// <summary>The text of every cover's failure, whatever its code.</summary>
    internal const string Failed = "Couldn't load the cover art.";

    private static readonly MediaStatus NoArt = new("media.provider", "No cover art.");
    private static readonly MediaStatus Decoding = new("media.loading", "Decoding.");
    private static readonly MediaStatus Loading = new("media.loading", "Loading the cover art.");
    private static readonly MediaStatus ShutDown = new("media.provider", "The provider is shut down.");

    private readonly NowPlayingArt? _art;
    private readonly MediaSampleCache _cache;
    private readonly FetchedMediaStore _store;
    private readonly IMediaProvider? _web;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    /// <summary>
    /// The covers it put in the store, by token: the store's key, when a tick last asked, and the admission that put it
    /// there (an ask's lookup is kept only while its admission stands).
    /// </summary>
    private readonly Dictionary<string, (string Key, long LastAsked, long Admission)> _held = new(StringComparer.Ordinal);

    /// <summary>Admissions made; under the lock.</summary>
    private long _admissions;
    private readonly ITimer _expiry;

    /// <summary>Set under the lock; read outside it only as a shortcut.</summary>
    private volatile bool _disposed;

    /// <param name="art">The now-playing source's covers; null in an engine without the source (mock mode, a replay).</param>
    /// <param name="cache">The engine's media cache, which decodes the covers from <paramref name="store"/>.</param>
    /// <param name="store">The fetched media's store, where a cover's bytes are kept while it is asked for.</param>
    /// <param name="web">The provider a cover given as a web address is asked of (<c>http</c>); none leaves such covers out.</param>
    /// <param name="time">The clock that times the tokens no tick asks for.</param>
    public NowPlayingProvider(NowPlayingArt? art, MediaSampleCache cache, FetchedMediaStore store, IMediaProvider? web = null, TimeProvider? time = null)
    {
        _art = art;
        _cache = cache;
        _store = store;
        _web = web;
        _time = time ?? TimeProvider.System;
        // A cover the source lets go of leaves the store and the cache with it.
        if (_art is not null) _art.Dropped += Drop;
        // Tokens no tick asks for are dropped on the clock: once nothing asks, no lookup would notice them.
        _expiry = _time.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Tests: run by an ask past its first check, before it admits a cover.</summary>
    internal Action? Admitting;

    /// <summary>Tests: run by an ask once it admitted a cover, before the cache's lookup.</summary>
    internal Action? Admitted;

    /// <summary>
    /// Tests: run by the expiry for each token it drops, under its lock, once it has let go of the token's bytes and
    /// before it forgets its frames: where an ask that renewed the token was once undone.
    /// </summary>
    internal Action? Expired;

    /// <summary>The store's and the cache's key of the cover whose bytes have the SHA-256 <paramref name="sha256"/>.</summary>
    internal static string KeyOf(string sha256) => KeyPrefix + "sha256:" + sha256;

    /// <summary>The covers whose bytes it keeps in the store.</summary>
    internal int Held
    {
        get
        {
            lock (_lock) return _held.Count;
        }
    }

    public ProvidedMedia Get(in MediaRequest request) => Answer(request, null);

    public ProvidedMedia Get(in MediaRequest request, in MediaAsk ask) => Answer(request, ask);

    /// <summary>Another ask sharing the tick's answer: told on to the web image provider for a cover given as an address.</summary>
    public void Asked(in MediaRequest request)
    {
        if (!_disposed && _web is not null && _art is not null && _art.Keeps(request.Reference, out var address) && address is not null)
            _web.Asked(Forwarded(request, address));
    }

    /// <summary>
    /// The web image provider's request for a cover at <paramref name="address"/>: a private address, with no refresh
    /// (it doesn't apply to a cover: a new cover is a new token).
    /// </summary>
    private static MediaRequest Forwarded(in MediaRequest request, string address) =>
        request with { Reference = address, Refresh = 0, Private = true };

    private ProvidedMedia Answer(in MediaRequest request, MediaAsk? ask)
    {
        if (_disposed) return new ProvidedMedia(null, ShutDown);
        // Whether the source keeps the token, and its address: nothing holding a cover's bytes is taken outside the lock,
        // so an ask paused on its way keeps none alive while the source pushes the cover out.
        if (_art is null || !_art.Keeps(request.Reference, out var address)) return new ProvidedMedia(null, NoArt);
        if (address is not null)
        {
            if (_web is null) return new ProvidedMedia(null, NoArt);
            var web = Forwarded(request, address);
            return Covered(ask is { } by ? _web.Get(web, by) : _web.Get(web));
        }

        Admitting?.Invoke();
        var token = request.Reference;
        if (Admit(token, request.Live, out var key, out var admission) is { } refused) return new ProvidedMedia(null, refused);

        Admitted?.Invoke();
        var lookup = _cache.Get(key, request.Seconds, request.Playback, privateMedia: true);
        lock (_lock)
        {
            // Let go of since it was admitted (dropped, expired, shut down; perhaps admitted afresh meanwhile): what this
            // lookup made of it is dropped, and the cache's entry it may have made again goes too unless a newer
            // admission holds the cover.
            if (_disposed || !_held.TryGetValue(token, out var held) || held.Admission != admission)
            {
                lookup.Sample?.Release();
                var current = !_disposed && _held.ContainsKey(token);
                if (!current) _cache.Forget(key);
                return new ProvidedMedia(null, _disposed ? ShutDown : current ? Decoding : NoArt);
            }
        }
        if (lookup.Sample is { } sample) return new ProvidedMedia(sample);
        if (lookup.State != MediaState.Failed || !_store.Contains(key)) return new ProvidedMedia(null, Decoding);
        // Refused by the media limits, or not decodable: the cache's code ("code: message"), with the cover's own text.
        var problem = lookup.Problem ?? "";
        var colon = problem.IndexOf(": ", StringComparison.Ordinal);
        return new ProvidedMedia(null, new MediaStatus(colon > 0 ? problem[..colon] : "media.malformed", Failed));
    }

    /// <summary>
    /// Admits the cover the source keeps under <paramref name="token"/>, under the lock: noted as held (its
    /// <paramref name="admission"/>, a new one unless it is held already), and its bytes in the store under
    /// <paramref name="key"/>. Null when admitted, else why not: shut down, or the cover left the source since it was
    /// looked up (its drop has run, finding nothing of it). The cover's bytes are taken from the source here only, under
    /// the lock the source's drop waits for, and held by nothing of the ask's once it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private MediaStatus? Admit(string token, bool live, out string key, out long admission)
    {
        key = "";
        admission = 0;
        lock (_lock)
        {
            if (_disposed) return ShutDown;
            if (!_art!.TryGet(token, out var picture) || picture.Bytes is not { } bytes) return NoArt;
            key = KeyOf(picture.Sha256!);
            var now = _time.GetTimestamp();
            if (!_held.TryGetValue(token, out var held))
                held = (key, now, ++_admissions);
            else if (live)
                held.LastAsked = now;
            _held[token] = held;
            admission = held.Admission;
            // Its bytes in the store while it is asked for (again, when the store let them go): the source's own buffer.
            if (!_store.Contains(key))
            {
                _store.Put(key, bytes, out _);
                // Bytes back in the store: a lookup that failed for want of them is forgotten, and decoded afresh.
                _cache.Retry(key);
            }
            return null;
        }
    }

    /// <summary>
    /// The web image provider's answer for a cover, as the cover's: its picture, and its status's code with the
    /// provider's own text (the web provider's could name the address's host).
    /// </summary>
    private static ProvidedMedia Covered(ProvidedMedia answer) => answer.Status is { } status
        ? answer with { Status = status.Code == "media.loading" ? Loading : new MediaStatus(status.Code, Failed) }
        : answer;

    /// <summary>The source let go of <paramref name="token"/>'s cover: its bytes leave the store, its frames the cache.</summary>
    private void Drop(string token)
    {
        lock (_lock)
        {
            if (_held.Remove(token, out var held)) Release(held.Key);
        }
    }

    /// <summary>Removes <paramref name="key"/>'s bytes from the store and its frames from the cache. Under the lock.</summary>
    private void Release(string key)
    {
        _store.Remove(key);
        _cache.Forget(key);
    }

    /// <summary>The timer's sweep: the tokens no tick asked for in <see cref="IdleAfter"/> are dropped.</summary>
    private void Expire()
    {
        if (_disposed) return;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            List<string>? dropped = null;
            foreach (var (token, held) in _held)
            {
                if (_time.GetElapsedTime(held.LastAsked, now) >= IdleAfter)
                    (dropped ??= []).Add(token);
            }
            if (dropped is null) return;
            // Forgotten under the lock too: an ask renewing a token waits for its drop to finish, never the other way.
            foreach (var token in dropped)
            {
                var key = _held[token].Key;
                _held.Remove(token);
                _store.Remove(key);
                Expired?.Invoke();
                _cache.Forget(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var held in _held.Values) Release(held.Key);
            _held.Clear();
        }
        if (_art is not null) _art.Dropped -= Drop;
        _expiry.Dispose();
    }
}
