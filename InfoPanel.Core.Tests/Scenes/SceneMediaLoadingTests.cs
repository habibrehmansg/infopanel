using System.Diagnostics;
using System.Text;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A natural-size picture whose decode hasn't finished is laid out at the size its header gives, the size its decoded
/// frame has, so its bounds don't jump when the decode lands (spec/docs/smart-nodes.md, media). The decode is held back
/// by a gate on the store's reads, never by timing: the engine's own wiring (a blob stored through the workspace, the
/// media cache told of it) on a cache with a decode worker.
/// </summary>
public sealed class SceneMediaLoadingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Workspace _workspace;
    private readonly GatedReads _store;
    private readonly MediaSampleCache _media;
    private readonly int _renderThread = Environment.CurrentManagedThreadId;

    public SceneMediaLoadingTests() : this(new MediaSampleCacheOptions { Workers = 1 })
    {
    }

    private SceneMediaLoadingTests(MediaSampleCacheOptions cache)
    {
        var paths = new DataPaths(_temp.Path);
        var documents = new DocumentStore(paths);
        var blobs = new BlobStore(paths);
        documents.Open();
        blobs.Open();
        _workspace = new Workspace(documents, blobs, new WorkspaceOptions { Instance = DocKit.Instance, HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes) });
        _store = new GatedReads(new BlobMediaStore(_workspace.Blobs));
        _media = new MediaSampleCache(_store, cache);
        // As the engine wires them: a blob stored through the workspace is made known to the media cache.
        _workspace.BlobStored += _media.Retry;
    }

    public void Dispose()
    {
        _store.OpenAll();
        _media.Dispose();
        _workspace.Dispose();
        _temp.Dispose();
    }

    /// <summary>Pictures whose decoded size is checked: their bytes and the size the decoder gives them.</summary>
    public static TheoryData<string, float, float> Pictures => new()
    {
        { "png 40 x 30", 40, 30 },
        // A GIF's canvas is its logical screen widened to its first frame at its offset (10 x 10 at (5, 5): 15 x 15).
        { "gif first frame", 15, 15 },
        // An SVG's written size, each side rounded up to whole pixels.
        { "svg 10.25 x 20.5", 11, 21 },
        // A viewBox side the rebuilt document rounds to 10: drawn at 10, though the size as written rounds up to 11.
        { "svg viewBox 10.00000001 x 5", 10, 5 },
    };

    private static byte[] Picture(string name) => name switch
    {
        "png 40 x 30" => Png(40, 30, SKColors.Red),
        "gif first frame" => TestGif.Build(10, 10, new TestGif.Frame(5, 5, 10, 10, 1), new TestGif.Frame(0, 0, 4, 4, 2)),
        "svg 10.25 x 20.5" => Svg("width='10.25' height='20.5'"),
        "svg viewBox 10.00000001 x 5" => Svg("viewBox='0 0 10.00000001 5'"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Pictures))]
    public void A_loading_picture_is_laid_out_at_the_size_it_is_drawn_at_once_decoded(string picture, float w, float h)
    {
        var id = _workspace.PutBlob(Picture(picture)).Info.Id;
        using var runtime = Runtime(Media(id, x: 100, y: 100));

        using (var loading = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Contains(loading.Diagnostics, d => d.Code == "media.loading");
            Assert.Equal(0, loading.SampleCount);
            AssertFrame(loading, "item", 100, 100, w, h);
        }

        Decode(id);
        using var loaded = runtime.Tick(0.1, SceneKit.Now);
        Assert.Equal(1, loaded.SampleCount);
        AssertFrame(loaded, "item", 100, 100, w, h);
    }

    [Fact]
    public void A_loading_picture_with_one_side_given_keeps_its_header_proportions()
    {
        var id = _workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        using var runtime = Runtime(Media(id, w: 50));
        using (var loading = runtime.Tick(0, SceneKit.Now))
            AssertFrame(loading, "item", 0, 0, 50, 37.5);
        Decode(id);
        using var loaded = runtime.Tick(0.1, SceneKit.Now);
        Assert.Equal(1, loaded.SampleCount);
        AssertFrame(loaded, "item", 0, 0, 50, 37.5);
    }

    [Fact]
    public void A_loading_picture_with_its_size_given_keeps_that_size()
    {
        var id = _workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        using var runtime = Runtime(Media(id, w: 50, h: 60));
        using (var loading = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Contains(loading.Diagnostics, d => d.Code == "media.loading");
            AssertFrame(loading, "item", 0, 0, 50, 60);
        }
        Decode(id);
        using var loaded = runtime.Tick(0.1, SceneKit.Now);
        AssertFrame(loaded, "item", 0, 0, 50, 60);
    }

    [Fact]
    public void Groups_around_a_loading_picture_have_their_final_size_on_the_first_frame()
    {
        var id = _workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        // A document group around a scene item whose picture sits in a translated scene group beside a 1 x 1 mark.
        var document = SceneKit.Json("{'format':'infopanel.document/1','id':'3dd19c0e-675a-4591-b096-83417853861f','profile':{'name':'L','width':400,'height':300},'items':["
            + "{'kind':'group','id':'g','placement':{'x':100,'y':100},'children':["
            + "{'kind':'scene','id':'item','placement':{'x':10,'y':20},'scene':{'format':'infopanel.scene/1','nodes':["
            + "{'kind':'rect','w':1,'h':1,'fill':'#ffffff'},"
            + $"{{'kind':'group','transform':[{{'type':'translate','x':5,'y':5}}],'children':[{{'kind':'media','source':{{'asset':'{id}'}}}}]}}]}}}}]}}]}}");
        using var runtime = Runtime(document);
        using (var loading = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Contains(loading.Diagnostics, d => d.Code == "media.loading");
            AssertFrame(loading, "item", 110, 120, 45, 35);
            AssertFrame(loading, "g", 110, 120, 45, 35);
        }
        Decode(id);
        using var loaded = runtime.Tick(0.1, SceneKit.Now);
        Assert.Equal(1, loaded.SampleCount);
        AssertFrame(loaded, "item", 110, 120, 45, 35);
        AssertFrame(loaded, "g", 110, 120, 45, 35);
    }

    [Fact]
    public void Media_with_no_size_known_is_100_by_100_whether_missing_refused_or_not_yet_read()
    {
        // No such asset; stored bytes that aren't media; a picture over the media limits: none has a header size.
        var missing = BlobStore.IdOf(Png(40, 30, SKColors.Blue));
        var notMedia = _workspace.PutBlob(Encoding.UTF8.GetBytes("not a picture")).Info.Id;
        var refused = _workspace.PutBlob(Png(9000, 1, SKColors.Red)).Info.Id;
        foreach (var id in new[] { missing, notMedia, refused })
        {
            using var runtime = Runtime(Media(id));
            using (var first = runtime.Tick(0, SceneKit.Now))
                AssertFrame(first, "item", 0, 0, 100, 100);
        }
    }

    [Fact]
    public void Media_the_cache_refuses_after_its_upload_is_100_by_100()
    {
        // Stored and inspected by the workspace (40 x 30), then refused by the media cache's own check (a cache that
        // decodes on the calling thread, with a smaller side limit): the size the upload saw isn't used for media that
        // won't be drawn.
        using var kit = new SceneMediaLoadingTests(new MediaSampleCacheOptions { Synchronous = true, Limits = SpecLimits.Default with { MediaSide = 20 } });
        kit._store.OpenAll();
        var id = kit._workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        using var runtime = kit.Runtime(Media(id));
        using var refused = runtime.Tick(0, SceneKit.Now);
        Assert.Contains(refused.Diagnostics, d => d.Code == "media.missing");
        AssertFrame(refused, "item", 0, 0, 100, 100);
    }

    [Fact]
    public void A_stored_picture_not_yet_read_is_100_by_100_until_a_worker_reads_it_and_nothing_reads_it_on_the_render_thread()
    {
        // Stored before this run (no upload told the cache): its size is known only once a decode worker has read it.
        var bytes = Png(40, 30, SKColors.Red);
        var id = _workspace.Blobs.Put(bytes);
        using var runtime = Runtime(Media(id));
        using (var first = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Contains(first.Diagnostics, d => d.Code == "media.loading");
            AssertFrame(first, "item", 0, 0, 100, 100);
        }
        Assert.True(_store.Entered.Wait(TimeSpan.FromSeconds(10)), "no worker read the asset");
        Assert.DoesNotContain(_renderThread, _store.Readers);
        Decode(id);
        using var loaded = runtime.Tick(0.1, SceneKit.Now);
        AssertFrame(loaded, "item", 0, 0, 40, 30);
        Assert.DoesNotContain(_renderThread, _store.Readers);
    }

    [Fact]
    public void An_uploaded_picture_is_sized_without_reading_it_on_the_render_thread()
    {
        var id = _workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        using var runtime = Runtime(Media(id));
        using (var first = runtime.Tick(0, SceneKit.Now))
            AssertFrame(first, "item", 0, 0, 40, 30);
        Assert.DoesNotContain(_renderThread, _store.Readers);
    }

    [Fact]
    public void Uploads_with_nothing_drawn_keep_at_most_the_cap_of_sizes_and_no_asset()
    {
        // Ten pictures stored, none drawn, with room for four sizes: the cache holds no asset for them and four sizes,
        // the most recently stored; the oldest has none left.
        using var kit = new SceneMediaLoadingTests(new MediaSampleCacheOptions { Workers = 1, SizesKept = 4 });
        var ids = Enumerable.Range(1, 10).Select(i => kit._workspace.PutBlob(Png(i, 1, SKColors.Red)).Info.Id).ToList();
        Assert.Equal(0, kit._media.AssetCount);
        Assert.Equal(4, kit._media.KnownSizes);
        using (var newest = kit.Runtime(Media(ids[^1])))
        using (var first = newest.Tick(0, SceneKit.Now))
            AssertFrame(first, "item", 0, 0, 10, 1);
        using (var oldest = kit.Runtime(Media(ids[0])))
        using (var first = oldest.Tick(0, SceneKit.Now))
            AssertFrame(first, "item", 0, 0, 100, 100);
        Assert.Equal(4, kit._media.KnownSizes);
    }

    [Fact]
    public void A_sequence_picture_not_drawn_past_the_idle_sweep_keeps_its_size()
    {
        // Two pictures in a sequence, index held at 0 past the idle sweep (the cache's clock moved 40 s, the decodes
        // held), then index 1: picture 1, unused all that time and still not decoded, has its own size on its first frame.
        var time = new ManualTimeProvider(SceneKit.Now);
        using var kit = new SceneMediaLoadingTests(new MediaSampleCacheOptions { Workers = 1, Time = time });
        var a = kit._workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        var b = kit._workspace.PutBlob(Png(80, 60, SKColors.Blue)).Info.Id;
        using (var held = kit.Runtime(Sequence(0, crossfade: false, a, b)))
        {
            using (var first = held.Tick(0, SceneKit.Now))
                AssertFrame(first, "item", 0, 0, 40, 30);
            time.Advance(TimeSpan.FromSeconds(40));
            using (var later = held.Tick(40, SceneKit.Now))
                AssertFrame(later, "item", 0, 0, 40, 30);
        }
        using var moved = kit.Runtime(Sequence(1, crossfade: false, a, b));
        using var shown = moved.Tick(0, SceneKit.Now);
        Assert.Contains(shown.Diagnostics, d => d.Code == "media.loading");
        Assert.Equal(0, shown.SampleCount);
        AssertFrame(shown, "item", 0, 0, 80, 60);
    }

    [Fact]
    public void A_picture_whose_decode_failed_is_100_by_100_again_after_the_idle_sweep_dropped_it()
    {
        // A PNG whose header says 40 x 30 and that has no image data: the upload's inspection sizes it, the decode fails.
        // The size is forgotten with the failure, so once the idle sweep has dropped the failed asset a lookup with the
        // decode held back (it would fail again) still lays it out at 100 x 100, not at the header's size.
        var time = new ManualTimeProvider(SceneKit.Now);
        using var kit = new SceneMediaLoadingTests(new MediaSampleCacheOptions { Workers = 2, Time = time });
        var bad = kit._workspace.PutBlob(HeaderOnlyPng(40, 30)).Info.Id;
        var other = kit._workspace.PutBlob(Png(80, 60, SKColors.Blue)).Info.Id;
        using (var runtime = kit.Runtime(Media(bad)))
        {
            using (var loading = runtime.Tick(0, SceneKit.Now))
                AssertFrame(loading, "item", 0, 0, 40, 30);
            kit._store.Open(bad);
            for (var wait = Stopwatch.StartNew(); ; Thread.Yield())
            {
                using var tick = runtime.Tick(0.1, SceneKit.Now);
                if (tick.Diagnostics.Any(d => d.Code == "media.missing")) break;
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "the decode did not fail");
            }
            using var failed = runtime.Tick(0.2, SceneKit.Now);
            AssertFrame(failed, "item", 0, 0, 100, 100);
        }
        // Past the idle sweep, triggered by another asset's lookup; the bad picture's decode is held again.
        time.Advance(TimeSpan.FromSeconds(40));
        using (var sweeper = kit.Runtime(Media(other)))
        using (var swept = sweeper.Tick(40, SceneKit.Now))
            Assert.Contains(swept.Diagnostics, d => d.Code == "media.loading");
        Assert.Equal(1, kit._media.AssetCount);
        kit._store.Close(bad);
        using var again = kit.Runtime(Media(bad));
        using var shown = again.Tick(0, SceneKit.Now);
        Assert.Contains(shown.Diagnostics, d => d.Code == "media.loading");
        AssertFrame(shown, "item", 0, 0, 100, 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_crossfade_keeps_its_bounds_whichever_picture_decodes_first(bool upperFirst)
    {
        // Halfway between a 40 x 30 picture and an 80 x 60 one: laid out at the lower one's size from the first frame,
        // whichever decode lands first (two workers, each decode held at its own gate).
        using var kit = new SceneMediaLoadingTests(new MediaSampleCacheOptions { Workers = 2 });
        var lower = kit._workspace.PutBlob(Png(40, 30, SKColors.Red)).Info.Id;
        var upper = kit._workspace.PutBlob(Png(80, 60, SKColors.Blue)).Info.Id;
        using var runtime = kit.Runtime(Sequence(0.5, crossfade: true, lower, upper));
        using (var loading = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Equal(0, loading.SampleCount);
            AssertFrame(loading, "item", 0, 0, 40, 30);
        }
        kit.Decode(upperFirst ? upper : lower, only: true);
        using (var one = runtime.Tick(0.1, SceneKit.Now))
        {
            Assert.Equal(1, one.SampleCount);
            AssertFrame(one, "item", 0, 0, 40, 30);
        }
        kit.Decode(upperFirst ? lower : upper, only: true);
        using var both = runtime.Tick(0.2, SceneKit.Now);
        Assert.Equal(2, both.SampleCount);
        AssertFrame(both, "item", 0, 0, 40, 30);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private SceneRuntime Runtime(System.Text.Json.JsonElement document)
    {
        var runtime = new SceneRuntime(SceneKit.Options(media: _media));
        runtime.Load(document);
        return runtime;
    }

    /// <summary>Lets the workers read and decode (every asset, or only <paramref name="id"/>), and waits until <paramref name="id"/> is decoded.</summary>
    private void Decode(string id, bool only = false)
    {
        using var decoded = new ManualResetEventSlim();
        void Done(string hash)
        {
            if (hash == id) decoded.Set();
        }
        _media.FrameDecoded += Done;
        try
        {
            if (only) _store.Open(id);
            else _store.OpenAll();
            Assert.True(decoded.Wait(TimeSpan.FromSeconds(10)), "the asset was not decoded");
        }
        finally
        {
            _media.FrameDecoded -= Done;
        }
    }

    private static System.Text.Json.JsonElement Media(string id, double x = 0, double y = 0, double? w = null, double? h = null)
    {
        var size = (w is { } ww ? $",'w':{ww}" : "") + (h is { } hh ? $",'h':{hh}" : "");
        return SceneKit.Doc(SceneKit.Json($"{{'format':'infopanel.scene/1','nodes':[{{'kind':'media','source':{{'asset':'{id}'}}{size}}}]}}"), 400, 300, x, y);
    }

    private static System.Text.Json.JsonElement Sequence(double index, bool crossfade, params string[] ids)
    {
        var assets = string.Join(",", ids.Select(id => $"{{'asset':'{id}'}}"));
        return SceneKit.Doc(SceneKit.Json($"{{'format':'infopanel.scene/1','nodes':[{{'kind':'media','source':{{'sequence':[{assets}],'index':{index.ToString(System.Globalization.CultureInfo.InvariantCulture)},'crossfade':{(crossfade ? "true" : "false")}}}}}]}}"), 400, 300);
    }

    /// <summary>The item's reported frame is (x, y, w, h), to 1/1000 of a pixel.</summary>
    private static void AssertFrame(TickSnapshot snapshot, string id, double x, double y, double w, double h)
    {
        var b = snapshot.Bounds.Single(i => i.Id == id);
        Assert.Null(b.Problem);
        Assert.Equal($"{x:0.###} {y:0.###} {w:0.###} {h:0.###}", $"{b.Frame.X:0.###} {b.Frame.Y:0.###} {b.Frame.W:0.###} {b.Frame.H:0.###}");
    }

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A PNG signature and header for a <paramref name="w"/> x <paramref name="h"/> image, and no image data.</summary>
    private static byte[] HeaderOnlyPng(int w, int h) => Png(w, h, SKColors.Red).AsSpan(0, 33).ToArray();

    private static byte[] Svg(string size) =>
        Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' {size}><rect width='5' height='5' fill='#ff0000'/></svg>");

    /// <summary>
    /// The blob store's media reads, each held at its asset's gate until that gate (or every gate) opens; records the
    /// threads that read.
    /// </summary>
    private sealed class GatedReads(IMediaAssetStore inner) : IMediaAssetStore
    {
        public ManualResetEventSlim Entered { get; } = new();
        private readonly Dictionary<string, ManualResetEventSlim> _gates = new(StringComparer.Ordinal);
        private readonly List<int> _readers = [];
        private bool _allOpen;

        public IReadOnlyList<int> Readers
        {
            get
            {
                lock (_readers) return [.. _readers];
            }
        }

        public void OpenAll()
        {
            lock (_gates)
            {
                _allOpen = true;
                foreach (var gate in _gates.Values) gate.Set();
            }
        }

        public void Open(string hash) => GateOf(hash).Set();

        /// <summary>Holds the reads of <paramref name="hash"/> again.</summary>
        public void Close(string hash) => GateOf(hash).Reset();

        private ManualResetEventSlim GateOf(string hash)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(hash, out var gate)) _gates[hash] = gate = new ManualResetEventSlim(_allOpen);
                return gate;
            }
        }

        public byte[]? Read(string hash)
        {
            lock (_readers) _readers.Add(Environment.CurrentManagedThreadId);
            Entered.Set();
            GateOf(hash).Wait(TimeSpan.FromSeconds(20));
            return inner.Read(hash);
        }
    }
}
