using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Whether two tick snapshots draw the same pixels (<see cref="TickSnapshot.DrawsAs"/>), which lets the render loop keep the
/// pixels it last drew. Goldens can't see a draw wrongly skipped, so each case here is two snapshots that differ in one
/// field a draw reads, and must compare as different; the same document ticked again with nothing changing compares as
/// the same.
/// </summary>
public sealed class TickSnapshotComparisonTests : IDisposable
{
    private readonly MemoryAssets _assets = new();
    private readonly MediaSampleCache _media;
    private readonly string _red, _blue, _gif;

    public TickSnapshotComparisonTests()
    {
        _red = Put(Png(SKColors.Red));
        _blue = Put(Png(SKColors.Blue));
        // Two 100 ms frames, red then blue.
        _gif = Put(TestGif.Build(8, 8, new TestGif.Frame(0, 0, 8, 8, 1), new TestGif.Frame(0, 0, 8, 8, 3)));
        _media = SceneKit.SyncMedia(_assets);
    }

    public void Dispose() => _media.Dispose();

    [Fact]
    public void The_same_document_ticked_again_with_nothing_changing_draws_the_same()
    {
        var nodes = "{'kind':'group','clip':{'kind':'rect','x':0,'y':0,'w':60,'h':40},'children':["
            + "{'kind':'rect','x':5,'y':5,'w':80,'h':30,'fill':'#00ff00','stroke':{'paint':'#ff0000','width':2,'dash':[4,2]}}]},"
            + "{'kind':'text','x':0,'y':50,'font':{'family':'Inter','size':16},'fill':'#ffffff','text':'Static'},"
            + $"{{'kind':'media','x':100,'y':0,'w':40,'h':40,'sampling':'nearest','source':{{'asset':'{_red}'}}}},"
            + $"{{'kind':'media','x':150,'y':0,'w':40,'h':40,'source':{{'sequence':[{{'asset':'{_red}'}},{{'asset':'{_blue}'}}],'index':0.5,'crossfade':true}}}}";
        using var runtime = Runtime(Scene(nodes));
        using var first = runtime.Tick(0, SceneKit.Now);
        using var second = runtime.Tick(0.034, SceneKit.Now);
        Assert.True(second.DrawsAs(first));
        Assert.True(first.DrawsAs(second));
    }

    /// <summary>Two documents, the same but for one field: each loaded twice compares as the same, and as different from the other.</summary>
    public static TheoryData<string, string, string> Documents => new()
    {
        {
            "group clip rect",
            "{'kind':'group','clip':{'kind':'rect','x':0,'y':0,'w':60,'h':40},'children':[{'kind':'rect','w':80,'h':80,'fill':'#00ff00'}]}",
            "{'kind':'group','clip':{'kind':'rect','x':0,'y':0,'w':61,'h':40},'children':[{'kind':'rect','w':80,'h':80,'fill':'#00ff00'}]}"
        },
        {
            "stroke width",
            "{'kind':'rect','x':5,'y':5,'w':80,'h':30,'stroke':{'paint':'#ff0000','width':2}}",
            "{'kind':'rect','x':5,'y':5,'w':80,'h':30,'stroke':{'paint':'#ff0000','width':3}}"
        },
        {
            "stroke dash",
            "{'kind':'rect','x':5,'y':5,'w':80,'h':30,'stroke':{'paint':'#ff0000','width':2,'dash':[4,2]}}",
            "{'kind':'rect','x':5,'y':5,'w':80,'h':30,'stroke':{'paint':'#ff0000','width':2,'dash':[4,3]}}"
        },
        {
            "media sampling",
            "{'kind':'media','w':40,'h':40,'source':{'asset':'RED'}}",
            "{'kind':'media','w':40,'h':40,'sampling':'nearest','source':{'asset':'RED'}}"
        },
        {
            "crossfade fraction",
            "{'kind':'media','w':40,'h':40,'source':{'sequence':[{'asset':'RED'},{'asset':'BLUE'}],'index':0.25,'crossfade':true}}",
            "{'kind':'media','w':40,'h':40,'source':{'sequence':[{'asset':'RED'},{'asset':'BLUE'}],'index':0.75,'crossfade':true}}"
        },
        {
            // The same lower picture and fraction: only the picture faded in over it differs.
            "crossfade upper picture",
            "{'kind':'media','w':40,'h':40,'source':{'sequence':[{'asset':'RED'},{'asset':'BLUE'}],'index':0.5,'crossfade':true}}",
            "{'kind':'media','w':40,'h':40,'source':{'sequence':[{'asset':'RED'},{'asset':'RED'}],'index':0.5,'crossfade':true}}"
        },
        {
            // The same nodes drawn, but a hidden Porter-Duff child makes its parent a layer: the multiply no longer reaches the red.
            "a group made a layer by a hidden operator child",
            RedUnderMultiply("", ""),
            RedUnderMultiply(",{'kind':'group','visible':false,'blend':'destination-in','children':[]}", "")
        },
        {
            // The same, at the scene's top level: the scene's nodes become one layer.
            "a scene made a layer by a hidden top-level operator",
            RedUnderMultiply("", ""),
            RedUnderMultiply("", ",{'kind':'group','visible':false,'blend':'destination-in','children':[]}")
        },
    };

