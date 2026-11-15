using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// The cover art <see cref="NowPlayingSource"/> read, for the <c>now-playing</c> media provider: the current picture and
/// the few before it (at most <see cref="MaxKept"/>, each at most <see cref="NowPlayingSource.MaxArtBytes"/>, so together
/// at most 32 MiB; the oldest dropped first), each under its token, the <c>art</c> reading's text: 16 hexadecimal digits
/// of the SHA-256 of its bytes, or of its address for a cover a player gives as a web address (<c>http</c> or
/// <c>https</c>, at most <see cref="MaxAddressChars"/> characters). In memory only, and emptied when the source stops
/// reading. The sampler publishes on its poll thread; the provider reads from render threads.
/// <para>
/// A picture given as bytes has a main colour (<see cref="NowPlayingArtColor"/>, the <c>art-color</c> reading), computed
/// once per picture off the poll thread, on a worker of the art's own (a thread started when there is a colour to
/// compute and ended when there is none left, so none while idle; one computation at a time, at most one waiting, a newer
/// cover's taking the waiting one's place), and kept with that very picture, never by anything else: the poll thread
/// never waits for it (until it is computed the colour is none), a colour goes out only with the token of the picture it
/// was computed from, a result for a picture no longer kept is dropped, and a picture kept before has its colour at once.
/// The worker holds no lock while it decodes. Stopped with the source (<see cref="Close"/>): nothing more is computed, a
/// computation under way is let finish on its own and its result dropped, and the worker ends with it. A cover given as
/// an address has none.
/// </para>
/// </summary>
public sealed class NowPlayingArt : InfoPanel.Images.IKeptPictures
{
    /// <summary>Pictures kept at most: the current one and those before it.</summary>
    public const int MaxKept = 4;

    /// <summary>A cover's address over this many characters is left out.</summary>
    public const int MaxAddressChars = 2048;

    /// <summary>A kept picture: its <paramref name="Token"/>, and its bytes (with their SHA-256 in lower-case hexadecimal) or its address.</summary>
    public sealed record Picture(string Token, byte[]? Bytes, string? Sha256, string? Address);

    private readonly Lock _lock = new();
    private readonly LinkedList<Picture> _kept = new(); // the current first

    // Under _lock: the main colours computed for pictures kept, by the picture itself (not its token), so a colour is
    // only ever that picture's; null when it has none. A picture's goes with it.
    private readonly Dictionary<Picture, string?> _colors = new(ReferenceEqualityComparer.Instance);

    // The colour worker's, under _work (taken before _lock, never inside it): the picture waiting (at most one), the one
    // being computed, whether a worker runs, and whether the art is closed.
    private readonly object _work = new();
    private Picture? _waiting;
    private Picture? _computing;
    private bool _working;
    private bool _closed;

    // The poll thread's own: the last address published and its token, so an unchanged address isn't hashed again. (An
    // unchanged cover's bytes are compared with the current picture's copy: the reader's buffer is never kept.)
    private string? _lastAddress;
    private string? _lastToken;

    /// <summary>Tests: run before a new picture is made (its bytes copied).</summary>
    internal Action? Copying;

    /// <summary>Tests: run on the colour's worker before a picture's main colour is computed (a test holds it there).</summary>
    internal Action? Coloring;

    /// <summary>Tests: the colour's worker last started (it may have ended).</summary>
    internal Thread? Worker { get; private set; }

