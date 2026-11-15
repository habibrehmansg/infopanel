using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A live number set in the bundled "Inter Tabular" keeps its width as its digits change: the scene measures strings that
/// differ only in their digits at the same width, shaped as it shapes them (kerning and Inter's default features on), in
/// each of the four styles. In "Inter" the same strings measure differently. Widths are read back from a rect drawn as
/// wide as the scene measured the text (at 8x, so to within 1/8 px).
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class BundledTabularDigitsTests
{
    private const float Size = 24;
    private const int Scale = 8;

    private static double SceneWidth(string family, string text, int weight = 400, bool italic = false)
    {
        var scene = SceneKit.Json("""
            {
              'format': 'infopanel.scene/1',
              'values': {'w': {'op': 'measure', 'node': 'a', 'field': 'w'}},
              'nodes': [
                {'kind': 'rect', 'x': 0, 'y': 0, 'h': 4, 'w': {'op': 'value', 'name': 'w'}, 'fill': '#00ff00ff'},
                {'kind': 'text', 'id': 'a', 'y': 8, 'font': {'family': '%FAMILY%', 'size': %SIZE%, 'weight': %WEIGHT%, 'italic': %ITALIC%}, 'fill': '#ffffff', 'text': '%TEXT%'}
              ]
            }
            """.Replace("%FAMILY%", family).Replace("%SIZE%", Size.ToString()).Replace("%WEIGHT%", weight.ToString())
                .Replace("%ITALIC%", italic ? "true" : "false").Replace("%TEXT%", text));
        var document = SceneKit.Doc(scene, 400, 80);
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(document);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        using var bitmap = snapshot.Render(Scale);
        var green = 0;
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, Scale) == new SKColor(0, 255, 0)) green++;
        return green / (double)Scale;
    }

    [Fact]
    public void OnesAndEights_MeasureTheSame_InInterTabular_AndDifferently_InInter()
    {
        var ones = SceneWidth("Inter Tabular", "111");
        Assert.InRange(ones, 40, 52); // three tabular figures, 1328/2048 em each
        Assert.Equal(ones, SceneWidth("Inter Tabular", "888"));

        Assert.True(SceneWidth("Inter", "888") - SceneWidth("Inter", "111") > 10);
    }

    /// <summary>Strings that differ only in their digits measure alike: the punctuation around them is Inter's and stays put.</summary>
    [Theory]
    [InlineData("-1.5", "-8.0")]
    [InlineData("1:23", "8:45")]
    [InlineData("0123456789", "8888888888")]
    public void LiveNumbers_KeepTheirWidth_InEachStyle(string a, string b)
    {
        foreach (var (weight, italic) in new[] { (400, false), (700, false), (400, true), (700, true) })
        {
            var width = SceneWidth("Inter Tabular", a, weight, italic);
            Assert.True(width > 0);
            Assert.Equal(width, SceneWidth("Inter Tabular", b, weight, italic));
        }

        Assert.NotEqual(SceneWidth("Inter", a), SceneWidth("Inter", b));
    }

    /// <summary>
    /// Inter's features that are on by default, where they touch digits: the contextual alternates take the case forms of
    /// brackets and dashes beside a digit ("(1)", "-1"), leave "&lt;-" before a digit unjoined ("&lt;-1") and the glyph
    /// composition makes a zero with U+0338 a slashed zero; their classes name the tabular figures, so each still
    /// keeps one advance whatever the digit.
    /// </summary>
    [Theory]
    [InlineData("(1)", "(8)")]
    [InlineData("-1", "-8")]
    [InlineData("<-1", "<-8")]
    [InlineData("A1", "A8")]
    [InlineData("0̸", "8")]
    public void IntersDefaultFeatures_KeepTheTabularDigitsOneAdvance(string a, string b)
    {
        Assert.Equal(SceneWidth("Inter Tabular", a), SceneWidth("Inter Tabular", b));
        Assert.NotEqual(SceneWidth("Inter", a), SceneWidth("Inter", b));
    }
}
