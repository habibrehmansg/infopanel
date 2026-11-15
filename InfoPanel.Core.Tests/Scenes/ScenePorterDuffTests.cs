using System.Globalization;
using System.Text.Json;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The Porter-Duff operators in <c>blend</c> (spec/docs/scene.md, Groups): each one's result by the W3C compositing
/// formulas on a group, at a scene's top level and in a compose paint; the isolation (an operator never reaches another
/// item, a sibling outside its parent or the panel's background); where the layer is and how the layer budget counts
/// it; the other blends as before; and what the validator refuses, each row the editor's validator's too.
/// </summary>
/// <remarks>
/// Every expected colour is computed here from the formulas over straight colours, premultiplied, and compared with the
/// frame's premultiplied bytes: a channel may be either level next to the exact value (8-bit storage), never further.
/// The rectangles are pixel-aligned, so each region is one colour.
/// </remarks>
public sealed class ScenePorterDuffTests
{
    public static readonly string[] Operators =
        ["source-in", "source-out", "source-atop", "destination-over", "destination-in", "destination-out", "destination-atop", "xor"];

    /// <summary>The operators that can remove what the destination drew.</summary>
    public static readonly string[] Erasing = ["source-in", "source-out", "destination-in", "destination-out", "destination-atop", "xor"];

    /// <summary>Destination and source colours: opaque; both part-transparent; opaque under part-transparent.</summary>
    private static readonly (string Dst, string Src)[] ColourSets =
    [
        ("#2a6fdb", "#e0552b"),
        ("#2a6fdb99", "#e0552b66"),
        ("#2a6fdb", "#e0552b80"),
    ];

    private const string Transparent = "#00000000";
    private const string Blue = "#2050c0";
    private const string Green = "#22c55e";
    private const string Black = "#101010";

    // ---- the formulas ---------------------------------------------------------------------------------------------

    /// <summary>W3C Compositing and Blending, "Porter Duff Compositing Operators": Fa and Fb.</summary>
    private static (double Fa, double Fb) Factors(string op, double αs, double αb) => op switch
    {
        "source-in" => (αb, 0),
        "source-out" => (1 - αb, 0),
        "source-atop" => (αb, 1 - αs),
        "destination-over" => (1 - αb, 1),
        "destination-in" => (0, αs),
        "destination-out" => (0, 1 - αs),
        "destination-atop" => (1 - αb, αs),
        "xor" => (1 - αb, 1 - αs),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>A straight colour <c>#RRGGBB[AA]</c> as r, g, b, a in 0–1.</summary>
    private static double[] Straight(string hex)
    {
        double C(int i) => int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber) / 255.0;
        return [C(0), C(1), C(2), hex.Length > 7 ? C(3) : 1];
    }

    /// <summary>
    /// The premultiplied result of <paramref name="op"/> with a straight source and destination (null where absent):
    /// co = αs·Fa·Cs + αb·Fb·Cb, αo = αs·Fa + αb·Fb.
    /// </summary>
    private static double[] Composite(string op, double[]? src, double[]? dst)
    {
        double αs = src?[3] ?? 0, αb = dst?[3] ?? 0;
        var (fa, fb) = Factors(op, αs, αb);
        var o = new double[4];
        for (var c = 0; c < 3; c++) o[c] = αs * fa * (src?[c] ?? 0) + αb * fb * (dst?[c] ?? 0);
        o[3] = αs * fa + αb * fb;
        return o;
    }

    /// <summary>A straight colour premultiplied.</summary>
    private static double[] Premul(double[] c) => [c[0] * c[3], c[1] * c[3], c[2] * c[3], c[3]];

    /// <summary>Premultiplied <paramref name="top"/> over premultiplied <paramref name="below"/> (source-over).</summary>
    private static double[] Over(double[] top, double[] below) =>
        [.. Enumerable.Range(0, 4).Select(c => top[c] + below[c] * (1 - top[3]))];

    private static double[] Over(double[] top, string below) => Over(top, Premul(Straight(below)));

    /// <summary>Premultiplied <paramref name="c"/> at <paramref name="opacity"/>.</summary>
    private static double[] Faded(double[] c, double opacity) => [.. c.Select(v => v * opacity)];

    // ---- drawing --------------------------------------------------------------------------------------------------

    private static string Item(string id, string nodes) =>
        "{'kind':'scene','id':'" + id + "','placement':{'x':0,'y':0},'scene':{'format':'infopanel.scene/1','nodes':[" + nodes + "]}}";

    private static JsonElement Document(string background, int width, int height, params string[] items) =>
        SceneKit.Json("{'format':'infopanel.document/1','id':'00000000-0000-4000-8000-000000000001','profile':{'name':'Scene','width':" + width +
            ",'height':" + height + ",'background':'" + background + "'},'items':[" + string.Join(",", items) + "]}");

