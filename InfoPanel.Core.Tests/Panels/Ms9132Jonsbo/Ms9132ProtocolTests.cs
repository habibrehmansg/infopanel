using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The MS9132's bytes against 1.4.x's own code (the oracles in <c>V1Oracle/</c>, 1.4.x c71f0f00): the Jonsbo DS339's
/// startup block, mode set, output enable, status, power-off, pauses and frame (JonsboMs9132Device.cs), the VMAX 4.6"'s
/// streaming start, screen switch, pauses and frame (VmaxUsbDevice.cs), the feature report's layout, the write endpoint's
/// choice, and the EDID parser on blocks of the values 1.4.x names.
/// </summary>
public sealed class Ms9132ProtocolTests
{
    private static byte[] Payload(byte[] report9) => report9[1..];

    [Fact]
    public void JonsboStartup_Is1_4sBlock_AndItsLast32ReportsReadTheEdid()
    {
        // JonsboMs9132Device.cs:50-77: 24 reports, then B5 C0 00 to B5 C0 7C.
        Assert.Equal(V1JonsboMs9132Device.StartupReports, JonsboMs9132.Startup);
        Assert.Equal(56, JonsboMs9132.Startup.Count);
        Assert.Equal(V1JonsboMs9132Device.StartupReports[24..], Ms9132Protocol.EdidReads);
        // :220-221: a report is an EDID read when it is B5 C0 xx, xx + 4 within the block.
        foreach (var report in JonsboMs9132.Startup.Concat(VmaxMs9132.Startup))
            Assert.Equal(report[0] == 0xB5 && report[1] == 0xC0 && report[2] + 4 <= 128, Ms9132Protocol.IsEdidRead(report));
    }

    [Fact]
    public void JonsboEnableVideoStatusAndPowerOff_Are1_4s()
    {
        // :80-88, :361-365
        Assert.Equal(V1JonsboMs9132Device.EnableVideoReports, JonsboMs9132.EnableVideo);
        Assert.Equal(V1JonsboMs9132Device.PanelStatusReport, JonsboMs9132.PanelStatus.ToArray());
        var oracle = new V1JonsboMs9132Device();
        oracle.PowerOff();
        Assert.Equal(Payload(Assert.Single(oracle.Reports)), JonsboMs9132.PowerOff.ToArray());
        Assert.Equal(EnableVideoAfter2(), JonsboMs9132.EnableVideoAfter);
    }

    // JonsboPanelDeviceTask.cs:394: the frame after which the oracle's loop turns the output on.
    private static int EnableVideoAfter2()
    {
        var oracle = new V1JonsboMs9132Device();
        var sent = 0;
        for (var frame = 1; frame <= 3; frame++)
        {
            V1JonsboPanelDeviceTask.SendOne(oracle, new byte[16 * 1 * 3], 16, 1, ref sent);
            if (oracle.Reports.Any(r => r[1] == 0xA6 && r[2] == 0x05 && r[3] == 0x01))
                return frame;
        }
        return 0;
    }

    public static TheoryData<int, int, byte> Modes()
    {
        var data = new TheoryData<int, int, byte>();
        foreach (var ((width, height), vic) in V1JonsboMs9132Device.VicByResolution)
            data.Add(width, height, vic);
        data.Add(1920, 1080, 16); // a size the table hasn't, at any index
        return data;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void SetMode_Is1_4s_ForEverySize(int width, int height, byte vic)
    {
        // :326-344: nine reports, then the 400 ms before the first frame.
        var oracle = new V1JonsboMs9132Device();
        oracle.SetMode(width, height, vic);

        Assert.Equal(oracle.Reports.Select(Payload), JonsboMs9132.SetMode(width, height, vic));
        Assert.Equal(400, oracle.Delays[^1]);
        Assert.Equal(TimeSpan.FromMilliseconds(oracle.Delays[^1]), JonsboMs9132.FirstFrameDelay);
    }

    [Fact]
    public void ModeIndex_Is1_4sTable()
    {
        // :95-109
        foreach (var ((width, height), vic) in V1JonsboMs9132Device.VicByResolution)
            Assert.Equal(vic, JonsboMs9132.VicOf(width, height));
        foreach (var (width, height) in new[] { (960, 376), (1920, 1080), (0, 0), (480, 481), (462, 1920) })
            Assert.Equal(V1JonsboMs9132Device.GetVicForResolution(width, height), JonsboMs9132.VicOf(width, height));
    }

    [Fact]
    public void JonsboPauses_Are1_4s_ForEveryReportItSends()
    {
        // :290-303
        IEnumerable<byte[]> reports = [.. JonsboMs9132.Startup, .. JonsboMs9132.EnableVideo, .. JonsboMs9132.SetMode(376, 960, 171),
            JonsboMs9132.PanelStatus.ToArray(), JonsboMs9132.PowerOff.ToArray(), [0xA6, 0x03, 0, 0, 0, 0, 0, 0], [0xB5, 0x00, 0x31, 0, 0, 0, 0, 0]];
        foreach (var report in reports)
            Assert.Equal(V1JonsboMs9132Device.CommandDelay(report), JonsboMs9132.DelayMs(report));
    }

    [Fact]
    public void AFeatureReport_IsReportIdZeroThenThePayload_AndItsReplyDataIsBytes4To8()
    {
        // :276-286
        var oracle = new V1JonsboMs9132Device { GetFeature = (_, reply) => { for (var i = 0; i < 9; i++) reply[i] = (byte)(0xA0 + i); } };
        foreach (var report in JonsboMs9132.Startup.Take(12))
        {
            var data = oracle.SendReport(report);
            Assert.Equal(oracle.Reports[^1], Ms9132Protocol.Feature(report));
            byte[] reply = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8];
            Assert.Equal(data, Ms9132Protocol.ReplyData(reply).ToArray());
        }
        Assert.Equal(9, Ms9132Protocol.FeatureLength);
        Assert.Equal(1000, Ms9132Protocol.HidTimeoutMs); // :160-161 (VmaxUsbDevice.cs:89,214-215)
    }

