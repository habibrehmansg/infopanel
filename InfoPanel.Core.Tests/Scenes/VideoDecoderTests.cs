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
