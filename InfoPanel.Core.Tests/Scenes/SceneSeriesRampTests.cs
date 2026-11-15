using System.Data;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A colour per bar or cell (spec/docs/smart-nodes.md, <c>series</c>): <c>ramp</c>, its stops read and placed as a
/// gradient's, each sample's colour at its clamped position between this tick's <c>min</c> and <c>max</c>; mode
/// <c>cells</c> and its geometry; what <c>line</c> and <c>area</c> do with a ramp; the cost; and what the validator
/// refuses, each row the editor's validator's too (its parity rows).
/// </summary>
public sealed class SceneSeriesRampTests
{
    /// <summary>A ramp whose colours at 0, 0.25, 0.5 and 1 are whole channel values: #102030, #405060, #708090, #d0e0f0.</summary>
    private const string Ramp = "[{'at':0,'color':'#102030'},{'at':1,'color':'#d0e0f0'}]";

    private const string Background = "#15191f";

    private static string Scene(string nodes, string inputType = "number", string values = "") =>
        "{'format':'infopanel.scene/1','inputs':{'v':{'type':'" + inputType + "'},'k':{'type':'number'}}," + values + "'nodes':[" + nodes + "]}";

    /// <summary>The input <c>v</c> holding <paramref name="samples"/> as its history (the last newest).</summary>
    private static FakeSceneInputs History(params double[] samples)
    {
        var inputs = new FakeSceneInputs();
        inputs.Number("test:v", samples.Length > 0 ? samples[^1] : 0);
        inputs.Histories["test:v"] = samples;
        return inputs;
    }

    /// <summary>The nodes drawn on a canvas of <paramref name="width"/> × <paramref name="height"/>, the scene at (<paramref name="x"/>, 0).</summary>
    private static SKBitmap Draw(string nodes, FakeSceneInputs inputs, int width = 40, int height = 20, double x = 0, string values = "") =>
        SceneKit.Render(SceneKit.Doc(SceneKit.Json(Scene(nodes, values: values)), width, height, x, 0, background: Background + "ff"), SceneKit.Options(inputs));

    /// <summary>A 40 × 20 series of one cell, 10 wide at the box's right edge, with <see cref="Ramp"/>.</summary>
    private static string OneCell(string range) =>
        "{'kind':'series','input':'v','w':40,'h':20," + range + ",'mode':'cells','pitch':10,'count':1,'ramp':" + Ramp + "}";

