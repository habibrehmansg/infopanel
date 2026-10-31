using InfoPanel.Core.Tests.Images;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Provider media held across a change of reference (spec/docs/smart-nodes.md, "Media held across a change of
/// reference"): an item whose reference changes keeps drawing the previous reference's media until the new one's arrives
/// or fails, and takes at most one new reference a second, the latest. Per-item runtime state, so every output draws the
/// same; a one-off render asks for the reference as it is.
/// </summary>
public sealed class ProviderHoldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string A = "https://example.test/a.png";
    private const string B = "https://example.test/b.png";
    private const string C = "https://example.test/c.png";

    private const string Scene = "{'format':'infopanel.scene/1','requires':['media.provider'],'inputs':{'t':{'type':'text'}},"
        + "'nodes':[{'kind':'media','w':40,'h':30,'source':{'provider':'http','ref':{'op':'input','name':'t','field':'text'}}}]}";

    /// <summary>A provider that answers each reference what the test set, and is loading for the rest; it records every ask.</summary>
    private sealed class Keyed : IMediaProvider
    {
        public readonly Dictionary<string, ProvidedMedia> Answers = new(StringComparer.Ordinal);
        public readonly List<MediaRequest> Asks = [];

        public ProvidedMedia Get(in MediaRequest request)
        {
            lock (Asks) Asks.Add(request);
            return Answers.TryGetValue(request.Reference, out var a)
                ? new ProvidedMedia(a.Sample?.Retain(), a.Status)
                : new ProvidedMedia(null, new MediaStatus("media.loading", "Loading."));
        }

        public IEnumerable<string> Live => Asks.Where(a => a.Live).Select(a => a.Reference);
    }

    private static ProvidedMedia Solid(SKColor color)
    {
        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(color);
        return new ProvidedMedia(MediaSample.FromImage(SKImage.FromBitmap(bitmap)));
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeSceneInputs Inputs = new();
        public readonly SceneRuntime Runtime;

        public Rig(IMediaProvider provider, string scene = Scene, MediaSampleCache? media = null)
        {
            Runtime = new SceneRuntime(SceneKit.Options(Inputs, media, new Dictionary<string, IMediaProvider> { ["http"] = provider }));
            Runtime.Load(SceneKit.Doc(SceneKit.Json(scene), 60, 40, 5, 5));
        }

        /// <summary>A tick at <paramref name="elapsed"/> with the input <c>t</c> reading <paramref name="reference"/>: its pixel in the box and its media status.</summary>
        public (SKColor Pixel, ItemMedia? Media) Tick(double elapsed, string reference, string key = "test:t")
        {
            Inputs.Text(key, reference);
            using var s = Runtime.Tick(elapsed, SceneKit.Now);
            using var frame = s.Render();
            return (frame.GetPixel(25, 20), s.Bounds.Single().Media);
        }

        public void Dispose() => Runtime.Dispose();
    }

    [Fact]
    public void A_new_reference_keeps_the_old_picture_until_its_own_arrives()
    {
        var provider = new Keyed { Answers = { [A] = Solid(SKColors.Red) } };
        using var rig = new Rig(provider);
        Assert.Equal((SKColors.Red, (ItemMedia?)null), rig.Tick(0, A));

        // B is loading: A's picture stays, and the item says B is loading. A is asked for without fetching.
        var asked = provider.Asks.Count;
        Assert.Equal((SKColors.Red, new ItemMedia { Code = "media.loading", Message = "Loading." }), rig.Tick(1.5, B));
        Assert.Equal(
            [new MediaRequest(B, 1.5, MediaPlayback.Loop, 0, Live: true), new MediaRequest(A, 1.5, MediaPlayback.Loop, 0, Live: false)],
            provider.Asks.Skip(asked));

        // B arrives: it is drawn, and A is no longer asked for.
        provider.Answers[B] = Solid(SKColors.Blue);
        Assert.Equal((SKColors.Blue, (ItemMedia?)null), rig.Tick(1.6, B));
        asked = provider.Asks.Count;
        Assert.Equal((SKColors.Blue, (ItemMedia?)null), rig.Tick(1.7, B));
        Assert.Equal([new MediaRequest(B, 1.7, MediaPlayback.Loop, 0, Live: true)], provider.Asks.Skip(asked));
    }

    [Fact]
    public void A_reference_taken_while_the_last_still_loads_holds_the_picture_last_drawn()
    {
        var provider = new Keyed { Answers = { [A] = Solid(SKColors.Red) } };
        using var rig = new Rig(provider);
        Assert.Equal(SKColors.Red, rig.Tick(0, A).Pixel);
        Assert.Equal(SKColors.Red, rig.Tick(1.5, B).Pixel);
        // C replaces B, which never arrived: A's picture is still the one held.
        Assert.Equal(SKColors.Red, rig.Tick(2.6, C).Pixel);
        Assert.Contains(new MediaRequest(A, 2.6, MediaPlayback.Loop, 0, Live: false), provider.Asks);
        Assert.DoesNotContain(provider.Asks, a => a.Reference == B && a.Seconds >= 2.6);

        // C fails: the hold ends, and the item draws the placeholder with C's status.
        provider.Answers[C] = new ProvidedMedia(null, new MediaStatus("media.provider", "The server answered 500."));
        var (pixel, media) = rig.Tick(2.7, C);
        Assert.NotEqual(SKColors.Red, pixel);
        Assert.Equal(new ItemMedia { Code = "media.provider", Message = "The server answered 500." }, media);
        Assert.NotEqual(SKColors.Red, rig.Tick(2.8, C).Pixel);
    }

    [Fact]
    public void An_item_takes_at_most_one_new_reference_a_second_and_the_latest_wins()
    {
        // The input names another address every 0.1 s. The first change is taken at once; the next only a second later,
        // and then the newest address, not the one after the first.
        var provider = new Keyed();
        using var rig = new Rig(provider);
        for (var k = 0; k <= 12; k++) rig.Tick(k / 10.0, $"https://example.test/{k}.png");
        string[] expected =
            ["https://example.test/0.png", .. Enumerable.Repeat("https://example.test/1.png", 10), "https://example.test/11.png", "https://example.test/11.png"];
        Assert.Equal(expected, provider.Live);

        // A one-off render asks for the reference as it is (12), and holds nothing.
        var asked = provider.Asks.Count;
        rig.Runtime.Snapshot(SceneKit.Now).Dispose();
        Assert.Equal([new MediaRequest("https://example.test/12.png", 0, MediaPlayback.Loop, 0, Live: false)], provider.Asks.Skip(asked));
    }

    [Fact]
    public void The_sensor_address_example_holds_its_picture_across_a_change()
    {
        var provider = new Keyed { Answers = { [A] = Solid(SKColors.Red) } };
        var scene = SceneKit.Example("scene/valid/web-image-from-sensor.json").GetRawText();
        using var rig = new Rig(provider, scene);
        Assert.Equal(SKColors.Red, rig.Tick(0, A, "test:address").Pixel);
        Assert.Equal(SKColors.Red, rig.Tick(1.5, B, "test:address").Pixel);
        provider.Answers[B] = Solid(SKColors.Blue);
        Assert.Equal(SKColors.Blue, rig.Tick(1.6, B, "test:address").Pixel);
    }

    [Fact]
    public async Task A_web_image_whose_address_changes_keeps_its_picture_until_the_new_one_arrives()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var http = new HttpMediaProviderTests.Rig(async (r, s, t) =>
        {
            if (r.Path == "/b.png") await gate.Task.WaitAsync(t);
            await LocalHttpServer.Answer(s, 200, HttpMediaProviderTests.Png(r.Path == "/a.png" ? SKColors.Lime : SKColors.Blue));
        });
        string a = http.Url("/a.png"), b = http.Url("/b.png");
        using var rig = new Rig(http.Provider, media: http.Cache);

        var first = http.NextAnswer(a);
        rig.Tick(0, a);
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((SKColors.Lime, (ItemMedia?)null), rig.Tick(0.1, a));

        var second = http.NextAnswer(b);
        Assert.Equal((SKColors.Lime, new ItemMedia { Code = "media.loading", Message = "Loading." }), rig.Tick(1.5, b));
        Assert.Equal(SKColors.Lime, rig.Tick(1.6, b).Pixel);
        gate.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((SKColors.Blue, (ItemMedia?)null), rig.Tick(1.7, b));
    }

    [Fact]
    public void Two_provider_sources_in_one_item_take_their_new_references_in_turn()
    {
        // t names another address every 0.1 s; u changes once, at 0.1 s. The item takes one new reference a second
        // between them: t's first change at once, then u's, which has waited longest, then t's newest.
        const string scene = "{'format':'infopanel.scene/1','requires':['media.provider'],'inputs':{'t':{'type':'text'},'u':{'type':'text'}},'nodes':["
            + "{'kind':'media','w':20,'h':30,'source':{'provider':'http','ref':{'op':'input','name':'t','field':'text'}}},"
            + "{'kind':'media','x':30,'w':20,'h':30,'source':{'provider':'http','ref':{'op':'input','name':'u','field':'text'}}}]}";
        var provider = new Keyed();
        using var rig = new Rig(provider, scene);
        for (var k = 0; k <= 30; k++)
        {
            rig.Inputs.Text("test:u", k == 0 ? "https://example.test/u0.png" : "https://example.test/u1.png");
            rig.Tick(k / 10.0, $"https://example.test/t{k}.png");
        }
        // The first tick a reference was asked for live.
        double First(string reference) => provider.Asks.First(a => a.Live && a.Reference == reference).Seconds;
        Assert.DoesNotContain(provider.Asks, a => a.Live && a.Reference == "https://example.test/t11.png");
        Assert.Contains(provider.Asks, a => a.Live && a.Reference == "https://example.test/u1.png");
        Assert.Equal(0.1, First("https://example.test/t1.png"), 6);
        Assert.Equal(1.1, First("https://example.test/u1.png"), 6);
        Assert.Equal(2.1, First("https://example.test/t21.png"), 6);
    }

    [Fact]
    public async Task Two_nodes_on_one_stream_with_different_playback_show_the_same_part_in_a_tick()
    {
        var parts = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        await using var http = new HttpMediaProviderTests.Rig(async (r, s, t) =>
        {
            await LocalHttpServer.Head(s, 200, "multipart/x-mixed-replace; boundary=frame");
            await foreach (var part in parts.Reader.ReadAllAsync(t)) await MjpegStreamTests.Part(s, part, t);
        });
        var url = http.Url("/video.mjpg");
        // One stream, shown by two nodes: the second at twice the speed, so each asks with its own time.
        var scene = "{'format':'infopanel.scene/1','requires':['media.provider'],'nodes':["
            + "{'kind':'media','w':20,'h':30,'source':{'provider':'http','ref':'" + url + "'}},"
            + "{'kind':'media','x':30,'w':20,'h':30,'speed':2,'source':{'provider':'http','ref':'" + url + "'}}]}";
        using var rig = new Rig(http.Provider, scene, http.Cache);

        Task Part(byte[] jpeg)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(string a)
            {
                http.Provider.PartReceived -= Handler;
                done.TrySetResult();
            }
            http.Provider.PartReceived += Handler;
            parts.Writer.TryWrite(jpeg);
            return done.Task;
        }

        (string, string) Tick(double elapsed)
        {
            using var s = rig.Runtime.Tick(elapsed, SceneKit.Now);
            using var frame = s.Render();
            static string Colour(SKColor p) => (p.Red > 200, p.Green > 200, p.Blue > 200) switch
            {
                (true, false, false) => "red",
                (false, true, false) => "green",
                _ => $"{p}",
            };
            return (Colour(frame.GetPixel(15, 20)), Colour(frame.GetPixel(45, 20)));
        }

        rig.Tick(0, "");
        await Part(MjpegStreamTests.Jpeg(SKColors.Red)).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The first node's ask takes the red part and decodes it; before the second node asks, a green part arrives.
        var decoded = 0;
        var green = MjpegStreamTests.Jpeg(SKColors.Lime);
        void Arrive(string hash)
        {
            if (Interlocked.Increment(ref decoded) == 1) Assert.True(Part(green).Wait(TimeSpan.FromSeconds(10), Ct));
        }
        http.Cache.FrameDecoded += Arrive;
        Assert.Equal(("red", "red"), Tick(0.1));
        Assert.Equal(1, decoded);
        // The next tick takes the green part, for both.
        Assert.Equal(("green", "green"), Tick(0.2));
        Assert.Equal(2, decoded);
        http.Cache.FrameDecoded -= Arrive;
    }
}
