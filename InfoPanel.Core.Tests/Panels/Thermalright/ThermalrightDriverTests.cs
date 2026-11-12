using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Import;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The Thermalright driver over the kit's fake devices: its manifest read as the SDK reads a package's, the 1.x import's
/// model and settings, each protocol's probe and start, a frame, and the stop, as text traces in 1.4.x's call order
/// (<c>Services/ThermalrightPanelDeviceTask.cs</c> and <c>ThermalrightPanel/{ThermalrightPanelHelper,HidPanelDevice}.cs</c>
/// at c71f0f00, the lines cited per step), and the stop sending nothing, as 1.4.x's sent nothing.
/// </summary>
public sealed class ThermalrightDriverTests
{
    private static readonly PanelCandidate ChiZhu = new(0x87AD, 0x70DB, 1, 2, "1-2");
    private static readonly PanelCandidate Hid = new(0x0416, 0x5302, 1, 3, "1-3");
    private static readonly PanelCandidate Ly = new(0x0416, 0x5408, 1, 4, "1-4");
    private static readonly PanelCandidate Ly1 = new(0x0416, 0x5409, 1, 5, "1-5");
    private static readonly PanelCandidate Ali = new(0x0416, 0x5406, 1, 6, "1-6");

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 1, 2, 3, 4, 5, 6, 0xFF, 0xD9];

    private readonly FakePanelDevices _devices = new();
    private readonly List<double> _waits = [];
    private readonly ThermalrightDriver _driver;

    public ThermalrightDriverTests()
    {
        _driver = new ThermalrightDriver(_devices) { Wait = (time, stop) => { stop.ThrowIfCancellationRequested(); _waits.Add(time.TotalMilliseconds); } };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Z(int bytes) => new('0', bytes * 2);

    /// <summary>A transfer's bytes written as its fields, in hexadecimal.</summary>
    private static string H(params string[] fields) => string.Concat(fields);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>The trace with each transfer over 96 bytes shown as its length and its first 80 bytes (the protocol tests hold every byte against 1.4.x).</summary>
    private static string Short(PanelTrace trace) => string.Concat(trace.Entries.Select(entry =>
        entry.Data.Length <= 96
            ? PanelTrace.FormatLine(entry) + "\n"
            : PanelTrace.FormatLine(entry with { Data = [] }).Replace(" zlp", "") + $" [{entry.Data.Length}] {Hex(entry.Data.AsSpan(0, 80))}…\n"));

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static byte[] ChiZhuReply(string identifier, byte pm, byte sub)
    {
        var reply = new byte[48];
        (reply[0], reply[1], reply[2], reply[3]) = (0x12, 0x34, 0x56, 0x78);
        Encoding.ASCII.GetBytes(identifier).CopyTo(reply, 4);
        reply[24] = pm;
        reply[28] = sub;
        return reply;
    }

    private static byte[] HidReply(byte pm, byte sub)
    {
        // An input report: report id 0, then the reply (DA DB DC DD, SUB at 4, PM at 5, ACK 1 at 12).
        var report = new byte[64];
        (report[1], report[2], report[3], report[4]) = (0xDA, 0xDB, 0xDC, 0xDD);
        report[1 + 4] = sub;
        report[1 + 5] = pm;
        report[1 + 12] = 1;
        return report;
    }

    private static byte[] TrofeoBulkReply(int length, byte byte20, int width, int height)
    {
        var reply = new byte[length];
        (reply[0], reply[1], reply[8]) = (0x03, 0xFF, 0x01);
        (reply[16], reply[17], reply[18], reply[19]) = (0xA1, 0xB2, 0xC3, 0xD4);
        reply[20] = byte20;
        BitConverter.GetBytes(width).CopyTo(reply, 24);
        BitConverter.GetBytes(height).CopyTo(reply, 28);
        return reply;
    }

    private FakePanelDevice AddHid(PanelCandidate candidate)
    {
        var device = _devices.Add(candidate, PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = 513;
        device.Hid.MaxInputReportLength = 513;
        return device;
    }

    private IPanelSession Start(PanelCandidate candidate, PanelIdentity identity, string settings = "{}")
    {
        var open = _driver.Open(candidate, identity, new PanelOpenOptions(100, FamilySettings: Settings(settings)), Ct);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        return open.Session!;
    }

    private static PanelWrite Write(IPanelSession session, byte[] frame, PanelFrameFormat? format = null) =>
        session.WriteFrame(new PanelFrameView(frame, format ?? session.Capabilities.Formats[0], 1, false), Ct);

    // ---- The manifest and the 1.x import ----

    [Fact]
    public void Manifest_ReadAsTheSdkReadsAPackage_IsTheDriversOneTable()
    {
        // §2.2 (4): the package's panel.json (published beside the driver's DLL), through the SDK's reader (the engine's
        // rules), gives the driver's family, matches and models: no second table.
        var manifest = PanelManifest.Read(Path.GetFullPath(Path.Combine(TestPaths.ProjectDirectory, "..", "InfoPanel.Panels.Thermalright", PanelManifest.FileName)));
        var family = Assert.Single(manifest.Families);
        Assert.Equal(_driver.Family, family.Id);
        Assert.Equal(_driver.Matches, family.Matches);
        Assert.Equal(ThermalrightCatalog.Default.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, (string?)m.V1Model)),
            family.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)));
    }

    [Fact]
    public void V1Import_TheFixturesWonderVision360_MapsToItsModelAndDisplayMask()
    {
        // §2.2 (5): the engine's 1.x fixture read through V1SettingsReader; its Thermalright device's Model maps through
        // the package's v1Model, and its DisplayMask and FlickerFix (1.4's ThermalrightPanelDevice fields) read as the
        // family settings the driver takes.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var settings = V1SettingsReader.Read(XElement.Load(path, LoadOptions.SetLineInfo), path, new ImportLimits(), new ImportDiagnostics(100));
        var devices = settings.Devices.Where(d => d.Family == ThermalrightCatalog.FamilyId).ToList();
        Assert.Equal(2, devices.Count);
        foreach (var device in devices)
        {
            var model = ThermalrightCatalog.Default.ByV1Model(device.Fields["Model"]);
            Assert.Equal("wondervision360", model?.Id);
            Assert.True(model!.DisplayMask);
            var family = ThermalrightSettings.Read(Settings(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [ThermalrightSettings.MaskKey] = device.Fields["DisplayMask"],
                [ThermalrightSettings.FlickerFixKey] = bool.Parse(device.Fields["FlickerFix"]),
            })));
            Assert.Equal(new ThermalrightSettings(ThermalrightMask.None, false, null), family);
        }
        // 1.4's other two mask names read as the driver's.
        Assert.Equal(ThermalrightMask.RoundedLeft, ThermalrightSettings.MaskOf("RoundedLeft"));
        Assert.Equal(ThermalrightMask.RoundedAll, ThermalrightSettings.MaskOf("roundedAll"));
    }

    // ---- Probes ----

    [Fact]
    public void Probe_ChiZhu_IsThe1_4ScanProbe_PmAndSubThenIdentifier()
    {
        // ThermalrightPanelHelper.cs:295-382: claim interface 0, the 64-byte init (3 s), a reply of up to 1024 bytes (3 s);
        // PM 4 SUB 1 is the Hyper Vision, 480 × 480.
        var device = _devices.Add(ChiZhu);
        device.Usb.SerialNumber = "CZ01";
        var reply = ChiZhuReply("SSCRM-V1", 4, 1);
        device.Usb.Reply(reply);
        var probe = _driver.Probe(ChiZhu, Ct);
        Assert.Equal(new PanelIdentity("hypervision", 480, 480, "CZ01", null), probe.Identity);
        Assert.True(probe.Mine);
        Assert.Equal(
            "h1 open usb if0\n" +
            $"h1 > ep01 12345678{Z(52)}01000000{Z(4)}\n" +
            $"h1 < ep81 {Hex(reply)}\n" +
            "h1 close usb if0\n",
            Short(device.Trace));
    }

    [Fact]
    public void Probe_ChiZhu_Booting_IsAskedAgainLater_AndAnUnknownCodeIsMine_ByItsCode()
    {
        // :356-363: A1 A2 A3 A4 at bytes 4-7, the panel still starting: not now.
        var device = _devices.Add(ChiZhu);
        var booting = ChiZhuReply("", 0, 0);
        (booting[4], booting[5], booting[6], booting[7]) = (0xA1, 0xA2, 0xA3, 0xA4);
        device.Usb.Reply(booting);
        var probe = _driver.Probe(ChiZhu, Ct);
        Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));

        // A PM and SUB no rule names, with no identifier: an unknown model, named by its code, at 1.4's 480 × 480. (1.4's
        // scan, :378-381, would take the first model with no identifier, a TrofeoBulk 9.16", which no ChiZhu start can drive.)
        device.Usb.Reply(ChiZhuReply("", 200, 9));
        probe = _driver.Probe(ChiZhu, Ct);
        Assert.Equal((true, new PanelIdentity("pm-200-sub-9", 480, 480, null, null)), (probe.Mine, probe.Identity));
    }

    [Fact]
    public void Probe_ChiZhu_ThatCantBeOpened_PassesTheStatusOn()
    {
        var device = _devices.Add(ChiZhu);
        device.Usb.OpenStatus = PanelDeviceStatus.Busy;
        var busy = _driver.Probe(ChiZhu, Ct);
        Assert.Equal((false, PanelDeviceStatus.Busy), (busy.Mine, busy.Status));
        device.Usb.OpenStatus = PanelDeviceStatus.Ok;
        device.Fail(device.Calls + 2, PanelDeviceStatus.Io); // the next probe's init write
        var failed = _driver.Probe(ChiZhu, Ct);
        Assert.Equal((false, PanelDeviceStatus.Io), (failed.Mine, failed.Status));
        Assert.Equal("h2 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void Probe_Hid_ReadsTheModelFromThePm_OnTheDataCollection()
    {
        // ThermalrightPanelHelper.cs:128-146 (the collection with a 513-byte output report; the first model with the ids)
        // and ThermalrightPanelDeviceTask.cs:1666-1701 (the PM and SUB): PM 0x40 is the 1600 × 720.
        var device = AddHid(Hid);
        device.AddHid(); // the device's other collection, 65-byte reports: never opened
        device.Hid.Reply(HidReply(0x40, 0));
        var probe = _driver.Probe(Hid, Ct);
        Assert.Equal(new PanelIdentity("trofeovision1600x720", 1600, 720, null, null), probe.Identity);
        Assert.Equal(
            "h1 open hid hid0\n" +
            $"h1 hid> [513] 00dadbdcdd{Z(8)}01{Z(66)}…\n" +
            $"h1 hid< {Hex(HidReply(0x40, 0))}\n" +
            "h1 close hid\n",
            Short(device.Trace));
    }

    [Fact]
    public void Probe_TrofeoBulk_Byte20_TellsThe11_3FromThe9_16()
    {
        // ThermalrightPanelHelper.cs:423-492: the 2048-byte init, a 512-byte reply; byte 20 = 5 is the 11.3", 1920 × 400;
        // 1 is the 9.16" (the model with the ids).
        var device = _devices.Add(Ly);
        device.Usb.Reply(TrofeoBulkReply(512, 5, 1920, 480));
        Assert.Equal(new PanelIdentity("trofeovision113", 1920, 400, "A1B2C3D4", null), _driver.Probe(Ly, Ct).Identity);
        device.Usb.Reply(TrofeoBulkReply(512, 1, 1920, 480));
        Assert.Equal(new PanelIdentity("trofeovision916", 1920, 480, "A1B2C3D4", null), _driver.Probe(Ly, Ct).Identity);
        Assert.StartsWith($"h1 open usb if0\nh1 > ep01 [2048] 02ff{Z(6)}01{Z(71)}…\nh1 < ep81 [512] 03ff", Short(device.Trace));
    }

    [Fact]
    public void Probe_Ly1_IsTheModelWithItsIds_NothingSent()
    {
        var device = _devices.Add(Ly1);
        device.Usb.SerialNumber = "LY1";
        Assert.Equal(new PanelIdentity("trofeovision916ly1", 1920, 480, "LY1", null), _driver.Probe(Ly1, Ct).Identity);
        Assert.Equal("h1 open usb if0\nh1 close usb if0\n", Short(device.Trace));
    }

    [Fact]
    public void Probe_0416_5406_IsAliOrScsi_ByItsCachedInterfaces()
    {
        // The id is both an ALi panel's (vendor-specific bulk) and a SCSI panel's (mass storage): told apart without I/O.
        var ali = _devices.Add(Ali);
        var reply = new byte[16];
        reply[0] = 54; // 320 × 240 (:1520-1527)
        ali.Usb.Reply(reply);
        Assert.Equal(new PanelIdentity("alivision320x240", 320, 240, "00000000", null), _driver.Probe(Ali, Ct).Identity); // serial: bytes 10-13
        Assert.StartsWith($"h1 open usb if0\nh1 > ep01 [1040] f5000100bcffb6c8{Z(5)}04{Z(2)}{Z(64)}…\nh1 < ep81 36", Short(ali.Trace));

        var scsi = new PanelCandidate(0x0416, 0x5406, 2, 9, "2-9");
        var disk = _devices.Add(scsi);
        disk.Descriptor = new PanelUsbDescriptor(0, 0, 0, 0x0100, [new PanelUsbInterface(0, 0x08, 0x06, 0x50,
            [new PanelUsbEndpoint(0x01, PanelEndpointType.Bulk, 512), new PanelUsbEndpoint(0x81, PanelEndpointType.Bulk, 512)])]);
        var probe = _driver.Probe(scsi, Ct);
        Assert.Equal((true, "winbondscsi"), (probe.Mine, probe.Identity!.Model));
        Assert.Empty(disk.Trace.Entries);

        var open = _driver.Open(scsi, probe.Identity, new PanelOpenOptions(100), Ct);
        Assert.Equal(PanelDeviceStatus.NotSupported, open.Status);
        Assert.Equal(OperatingSystem.IsWindows() ? "Thermalright's SCSI panels aren't driven yet" : "Thermalright's SCSI panels are driven on Windows only", open.Message);
        Assert.Empty(disk.Trace.Entries);
    }

    // ---- Sequence traces: start, a frame, the brightness, the stop ----

    [Fact]
    public void ChiZhu_Trace_StartAFrameAndTheStop()
    {
        var device = _devices.Add(ChiZhu);
        var reply = ChiZhuReply("SSCRM-V1", 4, 1);
        device.Usb.Reply(reply);
        var session = Start(ChiZhu, new PanelIdentity("hypervision", 480, 480, null, null));
        var c = session.Capabilities;
        Assert.Equal((480, 480, PanelFrameFormat.Jpeg, 30, (int?)null, 95, 50, 5, true, false, (PanelRange?)null),
            (c.Width, c.Height, Assert.Single(c.Formats), c.MaxFps, c.MaxFrameBytes, c.JpegQuality, c.MinJpegQuality, c.JpegQualityStep, c.DimInSoftware, c.RotatesItself, c.Brightness));
        Assert.Equal(((IReadOnlyDictionary<int, byte[]>?)null, (PanelCrop?)null), (c.Overlays, c.Crop));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(40)); // the engine dims: nothing sent
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        Assert.Equal(
            // :405-407 300 ms; :719-729 the init (5 s) and its reply (5 s); :848-849 100 ms
            "h1 open usb if0\n" +
            $"h1 > ep01 12345678{Z(52)}01000000{Z(4)}\n" +
            $"h1 < ep81 {Hex(reply)}\n" +
            // :851-870 the header (JPEG 2, 480 × 480, type 2, 10 bytes) and the frame in one write, then 15 ms
            $"h1 > ep01 {H("12345678", "02000000", "e0010000", "e0010000", Z(40), "02000000", "0a000000", Hex(Jpeg))}\n" +
            // 1.4's stop sent nothing: the device is let go
            "h1 close usb if0\n",
            Short(device.Trace));
        Assert.Equal([300, 100, 15], _waits);
    }

    [Fact]
    public void ChiZhu_AFrameThatFillsItsLastPacket_IsFollowedByAZeroLengthPacket_AndRgb565IsFormat3()
    {
        // :865-867: a write whose length is a multiple of 512 is followed by a zero-length packet: a 448-byte JPEG and its
        // 64-byte header are 512 bytes; 449 bytes need none.
        var device = _devices.Add(ChiZhu);
        device.Usb.Reply(ChiZhuReply("SSCRM-V1", 4, 1));
        var session = Start(ChiZhu, new PanelIdentity("hypervision", 480, 480, null, null));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[448]).Status);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[449]).Status);
        var lines = Short(device.Trace).Split('\n');
        Assert.Equal(("h1 > ep01 [512]", "h1 > ep01 zlp", "h1 > ep01 [513]"), (lines[3][..15], lines[4], lines[5][..15]));
        session.Dispose();

        // The 320 × 320 SPISCRM-V2 takes RGB565, big-endian (5a8bd39): header format 3, frame type still 2 (:110-128).
        device.Usb.Reply(ChiZhuReply("SPISCRM-V2", 0x20, 0x20));
        session = Start(ChiZhu, new PanelIdentity("elitevision360", 320, 320, null, null));
        Assert.Equal(PanelFrameFormat.Rgb565Be, Assert.Single(session.Capabilities.Formats));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[320 * 320 * 2]).Status);
        Assert.Equal($"h2 > ep01 [204864] {H("12345678", "03000000", "40010000", "40010000", Z(40), "02000000", "00200300", Z(16))}…",
            Short(device.Trace).Split('\n')[^2]);
    }

    [Fact]
    public void ChiZhu_BootingPanel_IsAskedFiveTimes3sApart_ThenReadAsItIs()
    {
        // :704-741: five tries, 3 s between; the last reply (still booting) is read as any other.
        var device = _devices.Add(ChiZhu);
        var booting = ChiZhuReply("", 4, 1);
        (booting[4], booting[5], booting[6], booting[7]) = (0xA1, 0xA2, 0xA3, 0xA4);
        for (var i = 0; i < 5; i++)
            device.Usb.Reply(booting);
        var session = Start(ChiZhu, new PanelIdentity("hypervision", 480, 480, null, null));
        Assert.Equal([300, 3000, 3000, 3000, 3000, 100], _waits);
        Assert.Equal(10, device.Trace.Entries.Count(e => e.Op is PanelTraceOp.UsbWrite or PanelTraceOp.UsbRead));
        Assert.Equal(480, session.Capabilities.Width); // PM 4 SUB 1 (the booting bytes are the identifier's)
    }

    [Fact]
    public void Hid_Trace_StartAJpegFrameAndTheStop()
    {
        var device = AddHid(Hid);
        device.Hid.Reply(HidReply(0x40, 0));
        var session = Start(Hid, new PanelIdentity("trofeovision1600x720", 1600, 720, null, null));
        Assert.Equal((1600, 720, 400_000), (session.Capabilities.Width, session.Capabilities.Height, session.Capabilities.MaxFrameBytes));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        session.Close(true, TimeSpan.FromSeconds(3));

        Assert.Equal(
            // :1613-1641: 50 ms, the init report (5 s), 200 ms, the reply (5 s); :1740 100 ms
            "h1 open hid hid0\n" +
            $"h1 hid> [513] 00dadbdcdd{Z(8)}01{Z(66)}…\n" +
            $"h1 hid< {Hex(HidReply(0x40, 0))}\n" +
            // HidPanelDevice.SendJpegFrame: DA DB DC DD, command 2, 1600 × 720, type 2, 10 bytes, the frame at 20; then 1 ms
            $"h1 hid> [513] {H("00", "dadbdcdd", "02000000", "4006", "d002", "02000000", "0a000000", Hex(Jpeg), Z(49))}…\n" +
            "h1 close hid\n",
            Short(device.Trace));
        Assert.Equal([300, 50, 200, 100, 1], _waits);
    }

    [Fact]
    public void Hid_Rgb565Frame_RestsEvery32ndPacket_And20msAfter()
    {
        // HidPanelDevice.SendRgb565Frame: 1 ms after every 32nd packet; :1852 20 ms after the frame. PM 58 SUB 0 is the
        // Frozen Warframe SE, 240 × 320, RGB565 little-endian.
        var device = AddHid(Hid);
        device.Hid.Reply(HidReply(58, 0));
        var session = Start(Hid, new PanelIdentity("frozenwarframe", 240, 320, null, null));
        Assert.Equal(PanelFrameFormat.Rgb565LeTruncated, Assert.Single(session.Capabilities.Formats));
        Assert.Null(session.Capabilities.MaxFrameBytes);
        _waits.Clear();
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[240 * 320 * 2]).Status);
        var reports = device.Trace.Entries.Count(e => e.Op == PanelTraceOp.HidWrite) - 1; // less the init
        Assert.Equal(1 + (240 * 320 * 2 - 492 + 511) / 512, reports);
        Assert.Equal(Enumerable.Repeat(1.0, reports / 32).Append(20), _waits);
        // The header: command 2, RGB565 flag 1 at byte 6, 240 × 320, type 2.
        var header = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.HidWrite).ElementAt(1);
        Assert.StartsWith(H("00", "dadbdcdd", "02000100", "f000", "4001", "02"), Hex(header.Data));
    }

    [Fact]
    public void Hid_TwoMatchingCollections_AFirstThatIsBusyOrSilent_IsLetGo_AndTheSecondIsTried()
    {
        // The SDK lists every collection the filter passes: a device can have two it can't tell apart. Each is tried in
        // order, a failed one let go before the next.
        var device = AddHid(Hid);
        device.Hid.OpenStatus = PanelDeviceStatus.Busy;
        var second = device.AddHid();
        (second.MaxOutputReportLength, second.MaxInputReportLength) = (513, 513);
        second.Reply(HidReply(0x40, 0));
        var probe = _driver.Probe(Hid, Ct);
        Assert.Equal(new PanelIdentity("trofeovision1600x720", 1600, 720, null, null), probe.Identity);
        Assert.Equal(
            "h1 open hid hid0 !busy\n" +
            "h2 open hid hid1\n" +
            $"h2 hid> [513] 00dadbdcdd{Z(8)}01{Z(66)}…\n" +
            $"h2 hid< {Hex(HidReply(0x40, 0))}\n" +
            "h2 close hid\n",
            Short(device.Trace));

        // The first opens but never answers its init: its three tries (:1613-1648), let go, then the second's init.
        device.Hid.OpenStatus = PanelDeviceStatus.Ok;
        second.Reply(HidReply(0x40, 0));
        var before = device.Trace.Entries.Count;
        var session = Start(Hid, probe.Identity!);
        Assert.Equal((1600, 720), (session.Capabilities.Width, session.Capabilities.Height));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        session.Dispose();
        var lines = device.Trace.Entries.Skip(before).Select(PanelTrace.FormatLine).ToList();
        Assert.Equal(
            ["h3 open hid hid0", "h3 hid< !timeout", "h3 hid< !timeout", "h3 hid< !timeout", "h3 close hid", "h4 open hid hid1", "h4 close hid"],
            lines.Where(l => !l.StartsWith("h3 hid>") && !l.StartsWith("h4 hid")).ToList());
        Assert.Equal(3, lines.Count(l => l.StartsWith("h3 hid>")));
        Assert.Equal([$"h4 hid< {Hex(HidReply(0x40, 0))}"], lines.Where(l => l.StartsWith("h4 hid<")).ToList());
        Assert.Equal([300, 50, 200, 500, 50, 200, 500, 50, 200, 50, 200, 100, 1], _waits);
    }

    [Fact]
    public void Hid_AReplyNamingNoModel_IsMineByItsCode_AndWaitsForTheModelSetting()
    {
        // PM 200 SUB 9 names no model, nor does the reply's identifier: the first model with the ids (1.4's scan) is only
        // where a start began, never what the panel is. The probe answers the code; a start from it is refused until the
        // model setting names a HID model, which then runs at its size.
        var device = AddHid(Hid);
        device.Hid.Reply(HidReply(200, 9));
        var probe = _driver.Probe(Hid, Ct);
        Assert.Equal((true, new PanelIdentity("pm-200-sub-9", 480, 480, null, null)), (probe.Mine, probe.Identity));

        device.Hid.Reply(HidReply(200, 9));
        var open = _driver.Open(Hid, probe.Identity!, new PanelOpenOptions(100), Ct);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (open.Session, open.Status));
        Assert.Equal("h2 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));

        device.Hid.Reply(HidReply(200, 9));
        var session = Start(Hid, probe.Identity!, """{"model": "trofeovision"}""");
        Assert.Equal((1280, 480), (session.Capabilities.Width, session.Capabilities.Height));
        session.Dispose();
    }

    [Fact]
    public void Hid_Probe_TwoSilentCollections_ShareTheProbesBudget_SoAThirdThatAnswersIsReached()
    {
        // Three collections the filter passes, the first two never answering their init (each read waits out its
        // timeout): the probe gives each collection's calls a share of what is left of its budget, so the third is still
        // asked before the engine's deadline ends the probe, and answers its model.
        var device = AddHid(Hid);
        var second = device.AddHid();
        var third = device.AddHid();
        (second.MaxOutputReportLength, second.MaxInputReportLength) = (513, 513);
        (third.MaxOutputReportLength, third.MaxInputReportLength) = (513, 513);
        third.Reply(HidReply(0x40, 0));
        _ = device.Stall(3); // the first collection's read (its open is call 1, its init call 2)
        _ = device.Stall(6); // the second's
        _driver.ProbeBudget = TimeSpan.FromMilliseconds(900);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(1500)); // the engine's deadline, a little after the driver's budget

        var probe = _driver.Probe(Hid, deadline.Token);

        Assert.Equal((true, new PanelIdentity("trofeovision1600x720", 1600, 720, null, null)), (probe.Mine, probe.Identity));
        var stalled = device.Timings.Where(t => t.Call is 3 or 6).ToList();
        Assert.Equal(2, stalled.Count);
        Assert.All(stalled, t => Assert.InRange(t.TimeoutMs, 1, 300));
        Assert.Equal(["h1 open hid hid0", "h1 close hid", "h2 open hid hid1", "h2 close hid", "h3 open hid hid2", "h3 close hid"],
            device.Trace.Entries.Where(e => e.Op is PanelTraceOp.Open or PanelTraceOp.Close).Select(PanelTrace.FormatLine));
    }

    [Fact]
    public void Hid_Probe_AShareThatEndsAfterItsCheck_OpensWithTheTimeItChecked_AndAnswersTimeout()
    {
        // The collection's share is read once: a pause after the check that outlasts the share (here a clock that jumps
        // 10 s after its third reading: the budget's start, the share's, the check) can't hand the open a timeout of 0,
        // which the device seam refuses by throwing. The open gets the time checked; the init after it finds none left.
        var device = AddHid(Hid);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var readings = 0;
        _driver.Clock = () => ++readings <= 3 ? start : start + 10 * System.Diagnostics.Stopwatch.Frequency;

        var probe = _driver.Probe(Hid, Ct);

        Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
        var open = Assert.Single(device.Timings, t => t.Call == 1);
        Assert.Equal(ThermalrightDriver.ProbeTimeoutMs, open.TimeoutMs);
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], device.Trace.Entries.Select(PanelTrace.FormatLine));
    }

    [Fact]
    public void Hid_AReportHoldingOnlyItsReportId_IsNoAnswer_InTheProbeAndTheStart()
    {
        // An input report of its report id alone carries no reply: the probe counts the init unanswered, and a start
        // tries it again, three times in all, then fails.
        var device = AddHid(Hid);
        device.Hid.Reply([0x00]);
        var probe = _driver.Probe(Hid, Ct);
        Assert.Equal((false, PanelDeviceStatus.Io), (probe.Mine, probe.Status));

        for (var i = 0; i < 3; i++)
            device.Hid.Reply([0x00]);
        var open = _driver.Open(Hid, new PanelIdentity("trofeovision", 1280, 480, null, null), new PanelOpenOptions(100), Ct);
        Assert.Equal((null, PanelDeviceStatus.Io), (open.Session, open.Status));
        Assert.Equal(3, device.Trace.Entries.Count(e => e.Op == PanelTraceOp.HidRead && e.Handle == 2));
        Assert.Equal("h2 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void Hid_InitIsTriedThreeTimes_ThenTheStartFails()
    {
        // :1613-1648: three tries 500 ms apart; none answered fails the start (the device let go).
        var device = AddHid(Hid);
        var open = _driver.Open(Hid, new PanelIdentity("trofeovision", 1280, 480, null, null), new PanelOpenOptions(100), Ct);
        Assert.Equal((null, PanelDeviceStatus.Io), (open.Session, open.Status));
        Assert.Equal([300, 50, 200, 500, 50, 200, 500, 50, 200], _waits);
        Assert.Equal("h1 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void TrofeoBulk_Trace_StartTheFlickerFixAFrameAndItsAck()
    {
        var device = _devices.Add(Ly);
        device.Usb.Reply(TrofeoBulkReply(512, 1, 1920, 480));
        var session = Start(Ly, new PanelIdentity("trofeovision916", 1920, 480, null, null), """{"flickerFix": true}""");
        Assert.Equal((1920, 480, 230_000, new PanelCrop(0, 0, 1920, 462)),
            (session.Capabilities.Width, session.Capabilities.Height, session.Capabilities.MaxFrameBytes, session.Capabilities.Crop));
        device.Usb.Reply([0x03, 0xFF, 0, 0]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        session.Close(true, TimeSpan.FromSeconds(3));

        Assert.Equal(
            // :977 50 ms; :979-1013 the 2048-byte init and the 512-byte reply, 200 ms before they are checked (:1002); :1121 100 ms
            "h1 open usb if0\n" +
            $"h1 > ep01 [2048] 02ff{Z(6)}01{Z(71)}…\n" +
            $"h1 < ep81 [512] {Hex(TrofeoBulkReply(512, 1, 1920, 480).AsSpan(0, 80))}…\n" +
            // :1139-1184: one 496-byte piece in its 512-byte sub-packet, padded to four, in one 4096-byte transfer
            $"h1 > ep01 [2048] 01ff0a0000000a0001010000{Z(4)}{Hex(Jpeg)}{Z(54)}…\n" +
            // :1192: the acknowledgement (500 ms)
            "h1 < ep81 03ff0000\n" +
            "h1 close usb if0\n",
            Short(device.Trace));
        Assert.Equal([300, 50, 200, 100], _waits);
    }

    [Fact]
    public void TrofeoBulk_FiveMissingAcksInARow_EndTheSession()
    {
        // :1191-1204: an acknowledgement that doesn't come is counted; one that does resets the count; the fifth in a row ends it.
        var device = _devices.Add(Ly);
        device.Usb.Reply(TrofeoBulkReply(512, 1, 1920, 480));
        var session = Start(Ly, new PanelIdentity("trofeovision916", 1920, 480, null, null));
        for (var i = 0; i < 4; i++)
            Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        device.Usb.Reply([1]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        for (var i = 0; i < 4; i++)
            Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        Assert.Equal(PanelDeviceStatus.Io, Write(session, Jpeg).Status);
    }

    [Fact]
    public void TrofeoBulk_AJpegOverTheCap_IsNotSent()
    {
        // The 230,000-byte cap (:1129): the engine encodes again lower first; a frame still over it is left unsent (TRCC
        // drops such frames) and the session goes on.
        var device = _devices.Add(Ly);
        device.Usb.Reply(TrofeoBulkReply(512, 1, 1920, 480));
        var session = Start(Ly, new PanelIdentity("trofeovision916", 1920, 480, null, null));
        var before = device.Trace.Entries.Count;
        Assert.Equal(new PanelWrite(PanelDeviceStatus.Ok, TimeSpan.Zero), Write(session, new byte[230_001]));
        Assert.Equal(before, device.Trace.Entries.Count);
        device.Usb.Reply([1]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[230_000]).Status);
        Assert.Equal(before + 58 + 1, device.Trace.Entries.Count); // 464 pieces padded to 464: 58 transfers, then the ack
    }

    [Fact]
    public void Ly1_Trace_TheReportedSizeAFrameInOneWriteAndItsAck()
    {
        var device = _devices.Add(Ly1);
        device.Usb.Reply(TrofeoBulkReply(511, 5, 1920, 462)); // byte 20 = 5 changes nothing on LY1 (:1347-1359)
        var session = Start(Ly1, new PanelIdentity("trofeovision916ly1", 1920, 480, null, null), """{"flickerFix": true}""");
        Assert.Equal((1920, 462, (PanelCrop?)null), (session.Capabilities.Width, session.Capabilities.Height, session.Capabilities.Crop));
        device.Usb.Reply([0x03]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        session.Close(true, TimeSpan.FromSeconds(3));
        Assert.Equal(
            // :1288-1322 the 512-byte init, the 511-byte reply, 200 ms before they are checked (:1311); :1367 100 ms
            "h1 open usb if0\n" +
            $"h1 > ep01 [512] 02ff{Z(6)}01{Z(71)}…\n" +
            $"h1 < ep81 [511] {Hex(TrofeoBulkReply(511, 5, 1920, 462).AsSpan(0, 80))}…\n" +
            // :1400-1459: one sub-packet, command 2, unpadded, in one write; :1379 the acknowledgement
            $"h1 > ep01 [512] 01ff0a0000000a0002010000{Z(4)}{Hex(Jpeg)}{Z(54)}…\n" +
            "h1 < ep81 03\n" +
            "h1 close usb if0\n",
            Short(device.Trace));
        Assert.Equal([300, 200, 100], _waits);
    }

    [Fact]
    public void Ali_Trace_StartAFrameAndItsAck()
    {
        var device = _devices.Add(Ali);
        var reply = new byte[16];
        reply[0] = 101; // 320 × 320 (:1528-1534)
        device.Usb.Reply(reply);
        var session = Start(Ali, new PanelIdentity("alivision320x320", 320, 320, null, null));
        Assert.Equal([PanelFrameFormat.Rgb565LeTruncated], session.Capabilities.Formats);
        device.Usb.Reply(new byte[16]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[320 * 320 * 2]).Status);
        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, new byte[320 * 320 * 2]).Status); // no ack: 1.4 threw (:1583-1585)
        session.Close(true, TimeSpan.FromSeconds(3));
        Assert.Equal(
            "h1 open usb if0\n" +
            $"h1 > ep01 [1040] f5000100bcffb6c8{Z(5)}04{Z(2)}{Z(64)}…\n" +
            $"h1 < ep81 {Hex(reply)}\n" +
            // :1555-1586 the F5 01 header with the frame size (204,800) and the pixels in one write; the 16-byte ack
            $"h1 > ep01 [204816] f5010100bcffb6c8{Z(4)}00200300{Z(64)}…\n" +
            $"h1 < ep81 {Z(16)}\n" +
            $"h1 > ep01 [204816] f5010100bcffb6c8{Z(4)}00200300{Z(64)}…\n" +
            "h1 < ep81 !timeout\n" +
            "h1 close usb if0\n",
            Short(device.Trace));
    }

    [Fact]
    public void Ali_NoInitReply_SendsThe1_4DefaultFrameSize()
    {
        // :1508: with no reply 1.4 kept its default frame size, 204,800 bytes (320 × 320 RGB565), whatever model the start
        // began from: a 320 × 240 frame is padded with zeros to it.
        var device = _devices.Add(Ali);
        var session = Start(Ali, new PanelIdentity("alivision320x240", 320, 240, null, null));
        Assert.Equal((320, 240), (session.Capabilities.Width, session.Capabilities.Height));
        device.Usb.Reply(new byte[16]);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, new byte[320 * 240 * 2]).Status);
        var lines = Short(device.Trace).Split('\n');
        Assert.Equal("h1 < ep81 !timeout", lines[2]);
        Assert.Equal($"h1 > ep01 [204816] f5010100bcffb6c8{Z(4)}00200300{Z(64)}…", lines[3]);
    }

    // ---- The stop, settings, and unknown models ----

    [Fact]
    public void Stop_SendsNothing_AsIn1_4_AndNothingIsWrittenAfterClose()
    {
        // 1.4's Thermalright task sent nothing when it stopped (no screen-off command; its panels keep their last frame):
        // Close, with the screen-off asked or not, only lets the device go, and every call after it reaches nothing.
        var device = _devices.Add(ChiZhu);
        device.Usb.Reply(ChiZhuReply("SSCRM-V1", 4, 1));
        var session = Start(ChiZhu, new PanelIdentity("hypervision", 480, 480, null, null));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Jpeg).Status);
        var before = device.Trace.Entries.Count;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(Assert.Single(device.Trace.Entries.Skip(before))));
        Assert.Equal(PanelDeviceStatus.Io, Write(session, Jpeg).Status);
        Assert.Equal(PanelDeviceStatus.Io, session.SetBrightness(10));
        session.Close(true, TimeSpan.FromSeconds(3));
        session.Dispose();
        Assert.Equal(before + 1, device.Trace.Entries.Count);
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Fact]
    public void DisplayMask_IsTheModelsOverlays_TheLevitaTurned180More()
    {
        // :169-175, :294-341: the mask's PNG for the panel's turn plus the model's, at every rotation; only models with a
        // mask draw one. At 90° and 270° it is the panel-size PNG of 1.4's portrait one as 1.4 drew it, clipped (its
        // pixels proven against 1.4's drawing in ThermalrightFramesTests).
        var device = _devices.Add(ChiZhu);
        device.Usb.Reply(ChiZhuReply("SSCRM-V3", 0, 3)); // SSCRM-V3 SUB 3: the Levita Vision 360
        var session = Start(ChiZhu, new PanelIdentity("levitavision360", 1600, 720, null, null), """{"displayMask": "roundedLeft"}""");
        var overlays = session.Capabilities.Overlays!;
        Assert.Equal([0, 90, 180, 270], overlays.Keys.Order());
        Assert.Null(InfoPanel.Panels.PanelFrameWriter.Problem(session.Capabilities));
        foreach (var (rotation, png) in overlays)
        {
            using var expected = ThermalrightFrames.OpenOverlay(ThermalrightMask.RoundedLeft, (rotation + 180) % 360)!;
            using var copy = new MemoryStream();
            expected.CopyTo(copy);
            Assert.Equal(copy.ToArray(), png);
        }
        session.Dispose();

        device.Usb.Reply(ChiZhuReply("SSCRM-V1", 4, 1)); // a Hyper Vision has no mask
        Assert.Null(Start(ChiZhu, new PanelIdentity("hypervision", 480, 480, null, null), """{"displayMask": "roundedAll"}""").Capabilities.Overlays);
    }

    [Fact]
    public void UnknownModel_WaitsForTheModelSetting_WhichThenDrivesIt()
    {
        // A reply that names no model (PM 200): not driven until the model setting names one (the panel then runs as it,
        // at its size); a setting naming another protocol's model is refused.
        var device = _devices.Add(ChiZhu);
        device.Usb.Reply(ChiZhuReply("", 200, 9));
        var open = _driver.Open(ChiZhu, new PanelIdentity("pm-200-sub-9", 480, 480, null, null), new PanelOpenOptions(100), Ct);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (open.Session, open.Status));
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));

        device.Usb.Reply(ChiZhuReply("", 200, 9));
        var session = Start(ChiZhu, new PanelIdentity("pm-200-sub-9", 480, 480, null, null), """{"model": "lc3"}""");
        Assert.Equal((960, 540), (session.Capabilities.Width, session.Capabilities.Height));
        session.Dispose();

        open = _driver.Open(ChiZhu, new PanelIdentity("pm-200-sub-9", 480, 480, null, null), new PanelOpenOptions(100, FamilySettings: Settings("""{"model": "trofeovision"}""")), Ct);
        Assert.Equal(PanelDeviceStatus.NotSupported, open.Status);
    }

    [Fact]
    public void ChiZhu_AReplyWhoseIdentifierNamesAHidModel_IsRefusedAtStart_TheIdentityStaysItsCode()
    {
        // BP21940 is the HID panels' identifier: a ChiZhu reply with it and a PM and SUB no rule names is no model a
        // ChiZhu start can drive, as a model setting naming another protocol's model isn't. The setting naming a ChiZhu
        // model drives it.
        var device = _devices.Add(ChiZhu);
        device.Usb.Reply(ChiZhuReply("BP21940", 200, 9));
        var probe = _driver.Probe(ChiZhu, Ct);
        Assert.Equal((true, new PanelIdentity("pm-200-sub-9", 480, 480, null, null)), (probe.Mine, probe.Identity));

        device.Usb.Reply(ChiZhuReply("BP21940", 200, 9));
        var open = _driver.Open(ChiZhu, probe.Identity!, new PanelOpenOptions(100), Ct);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (open.Session, open.Status));
        Assert.Equal("h2 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));

        device.Usb.Reply(ChiZhuReply("BP21940", 200, 9));
        var session = Start(ChiZhu, probe.Identity!, """{"model": "lc3"}""");
        Assert.Equal((960, 540), (session.Capabilities.Width, session.Capabilities.Height));
        session.Dispose();
    }

    [Fact]
    public void Open_Cancelled_LetsTheDeviceGo()
    {
        // Cancelled in the 300 ms before the device is opened: nothing reached.
        var device = _devices.Add(ChiZhu);
        var identity = new PanelIdentity("hypervision", 480, 480, null, null);
        using (var first = new CancellationTokenSource())
        {
            _driver.Wait = (time, stop) => { first.Cancel(); stop.ThrowIfCancellationRequested(); };
            Assert.Throws<OperationCanceledException>(() => _driver.Open(ChiZhu, identity, new PanelOpenOptions(100), first.Token));
            Assert.Empty(device.Trace.Entries);
        }

        // Cancelled in the 100 ms after the init: the device is let go.
        device.Usb.Reply(ChiZhuReply("SSCRM-V1", 4, 1));
        using var second = new CancellationTokenSource();
        var waits = 0;
        _driver.Wait = (time, stop) => { if (++waits == 2) second.Cancel(); stop.ThrowIfCancellationRequested(); };
        Assert.Throws<OperationCanceledException>(() => _driver.Open(ChiZhu, identity, new PanelOpenOptions(100), second.Token));
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }
}
