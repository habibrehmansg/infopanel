using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The MS9132's detection vectors: the engine asks the package's families in its manifest's order, <c>jonsbo</c> then
/// <c>vmax</c>; each probe reads the EDID (1.4.x's reads, JonsboMs9132Device.cs:76,211-229) and answers by its size: a
/// DS339's EDID is jonsbo's DS339, a VMAX's is passed on by jonsbo and taken by vmax, any other is jonsbo's unknown model
/// ("Try it as…"). And the probe's rules: it only asks, it answers "not mine" only after every collection went
/// unanswered for its whole wait, a call cut short by its budget answers a status, a cancel lets the device go and sends
/// nothing more.
/// </summary>
public sealed class Ms9132DetectionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static void NoWait(TimeSpan _, CancellationToken stop) => stop.ThrowIfCancellationRequested();

    private static (JonsboDriver Jonsbo, VmaxDriver Vmax, Ms9132Devices Devices) Drivers(TimeProvider? time = null, Action<TimeSpan, CancellationToken>? wait = null)
    {
        var devices = new Ms9132Devices();
        return (new JonsboDriver(devices) { Wait = wait ?? NoWait, Time = time ?? TimeProvider.System },
            new VmaxDriver(devices) { Wait = wait ?? NoWait, Time = time ?? TimeProvider.System }, devices);
    }

    /// <summary>The engine's order: each family's probe until one says "mine".</summary>
    private static (string? Family, PanelProbe Probe) Identify(JonsboDriver jonsbo, VmaxDriver vmax)
    {
        foreach (IPanelDriver driver in new IPanelDriver[] { jonsbo, vmax })
        {
            var probe = driver.Probe(Ms9132Fake.Candidate, None);
            if (probe.Status != PanelDeviceStatus.Ok || probe.Mine)
                return (probe.Mine ? driver.Family : null, probe);
        }
        return (null, new PanelProbe(false, null, PanelDeviceStatus.Ok));
    }

    [Fact]
    public void ADs339sEdid_IsJonsbosDs339_ReadByTheEdidReadsAlone()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);

        var (family, probe) = Identify(jonsbo, vmax);

        Assert.Equal("jonsbo", family);
        Assert.Equal(new PanelIdentity("ds339", 376, 960, null, null), probe.Identity);
        Assert.StartsWith("EDID 376x960 at 29.16 MHz, panel name VMAXB01; block 0: 00FFFFFFFFFFFF00", probe.Message, StringComparison.Ordinal);
        // Only the 32 reads, each set and got back once (JonsboMs9132Device.cs:76); the collection let go.
        Assert.Equal(V1JonsboMs9132Device.StartupReports[24..], fake.Reports());
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
        Assert.All(fake.Device.Trace.Entries.Where(e => e.Op == PanelTraceOp.HidGetFeature), e => Assert.Equal(PanelDeviceStatus.Ok, e.Status));
    }

    [Fact]
    public void AVmaxsEdid_IsPassedOnByJonsbo_AndTakenByVmax()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);

        var passed = jonsbo.Probe(Ms9132Fake.Candidate, None);
        Assert.Equal((false, PanelDeviceStatus.Ok), (passed.Mine, passed.Status));
        Assert.Contains("vmax", passed.Message, StringComparison.Ordinal);
        var (family, probe) = Identify(jonsbo, vmax);

        Assert.Equal("vmax", family);
        Assert.Equal(new PanelIdentity("vmax46inch", 320, 960, null, null), probe.Identity);
        Assert.Equal(["h1 open hid hid0", "h1 close hid", "h2 open hid hid0", "h2 close hid", "h3 open hid hid0", "h3 close hid"], fake.Handles());
    }

    [Fact]
    public void AnEdidOfAnotherSize_IsJonsbosUnknownModel_NamedByItsSize_AndVmaxDoesntTakeIt()
    {
        var (jonsbo, vmax, devices) = Drivers();
        Ms9132Fake.Add(devices, EdidSamples.Unknown);

        var (family, probe) = Identify(jonsbo, vmax);
        Assert.Equal("jonsbo", family);
        Assert.Equal(new PanelIdentity("edid-480x480", 480, 480, null, null), probe.Identity);
        Assert.Null(Ms9132JonsboManifestModel(probe.Identity!.Model));
        Assert.Contains("PANEL480", probe.Message, StringComparison.Ordinal);

        var notVmax = vmax.Probe(Ms9132Fake.Candidate, None);
        Assert.Equal((false, PanelDeviceStatus.Ok), (notVmax.Mine, notVmax.Status));
    }

    private static PanelManifestModel? Ms9132JonsboManifestModel(string id) => PanelManifest.Read(typeof(JonsboDriver).Assembly).Family("jonsbo").Model(id);

    [Fact]
    public void AnEdidThatOnlyReadsAfterThePowerUp_IsReadBy1_4sStartupBlock_ThenThePanelIsPoweredOff()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;

        var (family, probe) = Identify(jonsbo, vmax);

        Assert.Equal(("jonsbo", "ds339"), (family, probe.Identity!.Model));
        // The reads alone (zeros: no header), then 1.4.x's whole startup block, then its power-off (JonsboMs9132Device.cs:50-77,361-365).
        var oracle = new V1JonsboMs9132Device();
        oracle.RunStartupBlock();
        oracle.PowerOff();
        Assert.Equal([.. V1JonsboMs9132Device.StartupReports[24..], .. oracle.Reports.Select(r => r[1..])], fake.Reports());
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
    }

    [Fact]
    public void AnEdidThatNeverReads_IsJonsbosUnknownModel_AtTheDs339sSize()
    {
        var (jonsbo, vmax, devices) = Drivers();
        Ms9132Fake.Add(devices, edid: null); // every read answered, with zeros

        var (family, probe) = Identify(jonsbo, vmax);

        Assert.Equal("jonsbo", family);
        Assert.Equal(new PanelIdentity("edid-unread", 376, 960, null, null), probe.Identity);
    }

    [Fact]
    public void ACollectionThatRefusesEveryReport_IsLeft_AndTheNextOneAnswers()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        var first = fake.Device.Hid;
        first.RespondFeature(_ => null); // refuses: Io
        var second = fake.Device.AddHid();
        second.MaxFeatureReportLength = 9;
        second.RespondFeature(_ =>
        {
            var set = fake.Device.Trace.Entries.Last(e => e.Op == PanelTraceOp.HidSetFeature).Data;
            return EdidSamples.Reply(set, EdidSamples.Ds339);
        });

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal(("ds339", PanelDeviceStatus.Ok), (probe.Identity?.Model, probe.Status));
        // hid0: its first read refused, so nothing more is sent to it; hid1 answers the reads.
        Assert.Equal(["h1 open hid hid0", "h1 close hid", "h2 open hid hid1", "h2 close hid"], fake.Handles());
    }

    [Fact]
    public void ADeviceWhoseCollectionsAllRefuse_IsNotMine_AndIsSentNothingButARead()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.RespondFeature(_ => null);

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);
        Assert.Equal((false, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        // A device that didn't answer a read gets no command that changes it: no power-up, no power-off.
        Assert.Equal([V1JonsboMs9132Device.StartupReports[24]], fake.Reports());
        Assert.Equal((false, PanelDeviceStatus.Ok), (vmax.Probe(Ms9132Fake.Candidate, None) is var v ? (v.Mine, v.Status) : default));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("anotherReportsEcho")]
    [InlineData("zeros")]
    public void AReplyThatIsntAnMs9132sAnswer_IsUnanswered_AndTheCollectionGetsNothingMore(string kind)
    {
        // An MS9132's reply is the whole feature report, echoing the opcode and address it answers (JonsboMs9132Device.cs:285):
        // a short one, or one that answers another report, isn't the bridge's answer, so no startup write follows and no
        // unknown-model identity comes of it.
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.RespondFeature(_ => kind switch
        {
            "short" => [0x00, 0xB5, 0xC0, 0x00, 0x00],
            "anotherReportsEcho" => [0x00, 0xB5, 0x00, 0x32, 0x01, 0x00, 0x00, 0x00, 0x00],
            _ => new byte[9],
        });

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal((false, PanelDeviceStatus.Ok, (PanelIdentity?)null), (probe.Mine, probe.Status, probe.Identity));
        Assert.Equal([V1JonsboMs9132Device.StartupReports[24]], fake.Reports()); // the first read alone
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
        Assert.Equal((false, PanelDeviceStatus.Ok), vmax.Probe(Ms9132Fake.Candidate, None) is var v ? (v.Mine, v.Status) : default);
    }

    [Theory]
    [InlineData(PanelDeviceStatus.Io)]
    [InlineData(PanelDeviceStatus.Timeout)]
    public void AStartupBlockThatFails_AfterTheBridgeAnswered_IsTransient_AndALaterProbeTakesTheDs339(PanelDeviceStatus failure)
    {
        // The EDID reads only after the power-up: the reads alone answer with zeros, so the startup block follows, and one
        // of its reports after the power-up fails (Io, or no reply in its whole wait). The bridge answered: the probe
        // answers that failure, asked again later, never "not mine"; the panel, powered up, is sent the power-off. After
        // a refusal (Io) it is delivered; after a timeout the engine's seam has closed the collection's stream, so it is
        // attempted and fails there, delivering nothing.
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        // Calls: 1 the open, 2 to 65 the 32 reads alone, 66 on the startup block (report n's set 66 + 2n, its get one
        // after); the power-up is report 9, the get of report 29 fails.
        fake.Device.Fail(66 + 2 * 29 + 1, failure);

        var first = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal((false, failure), (first.Mine, first.Status));
        Assert.Equal([.. JonsboMs9132.Startup.Take(30), JonsboMs9132.PowerOff.ToArray()], fake.Reports().Skip(32)); // attempted
        Assert.Equal(failure == PanelDeviceStatus.Timeout
            ? [.. JonsboMs9132.Startup.Take(30)]
            : [.. JonsboMs9132.Startup.Take(30), JonsboMs9132.PowerOff.ToArray()], fake.Delivered().Skip(32));
        Assert.Equal(failure == PanelDeviceStatus.Timeout ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok,
            fake.Device.Trace.Entries.Last(e => e.Op == PanelTraceOp.HidSetFeature).Status);
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());

        var (family, probe) = Identify(jonsbo, vmax);
        Assert.Equal(("jonsbo", "ds339"), (family, probe.Identity?.Model));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void TheStartupBlock_StopsWhereThePowerOffsReserveBegins_AndThePowerOffIsSentWithinIt(int over, bool whole)
    {
        // The clock passes the probe's time once the power-up is set, leaving the power-off's reserve and `over` ms: with
        // 1 ms the block's calls are each given 1 ms (the fake answers at once) and it ends whole; with none it stops at
        // the power-up's get, and the power-off is sent either way, within its reserve.
        var reserve = Ms9132Probe.PowerOffReserveMs;
        var time = new PoweredUpTime { By = Ms9132Probe.Budget - TimeSpan.FromMilliseconds(reserve + over) };
        var (jonsbo, _, devices) = Drivers(time);
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        time.Fake = fake;

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal(whole ? (true, PanelDeviceStatus.Ok) : (false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
        Assert.Equal([0xA6, 0x07, 0, 0, 0, 0, 0, 0], fake.Reports()[^1]);
        var timings = fake.Device.Timings;
        var powerOff = timings[^2]; // its set, then its get
        Assert.InRange(powerOff.TimeoutMs, 1, reserve + over);
        Assert.All(timings.Where(t => t.Call > time.JumpedAt && t.Call < powerOff.Call), t => Assert.InRange(t.TimeoutMs, 1, over));
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
    }

    [Theory]
    [InlineData(990, false)]
    [InlineData(100, true)]
    public void ThePowerOffAfterTheStartupBlock_KeepsItsWholePause_ElseAnswersTimeout(int setMs, bool whole)
    {
        // A clock that charges the pauses waited and what the power-off's calls take: the startup block ends with the
        // power-off's reserve left, then its set takes `setMs` and its get 100 ms. The get is given what is left after the
        // pause; when the whole pause no longer fits, the probe answers Timeout (asked again later), never a cut pause.
        var time = new ChargedTime();
        var waits = new List<int>();
        var poweringOff = false;
        var (jonsbo, _, devices) = Drivers(time, (span, stop) =>
        {
            if (poweringOff)
                waits.Add((int)span.TotalMilliseconds);
            time.Advance(span);
            stop.ThrowIfCancellationRequested();
        });
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        var reserve = Ms9132Probe.PowerOffReserveMs;
        var block = Ms9132Protocol.EdidReads.Count + JonsboMs9132.Startup.Count;
        var sets = 0;
        fake.Device.Trace.Added += e =>
        {
            if (e.Op == PanelTraceOp.HidSetFeature && ++sets == block + 1)
            {
                poweringOff = true; // the power-off's set
                time.Advance(TimeSpan.FromMilliseconds(setMs));
            }
            else if (e.Op == PanelTraceOp.HidGetFeature && poweringOff)
                time.Advance(TimeSpan.FromMilliseconds(100));
            else if (e.Op == PanelTraceOp.HidGetFeature && sets == block)
                time.Set(Ms9132Probe.Budget - TimeSpan.FromMilliseconds(reserve + JonsboMs9132.DelayMs(JonsboMs9132.Startup[^1]))); // the block's last get
        };

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal([0xA6, 0x07, 0, 0, 0, 0, 0, 0], fake.Reports()[^1]);
        var get = fake.Device.Timings[^1]; // the power-off's get: what is left after its set, less its pause
        if (whole)
        {
            Assert.Equal((true, PanelDeviceStatus.Ok, "ds339"), (probe.Mine, probe.Status, probe.Identity?.Model));
            Assert.Equal(reserve - setMs - 105, get.TimeoutMs);
            Assert.Equal([105], waits);
        }
        else
        {
            Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
            Assert.Equal(reserve - setMs - 105, get.TimeoutMs);
            Assert.Empty(waits); // no pause cut short
        }
    }

    [Fact]
    public void AWriteReplyOfNineZeroBytes_InTheStartupBlock_IsAccepted_AsTheReadsEchoThem()
    {
        // The identity reads keep their strict match (the EDID reads alone, and the block's own status and EDID reads);
        // the block's writes are answered with nine zero bytes, echoing nothing, which 1.4.x never looked at.
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        fake.WriteRepliesZero = true;

        var (family, probe) = Identify(jonsbo, vmax);

        Assert.Equal(("jonsbo", "ds339", PanelDeviceStatus.Ok), (family, probe.Identity?.Model, probe.Status));
        Assert.Equal([.. JonsboMs9132.Startup, JonsboMs9132.PowerOff.ToArray()], fake.Delivered().Skip(32));
        Assert.Contains(fake.Device.Trace.Entries, e => e.Op == PanelTraceOp.HidGetFeature && e.Data.Length == 9 && e.Data.All(b => b == 0));
    }

    [Fact]
    public void APowerOffThatFails_AnswersATransientStatus_NeverMine()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        // Calls: 1 the open, 2 to 65 the reads alone, 66 to 177 the startup block, 178 the power-off's set, 179 its get.
        fake.Device.Fail(179, PanelDeviceStatus.Io);

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal((false, PanelDeviceStatus.Io), (probe.Mine, probe.Status));
        Assert.Equal([0xA6, 0x07, 0, 0, 0, 0, 0, 0], fake.Reports()[^1]);
    }

    [Fact]
    public void ADeviceWithNoHidCollection_IsNotMine()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.Present = false;

        Assert.Equal((false, PanelDeviceStatus.Ok), jonsbo.Probe(Ms9132Fake.Candidate, None) is var j ? (j.Mine, j.Status) : default);
        Assert.Equal((false, PanelDeviceStatus.Ok), vmax.Probe(Ms9132Fake.Candidate, None) is var v ? (v.Mine, v.Status) : default);
        Assert.Empty(fake.Device.Trace.Entries);
    }

    [Fact]
    public void AReplyCutShortByTheBudget_AnswersTimeout_NeverNotMine()
    {
        // The probe's clock passes most of its budget at the open: the first read is given what is left, and when that
        // runs out with no reply the probe answers Timeout (asked again later), not "not mine".
        var time = new SteppedTime();
        var (jonsbo, _, devices) = Drivers(time);
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.RespondFeature(_ => null);
        _ = fake.Device.Stall(2); // the first read's set: no answer within its timeout
        time.StepOnRead = Ms9132Probe.Budget - TimeSpan.FromMilliseconds(300);

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
        var timing = fake.Device.Timings.Single(t => t.Call == 2);
        Assert.True(timing.TimeoutMs < Ms9132Protocol.HidTimeoutMs, $"the cut-short call was given {timing.TimeoutMs} ms");
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
    }

    [Fact]
    public void AReplyUnansweredForItsWholeWait_IsATryThatWentUnanswered()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.RespondFeature(_ => null);
        _ = fake.Device.Stall(2); // the first read's set times out after its whole 1000 ms

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);

        Assert.Equal((false, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Equal(Ms9132Protocol.HidTimeoutMs, fake.Device.Timings.Single(t => t.Call == 2).TimeoutMs);
    }

    [Fact]
    public void TheWholeProbe_KeepsInsideItsBudget_InsideTheEngines()
    {
        Assert.True(Ms9132Probe.Budget < InfoPanel.Panels.PanelManager.ProbeDeadline);
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.EdidAfterPowerOn = true;
        jonsbo.Probe(Ms9132Fake.Candidate, None);
        Assert.All(fake.Device.Timings, t => Assert.InRange(t.TimeoutMs, 1, Ms9132Protocol.HidTimeoutMs));
    }

    [Fact]
    public void ACancelledProbe_Throws_LetsTheCollectionGo_AndSendsNothingMore()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource();
        var held = fake.Device.Hold(4, release.Task); // the second read's set
        var probing = Task.Run(() => jonsbo.Probe(Ms9132Fake.Candidate, cancel.Token));
        held.Wait(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => probing.GetAwaiter().GetResult());
        release.SetResult();
        var calls = fake.Device.Calls;
        Thread.Sleep(50);
        Assert.Equal(calls, fake.Device.Calls);
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
        Assert.Single(fake.Reports()); // the first read; the second's, held, never went: nothing after
    }

    [Fact]
    public void AnUnplugDuringTheProbe_AnswersGone()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.UnplugAt(5);

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);
        Assert.Equal((false, PanelDeviceStatus.Gone), (probe.Mine, probe.Status));
        Assert.Equal(["h1 open hid hid0", "h1 close hid"], fake.Handles());
    }

    [Fact]
    public void ABusyCollection_AnswersBusy_AskedAgainLater()
    {
        var (jonsbo, _, devices) = Drivers();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
        fake.Device.Hid.OpenStatus = PanelDeviceStatus.Busy;

        var probe = jonsbo.Probe(Ms9132Fake.Candidate, None);
        Assert.Equal((false, PanelDeviceStatus.Busy), (probe.Mine, probe.Status));
    }

    [Fact]
    public void AnotherUsbId_IsNeitherFamilysPanel_AndIsntAsked()
    {
        var (jonsbo, vmax, devices) = Drivers();
        var other = new PanelCandidate(0x345F, 0x9120, 1, 3, "1-3");
        devices.Add(other, PanelTransport.Hid);
        Assert.Equal((false, PanelDeviceStatus.Ok), jonsbo.Probe(other, None) is var j ? (j.Mine, j.Status) : default);
        Assert.Equal((false, PanelDeviceStatus.Ok), vmax.Probe(other, None) is var v ? (v.Mine, v.Status) : default);
        Assert.Empty(devices[other].Trace.Entries);
    }

    /// <summary>A clock that stands still, but jumps by <see cref="By"/> the first time it is read after the power-up was set.</summary>
    private sealed class PoweredUpTime : TimeProvider
    {
        private long _now;

        public Ms9132Fake? Fake { get; set; }

        public TimeSpan By { get; init; }

        /// <summary>The calls made when it jumped (none yet: int.MaxValue).</summary>
        public int JumpedAt { get; private set; } = int.MaxValue;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (JumpedAt == int.MaxValue && Fake != null && Fake.Reports().Any(r => r.AsSpan(0, 4).SequenceEqual((byte[])[0xA6, 0x07, 0x01, 0x02])))
            {
                JumpedAt = Fake.Device.Calls;
                _now += By.Ticks;
            }
            return _now;
        }
    }

    /// <summary>A clock that moves only when the test moves it: by what a call or pause takes, or to a time.</summary>
    private sealed class ChargedTime : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);

        public void Set(TimeSpan at) => Interlocked.Exchange(ref _now, at.Ticks);
    }

    /// <summary>A clock that jumps by <see cref="StepOnRead"/> once it has been read twice (the probe's start, then its first call's budget).</summary>
    private sealed class SteppedTime : TimeProvider
    {
        private long _now;
        private int _reads;

        public TimeSpan StepOnRead { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (++_reads == 3)
                _now += StepOnRead.Ticks;
            return _now;
        }
    }
}