    public static TheoryData<string> EdidCases() => ["ds339", "vmax", "unknown", "nameNoNewline", "noName", "zeroWidth", "badHeader", "tall", "nameLate"];

    private static byte[] EdidOf(string name) => name switch
    {
        "ds339" => EdidSamples.Ds339,
        "vmax" => EdidSamples.Vmax,
        "unknown" => EdidSamples.Unknown,
        "nameNoNewline" => EdidSamples.Block(376, 960, 2916, "ABCDEFGHIJKLM"),
        "noName" => EdidSamples.Block(240, 240, 900),
        "zeroWidth" => EdidSamples.Malformed.ZeroWidth,
        "badHeader" => EdidSamples.Malformed.BadHeader,
        "tall" => EdidSamples.Block(600, 1600, 5000, "TALL"), // in 1.4.x's mode table; its height has bit 10 set
        "nameLate" => EdidSamples.Block(320, 240, 640, "LATE", nameAt: 0x6C),
        "fullHd" => EdidSamples.Block(1920, 1080, 14850, "MONITOR"),
        _ => throw new ArgumentException(name),
    };

    public static TheoryData<string> ValidEdidCases() => ["ds339", "vmax", "unknown", "nameNoNewline", "noName", "tall", "nameLate", "fullHd"];

    [Theory]
    [MemberData(nameof(ValidEdidCases))]
    public void TheSyntheticEdids_AreValidBlocks_OfEdid1_3(string name)
    {
        // 1.4.x has no captured EDID: the samples are synthetic, built from the values 1.4.x names, but each a valid block 0.
        var edid = EdidOf(name);
        Assert.Equal([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00], edid[..8]);
        var manufacturer = (edid[8] << 8) | edid[9];
        Assert.Equal(0, manufacturer >> 15);
        foreach (var shift in new[] { 10, 5, 0 })
            Assert.InRange((manufacturer >> shift) & 0x1F, 1, 26); // three letters A to Z
        Assert.Equal((1, 3), (edid[18], edid[19]));
        Assert.Equal(0, edid.Sum(b => b) % 256);
        // The preferred timing: a pixel clock, and each sync's offset and width inside its blanking.
        var t = edid.AsSpan(0x36, 18).ToArray();
        Assert.True(((t[1] << 8) | t[0]) > 0);
        var (hBlank, vBlank) = (((t[4] & 0x0F) << 8) | t[3], ((t[7] & 0x0F) << 8) | t[6]);
        var (hOffset, hWidth) = (((t[11] & 0xC0) << 2) | t[8], ((t[11] & 0x30) << 4) | t[9]);
        var (vOffset, vWidth) = (((t[11] & 0x0C) << 2) | (t[10] >> 4), ((t[11] & 0x03) << 4) | (t[10] & 0x0F));
        Assert.True(hOffset > 0 && hWidth > 0 && hOffset + hWidth <= hBlank, $"horizontal sync {hOffset}+{hWidth} in {hBlank}");
        Assert.True(vOffset > 0 && vWidth > 0 && vOffset + vWidth <= vBlank, $"vertical sync {vOffset}+{vWidth} in {vBlank}");
        Assert.Equal(0x18, t[17] & 0x18); // digital separate sync
    }

