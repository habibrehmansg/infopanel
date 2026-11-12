using System.Text;
using System.Text.Json;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The conformance suite (§2.2 (6)) against the Thermalright driver over the kit's fakes, for every protocol it drives and
/// each format a model of it takes: ChiZhu (JPEG; RGB565 big-endian), Trofeo over HID (JPEG; RGB565 little-endian by
/// truncation; RGB565 big-endian), TrofeoBulk LY (with the flicker fix's crop) and LY1, and ALi; a 6.67" ChiZhu panel with
/// its display mask's overlays. Each probe's writes are its protocol's init and nothing else.
/// </summary>
public sealed class ThermalrightConformanceTests
{
    private static byte[] ChiZhuReply(string identifier, byte pm, byte sub)
    {
        var reply = new byte[48];
        (reply[0], reply[1], reply[2], reply[3]) = (0x12, 0x34, 0x56, 0x78);
        Encoding.ASCII.GetBytes(identifier).CopyTo(reply, 4);
        (reply[24], reply[28]) = (pm, sub);
        return reply;
    }

    private static byte[] HidReply(byte pm, byte sub)
    {
        var report = new byte[64];
        (report[1], report[2], report[3], report[4]) = (0xDA, 0xDB, 0xDC, 0xDD);
        (report[1 + 4], report[1 + 5], report[1 + 12]) = (sub, pm, 1);
        return report;
    }

    private static byte[] TrofeoBulkReply(int length, byte byte20, int width, int height)
    {
        var reply = new byte[length];
        (reply[0], reply[1], reply[8], reply[20]) = (0x03, 0xFF, 0x01, byte20);
        BitConverter.GetBytes(width).CopyTo(reply, 24);
        BitConverter.GetBytes(height).CopyTo(reply, 28);
        return reply;
    }

    /// <summary>The driver with a panel's family settings in every start, as the engine passes a saved panel's.</summary>
    private sealed class WithSettings(ThermalrightDriver driver, string settings) : IPanelDriver
    {
        private readonly JsonElement _settings = JsonDocument.Parse(settings).RootElement.Clone();

        public string Family => driver.Family;

