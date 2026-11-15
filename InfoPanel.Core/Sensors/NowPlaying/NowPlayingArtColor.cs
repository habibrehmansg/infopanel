using InfoPanel.Images;
using InfoPanel.Spec;
using SkiaSharp;
using System;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// A cover's main colour, the <c>art-color</c> reading (<c>#RRGGBB</c>): the picture decoded to <see cref="Side"/> ×
/// <see cref="Side"/> pixels, each pixel at least half opaque (alpha <see cref="MinAlpha"/> or more; those below are
/// skipped) put in a bucket by the top 4 bits of each of its red, green and blue, and the most frequent bucket (a tie
/// to the lowest, red's bits first, then green's, then blue's) read as the mean of its pixels' colours, each channel
/// rounded. No vibrance or contrast work: what a client makes of it is its own.
/// <para>
/// Within the bounds the cover's own drawing is held to: at most <see cref="NowPlayingSource.MaxArtBytes"/> (the
/// source's cap, checked before), then the media limits (<see cref="SpecLimits.Default"/>) as the media cache holds a
/// cover: the probe (<see cref="MediaValidator"/>), a PNG, JPEG, GIF (its first frame) or WebP only, and the decoder's
/// own size checked against the probe's and the limits (<see cref="MediaSampleCache.CheckCodec"/>); decoded once, at
/// the decoder's own reduced size nearest to <see cref="Side"/> that isn't smaller (a JPEG's 1/2, 1/4 or 1/8; any other
/// whole), into sRGB, then drawn to <see cref="Side"/> × <see cref="Side"/> with the scenes' linear, mipmapped
/// sampling. A picture refused, not decoded whole (one whose pixels are cut short too), or with no pixel at least half
/// opaque has none (null), and nothing here throws.
/// </para>
/// </summary>
internal static class NowPlayingArtColor
{
    /// <summary>The side of the square the picture is decoded to.</summary>
    public const int Side = 32;

    /// <summary>A pixel counts when at least this opaque (half); one less opaque is skipped.</summary>
    public const byte MinAlpha = 128;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>The main colour of the picture <paramref name="bytes"/>, as <c>#RRGGBB</c>; null when it has none.</summary>
    public static string? Of(byte[] bytes)
    {
        try
        {
            return Decode(bytes) is { } pixels ? Rule(pixels) : null;
        }
        catch (Exception)
        {
            // A cover is another program's data: whatever its decoding meets, it has no colour.
            return null;
        }
    }

    /// <summary>The rule over <see cref="Side"/> × <see cref="Side"/> unpremultiplied RGBA pixels (4 bytes a pixel).</summary>
    internal static string? Rule(ReadOnlySpan<byte> rgba)
    {
        var counts = new int[16 * 16 * 16];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] >= MinAlpha)
                counts[Bucket(rgba[i], rgba[i + 1], rgba[i + 2])]++;
        }

        var best = -1;
        for (var b = 0; b < counts.Length; b++)
        {
            if (counts[b] > 0 && (best < 0 || counts[b] > counts[best]))
                best = b;
        }
        if (best < 0)
            return null;

        long red = 0, green = 0, blue = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < MinAlpha || Bucket(rgba[i], rgba[i + 1], rgba[i + 2]) != best)
                continue;
            red += rgba[i];
            green += rgba[i + 1];
            blue += rgba[i + 2];
        }
        long n = counts[best];
        return $"#{(red + n / 2) / n:X2}{(green + n / 2) / n:X2}{(blue + n / 2) / n:X2}";
    }

    private static int Bucket(byte r, byte g, byte b) => (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;

    /// <summary>The picture as <see cref="Side"/> × <see cref="Side"/> unpremultiplied sRGB RGBA pixels; null when refused or not decoded.</summary>
    private static byte[]? Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > NowPlayingSource.MaxArtBytes)
            return null;
        var limits = SpecLimits.Default;
        var check = MediaValidator.Check(bytes, limits);
        if (check.Info is not { } info || check.Errors.Count > 0 || Encoded(info.Format) is not { } format)
            return null;

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != format || MediaSampleCache.CheckCodec(codec, info, limits) is not null)
            return null;

        var whole = codec.Info.Size;
        var size = codec.GetScaledDimensions(Math.Min(1f, Math.Max((float)Side / whole.Width, (float)Side / whole.Height)));
        if (size.Width < 1 || size.Height < 1 || size.Width > whole.Width || size.Height > whole.Height)
            size = whole;

        using var srgb = SKColorSpace.CreateSrgb();
        var decodedInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, srgb);
        using var decoded = new SKBitmap(decodedInfo);
        // Only a picture decoded whole: one cut short (IncompleteInput) holds pixels the decoder made up.
        if (codec.GetPixels(decodedInfo, decoded.GetPixels()) is not SKCodecResult.Success)
            return null;

        var pixels = new byte[Side * Side * 4];
        if (size.Width == Side && size.Height == Side && decoded.RowBytes == Side * 4)
        {
            decoded.GetPixelSpan().CopyTo(pixels);
            return pixels;
        }

        using var image = SKImage.FromBitmap(decoded);
        using var surface = SKSurface.Create(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul, srgb));
        if (image is null || surface is null)
            return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, SKRect.Create(Side, Side), Sampling);
        using var small = new SKBitmap(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Unpremul, srgb));
        if (small.RowBytes != Side * 4 || !surface.ReadPixels(small.Info, small.GetPixels(), small.RowBytes, 0, 0))
            return null;
        small.GetPixelSpan().CopyTo(pixels);
        return pixels;
    }

    /// <summary>The decoder a probed format takes, as the media cache picks it; none for an SVG or a video.</summary>
    private static SKEncodedImageFormat? Encoded(MediaFormat format) => format switch
    {
        MediaFormat.Png => SKEncodedImageFormat.Png,
        MediaFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        MediaFormat.Gif => SKEncodedImageFormat.Gif,
        MediaFormat.WebP => SKEncodedImageFormat.Webp,
        _ => null,
    };
}
