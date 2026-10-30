using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InfoPanel.Spec;

/// <summary>
/// Reads a video file's container (ISO BMFF: mp4 and mov; Matroska: mkv and webm; AVI) from its headers, without
/// decoding anything, before any decoder sees the file (spec/docs/smart-nodes.md, "Video files"): the first video
/// track's codec, coded size, frame count and duration. A codec outside the spec's list is refused (<c>media.codec</c>).
/// <para>
/// Safe on any bytes, as <see cref="MediaProbe"/> is: it reads through fixed buffers only, allocates nothing from a size
/// a header claims, never recurses into what a file nests (it descends only into the boxes and elements it knows, at the
/// depth it expects them), and every step advances through the file, within <see cref="MaxElements"/> steps and
/// <see cref="MaxReadBytes"/> bytes read. An mp4 whose <c>moov</c> follows its media data is found by skipping the
/// <c>mdat</c> box by its size, not by reading it.
/// </para>
/// </summary>
public static class VideoProbe
{
    public const string CodecCode = "media.codec";

    /// <summary>The codecs a video may use (the spec's list), as <see cref="MediaInfo.Codec"/> names them.</summary>
    public static IReadOnlyList<string> Codecs { get; } = ["h264", "hevc", "vp8", "vp9", "av1", "mpeg4", "mjpeg", "prores"];

    /// <summary>Boxes, elements or chunks the probe looks at, at most; a file with more is refused.</summary>
    internal const int MaxElements = 100_000;

    /// <summary>Bytes the probe reads, at most (headers only: tables and media data are skipped by their sizes).</summary>
    internal const int MaxReadBytes = 1 << 20;

    private static ReadOnlySpan<byte> EbmlMagic => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>The containers the probe reads.</summary>
    internal enum Container { None, IsoBmff, Matroska, Avi }