    private static string Hex(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";

    private static string CellColour(string range, double sample)
    {
        using var bitmap = Draw(OneCell(range), History(sample));
        return Hex(bitmap.GetPixel(35, 10));
    }

    // ---- the colour's position: clamp((v − min) ÷ (max − min), 0, 1), 0 when max ≤ min ---------------------------------

    [Theory]
    [InlineData(0, "#102030")]
    [InlineData(100, "#d0e0f0")]
    public void A_sample_at_min_takes_the_colour_at_0_and_at_max_the_colour_at_1(double sample, string colour) =>
        Assert.Equal(colour, CellColour("'min':0,'max':100", sample));

    [Theory]
    [InlineData(-50, "#102030")]
    [InlineData(250, "#d0e0f0")]
    public void Below_min_and_above_max_the_position_is_clamped(double sample, string colour) =>
        Assert.Equal(colour, CellColour("'min':0,'max':100", sample));

    [Theory]
    [InlineData(25, "#405060")]
    [InlineData(50, "#708090")]
    public void Between_them_the_position_is_v_minus_min_over_max_minus_min(double sample, string colour) =>
        Assert.Equal(colour, CellColour("'min':0,'max':100", sample));

    [Theory]
    [InlineData("'min':100,'max':0", 50)]
    [InlineData("'min':50,'max':50", 80)]
    public void When_max_is_at_or_below_min_the_position_is_0(string range, double sample) =>
        Assert.Equal("#102030", CellColour(range, sample));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_sample_that_is_not_finite_reads_0(double sample) =>
        // Over −100 … 100, the sample 0 is at 0.5.
        Assert.Equal("#708090", CellColour("'min':-100,'max':100", sample));

    [Fact]
    public void The_range_is_the_one_the_series_draws_with_this_tick()
    {
        // An automatic max, the window's greatest sample (40): the samples 10, 20 and 40 are at 0.25, 0.5 and 1.
        var series = "{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':{'op':'history','input':'v','fn':'max','count':4},'mode':'cells','pitch':10,'count':4,'ramp':" + Ramp + "}";
        using var bitmap = Draw(series, History(10, 20, 30, 40));
        Assert.Equal("#405060", Hex(bitmap.GetPixel(5, 10)));
        Assert.Equal("#708090", Hex(bitmap.GetPixel(15, 10)));
        Assert.Equal("#d0e0f0", Hex(bitmap.GetPixel(35, 10)));
    }

    [Fact]
    public void A_bar_takes_its_samples_colour()
    {
        // 50 of 0 … 100: a bar from the box's middle down to its bottom, in the colour at 0.5.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'bars','pitch':10,'count':1,'ramp':" + Ramp + "}", History(50));
        Assert.Equal("#708090", Hex(bitmap.GetPixel(35, 15)));
        Assert.Equal(Background, Hex(bitmap.GetPixel(35, 5)));
    }

    // ---- sampled between the stops as a gradient of the same stops is ----------------------------------------------

    /// <summary>
    /// Opaque stops written as an author might (smooth, hard, out of order, outside 0–1, short of the ends), and at how
    /// many of the nine positions a channel may round 1 apart: 2, where the exact value is a half (the gradient computes
    /// it in single precision). Stops whose alpha changes are compared with the rule itself, below
    /// (<see cref="A_ramps_colour_is_the_premultiplied_rule_computed_exactly_on_and_between_the_eighths"/>), and with a
    /// drawn gradient only for the gap the gradient's approximation leaves.
    /// </summary>
    public static TheoryData<string, int> Stops() => new()
    {
        // Four opaque stops, smooth.
        { "[{'at':0,'color':'#1e3a8a'},{'at':0.3,'color':'#22c55e'},{'at':0.6,'color':'#f59e0b'},{'at':1,'color':'#ef4444'}]", 2 },
        // A hard edge at 0.5: below it the first colour, at it and above the second.
        { "[{'at':0,'color':'#22c55e'},{'at':0.5,'color':'#22c55e'},{'at':0.5,'color':'#ef4444'},{'at':1,'color':'#ef4444'}]", 2 },
        // Three stops at 0.5: the one between shows nowhere.
        { "[{'at':0,'color':'#22c55e'},{'at':0.5,'color':'#1e3a8a'},{'at':0.5,'color':'#ffffff'},{'at':0.5,'color':'#ef4444'},{'at':1,'color':'#f59e0b'}]", 2 },
        // Out of order: placed at 0, 0.625, 0.625, 1 (raised, never reordered).
        { "[{'at':0,'color':'#000000'},{'at':0.625,'color':'#ffffff'},{'at':0.375,'color':'#ff0000'},{'at':1,'color':'#0000ff'}]", 2 },
        // Outside 0–1: clamped.
        { "[{'at':-0.5,'color':'#000000'},{'at':0.25,'color':'#00ff00'},{'at':1.5,'color':'#0000ff'}]", 2 },
        // Neither end at 0 or 1: the end colours outside the stops.
        { "[{'at':0.25,'color':'#ff00ff'},{'at':0.75,'color':'#00ffff'}]", 2 },
    };

    /// <summary>
    /// With opaque stops, the ramp's colour and a gradient's at the same nine positions, on the CPU: the same colour, the
    /// segment, the hard edges and the clamping exactly the gradient's, a channel's last step rounded apart at most (see
    /// <see cref="Stops"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(Stops))]
    public void With_opaque_stops_a_ramped_cell_has_the_colour_a_gradient_of_the_same_stops_has_at_its_position(string stops, int roundedApart)
    {
        // Nine cells a pixel wide holding 0, 12.5 … 100 (positions 0, 1/8 … 1), and below them a rectangle whose
        // gradient of the same stops puts the same positions at the same pixel columns' centres.
        var nodes = "{'kind':'series','input':'v','w':9,'h':4,'min':0,'max':100,'mode':'cells','pitch':1,'count':9,'ramp':" + stops + "},"
            + "{'kind':'rect','y':6,'w':9,'h':4,'fill':{'type':'linear','x1':0.5,'y1':0,'x2':8.5,'y2':0,'stops':" + stops + "}}";
        using var bitmap = Draw(nodes, History([.. Enumerable.Range(0, 9).Select(c => 12.5 * c)]), 9, 10);
        var apart = 0;
        for (var c = 0; c < 9; c++)
        {
            var (ramp, gradient) = (bitmap.GetPixel(c, 2), bitmap.GetPixel(c, 8));
            var most = Math.Max(Math.Abs(ramp.Red - gradient.Red), Math.Max(Math.Abs(ramp.Green - gradient.Green), Math.Abs(ramp.Blue - gradient.Blue)));
            Assert.True(most <= 1 && ramp.Alpha == gradient.Alpha, $"position {c}/8: the ramp's {ramp}, the gradient's {gradient}");
            if (most > 0) apart++;
        }
        Assert.True(apart <= roundedApart, $"{apart} of the nine positions round apart");
    }

    /// <summary>
    /// Stops, each <c>(at, "#rrggbb[aa]")</c> in order within 0–1, whose alpha changes (unequal and not zero, 1/16 opaque
    /// red to opaque blue first; from transparent; three stops; a hard edge between translucent colours),
    /// and one opaque set.
    /// </summary>
    private static readonly (double At, string Color)[][] RuleStops =
    [
        [(0, "#ff000010"), (1, "#0000ffff")],
        [(0, "#ff000000"), (1, "#0000ff")],
        [(0, "#22c55e80"), (0.5, "#f59e0bff"), (1, "#1e3a8a40")],
        [(0, "#22c55e40"), (0.55, "#22c55e40"), (0.55, "#ef4444"), (1, "#ef4444c0")],
        [(0, "#1e3a8a"), (0.3, "#22c55e"), (0.6, "#f59e0b"), (1, "#ef4444")],
    ];

    public static TheoryData<int> RuleStopSets() => [.. Enumerable.Range(0, RuleStops.Length)];

    private static string StopsJson((double At, string Color)[] stops) =>
        "[" + string.Join(",", stops.Select(s => "{'at':" + s.At.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'color':'" + s.Color + "'}")) + "]";

    /// <summary>
    /// The format's rule for a gradient's colour at <paramref name="t"/> (spec/docs/scene.md, the drawing model), computed
    /// here on its own, before rounding, in levels (0–255) of each straight channel and the alpha: before the first stop
    /// the first colour, from the last stop (the last of several at one position) the last; between two stops each colour
    /// premultiplied by its alpha, the two mixed linearly, then divided by the mixed alpha; a mixed alpha of 0 is
    /// transparent black.
    /// </summary>
    private static double[] Rule((double At, string Color)[] stops, double t)
    {
        static double[] Straight(string hex)
        {
            int Hex2(int i) => Convert.ToInt32(hex.Substring(i, 2), 16);
            return [Hex2(1), Hex2(3), Hex2(5), hex.Length == 9 ? Hex2(7) : 255];
        }
        if (t < stops[0].At) return Straight(stops[0].Color);
        var k = Array.FindLastIndex(stops, s => s.At <= t);
        if (k == stops.Length - 1) return Straight(stops[k].Color);
        var (a, b) = (Straight(stops[k].Color), Straight(stops[k + 1].Color));
        var u = (t - stops[k].At) / (stops[k + 1].At - stops[k].At);
        double aa = a[3] / 255, ba = b[3] / 255;
        var alpha = (1 - u) * aa + u * ba;
        if (alpha <= 0) return [0, 0, 0, 0];
        double Mix(int i) => ((1 - u) * a[i] * aa + u * b[i] * ba) / alpha;
        return [Mix(0), Mix(1), Mix(2), alpha * 255];
    }

    /// <summary>
    /// Whether <paramref name="level"/> is <paramref name="exact"/> rounded as <see cref="RGradient.ColorAt"/> documents:
    /// to the nearest level, and where the exact value is half way between two levels either of them (the engine reads
    /// the position and the stops in single precision, which tips a half either way).
    /// </summary>
    private static bool Rounded(byte level, double exact) =>
        Math.Abs(exact - Math.Floor(exact) - 0.5) < 1e-6
            ? level == Math.Floor(exact) || level == Math.Ceiling(exact)
            : level == Math.Round(exact);

    /// <summary>The shapes a one-off snapshot resolves, in drawing order.</summary>
    private static List<RShape> Shapes(string nodes, FakeSceneInputs inputs, int width, int height)
    {
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(SceneKit.Json(Scene(nodes)), width, height), SceneKit.Now, SceneKit.Options(inputs));
        // The snapshot's evaluated items aren't exposed; the test reads them rather than widen the runtime's surface.
        var field = typeof(TickSnapshot).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var shapes = new List<RShape>();
        void Nodes(RNode[] nodes)
        {
            foreach (var n in nodes)
                if (n is RGroup g) Nodes(g.Children);
                else if (n is RShape s) shapes.Add(s);
        }
        void Items(SnapItem[] items)
        {
            foreach (var item in items)
                if (item is SnapGroup g) Items(g.Children);
                else if (item is SnapScene { Nodes: { } n }) Nodes(n);
        }
        Items(Assert.IsType<SnapItem[]>(field?.GetValue(snapshot)));
        return shapes;
    }

