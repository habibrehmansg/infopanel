using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Turing.V1Oracle;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Turing;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// The serial panels over the kit's fake devices (§2.2 (2), (6), (8)): the 3.5"'s wake (<c>TuringPanel/TuringPanelHelper.cs</c>),
/// the probe, and each revision's start, full frame, sector update, brightness and stop as 1.4.x's
/// <c>Services/TuringPanelSerialTask.cs</c> made them through the library (<c>V1Oracle/TuringSmartScreenLib.cs</c>), fed
/// the same frames and replies; the XuanFang; the conformance suite.
/// </summary>
public sealed class TuringSerialDriverTests
{
    private static readonly PanelCandidate ThreeAndAHalf = new(0x1A86, 0x5722, 1, 7, "1-4");

    private static PanelCandidate CandidateOf(string usb) => usb switch
    {
        "1a86:5722" => ThreeAndAHalf,
        "1d6b:0121" => new(0x1D6B, 0x0121, 1, 8, "1-5"),
        "1d6b:0106" => new(0x1D6B, 0x0106, 1, 8, "1-5"),
        _ => new(0x0525, 0xA4A7, 1, 8, "1-5"),
    };

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] Hello(string answer) => [.. Encoding.ASCII.GetBytes(answer), 0];

    /// <summary>A serial device answering Hello as <paramref name="hello"/> and each QueryStatus as <paramref name="status"/>.</summary>
    private static (FakePanelDevices Devices, FakePanelDevice Device) Plug(PanelCandidate candidate, string? hello = "chs_5inch.dev1_rom1.88", Func<string?>? status = null)
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(candidate, PanelTransport.Serial);
        device.Serial.Respond(w => w.Data.Length > 0 && w.Data[0] == 0x01 && w.Data[1] == 0xef && hello != null ? Hello(hello)
            : w.Data.Length > 0 && w.Data[0] == 0xcf && (status ?? (() => "needReSend:0"))() is { } s ? Encoding.ASCII.GetBytes(s) : null);
        return (devices, device);
    }

    private sealed class Waits
    {
        public List<TimeSpan> Asked { get; } = [];
    }

    /// <summary>A driver whose pauses take no time, on a clock that stands still (a frame's sending is never cut short).</summary>
    private static TuringDriver Driver(IPanelDevices devices, Waits? waits = null) =>
        new(devices) { Wait = (time, stop) => { waits?.Asked.Add(time); stop.ThrowIfCancellationRequested(); }, Milliseconds = () => 1_000_000 };

    /// <summary>
    /// The device seam, passed through: swappable (another device plugged in at the same port), with a clock that moves
    /// as a real port's transfers would take (a write as its bytes take at <see cref="BytesPerSecond"/>, a read that
    /// brings an answer 2 ms, one that brings nothing its whole timeout), and an unplug at the read that follows a command.
    /// </summary>
    private sealed class Seam(IPanelDevices inner) : IPanelDevices
    {
        private int? _armed;

        public IPanelDevices Inner { get; set; } = inner;

        /// <summary>0: transfers take no time.</summary>
        public double BytesPerSecond { get; init; }

        /// <summary>The clock, in milliseconds.</summary>
        public double Now { get; private set; }

        /// <summary>The first byte of a write whose answer's read finds the device gone (the device unplugged there).</summary>
        public byte? UnplugAtTheAnswerTo { get; init; }

        public Action? Unplug { get; init; }

        /// <summary>A write also takes its time on the real clock (it sleeps it): for a test through the host, whose bounds are real.</summary>
        public bool RealTime { get; init; }

        /// <summary>Called with each write's bytes before it goes.</summary>
        public Action<byte[]>? OnWrite { get; init; }

        /// <summary>Each write's end on the real clock (<see cref="System.Diagnostics.Stopwatch"/>), its length and its last byte.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<(long At, int Length, byte Last)> Ends { get; } = new();

        public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor) => Inner.Describe(device, out descriptor);

        public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default) =>
            Inner.OpenUsb(device, interfaceNumber, timeoutMs, out usb, cancellationToken);

        public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections) =>
            Inner.ListHid(device, filter, out collections);

        public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default) =>
            Inner.OpenHid(device, filter, timeoutMs, out hid, cancellationToken);

        public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default) =>
            Inner.OpenHid(device, collection, timeoutMs, out hid, cancellationToken);

        public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default)
        {
            var status = Inner.OpenSerial(device, options, timeoutMs, out var opened, cancellationToken);
            serial = opened == null ? null : new Port(this, opened);
            return status;
        }

        private sealed class Port(Seam seam, IPanelSerial inner) : IPanelSerial
        {
            public string PortName => inner.PortName;

            public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default)
            {
                seam.OnWrite?.Invoke(data.ToArray());
                if (seam.BytesPerSecond > 0)
                {
                    var ms = data.Length * 1000.0 / seam.BytesPerSecond;
                    seam.Now += ms;
                    if (seam.RealTime)
                        Thread.Sleep(TimeSpan.FromMilliseconds(ms));
                }
                if (seam.UnplugAtTheAnswerTo is { } command && data.Length > 0 && data[0] == command && seam._armed == null)
                    seam._armed = 1;
                var status = inner.Write(data, timeoutMs, cancellationToken);
                if (seam.RealTime && data.Length > 0)
                    seam.Ends.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp(), data.Length, data[^1]));
                return status;
            }

            public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
            {
                if (seam._armed == 1)
                {
                    seam._armed = 2;
                    seam.Unplug!();
                }
                var status = inner.Read(buffer, timeoutMs, out read, cancellationToken);
                if (seam.BytesPerSecond > 0)
                    seam.Now += status == PanelDeviceStatus.Ok && read > 0 ? 2 : timeoutMs;
                return status;
            }

            public PanelDeviceStatus DiscardInput() => inner.DiscardInput();

            public void Dispose() => inner.Dispose();
        }
    }

    private static IPanelSession Start(TuringDriver driver, PanelCandidate candidate, int brightness = 70, JsonElement settings = default)
    {
        var probe = driver.Probe(candidate, TestContext.Current.CancellationToken);
        Assert.True(probe is { Mine: true, Identity: not null }, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(brightness, FamilySettings: settings), TestContext.Current.CancellationToken);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        return open.Session!;
    }

    private static List<string> SerialLines(FakePanelDevice device, int skip = 0) =>
        [.. device.Trace.Entries.Skip(skip).Select(PanelTrace.FormatLine)];

    // ---- The 3.5"'s wake, and the probe ----

    [Fact]
    public void The3_5_IsTakenAtOnce_AndItsStartWakesItFiveTimesASecondApart_ThenDrivesItAsRevisionA()
    {
        // TuringPanelHelper.cs:61-80,125-165: every 1a86:5722 port opened at 115200 and closed five times a second apart
        // (the loop never ended early: every such port counted) before the ports were searched. The probe asks nothing
        // (revision A has no query); the start wakes the port as 1.4.x did, then drives the 3.5" as revision A
        // (TuringPanelSerialTask.cs:43-51).
        var (devices, device) = Plug(ThreeAndAHalf);
        var waits = new Waits();
        var driver = Driver(devices, waits);

        var taken = driver.Probe(ThreeAndAHalf, TestContext.Current.CancellationToken);
        Assert.Equal(new PanelProbe(true, new PanelIdentity("turing35", 320, 480, null, null), PanelDeviceStatus.Ok), taken);
        Assert.Empty(device.Trace.Entries);

        var open = driver.Open(ThreeAndAHalf, taken.Identity!, new PanelOpenOptions(70), TestContext.Current.CancellationToken);
        Assert.NotNull(open.Session);
        open.Session!.Dispose();

        Assert.Equal(Enumerable.Repeat("h? open serial 115200|h? close serial", 5), Pairs(device).Take(5));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(1), 4), waits.Asked);
        Assert.Equal("open serial 115200 dtr rts", SerialLines(device)[10][3..]);
        Assert.DoesNotContain(device.Trace.Entries.Take(10), e => e.Op is PanelTraceOp.SerialWrite or PanelTraceOp.SerialRead);
    }

    private static IEnumerable<string> Pairs(FakePanelDevice device)
    {
        var lines = SerialLines(device).Select(l => "h?" + l[l.IndexOf(' ')..]).ToList();
        for (var i = 0; i + 1 < lines.Count; i += 2)
            yield return lines[i] + "|" + lines[i + 1];
    }

    [Fact]
    public void AWakeWhosePortWontOpen_StillCounts_As1_4sDid()
    {
        // TuringPanelHelper.cs:143-152: an open that fails is logged and counted: all five rounds run; the start's own
        // open then answers why.
        var (devices, device) = Plug(ThreeAndAHalf);
        device.Serial.OpenStatus = PanelDeviceStatus.Busy;
        var waits = new Waits();
        var driver = Driver(devices, waits);

        var open = driver.Open(ThreeAndAHalf, new PanelIdentity("turing35", 320, 480, null, null), new PanelOpenOptions(70), TestContext.Current.CancellationToken);

        Assert.Equal((null, PanelDeviceStatus.Busy), (open.Session, open.Status));
        Assert.Equal(4, waits.Asked.Count);
    }

    [Fact]
    public void EveryStart_WakesThe3_5Afresh_AfterAnUnplug_AndAtAnAddressReused()
    {
        // 1.4.x woke every port each time it looked for panels and remembered nothing, so a start remembers nothing of an
        // earlier one: a 3.5" unplugged while it is woken and plugged back, or another at the same port and address once
        // a session has ended, is woken from its first round.
        static int Wakes(FakePanelDevice device) => SerialLines(device).Count(l => l.EndsWith("open serial 115200", StringComparison.Ordinal));
        var identity = new PanelIdentity("turing35", 320, 480, null, null);
        var stop = TestContext.Current.CancellationToken;
        var (first, unplugged) = Plug(ThreeAndAHalf);
        var seam = new Seam(first);
        var driver = Driver(seam);
        unplugged.UnplugAt(2); // the second wake's open

        var gone = driver.Open(ThreeAndAHalf, identity, new PanelOpenOptions(70), stop);
        Assert.Equal((null, PanelDeviceStatus.Gone), (gone.Session, gone.Status));
        Assert.Equal(1, Wakes(unplugged));

        var (back, pluggedBack) = Plug(ThreeAndAHalf); // the same port and address
        seam.Inner = back;
        var started = driver.Open(ThreeAndAHalf, identity, new PanelOpenOptions(70), stop);
        Assert.NotNull(started.Session);
        started.Session!.Close(screenOff: true, TimeSpan.FromSeconds(3));
        started.Session.Dispose();
        Assert.Equal(TuringDriver.WakeRounds, Wakes(pluggedBack));

        var (other, another) = Plug(ThreeAndAHalf); // another panel given the same address at that port
        seam.Inner = other;
        var again = driver.Open(ThreeAndAHalf, identity, new PanelOpenOptions(70), stop);
        Assert.NotNull(again.Session);
        again.Session!.Dispose();
        Assert.Equal(TuringDriver.WakeRounds, Wakes(another));
    }

    public static TheoryData<string, string, string, int, int> Revisions() => new()
    {
        { "1d6b:0121", "chs_21inch.dev1", "rev2inch", 480, 480 },
        { "1d6b:0106", "chs_5inch.dev1_rom1.88", "rev5inch", 800, 480 },
        { "0525:a4a7", "chs_88inch.dev1_rom1.88", "rev8inch", 480, 1920 },
    };

    [Theory]
    [MemberData(nameof(Revisions))]
    public void Probe_OfARevisionCOrEId_AsksHello_AndTakesTheModelItsIdNames(string usb, string hello, string model, int width, int height)
    {
        // The model by VID and PID (TuringPanelHelper.cs:95-110); the probe sends the library's Hello and checks its
        // answer as the library's Open did (RevisionC.cs: "chs_5inch", RevisionE.cs: "chs_"), so another device on these
        // USB gadget ids isn't taken; then lets the port go.
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate, hello);

        var probe = Driver(devices).Probe(candidate, TestContext.Current.CancellationToken);

        Assert.Equal(new PanelProbe(true, new PanelIdentity(model, width, height, null, null), PanelDeviceStatus.Ok), probe);
        Assert.Equal(
            ["h1 open serial 115200 dtr rts", "h1 serial discard", "h1 serial> 01ef6900000001000000c5d3" + new string('0', 476), "h1 serial< " + Hex(Hello(hello)), "h1 close serial"],
            SerialLines(device));
    }

    [Theory]
    [InlineData(null, false, PanelDeviceStatus.Timeout)]
    [InlineData("OK", false, PanelDeviceStatus.Ok)]
    public void Probe_OfAPortThatDoesntAnswerAsATuring_IsntTaken(string? hello, bool mine, PanelDeviceStatus status)
    {
        var candidate = CandidateOf("1d6b:0106");
        var (devices, device) = Plug(candidate, hello);

        var probe = Driver(devices).Probe(candidate, TestContext.Current.CancellationToken);

        Assert.Equal((mine, status), (probe.Mine, probe.Status));
        Assert.Equal("h1 close serial", SerialLines(device)[^1]);
    }

    // ---- Each revision: start, frames, brightness and the stop, as the library did them ----

    public static TheoryData<string, char, int, int> Sessions() => new()
    {
        { "1a86:5722", 'A', 320, 480 },
        { "1d6b:0106", 'C', 800, 480 },
        { "0525:a4a7", 'E', 480, 1920 },
        { "1d6b:0121", 'E', 480, 480 },
    };

    private static (SKBitmap Bitmap, byte[] Rgb565, byte[] Bgr) Changed((SKBitmap Bitmap, byte[], byte[]) from, Func<int, int, bool> change)
    {
        var copy = from.Bitmap.Copy();
        for (var y = 0; y < copy.Height; y++)
            for (var x = 0; x < copy.Width; x++)
                if (change(x, y))
                {
                    var c = copy.GetPixel(x, y);
                    copy.SetPixel(x, y, new SKColor((byte)(c.Red ^ 0x40), (byte)(c.Green ^ 0x40), (byte)(c.Blue ^ 0x40)));
                }
        return TuringSerialProtocolTests.Picture(copy.Width, copy.Height, 0, (_, _) => true, (copy, [], []));
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void StartFramesBrightnessAndStop_Trace_IsTheLibrarys(string usb, char letter, int width, int height)
    {
        var candidate = CandidateOf(usb);
        var hello = letter == 'C' ? "chs_5inch.dev1_rom1.88" : "chs_88inch.dev1_rom1.88";
        var (devices, device) = Plug(candidate, hello);
        var revision = SerialRevision.Of(letter.ToString())!;
        var first = TuringSerialProtocolTests.Picture(width, height, 7);
        var second = Changed(first, (x, y) => x >= 13 && x < 70 && y >= 31 && y < 60);     // a few sectors
        var third = Changed(second, (_, _) => true);                                         // every sector: past the cap
        byte[] Frame((SKBitmap, byte[] Rgb565, byte[] Bgr) p) => letter == 'A' ? p.Rgb565 : p.Bgr;

        // TuringPanelSerialTask.cs, fed the same frames: ScreenFactory.Create (:101, Open and, for C and E, Hello), ScreenOn
        // (:113), SetBrightness (:115); the first frame whole (:133-137); a brightness change (:128-132); the second frame's
        // changed sectors, each at its place (:141-154); the third, past the cap, whole (:144-148); the stop's ScreenOff
        // (:194) and the port's close (the screen's Dispose). Revision A's whole frame goes as bands of 16 rows, each its
        // own bitmap command (DisplayBuffer at the band's place), so that a write keeps within a hosted package's deadline
        // (300 KB is about 27 s at 115200 baud; a band, 10 KB, under a second); C's and E's as the 80 x 80 blocks of the
        // library's partial updates (DisplayBuffer at each block's place), never the whole-screen command.
        using var oracle = V1Screen.Make(letter);
        oracle.Port.Respond = w => w[0] == 0x01 && w[1] == 0xef ? Hello(hello) : w[0] == 0xcf ? Encoding.ASCII.GetBytes("needReSend:0") : null;
        void Whole(SKBitmap bitmap)
        {
            if (letter != 'A')
            {
                for (var y = 0; y < height; y += 80)
                    for (var x = 0; x < width; x += 80)
                        Assert.True(oracle.DisplayBuffer(bitmap, x, y, Math.Min(80, width - x), Math.Min(80, height - y)));
                return;
            }
            for (var y = 0; y < height; y += 16)
                Assert.True(oracle.DisplayBuffer(bitmap, 0, y, width, Math.Min(16, height - y)));
        }
        oracle.Open();
        oracle.ScreenOn();
        oracle.SetBrightness(70);
        Whole(first.Bitmap);
        oracle.SetBrightness(30);
        var sectors = V1SKBitmapComparison.GetChangedSectors(first.Bitmap, second.Bitmap, revision.SectorWidth, revision.SectorHeight, revision.MaxSectorWidth, revision.MaxSectorHeight);
        Assert.InRange(sectors.Count, 1, revision.MaxSectors);
        foreach (var sector in sectors)
            oracle.DisplayBuffer(second.Bitmap, sector.Left, sector.Top, sector.Width, sector.Height);
        var all = V1SKBitmapComparison.GetChangedSectors(second.Bitmap, third.Bitmap, revision.SectorWidth, revision.SectorHeight, revision.MaxSectorWidth, revision.MaxSectorHeight);
        if (all.Count > revision.MaxSectors)
            Whole(third.Bitmap);
        else // the 2.1": its 480 x 480 cut into at most 128 x 96 is 20 sectors, under its cap
            foreach (var sector in all)
                oracle.DisplayBuffer(third.Bitmap, sector.Left, sector.Top, sector.Width, sector.Height);
        Assert.Equal(width * height == 480 * 480, all.Count <= revision.MaxSectors);
        oracle.ScreenOff();

        var driver = Driver(devices);
        var session = Start(driver, candidate);
        var wakes = letter == 'A' ? 2 * TuringDriver.WakeRounds : 0; // the start's wake: five opens and closes
        var stop = TestContext.Current.CancellationToken;
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(Frame(first), revision.Format, 1, false), stop).Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(30));
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(Frame(second), revision.Format, 2, false), stop).Status);
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(Frame(second), revision.Format, 2, true), stop).Status); // a keep-alive: nothing new
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(Frame(third), revision.Format, 3, false), stop).Status);
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        var entries = device.Trace.Entries.Skip(wakes).ToList();
        if (letter != 'A')
            entries = [.. entries.SkipWhile(e => e.Op != PanelTraceOp.Close).Skip(1)]; // the probe's Hello, on its own handle
        var writes = entries.Where(e => e.Op == PanelTraceOp.SerialWrite).Select(e => Hex(e.Data)).ToList();
        Assert.Equal(oracle.Port.Writes.Count, writes.Count);
        Assert.Equal(oracle.Port.Writes.Select(Hex), writes);
        Assert.Equal(PanelTraceOp.Open, entries[0].Op);
        Assert.Equal(new PanelSerialOptions(115200, true, true), device.Serial.Options);
        Assert.Equal(PanelTraceOp.SerialDiscard, entries[1].Op);
        Assert.Equal(PanelTraceOp.Close, entries[^1].Op);
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Theory]
    [InlineData('C')]
    [InlineData('E')]
    public void ASectorThePanelAsksForAgain_MakesTheNextFrameWhole(char letter)
    {
        // TuringPanelSerialTask.cs:133-137,151-154: DisplayBuffer's answer is kept (the last sector's), and a false one
        // has the next frame sent whole.
        var usb = letter == 'C' ? "1d6b:0106" : "0525:a4a7";
        var (width, height) = letter == 'C' ? (800, 480) : (480, 1920);
        var resend = false;
        var (devices, device) = Plug(CandidateOf(usb), letter == 'C' ? "chs_5inch" : "chs_88inch", () => resend ? "needReSend:1" : "needReSend:0");
        var session = Start(Driver(devices), CandidateOf(usb));
        var first = TuringSerialProtocolTests.Picture(width, height, 8);
        var second = Changed(first, (x, y) => x < 10 && y < 10);
        var third = Changed(second, (x, y) => x < 10 && y < 10);
        var stop = TestContext.Current.CancellationToken;

        session.WriteFrame(new PanelFrameView(first.Bgr, PanelFrameFormat.Bgr888, 1, false), stop);
        resend = true;
        session.WriteFrame(new PanelFrameView(second.Bgr, PanelFrameFormat.Bgr888, 2, false), stop);
        resend = false;
        var before = device.Trace.Entries.Count;
        session.WriteFrame(new PanelFrameView(third.Bgr, PanelFrameFormat.Bgr888, 3, false), stop);

        // A whole frame: every 80 x 80 block of the screen, each its UpdateBitmap (the change alone would be one).
        var blocks = device.Trace.Entries.Skip(before).Count(e => e.Op == PanelTraceOp.SerialWrite && e.Data is [0xcc, 0xef, 0x69, ..]);
        Assert.Equal(width / 80 * (height / 80), blocks);
        session.Dispose();
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void Capabilities_AreTheModelsSize_AndItsRevisionsPixels(string usb, char letter, int width, int height)
    {
        // A: RGB565 little-endian, truncated (ScreenBufferRgb353); C and E: BGR (ScreenBufferBgr888, and E's Bgra8888 with
        // 255 after each pixel, added by the driver); at most 30 fps (1.4.x's Target FPS slider's top); brightness 0-100.
        var (devices, _) = Plug(CandidateOf(usb), letter == 'C' ? "chs_5inch" : "chs_88inch");
        using var session = Start(Driver(devices), CandidateOf(usb));
        var caps = session.Capabilities;

        Assert.Equal((width, height, 30), (caps.Width, caps.Height, caps.MaxFps));
        Assert.Equal(letter == 'A' ? PanelFrameFormat.Rgb565LeTruncated : PanelFrameFormat.Bgr888, Assert.Single(caps.Formats));
        Assert.Equal(new PanelRange(0, 100), caps.Brightness);
        Assert.False(caps.RotatesItself);
    }

    // ---- The XuanFang, and "Try it as…" ----

    private static JsonElement Settings(string? model) =>
        JsonDocument.Parse(model == null ? "{}" : $$"""{"model":"{{model}}"}""").RootElement.Clone();

    [Fact]
    public void TheXuanFang_IsntDriven_UntilItIsTriedAsRevisionA()
    {
        // TuringPanelSerialTask.cs:70-71: any model but the four throws, so 1.4.x never drove the XuanFang 3.5". A panel
        // saved as one (a 1.x import) answers notSupported; its model setting may try it as the Turing 3.5" (revision
        // A), and as nothing else.
        var (devices, device) = Plug(ThreeAndAHalf);
        var driver = Driver(devices);
        var xuanFang = new PanelIdentity("xuanfang35", 320, 480, null, null);
        var before = device.Calls;

        var refused = driver.Open(ThreeAndAHalf, xuanFang, new PanelOpenOptions(70), TestContext.Current.CancellationToken);
        var other = driver.Open(ThreeAndAHalf, xuanFang, new PanelOpenOptions(70, FamilySettings: Settings("rev5inch")), TestContext.Current.CancellationToken);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (refused.Session, refused.Status));
        Assert.Contains("XuanFang", refused.Message);
        Assert.Equal((null, PanelDeviceStatus.NotSupported), (other.Session, other.Status));
        Assert.Equal(before, device.Calls);

        var tried = driver.Open(ThreeAndAHalf, xuanFang, new PanelOpenOptions(70, FamilySettings: Settings("turing35")), TestContext.Current.CancellationToken);
        Assert.NotNull(tried.Session);
        Assert.Equal((320, 480, PanelFrameFormat.Rgb565LeTruncated), (tried.Session!.Capabilities.Width, tried.Session.Capabilities.Height, tried.Session.Capabilities.Formats[0]));
        tried.Session.Dispose();
    }

    // ---- The stop ----

    [Theory]
    [MemberData(nameof(Sessions))]
    public void AFrameWhoseDeviceGoes_StopsThere_AndTheStopSendsNothing(string usb, char letter, int width, int height)
    {
        var (devices, device) = Plug(CandidateOf(usb), letter == 'C' ? "chs_5inch" : "chs_88inch");
        var session = Start(Driver(devices), CandidateOf(usb));
        device.UnplugAt(device.Calls + 2);
        var picture = TuringSerialProtocolTests.Picture(width, height, 9);

        var write = session.WriteFrame(new PanelFrameView(letter == 'A' ? picture.Rgb565 : picture.Bgr, SerialRevision.Of(letter.ToString())!.Format, 1, false), TestContext.Current.CancellationToken);
        var calls = device.Calls;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        Assert.Equal(PanelDeviceStatus.Gone, write.Status);
        Assert.Equal(calls, device.Calls);
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Fact]
    public void TheStop_TurnsTheScreenOff_WithinItsBudget()
    {
        // TuringPanelSerialTask.cs:194: ScreenOff in the loop's finally; A's is its command 108.
        var (devices, device) = Plug(ThreeAndAHalf);
        var session = Start(Driver(devices), ThreeAndAHalf);

        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();
        Assert.Equal(["h6 serial> 00000000006c", "h6 close serial"], SerialLines(device)[^2..]);

        var (quietDevices, quiet) = Plug(ThreeAndAHalf);
        var spent = Start(Driver(quietDevices), ThreeAndAHalf);
        var before = quiet.Calls;
        spent.Close(screenOff: true, TimeSpan.Zero);
        spent.Dispose();
        Assert.Equal(before, quiet.Calls);
    }

    // ---- A frame's write keeps within a hosted package's deadline ----

    /// <summary>The longest a hosted package's write may take: the engine's own bound, past which the session is ended.</summary>
    private static readonly double HostDeadlineMs = new InfoPanel.Panels.Hosted.HostedPanelOptions().WriteDeadline.TotalMilliseconds;

    /// <summary>Revision A's screen as its writes left it: each bitmap command's pixels between its corners.</summary>
    private static byte[] ScreenA(IEnumerable<PanelTraceEntry> entries, int width, int height)
    {
        var screen = new byte[width * height * 2];
        var writes = entries.Where(e => e.Op == PanelTraceOp.SerialWrite && e.Status == PanelDeviceStatus.Ok).Select(e => e.Data).ToList();
        for (var i = 0; i < writes.Count; i++)
        {
            var h = writes[i];
            if (h.Length != 6 || h[5] != RevisionAScreen.BitmapCommand)
                continue;
            var x = (h[0] << 2) | (h[1] >> 6);
            var y = ((h[1] & 63) << 4) | (h[2] >> 4);
            var ex = ((h[2] & 15) << 6) | (h[3] >> 2);
            var ey = ((h[3] & 3) << 8) | h[4];
            var w = ex - x + 1;
            var pixels = writes[++i];
            Assert.Equal(w * (ey - y + 1) * 2, pixels.Length);
            for (var row = 0; row <= ey - y; row++)
                pixels.AsSpan(row * w * 2, w * 2).CopyTo(screen.AsSpan((((y + row) * width) + x) * 2));
        }
        return screen;
    }

    /// <summary>
    /// The frames written, each as a new frame once (<paramref name="frames"/>, the next given after <paramref name="every"/>
    /// writes) and then again as the engine's keep-alive until a write sends nothing; each write's time on the seam's clock.
    /// </summary>
    private static List<double> WriteUntilShown(IPanelSession session, Seam seam, FakePanelDevice device, PanelFrameFormat format, IReadOnlyList<byte[]> frames, int every)
    {
        var stop = TestContext.Current.CancellationToken;
        var times = new List<double>();
        var given = 0;
        for (var i = 0; i < 200; i++)
        {
            var isNew = given < frames.Count && i == given * every;
            var frame = frames[Math.Max(0, isNew ? given : given - 1)];
            var before = (seam.Now, device.Trace.Entries.Count);
            var write = session.WriteFrame(new PanelFrameView(frame, format, given + (isNew ? 1 : 0), repeat: !isNew), stop);
            Assert.Equal(PanelDeviceStatus.Ok, write.Status);
            if (isNew)
                given++;
            else if (device.Trace.Entries.Count == before.Item2)
                return times; // a keep-alive that sent nothing: the panel shows the frame
            times.Add(seam.Now - before.Item1);
        }
        Assert.Fail("the panel never caught up");
        return times;
    }

    [Fact]
    public void RevisionA_AWholeFrame_GoesOverSeveralWrites_EachWithinTheHostsDeadline_AndThePanelEndsWithTheNewestFrame()
    {
        // A whole 320 x 480 frame is 300 KB, about 27 s at 115200 baud: a hosted package's write ends at 5 s, and a stop
        // waits for the write under way. The session starts bands for 1.2 s (two bands, about 1.8 s), keeps what it sent,
        // and the next write (a new frame, or the engine's keep-alive a second later) sends the rest, then the sectors the
        // newer frame changed.
        var (devices, device) = Plug(ThreeAndAHalf);
        var seam = new Seam(devices) { BytesPerSecond = 115200 / 10.0 };
        var driver = new TuringDriver(seam) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Milliseconds = () => (long)seam.Now };
        using var session = Start(driver, ThreeAndAHalf);
        var first = TuringSerialProtocolTests.Picture(320, 480, 11);
        var second = Changed(first, (x, y) => (x / 50 + y / 70) % 2 == 0); // a new frame while the first is under way
        var from = device.Trace.Entries.Count;

        var times = WriteUntilShown(session, seam, device, PanelFrameFormat.Rgb565LeTruncated, [first.Rgb565, second.Rgb565], every: 2);

        Assert.True(times.Count >= 15, $"{times.Count} writes"); // 27 s of a first frame in writes of about 1.8 s
        Assert.All(times, t => Assert.InRange(t, 1, HostDeadlineMs - 1000));
        Assert.All(times, t => Assert.InRange(t, 1, StopAllowanceMs - 500));
        Assert.Equal(second.Rgb565, ScreenA(device.Trace.Entries.Skip(from), 320, 480));
    }

    [Fact]
    public void The2_1_AWholeFramesBlocks_GoOverSeveralWrites_EachWithinTheHostsDeadline_AsTheLibrarySentThem()
    {
        // The 2.1"'s 480 x 480 frame is never whole to its revision E screen (480 x 1920): it goes as 36 blocks of 80 x 80,
        // each answered, and its answer's read ends only when a read brings nothing in 1000 ms: about a second a block over
        // a USB serial port (here 1 MB/s). The session starts blocks for 1.2 s (two blocks, about 2 s); the rest go with
        // the next writes, the panel getting, in all, the library's bytes for the frame.
        var candidate = CandidateOf("1d6b:0121");
        var (devices, device) = Plug(candidate, "chs_21inch.dev1");
        var seam = new Seam(devices) { BytesPerSecond = 1_000_000 };
        var driver = new TuringDriver(seam) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Milliseconds = () => (long)seam.Now };
        using var session = Start(driver, candidate);
        var picture = TuringSerialProtocolTests.Picture(480, 480, 12);
        var from = device.Trace.Entries.Count;

        var times = WriteUntilShown(session, seam, device, PanelFrameFormat.Bgr888, [picture.Bgr], every: 1);

        using var oracle = V1Screen.Make('E');
        oracle.Port.Respond = w => w[0] == 0xcf ? Encoding.ASCII.GetBytes("needReSend:0") : null;
        Assert.True(oracle.DisplayBuffer(picture.Bitmap, 0, 0, 480, 480));
        Assert.True(times.Count >= 18, $"{times.Count} writes");
        Assert.All(times, t => Assert.InRange(t, 1, HostDeadlineMs - 1000));
        Assert.All(times, t => Assert.InRange(t, 1, StopAllowanceMs - 500));
        Assert.Equal(oracle.Port.Writes.Select(Hex), device.Trace.Entries.Skip(from).Where(e => e.Op == PanelTraceOp.SerialWrite).Select(e => Hex(e.Data)));
    }

    /// <summary>
    /// The engine's bound for a stop, from its ask to the screen-off (<c>PanelRunner.Close</c>): a write under way when a
    /// hosted panel is asked to stop is waited for (the host's close takes the session's turn), so a write and the
    /// screen-off after it must fit in it.
    /// </summary>
    private static readonly double StopAllowanceMs = InfoPanel.Panels.BeadaPanel.BeadaPanelSession.ScreenOffBound.TotalMilliseconds;

    [Theory]
    [InlineData("1d6b:0106", "chs_5inch.dev1_rom1.88", 800, 480, 3)]    // C, the 5"
    [InlineData("0525:a4a7", "chs_88inch.dev1_rom1.88", 480, 1920, 4)]  // E, the 8.8" Rev 1.0
    public void ANativeWholeFrame_GoesAsBlocksOverSeveralWrites_EachWithinTheHostsDeadlineAndTheStopsBound_AndThePanelEndsWithTheNewestFrame(string usb, string hello, int width, int height, int pixelBytes)
    {
        // A frame of the screen's own size was one command to the library (0x2c, the display command, the payload, then
        // PreUpdate and QueryStatus, each answer's read ending only when a read brings nothing in 1000 ms): the 8.8"'s
        // 3.7 MB about 5.7 s at 1 MB/s, past a hosted write's 5 s. It goes as the 80 x 80 blocks the library's partial
        // updates use, each answered (about a second a block), a few to a write; a newer frame given while it is under
        // way is what the rest are sent in, and the sectors already sent that it changed follow.
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate, hello);
        var seam = new Seam(devices) { BytesPerSecond = 1_000_000 };
        var driver = new TuringDriver(seam) { Wait = (_, stop) => stop.ThrowIfCancellationRequested(), Milliseconds = () => (long)seam.Now };
        using var session = Start(driver, candidate);
        var first = TuringSerialProtocolTests.Picture(width, height, 14);
        var second = Changed(first, (x, y) => x < 100 && y < 100);
        var from = device.Trace.Entries.Count;

        var times = WriteUntilShown(session, seam, device, PanelFrameFormat.Bgr888, [first.Bgr, second.Bgr], every: 2);

        Assert.All(times, t => Assert.InRange(t, 1, HostDeadlineMs - 1000));
        Assert.All(times, t => Assert.InRange(t, 1, StopAllowanceMs - 500));
        var writes = device.Trace.Entries.Skip(from).Where(e => e.Op == PanelTraceOp.SerialWrite && e.Status == PanelDeviceStatus.Ok).Select(e => e.Data).ToList();
        Assert.DoesNotContain(writes, w => w.All(b => b == 0x2c)); // no whole-screen command's start
        Assert.Equal(second.Bgr, TuringSerialProtocolTests.ScreenCe(writes, width, width, height, pixelBytes));
    }

    [Fact]
    public async Task AStopAskedAsAHostedWriteBegins_TurnsTheScreenOff_WithinTheStopsBound()
    {
        // The engine asks a panel to stop (PanelRunner.RequestStop) and gives its screen-off 3 s from the ask
        // (PanelRunner.Close). A hosted package's write gets the host's token, not the stop, and the host's close waits for
        // the write under way (PanelHostServer.Close): a write's sending, its last piece and the screen-off must fit in
        // those 3 s. Revision A at 115200 baud, on the real clock: its whole first frame, 30 bands of about 0.9 s, the stop
        // asked as the write's first band begins.
        var (devices, device) = Plug(ThreeAndAHalf);
        using var begun = new ManualResetEventSlim();
        var seam = new Seam(devices)
        {
            BytesPerSecond = 115200 / 10.0,
            RealTime = true,
            OnWrite = data =>
            {
                if (data.Length == 6 && data[5] == RevisionAScreen.BitmapCommand)
                    begun.Set();
            },
        };
        var driver = new TuringDriver(seam) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };
        var slots = Directory.CreateTempSubdirectory("panel-slots-").FullName;
        var log = Serilog.Core.Logger.None;
        var host = await InfoPanel.Core.Tests.Panels.Hosted.TestHost.StartAsync(driver, log);
        try
        {
            var link = new InfoPanel.Core.Tests.Panels.Hosted.TestLink(slots) { Current = host.Connection };
            var family = new InfoPanel.Panels.PanelPackageFamily(TuringDriver.FamilyId, "Turing", [new PanelMatch(0x1A86, 0x5722)],
                [new InfoPanel.Panels.PanelPackageModel("turing35", "Turing Smart Screen 3.5\"", 320, 480, null)]);
            var hosted = new InfoPanel.Panels.Hosted.HostedPanelDriver(family, link, log);
            var open = hosted.Open(ThreeAndAHalf, new PanelIdentity("turing35", 320, 480, null, null), new PanelOpenOptions(70), TestContext.Current.CancellationToken);
            Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
            var session = open.Session!;
            using var stop = new CancellationTokenSource();
            var frame = TuringSerialProtocolTests.Picture(320, 480, 15).Rgb565;

            var writing = Task.Run(() => session.WriteFrame(new PanelFrameView(frame, PanelFrameFormat.Rgb565LeTruncated, 1, false), stop.Token), TestContext.Current.CancellationToken);
            Assert.True(begun.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var askedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            session.Close(screenOff: true, InfoPanel.Panels.BeadaPanel.BeadaPanelSession.ScreenOffBound - System.Diagnostics.Stopwatch.GetElapsedTime(askedAt));
            session.Dispose();

            var off = seam.Ends.Where(e => e.Length == 6 && e.Last == RevisionAScreen.ScreenOffCommand).ToList();
            Assert.True(off.Count == 1, $"{off.Count} screen-offs, {seam.Ends.Count} writes");
            var at = System.Diagnostics.Stopwatch.GetElapsedTime(askedAt, off[0].At).TotalMilliseconds;
            Assert.True(at < StopAllowanceMs, $"the screen went off {at:0} ms after the ask");
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(slots);
        }
    }

    // ---- A read that finds the device gone ----

    [Theory]
    [InlineData("1d6b:0106", 800, 480, 0xcf)]  // C's whole frame: its first block's QueryStatus's answer
    [InlineData("0525:a4a7", 480, 1920, 0xcf)] // E's whole frame: its first block's QueryStatus's answer
    [InlineData("1d6b:0121", 480, 480, 0xcf)]  // the 2.1"'s first block: its QueryStatus's answer
    public void AnUnplugAtAnAnswersRead_EndsTheFrameThere_AndNothingMoreIsSent(string usb, int width, int height, byte command)
    {
        var candidate = CandidateOf(usb);
        var (devices, device) = Plug(candidate, usb == "1d6b:0106" ? "chs_5inch" : "chs_88inch");
        var seam = new Seam(devices) { UnplugAtTheAnswerTo = command, Unplug = device.Unplug };
        var session = Start(Driver(seam), candidate);
        var picture = TuringSerialProtocolTests.Picture(width, height, 13);

        var write = session.WriteFrame(new PanelFrameView(picture.Bgr, PanelFrameFormat.Bgr888, 1, false), TestContext.Current.CancellationToken);
        var calls = device.Calls;
        session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        session.Dispose();

        Assert.Equal(PanelDeviceStatus.Gone, write.Status);
        var gone = device.Trace.Entries.ToList().FindIndex(e => e.Op == PanelTraceOp.SerialRead && e.Status == PanelDeviceStatus.Gone);
        Assert.True(gone > 0);
        Assert.DoesNotContain(device.Trace.Entries.Skip(gone), e => e.Op == PanelTraceOp.SerialWrite);
        Assert.Equal(calls, device.Calls);
    }

    // ---- The conformance suite ----

    /// <summary>
    /// The suite's frames, each one different in every byte from the last: a serial session sends only what changed since
    /// the frame before (nothing for the same frame), and the suite times each frame's transfer.
    /// </summary>
    private static Func<PanelFrameFormat, int, int, byte[]> Varying()
    {
        var count = 0;
        return (format, width, height) =>
        {
            var frame = ConformanceFrames.Of(format, width, height);
            var key = (byte)((Interlocked.Increment(ref count) % 255) + 1);
            for (var i = 0; i < frame.Length; i++)
                frame[i] ^= key;
            return frame;
        };
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void TheDriver_PassesTheConformanceSuite(string usb, char letter, int width, int height)
    {
        var candidate = CandidateOf(usb);
        var hello = letter == 'C' ? "chs_5inch" : "chs_88inch";
        var helloWrite = new byte[250];
        RevisionCeScreen.CommandHello.CopyTo(helloWrite, 0);
        var failures = Conformance.Check(() =>
        {
            var (devices, device) = Plug(candidate, hello);
            return new ConformanceSetup(Driver(devices), candidate, device) { ProbeWrite = letter == 'A' ? null : Conformance.ProbeWrites(helloWrite) };
        }, new ConformanceOptions { Frame = Varying() });

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        _ = (width, height);
    }
}
