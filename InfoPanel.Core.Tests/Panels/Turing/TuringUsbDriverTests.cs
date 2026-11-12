using InfoPanel.Core.Tests.Panels.Turing.V1Oracle;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Turing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The USB panels over the kit's fake devices (§2.2 (2), (6), (8)): Turing's (<c>1cbe:00xx</c>) as 1.4.x drove them
/// through the library's <c>ScreenDevice</c> (<c>Services/TuringPanelUsbDeviceTask.cs</c>), Lian Li's
/// (<c>1cbe:a0xx</c>) as 1.4.x's <c>Services/LianLiPanelDeviceTask.cs</c> did; each trace built from the oracle fed the
/// same replies, in 1.4.x's call order (each step cited); the conformance suite.
/// </summary>
public sealed class TuringUsbDriverTests
{
    private const int Timestamp = 0x0102_0304;
    private static readonly DateTime Now = new(2026, 11, 12, 16, 30, 5, DateTimeKind.Local);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>A panel's answer: 512 bytes, byte 8 the buffer level Lian Li reports.</summary>
    private static byte[] Answer(byte level = 0)
    {
        var answer = new byte[512];
        answer[0] = 0x0a;
        answer[8] = level;
        return answer;
    }

    private static (FakePanelDevices Devices, FakePanelDevice Device) Plug(PanelCandidate candidate, Func<byte[]?>? reply = null)
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(candidate);
        // One answer a command, read once: LibUsbDotNet's ReadFlush before the next command finds nothing.
        PanelTraceEntry? answered = null;
        device.Usb.Respond(last =>
        {
            if (last == null || ReferenceEquals(last, answered))
                return null;
            answered = last;
            return (reply ?? (() => Answer()))();
        });
        return (devices, device);
    }

    private static IPanelDriver Driver(string family, IPanelDevices devices) => family == TuringDriver.FamilyId
        ? new TuringDriver(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Timestamp = () => Timestamp }
        : new LianLiDriver(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Timestamp = () => Timestamp, LocalNow = () => Now };

    public static TheoryData<string, ushort, string, int, int> Models() => new()
    {
        { "turing", 0x0088, "rev88inchusb", 480, 1920 },
        { "turing", 0x0080, "rev8inchusb", 800, 1280 },
        { "turing", 0x0092, "rev92inchusb", 464, 1920 },
        { "turing", 0x0046, "rev46inchusb", 320, 960 },
        { "turing", 0x0050, "rev5inchusb", 720, 1280 },
        { "turing", 0x0016, "rev16inchusb", 400, 400 },
        { "turing", 0x0021, "rev21inchusb", 480, 480 },
        { "lianli", 0xA088, "universalscreen88inch", 480, 1920 },
        { "lianli", 0xA092, "universalscreen92inch", 464, 1920 },
        { "lianli", 0xA068, "hydroshiftiioledcurve", 2288, 1080 },
        { "lianli", 0xA034, "hydroshiftiilcd", 480, 480 },
    };

    private static PanelCandidate CandidateOf(ushort product) => new(0x1CBE, product, 1, 9, "1-6");

    private static IPanelSession Start(IPanelDriver driver, PanelCandidate candidate, int brightness = 70)
    {
        var probe = driver.Probe(candidate, TestContext.Current.CancellationToken);
        Assert.True(probe is { Mine: true, Identity: not null }, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(brightness), TestContext.Current.CancellationToken);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        return open.Session!;
    }

    // ---- The probe ----

    [Theory]
    [MemberData(nameof(Models))]
    public void Probe_TakesTheModelItsUsbIdNames_AndTouchesNothing(string family, ushort product, string model, int width, int height)
    {
        // TuringPanelHelper.cs:21-58 and LianLiPanelHelper.cs:15-57: the model by VID and PID, nothing sent.
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);

        var probe = Driver(family, devices).Probe(candidate, TestContext.Current.CancellationToken);

        Assert.Equal(new PanelProbe(true, new PanelIdentity(model, width, height, null, null), PanelDeviceStatus.Ok), probe);
        Assert.Empty(device.Trace.Entries);
    }

    [Fact]
    public void Probe_OfAnotherFamilysId_PassesItOn_AndOfADeviceGone_AnswersGone()
    {
        var (devices, _) = Plug(CandidateOf(0xA088));
        var turing = Driver("turing", devices).Probe(CandidateOf(0xA088), TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Ok), (turing.Mine, turing.Status));

        var gone = Driver("lianli", new FakePanelDevices()).Probe(CandidateOf(0xA088), TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Gone), (gone.Mine, gone.Status));

        var seamless = new LianLiDriver().Probe(CandidateOf(0xA088), TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.NotSupported), (seamless.Mine, seamless.Status));
    }

    // ---- Turing: the start, a frame, brightness and the stop, as the library did them ----

    [Fact]
    public void Turing_StartFrameBrightnessAndStop_Trace_IsTheLibrarys()
    {
        var candidate = CandidateOf(0x0088);
        var (devices, device) = Plug(candidate);
        var jpeg = new byte[3000];
        new Random(1).NextBytes(jpeg);

        // TuringPanelUsbDeviceTask.cs, fed the same answers: Sync (:127), Sync (:135), StopMedia (:144), SetBrightness
        // (:149), Sync (:151); a frame, DrawJpeg (:228); a brightness change, SetBrightness and Sync (:209-213); the stop's
        // SetBrightness(0) (:266). Each command: ReadFlush, the packet, the image's bytes as a second write, one read.
        var oracle = new OracleUsb();
        using var library = new V1ScreenDevice(oracle) { Timestamp = Timestamp };
        for (var i = 0; i < 9; i++)
            oracle.Replies.Enqueue(Answer());
        Assert.True(library.Sync());
        Assert.True(library.Sync());
        library.StopMedia();
        library.SetBrightness(70);
        Assert.True(library.Sync());
        Assert.True(library.DrawJpeg(jpeg));
        library.SetBrightness(30);
        Assert.True(library.Sync());
        library.SetBrightness(0);

        var session = Start(Driver("turing", devices), candidate);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(jpeg, PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken).Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(30));
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        Assert.Equal(Expected(oracle), Transfers(device));
        Assert.StartsWith("h1 open usb", PanelTrace.FormatLine(device.Trace.Entries[0]));
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
        Assert.Equal(0, device.CallsAfterDispose);
    }

    /// <summary>The oracle's calls as trace lines: a flush is one read that finds nothing.</summary>
    private static List<string> Expected(OracleUsb oracle)
    {
        return [.. oracle.Calls.Select(c => c switch
        {
            "flush" => "h1 < ep81 !timeout",
            "read" => "h1 < ep81 " + Hex(Answer()),
            _ => "h1 > ep01 " + c["write ".Length..],
        })];
    }

    private static List<string> Transfers(FakePanelDevice device) =>
        [.. device.Trace.Entries.Where(e => e.Op is PanelTraceOp.UsbWrite or PanelTraceOp.UsbRead).Select(PanelTrace.FormatLine)];

    [Fact]
    public void Turing_AFrameWhoseAnswerDoesntCome_EndsTheSession_AsTheLibrarysFalse()
    {
        // ScreenDevice.RequestResponse: a read that brings no 512 bytes is false; DrawJpeg false ends the send loop
        // (TuringPanelUsbDeviceTask.cs:228-233).
        var candidate = CandidateOf(0x0021);
        var answers = 5; // the start's
        var (devices, _) = Plug(candidate, () => answers-- > 0 ? Answer() : null);
        using var session = Start(Driver("turing", devices), candidate);

        var write = session.WriteFrame(new PanelFrameView(new byte[100], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(PanelDeviceStatus.Timeout, write.Status);
    }

    [Theory]
    [InlineData("turing", (ushort)0x0050)]
    [InlineData("lianli", (ushort)0xA088)]
    public void AStartWhoseFirstSyncIsntAnswered_SendsNothingMore_AndLetsGo(string family, ushort product)
    {
        // 1.4.x's finally (TuringPanelUsbDeviceTask.cs:262-272, LianLiPanelDeviceTask.cs:324-343) set the brightness to 0
        // (Lian Li: StopMedia first) even when the first Sync had gone unanswered (:127-131, :194-198): a device that never
        // answered, which may not be a panel at all, was sent mutating commands. Only a device that answered a Sync gets
        // the stop's commands; this one is only let go.
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate, () => null);
        var driver = Driver(family, devices);
        var identity = driver.Probe(candidate, TestContext.Current.CancellationToken).Identity!;

        var open = driver.Open(candidate, identity, new PanelOpenOptions(70), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Timeout), (open.Session, open.Status));
        var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite).ToList();
        Assert.Equal(Hex(TuringUsbProtocol.Packet(family == "turing" ? UsbVariant.Turing : UsbVariant.LianLi, TuringUsbProtocol.Command(TuringUsbProtocol.Sync, Timestamp))), Hex(Assert.Single(writes).Data));
        Assert.Equal($"h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Theory]
    [InlineData("turing", (ushort)0x0050)]
    [InlineData("lianli", (ushort)0xA088)]
    public void AStartCancelledDuringItsFirstSync_SendsNothingMore_AndLetsGo(string family, ushort product)
    {
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate, () => null);
        var driver = Driver(family, devices);
        var identity = driver.Probe(candidate, TestContext.Current.CancellationToken).Identity!;
        using var cancel = new CancellationTokenSource();
        var stalled = device.Stall(4); // the open, the flush's read, the Sync, then its answer's read

        var start = Task.Run(() => driver.Open(candidate, identity, new PanelOpenOptions(70), cancel.Token), TestContext.Current.CancellationToken);
        stalled.Wait(TestContext.Current.CancellationToken);
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => start.GetAwaiter().GetResult());
        Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.UsbWrite);
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    // ---- What the device left on its in endpoint ----

    [Theory]
    [InlineData("turing", (ushort)0x0088)]
    [InlineData("lianli", (ushort)0xA088)]
    public void ABacklogTheFlushDoesntEmpty_FailsTheCommand_WithNothingWritten(string family, ushort product)
    {
        // LibUsbDotNet's ReadFlush stops at 128 reads; a device that still has more on its endpoint after them hasn't
        // settled, and a command written then would take a stale packet for its answer.
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);
        using var session = Start(Driver(family, devices), candidate);
        for (var i = 0; i < 129; i++)
            device.Usb.Reply(Answer());
        var before = device.Trace.Entries.Count;

        var write = session.WriteFrame(new PanelFrameView(new byte[300], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(PanelDeviceStatus.Io, write.Status);
        Assert.Equal(128, device.Trace.Entries.Skip(before).Count(e => e.Op == PanelTraceOp.UsbRead));
        Assert.DoesNotContain(device.Trace.Entries.Skip(before), e => e.Op == PanelTraceOp.UsbWrite);
    }

    [Theory]
    [InlineData("turing", PanelDeviceStatus.Io)]
    [InlineData("turing", PanelDeviceStatus.NotSupported)]
    [InlineData("lianli", PanelDeviceStatus.Io)]
    [InlineData("lianli", PanelDeviceStatus.Gone)]
    public void AFlushReadThatFails_FailsTheCommand_WithNothingWritten(string family, PanelDeviceStatus status)
    {
        var candidate = CandidateOf(family == "turing" ? (ushort)0x0088 : (ushort)0xA088);
        var (devices, device) = Plug(candidate);
        using var session = Start(Driver(family, devices), candidate);
        var before = device.Trace.Entries.Count;
        device.Fail(device.Calls + 1, status); // the frame's command's flush

        var write = session.WriteFrame(new PanelFrameView(new byte[300], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(status, write.Status);
        Assert.DoesNotContain(device.Trace.Entries.Skip(before), e => e.Op == PanelTraceOp.UsbWrite);
    }

    [Theory]
    [InlineData("turing", (ushort)0x0021, 2, "StopMedia")]
    [InlineData("turing", (ushort)0x0021, 3, "the brightness")]
    [InlineData("lianli", (ushort)0xA034, 2, "StopMedia")]
    [InlineData("lianli", (ushort)0xA034, 3, "the clock")]
    [InlineData("lianli", (ushort)0xA034, 4, "StopClock")]
    [InlineData("lianli", (ushort)0xA034, 5, "the overlay's clearing")]
    [InlineData("lianli", (ushort)0xA034, 6, "the background's clearing")]
    [InlineData("lianli", (ushort)0xA034, 7, "the frame rate")]
    [InlineData("lianli", (ushort)0xA034, 8, "the brightness")]
    public void AStartCommandWhoseFlushFails_FailsTheStart_ItsCommandUnwritten(string family, ushort product, int command, string step)
    {
        // The start's commands whose answers 1.4.x didn't look at still go only after the in endpoint is read away: a
        // flush that fails fails the start there. Each command is three calls (its flush, its write, its answer's read),
        // after the open.
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);
        var driver = Driver(family, devices);
        var identity = driver.Probe(candidate, TestContext.Current.CancellationToken).Identity!;
        device.Fail(device.Calls + 2 + (3 * command), PanelDeviceStatus.Io);

        var open = driver.Open(candidate, identity, new PanelOpenOptions(70), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Io), (open.Session, open.Status));
        Assert.Contains(step, open.Message);
        var entries = device.Trace.Entries.ToList();
        var failed = entries.FindIndex(e => e.Status == PanelDeviceStatus.Io);
        Assert.Equal(PanelTraceOp.UsbRead, entries[failed].Op);
        Assert.Equal(command, entries.Take(failed).Count(e => e.Op == PanelTraceOp.UsbWrite));
        Assert.NotEqual(PanelTraceOp.UsbWrite, entries[failed + 1].Op); // what follows is the stop's flush, not the command
    }

    [Theory]
    [InlineData("turing", (ushort)0x0088)]
    [InlineData("lianli", (ushort)0xA088)]
    public async Task ABrightnessWhoseFlushFails_FailsTheChange_AndTheEngineKeepsTheOldBrightness(string family, ushort product)
    {
        // A backlog of 129 packets: the brightness command's flush reads 128 and fails (Io), so the brightness isn't
        // written; the change answers that, though the Sync after it would drain the last packet and be answered. The
        // engine's proxy keeps the old brightness, so asking for the same one again goes to the panel.
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);
        var log = Serilog.Core.Logger.None;
        var slots = Directory.CreateTempSubdirectory("panel-slots-").FullName;
        var host = await InfoPanel.Core.Tests.Panels.Hosted.TestHost.StartAsync(Driver(family, devices), log);
        try
        {
            var link = new InfoPanel.Core.Tests.Panels.Hosted.TestLink(slots) { Current = host.Connection };
            var model = family == "turing" ? "rev88inchusb" : "universalscreen88inch";
            var hosted = new InfoPanel.Panels.Hosted.HostedPanelDriver(new InfoPanel.Panels.PanelPackageFamily(family, family,
                [new PanelMatch(0x1CBE, product)], [new InfoPanel.Panels.PanelPackageModel(model, model, 480, 1920, null)]), link, log);
            var open = hosted.Open(candidate, new PanelIdentity(model, 480, 1920, null, null), new PanelOpenOptions(70), TestContext.Current.CancellationToken);
            Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
            using var session = open.Session!;
            var variant = family == "turing" ? UsbVariant.Turing : UsbVariant.LianLi;
            var brightness30 = Hex(TuringUsbProtocol.Packet(variant, TuringUsbProtocol.WithValue(TuringUsbProtocol.Brightness, 30, Timestamp)));
            for (var i = 0; i < 129; i++)
                device.Usb.Reply(Answer());

            Assert.Equal(PanelDeviceStatus.Io, session.SetBrightness(30));
            Assert.DoesNotContain(device.Trace.Entries, e => e.Op == PanelTraceOp.UsbWrite && Hex(e.Data) == brightness30);

            Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(30));
            Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.UsbWrite && Hex(e.Data) == brightness30);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(slots);
        }
    }

    [Fact]
    public void LianLi_AQueryBlockWhoseFlushFails_FailsTheFrame()
    {
        // An image's answer reporting 5 frames buffered has the device asked (QueryBlock); that command's flush failing
        // fails the frame, unwritten, as any command's does.
        var answers = new Queue<byte[]>([.. Enumerable.Range(0, 10).Select(_ => Answer()), Answer(5), Answer(2)]);
        var candidate = CandidateOf(0xA088);
        var (devices, device) = Plug(candidate, () => answers.Count > 0 ? answers.Dequeue() : null);
        using var session = Start(Driver("lianli", devices), candidate);
        device.Fail(device.Calls + 4, PanelDeviceStatus.Io); // the frame's flush, its write, its answer, then QueryBlock's flush
        var before = device.Trace.Entries.Count;

        var write = session.WriteFrame(new PanelFrameView(new byte[300], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(PanelDeviceStatus.Io, write.Status);
        Assert.Single(device.Trace.Entries.Skip(before), e => e.Op == PanelTraceOp.UsbWrite); // the frame's, not QueryBlock's
    }

    // ---- Lian Li: the start, a frame, brightness and the stop, as 1.4.x did them ----

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    public void LianLi_StartFrameBrightnessAndStop_Trace_Is1_4s(byte level)
    {
        // The answers, in order: the start's ten commands; the frame's, its buffer `level`, which above 3 has the device
        // asked (QueryBlock) until it is 2 or less; the brightness change's two; the stop's two.
        List<byte[]> answers = [.. Enumerable.Range(0, 10).Select(_ => Answer()), Answer(level)];
        if (level > 3)
            answers.AddRange([Answer(4), Answer(2)]);
        answers.AddRange(Enumerable.Range(0, 4).Select(_ => Answer()));
        var candidate = CandidateOf(0xA034);
        var queue = new Queue<byte[]>(answers);
        var (devices, device) = Plug(candidate, () => queue.Count > 0 ? queue.Dequeue() : null);
        var jpeg = new byte[2000];
        new Random(2).NextBytes(jpeg);

        // LianLiPanelDeviceTask.cs, fed the same answers: Sync (:194), Sync (:202), StopMedia (:210), the layers'
        // preparation (:97-132: the clock, StopClock, the transparent PNG, the black JPEG, the frame rate 30), SetBrightness
        // (:217), Sync (:219); a frame, DrawJpeg (:290), its answer's buffer level drained (LianLiUsbScreenDevice.cs:189-194);
        // a brightness change (:271-281); the stop: StopMedia and SetBrightness(0) (:326-342). Each command: ReadFlush, the
        // packet and any image as one write, one read (2000 ms; an image's 200 ms, which may time out).
        var oracle = new OracleUsb();
        foreach (var answer in answers)
            oracle.Replies.Enqueue(answer);
        using var lianLi = new V1LianLiUsbScreenDevice(oracle) { Timestamp = Timestamp, Now = Now };
        Assert.True(lianLi.Sync());
        Assert.True(lianLi.Sync());
        lianLi.StopMedia();
        lianLi.SyncClockOnly();
        lianLi.StopClock();
        lianLi.DrawPngLayer(SolidImages.TransparentPng(480, 480));
        lianLi.DrawJpegLayer(SolidImages.BlackJpeg(480, 480));
        lianLi.SetFrameRate(30);
        lianLi.SetBrightness(70);
        Assert.True(lianLi.Sync());
        Assert.True(lianLi.DrawJpeg(jpeg));
        lianLi.SetBrightness(30);
        Assert.True(lianLi.Sync());
        lianLi.StopMedia();
        lianLi.SetBrightness(0);
        Assert.Empty(oracle.Replies);

        var session = Start(Driver("lianli", devices), candidate);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(jpeg, PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken).Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(30));
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        var replies = new Queue<byte[]>(answers);
        var expected = oracle.Calls.Select(c => c switch
        {
            "flush" => "h1 < ep81 !timeout",
            "read" => "h1 < ep81 " + Hex(replies.Dequeue()),
            _ => "h1 > ep01 " + c["write ".Length..],
        }).ToList();
        Assert.Equal(expected, Transfers(device));
        Assert.Equal([2000, 2000, 2000, 2000, 2000, 200, 200, 2000, 2000, 2000], lianLi.ReadTimeouts.Take(10));
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    [Fact]
    public void LianLi_AnImagesAnswerThatDoesntCome_IsFine_As1_4Took_It()
    {
        // LianLiUsbScreenDevice.cs:263-270: an image's read that times out (200 ms) is success.
        var candidate = CandidateOf(0xA088);
        var answers = 10;
        var (devices, _) = Plug(candidate, () => answers-- > 0 ? Answer() : null);
        using var session = Start(Driver("lianli", devices), candidate);

        var write = session.WriteFrame(new PanelFrameView(new byte[100], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(PanelDeviceStatus.Ok, write.Status);
    }

    [Fact]
    public void LianLi_AFrameOverItsLimit_IsNeverSent_AndTheEngineIsToldTheLimit()
    {
        // LianLiUsbScreenDevice.cs:176-183: an image over 512,000 bytes is dropped; the engine encodes within it
        // (MaxFrameBytes) so it never comes.
        var candidate = CandidateOf(0xA068);
        var (devices, device) = Plug(candidate);
        using var session = Start(Driver("lianli", devices), candidate);
        var before = device.Calls;

        var write = session.WriteFrame(new PanelFrameView(new byte[512_001], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);

        Assert.Equal(512_000, session.Capabilities.MaxFrameBytes);
        Assert.Equal(PanelDeviceStatus.NotSupported, write.Status);
        Assert.Equal(before, device.Calls);
    }

    // ---- The session ----

    [Theory]
    [MemberData(nameof(Models))]
    public void Capabilities_AreTheModelsSize_Jpeg90_30Fps_Brightness0To100(string family, ushort product, string model, int width, int height)
    {
        // The size from the table; JPEG at the devices' default quality 90 (Models/TuringPanelDevice.cs:65,
        // LianLiPanelDevice.cs:55) at their default 30 fps (:62, :52); brightness 0-100 sent as it is (SetBrightness's byte 8).
        var candidate = CandidateOf(product);
        var (devices, _) = Plug(candidate);
        using var session = Start(Driver(family, devices), candidate);
        var caps = session.Capabilities;

        Assert.Equal((width, height, PanelFrameFormat.Jpeg, 30, 90), (caps.Width, caps.Height, Assert.Single(caps.Formats), caps.MaxFps, caps.JpegQuality));
        Assert.Equal(new PanelRange(0, 100), caps.Brightness);
        Assert.False(caps.RotatesItself);
        Assert.Equal(family == "lianli" ? 512_000 : null, caps.MaxFrameBytes);
        _ = model;
    }

    [Theory]
    [InlineData("turing", (ushort)0x0046)]
    [InlineData("lianli", (ushort)0xA092)]
    public void AFrameWhoseDeviceGoes_StopsThere_AndTheStopSendsNothing(string family, ushort product)
    {
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);
        var session = Start(Driver(family, devices), candidate);
        device.UnplugAt(device.Calls + 2); // the frame's packet

        var write = session.WriteFrame(new PanelFrameView(new byte[700], PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);
        var calls = device.Calls;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        Assert.Equal(PanelDeviceStatus.Gone, write.Status);
        Assert.Equal(calls, device.Calls);
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Theory]
    [InlineData("turing", (ushort)0x0016)]
    [InlineData("lianli", (ushort)0xA088)]
    public void TheStop_KeepsToItsBudget(string family, ushort product)
    {
        var candidate = CandidateOf(product);
        var (devices, device) = Plug(candidate);
        var session = Start(Driver(family, devices), candidate);
        var before = device.Calls;

        session.Close(screenOff: true, TimeSpan.Zero);
        session.Dispose();

        Assert.Equal(before, device.Calls);
        Assert.Equal("h1 close usb if0", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    // ---- The conformance suite ----

    [Theory]
    [MemberData(nameof(Models))]
    public void TheDriver_PassesTheConformanceSuite(string family, ushort product, string model, int width, int height)
    {
        var candidate = CandidateOf(product);
        var failures = Conformance.Check(() =>
        {
            var (devices, device) = Plug(candidate);
            return new ConformanceSetup(Driver(family, devices), candidate, device);
        });

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        _ = (model, width, height);
    }
}