    /// <summary>The frame at <paramref name="scale"/>, premultiplied RGBA.</summary>
    private static SKBitmap Render(JsonElement document, int width, int height, float scale = 1)
    {
        using var media = SceneKit.SyncMedia();
        using var snapshot = SceneRuntime.RenderOnce(document, SceneKit.Now, SceneKit.Options(null, media));
        Assert.All(snapshot.Bounds, b => Assert.Null(b.Problem));
        var bitmap = new SKBitmap(new SKImageInfo((int)(width * scale), (int)(height * scale), SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        snapshot.Draw(canvas, scale);
        return bitmap;
    }

    /// <summary>One item of <paramref name="nodes"/> at (0, 0) on a 40 × 4 canvas.</summary>
    private static SKBitmap Strip(string nodes, string background = Transparent) => Render(Document(background, 40, 4, Item("item", nodes)), 40, 4);

    private static byte[] Pixel(SKBitmap b, int x, int y)
    {
        var i = (y * b.Width + x) * 4;
        var span = b.GetPixelSpan();
        return [span[i], span[i + 1], span[i + 2], span[i + 3]];
    }

    /// <summary>The pixel at (<paramref name="x"/>, 2) has the premultiplied <paramref name="exact"/> colour, to a level.</summary>
    private static void Region(SKBitmap b, int x, double[] exact, string region)
    {
        var p = Pixel(b, x, 2);
        var want = string.Join(" ", exact.Select(v => (v * 255).ToString("F2", CultureInfo.InvariantCulture)));
        Assert.True(Enumerable.Range(0, 4).All(c => Math.Abs(p[c] - exact[c] * 255) <= 1),
            $"{region} (x {x}): premultiplied RGBA {string.Join(" ", p)}, the formula gives {want}");
    }

    private static string Rect(double x, double w, string fill) =>
        "{'kind':'rect','x':" + x.ToString(CultureInfo.InvariantCulture) + ",'w':" + w.ToString(CultureInfo.InvariantCulture) + ",'h':4,'fill':'" + fill + "'}";

    private static string Group(string blend, params string[] children) =>
        "{'kind':'group'" + (blend == "" ? "" : ",'blend':'" + blend + "'") + ",'children':[" + string.Join(",", children) + "]}";

    /// <summary>A destination drawn on 0–20 and a source on 10–30, as <paramref name="context"/> combines them.</summary>
    private static string Pair(string context, string op, string dst, string src) => context switch
    {
        "group" => Group("", Rect(0, 20, dst), Group(op, Rect(10, 20, src))),
        "scene" => Rect(0, 20, dst) + "," + Group(op, Rect(10, 20, src)),
        "compose" => "{'kind':'rect','w':40,'h':4,'fill':{'type':'compose','blend':'" + op + "'," +
            "'dst':{'type':'linear','x1':0,'y1':0,'x2':40,'y2':0,'stops':[{'at':0,'color':'" + dst + "'},{'at':0.5,'color':'" + dst + "'},{'at':0.5,'color':'" + Transparent + "'},{'at':1,'color':'" + Transparent + "'}]}," +
            "'src':{'type':'linear','x1':0,'y1':0,'x2':40,'y2':0,'stops':[{'at':0,'color':'" + Transparent + "'},{'at':0.25,'color':'" + Transparent + "'},{'at':0.25,'color':'" + src + "'},{'at':0.75,'color':'" + src + "'},{'at':0.75,'color':'" + Transparent + "'},{'at':1,'color':'" + Transparent + "'}]}}}",
        _ => throw new ArgumentOutOfRangeException(nameof(context)),
    };

    // ---- each operator's result -----------------------------------------------------------------------------------

    public static TheoryData<string, string, int> Results()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var context in new[] { "group", "scene", "compose" })
            foreach (var op in Operators)
                for (var set = 0; set < ColourSets.Length; set++)
                    data.Add(context, op, set);
        return data;
    }

    /// <summary>
    /// On a group (the group the source, its parent's earlier drawing the destination), at a scene's top level and in a
    /// compose paint (<c>src</c> and <c>dst</c>): the four regions, each its own assertion. The destination-only region
    /// is 5 pixels and more from the source, so an operator that clears it clears all of its parent's layer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Results))]
    public void Each_operator_gives_the_formulas_colour_and_alpha_in_each_region(string context, string op, int set)
    {
        var (dst, src) = ColourSets[set];
        using var bitmap = Strip(Pair(context, op, dst, src));
        var (d, s) = (Straight(dst), Straight(src));
        Region(bitmap, 5, Composite(op, null, d), "destination only");
        Region(bitmap, 15, Composite(op, s, d), "both");
        Region(bitmap, 25, Composite(op, s, null), "source only");
        Region(bitmap, 35, Composite(op, null, null), "neither");
    }

