using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SkiaSharp;

namespace InfoPanel.Images;

/// <summary>
/// A media provider that draws the pictures a sensor source keeps (<see cref="IKeptPictures"/>), each by its image
/// sensor's reference (spec/docs/providers.md: <c>now-playing</c>, <c>plugin</c>): a source's <c>ref</c> is the reading's
/// text, which a <c>text</c> input bound to the sensor reads. A reference the source doesn't keep, and every reference
/// when there is no source, is answered with no picture (<c>media.provider</c> and the provider's words): a sample that
/// draws nothing, so the item is transparent rather than the missing-media placeholder, which a picture that fails still
/// draws. It fetches nothing and connects nowhere: a picture's bytes are the source's, kept in memory in the fetched
/// media's store and decoded by the engine's <see cref="MediaSampleCache"/> as fetched media are, under the media limits.
/// A picture the source gives as a web address is asked of the web image provider (<c>http</c>) for that address, under
/// its rules, as a private address (its fetch logs neither it nor a message that could name it) and with no refresh.
/// <para>
/// The source's pictures own their bytes: the store holds a picture's buffer itself (no copy) only while the source keeps
/// the picture, under a key of the provider's own (so nothing it removes is another provider's), and a picture the source
/// lets go of leaves the store and the cache with it (at once when the source tells it, <see cref="Drop"/>; else at the
/// next sweep, within a second, when the provider sweeps for them). A reference no tick asked for in
/// <see cref="IdleAfter"/> leaves them sooner (it is put back when asked again); a one-off ask (a thumbnail, a held
/// picture) is answered as a tick's ask is, decoding a picture it hasn't yet, but keeps nothing alive. Admitting, dropping
/// and expiring each run under one lock, so none undoes another; a picture's bytes are taken from the source only under
/// that lock (an ask paused on its way holds none), and an ask's lookup, made after it admitted the picture, is kept only
/// while that admission stands (else dropped, with the cache entry it made).
/// </para>
/// <para>
/// What a picture's failure says is its code and the provider's fixed text, never a message that could carry what the
/// source gave (an address's host, an SVG's own ids). It logs nothing, and its pictures are decoded as private media (a
/// decode's failure logs neither the key nor the exception's message).
/// </para>
/// </summary>
public abstract class KeptPictureProvider : IMediaProvider, IDisposable
{
    /// <summary>A reference no tick asked for this long is dropped.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What "no picture" draws: nothing. A picture with no content and no natural size, so the item keeps its box (as the
    /// placeholder would) and stays transparent in it; never released (each answer retains it for its caller).
    /// </summary>
    private static readonly MediaSample Nothing = Empty();

    private static readonly MediaStatus Decoding = new("media.loading", "Decoding.");
    private static readonly MediaStatus ShutDown = new("media.provider", "The provider is shut down.");

    private readonly IKeptPictures? _pictures;
    private readonly MediaSampleCache _cache;
    private readonly FetchedMediaStore _store;
    private readonly IMediaProvider? _web;
    private readonly TimeProvider _time;
    private readonly string _keyPrefix;
    private readonly MediaStatus _none;
    private readonly MediaStatus _loading;
    private readonly string _failed;
    private readonly bool _sweepUnkept;
    private readonly Lock _lock = new();

    /// <summary>
    /// The pictures it put in the store, by reference: the store's key, when a tick last asked, and the admission that put
    /// it there (an ask's lookup is kept only while its admission stands).
    /// </summary>
    private readonly Dictionary<string, (string Key, long LastAsked, long Admission)> _held = new(StringComparer.Ordinal);

    /// <summary>Admissions made; under the lock.</summary>
    private long _admissions;
    private readonly ITimer _expiry;

    /// <summary>Set under the lock; read outside it only as a shortcut.</summary>
    private volatile bool _disposed;

