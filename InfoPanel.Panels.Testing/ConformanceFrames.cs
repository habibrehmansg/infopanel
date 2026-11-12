using System;
using System.Collections.Generic;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Testing;

/// <summary>
/// Frames for a driver's tests, in any of the SDK's formats, without an image library: a raw frame is a pattern of the
/// format's bytes (never uniform, so one frame can't pass for another), and a JPEG is a baseline JPEG of the size asked
/// for, mid-grey, which any decoder reads. The conformance suite writes these unless its options give it others.
/// </summary>
public static class ConformanceFrames
{
    /// <summary>A frame of <paramref name="width"/> × <paramref name="height"/> pixels in <paramref name="format"/>.</summary>
    public static byte[] Of(PanelFrameFormat format, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        return format switch
        {
            PanelFrameFormat.Jpeg => GreyJpeg(width, height),
            PanelFrameFormat.Rgb565Le or PanelFrameFormat.Rgb565Be or PanelFrameFormat.Rgb565LeTruncated => Pattern(width, height, 2, opaque: false),
            PanelFrameFormat.Rgb888 or PanelFrameFormat.Bgr888 => Pattern(width, height, 3, opaque: false),
            PanelFrameFormat.Rgba8888 => Pattern(width, height, 4, opaque: true),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "a format the SDK doesn't have"),
        };
    }

    /// <summary>The bytes a pixel of a raw format; null for <see cref="PanelFrameFormat.Jpeg"/> (or a format the SDK doesn't have).</summary>
    public static int? BytesPerPixel(PanelFrameFormat format) => format switch
    {
        PanelFrameFormat.Rgb565Le or PanelFrameFormat.Rgb565Be or PanelFrameFormat.Rgb565LeTruncated => 2,
        PanelFrameFormat.Rgb888 or PanelFrameFormat.Bgr888 => 3,
        PanelFrameFormat.Rgba8888 => 4,
        _ => null,
    };

    private static byte[] Pattern(int width, int height, int bytesPerPixel, bool opaque)
    {
        var frame = new byte[checked(width * height * bytesPerPixel)];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = opaque && i % 4 == 3 ? (byte)255 : (byte)(i * 7 + i / bytesPerPixel / width);
        return frame;
    }

    /// <summary>
    /// A baseline JPEG of one component, every pixel 128: each 8 × 8 block's coefficients are all zero, so with a DC table
    /// and an AC table of one one-bit code each (difference 0, end of block) a block is two zero bits.
    /// </summary>
    private static byte[] GreyJpeg(int width, int height)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };

        void Segment(byte marker, params byte[] body)
        {
            jpeg.AddRange([0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2)]);
            jpeg.AddRange(body);
        }

        var quantisation = new byte[65]; // table 0, eight-bit, every step 1
        Array.Fill(quantisation, (byte)1, 1, 64);
        Segment(0xDB, quantisation);
        Segment(0xC0, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0);
        var oneCode = new byte[18]; // a class and id byte, sixteen counts (one code of length 1), its symbol 0
        oneCode[1] = 1;
        Segment(0xC4, oneCode); // DC table 0: category 0
        oneCode[0] = 0x10;
        Segment(0xC4, oneCode); // AC table 0: end of block
        Segment(0xDA, 1, 1, 0x00, 0, 63, 0);

        var blocks = (long)((width + 7) / 8) * ((height + 7) / 8);
        var bits = blocks * 2;
        for (var i = 0L; i < bits / 8; i++)
            jpeg.Add(0x00);
        if (bits % 8 != 0)
            jpeg.Add((byte)((1 << (int)(8 - bits % 8)) - 1)); // the last byte's rest padded with ones
        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }
}
