using Xunit;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels.BeadaPanel;

/// <summary>
/// A BeadaPanel session over the fake backend and its clock: the full transcript of a start, frames and a stop, as
/// InfoPanel 1.4's <c>InfoPanel/Services/BeadaPanelDeviceTask.cs</c> writes it: wait 300 ms (line 89); open, set
/// configuration 1, claim interface 0 (108-112); GetPanelInfo on 0x02 and 100 bytes read on 0x82 (114-127); PanelLinkReset
/// on 0x02 (174); wait 1000 ms (176); SetBacklight on 0x02 (178-182); the stream's start on 0x01 (152-156, 185);
/// frames on 0x01 (258); brightness re-sent between frames when it changed (245-250); on stop SetBacklight raw 0, a black
/// frame and the stream's end (291-301).
/// </summary>
public class BeadaPanelSessionTests
{
    private static readonly UsbDeviceId PanelId = new(0x4E58, 0x1001, 1, 4, "1-2");

    private static (FakeUsbBackend Backend, FakeUsbDevice Device) Panel(byte[] reply, int configuration = 1)
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(PanelId) { Reply = reply, Configuration = configuration, UsbSerial = "934072005c" });
        return (backend, device);
    }

    private static BeadaPanelSession Started(FakeUsbBackend backend, int brightness = 100)
    {
        var start = BeadaPanelSession.Start(backend, PanelId, brightness, backend.Clock, CancellationToken.None);
        Assert.Equal(UsbStatus.Ok, start.Status);
        return start.Session!;
    }

    private static byte[] Frame(int length, byte fill)
    {
        var frame = new byte[length];
        Array.Fill(frame, fill);
        frame[0] = 0x01; // not uniform, so a frame can't pass for another
        return frame;
    }

    public static TheoryData<byte, byte, byte, ushort, ushort, byte[], byte[], byte, int> Modes => new()
    {
        // PanelLink 2, write-through (platform 1): model 5 (code 0) at its 800 × 480, not the reported 1024 × 600.
        { 2, 1, 0, 1024, 600, V.StartMediaStream800x480, V.EndMediaStream, 50, 800 * 480 * 2 },
        // PanelLink 1, write-through (platform 2): LegacyCommand1 and 2, brightness 50 → 62.
        { 1, 2, 0, 1024, 600, V.LegacyCommand1_800x480, V.LegacyCommand2, 62, 800 * 480 * 2 },
        // PanelLink 2, platform 0: the reported 480 × 320, RGB16.
        { 2, 0, 3, 480, 320, V.StartMediaStream480x320, V.EndMediaStream, 50, 480 * 320 * 2 },
        // PanelLink 1, platform 0.
        { 1, 0, 3, 480, 320, V.LegacyCommand1_480x320, V.LegacyCommand2, 62, 480 * 320 * 2 },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_start_two_frames_and_a_stop_are_byte_exact(byte panelLink, byte platform, byte modelCode, ushort resX, ushort resY,
        byte[] start, byte[] end, byte backlight50, int frameLength)
    {
        var reply = V.InfoReply(panelLink, platform, modelCode, resX, resY);
        var (backend, device) = Panel(reply);

        var session = Started(backend, brightness: 50);
        Assert.Equal(frameLength, session.FrameLength);
        var one = Frame(frameLength, 0x11);
        var two = Frame(frameLength, 0x22);
        Assert.Equal(UsbStatus.Ok, session.WriteFrame(one));
        Assert.Equal(UsbStatus.Ok, session.WriteFrame(two));
        session.Stop();
        session.Dispose();

        FakeTransfer[] expected =
        [
            new(300, 0x02, V.GetPanelInfo),
            new(300, 0x82, reply),
            new(300, 0x02, V.PanelLinkReset),
            new(1300, 0x02, V.SetBacklight(backlight50)),
            new(1300, 0x01, start),
            new(1300, 0x01, one),
            new(1300, 0x01, two),
            new(1300, 0x02, V.SetBacklight(0)),
            new(1300, 0x01, new byte[frameLength]),
            new(1300, 0x01, end),
        ];
        AssertTranscript(expected, device.Transfers);
        Assert.Equal(["open", "getConfiguration", "claim 0", "release 0", "close"], device.Calls);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void The_waits_come_from_the_clock_and_writes_take_their_time()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.WriteDelay = TimeSpan.FromMilliseconds(7);
        var session = Started(backend);
        session.WriteFrame(Frame(session.FrameLength, 0x33));
        session.Stop();

        // 300 ms before the info; 7 ms per write; 1000 ms after the reset.
        long[] at = [.. device.Transfers.Select(t => t.AtMs)];
        Assert.Equal([300, 307, 307, 1314, 1321, 1328, 1335, 1342, 1349], at);
    }

    [Fact]
    public void Brightness_is_sent_between_frames_only_when_it_changed()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        var session = Started(backend, brightness: 100);
        var frame = Frame(session.FrameLength, 0x44);

        session.WriteFrame(frame);
        Assert.Equal(UsbStatus.Ok, session.SetBrightness(100)); // unchanged: nothing sent
        Assert.Equal(UsbStatus.Ok, session.SetBrightness(40));
        Assert.Equal(UsbStatus.Ok, session.SetBrightness(40));
        session.WriteFrame(frame);
        session.Stop();

        var afterStart = device.Transfers.Skip(5).ToArray();
        AssertTranscript(
        [
            new(1300, 0x01, frame),
            new(1300, 0x02, V.SetBacklight(40)),
            new(1300, 0x01, frame),
            new(1300, 0x02, V.SetBacklight(0)),
            new(1300, 0x01, new byte[session.FrameLength]),
            new(1300, 0x01, V.EndMediaStream),
        ], afterStart);
    }

    [Fact]
    public void Panel_link_1_maps_brightness_between_frames_too()
    {
        var (backend, device) = Panel(V.InfoReply(1, 1, 0, 800, 480));
        var session = Started(backend, brightness: 100);
        session.SetBrightness(0);
        session.Stop();
        // 0 → 25 for PanelLink 1 while running; the stop's backlight is raw 0 on every version (line 295).
        Assert.Equal(V.SetBacklight(100), device.Transfers[3].Data);
        Assert.Equal(V.SetBacklight(25), device.Transfers[5].Data);
        Assert.Equal(V.SetBacklight(0), device.Transfers[6].Data);
    }

    [Fact]
    public void A_failed_frame_write_reports_it_and_the_stop_still_turns_the_screen_off()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.FailWrite(5, UsbStatus.Io); // writes: info, reset, backlight, start, then the first frame
        var session = Started(backend);
        var frame = Frame(session.FrameLength, 0x55);

        Assert.Equal(UsbStatus.Io, session.WriteFrame(frame));
        session.Stop();
        session.Dispose();

        AssertTranscript(
        [
            new(1300, 0x01, frame),
            new(1300, 0x02, V.SetBacklight(0)),
            new(1300, 0x01, new byte[session.FrameLength]),
            new(1300, 0x01, V.EndMediaStream),
        ], device.Transfers.Skip(5).ToArray());
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void An_unplug_mid_stream_skips_the_rest_of_the_stop()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.UnplugAtWrite = 6; // the second frame
        var session = Started(backend);
        var frame = Frame(session.FrameLength, 0x66);

        Assert.Equal(UsbStatus.Ok, session.WriteFrame(frame));
        Assert.Equal(UsbStatus.NoDevice, session.WriteFrame(frame));
        session.Stop();
        session.Dispose();

        // The backlight write fails (gone), so the black frame and the end aren't tried.
        Assert.Equal(7, device.Transfers.Length);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void Stop_runs_once()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        var session = Started(backend);
        session.Stop();
        session.Stop();
        session.Dispose();
        Assert.Equal(8, device.Transfers.Length);
    }

    [Fact]
    public void No_zero_length_packet_is_ever_written()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        var session = Started(backend);
        session.WriteFrame(Frame(session.FrameLength, 0x77));
        // A frame of any other length is refused before anything is written.
        var before = device.Transfers.Length;
        Assert.Throws<ArgumentException>(() => session.WriteFrame([]));
        Assert.Throws<ArgumentException>(() => session.WriteFrame(new byte[session.FrameLength - 2]));
        Assert.Equal(before, device.Transfers.Length);
        session.Stop();
        Assert.All(device.Transfers.Where(t => !t.In), t => Assert.NotEmpty(t.Data));
    }

    [Fact]
    public void Configuration_1_is_set_only_when_the_device_has_none()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480), configuration: 0);
        var session = Started(backend);
        session.Dispose();
        Assert.Equal(["open", "getConfiguration", "setConfiguration 1", "claim 0", "release 0", "close"], device.Calls);
    }

    [Theory]
    [InlineData(UsbStatus.Busy)]
    [InlineData(UsbStatus.Access)]
    public void A_failed_claim_is_reported_and_nothing_is_written(UsbStatus claim)
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.ClaimResult = claim;

        var start = BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, CancellationToken.None);

        Assert.Equal(claim, start.Status);
        Assert.Null(start.Session);
        Assert.Empty(device.Transfers);
        Assert.Equal(["open", "getConfiguration", "claim 0", "close"], device.Calls);
        Assert.Equal(0, device.OpenHandles);
    }

    [Theory]
    [InlineData(UsbStatus.Busy)]
    [InlineData(UsbStatus.Access)]
    public void A_device_that_wont_open_is_reported_and_nothing_is_written(UsbStatus open)
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.OpenResult = open;
        var start = BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, CancellationToken.None);
        Assert.Equal(open, start.Status);
        Assert.Empty(device.Transfers);
    }

    [Fact]
    public void A_panel_whose_reply_doesnt_parse_isnt_started()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 1, 800, 480)); // model code 1: no table entry
        var start = BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, CancellationToken.None);
        Assert.Equal(UsbStatus.NotSupported, start.Status);
        Assert.Null(start.Session);
        Assert.Equal(2, device.Transfers.Length); // the query and its reply, nothing after
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void A_panel_that_doesnt_answer_times_out()
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(PanelId)); // no reply
        var start = BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, CancellationToken.None);
        Assert.Equal(UsbStatus.Timeout, start.Status);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void A_gone_panel_is_no_device()
    {
        var backend = new FakeUsbBackend();
        var start = BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, CancellationToken.None);
        Assert.Equal(UsbStatus.NoDevice, start.Status);
    }

    [Fact]
    public void Identify_queries_the_info_and_the_descriptors_serial_then_lets_go()
    {
        var reply = V.InfoReply(2, 1, 24, 1920, 480, serial: "934072005C");
        var (backend, device) = Panel(reply);

        var identity = BeadaPanelSession.Identify(backend, PanelId);

        Assert.Equal(UsbStatus.Ok, identity.Status);
        Assert.Equal("Y", identity.Info!.Model.Id);
        Assert.Equal("934072005c", identity.UsbSerial);
        AssertTranscript([new(0, 0x02, V.GetPanelInfo), new(0, 0x82, reply)], device.Transfers);
        Assert.Equal(["open", "getConfiguration", "claim 0", "readSerial", "release 0", "close"], device.Calls);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void Identify_reports_an_unknown_model_with_its_code()
    {
        var (backend, _) = Panel(V.InfoReply(2, 1, 1, 800, 480));
        var identity = BeadaPanelSession.Identify(backend, PanelId);
        Assert.Equal(UsbStatus.NotSupported, identity.Status);
        Assert.Equal(BeadaPanelInfoError.UnknownModel, identity.Error);
        Assert.Equal(1, identity.ModelCode);
    }

    [Theory]
    [InlineData(UsbStatus.Busy)]
    [InlineData(UsbStatus.Access)]
    public void Identify_on_a_held_panel_writes_nothing(UsbStatus claim)
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        device.ClaimResult = claim;
        var identity = BeadaPanelSession.Identify(backend, PanelId);
        Assert.Equal(claim, identity.Status);
        Assert.Empty(device.Transfers);
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void A_cancelled_start_opens_nothing()
    {
        var (backend, device) = Panel(V.InfoReply(2, 1, 0, 800, 480));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BeadaPanelSession.Start(backend, PanelId, 100, backend.Clock, cancelled.Token));
        Assert.Empty(device.Calls);
    }

    private static void AssertTranscript(FakeTransfer[] expected, FakeTransfer[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Endpoint == actual[i].Endpoint, $"transfer {i}: endpoint 0x{actual[i].Endpoint:x2}, expected 0x{expected[i].Endpoint:x2}");
            Assert.True(expected[i].AtMs == actual[i].AtMs, $"transfer {i}: at {actual[i].AtMs} ms, expected {expected[i].AtMs} ms");
            Assert.True(expected[i].Data.AsSpan().SequenceEqual(actual[i].Data), $"transfer {i}: bytes differ (length {actual[i].Data.Length}, expected {expected[i].Data.Length})");
        }
    }
}
