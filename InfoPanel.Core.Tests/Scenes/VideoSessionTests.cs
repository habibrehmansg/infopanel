using System.Collections.Concurrent;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Images.Video;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Videos decoded by FFmpeg for the media cache (spec/docs/smart-nodes.md, "Video files"): the frame shown at a time,
/// never early, with loops, once, still; the decode size; the session budget and idle closing; frames counted in the
/// cache's budget; the probe before FFmpeg on every path; FFmpeg reading only through the store's stream. The fixtures'
/// frame i is one colour, so the colour drawn says which frame was shown.
/// </summary>
public sealed class VideoSessionTests : IDisposable
{
    /// <summary>Videos in memory only: no file exists for FFmpeg to open by name.</summary>
    private sealed class MemoryVideos : IMediaAssetStore
    {
        public readonly ConcurrentDictionary<string, byte[]> Blobs = new();
        public int Opens;

        public string Add(byte[] bytes)
        {
            var id = BlobStore.IdOf(bytes);
            Blobs[id] = bytes;
            return id;
        }

        public string Add(string fixture) => Add(VideoFixtures.Read(fixture));

        public byte[]? Read(string hash) => Blobs.TryGetValue(hash, out var b) ? b : null;

        public long? Size(string hash) => Blobs.TryGetValue(hash, out var b) ? b.Length : null;

        public Stream? Open(string hash)
        {
            Interlocked.Increment(ref Opens);
            return Blobs.TryGetValue(hash, out var b) ? new MemoryStream(b, writable: false) : null;
        }
    }

    private readonly MemoryVideos _store = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly ConcurrentBag<(string Hash, string? Url)> _opened = [];
    private readonly List<MediaSampleCache> _caches = [];

