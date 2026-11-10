using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// What identifies the device a hwmon device or a thermal zone belongs to, as the Linux fork's ids anchor it: never the
/// kernel's numbering (<c>hwmonN</c>, <c>thermal_zoneN</c>, <c>nvmeN</c>, an I²C bus's or a USB bus's number), which
/// changes from boot to boot. The device that owns a hwmon device is the one its <c>device</c> link names; it and the
/// devices above it (the folders its real path passes through, each typed by its <c>subsystem</c> link) are tried with
/// these rules in this order, the first that applies giving the anchor:
/// <list type="number">
/// <item>an NVMe controller with a serial: <c>nvme-serial+{serial}</c>, <c>~pci+{its PCI function}</c>;</item>
/// <item>a disk (a SCSI device) with a WWID, else a serial: <c>block-wwid+{wwid}</c>, <c>block-serial+{serial}</c>;</item>
/// <item>an I²C client: <c>i2c+{its adapter's name}+{its address, 4 hex digits}</c>, <c>~adapter+{the anchor of the
/// device the I²C bus hangs on}</c>;</item>
/// <item>a USB device (the nearest with a vendor and product id: not a hub above it) with a serial:
/// <c>usb-serial+{vid}+{pid}+{serial}</c>; without one, <c>usb-port+{vid}+{pid}+{the host controller's anchor}+{its
/// ports, 3.2}</c>;</item>
/// <item>a thermal zone: <c>type+{its type}</c>, <c>~acpi+{its ACPI path}</c> when it has one;</item>
/// <item>the nearest PCI function or platform device: <c>pci+{0000-01-00.0}</c> (<c>:</c> written <c>-</c>) or
/// <c>platform+{its name}</c>, <c>~role+mdio+{address}</c> for a PHY on a network card's MDIO bus;</item>
/// <item>else, and for a hwmon device with no owner: <c>name+{chip}</c>.</item>
/// </list>
/// A secondary anchor is added whenever it is there, even when the anchor alone is unique, so adding or removing a
/// device never changes another's id. Values are trimmed; the chip and a zone's type drop the kernel's instance
/// numbers (<see cref="NormalizeChip"/>).
/// </summary>
internal static partial class HwmonAnchors
{
    /// <summary>A device's folder on the way up from a device, and its subsystem (null for a folder that is no device).</summary>
    private readonly record struct Device(string Path, string Name, string? Subsystem);

    /// <summary>
    /// A hwmon device's <c>name</c> or a thermal zone's <c>type</c> as an id holds it: trimmed, the forms the kernel
    /// numbers per instance without their numbers (<c>iwlwifi_1</c> → <c>iwlwifi</c>, <c>r8169_0_ab00:00</c> →
    /// <c>r8169</c>); every other name as it is.
    /// </summary>
    public static string NormalizeChip(string name)
    {
        var trimmed = name.Trim();
        var match = NumberedChip().Match(trimmed);
        return match.Success ? match.Groups["chip"].Value : trimmed;
    }

    [GeneratedRegex(@"^(?:(?<chip>iwlwifi)_[0-9]+|(?<chip>r8169)_[0-9]+_[0-9a-f]+:[0-9a-f]+)$")]
    private static partial Regex NumberedChip();

    [GeneratedRegex(@"^[0-9]+-(?<address>[0-9a-f]{4})$")]
    private static partial Regex I2cClient();

    [GeneratedRegex(@"^i2c-[0-9]+$")]
    private static partial Regex I2cAdapter();

    [GeneratedRegex(@"^usb[0-9]+$")]
    private static partial Regex UsbRootHub();

    [GeneratedRegex(@"^[0-9]+-(?<ports>[0-9]+(?:\.[0-9]+)*)$")]
    private static partial Regex UsbDevice();

