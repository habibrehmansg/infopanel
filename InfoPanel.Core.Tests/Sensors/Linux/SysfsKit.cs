using InfoPanel.Sensors.Linux;

namespace InfoPanel.Core.Tests.Sensors.Linux;

/// <summary>
/// A sysfs tree in memory for the Linux source's tests, laid out as the kernel lays one out: devices under
/// <c>devices/</c>, each with its <c>subsystem</c> link to <c>bus/{bus}</c> or <c>class/{class}</c>; a hwmon device in a
/// <c>hwmon/</c> folder under a bus device, right under a class device (an NVMe controller, a thermal zone), or under
/// <c>devices/virtual/hwmon/</c> with no owner; <c>class/hwmon/hwmonN</c> and <c>class/thermal/thermal_zoneN</c> as links;
/// every link relative. <see cref="WriteTo"/> writes it to a folder with real links, for the disk reader's test and the
/// Docker live check (<see cref="LinuxFixtureWriter"/>), so no tree or link is committed. The numbers a test gives
/// (<c>hwmonN</c>, <c>thermal_zoneN</c>) are the kernel's, which never enter a key.
/// </summary>
internal sealed class SysfsKit : Sysfs
{
    private abstract record Node;

    private sealed record DirNode : Node;

    private sealed record FileNode(string Text, bool Denied) : Node;

    private sealed record LinkNode(string Target) : Node;

    // Ordinal order: a folder comes before what it holds, so WriteTo creates parents first.
    private readonly SortedDictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _readsOf = new(StringComparer.Ordinal);

    /// <summary>How many files were read (<see cref="Read"/>).</summary>
    public int Reads { get; private set; }

    /// <summary>How many times <paramref name="path"/> was read (<see cref="Read"/>, by the path as given).</summary>
    public int ReadsOf(string path) => _readsOf.GetValueOrDefault(path);

    public SysfsKit Dir(string path)
    {
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var p = string.Join('/', parts, 0, i);
            if (!_nodes.TryGetValue(p, out var node))
                _nodes[p] = new DirNode();
            else if (node is not DirNode)
                throw new InvalidOperationException($"{p} is not a folder");
        }

