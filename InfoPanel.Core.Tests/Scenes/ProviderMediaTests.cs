using InfoPanel.Core.Tests.Images;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Provider media in the scene runtime (spec/docs/providers.md): what a tick asks a provider for (the reference, the
/// source's refresh, whether the ask is live), the status an item reports in its bounds (<c>itemBounds[].media</c>),
/// and the last good web image kept across ticks.
/// </summary>
public sealed class ProviderMediaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Scene(string source) =>
        "{'format':'infopanel.scene/1','requires':['media.provider'],'nodes':[{'kind':'media','w':40,'h':30,'source':" + source + "}]}";

    /// <summary>A provider that records each ask and answers what the test sets.</summary>
    private sealed class Recording : IMediaProvider
    {
        public readonly List<MediaRequest> Asks = [];

        /// <summary>The asks that shared an answer already given in the tick, told for their refresh.</summary>
        public readonly List<MediaRequest> Also = [];
        public ProvidedMedia Answer;

        public ProvidedMedia Get(in MediaRequest request)
        {
            lock (Asks) Asks.Add(request);
            return new ProvidedMedia(Answer.Sample?.Retain(), Answer.Status);
        }

        public void Asked(in MediaRequest request)
        {
            lock (Also) Also.Add(request);
        }
    }

    private static SceneRuntime Runtime(string scene, IMediaProvider provider, MediaSampleCache? media = null)
    {
        var runtime = new SceneRuntime(SceneKit.Options(media: media, providers: new Dictionary<string, IMediaProvider> { ["http"] = provider }));
        runtime.Load(SceneKit.Doc(SceneKit.Json(scene), 60, 40, 5, 5));
        return runtime;
    }

    [Fact]
    public void A_tick_asks_with_the_sources_refresh_and_a_one_off_render_asks_without_fetching()
    {
        // Two media nodes: one with a refresh of 600 s, one without (0).
        var provider = new Recording();
        using var runtime = Runtime("{'format':'infopanel.scene/1','requires':['media.provider'],'nodes':["
            + "{'kind':'media','w':20,'h':20,'source':{'provider':'http','ref':'https://example.test/radar.png','refresh':600}},"
            + "{'kind':'media','x':30,'w':20,'h':20,'source':{'provider':'http','ref':'https://example.test/logo.png'}}]}", provider);
        runtime.Tick(0.5, SceneKit.Now).Dispose();
        runtime.Snapshot(SceneKit.Now).Dispose();
        Assert.Equal(
            [new MediaRequest("https://example.test/radar.png", 0.5, MediaPlayback.Loop, 600, Live: true),
             new MediaRequest("https://example.test/logo.png", 0.5, MediaPlayback.Loop, 0, Live: true),
             new MediaRequest("https://example.test/radar.png", 0, MediaPlayback.Loop, 600, Live: false),
             new MediaRequest("https://example.test/logo.png", 0, MediaPlayback.Loop, 0, Live: false)],
            provider.Asks);
    }

    [Fact]
    public void Items_sharing_a_reference_share_one_answer_and_the_provider_hears_every_refresh()
    {
        // Four nodes on one reference (refresh 0, 600, 600 and 60) and one on another: one answer for each reference in
        // the tick, and each other refresh told once; a one-off render tells none.
        var provider = new Recording();
        static string Node(int x, string reference, int? refresh) =>
            "{'kind':'media','x':" + x + ",'w':10,'h':10,'source':{'provider':'http','ref':'" + reference + "'" + (refresh is { } r ? ",'refresh':" + r : "") + "}}";
        using var runtime = Runtime("{'format':'infopanel.scene/1','requires':['media.provider'],'nodes':["
            + string.Join(",", Node(0, "https://example.test/a.png", null), Node(10, "https://example.test/a.png", 600), Node(20, "https://example.test/a.png", 600),
                Node(30, "https://example.test/a.png", 60), Node(40, "https://example.test/b.png", 5)) + "]}", provider);
        runtime.Tick(0.5, SceneKit.Now).Dispose();
        Assert.Equal(
            [new MediaRequest("https://example.test/a.png", 0.5, MediaPlayback.Loop, 0, Live: true),
             new MediaRequest("https://example.test/b.png", 0.5, MediaPlayback.Loop, 5, Live: true)],
            provider.Asks);
        Assert.Equal(
            [new MediaRequest("https://example.test/a.png", 0.5, MediaPlayback.Loop, 600, Live: true),
             new MediaRequest("https://example.test/a.png", 0.5, MediaPlayback.Loop, 60, Live: true)],
            provider.Also);
        runtime.Snapshot(SceneKit.Now).Dispose();
        Assert.Equal(4, provider.Asks.Count);
        Assert.Equal(2, provider.Also.Count);
    }

    [Fact]
    public void The_providers_status_is_the_items_media_in_its_bounds()
    {
        var provider = new Recording { Answer = new ProvidedMedia(null, new MediaStatus("media.address", "192.168.1.20 is on your local network.")) };
        using var runtime = Runtime(Scene("{'provider':'http','ref':'http://192.168.1.20/cam.jpg'}"), provider);
        using var s = runtime.Tick(0, SceneKit.Now);
        var bounds = s.Bounds.Single();
        Assert.Null(bounds.Problem);
        Assert.Equal(new ItemMedia { Code = "media.address", Message = "192.168.1.20 is on your local network." }, bounds.Media);
        Assert.Contains(s.Diagnostics, d => d.Code == "media.address");

        // An item drawn as a placeholder (here unsupported: an engine without media.provider) reports its problem and
        // no media status.
        using var placeholder = new SceneRuntime(new SceneRuntimeOptions
        {
            Inputs = new FakeSceneInputs(),
            Providers = new Dictionary<string, IMediaProvider> { ["http"] = provider },
            Support = EngineSupport.Base,
            LocalZone = TimeZoneInfo.Utc,
            Locale = RegionalFormat.Invariant,
        });
        placeholder.Load(SceneKit.Doc(SceneKit.Json(Scene("{'provider':'http','ref':'http://192.168.1.20/cam.jpg'}")), 60, 40));
        using var p = placeholder.Tick(0, SceneKit.Now);
        Assert.Equal("unsupported", p.Bounds.Single().Problem);
        Assert.Null(p.Bounds.Single().Media);
    }

    [Fact]
    public void A_status_past_the_frame_headers_bounds_is_cut_where_it_is_reported()
    {
        // A provider's code of 100 characters and a message of 2,000 with a surrogate pair at the cut: at most 64 and
        // 1,024 Unicode scalar values, the message ending in "…", the pair never split.
        var message = new string('m', 1022) + "😀" + new string('z', 977);
        var provider = new Recording { Answer = new ProvidedMedia(null, new MediaStatus(new string('c', 100), message)) };
        using var runtime = Runtime(Scene("{'provider':'http','ref':'https://example.test/radar.png'}"), provider);
        using var s = runtime.Tick(0, SceneKit.Now);
        var media = s.Bounds.Single().Media!;
        Assert.Equal(new string('c', 63) + "…", media.Code);
        Assert.Equal(new string('m', 1022) + "😀…", media.Message);
        Assert.Equal(1024, media.Message.EnumerateRunes().Count());
        Assert.All(s.Diagnostics, d => Assert.True(d.Message.EnumerateRunes().Count() <= 1024));
    }

    [Fact]
    public void Media_with_no_answer_yet_is_loading_and_media_shown_without_a_problem_has_no_status()
    {
        var provider = new Recording();
        using var runtime = Runtime(Scene("{'provider':'http','ref':'https://example.test/radar.png'}"), provider);
        using (var s = runtime.Tick(0, SceneKit.Now))
            Assert.Equal("media.loading", s.Bounds.Single().Media?.Code);

        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(SKColors.Red);
        provider.Answer = new ProvidedMedia(MediaSample.FromImage(SKImage.FromBitmap(bitmap)));
        using (var s = runtime.Tick(1, SceneKit.Now))
        {
            Assert.Null(s.Bounds.Single().Media);
            using var frame = s.Render();
            Assert.Equal(SKColors.Red, frame.GetPixel(25, 20));
        }
    }

    [Fact]
    public void A_provider_that_isnt_configured_and_an_asset_that_isnt_stored_are_reported()
    {
        var provider = new Recording();
        using (var runtime = Runtime(Scene("{'provider':'rtsp','ref':'rtsp://camera.test/stream'}"), provider))
        using (var s = runtime.Tick(0, SceneKit.Now))
            Assert.Equal(new ItemMedia { Code = "media.provider", Message = "No media provider 'rtsp' is configured." }, s.Bounds.Single().Media);

        using var media = SceneKit.SyncMedia();
        var missing = "sha256:" + new string('0', 64);
        using var assets = new SceneRuntime(SceneKit.Options(media: media));
        assets.Load(SceneKit.Doc(SceneKit.Json("{'format':'infopanel.scene/1','nodes':[{'kind':'media','w':40,'h':30,'source':{'asset':'" + missing + "'}}]}"), 60, 40));
        using var t = assets.Tick(0, SceneKit.Now);
        Assert.Equal("media.missing", t.Bounds.Single().Media?.Code);
    }

    [Fact]
    public async Task A_web_images_last_good_image_stays_across_ticks_through_a_failed_refresh()
    {
        var count = 0;
        await using var rig = new HttpMediaProviderTests.Rig((r, s, t) => Interlocked.Increment(ref count) == 1
            ? LocalHttpServer.Answer(s, 200, HttpMediaProviderTests.Png(SKColors.Lime))
            : LocalHttpServer.Answer(s, 500, contentType: null));
        var url = rig.Url("/radar.png");
        using var runtime = new SceneRuntime(SceneKit.Options(media: rig.Cache, providers: new Dictionary<string, IMediaProvider> { ["http"] = rig.Provider }));
        runtime.Load(SceneKit.Doc(SceneKit.Json(Scene("{'provider':'http','ref':'" + url + "','refresh':1}")), 60, 40, 5, 5));

        var first = rig.NextAnswer(url);
        using (var s = runtime.Tick(0, SceneKit.Now))
            Assert.Equal("media.loading", s.Bounds.Single().Media?.Code);
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var s = runtime.Tick(0.1, SceneKit.Now))
        {
            Assert.Null(s.Bounds.Single().Media);
            using var frame = s.Render();
            Assert.Equal(SKColors.Lime, frame.GetPixel(25, 20));
        }

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var second = rig.NextAnswer(url);
        runtime.Tick(1.1, SceneKit.Now).Dispose();
        await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var s = runtime.Tick(1.2, SceneKit.Now))
        {
            Assert.Equal(new ItemMedia { Code = "media.provider", Message = "The server answered 500." }, s.Bounds.Single().Media);
            using var frame = s.Render();
            Assert.Equal(SKColors.Lime, frame.GetPixel(25, 20));
        }
    }
}
