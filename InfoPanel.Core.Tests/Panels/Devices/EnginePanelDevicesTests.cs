using System.Xml.Linq;
using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Devices;

/// <summary>
/// The engine's device seam over fakes of what the OS gives: USB over a fake <see cref="IUsbBackend"/> (the interface
/// asked for claimed, configuration 1 only on an unconfigured device, a zero-length packet only when asked and the write
/// fills its last packet, another holder's device answering busy with nothing written), the HID chooser over listed
/// collections per OS, and the serial-port finder over fake OS tables per OS (only the device's own port, never another).
/// </summary>
public sealed class EnginePanelDevicesTests : IDisposable
{
    private static readonly PanelCandidate Candidate = new(0x0416, 0x5302, 1, 7, "1-2.3");
    private readonly KeptLog _log = new();
    private readonly string _root = Directory.CreateTempSubdirectory("panel-devices-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static UsbDescriptor Descriptor() => new(0xEF, 2, 1, 0x0100,
    [
        new UsbInterfaceDescriptor(0, 0x02, 2, 1, [(0x83, 3, 8)]),
        new UsbInterfaceDescriptor(1, 0xFF, 0, 0, [(0x01, 2, 512), (0x81, 2, 512)]),
    ]);

    private EnginePanelDevices Seam(Backend backend) => new(backend, null, null, _log.Logger);

    [Fact]
    public void The_interface_asked_for_is_claimed_with_its_endpoints_and_the_devices_serial()
    {
        var backend = new Backend(Descriptor()) { Serial = "ABC123" };

        var status = Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(PanelDeviceStatus.Ok, status);
        Assert.Equal(["open", "getConfiguration", "claim 1", "serial"], backend.Calls);
        Assert.Equal([new PanelUsbEndpoint(0x01, PanelEndpointType.Bulk, 512), new PanelUsbEndpoint(0x81, PanelEndpointType.Bulk, 512)], usb!.Endpoints);
        Assert.Equal("ABC123", usb.SerialNumber);
        usb.Dispose();
        Assert.Equal("close", backend.Calls[^1]);
    }

    [Fact]
    public void An_unconfigured_device_is_set_to_configuration_1_before_the_claim_and_a_configured_one_never_is()
    {
        var unconfigured = new Backend(Descriptor()) { Configuration = 0 };

        Seam(unconfigured).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(["open", "getConfiguration", "setConfiguration 1", "claim 1", "serial"], unconfigured.Calls);
        usb!.Dispose();
    }

