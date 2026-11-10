using InfoPanel.Sensors;
using InfoPanel.Sensors.Linux;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Linux;

/// <summary>
/// The Linux sensor source (<c>infopanel.linux</c>) on sysfs trees made by <see cref="SysfsKit"/>: hwmon channels and
/// thermal zones under the Linux fork's ids, each device anchored by what identifies it (never the kernel's
/// <c>hwmonN</c>), values in °C, RPM, V, A, W, MHz and %, rescanned every 10 s, and what can't be read never read as 0.
/// </summary>
public sealed class LinuxSensorSourceTests : IDisposable
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly List<LogEvent> _events = [];
    private LinuxSensorSource? _source;

    public void Dispose() => _source?.Dispose();

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>
    /// The source over <paramref name="sysfs"/>, discovered and polled once, then every sensor it lists put in use (as a
    /// profile showing them all would) and polled again at the same moment, so each is read.
    /// </summary>
    private LinuxSensorSource Open(Sysfs sysfs)
    {
        OpenIdle(sysfs);
        foreach (var sensor in _source!.GetSensors())
            _source.NoteUse(sensor.Key);
        _source.Poll();
        return _source;
    }

    /// <summary>The source over <paramref name="sysfs"/>, discovered and polled once, nothing in use.</summary>
    private LinuxSensorSource OpenIdle(Sysfs sysfs)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = LinuxSensorSource.Create(sysfs, _time, log: log);
        _source.Open();
        return _source;
    }

    /// <summary>A consumer uses the sensor (a graph's draw), as a read would.</summary>
    private void Use(string id) => _source!.NoteUse(Key(id));

    /// <summary>One poll a second, <paramref name="count"/> times.</summary>
    private void Round(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _source!.Poll();
        }
    }

    /// <summary>Polls until the next rescan has run and its catalogue is listed.</summary>
    private void Rescan() => Round((int)LinuxSensorSampler.RescanInterval.TotalSeconds);

    private static SensorKey Key(string id) => LinuxSensorSource.KeyOf(id);

    private string[] Ids() => _source!.GetSensors().Select(s => s.Key.Id[LinuxSensorSource.KeyPrefix.Length..]).ToArray();

    private SensorInfo Info(string id) => Assert.Single(_source!.GetSensors(), s => s.Key == Key(id));

    private double Value(string id)
    {
        Assert.Equal(SensorStatus.Live, _source!.GetStatus(Key(id)));
        Assert.True(_source.TryRead(Key(id), out var reading));
        return reading.ValueNow;
    }

    [Fact]
    public void The_VFD_templates_ids_are_listed_exactly()
    {
        Open(SysfsKit.VfdMachine());

        var ids = Ids();

        Assert.Contains("hwmon/v1/it8696/platform+it87.2624/fan1", ids);
        Assert.Contains("hwmon/v1/wireview/platform+wireview_hwmon/curr1", ids);
        Assert.Equal("infopanel:/linux/hwmon/v1/it8696/platform+it87.2624/fan1", Key("hwmon/v1/it8696/platform+it87.2624/fan1").ToString());
        Assert.Equal(1250, Value("hwmon/v1/it8696/platform+it87.2624/fan1"));
        Assert.Equal(8.25, Value("hwmon/v1/wireview/platform+wireview_hwmon/curr1"));
        Assert.Equal(LinuxSensorSource.SourceId, Info("hwmon/v1/it8696/platform+it87.2624/fan1").SourceId);
        Assert.Equal(
        [
            "hwmon/v1/acpitz/type+acpitz~acpi+%5C_TZ_.TZ00/temp1",
            "hwmon/v1/it8696/platform+it87.2624/fan1",
            "hwmon/v1/it8696/platform+it87.2624/fan2",
            "hwmon/v1/it8696/platform+it87.2624/in0",
            "hwmon/v1/it8696/platform+it87.2624/in1",
            "hwmon/v1/it8696/platform+it87.2624/temp1",
            "hwmon/v1/it8696/platform+it87.2624/temp2",
            "hwmon/v1/k10temp/pci+0000-00-18.3/temp1",
            "hwmon/v1/k10temp/pci+0000-00-18.3/temp3",
            "hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0001~pci+0000-01-00.0/temp1",
            "hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0001~pci+0000-01-00.0/temp2",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr1",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr2",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr3",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr4",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr5",
            "hwmon/v1/wireview/platform+wireview_hwmon/curr6",
            "hwmon/v1/wireview/platform+wireview_hwmon/in1",
            "hwmon/v1/wireview/platform+wireview_hwmon/power1",
            "hwmon/v1/wireview/platform+wireview_hwmon/temp1",
            "thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp",
        ], ids);
    }

    [Fact]
    public void A_channel_is_named_by_its_label_else_its_channel_and_grouped_by_its_chip()
    {
        Open(SysfsKit.VfdMachine());

        var tctl = Info("hwmon/v1/k10temp/pci+0000-00-18.3/temp1");
        var fan = Info("hwmon/v1/it8696/platform+it87.2624/fan1");
        var zone = Info("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp");

        Assert.Equal(("k10temp (0000-00-18.3)", "Tctl", "°C"), (tctl.Group, tctl.Name, tctl.Unit));
        Assert.Equal(("it8696 (it87.2624)", "fan1", "RPM"), (fan.Group, fan.Name, fan.Unit));
        Assert.Equal(("Thermal zones", "acpitz", "°C"), (zone.Group, zone.Name, zone.Unit));
    }

    // One test per anchor kind: what identifies the device that owns the hwmon device.

    [Fact]
    public void Anchor_nvme_serial_is_the_controllers_serial_with_its_PCI_function()
    {
        var kit = new SysfsKit();
        kit.AddNvme(7, "S5GX NF0R%123");
        Open(kit);

        Assert.Equal(["hwmon/v1/nvme/nvme-serial+S5GX%20NF0R%25123~pci+0000-01-00.0/temp1", "hwmon/v1/nvme/nvme-serial+S5GX%20NF0R%25123~pci+0000-01-00.0/temp2"], Ids());
        Assert.Equal(38.85, Value("hwmon/v1/nvme/nvme-serial+S5GX%20NF0R%25123~pci+0000-01-00.0/temp1"));
    }

    [Fact]
    public void Anchor_block_wwid_is_the_disks_WWID()
    {
        var kit = new SysfsKit();
        kit.AddDrivetemp(1, "naa.5000c500a1b2c3d4", "WD-123");
        Open(kit);

        Assert.Equal(["hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1"], Ids());
    }

    [Fact]
    public void Anchor_block_serial_is_the_disks_serial_without_a_WWID()
    {
        var kit = new SysfsKit();
        kit.AddDrivetemp(1, wwid: null, "WD-123");
        Open(kit);

        Assert.Equal(["hwmon/v1/drivetemp/block-serial+WD-123/temp1"], Ids());
    }

    [Fact]
    public void Anchor_i2c_is_the_adapters_name_and_the_address_with_the_adapters_owner()
    {
        var kit = new SysfsKit();
        kit.AddI2c(4);
        Open(kit);

        Assert.Equal(["hwmon/v1/nct7802/i2c+SMBus%20I801%20adapter+002d~adapter+pci%2B0000-00-1f.4/temp1"], Ids());
    }

    [Fact]
    public void Anchor_usb_serial_is_the_USB_devices_ids_and_serial()
    {
        var kit = new SysfsKit();
        kit.AddUsb(5, "FX0003");
        Open(kit);

        Assert.Equal(["hwmon/v1/corsaircpro/usb-serial+1b1c+0c10+FX0003/fan1", "hwmon/v1/corsaircpro/usb-serial+1b1c+0c10+FX0003/temp1"], Ids());
    }

    [Fact]
    public void Anchor_usb_port_is_the_USB_devices_ids_its_host_and_its_ports_without_a_serial()
    {
        var kit = new SysfsKit();
        kit.AddUsb(5, serial: null);
        Open(kit);

        Assert.Equal(["hwmon/v1/corsaircpro/usb-port+1b1c+0c10+pci%2B0000-00-14.0+3.2/fan1", "hwmon/v1/corsaircpro/usb-port+1b1c+0c10+pci%2B0000-00-14.0+3.2/temp1"], Ids());
    }

    [Fact]
    public void Anchor_pci_is_the_nearest_PCI_function_with_a_PHYs_role()
    {
        var kit = new SysfsKit();
        kit.AddK10Temp(2);
        kit.AddR8169(1);
        Open(kit);

        Assert.Equal(["hwmon/v1/k10temp/pci+0000-00-18.3/temp1", "hwmon/v1/k10temp/pci+0000-00-18.3/temp3", "hwmon/v1/r8169/pci+0000-ab-00.0~role+mdio+00/temp1"], Ids());
    }

    [Fact]
    public void Anchor_platform_is_the_platform_devices_name()
    {
        var kit = new SysfsKit();
        kit.AddWireView(0);
        Open(kit);

        Assert.All(Ids(), id => Assert.StartsWith("hwmon/v1/wireview/platform+wireview_hwmon/", id));
        Assert.Equal(9, Ids().Length);
    }

    [Fact]
    public void Anchor_type_is_the_thermal_zones_type_for_its_hwmon_device_and_the_zone()
    {
        var kit = new SysfsKit();
        kit.AddAcpiZone(0, zone: 0);
        kit.AddIwlwifi(6, zone: 5);
        Open(kit);

        Assert.Equal(
        [
            "hwmon/v1/acpitz/type+acpitz~acpi+%5C_TZ_.TZ00/temp1",
            "hwmon/v1/iwlwifi/type+iwlwifi/temp1",
            "thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp",
            "thermal/v1/type+iwlwifi/temp",
        ], Ids());
        Assert.Equal(16.8, Value("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp"));
        Assert.Equal(45, Value("thermal/v1/type+iwlwifi/temp"));
    }

    [Fact]
    public void Anchor_name_is_the_chip_for_a_device_with_no_owner()
    {
        var kit = new SysfsKit();
        kit.Hwmon(3, owner: null, "acpi_fan", ("fan1_input", "2100"));
        Open(kit);

        Assert.Equal(["hwmon/v1/acpi_fan/name+acpi_fan/fan1"], Ids());
        Assert.Equal(2100, Value("hwmon/v1/acpi_fan/name+acpi_fan/fan1"));
    }

    [Fact]
    public void Values_are_scaled_from_the_kernels_units()
    {
        var kit = new SysfsKit();
        kit.Hwmon(0, owner: null, "fixture",
            ("temp1_input", "42000"), ("fan1_input", "1250"), ("in0_input", "1032"), ("curr1_input", "8250"),
            ("power1_input", "99000000"), ("freq1_input", "1800000000"), ("humidity1_input", "45500"), ("temp2_input", "-5500"));
        Open(kit);

        string Id(string channel) => "hwmon/v1/fixture/name+fixture/" + channel;
        Assert.Equal((42.0, "°C"), (Value(Id("temp1")), Info(Id("temp1")).Unit));
        Assert.Equal((-5.5, "°C"), (Value(Id("temp2")), Info(Id("temp2")).Unit));
        Assert.Equal((1250.0, "RPM"), (Value(Id("fan1")), Info(Id("fan1")).Unit));
        Assert.Equal((1.032, "V"), (Value(Id("in0")), Info(Id("in0")).Unit));
        Assert.Equal((8.25, "A"), (Value(Id("curr1")), Info(Id("curr1")).Unit));
        Assert.Equal((99.0, "W"), (Value(Id("power1")), Info(Id("power1")).Unit));
        Assert.Equal((1800.0, "MHz"), (Value(Id("freq1")), Info(Id("freq1")).Unit));
        Assert.Equal((45.5, "%"), (Value(Id("humidity1")), Info(Id("humidity1")).Unit));
    }

    [Fact]
    public void Power_average_is_read_under_the_same_id_when_there_is_no_power_input()
    {
        var kit = new SysfsKit();
        kit.Hwmon(0, owner: null, "amdgpu",
            ("power1_average", "65000000"), ("power2_input", "10000000"), ("power2_average", "20000000"), ("power3_cap", "300000000"));
        Open(kit);

        Assert.Equal(["hwmon/v1/amdgpu/name+amdgpu/power1", "hwmon/v1/amdgpu/name+amdgpu/power2"], Ids());
        Assert.Equal(65, Value("hwmon/v1/amdgpu/name+amdgpu/power1"));
        Assert.Equal(10, Value("hwmon/v1/amdgpu/name+amdgpu/power2"));
    }

    [Fact]
    public void Renumbered_hwmon_devices_keep_their_keys()
    {
        var first = SysfsKit.VfdMachine([0, 1, 2, 3, 4]);
        Open(first);
        var before = Ids();
        _source!.Dispose();

        var renumbered = SysfsKit.VfdMachine([4, 2, 0, 3, 1]);
        renumbered.File("devices/platform/it87.2624/hwmon/hwmon4/fan1_input", "1300\n");
        Open(renumbered);

        Assert.Equal(before, Ids());
        Assert.Equal(1300, Value("hwmon/v1/it8696/platform+it87.2624/fan1"));
    }

    [Fact]
    public void A_device_renumbered_while_running_is_read_where_it_now_is_under_the_same_key()
    {
        var kit = new SysfsKit();
        kit.AddIt8696(3);
        Open(kit);
        var before = Ids();

        // The driver reloaded: hwmon3 is gone, the same chip is hwmon8 now.
        kit.Remove("devices/platform/it87.2624/hwmon/hwmon3").Remove("class/hwmon/hwmon3");
        kit.AddIt8696(8);
        kit.File("devices/platform/it87.2624/hwmon/hwmon8/fan1_input", "1400\n");
        Rescan();

        Assert.Equal(before, Ids());
        Assert.Equal(1400, Value("hwmon/v1/it8696/platform+it87.2624/fan1"));
    }

    [Fact]
    public void A_device_added_or_removed_between_scans_changes_the_catalogue()
    {
        var kit = new SysfsKit();
        kit.AddIt8696(0);
        Open(kit);
        Assert.DoesNotContain(Ids(), id => id.StartsWith("hwmon/v1/nvme/", StringComparison.Ordinal));

        kit.AddNvme(1, "FIXTURE-NVME-0002");
        Round();
        Assert.DoesNotContain(Ids(), id => id.StartsWith("hwmon/v1/nvme/", StringComparison.Ordinal));
        Rescan();
        Assert.Contains("hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0002~pci+0000-01-00.0/temp1", Ids());
        // Listed now; read once something uses it.
        Use("hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0002~pci+0000-01-00.0/temp1");
        Round();
        Assert.Equal(38.85, Value("hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0002~pci+0000-01-00.0/temp1"));

        kit.Remove("devices/pci0000:00/0000:00:01.1/0000:01:00.0").Remove("class/hwmon/hwmon1");
        Round();
        // Its files are gone: no reading at once, never 0; listed until the next scan.
        Assert.Equal(SensorStatus.Unavailable, _source!.GetStatus(Key("hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0002~pci+0000-01-00.0/temp1")));
        Assert.False(_source.TryRead(Key("hwmon/v1/nvme/nvme-serial+FIXTURE-NVME-0002~pci+0000-01-00.0/temp1"), out _));
        Rescan();
        Assert.DoesNotContain(Ids(), id => id.StartsWith("hwmon/v1/nvme/", StringComparison.Ordinal));
        Assert.Contains("hwmon/v1/it8696/platform+it87.2624/fan1", Ids());
    }

    [Fact]
    public void An_unreadable_file_reads_permission_denied_never_0()
    {
        var kit = new SysfsKit();
        var dir = kit.AddIt8696(0);
        kit.File(dir + "/fan2_input", "980\n", denied: true);
        Open(kit);

        var key = Key("hwmon/v1/it8696/platform+it87.2624/fan2");
        Assert.Contains("hwmon/v1/it8696/platform+it87.2624/fan2", Ids());
        Assert.Equal(SensorStatus.PermissionDenied, _source!.GetStatus(key));
        Assert.False(_source.TryRead(key, out _));
        Assert.Equal(1250, Value("hwmon/v1/it8696/platform+it87.2624/fan1"));
    }

    [Fact]
    public void A_missing_file_reads_unavailable_never_0()
    {
        var kit = new SysfsKit();
        var dir = kit.AddIt8696(0);
        Open(kit);
        var key = Key("hwmon/v1/it8696/platform+it87.2624/fan2");
        Assert.Equal(980, Value("hwmon/v1/it8696/platform+it87.2624/fan2"));

        kit.Remove(dir + "/fan2_input");
        Round();

        Assert.Equal(SensorStatus.Unavailable, _source!.GetStatus(key));
        Assert.False(_source.TryRead(key, out _));
    }

    [Fact]
    public void A_value_that_does_not_read_as_a_number_is_no_reading_never_0()
    {
        var kit = new SysfsKit();
        var dir = kit.AddIt8696(0);
        kit.File(dir + "/fan2_input", "\n");
        Open(kit);

        var key = Key("hwmon/v1/it8696/platform+it87.2624/fan2");
        Assert.Equal(SensorStatus.Unavailable, _source!.GetStatus(key));
        Assert.False(_source.TryRead(key, out _));
    }

    [Fact]
    public void Two_devices_with_one_id_are_left_out_and_logged()
    {
        var kit = new SysfsKit();
        kit.Hwmon(0, owner: null, "acpi_fan", ("fan1_input", "2100"));
        kit.Hwmon(1, owner: null, "acpi_fan", ("fan1_input", "2200"));
        kit.Hwmon(2, owner: null, "other", ("fan1_input", "900"));
        Open(kit);

        Assert.Equal(["hwmon/v1/other/name+other/fan1"], Ids());
        Assert.Contains(_events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("hwmon/v1/acpi_fan/name+acpi_fan/fan1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_chip_name_drops_the_kernels_instance_numbers()
    {
        var kit = new SysfsKit();
        kit.AddR8169(1);
        kit.AddIwlwifi(2, zone: 1);
        Open(kit);

        Assert.Contains("hwmon/v1/r8169/pci+0000-ab-00.0~role+mdio+00/temp1", Ids());
        Assert.Contains("hwmon/v1/iwlwifi/type+iwlwifi/temp1", Ids());
    }

    [Fact]
    public void A_link_that_leaves_the_root_is_not_followed()
    {
        var kit = new SysfsKit();
        kit.AddIt8696(0);
        kit.RawLink("class/hwmon/hwmon9", "../../../etc");
        kit.RawLink("class/hwmon/hwmon10", "/etc");
        Open(kit);

        Assert.Equal(6, Ids().Length);
        Assert.Null(kit.RealPath("class/hwmon/hwmon9"));
        Assert.Null(kit.RealPath("class/hwmon/hwmon10"));
        Assert.Equal("devices/platform/it87.2624/hwmon/hwmon0", kit.RealPath("class/hwmon/hwmon0"));
        Assert.Equal("devices/platform/it87.2624", kit.RealPath("class/hwmon/hwmon0/device"));
    }

    [Fact]
    public void The_sysfs_root_is_the_variables_full_path_else_sys()
    {
        var log = Serilog.Core.Logger.None;
        Assert.Equal("/sys", LinuxSensorSource.SysfsRoot(null, log));
        Assert.Equal("/sys", LinuxSensorSource.SysfsRoot("", log));
        Assert.Equal("/sys", LinuxSensorSource.SysfsRoot("fixture/sys", log));
        var full = Path.Combine(Path.GetTempPath(), "fixture");
        Assert.Equal(full, LinuxSensorSource.SysfsRoot(full, log));
    }

    // Demand: every sensor is listed, only the ones in use are read (a disk's drivetemp read resets its spin-down timer).

    [Fact]
    public void A_sensor_nothing_uses_is_listed_but_never_read()
    {
        var kit = new SysfsKit();
        var fan = kit.AddIt8696(0) + "/fan1_input";
        var disk = kit.AddDrivetemp(1, "naa.5000c500a1b2c3d4", serial: null) + "/temp1_input";
        OpenIdle(kit);
        Round(3);

        Assert.Contains("hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1", Ids());
        Assert.Contains("hwmon/v1/it8696/platform+it87.2624/fan1", Ids());
        // A listing, a status and a peek aren't uses.
        Assert.Equal(SensorStatus.Unavailable, _source!.GetStatus(Key("hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1")));
        Assert.False(_source.TryPeek(Key("hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1"), out _));
        Assert.Equal(SensorStatus.Unavailable, _source.PeekState(Key("hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1")).Status);
        Round(2);
        Assert.Equal(0, kit.ReadsOf(disk));
        Assert.Equal(0, kit.ReadsOf(fan));
    }

    [Fact]
    public void A_sensor_in_use_is_read_from_the_next_poll_until_it_has_not_been_used_for_the_use_window()
    {
        var kit = new SysfsKit();
        var fan = kit.AddIt8696(0) + "/fan1_input";
        var disk = kit.AddDrivetemp(1, "naa.5000c500a1b2c3d4", serial: null) + "/temp1_input";
        OpenIdle(kit);
        const string Fan = "hwmon/v1/it8696/platform+it87.2624/fan1";
        const string Disk = "hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1";

        // A consumer's read puts the fan in use: it reads from the next poll on; the disk still isn't read.
        Assert.False(_source!.TryRead(Key(Fan), out _));
        Round();
        Assert.Equal(1250, Value(Fan));
        Assert.Equal(1, kit.ReadsOf(fan));
        Assert.Equal(0, kit.ReadsOf(disk));

        // A graph's draw (NoteUse) and a client's ReadState are uses too.
        Use(Disk);
        Round();
        Assert.Equal(33, Value(Disk));
        Assert.Equal(SensorStatus.Live, _source.ReadState(Key(Fan)).Status);

        // Unused for the use window, the disk is read no more; the fan, read every poll, still is.
        var reads = kit.ReadsOf(disk);
        var seconds = (int)LinuxDemand.UseWindow.TotalSeconds;
        for (var i = 0; i < seconds + 5; i++)
        {
            Round();
            _source.TryRead(Key(Fan), out _);
        }

        Assert.InRange(kit.ReadsOf(disk), reads, reads + seconds);
        var stopped = kit.ReadsOf(disk);
        Round(5);
        Assert.Equal(stopped, kit.ReadsOf(disk));
        Assert.NotEqual(SensorStatus.Live, _source.GetStatus(Key(Disk)));
        Assert.Equal(1250, Value(Fan));
    }

    // The older hwmon layout: name and channels on the physical device, the hwmon folder holding only its links.

    [Fact]
    public void A_device_whose_attributes_are_on_its_physical_device_is_listed_with_the_same_ids()
    {
        var classLayout = new SysfsKit();
        classLayout.AddIt8696(0);
        classLayout.AddI2c(1);
        Open(classLayout);
        var expected = Ids();
        _source!.Dispose();

        var physical = new SysfsKit();
        physical.PhysicalHwmon(0, physical.BusDevice("devices/platform/it87.2624", "platform"), "it8696",
            ("fan1_input", "1250"), ("fan2_input", "980"), ("temp1_input", "42000"), ("temp2_input", "38000"),
            ("in0_input", "1032"), ("in1_input", "2016"), ("pwm1", "128"));
        var pci = physical.BusDevice("devices/pci0000:00/0000:00:1f.4", "pci");
        var adapter = physical.BusDevice($"{pci}/i2c-0", "i2c", ("name", "SMBus I801 adapter"));
        // An I²C client has a name of its own: the chip's, which the older layout's hwmon name is.
        physical.PhysicalHwmon(1, physical.BusDevice($"{adapter}/0-002d", "i2c"), "nct7802", ("temp1_input", "40000"));
        Open(physical);

        Assert.Equal(7, expected.Length);
        Assert.Equal(expected, Ids());
        Assert.Equal(1250, Value("hwmon/v1/it8696/platform+it87.2624/fan1"));
        Assert.Equal(40, Value(Assert.Single(expected, id => id.StartsWith("hwmon/v1/nct7802/", StringComparison.Ordinal))));
    }
}
