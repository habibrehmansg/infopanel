using SkiaSharp;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// The notification-area icon's image: <see cref="ShellGlyph"/> (the logo's hexagon and tiles, black) as it is on a
/// light taskbar, white on a dark one. Windows doesn't tint tray icons as macOS tints template images, so each theme
/// gets its own.
/// </summary>
internal static class WinTrayGlyph
{
    /// <summary>The glyph as a PNG <paramref name="pixels"/> square for a light or dark taskbar.</summary>
    public static byte[] Png(int pixels, bool lightTaskbar)
    {
        var black = ShellGlyph.Png(pixels);
        if (lightTaskbar)
            return black;

        using var glyph = SKImage.FromEncodedData(black);
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var white = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
        surface.Canvas.DrawImage(glyph, 0, 0, white);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