    /// <param name="pictures">The source's pictures; null in an engine without the source.</param>
    /// <param name="cache">The engine's media cache, which decodes the pictures from <paramref name="store"/>.</param>
    /// <param name="store">The fetched media's store, where a picture's bytes are kept while it is asked for.</param>
    /// <param name="keyPrefix">What every key of its own in the store and the cache starts with; no other provider's key does.</param>
    /// <param name="none">The status of no picture.</param>
    /// <param name="failed">The text of every picture's failure, whatever its code.</param>
    /// <param name="loading">The status while a picture given as an address loads.</param>
    /// <param name="sweepUnkept">
    /// Whether each sweep (every second) also lets go of the pictures the source no longer keeps: for a source that
    /// doesn't tell of each it lets go of (<see cref="Drop"/>).
    /// </param>
    /// <param name="web">The provider a picture given as a web address is asked of (<c>http</c>); none leaves such pictures out.</param>
    /// <param name="time">The clock that times the references no tick asks for.</param>
    protected KeptPictureProvider(IKeptPictures? pictures, MediaSampleCache cache, FetchedMediaStore store, string keyPrefix,
        MediaStatus none, string failed, MediaStatus loading, bool sweepUnkept, IMediaProvider? web, TimeProvider? time)
    {
        _pictures = pictures;
        _cache = cache;
        _store = store;
        _keyPrefix = keyPrefix;
        _none = none;
        _failed = failed;
        _loading = loading;
        _sweepUnkept = sweepUnkept;
        _web = web;
        _time = time ?? TimeProvider.System;
        // References no tick asks for are dropped on the clock: once nothing asks, no lookup would notice them.
        _expiry = _time.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Tests: run by an ask past its first check, before it admits a picture.</summary>
    internal Action? Admitting;

    /// <summary>Tests: run by an ask once it admitted a picture, before the cache's lookup.</summary>
    internal Action? Admitted;

    /// <summary>
    /// Tests: run by the expiry for each reference it drops, under its lock, once it has let go of the reference's bytes
    /// and before it forgets its frames: where an ask that renewed the reference was once undone.
    /// </summary>
    internal Action? Expired;

    /// <summary>The pictures whose bytes it keeps in the store.</summary>
    internal int Held
    {
        get
        {
            lock (_lock) return _held.Count;
        }
    }

    public ProvidedMedia Get(in MediaRequest request) => Answer(request, null);

    public ProvidedMedia Get(in MediaRequest request, in MediaAsk ask) => Answer(request, ask);

    /// <summary>Another ask sharing the tick's answer: told on to the web image provider for a picture given as an address.</summary>
    public void Asked(in MediaRequest request)
    {
        if (!_disposed && _web is not null && _pictures is not null && _pictures.Keeps(request.Reference, out var address) && address is not null)
            _web.Asked(Forwarded(request, address));
    }

    /// <summary>
    /// The web image provider's request for a picture at <paramref name="address"/>: a private address, with no refresh
    /// (it doesn't apply to a kept picture: a new picture is a new reference).
    /// </summary>
    private static MediaRequest Forwarded(in MediaRequest request, string address) =>
        request with { Reference = address, Refresh = 0, Private = true };

    private static MediaSample Empty()
    {
        using var recorder = new SKPictureRecorder();
        recorder.BeginRecording(new SKRect(0, 0, 1, 1));
        return MediaSample.FromPicture(recorder.EndRecording(), 0, 0);
    }

    /// <summary>
    /// No picture (the source keeps none under the reference): the item draws nothing, not the missing-media placeholder,
    /// which stays for a picture that fails; its status still says why.
    /// </summary>
    private ProvidedMedia None() => new(Nothing.Retain(), _none);

    private ProvidedMedia Answer(in MediaRequest request, MediaAsk? ask)
    {
        if (_disposed) return new ProvidedMedia(null, ShutDown);
        // Whether the source keeps the reference, and its address: nothing holding a picture's bytes is taken outside the
        // lock, so an ask paused on its way keeps none alive while the source lets the picture go.
        if (_pictures is null || !_pictures.Keeps(request.Reference, out var address)) return None();
        if (address is not null)
        {
            if (_web is null) return None();
            var web = Forwarded(request, address);
            return Covered(ask is { } by ? _web.Get(web, by) : _web.Get(web));
        }

        Admitting?.Invoke();
        var reference = request.Reference;
        if (Admit(reference, request.Live, out var key, out var admission) is { } refused) return refused == _none ? None() : new ProvidedMedia(null, refused);

        Admitted?.Invoke();
        var lookup = _cache.Get(key, request.Seconds, request.Playback, privateMedia: true);
        lock (_lock)
        {
            // Let go of since it was admitted (dropped, expired, shut down; perhaps admitted afresh meanwhile): what this
            // lookup made of it is dropped, and the cache's entry it may have made again goes too unless a newer
            // admission holds the picture.
            if (_disposed || !_held.TryGetValue(reference, out var held) || held.Admission != admission)
            {
                lookup.Sample?.Release();
                var current = !_disposed && _held.ContainsKey(reference);
                if (!current) _cache.Forget(key);
                return _disposed ? new ProvidedMedia(null, ShutDown) : current ? new ProvidedMedia(null, Decoding) : None();
            }
        }
        if (lookup.Sample is { } sample) return new ProvidedMedia(sample);
        if (lookup.State != MediaState.Failed || !_store.Contains(key)) return new ProvidedMedia(null, Decoding);
        // Refused by the media limits, or not decodable: the cache's code ("code: message"), with the provider's own text.
        var problem = lookup.Problem ?? "";
        var colon = problem.IndexOf(": ", StringComparison.Ordinal);
        return new ProvidedMedia(null, new MediaStatus(colon > 0 ? problem[..colon] : "media.malformed", _failed));
    }

    /// <summary>
    /// Admits the picture the source keeps under <paramref name="reference"/>, under the lock: noted as held (its
    /// <paramref name="admission"/>, a new one unless it is held already), and its bytes in the store under
    /// <paramref name="key"/>. Null when admitted, else why not: shut down, or the picture left the source since it was
    /// looked up (its drop has run, finding nothing of it). The picture's bytes are taken from the source here only, under
    /// the lock the provider's drop waits for, and held by nothing of the ask's once it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private MediaStatus? Admit(string reference, bool live, out string key, out long admission)
    {
        key = "";
        admission = 0;
        lock (_lock)
        {
            if (_disposed) return ShutDown;
            if (!_pictures!.TryGet(reference, out var bytes, out var id)) return _none;
            key = _keyPrefix + "sha256:" + id;
            var now = _time.GetTimestamp();
            if (!_held.TryGetValue(reference, out var held))
                held = (key, now, ++_admissions);
            else if (live)
                held.LastAsked = now;
            _held[reference] = held;
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
    /// The web image provider's answer for a picture, as the picture's: its sample, and its status's code with the
    /// provider's own text (the web provider's could name the address's host).
    /// </summary>
    private ProvidedMedia Covered(ProvidedMedia answer) => answer.Status is { } status
        ? answer with { Status = status.Code == "media.loading" ? _loading : new MediaStatus(status.Code, _failed) }
        : answer;

    /// <summary>The source let go of <paramref name="reference"/>'s picture: its bytes leave the store, its frames the cache.</summary>
    protected void Drop(string reference)
    {
        lock (_lock)
        {
            if (_held.Remove(reference, out var held)) Release(held.Key);
        }
    }

    /// <summary>Removes <paramref name="key"/>'s bytes from the store and its frames from the cache. Under the lock.</summary>
    private void Release(string key)
    {
        _store.Remove(key);
        _cache.Forget(key);
    }

    /// <summary>
    /// The timer's sweep: the references no tick asked for in <see cref="IdleAfter"/> are dropped, and, when it sweeps for
    /// them, those the source no longer keeps.
    /// </summary>
    private void Expire()
    {
        if (_disposed) return;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            List<string>? dropped = null;
            foreach (var (reference, held) in _held)
            {
                if (_time.GetElapsedTime(held.LastAsked, now) >= IdleAfter)
                    (dropped ??= []).Add(reference);
                else if (_sweepUnkept && !_pictures!.Keeps(reference, out _))
                    (dropped ??= []).Add(reference);
            }
            if (dropped is null) return;
            // Forgotten under the lock too: an ask renewing a reference waits for its drop to finish, never the other way.
            foreach (var reference in dropped)
            {
                var key = _held[reference].Key;
                _held.Remove(reference);
                _store.Remove(key);
                Expired?.Invoke();
                _cache.Forget(key);
            }
        }
    }

    /// <summary>Run once by <see cref="Dispose"/>, after it let go of every picture (a subclass stops listening to its source).</summary>
    protected virtual void OnDisposed()
    {
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
        OnDisposed();
        _expiry.Dispose();
        GC.SuppressFinalize(this);
    }
}
