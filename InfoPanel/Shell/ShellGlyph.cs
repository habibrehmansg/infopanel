using SkiaSharp;

namespace InfoPanel.Shell;

/// <summary>
/// The menu-bar glyph: the logo's hexagon and tiles in one colour, black with alpha, which macOS tints for light and dark
/// menu bars and the selection (a template image). Drawn from its outline at the size asked, so no image file is shipped.
/// </summary>
public static class ShellGlyph
{
    /// <summary>The hexagon's outline, in a 100 × 100 box.</summary>
    private const string Hexagon = "M50 6 L89 28.5 V71.5 L50 94 L11 71.5 V28.5 Z";

    /// <summary>The three tiles inside it.</summary>
    private const string Tiles = "M53 51 L64 45 V60 L53 66 Z M68 42.5 L80 36 V63 L68 69.5 Z M53 70 L64 64 V78 L53 84 Z";

    /// <summary>The glyph as a PNG <paramref name="pixels"/> square (36 for an 18-point menu-bar icon at 2×).</summary>
    public static byte[] Png(int pixels)
    {
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixels / 100f);
        using var hexagon = SKPath.ParseSvgPathData(Hexagon);
        using var tiles = SKPath.ParseSvgPathData(Tiles);
        using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(hexagon, stroke);
        canvas.DrawPath(tiles, fill);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
