using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Dashes at drawing resolution (spec/docs/scene.md, strokes): every dash and gap is at least an eighth of a device
/// pixel after the stroke's whole transform, a zero-length outline draws undashed, and the dash budget's estimate
/// bounds what Skia draws at every scale a frame can be drawn at. Counted with the drawer's stroke probe, which hands
/// each dashed outline to Skia's own path effect.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneDashTests
{
    private const int Budget = 100_000;

    private static JsonElement Doc(string nodes, int width = 100, int height = 100) =>
        SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','nodes':[" + nodes + "]}"), width, height);

    private static TickSnapshot Tick(JsonElement doc, SceneRuntimeOptions? options = null)
    {
        using var runtime = new SceneRuntime(options ?? SceneKit.Options());
        runtime.Load(doc);
        return runtime.Tick(0, SceneKit.Now);
    }

    /// <summary>
    /// The dash segments Skia steps through for every stroke of <paramref name="s"/> together drawn at
    /// <paramref name="scale"/>: a dash and a gap for every dash it draws, as the budget counts them. Every snapshot
    /// here holds one item, so this is the item's total.
    /// </summary>
    private static long Dashes(TickSnapshot s, float scale)
    {
        long total = 0;
        SceneDrawer.StrokeProbe = (_, path, paint, matrix) =>
        {
            var width = paint.StrokeWidth;
            paint.StrokeWidth = 0; // the path effect's output itself, one contour per dash
            using var dashes = new SKPath();
            paint.GetFillPath(path, dashes, new SKRect(-1e9f, -1e9f, 1e9f, 1e9f), matrix);
            paint.StrokeWidth = width;
            long moves = 0;
            using (var it = dashes.CreateRawIterator())
            {
                var points = new SKPoint[4];
                SKPathVerb verb;
                while ((verb = it.Next(points)) != SKPathVerb.Done)
                    if (verb == SKPathVerb.Move) moves++;
            }
            total += 2 * moves;
        };
        try
        {
            using var bitmap = new SKBitmap((int)Math.Ceiling(s.Width * scale), (int)Math.Ceiling(s.Height * scale));
            using var canvas = new SKCanvas(bitmap);
            s.Draw(canvas, scale);
        }
        finally
        {
            SceneDrawer.StrokeProbe = null;
        }
        return total;
    }

    /// <summary>Refused as over the dash budget, or drawn within it at scale 1 and at the largest scale the frame cap allows.</summary>
    private static void RefusedOrWithinBudget(JsonElement doc, SceneRuntimeOptions? options = null)
    {
        using var s = Tick(doc, options);
        if (s.Diagnostics.Any(d => d.Code == "budget.dash"))
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            return;
        }
        Assert.Null(s.Bounds.Single().Problem);
        foreach (var scale in new[] { 1f, s.EffectiveScale(float.MaxValue) })
        {
            var most = Dashes(s, scale);
            Assert.True(most <= Budget, $"{most} dashes at scale {scale}");
        }
    }

    [Theory]
    // Measured at scale 1 the curve collapses to a zero-length chord; scaled by 1,000 Skia measures it at about 0.5
    // units and, without a floor, draws 250,000 dashes of 0.000001.
    [InlineData("M0.1 0.1 Q0.1 0.6 0.1 0.1")]
    // The same with a short line after it, so the outline isn't zero-length at scale 1 either.
    [InlineData("M0.1 0.1 Q0.1 0.6 0.1 0.1 L0.1 0.11")]
    public void A_scaled_curve_is_refused_or_draws_within_the_dash_budget(string d)
    {
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':1000,'y':1000}],'children':["
            + "{'kind':'path','d':'" + d + "','stroke':{'paint':'#ffffff','width':0.001,'dash':[0.000001,0.000001]}}]}"));
    }

    [Theory]
    [InlineData("0.0003")]
    [InlineData("0.0000001")]
    public void Scaled_glyph_outlines_are_refused_or_draw_within_the_dash_budget(string dash)
    {
        // Glyphs of size 1 scaled by 1,000: their curves are measured as a bound at any resolution, not at scale 1.
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':1000,'y':1000}],'children':["
            + "{'kind':'text','y':0.01,'font':{'family':'Inter','size':1},'fill':'#ffffff','stroke':{'paint':'#ff0000','width':0.001,'dash':["
            + dash + "," + dash + "]},'text':'Dashes'}]}"));
    }

    [Fact]
    public void A_glyph_outline_is_at_least_as_long_as_Skia_measures_it_at_any_resolution()
    {
        // Measured at scale 1, glyphs of size 1 are chords of their curves: 20.21 here against 20.36 at scale 1,000.
        var layout = TextLayoutEngine.Layout(new TextLayoutKey { Runs = [new TextRun("Dashes", "Inter", 1, 400, false)], BaseSize = 1 });
        var (length, _) = layout.GlyphOutline;
        double measured = 0;
        foreach (var line in layout.Lines)
            foreach (var run in line.Runs)
            {
                using var font = TextLayoutEngine.MakeFont(run.Typeface!, run.FontSize);
                foreach (var glyph in run.Glyphs)
                {
                    using var path = font.GetGlyphPath(glyph);
                    if (path is null || path.IsEmpty) continue;
                    using var measure = new SKPathMeasure(path, false, 1000);
                    do measured += measure.Length;
                    while (measure.NextContour());
                }
            }
        Assert.True(measured > 20, $"measured {measured}");
        Assert.True(length >= measured, $"{length} < {measured}");
    }

    [Fact]
    public void A_scaled_line_is_refused_or_draws_within_the_dash_budget()
    {
        // 500 units scaled by 100 are 50,000 device pixels: dashes of 0.001 are under the floor at every scale, so
        // the count follows the device length, which the estimate takes from the group's scale.
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':100,'y':100}],'children':["
            + "{'kind':'line','x1':0,'y1':0.5,'x2':500,'y2':0.5,'stroke':{'paint':'#ffffff','width':0.01,'dash':[0.001,0.001]}}]}"));
    }

    [Fact]
    public void A_coincident_line_draws_its_cap_without_dashing()
    {
        // Skia lengthens a zero-length line by a hair before dashing it: 244,000 dashes of 0.000000001 unfloored, and
        // as many floored when the line is scaled by 100,000,000. A zero-length outline draws as an undashed stroke
        // would, its round cap a dot, whatever the pattern and offset.
        const string line = "{'kind':'line','x1':2,'y1':2,'x2':2,'y2':2,'stroke':{'paint':'#ffffff','width':2,'cap':'round'";
        RefusedOrWithinBudget(Doc(line + ",'dash':[0.000000001,0.000000001]}}", 40, 20));
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':100000000,'y':100000000}],'children':["
            + line + ",'dash':[0.000000001,0.000000001]}}]}", 40, 20));
        using var plain = SceneKit.Render(Doc(line + "}}", 40, 20), SceneKit.Options());
        Assert.True(plain.GetPixel(2, 2).Red > 0x80, $"{plain.GetPixel(2, 2)}"); // the dot is drawn
        foreach (var dash in new[] { "'dash':[0.000000001,0.000000001]", "'dash':[1,1],'dashOffset':1" })
        {
            using var dashed = SceneKit.Render(Doc(line + "," + dash + "}}", 40, 20), SceneKit.Options());
            Assert.Equal(0, SceneKit.DifferingPixels(dashed, plain, 0));
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void A_pattern_under_the_floor_draws_as_the_floor(float scale)
    {
        // A dash of 0.05 is under an eighth of a device pixel at both scales: it draws as a dash of 0.125 / scale.
        static string Line(string dash) => "{'kind':'line','x1':10,'y1':10,'x2':110,'y2':10,'stroke':{'paint':'#ffffff','width':4,'dash':" + dash + "}}";
        var floor = (0.125 / scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var fine = SceneKit.Render(Doc(Line("[0.05,0.45]"), 120, 20), SceneKit.Options(), scale);
        using var floored = SceneKit.Render(Doc(Line("[" + floor + ",0.45]"), 120, 20), SceneKit.Options(), scale);
        Assert.Equal(0, SceneKit.DifferingPixels(fine, floored, 0));
    }

    [Fact]
    public void An_arc_is_counted_as_the_path_Skia_draws_for_it()
    {
        // At -1,000,000,000 floats are 64 apart: the arc of radius 33 is drawn from an oval of radius 64, its ends 64
        // units apart, where its radius and sweep give 5.76 units. At the thumbnail's scale of 2,048 a unit, a pattern
        // under the floor draws 1,048,576 dashes and gaps along that path. (With a sweep of 2, Skia measures the
        // rounded curve at over 122 units and gives up dashing it: over a million dashes.)
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':64,'y':64},{'type':'translate','x':999999936,'y':1000000000}],"
            + "'children':[{'kind':'arc','cx':-1000000000,'cy':-1000000000,'r':33,'start':29,'sweep':10,'close':'open',"
            + "'stroke':{'paint':'#ffffff','width':128,'dash':[0.000000001,0.000000001]}}]}", 128, 128));
    }

    [Fact]
    public void An_outline_drawn_with_no_length_draws_undashed()
    {
        // At -100,000,000 floats are 8 apart: this arc's path has every point in one place, though its radius and sweep
        // give 0.29 units, and Skia, dashing it anyway, draws 158,218 dashes and gaps at the thumbnail's scale. Zero
        // length is decided on the path drawn, as the budget measures it: drawn undashed, as its caps.
        RefusedOrWithinBudget(Doc("{'kind':'group','transform':[{'type':'scale','x':16,'y':16},{'type':'translate','x':100000000,'y':100000000}],"
            + "'children':[{'kind':'arc','cx':-100000000,'cy':-100000000,'r':33,'start':29,'sweep':0.5,'close':'open',"
            + "'stroke':{'paint':'#ffffff','width':1,'dash':[0.000000001,0.000000001]}}]}", 128, 128));
    }

    /// <summary>A trimmed curve 0.5 units long under <paramref name="transforms"/>, dashed under the floor.</summary>
    private static JsonElement TrimmedCurve(string d, string transforms, string inner = "") =>
        Doc("{'kind':'group','transform':[" + transforms + "],'children':["
            + (inner.Length > 0 ? "{'kind':'group','transform':[" + inner + "],'children':[" : "")
            + "{'kind':'path','d':'" + d + "','stroke':{'paint':'#ffffff','width':0.001,'dash':[0.000000001,0.000000001],'trimEnd':0.1}}"
            + (inner.Length > 0 ? "]}" : "") + "]}", 128, 128);

    [Fact]
    public void A_trimmed_curve_is_counted_along_its_whole_outline()
    {
        // Skia trims a curve measured at its default resolution and finds the end by parameter: a tenth of this one
        // keeps 0.1355 units, not 0.05, so the trim fraction of the outline isn't a bound for a curve.
        RefusedOrWithinBudget(TrimmedCurve("M0 0 C0.5 0 0.5 0 0.5 0", "{'type':'scale','x':6000,'y':6000}"));
    }

    [Theory]
    // Stretched 6,000 times across the curve and not along it: the floor follows the largest stretch, so Skia draws by
    // the outline's length times 6,000 although its length on the device is 0.5 units a frame pixel.
    [InlineData("{'type':'scale','x':6000,'y':1}", "")]
    // Skewed by 89° (57 times along x) and scaled by 100: the largest stretch, about 5,730, isn't the scale's.
    [InlineData("{'type':'scale','x':100,'y':100},{'type':'skew','x':89}", "")]
    // Nested: the outer group's scale and the inner one's rotation together.
    [InlineData("{'type':'scale','x':6000,'y':6000}", "{'type':'rotate','angle':30}")]
    public void A_trimmed_curve_under_any_transform_is_refused_or_draws_within_the_dash_budget(string transforms, string inner)
    {
        RefusedOrWithinBudget(TrimmedCurve("M0 0 C0 0.5 0 0.5 0 0.5", transforms, inner));
    }

    [Fact]
    public void A_trimmed_line_is_counted_by_the_part_it_draws()
    {
        // Lines are measured exactly, so a trimmed line counts only what it draws: half of 400 units at the floor of a
        // 100 × 100 frame's largest scale is about 69,000 dashes and gaps, where the whole line would be 137,000.
        const string line = "{'kind':'line','x1':-150,'y1':50,'x2':250,'y2':50,'stroke':{'paint':'#ffffff','width':1,'dash':[0.0001,0.0001],'trimEnd':0.5}}";
        using var s = Tick(Doc(line));
        Assert.Empty(s.Diagnostics);
        Assert.Null(s.Bounds.Single().Problem);
        var most = Dashes(s, s.EffectiveScale(float.MaxValue));
        Assert.InRange(most, 50_000, Budget);
    }

    [Fact]
    public void A_dashed_outline_is_drawn_from_the_path_the_budget_measured()
    {
        // One path a tick: built and measured for the budget, then drawn at every scale (no copy rebuilt per draw).
        using var s = Tick(Doc("{'kind':'arc','cx':50,'cy':50,'r':30,'start':10,'sweep':200,'close':'open',"
            + "'stroke':{'paint':'#ffffff','width':2,'dash':[3,2]}}"));
        var paths = new List<SKPath>();
        SceneDrawer.StrokeProbe = (_, path, _, _) => paths.Add(path);
        try
        {
            foreach (var scale in new[] { 1f, 2f })
            {
                using var bitmap = new SKBitmap((int)(s.Width * scale), (int)(s.Height * scale));
                using var canvas = new SKCanvas(bitmap);
                s.Draw(canvas, scale);
            }
        }
        finally
        {
            SceneDrawer.StrokeProbe = null;
        }
        Assert.Equal(2, paths.Count);
        Assert.True(ReferenceEquals(paths[0], paths[1]), "a path built for each draw"); // not Assert.Same: it would print a disposed path
    }

    // ---- one total per item ------------------------------------------------------------------------------------

    /// <summary>
    /// A circle of radius 40, a hair wide, dashed far under the floor: at a 128 × 128 frame's largest scale (32, a
    /// 4,096-pixel thumbnail) its control polygon of 320 units counts 81,922 dashes and gaps, within the budget alone.
    /// </summary>
    private const string Circle = "{'kind':'ellipse','cx':64,'cy':64,'rx':40,'ry':40,'stroke':{'paint':'#ffffff','width':0.1,'dash':[0.000000001,0.000000001]}}";

    /// <summary>A line 100 units long, dashed far under the floor: 25,602 dashes and gaps at scale 32, within the budget alone.</summary>
    private const string Line = "{'kind':'line','x1':14,'y1':64,'x2':114,'y2':64,'stroke':{'paint':'#ffffff','width':0.1,'dash':[0.000000001,0.000000001]}}";

    private static bool Refused(JsonElement doc, SceneRuntimeOptions? options = null)
    {
        using var s = Tick(doc, options);
        var refused = s.Diagnostics.Any(d => d.Code == "budget.dash");
        Assert.Equal(refused ? "budget" : null, s.Bounds.Single().Problem);
        return refused;
    }

    [Fact]
    public void Many_dashed_strokes_in_one_item_share_one_dash_budget()
    {
        // Each circle passes alone; 2,000 of them in one item would draw about 164 million dashes and gaps in one draw,
        // before the watchdog could see it. The item's dashed strokes share one total.
        Assert.False(Refused(Doc(Circle, 128, 128)));
        RefusedOrWithinBudget(Doc(Circle, 128, 128));
        Assert.True(Refused(Doc(string.Join(",", Enumerable.Repeat(Circle, 2_000)), 128, 128)));
    }

    [Theory]
    // Two siblings, each within the budget, over it together (107,524).
    [InlineData("C,L")]
    // One of them inside two nested groups: counted into the same total.
    [InlineData("C,{'kind':'group','children':[{'kind':'group','children':[L]}]}")]
    // Both in different groups.
    [InlineData("{'kind':'group','children':[C]},{'kind':'group','opacity':0.5,'children':[{'kind':'group','children':[L]}]}")]
    // The same line four times (102,408).
    [InlineData("L,L,L,L")]
    public void Dashed_strokes_within_the_budget_alone_are_over_it_together(string nodes)
    {
        Assert.False(Refused(Doc(Circle, 128, 128)));
        Assert.False(Refused(Doc(Line, 128, 128)));
        Assert.False(Refused(Doc(Line + "," + Line + "," + Line, 128, 128)));
        Assert.True(Refused(Doc(nodes.Replace("C", Circle).Replace("L", Line), 128, 128)));
    }

    [Fact]
    public void A_table_stroke_repeated_over_its_cells_counts_into_the_item_total()
    {
        // One stroke drawn over every cell's glyphs: within the budget alone, over it with the circle beside it.
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(["a"], Enumerable.Range(0, 4).Select(r => (IReadOnlyList<string>)[$"row {r}"]).ToArray()),
        };
        static JsonElement Table(string nodes) => SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':["
            + nodes + "]}"), 128, 128);
        const string table = "{'kind':'table','input':'t','rows':4,'columns':[{'column':0,'w':100}],'font':{'family':'Inter','size':12},"
            + "'fill':'#ffffff','stroke':{'paint':'#ff0000','width':0.1,'dash':[0.01,0.01]}}";
        Assert.False(Refused(Table(table), SceneKit.Options(inputs)));
        RefusedOrWithinBudget(Table(table), SceneKit.Options(inputs));
        Assert.True(Refused(Table(table + "," + Circle), SceneKit.Options(inputs)));
    }

    [Fact]
    public void An_item_with_a_few_dashed_strokes_draws_them_all()
    {
        // A gauge's usual strokes: a dashed ring, a dashed rectangle, a dashed line and dashed text, drawn as before.
        using var s = Tick(Doc("{'kind':'arc','cx':50,'cy':50,'r':30,'start':135,'sweep':270,'close':'open','stroke':{'paint':'#ffffff','width':6,'dash':[2,3]}},"
            + "{'kind':'rect','x':5,'y':5,'w':90,'h':90,'r':8,'stroke':{'paint':'#888888','width':1,'dash':[4,2]}},"
            + "{'kind':'line','x1':10,'y1':90,'x2':90,'y2':90,'stroke':{'paint':'#ffffff','width':1,'dash':[1,1]}},"
            + "{'kind':'text','x':50,'y':50,'align':'center','font':{'family':'Inter','size':16},'fill':'#ffffff','stroke':{'paint':'#ff0000','width':0.5,'dash':[1,1]},'text':'42'}"));
        Assert.Empty(s.Diagnostics);
        Assert.Null(s.Bounds.Single().Problem);
        foreach (var scale in new[] { 1f, s.EffectiveScale(float.MaxValue) })
            Assert.InRange(Dashes(s, scale), 100, Budget);
    }
}
