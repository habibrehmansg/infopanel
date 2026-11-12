using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using InfoPanel.Core.Tests.Panels.Jl.V1Oracle;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Import;
using InfoPanel.Panels.Jl;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Jl;

/// <summary>
/// The driver over the kit's fake devices: the probe and its detection vectors (the model by the
/// resolution the device info gives; an unknown one the unknown model), the start, a frame, an idle keep-alive, the
/// brightness and the stop as traces built from 1.4.x's own calls (the oracle, <c>V1Oracle/JlSerialDevice.cs</c>, fed the
/// same replies), in the order 1.4.x's task made them (each step's line cited); the conformance suite; the 1.x import of
/// the engine's fixture.
/// </summary>
public sealed class JlDriverTests
{
    private static readonly PanelCandidate Panel = new(0x33C3, 0x7788, 1, 6, "1-4");

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    private static string InfoJson(int width, int height, int status = 200, string uid = "A1B2C3", string version = "Ver1.0") =>
        $"{{\"status\":{status},\"uid\":\"{uid}\",\"model\":\"TXW818-ST7701S-4.0inch\",\"version\":\"{version}\",\"width\":{width},\"height\":{height},\"angle\":0,\"region\":\"sulcs\"}}";

    /// <summary>The panel's reply to the device info query, framed by 1.4.x's own rule.</summary>
    private static byte[] Reply(string json) => V1JlSerialDevice.BuildFrame(V1JlSerialDevice.CMD_GET_DEVICE_INFO, Encoding.UTF8.GetBytes(json));

    private static bool IsQuery(PanelTraceEntry seen) => seen.Data.AsSpan().SequenceEqual(JlProtocol.DeviceInfoQuery);

