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

    // The poll thread's own: the last address published and its token, so an unchanged address isn't hashed again. (An
    // unchanged cover's bytes are compared with the current picture's copy: the reader's buffer is never kept.)
    private string? _lastAddress;
    private string? _lastToken;

    /// <summary>Tests: run before a new picture is made (its bytes copied).</summary>
    internal Action? Copying;

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
    internal string? Publish(NowPlayingSnapshot snapshot)
    {
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
                return current.Token;
            }
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(art.Span));
            var token = sha256[..16];
            Keep(token, () => new Picture(token, art.ToArray(), sha256, null));
            _lastToken = token;
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
        lock (_lock)
        {
            foreach (var p in _kept) dropped.Add(p.Token);
            _kept.Clear();
        }
        _lastAddress = null;
        _lastToken = null;
        foreach (var token in dropped) Dropped?.Invoke(token);
    }

    /// <summary>
    /// Makes <paramref name="token"/>'s picture the current one, made by <paramref name="make"/> when it isn't kept. The
    /// oldest go first, to make room for it, so the pictures kept and the one being made never pass
    /// <see cref="MaxKept"/> between them.
    /// </summary>
    private void Keep(string token, Func<Picture> make)
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
                    return;
                }
            }
            while (_kept.Count >= MaxKept)
            {
                (dropped ??= []).Add(_kept.Last!.Value.Token);
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
    }

    private static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
