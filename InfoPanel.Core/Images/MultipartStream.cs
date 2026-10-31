using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Images;

/// <summary>
/// Reads the parts of a <c>multipart/x-mixed-replace</c> answer (an MJPEG camera's stream) one at a time, as they come:
/// each part's body whole, ended by its <c>Content-Length</c> or else by the next boundary line, within
/// <paramref name="maxPart"/> bytes counted as they are read. A delimiter line is <c>--</c> and the boundary (or the
/// boundary as declared, when it starts with <c>--</c> itself, as some cameras write it); only blank lines may come
/// before one. Lines and a part's headers are bounded too. A problem ends the stream with a <see cref="Problem"/>.
/// </summary>
internal sealed class MultipartStream
{
    /// <summary>The longest line read (a delimiter or a header).</summary>
    public const int MaxLine = 8 * 1024;

    /// <summary>The most header bytes one part may have.</summary>
    public const int MaxHeaders = 16 * 1024;

    /// <summary>The most blank lines before a delimiter.</summary>
    private const int MaxBlankLines = 16;

    private readonly Stream _stream;
    private readonly long _maxPart;
    private readonly Action<long>? _charge;
    private readonly byte[][] _delimiters;
    private readonly byte[][] _endings;
    private readonly int _longestEnding;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _eof;

    /// <param name="charge">
    /// Told of each part's bytes as they are read, before they are kept (a budget across streams); it throws
    /// <see cref="Problem"/> to end the stream.
    /// </param>
    public MultipartStream(Stream stream, string boundary, long maxPart, Action<long>? charge = null)
    {
        _stream = stream;
        _maxPart = maxPart;
        _charge = charge;
        var standard = Encoding.ASCII.GetBytes("--" + boundary);
        _delimiters = boundary.StartsWith("--", StringComparison.Ordinal) ? [standard, Encoding.ASCII.GetBytes(boundary)] : [standard];
        _endings = Array.ConvertAll(_delimiters, d =>
        {
            byte[] ending = [(byte)'\n', .. d];
            return ending;
        });
        foreach (var e in _endings) _longestEnding = Math.Max(_longestEnding, e.Length);
    }

    /// <summary>A problem that ends the stream: its <see cref="Code"/> (<c>media.provider</c>, <c>limit.mediaBytes</c>) and why.</summary>
    public sealed class Problem(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// The boundary a <c>multipart/x-mixed-replace</c> content type declares (its <c>boundary</c> parameter, unquoted), or
    /// null when it declares none or an empty one, or one past 70 characters (RFC 2046) or with a line break.
    /// </summary>
    public static string? BoundaryOf(System.Net.Http.Headers.MediaTypeHeaderValue type)
    {
        foreach (var p in type.Parameters)
        {
            if (!p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)) continue;
            var value = p.Value?.Trim() ?? "";
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value.Length is > 0 and <= 72 && value.IndexOfAny(['\r', '\n']) < 0 ? value : null;
        }
        return null;
    }

    private static Problem Ended() => new("media.provider", "The stream ended.");

    private Problem OverLimit() => new("limit.mediaBytes", $"A part of the stream is over {_maxPart} bytes (mediaBytes).");

    private static Problem Malformed(string why) => new("media.provider", why);

