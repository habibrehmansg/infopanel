using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Xml;

namespace InfoPanel.Spec;

public enum MediaFormat { Png, Jpeg, Gif, WebP, Svg }

/// <summary>
/// What a probe found. <paramref name="Elements"/> is the SVG's expanded element count (spec/docs/svg.md, "Expanded
/// size": its elements, their path numbers and gradient references, plus for each <c>use</c>, <c>clip-path</c> and
/// <c>mask</c> reference the expanded count of what it draws; 0 for raster formats); it is an optional trailing
/// parameter so the five-argument form still works.
/// </summary>
public sealed record MediaInfo(MediaFormat Format, int Width, int Height, int Frames, double DurationSeconds, int Elements = 0);

/// <summary>
/// Why a probe refused: <c>media.format</c>, <c>media.truncated</c>, <c>media.malformed</c>, <c>media.svg-external</c>
/// (outside the SVG profile), <c>media.svg-size</c> (a width or height in other units) or <c>media.svg-parse</c>.
/// </summary>
public sealed record MediaProblem(string Code, string Message);

/// <summary>
/// Inspects image bytes before anything decodes them, so oversized or hostile media can be refused cheaply.
/// Reads headers and walks the container structure (PNG chunks, JPEG segments, GIF blocks, WebP chunks) and never
/// decompresses pixel data. Safe on any bytes: every loop advances through the input, nothing is allocated from a
/// size the header claims, and there is no recursion. Limits (dimensions, frames, duration) are the caller's to apply.
/// SVG is read as a stream and held to the restricted profile of spec/docs/svg.md.
/// </summary>
public static class MediaProbe
{
    public const string FormatCode = "media.format";
    public const string TruncatedCode = "media.truncated";
    public const string MalformedCode = "media.malformed";
    public const string SvgExternalCode = "media.svg-external";
    public const string SvgSizeCode = "media.svg-size";
    public const string SvgParseCode = "media.svg-parse";

    /// <summary>Largest SVG document the reader accepts, in characters.</summary>
    public const int MaxSvgCharacters = 4_194_304;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static MediaInfo? Inspect(ReadOnlySpan<byte> bytes, out MediaProblem? problem) =>
        Inspect(bytes, out problem, int.MaxValue);

    /// <summary>
    /// As <see cref="Inspect(ReadOnlySpan{byte}, out MediaProblem?)"/>, but an SVG stops being read at its element number
    /// <paramref name="maxSvgElements"/> + 1, and the result then has that many <see cref="MediaInfo.Elements"/>; so it
    /// does when the SVG's expanded element count (references included, <see cref="SvgProfile"/>) is over the limit.
    /// </summary>
    public static MediaInfo? Inspect(ReadOnlySpan<byte> bytes, out MediaProblem? problem, int maxSvgElements) =>
        Inspect(bytes, out problem, maxSvgElements, out _);

    /// <summary>
    /// As <see cref="Inspect(ReadOnlySpan{byte}, out MediaProblem?, int)"/>; for an SVG in the profile and within the
    /// element limit, <paramref name="rebuiltSvg"/> is the canonical document rebuilt from it (<see cref="SvgProfile"/>).
    /// </summary>
    internal static MediaInfo? Inspect(ReadOnlySpan<byte> bytes, out MediaProblem? problem, int maxSvgElements, out byte[]? rebuiltSvg)
    {
        problem = null;
        rebuiltSvg = null;
        try
        {
            if (bytes.Length > 0 && IsPrefixOrMatch(bytes, PngSignature, out bool full))
                return full ? InspectPng(bytes, out problem) : Fail(out problem, TruncatedCode, "PNG signature is incomplete.");

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                return InspectJpeg(bytes, out problem);
            if (bytes.Length == 1 && bytes[0] == 0xFF)
                return Fail(out problem, TruncatedCode, "JPEG signature is incomplete.");

            if (bytes.Length > 0)
            {
                if (IsPrefixOrMatch(bytes, "GIF87a"u8, out _) || IsPrefixOrMatch(bytes, "GIF89a"u8, out _))
                {
                    // Matches one of the two version strings, or is a prefix of one.
                    bool complete = bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));
                    return complete ? InspectGif(bytes, out problem) : Fail(out problem, TruncatedCode, "GIF signature is incomplete.");
                }

                if (LooksLikeWebP(bytes, out bool webpComplete))
                    return webpComplete ? InspectWebP(bytes, out problem) : Fail(out problem, TruncatedCode, "WebP header is incomplete.");
            }