    /// <summary>81 positions, c ÷ 80: the eighths, and between them 1/16, 3/16, 0.3, 0.55, 15/16 and the rest.</summary>
    private const int Positions = 81;

    private static double[] Samples81 => [.. Enumerable.Range(0, Positions).Select(c => 1.25 * c)];

    /// <summary>
    /// The ramp's colour, as each cell is filled with it, is the format's premultiplied rule computed exactly (see
    /// <see cref="Rule"/>, a computation of its own) at 81 positions on and between the eighths: each channel and the
    /// alpha the rule's value rounded to the nearest level (see <see cref="Rounded"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(RuleStopSets))]
    public void A_ramps_colour_is_the_premultiplied_rule_computed_exactly_on_and_between_the_eighths(int set)
    {
        var stops = RuleStops[set];
        var series = "{'kind':'series','input':'v','w':81,'h':4,'min':0,'max':100,'mode':'cells','pitch':1,'count':81,'ramp':" + StopsJson(stops) + "}";
        var samples = Samples81;
        var shapes = Shapes(series, History(samples), 81, 4);
        Assert.Equal(Positions, shapes.Count);
        for (var c = 0; c < Positions; c++)
        {
            var fill = SceneColor.ToSk(Assert.IsType<RSolid>(shapes[c].Fill).Color);
            var rule = Rule(stops, (samples[c] - 0) / (100 - 0));
            var ok = Rounded(fill.Red, rule[0]) && Rounded(fill.Green, rule[1]) && Rounded(fill.Blue, rule[2]) && Rounded(fill.Alpha, rule[3]);
            Assert.True(ok, $"position {c}/80: the ramp's (r, g, b, a) = ({fill.Red}, {fill.Green}, {fill.Blue}, {fill.Alpha}), the rule's ({string.Join(", ", rule)})");
        }
    }

