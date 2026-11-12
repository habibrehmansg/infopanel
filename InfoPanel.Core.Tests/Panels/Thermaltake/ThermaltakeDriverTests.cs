using System.Text;
using System.Xml.Linq;
using InfoPanel.Core.Tests.Panels.Thermaltake.V1Oracle;
using InfoPanel.Import;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermaltake;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// The driver over the kit's fake devices (§2.2 (2), (6), (8)): the probe, the start, a frame and the stop as traces
/// built from 1.4.x's own calls (the oracle, <c>V1Oracle/ThermaltakeHidDevice.cs</c>, fed the same replies), in the
/// order 1.4.x made them (each step's line cited); the conformance suite; the 1.x import of the engine's fixture.
/// </summary>
public sealed class ThermaltakeDriverTests
{
    private const long Date = 1_731_400_000_123;

    private static readonly PanelCandidate SixInch = new(0x264A, 0x2347, 1, 4, "1-2");
    private static readonly PanelCandidate Asrock = new(0x26CE, 0x0A10, 1, 5, "1-3");

    /// <summary>A reply the panel might send: its text framed as a command is, as an input report (the driver reads only its text).</summary>
    private static byte[] Answer(string text) => ThermaltakeProtocol.Report(ThermaltakeProtocol.Frame(Encoding.ASCII.GetBytes(text)));

    private static readonly byte[] Ok = Answer("200 OK\r\n\r\n");

    private static (FakePanelDevices Devices, FakePanelDevice Device) Plug(PanelCandidate candidate)
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(candidate, PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = ThermaltakeProtocol.ReportSize;
        device.Hid.MaxInputReportLength = ThermaltakeProtocol.ReportSize;
        return (devices, device);
    }

    /// <summary>The driver with a fixed Date and no real pauses (the 50 ms before each reply is read).</summary>
    private static ThermaltakeDriver Driver(IPanelDevices devices) =>
        new(devices) { Now = () => Date, Wait = (_, stop) => stop.ThrowIfCancellationRequested() };

    private static PanelIdentity Probed(ThermaltakeDriver driver, PanelCandidate candidate)
    {
        var probe = driver.Probe(candidate, TestContext.Current.CancellationToken);
        Assert.True(probe is { Mine: true, Status: PanelDeviceStatus.Ok, Identity: not null }, $"{probe.Status}: {probe.Message}");
        return probe.Identity!;
    }

    private static IPanelSession Start(ThermaltakeDriver driver, PanelCandidate candidate)
    {
        var open = driver.Open(candidate, Probed(driver, candidate), new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        return open.Session!;
    }

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    // ---- The probe ----

    public static TheoryData<string, string, int, int> Models() => new()
    {
        { "264a:2347", "toughliquid6inch", 1480, 720 },
        { "26ce:0a10", "asrockphantomgaming360lcd", 480, 480 },
    };

    private static PanelCandidate CandidateOf(string usb) => usb == "264a:2347" ? SixInch : Asrock;

    [Theory]
    [MemberData(nameof(Models))]
    public void Probe_TakesTheModelItsUsbIdNames_AndTouchesNothing(string usb, string model, int width, int height)
    {
        // 1.4.x's scan (ThermaltakePanelHelper.cs:33-58): the model by VID and PID, a HID collection whose output report
        // holds 1025 bytes; nothing sent. The serial isn't readable through HID: none.
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate);

        var probe = Driver(devices).Probe(candidate, TestContext.Current.CancellationToken);

        Assert.Equal(new PanelProbe(true, new PanelIdentity(model, width, height, null, null), PanelDeviceStatus.Ok), probe);
        Assert.Empty(device.Trace.Entries);
    }

    [Fact]
    public void Probe_WithoutACollectionThatHoldsAPacket_PassesTheDeviceOn()
    {
        // ThermaltakePanelHelper.cs:43-48: a collection whose output report is shorter than 1025 bytes is skipped.
        var (devices, device) = Plug(SixInch);
        device.Hid.MaxOutputReportLength = 65;

        var probe = Driver(devices).Probe(SixInch, TestContext.Current.CancellationToken);

        Assert.Equal((false, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Empty(device.Trace.Entries);
    }

    [Fact]
    public void Probe_OfADeviceGone_AnswersGone_AndWithoutTheDeviceSeam_NotSupported()
    {
        var gone = Driver(new FakePanelDevices()).Probe(SixInch, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Gone), (gone.Mine, gone.Status));

        var seamless = new ThermaltakeDriver().Probe(SixInch, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.NotSupported), (seamless.Mine, seamless.Status));
        Assert.Equal(ThermaltakeDriver.FamilyId, new ThermaltakeDriver().Family);
    }