            if (LooksLikeSvg(bytes))
                return SvgProfile.Read(bytes, out problem, maxSvgElements, out rebuiltSvg);

            return Fail(out problem, FormatCode, "Not a supported image format (PNG, JPEG, GIF, WebP or SVG).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defence in depth: the walkers do not throw on any input, but a probe must never take the caller down.
            rebuiltSvg = null;
            return Fail(out problem, MalformedCode, "The file could not be inspected.");
        }
    }

    private static MediaInfo? Fail(out MediaProblem? problem, string code, string message)
    {
        problem = new MediaProblem(code, message);
        return null;
    }

    /// <summary>True when <paramref name="b"/> equals the first bytes of <paramref name="sig"/> (or all of it, with <paramref name="full"/>).</summary>
    private static bool IsPrefixOrMatch(ReadOnlySpan<byte> b, ReadOnlySpan<byte> sig, out bool full)
    {
        int n = Math.Min(b.Length, sig.Length);
        full = b.Length >= sig.Length && b[..sig.Length].SequenceEqual(sig);
        return b[..n].SequenceEqual(sig[..n]);
    }

    // ---------------------------------------------------------------- PNG

    private static MediaInfo? InspectPng(ReadOnlySpan<byte> b, out MediaProblem? problem)
    {
        problem = null;
        // signature(8) length(4) "IHDR"(4) data(13)
        if (b.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(b[8..]) != 13)
            return Fail(out problem, MalformedCode, "PNG: the first chunk is not a 13-byte IHDR.");
        if (b.Length >= 16 && !b.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Fail(out problem, MalformedCode, "PNG: IHDR must be the first chunk.");
        if (b.Length < 29)
            return Fail(out problem, TruncatedCode, "PNG: IHDR is incomplete.");

        uint w = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        if (w < 1 || w > int.MaxValue || h < 1 || h > int.MaxValue)
            return Fail(out problem, MalformedCode, "PNG: width and height must be 1 to 2^31-1.");

        int frames = 1;
        double seconds = 0;
        bool animated = false, seenIdat = false;
        int pos = 33; // after IHDR and its CRC
        while (pos <= b.Length - 8)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(b[pos..]);
            ReadOnlySpan<byte> type = b.Slice(pos + 4, 4);
            int data = pos + 8;
            long avail = b.Length - data;

            if (type.SequenceEqual("IEND"u8))
                break;
            if (type.SequenceEqual("IDAT"u8))
                seenIdat = true;
            else if (!seenIdat && !animated && type.SequenceEqual("acTL"u8) && len >= 8 && avail >= 8)
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(b[data..]);
                if (n < 1 || n > int.MaxValue)
                    return Fail(out problem, MalformedCode, "PNG: acTL frame count must be 1 to 2^31-1.");
                frames = (int)n;
                animated = true;
            }
            else if (animated && type.SequenceEqual("fcTL"u8) && len >= 26 && avail >= 26)
            {
                int num = BinaryPrimitives.ReadUInt16BigEndian(b[(data + 20)..]);
                int den = BinaryPrimitives.ReadUInt16BigEndian(b[(data + 22)..]);
                if (den == 0) den = 100;
                seconds += (double)num / den;
            }

            long next = (long)pos + 12 + len;
            if (next > b.Length) break;
            pos = (int)next;
        }

        return new MediaInfo(MediaFormat.Png, (int)w, (int)h, frames, seconds);
    }

    // ---------------------------------------------------------------- JPEG

    private static MediaInfo? InspectJpeg(ReadOnlySpan<byte> b, out MediaProblem? problem)
    {
        problem = null;
        int pos = 2;
        while (true)
        {
            if (pos >= b.Length)
                return Fail(out problem, MalformedCode, "JPEG: no frame header (SOF) before the end of the data.");
            if (b[pos] != 0xFF)
                return Fail(out problem, MalformedCode, "JPEG: expected a marker.");
            while (pos < b.Length && b[pos] == 0xFF) pos++; // fill bytes
            if (pos >= b.Length)
                return Fail(out problem, MalformedCode, "JPEG: no frame header (SOF) before the end of the data.");
            byte m = b[pos++];

            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7))
                continue; // standalone, no length
            if (m == 0x00 || m == 0xD8)
                return Fail(out problem, MalformedCode, "JPEG: unexpected marker.");
            if (m == 0xD9 || m == 0xDA)
                return Fail(out problem, MalformedCode, "JPEG: no frame header (SOF) before the image data.");

            bool sof = m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
            if (sof)
            {
                if (pos + 7 > b.Length)
                    return Fail(out problem, TruncatedCode, "JPEG: the frame header is incomplete.");
                int segLen = BinaryPrimitives.ReadUInt16BigEndian(b[pos..]);
                if (segLen < 8)
                    return Fail(out problem, MalformedCode, "JPEG: frame header too short.");
                int h = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 3)..]);
                int w = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 5)..]);
                if (w == 0 || h == 0)
                    return Fail(out problem, MalformedCode, "JPEG: zero width or height.");
                return new MediaInfo(MediaFormat.Jpeg, w, h, 1, 0);
            }

            if (pos + 2 > b.Length)
                return Fail(out problem, MalformedCode, "JPEG: no frame header (SOF) before the end of the data.");
            int len = BinaryPrimitives.ReadUInt16BigEndian(b[pos..]);
            if (len < 2)
                return Fail(out problem, MalformedCode, "JPEG: segment length below 2.");
            pos += len; // may run past the end; the next iteration reports it
        }
    }

    // ---------------------------------------------------------------- GIF

    private static MediaInfo? InspectGif(ReadOnlySpan<byte> b, out MediaProblem? problem)
    {
        problem = null;
        if (b.Length < 13)
            return Fail(out problem, TruncatedCode, "GIF: the logical screen descriptor is incomplete.");
        int w = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        if (w == 0 || h == 0)
            return Fail(out problem, MalformedCode, "GIF: zero width or height.");

        int pos = 13;
        if ((b[10] & 0x80) != 0)
            pos += 3 << ((b[10] & 7) + 1);

        int frames = 0;
        long centis = 0;
        while (pos < b.Length)
        {
            byte intro = b[pos];
            if (intro == 0x3B) break;

            if (intro == 0x21) // extension
            {
                if (pos + 2 > b.Length) break;
                byte label = b[pos + 1];
                if (label == 0xF9 && pos + 6 <= b.Length && b[pos + 2] >= 4)
                    centis += BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 4)..]);
                pos += 2;
                if (!SkipSubBlocks(b, ref pos)) break;
            }
            else if (intro == 0x2C) // image descriptor
            {
                if (pos + 10 > b.Length) break;
                frames++;
                byte flags = b[pos + 9];
                pos += 10;
                if ((flags & 0x80) != 0)
                    pos += 3 << ((flags & 7) + 1);
                pos++; // LZW minimum code size
                if (!SkipSubBlocks(b, ref pos)) break;
            }
            else
            {
                if (frames == 0)
                    return Fail(out problem, MalformedCode, "GIF: unknown block.");
                break;
            }
        }

        if (frames == 0)
            return Fail(out problem, MalformedCode, "GIF: no image data.");
        return new MediaInfo(MediaFormat.Gif, w, h, frames, centis / 100.0);
    }

    /// <summary>Skips data sub-blocks up to and including the zero terminator. False when the data ends first.</summary>
    private static bool SkipSubBlocks(ReadOnlySpan<byte> b, ref int pos)
    {
        while (pos < b.Length)
        {
            int n = b[pos];
            pos += 1 + n;
            if (n == 0) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- WebP

    private static bool LooksLikeWebP(ReadOnlySpan<byte> b, out bool complete)
    {
        complete = false;
        ReadOnlySpan<byte> riff = "RIFF"u8, webp = "WEBP"u8;
        int n = Math.Min(b.Length, 4);
        if (!b[..n].SequenceEqual(riff[..n])) return false;
        if (b.Length < 12)
            return b.Length <= 8 || b[8..].SequenceEqual(webp[..(b.Length - 8)]);
        if (!b.Slice(8, 4).SequenceEqual(webp)) return false;
        complete = true;
        return true;
    }

    private static MediaInfo? InspectWebP(ReadOnlySpan<byte> b, out MediaProblem? problem)
    {
        problem = null;
        if (b.Length < 20)
            return Fail(out problem, TruncatedCode, "WebP: the first chunk header is incomplete.");

        ReadOnlySpan<byte> type = b.Slice(12, 4);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);
        int d = 20;

        if (type.SequenceEqual("VP8 "u8))
        {
            if (size < 10) return Fail(out problem, MalformedCode, "WebP: VP8 chunk too short.");
            if (b.Length < d + 10) return Fail(out problem, TruncatedCode, "WebP: the VP8 frame header is incomplete.");
            if (b[d + 3] != 0x9D || b[d + 4] != 0x01 || b[d + 5] != 0x2A)
                return Fail(out problem, MalformedCode, "WebP: bad VP8 start code.");
            int w = BinaryPrimitives.ReadUInt16LittleEndian(b[(d + 6)..]) & 0x3FFF;
            int h = BinaryPrimitives.ReadUInt16LittleEndian(b[(d + 8)..]) & 0x3FFF;
            if (w == 0 || h == 0) return Fail(out problem, MalformedCode, "WebP: zero width or height.");
            return new MediaInfo(MediaFormat.WebP, w, h, 1, 0);
        }

        if (type.SequenceEqual("VP8L"u8))
        {
            if (size < 5) return Fail(out problem, MalformedCode, "WebP: VP8L chunk too short.");
            if (b.Length < d + 5) return Fail(out problem, TruncatedCode, "WebP: the VP8L header is incomplete.");
            if (b[d] != 0x2F) return Fail(out problem, MalformedCode, "WebP: bad VP8L signature.");
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b[(d + 1)..]);
            return new MediaInfo(MediaFormat.WebP, (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, 1, 0);
        }

        if (type.SequenceEqual("VP8X"u8))
        {
            if (size < 10) return Fail(out problem, MalformedCode, "WebP: VP8X chunk too short.");
            if (b.Length < d + 10) return Fail(out problem, TruncatedCode, "WebP: the VP8X header is incomplete.");
            bool animated = (b[d] & 0x02) != 0;
            int w = 1 + (b[d + 4] | b[d + 5] << 8 | b[d + 6] << 16);
            int h = 1 + (b[d + 7] | b[d + 8] << 8 | b[d + 9] << 16);
            if (!animated) return new MediaInfo(MediaFormat.WebP, w, h, 1, 0);

            int frames = 0;
            long ms = 0;
            long pos = (long)d + size + (size & 1);
            while (pos <= b.Length - 8)
            {
                int p = (int)pos;
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(b[(p + 4)..]);
                if (b.Slice(p, 4).SequenceEqual("ANMF"u8))
                {
                    frames++;
                    if (len >= 16 && (long)p + 8 + 16 <= b.Length)
                        ms += b[p + 8 + 12] | b[p + 8 + 13] << 8 | b[p + 8 + 14] << 16;
                }
                pos += 8L + len + (len & 1);
            }
            if (frames == 0) return Fail(out problem, MalformedCode, "WebP: animation flag set but no frames.");
            return new MediaInfo(MediaFormat.WebP, w, h, frames, ms / 1000.0);
        }

        return Fail(out problem, MalformedCode, "WebP: the first chunk is not VP8, VP8L or VP8X.");
    }

    // ---------------------------------------------------------------- SVG

    /// <summary>True when the bytes are XML text that <see cref="Inspect(ReadOnlySpan{byte}, out MediaProblem?)"/> reads as SVG.</summary>
    internal static bool LooksLikeSvg(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) b = b[3..];
        int i = 0;
        while (i < b.Length && (b[i] == ' ' || b[i] == '\t' || b[i] == '\r' || b[i] == '\n')) i++;
        b = b[i..];
        if (!(b.StartsWith("<?xml"u8) || b.StartsWith("<svg"u8) || b.StartsWith("<!--"u8))) return false;
        return b.IndexOf("<svg"u8) >= 0;
    }

    /// <summary>A width or height: absent (null), or a non-negative number, unitless or in px. False for anything else.</summary>
    internal static bool TryLength(string? s, out double? value)
    {
        value = null;
        if (s is null) return true;
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2].TrimEnd();
        if (s.Length == 0 || !(char.IsAsciiDigit(s[0]) || s[0] is '.' or '+')) return false;
        value = ParseNumber(s);
        return value is not null;
    }

    internal static double? ParseNumber(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) && d >= 0 ? d : null;

    internal static int ToPixels(double? v) =>
        v is null ? 0 : v.Value >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(v.Value);
}
