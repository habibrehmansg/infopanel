using System.Text;
using InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// Which model an init reply makes, against 1.4.x's own detection (the V1 oracle): the scan's probes
/// (<c>ThermalrightPanelHelper.cs</c>) and each session's start (<c>Services/ThermalrightPanelDeviceTask.cs</c>), at
/// c71f0f00, the lines cited in each test. A probe vector per model rule: every PM and SUB, identifier string, byte 20,
/// reported height and ALi device type, with a 1.4.x session's starting model as it had it.
/// </summary>
public sealed class ThermalrightDetectionTests
{
    private static ThermalrightCatalog Catalog => ThermalrightCatalog.Default;

    private static readonly string[] Identifiers = ["SSCRM-V1", "SSCRM-V3", "SSCRM-V4", "SPISCRM-V2", "BP21940", "", "QQQ"];

    private static readonly byte[] Subs = [0, 1, 2, 3, 4, 5, 6, 7, 0x20, 0x2E, 48, 49, 0xFF];

    private static V1DeviceTask Task(ThermalrightModel? model) =>
        new(new V1Device { Model = model == null ? ThermalrightPanelModel.Unknown : Enum.Parse<ThermalrightPanelModel>(model.V1Model) });

    private static void Same(V1DeviceTask v1, ThermalrightDetection detected, string because)
    {
        ThermalrightLookupTests.Same(v1._detectedModel, detected.Model, because);
        Assert.True((v1._panelWidth, v1._panelHeight) == (detected.Width, detected.Height),
            $"{because}: 1.4.x {v1._panelWidth}x{v1._panelHeight}, the port {detected.Width}x{detected.Height}");
    }

    private static byte[] ChiZhuReply(string identifier, byte pm, byte sub, int seed)
    {
        var reply = ThermalrightProtocolTests.Bytes(1024, seed);
        var id = new byte[16];
        Encoding.ASCII.GetBytes(identifier).CopyTo(id, 0);
        id.CopyTo(reply, 4);
        reply[24] = pm;
        reply[28] = sub;
        reply[17] = (byte)(seed % 2 == 0 ? 0x10 : 0x00);
        return reply;
    }

    private static IEnumerable<ThermalrightModel?> Starts(ThermalrightProtocol protocol) =>
        Catalog.Models.Where(m => m.Protocol == protocol).Cast<ThermalrightModel?>().Prepend(null);

    [Fact]
    public void ChiZhu_ScanProbe_EveryPmIdentifierAndSub()
    {
        // ThermalrightPanelHelper.cs:348-381
        var seed = 0;
        foreach (var identifier in Identifiers)
            for (var pm = 0; pm < 256; pm++)
                foreach (var sub in Subs)
                    foreach (var length in new[] { 11, 12, 24, 25, 28, 29, 1024 })
                    {
                        var reply = ChiZhuReply(identifier, (byte)pm, sub, seed++);
                        ThermalrightLookupTests.Same(V1PanelHelper.ProbeWinUsbModel(reply, length),
                            ThermalrightDetect.ChiZhuScan(Catalog, reply.AsSpan(0, length)), $"{identifier} PM {pm} SUB {sub} length {length}");
                    }
    }

    [Fact]
    public void ChiZhu_Session_ThreePasses_FromEveryStartingModel()
    {
        // ThermalrightPanelDeviceTask.cs:729-841 (f7ca672's three passes keep a PM-identified panel; 5a8bd39's SPISCRM-V2)
        var seed = 0;
        foreach (var start in new[] { null, Catalog.ByV1Model("PeerlessVision360"), Catalog.ByV1Model("WonderVision360"), Catalog.ByV1Model("StreamVision"), Catalog.ByV1Model("ChiZhuVision320x320") })
            foreach (var identifier in Identifiers)
                for (var pm = 0; pm < 256; pm++)
                    foreach (var sub in Subs)
                        foreach (var length in new[] { 0, 12, 29, 1024 })
                        {
                            var reply = ChiZhuReply(identifier, (byte)pm, sub, seed++);
                            var v1 = Task(start);
                            var booting = v1.ChiZhuReply(reply, length, out var serial, out var flags, out var disp);
                            var span = reply.AsSpan(0, length);
                            Assert.Equal(booting, ChiZhuProtocol.IsBooting(span));
                            Same(v1, ThermalrightDetect.ChiZhuSession(Catalog, start, span), $"from {start?.V1Model}: {identifier} PM {pm} SUB {sub} length {length}");
                            if (length > 0)
                            {
                                var parsed = ChiZhuProtocol.ParseReply(span);
                                Assert.Equal(serial, parsed.Serial);
                                Assert.Equal((flags, disp), (parsed.Flags, (parsed.Display1, parsed.Display2, parsed.Display3)));
                            }
                        }
    }