    [Fact]
    public void Matches_AreTheManifests()
    {
        Assert.Equal([new PanelMatch(0x264A, 0x2347, null, PanelTransport.Hid), new PanelMatch(0x26CE, 0x0A10, null, PanelTransport.Hid)],
            Driver(new FakePanelDevices()).Matches);
    }

    // ---- The start, a frame and the stop, as 1.4.x made them ----

    [Theory]
    [MemberData(nameof(Models))]
    public void StartFrameAndStop_Trace_Is1_4s(string usb, string model, int width, int height)
    {
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate);
        device.Hid.Respond(_ => Ok);
        var jpeg = Jpeg(2500); // three chunks: 1000, 1000 and 500 bytes

        // 1.4.x, fed the same replies: Handshake (:83-100), one frame (SendJpegFrame, :115-141), and the task's dispose of
        // the device (ThermaltakePanelDeviceTask.cs:91, Dispose :273-282), which turns realtime display off (:146-158).
        var oracle = new V1ThermaltakeHidDevice { Now = () => Date };
        oracle.Replies.Enqueue((Ok, Ok.Length));
        oracle.Replies.Enqueue((Ok, Ok.Length));
        Assert.True(oracle.Handshake());
        oracle.SendJpegFrame(jpeg);
        oracle.Replies.Enqueue((Ok, Ok.Length));
        oracle.DisableRealtimeDisplay();
        var reports = oracle.Reports.Select(Hex).ToList();
        Assert.Equal(6, reports.Count);

