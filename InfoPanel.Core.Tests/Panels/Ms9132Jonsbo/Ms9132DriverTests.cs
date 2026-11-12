using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The MS9132 sessions' traces against 1.4.x's call order, run on the kit's fakes: the DS339's start (its startup
/// block, the video interface, the status, the mode set for its EDID), frames in 64 KiB writes with the status after each
/// and the output turned on after the second, and its stop's power-off, else its stay-powered close after a failed
/// transfer (JonsboMs9132Device.cs, JonsboPanelDeviceTask.cs:295-403); the VMAX's streaming start, frames, status poll,
/// screen switch and its stop that sends nothing (VmaxUsbDevice.cs, VmaxPanelDeviceTask.cs). Each step's bytes are the
/// oracle's (V1Oracle/), driven in 1.4.x's order.
/// </summary>
public sealed class Ms9132DriverTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void NoWait(TimeSpan _, CancellationToken stop) => stop.ThrowIfCancellationRequested();

    private static byte[] Pixels(int width, int height, int seed) =>
        [.. Enumerable.Range(0, width * height * 3).Select(i => (byte)(i * 31 + seed))];

    private static PanelWrite Write(IPanelSession session, byte[] pixels, long id) =>
        session.WriteFrame(new PanelFrameView(pixels, PanelFrameFormat.Bgr888, id, false), None);

    /// <summary>1.4.x's DS339 device over the same EDID and replies as the fake.</summary>
    private static V1JonsboMs9132Device Oracle(byte[]? edid) => new()
    {
        GetFeature = (report, reply) =>
        {
            EdidSamples.Reply(report, edid).CopyTo(reply, 0);
            if (report[1] == 0xB5 && report[2] == 0x00 && report[3] == 0x32)
                reply[4] = 1;
        },
    };

    [Fact]
    public void Ds339_Trace_StartTwoFramesAndTheStop_Are1_4s()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var waits = new List<int>();
        var driver = new JonsboDriver(devices) { Wait = (time, stop) => waits.Add((int)time.TotalMilliseconds) };

        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None).Session!;

        var oracle = Oracle(EdidSamples.Ds339);
        oracle.RunStartupBlock();
        oracle.IsPanelConnected();
        var (width, height, vic) = V1JonsboPanelDeviceTask.ChooseMode(JonsboPanelModelDatabase.Models[JonsboPanelModel.DS339], oracle);
        oracle.SetMode(width, height, vic);
        Assert.Equal((376, 960, (byte)171), (width, height, vic));
        Assert.Equal(oracle.Reports.Select(r => r[1..]), fake.Reports());
        Assert.Equal(oracle.Delays, waits);
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3"], fake.Handles());
        var caps = session.Capabilities;
        Assert.Equal((376, 960, PanelFrameFormat.Bgr888, 60, true, (PanelRange?)null), (caps.Width, caps.Height, Assert.Single(caps.Formats), caps.MaxFps, caps.DimInSoftware, caps.Brightness));

        // Two frames: each in 64 KiB writes to endpoint 0x04, the status after each, the output on after the second.
        var mark = fake.Device.Trace.Entries.Count;
        oracle.Reports.Clear();
        var sent = 0;
        for (var frame = 1; frame <= 2; frame++)
        {
            var pixels = Pixels(376, 960, frame);
            Assert.Equal(PanelDeviceStatus.Ok, Write(session, pixels, frame).Status);
            V1JonsboPanelDeviceTask.SendOne(oracle, pixels, 376, 960, ref sent);
        }
        Assert.Equal(oracle.Chunks, fake.Writes(mark));
        Assert.All(fake.Device.Trace.Entries.Skip(mark).Where(e => e.Op == PanelTraceOp.UsbWrite), e => Assert.Equal(("ep04", 2), (e.Target, e.Handle)));
        Assert.Equal(oracle.Reports.Select(r => r[1..]), fake.Reports(mark));
        Assert.Equal(34, oracle.Chunks.Count);

        // The stop: 1.4.x's power-off, then the collection and the interface let go.
        mark = fake.Device.Trace.Entries.Count;
        oracle.Reports.Clear();
        session.Close(screenOff: true, Bound);
        oracle.PowerOff();
        Assert.Equal(oracle.Reports.Select(r => r[1..]), fake.Reports(mark));
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3", "h1 close hid", "h2 close usb if3"], fake.Handles());
        Assert.Equal(0, fake.Device.CallsAfterDispose);
    }

    [Fact]
    public void Ds339_AFailedTransfer_EndsTheSession_AndItsCloseLeavesThePanelPowered()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None).Session!;
        fake.Device.Fail(fake.Device.Calls + 3, PanelDeviceStatus.Timeout); // the frame's third write

        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, Pixels(376, 960, 1), 1).Status);
        var mark = fake.Device.Trace.Entries.Count;
        session.Close(screenOff: true, Bound);
        session.Dispose();

        // JonsboPanelDeviceTask.cs:359-361: 1.4.x's retry left the panel powered.
        Assert.Empty(fake.Reports(mark));
        Assert.Equal(["h1 close hid", "h2 close usb if3"], fake.Device.Trace.Entries.Skip(mark).Select(PanelTrace.FormatLine));
    }

    [Fact]
    public void Ds339_AnUnplug_EndsTheSession_AndItsCloseSendsNothing()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None).Session!;
        fake.Device.UnplugAt(fake.Device.Calls + 2);

        Assert.Equal(PanelDeviceStatus.Gone, Write(session, Pixels(376, 960, 1), 1).Status);
        var mark = fake.Device.Trace.Entries.Count;
        session.Close(screenOff: true, Bound);
        Assert.Equal(["h1 close hid", "h2 close usb if3"], fake.Device.Trace.Entries.Skip(mark).Select(PanelTrace.FormatLine));
    }

    [Fact]
    public void Ds339_AStatusPollThatTimesOut_AfterTheVideoEnable_EndsTheSession_AndItsCloseSendsNothing()
    {
        // The engine's HID seam closes a collection's stream when a feature report times out: the session can't poll or
        // power the panel off any more, so it ends (the engine opens it again), and its close sends nothing.
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None).Session!;
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(376, 960, 1), 1).Status);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(376, 960, 2), 2).Status); // the output turned on after it
        Assert.Contains(JonsboMs9132.EnableVideo[2], fake.Reports());
        var get = fake.Device.Calls + 17 + 2; // the third frame: 17 writes, then the status's set and get
        fake.Device.Fail(get, PanelDeviceStatus.Timeout);

        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, Pixels(376, 960, 3), 3).Status);
        Assert.Equal(get, fake.Device.Calls);
        var mark = fake.Device.Trace.Entries.Count;
        session.Close(screenOff: true, Bound);
        Assert.Equal(["h1 close hid", "h2 close usb if3"], fake.Device.Trace.Entries.Skip(mark).Select(PanelTrace.FormatLine));
    }

    [Fact]
    public void Ds339_AStartWhoseStatusAskTimesOut_Fails_AndLetsThePanelGo_SendingNothingMore()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        // Calls: 1 the collection's open, 2 to 113 the startup block, 114 the interface's open, 115 and 116 the status.
        fake.Device.Fail(116, PanelDeviceStatus.Timeout);

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Timeout), (open.Session, open.Status));
        Assert.Equal(116, fake.Device.Calls);
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3", "h1 close hid", "h2 close usb if3"], fake.Handles());
    }

    [Fact]
    public void Ds339_AVideoInterfaceGoneAtItsOpen_LetsTheCollectionGo_AndSendsNothingMore()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Usb.OpenStatus = PanelDeviceStatus.Gone;
        var driver = new JonsboDriver(devices) { Wait = NoWait };

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Gone), (open.Session, open.Status));
        Assert.Equal(JonsboMs9132.Startup, fake.Reports()); // the startup block, then nothing: no power-off
        Assert.Equal("h1 close hid", fake.Handles()[^1]);
    }

    [Fact]
    public void Ds339_AFailedModeSet_LeavesThePanelPowered_As1_4sRetry()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        // Calls: 1 the collection's open, 2 to 113 the startup block, 114 the interface's open, 115 and 116 the status, 117 the mode set's first.
        fake.Device.Fail(117, PanelDeviceStatus.Io);

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Io), (open.Session, open.Status));
        Assert.DoesNotContain(fake.Reports(), r => r[0] == 0xA6 && r[1] == 0x07 && r[2] == 0x00); // no power-off
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3", "h1 close hid", "h2 close usb if3"], fake.Handles());
    }

    [Fact]
    public void Ds339_AVideoInterfaceThatWontOpen_FailsTheStart_AndPowersThePanelOff_As1_4sDispose()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Usb.OpenStatus = PanelDeviceStatus.Access;
        var driver = new JonsboDriver(devices) { Wait = NoWait };

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Access), (open.Session, open.Status));
        Assert.Contains("WinUSB", open.Message, StringComparison.Ordinal);
        Assert.Equal([0xA6, 0x07, 0, 0, 0, 0, 0, 0], fake.Reports()[^1]);
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3 !access", "h1 close hid"], fake.Handles());
    }

    [Fact]
    public void Ds339_ACancelledStart_PowersThePanelOffAgain_AndLetsItGo()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        using var cancel = new CancellationTokenSource();
        var driver = new JonsboDriver(devices)
        {
            // The pause before the first frame is where the start is cancelled.
            Wait = (time, stop) =>
            {
                if (time == JonsboMs9132.FirstFrameDelay)
                    cancel.Cancel();
                stop.ThrowIfCancellationRequested();
            },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), cancel.Token));
        Assert.Equal([0xA6, 0x07, 0, 0, 0, 0, 0, 0], fake.Reports()[^1]);
        Assert.Equal(["h1 open hid hid0", "h2 open usb if3", "h1 close hid", "h2 close usb if3"], fake.Handles());
    }

    [Fact]
    public void AnUnknownModel_DoesntStart_UntilTheModelSettingNamesTheDs339_ThenRunsAtItsEdidsSize()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Unknown);
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        var identity = new PanelIdentity("edid-480x480", 480, 480, null, null);

        var refused = driver.Open(Ms9132Fake.Candidate, identity, new PanelOpenOptions(100), None);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (refused.Session, refused.Status));
        Assert.Contains("try it as", refused.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Device.Trace.Entries);

        var notSerial = driver.Open(Ms9132Fake.Candidate, identity, new PanelOpenOptions(100, FamilySettings: Json("""{"model": "ds916"}""")), None);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (notSerial.Session, notSerial.Status));
        Assert.Empty(fake.Device.Trace.Entries);

        // Tried as the DS339: 1.4.x drove it at its EDID's size, which its mode table has (480 × 480, 143).
        using var session = driver.Open(Ms9132Fake.Candidate, identity, new PanelOpenOptions(100, FamilySettings: Json("""{"model": "ds339"}""")), None).Session!;
        Assert.Equal((480, 480), (session.Capabilities.Width, session.Capabilities.Height));
        Assert.Contains(JonsboMs9132.SetMode(480, 480, 143)[6], fake.Reports());
    }

    [Fact]
    public void APanelTriedAsTheDs339_WhoseEdidSizeHasNoModeIndex_RunsAtTheDs339s_As1_4()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Block(1920, 1080, 14850, "MONITOR"));
        var driver = new JonsboDriver(devices) { Wait = NoWait };

        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("edid-1920x1080", 1920, 1080, null, null),
            new PanelOpenOptions(100, FamilySettings: Json("""{"model": "ds339"}""")), None).Session!;

        // JonsboPanelDeviceTask.cs:338-343: the model database's 376 × 960, VIC 171.
        Assert.Equal((376, 960), (session.Capabilities.Width, session.Capabilities.Height));
        Assert.Contains(JonsboMs9132.SetMode(376, 960, 171)[6], fake.Reports());
    }

    [Fact]
    public void TheWriteEndpoint_IsTheVideoInterfacesOwn()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Descriptor = fake.Device.Descriptor with
        {
            Interfaces = [fake.Device.Descriptor.Interfaces[0], new PanelUsbInterface(3, 0xFF, 0, 0, [new PanelUsbEndpoint(0x82, PanelEndpointType.Bulk, 512), new PanelUsbEndpoint(0x02, PanelEndpointType.Bulk, 512)])],
        };
        var driver = new JonsboDriver(devices) { Wait = NoWait };
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("ds339", 376, 960, null, null), new PanelOpenOptions(100), None).Session!;
        var mark = fake.Device.Trace.Entries.Count;

        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(376, 960, 1), 1).Status);
        Assert.All(fake.Device.Trace.Entries.Skip(mark).Where(e => e.Op == PanelTraceOp.UsbWrite), e => Assert.Equal("ep02", e.Target));
    }

    /// <summary>1.4.x's VMAX device over the same replies as the fake, its status clock at <paramref name="now"/>.</summary>
    private static V1VmaxUsbDevice VmaxOracle(Func<TimeSpan> now) => new()
    {
        GetFeature = (report, reply) => EdidSamples.Reply(report, EdidSamples.Vmax).CopyTo(reply, 0),
        Now = () => new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc) + now(),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Vmax_Trace_StartFramesStatusScreenSwitchAndTheStop_Are1_4s(bool screenOn)
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        var waits = new List<int>();
        var time = new ManualTime();
        var driver = new VmaxDriver(devices) { Wait = (t, _) => waits.Add((int)t.TotalMilliseconds), Time = time };
        var settings = Json($$"""{"screenSwitch": {{(screenOn ? "true" : "false")}}}""");

        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), new PanelOpenOptions(100, FamilySettings: settings), None).Session!;

        var oracle = VmaxOracle(() => time.Elapsed);
        oracle.InitializeStreamingMode();
        Assert.Equal(oracle.Reports.Select(r => r[1..]), fake.Reports());
        Assert.Equal(oracle.Delays, waits);
        Assert.Equal(["h1 open usb if3", "h2 open hid hid0"], fake.Handles());
        var caps = session.Capabilities;
        Assert.Equal((320, 960, PanelFrameFormat.Bgr888, 30, true), (caps.Width, caps.Height, Assert.Single(caps.Formats), caps.MaxFps, caps.DimInSoftware));

        // Frames at 0, 100 and 600 ms: the status polled at 0 and 600 (at most every 500 ms), the switch once frames flow.
        var mark = fake.Device.Trace.Entries.Count;
        oracle.Reports.Clear();
        oracle.Delays.Clear();
        waits.Clear();
        foreach (var at in new[] { 0, 100, 600 })
        {
            time.Elapsed = TimeSpan.FromMilliseconds(at);
            var pixels = Pixels(320, 960, at);
            Assert.Equal(PanelDeviceStatus.Ok, Write(session, pixels, at + 1).Status);
            V1VmaxPanelDeviceTask.SendOne(oracle, pixels, screenOn);
        }
        Assert.Equal(oracle.Chunks, fake.Writes(mark));
        Assert.All(fake.Device.Trace.Entries.Skip(mark).Where(e => e.Op == PanelTraceOp.UsbWrite), e => Assert.Equal(("ep04", 1), (e.Target, e.Handle)));
        Assert.Equal(oracle.Reports.Select(r => r[1..]), fake.Reports(mark));
        Assert.Equal(oracle.Delays, waits);
        Assert.Equal(2, fake.Reports(mark).Count(r => r.AsSpan().SequenceEqual(VmaxMs9132.Status)));

        // The stop sends nothing, as 1.4.x's dispose; the collection, then the interface, let go.
        mark = fake.Device.Trace.Entries.Count;
        session.Close(screenOff: true, Bound);
        Assert.Equal(["h2 close hid", "h1 close usb if3"], fake.Device.Trace.Entries.Skip(mark).Select(PanelTrace.FormatLine));
    }

    [Theory]
    [InlineData(PanelDeviceStatus.Timeout, 2)] // the status's get: the first status poll
    [InlineData(PanelDeviceStatus.Io, 3)] // the screen switch's first set
    [InlineData(PanelDeviceStatus.Timeout, 4)] // its first get
    public void Vmax_AControlReportThatTimesOutOrFails_EndsTheSession_NothingMoreSent(PanelDeviceStatus failure, int after)
    {
        // The engine's HID seam closes the collection's stream when a feature report times out (and answers Io once it is
        // closed): a session that let it pass would send frames to a screen it can no longer switch on. It ends; the
        // engine opens it again.
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        var driver = new VmaxDriver(devices) { Wait = NoWait, Time = new ManualTime() };
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), new PanelOpenOptions(100), None).Session!;
        var failed = fake.Device.Calls + 15 + after; // the first frame: 15 writes, then the status's set and get, then the switch
        fake.Device.Fail(failed, failure);

        Assert.Equal(failure, Write(session, Pixels(320, 960, 1), 1).Status);
        Assert.Equal(failed, fake.Device.Calls);
    }

    [Fact]
    public void Vmax_AScreenSwitchRefusedWithoutAFailedStream_IsLetPass_AndSentAgainWithTheNextFrame()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        var driver = new VmaxDriver(devices) { Wait = NoWait, Time = new ManualTime() };
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), new PanelOpenOptions(100), None).Session!;
        // The first frame: 15 writes, the status (2 calls), then the switch's first report's set is refused as busy.
        fake.Device.Fail(fake.Device.Calls + 15 + 2 + 1, PanelDeviceStatus.Busy);

        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(320, 960, 1), 1).Status);
        var mark = fake.Device.Trace.Entries.Count;
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(320, 960, 2), 2).Status);
        Assert.Equal(VmaxMs9132.ScreenSwitch(true), fake.Reports(mark));
        mark = fake.Device.Trace.Entries.Count;
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(320, 960, 3), 3).Status);
        Assert.Empty(fake.Reports(mark)); // set: not again, and no status within 500 ms
    }

    [Theory]
    [InlineData("jonsbo")]
    [InlineData("vmax")]
    public void AFrameWhoseWritesRunSlow_ReturnsWithinItsWriteBudget_InsideTheHostsPerCallDeadline(string family)
    {
        // A hosted package's write answers inside the host's 5 s (HostedPanelDriver.WriteDeadline): 1.4.x gave each of a
        // frame's 64 KiB writes its own timeout (5 s on the DS339, 1 s on the VMAX), so a stalling panel could hold a frame
        // for 85 s. The session keeps the whole frame to its write budget: each write is given what is left of it.
        Assert.True(Ms9132Protocol.WriteBudget < new InfoPanel.Panels.Hosted.HostedPanelOptions().WriteDeadline);
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, family == "jonsbo" ? EdidSamples.Ds339 : EdidSamples.Vmax);
        var budget = TimeSpan.FromMilliseconds(400);
        IPanelDriver driver = family == "jonsbo"
            ? new JonsboDriver(devices) { Wait = NoWait, WriteBudget = budget }
            : new VmaxDriver(devices) { Wait = NoWait, WriteBudget = budget };
        var (model, width) = family == "jonsbo" ? ("ds339", 376) : ("vmax46inch", 320);
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity(model, width, 960, null, null), new PanelOpenOptions(100), None).Session!;
        var first = fake.Device.Calls + 1;
        for (var call = first; call < first + 15; call++)
            _ = fake.Device.Delay(call, TimeSpan.FromMilliseconds(150)); // each write takes 150 ms: 15 of them, 2.25 s

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var write = Write(session, Pixels(width, 960, 1), 1);

        Assert.Equal(PanelDeviceStatus.Timeout, write.Status);
        Assert.True(clock.Elapsed < budget + TimeSpan.FromMilliseconds(250), $"the frame took {clock.Elapsed.TotalMilliseconds} ms");
        Assert.All(fake.Device.Timings.Where(t => t.Call >= first), t => Assert.True(t.TimeoutMs <= budget.TotalMilliseconds, $"call {t.Call} was given {t.TimeoutMs} ms"));
    }

    [Fact]
    public void Vmax_AFailedTransfer_EndsTheSession()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        var driver = new VmaxDriver(devices) { Wait = NoWait };
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), new PanelOpenOptions(100), None).Session!;
        fake.Device.Fail(fake.Device.Calls + 1, PanelDeviceStatus.Timeout);

        Assert.Equal(PanelDeviceStatus.Timeout, Write(session, Pixels(320, 960, 1), 1).Status);
        Assert.Single(fake.Writes(fake.Device.Trace.Entries.Count - 1));
    }

    [Fact]
    public void Vmax_AStreamingStartNoCollectionTakes_LetsTheInterfaceGo()
    {
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        fake.Device.Hid.RespondFeature(_ => null);
        var driver = new VmaxDriver(devices) { Wait = NoWait };

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Io), (open.Session, open.Status));
        Assert.Equal(["h1 open usb if3", "h2 open hid hid0", "h2 close hid", "h1 close usb if3"], fake.Handles());
    }

    [Theory]
    [InlineData("jonsbo")]
    [InlineData("vmax")]
    public void AWriteReplyOfNineZeroBytes_IsAcceptedAtStartAndInFrames_As1_4xNeverLookedAtIt(string family)
    {
        // 1.4.x got each report's reply back and looked at none but the reads' (JonsboMs9132Device.cs:272-287,
        // VmaxUsbDevice.cs:258-270): a bridge that answers its writes with nine zero bytes, echoing nothing, starts and
        // runs as it did. Only the length of such a reply is checked.
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, family == "jonsbo" ? EdidSamples.Ds339 : EdidSamples.Vmax);
        fake.WriteRepliesZero = true;
        IPanelDriver driver = family == "jonsbo"
            ? new JonsboDriver(devices) { Wait = NoWait }
            : new VmaxDriver(devices) { Wait = NoWait, Time = new ManualTime() };
        var (model, width) = family == "jonsbo" ? ("ds339", 376) : ("vmax46inch", 320);

        var open = driver.Open(Ms9132Fake.Candidate, new PanelIdentity(model, width, 960, null, null), new PanelOpenOptions(100), None);

        Assert.Equal(PanelDeviceStatus.Ok, open.Status);
        using var session = open.Session!;
        Assert.Equal((width, 960), (session.Capabilities.Width, session.Capabilities.Height));
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(width, 960, 1), 1).Status);
        Assert.Equal(PanelDeviceStatus.Ok, Write(session, Pixels(width, 960, 2), 2).Status); // the DS339's output enable after it
        Assert.Contains(fake.Device.Trace.Entries, e => e.Op == PanelTraceOp.HidGetFeature && e.Data.Length == 9 && e.Data.All(b => b == 0));
        Assert.All(fake.Device.Trace.Entries.Where(e => e.Op is PanelTraceOp.HidSetFeature or PanelTraceOp.HidGetFeature), e => Assert.Equal(PanelDeviceStatus.Ok, e.Status));
    }

    [Fact]
    public void TheFake_AfterAFeatureReportTimesOut_FailsEveryReportOnThatHandle_AsTheEnginesSeamClosesItsStream()
    {
        // The engine's HID seam (HidSharpSystem.Bounded) closes a collection's stream when a feature report on it times
        // out: a set after it fails too, delivering nothing. The next handle on the collection is a new stream.
        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        devices.ListHid(Ms9132Fake.Candidate, new PanelHidFilter(), out var collections);
        var report = Ms9132Protocol.Feature(JonsboMs9132.PanelStatus);
        var reply = new byte[Ms9132Protocol.FeatureLength];
        devices.OpenHid(Ms9132Fake.Candidate, collections[0], 1000, out var hid, None);
        fake.Device.Fail(fake.Device.Calls + 2, PanelDeviceStatus.Timeout); // the get

        Assert.Equal(PanelDeviceStatus.Ok, hid!.SetFeature(report, 1000, None));
        Assert.Equal(PanelDeviceStatus.Timeout, hid.GetFeature(reply, 1000, out _, None));
        Assert.Equal(PanelDeviceStatus.Io, hid.SetFeature(report, 1000, None));
        Assert.Equal(PanelDeviceStatus.Io, hid.GetFeature(reply, 1000, out _, None));
        hid.Dispose();
        devices.OpenHid(Ms9132Fake.Candidate, collections[0], 1000, out var again, None);
        Assert.Equal(PanelDeviceStatus.Ok, again!.SetFeature(report, 1000, None));
        Assert.Equal(PanelDeviceStatus.Ok, again.GetFeature(reply, 1000, out _, None));
        again.Dispose();

        // Attempted: three sets; delivered: the first handle's first and the second handle's.
        Assert.Equal(3, fake.Reports().Count);
        Assert.Equal(2, fake.Delivered().Count);
    }

    [Fact]
    public void Vmax_ScreenOn_IsOnUnlessTheSettingSaysFalse()
    {
        Assert.True(VmaxDriver.ScreenOn(default));
        Assert.True(VmaxDriver.ScreenOn(Json("{}")));
        Assert.True(VmaxDriver.ScreenOn(Json("""{"screenSwitch": true}""")));
        Assert.False(VmaxDriver.ScreenOn(Json("""{"screenSwitch": false}""")));
    }

    /// <summary>A clock set by the test.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;
    }
}