    // ---- the isolation ----------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Isolations()
    {
        var data = new TheoryData<string, string>();
        foreach (var op in Erasing)
            foreach (var below in new[] { "another item", "an item in the same document group", "a sibling outside its parent", "the background" })
                data.Add(op, below);
        return data;
    }

    /// <summary>
    /// An item's drawing is its scene's nodes, drawn between its placement's save and restore: another item before it
    /// (side by side, or in the same document group, which isn't a layer), a node of its own scene outside the operator's
    /// parent, and the panel's background are below the layer, and each comes through it as source-over leaves it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Isolations))]
    public void An_operator_never_reaches_what_is_outside_its_parents_layer(string op, string below)
    {
        var mine = Rect(0, 20, Green) + "," + Group(op, Rect(10, 20, Black));
        var full = Rect(0, 40, Blue);
        var document = below switch
        {
            "another item" => Document(Transparent, 40, 4, Item("a", full), Item("b", mine)),
            "an item in the same document group" => Document(Transparent, 40, 4,
                "{'kind':'group','id':'g','placement':{'x':0,'y':0},'children':[" + Item("a", full) + "," + Item("b", mine) + "]}"),
            "a sibling outside its parent" => Document(Transparent, 40, 4, Item("b", full + "," + Group("", mine))),
            "the background" => Document(Blue + "ff", 40, 4, Item("b", mine)),
            _ => throw new ArgumentOutOfRangeException(nameof(below)),
        };
        using var bitmap = Render(document, 40, 4);
        var (green, black) = (Straight(Green), Straight(Black));
        Region(bitmap, 5, Over(Composite(op, null, green), Blue), "destination only, over " + below);
        Region(bitmap, 15, Over(Composite(op, black, green), Blue), "both, over " + below);
        Region(bitmap, 25, Over(Composite(op, black, null), Blue), "source only, over " + below);
        Region(bitmap, 35, Premul(Straight(Blue)), below + " alone");
    }

    /// <summary>A destination-in group never erases another item drawn before it in the document.</summary>
    [Fact]
    public void A_destination_in_group_never_erases_another_item_drawn_before_it()
    {
        // Item a fills the canvas; item b draws text-like content and masks it to 10–30 with destination-in.
        using var bitmap = Render(Document(Transparent, 40, 4, Item("a", Rect(0, 40, Blue)),
            Item("b", Group("", Rect(0, 40, Green), Group("destination-in", Rect(10, 20, "#000000"))))), 40, 4);
        foreach (var x in new[] { 0, 5, 9, 30, 35, 39 }) Region(bitmap, x, Premul(Straight(Blue)), "item a, outside item b's mask");
        foreach (var x in new[] { 10, 20, 29 }) Region(bitmap, x, Premul(Straight(Green)), "item b, inside its mask");
    }

    // ---- where the layer is -----------------------------------------------------------------------------------------

    /// <summary>
    /// The operator acts on all of its parent's layer, however far from the group: the source is the group's children
    /// within its own clip, transparent elsewhere, so destination-in clears the parent's drawing outside that clip.
    /// </summary>
    [Fact]
    public void A_groups_operator_covers_its_parents_whole_layer_and_its_own_clip_bounds_the_source()
    {
        var clipped = "{'kind':'group','blend':'destination-in','clip':{'kind':'rect','x':10,'w':10,'h':4},'children':[" + Rect(0, 40, "#000000") + "]}";
        using var bitmap = Strip(Group("", Rect(0, 40, Green), clipped));
        Region(bitmap, 2, [0, 0, 0, 0], "before the clip");
        Region(bitmap, 15, Premul(Straight(Green)), "inside the clip");
        Region(bitmap, 25, [0, 0, 0, 0], "past the clip");
        Region(bitmap, 38, [0, 0, 0, 0], "18 pixels past the clip");
    }

