using InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The packets and the reply parsers of the five protocols, byte for byte against 1.4.x's own code (the V1 oracle):
/// <c>Services/ThermalrightPanelDeviceTask.cs</c> and <c>ThermalrightPanel/HidPanelDevice.cs</c> at c71f0f00, the lines
/// cited in each test.
/// </summary>
public sealed class ThermalrightProtocolTests
{
    private static ThermalrightCatalog Catalog => ThermalrightCatalog.Default;

    internal static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static V1DeviceTask Task(ThermalrightModel? model) =>
        new(new V1Device { Model = model == null ? ThermalrightPanelModel.Unknown : Enum.Parse<ThermalrightPanelModel>(model.V1Model) });

    // Lengths around every packet boundary the protocols have: 512-byte packets with a 20-byte header (492), 496-byte
    // sub-packets, 64-byte ChiZhu headers (448 + 64 = 512), four-chunk padding (1984), 4096-byte transfers.
    public static TheoryData<int> Lengths => new(0, 1, 447, 448, 449, 491, 492, 493, 495, 496, 497, 960, 1004, 1005, 1983, 1984, 1985, 4032, 4096, 8128, 10000, 153600, 229999);

    [Fact]
    public void ChiZhu_InitCommand()
    {
        // :82-97
        Assert.Equal(Task(null).BuildInitCommand(), ChiZhuProtocol.InitCommand());
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ChiZhu_EveryModelsFrame_AndItsZeroLengthPacket(int length)
    {
        // :103-134 (the header), :853-867 (the packet; a zero-length packet after one whose length is a multiple of 512)
        var frame = Bytes(length, length);
        foreach (var model in Catalog.Models.Where(m => m.Protocol == ThermalrightProtocol.ChiZhu))
        {
            var v1 = Task(model);
            var zlp = v1.ChiZhuFrame(frame);
            Assert.Equal(v1.Writes[0], ChiZhuProtocol.FramePacket(frame, model.Width, model.Height, model.Pixels));
            Assert.Equal(v1.BuildDisplayHeader(length), ChiZhuProtocol.DisplayHeader(model.Width, model.Height, length, model.Pixels));
            Assert.Equal(zlp, ChiZhuProtocol.NeedsZeroLengthPacket(v1.Writes[0].Length));
            Assert.Equal(zlp ? 2 : 1, v1.Writes.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Trofeo_Frame_AsHidReportsAndOverBulk(int length)
    {
        // HidPanelDevice.cs:137-190 (JPEG) and :197-245 (RGB565), with the report id :250-260; over bulk :1213-1265.
        var frame = Bytes(length, length + 1);
        foreach (var (width, height) in new[] { (1280, 480), (240, 320), (320, 320), (65535, 1) })
        {
            foreach (var pixels in new[] { ThermalrightPixels.Jpeg, ThermalrightPixels.Rgb565Le, ThermalrightPixels.Rgb565Be })
            {
                var packets = TrofeoProtocol.FramePackets(frame, width, height, pixels);

                var hid = new V1HidPanelDevice();
                if (pixels == ThermalrightPixels.Jpeg)
                    hid.SendJpegFrame(frame, width, height);
                else
                    hid.SendRgb565Frame(frame, width, height);
                Assert.Equal(hid.Reports, packets.Select(p => TrofeoProtocol.HidReport(p)).ToList());

                var bulk = Task(null);
                bulk.SendTrofeoFrameOverBulk(frame, width, height, pixels switch
                {
                    ThermalrightPixels.Jpeg => ThermalrightPixelFormat.Jpeg,
                    ThermalrightPixels.Rgb565Le => ThermalrightPixelFormat.Rgb565,
                    _ => ThermalrightPixelFormat.Rgb565BigEndian,
                });
                Assert.Equal(bulk.Writes, packets);
            }
        }
    }

    [Fact]
    public void Trofeo_InitPacket_AndTheReportIdStripped()
    {
        // HidPanelDevice.cs:73-95, :101-130
        var hid = new V1HidPanelDevice();
        hid.SendInit();
        Assert.Equal(hid.Reports[0], TrofeoProtocol.HidReport(TrofeoProtocol.InitPacket()));
        foreach (var length in new[] { 0, 1, 2, 513 })
        {
            var read = Bytes(length, length);
            Assert.Equal(V1HidPanelDevice.ReadInitResponse(read, length), TrofeoProtocol.FromHidReport(read));
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void TrofeoBulk_LyFrame_In4096ByteTransfers(int length)
    {
        // :1139-1184
        if (length == 0)
            return; // 1.4.x never sends an empty JPEG, and frames it as nothing
        var jpeg = Bytes(length, length + 2);
        var v1 = Task(null);
        v1.TrofeoBulkFrame(jpeg);
        var frame = TrofeoBulkProtocol.LyFrame(jpeg);
        var transfers = TrofeoBulkProtocol.LyTransfers(frame.Length).Select(t => frame.AsSpan(t.Offset, t.Length).ToArray()).ToList();
        Assert.Equal(v1.Writes, transfers);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void TrofeoBulk_Ly1Frame(int length)
    {
        // :1400-1459, the whole frame in one write (the remainder after a short write is the driver's)
        if (length == 0)
            return;
        var jpeg = Bytes(length, length + 3);
        var v1 = Task(null);
        v1.TrofeoBulkLY1WriteFrame(jpeg, 512, 16, 496);
        Assert.Equal(Assert.Single(v1.Writes), TrofeoBulkProtocol.Ly1Frame(jpeg));
    }

    [Fact]
    public void TrofeoBulk_InitPackets()
    {
        // :979-983 (LY, 2048 bytes), :1288-1292 (LY1, 512)
        Assert.Equal(V1DeviceTask.TrofeoBulkInit(), TrofeoBulkProtocol.InitPacket(ThermalrightProtocol.TrofeoBulk));
        Assert.Equal(V1DeviceTask.TrofeoBulkLy1Init(), TrofeoBulkProtocol.InitPacket(ThermalrightProtocol.TrofeoBulkLy1));
    }

    [Fact]
    public void TrofeoBulk_Reply_HeaderAndSerial()
    {
        // :1027-1043
        foreach (var length in new[] { 0, 1, 8, 9, 19, 20, 32, 512 })
        {
            foreach (var good in new[] { true, false })
            {
                var reply = Bytes(512, length * 2 + (good ? 1 : 0));
                if (good)
                    (reply[0], reply[1], reply[8]) = (0x03, 0xFF, 0x01);
                var v1 = Task(null);
                var headerOk = v1.TrofeoBulkReply(reply, length, out var serial);
                var parsed = TrofeoBulkProtocol.ParseReply(reply.AsSpan(0, length));
                Assert.Equal(headerOk, parsed.HeaderOk);
                Assert.Equal(serial, parsed.Serial);
            }
        }
    }

    [Fact]
    public void Ali_InitPacket_AndFrames()
    {
        // :1477-1481 (init), :1555-1576 (frame). 1.4.x's packet is rented uncleared, so only frames of at least the
        // frame size (all 1.4.x ever sent) are compared: a shorter one is Ali_ShortFrame_IsPaddedWithZeros.
        Assert.Equal(V1DeviceTask.AliInit(), AliProtocol.InitPacket());
        foreach (var frameSize in new[] { 153600, 204800 })
        {
            foreach (var length in new[] { frameSize, frameSize + 7 })
            {
                var pixels = Bytes(length, length);
                var v1 = Task(null);
                v1.AliFrame(pixels, frameSize);
                Assert.Equal(Assert.Single(v1.Writes), AliProtocol.FramePacket(pixels, frameSize));
            }
        }
    }

    [Fact]
    public void Ali_ShortFrame_IsPaddedWithZeros()
    {
        // The port's own rule (1.4.x would send what its pooled buffer held): the header, the pixels, then zeros up to
        // the frame size.
        foreach (var length in new[] { 0, 10, 153599 })
        {
            var pixels = Bytes(length, length + 11);
            var packet = AliProtocol.FramePacket(pixels, 153600);
            Assert.Equal(16 + 153600, packet.Length);
            Assert.Equal(pixels, packet.AsSpan(16, length).ToArray());
            Assert.True(packet.AsSpan(16 + length).IndexOfAnyExcept((byte)0) < 0, $"length {length}");
        }
    }

    [Fact]
    public void TrofeoBulk_Frames_RefuseAJpegTheirPieceCountCantHold()
    {
        // A 16-bit piece count (1.4.x's `(ushort)totalChunks`, :1152, :1419) holds 65,535 pieces of 496 bytes: a longer
        // frame is refused before anything is built, never sent with its count wrapped. The driver's cap (230,000 bytes)
        // keeps every frame it writes far below it.
        var most = new byte[65_535 * TrofeoBulkProtocol.SubDataSize];
        Assert.Equal(65_535, BitConverter.ToUInt16(TrofeoBulkProtocol.Ly1Frame(most), 9));
        var over = new byte[most.Length + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => TrofeoBulkProtocol.Ly1Frame(over));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrofeoBulkProtocol.LyFrame(over));
    }

    [Fact]
    public void Ali_Reply_Serial()
    {
        // :1539-1545
        foreach (var length in new[] { 1, 2, 13, 14, 1024 })
        {
            var reply = Bytes(1024, length);
            Task(null).AliReply(reply, length, out var serial);
            var parsed = AliProtocol.ParseReply(reply.AsSpan(0, length));
            Assert.Equal(serial, parsed.Serial);
            Assert.Equal(reply[0], parsed.DeviceType);
        }
    }
}