        var session = Start(Driver(devices), candidate);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(jpeg, PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken).Status);
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        var expected =
            "h1 open hid hid0\n" +                      // :62 device.Open(), the first collection with a 1025-byte output report (:55)
            $"h1 hid> {reports[0]}\n" +                 // :88-89 POST conn 1, SeqNumber 100 (:244 WritePacket)
            $"h1 hid< {Hex(Ok)}\n" +                    // :246-249 50 ms, then one reply read; it holds 200
            $"h1 hid> {reports[1]}\n" +                 // :93-95 POST realtimeDisplay 1 {"enable":true}, SeqNumber 101
            $"h1 hid< {Hex(Ok)}\n" +                    // :246-249
            $"h1 hid> {reports[2]}\n" +                 // :121-140 chunk 0 of 3
            $"h1 hid> {reports[3]}\n" +                 // chunk 1
            $"h1 hid> {reports[4]}\n" +                 // chunk 2: 500 bytes, zeros after
            $"h1 hid> {reports[5]}\n" +                 // :150-152 POST realtimeDisplay 1 {"enable":false}, SeqNumber 102
            $"h1 hid< {Hex(Ok)}\n" +                    // :246-248, its answer not acted on
            "h1 close hid\n";                           // :279 the stream disposed
        Assert.Equal(expected, device.Trace.Format());
        Assert.Equal(0, device.CallsAfterDispose);
        Assert.Equal((model, width, height), (ThermaltakeDriver.ModelOf(candidate)!.Id, session.Capabilities.Width, session.Capabilities.Height));
    }

    [Fact]
    public void Start_TakesTheFirstCollectionThatOpens_As1_4Did()
    {
        // ThermaltakeHidDevice.cs:51-73: each listed collection whose output report holds a packet is opened in turn,
        // and the first that opens is used; one that fails to open is passed over.
        var (devices, device) = Plug(SixInch);
        device.Hid.OpenStatus = PanelDeviceStatus.Busy;
        var second = device.AddHid();
        second.MaxOutputReportLength = ThermaltakeProtocol.ReportSize;
        second.MaxInputReportLength = ThermaltakeProtocol.ReportSize;
        second.Respond(_ => Ok);

        using var session = Start(Driver(devices), SixInch);

        var lines = device.Trace.Format().Split('\n');
        Assert.Equal(("h1 open hid hid0 !busy", "h2 open hid hid1"), (lines[0], lines[1]));
        Assert.StartsWith("h2 hid> " + Hex(ThermaltakeProtocol.Report(ThermaltakeProtocol.Conn(100, Date))), lines[2]);
    }

    [Fact]
    public void Start_WithNoCollectionThatOpens_AnswersTheLastStatus_AndWithNoneListed_NotSupported()
    {
        var (devices, device) = Plug(SixInch);
        device.Hid.OpenStatus = PanelDeviceStatus.Access;
        var identity = new PanelIdentity("toughliquid6inch", 1480, 720, null, null);

        var open = Driver(devices).Open(SixInch, identity, new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        Assert.Equal((null, PanelDeviceStatus.Access), (open.Session, open.Status));

        device.Hid.MaxOutputReportLength = 65;
        var none = Driver(devices).Open(SixInch, identity, new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (none.Session, none.Status));
    }

    public static TheoryData<string, string> RefusedHandshakes() => new()
    {
        { "conn", "404 Not Found" },
        { "realtimeDisplay", "404 Not Found" },
    };

    [Theory]
    [MemberData(nameof(RefusedHandshakes))]
    public void AHandshakeRefused_AnswersNoSession_AndTurnsRealtimeDisplayOffAsTheDisposeDid(string refused, string text)
    {
        // ThermaltakePanelDeviceTask.cs:65-71: a handshake that fails disposes the device, whose Dispose (:273-282) sends
        // POST realtimeDisplay 1 {"enable":false} and reads its reply, then lets the stream go.
        var (devices, device) = Plug(SixInch);
        var no = Answer(text);
        device.Hid.Reply(refused == "conn" ? no : Ok);
        if (refused != "conn")
            device.Hid.Reply(no);
        device.Hid.Respond(_ => Ok);
        var oracle = new V1ThermaltakeHidDevice { Now = () => Date };
        oracle.Replies.Enqueue(refused == "conn" ? (no, no.Length) : (Ok, Ok.Length));
        if (refused != "conn")
            oracle.Replies.Enqueue((no, no.Length));
        Assert.False(oracle.Handshake());
        oracle.Replies.Enqueue((Ok, Ok.Length));
        oracle.DisableRealtimeDisplay();

        var open = Driver(devices).Open(SixInch, Probed(Driver(devices), SixInch), new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.Null(open.Session);
        Assert.NotEqual(PanelDeviceStatus.Ok, open.Status);
        Assert.Contains(refused, open.Message);
        var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.HidWrite).Select(e => Hex(e.Data));
        Assert.Equal(oracle.Reports.Select(Hex), writes);
        Assert.Equal("h1 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void AHandshakeUnanswered_AnswersTimeout()
    {
        // ReadResponse (:163-193): a read that times out is no answer, and the handshake fails (:249-256).
        var (devices, device) = Plug(SixInch);

        var open = Driver(devices).Open(SixInch, Probed(Driver(devices), SixInch), new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Timeout), (open.Session, open.Status));
        Assert.Equal("h1 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void AStartWhoseDeviceGoes_TriesNothingMore()
    {
        var (devices, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);
        device.UnplugAt(2); // the conn's write

        var open = Driver(devices).Open(SixInch, Probed(Driver(devices), SixInch), new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Gone), (open.Session, open.Status));
        Assert.Equal(["h1 open hid hid0", "h1 hid> !gone", "h1 close hid"], device.Trace.Entries.Select(PanelTrace.FormatLine).Select(l => l.StartsWith("h1 hid> ", StringComparison.Ordinal) ? "h1 hid> " + l[^5..] : l));
    }

    // ---- The session ----

    [Theory]
    [MemberData(nameof(Models))]
    public void Capabilities_AreTheModelsSize_Jpeg85_30Fps_DimmedInSoftware(string usb, string model, int width, int height)
    {
        // The size from the table (ThermaltakePanelModelDatabase.cs:20-40); JPEG at the device's default quality 85
        // (Models/ThermaltakePanelDevice.cs:54); at most 30 fps, 1.4's Target FPS slider's top (UsbPanelsPage.xaml:1395);
        // brightness by dimming the frame (ThermaltakePanelDeviceTask.cs:199-203, :220-235), never the device's command.
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate);
        device.Hid.Respond(_ => Ok);

        using var session = Start(Driver(devices), candidate);
        var caps = session.Capabilities;

        Assert.Equal((width, height, PanelFrameFormat.Jpeg, 30, 85), (caps.Width, caps.Height, Assert.Single(caps.Formats), caps.MaxFps, caps.JpegQuality));
        Assert.True(caps.DimInSoftware);
        Assert.Null(caps.Brightness);
        Assert.Null(caps.MaxFrameBytes);
        Assert.False(caps.RotatesItself);
        _ = model;
    }

    [Fact]
    public void AFrameWhoseDeviceGoes_StopsThere_AndTheStopSendsNothing()
    {
        var (devices, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);
        var session = Start(Driver(devices), SixInch);
        device.UnplugAt(device.Calls + 2); // the frame's second chunk

        var write = session.WriteFrame(new PanelFrameView(Jpeg(2500), PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);
        var calls = device.Calls;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));

        Assert.Equal(PanelDeviceStatus.Gone, write.Status);
        Assert.Equal(calls, device.Calls);
        Assert.Equal("h1 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void Stop_TurnsRealtimeDisplayOff_As1_4sDispose_AndNothingIsWrittenAfterClose()
    {
        // 1.4.x's stop disposed the device (ThermaltakePanelDeviceTask.cs:89-93), whose Dispose turned realtime
        // display off (:278) before the stream went (:279). Close(screenOff: false), for a device gone, writes nothing.
        var (devices, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);

        var session = Start(Driver(devices), SixInch);
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();
        var lines = device.Trace.Entries.Skip(5).Select(PanelTrace.FormatLine).ToList();
        Assert.Equal(
            [$"h1 hid> {Hex(ThermaltakeProtocol.Report(ThermaltakeProtocol.RealtimeDisplay(false, 102, Date)))}", $"h1 hid< {Hex(Ok)}", "h1 close hid"],
            lines);
        Assert.Equal(0, device.CallsAfterDispose);

        var (quietDevices, quiet) = Plug(SixInch);
        quiet.Hid.Respond(_ => Ok);
        var unplugged = Start(Driver(quietDevices), SixInch);
        var before = quiet.Calls;
        unplugged.Close(screenOff: false, TimeSpan.FromSeconds(3));
        unplugged.Dispose();
        Assert.Equal(before, quiet.Calls);
        Assert.Equal("h1 close hid", PanelTrace.FormatLine(quiet.Trace.Entries[^1]));
    }

    [Fact]
    public void TheStopsReply_IsReadOnlyWithinTheBudget()
    {
        // The screen-off's write and read each get what is left of the budget; none starts once it is spent.
        var (devices, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);
        var session = Start(Driver(devices), SixInch);
        var before = device.Calls;

        session.Close(screenOff: true, TimeSpan.Zero);
        session.Dispose();

        Assert.Equal(before, device.Calls);
    }

    [Fact]
    public void AnEmptyFrame_SendsNothing_As1_4Did()
    {
        // SendJpegFrame (:119): no bytes, no chunks.
        var (devices, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);
        using var session = Start(Driver(devices), SixInch);
        var before = device.Calls;

        var write = session.WriteFrame(new PanelFrameView([], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal((PanelDeviceStatus.Ok, before), (write.Status, device.Calls));
    }

    // ---- The conformance suite ----

    [Theory]
    [MemberData(nameof(Models))]
    public void TheDriver_PassesTheConformanceSuite(string usb, string model, int width, int height)
    {
        var candidate = CandidateOf(usb);
        var failures = Conformance.Check(() =>
        {
            var (devices, device) = Plug(candidate);
            device.Hid.Respond(_ => Ok);
            return new ConformanceSetup(new ThermaltakeDriver(devices), candidate, device);
        });

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        _ = (model, width, height);
    }

    // ---- The 1.x import ----

    [Fact]
    public void V1Import_TheFixturesToughLiquid6Inch_MapsToItsModel_AtItsQuality()
    {
        // §2.2 (5): the engine's 1.x fixture read through V1SettingsReader; its Thermaltake devices' Model maps through
        // the package's v1Model, and their JpegQuality (1.4's per-device setting, 85 by default) is the family's own.
        // The family has no settings of its own to import: rotation, brightness and the frame rate are the engine's.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var settings = V1SettingsReader.Read(XElement.Load(path, LoadOptions.SetLineInfo), path, new ImportLimits(), new ImportDiagnostics(100));
        var devices = settings.Devices.Where(d => d.Family == ThermaltakeDriver.FamilyId).ToList();
        var family = PanelManifest.Read(typeof(ThermaltakeDriver).Assembly).Family(ThermaltakeDriver.FamilyId);

        Assert.Equal(2, devices.Count);
        var (fakes, device) = Plug(SixInch);
        device.Hid.Respond(_ => Ok);
        using var session = Start(Driver(fakes), SixInch);
        foreach (var v1 in devices)
        {
            var model = Assert.Single(family.Models, m => m.V1Model == v1.Fields["Model"]);
            Assert.Equal(("toughliquid6inch", 1480, 720), (model.Id, model.Width, model.Height));
            Assert.Equal(int.Parse(v1.Fields["JpegQuality"], System.Globalization.CultureInfo.InvariantCulture), session.Capabilities.JpegQuality);
        }
        // 1.4's Unknown names no model: the import takes such a device's model by its USB id instead.
        Assert.DoesNotContain(family.Models, m => m.V1Model == "Unknown");
    }
}