    /// <summary>A group whose child has an operator and which has one itself against its own parent: each in its parent's layer.</summary>
    [Fact]
    public void Nested_operators_each_combine_in_their_parents_layer()
    {
        // g (destination-out) holds a black 0–30 and, inside it, h (xor) with a black 0–10: g's layer is black on 10–30.
        // p holds a green 0–40, then g erases it where g's layer is: green on 0–10 and 30–40.
        var h = Group("xor", Rect(0, 10, Black));
        var g = Group("destination-out", Rect(0, 30, Black), h);
        using var bitmap = Strip(Group("", Rect(0, 40, Green), g));
        var (green, black) = (Straight(Green), Straight(Black));
        double[] Inner(bool hs, bool bs) => Composite("xor", hs ? black : null, bs ? black : null);
        double[] Unpremul(double[] p) => p[3] == 0 ? [0, 0, 0, 0] : [p[0] / p[3], p[1] / p[3], p[2] / p[3], p[3]];
        Region(bitmap, 5, Composite("destination-out", Unpremul(Inner(true, true)), green), "h and its sibling");
        Region(bitmap, 15, Composite("destination-out", Unpremul(Inner(false, true)), green), "g's own child");
        Region(bitmap, 35, Composite("destination-out", null, green), "outside g");
    }

    public static TheoryData<string> Erasers() => [.. Erasing];

    /// <summary>
    /// A parent with an opacity of its own: its children combine in its layer first, then the layer is drawn at the
    /// parent's opacity (one layer, not two).
    /// </summary>
    [Theory]
    [MemberData(nameof(Erasers))]
    public void A_parent_at_half_opacity_combines_its_children_then_draws_its_layer_at_that_opacity(string op)
    {
        var parent = "{'kind':'group','opacity':0.5,'children':[" + Rect(0, 20, Green) + "," + Group(op, Rect(10, 20, Black)) + "]}";
        using var bitmap = Strip(parent, Blue + "ff");
        var (green, black) = (Straight(Green), Straight(Black));
        Region(bitmap, 5, Over(Faded(Composite(op, null, green), 0.5), Blue), "destination only");
        Region(bitmap, 15, Over(Faded(Composite(op, black, green), 0.5), Blue), "both");
        Region(bitmap, 25, Over(Faded(Composite(op, black, null), 0.5), Blue), "source only");
        Region(bitmap, 35, Premul(Straight(Blue)), "the background");
    }

    /// <summary>A parent with a blend of its own (multiply): its layer, as its children left it, multiplies what is below.</summary>
    [Fact]
    public void A_parent_with_a_blend_of_its_own_blends_its_layer_after_its_children_combine()
    {
        var parent = "{'kind':'group','blend':'multiply','children':[" + Rect(0, 20, Green) + "," + Group("destination-in", Rect(10, 20, "#000000")) + "]}";
        using var bitmap = Strip(parent, Blue + "ff");
        var (green, blue) = (Straight(Green), Straight(Blue));
        Region(bitmap, 5, Premul(blue), "the destination cleared: the background as it was");
        Region(bitmap, 15, [green[0] * blue[0], green[1] * blue[1], green[2] * blue[2], 1], "kept and multiplied");
        Region(bitmap, 25, Premul(blue), "the source alone: nothing kept");
    }

    // ---- the empty destination --------------------------------------------------------------------------------------

    public static TheoryData<string> AllOperators() => [.. Operators];

    /// <summary>
    /// The first node its parent draws has nothing below it in the parent's layer: source-out, destination-over,
    /// destination-atop and xor draw it as normal does; source-in, source-atop, destination-in and destination-out
    /// draw nothing (scene.md). The background stays as it was either way.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllOperators))]
    public void A_group_first_in_its_parent_has_an_empty_destination(string op)
    {
        var asNormal = op is "source-out" or "destination-over" or "destination-atop" or "xor";
        foreach (var nodes in new[] { Group("", Group(op, Rect(10, 20, Green))), Group(op, Rect(10, 20, Green)) })
        {
            using var bitmap = Strip(nodes, Blue + "ff");
            Region(bitmap, 15, asNormal ? Premul(Straight(Green)) : Premul(Straight(Blue)), op + (asNormal ? " draws as normal" : " draws nothing"));
            Region(bitmap, 5, Premul(Straight(Blue)), "the background");
        }
    }

    /// <summary>
    /// Isolation follows the declared blend: a Porter-Duff group that isn't drawn (hidden, or at opacity 0) still makes
    /// its parent a layer, the scene's own at the top level as a group's when nested, so a sibling's multiply combines
    /// inside it (green on nothing: green), never with the red background; and, not drawn, it applies no operator (its
    /// destination-in would erase the green). The nested rows were already so: guards.
    /// </summary>
    [Theory]
    [InlineData("top level", "'visible':false")]
    [InlineData("top level", "'opacity':0")]
    [InlineData("nested", "'visible':false")]
    [InlineData("nested", "'opacity':0")]
    public void An_operator_that_isnt_drawn_still_isolates_its_parent_and_applies_nothing(string where, string notDrawn)
    {
        var multiply = Group("multiply", Rect(10, 20, Green));
        var op = "{'kind':'group'," + notDrawn + ",'blend':'destination-in','children':[" + Rect(0, 5, Black) + "]}";
        using var bitmap = Strip(where == "top level" ? multiply + "," + op : Group("", multiply, op), "#ff0000ff");
        Region(bitmap, 15, Premul(Straight(Green)), where + ", " + notDrawn + ": the multiply inside the layer");
        Region(bitmap, 5, Premul(Straight("#ff0000")), "the background");
        Region(bitmap, 35, Premul(Straight("#ff0000")), "the background");
    }

