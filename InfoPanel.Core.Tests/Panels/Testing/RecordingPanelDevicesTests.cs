using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The hardware tests' recorder (<see cref="RecordingPanelDevices"/>): it passes every call to the seam it wraps and
/// writes each, with what it came to, as the kit's trace, so a tester's run reads back as the calls the driver made.
/// </summary>
public sealed class RecordingPanelDevicesTests
{
    private static readonly PanelCandidate Candidate = new(0x1234, 0x5678, 1, 7, "1-2");

    [Fact]
    public void Every_call_is_passed_on_and_written_to_the_file_as_the_trace()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid, PanelTransport.Serial);
        device.Usb.Reply([0x10, 0x20]);
        device.Hid.Reply([0x00, 0x01]);
        device.Serial.Reply([0x4f, 0x4b]);
        device.Fail(4, PanelDeviceStatus.Timeout);
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-trace-{Guid.NewGuid():N}.txt");
        try
        {
            var recorder = new RecordingPanelDevices(devices, path);
            using (recorder)
            {
                Assert.Equal(PanelDeviceStatus.Ok, recorder.Describe(Candidate, out var descriptor));
                Assert.NotNull(descriptor);
                Assert.Equal(PanelDeviceStatus.Ok, recorder.OpenUsb(Candidate, 0, 1000, out var usb)); // call 1
                using (usb)
                {
                    var buffer = new byte[16];
                    Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkWrite(0x01, new byte[512], 100, zeroLengthPacketIfFull: true)); // 2
                    Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var read)); // 3
                    Assert.Equal([0x10, 0x20], buffer[..read]);
                    Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkWrite(0x01, [0x01], 100)); // 4
                }
                Assert.Equal(PanelDeviceStatus.Ok, recorder.OpenHid(Candidate, new PanelHidFilter(), 1000, out var hid));
                using (hid)
                {
                    Assert.Equal(PanelDeviceStatus.Ok, hid!.Write([0x00, 0x05], 100));
                    Assert.Equal(PanelDeviceStatus.Ok, hid.Read(new byte[65], 100, out _));
                }
                Assert.Equal(PanelDeviceStatus.Ok, recorder.OpenSerial(Candidate, new PanelSerialOptions(115200, Rts: true), 1000, out var serial));
                using (serial)
                {
                    Assert.Equal(PanelDeviceStatus.Ok, serial!.Write([0x41], 100));
                    Assert.Equal(PanelDeviceStatus.Ok, serial.Read(new byte[8], 100, out _));
                    Assert.Equal(PanelDeviceStatus.Ok, serial.DiscardInput());
                }

                // The fake's own trace is one device's: its opens name none.
                Assert.Equal(device.Trace.Entries, recorder.Trace.Entries.Select(e => e with { Device = null }));
            }

            var text = File.ReadAllText(path);
            Assert.Contains("# 1234:5678 at 1-2 (bus 1, address 7)\nh1 open usb if0\n", text);
            Assert.Equal(recorder.Trace.Format(), text);
            Assert.Equal(recorder.Trace.Entries, PanelTrace.Parse(text));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_failed_open_is_recorded_with_its_status()
    {
        var devices = new FakePanelDevices();
        devices.Add(Candidate).Usb.OpenStatus = PanelDeviceStatus.Busy;
        using var writer = new StringWriter();
        using var recorder = new RecordingPanelDevices(devices, writer);

        Assert.Equal(PanelDeviceStatus.Busy, recorder.OpenUsb(Candidate, 0, 1000, out var usb));
        Assert.Null(usb);
        Assert.Equal("h1 open usb if0 !busy\n", recorder.Trace.Entries.Select(PanelTrace.FormatLine).Single() + "\n");
        Assert.Contains("h1 open usb if0 !busy\n", writer.ToString());
    }

    // Finding 5: a handle opened when recording then fails is let go, not leaked.

    [Fact]
    public void A_handle_whose_open_cant_be_recorded_is_let_go()
    {
        var devices = new FakePanelDevices();
        devices.Add(Candidate, PanelTransport.Hid, PanelTransport.Serial);
        Assert.Equal(PanelDeviceStatus.Ok, devices.ListHid(Candidate, new PanelHidFilter(), out var collections));
        using var writer = new FullDisk();
        using var recorder = new RecordingPanelDevices(devices, writer);

        Assert.Throws<IOException>(() => recorder.OpenUsb(Candidate, 0, 1000, out _));
        Assert.Throws<IOException>(() => recorder.OpenHid(Candidate, new PanelHidFilter(), 1000, out _));
        Assert.Throws<IOException>(() => recorder.OpenHid(Candidate, collections[0], 1000, out _));
        Assert.Throws<IOException>(() => recorder.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out _));

        // Each handle was let go: the device opens again, not busy.
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenUsb(Candidate, 0, 1000, out var usb));
        usb!.Dispose();
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, new PanelHidFilter(), 1000, out var hid));
        hid!.Dispose();
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out var serial));
        serial!.Dispose();
    }

    // Finding 8: two panels recorded at once are told apart.

    [Fact]
    public void Two_panels_recorded_at_once_read_back_with_each_write_on_its_own_panel()
    {
        var other = Candidate with { Address = 9, PortPath = "1-3" };
        var devices = new FakePanelDevices();
        devices.Add(Candidate);
        devices.Add(other);
        using var writer = new StringWriter();
        using (var recorder = new RecordingPanelDevices(devices, writer))
        {
            Assert.Equal(PanelDeviceStatus.Ok, recorder.OpenUsb(Candidate, 0, 1000, out var first));
            Assert.Equal(PanelDeviceStatus.Ok, recorder.OpenUsb(other, 0, 1000, out var second));
            Assert.Equal(PanelDeviceStatus.Ok, first!.BulkWrite(0x01, [0xaa], 100));
            Assert.Equal(PanelDeviceStatus.Ok, second!.BulkWrite(0x01, [0xbb], 100));
            Assert.Equal(PanelDeviceStatus.Ok, first.BulkWrite(0x01, [0xa2], 100));
            second.Dispose();
            first.Dispose();
        }

        var entries = PanelTrace.Parse(writer.ToString());
        var opened = entries.Where(e => e.Op == PanelTraceOp.Open).ToDictionary(e => e.Handle, e => e.Device);
        Assert.Equal(2, opened.Count);
        var writes = entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => (opened[e.Handle], e.Data[0])).ToArray();
        Assert.Equal([("1234:5678 at 1-2 (bus 1, address 7)", (byte)0xaa), ("1234:5678 at 1-3 (bus 1, address 9)", (byte)0xbb), ("1234:5678 at 1-2 (bus 1, address 7)", (byte)0xa2)], writes);
        var closed = entries.Where(e => e.Op == PanelTraceOp.Close).Select(e => opened[e.Handle]).ToArray();
        Assert.Equal(["1234:5678 at 1-3 (bus 1, address 9)", "1234:5678 at 1-2 (bus 1, address 7)"], closed);
    }

    // Round 2, finding 5: a recording appended to a file goes on from the handles already in it.

    [Fact]
    public void A_recording_appended_to_a_file_numbers_its_handles_after_those_already_in_it()
    {
        var other = Candidate with { Address = 9, PortPath = "1-3" };
        var devices = new FakePanelDevices();
        devices.Add(Candidate);
        devices.Add(other);
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-trace-{Guid.NewGuid():N}.txt");
        try
        {
            using (var first = new RecordingPanelDevices(devices, path))
            {
                Assert.Equal(PanelDeviceStatus.Ok, first.OpenUsb(Candidate, 0, 1000, out var usb));
                Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkWrite(0x01, [0xaa], 100));
                usb.Dispose();
            }
            using (var second = new RecordingPanelDevices(devices, path))
            {
                Assert.Equal(PanelDeviceStatus.Ok, second.OpenUsb(other, 0, 1000, out var usb));
                Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkWrite(0x01, [0xbb], 100));
                usb.Dispose();
                Assert.Equal(2, second.Trace.Entries[0].Handle);
            }

            var entries = PanelTrace.Parse(File.ReadAllText(path));
            var opened = entries.Where(e => e.Op == PanelTraceOp.Open).ToDictionary(e => e.Handle, e => e.Device);
            Assert.Equal(2, opened.Count);
            var writes = entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => (opened[e.Handle], e.Data[0])).ToArray();
            Assert.Equal([("1234:5678 at 1-2 (bus 1, address 7)", (byte)0xaa), ("1234:5678 at 1-3 (bus 1, address 9)", (byte)0xbb)], writes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A writer that can't write (a full disk, say).
    private sealed class FullDisk : StringWriter
    {
        public override void Write(string? value) => throw new IOException("There is no space left on the device.");

        public override void Write(char value) => throw new IOException("There is no space left on the device.");
    }
}
