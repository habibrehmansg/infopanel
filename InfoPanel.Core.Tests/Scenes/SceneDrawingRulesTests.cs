using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Drawing rules of spec/docs/scene.md: a series' stroke is one stroke (evaluated once per tick, its dashes counted along
/// every bar; a bar with a negative width draws nothing, and outline lengths never cancel), gradient stops are clamped
/// in written order, and an absent member is its schema default.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneDrawingRulesTests
{
    private static JsonElement Doc(string nodes, int width = 120, int height = 20) =>
        SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'nodes':[" + nodes + "]}"), width, height);

    private static FakeSceneInputs Full(int samples)
    {
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 100);
        fake.Histories["test:v"] = Enumerable.Repeat(100.0, samples).ToArray();
        return fake;
    }

    private static TickSnapshot Tick(JsonElement doc, FakeSceneInputs inputs)
    {
        var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(doc);
        var s = runtime.Tick(0, SceneKit.Now);
        runtime.Dispose();
        return s;
    }

    [Fact]
    public void A_bar_series_evaluates_its_stroke_once_per_tick()
    {
        // 4,096 bars whose stroke width is 12 operations: 12 units of work, as validation counts it, not 49,152.
        var width = "1";
        for (var i = 0; i < 12; i++) width = "{'op':'add','args':[" + width + ",0]}";
        var doc = Doc("{'kind':'series','input':'v','w':1024,'h':100,'min':0,'max':100,'mode':'bars','pitch':0.25,'barWidth':0.25,"
            + "'fill':'#ffffff','stroke':{'paint':'#ffffff','width':" + width + "}}", 1030, 110);
        using var s = Tick(doc, Full(4096));
        Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
        Assert.Null(s.Bounds.Single().Problem);
    }

    [Fact]
    public void A_bar_series_counts_its_dashes_along_every_bar()
    {
        // One stroke along 4,096 bars of 100 px: about 200 dashes a bar, 800,000 in all, over the 100,000 a stroke may draw.
        var doc = Doc("{'kind':'series','input':'v','w':1024,'h':100,'min':0,'max':100,'mode':'bars','pitch':0.25,'barWidth':0.25,"
            + "'stroke':{'paint':'#ffffff','width':1,'dash':[1,1]}}", 1030, 110);
        using (var s = Tick(doc, Full(4096)))
        {
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
            Assert.Equal("budget", s.Bounds.Single().Problem);
        }

        // 400 such bars stay under it.
        using (var s = Tick(doc, Full(400)))
            Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
    }

    [Theory]
    // 600 bars of 1 × 100 px (202 px of outline each) with dashes of period 2: untrimmed, 122,400 dashes.
    [InlineData(0, 0.1, false)]    // a tenth of each outline: 13,320
    [InlineData(0.1, 0.9, false)]  // 0.8 of it: 98,160
    [InlineData(0.05, 0.95, true)] // 0.9 of it: 110,280, over the 100,000 a stroke may draw
    [InlineData(0, 1, true)]       // all of it: 122,400
    public void A_bar_series_counts_its_dashes_along_the_trimmed_part_of_every_bar(double start, double end, bool over)
    {
        var doc = Doc("{'kind':'series','input':'v','w':600,'h':100,'min':0,'max':100,'mode':'bars','pitch':1,'barWidth':1,"
            + "'stroke':{'paint':'#ffffff','width':1,'dash':[1,1],'trimStart':" + start.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",'trimEnd':" + end.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}", 610, 110);
        using var s = Tick(doc, Full(600));
        if (over)
        {
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
            Assert.Equal("budget", s.Bounds.Single().Problem);
        }
        else
        {
            Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal));
            Assert.Null(s.Bounds.Single().Problem);
        }
    }

    [Fact]
    public void Bars_with_a_negative_width_draw_nothing()
    {
        // A bar is a rectangle, and a rectangle with a negative width draws nothing: neither its fill nor its stroke.
        var fake = Full(20);
        using var empty = Tick(Doc("", 120, 110), fake);
        using var bars = Tick(Doc("{'kind':'series','input':'v','w':100,'h':100,'min':0,'max':100,'mode':'bars','pitch':5,'barWidth':-3,"
            + "'fill':'#ffffff','stroke':{'paint':'#ff0000','width':2}}", 120, 110), fake);
        Assert.Empty(bars.Diagnostics);
        using var a = empty.Render();
        using var b = bars.Render();
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, 0));
    }

    [Fact]
    public void Bar_lengths_never_cancel_in_the_dash_budget()
    {
        // Bars alternating 1,000 and 0 px tall, barWidth −500: signed perimeters of +1,000 and −1,000 would sum to 0 and
        // let one dashed stroke past the budget, while Skia strokes each reversed rectangle in full (1.5 million dashes).
        // The stroke is refused, or draws nothing at all.
        var fake = new FakeSceneInputs();
        fake.Number("test:v", 0);
        fake.Histories["test:v"] = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 100.0 : 0).ToArray();
        using var empty = Tick(Doc("", 1030, 1010), fake);
        using var s = Tick(Doc("{'kind':'series','input':'v','w':1024,'h':1000,'min':0,'max':100,'mode':'bars','pitch':1,'barWidth':-500,"
            + "'stroke':{'paint':'#ffffff','width':1,'dash':[0.005,0.005],'trimEnd':0.5}}", 1030, 1010), fake);
        if (s.Diagnostics.Any(d => d.Code == "budget.dash")) return;
        Assert.Null(s.Bounds.Single().Problem);
        using var a = empty.Render();
        using var b = s.Render();
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, 0));
    }

    [Theory]
    // An outline's length for the dash budget is the control polygon of the path Skia draws: at least the length it
    // draws (exactly it for a rectangle), never negative, so lengths never cancel. An ellipse's is its four conics'
    // control points, the oval's bounding rectangle.
    [InlineData("rect", 0, 0, -500, 1000, 3000)]
    [InlineData("rect", 0, 0, 500, -1000, 3000)]
    [InlineData("rect", 0, 0, -500, -1000, 3000)]
    [InlineData("ellipse", 0, 0, -10, -10, 80)]
    [InlineData("ellipse", 0, 0, 10, -10, 80)]
    public void An_outline_length_is_never_negative(string kind, float x, float y, float a, float b, double expected)
    {
        RGeom geom = kind == "rect" ? new RectGeom(x, y, a, b, null) : new EllipseGeom(x, y, a, b);
        var owner = new List<IDisposable>();
        try
        {
            var (length, _, _) = geom.Measure(owner);
            Assert.Equal(expected, length, 3);
            double drawn = 0;
            using (var measure = new SKPathMeasure(geom.Drawn!, false, 1000))
                do drawn += measure.Length;
                while (measure.NextContour());
            Assert.InRange(drawn, Math.Min(expected, 62.8), length + 1e-3);
        }
        finally
        {
            foreach (var o in owner) o.Dispose();
        }
    }

    private static SKBitmap Gradient(string stops, string spread = "pad")
    {
        var doc = Doc("{'kind':'rect','w':100,'h':20,'fill':{'type':'linear','x1':0,'y1':0,'x2':100,'y2':0,'spread':'" + spread + "','stops':[" + stops + "]}}", 100, 20);
        using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options());
        return s.Render();
    }

    [Fact]
    public void Gradient_stops_are_clamped_in_the_order_written()
    {
        // A stop below the one before it is raised to it: 0.6 then 0.4 is a hard edge at 0.6.
        using var raised = Gradient("{'at':0,'color':'#ff0000'},{'at':0.6,'color':'#ff0000'},{'at':0.4,'color':'#0000ff'},{'at':1,'color':'#0000ff'}");
        using var explicitEdge = Gradient("{'at':0,'color':'#ff0000'},{'at':0.6,'color':'#ff0000'},{'at':0.6,'color':'#0000ff'},{'at':1,'color':'#0000ff'}");
        Assert.Equal(0, SceneKit.DifferingPixels(raised, explicitEdge, 0));
        Assert.Equal(new SKColor(255, 0, 0, 255), raised.GetPixel(58, 10));
        Assert.Equal(new SKColor(0, 0, 255, 255), raised.GetPixel(62, 10));

        // Positions are clamped to 0–1 first: −0.5 is 0 and 1.5 is 1.
        using var outside = Gradient("{'at':-0.5,'color':'#ff0000'},{'at':1.5,'color':'#0000ff'}");
        using var inside = Gradient("{'at':0,'color':'#ff0000'},{'at':1,'color':'#0000ff'}");
        Assert.Equal(0, SceneKit.DifferingPixels(outside, inside, 0));

        // A stop between two at its position shows nowhere: red, green and blue at 0.5 are red, then blue.
        using var between = Gradient("{'at':0,'color':'#ff0000'},{'at':0.5,'color':'#ff0000'},{'at':0.5,'color':'#00ff00'},{'at':0.5,'color':'#0000ff'},{'at':1,'color':'#0000ff'}");
        using var edge = Gradient("{'at':0,'color':'#ff0000'},{'at':0.5,'color':'#ff0000'},{'at':0.5,'color':'#0000ff'},{'at':1,'color':'#0000ff'}");
        Assert.Equal(0, SceneKit.DifferingPixels(between, edge, 0));

        // Inside 0–1, before the first stop is its colour and after the last the last colour, whatever the spread.
        foreach (var spread in new[] { "pad", "repeat", "reflect" })
        {
            using var inner = Gradient("{'at':0.25,'color':'#ff0000'},{'at':0.75,'color':'#0000ff'}", spread);
            Assert.Equal(new SKColor(255, 0, 0, 255), inner.GetPixel(10, 10));
            Assert.Equal(new SKColor(0, 0, 255, 255), inner.GetPixel(90, 10));
        }
    }

    public static TheoryData<string, string> Defaults() => new()
    {
        // Absent members are their schema defaults (scene.schema.json, "default"); the transform steps are the cases
        // that scene.md doesn't spell out.
        { "{'type':'translate','x':20}", "{'type':'translate','x':20,'y':0}" },
        { "{'type':'translate','y':5}", "{'type':'translate','x':0,'y':5}" },
        { "{'type':'rotate','angle':30}", "{'type':'rotate','angle':30,'cx':0,'cy':0}" },
        { "{'type':'scale','x':2,'y':0.5}", "{'type':'scale','x':2,'y':0.5,'cx':0,'cy':0}" },
        { "{'type':'skew','x':20}", "{'type':'skew','x':20,'y':0}" },
        { "{'type':'skew','y':20}", "{'type':'skew','x':0,'y':20}" },
    };

    [Theory]
    [MemberData(nameof(Defaults))]
    public void An_absent_transform_member_is_its_schema_default(string absent, string written)
    {
        static SKBitmap Draw(string step)
        {
            var doc = Doc("{'kind':'group','transform':[{'type':'translate','x':30,'y':20}," + step + "],'children':["
                + "{'kind':'rect','x':-10,'y':-10,'w':40,'h':20,'fill':'#ffffff'}]}", 120, 80);
            using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options());
            return s.Render();
        }
        using var a = Draw(absent);
        using var b = Draw(written);
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, 0));
    }
}
