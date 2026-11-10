using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// What the engine's first drawing needs, on every OS (never skipped): Skia's native library loads beside the managed
/// SkiaSharp (on Linux the native comes from <c>SkiaSharp.NativeAssets.Linux</c>, which must stay in SkiaSharp's own
/// release line and resolve its own system libraries), the system's fonts are listed (fontconfig on Linux), and one
/// render puts a system font's text down.
/// </summary>
public class SkiaNativeTests
{
    [Fact]
    public void Native_LoadsListsSystemFontsAndRenders()
    {
        Assert.True(SkiaSharpVersion.CheckNativeLibraryCompatible(throwIfIncompatible: true));

        // A Skia built without the system's font manager lists none: the engine would lose the system's fonts.
        Assert.True(SKFontManager.Default.FontFamilyCount > 0, "Skia lists no system font family.");

        using var typeface = SKFontManager.Default.MatchCharacter('A');
        Assert.NotNull(typeface);

        var info = new SKImageInfo(96, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Assert.NotNull(surface);
        surface.Canvas.Clear(SKColors.White);
        using (var font = new SKFont(typeface, 32))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            surface.Canvas.DrawText("Ag8", 4, 36, font, paint);
        }

        using var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));
        var inked = 0;
        for (var y = 0; y < info.Height; y++)
        {
            for (var x = 0; x < info.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.White) inked++;
            }
        }

        Assert.True(inked > 50, $"The render put down {inked} pixels of text.");
    }
}