    /// <summary>
    /// Which container <paramref name="head"/> (a file's first bytes, at least 12 to tell all three) starts. With
    /// <see cref="Container.None"/>, <paramref name="incomplete"/> says the bytes are a prefix of a video's signature too
    /// short to tell.
    /// </summary>
    internal static Container Detect(ReadOnlySpan<byte> head, out bool incomplete)
    {
        incomplete = false;
        if (head.Length >= 4 && head[..4].SequenceEqual(EbmlMagic)) return Container.Matroska;
        if (head.Length is > 0 and < 4 && EbmlMagic[..head.Length].SequenceEqual(head))
        {
            incomplete = true;
            return Container.None;
        }
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("AVI "u8)) return Container.Avi;
        if (head.Length >= 8 && IsIsoTopBox(head.Slice(4, 4))) return Container.IsoBmff;
        return Container.None;
    }

    private static bool IsIsoTopBox(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("free"u8)
        || type.SequenceEqual("skip"u8) || type.SequenceEqual("wide"u8);

    /// <summary>Whether <paramref name="format"/> is a video container.</summary>
    public static bool IsVideo(MediaFormat format) => format is MediaFormat.Mp4 or MediaFormat.Matroska or MediaFormat.Avi;

    /// <summary>
    /// As <see cref="Inspect(Stream, out MediaProblem?)"/>, over bytes in memory (read in place, not copied).
    /// </summary>
    public static unsafe MediaInfo? Inspect(ReadOnlySpan<byte> bytes, out MediaProblem? problem)
    {
        fixed (byte* p = bytes)
        {
            using var stream = new UnmanagedMemoryStream(p, bytes.Length);
            return Inspect(stream, out problem);
        }
    }

    /// <summary>
    /// The first video track of the file <paramref name="stream"/> holds (seekable, read from its start; its position is
    /// left anywhere), or null with <paramref name="problem"/>: <c>media.format</c> (not one of the containers, or no
    /// video track), <c>media.truncated</c>, <c>media.malformed</c>, or <c>media.codec</c>. Never throws for what the file
    /// holds.
    /// </summary>
    public static MediaInfo? Inspect(Stream stream, out MediaProblem? problem)
    {
        problem = null;
        try
        {
            var source = new Source(stream);
            Span<byte> head = stackalloc byte[12];
            var n = source.Read(0, head);
            return Detect(head[..n], out var incomplete) switch
            {
                Container.IsoBmff => new IsoReader(source).Read(out problem),
                Container.Matroska => new MatroskaReader(source).Read(out problem),
                Container.Avi => new AviReader(source).Read(out problem),
                _ => Fail(out problem, incomplete ? MediaProbe.TruncatedCode : MediaProbe.FormatCode,
                    incomplete ? "The video's signature is incomplete." : "Not a video container (mp4, mov, mkv, webm or avi)."),
            };
        }
        catch (ProbeException e)
        {
            return Fail(out problem, e.Code, e.Message);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        {
            return Fail(out problem, MediaProbe.MalformedCode, "The video could not be read.");
        }
    }

    private static MediaInfo? Fail(out MediaProblem? problem, string code, string message)
    {
        problem = new MediaProblem(code, message);
        return null;
    }

    private sealed class ProbeException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private static ProbeException Malformed(string message) => new(MediaProbe.MalformedCode, message);

    private static ProbeException Truncated(string message) => new(MediaProbe.TruncatedCode, message);

    /// <summary>The track the probe found, before the limits.</summary>
    private static MediaInfo Result(MediaFormat format, string? codec, string tag, long width, long height, long frames, double seconds)
    {
        if (codec is null)
            throw new ProbeException(CodecCode, $"The video's codec ({tag}) isn't one of {string.Join(", ", Codecs)}.");
        if (width < 1 || height < 1 || width > int.MaxValue || height > int.MaxValue)
            throw Malformed("The video's width and height must be 1 to 2^31-1.");
        var duration = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
        var count = frames is > 0 and <= int.MaxValue ? (int)frames : 1;
        return new MediaInfo(format, (int)width, (int)height, count, duration, Codec: codec);
    }

    /// <summary>Reads at offsets of a seekable stream, counting what it reads and the elements it looks at.</summary>
    private sealed class Source(Stream stream)
    {
        private long _read;
        private int _elements;

        public long Length { get; } = stream.Length;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= Length) return 0;
            _read += buffer.Length;
            if (_read > MaxReadBytes) throw Malformed("The video's headers are too large to read.");
            stream.Position = offset;
            var total = 0;
            while (total < buffer.Length)
            {
                var n = stream.Read(buffer[total..]);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Reads exactly <paramref name="buffer"/>'s length, or the file is truncated.</summary>
        public void ReadExactly(long offset, Span<byte> buffer, string what)
        {
            if (Read(offset, buffer) != buffer.Length) throw Truncated($"The video's {what} is incomplete.");
        }

        public void Step()
        {
            if (++_elements > MaxElements) throw Malformed("The video's container has too many boxes or elements.");
        }
    }

    // ---------------------------------------------------------------- ISO BMFF (mp4, mov)

    private sealed class IsoReader(Source source)
    {
        private readonly record struct Box(string Type, long Start, long Payload, long End);

        private string? _codec;
        private string _tag = "";
        private long _width, _height, _frames;
        private double _seconds = double.NaN, _movieSeconds = double.NaN;
        private bool _found;

        public MediaInfo? Read(out MediaProblem? problem)
        {
            problem = null;
            var first = true;
            var moov = false;
            foreach (var box in Children(0, source.Length, top: true))
            {
                if (first && box.Type == "ftyp") CheckBrand(box);
                first = false;
                if (box.Type == "moov")
                {
                    ReadMoov(box);
                    moov = true;
                    break;
                }
            }
            if (!moov) throw Malformed("The video has no moov box.");
            if (!_found) return Fail(out problem, MediaProbe.FormatCode, "The file has no video track.");
            var seconds = double.IsNaN(_seconds) ? _movieSeconds : _seconds;
            return Result(MediaFormat.Mp4, _codec, _tag, _width, _height, _frames, seconds);
        }

        /// <summary>HEIF and AVIF images are ISO BMFF too, and not videos.</summary>
        private void CheckBrand(Box ftyp)
        {
            Span<byte> brand = stackalloc byte[4];
            if (ftyp.End - ftyp.Payload < 4) throw Malformed("The ftyp box is too short.");
            source.ReadExactly(ftyp.Payload, brand, "ftyp box");
            var name = Encoding.ASCII.GetString(brand);
            if (name is "avif" or "avis" or "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1")
                throw new ProbeException(MediaProbe.FormatCode, $"An image container ({name}), not a video.");
        }

        /// <summary>
        /// The boxes from <paramref name="start"/> to <paramref name="end"/>, each checked to lie within it. A box of size 0
        /// runs to <paramref name="end"/>. A box past the end of the file is truncated; past its parent, malformed.
        /// </summary>
        private IEnumerable<Box> Children(long start, long end, bool top = false)
        {
            var pos = start;
            var header = new byte[16];
            while (pos < end)
            {
                source.Step();
                if (end - pos < 8)
                {
                    if (top && pos + 8 > source.Length) throw Truncated("A box header is incomplete.");
                    throw Malformed("A box header doesn't fit in its parent.");
                }
                source.ReadExactly(pos, header.AsSpan(0, 8), "box header");
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                long payload = pos + 8;
                if (size == 1)
                {
                    if (end - pos < 16) throw top && pos + 16 > source.Length ? Truncated("A box header is incomplete.") : Malformed("A box header doesn't fit in its parent.");
                    source.ReadExactly(pos + 8, header.AsSpan(8, 8), "box header");
                    var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    if (large > long.MaxValue) throw Malformed($"The {Printable(type)} box's size is out of range.");
                    size = (long)large;
                    payload = pos + 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }
                if (size < payload - pos) throw Malformed($"The {Printable(type)} box's size is smaller than its header.");
                var boxEnd = pos + size;
                if (boxEnd > end)
                {
                    if (boxEnd > source.Length && end >= source.Length) throw Truncated($"The {Printable(type)} box runs past the end of the file.");
                    throw Malformed($"The {Printable(type)} box runs past its parent.");
                }
                yield return new Box(type, pos, payload, boxEnd);
                pos = boxEnd;
            }
        }

        private void ReadMoov(Box moov)
        {
            foreach (var box in Children(moov.Payload, moov.End))
            {
                if (box.Type == "mvhd") _movieSeconds = ReadTimeHeader(box);
                else if (box.Type == "trak" && !_found) ReadTrak(box);
            }
        }

        private void ReadTrak(Box trak)
        {
            foreach (var box in Children(trak.Payload, trak.End))
                if (box.Type == "mdia") ReadMdia(box);
        }

        private void ReadMdia(Box mdia)
        {
            Box? minf = null;
            double seconds = double.NaN;
            var video = false;
            Span<byte> hdlr = stackalloc byte[12];
            foreach (var box in Children(mdia.Payload, mdia.End))
            {
                switch (box.Type)
                {
                    case "mdhd": seconds = ReadTimeHeader(box); break;
                    case "hdlr":
                        if (box.End - box.Payload < 12) throw Malformed("The hdlr box is too short.");
                        source.ReadExactly(box.Payload, hdlr, "hdlr box");
                        video = hdlr.Slice(8, 4).SequenceEqual("vide"u8);
                        break;
                    case "minf": minf = box; break;
                }
            }
            if (!video || minf is not { } m) return;
            foreach (var box in Children(m.Payload, m.End))
            {
                if (box.Type != "stbl") continue;
                foreach (var table in Children(box.Payload, box.End))
                {
                    if (table.Type == "stsd") ReadStsd(table);
                    else if (table.Type == "stsz" || table.Type == "stz2") ReadSampleCount(table);
                }
            }
            if (_tag.Length == 0) throw Malformed("The video track has no sample description.");
            _seconds = seconds;
            _found = true;
        }

        /// <summary>An mvhd's or mdhd's duration in seconds (NaN when unknown).</summary>
        private double ReadTimeHeader(Box box)
        {
            Span<byte> b = stackalloc byte[32];
            var length = (int)Math.Min(32, box.End - box.Payload);
            if (length < 4) throw Malformed($"The {box.Type} box is too short.");
            source.ReadExactly(box.Payload, b[..length], $"{box.Type} box");
            if (b[0] == 1)
            {
                if (length < 32) throw Malformed($"The {box.Type} box is too short.");
                var scale = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
                var duration = BinaryPrimitives.ReadUInt64BigEndian(b[24..]);
                return scale == 0 || duration == ulong.MaxValue ? double.NaN : duration / (double)scale;
            }
            if (length < 20) throw Malformed($"The {box.Type} box is too short.");
            var scale0 = BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
            var duration0 = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
            return scale0 == 0 || duration0 == uint.MaxValue ? double.NaN : duration0 / (double)scale0;
        }

        private void ReadStsd(Box stsd)
        {
            // version and flags (4), entry count (4); the first entry: size (4), format (4), reserved (6), data
            // reference index (2), pre-defined and reserved (16), width (2), height (2).
            Span<byte> b = stackalloc byte[44];
            if (stsd.End - stsd.Payload < 44) throw Malformed("The video's sample description is too short.");
            source.ReadExactly(stsd.Payload, b, "sample description");
            if (BinaryPrimitives.ReadUInt32BigEndian(b[4..]) < 1) throw Malformed("The video's sample description has no entries.");
            var entry = BinaryPrimitives.ReadUInt32BigEndian(b[8..]);
            if (entry < 36 || entry > stsd.End - stsd.Payload - 8) throw Malformed("The video's sample entry has a bad size.");
            _tag = Encoding.ASCII.GetString(b.Slice(12, 4));
            _codec = _tag switch
            {
                "avc1" or "avc3" => "h264",
                "hvc1" or "hev1" => "hevc",
                "vp08" => "vp8",
                "vp09" => "vp9",
                "av01" => "av1",
                "mp4v" => "mpeg4",
                "jpeg" or "mjpa" or "mjpb" => "mjpeg",
                "apch" or "apcn" or "apcs" or "apco" or "ap4h" or "ap4x" => "prores",
                _ => null,
            };
            _tag = Printable(_tag);
            _width = BinaryPrimitives.ReadUInt16BigEndian(b[40..]);
            _height = BinaryPrimitives.ReadUInt16BigEndian(b[42..]);
        }

        private void ReadSampleCount(Box box)
        {
            // version and flags (4), then stsz: sample size (4), sample count (4); stz2: reserved and field size (4), count (4).
            Span<byte> b = stackalloc byte[12];
            if (box.End - box.Payload < 12) throw Malformed($"The {box.Type} box is too short.");
            source.ReadExactly(box.Payload, b, $"{box.Type} box");
            _frames = BinaryPrimitives.ReadUInt32BigEndian(b[8..]);
        }
    }

    // ---------------------------------------------------------------- Matroska (mkv, webm)

    private sealed class MatroskaReader(Source source)
    {
        private const uint EbmlId = 0x1A45DFA3, DocTypeId = 0x4282, SegmentId = 0x18538067, InfoId = 0x1549A966,
            TimecodeScaleId = 0x2AD7B1, DurationId = 0x4489, TracksId = 0x1654AE6B, TrackEntryId = 0xAE, TrackTypeId = 0x83,
            CodecIdId = 0x86, DefaultDurationId = 0x23E383, VideoId = 0xE0, PixelWidthId = 0xB0, PixelHeightId = 0xBA,
            ClusterId = 0x1F43B675;

        private readonly record struct Element(uint Id, long Payload, long End, bool UnknownSize);

        public MediaInfo? Read(out MediaProblem? problem)
        {
            problem = null;
            var header = Next(0, source.Length);
            if (header.Id != EbmlId || header.UnknownSize) throw Malformed("The file doesn't start with an EBML header.");
            string? docType = null;
            for (var e = header.Payload; e < header.End;)
            {
                var child = Next(e, header.End);
                if (child.Id == DocTypeId) docType = ReadString(child, 32);
                e = child.End;
            }
            if (docType is not ("matroska" or "webm"))
                return Fail(out problem, MediaProbe.FormatCode, "An EBML file that isn't Matroska or WebM.");

            if (header.End >= source.Length) throw Truncated("The file ends after its EBML header.");
            var segment = Next(header.End, source.Length, unknownAllowed: true);
            if (segment.Id != SegmentId) throw Malformed("The EBML header isn't followed by a Segment.");

            double scale = 1_000_000, duration = double.NaN;
            (string? Codec, string Tag, long Width, long Height, long DefaultDuration)? track = null;
            bool info = false, tracks = false;
            for (var pos = segment.Payload; pos < segment.End && !(info && tracks);)
            {
                if (segment.End - pos < 2 && segment.UnknownSize) break;
                var child = Next(pos, segment.End, unknownAllowed: true);
                if (child.Id == ClusterId)
                {
                    // Media data: the headers come before it in any file a muxer writes; one of unknown size can't be skipped.
                    if (child.UnknownSize) break;
                }
                else if (child.UnknownSize)
                {
                    throw Malformed("Only a Segment or a Cluster may have an unknown size.");
                }
                else if (child.Id == InfoId)
                {
                    info = true;
                    for (var e = child.Payload; e < child.End;)
                    {
                        var field = Next(e, child.End);
                        if (field.Id == TimecodeScaleId) scale = ReadUInt(field);
                        else if (field.Id == DurationId) duration = ReadFloat(field);
                        e = field.End;
                    }
                }
                else if (child.Id == TracksId)
                {
                    tracks = true;
                    for (var e = child.Payload; e < child.End && track is null;)
                    {
                        var entry = Next(e, child.End);
                        if (entry.Id == TrackEntryId) track = ReadTrack(entry);
                        e = entry.End;
                    }
                }
                pos = child.End;
            }
            if (!tracks) throw Malformed("The file has no Tracks before its media data.");
            if (track is not { } t) return Fail(out problem, MediaProbe.FormatCode, "The file has no video track.");
            var seconds = duration * scale / 1e9;
            var frames = t.DefaultDuration > 0 && double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds * 1e9 / t.DefaultDuration) : 0;
            return Result(MediaFormat.Matroska, t.Codec, t.Tag, t.Width, t.Height, frames, seconds);
        }

        private (string? Codec, string Tag, long Width, long Height, long DefaultDuration)? ReadTrack(Element entry)
        {
            long type = 0, width = 0, height = 0, defaultDuration = 0;
            string? codecId = null;
            for (var e = entry.Payload; e < entry.End;)
            {
                var field = Next(e, entry.End);
                switch (field.Id)
                {
                    case TrackTypeId: type = ReadUInt(field); break;
                    case CodecIdId: codecId = ReadString(field, 64); break;
                    case DefaultDurationId: defaultDuration = ReadUInt(field); break;
                    case VideoId:
                        for (var v = field.Payload; v < field.End;)
                        {
                            var size = Next(v, field.End);
                            if (size.Id == PixelWidthId) width = ReadUInt(size);
                            else if (size.Id == PixelHeightId) height = ReadUInt(size);
                            v = size.End;
                        }
                        break;
                }
                e = field.End;
            }
            if (type != 1) return null;
            var tag = codecId ?? "none";
            var codec = tag switch
            {
                "V_MPEG4/ISO/AVC" => "h264",
                "V_MPEGH/ISO/HEVC" => "hevc",
                "V_VP8" => "vp8",
                "V_VP9" => "vp9",
                "V_AV1" => "av1",
                "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/AP" => "mpeg4",
                "V_MJPEG" => "mjpeg",
                "V_PRORES" => "prores",
                _ => null,
            };
            return (codec, Printable(tag), width, height, defaultDuration);
        }

        /// <summary>The element at <paramref name="pos"/>, within <paramref name="end"/>.</summary>
        private Element Next(long pos, long end, bool unknownAllowed = false)
        {
            source.Step();
            Span<byte> b = stackalloc byte[12];
            var n = source.Read(pos, b[..(int)Math.Min(12, Math.Max(0, end - pos))]);
            var head = b[..n];
            if (head.Length == 0) throw Truncated("An element is incomplete.");
            var idLength = VintLength(head[0]);
            if (idLength is 0 or > 4) throw Malformed("An element ID is malformed.");
            if (head.Length < idLength + 1) throw Incomplete(pos, end);
            uint id = 0;
            for (var i = 0; i < idLength; i++) id = (id << 8) | head[i];
            var sizeLength = VintLength(head[idLength]);
            if (sizeLength == 0) throw Malformed("An element size is malformed.");
            if (head.Length < idLength + sizeLength) throw Incomplete(pos, end);
            ulong size = (ulong)(head[idLength] & (0xFF >> sizeLength));
            var allOnes = size == (ulong)(0xFF >> sizeLength);
            for (var i = 1; i < sizeLength; i++)
            {
                size = (size << 8) | head[idLength + i];
                allOnes &= head[idLength + i] == 0xFF;
            }
            long payload = pos + idLength + sizeLength;
            if (allOnes)
            {
                if (!unknownAllowed) throw Malformed("Only a Segment or a Cluster may have an unknown size.");
                return new Element(id, payload, end, true);
            }
            if (size > (ulong)(long.MaxValue - payload)) throw Malformed("An element's size is out of range.");
            var elementEnd = payload + (long)size;
            if (elementEnd > end)
            {
                if (elementEnd > source.Length && end >= source.Length) throw Truncated("An element runs past the end of the file.");
                throw Malformed("An element runs past its parent.");
            }
            return new Element(id, payload, elementEnd, false);
        }

        private ProbeException Incomplete(long pos, long end) =>
            end >= source.Length ? Truncated("An element header is incomplete.") : Malformed("An element header doesn't fit in its parent.");

        private static int VintLength(byte first)
        {
            for (var i = 0; i < 8; i++)
                if ((first & (0x80 >> i)) != 0) return i + 1;
            return 0;
        }

        private ulong ReadRaw(Element e, int max)
        {
            var length = e.End - e.Payload;
            if (length > max) throw Malformed("An integer element is too long.");
            Span<byte> b = stackalloc byte[8];
            source.ReadExactly(e.Payload, b[..(int)length], "element");
            ulong v = 0;
            for (var i = 0; i < length; i++) v = (v << 8) | b[i];
            return v;
        }

        private long ReadUInt(Element e)
        {
            var v = ReadRaw(e, 8);
            return v > long.MaxValue ? long.MaxValue : (long)v;
        }

        private double ReadFloat(Element e)
        {
            var length = e.End - e.Payload;
            var bits = ReadRaw(e, 8);
            return length switch
            {
                0 => 0,
                4 => BitConverter.Int32BitsToSingle((int)bits),
                8 => BitConverter.Int64BitsToDouble((long)bits),
                _ => throw Malformed("A float element is neither 4 nor 8 bytes."),
            };
        }

        private string ReadString(Element e, int max)
        {
            var length = e.End - e.Payload;
            if (length > max) return "(too long)";
            Span<byte> b = stackalloc byte[64];
            source.ReadExactly(e.Payload, b[..(int)length], "element");
            var text = b[..(int)length];
            var nul = text.IndexOf((byte)0);
            return Encoding.ASCII.GetString(nul >= 0 ? text[..nul] : text);
        }
    }

    // ---------------------------------------------------------------- AVI

    private sealed class AviReader(Source source)
    {
        private readonly record struct Chunk(string Id, string ListType, long Payload, long End);

        public MediaInfo? Read(out MediaProblem? problem)
        {
            problem = null;
            Span<byte> riff = stackalloc byte[12];
            source.ReadExactly(0, riff, "RIFF header");
            var riffEnd = Math.Min(8L + BinaryPrimitives.ReadUInt32LittleEndian(riff[4..]), source.Length);
            Span<byte> avih = stackalloc byte[40];
            foreach (var chunk in Chunks(12, riffEnd))
            {
                if (chunk.Id != "LIST" || chunk.ListType != "hdrl") continue;
                long totalFrames = 0;
                foreach (var header in Chunks(chunk.Payload, chunk.End))
                {
                    if (header.Id == "avih")
                    {
                        if (header.End - header.Payload < 40) throw Malformed("The AVI main header is too short.");
                        source.ReadExactly(header.Payload, avih, "AVI main header");
                        totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(avih[16..]);
                    }
                    else if (header.Id == "LIST" && header.ListType == "strl" && ReadStream(header, totalFrames) is { } found)
                    {
                        return found;
                    }
                }
                return Fail(out problem, MediaProbe.FormatCode, "The file has no video stream.");
            }
            throw Malformed("The AVI file has no header list.");
        }

        private MediaInfo? ReadStream(Chunk strl, long totalFrames)
        {
            Span<byte> strh = stackalloc byte[36];
            Span<byte> strf = stackalloc byte[20];
            bool video = false, format = false;
            foreach (var chunk in Chunks(strl.Payload, strl.End))
            {
                if (chunk.Id == "strh")
                {
                    if (chunk.End - chunk.Payload < 36) throw Malformed("The AVI stream header is too short.");
                    source.ReadExactly(chunk.Payload, strh, "AVI stream header");
                    video = strh[..4].SequenceEqual("vids"u8);
                    if (!video) return null;
                }
                else if (chunk.Id == "strf" && video)
                {
                    if (chunk.End - chunk.Payload < 20) throw Malformed("The AVI stream format is too short.");
                    source.ReadExactly(chunk.Payload, strf, "AVI stream format");
                    format = true;
                }
            }
            if (!video) return null;
            if (!format) throw Malformed("The AVI video stream has no format.");
            var scale = BinaryPrimitives.ReadUInt32LittleEndian(strh[20..]);
            var rate = BinaryPrimitives.ReadUInt32LittleEndian(strh[24..]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(strh[32..]);
            var width = (long)BinaryPrimitives.ReadInt32LittleEndian(strf[4..]);
            var height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(strf[8..]));
            var fourcc = Encoding.ASCII.GetString(strf.Slice(16, 4));
            var codec = fourcc.ToUpperInvariant() switch
            {
                "H264" or "X264" or "AVC1" or "DAVC" => "h264",
                "HEVC" or "H265" or "HVC1" => "hevc",
                "VP80" => "vp8",
                "VP90" => "vp9",
                "AV01" => "av1",
                "FMP4" or "DIVX" or "DX50" or "XVID" or "MP4V" or "MP4S" or "M4S2" or "3IV2" => "mpeg4",
                "MJPG" or "AVRN" or "AVDJ" or "DMB1" => "mjpeg",
                _ => null,
            };
            var frames = length > 0 ? length : totalFrames;
            var seconds = rate > 0 ? frames * (double)scale / rate : 0;
            return Result(MediaFormat.Avi, codec, Printable(fourcc), width, height, frames, seconds);
        }

        /// <summary>The chunks from <paramref name="start"/> to <paramref name="end"/>, each padded to an even size.</summary>
        private IEnumerable<Chunk> Chunks(long start, long end)
        {
            var pos = start;
            var header = new byte[12];
            while (end - pos >= 8)
            {
                source.Step();
                source.ReadExactly(pos, header.AsSpan(0, 8), "chunk header");
                var id = Encoding.ASCII.GetString(header, 0, 4);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var payload = pos + 8;
                var chunkEnd = payload + size;
                if (chunkEnd > end)
                {
                    if (chunkEnd > source.Length && end >= source.Length) throw Truncated($"The {Printable(id)} chunk runs past the end of the file.");
                    throw Malformed($"The {Printable(id)} chunk runs past its parent.");
                }
                var listType = "";
                if (id is "LIST" or "RIFF")
                {
                    if (size < 4) throw Malformed("A list chunk is too short.");
                    source.ReadExactly(payload, header.AsSpan(8, 4), "list type");
                    listType = Encoding.ASCII.GetString(header, 8, 4);
                    payload += 4;
                }
                yield return new Chunk(id, listType, payload, chunkEnd);
                pos = chunkEnd + (size & 1);
            }
        }
    }

    /// <summary>A four-character code or codec id as text a message can carry: printable ASCII, else escaped.</summary>
    private static string Printable(string tag)
    {
        var b = new StringBuilder(tag.Length);
        foreach (var c in tag) b.Append(c is >= ' ' and <= '~' ? c : '?');
        return b.ToString();
    }
}
