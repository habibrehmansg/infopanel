using System.Globalization;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The state and time rules of spec/docs/identity.md: a cell is keyed by its resolved type as well as its definition,
/// frame-rate independence holds for updates proportional to <c>dt</c>, and values are equal across engines given the
/// same fonts, locale and time zone.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneStateRulesTests
{
    // c starts at a colour literal; while v > 0 it takes the value k, otherwise it keeps its previous value. Its init
    // and next are the same JSON in both scenes; only k's type (and so c's resolved type) differs.
    private const string Cell = "'state':{'c':{'init':'#000000','next':{'op':'if','args':["
        + "{'op':'gt','args':[{'op':'input','name':'v'},0]},{'op':'value','name':'k'},{'op':'state','name':'c'}]}}}";

    private static JsonElement Colour(string rgba = "255,0,0,1") => SceneKit.Doc(SceneKit.Json(
        "{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'values':{'k':{'op':'rgba','args':[" + rgba + "]}}," + Cell + ","
        + "'nodes':[{'kind':'rect','w':40,'h':40,'fill':{'op':'state','name':'c'}}]}"), 120, 40);

    private static readonly JsonElement ColourCell = Colour();

    private static readonly JsonElement TextCell = SceneKit.Doc(SceneKit.Json(
        "{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'values':{'k':'hot'}," + Cell + ","
        + "'nodes':[{'kind':'text','w':120,'h':40,'font':{'family':'Inter','size':24},'fill':'#ffffff','text':{'op':'state','name':'c'}}]}"), 120, 40);

    private static int LitPixels(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red > 128) n++;
        return n;
    }

    [Fact]
    public void A_colour_cell_turned_text_starts_again()
    {
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        fake.Number("test:v", 1);
        runtime.Load(ColourCell);
        runtime.Tick(0, SceneKit.Now).Dispose();
        runtime.Tick(0.1, SceneKit.Now).Dispose(); // c is now the computed colour k: a colour, no text

        // k becomes text, so c is a text cell now, though its init and next are written as before: it starts again
        // at init ("#000000", drawn as text), instead of keeping a colour that reads as "" for good.
        fake.Number("test:v", 0);
        runtime.Load(TextCell);
        for (var t = 0.2; t < 0.5; t += 0.1)
        {
            using var s = runtime.Tick(t, SceneKit.Now);
            Assert.Null(s.Bounds.Single().Problem);
            using var bmp = s.Render();
            Assert.True(LitPixels(bmp) > 50, $"the cell's text is drawn at {t:0.0} s");
        }
    }

    [Fact]
    public void A_text_cell_turned_colour_starts_again()
    {
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        fake.Number("test:v", 1);
        runtime.Load(TextCell);
        runtime.Tick(0, SceneKit.Now).Dispose();
        runtime.Tick(0.1, SceneKit.Now).Dispose(); // c is now the text "hot": no colour

        // k becomes a colour: c starts again at init (black), instead of keeping text that draws transparent.
        fake.Number("test:v", 0);
        runtime.Load(ColourCell);
        for (var t = 0.2; t < 0.5; t += 0.1)
        {
            using var s = runtime.Tick(t, SceneKit.Now);
            using var bmp = s.Render();
            Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(20, 20));
        }
    }

    [Fact]
    public void A_cell_whose_type_stays_keeps_its_state_when_what_it_reads_changes()
    {
        // k changes from one computed colour to another: c's resolved type stays colour, so it keeps its state.
        var fake = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(fake));
        fake.Number("test:v", 1);
        runtime.Load(ColourCell);
        runtime.Tick(0, SceneKit.Now).Dispose();
        runtime.Tick(0.1, SceneKit.Now).Dispose(); // c is red
        fake.Number("test:v", 0);
        runtime.Load(Colour("0,0,255,1"));
        using var s = runtime.Tick(0.2, SceneKit.Now);
        using var bmp = s.Render();
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(20, 20));
    }

    private static double Counter(string next, double fps)
    {
        var doc = SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','state':{'c':{'init':0,'next':" + next + "}},"
            + "'nodes':[{'kind':'rect','w':{'op':'state','name':'c'},'h':10,'fill':'#ffffff'}]}"), 100, 20);
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(doc);
        for (var k = 0; k <= (int)fps; k++) runtime.Tick(k / fps, SceneKit.Now).Dispose();
        return runtime.CellValue("item", "c")!.Value;
    }

    [Fact]
    public void Frame_rate_independence_holds_for_updates_proportional_to_dt()
    {
        // previous + dt: one second at any rate.
        const string linear = "{'op':'add','args':[{'op':'state','name':'c'},{'op':'dt'}]}";
        Assert.Equal(1, Counter(linear, 1), 9);
        Assert.Equal(1, Counter(linear, 2), 9);
        Assert.Equal(1, Counter(linear, 30), 9);

        // previous + dt²: an update that isn't proportional to dt depends on the rate, as identity.md says.
        const string squared = "{'op':'add','args':[{'op':'state','name':'c'},{'op':'mul','args':[{'op':'dt'},{'op':'dt'}]}]}";
        Assert.Equal(1, Counter(squared, 1), 9);
        Assert.Equal(0.5, Counter(squared, 2), 9);
    }

    [Fact]
    public void Values_are_equal_across_engines_given_the_same_locale_and_zone()
    {
        static SKBitmap Draw(string locale, string zone, string time)
        {
            var options = new SceneRuntimeOptions
            {
                Inputs = new FakeSceneInputs(),
                Locale = RegionalFormat.Of(CultureInfo.GetCultureInfo(locale)),
                LocalZone = TimeZoneInfo.FindSystemTimeZoneById(zone),
            };
            var doc = SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','nodes':[{'kind':'text','w':300,'h':40,"
                + "'font':{'family':'Inter','size':20},'fill':'#ffffff','text':" + time + "}]}"), 300, 40);
            using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, options);
            return s.Render();
        }

        // Without a locale and zone of its own, formatTime follows the engine's: two engines differ.
        const string engine = "{'op':'formatTime','pattern':'MMMM HH'}";
        using (var a = Draw("en-GB", "UTC", engine))
        using (var b = Draw("fr-FR", "Asia/Tokyo", engine))
            Assert.NotEqual(0, SceneKit.DifferingPixels(a, b, 0));

        // With them pinned, they agree.
        const string pinned = "{'op':'formatTime','pattern':'MMMM HH','locale':'en-GB','zone':'UTC'}";
        using (var a = Draw("en-GB", "UTC", pinned))
        using (var b = Draw("fr-FR", "Asia/Tokyo", pinned))
            Assert.Equal(0, SceneKit.DifferingPixels(a, b, 0));
    }
}
