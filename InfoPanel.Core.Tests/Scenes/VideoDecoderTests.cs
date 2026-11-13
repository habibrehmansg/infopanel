using InfoPanel.Images.Video;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The FFmpeg decoder on its own (<see cref="VideoDecoder"/>): the options that keep FFmpeg to the probed file are
/// applied or the video fails; a cancelled decoder reads no further; FFmpeg reads every byte through the stream it is
/// given, the blob store's over a file too, and never the other file a mov's data reference names.
/// </summary>
public sealed class VideoDecoderTests
{
    /// <summary>
    /// A video's bytes read 16 at a time (FFmpeg reads many times per packet), calling <paramref name="reading"/> first.
    /// (A span read of a stream derived from MemoryStream comes here through Stream's own.)
    /// </summary>
    private sealed class ReadingStream(byte[] bytes, Action reading) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            reading();
            return base.Read(buffer, offset, Math.Min(count, 16));
        }
    }

    [Fact]
    public void ADecoderCancelledWhileItReads_StopsBeforeItsNextRead()
    {
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var probe = VideoProbe.Inspect(bytes, out _)!;
        using var cancel = new CancellationTokenSource();
        var armed = false;
        var readsAfterCancel = 0;
        // Once armed, the next read cancels the decoder from inside a decode, as a session closed meanwhile does.
        var stream = new ReadingStream(bytes, () =>
        {
            if (cancel.IsCancellationRequested) readsAfterCancel++;
            else if (armed) cancel.Cancel();
        });
        using var decoder = VideoDecoder.Open(FFmpegForTests.Library, stream, probe, 64, 48, SpecLimits.Default, cancel: cancel.Token);
        Assert.Equal(0, decoder.Next());

        armed = true;
        var frames = 0;
        Assert.Throws<OperationCanceledException>(() =>
        {
            while (decoder.Next() is not null) frames++;
        });
        Assert.Equal(0, readsAfterCancel);
        Assert.InRange(frames, 0, 3); // at most the frames the decoder had already been given
    }

    /// <summary>Counts the bytes read through it from <paramref name="inner"/>; <see cref="Name"/> is the file's path.</summary>
    private sealed class CountingStream(Stream inner, string? name = null) : Stream
    {
        public long BytesRead;

        public string? Name => name;

        public bool OverAFile => inner is FileStream;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            Interlocked.Add(ref BytesRead, n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The fixture frame an image shows: its centre pixel's colour.</summary>
    private static int FrameOf(SkiaSharp.SKImage image)
    {
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        Assert.True(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, image.Width / 2, image.Height / 2));
        return VideoFixtures.FrameOf(bitmap.GetPixel(0, 0));
    }

    [Fact]
    public void OnTheBlobStoresOwnFile_FFmpegReadsEveryByteThroughTheStreamItIsGiven()
    {
        // The blob store's checked stream over the stored file (a file FFmpeg could open by name), counted: the frames
        // decoded are the file's, and all of its media data came through the stream after the open began.
        using var temp = new Persistence.TempDirectory();
        var blobs = new InfoPanel.Documents.BlobStore(new InfoPanel.Persistence.DataPaths(temp.Path));
        blobs.Open();
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var id = blobs.Put(bytes);
        var counting = new CountingStream(blobs.OpenVerified(id)!, blobs.Paths.BlobFile(id));
        Assert.True(counting.OverAFile && File.Exists(counting.Name), "the blob store's stream is its file's");

        long atOpen = -1;
        using (var decoder = VideoDecoder.Open(FFmpegForTests.Library, counting, VideoProbe.Inspect(bytes, out _)!, 64, 48, SpecLimits.Default,
            opening: _ => atOpen = Interlocked.Read(ref counting.BytesRead)))
        {
            var frames = new List<int>();
            while (decoder.Next() is not null)
            {
                using var image = decoder.Convert();
                frames.Add(FrameOf(image));
            }
            Assert.Equal(Enumerable.Range(0, VideoFixtures.Frames), frames);
        }

        var mdat = bytes.AsSpan().IndexOf("mdat"u8) - 4;
        var mediaData = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdat)) - 8;
        Assert.True(atOpen >= 0, "FFmpeg was never opened");
        Assert.InRange(Interlocked.Read(ref counting.BytesRead) - atOpen, mediaData, long.MaxValue);
    }

    /// <summary>
    /// <paramref name="mov"/> with its video track's data in another file: its data reference becomes a Mac alias
    /// record naming <paramref name="path"/> (absolute; not self-contained), and its own media data is zeroed. The
    /// boxes above the reference grow; the sample offsets stay, so they are right for a copy of the original at <paramref name="path"/>.
    /// </summary>
    private static byte[] WithDataElsewhere(byte[] mov, string path)
    {
        static uint Size(byte[] b, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
        static int Child(byte[] b, int start, int end, string type)
        {
            for (var at = start; at + 8 <= end; at += (int)Size(b, at))
                if (System.Text.Encoding.ASCII.GetString(b, at + 4, 4) == type) return at;
            throw new InvalidDataException("no " + type);
        }

        var chain = new List<int> { Child(mov, 0, mov.Length, "moov") };
        foreach (var type in new[] { "trak", "mdia", "minf", "dinf", "dref" })
            chain.Add(Child(mov, chain[^1] + 8, chain[^1] + (int)Size(mov, chain[^1]), type));
        var entry = chain[^1] + 16; // the dref's full box header and entry count
        Assert.Equal("url ", System.Text.Encoding.ASCII.GetString(mov, entry + 4, 4));
        var oldSize = (int)Size(mov, entry);

        // An alias record: fixed fields (volume, file name, levels) FFmpeg reads or skips, then tagged records: type 2 the
        // absolute path, type -1 the end.
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        var padded = pathBytes.Length + (pathBytes.Length & 1);
        const int Fixed = 12 + 10 + 28 + 12 + 64 + 16 + 4 + 16;
        var alias = new byte[Fixed + 4 + padded + 4];
        var w = new Span<byte>(alias);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(w, (uint)alias.Length);
        "alis"u8.CopyTo(w[4..]); // version and flags 0: the data is elsewhere
        var name = 12 + 10 + 28 + 12;
        alias[name] = 10;
        "target.mov"u8.CopyTo(w[(name + 1)..]);
        var records = Fixed;
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(w[records..], 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(w[(records + 2)..], (ushort)pathBytes.Length);
        pathBytes.CopyTo(w[(records + 4)..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(w[(records + 4 + padded)..], -1);

        var grow = alias.Length - oldSize;
        var result = new byte[mov.Length + grow];
        mov.AsSpan(0, entry).CopyTo(result);
        alias.CopyTo(result, entry);
        mov.AsSpan(entry + oldSize).CopyTo(result.AsSpan(entry + alias.Length));
        foreach (var box in chain)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(box), Size(mov, box) + (uint)grow);
        var mdat = result.AsSpan().IndexOf("mdat"u8) - 4;
        result.AsSpan(mdat + 8, (int)Size(result, mdat) - 8).Clear();
        return result;
    }

    [Fact]
    public void AMovWhoseDataIsInAnotherFile_IsNeverReadFromThere()
    {
        // The probe reads such a mov as a video (its data's place isn't its concern); FFmpeg is the one that would open
        // the other file. It doesn't: the track has no packets (enable_drefs=0, and no protocol to open a path with).
        using var temp = new Persistence.TempDirectory();
        var target = temp.Combine("target.mov");
        var original = VideoFixtures.Read("clip-h264.mov");
        File.WriteAllBytes(target, original);
        var bytes = WithDataElsewhere(original, target);
        var probe = VideoProbe.Inspect(bytes, out var problem);
        Assert.True(probe is { Codec: "h264" }, problem?.Message);

        using var decoder = VideoDecoder.Open(FFmpegForTests.Library, new MemoryStream(bytes), probe!, 64, 48, SpecLimits.Default);
        Assert.Null(decoder.Next());
    }

    [Fact]
    public void AnOptionFFmpegRefuses_FailsTheVideo()
    {
        // FFmpeg refuses an option without a key (AVERROR(EINVAL)): a safety option that doesn't apply must not pass unnoticed.
        var f = FFmpegForTests.Library;
        var options = IntPtr.Zero;
        try
        {
            var refused = Assert.Throws<VideoException>(() => VideoDecoder.SetOption(f, ref options, null, "1"));
            Assert.Equal(InfoPanel.Spec.MediaProbe.MalformedCode, refused.Code);
            VideoDecoder.SetOption(f, ref options, "threads", "2");
            Assert.NotEqual(IntPtr.Zero, options);
        }
        finally
        {
            VideoDecoder.FreeOptions(f, ref options);
        }
    }

    /// <summary>A luma row's bytes as FFmpeg's frame pool lays it out (libavcodec/get_buffer.c).</summary>
    /// <param name="width">The picture's width.</param>
    /// <param name="sampleBytes">Bytes a sample: 1, or 2 past 8 bits.</param>
    /// <param name="chromaShift">How the chroma planes' width is halved: 1 for 4:2:0 and 4:2:2, 0 for 4:4:4; null without chroma (gray).</param>
    /// <param name="strideAlign">FFmpeg's <c>STRIDE_ALIGN</c>: 16, 32, or 64 when built with AVX-512.</param>
    private static long LumaRowBytes(int width, int sampleBytes, int? chromaShift, int strideAlign)
    {
        // avcodec_align_dimensions2 rounds the width to 16 for the planar formats MPEG-4 decodes in; then the width
        // grows by its lowest set bit until every plane's row is a multiple of STRIDE_ALIGN.
        long w = (width + 15) / 16 * 16;
        while (w * sampleBytes % strideAlign != 0 || (chromaShift is { } shift && ((w + (1 << shift) - 1) >> shift) * sampleBytes % strideAlign != 0))
            w += w & -w;
        return w * sampleBytes;
    }

    private static long Align(long value, long to) => (value + to - 1) / to * to;

    /// <summary>The pixel formats MPEG-4 decodes in: 8-bit 4:2:0 and gray, and its 10-bit profile's 4:2:2 and 4:4:4.</summary>
    private static readonly (int SampleBytes, int? ChromaShift)[] Mpeg4Formats = [(1, 1), (1, null), (2, 1), (2, 0)];

    /// <summary>FFmpeg's <c>STRIDE_ALIGN</c>s: 16 (NEON, SSE), 32 (AVX2), 64 (AVX-512).</summary>
    private static readonly int[] StrideAligns = [16, 32, 64];

    [Fact]
    public void ThePictureArea_HoldsWhatFFmpegChecksAgainstMaxPixels_AtItsWidestRowAlignment()
    {
        // FFmpeg refuses to allocate, and the video fails, when one of these passes the decoder's max_pixels: the coded
        // size (avcodec_open2, ff_set_dimensions; a codec codes up to its block size, at most 64, past the picture), a
        // frame's rows at STRIDE_ALIGN (ff_get_buffer), and the MPEG-4 decoder's scratch buffers, FFALIGN(linesize + 64,
        // 32) bytes by 280 rows (ff_mpv_framesize_alloc, libavcodec/mpegpicture.c), whose linesize is the frame pool's
        // luma row in bytes.
        var limits = SpecLimits.Default;
        var sizes = new List<(int, int)>();
        int[] heights = [1, 2, 15, 16, 17, 47, 48, 63, 64, 65, 127, 128, 129, 191, 192, 193, 255, 256, 257, 279, 280, 281, 319, 320, 321, 559, 560, 561, 1080, 2160];
        foreach (var h in heights)
            for (var w = 1; w <= 1100; w += w < 300 ? 1 : 7)
                sizes.Add((w, h));
        // The largest pictures the media limits accept: the widest side, and the widest at the most pixels.
        sizes.AddRange([(limits.MediaSide, 1), (limits.MediaSide, (int)(limits.MediaArea / limits.MediaSide)), (4096, 4096), (1920, 1080), (3840, 2160)]);
        foreach (var (width, height) in sizes)
        {
            var area = VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, width, height, 0, 0));
            Assert.True(Align(width, 64) * Align(height, 64) <= area, $"coded {width} x {height}: {area}");
            foreach (var strideAlign in StrideAligns)
            {
                Assert.True(Align(width, strideAlign) * height <= area, $"rows of {width} x {height} at {strideAlign}: {area}");
                foreach (var (sampleBytes, chromaShift) in Mpeg4Formats)
                {
                    var scratch = Align(LumaRowBytes(width, sampleBytes, chromaShift, strideAlign) + 64, 32) * 280;
                    Assert.True(scratch <= area,
                        $"MPEG-4's scratch for {width} x {height} ({sampleBytes} bytes a sample, chroma shift {chromaShift}, rows at {strideAlign}): {scratch} > {area}");
                }
            }
        }
        // The decoder's cap is also held to the media area: the scratch of the widest picture it accepts fits within it.
        foreach (var strideAlign in StrideAligns)
            foreach (var (sampleBytes, chromaShift) in Mpeg4Formats)
                Assert.True(Align(LumaRowBytes(limits.MediaSide, sampleBytes, chromaShift, strideAlign) + 64, 32) * 280 <= limits.MediaArea);
    }

    [Theory]
    [InlineData(64, 48, 53_760)] // the fixtures' clips: MPEG-4's scratch rows at 64-byte alignment (was 40,960)
    [InlineData(1, 1, 53_760)]
    [InlineData(1920, 64, 1_093_120)] // a wide strip: its scratch rows
    [InlineData(1280, 720, 1_344 * 832)] // pictures at least about 560 rows tall: as before, each side padded
    [InlineData(1920, 1080, 1_984 * 1_152)]
    [InlineData(3840, 2160, 3_904 * 2_240)]
    public void ThePictureArea_GrowsOnlyForPicturesShorterThanMPEG4sScratchRows(int width, int height, long expected)
    {
        // The cap still bounds the decoder's memory by the container's stated size: a stream larger than that by more
        // than the padding still fails, and pictures at least as tall as MPEG-4's scratch needs keep the cap they had.
        Assert.Equal(expected, VideoDecoder.PictureArea(new MediaInfo(MediaFormat.Mp4, width, height, 0, 0)));
    }

    [Fact]
    public void AnOptionFFmpegLeftUnused_FailsTheVideo()
    {
        var f = FFmpegForTests.Library;
        var options = IntPtr.Zero;
        try
        {
            VideoDecoder.RequireConsumed(f, options, "the demuxer"); // none left: fine
            VideoDecoder.SetOption(f, ref options, "enable_drefs", "0");
            var left = Assert.Throws<VideoException>(() => VideoDecoder.RequireConsumed(f, options, "the demuxer"));
            Assert.Contains("enable_drefs", left.Message);
        }
        finally
        {
            VideoDecoder.FreeOptions(f, ref options);
        }
    }
}