        return this;
    }

    /// <summary>A file with <paramref name="text"/>; a <paramref name="denied"/> one can't be read (mode 000 on disk).</summary>
    public SysfsKit File(string path, string text, bool denied = false)
    {
        Dir(Parent(path));
        _nodes[path] = new FileNode(text, denied);
        return this;
    }

    /// <summary>A link at <paramref name="path"/> to <paramref name="target"/> (a path from the root), kept relative.</summary>
    public SysfsKit Link(string path, string target)
    {
        Dir(Parent(path));
        _nodes[path] = new LinkNode(Relative(Parent(path), target));
        return this;
    }

    /// <summary>A link whose target is written as given (an absolute one, or one that leaves the root).</summary>
    public SysfsKit RawLink(string path, string target)
    {
        Dir(Parent(path));
        _nodes[path] = new LinkNode(target);
        return this;
    }

    /// <summary>Removes <paramref name="path"/> and everything under it.</summary>
    public SysfsKit Remove(string path)
    {
        foreach (var key in _nodes.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            _nodes.Remove(key);
        return this;
    }

    public override IReadOnlyList<string> List(string path)
    {
        var real = RealPath(path);
        if (real == null || (real.Length > 0 && !(_nodes.TryGetValue(real, out var node) && node is DirNode)))
            return [];
        var prefix = real.Length == 0 ? "" : real + "/";
        return _nodes.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Length > prefix.Length && k.IndexOf('/', prefix.Length) < 0)
            .Select(k => k[prefix.Length..])
            .ToList();
    }

    public override string? ReadLink(string path) => _nodes.TryGetValue(path, out var node) && node is LinkNode link ? link.Target : null;

    public override SysfsRead Read(string path, out string text)
    {
        Reads++;
        _readsOf[path] = _readsOf.GetValueOrDefault(path) + 1;
        text = "";
        var real = RealPath(path);
        if (real == null || !_nodes.TryGetValue(real, out var node))
            return SysfsRead.Missing;
        if (node is not FileNode file)
            return SysfsRead.Failed;
        if (file.Denied)
            return SysfsRead.Denied;
        text = file.Text;
        return SysfsRead.Ok;
    }

    /// <summary>Writes the tree under <paramref name="folder"/> (Unix): folders, files, relative links, unreadable files at mode 000.</summary>
    public void WriteTo(string folder)
    {
        foreach (var (path, node) in _nodes)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            switch (node)
            {
                case DirNode:
                    Directory.CreateDirectory(full);
                    break;
                case FileNode file:
                    System.IO.File.WriteAllText(full, file.Text);
                    if (file.Denied && !OperatingSystem.IsWindows())
                        System.IO.File.SetUnixFileMode(full, UnixFileMode.None);
                    break;
                case LinkNode link:
                    System.IO.File.CreateSymbolicLink(full, link.Target);
                    break;
            }
        }
    }

    /// <summary>A device on a bus (<c>subsystem</c> → <c>bus/{bus}</c>) with its attributes.</summary>
    public string BusDevice(string path, string bus, params (string Name, string Text)[] attributes) => Device(path, "bus/" + bus, attributes);

    /// <summary>A class device (<c>subsystem</c> → <c>class/{class}</c>) with its attributes.</summary>
    public string ClassDevice(string path, string @class, params (string Name, string Text)[] attributes) => Device(path, "class/" + @class, attributes);

    private string Device(string path, string subsystem, (string Name, string Text)[] attributes)
    {
        Dir(path);
        Dir(subsystem);
        Link(path + "/subsystem", subsystem);
        foreach (var (name, text) in attributes)
            File(path + "/" + name, text + "\n");
        return path;
    }

    /// <summary>
    /// <c>hwmon{number}</c> named <paramref name="name"/> (as the kernel writes it), owned by <paramref name="owner"/>: in
    /// its <c>hwmon/</c> folder for a bus device, right under it for a class device, under <c>devices/virtual/hwmon/</c>
    /// when null (no <c>device</c> link then); its channel files with a newline, as sysfs gives them.
    /// </summary>
    public string Hwmon(int number, string? owner, string name, params (string File, string Text)[] files)
    {
        var dir = owner == null ? $"devices/virtual/hwmon/hwmon{number}"
            : IsClassDevice(owner) ? $"{owner}/hwmon{number}"
            : $"{owner}/hwmon/hwmon{number}";
        Dir(dir);
        Dir("class/hwmon");
        File(dir + "/name", name + "\n");
        Link($"class/hwmon/hwmon{number}", dir);
        Link(dir + "/subsystem", "class/hwmon");
        if (owner != null)
            Link(dir + "/device", owner);
        foreach (var (file, text) in files)
            File(dir + "/" + file, text + "\n");
        return dir;
    }

    /// <summary>
    /// <c>hwmon{number}</c> in the older layout, its <c>name</c> and channel files on the physical device
    /// <paramref name="owner"/> (a bus device), the hwmon folder holding only its <c>device</c> and <c>subsystem</c> links.
    /// </summary>
    public string PhysicalHwmon(int number, string owner, string name, params (string File, string Text)[] files)
    {
        var dir = $"{owner}/hwmon/hwmon{number}";
        Dir(dir);
        Dir("class/hwmon");
        Link($"class/hwmon/hwmon{number}", dir);
        Link(dir + "/subsystem", "class/hwmon");
        Link(dir + "/device", owner);
        File(owner + "/name", name + "\n");
        foreach (var (file, text) in files)
            File(owner + "/" + file, text + "\n");
        return dir;
    }

    /// <summary><c>thermal_zone{number}</c> of <paramref name="type"/>, with an ACPI device and its namespace path when given.</summary>
    public string ThermalZone(int number, string type, string milli, string? acpiPath = null)
    {
        var dir = ClassDevice($"devices/virtual/thermal/thermal_zone{number}", "thermal", ("type", type), ("temp", milli));
        Link($"class/thermal/thermal_zone{number}", dir);
        if (acpiPath != null)
        {
            var acpi = BusDevice($"devices/LNXSYSTM:00/LNXSYBUS:00/LNXTHERM:{number:00}", "acpi", ("path", acpiPath));
            Link(dir + "/device", acpi);
        }

        return dir;
    }

    private bool IsClassDevice(string path) =>
        _nodes.TryGetValue(path + "/subsystem", out var node) && node is LinkNode link && link.Target.Split('/') is { Length: >= 2 } parts && parts[^2] == "class";

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "";

    private static string Relative(string fromDir, string to)
    {
        var from = fromDir.Split('/');
        var target = to.Split('/');
        var common = 0;
        while (common < from.Length && common < target.Length && from[common] == target[common])
            common++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(target.Skip(common)));
    }

    // The hardware the VFD templates were made on, and the anchor kinds' devices. Values are the kernel's units.

    /// <summary>
    /// The VFD templates' hardware: an ITE IT8696 Super I/O at <c>platform it87.2624</c>, the WireView's hwmon device,
    /// an AMD <c>k10temp</c>, an NVMe drive and the ACPI thermal zone (with its hwmon device). <paramref name="numbers"/>
    /// are their <c>hwmonN</c>, in that order.
    /// </summary>
    public static SysfsKit VfdMachine(int[]? numbers = null)
    {
        var n = numbers ?? [0, 1, 2, 3, 4];
        var kit = new SysfsKit();
        kit.AddIt8696(n[0]);
        kit.AddWireView(n[1]);
        kit.AddK10Temp(n[2]);
        kit.AddNvme(n[3], "FIXTURE-NVME-0001");
        kit.AddAcpiZone(n[4], zone: 0);
        return kit;
    }

    public string AddIt8696(int number) =>
        Hwmon(number, BusDevice("devices/platform/it87.2624", "platform"), "it8696",
            ("fan1_input", "1250"), ("fan2_input", "980"), ("temp1_input", "42000"), ("temp2_input", "38000"),
            ("in0_input", "1032"), ("in1_input", "2016"), ("pwm1", "128"));

    public string AddWireView(int number) =>
        Hwmon(number, BusDevice("devices/platform/wireview_hwmon", "platform"), "wireview",
            ("curr1_input", "8250"), ("curr2_input", "8190"), ("curr3_input", "8310"), ("curr4_input", "8270"),
            ("curr5_input", "8220"), ("curr6_input", "8300"), ("in1_input", "12100"), ("power1_input", "99000000"),
            ("temp1_input", "35000"));

    public string AddK10Temp(int number) =>
        Hwmon(number, BusDevice("devices/pci0000:00/0000:00:18.3", "pci"), "k10temp",
            ("temp1_input", "45250"), ("temp1_label", "Tctl"), ("temp3_input", "44000"), ("temp3_label", "Tccd1"));

    /// <summary>An NVMe controller with <paramref name="serial"/> (space-padded, as the kernel gives it) at PCI <paramref name="pci"/>.</summary>
    public string AddNvme(int number, string serial, string pci = "0000:01:00.0", string controller = "nvme0")
    {
        var bridge = BusDevice("devices/pci0000:00/0000:00:01.1", "pci");
        var function = BusDevice($"{bridge}/{pci}", "pci");
        var nvme = ClassDevice($"{function}/nvme/{controller}", "nvme", ("serial", serial + "      "), ("model", "Fixture NVMe 1TB"));
        return Hwmon(number, nvme, "nvme",
            ("temp1_input", "38850"), ("temp1_label", "Composite"), ("temp2_input", "41850"), ("temp2_label", "Sensor 1"));
    }

    public string AddAcpiZone(int number, int zone)
    {
        var dir = ThermalZone(zone, "acpitz", "16800", @"\_TZ_.TZ00");
        return Hwmon(number, dir, "acpitz", ("temp1_input", "16800"));
    }

    public string AddIwlwifi(int number, int zone)
    {
        var dir = ThermalZone(zone, "iwlwifi_1", "45000");
        return Hwmon(number, dir, "iwlwifi_1", ("temp1_input", "45000"));
    }

    /// <summary>A SATA disk's <c>drivetemp</c>, owned by its SCSI device, with its WWID or serial when given.</summary>
    public string AddDrivetemp(int number, string? wwid, string? serial)
    {
        var attributes = new List<(string, string)>();
        if (wwid != null)
            attributes.Add(("wwid", wwid));
        if (serial != null)
            attributes.Add(("serial", serial));
        var pci = BusDevice("devices/pci0000:00/0000:00:17.0", "pci");
        var host = BusDevice($"{pci}/ata1/host0", "scsi");
        var target = BusDevice($"{host}/target0:0:0", "scsi");
        var disk = BusDevice($"{target}/0:0:0:0", "scsi", [.. attributes]);
        return Hwmon(number, disk, "drivetemp", ("temp1_input", "33000"));
    }

    /// <summary>An <c>nct7802</c> at address 0x2d on the SMBus adapter of PCI <c>0000:00:1f.4</c>.</summary>
    public string AddI2c(int number)
    {
        var pci = BusDevice("devices/pci0000:00/0000:00:1f.4", "pci");
        var adapter = BusDevice($"{pci}/i2c-0", "i2c", ("name", "SMBus I801 adapter"));
        var client = BusDevice($"{adapter}/0-002d", "i2c", ("name", "nct7802"));
        return Hwmon(number, client, "nct7802", ("temp1_input", "40000"));
    }

    /// <summary>
    /// A USB fan controller (1b1c:0c10) on port 3.2 of the xHCI controller at PCI <c>0000:00:14.0</c>, its hwmon device
    /// owned by its HID device under its interface, with a serial when given.
    /// </summary>
    public string AddUsb(int number, string? serial)
    {
        var pci = BusDevice("devices/pci0000:00/0000:00:14.0", "pci");
        var root = BusDevice($"{pci}/usb1", "usb", ("idVendor", "1d6b"), ("idProduct", "0002"));
        var hub = BusDevice($"{root}/1-3", "usb", ("idVendor", "05e3"), ("idProduct", "0610"));
        var attributes = new List<(string, string)> { ("idVendor", "1b1c"), ("idProduct", "0c10") };
        if (serial != null)
            attributes.Add(("serial", serial));
        var device = BusDevice($"{hub}/1-3.2", "usb", [.. attributes]);
        var usbInterface = BusDevice($"{device}/1-3.2:1.0", "usb", ("bInterfaceNumber", "00"));
        var hid = BusDevice($"{usbInterface}/0003:1B1C:0C10.0001", "hid");
        return Hwmon(number, hid, "corsaircpro", ("temp1_input", "31000"), ("fan1_input", "800"));
    }

    /// <summary>A Realtek NIC's PHY temperature: the PHY on the NIC's MDIO bus, the NIC at PCI <c>0000:ab:00.0</c>.</summary>
    public string AddR8169(int number)
    {
        var bridge = BusDevice("devices/pci0000:00/0000:00:1c.0", "pci");
        var nic = BusDevice($"{bridge}/0000:ab:00.0", "pci");
        var bus = ClassDevice($"{nic}/mdio_bus/r8169-0-ab00", "mdio_bus");
        var phy = BusDevice($"{bus}/r8169-0-ab00:00", "mdio_bus");
        return Hwmon(number, phy, "r8169_0_ab00:00", ("temp1_input", "52000"));
    }
}
