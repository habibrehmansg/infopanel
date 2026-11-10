using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>Measured frame and ink (spec/docs/geometry.md) and the placement transform, from the tick snapshot.</summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneBoundsTests
{
    private static IReadOnlyList<ItemBounds> Bounds(JsonElement doc, SceneRuntimeOptions? options = null)
    {
        using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, options ?? SceneKit.Options());
        return s.Bounds;
    }

    private static JsonElement Rect(string placement, string layout = "", string nodes = "[{'kind':'rect','w':40,'h':20,'fill':'#ffffff'}]") =>
        SceneKit.Json($"{{'format':'infopanel.document/1','id':'3dd19c0e-675a-4591-b096-83417853861f','profile':{{'name':'B','width':400,'height':300}},"
            + $"'items':[{{'kind':'scene','id':'a','placement':{placement}{layout},'scene':{{'format':'infopanel.scene/1','nodes':{nodes}}}}}]}}");

    [Fact]
    public void Placement_turns_about_the_pivot_as_the_worked_example()
    {
        // x 100, y 50, rotation 90, pivot (20, 10): the origin goes to (130, 40), the pivot stays at (120, 60).
        var doc = Rect("{'x':100,'y':50,'rotation':90,'pivot':[20,10]}", nodes: "[{'kind':'rect','w':1,'h':1,'fill':'#ff0000'}]");
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(doc);
        using var s = runtime.Snapshot(SceneKit.Now);
        var b = s.Bounds.Single();
        // The 1 × 1 rectangle at the origin: its centre (0.5, 0.5) maps to (130 − 0.5, 40 + 0.5).
        Assert.Equal(129.5 - 0.5, b.Frame.X, 3);
        Assert.Equal(40.5 - 0.5, b.Frame.Y, 3);
        Assert.Equal(90, b.Rotation);
        using var bmp = s.Render();
        Assert.True(bmp.GetPixel(129, 40).Green < 80);
        Assert.True(bmp.GetPixel(100, 50).Green > 200);
    }

    [Fact]
    public void An_automatic_frame_is_the_content_box_and_ink_adds_effects()
    {
        var b = Bounds(Rect("{'x':10,'y':20}", nodes: "[{'kind':'group','effects':[{'type':'glow','sigma':4,'color':'#ffffff'}],'children':[{'kind':'rect','x':-5,'w':40,'h':20,'fill':'#ffffff','stroke':{'paint':'#000000','width':2}}]}]")).Single();
        Assert.Equal(new[] { 4.0, 19, 42, 22 }, new[] { b.Frame.X, b.Frame.Y, b.Frame.W, b.Frame.H });
        Assert.Equal(new[] { -8.0, 7, 66, 46 }, new[] { b.Ink.X, b.Ink.Y, b.Ink.W, b.Ink.H });
    }

    [Fact]
    public void A_layout_box_is_the_frame_on_each_axis_it_sets()
    {
        var b = Bounds(Rect("{'x':10,'y':20}", ",'layout':{'w':100}", "[{'kind':'rect','x':-5,'y':-3,'w':40,'h':20,'fill':'#ffffff'}]")).Single();
        Assert.Equal(new[] { 10.0, 17, 100, 20 }, new[] { b.Frame.X, b.Frame.Y, b.Frame.W, b.Frame.H });
    }

    [Fact]
    public void Rotated_bounds_rotate_about_their_own_centre()
    {
        var b = Bounds(Rect("{'x':100,'y':100,'rotation':30}", ",'layout':{'w':40,'h':20}")).Single();
        // Pivot = layout centre (20, 10), which doesn't move: the frame's centre stays at (120, 110).
        Assert.Equal(100, b.Frame.X, 3);
        Assert.Equal(100, b.Frame.Y, 3);
        Assert.Equal(40, b.Frame.W, 3);
        Assert.Equal(30, b.Rotation);
    }

    [Fact]
    public void Automatic_text_grows_from_its_anchor()
    {
        string Text(string text) => $"[{{'kind':'text','align':'end','font':{{'family':'Inter','size':20}},'fill':'#ffffff','text':'{text}'}}]";
        var short1 = Bounds(Rect("{'x':300,'y':50}", nodes: Text("42"))).Single();
        var long1 = Bounds(Rect("{'x':300,'y':50}", nodes: Text("4242424242"))).Single();
        Assert.Equal(300, short1.Frame.X + short1.Frame.W, 2);
        Assert.Equal(300, long1.Frame.X + long1.Frame.W, 2);
        Assert.True(long1.Frame.W > 3 * short1.Frame.W);
        Assert.Equal(short1.Frame.H, long1.Frame.H, 2);
    }

    [Fact]
    public void Groups_report_the_union_of_their_visible_children()
    {
        var doc = SceneKit.Json("{'format':'infopanel.document/1','id':'3dd19c0e-675a-4591-b096-83417853861f','profile':{'name':'B','width':400,'height':300},'items':["
            + "{'kind':'group','id':'g','placement':{'x':100,'y':100},'children':["
            + "{'kind':'scene','id':'a','placement':{'x':0,'y':0},'scene':{'format':'infopanel.scene/1','nodes':[{'kind':'rect','w':10,'h':10,'fill':'#ffffff'}]}},"
            + "{'kind':'scene','id':'b','placement':{'x':50,'y':20},'scene':{'format':'infopanel.scene/1','nodes':[{'kind':'rect','w':10,'h':10,'fill':'#ffffff'}]}},"
            + "{'kind':'scene','id':'hidden','visible':false,'placement':{'x':300,'y':300},'scene':{'format':'infopanel.scene/1','nodes':[{'kind':'rect','w':10,'h':10,'fill':'#ffffff'}]}}]},"
            + "{'kind':'group','id':'empty','placement':{'x':0,'y':0},'children':[]}]}");
        var all = Bounds(doc);
        Assert.Equal(new[] { "g", "a", "b" }, all.Select(b => b.Id));
        var g = all[0];
        Assert.Equal(new[] { 100.0, 100, 60, 30 }, new[] { g.Frame.X, g.Frame.Y, g.Frame.W, g.Frame.H });
        Assert.Equal(150, all[2].Frame.X);
    }

    /// <summary>
    /// A gauge's outline: a ring segment of two SVG arcs (radii 190.5 and 139.5 about (202, 212)) joined by straight
    /// ends, stroked 4 wide with miter joins. Its ink reaches the outer arc's extremes (x 11.5 to 392.5, top 21.5) and the
    /// stroke's conservative reach past them; never the arcs' control points, which lie up to 1.41 radii from the centre.
    /// </summary>
    private const string GaugeOutline = "[{'kind':'path','d':'M15.75 252A190.5 190.5 0 1 1 388.25 252L339 236A139.5 139.5 0 1 0 65 236Z',"
        + "'stroke':{'paint':'#ffffff','width':4,'join':'miter'}}]";

    [Fact]
    public void An_arc_outline_is_measured_by_its_curve_not_its_control_points()
    {
        var b = Bounds(Rect("{'x':0,'y':0}", ",'layout':{'w':400,'h':258}", GaugeOutline)).Single();
        // At the arc's extremes the stroke reaches half its width (2); the miter joins are at the ends, inside them. And
        // the box holds every pixel drawn.
        Assert.Equal(11.5 - 2, b.Ink.X, 0.2);
        Assert.Equal(21.5 - 2, b.Ink.Y, 0.2);
        Assert.Equal(392.5 + 2, b.Ink.X + b.Ink.W, 0.2);
        AssertHoldsPaint(Canvas400(0, 0, GaugeOutline));
    }

    [Fact]
    public void An_arc_outline_really_past_the_edge_is_measured_past_it()
    {
        var b = Bounds(Rect("{'x':-20,'y':-30}", ",'layout':{'w':400,'h':258}", GaugeOutline)).Single();
        Assert.Equal(-20 + 11.5 - 2, b.Ink.X, 0.2);
        Assert.Equal(-30 + 21.5 - 2, b.Ink.Y, 0.2);
        AssertHoldsPaint(Canvas400(-20, -30, GaugeOutline));
    }

    /// <summary>
    /// A stroked curve that doubles back to where it starts: its ends are one point, but the curve reaches (150, 50) and
    /// is drawn there. Its ink and automatic frame hold that reach and its stroke, and not the control point (200, 0).
    /// </summary>
    [Fact]
    public void A_curve_that_doubles_back_is_measured_by_its_reach()
    {
        var b = Bounds(Rect("{'x':0,'y':0}", nodes: "[{'kind':'path','d':'M100 100 Q200 0 100 100','stroke':{'paint':'#ffffff','width':4}}]")).Single();
        foreach (var box in new[] { b.Ink, b.Frame })
        {
            // The stroke reaches half its width across the curve, about 1.4 on each axis along its 45° line.
            Assert.InRange(box.X, 98, 99);
            Assert.InRange(box.Y, 47, 49);
            Assert.InRange(box.X + box.W, 151, 153);
            Assert.InRange(box.Y + box.H, 101, 102);
        }
    }

    /// <summary>
    /// A ring drawn as an SVG path: a circle of radius 48 about (50, 50) stroked 4 wide with the default miter join, so
    /// its stroke just touches the square (0, 0)–(100, 100). A smooth outline has no corner a miter could reach past.
    /// </summary>
    private const string RingPath = "{'kind':'path','d':'M2 50A48 48 0 1 1 98 50A48 48 0 1 1 2 50Z','stroke':{'paint':'#ffffff','width':4}}";

    private static string Grouped(string transform, string child) => $"[{{'kind':'group','transform':{transform},'children':[{child}]}}]";

    private static JsonElement Canvas400(double x, double y, string nodes) =>
        SceneKit.Json("{'format':'infopanel.document/1','id':'3dd19c0e-675a-4591-b096-83417853861f','profile':{'name':'B','width':400,'height':400,'background':'#000000'},"
            + $"'items':[{{'kind':'scene','id':'a','placement':{{'x':{x},'y':{y}}},'scene':{{'format':'infopanel.scene/1','nodes':{nodes}}}}}]}}");

    /// <summary>The item's bounds and the box of the pixels it paints on a black canvas, at scale 1.</summary>
    private static (ItemBounds Bounds, SkiaSharp.SKRect Painted) BoundsAndPixels(JsonElement doc)
    {
        var (b, painted) = BoundsAndPixelsIfAny(doc);
        Assert.True(painted is not null, "nothing painted");
        return (b, painted.Value);
    }

    /// <summary>As <see cref="BoundsAndPixels"/>; null when nothing is painted on the canvas.</summary>
    private static (ItemBounds Bounds, SkiaSharp.SKRect? Painted) BoundsAndPixelsIfAny(JsonElement doc)
    {
        using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options());
        var b = s.Bounds.Single();
        using var bmp = s.Render();
        int l = int.MaxValue, t = int.MaxValue, r = -1, bottom = -1;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red == 0 && c.Green == 0 && c.Blue == 0) continue;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); bottom = Math.Max(bottom, y);
            }
        }
        return (b, r >= 0 ? new SkiaSharp.SKRect(l, t, r + 1, bottom + 1) : null);
    }

    [Fact]
    public void A_scaled_ring_is_measured_to_its_stroke_not_to_a_miter_it_has_no_corner_for()
    {
        // Scaled 1.2 at x = 0: the stroke touches the 120 × 120 layout box and reaches no further.
        var b = Bounds(Rect("{'x':0,'y':0}", ",'layout':{'w':120,'h':120}", Grouped("[{'type':'scale','x':1.2,'y':1.2}]", RingPath))).Single();
        Assert.Equal(0, b.Ink.X, 0.25);
        Assert.Equal(0, b.Ink.Y, 0.25);
        Assert.Equal(120, b.Ink.W, 0.25);
        Assert.Equal(120, b.Ink.H, 0.25);
    }

    [Fact]
    public void A_turned_ring_is_measured_as_drawn_not_as_its_box_turned()
    {
        // Turned 135° about its centre (its gap at the bottom, as a gauge): the ring's box is the same 100 × 100.
        var (b, painted) = BoundsAndPixels(Canvas400(150, 150, Grouped("[{'type':'rotate','angle':135,'cx':50,'cy':50}]", RingPath)));
        Assert.InRange(b.Ink.W, painted.Width - 1, painted.Width + 1);
        Assert.InRange(b.Ink.H, painted.Height - 1, painted.Height + 1);
        Assert.InRange(b.Ink.X, painted.Left - 1, painted.Left + 1);
        Assert.InRange(b.Ink.Y, painted.Top - 1, painted.Top + 1);
    }

    private static readonly string[] OracleKinds =
    [
        "{'kind':'path','d':'M10 85L50 15L90 85Q50 55 20 80','stroke':S}",
        "{'kind':'polyline','points':[[10,80],[30,20],[50,80],[70,20],[90,80]],'stroke':S}",
        "{'kind':'arc','cx':50,'cy':50,'r':40,'inner':25,'start':135,'sweep':270,'close':'sector','stroke':S}",
        "{'kind':'arc','cx':50,'cy':50,'r':40,'start':200,'sweep':140,'close':'open','stroke':S}",
        "{'kind':'rect','x':15,'y':25,'w':70,'h':50,'stroke':S}",
        "{'kind':'ellipse','cx':50,'cy':50,'rx':40,'ry':25,'stroke':S}",
    ];

    private static readonly string?[] OracleGroups =
    [
        null,
        "[{'type':'scale','x':1.5,'y':0.8,'cx':50,'cy':50}]",
        "[{'type':'rotate','angle':30,'cx':50,'cy':50}]",
        "[{'type':'rotate','angle':135,'cx':50,'cy':50}]",
        "[{'type':'skew','x':20,'y':10}]",
    ];

    public static TheoryData<int, string, string, int> OracleCases()
    {
        var data = new TheoryData<int, string, string, int>();
        for (var k = 0; k < OracleKinds.Length; k++)
            foreach (var join in new[] { "miter", "round", "bevel" })
                foreach (var cap in new[] { "butt", "square" })
                    for (var g = 0; g < OracleGroups.Length; g++)
                        data.Add(k, join, cap, g);
        return data;
    }

    /// <summary>
    /// Each kind drawn alone with no effects, every join and cap, plain, scaled, turned and skewed: its reported box holds
    /// every pixel it paints and passes them by no more than 1.5 px a side (spec/docs/geometry.md: the content box is the
    /// outlines with their strokes).
    /// </summary>
    [Theory]
    [MemberData(nameof(OracleCases))]
    public void The_content_box_holds_the_drawn_pixels_tightly(int kind, string join, string cap, int group)
    {
        var node = OracleKinds[kind].Replace("'stroke':S", $"'stroke':{{'paint':'#ffffff','width':8,'join':'{join}','cap':'{cap}'}}");
        var nodes = OracleGroups[group] is { } transform ? Grouped(transform, node) : $"[{node}]";
        var (b, painted) = BoundsAndPixels(Canvas400(150, 150, nodes));
        var box = new SkiaSharp.SKRect((float)b.Ink.X, (float)b.Ink.Y, (float)(b.Ink.X + b.Ink.W), (float)(b.Ink.Y + b.Ink.H));
        var said = $"box {box}, painted {painted}";
        // Holds every painted pixel (reaches into the outermost ones, give or take the quarter pixel Skia's drawing of a
        // curve may stray from the curve: a round join drawn at scale 1 can paint a fifth of the row past its circle),
        // and passes them by at most 1.5 px.
        const float Curve = 0.25f;
        Assert.True(box.Left <= painted.Left + 1 + Curve && box.Top <= painted.Top + 1 + Curve
            && box.Right >= painted.Right - 1 - Curve && box.Bottom >= painted.Bottom - 1 - Curve, said);
        Assert.True(box.Left >= painted.Left - 1.5 && box.Top >= painted.Top - 1.5 && box.Right <= painted.Right + 1.5 && box.Bottom <= painted.Bottom + 1.5, said);
    }

    [Fact]
    public void A_path_scaled_far_is_measured_at_the_resolution_it_is_drawn_at()
    {
        // Stretched 100 times down the canvas, an outline made at the path's own resolution misses the curve's offset by
        // several pixels. Moved up so the stroke's bottom lands on the canvas: the box reaches its last painted row (past
        // the scale the stroke is measured exactly at, the box may pass it).
        var node = "{'kind':'path','d':'M71 -87C-4 -66 99 70 -1 47','stroke':{'paint':'#ffffff','width':20}}";
        var (b, painted) = BoundsAndPixels(Canvas400(150, -5700, Grouped("[{'type':'scale','x':1,'y':100}]", node)));
        var bottom = b.Ink.Y + b.Ink.H;
        Assert.True(bottom >= painted.Bottom - 1, $"box bottom {bottom}, painted bottom {painted.Bottom}");
    }

    /// <summary>
    /// The item's content box holds every pixel it paints (one-sided: it may be loose), give or take the pixel a box edge
    /// reaches into and the quarter pixel Skia's drawing of a curve may stray from it, as the oracle above. Something must
    /// be painted on the canvas (the positive control): a blank canvas would show nothing about the box.
    /// </summary>
    private static void AssertHoldsPaint(JsonElement doc) =>
        Assert.True(HoldsPaint(doc), "nothing painted: the canvas doesn't show the paint the test expects there");

    /// <summary>As <see cref="AssertHoldsPaint"/>, for one of several windows: false when this one is left black.</summary>
    private static bool HoldsPaint(JsonElement doc)
    {
        var (b, any) = BoundsAndPixelsIfAny(doc);
        if (any is not { } painted) return false;
        var box = new SkiaSharp.SKRect((float)b.Ink.X, (float)b.Ink.Y, (float)(b.Ink.X + b.Ink.W), (float)(b.Ink.Y + b.Ink.H));
        const float Slack = 1.25f;
        Assert.True(box.Left <= painted.Left + Slack && box.Top <= painted.Top + Slack
            && box.Right >= painted.Right - Slack && box.Bottom >= painted.Bottom - Slack, $"box {box}, painted {painted}");
        return true;
    }

    [Fact]
    public void A_wide_round_capped_curve_stretched_far_holds_its_paint()
    {
        // Stretched 1000 times across, Skia's outline of this curve made at a high resolution loses its round cap's reach
        // (left edge −113.7 in the path's units, the cap reaches −172), while the drawing paints the canvas's first column.
        var node = "{'kind':'path','d':'M92 -78 C65 74 90 -93 28 52','stroke':{'paint':'#ffffff','width':400,'cap':'round'}}";
        AssertHoldsPaint(Canvas400(114000, 100, Grouped("[{'type':'scale','x':1000,'y':1}]", node)));
    }

    [Fact]
    public void A_nearly_straight_wide_curve_stretched_far_holds_its_paint()
    {
        // Skia strokes this nearly straight curve as its chord, whose ends turn the stroke's butt ends from the curve's:
        // stretched 1000 times across, the drawing paints the canvas's first column, far left of the curve's own ends.
        var node = "{'kind':'path','d':'M32 98 Q-15 28 -43 -13','stroke':{'paint':'#ffffff','width':200}}";
        AssertHoldsPaint(Canvas400(125700, 150, Grouped("[{'type':'scale','x':1000,'y':1}]", node)));
    }

    [Fact]
    public void A_nearly_straight_wide_curve_scaled_holds_its_paint()
    {
        // The same curve scaled 4 times both ways, within the scale its stroke is measured exactly at: the chord's turned
        // ends reach a few pixels past the curve's own.
        AssertHoldsPaintAtItsEdge("M32 98 Q-15 28 -43 -13", 200, "butt", 4, 4, far: false);
    }

    [Fact]
    public void A_thin_curve_stretched_far_holds_its_paint()
    {
        // Stretched 1000 times across, Skia's offset of this curve strays 0.006 of a unit past the ideal, six pixels.
        var node = "{'kind':'path','d':'M-3 85 Q-89 -22 67 -55','stroke':{'paint':'#ffffff','width':4,'cap':'round'}}";
        AssertHoldsPaint(Canvas400(35762, 182, Grouped("[{'type':'scale','x':1000,'y':1}]", node)));
    }

    /// <summary>Wide strokes on small curves: one single curve, much tighter than its stroke, and a curve and a corner.</summary>
    private static readonly (string D, float Width)[] WideCurves =
    [
        ("M92 -78 C65 74 90 -93 28 52", 400),
        ("M71 -87C-4 -66 99 70 -1 47L60 60", 20),
    ];

    public static TheoryData<int, string, double, double, bool> WideCurveCases()
    {
        var data = new TheoryData<int, string, double, double, bool>();
        (double X, double Y)[] scales = [(1, 1), (4, 4), (4, 1), (10, 1), (100, 1), (1000, 1), (1, 4), (1, 10), (1, 100), (1, 1000)];
        for (var k = 0; k < WideCurves.Length; k++)
            foreach (var cap in new[] { "butt", "round", "square" })
                foreach (var (x, y) in scales)
                    foreach (var far in new[] { false, true })
                        data.Add(k, cap, x, y, far);
        return data;
    }

    /// <summary>
    /// A wide stroke on a small curve, stretched up to 1000 times along either axis (4, the most its stroke is measured
    /// exactly at, and past it): its box holds what is painted, in
    /// windows placed independently of the box, at the edges of the outline Skia strokes (<see cref="AssertHoldsPaintAtItsEdge"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(WideCurveCases))]
    public void A_wide_curve_stretched_holds_its_paint(int curve, string cap, double sx, double sy, bool far) =>
        AssertHoldsPaintAtItsEdge(WideCurves[curve].D, WideCurves[curve].Width, cap, sx, sy, far);

    /// <summary>
    /// A path (miter joins) scaled by (<paramref name="sx"/>, <paramref name="sy"/>) holds its paint where the outline
    /// Skia strokes at the drawing's resolution reaches furthest along the more stretched axis (x when equal): the item is
    /// moved so that outline's near edge (left or top), or its far one, lands in the canvas's middle, and each window
    /// across the outline's extent on the other axis holds its paint; at least one of them shows some (the positive control).
    /// </summary>
    private static void AssertHoldsPaintAtItsEdge(string d, float width, string cap, double sx, double sy, bool far)
    {
        var node = $"{{'kind':'path','d':'{d}','stroke':{{'paint':'#ffffff','width':{width},'cap':'{cap}'}}}}";
        var nodes = Grouped($"[{{'type':'scale','x':{sx},'y':{sy}}}]", node);
        using var path = SkiaSharp.SKPath.ParseSvgPathData(d);
        using var paint = new SkiaSharp.SKPaint
        {
            Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = width, StrokeJoin = SkiaSharp.SKStrokeJoin.Miter, StrokeMiter = 4,
            StrokeCap = cap switch { "round" => SkiaSharp.SKStrokeCap.Round, "square" => SkiaSharp.SKStrokeCap.Square, _ => SkiaSharp.SKStrokeCap.Butt },
        };
        using var outline = new SkiaSharp.SKPath();
        Assert.True(paint.GetFillPath(path, outline, (float)Math.Max(sx, sy)));
        outline.Transform(SkiaSharp.SKMatrix.CreateScale((float)sx, (float)sy));
        var o = outline.ComputeTightBounds();
        var alongX = sx >= sy;
        var edge = alongX ? (far ? o.Right : o.Left) : (far ? o.Bottom : o.Top);
        var (from, to) = alongX ? (o.Top, o.Bottom) : (o.Left, o.Right);
        // The canvas's windows across the outline's whole extent on the other axis; whole numbers, so the document's text
        // reads the same in any culture.
        var painted = 0;
        for (var start = Math.Floor(from); start < to; start += 400)
        {
            var doc = alongX ? Canvas400(Math.Round(200 - edge), -start, nodes) : Canvas400(-start, Math.Round(200 - edge), nodes);
            if (HoldsPaint(doc)) painted++;
        }
        Assert.True(painted > 0, "nothing painted: no window shows the paint at the outline's edge");
    }

    /// <summary>
    /// A start of 10⁹ degrees (an animated spinner's after a long run) is 2,777,777 turns and 280°: drawn and measured as the
    /// arc at 280°. A float can't hold 10⁹ + 30 (its step there is 64), so a start left unreduced loses or snaps the sweep.
    /// </summary>
    [Theory]
    [InlineData("'close':'open'")]
    [InlineData("'close':'sector','inner':100")]
    public void An_arc_at_a_large_start_angle_is_drawn_and_measured_as_its_start_less_whole_turns(string close)
    {
        static string Arc(string start, string close) =>
            $"[{{'kind':'arc','cx':200,'cy':200,'r':150,'start':{start},'sweep':30,{close},'stroke':{{'paint':'#ffffff','width':4}}}}]";
        var (reduced, reducedPainted) = BoundsAndPixels(Canvas400(0, 0, Arc("280", close)));
        var (large, largePainted) = BoundsAndPixels(Canvas400(0, 0, Arc("1e9", close)));
        Assert.Equal(new[] { reduced.Ink.X, reduced.Ink.Y, reduced.Ink.W, reduced.Ink.H }, new[] { large.Ink.X, large.Ink.Y, large.Ink.W, large.Ink.H });
        Assert.Equal(reducedPainted, largePainted);
    }

    [Theory]
    [InlineData("'close':'open'")]
    [InlineData("'close':'sector','inner':20")]
    [InlineData("'close':'sector'")]
    public void A_full_sweep_arc_not_started_on_a_quarter_holds_its_square_caps(string close)
    {
        // Skia draws a full sweep from 80° as an arc that ends where it starts, not closed: its square caps are drawn.
        var node = $"{{'kind':'arc','cx':100,'cy':100,'r':40,'start':80,'sweep':360,{close},'stroke':{{'paint':'#ffffff','width':80,'cap':'square'}}}}";
        var (b, painted) = BoundsAndPixels(Canvas400(100, 100, $"[{node}]"));
        var box = new SkiaSharp.SKRect((float)b.Ink.X, (float)b.Ink.Y, (float)(b.Ink.X + b.Ink.W), (float)(b.Ink.Y + b.Ink.H));
        Assert.True(box.Left <= painted.Left + 1 && box.Top <= painted.Top + 1 && box.Right >= painted.Right - 1 && box.Bottom >= painted.Bottom - 1,
            $"box {box}, painted {painted}");
    }

    private static RStroke Stroke4() => new(new RSolid(0xffffffff), 4, SkiaSharp.SKStrokeCap.Butt, SkiaSharp.SKStrokeJoin.Miter, 4, null, 0, 0, 1);

    private static RNode[] Turned(StaticPathGeom geom, float degrees, float dx = 0) =>
    [
        new RGroup
        {
            Matrix = SkiaSharp.SKMatrix.CreateTranslation(dx, 0).PreConcat(SkiaSharp.SKMatrix.CreateRotationDegrees(degrees, 50, 50)),
            Children = [new RShape { Geom = geom, Stroke = Stroke4() }],
        },
    ];

    [Fact]
    public void A_turned_static_path_is_outlined_once_not_at_every_tick()
    {
        var geom = StaticPathGeom.Parse("M2 50A48 48 0 1 1 98 50A48 48 0 1 1 2 50Z", SkiaSharp.SKPathFillType.Winding);
        var first = SceneBounds.Measure(Turned(geom, 135))!.Value.Box;
        var second = SceneBounds.Measure(Turned(geom, 135))!.Value.Box; // the next tick: new nodes, the same path and stroke
        var moved = SceneBounds.Measure(Turned(geom, 135, dx: 30))!.Value.Box; // moved, not turned further
        Assert.Equal(1, geom.OutlinesBuilt);
        Assert.Equal(first, second);
        Assert.Equal(first.Left + 30, moved.Left, 3);
        Assert.Equal(first.Right + 30, moved.Right, 3);
    }

    [Fact]
    public void A_static_path_keeps_a_few_outlines_not_one_for_every_turn()
    {
        var geom = StaticPathGeom.Parse("M2 50A48 48 0 1 1 98 50A48 48 0 1 1 2 50Z", SkiaSharp.SKPathFillType.Winding);
        for (var a = 0; a < 40; a++) _ = SceneBounds.Measure(Turned(geom, 3 + a));
        Assert.Equal(40, geom.OutlinesBuilt);
        Assert.InRange(geom.OutlinesKept, 1, StaticPathGeom.OutlinesKeptAtMost);
    }

    [Theory]
    [InlineData("document/placeholder/invalid-scene.json", "a", "invalid")]
    [InlineData("document/placeholder/invalid-scene-schema.json", "a", "invalid")]
    [InlineData("document/placeholder/unsupported-scene.json", "b", "unsupported")]
    [InlineData("document/placeholder/unsupported-capability.json", "a", "unsupported")]
    public void Placeholders_fill_their_layout_box_and_report_the_problem(string example, string id, string problem)
    {
        var b = Bounds(SceneKit.Example(example), SceneKit.Options()).Single(x => x.Id == id);
        Assert.Equal(problem, b.Problem);
        Assert.Equal(100, b.Frame.W);
        Assert.Equal(100, b.Frame.H);
    }

    [Theory]
    [InlineData("document/placeholder/budget-nodes.json", "documentNodes", 3)]
    [InlineData("document/placeholder/budget-text.json", "documentTextNodes", 1)]
    [InlineData("document/placeholder/budget-work.json", "documentWork", 2)]
    [InlineData("document/placeholder/budget-effects.json", "documentEffectCost", 3)]
    public void Over_budget_items_are_admitted_in_document_order(string example, string limit, double value)
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { [limit] = value });
        var all = Bounds(SceneKit.Example(example), SceneKit.Options(limits: limits));
        Assert.Null(all.Single(b => b.Id == "a").Problem);
        Assert.Equal("budget", all.Single(b => b.Id == "b").Problem);
    }
}
