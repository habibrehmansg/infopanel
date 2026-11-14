using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Output;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

public class LoopRecorderTests
{
    /// <summary>A bar as wide as the reading, on a 120 × 20 canvas; input <c>v</c> is bound to key <c>test:v</c>.</summary>
    private static readonly System.Text.Json.JsonElement Bar = SceneKit.Doc(SceneKit.Json(
        "{'format':'infopanel.scene/1','inputs':{'v':{'type':'number'}},'nodes':["
        + "{'kind':'rect','w':{'op':'input','name':'v'},'h':20,'fill':'#ffffff'}]}"), 120, 20);

    private static SceneReading Number(double v) =>
        new() { Status = SceneInputStatus.Live, Now = v, Min = v, Max = v, Avg = v, Unit = "" };

    private static LoopState State(int frames, double? v) =>
        new(frames, v is { } x ? new Dictionary<string, LoopReading> { ["test:v"] = new("number", Number(x)) } : new Dictionary<string, LoopReading>());

    private static RecordedLoop Record() => LoopRecorder.Record(Bar, [State(3, 30), State(2, 60), State(2, 100), State(3, null)],
        inputs => SceneKit.Options(inputs), SceneKit.Now);

    /// <summary>How many of the row's pixels are white.</summary>
    private static int Lit(SKBitmap bitmap) =>
        Enumerable.Range(0, bitmap.Width).Count(x => bitmap.GetPixel(x, 10) == SKColors.White);

    [Fact]
    public void Record_DrawsEachStatesReadings_InOrder_AndMarksEachStatesFirstFrame()
    {
        using var loop = Record();
        Assert.Equal(10, loop.Frames.Count);
        Assert.Equal([0, 3, 5, 7], loop.StateStarts);
        Assert.All(loop.Frames, f => Assert.Equal(TimeSpan.FromMilliseconds(100), f.Duration));
        Assert.All(loop.Frames, f => Assert.Equal(new SKSizeI(120, 20), new SKSizeI(f.Bitmap.Width, f.Bitmap.Height)));
        Assert.Equal([30, 30, 30, 60, 60, 100, 100, 0, 0, 0], loop.Frames.Select(f => Lit(f.Bitmap)).ToArray());
    }

    [Fact]
    public void Record_IsStateless_TheSameCallGivesTheSamePixels()
    {
        using var a = Record();
        using var b = Record();
        Assert.Equal(a.Frames.Count, b.Frames.Count);
        for (var i = 0; i < a.Frames.Count; i++)
            Assert.True(a.Frames[i].Bitmap.GetPixelSpan().SequenceEqual(b.Frames[i].Bitmap.GetPixelSpan()), $"frame {i}");
    }

    /// <summary>One stored asset, counting its reads.</summary>
    private sealed class OneAsset(string hash, byte[] bytes) : InfoPanel.Images.IMediaAssetStore
    {
        public int Reads;

        public byte[]? Read(string h)
        {
            if (h != hash) return null;
            Interlocked.Increment(ref Reads);
            return bytes;
        }

        public long? Size(string h) => h == hash ? bytes.Length : null;
    }

    /// <summary>Stored assets by hash, counting whole reads and opened streams.</summary>
    private sealed class Assets : InfoPanel.Images.IMediaAssetStore
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        public int Reads;
        public int Opens;

        public string Add(byte[] bytes)
        {
            var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            _blobs[hash] = bytes;
            return hash;
        }

        public byte[]? Read(string hash)
        {
            Interlocked.Increment(ref Reads);
            return _blobs.GetValueOrDefault(hash);
        }

        public long? Size(string hash) => _blobs.TryGetValue(hash, out var b) ? b.Length : null;