    [Theory]
    [MemberData(nameof(EdidCases))]
    public void TheEdid_AssembledFromTheStartupBlocksReplies_AndParsed_Is1_4s(string name)
    {
        // :211-266: each B5 C0 xx's reply gives 4 bytes at xx; the block is parsed only past its header check.
        var block = EdidOf(name);
        var oracle = new V1JonsboMs9132Device { GetFeature = (report, reply) => EdidSamples.Reply(report, block).CopyTo(reply, 0) };
        oracle.RunStartupBlock();

        var edid = new byte[128];
        foreach (var report in JonsboMs9132.Startup)
            if (Ms9132Protocol.IsEdidRead(report))
                Ms9132Protocol.TakeEdid(report, EdidSamples.Reply(Ms9132Protocol.Feature(report), block), edid);
        Assert.Equal(block, edid);

        var parsed = Edid.Parse(edid);
        Assert.Equal(oracle.Edid != null, parsed != null);
        if (parsed == null)
            return;
        Assert.Equal(oracle.Edid, parsed.Block);
        Assert.Equal(oracle.EdidResolution, parsed.HasSize ? (parsed.Width, parsed.Height) : null);
        Assert.Equal(oracle.EdidPanelName, parsed.Name);
        // 3939e56: the summary and the whole block, as 1.4.x logged them.
        var described = parsed.Describe();
        Assert.Contains(oracle.EdidLog[0]["JonsboMs9132: EDID reports ".Length..].Split(',')[0].Split(" @ ")[1], described, StringComparison.Ordinal);
        Assert.Contains($"panel name {oracle.EdidPanelName ?? "(none)"}", described, StringComparison.Ordinal);
        Assert.EndsWith(oracle.EdidLog[1]["JonsboMs9132: EDID block 0: ".Length..], described, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEdidsOfTheTwoPanels_GiveTheirSizesAndNames()
    {
        var ds339 = Edid.Parse(EdidSamples.Ds339)!;
        Assert.Equal((376, 960, "VMAXB01"), (ds339.Width, ds339.Height, ds339.Name));
        Assert.StartsWith("EDID 376x960 at 29.16 MHz, panel name VMAXB01; block 0: 00FFFFFFFFFFFF00", ds339.Describe(), StringComparison.Ordinal);
        var vmax = Edid.Parse(EdidSamples.Vmax)!;
        Assert.Equal((320, 960, (string?)null), (vmax.Width, vmax.Height, vmax.Name));
        // 1.4.x's height mask (0x30) drops bit 10: a 600 × 1600 panel reads 600 × 576, in 1.4.x and here alike.
        var tall = Edid.Parse(EdidSamples.Block(600, 1600, 5000, "TALL"))!;
        Assert.Equal((600, 576), (tall.Width, tall.Height));
    }

    public static TheoryData<int, int> Frames() => new() { { 376, 960 }, { 320, 960 }, { 480, 480 }, { 16, 2 } };

    [Theory]
    [MemberData(nameof(Frames))]
    public void JonsboFrame_IsItsHeaderPixelsAndTrailer_In64KiBWrites_As1_4(int width, int height)
    {
        // :371-422
        var bgr = Enumerable.Range(0, width * height * 3).Select(i => (byte)(i * 7 + 3)).ToArray();
        var oracle = new V1JonsboMs9132Device();
        oracle.SendFrame(bgr, width, height);

        var frame = new byte[Ms9132Protocol.FrameLength(width, height)];
        JonsboMs9132.Frame(bgr, width, height, frame);
        Assert.Equal(oracle.Chunks, Chunked(frame));
        Assert.Equal(V1JonsboMs9132Device.WriteTimeout, JonsboMs9132.WriteTimeoutMs);
    }

    [Fact]
    public void TheDs339sFrame_Is17WritesOf1_082_896Bytes()
    {
        // JonsboMs9132Device.cs:25-26: "17 URBs, 1,082,896 B/frame", from the vendor app's capture.
        Assert.Equal(1_082_896, Ms9132Protocol.FrameLength(376, 960));
        Assert.Equal(17, Chunked(new byte[Ms9132Protocol.FrameLength(376, 960)]).Count);
    }

    private static List<byte[]> Chunked(byte[] frame) =>
        [.. Enumerable.Range(0, (frame.Length + Ms9132Protocol.ChunkSize - 1) / Ms9132Protocol.ChunkSize)
            .Select(i => frame[(i * Ms9132Protocol.ChunkSize)..Math.Min(frame.Length, (i + 1) * Ms9132Protocol.ChunkSize)])];

    [Fact]
    public void VmaxStart_Is1_4sStreamingStart()
    {
        // VmaxUsbDevice.cs:15-91
        Assert.Equal(V1VmaxUsbDevice.StreamingStartupFeatureReports, VmaxMs9132.Startup);
        Assert.Equal(V1VmaxUsbDevice.StreamingPulseFeatureReport, VmaxMs9132.Pulse.ToArray());
        Assert.Equal(V1VmaxUsbDevice.DisplayEnableFeatureReport, VmaxMs9132.DisplayEnable.ToArray());
        Assert.Equal(V1VmaxUsbDevice.StreamingStatusFeatureReport, VmaxMs9132.Status.ToArray());
        Assert.Equal(V1VmaxUsbDevice.FramePrefix, VmaxMs9132.Prefix.ToArray());
        Assert.Equal(V1VmaxUsbDevice.FrameSuffix, Ms9132Protocol.Trailer.ToArray());
        Assert.Equal((V1VmaxUsbDevice.InterfaceNumber, V1VmaxUsbDevice.UsbWriteTimeoutMs, V1VmaxUsbDevice.UsbWriteChunkSize, V1VmaxUsbDevice.StreamingInitDelayMs),
            (Ms9132Protocol.VideoInterface, VmaxMs9132.WriteTimeoutMs, Ms9132Protocol.ChunkSize, VmaxMs9132.StreamingDelayMs));

        // :226-230: 42 pulses, 2 ms apart.
        var oracle = new V1VmaxUsbDevice();
        oracle.InitializeStreamingMode();
        Assert.Equal(VmaxMs9132.Pulses, oracle.Reports.Skip(V1VmaxUsbDevice.StreamingStartupFeatureReports.Length)
            .TakeWhile(r => Payload(r).SequenceEqual(V1VmaxUsbDevice.StreamingPulseFeatureReport)).Count());
        Assert.Equal(VmaxMs9132.PulseGapMs, oracle.Delays[V1VmaxUsbDevice.StreamingStartupFeatureReports.Length]);
    }

    [Fact]
    public void VmaxPausesAndScreenSwitch_Are1_4s()
    {
        // :272-293, :367-373
        foreach (var on in new[] { true, false })
            Assert.Equal(V1VmaxUsbDevice.GetScreenSwitchFeatureReports(on), VmaxMs9132.ScreenSwitch(on));
        foreach (var report in VmaxMs9132.Startup.Concat(VmaxMs9132.ScreenSwitch(true)).Concat(VmaxMs9132.ScreenSwitch(false)))
            Assert.Equal(V1VmaxUsbDevice.StartupDelay(report), VmaxMs9132.DelayMs(report));
        Assert.Equal(TimeSpan.FromMilliseconds(500), VmaxMs9132.StatusEvery); // :381
    }

    [Fact]
    public void VmaxFrame_IsItsFixedHeaderPixelsAndTrailer_In64KiBWrites_As1_4()
    {
        // :308-339
        var bgr = Enumerable.Range(0, 320 * 960 * 3).Select(i => (byte)(i * 13 + 1)).ToArray();
        var oracle = new V1VmaxUsbDevice();
        oracle.SendRgb888Frame(bgr, bgr.Length);

        var frame = new byte[Ms9132Protocol.FrameLength(320, 960)];
        VmaxMs9132.Frame(bgr, frame);
        Assert.Equal(oracle.Chunks, Chunked(frame));
        Assert.Equal(15, oracle.Chunks.Count);
    }

    public static TheoryData<byte[]> EndpointLists() => new()
    {
        new byte[] { 0x84, 0x04 },
        new byte[] { 0x02 },
        new byte[] { 0x01, 0x02 },
        Array.Empty<byte>(),
        new byte[] { 0x81 },
        new byte[] { 0x03, 0x04, 0x05 },
        new byte[] { 0x83, 0x05, 0x06 },
    };

    [Theory]
    [MemberData(nameof(EndpointLists))]
    public void TheWriteEndpoint_Is1_4sChoice(byte[] addresses)
    {
        // VmaxUsbDevice.cs:164-192, over the video interface's endpoints.
        var endpoints = addresses.Select(a => new PanelUsbEndpoint(a, PanelEndpointType.Bulk, 512)).ToList();
        Assert.Equal(V1VmaxUsbDevice.FindWriteEndpoint([(3, addresses)]), Ms9132Protocol.WriteEndpoint(endpoints));
    }
}
