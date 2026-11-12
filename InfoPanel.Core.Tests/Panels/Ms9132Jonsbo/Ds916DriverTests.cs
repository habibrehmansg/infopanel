using System.Text;
using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The Jonsbo DS916 on the kit's fake serial port, against 1.4.x's call order (JonsboSerialDevice.cs,
/// JonsboPanelDeviceTask.cs:28-208): the probe's handshake (asked again after 1 s of silence, "not mine" only after both
/// went unanswered in their whole waits, inside its budget), the start's open and handshake at the size the panel
/// reports, a frame as one raw JPEG, the stop that sends nothing; and the conformance suite.
/// </summary>
public sealed class Ds916DriverTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly PanelCandidate Ds916 = new(0x33C3, 0xF101, 1, 6, "1-2");
    private static readonly byte[] Reply = Encoding.ASCII.GetBytes("VMAXB160462*1920S261301227");
    private static readonly string Handshake = Convert.ToHexStringLower(V1JonsboSerialDevice.HandshakeCommand);

    private static void NoWait(TimeSpan _, CancellationToken stop) => stop.ThrowIfCancellationRequested();

    private static (JonsboDriver Driver, FakePanelDevice Device, List<TimeSpan> Waits) Plug(byte[]? reply = null, TimeProvider? time = null, TimeSpan? writeBudget = null)
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Ds916, PanelTransport.Serial);
        device.Serial.PortName = "/dev/cu.usbmodemDS9161";
        if (reply != null)
            device.Serial.Respond(_ => reply);
        var waits = new List<TimeSpan>();
        var driver = new JonsboDriver(devices)
        {
            Wait = (span, stop) =>
            {
                waits.Add(span);
                stop.ThrowIfCancellationRequested();
            },
            Time = time ?? TimeProvider.System,
            WriteBudget = writeBudget ?? Ms9132Protocol.WriteBudget,
        };
        return (driver, device, waits);
    }

    private static List<string> Lines(FakePanelDevice device, int from = 0) => [.. device.Trace.Entries.Skip(from).Select(PanelTrace.FormatLine)];

    [Fact]
    public void TheProbe_AsksTheHandshake_AndAnswersTheDs916_AtTheSizeAndSerialItReports()
    {
        var (driver, device, _) = Plug(Reply);

        var probe = driver.Probe(Ds916, None);

        Assert.Equal((true, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Equal(new PanelIdentity("ds916", 462, 1920, "S261301227", null), probe.Identity);
        // JonsboSerialDevice.cs:53-63 (9600, DTR and RTS, the input dropped), :84-109 (dropped again, the handshake, the reply).
        Assert.Equal([
            "h1 open serial 9600 dtr rts",
            "h1 serial discard",
            "h1 serial discard",
            $"h1 serial> {Handshake}",
            $"h1 serial< {Convert.ToHexStringLower(Reply)}",
            "h1 serial< !timeout",
            "h1 close serial",
        ], Lines(device));
    }

    [Fact]
    public void AnotherSizeItReports_IsTheDs916AtThatSize_As1_4TrustedIt()
    {
        var (driver, _, _) = Plug(Encoding.ASCII.GetBytes("VMAXB201920*0480S1\r\n"));
        Assert.Equal(new PanelIdentity("ds916", 1920, 480, "S1", null), driver.Probe(Ds916, None).Identity);
    }

    [Fact]
    public void ASilentFirstHandshake_IsAskedAgainAfter1s_As1_4()
    {
        var (driver, device, waits) = Plug();
        var asked = 0;
        device.Serial.Respond(_ => ++asked == 1 ? null : Reply);

        var probe = driver.Probe(Ds916, None);

        Assert.Equal(("ds916", PanelDeviceStatus.Ok), (probe.Identity?.Model, probe.Status));
        Assert.Equal([TimeSpan.FromSeconds(1)], waits); // JonsboPanelDeviceTask.cs:69-73
        Assert.Equal(2, device.Trace.Entries.Count(e => e.Op == PanelTraceOp.SerialWrite));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeviceThatAnswersNeitherHandshake_IsNotMine_AfterBothWholeWaits(bool garbage)
    {
        var (driver, device, _) = Plug(garbage ? Encoding.ASCII.GetBytes("hello") : null);

        var probe = driver.Probe(Ds916, None);

        Assert.Equal((false, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Equal([Handshake, Handshake], device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialWrite).Select(e => Convert.ToHexStringLower(e.Data)));
        Assert.Contains(device.Timings, t => t.TimeoutMs == Ds916Session.ProbeReplyMs);
        Assert.Equal("h1 close serial", Lines(device)[^1]);
    }

    [Fact]
    public void AReplyWaitCutShortByTheBudget_AnswersTimeout_NeverNotMine()
    {
        // The probe's clock passes most of its budget before the first reply's wait: the wait is cut, and the probe
        // answers Timeout (asked again later), not "not mine".
        var time = new JumpingTime { JumpAt = 4, By = Ds916Session.ProbeBudget - TimeSpan.FromMilliseconds(200) };
        var (driver, device, _) = Plug(time: time);

        var probe = driver.Probe(Ds916, None);

        Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
        Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.SerialWrite);
        Assert.True(device.Timings.Max(t => t.TimeoutMs) <= 5000);
        Assert.Equal("h1 close serial", Lines(device)[^1]);
    }

    [Fact]
    public void AReplyWhoseGatheringIsCutByTheBudget_AnswersTimeout_NeverAPartIdentity()
    {
        // The reply's first part comes as the probe's time is nearly spent: the rest of its serial would come within the
        // 300 ms gathering window, but after the budget. What came reads (serial "S26"), yet it isn't the reply: the probe
        // answers Timeout (asked again later), never the DS916 with a cut serial.
        var time = new AfterFirstPartTime { By = Ds916Session.ProbeBudget - TimeSpan.FromMilliseconds(100) };
        var (driver, device, _) = Plug(Encoding.ASCII.GetBytes("VMAXB160462*1920S26"), time);
        time.Device = device;

        var probe = driver.Probe(Ds916, None);

        Assert.Equal((false, PanelDeviceStatus.Timeout, (PanelIdentity?)null), (probe.Mine, probe.Status, probe.Identity));
        Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.SerialWrite);
        Assert.Equal("h1 close serial", Lines(device)[^1]);
    }

    [Fact]
    public void AWholeReplyGatheredJustInsideTheBudget_IsTheDs916()
    {
        // The cut-reply rule's other side: the reply's first part comes as the probe's time is nearly spent, and the rest,
        // to its 64 bytes, comes within what is left. The gathering ended at the 64 bytes 1.4 kept, not by the budget,
        // so the reply is whole: the DS916, with its whole serial.
        var time = new AfterFirstPartTime { By = Ds916Session.ProbeBudget - TimeSpan.FromMilliseconds(100) };
        var (driver, device, _) = Plug(Reply, time);
        time.Device = device;
        byte[] rest = [(byte)'\r', (byte)'\n', .. new byte[Ds916Protocol.ReplyCap - Reply.Length - 2]];
        var arrived = false;
        device.Trace.Added += e =>
        {
            if (!arrived && e.Op == PanelTraceOp.SerialRead && e.Data.Length > 0)
            {
                arrived = true;
                device.Serial.Arrive(rest);
            }
        };

        var probe = driver.Probe(Ds916, None);

        Assert.Equal((true, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Equal(new PanelIdentity("ds916", 462, 1920, "S261301227", null), probe.Identity);
        var reads = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialRead).ToList();
        Assert.Equal([Reply.Length, rest.Length], reads.Select(e => e.Data.Length)); // the 64 bytes, then no read more
        Assert.InRange(device.Timings.Last(t => t.TimeoutMs < Ds916Protocol.GatherMs).TimeoutMs, 1, 100); // the gathering, cut to what was left
        Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.SerialWrite);
    }

    [Fact]
    public void TheProbesBudget_IsInsideTheEngines_AndHoldsBothWaitsAndTheGap()
    {
        Assert.True(Ds916Session.ProbeBudget < InfoPanel.Panels.PanelManager.ProbeDeadline);
        Assert.True(TimeSpan.FromMilliseconds(2 * (Ds916Session.ProbeReplyMs + Ds916Protocol.GatherMs)) + Ds916Protocol.RetryGap < Ds916Session.ProbeBudget);
    }

    [Fact]
    public void ACancelledProbe_Throws_LetsThePortGo_AndSendsNothingMore()
    {
        var (driver, device, _) = Plug(Reply);
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource();
        var held = device.Hold(5, release.Task); // the handshake's first-byte read (1 open, 2 and 3 discards, 4 write, 5 read)
        var probing = Task.Run(() => driver.Probe(Ds916, cancel.Token));
        held.Wait(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => probing.GetAwaiter().GetResult());
        release.SetResult();
        Assert.Equal("h1 close serial", Lines(device)[^1]);
        Assert.Single(device.Trace.Entries, e => e.Op == PanelTraceOp.SerialWrite);
    }

    [Fact]
    public void ABusyPort_AnswersBusy_AskedAgainLater()
    {
        var (driver, device, _) = Plug(Reply);
        device.Serial.OpenStatus = PanelDeviceStatus.Busy;
        Assert.Equal((false, PanelDeviceStatus.Busy), driver.Probe(Ds916, None) is var p ? (p.Mine, p.Status) : default);
    }

    [Fact]
    public void TheStart_TraceAFrameAndTheStop_Are1_4s()
    {
        var (driver, device, _) = Plug(Reply);
        var oracle = new V1JonsboSerialDevice();
        var identity = oracle.GetIdentity(Reply)!;

        using var session = driver.Open(Ds916, new PanelIdentity("ds916", 462, 1920, "S261301227", null), new PanelOpenOptions(100), None).Session!;

        var caps = session.Capabilities;
        Assert.Equal((identity.Width, identity.Height), (caps.Width, caps.Height));
        Assert.Equal((PanelFrameFormat.Jpeg, 60, 262_144, 80, 15, 30, TimeSpan.FromSeconds(1), true),
            (Assert.Single(caps.Formats), caps.MaxFps, caps.MaxFrameBytes, caps.JpegQuality, caps.JpegQualityStep, caps.MinJpegQuality, caps.KeepAliveEvery, caps.DimInSoftware));
        Assert.Null(caps.Brightness);
        Assert.Equal(Convert.ToHexStringLower(oracle.Writes[0]), Handshake);

        // A frame: the JPEG's bytes, nothing around them (JonsboSerialDevice.cs:142-146).
        var mark = device.Trace.Entries.Count;
        var jpeg = ConformanceFrames.Of(PanelFrameFormat.Jpeg, 462, 1920);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(jpeg, PanelFrameFormat.Jpeg, 1, false), None).Status);
        // The stop: no command (JonsboSerialDevice.cs:156-157), the port let go.
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        Assert.Equal([$"h1 serial> {Convert.ToHexStringLower(jpeg)}", "h1 close serial"], Lines(device, mark));
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Fact]
    public void AStartTheHandshakeGoesUnanswered_Fails_AndLetsThePortGo()
    {
        var (driver, device, waits) = Plug();

        var open = driver.Open(Ds916, new PanelIdentity("ds916", 462, 1920, null, null), new PanelOpenOptions(100), None);

        Assert.Equal((null, PanelDeviceStatus.Timeout), (open.Session, open.Status));
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);
        Assert.Contains(device.Timings, t => t.TimeoutMs == Ds916Protocol.ReadTimeoutMs); // the start waits 1.4.x's 2 s
        Assert.Equal("h1 close serial", Lines(device)[^1]);
    }

    [Fact]
    public void AFrameWriteIsBoundedByTheWriteBudget_AndAnUnplugEndsTheSessionWithNothingMoreSent()
    {
        var (driver, device, _) = Plug(Reply, writeBudget: TimeSpan.FromMilliseconds(300));
        Assert.True(Ms9132Protocol.WriteBudget.TotalMilliseconds < Ds916Protocol.WriteTimeoutMs, "1.4.x's 5 s write is cut to the budget");

        using var session = driver.Open(Ds916, new PanelIdentity("ds916", 462, 1920, null, null), new PanelOpenOptions(100), None).Session!;
        var write = device.Calls + 1;
        _ = device.Stall(write);
        Assert.Equal(PanelDeviceStatus.Timeout, session.WriteFrame(new PanelFrameView(new byte[100], PanelFrameFormat.Jpeg, 1, false), None).Status);
        Assert.Equal(300, device.Timings.Single(t => t.Call == write).TimeoutMs);

        device.UnplugAt(device.Calls + 1);
        Assert.Equal(PanelDeviceStatus.Gone, session.WriteFrame(new PanelFrameView(new byte[100], PanelFrameFormat.Jpeg, 2, false), None).Status);
        var mark = device.Trace.Entries.Count;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        Assert.Equal(["h1 close serial"], Lines(device, mark));
    }

    [Fact]
    public void TheJonsboDriver_PassesTheConformanceSuite_OnADs916()
    {
        Assert.Empty(Conformance.Check(() =>
        {
            var devices = new FakePanelDevices();
            var device = devices.Add(Ds916, PanelTransport.Serial);
            device.Serial.Respond(write => write.Data.AsSpan().SequenceEqual(V1JonsboSerialDevice.HandshakeCommand) ? Reply : null);
            return new ConformanceSetup(new JonsboDriver(devices) { Wait = NoWait }, Ds916, device)
            {
                ProbeWrite = Conformance.ProbeWrites(V1JonsboSerialDevice.HandshakeCommand),
            };
        }));
    }

    /// <summary>A clock that stands still, but jumps by <see cref="By"/> the first time it is read after a read got bytes.</summary>
    private sealed class AfterFirstPartTime : TimeProvider
    {
        private long _now;
        private bool _jumped;

        public FakePanelDevice? Device { get; set; }

        public TimeSpan By { get; init; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (!_jumped && Device != null && Device.Trace.Entries.Any(e => e.Op == PanelTraceOp.SerialRead && e.Data.Length > 0))
            {
                _jumped = true;
                _now += By.Ticks;
            }
            return _now;
        }
    }

    /// <summary>A clock that jumps by <see cref="By"/> at its <see cref="JumpAt"/>th read.</summary>
    private sealed class JumpingTime : TimeProvider
    {
        private long _now;
        private int _reads;

        public int JumpAt { get; init; }

        public TimeSpan By { get; init; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (++_reads == JumpAt)
                _now += By.Ticks;
            return _now;
        }
    }
}