    /// <summary>A JL panel on its serial port; it answers the device info query with <paramref name="json"/> (null: never).</summary>
    private static (FakePanelDevices Devices, FakePanelDevice Device) Plug(string? json)
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Panel, PanelTransport.Serial);
        if (json != null)
            device.Serial.Respond(seen => IsQuery(seen) ? Reply(json) : null);
        return (devices, device);
    }

    /// <summary>The driver with no real pauses (1.4.x's two 200 ms waits at the open).</summary>
    private static JlDriver Driver(IPanelDevices devices) => new(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };

    private static IPanelSession Start(JlDriver driver, PanelIdentity identity, int brightness = 100, JsonElement settings = default)
    {
        var open = driver.Open(Panel, identity, new PanelOpenOptions(brightness, FamilySettings: settings), TestContext.Current.CancellationToken);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        return open.Session!;
    }

    private static readonly PanelIdentity ChillArc = new("chillarc360", 480, 960, "A1B2C3", "Ver1.0");

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> Lines(FakePanelDevice device) => device.Trace.Entries.Select(PanelTrace.FormatLine).ToList();

    /// <summary>What 1.4.x wrote from its open to its device info (JlSerialDevice.cs:82-86, :192), fed <paramref name="reply"/>.</summary>
    private static V1JlSerialDevice OracleOpened(byte[]? reply, out JlDeviceInfoView? info)
    {
        var oracle = V1JlSerialDevice.Open()!;
        foreach (var b in reply ?? [])
            oracle.Port.Input.Enqueue(b);
        var read = oracle.GetDeviceInfo();
        info = read == null ? null : new JlDeviceInfoView(read.Status, read.Width, read.Height);
        return oracle;
    }

    private sealed record JlDeviceInfoView(int Status, int Width, int Height);

    // ---- The probe: detection vectors ----

    public static TheoryData<int, int, string, int, int> Resolutions() => new()
    {
        { 480, 960, "chillarc360", 480, 960 },   // JlPanelModelDatabase.cs:28-38
        { 1920, 462, "stripdisplay", 1920, 462 }, // :39-49, "matched by resolution" (:46)
        { 480, 1920, "480x1920", 480, 1920 },     // a resolution the table hasn't: the unknown model, by its size
        { 800, 480, "800x480", 800, 480 },
        { 960, 480, "960x480", 960, 480 },        // the Chill Arc's size turned is not the Chill Arc
        { 0, 0, "chillarc360", 480, 960 },        // no size: the table's size (JlPanelDeviceTask.cs:78-82), its model for the USB id (JlPanelHelper.cs:42)
        { 480, 0, "chillarc360", 480, 960 },      // both must be over 0 (:78)
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Probe_TakesTheModelByTheResolutionTheDeviceInfoGives_AndOnlyAsks(int width, int height, string model, int modelWidth, int modelHeight)
    {
        var json = InfoJson(width, height);
        var (devices, device) = Plug(json);
        var oracle = OracleOpened(Reply(json), out var info);
        Assert.Equal(200, info!.Status);

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal(new PanelProbe(true, new PanelIdentity(model, modelWidth, modelHeight, "A1B2C3", "Ver1.0"), PanelDeviceStatus.Ok), probe);
        // A panel that answers is asked only 1.4.x's getDeviceInfo (:192), the input dropped before it (:86): no pipeline
        // reset, no light, no close, no frame; then the port let go.
        Assert.Equal(
            ["h1 open serial 2000000 dtr rts", "h1 serial discard", $"h1 serial> {Hex(oracle.Port.Writes[1])}", $"h1 serial< {Hex(Reply(json))}", "h1 close serial"],
            Lines(device));
        // Within the probe's budget: the open given no more than it, the answer waited for a second.
        var timings = device.Timings;
        Assert.True(timings[0].TimeoutMs <= JlDriver.ProbeBudget.TotalMilliseconds, $"the open was given {timings[0].TimeoutMs} ms");
        Assert.Equal(JlDriver.ProbeReplyMs, timings[3].TimeoutMs);
    }

    [Fact]
    public void Probe_OfAPanelThatAnswersOnlyAfterThePipelineReset_SendsItOnce_After1_4sPauses_AndAsksAgain()
    {
        // A panel whose parser is still in a frame (a stream cut off) swallows the query; 1.4.x's open (JlSerialDevice.cs:82-86)
        // flushed it with the reset: sent only once the bare query went unanswered for a whole second, then the query again.
        var json = InfoJson(480, 960);
        var (devices, device) = Plug(null);
        var flushed = false;
        device.Serial.Respond(seen =>
        {
            flushed |= seen.Data.AsSpan().SequenceEqual(JlProtocol.PipelineReset);
            return flushed && IsQuery(seen) ? Reply(json) : null;
        });
        var pauses = 0;
        var driver = new JlDriver(devices) { Wait = (_, stop) => { pauses++; stop.ThrowIfCancellationRequested(); } };

        var probe = driver.Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((true, PanelDeviceStatus.Ok, "chillarc360"), (probe.Mine, probe.Status, probe.Identity?.Model));
        var query = Hex(JlProtocol.DeviceInfoQuery);
        Assert.Equal(
            ["h1 open serial 2000000 dtr rts", "h1 serial discard", $"h1 serial> {query}", "h1 serial< !timeout",
             $"h1 serial> {Hex(JlProtocol.PipelineReset.ToArray())}", "h1 serial discard", $"h1 serial> {query}", $"h1 serial< {Hex(Reply(json))}", "h1 close serial"],
            Lines(device));
        Assert.Equal(2, pauses); // 1.4.x's 200 ms after the open, and after the reset
        var reads = device.Timings.Where((_, i) => i is 3 or 7).Select(t => t.TimeoutMs);
        Assert.Equal([JlDriver.ProbeReplyMs, JlDriver.ProbeReplyMs], reads);
    }

    [Fact]
    public void Probe_OfADeviceThatAnswersNeitherAloneNorAfterTheReset_IsNotMine_AndAsksNothingMore()
    {
        // Not the family's: "not mine", so the engine leaves it alone until it is plugged in again (its own per-plug state).
        var (devices, device) = Plug(null);
        var driver = Driver(devices);
        var query = Hex(JlProtocol.DeviceInfoQuery);
        string[] Asked(int handle) =>
            [$"h{handle} open serial 2000000 dtr rts", $"h{handle} serial discard", $"h{handle} serial> {query}", $"h{handle} serial< !timeout",
             $"h{handle} serial> {Hex(JlProtocol.PipelineReset.ToArray())}", $"h{handle} serial discard", $"h{handle} serial> {query}", $"h{handle} serial< !timeout",
             $"h{handle} close serial"];

        var probe = driver.Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((false, null, PanelDeviceStatus.Ok), (probe.Mine, probe.Identity, probe.Status));
        Assert.Equal(Asked(1), Lines(device));

        // The driver remembers nothing: asked again, it asks the same way.
        var again = driver.Probe(Panel, TestContext.Current.CancellationToken);
        Assert.Equal(probe, again);
        Assert.Equal(Asked(2), Lines(device).Skip(Asked(1).Length));
    }

    [Fact]
    public void Probe_CancelledBeforeAnAnswer_SendsNothingMore_AndTheNextProbeStartsOver()
    {
        // A probe cut short (the engine's stop, a sleep, the deadline) holds nothing back: a panel that needs the reset
        // is found by the next one.
        var json = InfoJson(480, 960);
        var (devices, device) = Plug(null);
        var flushed = false;
        device.Serial.Respond(seen =>
        {
            flushed |= seen.Data.AsSpan().SequenceEqual(JlProtocol.PipelineReset);
            return flushed && IsQuery(seen) ? Reply(json) : null;
        });
        using var cancel = new CancellationTokenSource();
        var pauses = 0;
        var atCancel = 0;
        var driver = new JlDriver(devices)
        {
            Wait = (_, stop) =>
            {
                if (++pauses == 2)
                {
                    atCancel = device.Trace.Entries.Count;
                    cancel.Cancel();
                }
                stop.ThrowIfCancellationRequested();
            },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => driver.Probe(Panel, cancel.Token));

        Assert.Equal(["h1 close serial"], Lines(device).Skip(atCancel));
        var next = driver.Probe(Panel, TestContext.Current.CancellationToken);
        Assert.True((next.Mine, next.Status) == (true, PanelDeviceStatus.Ok), $"{next.Status}: {next.Message}");
    }

    [Fact]
    public void Probe_WithASlowOpen_AndAnAnswerLateAfterTheReset_FindsThePanelWithinTheEnginesDeadline()
    {
        // The whole probe on one clock: an open of 1.6 s, the bare query unanswered for its second, the reset, and an
        // answer 0.9 s after the query again still lands inside the probe's budget, so inside the engine's 5 s.
        var json = InfoJson(480, 960);
        var (devices, device) = Plug(null);
        var flushed = false;
        device.Serial.Respond(seen =>
        {
            flushed |= seen.Data.AsSpan().SequenceEqual(JlProtocol.PipelineReset);
            return flushed && IsQuery(seen) ? Reply(json) : null;
        });
        var clock = new ManualClock();
        var driver = new JlDriver(new SlowPort(devices, clock, TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(0.9)))
        {
            Time = clock,
            Wait = (time, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                clock.Advance(time);
            },
        };
        var started = clock.GetTimestamp();

        var probe = driver.Probe(Panel, TestContext.Current.CancellationToken);

        var took = clock.GetElapsedTime(started);
        Assert.True((probe.Mine, probe.Status) == (true, PanelDeviceStatus.Ok), $"{probe.Status}: {probe.Message}");
        Assert.True(took <= JlDriver.ProbeBudget, $"the probe took {took.TotalSeconds:0.###} s");
        Assert.True(took < global::InfoPanel.Panels.PanelManager.ProbeDeadline, $"the probe took {took.TotalSeconds:0.###} s");
        Assert.Contains($"h1 serial> {Hex(JlProtocol.PipelineReset.ToArray())}", Lines(device));
    }

    [Fact]
    public void Probe_WhoseTimeRunsOutBeforeTheDeviceCouldAnswer_CouldntAskIt_AndSendsNoReset()
    {
        // An open of 3.5 s leaves the bare query less than its second: unanswered then, the device wasn't asked fully,
        // which is a probe that couldn't ask (transient), not "not mine"; no reset is sent in what is left.
        var (devices, device) = Plug(null);
        var clock = new ManualClock();
        var driver = new JlDriver(new SlowPort(devices, clock, TimeSpan.FromSeconds(3.5), TimeSpan.Zero))
        {
            Time = clock,
            Wait = (time, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                clock.Advance(time);
            },
        };
        var started = clock.GetTimestamp();

        var probe = driver.Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((false, PanelDeviceStatus.Timeout), (probe.Mine, probe.Status));
        Assert.True(clock.GetElapsedTime(started) <= JlDriver.ProbeBudget, $"the probe took {clock.GetElapsedTime(started).TotalSeconds:0.###} s");
        Assert.DoesNotContain(Lines(device), l => l.Contains(Hex(JlProtocol.PipelineReset.ToArray()), StringComparison.Ordinal));
    }

    /// <summary>
    /// The fake devices on a scripted clock: a serial open takes <c>open</c> (one longer than its timeout fails at it);
    /// a read with nothing come waits its whole timeout, as a port does; a reply comes <c>answer</c> into its read (one
    /// later than the read's timeout isn't read in time).
    /// </summary>
    private sealed class SlowPort(IPanelDevices inner, ManualClock clock, TimeSpan open, TimeSpan answer) : IPanelDevices
    {
        public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor) => inner.Describe(device, out descriptor);

        public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default) =>
            inner.OpenUsb(device, interfaceNumber, timeoutMs, out usb, cancellationToken);

        public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections) =>
            inner.ListHid(device, filter, out collections);

        public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default) =>
            inner.OpenHid(device, filter, timeoutMs, out hid, cancellationToken);

        public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default) =>
            inner.OpenHid(device, collection, timeoutMs, out hid, cancellationToken);

        public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default)
        {
            serial = null;
            if (open.TotalMilliseconds > timeoutMs)
            {
                clock.Advance(TimeSpan.FromMilliseconds(timeoutMs));
                return PanelDeviceStatus.Timeout;
            }
            clock.Advance(open);
            var status = inner.OpenSerial(device, options, timeoutMs, out var port, cancellationToken);
            if (port != null)
                serial = new Port(port, clock, answer);
            return status;
        }

        private sealed class Port(IPanelSerial inner, ManualClock clock, TimeSpan answer) : IPanelSerial
        {
            public string PortName => inner.PortName;

            public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default) => inner.Write(data, timeoutMs, cancellationToken);

            public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
            {
                var status = inner.Read(buffer, timeoutMs, out read, cancellationToken);
                if (status == PanelDeviceStatus.Ok && answer.TotalMilliseconds <= timeoutMs)
                {
                    clock.Advance(answer);
                    return status;
                }
                if (status is PanelDeviceStatus.Ok or PanelDeviceStatus.Timeout)
                {
                    read = 0;
                    clock.Advance(TimeSpan.FromMilliseconds(timeoutMs));
                    return PanelDeviceStatus.Timeout;
                }
                return status;
            }

            public PanelDeviceStatus DiscardInput() => inner.DiscardInput();

            public void Dispose() => inner.Dispose();
        }
    }

    [Fact]
    public void Probe_WithoutAUid_HasNoSerial_AndTheFirmwareIsTheVersion()
    {
        var (devices, _) = Plug("{\"status\":200,\"version\":\"4.1\",\"width\":480,\"height\":960}");

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal(new PanelIdentity("chillarc360", 480, 960, null, "4.1"), probe.Identity);
    }

    public static TheoryData<string, string?, PanelDeviceStatus> RefusedProbes() => new()
    {
        { "a status other than 200", InfoJson(480, 960, status: 500), PanelDeviceStatus.Io },   // JlPanelDeviceTask.cs:65
        { "not JSON", "this is not json", PanelDeviceStatus.Io },                                // JlSerialDevice.cs:202-206
    };

    [Theory]
    [MemberData(nameof(RefusedProbes))]
    public void Probe_OfAPanelThatDoesntAnswerAs1_4Wanted_CouldntAskIt_AndLetsThePortGo(string why, string? json, PanelDeviceStatus status)
    {
        var (devices, device) = Plug(json);

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.True((probe.Mine, probe.Status) == (false, status), $"{why}: {probe.Status} {probe.Message}");
        Assert.Equal("h1 close serial", Lines(device)[^1]);
        Assert.DoesNotContain(Lines(device), l => l.Contains(Hex(JlProtocol.CloseFrame), StringComparison.Ordinal));
    }

    [Fact]
    public void AReplyPastTheCap_IsRefused_ThoughAWholeOneFollows_AndNoMoreThanTheCapIsRead()
    {
        // The session reads at most MaxReplyBytes for one reply (a whole reply and some bytes before it): noise up to the
        // cap and then a whole reply is refused, the reply never read; the same reply just within the cap is taken.
        var reply = Reply(InfoJson(480, 960));
        var (devices, device) = Plug(null);
        device.Serial.Respond(seen => IsQuery(seen) ? [.. new byte[JlSession.MaxReplyBytes], .. reply] : null);

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((false, PanelDeviceStatus.Io), (probe.Mine, probe.Status));
        Assert.True(Read(device) <= JlSession.MaxReplyBytes, $"{Read(device)} bytes read");

        var (within, at) = Plug(null);
        at.Serial.Respond(seen => IsQuery(seen) ? [.. new byte[JlSession.MaxReplyBytes - reply.Length], .. reply] : null);
        var taken = Driver(within).Probe(Panel, TestContext.Current.CancellationToken);
        Assert.Equal((true, PanelDeviceStatus.Ok), (taken.Mine, taken.Status));
        Assert.Equal(JlSession.MaxReplyBytes, Read(at));
    }

    private static int Read(FakePanelDevice device) => device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialRead).Sum(e => e.Data.Length);

    [Fact]
    public void Probe_OfAnotherDevice_TouchesNothing_OfOneGone_AnswersGone_AndWithoutTheSeam_NotSupported()
    {
        var (devices, device) = Plug(InfoJson(480, 960));
        var other = new PanelCandidate(0x33C3, 0x7793, 1, 7, "1-5"); // the SPI variant (JlPanelModelDatabase.cs:17-20)
        var notMine = Driver(devices).Probe(other, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Ok), (notMine.Mine, notMine.Status));
        Assert.Empty(device.Trace.Entries);

        var gone = Driver(new FakePanelDevices()).Probe(Panel, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Gone), (gone.Mine, gone.Status));

        var seamless = new JlDriver().Probe(Panel, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.NotSupported), (seamless.Mine, seamless.Status));
        Assert.Equal(JlDriver.FamilyId, new JlDriver().Family);
    }

    [Theory]
    [InlineData(PanelDeviceStatus.Busy, "busy")]     // another program holds the port
    [InlineData(PanelDeviceStatus.Access, "access")]
    [InlineData(PanelDeviceStatus.Gone, "gone")]
    [InlineData(PanelDeviceStatus.Timeout, "timeout")]
    public void Probe_OfAPortThatCantBeOpened_CouldntAskIt_ATransientStatus(PanelDeviceStatus status, string traced)
    {
        // Nothing was asked, so nothing is known: the status, and the engine offers the device again on a later pass.
        var (devices, device) = Plug(InfoJson(480, 960));
        device.Serial.OpenStatus = status;

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((false, status), (probe.Mine, probe.Status));
        Assert.Equal([$"h1 open serial 2000000 dtr rts !{traced}"], Lines(device));
    }

    [Fact]
    public void APlatformThatRefuses2Mbaud_IsAskedForTheNominal115200()
    {
        // 1.4.x asked for 2,000,000 (JlSerialDevice.cs:35, :71); a CDC port ignores the rate, but a platform may refuse it
        // (the open answers io): then the nominal rate, once.
        var (devices, device) = Plug(InfoJson(480, 960));
        device.Fail(1, PanelDeviceStatus.Io);

        var probe = Driver(devices).Probe(Panel, TestContext.Current.CancellationToken);

        Assert.Equal((true, PanelDeviceStatus.Ok), (probe.Mine, probe.Status));
        Assert.Equal(["h1 open serial 2000000 dtr rts !io", "h2 open serial 115200 dtr rts"], Lines(device).Take(2));

        var (refusing, both) = Plug(InfoJson(480, 960));
        both.Fail(1, PanelDeviceStatus.Io);
        both.Fail(2, PanelDeviceStatus.Io);
        var refused = Driver(refusing).Probe(Panel, TestContext.Current.CancellationToken);
        Assert.Equal((false, PanelDeviceStatus.Io), (refused.Mine, refused.Status));
        Assert.Equal(2, both.Calls);
    }

    [Fact]
    public void Matches_AreTheManifests()
    {
        Assert.Equal([new PanelMatch(0x33C3, 0x7788, null, PanelTransport.Serial)], Driver(new FakePanelDevices()).Matches);
    }

    // ---- The start, a frame and the stop, as 1.4.x made them ----

    public static TheoryData<string, int, int> Models() => new()
    {
        { "chillarc360", 480, 960 },
        { "stripdisplay", 1920, 462 },
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void StartFrameAndStop_Trace_Is1_4s(string model, int width, int height)
    {
        var json = InfoJson(width, height);
        var (devices, device) = Plug(json);
        var jpeg = Jpeg(3000);

        // 1.4.x, fed the same reply: the task's open (JlPanelDeviceTask.cs:52, JlSerialDevice.cs:66-97), its device info
        // (:64), the brightness (:85), one frame (:177, SendJpegFrame :223-250), and the dispose of the stop (:100, :252-273).
        var oracle = OracleOpened(Reply(json), out var info);
        Assert.Equal((200, width, height), (info!.Status, info.Width, info.Height));
        oracle.SetBrightness(65);
        oracle.SendJpegFrame(jpeg);
        oracle.Dispose();
        var w = oracle.Port.Writes.Select(Hex).ToList();
        Assert.Equal((7, 1), (w.Count, oracle.Port.Discards));

        var session = Start(Driver(devices), new PanelIdentity(model, width, height, "A1B2C3", "Ver1.0"), brightness: 65);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(jpeg, PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken).Status);
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        var expected =
            "h1 open serial 2000000 dtr rts\n" +    // JlSerialDevice.cs:71-79, DTR and RTS on
            $"h1 serial> {w[0]}\n" +                // :83-84 the pipeline reset, 200 ms after the open
            "h1 serial discard\n" +                 // :85-86 200 ms, then the input dropped
            $"h1 serial> {w[1]}\n" +                // :192 getDeviceInfo (JlPanelDeviceTask.cs:64)
            $"h1 serial< {Hex(Reply(json))}\n" +    // :140-185 its reply, status 200 (:65)
            $"h1 serial> {w[2]}\n" +                // JlPanelDeviceTask.cs:85 the brightness pushed once at the start
            $"h1 serial> {w[3]}\n" +                // :228 the keep-alive before the frame
            $"h1 serial> {w[4]}\n" +                // :230-247 the envelope
            $"h1 serial> {w[5]}\n" +                // :249 FF D9 FF D9
            $"h1 serial> {w[6]}\n" +                // :262 close, as the dispose sent it
            "h1 close serial\n";                    // :263-265
        Assert.Equal(expected, device.Trace.Format());
        Assert.Equal(0, device.CallsAfterDispose);
        Assert.Equal((width, height), (session.Capabilities.Width, session.Capabilities.Height));
    }

    [Fact]
    public void ARepeatedFrame_SendsOnlyTheKeepAlive_AsTheIdleSendLoopDid()
    {
        // JlPanelDeviceTask.cs:162, :183-186: no frame within 1 s, a bare 0x11 (SendKeepAlive, JlSerialDevice.cs:210).
        var (devices, device) = Plug(InfoJson(480, 960));
        using var session = Start(Driver(devices), ChillArc);
        var before = device.Trace.Entries.Count;
        var oracle = V1JlSerialDevice.Open()!;
        oracle.Port.Writes.Clear();
        oracle.SendKeepAlive();

        var write = session.WriteFrame(new PanelFrameView(Jpeg(3000), PanelFrameFormat.Jpeg, 1, repeat: true), TestContext.Current.CancellationToken);

        Assert.Equal(PanelDeviceStatus.Ok, write.Status);
        Assert.Equal([$"h1 serial> {Hex(Assert.Single(oracle.Port.Writes))}"], Lines(device).Skip(before));
    }

    [Fact]
    public void TheBrightness_IsSentOnlyWhenItChanges_As1_4sSendLoopDid()
    {
        // JlPanelDeviceTask.cs:85 at the start, then :170-174 only when it differs from the last sent; the light clamped
        // to 0–100 (JlSerialDevice.cs:213-217).
        var (devices, device) = Plug(InfoJson(480, 960));
        using var session = Start(Driver(devices), ChillArc, brightness: 40);
        var lights = () => device.Trace.Entries.Where(e => e.Op == PanelTraceOp.SerialWrite && e.Data.Length == 8 && e.Data[4] == JlProtocol.SetLight).Select(e => e.Data[5]).ToList();
        Assert.Equal([40], lights());

        foreach (var percent in new[] { 40, 41, 41, 0, 100, 100, 7 })
            Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(percent));

        Assert.Equal([40, 41, 0, 100, 7], lights().Select(b => (int)b));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Capabilities_AreTheModelsSize_Jpeg80Within80KB_Step15ToQ30_TheLightAndDimming_AKeepAliveASecond(string model, int width, int height)
    {
        // The size the device info gives (JlPanelDeviceTask.cs:77-82); JPEG at the device's default quality 80
        // (Models/JlPanelDevice.cs:47) within the table's 80 KB (JlPanelModelDatabase.cs:36, :47), 15 lower a try down
        // to 30 (JlPanelDeviceTask.cs:243-249); the light 0–100 (JlSerialDevice.cs:213-217) and the frame dimmed below 100
        // too (JlPanelDeviceTask.cs:231-236); at most 60 fps, the Target FPS slider's top (UsbPanelsPage.xaml:1688); the
        // keep-alive after 1 s without a frame (:162, :183-186).
        var (devices, _) = Plug(InfoJson(width, height));
        using var session = Start(Driver(devices), new PanelIdentity(model, width, height, null, null));
        var caps = session.Capabilities;

        Assert.Equal((width, height, PanelFrameFormat.Jpeg, 60), (caps.Width, caps.Height, Assert.Single(caps.Formats), caps.MaxFps));
        Assert.Equal((80, 30, 15, (int?)80 * 1024), (caps.JpegQuality, caps.MinJpegQuality, caps.JpegQualityStep, caps.MaxFrameBytes));
        Assert.Equal(new PanelRange(0, 100), caps.Brightness);
        Assert.True(caps.DimInSoftware);
        Assert.Equal(TimeSpan.FromSeconds(1), caps.KeepAliveEvery);
        Assert.False(caps.RotatesItself);
        Assert.Null(caps.Crop);
        Assert.Null(caps.Overlays);
    }

    public static TheoryData<string, string?, PanelDeviceStatus> RefusedStarts() => new()
    {
        { "a status other than 200", InfoJson(480, 960, status: 500), PanelDeviceStatus.Io },
        { "no answer", null, PanelDeviceStatus.Timeout },
    };

    [Theory]
    [MemberData(nameof(RefusedStarts))]
    public void AStartWhoseDeviceInfoFails_AnswersNoSession_AndClosesAsTheDisposeDid(string why, string? json, PanelDeviceStatus status)
    {
        // JlPanelDeviceTask.cs:64-71: getDeviceInfo failed, the device disposed, whose Dispose sends close (JlSerialDevice.cs:262).
        var (devices, device) = Plug(json);

        var open = Driver(devices).Open(Panel, ChillArc, new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.True((open.Session, open.Status) == (null, status), $"{why}: {open.Status} {open.Message}");
        Assert.Equal([$"h1 serial> {Hex(JlProtocol.CloseFrame)}", "h1 close serial"], Lines(device).TakeLast(2));
        Assert.DoesNotContain(Lines(device), l => l.Contains(Hex(JlProtocol.Light(100)), StringComparison.Ordinal));
    }

    [Fact]
    public void AStartWhoseLightIsntTaken_AnswersNoSession()
    {
        // The SDK's rule (a call that doesn't answer Ok ends it), where 1.4.x logged the failure and went on (:85-86).
        var (devices, device) = Plug(InfoJson(480, 960));
        device.Fail(6, PanelDeviceStatus.Timeout); // open, reset, discard, query, reply, light

        var open = Driver(devices).Open(Panel, ChillArc, new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Timeout), (open.Session, open.Status));
        Assert.Equal([$"h1 serial> {Hex(JlProtocol.Light(100))} !timeout", $"h1 serial> {Hex(JlProtocol.CloseFrame)}", "h1 close serial"], Lines(device).TakeLast(3));
    }

    [Fact]
    public void AStartWhoseDeviceGoes_TriesNothingMore()
    {
        var (devices, device) = Plug(InfoJson(480, 960));
        device.UnplugAt(2); // the pipeline reset

        var open = Driver(devices).Open(Panel, ChillArc, new PanelOpenOptions(100), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Gone), (open.Session, open.Status));
        Assert.Equal(["h1 open serial 2000000 dtr rts", $"h1 serial> {Hex(JlProtocol.PipelineReset.ToArray())} !gone", "h1 close serial"], Lines(device));
    }

    [Fact]
    public void AStartCancelled_Throws_AndClosesAsTheDisposeDid()
    {
        var (devices, device) = Plug(InfoJson(480, 960));
        using var cancel = new CancellationTokenSource();
        var driver = new JlDriver(devices) { Wait = (_, stop) => { cancel.Cancel(); stop.ThrowIfCancellationRequested(); } };

        Assert.Throws<OperationCanceledException>(() => driver.Open(Panel, ChillArc, new PanelOpenOptions(100), cancel.Token));

        Assert.Equal(["h1 open serial 2000000 dtr rts", $"h1 serial> {Hex(JlProtocol.CloseFrame)}", "h1 close serial"], Lines(device));
    }

    // ---- An unknown resolution: "Try it as…" ----

    [Fact]
    public void AnUnknownModel_IsntStarted_UntilTheModelSettingNamesOne_ThenRunsAtTheSizeTheDeviceGives()
    {
        var (devices, device) = Plug(InfoJson(800, 480));
        var unknown = new PanelIdentity("800x480", 800, 480, "A1B2C3", "Ver1.0");

        var refused = Driver(devices).Open(Panel, unknown, new PanelOpenOptions(100), TestContext.Current.CancellationToken);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (refused.Session, refused.Status));
        var notOne = Driver(devices).Open(Panel, unknown, new PanelOpenOptions(100, FamilySettings: Settings("{\"model\":\"800x480\"}")), TestContext.Current.CancellationToken);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (notOne.Session, notOne.Status));
        Assert.Empty(device.Trace.Entries);

        // Tried as the strip: 1.4.x trusted the size the device gives over its table's (JlPanelDeviceTask.cs:77-82).
        using var session = Start(Driver(devices), unknown, settings: Settings("{\"model\":\"stripdisplay\"}"));
        Assert.Equal((800, 480, (int?)80 * 1024), (session.Capabilities.Width, session.Capabilities.Height, session.Capabilities.MaxFrameBytes));
    }

    [Fact]
    public void AKnownModel_RunsAtTheSizeTheDeviceGivesAtItsStart_ElseItsOwn()
    {
        // :77-82: the device's size when both are over 0, else the table's.
        var (devices, _) = Plug(InfoJson(0, 0));
        using var session = Start(Driver(devices), ChillArc);
        Assert.Equal((480, 960), (session.Capabilities.Width, session.Capabilities.Height));
    }

    // ---- The session ----

    [Fact]
    public void AFrameWhoseDeviceGoes_StopsThere_AndTheStopSendsNothing()
    {
        var (devices, device) = Plug(InfoJson(480, 960));
        var session = Start(Driver(devices), ChillArc);
        device.UnplugAt(device.Calls + 2); // the envelope

        var write = session.WriteFrame(new PanelFrameView(Jpeg(3000), PanelFrameFormat.Jpeg, 1, false), TestContext.Current.CancellationToken);
        var calls = device.Calls;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));

        Assert.Equal(PanelDeviceStatus.Gone, write.Status);
        Assert.Equal(calls, device.Calls);
        Assert.Equal("h1 close serial", Lines(device)[^1]);
    }

    [Fact]
    public void Stop_SendsClose_As1_4sDispose_AndNothingIsWrittenAfterClose()
    {
        // 1.4.x's stop disposed the device (JlPanelDeviceTask.cs:98-101), whose Dispose sent close (JlSerialDevice.cs:262)
        // before the port went (:263-265). Close(screenOff: false), for a device gone, writes nothing.
        var (devices, device) = Plug(InfoJson(480, 960));
        var session = Start(Driver(devices), ChillArc);
        var before = device.Trace.Entries.Count;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();
        Assert.Equal([$"h1 serial> {Hex(JlProtocol.CloseFrame)}", "h1 close serial"], Lines(device).Skip(before));
        Assert.Equal(0, device.CallsAfterDispose);

        var (quietDevices, quiet) = Plug(InfoJson(480, 960));
        var unplugged = Start(Driver(quietDevices), ChillArc);
        var calls = quiet.Calls;
        unplugged.Close(screenOff: false, TimeSpan.FromSeconds(3));
        unplugged.Dispose();
        Assert.Equal(calls, quiet.Calls);
        Assert.Equal("h1 close serial", Lines(quiet)[^1]);

        var (spentDevices, spent) = Plug(InfoJson(480, 960));
        var late = Start(Driver(spentDevices), ChillArc);
        calls = spent.Calls;
        late.Close(screenOff: true, TimeSpan.Zero);
        Assert.Equal(calls, spent.Calls);
    }

    // ---- The conformance suite ----

    [Theory]
    [MemberData(nameof(Models))]
    public void TheDriver_PassesTheConformanceSuite(string model, int width, int height)
    {
        var failures = Conformance.Check(() =>
        {
            var (devices, device) = Plug(InfoJson(width, height));
            return new ConformanceSetup(new JlDriver(devices), Panel, device) { ProbeWrite = ProbeQueries(device) };
        });

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        _ = model;
    }

    /// <summary>
    /// The probe's writes, on its port's handle: the device info query; 1.4.x's pipeline reset only after that query went
    /// unanswered, once; then the query once more, and nothing else (each check's setup is a new device).
    /// </summary>
    private static Func<PanelTraceEntry, bool> ProbeQueries(FakePanelDevice device) => write =>
    {
        static bool IsReset(PanelTraceEntry e) => e.Data.AsSpan().SequenceEqual(JlProtocol.PipelineReset);
        var before = device.Trace.Entries.TakeWhile(e => !ReferenceEquals(e, write)).Where(e => e.Handle == write.Handle).ToList();
        var writes = before.Where(e => e.Op == PanelTraceOp.SerialWrite).ToList();
        var (queries, resets) = (writes.Count(IsQuery), writes.Count(IsReset));
        if (IsQuery(write))
            return queries == 0 || (queries == 1 && resets == 1);
        if (!IsReset(write))
            return false;
        var query = before.FindLastIndex(e => e.Op == PanelTraceOp.SerialWrite && IsQuery(e));
        return queries == 1 && resets == 0
            && !before.Skip(query + 1).Any(e => e.Op == PanelTraceOp.SerialRead && e.Data.Length > 0);
    };

    // ---- The 1.x import ----

    [Fact]
    public void V1Import_TheFixturesChillArc360_MapsToItsModel_AtItsQuality()
    {
        // The engine's 1.x fixture read through V1SettingsReader; its JL devices' Model maps through the package's
        // v1Model, and their JpegQuality (1.4's per-device setting, 80 by default) is the family's own. TargetFrameRate is the
        // engine's per-panel frame rate; DeviceLocation was a COM port's name, so an imported panel has no serial.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var settings = V1SettingsReader.Read(XElement.Load(path, LoadOptions.SetLineInfo), path, new ImportLimits(), new ImportDiagnostics(100));
        var v1Devices = settings.Devices.Where(d => d.Family == JlDriver.FamilyId).ToList();
        var family = PanelManifest.Read(typeof(JlDriver).Assembly).Family(JlDriver.FamilyId);

        Assert.Equal(2, v1Devices.Count);
        var (devices, _) = Plug(InfoJson(480, 960));
        using var session = Start(Driver(devices), ChillArc);
        foreach (var v1 in v1Devices)
        {
            var model = Assert.Single(family.Models, m => m.V1Model == v1.Fields["Model"]);
            Assert.Equal(("chillarc360", 480, 960), (model.Id, model.Width, model.Height));
            Assert.Equal(int.Parse(v1.Fields["JpegQuality"], System.Globalization.CultureInfo.InvariantCulture), session.Capabilities.JpegQuality);
        }
        Assert.DoesNotContain(family.Models, m => m.V1Model == "Unknown");
    }
}