        public Stream? Open(string hash)
        {
            Interlocked.Increment(ref Opens);
            return _blobs.TryGetValue(hash, out var b) ? new MemoryStream(b, writable: false) : null;
        }
    }

    /// <summary>A solid picture of <paramref name="side"/> x <paramref name="side"/> pixels, as a PNG.</summary>
    private static byte[] SolidPng(int side, SKColor color)
    {
        using var bitmap = new SKBitmap(side, side);
        bitmap.Erase(color);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 1));
        return data.ToArray();
    }

    /// <summary>A 1 x 1 PNG whose header says it is <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] PngSaying(int width, int height)
    {
        var png = Png();
        // The header chunk follows the 8-byte signature: length, "IHDR", width, height, ..., its CRC over type and data.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void ARecordingsMedia_PlaysNoVideo_OpensNoStoredVideo_AndStartsNoSession()
    {
        var store = new Assets();
        var video = store.Add(VideoFixtures.Read("clip-h264.mp4"));
        using var media = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default);
        var lookup = media.Cache.GetVideo(video, 0.35, InfoPanel.Images.MediaPlayback.Loop, 16, 16);
        lookup.Sample?.Release();
        // It draws as a video this engine can't play: nothing opened, read or decoded.
        Assert.Equal(InfoPanel.Images.MediaState.Failed, lookup.State);
        Assert.StartsWith("media.video", lookup.Problem);
        Assert.Equal((0, 0, 0, 0), (media.Cache.VideoSessions, store.Opens, store.Reads, media.Opened));
    }

    [Fact]
    public void APictureNamedAsAVideoFirst_IsReadOnlyAsAPicture_AndChargedThen()
    {
        var store = new Assets();
        var png = SolidPng(8, SKColors.Teal);
        var hash = store.Add(png);
        using var media = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default);
        var asVideo = media.Cache.GetVideo(hash, 0, InfoPanel.Images.MediaPlayback.Loop, 8, 8);
        Assert.Equal(InfoPanel.Images.MediaState.Failed, asVideo.State);
        Assert.Equal((0, 0, 0L), (store.Opens, store.Reads, media.StoredBytes));

        var asPicture = media.Cache.Get(hash, 0, InfoPanel.Images.MediaPlayback.Loop);
        Assert.Equal(InfoPanel.Images.MediaState.Ready, asPicture.State);
        asPicture.Sample!.Release();
        Assert.Equal((0, 1, (long)png.Length, 1), (store.Opens, store.Reads, media.StoredBytes, media.Read));
    }

    [Fact]
    public void LargePictures_AreReservedFromTheirHeadersBeforeTheyDecode_AndThosePastTheBudgetAreNotDecoded()
    {
        var store = new Assets();
        var colours = Enumerable.Range(0, LoopMedia.MaxPictures).Select(i => new SKColor((byte)(i * 7), (byte)(255 - i * 7), (byte)i)).ToList();
        var hashes = colours.Select(c => store.Add(SolidPng(4096, c))).ToList();
        using var media = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default);
        var ready = 0;
        foreach (var hash in hashes)
        {
            var lookup = media.Cache.Get(hash, 0, InfoPanel.Images.MediaPlayback.Loop);
            // Kept, as a tick's snapshot keeps what it drew: the reservations hold whatever the cache drops.
            if (lookup.State == InfoPanel.Images.MediaState.Ready) ready++;
            Assert.True(media.ReservedBytes <= LoopMedia.BudgetBytes, $"{media.ReservedBytes} bytes reserved");
        }
        // A 4096 x 4096 still reserves its composite and its copy: the whole budget, so one is decoded.
        Assert.Equal(1, ready);
        Assert.Equal(4096L * 4096 * 4 * 2, media.ReservedBytes);
        Assert.True(media.StoredBytes <= LoopMedia.MaxStoredBytes);
    }

    [Fact]
    public void APictureWhoseHeaderClaimsMoreThanItHolds_IsReservedAtItsHeadersSize()
    {
        var store = new Assets();
        var first = store.Add(PngSaying(4096, 4096));
        var second = store.Add(PngSaying(4096, 2048));
        var third = store.Add(PngSaying(64, 64));
        using var media = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default);
        var a = media.Cache.Get(first, 0, InfoPanel.Images.MediaPlayback.Loop);
        var b = media.Cache.Get(second, 0, InfoPanel.Images.MediaPlayback.Loop);
        var c = media.Cache.Get(third, 0, InfoPanel.Images.MediaPlayback.Loop);
        // The first takes the budget at its header's size; the second would pass it and is never decoded.
        Assert.Equal(InfoPanel.Images.MediaState.Failed, b.State);
        Assert.Equal(InfoPanel.Images.MediaState.Failed, c.State);
        Assert.Equal(4096L * 4096 * 4 * 2, media.ReservedBytes);
        Assert.Equal(LoopMedia.BudgetBytes, media.ReservedBytes);
        a.Sample?.Release();
        // A header whose bytes overflow can't be counted: it is never reserved, so never decoded.
        Assert.Null(LoopMedia.Pixels(int.MaxValue, int.MaxValue, int.MaxValue));
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void ARecordingsMedia_DecodesOnItsThread_ReadsNothingOnceCancelled_AndKeepsNothingOnceDisposed()
    {
        var hash = "sha256:" + new string('a', 64);
        var store = new OneAsset(hash, Png());
        using (var media = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default))
        {
            var ready = media.Cache.Get(hash, 0, InfoPanel.Images.MediaPlayback.Loop);
            Assert.Equal(InfoPanel.Images.MediaState.Ready, ready.State);
            ready.Sample!.Release();
        }
        Assert.Equal(1, store.Reads);

        using var cancel = new CancellationTokenSource();
        var media2 = new LoopMedia(store, InfoPanel.Spec.SpecLimits.Default, cancel: cancel.Token);
        cancel.Cancel();
        Assert.NotEqual(InfoPanel.Images.MediaState.Ready, media2.Cache.Get(hash, 0, InfoPanel.Images.MediaPlayback.Loop).State);
        Assert.Equal(1, store.Reads);
        media2.Dispose();
        var after = media2.Cache.Get(hash, 0, InfoPanel.Images.MediaPlayback.Loop);
        Assert.Equal(InfoPanel.Images.MediaState.Failed, after.State);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void ARecordedLoop_EncodesAsAnimatedWebp()
    {
        using var loop = Record();
        var webp = WebpLoopEncoder.Encode(loop.Frames, null);
        using var codec = SKCodec.Create(new SKMemoryStream(webp));
        Assert.NotNull(codec);
        // Identical neighbours fold into one frame: four states, four pictures.
        Assert.Equal(4, codec.FrameCount);
    }

    [Fact]
    public void JoinedRuns_KeepEveryStatesFirstFrameWhole_AndListThem()
    {
        using var loop = Record();
        var runs = loop.StateStarts.Select((start, s) =>
        {
            var end = s + 1 < loop.StateStarts.Count ? loop.StateStarts[s + 1] : loop.Frames.Count;
            return (IReadOnlyList<LoopFrame>)loop.Frames.Skip(start).Take(end - start).ToList();
        }).ToList();
        var (webp, keyFrames) = WebpLoopEncoder.EncodeRuns(runs, null);

        // Every state here is one picture, so each run is a still that becomes one frame.
        Assert.Equal([0, 1, 2, 3], keyFrames);
        var anmf = new List<int>();
        for (var at = 12; at + 8 <= webp.Length;)
        {
            var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            if (System.Text.Encoding.ASCII.GetString(webp, at, 4) == "ANMF") anmf.Add(at + 8);
            at += 8 + size + (size & 1);
        }
        Assert.Equal(4, anmf.Count);
        foreach (var k in keyFrames)
        {
            var p = anmf[k];
            Assert.True(webp.AsSpan(p, 6).ToArray().All(b => b == 0), "at 0,0");
            Assert.Equal(119, webp[p + 6] | webp[p + 7] << 8 | webp[p + 8] << 16);
            Assert.Equal(19, webp[p + 9] | webp[p + 10] << 8 | webp[p + 11] << 16);
            Assert.True((webp[p + 15] & 0x02) != 0, $"frame {k} isn't blended");
        }
        Assert.Equal([300, 200, 200, 300], anmf.Select(p => webp[p + 12] | webp[p + 13] << 8 | webp[p + 14] << 16).ToArray());

        using var codec = SKCodec.Create(new SKMemoryStream(webp));
        Assert.NotNull(codec);
        Assert.Equal(4, codec.FrameCount);
        int[] lit = [30, 60, 100, 0];
        for (var k = 0; k < 4; k++)
        {
            using var frame = new SKBitmap(new SKImageInfo(120, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(frame.Info, frame.GetPixels(), new SKCodecOptions(k)));
            Assert.Equal(lit[k], Lit(frame));
        }
    }
}
