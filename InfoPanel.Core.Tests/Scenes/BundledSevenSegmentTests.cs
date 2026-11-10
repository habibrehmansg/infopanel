using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Drawing;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A Text a 1.x profile sets in "DSEG7 Classic" draws with the bundled face, not Inter (the fallback of a family the
/// computer lacks): the scene's measured width of a string is the one the embedded typeface from the font registry gives,
/// and a monospace face or Inter measures it differently.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class BundledSevenSegmentTests
{
    private const float Size = 24;
    private const string Digits = "1188";
    private const int Scale = 8;

    /// <summary>The width the scene measures for <see cref="Digits"/> in <paramref name="family"/>, read back from a rect drawn that wide (at 8x, so to within 1/8 px).</summary>
    private static double SceneWidth(string family)
    {
        var scene = SceneKit.Json("""
            {
              'format': 'infopanel.scene/1',
              'values': {'w': {'op': 'measure', 'node': 'a', 'field': 'w'}},
              'nodes': [
                {'kind': 'rect', 'x': 0, 'y': 0, 'h': 4, 'w': {'op': 'value', 'name': 'w'}, 'fill': '#00ff00ff'},
                {'kind': 'text', 'id': 'a', 'y': 8, 'font': {'family': '%FAMILY%', 'size': %SIZE%}, 'fill': '#ffffff', 'text': '%TEXT%'}
              ]
            }
            """.Replace("%FAMILY%", family).Replace("%SIZE%", Size.ToString()).Replace("%TEXT%", Digits));
        var document = SceneKit.Doc(scene, 200, 80);
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(document);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        using var bitmap = snapshot.Render(Scale);
        var green = 0;
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, Scale) == new SKColor(0, 255, 0)) green++;
        return green / (double)Scale;
    }

    /// <summary>
    /// The width <see cref="Digits"/> takes in the registry's embedded DSEG7 Classic, set as the scene sets its glyphs
    /// (unhinted, linear metrics): Skia's default font is hinted, which FreeType (Linux) applies to the advances and
    /// CoreText (macOS) doesn't.
    /// </summary>
    private static double EmbeddedDseg7Width()
    {
        Assert.True(FontRegistry.TryGet("DSEG7 Classic", 400, SKFontStyleSlant.Upright, out var typeface));
        using var font = TextLayoutEngine.MakeFont(typeface, Size);
        return font.MeasureText(Digits);
    }

    [Fact]
    public void ATextInDseg7Classic_MeasuresAsTheEmbeddedTypefaceDoes()
    {
        var reference = EmbeddedDseg7Width();
        Assert.True(reference > 20, $"reference {reference}");
        Assert.InRange(SceneWidth("DSEG7 Classic"), reference - 0.5, reference + 0.5);
    }

    [Fact]
    public void Inter_AndAMonospaceFace_MeasureDifferentlyFromTheEmbeddedDseg7()
    {
        var reference = EmbeddedDseg7Width();
        Assert.NotInRange(SceneWidth("Inter"), reference - 0.5, reference + 0.5);
        Assert.NotInRange(SceneWidth("No Such Family Zq"), reference - 0.5, reference + 0.5);

        var monospace = new[] { "Menlo", "Courier New", "Consolas", "DejaVu Sans Mono" }.FirstOrDefault(SceneFontMapper.Has);
        Assert.SkipWhen(monospace is null, "No known monospace family is installed.");
        Assert.NotInRange(SceneWidth(monospace!), reference - 0.5, reference + 0.5);
    }
}