    /// <summary>
    /// Tests: waits until no colour is computed or waiting, at most <paramref name="timeout"/>; whether it came to that.
    /// </summary>
    internal bool WaitColored(TimeSpan timeout)
    {
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_work)
        {
            while (_working)
            {
                var left = until - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_work, TimeSpan.FromMilliseconds(left)))
                    return !_working;
            }
            return true;
        }
    }

    /// <summary>
    /// Raised with each picture's token as it stops being kept (pushed out by a newer one, or every picture when the
    /// source stops reading), once it is gone: whoever holds anything made of it (the provider's store and cache
    /// entries) lets go of it then. On the poll thread, before a newer picture is copied.
    /// </summary>
    internal event Action<string>? Dropped;

    /// <summary>The pictures kept.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _kept.Count;
        }
    }

    /// <summary>The bytes of the pictures kept: every cover's buffer it holds (it keeps no other).</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                long bytes = 0;
                foreach (var p in _kept) bytes += p.Bytes?.Length ?? 0;
                return bytes;
            }
        }
    }

    /// <summary>
    /// Whether a picture is kept under <paramref name="token"/>, and its <paramref name="address"/> when it was given as
    /// one: nothing that holds a cover's bytes, so a caller that pauses after asking keeps none alive.
    /// </summary>
    public bool Keeps(string token, out string? address)
    {
        lock (_lock)
        {
            foreach (var p in _kept)
            {
                if (string.Equals(p.Token, token, StringComparison.Ordinal))
                {
                    address = p.Address;
                    return true;
                }
            }
        }
        address = null;
        return false;
    }

    /// <summary>The picture kept under <paramref name="token"/>.</summary>
    public bool TryGet(string token, out Picture picture)
    {
        lock (_lock)
        {
            foreach (var p in _kept)
            {
                if (string.Equals(p.Token, token, StringComparison.Ordinal))
                {
                    picture = p;
                    return true;
                }
            }
        }
        picture = null!;
        return false;
    }

    /// <summary>The cover kept under <paramref name="token"/> as the <c>now-playing</c> provider takes it: its bytes and their whole SHA-256.</summary>
    bool InfoPanel.Images.IKeptPictures.TryGet(string token, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? bytes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? id)
    {
        (bytes, id) = TryGet(token, out var picture) && picture.Bytes is { } kept ? (kept, picture.Sha256!) : (null, null);
        return bytes is not null;
    }

    /// <summary>
    /// Keeps <paramref name="snapshot"/>'s cover as the current picture and answers its token; null when it has none
    /// (none given, over the bounds, or an address that isn't a web address). The poll thread's.
    /// </summary>
    internal string? Publish(NowPlayingSnapshot snapshot) => Publish(snapshot, out _);

    /// <summary>
    /// As <see cref="Publish(NowPlayingSnapshot)"/>, with the current picture's main colour, <paramref name="color"/>:
    /// the one computed from the very picture the token names, so never another cover's; null until it is computed
    /// (asked for then, never waited for), and for a cover given as an address, one that has none, and no cover.
    /// </summary>
    internal string? Publish(NowPlayingSnapshot snapshot, out string? color)
    {
        color = null;
        if (snapshot.Art is { Length: > 0 } art)
        {
            if (art.Length > NowPlayingSource.MaxArtBytes)
                return null;
            _lastAddress = null;
            // The same cover as the current picture (a player's unchanged art, read again): its token, nothing hashed.
            Picture? current;
            lock (_lock) current = _kept.First?.Value;
            if (current?.Bytes is { } kept && art.Span.SequenceEqual(kept))
            {
                _lastToken = current.Token;
                color = ColorOf(current);
                return current.Token;
            }
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(art.Span));
            var token = sha256[..16];
            var picture = Keep(token, () => new Picture(token, art.ToArray(), sha256, null));
            _lastToken = token;
            color = ColorOf(picture);
            return token;
        }

        if (snapshot.ArtAddress is { Length: > 0 and <= MaxAddressChars } address && IsWebAddress(address))
        {
            var token = _lastToken is not null && string.Equals(_lastAddress, address, StringComparison.Ordinal)
                ? _lastToken
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address)))[..16];
            Keep(token, () => new Picture(token, null, null, address));
            _lastAddress = address;
            _lastToken = token;
            return token;
        }

        return null;
    }

    /// <summary>Forgets every picture (the source stopped reading), and tells <see cref="Dropped"/> each. The poll thread's.</summary>
    internal void Clear()
    {
        List<string> dropped = [];
        lock (_work)
        {
            _waiting = null;
            lock (_lock)
            {
                foreach (var p in _kept) dropped.Add(p.Token);
                _kept.Clear();
                _colors.Clear();
            }
        }
        _lastAddress = null;
        _lastToken = null;
        foreach (var token in dropped) Dropped?.Invoke(token);
    }

    /// <summary>
    /// Makes <paramref name="token"/>'s picture the current one, made by <paramref name="make"/> when it isn't kept. The
    /// oldest go first, to make room for it, so the pictures kept and the one being made never pass
    /// <see cref="MaxKept"/> between them. Answers the picture now current.
    /// </summary>
    private Picture Keep(string token, Func<Picture> make)
    {
        List<string>? dropped = null;
        lock (_lock)
        {
            for (var node = _kept.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Token, token, StringComparison.Ordinal))
                {
                    _kept.Remove(node);
                    _kept.AddFirst(node);
                    return node.Value;
                }
            }
            while (_kept.Count >= MaxKept)
            {
                (dropped ??= []).Add(_kept.Last!.Value.Token);
                _colors.Remove(_kept.Last.Value);
                _kept.RemoveLast();
            }
        }
        // Those pushed out are let go of before the copy is made.
        if (dropped is not null)
            foreach (var gone in dropped) Dropped?.Invoke(gone);

        // Copied outside the lock: render threads asking for a picture don't wait for it.
        Copying?.Invoke();
        var picture = make();
        lock (_lock) _kept.AddFirst(picture);
        return picture;
    }

    /// <summary>
    /// Stops the colour's worker for good (the source is disposed): nothing waits any more and nothing more is computed;
    /// a computation under way isn't waited for, and its result is dropped. Never blocks.
    /// </summary>
    internal void Close()
    {
        lock (_work)
        {
            _closed = true;
            _waiting = null;
        }
    }

    /// <summary>
    /// The main colour computed from <paramref name="picture"/>, a kept one with bytes; when it hasn't been, none, and
    /// it waits to be computed (in place of any other waiting) unless it is already under way. The poll thread's; never
    /// waits for the worker.
    /// </summary>
    private string? ColorOf(Picture picture)
    {
        lock (_work)
        {
            lock (_lock)
            {
                if (_colors.TryGetValue(picture, out var color))
                    return color;
            }
            if (_closed || ReferenceEquals(_computing, picture))
                return null;
            _waiting = picture;
            if (!_working)
            {
                _working = true;
                Worker = new Thread(Work) { IsBackground = true, Name = "Now Playing cover colour" };
                Worker.Start();
            }
        }
        return null;
    }

    /// <summary>
    /// The colour's worker: computes the picture waiting, one at a time, until none waits, then ends. Holds no lock while
    /// it computes; keeps a result only for a picture still kept, and never once closed. Nothing it meets leaves it.
    /// </summary>
    private void Work()
    {
        while (true)
        {
            Picture picture;
            lock (_work)
            {
                if (_closed || _waiting is null)
                {
                    _waiting = null;
                    _working = false;
                    Monitor.PulseAll(_work);
                    return;
                }
                picture = _waiting;
                _waiting = null;
                lock (_lock)
                {
                    // Pushed out while it waited, or computed meanwhile: nothing to do.
                    if (!IsKept(picture) || _colors.ContainsKey(picture))
                        continue;
                }
                _computing = picture;
            }

            string? color;
            try
            {
                Coloring?.Invoke();
                color = NowPlayingArtColor.Of(picture.Bytes!);
            }
            catch (Exception)
            {
                // Of throws nothing; whatever else a cover's computation meets, it has no colour.
                color = null;
            }

            lock (_work)
            {
                _computing = null;
                lock (_lock)
                {
                    if (!_closed && IsKept(picture))
                        _colors[picture] = color;
                }
            }
        }
    }

    /// <summary>Whether <paramref name="picture"/> itself is kept. Under <see cref="_lock"/>.</summary>
    private bool IsKept(Picture picture)
    {
        foreach (var p in _kept)
        {
            if (ReferenceEquals(p, picture))
                return true;
        }
        return false;
    }

    private static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
