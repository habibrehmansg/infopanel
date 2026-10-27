using InfoPanel.Scenes;
using SkiaSharp;
using System;

namespace InfoPanel.Output;

/// <summary>Thumbnails (<c>GET /api/profiles/{id}/thumbnail</c>, spec/docs/api.md "Thumbnails"): sizes, and a snapshot drawn as a PNG.</summary>
public static class Thumbnail
{
    public const int DefaultWidth = 320;
    public const int MinWidth = 16;
    public const int MaxWidth = 4096;
    public const int MaxHeight = 4096;

    /// <summary>
    /// The pixel size and scale for <paramref name="requestedWidth"/>: the width clamped to
    /// <see cref="MinWidth"/>..<see cref="MaxWidth"/>, the height proportional, rounded and at least 1; a height over
    /// <see cref="MaxHeight"/> is capped and the width made proportional to it (rounded, at least 1). Both sides are
    /// bounded whatever the profile's size: the height is compared in double before it becomes an int.
    /// </summary>
    public static (int Width, int Height, float Scale) Size(int profileWidth, int profileHeight, int requestedWidth)
    {
        var pw = Math.Max(1, profileWidth);
        var ph = Math.Max(1, profileHeight);
        var width = Math.Clamp(requestedWidth, MinWidth, MaxWidth);
        var scale = (float)width / pw;
        var exactHeight = Math.Round(ph * (double)width / pw);
        if (exactHeight > MaxHeight)
        {
            scale = (float)MaxHeight / ph;
            width = (int)Math.Clamp(Math.Round(pw * (double)MaxHeight / ph), 1, MaxWidth);
            return (width, MaxHeight, scale);
        }

        return (width, (int)Math.Max(1, exactHeight), scale);
    }

    /// <summary>Draws <paramref name="snapshot"/> as a PNG <see cref="Size"/> gives for <paramref name="requestedWidth"/>.</summary>
    public static byte[] RenderPng(TickSnapshot snapshot, int requestedWidth)
    {
        var (width, height, scale) = Size(snapshot.Width, snapshot.Height, requestedWidth);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, scale);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
