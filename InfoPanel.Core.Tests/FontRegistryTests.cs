using InfoPanel.Core.Tests.Golden;
using InfoPanel.Drawing;
using SkiaSharp;
using Topten.RichTextKit;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>Registers fonts into the process-wide registry, so it runs in the goldens' non-parallel collection.</summary>
[Collection(GoldenCollection.Name)]
public class FontRegistryTests
{
    private static string InterDirectory => Path.Combine(TestPaths.ProjectDirectory, "..", "InfoPanel.Core", "Fonts");

    [Fact]
    public void BundledInter_IsRegistered()
    {
        Assert.Contains("Inter", FontRegistry.GetFamilies());
        Assert.Equal("Inter", FontDefaults.Family);
    }

    [Theory]
    [InlineData("Inter", 400, SKFontStyleSlant.Upright, 400, false)]
    [InlineData("inter", 700, SKFontStyleSlant.Upright, 700, false)]
    [InlineData("INTER", 300, SKFontStyleSlant.Upright, 400, false)]
    [InlineData("Inter", 600, SKFontStyleSlant.Upright, 700, false)]   // nearer to 700 than to 400
    [InlineData("Inter", 550, SKFontStyleSlant.Upright, 700, false)]   // tie (150 each way) → heavier
    [InlineData("Inter", 900, SKFontStyleSlant.Upright, 700, false)]
    [InlineData("Inter", 400, SKFontStyleSlant.Italic, 400, true)]
    [InlineData("Inter", 400, SKFontStyleSlant.Oblique, 400, true)]
    [InlineData("Inter", 800, SKFontStyleSlant.Italic, 700, true)]
    public void TryGet_PicksNearestStyle(string family, int weight, SKFontStyleSlant slant, int expectedWeight, bool expectedItalic)
    {
        Assert.True(FontRegistry.TryGet(family, weight, slant, out var typeface));
        Assert.Equal("Inter", typeface.FamilyName);
        Assert.Equal(expectedWeight, typeface.FontWeight);
        Assert.Equal(expectedItalic, typeface.FontSlant != SKFontStyleSlant.Upright);
    }

    [Theory]
    [InlineData("No Such Family")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGet_UnknownFamily_IsFalse(string? family)
    {
        Assert.False(FontRegistry.TryGet(family, 400, SKFontStyleSlant.Upright, out var typeface));
        Assert.Null(typeface);
    }

    [Theory]
    [InlineData("", 400, SKFontStyleSlant.Upright)]
    [InlineData("Regular", 400, SKFontStyleSlant.Upright)]
    [InlineData("Bold", 700, SKFontStyleSlant.Upright)]
    [InlineData("SemiBold Italic", 600, SKFontStyleSlant.Italic)]
    [InlineData("Extra Light", 200, SKFontStyleSlant.Upright)]
    [InlineData("Light Oblique", 300, SKFontStyleSlant.Oblique)]
    [InlineData("Black", 900, SKFontStyleSlant.Upright)]
    public void ParseStyleName(string name, int weight, SKFontStyleSlant slant)
    {
        Assert.Equal((weight, slant), FontRegistry.ParseStyleName(name));
    }

    [Fact]
    public void TextPaths_ResolveRegisteredFamilyBeforeSystemFonts()
    {
        Assert.True(FontRegistry.TryGet("Inter", 700, SKFontStyleSlant.Upright, out var bold));

        // SkiaGraphics: bold flag, and a style name that overrides the flags (as in 1.x).
        Assert.Same(bold, SkiaGraphics.CreateTypeface("Inter", "", bold: true, italic: false));
        Assert.Same(bold, SkiaGraphics.CreateTypeface("inter", "Bold", bold: false, italic: false));

        // The RichTextKit mapper set on every TextBlock.
        var style = new Style { FontFamily = "Inter", FontWeight = 700 };
        Assert.Same(bold, InfoPanelFontMapper.Instance.TypefaceFromStyle(style, ignoreFontVariants: false));
    }

    [Fact]
    public void RegisterFile_RejectsNonFonts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllText(path, "not a font");
        try
        {
            Assert.Empty(FontRegistry.RegisterFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegisterDirectory_RegistersFontFilesInSubfolders()
    {
        Assert.Empty(FontRegistry.RegisterDirectory(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));

        var directory = Path.Combine(Path.GetTempPath(), $"fonts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        try
        {
            // The same bytes as the bundled face, so replacing it changes no rendering.
            File.Copy(Path.Combine(InterDirectory, "Inter-Bold.ttf"), Path.Combine(directory, "sub", "Inter-Bold.ttf"));
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "ignored");

            Assert.Equal(["Inter"], FontRegistry.RegisterDirectory(directory));
            Assert.True(FontRegistry.TryGet("Inter", 700, SKFontStyleSlant.Upright, out var bold));
            Assert.Equal(700, bold.FontWeight);
            Assert.Single(FontRegistry.GetFamilies(), family => family.Equals("Inter", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
