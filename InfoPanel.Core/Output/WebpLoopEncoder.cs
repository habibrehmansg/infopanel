using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SkiaSharp;

namespace InfoPanel.Output;

/// <summary>One rendered frame of a loop and how long it shows.</summary>
public readonly record struct LoopFrame(SKBitmap Bitmap, TimeSpan Duration);

/// <summary>
/// <see cref="WebpLoopEncoder.EncodeWithin"/>'s answer: the file (null when no quality fits), its key frames, its quality
/// (null: lossless) and its size, or the smallest size reached when none fits.
/// </summary>
public sealed record LadderResult(byte[]? Webp, int[] KeyFrames, int? Quality, long Size);

/// <summary>
/// Encodes a preview recorder's rendered frames as one animated WebP (RIFF/WEBP with a VP8X header, an ANIM chunk and
/// one ANMF chunk a frame), through Skia's own WebP encoder. Every frame must have the first frame's size.
/// </summary>
public static class WebpLoopEncoder
{
    /// <summary>
    /// The frames as animated WebP bytes: lossy at <paramref name="quality"/> (0–100), or lossless when it is null.
    /// Throws when there is no frame, a frame's size differs from the first's, or Skia can't encode them.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<LoopFrame> frames, int? quality)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("A loop needs at least one frame.", nameof(frames));
        if (quality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Quality is 0 to 100.");

        var width = frames[0].Bitmap.Width;
        var height = frames[0].Bitmap.Height;
        var encoderFrames = new SKWebpEncoderFrame[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Bitmap.Width != width || frame.Bitmap.Height != height)
                throw new ArgumentException($"Frame {i} is {frame.Bitmap.Width}×{frame.Bitmap.Height}, not {width}×{height}.", nameof(frames));
            if (frame.Duration <= TimeSpan.Zero)
                throw new ArgumentException($"Frame {i} has no duration.", nameof(frames));
            encoderFrames[i] = new SKWebpEncoderFrame(frame.Bitmap, frame.Duration);
        }