    /// <summary>
    /// Where alpha changes, this engine draws a gradient as eight premultiplied subdivisions between two stops with
    /// straight interpolation between them, an approximation of the rule; the ramp's colour is the rule itself. Drawn
    /// over the background at the same 81 positions, the two differ by up to <paramref name="gap"/> levels of a channel,
    /// one above the largest difference measured for the stop set: 6 at 3/80 for 1/16 opaque red to opaque blue, 9 at
    /// 4/80 for transparent red to opaque blue (both between the first two eighths), 2 for the three translucent stops
    /// and the translucent hard edge. The bound documents the gap the subdivisions leave, not a likeness.
    /// </summary>
    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 10)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public void Where_alpha_changes_a_drawn_gradient_approximates_the_ramps_exact_colour_within_a_few_levels(int set, int gap)
    {
        var stops = StopsJson(RuleStops[set]);
        var nodes = "{'kind':'series','input':'v','w':81,'h':4,'min':0,'max':100,'mode':'cells','pitch':1,'count':81,'ramp':" + stops + "},"
            + "{'kind':'rect','y':6,'w':81,'h':4,'fill':{'type':'linear','x1':0.5,'y1':0,'x2':80.5,'y2':0,'stops':" + stops + "}}";
        using var bitmap = Draw(nodes, History(Samples81), 81, 10);
        var (most, at) = (0, 0);
        for (var c = 0; c < Positions; c++)
        {
            var (ramp, gradient) = (bitmap.GetPixel(c, 2), bitmap.GetPixel(c, 8));
            var d = Math.Max(Math.Abs(ramp.Red - gradient.Red), Math.Max(Math.Abs(ramp.Green - gradient.Green), Math.Abs(ramp.Blue - gradient.Blue)));
            if (d > most) (most, at) = (d, c);
        }
        Assert.True(most <= gap, $"{most} levels apart at position {at}/80");
    }

    [Fact]
    public void Transparent_to_opaque_is_interpolated_premultiplied()
    {
        // Halfway from transparent red to opaque blue: blue at half alpha over the background, no red.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'count':1,'ramp':[{'at':0,'color':'#ff000000'},{'at':1,'color':'#0000ff'}]}", History(50));
        var bg = SKColor.Parse(Background);
        var expected = new SKColor((byte)Math.Round(bg.Red * (1 - 128 / 255.0)), (byte)Math.Round(bg.Green * (1 - 128 / 255.0)), (byte)Math.Round(bg.Blue * (1 - 128 / 255.0) + 255 * 128 / 255.0));
        var p = bitmap.GetPixel(35, 10);
        Assert.True(Math.Abs(p.Red - expected.Red) <= 1 && Math.Abs(p.Green - expected.Green) <= 1 && Math.Abs(p.Blue - expected.Blue) <= 1, $"{p}, not {expected}");
    }

    [Fact]
    public void Hard_stops_give_hard_edges_the_position_itself_taking_the_colour_after()
    {
        // The status strip: green below 70, amber from 70, red from 85, with no blend between them.
        const string strip = "[{'at':0,'color':'#22c55e'},{'at':0.7,'color':'#22c55e'},{'at':0.7,'color':'#f59e0b'},{'at':0.85,'color':'#f59e0b'},{'at':0.85,'color':'#ef4444'},{'at':1,'color':'#ef4444'}]";
        double[] samples = [0, 69.9, 70, 84.9, 85, 100];
        using var bitmap = Draw("{'kind':'series','input':'v','w':60,'h':10,'min':0,'max':100,'mode':'cells','pitch':10,'count':6,'ramp':" + strip + "}", History(samples), 60, 10);
        Assert.Equal(["#22c55e", "#22c55e", "#f59e0b", "#f59e0b", "#ef4444", "#ef4444"], Enumerable.Range(0, 6).Select(i => Hex(bitmap.GetPixel(10 * i + 5, 5))));
    }

    [Fact]
    public void The_stops_are_read_each_tick()
    {
        // A hard edge at the input k: the sample 50 is past it at k = 0.3 and before it at k = 0.7.
        var k = "{'op':'input','name':'k'}";
        var series = "{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'count':1,'ramp':"
            + "[{'at':0,'color':'#22c55e'},{'at':" + k + ",'color':'#22c55e'},{'at':" + k + ",'color':'#ef4444'},{'at':1,'color':'#ef4444'}]}";
        var inputs = History(50);
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(SceneKit.Json(Scene(series)), 40, 20));
        string At(double edge, double elapsed)
        {
            inputs.Number("test:k", edge);
            using var tick = runtime.Tick(elapsed, SceneKit.Now);
            using var bitmap = tick.Render(1);
            return Hex(bitmap.GetPixel(35, 10));
        }
        Assert.Equal("#ef4444", At(0.3, 0));
        Assert.Equal("#22c55e", At(0.7, 0.1));
    }

    // ---- cells ---------------------------------------------------------------------------------------------------

    private const string Red = "#ff0000";

    [Fact]
    public void A_cell_is_the_boxs_height_whatever_its_value()
    {
        // A sample of 10 of 0 … 100: its cell fills the 20-pixel box from top to bottom, and nothing below it.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'count':1,'fill':'" + Red + "'}", History(10), 40, 24);
        Assert.Equal(Red, Hex(bitmap.GetPixel(35, 0)));
        Assert.Equal(Red, Hex(bitmap.GetPixel(35, 19)));
        Assert.Equal(Background, Hex(bitmap.GetPixel(35, 21)));
    }

    [Fact]
    public void A_cell_is_barWidth_wide_its_right_edge_on_the_samples_x()
    {
        // Samples at x = 30 and 40, cells 3 wide: 27–30 and 37–40.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'barWidth':3,'count':2,'fill':'" + Red + "'}", History(50, 50));
        var row = Enumerable.Range(0, 40).Select(x => Hex(bitmap.GetPixel(x, 10)) == Red).ToArray();
        Assert.Equal([.. Enumerable.Range(0, 40).Where(x => x is >= 27 and < 30 or >= 37)], Enumerable.Range(0, 40).Where(x => row[x]));
    }

    [Fact]
    public void Without_barWidth_a_cell_is_pitch_wide()
    {
        // Samples at x = 30 and 40, cells 10 wide: one band from 20 to 40.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'count':2,'fill':'" + Red + "'}", History(50, 50));
        Assert.Equal([.. Enumerable.Range(20, 20)], Enumerable.Range(0, 40).Where(x => Hex(bitmap.GetPixel(x, 10)) == Red));
    }

    [Fact]
    public void A_cell_whose_left_edge_is_outside_the_box_is_clipped_to_it()
    {
        // The scene at x = 10: the oldest of four samples is at the box's x 10, its cell 15 wide from −5, drawn from 0.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'barWidth':15,'count':4,'fill':'" + Red + "'}",
            History(50, 50, 50, 50), 60, 20, x: 10);
        Assert.Equal(Background, Hex(bitmap.GetPixel(9, 10)));
        Assert.Equal(Red, Hex(bitmap.GetPixel(10, 10)));
        Assert.Equal(Red, Hex(bitmap.GetPixel(49, 10)));
        Assert.Equal(Background, Hex(bitmap.GetPixel(50, 10)));
    }

    [Fact]
    public void Cells_wider_than_pitch_overlap_the_newer_on_top()
    {
        // Two cells 15 wide, 10 apart: the older (at 0, #102030) from 15 to 30, the newer (at 1, #d0e0f0) from 25 to 40.
        using var bitmap = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'barWidth':15,'count':2,'ramp':" + Ramp + "}", History(0, 100));
        Assert.Equal("#102030", Hex(bitmap.GetPixel(20, 10)));
        Assert.Equal("#d0e0f0", Hex(bitmap.GetPixel(27, 10)));
        Assert.Equal("#d0e0f0", Hex(bitmap.GetPixel(35, 10)));
    }

    [Fact]
    public void At_a_fractional_pitch_each_cell_is_its_own_rectangle_meeting_the_next_inside_a_pixel()
    {
        // pitch 0.5, the default width: the samples at x = 9, 9.5 and 10 make cells 8.5–9, 9–9.5 and 9.5–10, the box's
        // height, each half a pixel wide, so neighbours meet inside a pixel (where their antialiased edges meet).
        var shapes = Shapes("{'kind':'series','input':'v','w':10,'h':4,'min':0,'max':100,'mode':'cells','pitch':0.5,'count':3,'fill':'" + Red + "'}",
            History(10, 50, 90), 10, 4);
        Assert.Equal([new SKRect(8.5f, 0, 9, 4), new SKRect(9, 0, 9.5f, 4), new SKRect(9.5f, 0, 10, 4)],
            shapes.Select(s => Assert.IsType<RectGeom>(s.Geom).Rect));
    }

    [Fact]
    public void Without_a_ramp_cells_pitch_wide_at_whole_pixels_are_one_band_of_fill_where_there_are_samples_whatever_their_values()
    {
        // Three samples a pixel apart: the band covers their three cells, whatever the values, and nothing older.
        var series = "{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':1,'fill':'" + Red + "'}";
        using var low = Draw(series, History(-5, 10, 0));
        using var high = Draw(series, History(250, 90, 100));
        Assert.Equal(0, SceneKit.DifferingPixels(low, high, tolerance: 0));
        Assert.Equal([37, 38, 39], Enumerable.Range(0, 40).Where(x => Hex(low.GetPixel(x, 10)) == Red));
    }

    [Fact]
    public void A_negative_barWidth_draws_no_cell()
    {
        using var none = Draw("{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'cells','pitch':10,'barWidth':-1,'fill':'" + Red + "'}", History(50, 50));
        using var empty = Draw("{'kind':'rect','w':0,'h':0}", History());
        Assert.Equal(0, SceneKit.DifferingPixels(none, empty, tolerance: 0));
    }

    // ---- with line and area, and with fill and stroke --------------------------------------------------------------

    [Theory]
    [InlineData("line")]
    [InlineData("area")]
    public void Line_and_area_draw_as_without_the_ramp(string mode)
    {
        var plain = "{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'" + mode + "','pitch':4,'fill':'#f59e0b','stroke':{'paint':'#ffffff','width':1}";
        double[] wave = [.. Enumerable.Range(0, 11).Select(i => 50 + 40 * Math.Sin(i / 2.0))];
        using var without = Draw(plain + "}", History(wave));
        using var with = Draw(plain + ",'ramp':" + Ramp + "}", History(wave));
        Assert.Equal(0, SceneKit.DifferingPixels(without, with, tolerance: 0));
        using var nothing = Draw(plain + "}", History());
        Assert.NotEqual(0, SceneKit.DifferingPixels(without, nothing, tolerance: 0));
    }

    [Theory]
    [InlineData("bars")]
    [InlineData("cells")]
    public void The_ramp_fills_bars_and_cells_in_place_of_fill_and_the_stroke_is_unchanged(string mode)
    {
        // A ramp of one colour draws what that colour as the fill draws: the stroke on every bar or cell as before.
        var series = "{'kind':'series','input':'v','w':40,'h':20,'min':0,'max':100,'mode':'" + mode + "','pitch':5,'barWidth':4,'stroke':{'paint':'#ffffff','width':1},";
        double[] wave = [.. Enumerable.Range(0, 9).Select(i => 50 + 40 * Math.Sin(i / 2.0))];
        using var filled = Draw(series + "'fill':'#22c55e'}", History(wave));
        using var ramped = Draw(series + "'fill':'#ff0000','ramp':[{'at':0,'color':'#22c55e'},{'at':1,'color':'#22c55e'}]}", History(wave));
        Assert.Equal(0, SceneKit.DifferingPixels(filled, ramped, tolerance: 0));
        // Inside the newest bar or cell (36 to 40, its stroke a pixel wide on its outline): the ramp's colour.
        Assert.Equal("#22c55e", Hex(ramped.GetPixel(38, mode == "bars" ? 18 : 10)));
    }

    [Fact]
    public void A_ramp_colours_a_tables_column_as_it_colours_a_history_of_the_same_numbers()
    {
        double[] ms = [.. Enumerable.Range(0, 60).Select(k => Math.Round(10 + 8 * Math.Sin(k / 5.0), 2))];
        var table = new DataTable();
        table.Columns.Add("frame", typeof(object));
        table.Columns.Add("ms", typeof(object));
        for (var r = 0; r < ms.Length; r++) table.Rows.Add(new PluginText("frame", "#" + r), new PluginSensor("ms", (float)ms[r], "ms"));
        const string ramp = ",'w':60,'h':10,'min':0,'max':20,'mode':'cells','pitch':1,'count':60,'ramp':[{'at':0,'color':'#38bdf8'},{'at':0.5,'color':'#22c55e'},{'at':1,'color':'#ef4444'}]}";
        var overColumn = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[{'kind':'series','input':'t','column':'ms'" + ramp + "]}");
        var overHistory = SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'number'}},'nodes':[{'kind':'series','input':'t'" + ramp + "]}");
        var column = new FakeSceneInputs();
        SceneColumnSamplesTests.Table(table)(column);
        var history = new FakeSceneInputs();
        history.Number("test:t", ms[^1]);
        // The plugin's cell holds a float, as the history would hold the sensor's value.
        history.Histories["test:t"] = [.. ms.Select(v => (double)(float)v)];
        using var a = SceneKit.Render(SceneKit.Doc(overColumn, 60, 10), SceneKit.Options(column));
        using var b = SceneKit.Render(SceneKit.Doc(overHistory, 60, 10), SceneKit.Options(history));
        Assert.Equal(0, SceneKit.DifferingPixels(a, b, tolerance: 0));
        // And the colours vary with the samples.
        Assert.True(Enumerable.Range(0, 60).Select(x => a.GetPixel(x, 5)).Distinct().Count() > 10);
    }

    [Fact]
    public void A_ramped_series_draws_its_bars_or_cells_with_one_fill_paint_however_many_colours()
    {
        // 400 cells, each its own colour (the samples 0 … 99 in turn on a smooth ramp), then 400 in three runs: each draw
        // makes one fill paint, not one a cell or one a run.
        const string strip = "[{'at':0,'color':'#22c55e'},{'at':0.7,'color':'#22c55e'},{'at':0.7,'color':'#f59e0b'},{'at':0.85,'color':'#f59e0b'},{'at':0.85,'color':'#ef4444'},{'at':1,'color':'#ef4444'}]";
        const string smooth = "[{'at':0,'color':'#000000'},{'at':0.5,'color':'#22c55e'},{'at':1,'color':'#ffffff'}]";
        foreach (var (ramp, samples) in new[]
        {
            (smooth, Enumerable.Range(0, 400).Select(i => (double)(i * 37 % 100)).ToArray()),
            (strip, Enumerable.Range(0, 400).Select(i => i < 150 ? 50.0 : i < 300 ? 80 : 95).ToArray()),
        })
        {
            var scene = SceneKit.Json(Scene("{'kind':'series','input':'v','mode':'cells','x':10,'y':10,'w':400,'h':80,'pitch':1,'count':400,'min':0,'max':100,'ramp':" + ramp + "}"));
            using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, 420, 100), SceneKit.Now, SceneKit.Options(History(samples)));
            using var bitmap = new SKBitmap(new SKImageInfo(420, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            SceneDrawer.PaintsMade = 0;
            snapshot.Draw(canvas, 1);
            Assert.True(SceneDrawer.PaintsMade == 1, $"{SceneDrawer.PaintsMade} paints in one draw");
            if (ramp == strip) Assert.Equal(["#22c55e", "#f59e0b", "#ef4444"], new[] { 100, 250, 390 }.Select(x => Hex(bitmap.GetPixel(10 + x, 50))));
            else Assert.True(Enumerable.Range(10, 400).Select(x => bitmap.GetPixel(x, 50)).Distinct().Count() >= 90, "the cells' colours vary");
        }
    }

    /// <summary>
    /// The one fill paint a list of nodes reuses draws what a paint of its own for each shape draws, to the pixel: pixel
    /// aligned solid rectangles, opaque and part transparent, after a solid fill of another colour, after a gradient
    /// fill, after fills at other opacities, after a stroked shape and after a shape drawn in a layer of its own, and the
    /// same inside a clipped group drawn in a layer. The reference is the same nodes drawn in the same process with
    /// nothing to reuse: each top-level node in a draw call of its own, each of the group's children in a group of its
    /// own (no layer, no clip: only its own paints).
    /// </summary>
    [Fact]
    public void Solid_fills_drawn_with_the_reused_paint_draw_as_with_a_paint_each()
    {
        static RShape Rect(float x, float w, uint colour, float opacity = 1, RPaint? paint = null, RStroke? stroke = null) =>
            new() { Geom = new RectGeom(x, 1, w, 6, null), Fill = paint ?? new RSolid(colour), Stroke = stroke, Opacity = opacity };
        // "#rrggbb" or "#rrggbbaa", as the format writes a colour.
        static uint C(string hex) => SceneColor.Make(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16),
            hex.Length == 9 ? Convert.ToInt32(hex[7..9], 16) : 255);
        var gradient = new RGradient(GradientKind.Linear, [0, 0, 4, 0], [C("#ff0000"), C("#0000ff80")], [0, 1], SKShaderTileMode.Clamp);
        var stroke = new RStroke(new RSolid(C("#ffffff")), 1, SKStrokeCap.Butt, SKStrokeJoin.Miter, 4, null, 0, 0, 1);
        RNode[] Sequence(float x) =>
        [
            Rect(x + 0, 2, C("#22c55e")),
            Rect(x + 2, 2, C("#ef4444")),                    // a solid after a solid of another colour
            Rect(x + 3, 2, C("#3b82f680")),                  // part transparent, over the one before
            Rect(x + 5, 2, 0, paint: gradient),
            Rect(x + 7, 2, C("#f59e0b")),                    // after a gradient
            Rect(x + 9, 2, C("#a855f7"), opacity: 0.5f),
            Rect(x + 11, 2, C("#14b8a6")),                   // after a fill at another opacity
            Rect(x + 13, 2, C("#e11d48c0"), opacity: 0.5f),
            Rect(x + 14, 2, C("#84cc16"), opacity: 0.5f),    // the same opacity, another colour
            Rect(x + 16, 3, C("#0ea5e9"), stroke: stroke),
            Rect(x + 19, 2, C("#facc15")),                   // after a stroked shape
            Rect(x + 21, 3, C("#6366f1"), opacity: 0.6f, stroke: stroke),
            Rect(x + 24, 2, C("#f97316"), opacity: 0.6f),    // after a shape drawn in a layer of its own
            Rect(x + 26, 2, C("#10b98140")),
        ];
        RGroup Group(RNode[] children) => new()
        {
            Opacity = 0.8f, Clip = new RectGeom(32, 0, 27, 8, null), Children = children,
        };
        var top = Sequence(0);
        var inGroup = Sequence(32);
        SKBitmap Picture(Action<SKCanvas> draw)
        {
            var bitmap = new SKBitmap(new SKImageInfo(64, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColor.Parse(Background));
            draw(canvas);
            return bitmap;
        }
        using var reused = Picture(canvas => SceneDrawer.Draw(canvas, [.. top, Group(inGroup)]));
        using var own = Picture(canvas =>
        {
            foreach (var node in top) SceneDrawer.Draw(canvas, [node]);
            SceneDrawer.Draw(canvas, [Group([.. inGroup.Select(n => (RNode)new RGroup { Children = [n] })])]);
        });
        Assert.Equal(0, SceneKit.DifferingPixels(reused, own, tolerance: 0));
        // And the picture is what the colours draw: an opaque solid after a solid, after a gradient, after a stroke.
        Assert.Equal(["#ef4444", "#f59e0b", "#facc15"], new[] { 2, 7, 19 }.Select(x => Hex(reused.GetPixel(x, 4))));
    }

    // ---- the validator: each row the editor's validator's too (its parity rows) ----------------------------------------

    private static SceneCheck Check(string node, string inputType = "number") =>
        SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'v':{'type':'" + inputType + "'}},'values':{'s':'text','n':5},'state':{'c':{'init':0,'next':1}},'nodes':[" + node + "]}"));

    private static string Series(string members) => "{'kind':'series','input':'v','w':100,'h':20,'min':0,'max':1" + members + "}";

    private static string Stops(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "{'at':" + (i / 16.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'color':'#ffffff'}")) + "]";

    private const string Two = "{'at':1,'color':'#000000'}";

    public static TheoryData<string, string, string> Refused() => new()
    {
        { Series(",'mode':'cells','ramp':[{'at':0,'color':'#ffffff'}]"), "schema", "/nodes/0/ramp" },
        { Series(",'mode':'cells','ramp':" + Stops(17)), "schema", "/nodes/0/ramp" },
        { Series(",'mode':'cells','ramp':[]"), "schema", "/nodes/0/ramp" },
        { Series(",'mode':'bars','ramp':'#ffffff'"), "schema", "/nodes/0/ramp" },
        { Series(",'mode':'bars','ramp':{'at':0,'color':'#ffffff'}"), "schema", "/nodes/0/ramp" },
        { Series(",'mode':'bars','ramp':[{'at':0}," + Two + "]"), "schema", "/nodes/0/ramp/0" },
        { Series(",'mode':'bars','ramp':[{'at':0,'color':'#ffffff','x':1}," + Two + "]"), "schema", "/nodes/0/ramp/0/x" },
        { Series(",'mode':'bars','ramp':['#ffffff'," + Two + "]"), "schema", "/nodes/0/ramp/0" },
        { Series(",'mode':'bars','ramp':[{'at':'#ffffff','color':'#ffffff'}," + Two + "]"), "schema", "/nodes/0/ramp/0/at" },
        { Series(",'mode':'bars','ramp':[{'at':0,'color':5}," + Two + "]"), "schema", "/nodes/0/ramp/0/color" },
        { Series(",'mode':'bars','ramp':[{'at':{'op':'value','name':'s'},'color':'#ffffff'}," + Two + "]"), "type.mismatch", "/nodes/0/ramp/0/at" },
        { Series(",'mode':'bars','ramp':[{'at':0,'color':'#ffffff'},{'at':1,'color':{'op':'value','name':'n'}}]"), "type.mismatch", "/nodes/0/ramp/1/color" },
        { Series(",'mode':'bars','ramp':[{'at':{'op':'input','name':'nope'},'color':'#ffffff'}," + Two + "]"), "ref.input", "/nodes/0/ramp/0/at" },
        { Series(",'mode':'bars','ramp':[{'at':{'op':'measure','node':'nope','field':'w'},'color':'#ffffff'}," + Two + "]"), "ref.node", "/nodes/0/ramp/0/at/node" },
        { Series(",'mode':'bars','ramp':[{'at':{'op':'dt'},'color':'#ffffff'}," + Two + "]"), "context.dt", "/nodes/0/ramp/0/at" },
        // An unknown mode is still refused at mode.
        { Series(",'mode':'dots'"), "schema", "/nodes/0/mode" },
        { Series(",'mode':'dots','ramp':[{'at':0,'color':'#ffffff'}," + Two + "]"), "schema", "/nodes/0/mode" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_validator_refuses_at_its_code_and_path(string node, string code, string path)
    {
        var check = Check(node);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal([(code, path)], check.Errors.Select(e => (e.Code, e.Path)));
    }

    public static TheoryData<string> Accepted() => new()
    {
        Series(",'mode':'cells'"),
        Series(",'mode':'cells','barWidth':3,'pitch':4,'fill':'#ffffff'"),
        Series(",'mode':'cells','ramp':[{'at':0,'color':'#22c55e'},{'at':0.7,'color':'#22c55e'},{'at':0.7,'color':'#ef4444'},{'at':1,'color':'#ef4444'}]"),
        Series(",'mode':'bars','ramp':" + Stops(2)),
        Series(",'mode':'bars','ramp':" + Stops(16)),
        Series(",'mode':'line','ramp':" + Stops(2)),
        Series(",'mode':'area','fill':'#ffffff','ramp':" + Stops(2)),
        Series(",'ramp':" + Stops(2)),
        Series(",'mode':'cells','ramp':[{'at':{'op':'value','name':'n'},'color':{'op':'lerp','args':['#000000','#ffffff',{'op':'state','name':'c'}]}},{'at':1.5,'color':'#000000'}]"),
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void The_validator_accepts(string node)
    {
        var check = Check(node);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    [Fact]
    public void A_cells_series_over_a_tables_column_is_accepted()
    {
        var check = Check("{'kind':'series','input':'v','column':'ms','w':100,'h':20,'min':0,'max':1,'mode':'cells','ramp':" + Stops(2) + "}", "table");
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    /// <summary>
    /// A ramp's stops are walked for what they read as a gradient's are: a stop measuring a text node whose layout reads a
    /// measurement makes that a cycle (refused at the layout's member), and without the ramp's measurement it is none.
    /// </summary>
    [Fact]
    public void A_ramps_stop_that_measures_a_text_node_is_walked_for_the_measurement_cycle()
    {
        const string texts = "{'kind':'text','id':'t','w':{'op':'measure','node':'u','field':'w'},'font':{'family':'Inter','size':10},'text':'a'},"
            + "{'kind':'text','id':'u','y':20,'font':{'family':'Inter','size':10},'text':'b'},";
        string Scene(string at) => "{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'nodes':[" + texts
            + "{'kind':'series','input':'v','w':100,'h':20,'min':0,'max':1,'mode':'cells','ramp':[{'at':" + at + ",'color':'#ffffff'}," + Two + "]}]}";
        var cycle = SceneValidator.Check(SceneKit.Json(Scene("{'op':'div','args':[{'op':'measure','node':'t','field':'w'},100]}")));
        Assert.Equal([("cycle.measure", "/nodes/0/w")], cycle.Errors.Select(e => (e.Code, e.Path)));
        var none = SceneValidator.Check(SceneKit.Json(Scene("0")));
        Assert.True(none.Status == CheckStatus.Valid, string.Join("; ", none.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    /// <summary>A tree of 34,953 operations in a ramp's stop counts towards the scene's work: refused at the work limit.</summary>
    [Fact]
    public void A_ramps_stops_count_towards_the_work_limit()
    {
        var t1 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat("{'op':'neg','args':[1]}", 16)) + "]}";
        var t2 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t1, 16)) + "]}";
        var t3 = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t2, 16)) + "]}";
        var tree = "{'op':'add','args':[" + string.Join(",", Enumerable.Repeat(t3, 8)) + "]}";
        var check = Check(Series(",'mode':'cells','ramp':[{'at':" + tree + ",'color':'#ffffff'}," + Two + "]"));
        Assert.Equal([("limit.exprWork", "")], check.Errors.Select(e => (e.Code, e.Path)));
        // The same tree in a gradient's stop, for comparison: refused the same way.
        var gradient = Check("{'kind':'rect','w':10,'h':10,'fill':{'type':'linear','x1':0,'y1':0,'x2':10,'y2':0,'stops':[{'at':" + tree + ",'color':'#ffffff'}," + Two + "]}}");
        Assert.Equal([("limit.exprWork", "")], gradient.Errors.Select(e => (e.Code, e.Path)));
    }

    // ---- the cost ------------------------------------------------------------------------------------------------

    private const string Fixed = "[{'at':0,'color':'#000000'},{'at':1,'color':'#ffffff'}]";

    /// <summary>A series, its input <c>t</c>'s type, and the work: the validator's and the runtime's (the editor's rows too).</summary>
    public static TheoryData<string, string, int> Costs() => new()
    {
        // A ramp costs its stops' operations, once a tick, as a gradient's do; each sample's colour nothing.
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'cells','count':600}", 0 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'cells','count':600,'ramp':" + Fixed + "}", 0 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'bars','count':600,'ramp':" + Fixed + "}", 0 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'bars','count':600,'ramp':[{'at':{'op':'add','args':[0.25,0.5]},'color':'#000000'},{'at':1,'color':'#ffffff'}]}", 1 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'cells','ramp':[{'at':0,'color':{'op':'lerp','args':['#000000','#ffffff',{'op':'mul','args':[0.5,0.5]}]}},{'at':1,'color':'#ffffff'}]}", 2 },
        // Evaluated with line and area too, though not drawn, as fill is with line.
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'line','ramp':[{'at':{'op':'add','args':[0.25,0.5]},'color':'#000000'},{'at':1,'color':'#ffffff'}]}", 1 },
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'area','ramp':[{'at':{'op':'add','args':[0.25,0.5]},'color':'#000000'},{'at':1,'color':'#ffffff'}]}", 1 },
        // A stop reading a history: the history's own cost.
        { "number", "{'kind':'series','input':'t','w':300,'h':80,'min':0,'max':100,'mode':'cells','ramp':[{'at':0,'color':'#000000'},{'at':{'op':'div','args':[{'op':'history','input':'t','fn':'max','count':600},100]},'color':'#ffffff'}]}", 2 + 10 },
        // Cells over a column cost what bars do: 1 per 64 of the samples they may draw.
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'mode':'cells','count':64}", 1 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'mode':'cells','count':65}", 2 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'mode':'cells','count':600,'ramp':" + Fixed + "}", 10 },
        { "table", "{'kind':'series','input':'t','column':'ms','w':300,'h':80,'min':0,'max':33.4,'mode':'cells','ramp':[{'at':{'op':'add','args':[0.25,0.5]},'color':'#000000'},{'at':1,'color':'#ffffff'}]}", 64 + 1 },
    };

    [Theory]
    [MemberData(nameof(Costs))]
    public void A_ramp_and_cells_cost_the_same_in_the_validator_and_the_runtime(string inputType, string node, int work)
    {
        var check = SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','inputs':{'t':{'type':'" + inputType + "'}},'nodes':[" + node + "]}"));
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        Assert.Equal(work, check.Cost.Work);
        // The runtime counts the same: within a budget of exactly that work it evaluates, within one less it doesn't.
        Assert.True(Evaluates(check, inputType, work), "within its own work");
        if (work > 0) Assert.False(Evaluates(check, inputType, work - 1), "within one less");
    }

    private static bool Evaluates(SceneCheck check, string inputType, int workLimit)
    {
        var compiled = CompiledScene.Compile(check.Scene!, check.Cost, SpecLimits.Default);
        var rt = new ItemRuntime("x");
        rt.Use(compiled);
        var fake = new FakeSceneInputs();
        SceneColumnSamplesTests.Table(SceneColumnSamplesTests.FrameTimes())(fake);
        fake.Number("test:n", 7);
        fake.Histories["test:n"] = [.. Enumerable.Range(0, 600).Select(i => (double)(i % 100))];
        var reading = inputType == "table" ? fake.Read("test:t", "table") : fake.Read("test:n", "number");
        InputSnap[] inputs = [new InputSnap(reading, "now", inputType == "table" ? [] : fake.Histories["test:n"])];
        var evaluator = new SceneEvaluator(compiled, rt, new TickServices(), inputs, SceneClock.At(0, SceneKit.Now), 400, 100, new ItemDiagnostics("x"), workLimit);
        try
        {
            evaluator.Run(advance: false);
            return true;
        }
        catch (BudgetExceeded e) when (e.Code == "budget.work")
        {
            return false;
        }
    }
}
