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