    /// <summary>
    /// The anchor and secondary anchor of the hwmon device named <paramref name="chip"/> (normalised) whose owner is the
    /// device at <paramref name="owner"/> (a real path; null when it has none).
    /// </summary>
    public static (LinuxAnchor Anchor, LinuxAnchor? Secondary) OfHwmon(Sysfs sysfs, string? owner, string chip)
    {
        if (owner == null)
            return (new LinuxAnchor("name", chip), null);

        var chain = Chain(sysfs, owner);
        if (chain.Count == 0)
            return (new LinuxAnchor("name", chip), null);
        return Nvme(sysfs, chain)
            ?? Disk(sysfs, chain)
            ?? I2c(sysfs, chain)
            ?? Usb(sysfs, chain)
            ?? (chain[0].Subsystem == "thermal" ? OfZone(sysfs, chain[0].Path) : null)
            ?? PciOrPlatform(chain)
            ?? (new LinuxAnchor("name", chip), null);
    }

    /// <summary>
    /// A thermal zone's anchor (<c>type+{type}</c>) and secondary one (<c>acpi+{path}</c>, the ACPI namespace path of
    /// the device its <c>device</c> link names, when it has one), from its folder (a real path); null without a type.
    /// </summary>
    public static (LinuxAnchor Anchor, LinuxAnchor? Secondary)? OfZone(Sysfs sysfs, string zone)
    {
        if (sysfs.ReadAttribute(zone + "/type") is not { } type)
            return null;
        LinuxAnchor? secondary = null;
        if (sysfs.ReadLink(zone + "/device") != null
            && sysfs.RealPath(zone + "/device") is { } device
            && sysfs.ReadAttribute(device + "/path") is { } path
            && path.StartsWith('\\'))
        {
            secondary = new LinuxAnchor("acpi", path);
        }

        return (new LinuxAnchor("type", NormalizeChip(type)), secondary);
    }

    /// <summary>The device at <paramref name="path"/> and the folders above it below <c>devices/</c>, nearest first.</summary>
    private static List<Device> Chain(Sysfs sysfs, string path)
    {
        var chain = new List<Device>();
        while (path.StartsWith("devices/", System.StringComparison.Ordinal))
        {
            var slash = path.LastIndexOf('/');
            var target = sysfs.ReadLink(path + "/subsystem");
            var subsystem = target?[(target.LastIndexOf('/') + 1)..];
            chain.Add(new Device(path, path[(slash + 1)..], string.IsNullOrEmpty(subsystem) ? null : subsystem));
            path = path[..slash];
        }

        return chain;
    }