    // ---- with the existing blends -----------------------------------------------------------------------------------

    /// <summary>The other modes keep drawing onto what is below, with no layer for their parent: another item too.</summary>
    [Fact]
    public void Multiply_still_blends_with_another_item_below()
    {
        using var bitmap = Render(Document(Transparent, 40, 4, Item("a", Rect(0, 40, Blue)), Item("b", Group("", Group("multiply", Rect(10, 20, Green))))), 40, 4);
        var (green, blue) = (Straight(Green), Straight(Blue));
        Region(bitmap, 15, [green[0] * blue[0], green[1] * blue[1], green[2] * blue[2], 1], "item b's green multiplied with item a's blue");
        Region(bitmap, 5, Premul(blue), "item a alone");
    }

    /// <summary>
    /// A group with both kinds of children is a layer: inside it, multiply combines with what the group drew before it
    /// (with nothing where it drew nothing), not with the background below the group.
    /// </summary>
    [Fact]
    public void In_a_group_with_an_operator_the_other_blends_combine_inside_its_layer()
    {
        var parent = Group("", Rect(0, 20, Green), Group("multiply", Rect(10, 20, Black)), Group("destination-in", Rect(0, 40, "#000000")));
        using var bitmap = Strip(parent, Blue + "ff");
        var (green, black) = (Straight(Green), Straight(Black));
        Region(bitmap, 5, Premul(green), "green alone");
        Region(bitmap, 15, [green[0] * black[0], green[1] * black[1], green[2] * black[2], 1], "multiplied with the green");
        Region(bitmap, 25, Premul(black), "multiplied with nothing: the black as it is, not the background multiplied");
        Region(bitmap, 35, Premul(Straight(Blue)), "the background");
    }

