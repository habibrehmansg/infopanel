using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>Cases found in review of the scene runtime, each held here so it stays fixed.</summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneRegressionTests
{
    private sealed class Sensors : ISensorReader, ISensorHistory
    {
        public Dictionary<string, SensorReading> Readings { get; } = new(StringComparer.Ordinal);

        public bool TryRead(SensorKey key, out SensorReading reading) => Readings.TryGetValue(key.ToString(), out reading);

        public IReadOnlyList<double> GetSamples(SensorKey key) => [];
    }

    [Fact]
    public void A_text_input_bound_to_a_numeric_or_table_sensor_is_unavailable()
    {
        var sensors = new Sensors();
        var number = SensorKey.Plugin("test/number").ToString();
        var text = SensorKey.Plugin("test/text").ToString();
        var table = SensorKey.Plugin("test/table").ToString();
        sensors.Readings[number] = new SensorReading(1, 2, 1.5, 1.25, "W");
        sensors.Readings[text] = new SensorReading("hello");
        var dt = new DataTable();
        dt.Columns.Add("a");
        dt.Rows.Add("x");
        sensors.Readings[table] = new SensorReading(dt, "", "summary");
        var inputs = new SensorSceneInputs(sensors, sensors);

        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(number, "text").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(table, "text").Status);
        var live = inputs.Read(text, "text");
        Assert.Equal(SceneInputStatus.Live, live.Status);
        Assert.Equal("hello", live.Text);
        Assert.Equal(SceneInputStatus.Live, inputs.Read(number, "number").Status);
        Assert.Equal(SceneInputStatus.Live, inputs.Read(table, "table").Status);
        Assert.Equal(SceneInputStatus.Unavailable, inputs.Read(text, "number").Status);
    }

    [Fact]
    public void A_literal_that_rounds_to_infinity_makes_the_document_unreadable()
    {
        // 1e400 isn't a number the formats read (it rounds to infinity as a double): the document is refused as json at
        // the literal, not loaded with a placeholder.
        var bad = SceneKit.Json("{'format':'infopanel.scene/1','state':{'c':{'init':1e400,'next':{'op':'state','name':'c'}}},'nodes':[{'kind':'rect','w':{'op':'state','name':'c'},'h':10,'fill':'#ffffff'}]}");
        var good = SceneKit.Example("scene/valid/shapes.json");
        var doc = JsonNode.Parse(SceneKit.Doc(good, 300, 200).GetRawText())!;
        var item = doc["items"]![0]!.DeepClone();
        item["id"] = "bad";
        item["scene"] = JsonNode.Parse(bad.GetRawText());
        doc["items"]!.AsArray().Add(item);
        using var runtime = new SceneRuntime(SceneKit.Options());
        var check = Assert.Throws<SceneDocumentException>(() => runtime.Load(JsonSerializer.SerializeToElement(doc))).Check;
        Assert.Equal(InfoPanel.Spec.CheckStatus.Invalid, check.Status);
        var error = Assert.Single(check.Errors);
        Assert.Equal(("/items/1/scene/state/c/init", "json"), (error.Path, error.Code));
        var alone = InfoPanel.Spec.SceneValidator.Check(bad);
        Assert.Equal("json /state/c/init", string.Join("; ", alone.Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    private static TickSnapshot Tick(string scene, int width = 300, int height = 200, SceneRuntimeOptions? options = null)
    {
        using var runtime = new SceneRuntime(options ?? SceneKit.Options());
        runtime.Load(SceneKit.Doc(SceneKit.Json(scene), width, height));
        return runtime.Tick(0, SceneKit.Now);
    }

    [Fact]
    public void A_text_placed_by_its_own_measurement_is_placed()
    {
        using var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'text','id':'t','x':{'op':'measure','node':'t','field':'w'},'y':20,"
            + "'font':{'family':'Inter','size':20},'fill':'#ffffff','text':'Measured'}]}");
        var frame = s.Bounds.Single().Frame;
        Assert.True(frame.X > 40, $"x {frame.X}");
        Assert.Equal(frame.W, frame.X, 1); // the text starts where its own width ends
    }

    [Fact]
    public void A_dashed_text_stroke_too_fine_for_its_glyphs_is_over_budget()
    {
        using (var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'text','font':{'family':'Inter','size':200},'fill':'#ffffff',"
            + "'stroke':{'paint':'#ff0000','width':1,'dash':[0.001,0.001]},'text':'Dashes'}]}"))
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
        }
        using (var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'text','font':{'family':'Inter','size':40},'fill':'#ffffff',"
            + "'stroke':{'paint':'#ff0000','width':1,'dash':[4,2]},'text':'Dashes'}]}"))
            Assert.Null(s.Bounds.Single().Problem);
    }

    [Fact]
    public void A_dashed_table_stroke_too_fine_for_its_glyphs_is_over_budget()
    {
        var inputs = new FakeSceneInputs();
        inputs.Readings["test:t"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(["a"], Enumerable.Range(0, 20).Select(r => (IReadOnlyList<string>)[$"row {r} with text"]).ToArray()),
        };
        using var s = Tick("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[{'kind':'table','input':'t','rows':20,"
            + "'columns':[{'column':0,'w':280}],'font':{'family':'Inter','size':12},'fill':'#ffffff','stroke':{'paint':'#ff0000','width':1,'dash':[0.001,0.001]}}]}",
            options: SceneKit.Options(inputs));
        Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
    }

    [Fact]
    public void A_long_gap_dash_over_many_glyph_contours_is_over_budget()
    {
        // Skia starts the dash pattern again on every contour, so [1, 1000000] draws a dash on each glyph contour
        // however short the outline is: 100 rows of 16 cells of twenty '%' (five contours each) would be 160,000.
        var inputs = new FakeSceneInputs();
        var cell = new string('%', 20);
        inputs.Readings["test:t"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(Enumerable.Range(0, 16).Select(c => $"c{c}").ToArray(),
                Enumerable.Range(0, 100).Select(_ => (IReadOnlyList<string>)Enumerable.Repeat(cell, 16).ToArray()).ToArray()),
        };
        var columns = string.Join(",", Enumerable.Range(0, 16).Select(c => $"{{'column':{c},'w':200}}"));
        using var s = Tick("{'format':'infopanel.scene/1','inputs':{'t':{'type':'table'}},'nodes':[{'kind':'table','input':'t','rows':100,"
            + "'columns':[" + columns + "],'font':{'family':'Inter','size':12},'fill':'#ffffff','stroke':{'paint':'#ff0000','width':1,'dash':[1,1000000]}}]}",
            options: SceneKit.Options(inputs));
        Assert.Equal("budget", s.Bounds.Single().Problem);
        Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
    }

    [Fact]
    public void Lines_of_a_text_out_of_view_are_not_sent_to_skia()
    {
        // 300 lines, all but the last few above the canvas. A recording keeps every draw it is given, so its operation
        // count shows how many runs were submitted.
        var text = string.Join("\\n", Enumerable.Range(0, 300).Select(i => $"line {i}"));
        using var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'text','x':10,'y':-3550,'lineHeight':1.2,'font':{'family':'Inter','size':10},"
            + "'fill':'#000000','stroke':{'paint':'#ff0000','width':1},'text':'" + text + "'}]}", 300, 100);
        Assert.Null(s.Bounds.Single().Problem);
        using var recorder = new SKPictureRecorder();
        s.Draw(recorder.BeginRecording(new SKRect(0, 0, 300, 100)), 1);
        using var picture = recorder.EndRecording();
        Assert.True(picture.ApproximateOperationCount < 60, $"{picture.ApproximateOperationCount} operations");
        using var bmp = s.Render();
        Assert.Contains(Enumerable.Range(0, 100).SelectMany(y => Enumerable.Range(0, 300).Select(x => bmp.GetPixel(x, y))), p => p.Red > 200 && p.Green < 50);
    }

    [Fact]
    public void A_text_out_of_view_whose_shadow_is_in_view_is_drawn()
    {
        using var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'group','effects':[{'type':'shadow','dx':400,'dy':0,'sigma':0,'color':'#ff0000'}],"
            + "'children':[{'kind':'text','x':-380,'y':20,'font':{'family':'Inter','size':40},'fill':'#000000','text':'WWW'}]}]}", 200, 100);
        using var bmp = s.Render();
        Assert.Contains(Enumerable.Range(0, 100).SelectMany(y => Enumerable.Range(0, 200).Select(x => bmp.GetPixel(x, y))), p => p.Red > 200 && p.Green < 50);
    }

    [Fact]
    public void Three_slow_draws_of_one_tick_are_one_slow_tick()
    {
        // Everything is over the budget, so every tick is slow; drawing one tick three times (three outputs) must not
        // count as three ticks.
        var options = new SceneRuntimeOptions { ItemBudgetMs = 0.000001, LocalZone = TimeZoneInfo.Utc };
        using var runtime = new SceneRuntime(options);
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/shapes.json"), 220, 180));
        using (var first = runtime.Tick(0, SceneKit.Now))
            for (var i = 0; i < 3; i++) first.Render().Dispose();
        runtime.Tick(0.1, SceneKit.Now).Dispose();
        using (var third = runtime.Tick(0.2, SceneKit.Now))
            Assert.Null(third.Bounds.Single().Problem); // two slow ticks so far
        using var fourth = runtime.Tick(0.3, SceneKit.Now);
        Assert.Equal("budget", fourth.Bounds.Single().Problem); // the third slow tick suspended it
        Assert.Contains(fourth.Diagnostics, d => d.Code == "budget.time");
    }

    [Fact]
    public void A_text_stroke_reaching_into_view_from_outside_is_drawn()
    {
        // The glyph is left of the canvas; its 300-wide stroke reaches 150 px past it, well into view.
        using var s = Tick("{'format':'infopanel.scene/1','nodes':[{'kind':'text','x':-100,'y':0,'font':{'family':'Inter','size':20},"
            + "'fill':'#ffffff','stroke':{'paint':'#ff0000','width':300,'join':'round'},'text':'W'}]}", 100, 100);
        using var bmp = s.Render();
        var p = bmp.GetPixel(10, 10);
        Assert.True(p.Red > 200 && p.Green < 50, $"{p}");
    }

    [Fact]
    public void One_string_of_many_lines_is_held_to_the_text_budget_while_it_is_laid_out()
    {
        // 4,096 newlines from a text sensor: 4,097 paragraphs in one layout, past the item's 2,000.
        var inputs = new FakeSceneInputs();
        inputs.Text("test:t", string.Join("\n", Enumerable.Repeat("x", 4097)));
        const string text = "'text':{'op':'input','name':'t','field':'text'}";
        string Scene(string box) => "{'format':'infopanel.scene/1','inputs':{'t':{'type':'text'}},'nodes':[{'kind':'text'" + box
            + ",'font':{'family':'Inter','size':4},'fill':'#ffffff'," + text + "}]}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var s = Tick(Scene(""), options: SceneKit.Options(inputs)))
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");

        // The document's budget is enforced in the middle of a layout too, fit probes included.
        inputs.Text("test:t", string.Join("\n", Enumerable.Repeat("x", 40)));
        var options = new SceneRuntimeOptions { Inputs = inputs, LocalZone = TimeZoneInfo.Utc, LayoutsPerTick = 50 };
        using (var s = Tick(Scene(""), options: options))
            Assert.Null(s.Bounds.Single().Problem);
        using (var s = Tick(Scene(",'w':20,'fit':{'min':1}"), options: options))
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
    }

    private sealed class OnceProvider(SKImage image) : IMediaProvider
    {
        private readonly MediaSample _sample = MediaSample.FromImage(image);
        public int Calls;

        public MediaSample? TryGet(string reference, double seconds) => Interlocked.Increment(ref Calls) == 1 ? _sample.Retain() : null;
    }

    [Fact]
    public void The_same_media_source_shows_the_same_frame_everywhere_in_a_tick()
    {
        using var bmp = new SKBitmap(8, 8);
        bmp.Erase(SKColors.Red);
        var provider = new OnceProvider(SKImage.FromBitmap(bmp));
        var options = SceneKit.Options(providers: new Dictionary<string, IMediaProvider> { ["p"] = provider });
        using var s = Tick("{'format':'infopanel.scene/1','requires':['media.provider'],'nodes':["
            + "{'kind':'media','w':20,'h':20,'source':{'provider':'p','ref':'x'}},{'kind':'media','x':30,'w':20,'h':20,'source':{'provider':'p','ref':'x'}}]}",
            60, 30, options);
        Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain(s.Diagnostics, d => d.Code == "media.loading");
        using var frame = s.Render();
        Assert.Equal(SKColors.Red, frame.GetPixel(10, 10));
        Assert.Equal(SKColors.Red, frame.GetPixel(40, 10));
    }
}
