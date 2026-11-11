using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The test kit's fakes of the device seam (<see cref="FakePanelDevices"/>): every call on a device in its trace, in
/// order; replies scripted (queued, or computed from the last write); the nth call failed with a status; an unplug at a
/// call, every call after it <see cref="PanelDeviceStatus.Gone"/>; a zero-length packet recorded only when the driver
/// asks for one and the write fills its last packet.
/// </summary>
public sealed class FakePanelDevicesTests
{
    private static readonly PanelCandidate Candidate = new(0x1234, 0x5678, 1, 7, "1-2");

    private static IPanelUsb OpenUsb(FakePanelDevices devices, int interfaceNumber = 0)
    {
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenUsb(Candidate, interfaceNumber, 1000, out var usb));
        return Assert.IsAssignableFrom<IPanelUsb>(usb);
    }

    private static PanelUsbDescriptor Descriptor(int packetSize) =>
        new(0, 0, 0, 0x0200,
        [
            new PanelUsbInterface(0, 0xff, 0, 0,
            [
                new PanelUsbEndpoint(0x01, PanelEndpointType.Bulk, packetSize),
                new PanelUsbEndpoint(0x81, PanelEndpointType.Bulk, packetSize),
            ]),
        ]);

    [Fact]
    public void A_usb_device_records_every_call_in_order()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.Reply([0xaa, 0xbb]);

        using (var usb = OpenUsb(devices))
        {
            Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [0x01, 0x02, 0x03], 1000));
            var buffer = new byte[64];
            Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 1000, out var read));
            Assert.Equal([0xaa, 0xbb], buffer[..read]);
            Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [0xff], 1000));
        }

        Assert.Equal("h1 open usb if0\nh1 > ep01 010203\nh1 < ep81 aabb\nh1 > ep01 ff\nh1 close usb if0\n", device.Trace.Format());
        Assert.Equal(4, device.Calls);
    }

    [Fact]
    public void A_hid_device_records_reports_and_features_in_order()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = 513;
        device.Hid.Reply([0x00, 0x42]);
        device.Hid.ReplyFeature([0x05, 0x01, 0x02]);

        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, new PanelHidFilter(MinOutputReport: 513), 1000, out var opened));
        using (var hid = Assert.IsAssignableFrom<IPanelHid>(opened))
        {
            Assert.Equal(513, hid.MaxOutputReportLength);
            Assert.Equal(PanelDeviceStatus.Ok, hid.Write([0x00, 0x10, 0x20], 1000));
            Assert.Equal(PanelDeviceStatus.Ok, hid.SetFeature([0x05, 0x07], 1000));
            var feature = new byte[16];
            feature[0] = 0x05;
            Assert.Equal(PanelDeviceStatus.Ok, hid.GetFeature(feature, 1000, out var featureLength));
            Assert.Equal([0x05, 0x01, 0x02], feature[..featureLength]);
            var input = new byte[65];
            Assert.Equal(PanelDeviceStatus.Ok, hid.Read(input, 1000, out var inputLength));
            Assert.Equal([0x00, 0x42], input[..inputLength]);
        }

        Assert.Equal("h1 open hid hid0\nh1 hid> 001020\nh1 hid-feature> 0507\nh1 hid-feature< 050102\nh1 hid< 0042\nh1 close hid\n", device.Trace.Format());
    }

    [Fact]
    public void A_serial_port_records_its_settings_and_every_call_in_order()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Serial);
        device.Serial.PortName = "COM7";
        device.Serial.Reply([0x31, 0x32, 0x33]);

        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenSerial(Candidate, new PanelSerialOptions(115200, Dtr: true), 1000, out var opened));
        using (var serial = Assert.IsAssignableFrom<IPanelSerial>(opened))
        {
            Assert.Equal("COM7", serial.PortName);
            Assert.Equal(PanelDeviceStatus.Ok, serial.DiscardInput());
            Assert.Equal(PanelDeviceStatus.Ok, serial.Write([0x01], 1000));
            var buffer = new byte[2];
            Assert.Equal(PanelDeviceStatus.Ok, serial.Read(buffer, 1000, out var first));
            Assert.Equal([0x31, 0x32], buffer[..first]); // at most the buffer: the rest stays to be read
            Assert.Equal(PanelDeviceStatus.Ok, serial.Read(buffer, 1000, out var second));
            Assert.Equal([0x33], buffer[..second]);
        }

        Assert.Equal(new PanelSerialOptions(115200, Dtr: true), device.Serial.Options);
        Assert.Equal("h1 open serial 115200 dtr\nh1 serial discard\nh1 serial> 01\nh1 serial< 3132\nh1 serial< 33\nh1 close serial\n", device.Trace.Format());
    }

    [Fact]
    public void A_queued_reply_is_read_once_and_then_nothing_comes()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.Reply([0x01]);
        using var usb = OpenUsb(devices);
        var buffer = new byte[8];

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out _));
        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkRead(0x81, buffer, 100, out var read));
        Assert.Equal(0, read);
        Assert.Equal("h1 open usb if0\nh1 < ep81 01\nh1 < ep81 !timeout\n", device.Trace.Format());
    }

    [Fact]
    public void A_reply_can_be_computed_from_the_last_write()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        PanelTraceEntry? asked = null;
        device.Usb.Respond(last =>
        {
            asked = last;
            return last is { Data: [var command, ..] } ? [command, 0x80] : null;
        });
        using var usb = OpenUsb(devices);
        var buffer = new byte[8];

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [0x0a, 0x00], 100));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var read));
        Assert.Equal([0x0a, 0x80], buffer[..read]);
        Assert.Equal(PanelTraceOp.UsbWrite, asked?.Op);
        Assert.Equal([0x0a, 0x00], asked?.Data);
    }

    [Fact]
    public void A_serial_reply_comes_only_after_a_write_and_discarding_drops_what_came_unasked()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Serial);
        device.Serial.Reply([0x55]);
        device.Serial.Respond(write => [(byte)(write.Data[0] + 1)]);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out var opened));
        using var serial = opened!;
        var buffer = new byte[8];

        Assert.Equal(PanelDeviceStatus.Timeout, serial.Read(buffer, 100, out _)); // nothing asked yet
        device.Serial.Arrive([0xee, 0xee]);
        Assert.Equal(PanelDeviceStatus.Ok, serial.DiscardInput());
        Assert.Equal(PanelDeviceStatus.Ok, serial.Write([0x01], 100)); // the queued reply
        Assert.Equal(PanelDeviceStatus.Ok, serial.Read(buffer, 100, out var read));
        Assert.Equal([0x55], buffer[..read]);
        Assert.Equal(PanelDeviceStatus.Ok, serial.Write([0x20], 100)); // none queued: computed
        Assert.Equal(PanelDeviceStatus.Ok, serial.Read(buffer, 100, out read));
        Assert.Equal([0x21], buffer[..read]);
    }

    [Fact]
    public void A_hid_feature_reply_can_be_computed_from_its_report_id()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        device.Hid.RespondFeature(id => [id, 0x99]);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, new PanelHidFilter(), 1000, out var opened));
        using var hid = opened!;
        var buffer = new byte[8];
        buffer[0] = 0x03;

        Assert.Equal(PanelDeviceStatus.Ok, hid.GetFeature(buffer, 1000, out var read));
        Assert.Equal([0x03, 0x99], buffer[..read]);
    }

    [Fact]
    public void The_nth_call_fails_with_its_status_and_the_others_go_through()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.Reply([0x42]);
        device.Fail(2, PanelDeviceStatus.Timeout);
        device.Fail(3, PanelDeviceStatus.Io);
        using var usb = OpenUsb(devices); // call 1
        var buffer = new byte[8];

        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkWrite(0x01, [0x01], 100)); // 2
        Assert.Equal(PanelDeviceStatus.Io, usb.BulkRead(0x81, buffer, 100, out var read)); // 3: the reply stays queued
        Assert.Equal(0, read);
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out read)); // 4
        Assert.Equal([0x42], buffer[..read]);
        Assert.Equal("h1 open usb if0\nh1 > ep01 01 !timeout\nh1 < ep81 !io\nh1 < ep81 42\n", device.Trace.Format());
    }

    [Fact]
    public void An_open_can_fail_with_its_status()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.OpenStatus = PanelDeviceStatus.Access;

        Assert.Equal(PanelDeviceStatus.Access, devices.OpenUsb(Candidate, 0, 1000, out var usb));
        Assert.Null(usb);
        Assert.Equal("h1 open usb if0 !access\n", device.Trace.Format());
    }

    [Fact]
    public void After_an_unplug_at_a_call_that_call_and_every_later_one_are_gone()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.UnplugAt(3);
        var usb = OpenUsb(devices); // 1
        var buffer = new byte[8];

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, [0x01], 100)); // 2
        Assert.True(device.Plugged);
        Assert.Equal(PanelDeviceStatus.Gone, usb.BulkWrite(0x01, [0x02], 100)); // 3
        Assert.False(device.Plugged);
        Assert.Equal(PanelDeviceStatus.Gone, usb.BulkRead(0x81, buffer, 100, out _));
        usb.Dispose();
        Assert.Equal(PanelDeviceStatus.Gone, devices.Describe(Candidate, out var descriptor));
        Assert.Null(descriptor);
        Assert.Equal(PanelDeviceStatus.Gone, devices.OpenUsb(Candidate, 0, 1000, out _));
        Assert.Equal(PanelDeviceStatus.Gone, devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out _));
        Assert.Equal("h1 open usb if0\nh1 > ep01 01\nh1 > ep01 02 !gone\nh1 < ep81 !gone\nh1 close usb if0\nh2 open usb if0 !gone\nh3 open serial 9600 !gone\n",
            device.Trace.Format());
    }

    [Fact]
    public void An_unplug_now_makes_an_open_handle_gone()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, new PanelHidFilter(), 1000, out var hid));

        device.Unplug();

        Assert.Equal(PanelDeviceStatus.Gone, hid!.Write([0x00], 100));
        Assert.Equal(PanelDeviceStatus.Gone, hid.Read(new byte[8], 100, out _));
    }

    [Fact]
    public void A_zero_length_packet_is_recorded_only_when_asked_and_the_write_fills_its_last_packet()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Descriptor = Descriptor(packetSize: 64);
        using var usb = OpenUsb(devices);

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[128], 100, zeroLengthPacketIfFull: true));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[128], 100));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[100], 100, zeroLengthPacketIfFull: true));

        var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => e.Data.Length).ToArray();
        Assert.Equal([128, 0, 128, 100], writes);
        Assert.Contains("h1 > ep01 zlp\n", device.Trace.Format());
    }

    [Fact]
    public void A_zero_length_packet_follows_the_endpoints_own_packet_size()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate); // the default endpoints: 512-byte packets
        using var usb = OpenUsb(devices);

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[256], 100, zeroLengthPacketIfFull: true));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[1024], 100, zeroLengthPacketIfFull: true));

        var writes = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => e.Data.Length).ToArray();
        Assert.Equal([256, 1024, 0], writes);
        Assert.Equal(512, usb.Endpoints.Single(e => e.Address == 0x01).MaxPacketSize);
    }

    [Fact]
    public void A_device_is_held_by_one_handle_at_a_time()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        var first = OpenUsb(devices);

        Assert.Equal(PanelDeviceStatus.Busy, devices.OpenUsb(Candidate, 0, 1000, out var second));
        Assert.Null(second);
        first.Dispose();
        first.Dispose(); // once
        OpenUsb(devices).Dispose();

        Assert.Equal("h1 open usb if0\nh2 open usb if0 !busy\nh1 close usb if0\nh3 open usb if0\nh3 close usb if0\n", device.Trace.Format());
    }

    [Fact]
    public void What_a_device_has_not_is_not_supported_and_an_unknown_device_is_gone()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = 65;

        Assert.Equal(PanelDeviceStatus.NotSupported, devices.OpenUsb(Candidate, 3, 1000, out _)); // no interface 3
        Assert.Equal(PanelDeviceStatus.NotSupported, devices.OpenHid(Candidate, new PanelHidFilter(MinOutputReport: 513), 1000, out _));
        Assert.Equal(PanelDeviceStatus.NotSupported, devices.OpenHid(Candidate, new PanelHidFilter(Interface: 1), 1000, out _));
        Assert.Equal(PanelDeviceStatus.NotSupported, devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out _));
        Assert.Equal(PanelDeviceStatus.Gone, devices.OpenUsb(Candidate with { Address = 8 }, 0, 1000, out _));
        Assert.Equal(PanelDeviceStatus.Gone, devices.Describe(Candidate with { Address = 8 }, out _));
    }

    [Fact]
    public void Describe_answers_the_devices_descriptors_without_a_call()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Descriptor = Descriptor(packetSize: 64) with { BcdDevice = 0x0105 };

        Assert.Equal(PanelDeviceStatus.Ok, devices.Describe(Candidate, out var descriptor));
        Assert.Equal(0x0105, descriptor!.BcdDevice);
        Assert.Equal(0xff, descriptor.Interfaces[0].Class);
        Assert.Equal(0, device.Calls);
        Assert.Empty(device.Trace.Entries);
    }

    [Fact]
    public void A_handle_used_after_it_is_disposed_throws_and_nothing_reaches_the_device()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.SerialNumber = "A1";
        var usb = OpenUsb(devices);
        Assert.Equal("A1", usb.SerialNumber);
        usb.Dispose();

        Assert.Throws<ObjectDisposedException>(() => usb.BulkWrite(0x01, [0x01], 100));
        Assert.Equal(1, device.CallsAfterDispose);
        Assert.Equal("h1 open usb if0\nh1 close usb if0\n", device.Trace.Format());
    }

    [Fact]
    public void A_read_larger_than_the_buffer_and_a_write_to_an_in_endpoint_are_io_errors()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Usb.Reply([0x01, 0x02, 0x03]);
        using var usb = OpenUsb(devices);

        Assert.Equal(PanelDeviceStatus.Io, usb.BulkRead(0x81, new byte[2], 100, out _));
        Assert.Equal(PanelDeviceStatus.Io, usb.BulkWrite(0x81, [0x01], 100));
        Assert.Equal(PanelDeviceStatus.Io, usb.BulkWrite(0x02, [0x01], 100)); // no such endpoint
    }

    // Finding 6: packet boundaries, as libusb reads them.

    [Fact]
    public void Reads_keep_packet_boundaries_and_overflow_only_as_libusb_does()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate); // 512-byte packets
        var reply = Enumerable.Range(0, 1024).Select(i => (byte)(i / 4)).ToArray();
        device.Usb.Reply(reply);
        using var usb = OpenUsb(devices);
        var buffer = new byte[512];

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var first));
        Assert.Equal(reply[..512], buffer[..first]);
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var second));
        Assert.Equal(reply[512..], buffer[..second]);
        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkRead(0x81, buffer, 100, out _));

        // A short packet ends a read, and only a packet that doesn't fit what is left of the buffer overflows.
        device.Usb.Reply(new byte[100]);
        device.Usb.Reply(Enumerable.Repeat((byte)0x05, 50).ToArray());
        device.Usb.Reply(new byte[600]); // a full packet, then 88 bytes
        device.Usb.Reply([0x07]);

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, new byte[1024], 100, out var shortRead));
        Assert.Equal(100, shortRead); // the short packet ended it: the next reply stays
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, new byte[1024], 100, out var next));
        Assert.Equal(50, next);
        Assert.Equal(PanelDeviceStatus.Io, usb.BulkRead(0x81, new byte[520], 100, out _)); // 88 bytes into the 8 left
        var last = new byte[64];
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, last, 100, out var read)); // the reply after it stays
        Assert.Equal([0x07], last[..read]);
    }

    // Round 2, finding 3: a reply that ends on a full packet doesn't end the read; the next queued reply goes on into it.

    [Fact]
    public void A_read_runs_on_into_the_next_reply_when_the_last_packet_was_full()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate); // 512-byte packets
        var full = Enumerable.Repeat((byte)0x01, 512).ToArray();
        var tail = Enumerable.Repeat((byte)0x02, 100).ToArray();
        device.Usb.Reply(full);
        device.Usb.Reply(tail);
        using var usb = OpenUsb(devices);
        var buffer = new byte[1024];

        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var read));
        Assert.Equal([.. full, .. tail], buffer[..read]);
        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkRead(0x81, buffer, 100, out _));

        // A full buffer still ends a read, and a zero-length packet after a full one ends it too.
        device.Usb.Reply(full);
        device.Usb.Reply(full);
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, new byte[512], 100, out var first));
        Assert.Equal(512, first);
        device.Usb.Reply([]);
        device.Usb.Reply([0x07]);
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var second));
        Assert.Equal(full, buffer[..second]);
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, buffer, 100, out var third));
        Assert.Equal([0x07], buffer[..third]);
    }

    // Round 2, finding 4: a serial write that delivered only a prefix is answered from that prefix.

    [Fact]
    public void A_serial_write_that_delivered_a_prefix_is_answered_from_that_prefix()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Serial);
        var seen = new List<byte[]>();
        device.Serial.Respond(write =>
        {
            seen.Add(write.Data);
            return [0x99];
        });
        device.Fail(2, PanelDeviceStatus.Timeout, delivered: 3);
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenSerial(Candidate, new PanelSerialOptions(9600), 1000, out var opened)); // 1
        using var serial = opened!;

        Assert.Equal(PanelDeviceStatus.Timeout, serial.Write([0x10, 0x11, 0x12, 0x13, 0x14], 100)); // 2
        var buffer = new byte[8];
        Assert.Equal(PanelDeviceStatus.Ok, serial.Read(buffer, 100, out var read));

        Assert.Equal([0x99], buffer[..read]);
        Assert.Equal([0x10, 0x11, 0x12], Assert.Single(seen));
        Assert.Equal("h1 open serial 9600\nh1 serial> 1011121314 !timeout\nh1 serial< 99\n", device.Trace.Format());
    }

    // Finding 7: partial progress and a failing terminator.

    [Fact]
    public void A_failed_write_can_deliver_a_prefix_and_the_device_sees_only_that()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        var seen = new List<byte[]>();
        device.Usb.Respond(last =>
        {
            seen.Add(last?.Data ?? []);
            return [0x01];
        });
        device.Fail(2, PanelDeviceStatus.Timeout, delivered: 3);
        using var usb = OpenUsb(devices); // 1

        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkWrite(0x01, [0x10, 0x11, 0x12, 0x13, 0x14, 0x15], 100)); // 2
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, new byte[64], 100, out _));

        Assert.Equal([0x10, 0x11, 0x12], Assert.Single(seen));
        Assert.Equal("h1 open usb if0\nh1 > ep01 101112131415 !timeout\nh1 < ep81 01\n", device.Trace.Format());
    }

    [Fact]
    public void A_zero_length_packet_can_fail_after_its_payload_went()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate);
        device.Descriptor = Descriptor(packetSize: 4);
        var seen = new List<byte[]>();
        device.Usb.Respond(last =>
        {
            seen.Add(last?.Data ?? []);
            return null;
        });
        device.FailTerminator(2, PanelDeviceStatus.Timeout);
        using var usb = OpenUsb(devices); // 1

        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkWrite(0x01, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], 100, zeroLengthPacketIfFull: true)); // 2
        Assert.Equal(PanelDeviceStatus.Timeout, usb.BulkRead(0x81, new byte[64], 100, out _));

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], Assert.Single(seen)); // the payload reached the device
        Assert.Equal("h1 open usb if0\nh1 > ep01 0102030405060708\nh1 > ep01 zlp !timeout\nh1 < ep81 !timeout\n", device.Trace.Format());
    }

    // Finding 3: several HID collections that pass one filter.

    [Fact]
    public void A_device_lists_each_hid_collection_a_filter_passes_and_a_driver_can_open_each_in_turn()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        var second = device.AddHid(); // the same interface, usage page and report lengths
        var other = device.AddHid();
        other.MaxOutputReportLength = 9;
        second.RespondFeature(id => [id, 0x99]); // the first refuses every feature report
        var filter = new PanelHidFilter(Interface: 0, MinOutputReport: 65);

        Assert.Equal(PanelDeviceStatus.Ok, devices.ListHid(Candidate, filter, out var collections));
        Assert.Equal(2, collections.Count);
        Assert.Equal(["hid0", "hid1"], collections.Select(c => c.Id));
        Assert.All(collections, c => Assert.Equal((0, (ushort)0xff00, 65), (c.Interface, c.UsagePage, c.MaxOutputReportLength)));

        var buffer = new byte[8];
        PanelDeviceStatus started = PanelDeviceStatus.Io;
        foreach (var collection in collections)
        {
            Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, collection, 1000, out var hid));
            using (hid)
            {
                buffer[0] = 0x05;
                started = hid!.GetFeature(buffer, 1000, out _);
            }
            if (started == PanelDeviceStatus.Ok)
                break;
        }

        Assert.Equal(PanelDeviceStatus.Ok, started);
        Assert.Equal("h1 open hid hid0\nh1 hid-feature< !io\nh1 close hid\nh2 open hid hid1\nh2 hid-feature< 0599\nh2 close hid\n", device.Trace.Format());
        Assert.Equal(PanelDeviceStatus.Gone, devices.ListHid(Candidate with { Address = 8 }, filter, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Each_hid_collection_is_held_on_its_own()
    {
        var devices = new FakePanelDevices();
        var device = devices.Add(Candidate, PanelTransport.Hid);
        device.AddHid();
        Assert.Equal(PanelDeviceStatus.Ok, devices.ListHid(Candidate, new PanelHidFilter(), out var collections));

        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, collections[0], 1000, out var first));
        Assert.Equal(PanelDeviceStatus.Busy, devices.OpenHid(Candidate, collections[0], 1000, out _));
        Assert.Equal(PanelDeviceStatus.Ok, devices.OpenHid(Candidate, collections[1], 1000, out var second));
        Assert.Equal(PanelDeviceStatus.Gone, devices.OpenHid(Candidate, collections[1] with { Id = "hid7" }, 1000, out _));
        first!.Dispose();
        second!.Dispose();
    }
}