    private MediaSampleCache Cache(bool synchronous = true, long budget = 512L * 1024 * 1024, int sessions = 8, bool decoder = true)
    {
        var cache = new MediaSampleCache(_store, new MediaSampleCacheOptions
        {
            Synchronous = synchronous,
            BudgetBytes = budget,
            Time = _time,
            Video = new VideoOptions
            {
                Decoder = decoder ? FFmpegForTests.Library : null,
                MaxSessions = sessions,
                Opening = (hash, url) => _opened.Add((hash, url)),
            },
        });
        _caches.Add(cache);
        return cache;
    }

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
    }

    /// <summary>The frame a sample shows: its centre pixel's colour, matched to the fixtures' colours.</summary>
    private static int FrameOf(MediaSample sample)
    {
        var image = sample.Image!;
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.True(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, image.Width / 2, image.Height / 2));
        return VideoFixtures.FrameOf(bitmap.GetPixel(0, 0));
    }

    private static int Shown(MediaSampleCache cache, string hash, double seconds, MediaPlayback playback = MediaPlayback.Loop,
        float? w = null, float? h = null)
    {
        var lookup = cache.GetVideo(hash, seconds, playback, w, h);
        Assert.True(lookup.State == MediaState.Ready, $"{lookup.State}: {lookup.Problem}");
        try
        {
            return FrameOf(lookup.Sample!);
        }
        finally
        {
            lookup.Sample!.Release();
        }
    }

    public static TheoryData<string> Clips()
    {
        var data = new TheoryData<string>();
        foreach (var (file, _, _) in VideoFixtures.Clips) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void EachClip_ShowsTheFrameOfItsTime_NeverOneEarly(string clip)
    {
        var cache = Cache();
        var id = _store.Add(clip);
        // In order, then back in time (a seek), each the newest frame at or before its time.
        (double Seconds, int Frame)[] expected = [(0, 0), (0.05, 0), (0.1, 1), (0.35, 3), (0.39, 3), (0.4, 4), (0.95, 9), (0.2, 2), (0.69, 6)];
        foreach (var (seconds, frame) in expected)
            Assert.Equal((seconds, frame), (seconds, Shown(cache, id, seconds)));
    }

    [Fact]
    public void ADecodedFrame_IsTheVideosOwnSize_AndItsFieldsReadBack()
    {
        // The binding reads FFmpeg's structs at pinned offsets: the decoded frame's size and format must read back right.
        var probe = VideoProbe.Inspect(VideoFixtures.Read("clip-h264.mp4"), out _)!;
        using var decoder = VideoDecoder.Open(FFmpegForTests.Library, new MemoryStream(VideoFixtures.Read("clip-h264.mp4")), probe, 64, 48, SpecLimits.Default);
        Assert.Equal(0, decoder.Next());
        var (w, h, format) = decoder.FrameShape;
        Assert.Equal((64, 48), (w, h));
        Assert.Equal(0, format); // AV_PIX_FMT_YUV420P
        Assert.InRange(decoder.Next()!.Value, 0.099, 0.101);
        using var image = decoder.Convert();
        Assert.Equal((64, 48), (image.Width, image.Height));
    }

    [Fact]
    public void Loop_WrapsAtTheEnd()
    {
        var cache = Cache();
        var id = _store.Add("clip-vp9.webm");
        Assert.Equal(9, Shown(cache, id, 0.95));
        Assert.Equal(0, Shown(cache, id, 1.0));
        Assert.Equal(2, Shown(cache, id, 1.25));
        Assert.Equal(3, Shown(cache, id, 10.35));
    }

    /// <summary>The webm fixture with its header's duration (milliseconds) replaced: the stream is still 10 frames, 1 s.</summary>
    private static byte[] WithHeaderDuration(string clip, double milliseconds)
    {
        var bytes = VideoFixtures.Read(clip);
        var at = bytes.AsSpan().IndexOf(new byte[] { 0x44, 0x89, 0x88 }); // Info's Duration, an 8-byte float
        Assert.True(at > 0);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(at + 3), milliseconds);
        Assert.Equal(milliseconds / 1000, VideoProbe.Inspect(bytes, out _)!.DurationSeconds, 6);
        return bytes;
    }

    [Theory]
    [InlineData(10_000.0)] // a header claiming 10 s: looping on it would hold the last frame for 9 s
    [InlineData(500.0)] // a header claiming 0.5 s: looping on it would show the first half twice and never the second
    public void Loop_WrapsAtTheStreamsOwnEnd_NotTheHeaders(double headerMilliseconds)
    {
        var bytes = WithHeaderDuration("clip-vp9.webm", headerMilliseconds);
        var cache = Cache();
        var id = _store.Add(bytes);
        Assert.Equal(7, Shown(cache, id, 0.75));
        Assert.Equal(9, Shown(cache, id, 0.95));
        Assert.Equal(0, Shown(cache, id, 1.0));
        Assert.Equal(2, Shown(cache, id, 1.25));
        Assert.Equal(5, Shown(cache, id, 3.55));
        // A session asked first for a time past the end finds the end, then wraps.
        Assert.Equal(3, Shown(Cache(), id, 10.35));
    }

    [Theory]
    [InlineData(10_000.0)]
    [InlineData(500.0)]
    public void InARenderLoop_ALoopWrapsAtTheStreamsOwnEnd_NotTheHeaders(double headerMilliseconds)
    {
        var cache = Cache(synchronous: false);
        var id = _store.Add(WithHeaderDuration("clip-vp8.webm", headerMilliseconds));
        int Wait(double seconds, int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var frame = -1;
            while (DateTime.UtcNow < deadline)
            {
                var lookup = cache.GetVideo(id, seconds, MediaPlayback.Loop, null, null);
                Assert.NotEqual(MediaState.Failed, lookup.State);
                if (lookup.Sample is { } sample)
                {
                    frame = FrameOf(sample);
                    sample.Release();
                    if (frame == expected) return frame;
                }
                Thread.Sleep(2);
            }
            return frame;
        }
        for (var i = 0; i <= 9; i++) Assert.Equal(i, Wait(i * 0.1, i));
        Assert.Equal(0, Wait(1.0, 0));
        Assert.Equal(1, Wait(1.1, 1));
        Assert.Equal(5, Wait(1.5, 5));
        Assert.Equal(8, Wait(1.85, 8));
        Assert.Equal(1, Wait(2.15, 1));
    }

    [Fact]
    public void Once_HoldsTheLastFrame_AndStill_ShowsTheFirst()
    {
        var cache = Cache();
        var id = _store.Add("clip-h264.mov");
        Assert.Equal(4, Shown(cache, id, 0.45, MediaPlayback.Once));
        Assert.Equal(9, Shown(cache, id, 1.25, MediaPlayback.Once));
        Assert.Equal(9, Shown(cache, id, 50, MediaPlayback.Once));
        Assert.Equal(0, Shown(cache, id, 0.75, MediaPlayback.Still));
        Assert.Equal(0, Shown(cache, id, 7.5, MediaPlayback.Still));
    }

    [Fact]
    public void TheDecodeSize_IsTwiceTheBox_CappedAtTheVideosOwn_AndTheNaturalSizeStays()
    {
        var cache = Cache();
        var id = _store.Add("clip-h264.mp4");
        foreach (var (w, h, expected) in new (float?, float?, (int, int))[] { (16, 12, (32, 24)), (64, 48, (64, 48)), (null, null, (64, 48)), (8, null, (16, 12)), (200, 2, (64, 48)) })
        {
            var lookup = cache.GetVideo(id, 0.35, MediaPlayback.Loop, w, h);
            var sample = lookup.Sample!;
            Assert.Equal(expected, (sample.Image!.Width, sample.Image.Height));
            Assert.Equal((64f, 48f), (sample.Width, sample.Height));
            Assert.Equal(3, FrameOf(sample));
            sample.Release();
        }
    }

    [Fact]
    public void PastEightSessions_ANewOne_IsOverBudget_UntilOneHasntBeenAskedForASecond()
    {
        var cache = Cache();
        var ids = VideoFixtures.Clips.Select(c => _store.Add(c.File)).ToList();
        Assert.Equal(9, ids.Count);
        foreach (var id in ids.Take(8)) Assert.Equal(0, Shown(cache, id, 0));
        Assert.Equal(8, cache.VideoSessions);

        var refused = cache.GetVideo(ids[8], 0, MediaPlayback.Loop, null, null);
        Assert.Equal(MediaState.Failed, refused.State);
        Assert.StartsWith("media.budget", refused.Problem);

        _time.Advance(TimeSpan.FromMilliseconds(1500));
        foreach (var id in ids.Skip(1).Take(7)) Assert.Equal(1, Shown(cache, id, 0.1)); // the first goes unasked
        Assert.Equal(1, Shown(cache, ids[8], 0.1));
        Assert.Equal(8, cache.VideoSessions);
    }

    /// <summary>The h264 mov with its sample entry stating another coded size (the stream itself is still 64 x 48).</summary>
    private static byte[] WithCodedSize(int width, int height)
    {
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var format = bytes.AsSpan().IndexOf("stsd"u8) + 16; // version and flags, entry count, entry size, then the format
        Assert.Equal("avc1"u8.ToArray(), bytes[format..(format + 4)]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(format + 28), (ushort)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(format + 30), (ushort)height);
        Assert.Equal((width, height), VideoProbe.Inspect(bytes, out _) is { } info ? (info.Width, info.Height) : default);
        return bytes;
    }

    [Fact]
    public void ADecodersMemory_IsEstimatedFromItsCodedArea_AndALargeOneWaitsWhileAnotherPlays()
    {
        var cache = Cache();
        var large = WithCodedSize(4096, 4096);
        // An H.264 decoder at 4096 x 4096 keeps up to 16 reference pictures, a picture per thread and the one it decodes.
        Assert.InRange(VideoPlayer.WorkingSet(VideoProbe.Inspect(large, out _)!), 600_000_000L, 700_000_000L);
        var first = _store.Add(large);
        var second = _store.Add(WithCodedSize(4096, 4000));

        // One alone always fits; a second as large would take the decoders past their budget, so it waits.
        Assert.Equal(3, Shown(cache, first, 0.35, w: 16, h: 16));
        var refused = cache.GetVideo(second, 0.35, MediaPlayback.Loop, 16, 16);
        Assert.Equal(MediaState.Failed, refused.State);
        Assert.StartsWith("media.budget", refused.Problem);
        Assert.Equal(1, cache.VideoSessions);

        // A second later, unasked, the first makes room.
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(3, Shown(cache, second, 0.35, w: 16, h: 16));
        Assert.Equal(1, cache.VideoSessions);
        Assert.Equal(VideoPlayer.WorkingSet(VideoProbe.Inspect(_store.Read(second)!, out _)!), cache.VideoWorkingBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStreamLargerThanItsContainerStates_NeverDecodesPastItsReservation(bool synchronous)
    {
        // The mov's sample entry says 1 x 1; its stream is 64 x 48. Whatever the decoder decodes, the memory reserved
        // for it covers: 64 x 48 pictures at 2 bytes a pixel, 16 references, one a thread and the one being decoded.
        var cache = Cache(synchronous);
        var id = _store.Add(WithCodedSize(1, 1));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        MediaLookup lookup;
        while (true)
        {
            lookup = cache.GetVideo(id, 0.35, MediaPlayback.Loop, null, null);
            lookup.Sample?.Release();
            if (lookup.State != MediaState.Loading || DateTime.UtcNow > deadline) break;
            Thread.Sleep(2);
        }
        Assert.Equal(MediaState.Ready, lookup.State);
        Assert.InRange(cache.VideoWorkingBytes, 64L * 48 * 2 * (16 + VideoDecoder.Threads + 1), long.MaxValue);
    }

    [Fact]
    public void APictureLargerThanTheDecoderMayAllocate_FailsTheVideo_BeforeAnyFrame()
    {
        // The 1 x 1 container with an area limit under the stream's 64 x 48: FFmpeg allocates no such picture, so the
        // stream ends with no frame decoded, and the video fails rather than stand empty.
        var bytes = WithCodedSize(1, 1);
        var probe = VideoProbe.Inspect(bytes, out _)!;
        using var decoder = VideoDecoder.Open(FFmpegForTests.Library, new MemoryStream(bytes), probe, 1, 1,
            SpecLimits.Default with { MediaArea = 64 * 48 - 1 });
        var e = Assert.Throws<VideoException>(() => decoder.Next());
        Assert.Equal(MediaProbe.MalformedCode, e.Code);
    }

    /// <summary>The mov fixture with its movie's and media's durations (version 0 boxes) stretched: its frame times stay.</summary>
    private static byte[] WithMovDurations(string clip, uint factor)
    {
        var bytes = VideoFixtures.Read(clip);
        foreach (var box in new[] { "mvhd"u8.ToArray(), "mdhd"u8.ToArray() })
        {
            var at = bytes.AsSpan().IndexOf(box);
            Assert.True(at > 0 && bytes[at + 4] == 0);
            var duration = bytes.AsSpan(at + 20, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(duration, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(duration) * factor);
        }
        Assert.InRange(VideoProbe.Inspect(bytes, out _)!.DurationSeconds, factor * 0.99, factor * 1.01);
        return bytes;
    }

    [Theory]
    [InlineData("clip-mjpeg.avi")]
    [InlineData("clip-prores.mov")] // its header 10 s long, so the header's frame interval is 1 s
    public void ASeekNearTheEnd_DoesntTeachTheLoopAWrongLength(string clip)
    {
        // 0 s decodes frame 0 and the 0.1 s frame after it; 10.95 s seeks straight to the last frame, 0.9 s (every frame
        // is a key frame). The gap between them isn't one frame: the loop is 1 s, so 10.95 s is frame 9.
        var cache = Cache();
        var id = clip.EndsWith(".mov") ? _store.Add(WithMovDurations(clip, 10)) : _store.Add(clip);
        Assert.Equal(0, Shown(cache, id, 0));
        Assert.Equal(9, Shown(cache, id, 10.95));
        Assert.Equal(2, Shown(cache, id, 11.25));
        Assert.Equal(7, Shown(cache, id, 12.75));
    }

    [Fact]
    public void InARenderLoop_ASeekOnTheHeadersLength_IsRealignedOnceTheStreamsOwnIsLearned()
    {
        var cache = Cache(synchronous: false);
        var id = _store.Add(WithHeaderDuration("clip-vp8.webm", 500.0));
        int WaitFor(double seconds, Func<int, bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var frame = -1;
            while (DateTime.UtcNow < deadline)
            {
                var lookup = cache.GetVideo(id, seconds, MediaPlayback.Loop, null, null);
                Assert.NotEqual(MediaState.Failed, lookup.State);
                if (lookup.Sample is { } sample)
                {
                    frame = FrameOf(sample);
                    sample.Release();
                    if (done(frame)) return frame;
                }
                Thread.Sleep(2);
            }
            return frame;
        }
        Assert.Equal(0, WaitFor(0, f => f == 0));
        // Far ahead before the stream's end is known: the decoder seeks on the header's 0.5 s length, and shows a later frame.
        Assert.NotEqual(0, WaitFor(3.65, f => f != 0));
        // Once the end is found the loop is the stream's own 1 s: 4.45 s is 0.45 s into the video.
        Assert.Equal(4, WaitFor(4.45, f => f == 4));
        Assert.Equal(7, WaitFor(4.75, f => f == 7));
        Assert.Equal(2, WaitFor(5.25, f => f == 2));
    }

    /// <summary>Videos read through <see cref="GatedStream"/>s: the newest stream opened is <see cref="Last"/>.</summary>
    private sealed class GatedVideos : IMediaAssetStore
    {
        public readonly ConcurrentDictionary<string, byte[]> Blobs = new();
        public volatile GatedStream? Last;

        public string Add(byte[] bytes)
        {
            var id = BlobStore.IdOf(bytes);
            Blobs[id] = bytes;
            return id;
        }

        public byte[]? Read(string hash) => Blobs.TryGetValue(hash, out var b) ? b : null;

        public long? Size(string hash) => Blobs.TryGetValue(hash, out var b) ? b.Length : null;

        public Stream? Open(string hash) => Blobs.TryGetValue(hash, out var b) ? Last = new GatedStream(b) : null;
    }

    /// <summary>
    /// A video's bytes read 16 at a time, so FFmpeg reads many times per packet. Once <see cref="BlockAt"/> is set, the
    /// first read at or past it waits for <see cref="Release"/>; reads from then on are counted.
    /// </summary>
    internal sealed class GatedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BlockAt = long.MaxValue;
        public readonly ManualResetEventSlim Blocked = new(), Released = new(), Disposed = new();
        public int ReadsAfterRelease;

        // A span read of a stream derived from MemoryStream comes here through Stream's own.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Released.IsSet) Interlocked.Increment(ref ReadsAfterRelease);
            if (Position >= Volatile.Read(ref BlockAt) && !Blocked.IsSet)
            {
                Blocked.Set();
                Released.Wait(TimeSpan.FromSeconds(30));
            }
            return base.Read(buffer, offset, Math.Min(count, 16));
        }

        public void Release() => Released.Set();

        protected override void Dispose(bool disposing)
        {
            Disposed.Set();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void ASessionClosedWhileItDecodes_StopsAtItsNextRead_AndLeavesNothingInTheBudgets()
    {
        // A render loop's session, its thread held inside its first decode (reading the first packets); the session is
        // closed to make room for another. FFmpeg's next read ends the decode: it doesn't read on to a frame.
        var store = new GatedVideos();
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var id = store.Add(bytes);
        var mediaData = bytes.AsSpan().IndexOf("mdat"u8) + 4;
        GatedStream? held = null;
        using var cache = new MediaSampleCache(store, new MediaSampleCacheOptions
        {
            Synchronous = false,
            Time = _time,
            Video = new VideoOptions
            {
                Decoder = FFmpegForTests.Library,
                MaxSessions = 1,
                // The first decoder's stream, probed already: its reads from 64 bytes into the media data on are held.
                Opening = (_, _) =>
                {
                    if (held is null && store.Last is { } stream)
                    {
                        held = stream;
                        Volatile.Write(ref stream.BlockAt, mediaData + 64);
                    }
                },
            },
        });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (held?.Blocked.IsSet != true && DateTime.UtcNow < deadline)
        {
            Assert.NotEqual(MediaState.Failed, cache.GetVideo(id, 0, MediaPlayback.Loop, null, null).State);
            Thread.Sleep(2);
        }
        Assert.True(held?.Blocked.IsSet, "the decoder never read the media data");

        // Another size of the same video, past the one session: the first, not asked for in over a second, is closed.
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        cache.GetVideo(id, 0, MediaPlayback.Loop, 16, 12);
        Assert.Equal(1, cache.VideoSessions);
        held!.Release();

        Assert.True(held.Disposed.Wait(TimeSpan.FromSeconds(10)), "the closed session's decoder didn't stop");
        Assert.InRange(held.ReadsAfterRelease, 0, 1);
        cache.Dispose();
        Assert.Equal((0L, 0L), (cache.Bytes, cache.VideoWorkingBytes));
    }

    /// <summary>A frame budget that counts, and holds its first charge (a session's first frame, decoded, being kept) until released.</summary>
    private sealed class HeldBudget : IFrameBudget
    {
        public long Bytes;
        public readonly ManualResetEventSlim Charging = new(), Release = new();
        private int _charges;

        public void Charge(long bytes)
        {
            Interlocked.Add(ref Bytes, bytes);
            if (Interlocked.Increment(ref _charges) != 1) return;
            Charging.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
        }

        public void Uncharge(long bytes) => Interlocked.Add(ref Bytes, -bytes);
    }

    [Fact]
    public void ASessionClosedWhileItKeepsItsFirstFrame_ReleasesItAndItsBytes()
    {
        var budget = new HeldBudget();
        var id = _store.Add("clip-h264.mp4");
        var player = new VideoPlayer(_store, new VideoOptions { Decoder = FFmpegForTests.Library, MaxSessions = 1 }, SpecLimits.Default,
            synchronous: false, _time, budget);
        try
        {
            // A render loop's session, its thread keeping its first frame (charged, not yet installed).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!budget.Charging.IsSet && DateTime.UtcNow < deadline)
            {
                Assert.NotEqual(MediaState.Failed, player.Get(id, 0, MediaPlayback.Loop, null, null).State);
                Thread.Sleep(2);
            }
            Assert.True(budget.Charging.IsSet, "the session never decoded its first frame");

            // Closed meanwhile, to make room for another size of the video: then its thread goes on.
            _time.Advance(TimeSpan.FromMilliseconds(1500));
            player.Get(id, 0, MediaPlayback.Loop, 16, 12);
            Assert.Equal(1, player.Sessions);
            budget.Release.Set();
        }
        finally
        {
            budget.Release.Set();
            player.Dispose();
        }

        // Every frame either session kept is released: the closed one's first frame too, once its thread is done.
        var settled = DateTime.UtcNow.AddSeconds(10);
        while (Interlocked.Read(ref budget.Bytes) != 0 && DateTime.UtcNow < settled) Thread.Sleep(5);
        Assert.Equal(0, Interlocked.Read(ref budget.Bytes));
    }

    [Fact]
    public void ASessionNotAskedForIn30Seconds_IsClosed_AndItsFramesLeaveTheBudget()
    {
        var cache = Cache();
        var id = _store.Add("clip-h264.mp4");
        Assert.Equal(0, cache.Bytes);
        Assert.Equal(3, Shown(cache, id, 0.35));
        Assert.Equal(1, cache.VideoSessions);
        // The frame shown, 64 x 48 x 4 bytes, counted in the cache's budget (synchronous, the session decodes to it).
        Assert.Equal(64L * 48 * 4, cache.Bytes);

        _time.Advance(TimeSpan.FromSeconds(31));
        var other = _store.Add("clip-vp9.webm");
        Assert.Equal(0, Shown(cache, other, 0));
        Assert.Equal(1, cache.VideoSessions);
        Assert.Equal(64L * 48 * 4, cache.Bytes); // the closed session's frame is gone; the new one's first frame
    }

    [Fact]
    public void FramesOverTheBudget_PushOutPictureFrames()
    {
        // A budget a byte short of a video frame and a 40 x 30 picture: the video's frame counts, so the picture goes.
        var cache = Cache(budget: 64L * 48 * 4 + 40 * 30 * 4 - 1);
        using var png = SKImage.FromBitmap(new SKBitmap(40, 30)).Encode(SKEncodedImageFormat.Png, 100);
        var picture = _store.Add(png.ToArray());
        var pictureLookup = cache.Get(picture, 0, MediaPlayback.Loop);
        pictureLookup.Sample!.Release();
        Assert.Equal(40L * 30 * 4, cache.Bytes);

        var id = _store.Add("clip-h264.mp4");
        Assert.Equal(1, Shown(cache, id, 0.1));
        Assert.Equal(64L * 48 * 4, cache.Bytes);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("outside-qtrle.mov")]
    [InlineData("png-named.mp4")]
    [InlineData("garbage moov")]
    public void AFileTheProbeRefuses_NeverReachesFFmpeg(string what)
    {
        var bytes = what switch
        {
            "truncated" => VideoFixtures.Read("clip-h264.mp4")[..900],
            "garbage moov" => GarbageMoov(),
            _ => VideoFixtures.Read(what),
        };
        var id = _store.Add(bytes);
        var cache = Cache();
        var lookup = cache.GetVideo(id, 0.35, MediaPlayback.Loop, null, null);
        Assert.Equal(MediaState.Failed, lookup.State);
        Assert.Matches("^media\\.(truncated|malformed|codec|format):", lookup.Problem);
        Assert.Empty(_opened);
    }

    /// <summary>The mov fixture with its moov's first child given a size past the moov: a broken container.</summary>
    private static byte[] GarbageMoov()
    {
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var moov = bytes.AsSpan().IndexOf("moov"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(moov + 4), 0x7FFFFFF0);
        return bytes;
    }

    [Fact]
    public void FFmpegReadsOnlyThroughTheStoresStream_NeverByName()
    {
        var cache = Cache();
        var id = _store.Add("clip-hevc.mkv");
        Assert.Equal(3, Shown(cache, id, 0.35));
        // Opened once by the probe and once for the decoder, both by the store; FFmpeg was given no address.
        Assert.Equal(2, _store.Opens);
        Assert.Equal([(id, (string?)null)], _opened.ToArray());
    }

    [Fact]
    public void WithoutADecoder_AVideoIsMissing()
    {
        var cache = Cache(decoder: false);
        var lookup = cache.GetVideo(_store.Add("clip-h264.mp4"), 0, MediaPlayback.Loop, null, null);
        Assert.Equal(MediaState.Failed, lookup.State);
        Assert.StartsWith("media.video", lookup.Problem);
    }

    [Fact]
    public void AVideoNotStored_IsMissing_UntilItIsStored()
    {
        var cache = Cache();
        var bytes = VideoFixtures.Read("clip-h264.mp4");
        var id = BlobStore.IdOf(bytes);
        Assert.StartsWith("media.missing", cache.GetVideo(id, 0, MediaPlayback.Loop, null, null).Problem);
        _store.Add(bytes);
        cache.Retry(id);
        Assert.Equal(0, Shown(cache, id, 0));
    }

    /// <summary>
    /// A render loop's session decodes ahead on its own thread: asked for later and later times as a clock would, it
    /// shows the frames in order, each at or before its time, and catches up to the last.
    /// </summary>
    [Theory]
    [InlineData("clip-h264.mp4")]
    [InlineData("clip-av1.webm")]
    [InlineData("clip-mjpeg.avi")]
    public void InARenderLoop_FramesFollowTheClock_NeverEarly(string clip)
    {
        var cache = Cache(synchronous: false);
        var id = _store.Add(clip);
        var seen = new List<(double Seconds, int Frame)>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        for (var tick = 0; tick <= 30 && DateTime.UtcNow < deadline; tick++)
        {
            var seconds = tick * 0.05;
            while (DateTime.UtcNow < deadline)
            {
                var lookup = cache.GetVideo(id, seconds, MediaPlayback.Once, null, null);
                Assert.NotEqual(MediaState.Failed, lookup.State);
                if (lookup.Sample is { } sample)
                {
                    var frame = FrameOf(sample);
                    sample.Release();
                    seen.Add((seconds, frame));
                    if (frame == Math.Min(9, (int)Math.Floor(seconds * 10 + 1e-9))) break; // on time
                }
                Thread.Sleep(2);
            }
        }
        Assert.All(seen, s => Assert.True(s.Frame <= Math.Floor(s.Seconds * 10 + 1e-9), $"frame {s.Frame} shown at {s.Seconds} s: early"));
        Assert.Equal(seen.Select(s => s.Frame).Order(), seen.Select(s => s.Frame));
        Assert.Equal(9, seen[^1].Frame);
    }

    [Fact]
    public void InARenderLoop_ALoopWrapsToTheFirstFrame()
    {
        var cache = Cache(synchronous: false);
        var id = _store.Add("clip-vp8.webm");
        int Wait(double seconds, int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var frame = -1;
            while (DateTime.UtcNow < deadline)
            {
                var lookup = cache.GetVideo(id, seconds, MediaPlayback.Loop, null, null);
                if (lookup.Sample is { } sample)
                {
                    frame = FrameOf(sample);
                    sample.Release();
                    Assert.True(frame <= expected, $"frame {frame} at {seconds} s");
                    if (frame == expected) return frame;
                }
                Thread.Sleep(2);
            }
            return frame;
        }
        for (var i = 0; i <= 9; i++) Assert.Equal(i, Wait(i * 0.1, i));
        Assert.Equal(0, Wait(1.0, 0));
        Assert.Equal(1, Wait(1.1, 1));
        Assert.Equal(5, Wait(1.5, 5));
    }
}