        public IReadOnlyList<PanelMatch> Matches => driver.Matches;

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop) => driver.Probe(device, stop);

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop) =>
            driver.Open(device, identity, options with { FamilySettings = _settings }, stop);
    }

    public static TheoryData<string> Panels => ["chiZhu-jpeg", "chiZhu-rgb565be", "chiZhu-mask", "hid-jpeg", "hid-rgb565le", "hid-rgb565be", "ly-flickerFix", "ly1", "ali"];

    private static ConformanceSetup Setup(string panel)
    {
        var devices = new FakePanelDevices();
        var driver = new ThermalrightDriver(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };
        PanelCandidate candidate;
        FakePanelDevice device;
        byte[] probeWrite;
        var settings = "{}";
        switch (panel)
        {
            case "chiZhu-jpeg" or "chiZhu-rgb565be" or "chiZhu-mask":
                candidate = new PanelCandidate(0x87AD, 0x70DB, 1, 2, "1-2");
                device = devices.Add(candidate);
                var chiZhu = panel switch
                {
                    "chiZhu-jpeg" => ChiZhuReply("SSCRM-V1", 4, 1), // the Hyper Vision, 480 × 480 JPEG
                    "chiZhu-rgb565be" => ChiZhuReply("", 32, 0), // the ChiZhu Vision 320 × 320, RGB565 big-endian
                    _ => ChiZhuReply("SSCRM-V3", 0, 1), // the Wonder Vision 360, 1600 × 720, with a mask
                };
                device.Usb.Respond(_ => chiZhu);
                probeWrite = ChiZhuProtocol.InitCommand();
                if (panel == "chiZhu-mask")
                    settings = """{"displayMask": "RoundedAll"}""";
                break;
            case "hid-jpeg" or "hid-rgb565le" or "hid-rgb565be":
                candidate = new PanelCandidate(0x0416, 0x5302, 1, 3, "1-3");
                device = devices.Add(candidate, PanelTransport.Hid);
                (device.Hid.MaxOutputReportLength, device.Hid.MaxInputReportLength) = (513, 513);
                var hid = panel switch
                {
                    "hid-jpeg" => HidReply(128, 0), // the Trofeo Vision 6.86", 1280 × 480 JPEG
                    "hid-rgb565le" => HidReply(58, 0), // the Frozen Warframe SE, 240 × 320 RGB565 little-endian
                    _ => HidReply(32, 0), // the Trofeo Vision 320 × 320, RGB565 big-endian
                };
                device.Hid.Respond(_ => hid);
                probeWrite = TrofeoProtocol.HidReport(TrofeoProtocol.InitPacket());
                break;
            case "ly-flickerFix":
                candidate = new PanelCandidate(0x0416, 0x5408, 1, 4, "1-4");
                device = devices.Add(candidate);
                var ly = TrofeoBulkReply(TrofeoBulkProtocol.ReplySize, 1, 1920, 480); // the 9.16", its ack too
                device.Usb.Respond(_ => ly);
                probeWrite = TrofeoBulkProtocol.InitPacket(ThermalrightProtocol.TrofeoBulk);
                settings = """{"flickerFix": true}""";
                break;
            case "ly1":
                candidate = new PanelCandidate(0x0416, 0x5409, 1, 5, "1-5");
                device = devices.Add(candidate);
                var ly1 = TrofeoBulkReply(TrofeoBulkProtocol.Ly1ReplySize, 0, 1920, 480);
                device.Usb.Respond(_ => ly1);
                probeWrite = []; // LY1's probe sends nothing
                break;
            case "ali":
                candidate = new PanelCandidate(0x0416, 0x5406, 1, 6, "1-6");
                device = devices.Add(candidate);
                var ali = new byte[AliProtocol.AckSize];
                ali[0] = 101; // 320 × 320; its ack too
                device.Usb.Respond(_ => ali);
                probeWrite = AliProtocol.InitPacket();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(panel), panel, null);
        }
        return new ConformanceSetup(new WithSettings(driver, settings), candidate, device)
        {
            ProbeWrite = probeWrite.Length == 0 ? _ => false : Conformance.ProbeWrites(probeWrite),
        };
    }

    [Theory]
    [MemberData(nameof(Panels))]
    public void TheDriver_PassesTheConformanceSuite(string panel) =>
        // ALi's transfers wait 100 ms at most (1.4's, :1562-1586): the slow transfer the suite times is made slower than
        // the default's 100 ms by half, so it is slow and still within the protocol's timeout.
        Assert.Empty(Conformance.Check(() => Setup(panel), panel == "ali" ? new ConformanceOptions { TransferDelay = TimeSpan.FromMilliseconds(50) } : null));

    [Theory]
    [InlineData("chiZhu-rgb565be", PanelFrameFormat.Rgb565Be)]
    [InlineData("hid-rgb565le", PanelFrameFormat.Rgb565LeTruncated)]
    [InlineData("hid-rgb565be", PanelFrameFormat.Rgb565Be)]
    [InlineData("ali", PanelFrameFormat.Rgb565LeTruncated)]
    [InlineData("ly1", PanelFrameFormat.Jpeg)]
    public void EachSetup_IsTheModelAndFormatItNames(string panel, PanelFrameFormat format)
    {
        // The suite's own runs are only as good as the panels it is given: each setup's probe takes the model it names,
        // whose session takes the format the row says.
        var setup = Setup(panel);
        var probe = setup.Driver.Probe(setup.Device, TestContext.Current.CancellationToken);
        Assert.True(probe.Mine, $"{probe.Status}: {probe.Message}");
        using var session = setup.Driver.Open(setup.Device, probe.Identity!, new PanelOpenOptions(100), TestContext.Current.CancellationToken).Session!;
        Assert.Equal([format], session.Capabilities.Formats);
    }
}
