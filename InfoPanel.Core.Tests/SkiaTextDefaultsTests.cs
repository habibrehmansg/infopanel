using InfoPanel.Core.Tests.Golden;
using InfoPanel.Drawing;
using InfoPanel.Output;
using InfoPanel.Scenes;
using SkiaSharp;
using Topten.RichTextKit;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// Pins how text is drawn where SkiaSharp or RichTextKit choose for the engine: the font a missing family falls back to,
/// and the edging, hinting and subpixel positioning of fonts made without them. Each output is compared with the same
/// text drawn with those settings given explicitly, so a library whose defaults change shows here, not only in goldens.
/// </summary>
[Collection(GoldenCollection.Name)]
public class SkiaTextDefaultsTests
{
    /// <summary>A family no computer has; no installed family contains it either (the 1.x renderer looks for similar names).</summary>
    private const string Missing = "Qqzxw";

    /// <summary>The system's own font, which Skia falls back to for a family it doesn't find.</summary>
    private static string SystemFamily => OperatingSystem.IsWindows() ? "Segoe UI" : "Helvetica";

    private static void SkipOnLinux() =>
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux: the fallback family depends on the image's fontconfig.");

    /// <summary>
    /// A family that isn't installed is drawn in the system's font in the weight and slant asked for, not in its
    /// regular face. On Linux the system's font is fontconfig's default in the image, so the expectation is what the
    /// font manager gives for that family in the style asked for.
    /// </summary>
    [Theory]
    [InlineData(700, SKFontStyleSlant.Upright)]
    [InlineData(700, SKFontStyleSlant.Italic)]
    public void AMissingFamily_FallsBackToTheSystemFontInTheStyleAsked(int weight, SKFontStyleSlant slant)
    {
        using var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, slant);
        using var missing = FontRegistry.MatchSystem(Missing, style);
        Assert.NotNull(missing);
        Assert.True(missing.GlyphCount > 0);

        if (OperatingSystem.IsLinux())
        {
            using var expected = SKFontManager.Default.MatchFamily(SKTypeface.Default.FamilyName, style);
            Assert.NotNull(expected);
            Assert.Equal(expected.FamilyName, missing.FamilyName);
            Assert.Equal(expected.FontWeight, missing.FontWeight);
            Assert.Equal(expected.FontSlant, missing.FontSlant);
            return;
        }

        Assert.Equal(SystemFamily, missing.FamilyName);
        Assert.Equal(weight, missing.FontWeight);
        Assert.Equal(slant, missing.FontSlant);

        Assert.Equal(SystemFamily, SKTypeface.Default.FamilyName);
        Assert.True(SKTypeface.Default.GlyphCount > 0);
    }

    /// <summary>The 1.x renderer (the parity oracle): a missing family is drawn in Arial, or with a style name in the system's font.</summary>
    [Fact]
    public void TheV1Renderer_FallsBackAsIn1x()
    {
        SkipOnLinux();
        var plain = SkiaGraphics.CreateTypeface(Missing, "", bold: false, italic: false);
        Assert.Equal("Arial", plain.FamilyName);
        Assert.Equal(400, plain.FontWeight);

        var styled = SkiaGraphics.CreateTypeface(Missing + "b", "Bold", bold: false, italic: false);
        Assert.Equal(SystemFamily, styled.FamilyName);
        Assert.True(styled.GlyphCount > 0);
    }

    /// <summary>The scene runtime draws a family the engine doesn't have in Inter.</summary>
    [Fact]
    public void TheSceneRuntime_DrawsAMissingFamilyInInter()
    {
        Assert.False(SceneFontMapper.Has(Missing));
        var typeface = SceneFontMapper.Instance.TypefaceFromStyle(new Style { FontFamily = Missing, FontWeight = 700 }, false);
        Assert.Equal("Inter", typeface.FamilyName);
        Assert.Equal(700, typeface.FontWeight);
    }

    /// <summary>The 1.x renderer paints RichTextKit's blocks antialiased, normally hinted, with subpixel positions.</summary>
    [Fact]
    public void TheV1Renderer_PaintsTextAsToday()
    {
        using var actual = new SKBitmap(160, 40);
        using (var g = SkiaGraphics.FromBitmap(actual, 1f))
        {
            g.Clear(SKColors.Black);
            g.DrawString("Hello 42.7°", "Inter", "", 19, "#FFFFFF", 3, 4);
        }

        using var expected = new SKBitmap(160, 40);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.Black);
            var typeface = SkiaGraphics.CreateTypeface("Inter", "", false, false);
            var tb = new TextBlock { FontMapper = InfoPanelFontMapper.Instance, EllipsisEnabled = true };
            tb.AddText("Hello 42.7°", new Style
            {
                FontFamily = typeface.FamilyName,
                FontSize = 19,
                FontWeight = typeface.FontWeight,
                FontItalic = typeface.IsItalic,
                FontWidth = (SKFontStyleWidth)typeface.FontWidth,
                TextColor = SKColors.White,
            });
            tb.Paint(canvas, new SKPoint(3, 4), new TextPaintOptions
            {
                Edging = SKFontEdging.Antialias,
                Hinting = SKFontHinting.Normal,
                SubpixelPositioning = true,
            });
        }

        AssertSamePixels(expected, actual);
    }

    /// <summary>The stats overlay's font: antialiased, normally hinted, whole-pixel positions, as Skia's font defaults were.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void TheStatsOverlay_DrawsTextAsToday(float scale)
    {
        const int width = 200;
        var text = ("Profile", "CPU | FPS 30 | 4ms");
        var size = (int)(width * scale);

        using var actual = new SKBitmap(size, size / 4);
        using (var canvas = new SKCanvas(actual))
        {
            canvas.Clear(SKColors.White);
            StatsOverlay.Draw(canvas, scale, width, text);
        }

        using var expected = new SKBitmap(size, size / 4);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            using var band = new SKPaint { Color = new SKColor(0, 0, 0, 0x84) };
            canvas.DrawRect(new SKRect(0, 0, width, StatsOverlay.Height), band);
            Assert.True(FontRegistry.TryGet("Inter", 400, SKFontStyleSlant.Upright, out var inter));
            using var font = new SKFont(inter, 10)
            {
                Edging = SKFontEdging.Antialias,
                Hinting = SKFontHinting.Normal,
                Subpixel = false,
                LinearMetrics = false,
                BaselineSnap = true,
                EmbeddedBitmaps = false,
                ForceAutoHinting = false,
                Embolden = false,
            };
            using var ink = new SKPaint { Color = new SKColor(0, 0xFF, 0), IsAntialias = true };
            var baseline = (StatsOverlay.Height - font.Metrics.Ascent - font.Metrics.Descent) / 2;
            canvas.DrawText(text.Item1, 5, baseline, SKTextAlign.Left, font, ink);
            canvas.DrawText(text.Item2, width - 5, baseline, SKTextAlign.Right, font, ink);
        }

        AssertSamePixels(expected, actual);
    }

    private static void AssertSamePixels(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        var inked = 0;
        var differing = 0;
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var e = expected.GetPixel(x, y);
            if (e != expected.GetPixel(0, expected.Height - 1)) inked++;
            if (e != actual.GetPixel(x, y)) differing++;
        }
        Assert.True(inked > 50, $"the reference drew {inked} pixels of text");
        Assert.Equal(0, differing);
    }
}
