using System;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>
/// A panel's frame as one JPEG, with SkiaSharp's encoder (4:2:0, as Skia encodes by default): from the opaque RGBA frame
/// <see cref="PanelTransform"/> drew (<see cref="PanelPixelFormat.Rgba8888"/>), so the picture is already turned and fitted
/// to the panel. Pure: no device, no state.
/// </summary>
public static class PanelJpeg
{
    /// <summary>
    /// Encodes <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>, 4 bytes a pixel, rows packed)
    /// into <paramref name="buffer"/>, grown when it is too small and otherwise reused; the JPEG's length.
    /// </summary>
    public static unsafe int Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality, ref byte[] buffer)
    {
        var length = PanelTransform.TargetLength(width, height, PanelPixelFormat.Rgba8888);
        if (rgba.Length != length)
            throw new ArgumentException($"A {width}×{height} RGBA frame is {length} bytes; this one is {rgba.Length}.", nameof(rgba));
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        fixed (byte* pixels = rgba)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)pixels, info.RowBytes);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new InvalidOperationException("Skia could not encode the frame as JPEG.");
            var encoded = data.AsSpan();
            if (buffer.Length < encoded.Length)
                buffer = new byte[encoded.Length + encoded.Length / 4]; // room for the next, slightly larger frame
            encoded.CopyTo(buffer);
            return encoded.Length;
        }
    }

    /// <summary>
    /// Encodes at <paramref name="quality"/>; while the JPEG is over <paramref name="maxBytes"/> (null: no cap), encodes it
    /// again <paramref name="step"/> lower each time, never below <paramref name="minQuality"/> (TRCC's rule, 1.4.x's
    /// Thermalright, with a step of 5; JL's, with 15 down to 30). At the floor it is returned whatever its length: the
    /// caller decides (<see cref="PanelFrameWriter"/> doesn't send one still over). The JPEG's length; <paramref name="used"/>
    /// is the quality it took.
    /// </summary>
    public static int EncodeWithin(ReadOnlySpan<byte> rgba, int width, int height, int quality, int minQuality, int step, int? maxBytes,
        ref byte[] buffer, out int used)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        minQuality = Math.Clamp(minQuality, 1, quality);
        used = quality;
        var length = Encode(rgba, width, height, quality, ref buffer);
        while (maxBytes is { } max && length > max && used > minQuality)
        {
            used = Math.Max(minQuality, used - step);
            length = Encode(rgba, width, height, used, ref buffer);
        }
        return length;
    }

    /// <summary>The JPEG of <paramref name="rgba"/> in an array of its own.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality)
    {
        var buffer = Array.Empty<byte>();
        var length = Encode(rgba, width, height, quality, ref buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>A black frame of the panel's size as JPEG (the stop's screen-off).</summary>
    public static byte[] Black(int width, int height, int quality)
    {
        var rgba = new byte[PanelTransform.TargetLength(width, height, PanelPixelFormat.Rgba8888)];
        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        return Encode(rgba, width, height, quality);
    }
}