    /// <summary>
    /// A scene with none of the eight operators opens exactly the layers it did: one per group with effects, an opacity
    /// below 1 or a blend other than normal (the drawer's count against the rule, over every valid example, the fixture
    /// scenes and the 1.x all-kinds reference document). The example with operators opens one more per parent of an
    /// operator, and one for a scene with one at its top level.
    /// </summary>
    [Fact]
    public void A_scene_without_an_operator_opens_the_layers_it_did_and_one_with_them_opens_one_per_parent()
    {
        var documents = new List<(string Name, JsonElement Document)>();
        foreach (var path in Directory.GetFiles(Path.Combine(SceneKit.ExamplesDir, "scene", "valid"), "*.json").Order(StringComparer.Ordinal))
            documents.Add(("scene/valid/" + Path.GetFileName(path), SceneKit.Doc(SceneKit.Load(path), 400, 300, 10, 10, 200, 120)));
        foreach (var path in Directory.GetFiles(SceneKit.FixturesDir, "*.json").Order(StringComparer.Ordinal))
            documents.Add(("fixture " + Path.GetFileName(path), SceneKit.Doc(SceneKit.Load(path), 420, 260)));
        documents.Add(("all-kinds", SceneKit.Load(Path.Combine(Golden.TestPaths.ProjectDirectory, "Scenes", "Reference", "v1", "all-kinds.document.json"))));
        documents.Add(("one normal child", SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','nodes':[" + Group("", Rect(0, 10, Green), Group("normal", Rect(5, 10, Blue))) + "]}"), 40, 4)));
        var withOperators = 0;
        foreach (var (name, document) in documents)
        {
            using var media = SceneKit.SyncMedia();
            using var snapshot = SceneRuntime.RenderOnce(document, SceneKit.Now, SceneKit.Options(new ReadEveryKey(), media));
            var (rule, isolating) = Layers(snapshot);
            using var bitmap = new SKBitmap(new SKImageInfo(snapshot.Width, snapshot.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            var before = SceneDrawer.LayersMade;
            SceneDrawer.CountLayers = true;
            try
            {
                snapshot.Draw(canvas, 1);
            }
            finally
            {
                SceneDrawer.CountLayers = false;
            }
            var made = SceneDrawer.LayersMade - before;
            if (isolating > 0) withOperators++;
            Assert.True(made == rule + isolating, $"{name}: {made} layers, the rule gives {rule} and {isolating} for operators");
            if (name != "scene/valid/blend-porter-duff.json") Assert.Equal(0, isolating);
        }
        Assert.Equal(1, withOperators);
    }

    /// <summary>
    /// The layers a snapshot's drawn groups need by the rule before the operators (effects, opacity below 1, a blend
    /// other than normal), and those the operators add: a parent of one that needs none by that rule, and a scene's own.
    /// </summary>
    private static (int Rule, int Isolating) Layers(TickSnapshot snapshot)
    {
        var field = typeof(TickSnapshot).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        int rule = 0, isolating = 0;
        static bool Operator(RNode n) => n is RGroup { Opacity: > 0 } g && Operators.Contains(Name(g.Blend));
        void Nodes(RNode[] nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Opacity <= 0 || n is not RGroup g) continue;
                if (g.Effects.Length > 0 || g.Opacity < 1 || g.Blend != SKBlendMode.SrcOver) rule++;
                else if (g.Children.Any(Operator) || g.Isolates) isolating++;
                Nodes(g.Children);
            }
        }
        void Items(SnapItem[] items)
        {
            foreach (var item in items)
                if (item is SnapGroup sg) Items(sg.Children);
                else if (item is SnapScene { Nodes: { } n })
                {
                    if (n.Any(Operator)) isolating++;
                    Nodes(n);
                }
        }
        Items(Assert.IsType<SnapItem[]>(field?.GetValue(snapshot)));
        return (rule, isolating);
    }

    private static string Name(SKBlendMode mode) => mode switch
    {
        SKBlendMode.SrcIn => "source-in",
        SKBlendMode.SrcOut => "source-out",
        SKBlendMode.SrcATop => "source-atop",
        SKBlendMode.DstOver => "destination-over",
        SKBlendMode.DstIn => "destination-in",
        SKBlendMode.DstOut => "destination-out",
        SKBlendMode.DstATop => "destination-atop",
        SKBlendMode.Xor => "xor",
        _ => mode.ToString(),
    };

    /// <summary>Every number, history and table live, every text a short title (the example's marquee reads one).</summary>
    private sealed class ReadEveryKey : ISceneInputSource
    {
        private readonly EveryKeyInputs _inner = new();

        public SceneReading Read(string key, string inputType) =>
            inputType == "text" ? new SceneReading { Status = SceneInputStatus.Live, Text = "A title" } : _inner.Read(key, inputType);

        public IReadOnlyList<double> History(string key) => _inner.History(key);
    }

    // ---- text and media as destination or source --------------------------------------------------------------------

    /// <summary>
    /// The marquee's case: text masked by a destination-in rectangle keeps its anti-aliased edges as drawn where the mask
    /// is opaque (its coverage, byte for byte) and nothing where it is transparent.
    /// </summary>
    [Fact]
    public void Text_under_a_destination_in_mask_keeps_its_coverage_where_the_mask_is_opaque()
    {
        const string text = "{'kind':'text','text':'Masked text','font':{'family':'Inter','size':18},'fill':'#e5e7eb','w':120}";
        using var alone = Render(Document(Transparent, 120, 24, Item("t", text)), 120, 24);
        using var masked = Render(Document(Transparent, 120, 24, Item("t", Group("", text, Group("destination-in", "{'kind':'rect','w':60,'h':24,'fill':'#000000'}")))), 120, 24);
        var inked = 0;
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 120; x++)
            {
                var (a, m) = (Pixel(alone, x, y), Pixel(masked, x, y));
                if (x < 60)
                {
                    Assert.True(a.SequenceEqual(m), $"({x}, {y}): {string.Join(" ", m)}, the text alone {string.Join(" ", a)}");
                    if (a[3] is > 0 and < 255) inked++;
                }
                else
                {
                    Assert.True(m.All(v => v == 0), $"({x}, {y}) past the mask: {string.Join(" ", m)}");
                }
            }
        Assert.True(inked > 20, $"{inked} anti-aliased pixels compared");
    }

    /// <summary>
    /// The tinted picture: a source-atop colour over an image takes the image's coverage exactly (its alpha, its
    /// anti-aliased edges included) and the tint's colour; above scale 1 too, where the picture is the canvas's pixels
    /// enlarged inside its parent's layer.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void An_image_under_a_source_atop_tint_keeps_its_coverage_in_the_tints_colour(float scale)
    {
        const string media = "{'kind':'media','w':24,'h':24,'fit':'contain','source':{'asset':'sha256:2e6bf049cc4791922aabe0704b3d6e978102348ec52e63bc29a7574281450be1'}}";
        using var alone = Render(Document(Transparent, 24, 24, Item("m", media)), 24, 24, scale);
        using var tinted = Render(Document(Transparent, 24, 24, Item("m", media + "," + Group("source-atop", "{'kind':'rect','w':24,'h':24,'fill':'#38bdf8'}"))), 24, 24, scale);
        var tint = Straight("#38bdf8");
        int partial = 0, opaque = 0, clear = 0;
        for (var y = 0; y < alone.Height; y++)
            for (var x = 0; x < alone.Width; x++)
            {
                var (a, t) = (Pixel(alone, x, y), Pixel(tinted, x, y));
                Assert.True(a[3] == t[3], $"({x}, {y}): alpha {t[3]}, the image's {a[3]}");
                for (var c = 0; c < 3; c++)
                    Assert.True(Math.Abs(t[c] - tint[c] * a[3]) <= 1, $"({x}, {y}) channel {c}: {t[c]}, the tint at the image's alpha {tint[c] * a[3]:F2}");
                if (a[3] == 0) clear++;
                else if (a[3] == 255) opaque++;
                else partial++;
            }
        Assert.True(partial > 0 && opaque > 0 && clear > 0, $"partial {partial}, opaque {opaque}, clear {clear}");
    }

    // ---- the layer budget -------------------------------------------------------------------------------------------

    /// <summary><paramref name="n"/> groups nested, each holding the next and an operator (the innermost a rectangle and one).</summary>
    private static string Chain(int n, string extra = "")
    {
        var pd = Group("destination-over", Rect(0, 10, Green));
        var nodes = Rect(0, 40, Blue) + "," + pd;
        for (var i = 0; i < n; i++) nodes = "{'kind':'group'" + extra + ",'children':[" + nodes + "]}," + pd;
        return nodes;
    }

    private static string Nested(int n, string inner)
    {
        var nodes = inner;
        for (var i = 0; i < n; i++) nodes = "{'kind':'group','opacity':0.5,'children':[" + nodes + "]}";
        return nodes;
    }

    /// <summary>The layer budget's rows: each scene, the layers it holds at once, and whether that is over the 8.</summary>
    public static TheoryData<string, string, int> Budget() => new()
    {
        // A group with an operator child: a parent layer, and the child's own inside it.
        { "one parent", Group("", Rect(0, 40, Blue), Group("destination-in", Rect(0, 10, Green))), 2 },
        // p parents nested in one another, each with an operator child beside the next: p + 1 at the innermost operator
        // (one under the budget, at it, one over).
        { "6 nested parents", Group("", Chain(5)), 7 },
        { "7 nested parents", Group("", Chain(6)), 8 },
        { "8 nested parents", Group("", Chain(7)), 9 },
        // The same, each but the outermost at an opacity of 0.5: already layers, so each counts once.
        { "7 nested parents at half opacity", Group("", Chain(6, ",'opacity':0.5")), 8 },
        { "8 nested parents at half opacity", Group("", Chain(7, ",'opacity':0.5")), 9 },
        // A top-level operator beside groups drawn as layers: the scene's layer and theirs.
        { "top level beside 6", Group("xor", Rect(0, 10, Green)) + "," + Nested(6, Rect(0, 40, Blue)), 7 },
        { "top level beside 7", Group("xor", Rect(0, 10, Green)) + "," + Nested(7, Rect(0, 40, Blue)), 8 },
        { "top level beside 8", Group("xor", Rect(0, 10, Green)) + "," + Nested(8, Rect(0, 40, Blue)), 9 },
        // Without the operator the same 8 fit: the scene's layer is what goes over.
        { "8 without an operator", Group("", Rect(0, 10, Green)) + "," + Nested(8, Rect(0, 40, Blue)), 8 },
        // An operator group that is itself a parent of one: its own layer is the isolation, counted once.
        { "an operator in an operator", Group("", Rect(0, 40, Blue), Group("destination-out", Rect(0, 30, Black), Group("xor", Rect(0, 10, Black)))), 3 },
        // An operator child that isn't drawn still counts for its parent; at opacity 0 the parent holds none.
        { "an invisible operator", Group("", Rect(0, 40, Blue), "{'kind':'group','visible':false,'blend':'destination-in','children':[" + Rect(0, 10, Green) + "]}"), 1 },
        { "a parent at opacity 0", "{'kind':'group','opacity':0,'children':[" + Rect(0, 40, Blue) + "," + Group("destination-in", Rect(0, 10, Green)) + "]}", 0 },
        // A top-level operator that is never drawn still makes the scene a layer: counted, and opened.
        { "a hidden top-level operator", Rect(0, 40, Blue) + ",{'kind':'group','visible':false,'blend':'xor','children':[" + Rect(0, 10, Green) + "]}", 1 },
        { "a top-level operator at opacity 0", Rect(0, 40, Blue) + ",{'kind':'group','opacity':0,'blend':'xor','children':[" + Rect(0, 10, Green) + "]}", 1 },
    };

    /// <summary>
    /// The layers held at once, counted as the runtime allocates them (8 at most, the 9th over budget); validation
    /// counts no layers (a run-time budget refuses no content), so every row is valid.
    /// </summary>
    [Theory]
    [MemberData(nameof(Budget))]
    public void The_layer_budget_counts_each_parent_of_an_operator_once_and_a_scenes_own_layer(string row, string nodes, int layers)
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','nodes':[" + nodes + "]}");
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, $"{row}: " + string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, 40, 4), SceneKit.Now, SceneKit.Options());
        var over = layers > SceneEvaluator.MaxOpenLayers;
        Assert.True((over ? "budget" : null) == snapshot.Bounds.Single().Problem, $"{row}: {layers} layers, problem {snapshot.Bounds.Single().Problem}");
        Assert.Equal(over, snapshot.Diagnostics.Any(d => d.Code == "budget.layers"));
        if (over) return;
        // Within the budget, the drawer holds as many at once as counted.
        using var bitmap = new SKBitmap(new SKImageInfo(40, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        SceneDrawer.DeepestLayers = 0;
        SceneDrawer.CountLayers = true;
        try
        {
            snapshot.Draw(canvas, 1);
        }
        finally
        {
            SceneDrawer.CountLayers = false;
        }
        Assert.True(SceneDrawer.DeepestLayers == layers, $"{row}: {SceneDrawer.DeepestLayers} layers held at once, {layers} counted");
    }

    /// <summary>The drawer counts its layers only on a thread that asks (a test): otherwise drawing leaves the counters as they were.</summary>
    [Fact]
    public void The_drawer_counts_its_layers_only_when_asked()
    {
        var scene = SceneKit.Json("{'format':'infopanel.scene/1','nodes':[" + Group("", Rect(0, 40, Blue), Group("destination-in", Rect(0, 10, Green))) + "]}");
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, 40, 4), SceneKit.Now, SceneKit.Options());
        using var bitmap = new SKBitmap(new SKImageInfo(40, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        (SceneDrawer.LayersMade, SceneDrawer.DeepestLayers) = (0, 0);
        snapshot.Draw(canvas, 1);
        Assert.True((SceneDrawer.LayersMade, SceneDrawer.DeepestLayers) == (0, 0),
            $"not asked: {SceneDrawer.LayersMade} layers made, {SceneDrawer.DeepestLayers} at once");
        SceneDrawer.CountLayers = true;
        try
        {
            snapshot.Draw(canvas, 1);
        }
        finally
        {
            SceneDrawer.CountLayers = false;
        }
        Assert.Equal((2, 2), (SceneDrawer.LayersMade, SceneDrawer.DeepestLayers));
    }

    // ---- the validator: each row the editor's validator's too (its parity rows) ---------------------------------------

    private static SceneCheck Check(string node) => SceneValidator.Check(SceneKit.Json("{'format':'infopanel.scene/1','nodes':[" + node + "]}"));

    private static string Compose(string blend) =>
        "{'kind':'rect','w':10,'h':10,'fill':{'type':'compose','blend':'" + blend + "','dst':'#ff0000','src':'#0000ff'}}";

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "{'kind':'group','blend':'clear','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','blend':'copy','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','blend':'destination','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','blend':'destination-inn','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','blend':'Source-In','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','blend':'src-in','children':[]}", "schema", "/nodes/0/blend" },
        { "{'kind':'group','children':[{'kind':'group','blend':'clear','children':[]}]}", "schema", "/nodes/0/children/0/blend" },
        { Compose("clear"), "schema", "/nodes/0/fill/blend" },
        { Compose("copy"), "schema", "/nodes/0/fill/blend" },
        { Compose("destination"), "schema", "/nodes/0/fill/blend" },
        { Compose("xour"), "schema", "/nodes/0/fill/blend" },
        // A leaf node has no blend: refused as any unknown member is.
        { "{'kind':'rect','w':10,'h':10,'fill':'#ffffff','blend':'destination-in'}", "schema", "/nodes/0/blend" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_validator_refuses_at_its_code_and_path(string node, string code, string path)
    {
        var check = Check(node);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        Assert.Equal([(code, path)], check.Errors.Select(e => (e.Code, e.Path)));
    }

    public static TheoryData<string> Accepted()
    {
        var data = new TheoryData<string>();
        foreach (var op in Operators)
        {
            data.Add("{'kind':'group','blend':'" + op + "','children':[" + Rect(0, 10, Green) + "]}");
            data.Add(Compose(op));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public void The_validator_accepts_each_operator_on_a_group_and_in_a_compose_paint(string node)
    {
        var check = Check(node);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
    }
}