    /// <summary>The next part's body; null at the closing delimiter. Throws <see cref="Problem"/> when the stream breaks or ends.</summary>
    public async Task<byte[]?> ReadPartAsync(CancellationToken token)
    {
        // Blank lines, then the delimiter.
        var blanks = 0;
        while (true)
        {
            var line = await ReadLineAsync(token).ConfigureAwait(false) ?? throw Ended();
            if (Delimiter(line, out var closing))
            {
                if (closing) return null;
                break;
            }
            if (line.Length > 0 || ++blanks > MaxBlankLines) throw Malformed("The stream doesn't start with its boundary.");
        }

        // The part's headers: only its length matters (what the part is, is read from its bytes).
        long? length = null;
        var headerBytes = 0;
        while (true)
        {
            var line = await ReadLineAsync(token).ConfigureAwait(false) ?? throw Ended();
            if (line.Length == 0) break;
            headerBytes += line.Length + 2;
            if (headerBytes > MaxHeaders) throw Malformed("A part's headers are too long.");
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            var text = line.AsSpan(colon + 1).Trim();
            if (text.Length is 0 or > 19 || !long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                throw Malformed("A part's length isn't a number.");
            length = n;
        }

        if (length is { } exact)
        {
            if (exact > _maxPart) throw OverLimit();
            return await ReadExactAsync((int)exact, token).ConfigureAwait(false);
        }
        return await ReadToDelimiterAsync(token).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="line"/> is a delimiter line (trailing spaces allowed), and whether it is the closing one.</summary>
    private bool Delimiter(string line, out bool closing)
    {
        closing = false;
        var trimmed = line.AsSpan().TrimEnd(" \t");
        foreach (var d in _delimiters)
        {
            if (trimmed.Length < d.Length || !Ascii(trimmed[..d.Length], d)) continue;
            var rest = trimmed[d.Length..];
            if (rest.Length == 0) return true;
            if (rest is "--")
            {
                closing = true;
                return true;
            }
        }
        return false;
    }

    private static bool Ascii(ReadOnlySpan<char> text, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            if (text[i] != bytes[i]) return false;
        return true;
    }

    /// <summary>The next line without its line end (CRLF or LF), as Latin-1; null at the end of the stream.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken token)
    {
        var scanned = 0;
        while (true)
        {
            var at = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (at >= 0)
            {
                var end = at > _start && _buffer[at - 1] == '\r' ? at - 1 : at;
                var line = Encoding.Latin1.GetString(_buffer, _start, end - _start);
                _start = at + 1;
                return line;
            }
            scanned = _end - _start;
            if (scanned > MaxLine) throw Malformed("A line of the stream is too long.");
            if (!await FillAsync(token).ConfigureAwait(false)) return null;
        }
    }

    /// <summary>Exactly <paramref name="length"/> bytes, grown as they arrive (never allocated from the length alone).</summary>
    private async Task<byte[]> ReadExactAsync(int length, CancellationToken token)
    {
        using var body = new MemoryStream(Math.Min(length, 64 * 1024));
        while (body.Length < length)
        {
            if (_end == _start && !await FillAsync(token).ConfigureAwait(false)) throw Ended();
            var take = (int)Math.Min(length - body.Length, _end - _start);
            _charge?.Invoke(take);
            body.Write(_buffer, _start, take);
            _start += take;
        }
        return body.ToArray();
    }

    /// <summary>The bytes up to the line end before the next delimiter line, which is left to be read.</summary>
    private async Task<byte[]> ReadToDelimiterAsync(CancellationToken token)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var at = -1;
            foreach (var e in _endings)
            {
                var i = span.IndexOf(e);
                if (i >= 0 && (at < 0 || i < at)) at = i;
            }
            if (at >= 0)
            {
                if (body.Length + at > _maxPart + 1) throw OverLimit();
                _charge?.Invoke(at);
                body.Write(span[..at]);
                // The CR of the CRLF before the delimiter isn't the part's.
                if (body.Length > 0 && body.GetBuffer()[body.Length - 1] == '\r') body.SetLength(body.Length - 1);
                if (body.Length > _maxPart) throw OverLimit();
                _start += at + 1;
                return body.ToArray();
            }
            // Keep what could be the start of a delimiter; the rest is the part's.
            var move = Math.Max(0, span.Length - (_longestEnding - 1));
            if (body.Length + move > _maxPart + 1) throw OverLimit();
            _charge?.Invoke(move);
            body.Write(span[..move]);
            _start += move;
            if (!await FillAsync(token).ConfigureAwait(false)) throw Ended();
        }
    }

    /// <summary>Reads more into the buffer, moving what is left to its start; false at the end of the stream.</summary>
    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_eof) return false;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return false;
        }
        _end += read;
        return true;
    }
}