        var options = quality is { } q
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, q)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75);
        using var data = SKWebpEncoder.EncodeAnimated(encoderFrames, options)
            ?? throw new InvalidOperationException("Skia couldn't encode the loop as WebP.");
        return data.ToArray();
    }

    /// <summary>
    /// Encodes each run alone and joins the runs' frames under the first run's header, each run's first frame flagged
    /// no-blend, so it is whole on its own (a key frame). Returns the file and the index of each run's first frame in it
    /// (the encoder folds identical neighbours, so file indices aren't input indices). <paramref name="cancel"/> is
    /// checked before each run's encoding and after it.
    /// </summary>
    public static (byte[] Webp, int[] KeyFrames) EncodeRuns(IReadOnlyList<IReadOnlyList<LoopFrame>> runs, int? quality,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            throw new ArgumentException("A loop needs at least one run.", nameof(runs));

        var width = runs[0].Count > 0 ? runs[0][0].Bitmap.Width : 0;
        var height = runs[0].Count > 0 ? runs[0][0].Bitmap.Height : 0;
        using var body = new MemoryStream();
        var keyFrames = new int[runs.Count];
        var frameCount = 0;
        var alpha = false;
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Count == 0 || run[0].Bitmap.Width != width || run[0].Bitmap.Height != height)
                throw new ArgumentException($"Run {r} is empty or its canvas differs from the first run's.", nameof(runs));
            cancel.ThrowIfCancellationRequested();
            var webp = Encode(run, quality);
            cancel.ThrowIfCancellationRequested();
            var chunks = Chunks(webp);
            keyFrames[r] = frameCount;
            var animated = chunks.Exists(c => c.Id == "ANMF");
            if (animated)
            {
                alpha |= (webp[chunks[0].Start + 8] & 0x10) != 0;
                var first = true;
                foreach (var chunk in chunks)
                {
                    if (chunk.Id != "ANMF")
                        continue;
                    var bytes = webp.AsSpan(chunk.Start, chunk.Length).ToArray();
                    if (first)
                    {
                        // ANMF payload byte 15: reserved (6 bits), blending (1 = don't blend), disposal.
                        bytes[8 + 15] |= 0x02;
                        first = false;
                    }
                    body.Write(bytes);
                    frameCount++;
                }
            }
            else
            {
                // Every frame of the run alike: libwebp wrote a still. It becomes one whole frame lasting the run.
                using var image = new MemoryStream();
                foreach (var chunk in chunks)
                {
                    if (chunk.Id is "ALPH" or "VP8 " or "VP8L")
                    {
                        image.Write(webp.AsSpan(chunk.Start, chunk.Length));
                        alpha |= chunk.Id is "ALPH" or "VP8L";
                    }
                }
                var ms = 0L;
                foreach (var frame in run)
                    ms += (long)frame.Duration.TotalMilliseconds;
                Span<byte> anmf = stackalloc byte[8 + 16];
                "ANMF"u8.CopyTo(anmf);
                BinaryPrimitives.WriteUInt32LittleEndian(anmf[4..], (uint)(16 + image.Length));
                anmf[8..14].Clear(); // X/2 and Y/2: 0
                WriteUInt24(anmf[14..], width - 1);
                WriteUInt24(anmf[17..], height - 1);
                WriteUInt24(anmf[20..], (int)Math.Min(ms, 0xFFFFFF));
                anmf[23] = 0x02; // don't blend, don't dispose
                body.Write(anmf);
                body.Write(image.GetBuffer().AsSpan(0, (int)image.Length));
                frameCount++;
            }
        }

        // VP8X (animation, and alpha when any run has it; the canvas) and ANIM (no background colour, loop forever).
        Span<byte> header = stackalloc byte[18 + 14];
        "VP8X"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 10);
        header[8] = (byte)(0x02 | (alpha ? 0x10 : 0));
        header[9..12].Clear();
        WriteUInt24(header[12..], width - 1);
        WriteUInt24(header[15..], height - 1);
        "ANIM"u8.CopyTo(header[18..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], 6);
        header[26..32].Clear();

        var size = 4 + header.Length + body.Length;
        var file = new byte[8 + size];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)size);
        "WEBP"u8.CopyTo(file.AsSpan(8));
        header.CopyTo(file.AsSpan(12));
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(file.AsSpan(12 + header.Length));
        return (file, keyFrames);
    }

    /// <summary>The qualities a loop is tried at, best first: lossless (null), then lossy 85 and 70.</summary>
    public static IReadOnlyList<int?> Ladder { get; } = [null, 85, 70];

    /// <summary>
    /// <see cref="EncodeRuns"/> at the first of <see cref="Ladder"/> whose file is at most <paramref name="maxBytes"/>:
    /// that file, its key frames and its quality (null: lossless); when none fits, no file and the size of the last
    /// tried (the smallest the ladder reached). <paramref name="cancel"/> is checked before and after each run's encoding.
    /// </summary>
    public static LadderResult EncodeWithin(IReadOnlyList<IReadOnlyList<LoopFrame>> runs, long maxBytes, CancellationToken cancel = default)
    {
        long reached = 0;
        foreach (var quality in Ladder)
        {
            var (webp, keyFrames) = EncodeRuns(runs, quality, cancel);
            if (webp.Length <= maxBytes)
                return new LadderResult(webp, keyFrames, quality, webp.Length);
            reached = webp.Length;
        }
        return new LadderResult(null, [], null, reached);
    }

    private static void WriteUInt24(Span<byte> to, int value)
    {
        to[0] = (byte)value;
        to[1] = (byte)(value >> 8);
        to[2] = (byte)(value >> 16);
    }

    /// <summary>The top-level chunks of a RIFF/WEBP file: id, where the chunk starts and its length with its header and padding.</summary>
    private static List<(string Id, int Start, int Length)> Chunks(byte[] webp)
    {
        var chunks = new List<(string, int, int)>();
        if (webp.Length < 12 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidOperationException("Not a WebP file.");
        var at = 12;
        while (at + 8 <= webp.Length)
        {
            var id = Encoding.ASCII.GetString(webp, at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            var length = 8 + (long)size + (size & 1);
            if (at + length > webp.Length)
                throw new InvalidOperationException("A WebP chunk runs past the file's end.");
            chunks.Add((id, at, (int)length));
            at += (int)length;
        }
        return chunks;
    }
}