    [Fact]
    public void ChiZhu_Booting_IsSeen()
    {
        // :731-738: A1 A2 A3 A4 at bytes 4-7
        var reply = ChiZhuReply("", 4, 1, 1);
        (reply[4], reply[5], reply[6], reply[7]) = (0xA1, 0xA2, 0xA3, 0xA4);
        Assert.True(Task(null).ChiZhuReply(reply, 1024, out _, out _, out _));
        Assert.True(ChiZhuProtocol.IsBooting(reply));
        Assert.False(ChiZhuProtocol.IsBooting(reply.AsSpan(0, 7)));
        Assert.Null(ThermalrightDetect.ChiZhuScan(Catalog, reply));
    }

    [Fact]
    public void Hid_ScanModel_ForEveryId()
    {
        // ThermalrightPanelHelper.cs:136-146
        foreach (var (vid, pid) in ThermalrightPanelModelDatabase.SupportedDevices)
            ThermalrightLookupTests.Same(V1PanelHelper.HidScanModel(vid, pid), ThermalrightDetect.HidScan(Catalog, (ushort)vid, (ushort)pid), $"{vid:x4}:{pid:x4}");
    }

    [Fact]
    public void Hid_Session_EveryPmSubAndIdentifier()
    {
        // ThermalrightPanelDeviceTask.cs:1650-1735, from no model and from the scan's (TrofeoVision: ThermalrightPanelHelper.cs:144)
        var seed = 0;
        var scan = ThermalrightDetect.HidScan(Catalog, 0x0416, 0x5302);
        foreach (var start in new[] { null, scan })
            foreach (var identifier in new[] { "BP21940", "BP21940\a", "", "SSCRM-V1", "ZZ" })
                for (var pm = 0; pm < 256; pm++)
                    foreach (var sub in Subs)
                        foreach (var length in new[] { 0, 4, 5, 6, 13, 27, 28, 34, 35, 512 })
                        {
                            var reply = ThermalrightProtocolTests.Bytes(512, seed++);
                            (reply[0], reply[1], reply[2], reply[3]) = seed % 3 == 0 ? ((byte)1, (byte)2, (byte)3, (byte)4) : ((byte)0xDA, (byte)0xDB, (byte)0xDC, (byte)0xDD);
                            reply[4] = sub;
                            reply[5] = (byte)pm;
                            reply[15] = (byte)(seed % 2 == 0 ? 0x10 : 0);
                            var id = new byte[8];
                            Encoding.ASCII.GetBytes(identifier).CopyTo(id, 0);
                            id.CopyTo(reply, 20);
                            var response = reply.AsSpan(0, length).ToArray();

                            var v1 = Task(start);
                            v1.HidReply(response, out var magicOk, out var ackOk, out var serial);
                            Same(v1, ThermalrightDetect.HidSession(Catalog, start, response), $"from {start?.V1Model}: '{identifier}' PM {pm} SUB {sub} length {length}");
                            var parsed = TrofeoProtocol.ParseReply(response);
                            Assert.Equal((magicOk, ackOk, serial), (parsed.MagicOk, parsed.AckOk, parsed.Serial));
                        }
    }

    [Fact]
    public void TrofeoBulk_ScanProbe_EveryByte20()
    {
        // ThermalrightPanelHelper.cs:471-492 (689ee3e's byte 20 probe)
        for (var b20 = 0; b20 < 256; b20++)
            foreach (var length in new[] { 20, 21, 512 })
            {
                var reply = ThermalrightProtocolTests.Bytes(512, b20 * 7 + length);
                reply[20] = (byte)b20;
                ThermalrightLookupTests.Same(V1PanelHelper.ProbeTrofeoBulkModel(reply, length),
                    ThermalrightDetect.TrofeoBulkScan(Catalog, reply.AsSpan(0, length)), $"byte 20 {b20} length {length}");
            }
    }

    [Fact]
    public void TrofeoBulk_Session_Byte20AndReportedSize_FromEveryStartingModel()
    {
        // ThermalrightPanelDeviceTask.cs:1027-1118 (4d6b2d4's 11.3", 6b23229's 9.16" v2 at 480, the 462-row crop)
        var sizes = new (int Width, int Height)[] { (1920, 480), (1920, 599), (1920, 462), (1920, 400), (0, 480), (1920, 4097), (-1, -1) };
        var seed = 0;
        foreach (var start in Starts(ThermalrightProtocol.TrofeoBulk))
            for (var b20 = 0; b20 < 8; b20++)
                foreach (var (width, height) in sizes)
                    foreach (var length in new[] { 0, 9, 20, 21, 31, 32, 512 })
                    {
                        var reply = ThermalrightProtocolTests.Bytes(512, seed++);
                        BitConverter.GetBytes(width).CopyTo(reply, 24);
                        BitConverter.GetBytes(height).CopyTo(reply, 28);
                        reply[20] = (byte)b20;
                        var v1 = Task(start);
                        v1.TrofeoBulkReply(reply, length, out _);
                        var detected = ThermalrightDetect.TrofeoBulkSession(Catalog, start, reply.AsSpan(0, length));
                        var because = $"from {start?.V1Model}: byte 20 {b20}, {width}x{height}, length {length}";
                        Same(v1, detected, because);
                        Assert.True((v1._flickerFixCropHeight == 0 ? null : (int?)v1._flickerFixCropHeight) == detected.CropRows, because);

                        var parsed = TrofeoBulkProtocol.ParseReply(reply.AsSpan(0, length));
                        Assert.Equal(length >= 21 ? (byte?)b20 : null, parsed.Byte20);
                    }
    }