    /// <summary>
    /// A red rectangle, then a normal group holding a green rectangle in a multiply group and <paramref name="inGroup"/>;
    /// then <paramref name="atTop"/>.
    /// </summary>
    private static string RedUnderMultiply(string inGroup, string atTop) =>
        "{'kind':'rect','w':80,'h':40,'fill':'#ff0000'},{'kind':'group','children':[{'kind':'group','blend':'multiply','children':["
        + "{'kind':'rect','w':40,'h':40,'fill':'#00ff00'}]}" + inGroup + "]}" + atTop;

    [Theory]
    [MemberData(nameof(Documents))]
    public void Documents_differing_in_one_field_draw_differently(string field, string a, string b)
    {
        using var one = Once(Scene(Assets(a)));
        using var again = Once(Scene(Assets(a)));
        using var other = Once(Scene(Assets(b)));
        Assert.True(again.DrawsAs(one), $"{field}: the same document compares as different");
        Assert.False(other.DrawsAs(one), $"{field}: a change compares as the same");
        Assert.False(one.DrawsAs(other), $"{field}: a change compares as the same");
    }

    [Fact]
    public void A_placeholders_box_is_compared()
    {
        // An invalid scene (a value no one defines) is drawn as a placeholder the size of its layout box.
        var invalid = Json("{'format':'infopanel.scene/1','nodes':[{'kind':'rect','w':{'op':'value','name':'missing'},'h':10,'fill':'#ffffff'}]}");
        using var one = Once(SceneKit.Doc(invalid, 200, 100, w: 60, h: 40));
        using var again = Once(SceneKit.Doc(invalid, 200, 100, w: 60, h: 40));
        using var other = Once(SceneKit.Doc(invalid, 200, 100, w: 60, h: 41));
        Assert.NotNull(one.Bounds.Single().Problem);
        Assert.True(again.DrawsAs(one));
        Assert.False(other.DrawsAs(one));
    }

    [Fact]
    public void A_marquee_at_two_times_draws_differently()
    {
        var nodes = "{'kind':'text','x':0,'y':0,'w':80,'overflow':'marquee','font':{'family':'Inter','size':16},'fill':'#ffffff',"
            + "'text':'A title far too long for its box, so it scrolls'}";
        using var runtime = Runtime(Scene(nodes));
        using var first = runtime.Tick(0, SceneKit.Now);
        using var same = runtime.Tick(0, SceneKit.Now);
        using var later = runtime.Tick(0.5, SceneKit.Now);
        Assert.True(same.DrawsAs(first));
        Assert.False(later.DrawsAs(first));
    }

    [Fact]
    public void Two_frames_of_a_gif_draw_differently()
    {
        using var runtime = Runtime(Scene($"{{'kind':'media','w':40,'h':40,'source':{{'asset':'{_gif}'}}}}"));
        using var first = runtime.Tick(0, SceneKit.Now);
        using var same = runtime.Tick(0.05, SceneKit.Now);
        using var next = runtime.Tick(0.15, SceneKit.Now);
        Assert.Equal(1, first.SampleCount);
        Assert.True(same.DrawsAs(first));
        Assert.False(next.DrawsAs(first));
    }

    private SceneRuntime Runtime(JsonElement document)
    {
        var runtime = new SceneRuntime(SceneKit.Options(media: _media));
        runtime.Load(document);
        return runtime;
    }

    /// <summary>One tick of <paramref name="document"/> in a runtime of its own (media from the shared cache).</summary>
    private TickSnapshot Once(JsonElement document)
    {
        using var runtime = Runtime(document);
        return runtime.Tick(0, SceneKit.Now);
    }

    private string Assets(string nodes) => nodes.Replace("RED", _red).Replace("BLUE", _blue);

    private static JsonElement Scene(string nodes) => SceneKit.Doc(Json($"{{'format':'infopanel.scene/1','nodes':[{nodes}]}}"), 200, 100);

    private static JsonElement Json(string json) => SceneKit.Json(json);

    private string Put(byte[] bytes)
    {
        var hash = BlobStore.IdOf(bytes);
        _assets.Assets[hash] = bytes;
        return hash;
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        return Golden.GoldenImage.EncodePng(bitmap);
    }
}