    [Theory]
    [InlineData(UsbStatus.Busy, PanelDeviceStatus.Busy)]
    [InlineData(UsbStatus.Access, PanelDeviceStatus.Access)]
    public void A_device_another_program_holds_answers_so_with_nothing_written_and_is_let_go(UsbStatus claim, PanelDeviceStatus expected)
    {
        var backend = new Backend(Descriptor()) { ClaimResult = claim };

        var status = Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(expected, status);
        Assert.Null(usb);
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("write", StringComparison.Ordinal));
        Assert.Equal("close", backend.Calls[^1]);
    }

    [Fact]
    public void An_interface_the_device_doesnt_have_is_refused_before_the_device_is_opened()
    {
        var backend = new Backend(Descriptor());

        Assert.Equal(PanelDeviceStatus.NotSupported, Seam(backend).OpenUsb(Candidate, 3, 1000, out _));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void A_zero_length_packet_follows_a_write_only_when_asked_and_only_when_it_fills_its_last_packet()
    {
        var backend = new Backend(Descriptor());
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkWrite(0x01, new byte[1024], 1000));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[1000], 1000, zeroLengthPacketIfFull: true));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[1024], 1000, zeroLengthPacketIfFull: true));

        Assert.Equal(["write 01 1024", "write 01 1000", "write 01 1024", "zlp 01"], backend.Calls.Where(c => c.StartsWith("write", StringComparison.Ordinal) || c.StartsWith("zlp", StringComparison.Ordinal)));
        usb.Dispose();
    }

    [Fact]
    public void The_cached_descriptors_are_given_without_opening_the_device()
    {
        var backend = new Backend(Descriptor());

        Assert.Equal(PanelDeviceStatus.Ok, Seam(backend).Describe(Candidate, out var descriptor));

        Assert.Equal((0xEF, 2, 1, 0x0100), (descriptor!.DeviceClass, descriptor.DeviceSubClass, descriptor.DeviceProtocol, descriptor.BcdDevice));
        Assert.Equal([0, 1], descriptor.Interfaces.Select(i => i.Number));
        Assert.Equal(new PanelUsbEndpoint(0x83, PanelEndpointType.Interrupt, 8), descriptor.Interfaces[0].Endpoints[0]);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void A_device_gone_from_the_list_is_gone()
    {
        var backend = new Backend(Descriptor()) { Listed = false };

        Assert.Equal(PanelDeviceStatus.Gone, Seam(backend).OpenUsb(Candidate, 1, 1000, out _));
        Assert.Equal(PanelDeviceStatus.Gone, Seam(backend).Describe(Candidate, out _));
    }

    [Fact]
    public void A_read_that_gets_nothing_waits_its_whole_timeout_in_slices_and_a_cancel_ends_it_at_the_next()
    {
        var backend = new Backend(Descriptor());
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);
        var buffer = new byte[512];

        Assert.Equal(PanelDeviceStatus.Timeout, usb!.BulkRead(0x81, buffer, 250, out var read));
        Assert.Equal(0, read);
        Assert.Equal(["read 81 100", "read 81 100", "read 81 50"], backend.Calls.Where(c => c.StartsWith("read", StringComparison.Ordinal)));

        using var cancel = new CancellationTokenSource();
        backend.OnRead = cancel.Cancel;
        Assert.Throws<OperationCanceledException>(() => usb.BulkRead(0x81, buffer, 5000, out _, cancel.Token));
        Assert.Equal(4, backend.Calls.Count(c => c.StartsWith("read", StringComparison.Ordinal)));
        usb.Dispose();
    }

    // HID: which collections are the candidate's.

    private static HidEntry Hid(string path, string? port, int usbInterface = 0, int output = 513, string? serial = null, ushort vid = 0x0416, ushort pid = 0x5302, string? parent = null, byte? bus = null, byte? address = null) =>
        new(path, vid, pid, serial, port, usbInterface, 0xFF00, 1, 513, output, 0, parent, bus, address);

    [Fact]
    public void Where_the_system_gives_each_collections_port_only_those_at_the_devices_port_are_its()
    {
        var chooser = new HidChooser(_log.Logger);
        HidEntry[] listed = [Hid("a", "1-2.2"), Hid("b", "1-2.3", 1, bus: 1, address: 7), Hid("c", "1-2.3", 0, bus: 1, address: 7), Hid("d", "1-2.3", 2, output: 65, bus: 1, address: 7), Hid("e", "1-2.3", serial: null, vid: 0x1234)];

        Assert.Equal(PanelDeviceStatus.Ok, chooser.Collections(Candidate, listed, new PanelHidFilter(MinOutputReport: 513), out var chosen));

        Assert.Equal(["c", "b"], chosen.Select(e => e.Path)); // interface order, the short report and other ids left out
        chooser.Collections(Candidate, listed, new PanelHidFilter(Interface: 2), out var second);
        Assert.Equal(["d"], second.Select(e => e.Path));
    }

    [Fact]
    public void Identical_devices_the_system_cant_tell_apart_go_one_each_to_the_first_panels_asking_and_the_rest_are_busy()
    {
        var chooser = new HidChooser(_log.Logger);
        HidEntry[] listed = [Hid("x", null, serial: "S2"), Hid("w", null, serial: "S1")];
        var other = Candidate with { Address = 8, PortPath = "1-2.4" };
        var third = Candidate with { Address = 9, PortPath = "1-2.5" };

        chooser.Collections(Candidate, listed, new PanelHidFilter(), out var first);
        chooser.Collections(other, listed, new PanelHidFilter(), out var second);
        chooser.Collections(Candidate, listed, new PanelHidFilter(), out var again);

        Assert.Equal(["w"], first.Select(e => e.Path));
        Assert.Equal(["x"], second.Select(e => e.Path));
        Assert.Equal(["w"], again.Select(e => e.Path)); // a panel keeps the one it was given
        Assert.Equal(PanelDeviceStatus.Busy, chooser.Collections(third, listed, new PanelHidFilter(), out _));
        Assert.Contains(_log.Lines, l => l.Contains("can't tell apart"));
    }

    [Fact]
    public void A_single_device_of_its_ids_is_the_candidates_where_the_system_gives_no_port()
    {
        var chooser = new HidChooser(_log.Logger);

        chooser.Collections(Candidate, [Hid(@"\\?\hid#vid_0416&pid_5302&mi_01#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", null, 1)], new PanelHidFilter(), out var chosen);

        Assert.Single(chosen);
        Assert.Equal(PanelDeviceStatus.Gone, chooser.Collections(Candidate, [Hid("z", null, vid: 0x1111)], new PanelHidFilter(), out _));
    }

    [Fact]
    public void Each_systems_HID_path_gives_its_port_and_interface_where_it_has_them()
    {
        Assert.Equal(("1-2.3", 1), HidChooser.LinuxLocation("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2.3/1-2.3:1.1/0003:0416:5302.0004"));
        Assert.Null(HidChooser.LinuxLocation("/sys/devices/virtual/misc/uhid/0003:0416:5302.0004"));
        Assert.Equal(("20-2.1", 0), HidChooser.MacLocation(
            "IOService:/AppleARMPE/arm-io@10F00000/AppleT8103USBXHCI@14000000/usb-drd1-port-hs@14200000/USB2.0 Hub@14200000/AppleUSB20HubPort@14210000/LCD@14210000/IOUSBHostInterface@0/AppleUserUSBHostHIDDevice"));
        Assert.Equal(1, HidChooser.WindowsInterface(@"\\?\hid#vid_0416&pid_5302&mi_01#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.Equal(0, HidChooser.WindowsInterface(@"\\?\hid#vid_0416&pid_5302#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.Equal("20-2.1", HidChooser.PortPathOfLocation(0x14210000));
    }

    // Serial: the device's own port, by its location.

    private static readonly PanelCandidate SerialCandidate = new(0x1a86, 0x5722, 1, 7, "1-2.3");

    private string SysfsDevice(string port, string vendor = "1a86", string product = "5722", string bus = "1", string address = "7")
    {
        var device = Directory.CreateDirectory(Path.Combine(_root, "bus", "usb", "devices", port)).FullName;
        File.WriteAllText(Path.Combine(device, "idVendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(device, "idProduct"), product + "\n");
        File.WriteAllText(Path.Combine(device, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(device, "devnum"), address + "\n");
        return device;
    }

    [Fact]
    public void Linux_finds_the_tty_of_the_device_at_its_port_and_no_other()
    {
        var devices = Path.Combine(_root, "bus", "usb", "devices");
        SysfsDevice("1-2.3");
        Directory.CreateDirectory(Path.Combine(devices, "1-2.3:1.0", "tty", "ttyACM1"));
        Directory.CreateDirectory(Path.Combine(devices, "1-2.4:1.0", "tty", "ttyACM0")); // another device's: never it
        SysfsDevice("1-5", address: "9");
        Directory.CreateDirectory(Path.Combine(devices, "1-5:1.0", "ttyUSB0")); // usb-serial puts the node beside
        SysfsDevice("1-2", address: "3");

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Linux(_root, SerialCandidate, out var acm));
        Assert.Equal("/dev/ttyACM1", acm);
        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Linux(_root, SerialCandidate with { Address = 9, PortPath = "1-5" }, out var usb));
        Assert.Equal("/dev/ttyUSB0", usb);
        Assert.Equal(PanelDeviceStatus.NotSupported, SerialPortFinder.Linux(_root, SerialCandidate with { Address = 3, PortPath = "1-2" }, out _));
        Assert.Equal(PanelDeviceStatus.NotSupported, SerialPortFinder.Linux(_root, SerialCandidate with { PortPath = "../x" }, out _));
    }

    [Fact]
    public void Linux_answers_gone_when_the_device_at_the_port_is_no_longer_the_candidate()
    {
        var device = SysfsDevice("1-2.3");
        Directory.CreateDirectory(Path.Combine(_root, "bus", "usb", "devices", "1-2.3:1.0", "tty", "ttyACM1"));

        // Unplugged and another device plugged into the same port: another address, or other ids, or another bus.
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Linux(_root, SerialCandidate with { Address = 8 }, out var port));
        Assert.Null(port);
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Linux(_root, SerialCandidate with { ProductId = 0x7523 }, out _));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Linux(_root, SerialCandidate with { Bus = 2 }, out _));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Linux(_root, SerialCandidate with { PortPath = "1-2.6" }, out _)); // nothing there
        File.WriteAllText(Path.Combine(device, "devnum"), "11\n");
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Linux(_root, SerialCandidate, out _));
    }

    private static string MacDevice(long location, int vendor, int product, string callout, int? address = null) => $"""
        <dict>
          <key>idProduct</key><integer>{product}</integer>
          <key>idVendor</key><integer>{vendor}</integer>
          <key>locationID</key><integer>{location}</integer>
          {(address is { } a ? $"<key>USB Address</key><integer>{a}</integer>" : "")}
          <key>IORegistryEntryChildren</key>
          <array><dict><key>IORegistryEntryChildren</key><array><dict>
            {(callout.Length > 0 ? $"<key>IOCalloutDevice</key><string>{callout}</string>" : "")}
          </dict></array></dict></array>
        </dict>
        """;

    [Fact]
    public void MacOS_finds_the_callout_device_below_the_usb_device_at_its_location_with_its_ids()
    {
        var tree = XDocument.Parse($"""
            <plist version="1.0"><array>
            {MacDevice(0x14200000, 0x1a86, 0x5722, "/dev/cu.usbmodem0001")}
            {MacDevice(0x14210000, 0x1a86, 0x5722, "/dev/cu.usbmodem14210")}
            {MacDevice(0x14300000, 0x0525, 0xa4a7, "/dev/cu.usbmodem3")}
            {MacDevice(0x14400000, 0x1a86, 0x5722, "")}
            </array></plist>
            """);
        var candidate = SerialCandidate with { Bus = 20, PortPath = "20-2.1" };

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.MacOS(tree, candidate, out var port));
        Assert.Equal("/dev/cu.usbmodem14210", port);
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.MacOS(tree, candidate with { PortPath = "20-3" }, out _)); // the device there has other ids
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.MacOS(tree, candidate with { PortPath = "20-5" }, out _)); // none there
        Assert.Equal(PanelDeviceStatus.NotSupported, SerialPortFinder.MacOS(tree, candidate with { PortPath = "20-4" }, out _)); // it, without a port
    }

    [Fact]
    public void MacOS_answers_gone_when_the_device_at_the_location_has_another_address()
    {
        var tree = XDocument.Parse($"""<plist version="1.0"><array>{MacDevice(0x14210000, 0x1a86, 0x5722, "/dev/cu.usbmodem14210", address: 7)}</array></plist>""");
        var candidate = SerialCandidate with { Bus = 20, PortPath = "20-2.1" };

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.MacOS(tree, candidate, out _));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.MacOS(tree, candidate with { Address = 9 }, out var port));
        Assert.Null(port);
    }

    // Windows: the device of its ids told apart by its serial number, else the only one; never a guess.

    /// <summary>The location path of <see cref="SerialCandidate"/>'s port (libusb's <c>1-2.3</c>: root port 2, then the hub's port 3).</summary>
    private const string AtItsPort = @"PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USB(3)";

    private static SerialPortFinder.WindowsUsbDevice WinDevice(string instance, bool unique, params string[] ports) =>
        WinDeviceAt(AtItsPort, instance, unique, ports);

    private static SerialPortFinder.WindowsUsbDevice WinDeviceAt(string? location, string instance, bool unique, params string[] ports) =>
        new($@"USB\VID_1A86&PID_5722\{instance}", unique, ports, location);

    [Fact]
    public void Windows_takes_the_only_device_of_its_ids_only_in_the_candidates_own_port_and_with_its_serial()
    {
        // The listed device unplugged and another of the same ids plugged in elsewhere before the open: refused.
        var elsewhere = WinDeviceAt(@"PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)", "6&1A2B3C&0&4", false, "COM5");
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows([elsewhere], SerialCandidate, () => null, out var port, out var refusal));
        Assert.Null(port);
        Assert.Null(refusal);
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows([WinDeviceAt(@"PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USB(4)", "6&1A2B3C&0&4", false, "COM5")],
            SerialCandidate, () => null, out port, out _)); // the same hub, another port
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows([WinDeviceAt(@"PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)", "6&1A2B3C&0&2", false, "COM5")],
            SerialCandidate, () => null, out port, out _)); // the hub's own port
        Assert.Null(port);
        // In the very same port: the same model in the same place, the same panel.
        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Windows([WinDevice("6&1A2B3C&0&3", false, "COM5")], SerialCandidate, () => null, out port, out _));
        Assert.Equal("COM5", port);
        // The candidate's serial number known: another one is refused, even in the same port.
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows([WinDevice("S2", true, "COM5")], SerialCandidate, () => "S1", out port, out _));
        Assert.Null(port);
        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Windows([WinDevice("S1", true, "COM5")], SerialCandidate, () => "S1", out port, out _));
        Assert.Equal("COM5", port);
        // The system doesn't say where it is: refused with the reason, never a guess.
        Assert.Equal(PanelDeviceStatus.Busy, SerialPortFinder.Windows([WinDeviceAt(null, "6&1A2B3C&0&3", false, "COM5")], SerialCandidate, () => null, out port, out refusal));
        Assert.Null(port);
        Assert.Equal(SerialPortFinder.WindowsNoPort, refusal);
        // Checked again after the open, the same.
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.WindowsOwner([elsewhere], SerialCandidate, () => null, "COM5"));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.WindowsOwner([WinDevice("S2", true, "COM5")], SerialCandidate, () => "S1", "COM5"));
        Assert.Equal([2, 3], SerialPortFinder.WindowsPortChain(AtItsPort));
        Assert.Null(SerialPortFinder.WindowsPortChain(@"PCIROOT(0)#PCI(1400)"));
        Assert.Null(SerialPortFinder.WindowsPortChain(@"PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(0)"));
    }

    [Fact]
    public void Windows_tells_identical_devices_apart_by_their_serial_numbers()
    {
        SerialPortFinder.WindowsUsbDevice[] devices = [WinDevice("S1", true, "COM3"), WinDevice("S2", true, "COM5"), new(@"USB\VID_0525&PID_A4A7\S2", true, ["COM8"])];

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Windows(devices, SerialCandidate, () => "S2", out var port, out var refusal));
        Assert.Equal("COM5", port);
        Assert.Null(refusal);
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows(devices, SerialCandidate, () => "S9", out var none, out _)); // neither is it
        Assert.Null(none);
        // Its serial number can't be read: refused with the reason, never a guess.
        Assert.Equal(PanelDeviceStatus.Busy, SerialPortFinder.Windows(devices, SerialCandidate, () => null, out none, out refusal));
        Assert.Null(none);
        Assert.Equal(SerialPortFinder.WindowsIdenticalDevices, refusal);
    }

    [Fact]
    public void Windows_refuses_identical_devices_without_serial_numbers_with_the_reason()
    {
        SerialPortFinder.WindowsUsbDevice[] devices = [WinDevice("6&1A2B3C&0&3", false, "COM3"), WinDevice("6&1A2B3C&0&4", false, "COM5")];

        Assert.Equal(PanelDeviceStatus.Busy, SerialPortFinder.Windows(devices, SerialCandidate, () => "S1", out var port, out var refusal));

        Assert.Null(port);
        Assert.Equal(SerialPortFinder.WindowsIdenticalDevices, refusal);
    }

    [Fact]
    public void Windows_takes_the_only_device_of_its_ids_without_asking_its_serial_number()
    {
        var asked = 0;
        Func<string?> serial = () =>
        {
            asked++;
            return null;
        };
        SerialPortFinder.WindowsUsbDevice[] alone = [WinDevice("6&1A2B3C&0&3", false, "COM5"), new(@"USB\VID_1A86&PID_5722&MI_00\7&1&0&0000", false, ["COM9"]), new(@"USB\VID_0525&PID_A4A7\X", true, ["COM8"])];

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.Windows(alone, SerialCandidate, serial, out var port, out var refusal));

        Assert.Equal("COM5", port);
        Assert.Null(refusal);
        Assert.Equal(0, asked);
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.Windows([alone[2]], SerialCandidate, serial, out var none, out _));
        Assert.Null(none);
        Assert.Equal(PanelDeviceStatus.NotSupported, SerialPortFinder.Windows([WinDevice("S1", true)], SerialCandidate, serial, out none, out _)); // no COM port
        Assert.Null(none);
        Assert.Equal(PanelDeviceStatus.NotSupported, SerialPortFinder.Windows([WinDevice("S1", true, "COM5", "COM6")], SerialCandidate, serial, out none, out _)); // two: neither
        Assert.Null(none);
    }

    // The opened port checked to be the device's, before its lines are set.

    [Fact]
    public void The_opened_port_is_checked_to_be_the_devices_before_its_lines_are_set_and_another_is_closed_at_once()
    {
        var serial = new FakeSerial { Finder = (_, _) => (PanelDeviceStatus.Ok, "/dev/ttyACM1"), Verifier = _ => PanelDeviceStatus.Gone };
        var seam = new EnginePanelDevices(null, null, serial, _log.Logger);
        var options = new PanelSerialOptions(115200, Dtr: true, Rts: true);

        // Another device took the name between the lookup and the open.
        Assert.Equal(PanelDeviceStatus.Gone, seam.OpenSerial(SerialCandidate, options, 1000, out var port));

        Assert.Null(port);
        var replaced = Assert.Single(serial.Ports);
        Assert.True(replaced.Disposed);
        Assert.Equal(["find", "open /dev/ttyACM1", "verify /dev/ttyACM1", "close /dev/ttyACM1"], serial.Events);

        serial.Events.Clear();
        serial.Verifier = _ => PanelDeviceStatus.Ok;
        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenSerial(SerialCandidate, options, 1000, out port));
        Assert.Equal(["find", "open /dev/ttyACM1", "verify /dev/ttyACM1", "lines True True"], serial.Events);
        port!.Dispose();
    }

    [Fact]
    public void A_check_that_ends_past_the_deadline_or_after_a_cancel_closes_the_port_and_never_sets_its_lines()
    {
        // The check blocks past the caller's deadline, then says the port is the device's.
        var slow = new FakeSerial
        {
            Finder = (_, _) => (PanelDeviceStatus.Ok, "/dev/ttyACM1"),
            Verifier = _ =>
            {
                Thread.Sleep(300);
                return PanelDeviceStatus.Ok;
            },
        };
        var options = new PanelSerialOptions(115200, Dtr: true, Rts: true);

        Assert.Equal(PanelDeviceStatus.Timeout, new EnginePanelDevices(null, null, slow, _log.Logger).OpenSerial(SerialCandidate, options, 100, out var port));

        Assert.Null(port);
        Assert.True(SpinWait.SpinUntil(() => slow.Opens == 1 && slow.Ports[0].Disposed, 2000), "the late port is closed");
        Assert.DoesNotContain(slow.Events.ToArray(), e => e.StartsWith("lines", StringComparison.Ordinal));

        // The caller cancels during the check, which then says the port is the device's.
        using var cancel = new CancellationTokenSource();
        var cancelled = new FakeSerial
        {
            Finder = (_, _) => (PanelDeviceStatus.Ok, "/dev/ttyACM1"),
            Verifier = _ =>
            {
                cancel.Cancel();
                return PanelDeviceStatus.Ok;
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => new EnginePanelDevices(null, null, cancelled, _log.Logger).OpenSerial(SerialCandidate, options, 10_000, out _, cancel.Token));
        Assert.True(SpinWait.SpinUntil(() => cancelled.Opens == 1 && cancelled.Ports[0].Disposed, 2000), "the cancelled port is closed");
        Assert.DoesNotContain(cancelled.Events.ToArray(), e => e.StartsWith("lines", StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_checks_the_opened_ports_character_device_up_to_its_usb_device()
    {
        string UsbDevice(string at, string vendor, string product, string address)
        {
            var device = Directory.CreateDirectory(Path.Combine(_root, "devices", "pci0000:00", "usb1", at)).FullName;
            File.WriteAllText(Path.Combine(device, "idVendor"), vendor + "\n");
            File.WriteAllText(Path.Combine(device, "idProduct"), product + "\n");
            File.WriteAllText(Path.Combine(device, "busnum"), "1\n");
            File.WriteAllText(Path.Combine(device, "devnum"), address + "\n");
            return device;
        }
        var chars = Directory.CreateDirectory(Path.Combine(_root, "dev", "char")).FullName;
        var panel = UsbDevice(Path.Combine("1-2", "1-2.3"), "1a86", "5722", "7");
        Directory.CreateSymbolicLink(Path.Combine(chars, "166:1"), Directory.CreateDirectory(Path.Combine(panel, "1-2.3:1.0", "tty", "ttyACM1")).FullName);
        var other = UsbDevice("1-4", "0525", "a4a7", "9");
        Directory.CreateSymbolicLink(Path.Combine(chars, "188:0"), Directory.CreateDirectory(Path.Combine(other, "1-4:1.0", "ttyUSB0", "tty", "ttyUSB0")).FullName);

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.LinuxOwner(_root, 166, 1, SerialCandidate));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.LinuxOwner(_root, 188, 0, SerialCandidate)); // the name now another device's
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.LinuxOwner(_root, 166, 1, SerialCandidate with { Address = 8 })); // plugged in again since
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.LinuxOwner(_root, 4, 64, SerialCandidate)); // not a USB device's
    }

    [Fact]
    public void MacOS_checks_the_opened_ports_device_number_against_the_devices_callout_node()
    {
        var tree = XDocument.Parse($"""<plist version="1.0"><array>{MacDevice(0x14210000, 0x1a86, 0x5722, "/dev/cu.usbmodem14210", address: 7)}</array></plist>""");
        var candidate = SerialCandidate with { Bus = 20, PortPath = "20-2.1" };
        Func<string, long?> numbers = path => path == "/dev/cu.usbmodem14210" ? 0x09000003 : null;

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.MacOSOwner(tree, candidate, 0x09000003, numbers));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.MacOSOwner(tree, candidate, 0x09000004, numbers)); // another device's node
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.MacOSOwner(tree, candidate with { Address = 9 }, 0x09000003, numbers)); // another device there now
    }

    [Fact]
    public void Windows_checks_the_opened_com_name_against_the_devices()
    {
        SerialPortFinder.WindowsUsbDevice[] devices = [WinDevice("S1", true, "COM3"), WinDevice("S2", true, "COM5")];

        Assert.Equal(PanelDeviceStatus.Ok, SerialPortFinder.WindowsOwner(devices, SerialCandidate, () => "S2", "COM5"));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.WindowsOwner(devices, SerialCandidate, () => "S2", "COM3"));
        Assert.Equal(PanelDeviceStatus.Gone, SerialPortFinder.WindowsOwner([WinDevice("S2", true, "COM7")], SerialCandidate, () => "S2", "COM3")); // the device's port is another now
    }

    // A serial port's dispose and its calls' one deadline.

    [Fact]
    public void A_serial_dispose_throws_away_what_is_queued_and_returns_within_its_bound_even_when_the_close_hangs()
    {
        var line = new FakeLine();
        var port = new OpenedSerial(line);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        port.Dispose(); // the close waits for the queued output to go, as System.IO.Ports' does

        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(SpinWait.SpinUntil(() => line.Steps.Contains("close"), 1000));
        Assert.Equal(["discardOut", "discardIn", "close"], line.Steps.Take(3));

        var hanging = new FakeLine { CloseWaitsForDiscard = false };
        var other = new OpenedSerial(hanging);
        var reading = Task.Run(() => other.Read(new byte[8], 30_000, out _));
        Assert.True(SpinWait.SpinUntil(() => hanging.Steps.Contains("read"), 2000));
        watch.Restart();

        other.Dispose(); // the close hangs whatever is done first

        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(reading.Wait(1000), "the read ends");
        Assert.Equal(PanelDeviceStatus.Io, reading.Result);
    }

    [Fact]
    public void A_serial_call_waiting_behind_another_keeps_to_its_deadline_and_a_cancel_while_it_waits_throws()
    {
        var line = new FakeLine();
        var port = new OpenedSerial(line);
        var reading = Task.Run(() => port.Read(new byte[8], 1500, out _));
        Assert.True(SpinWait.SpinUntil(() => line.Steps.Contains("read"), 2000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, port.Write(new byte[4], 1));

        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
        Assert.DoesNotContain("write", line.Steps);
        using var cancel = new CancellationTokenSource(50);
        watch.Restart();
        Assert.ThrowsAny<OperationCanceledException>(() => port.Write(new byte[4], 10_000, cancel.Token));
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.DoesNotContain("write", line.Steps);
        reading.Wait(3000);
        port.Dispose();
    }

    // One deadline per call.

    [Fact]
    public void A_slow_port_finder_answers_timeout_at_the_callers_deadline_and_never_opens_the_port_late()
    {
        var serial = new FakeSerial();
        using var release = new ManualResetEventSlim();
        serial.Finder = (_, token) =>
        {
            WaitHandle.WaitAny([release.WaitHandle, token.WaitHandle], 5000);
            return (PanelDeviceStatus.Ok, "/dev/ttyACM1");
        };
        var seam = new EnginePanelDevices(null, null, serial, _log.Logger);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, seam.OpenSerial(SerialCandidate, new PanelSerialOptions(115200), 50, out var port));

        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Null(port);
        release.Set();
        Thread.Sleep(200);
        Assert.Equal(0, serial.Opens);
    }

    [Fact]
    public void A_cancel_during_an_open_throws_and_the_handle_that_opens_late_is_disposed()
    {
        var serial = new FakeSerial { Finder = (_, _) => (PanelDeviceStatus.Ok, "/dev/ttyACM1") };
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        serial.OnOpen = () =>
        {
            opening.Set();
            release.Wait(5000);
        };
        var seam = new EnginePanelDevices(null, null, serial, _log.Logger);
        using var cancel = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            opening.Wait(5000);
            cancel.Cancel();
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => seam.OpenSerial(SerialCandidate, new PanelSerialOptions(115200), 10_000, out _, cancel.Token));

        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => serial.Ports.Count == 1 && serial.Ports[0].Disposed, 2000), "the late handle is disposed");
    }

    [Fact]
    public void A_usb_open_past_its_deadline_answers_timeout_and_the_late_device_is_closed()
    {
        var backend = new Backend(Descriptor()) { OnOpen = () => Thread.Sleep(400) };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, Seam(backend).OpenUsb(Candidate, 1, 50, out var usb));

        Assert.True(watch.ElapsedMilliseconds < 350, $"{watch.ElapsedMilliseconds} ms");
        Assert.Null(usb);
        Assert.True(SpinWait.SpinUntil(() => backend.Calls.Length > 0 && backend.Calls[^1] == "close", 2000), string.Join(", ", backend.Calls));
    }

    [Fact]
    public void A_hid_feature_call_keeps_to_its_timeout_and_one_past_it_is_ended()
    {
        var interrupted = false;
        using var hang = new ManualResetEventSlim();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var status = HidSharpSystem.Bounded(50, () =>
        {
            hang.Wait(3000);
            return PanelDeviceStatus.Ok;
        }, () =>
        {
            interrupted = true;
            hang.Set();
        });

        Assert.Equal(PanelDeviceStatus.Timeout, status);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(interrupted);
        Assert.Equal(PanelDeviceStatus.Io, HidSharpSystem.Bounded(1000, () => PanelDeviceStatus.Io, () => throw new InvalidOperationException()));
    }

    // Endpoints: only the claimed interface's bulk endpoints, in the call's direction.

    private static UsbDescriptor ThreeInterfaces() => new(0xEF, 2, 1, 0x0100,
    [
        new UsbInterfaceDescriptor(0, 0x02, 2, 1, [(0x83, 3, 8)]),
        new UsbInterfaceDescriptor(1, 0xFF, 0, 0, [(0x01, 2, 512), (0x81, 2, 512), (0x84, 3, 64)]),
        new UsbInterfaceDescriptor(2, 0xFF, 0, 0, [(0x02, 2, 512), (0x82, 2, 512)]),
    ]);

    [Fact]
    public void A_transfer_on_an_endpoint_of_the_wrong_direction_another_interface_or_another_type_reaches_nothing()
    {
        var backend = new Backend(ThreeInterfaces());
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);
        var buffer = new byte[512];

        Assert.Equal(PanelDeviceStatus.NotSupported, usb!.BulkRead(0x01, buffer, 100, out _)); // an OUT endpoint
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkWrite(0x81, new byte[4], 100)); // an IN endpoint
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkWrite(0x02, new byte[4], 100)); // interface 2's
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkRead(0x82, buffer, 100, out _)); // interface 2's
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkRead(0x83, buffer, 100, out _)); // interface 0's interrupt
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkRead(0x84, buffer, 100, out _)); // its own, but interrupt
        Assert.Equal(PanelDeviceStatus.NotSupported, usb.BulkWrite(0x05, new byte[4], 100)); // none

        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("write", StringComparison.Ordinal) || c.StartsWith("read", StringComparison.Ordinal));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkWrite(0x01, new byte[4], 100));
        usb.Dispose();
    }

    [Fact]
    public void The_zero_length_packet_gets_only_what_is_left_of_the_deadline_and_a_cancel_between_transfers_stops_it()
    {
        var backend = new Backend(Descriptor()) { OnWrite = () => Thread.Sleep(300) };
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkWrite(0x01, new byte[1024], 400, zeroLengthPacketIfFull: true));
        Assert.Single(backend.ZlpTimeouts);
        Assert.InRange(backend.ZlpTimeouts[0], 1, 110);

        using var cancel = new CancellationTokenSource();
        backend.OnWrite = cancel.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => usb.BulkWrite(0x01, new byte[1024], 1000, zeroLengthPacketIfFull: true, cancel.Token));
        Assert.Single(backend.ZlpTimeouts);
        usb.Dispose();
    }

    [Fact]
    public void A_read_gathers_packets_that_come_across_slices_until_the_buffer_is_full()
    {
        var backend = new Backend(Descriptor());
        backend.Reads.Enqueue((UsbStatus.Timeout, 512));
        backend.Reads.Enqueue((UsbStatus.Timeout, 0));
        backend.Reads.Enqueue((UsbStatus.Timeout, 512));
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);

        Assert.Equal(PanelDeviceStatus.Ok, usb!.BulkRead(0x81, new byte[1024], 2000, out var read));

        Assert.Equal(1024, read);
        Assert.Equal(3, backend.Calls.Count(c => c.StartsWith("read", StringComparison.Ordinal)));
        Assert.Equal([1024, 512, 512], backend.ReadLengths);

        // A short packet ends it.
        backend.Reads.Enqueue((UsbStatus.Timeout, 512));
        backend.Reads.Enqueue((UsbStatus.Ok, 100));
        Assert.Equal(PanelDeviceStatus.Ok, usb.BulkRead(0x81, new byte[2048], 2000, out read));
        Assert.Equal(612, read);
        usb.Dispose();
    }

    // Dispose ends the call under way.

    [Fact]
    public void A_dispose_during_a_long_usb_write_returns_at_once_and_the_write_answers_io()
    {
        var backend = new Backend(Descriptor()) { OnWrite = () => Thread.Sleep(1500) };
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);
        var writing = Task.Run(() => usb!.BulkWrite(0x01, new byte[64], 60_000));
        Assert.True(SpinWait.SpinUntil(() => backend.Calls.Contains("write 01 64"), 2000));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        usb!.Dispose();

        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(PanelDeviceStatus.Io, writing.Result);
        Assert.True(SpinWait.SpinUntil(() => backend.Calls[^1] == "close", 2000), "closed once the write ended");
    }

    [Fact]
    public void A_dispose_wakes_the_calls_queued_behind_a_stalled_usb_write_with_io_and_a_queued_callers_cancel_still_throws()
    {
        var backend = new Backend(Descriptor()) { OnWrite = () => Thread.Sleep(1500) };
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);
        var writing = Task.Run(() => usb!.BulkWrite(0x01, new byte[64], 60_000));
        Assert.True(SpinWait.SpinUntil(() => backend.Calls.Contains("write 01 64"), 2000));
        var queued = Task.Run(() => usb!.BulkWrite(0x01, new byte[64], 1200));
        using var cancel = new CancellationTokenSource();
        var cancelled = Task.Run(() => usb!.BulkRead(0x81, new byte[64], 30_000, out _, cancel.Token));
        Thread.Sleep(100); // both wait for the write

        cancel.Cancel();
        Assert.True(SpinWait.SpinUntil(() => cancelled.IsCompleted, 1000), "the cancelled call ends");
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled.Exception?.InnerException);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        usb!.Dispose();

        Assert.True(queued.Wait(500), "the queued call ends at the dispose");
        Assert.Equal(PanelDeviceStatus.Io, queued.Result);
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(PanelDeviceStatus.Io, writing.Result);
        Assert.Single(backend.Calls, c => c.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dispose_during_a_hid_read_ends_it_with_io_at_once()
    {
        var hid = new FakeHid([Hid("x", null, serial: "S1")]);
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenHid(Candidate, new PanelHidFilter(), 1000, out var opened));
        var reading = Task.Run(() => opened!.Read(new byte[64], 30_000, out _));
        Assert.True(SpinWait.SpinUntil(() => hid.Streams[0].Reading, 2000));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        opened!.Dispose();

        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(reading.Wait(1000));
        Assert.Equal(PanelDeviceStatus.Io, reading.Result);
        Assert.True(hid.Streams[0].Disposed);
    }

    // One deadline from a call's start: the wait for another call and an open's lookups included.

    [Fact]
    public void A_short_usb_write_behind_a_long_read_answers_timeout_at_its_own_deadline_and_never_reaches_the_device()
    {
        var backend = new Backend(Descriptor()) { OnRead = () => Thread.Sleep(100) };
        Seam(backend).OpenUsb(Candidate, 1, 1000, out var usb);
        var reading = Task.Run(() => usb!.BulkRead(0x81, new byte[512], 1500, out _));
        Assert.True(SpinWait.SpinUntil(() => backend.Calls.Any(c => c.StartsWith("read", StringComparison.Ordinal)), 2000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, usb!.BulkWrite(0x01, new byte[4], 1));

        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
        using var cancel = new CancellationTokenSource(50);
        watch.Restart();
        Assert.ThrowsAny<OperationCanceledException>(() => usb.BulkWrite(0x01, new byte[4], 10_000, cancellationToken: cancel.Token));
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("write", StringComparison.Ordinal));
        reading.Wait(3000);
        usb.Dispose();
    }

    [Fact]
    public void A_hid_call_waiting_behind_another_keeps_to_its_deadline_and_a_cancel_while_it_waits_throws()
    {
        var hid = new FakeHid([Hid("x", null, serial: "S1")]);
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenHid(Candidate, new PanelHidFilter(), 1000, out var opened));
        var reading = Task.Run(() => opened!.Read(new byte[64], 1500, out _));
        Assert.True(SpinWait.SpinUntil(() => hid.Streams[0].Reading, 2000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, opened!.Write(new byte[8], 1));

        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
        using var cancel = new CancellationTokenSource(50);
        watch.Restart();
        Assert.ThrowsAny<OperationCanceledException>(() => opened.SetFeature(new byte[8], 10_000, cancel.Token));
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(0, hid.Streams[0].Calls);
        opened.Dispose();
        reading.Wait(3000);
    }

    [Fact]
    public void The_descriptor_lookup_and_the_hid_listing_are_inside_the_opens_deadline()
    {
        var backend = new Backend(Descriptor()) { OnDescribe = () => Thread.Sleep(400) };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PanelDeviceStatus.Timeout, Seam(backend).OpenUsb(Candidate, 1, 50, out var usb));

        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
        Assert.Null(usb);
        var hid = new FakeHid([Hid("x", null, serial: "S1")]) { OnList = () => Thread.Sleep(400) };
        watch.Restart();
        Assert.Equal(PanelDeviceStatus.Timeout, new EnginePanelDevices(null, hid, null, _log.Logger).OpenHid(Candidate, new PanelHidFilter(), 50, out var opened));
        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
        Assert.Null(opened);
    }

    [Fact]
    public void The_zero_length_packet_never_gets_a_timeout_of_0_which_libusb_takes_as_none()
    {
        var backend = new Backend(Descriptor());
        var afterWrite = 0;
        // The time runs out between two looks after the write: 1 ms left at the first, none at the next.
        var seam = new EnginePanelDevices(backend, null, null, _log.Logger)
        {
            Deadlines = _ => new DeviceDeadline(() => !backend.Calls.Contains("write 01 1024") ? 50 : afterWrite++ == 0 ? 1 : 0),
        };
        seam.OpenUsb(Candidate, 1, 1000, out var usb);

        var status = usb!.BulkWrite(0x01, new byte[1024], 1000, zeroLengthPacketIfFull: true);

        Assert.Equal(PanelDeviceStatus.Ok, status);
        Assert.Equal([1], backend.ZlpTimeouts);
        usb.Dispose();
    }

    // HID: exclusive opens, reservations, composite devices.

    [Fact]
    public void A_retry_with_another_collection_of_the_panels_device_keeps_that_device()
    {
        // A sorts first but fails the driver's filter; B has two collections that pass it.
        HidEntry a = Hid("a", null, output: 65, parent: @"USB\VID_0416&PID_5302\PA");
        HidEntry b1 = Hid("b1", null, 0, parent: @"USB\VID_0416&PID_5302\PB"), b2 = Hid("b2", null, 1, parent: @"USB\VID_0416&PID_5302\PB");
        var hid = new FakeHid([a, b1, b2]);
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(Candidate, new PanelHidFilter(MinOutputReport: 513), out var listed));
        Assert.Equal(["b1", "b2"], listed.Select(c => c.Id));
        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenHid(Candidate, listed[0], 1000, out var first));
        first!.Dispose(); // its handshake failed: the driver tries the next

        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenHid(Candidate, listed[1], 1000, out var second));

        Assert.Equal("b2", second!.Collection.Id);
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(Candidate with { Address = 8, PortPath = "1-2.4" }, new PanelHidFilter(), out var others));
        Assert.Equal(["a"], others.Select(c => c.Id)); // A is still free for another panel
        second.Dispose();
    }

    [Fact]
    public void A_collection_at_the_port_whose_device_has_another_bus_or_address_is_not_the_candidates()
    {
        var chooser = new HidChooser(_log.Logger);

        Assert.Equal(PanelDeviceStatus.Gone, chooser.Collections(Candidate, [Hid("a", "1-2.3", bus: 1, address: 9)], new PanelHidFilter(), out var none));
        Assert.Empty(none);
        Assert.Equal(PanelDeviceStatus.Gone, chooser.Collections(Candidate, [Hid("a", "1-2.3", bus: 2, address: 7)], new PanelHidFilter(), out none));
        Assert.Empty(none);
        Assert.Equal(PanelDeviceStatus.Ok, chooser.Collections(Candidate, [Hid("a", "1-2.3", bus: 1, address: 7)], new PanelHidFilter(), out var it));
        Assert.Single(it);
        Assert.Equal("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2.3", HidChooser.LinuxUsbDevice("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2.3/1-2.3:1.1/0003:0416:5302.0004"));
        Assert.Null(HidChooser.LinuxUsbDevice("/sys/devices/virtual/misc/uhid/0003:0416:5302.0004"));
        Assert.Equal("IOService:/AppleARMPE/arm-io@10F00000/AppleT8103USBXHCI@14000000/usb-drd1-port-hs@14200000/USB2.0 Hub@14200000/AppleUSB20HubPort@14210000/LCD@14210000",
            HidChooser.MacUsbDevice("IOService:/AppleARMPE/arm-io@10F00000/AppleT8103USBXHCI@14000000/usb-drd1-port-hs@14200000/USB2.0 Hub@14200000/AppleUSB20HubPort@14210000/LCD@14210000/IOUSBHostInterface@0/AppleUserUSBHostHIDDevice"));
    }

    [Fact]
    public void Where_the_system_gives_the_port_a_collection_whose_bus_or_address_cant_be_read_is_refused_with_the_reason()
    {
        var chooser = new HidChooser(_log.Logger);

        // Linux or macOS couldn't read the device's address (or bus): no evidence it is still the candidate.
        Assert.Equal(PanelDeviceStatus.Busy, chooser.Collections(Candidate, [Hid("a", "1-2.3", bus: 1)], new PanelHidFilter(), out var none));
        Assert.Empty(none);
        Assert.Equal(PanelDeviceStatus.Busy, chooser.Collections(Candidate, [Hid("a", "1-2.3", address: 7)], new PanelHidFilter(), out none));
        Assert.Empty(none);
        Assert.Contains(_log.Lines, l => l.Contains("bus and address can't be read"));
        Assert.Equal(PanelDeviceStatus.Busy, chooser.ForCollection(Candidate, [Hid("a", "1-2.3", bus: 1)], "a", out var entry));
        Assert.Null(entry);
        // After the open, a listing that no longer tells them is no proof either.
        Assert.False(HidChooser.IsStill(Hid("a", "1-2.3", bus: 1, address: 7), [Hid("a", "1-2.3", bus: 1)], Candidate));
        var hid = new FakeHid([Hid("a", "1-2.3", bus: 1)]);
        Assert.Equal(PanelDeviceStatus.Busy, new EnginePanelDevices(null, hid, null, _log.Logger).OpenHid(Candidate, new PanelHidFilter(), 1000, out var opened));
        Assert.Null(opened);
        Assert.Empty(hid.Streams);
        // Windows' exception stands: its collections carry no port and no address.
        Assert.Equal(PanelDeviceStatus.Ok, new HidChooser(_log.Logger).Collections(Candidate, [Hid("w", null, parent: @"USB\VID_0416&PID_5302\PA")], new PanelHidFilter(), out var windows));
        Assert.Single(windows);
    }

    [Fact]
    public void A_device_that_took_the_listed_ones_place_before_the_open_is_closed_at_once_and_gone()
    {
        var hid = new FakeHid([Hid("a", "1-2.3", bus: 1, address: 7)]);
        hid.OnOpen = () => hid.Entries = [Hid("a", "1-2.3", bus: 1, address: 8)]; // unplugged and another plugged in its place
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(Candidate, new PanelHidFilter(), out var listed));

        Assert.Equal(PanelDeviceStatus.Gone, seam.OpenHid(Candidate, listed[0], 1000, out var opened));

        Assert.Null(opened);
        var stream = Assert.Single(hid.Streams);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.Calls);
    }

    [Fact]
    public void Hid_opens_are_exclusive_and_a_collection_held_elsewhere_answers_busy_with_nothing_written()
    {
        var options = HidSharpSystem.OpenOptions(750);
        Assert.Equal(true, options.GetOption(HidSharp.OpenOption.Exclusive));
        Assert.Equal(false, options.GetOption(HidSharp.OpenOption.Interruptible));
        Assert.Equal(false, options.GetOption(HidSharp.OpenOption.Transient));
        Assert.Equal(750, options.GetOption(HidSharp.OpenOption.TimeoutIfInterruptible));
        Assert.Equal(750, options.GetOption(HidSharp.OpenOption.TimeoutIfTransient));

        var hid = new FakeHid([Hid("x", null, serial: "S1")]) { OpenResult = PanelDeviceStatus.Busy };
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        Assert.Equal(PanelDeviceStatus.Busy, seam.OpenHid(Candidate, new PanelHidFilter(), 1000, out var opened));
        Assert.Null(opened);
        Assert.Empty(hid.Streams);
    }

    [Fact]
    public void A_single_device_is_reserved_so_an_identical_one_plugged_later_gets_its_own()
    {
        HidEntry a = Hid("a", null, parent: @"USB\VID_0416&PID_5302\PA"), b = Hid("b", null, parent: @"USB\VID_0416&PID_5302\PB");
        var hid = new FakeHid([a]);
        var seam = new EnginePanelDevices(null, hid, null, _log.Logger);
        var panelB = Candidate with { Address = 8, PortPath = "1-2.4" };

        Assert.Equal(PanelDeviceStatus.Ok, seam.OpenHid(Candidate, new PanelHidFilter(), 1000, out var first)); // A alone
        hid.Entries = [a, b]; // B plugged in while A runs
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(panelB, new PanelHidFilter(), out var bs));

        Assert.Equal(["b"], bs.Select(c => c.Id));
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(Candidate, new PanelHidFilter(), out var asAgain));
        Assert.Equal(["a"], asAgain.Select(c => c.Id));

        // Closed, A's is free again for a third panel once B keeps its own.
        first!.Dispose();
        Assert.Equal(PanelDeviceStatus.Ok, seam.ListHid(Candidate with { Address = 9, PortPath = "1-2.5" }, new PanelHidFilter(), out var third));
        Assert.Equal(["a"], third.Select(c => c.Id));
    }

    [Fact]
    public void A_listing_of_other_ids_never_drops_a_reservation()
    {
        var chooser = new HidChooser(_log.Logger);
        PanelCandidate d = new(0x1111, 0x2222, 1, 4, "1-4"), e = new(0x1111, 0x2222, 1, 5, "1-5");
        HidEntry[] others = [Hid("q", null, serial: "Q", vid: 0x1111, pid: 0x2222), Hid("r", null, serial: "R", vid: 0x1111, pid: 0x2222)];

        chooser.Collections(d, others, new PanelHidFilter(), out var dq);
        chooser.Collections(Candidate, [Hid("x", null, serial: "S1"), Hid("y", null, serial: "S2")], new PanelHidFilter(), out _); // a listing without them
        chooser.Collections(e, others, new PanelHidFilter(), out var er);

        Assert.Equal(["q"], dq.Select(c => c.Path));
        Assert.Equal(["r"], er.Select(c => c.Path));
    }

    [Fact]
    public void A_composite_devices_collections_are_one_device_and_the_filter_comes_before_the_choice()
    {
        var chooser = new HidChooser(_log.Logger);
        const string parent = @"USB\VID_0416&PID_5302\6&1A2B3C&0&3";
        HidEntry[] listed = [Hid("a", null, 0, output: 65, parent: parent), Hid("b", null, 1, output: 513, parent: parent)];

        Assert.Equal(PanelDeviceStatus.Ok, chooser.Collections(Candidate, listed, new PanelHidFilter(Interface: 1), out var second));
        Assert.Equal(["b"], second.Select(e => e.Path));
        chooser.Collections(Candidate, listed, new PanelHidFilter(), out var all);
        Assert.Equal(["a", "b"], all.Select(e => e.Path));

        Assert.Equal(@"USB\VID_0416&PID_5302\5&3&0&4", HidChooser.WindowsUsbParent(
            [@"HID\VID_0416&PID_5302&MI_01\7&1&0&0000", @"USB\VID_0416&PID_5302&MI_01\6&2&0&1", @"USB\VID_0416&PID_5302\5&3&0&4", @"USB\ROOT_HUB30\4&1&0&0"]));
        Assert.Null(HidChooser.WindowsUsbParent([@"HID\VID_0416&PID_5302\7&1&0&0000", @"ROOT\UHID\0000"]));
    }

    /// <summary>A USB backend with one device at <see cref="Candidate"/>: every call recorded by name.</summary>
    private sealed class Backend(UsbDescriptor descriptor) : IUsbBackend
    {
        private readonly List<string> _calls = [];

        public int Configuration { get; set; } = 1;

        public UsbStatus ClaimResult { get; set; } = UsbStatus.Ok;

        public string? Serial { get; set; }

        public bool Listed { get; set; } = true;

        public Action? OnRead { get; set; }

        public Action? OnWrite { get; set; }

        public Action? OnOpen { get; set; }

        /// <summary>Scripted reads (status, bytes), in turn; none left: a timeout with nothing.</summary>
        public Queue<(UsbStatus Status, int Bytes)> Reads { get; } = new();

        public List<int> ReadLengths { get; } = [];

        public List<int> ZlpTimeouts { get; } = [];

        public string[] Calls
        {
            get
            {
                lock (_calls)
                    return [.. _calls];
            }
        }

        private void Call(string name)
        {
            lock (_calls)
                _calls.Add(name);
        }

        private UsbDeviceId Id => EnginePanelDevicesTests.Candidate is var c ? new UsbDeviceId(c.VendorId, c.ProductId, c.Bus, c.Address, c.PortPath) : null!;

        public IReadOnlyList<UsbDeviceId> ListDevices() => Listed ? [Id] : [];

        public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
        {
            Call("open");
            OnOpen?.Invoke();
            opened = new Device(this);
            return UsbStatus.Ok;
        }

        public Action? OnDescribe { get; set; }

        public UsbStatus Describe(UsbDeviceId device, out UsbDescriptor? described)
        {
            OnDescribe?.Invoke();
            described = Listed && device == Id ? descriptor : null;
            return described != null ? UsbStatus.Ok : UsbStatus.NoDevice;
        }

        public void Dispose()
        {
        }

        private sealed class Device(Backend owner) : IUsbDevice
        {
            public UsbStatus GetConfiguration(out int configuration)
            {
                owner.Call("getConfiguration");
                configuration = owner.Configuration;
                return UsbStatus.Ok;
            }

            public UsbStatus SetConfiguration(int configuration)
            {
                owner.Call($"setConfiguration {configuration}");
                return UsbStatus.Ok;
            }

            public UsbStatus ClaimInterface(int interfaceNumber)
            {
                owner.Call($"claim {interfaceNumber}");
                return owner.ClaimResult;
            }

            public UsbStatus ReleaseInterface(int interfaceNumber)
            {
                owner.Call($"release {interfaceNumber}");
                return UsbStatus.Ok;
            }

            public string? ReadSerialNumber()
            {
                owner.Call("serial");
                return owner.Serial;
            }

            public UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
            {
                owner.Call($"write {endpoint:x2} {data.Length}");
                owner.OnWrite?.Invoke();
                return UsbStatus.Ok;
            }

            public UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read)
            {
                owner.Call($"read {endpoint:x2} {timeoutMs}");
                owner.ReadLengths.Add(buffer.Length);
                owner.OnRead?.Invoke();
                if (owner.Reads.TryDequeue(out var scripted))
                {
                    read = Math.Min(scripted.Bytes, buffer.Length);
                    buffer[..read].Fill(0x5A);
                    return scripted.Status;
                }
                read = 0;
                return UsbStatus.Timeout;
            }

            public UsbStatus WriteZeroLengthPacket(byte endpoint, int timeoutMs)
            {
                owner.Call($"zlp {endpoint:x2}");
                owner.ZlpTimeouts.Add(timeoutMs);
                return UsbStatus.Ok;
            }

            public void Dispose() => owner.Call("close");
        }
    }

    /// <summary>The OS's HID devices faked: the listed collections, an open's answer, and every stream opened.</summary>
    private sealed class FakeHid(IReadOnlyList<HidEntry> entries) : IHidSystem
    {
        public IReadOnlyList<HidEntry> Entries { get; set; } = entries;

        public PanelDeviceStatus OpenResult { get; set; } = PanelDeviceStatus.Ok;

        public Action? OnList { get; set; }

        public Action? OnOpen { get; set; }

        public List<FakeHidStream> Streams { get; } = [];

        public IReadOnlyList<HidEntry> List(ushort vendorId, ushort productId)
        {
            OnList?.Invoke();
            return [.. Entries.Where(e => e.VendorId == vendorId && e.ProductId == productId)];
        }

        public PanelDeviceStatus Open(HidEntry entry, int timeoutMs, out IHidStream? stream)
        {
            stream = null;
            OnOpen?.Invoke();
            if (OpenResult != PanelDeviceStatus.Ok)
                return OpenResult;
            var opened = new FakeHidStream();
            lock (Streams)
                Streams.Add(opened);
            stream = opened;
            return PanelDeviceStatus.Ok;
        }
    }

    /// <summary>A HID stream whose read waits until it is disposed (then answers <see cref="PanelDeviceStatus.Io"/>) or its timeout; every other call counted.</summary>
    private sealed class FakeHidStream : IHidStream
    {
        private readonly ManualResetEventSlim _closed = new();
        private int _calls;

        public volatile bool Reading;

        public bool Disposed => _closed.IsSet;

        /// <summary>The writes and feature calls that reached it.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public PanelDeviceStatus Write(byte[] report, int timeoutMs)
        {
            Interlocked.Increment(ref _calls);
            return Disposed ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;
        }

        public PanelDeviceStatus Read(byte[] buffer, int timeoutMs, out int read)
        {
            Reading = true;
            read = 0;
            return _closed.Wait(timeoutMs) ? PanelDeviceStatus.Io : PanelDeviceStatus.Timeout;
        }

        public PanelDeviceStatus SetFeature(byte[] report, int timeoutMs)
        {
            Interlocked.Increment(ref _calls);
            return PanelDeviceStatus.Ok;
        }

        public PanelDeviceStatus GetFeature(byte[] buffer, int timeoutMs, out int read)
        {
            Interlocked.Increment(ref _calls);
            read = buffer.Length;
            return PanelDeviceStatus.Ok;
        }

        public void Dispose() => _closed.Set();
    }

    /// <summary>The OS's serial ports faked: the finder's and the check's answers, every port opened, and every step in order.</summary>
    private sealed class FakeSerial : ISerialSystem
    {
        public Func<PanelCandidate, CancellationToken, (PanelDeviceStatus Status, string? Port)> Finder { get; set; } = (_, _) => (PanelDeviceStatus.NotSupported, null);

        public Func<IOpenedSerial, PanelDeviceStatus> Verifier { get; set; } = _ => PanelDeviceStatus.Ok;

        public Action? OnOpen { get; set; }

        public List<FakePort> Ports { get; } = [];

        public List<string> Events { get; } = [];

        public int Opens
        {
            get
            {
                lock (Ports)
                    return Ports.Count;
            }
        }

        public void Event(string step)
        {
            lock (Events)
                Events.Add(step);
        }

        public PanelDeviceStatus Find(PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken, out string? port)
        {
            Event("find");
            (var status, port) = Finder(device, cancellationToken);
            return status;
        }

        public PanelDeviceStatus Open(string port, int baud, out IOpenedSerial? serial)
        {
            Event("open " + port);
            OnOpen?.Invoke();
            var opened = new FakePort(port, this);
            lock (Ports)
                Ports.Add(opened);
            serial = opened;
            return PanelDeviceStatus.Ok;
        }

        public PanelDeviceStatus Verify(IOpenedSerial serial, PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken)
        {
            Event("verify " + serial.PortName);
            return Verifier(serial);
        }
    }

    private sealed class FakePort(string name, FakeSerial owner) : IOpenedSerial
    {
        public volatile bool Disposed;

        public string PortName => name;

        public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default)
        {
            owner.Event("write");
            return PanelDeviceStatus.Ok;
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            read = 0;
            return PanelDeviceStatus.Timeout;
        }

        public PanelDeviceStatus DiscardInput() => PanelDeviceStatus.Ok;

        public PanelDeviceStatus SetLines(bool dtr, bool rts)
        {
            owner.Event($"lines {dtr} {rts}");
            return PanelDeviceStatus.Ok;
        }

        public void Dispose()
        {
            if (!Disposed)
                owner.Event("close " + name);
            Disposed = true;
        }
    }

    /// <summary>
    /// A raw serial port: a read waits a slice for nothing, or ends once it is closed; the close waits (up to 3 s) for the
    /// queued output to be thrown away, as System.IO.Ports' flush waits for it to go, or hangs 3 s whatever is done.
    /// </summary>
    private sealed class FakeLine : ISerialLine
    {
        private readonly ManualResetEventSlim _discarded = new();
        private readonly ManualResetEventSlim _closed = new();
        private readonly List<string> _steps = [];

        public bool CloseWaitsForDiscard { get; init; } = true;

        public string[] Steps
        {
            get
            {
                lock (_steps)
                    return [.. _steps];
            }
        }

        private void Step(string step)
        {
            lock (_steps)
                _steps.Add(step);
        }

        public string PortName => "/dev/ttyACM1";

        public bool IsOpen => !_closed.IsSet;

        public void Write(byte[] data, int timeoutMs) => Step("write");

        public int Read(byte[] buffer, int timeoutMs)
        {
            Step("read");
            if (_closed.Wait(timeoutMs))
                throw new IOException("closed");
            throw new TimeoutException();
        }

        public void DiscardInput() => Step("discardIn");

        public void DiscardOutput()
        {
            Step("discardOut");
            _discarded.Set();
        }

        public void SetLines(bool dtr, bool rts) => Step($"lines {dtr} {rts}");

        public void Close()
        {
            Step("close");
            if (CloseWaitsForDiscard)
                _discarded.Wait(3000);
            else
                Thread.Sleep(3000);
            _closed.Set();
        }
    }
}
