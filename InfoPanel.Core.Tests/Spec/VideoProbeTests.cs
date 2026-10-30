using System.Buffers.Binary;
using System.Text;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// The video container probe (spec/docs/smart-nodes.md, "Video files"): what it reads from each container, and that
/// hostile files are refused with a code, never a crash, and never an allocation sized by what a header claims.
/// </summary>
public class VideoProbeTests
{
    private static MediaInfo Ok(byte[] bytes)
    {
        var check = MediaValidator.Check(bytes);
        Assert.True(check.Errors.Count == 0, string.Join("; ", check.Errors.Select(e => $"{e.Code}: {e.Message}")));
        return check.Info!;
    }

    private static string Refused(byte[] bytes, SpecLimits? limits = null)
    {
        var check = MediaValidator.Check(bytes, limits);
        Assert.NotEmpty(check.Errors);
        return check.Errors[0].Code;
    }

    private static int IndexOf(byte[] bytes, string ascii, int from = 0)
    {
        var at = bytes.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(ascii));
        Assert.True(at >= 0, $"no {ascii} in the fixture");
        return from + at;
    }

    // ------------------------------------------------------------ what each container says

    public static TheoryData<string, string, string> Clips()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (file, format, codec) in VideoFixtures.Clips) data.Add(file, format, codec);
        return data;
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void EachClip_ReadsFormatCodecSizeFramesAndDuration(string file, string format, string codec)
    {
        var info = Ok(VideoFixtures.Read(file));
        Assert.Equal(format, info.Format.ToString().ToLowerInvariant());
        Assert.Equal(codec, info.Codec);
        Assert.True(info.IsVideo);
        Assert.Equal((VideoFixtures.Width, VideoFixtures.Height), (info.Width, info.Height));
        Assert.Equal(VideoFixtures.Frames, info.Frames);
        Assert.InRange(info.DurationSeconds, 0.95, 1.05);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void EachClip_ReadFromAStream_IsTheSame(string file, string format, string codec)
    {
        _ = (format, codec);
        var bytes = VideoFixtures.Read(file);
        using var stream = new MemoryStream(bytes, writable: false);
        Assert.Equal(VideoProbe.Inspect(bytes, out _), VideoProbe.Inspect(stream, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AnMp4WithItsMoovLast_IsFoundBySkippingTheMediaData()
    {
        var bytes = VideoFixtures.Read("clip-h264.mp4");
        Assert.True(IndexOf(bytes, "mdat") < IndexOf(bytes, "moov"), "the fixture keeps its moov after mdat");
        Assert.Equal("h264", Ok(bytes).Codec);
    }

    [Theory]
    [InlineData("outside-mpeg2.mkv")]
    [InlineData("outside-qtrle.mov")]
    [InlineData("outside-huffyuv.avi")]
    public void ACodecOutsideTheList_IsRefused(string file)
    {
        var check = MediaValidator.Check(VideoFixtures.Read(file));
        Assert.Equal(VideoProbe.CodecCode, Assert.Single(check.Errors).Code);
        Assert.Null(check.Info);
    }

    [Fact]
    public void APngNamedMp4_IsAPicture()
    {
        var info = Ok(VideoFixtures.Read("png-named.mp4"));
        Assert.Equal(MediaFormat.Png, info.Format);
        Assert.False(info.IsVideo);
        Assert.Null(info.Codec);
    }

    [Fact]
    public void AVideo_IsHeldToTheSizeLimits_NotTheFrameOrDurationLimits()
    {
        var bytes = VideoFixtures.Read("clip-h264.mp4");
        Assert.Empty(MediaValidator.Check(bytes, SpecLimits.Default with { MediaFrames = 2, MediaFrameArea = 1, MediaDuration = 0.1 }).Errors);
        Assert.Equal("limit.mediaSide", Refused(bytes, SpecLimits.Default with { MediaSide = 63 }));
        Assert.Equal("limit.mediaArea", Refused(bytes, SpecLimits.Default with { MediaArea = 64 * 48 - 1 }));
    }

    // ------------------------------------------------------------ hostile files

    /// <summary>The mov fixture (moov first) with its sample entry's width and height replaced.</summary>
    private static byte[] MovSized(int width, int height)
    {
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var fourcc = IndexOf(bytes, "avc1");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(fourcc + 28), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(fourcc + 30), (ushort)height);
        return bytes;
    }

    [Fact]
    public void AMoovClaiming9000x9000_IsOverTheSideLimit()
    {
        var info = VideoProbe.Inspect(MovSized(9000, 9000), out _);
        Assert.Equal((9000, 9000), (info!.Width, info.Height));
        Assert.Equal("limit.mediaSide", Refused(MovSized(9000, 9000)));
    }

    [Fact]
    public void AZeroSize_IsMalformed()
    {
        Assert.Equal(MediaProbe.MalformedCode, Refused(MovSized(0, 48)));
        Assert.Equal(MediaProbe.MalformedCode, Refused(MovSized(64, 0)));
    }

    [Fact]
    public void AnEmptyFile_IsNotMedia() => Assert.Equal(MediaProbe.FormatCode, Refused([]));

    [Fact]
    public void ATruncatedMp4_IsTruncated()
    {
        var bytes = VideoFixtures.Read("clip-h264.mp4");
        Assert.Equal(MediaProbe.TruncatedCode, Refused(bytes[..(bytes.Length / 2)]));
        Assert.Equal(MediaProbe.TruncatedCode, Refused(bytes[..(bytes.Length - 1)]));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void EveryPrefixOfAClip_IsRefusedOrRead_NeverThrows(string file, string format, string codec)
    {
        _ = (format, codec);
        var bytes = VideoFixtures.Read(file);
        for (var n = 0; n < bytes.Length; n++)
        {
            var check = MediaValidator.Check(bytes.AsSpan(0, n));
            if (check.Info is { } info)
                Assert.True(info.IsVideo, $"{file} cut at {n} read as {info.Format}");
            else
                Assert.Contains(check.Errors[0].Code, new[] { MediaProbe.TruncatedCode, MediaProbe.MalformedCode, MediaProbe.FormatCode });
        }
    }

    [Fact]
    public void EveryFixture_WithBytesChanged_IsRefusedOrRead_NeverThrows()
    {
        var random = new Random(20261031);
        foreach (var file in Directory.EnumerateFiles(VideoFixtures.Directory))
        {
            var original = File.ReadAllBytes(file);
            for (var round = 0; round < 300; round++)
            {
                var bytes = (byte[])original.Clone();
                // Header bytes mostly: the first 1 KiB and the moov of an mp4 whose moov is last.
                for (var k = 0; k < 4; k++)
                {
                    var at = random.Next(2) == 0 ? random.Next(Math.Min(1024, bytes.Length)) : random.Next(bytes.Length);
                    bytes[at] = (byte)random.Next(256);
                }
                var check = MediaValidator.Check(bytes);
                Assert.True(check.Info is not null || check.Errors.Count > 0);
            }
        }
    }

    private static byte[] Box(string type, params byte[][] content)
    {
        var payload = content.SelectMany(c => c).ToArray();
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static readonly byte[] Ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[4], Encoding.ASCII.GetBytes("isomiso2"));

    [Fact]
    public void BoxesNestedThousandsDeep_AreNotDescended()
    {
        var inner = Box("trak");
        for (var i = 0; i < 5000; i++) inner = Box("moov", inner);
        var bytes = Ftyp.Concat(inner).ToArray();
        Assert.Equal(MediaProbe.FormatCode, Refused(bytes)); // no video track: the moov holds only another moov
    }

    [Theory]
    [InlineData(4u, false)]
    [InlineData(7u, false)]
    [InlineData(0u, true)]
    [InlineData(15u, true)]
    public void ABoxSmallerThanItsHeader_IsMalformed(uint size, bool large)
    {
        var moov = large
            ? Box("moov", [0, 0, 0, 1], Encoding.ASCII.GetBytes("trak"), [0, 0, 0, 0, 0, 0, 0, (byte)size], new byte[8])
            : Box("moov", Box("trak", new byte[8]));
        if (!large) BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(8), size); // the trak's 32-bit size
        Assert.Equal(MediaProbe.MalformedCode, Refused(Ftyp.Concat(moov).ToArray()));
    }

    [Fact]
    public void AMediaDataBoxRunningToTheEnd_SwallowsTheMoov()
    {
        // Size 0: the box runs to the end of the file, so nothing after it is a box.
        var bytes = Ftyp.Concat(Box("mdat", new byte[16])).Concat(VideoFixtures.Read("clip-h264.mov")).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Ftyp.Length), 0);
        Assert.Equal(MediaProbe.MalformedCode, Refused(bytes));
    }

    [Fact]
    public void ManyBoxes_AreRefusedPastTheElementBudget()
    {
        var free = Box("free");
        var bytes = new byte[Ftyp.Length + free.Length * (VideoProbe.MaxElements + 1)];
        Ftyp.CopyTo(bytes, 0);
        for (var i = 0; i <= VideoProbe.MaxElements; i++) free.CopyTo(bytes, Ftyp.Length + i * free.Length);
        Assert.Equal(MediaProbe.MalformedCode, Refused(bytes));
    }

    [Fact]
    public void AnImageInAnIsoContainer_IsNotAVideo()
    {
        var avif = Box("ftyp", Encoding.ASCII.GetBytes("avif"), new byte[4], Encoding.ASCII.GetBytes("avifmif1"));
        Assert.Equal(MediaProbe.FormatCode, Refused(avif.Concat(Box("meta", new byte[8])).ToArray()));
    }

    [Fact]
    public void AMatroskaElementOfUnknownSizeOutsideASegmentOrCluster_IsMalformed()
    {
        var bytes = VideoFixtures.Read("clip-vp9.webm");
        var tracks = bytes.AsSpan().IndexOf(new byte[] { 0x16, 0x54, 0xAE, 0x6B });
        Assert.True(tracks > 0);
        // An 8-byte size of all ones is "unknown".
        var patched = bytes[..(tracks + 4)].Concat(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }).Concat(bytes[(tracks + 4)..]).ToArray();
        Assert.Equal(MediaProbe.MalformedCode, Refused(patched));
    }

    [Fact]
    public void AnEbmlFileThatIsNotMatroska_IsNotMedia()
    {
        byte[] bytes = [0x1A, 0x45, 0xDF, 0xA3, 0x87, 0x42, 0x82, 0x84, (byte)'f', (byte)'a', (byte)'k', (byte)'e'];
        Assert.Equal(MediaProbe.FormatCode, Refused(bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void APrefixOfTheEbmlSignature_IsTruncated(int length) =>
        Assert.Equal(MediaProbe.TruncatedCode, Refused(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }[..length]));

    [Fact]
    public void AnAviWithAShortStreamHeader_IsMalformed()
    {
        var bytes = VideoFixtures.Read("clip-mjpeg.avi");
        var strh = IndexOf(bytes, "strh");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(strh + 4), 8);
        Assert.Contains(Refused(bytes), new[] { MediaProbe.MalformedCode, MediaProbe.TruncatedCode });
    }

    [Fact]
    public void TheProbe_AllocatesNothingFromAClaimedSize()
    {
        // A moov claiming 2^62 bytes (a 64-bit size), and a sample table claiming 2^32 - 1 samples.
        var bytes = VideoFixtures.Read("clip-h264.mov");
        var moov = IndexOf(bytes, "moov") - 4;
        var huge = bytes[..moov].Concat(new byte[] { 0, 0, 0, 1 }).Concat(Encoding.ASCII.GetBytes("moov"))
            .Concat(new byte[] { 0x40, 0, 0, 0, 0, 0, 0, 0 }).Concat(bytes[(moov + 8)..]).ToArray();
        var stsz = VideoFixtures.Read("clip-h264.mov");
        BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(IndexOf(stsz, "stsz") + 12), uint.MaxValue);

        foreach (var file in new[] { huge, stsz })
        {
            MediaValidator.Check(file); // warm up
            var before = GC.GetAllocatedBytesForCurrentThread();
            MediaValidator.Check(file);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 64 * 1024, $"the probe allocated {allocated} bytes");
        }
        Assert.Equal(MediaProbe.TruncatedCode, Refused(huge));
        Assert.Equal(1, Ok(stsz).Frames); // a count past 2^31 - 1 is stated as 1
    }

    [Fact]
    public void Codecs_AreTheSpecsList() =>
        Assert.Equal(["h264", "hevc", "vp8", "vp9", "av1", "mpeg4", "mjpeg", "prores"], VideoProbe.Codecs);
}
