using SkiaSharp;

namespace InfoPanel.Shell;

/// <summary>
/// The round badge before the menu's row of who is signed in (macOS): the name's initial (<see cref="ShellMenu.InitialOf"/>),
/// white and bold on a grey disc that reads on light and dark menus alike. Drawn at the size asked; no picture is
/// downloaded.
/// </summary>
public static class ShellInitial
{
    /// <summary>The disc's colour: macOS's system grey.</summary>
    private static readonly SKColor Disc = new(0x8E, 0x8E, 0x93);

    /// <summary>The badge for <paramref name="letter"/> as a PNG <paramref name="pixels"/> square (52 for a 26-point image at 2×).</summary>
    public static byte[] Png(string letter, int pixels)
    {
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var disc = new SKPaint { Color = Disc, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(pixels / 2f, pixels / 2f, pixels / 2f, disc);

        using var typeface = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, pixels * 0.5f) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        font.MeasureText(letter, out var bounds, ink);
        // Centred on the letter's own outline, so a capital sits in the middle whatever the font's line metrics.
        canvas.DrawText(letter, (pixels - bounds.Width) / 2f - bounds.Left, (pixels - bounds.Height) / 2f - bounds.Top, font, ink);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