    private static (LinuxAnchor, LinuxAnchor?)? Nvme(Sysfs sysfs, List<Device> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i].Subsystem == "nvme" && sysfs.ReadAttribute(chain[i].Path + "/serial") is { } serial)
                return (new LinuxAnchor("nvme-serial", serial), Nearest(chain, i + 1, "pci") is { } pci ? new LinuxAnchor("pci", PciToken(pci.Name)) : null);
        }

        return null;
    }

    private static (LinuxAnchor, LinuxAnchor?)? Disk(Sysfs sysfs, List<Device> chain)
    {
        foreach (var device in chain)
        {
            if (device.Subsystem is not ("scsi" or "block"))
                continue;
            if (Wwid(sysfs, device.Path) is { } wwid)
                return (new LinuxAnchor("block-wwid", wwid), null);
            if (sysfs.ReadAttribute(device.Path + "/serial") is { } serial)
                return (new LinuxAnchor("block-serial", serial), null);
        }

        return null;
    }

    /// <summary>A disk's WWID: its block device's, else the device's own.</summary>
    private static string? Wwid(Sysfs sysfs, string device)
    {
        foreach (var block in sysfs.List(device + "/block"))
        {
            if (sysfs.ReadAttribute($"{device}/block/{block}/wwid") is { } wwid)
                return wwid;
        }

        return sysfs.ReadAttribute(device + "/wwid");
    }

    private static (LinuxAnchor, LinuxAnchor?)? I2c(Sysfs sysfs, List<Device> chain)
    {
        var client = chain[0];
        if (client.Subsystem != "i2c" || I2cClient().Match(client.Name) is not { Success: true } match)
            return null;
        if (chain.Count < 2 || !I2cAdapter().IsMatch(chain[1].Name) || sysfs.ReadAttribute(chain[1].Path + "/name") is not { } adapter)
            return null;

        // The device the bus hangs on: the one above the topmost adapter (through any multiplexers).
        var top = 1;
        for (var i = 2; i < chain.Count; i++)
        {
            if (chain[i].Subsystem == "i2c" && I2cAdapter().IsMatch(chain[i].Name))
                top = i;
        }

        var owner = DeviceAnchor(chain, top + 1);
        return (new LinuxAnchor("i2c", adapter, match.Groups["address"].Value), owner is { } o ? new LinuxAnchor("adapter", o.ToString()) : null);
    }

    private static (LinuxAnchor, LinuxAnchor?)? Usb(Sysfs sysfs, List<Device> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var device = chain[i];
            if (device.Subsystem != "usb"
                || sysfs.ReadAttribute(device.Path + "/idVendor") is not { } vendor
                || sysfs.ReadAttribute(device.Path + "/idProduct") is not { } product)
            {
                continue;
            }

            vendor = vendor.ToLowerInvariant();
            product = product.ToLowerInvariant();
            if (sysfs.ReadAttribute(device.Path + "/serial") is { } serial)
                return (new LinuxAnchor("usb-serial", vendor, product, serial), null);

            // Where it is plugged in: the host controller (above the root hub) and the ports, never the bus's number.
            if (UsbDevice().Match(device.Name) is not { Success: true } ports)
                return null;
            for (var j = i + 1; j < chain.Count; j++)
            {
                if (chain[j].Subsystem == "usb" && UsbRootHub().IsMatch(chain[j].Name))
                    return DeviceAnchor(chain, j + 1) is { } host ? (new LinuxAnchor("usb-port", vendor, product, host.ToString(), ports.Groups["ports"].Value), null) : null;
            }

            return null;
        }

        return null;
    }

    private static (LinuxAnchor, LinuxAnchor?)? PciOrPlatform(List<Device> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i].Subsystem is not ("pci" or "platform"))
                continue;

            var anchor = chain[i].Subsystem == "pci" ? new LinuxAnchor("pci", PciToken(chain[i].Name)) : new LinuxAnchor("platform", chain[i].Name);
            // A PHY on the device's MDIO bus (a network card's): its address tells it from the card's other sensors.
            LinuxAnchor? secondary = null;
            if (i > 0 && chain[0].Subsystem == "mdio_bus" && chain[0].Name.LastIndexOf(':') is var colon and >= 0 && colon < chain[0].Name.Length - 1)
                secondary = new LinuxAnchor("role", "mdio", chain[0].Name[(colon + 1)..]);
            return (anchor, secondary);
        }

        return null;
    }

    /// <summary>The anchor of the nearest PCI function or platform device from <paramref name="from"/> up, if any.</summary>
    private static LinuxAnchor? DeviceAnchor(List<Device> chain, int from)
    {
        for (var i = from; i < chain.Count; i++)
        {
            if (chain[i].Subsystem == "pci")
                return new LinuxAnchor("pci", PciToken(chain[i].Name));
            if (chain[i].Subsystem == "platform")
                return new LinuxAnchor("platform", chain[i].Name);
        }

        return null;
    }

    private static Device? Nearest(List<Device> chain, int from, string subsystem)
    {
        for (var i = from; i < chain.Count; i++)
        {
            if (chain[i].Subsystem == subsystem)
                return chain[i];
        }

        return null;
    }

    /// <summary>A PCI address as an id holds it: <c>0000:01:00.0</c> → <c>0000-01-00.0</c>.</summary>
    private static string PciToken(string address) => address.Replace(':', '-');
}