    [Fact]
    public void TrofeoBulkLy1_Session_TakesTheReportedSizeOnly_FromEveryStartingModel()
    {
        // ThermalrightPanelDeviceTask.cs:1323-1360: LY1 adopts the size the reply reports; unlike LY it never changes the
        // model by byte 20 or a reported height, and sets no crop (an LY1 9.16" whose byte 20 is 5 stays the 9.16").
        var sizes = new (int Width, int Height)[] { (1920, 480), (1920, 599), (1920, 462), (1920, 400), (0, 480), (1920, 4097), (-1, -1) };
        var seed = 0;
        foreach (var start in Starts(ThermalrightProtocol.TrofeoBulkLy1).Concat([Catalog.ByV1Model("TrofeoVision916"), Catalog.ByV1Model("TrofeoVision113")]))
            for (var b20 = 0; b20 < 8; b20++)
                foreach (var (width, height) in sizes)
                    foreach (var length in new[] { 0, 9, 20, 21, 31, 32, 511 })
                    {
                        var reply = ThermalrightProtocolTests.Bytes(511, seed++);
                        BitConverter.GetBytes(width).CopyTo(reply, 24);
                        BitConverter.GetBytes(height).CopyTo(reply, 28);
                        reply[20] = (byte)b20;
                        var v1 = Task(start);
                        v1.TrofeoBulkLy1Reply(reply, length, out _);
                        var detected = ThermalrightDetect.TrofeoBulkLy1Session(Catalog, start, reply.AsSpan(0, length));
                        var because = $"from {start?.V1Model}: byte 20 {b20}, {width}x{height}, length {length}";
                        Same(v1, detected, because);
                        Assert.True(v1._flickerFixCropHeight == 0 && detected.CropRows == null, because);
                    }
    }

    [Fact]
    public void TrofeoBulk_ScanThenSession_TheModelA9_16Or11_3Ends()
    {
        // The two together, as 1.4.x ran them: the scan's model is the session's start.
        foreach (var b20 in new byte[] { 0, 1, 2, 3, 4, 5, 6, 0xFF })
            foreach (var height in new[] { 480, 599, 400 })
            {
                var reply = new byte[512];
                (reply[0], reply[1], reply[8], reply[20]) = (0x03, 0xFF, 0x01, b20);
                BitConverter.GetBytes(1920).CopyTo(reply, 24);
                BitConverter.GetBytes(height).CopyTo(reply, 28);
                var v1Start = V1PanelHelper.ProbeTrofeoBulkModel(reply, 512);
                var v1 = new V1DeviceTask(new V1Device { Model = v1Start!.Model });
                v1.TrofeoBulkReply(reply, 512, out _);
                var start = ThermalrightDetect.TrofeoBulkScan(Catalog, reply);
                var detected = ThermalrightDetect.TrofeoBulkSession(Catalog, start, reply);
                Same(v1, detected, $"byte 20 {b20}, height {height}");
                Assert.Equal(v1._flickerFixCropHeight == 0 ? null : (int?)v1._flickerFixCropHeight, detected.CropRows);
            }
    }

    [Fact]
    public void Ali_Session_EveryDeviceType()
    {
        // ThermalrightPanelDeviceTask.cs:1508-1537
        foreach (var start in Starts(ThermalrightProtocol.Ali))
            for (var type = 0; type < 256; type++)
                foreach (var length in new[] { 0, 1, 2, 14 })
                {
                    var reply = ThermalrightProtocolTests.Bytes(1024, type + length);
                    reply[0] = (byte)type;
                    var v1 = Task(start);
                    var frameSize = v1.AliReply(reply, length, out _);
                    var detected = ThermalrightDetect.AliSession(Catalog, start, reply.AsSpan(0, length));
                    Same(v1, detected, $"from {start?.V1Model}: type {type} length {length}");
                    // No reply (length 0): 1.4's default frame size (:1508), whatever size the start keeps.
                    Assert.Equal(frameSize, ThermalrightDetect.AliFrameBytes(length, detected.Width, detected.Height));
                }
    }
}
