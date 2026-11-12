using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Devices;

/// <summary>
/// A HID collection as the OS lists it: its path (the OS's own name for it), the device's ids and USB serial, the USB port
/// path of its device when the OS tells it (<c>1-2.3</c>, as libusb numbers ports; null when it doesn't), its interface
/// number, its top-level usage page and usage, its longest reports (the report id byte included), and the id of the
/// physical USB device it belongs to when the OS tells it (Windows: the USB device's instance id above the collection;
/// a composite device's collections share it), and its USB device's bus and address when the OS tells them (Linux,
/// macOS: a device plugged in again, or another in its place, has a new address).
/// </summary>
public sealed record HidEntry(string Path, ushort VendorId, ushort ProductId, string? Serial, string? PortPath, int Interface,
    ushort UsagePage, ushort Usage, int MaxInputReportLength, int MaxOutputReportLength, int MaxFeatureReportLength, string? ParentId = null,
    byte? Bus = null, byte? Address = null)
{
    /// <summary>Which physical device it belongs to, as far as the OS tells: its port, else its parent device, else its serial, else its own path.</summary>
    public string DeviceKey => PortPath ?? (!string.IsNullOrEmpty(ParentId) ? "parent:" + ParentId.ToUpperInvariant()
        : string.IsNullOrEmpty(Serial) ? Path : "serial:" + Serial);
}

/// <summary>The OS's HID devices: listed by ids (no I/O to a device), and a collection opened.</summary>
public interface IHidSystem
{
    IReadOnlyList<HidEntry> List(ushort vendorId, ushort productId);

    /// <summary>
    /// Opens <paramref name="entry"/> under HidSharp's lock across processes, within <paramref name="timeoutMs"/>:
    /// <see cref="PanelDeviceStatus.Busy"/> when another panel holds it (nothing written),
    /// <see cref="PanelDeviceStatus.Access"/> when the OS refuses, <see cref="PanelDeviceStatus.Gone"/> when it went.
    /// </summary>
    PanelDeviceStatus Open(HidEntry entry, int timeoutMs, out IHidStream? stream);
}

/// <summary>An open HID collection's reports (each with its report id first). A dispose while a call waits ends that call.</summary>
public interface IHidStream : IDisposable
{
    PanelDeviceStatus Write(byte[] report, int timeoutMs);

    PanelDeviceStatus Read(byte[] buffer, int timeoutMs, out int read);

    PanelDeviceStatus SetFeature(byte[] report, int timeoutMs);

    PanelDeviceStatus GetFeature(byte[] buffer, int timeoutMs, out int read);
}

/// <summary>
/// Which HID collections are a USB device's: those with its ids, then (when the OS tells each one's USB port: Linux,
/// macOS) those at its port path whose device has its bus and address (one whose bus or address can't be read is refused,
/// <see cref="PanelDeviceStatus.Busy"/>, the reason logged); when it doesn't (Windows), the collections grouped by their physical device (the
/// parent USB device the OS gives, else the serial), and each panel given one device, reserved for it: a single device
/// too, so an identical one plugged in later goes to the next panel. The driver's filter (interface, output report
/// length, usage page) comes first: a panel is given the first device not reserved by another panel that has a
/// collection passing it; none free answers <see cref="PanelDeviceStatus.Busy"/>. A reservation is kept while a
/// collection of it is open (across its device's reconnect too), released when the last one closes, and dropped once
/// its device is no longer listed and nothing holds it; only a listing of its own ids drops it.
/// </summary>
public sealed partial class HidChooser(ILogger logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Reservation> _reserved = []; // a panel (its port path) → the device given it
    private readonly Dictionary<string, string> _given = []; // a panel → the device last given it, kept after its release
    private readonly HashSet<string> _logged = [];

    private sealed class Reservation(ushort vendorId, ushort productId, string deviceKey)
    {
        public ushort VendorId { get; } = vendorId;

        public ushort ProductId { get; } = productId;

        public string DeviceKey { get; } = deviceKey;

        public int Handles { get; set; }

        public bool Of(ushort vendorId, ushort productId) => VendorId == vendorId && ProductId == productId;
    }

    /// <summary>A panel's key: its USB port path, the same across a reconnect in that port.</summary>
    private static string PanelOf(PanelCandidate device) => device.PortPath;

    public PanelDeviceStatus Collections(PanelCandidate device, IReadOnlyList<HidEntry> all, PanelHidFilter filter, out IReadOnlyList<HidEntry> chosen)
    {
        chosen = [];
        var same = all.Where(e => e.VendorId == device.VendorId && e.ProductId == device.ProductId).ToList();
        if (same.Count == 0)
            return PanelDeviceStatus.Gone;

        if (same.Any(e => e.PortPath != null))
        {
            // At its port, with its device's bus and address (the evidence it is still the candidate: without it, refused):
            // another device there now is gone.
            var atPort = same.Where(e => e.PortPath == device.PortPath).ToList();
            if (atPort.Count == 0)
                return PanelDeviceStatus.Gone;
            if (atPort.Any(e => !HasAddress(e)))
                return Unproven(device);
            atPort = [.. atPort.Where(e => IsAt(e, device))];
            if (atPort.Count == 0)
                return PanelDeviceStatus.Gone;
            chosen = Passing(atPort, filter);
            return PanelDeviceStatus.Ok;
        }

        var devices = same.GroupBy(e => e.DeviceKey).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var panel = PanelOf(device);
        lock (_lock)
        {
            // Reservations of these ids whose device is no longer listed and that nothing holds let it go.
            foreach (var gone in _reserved.Where(r => r.Value.Of(device.VendorId, device.ProductId) && r.Value.Handles == 0
                && devices.All(d => d.Key != r.Value.DeviceKey)).Select(r => r.Key).ToList())
                _reserved.Remove(gone);

            if (_reserved.TryGetValue(panel, out var own) && own.Of(device.VendorId, device.ProductId))
            {
                // Its own device: gone while a handle still holds it (it was unplugged and isn't back yet).
                if (devices.FirstOrDefault(d => d.Key == own.DeviceKey) is not { } kept)
                    return PanelDeviceStatus.Gone;
                chosen = Passing(kept, filter);
                return PanelDeviceStatus.Ok;
            }
            if (own is { Handles: > 0 })
                return PanelDeviceStatus.Busy; // another device of other ids at its port is still open

            var taken = _reserved.Where(r => r.Key != panel && r.Value.Of(device.VendorId, device.ProductId)).Select(r => r.Value.DeviceKey).ToHashSet(StringComparer.Ordinal);
            var free = devices.Where(d => !taken.Contains(d.Key)).ToList();
            if (free.Count == 0)
            {
                if (_logged.Add("busy:" + panel))
                    logger.Warning("Panel device {Device}: {Count} identical HID devices {Ids} the system can't tell apart are each held by another panel", panel, devices.Count, $"{device.VendorId:x4}:{device.ProductId:x4}");
                return PanelDeviceStatus.Busy;
            }
            if (free.FirstOrDefault(d => Passing(d, filter).Count > 0) is not { } given)
                return PanelDeviceStatus.Ok; // none passes the filter: nothing is reserved
            _reserved[panel] = new Reservation(device.VendorId, device.ProductId, given.Key);
            _given[panel] = given.Key;
            if (devices.Count > 1 && _logged.Add(panel))
                logger.Information("Panel device {Device}: {Count} identical HID devices {Ids} the system can't tell apart; it is given the first not held by another panel", panel, devices.Count, $"{device.VendorId:x4}:{device.ProductId:x4}");
            chosen = Passing(given, filter);
            return PanelDeviceStatus.Ok;
        }
    }

    /// <summary>
    /// Whether <paramref name="entry"/>'s device has the candidate's bus and address. An entry with a port (Linux, macOS)
    /// must give both: one whose bus or address couldn't be read is never taken for the candidate. Windows' entries give
    /// neither port nor address (the documented exception: a same-id device in the same port is the same panel).
    /// </summary>
    private static bool IsAt(HidEntry entry, PanelCandidate device) => entry.PortPath is null
        ? (entry.Bus is not { } bus || bus == device.Bus) && (entry.Address is not { } address || address == device.Address)
        : entry.Bus == device.Bus && entry.Address == device.Address;

    /// <summary>Whether an entry the OS gives a port for (Linux, macOS) gives its device's bus and address too.</summary>
    private static bool HasAddress(HidEntry entry) => entry.PortPath is null || (entry.Bus is not null && entry.Address is not null);

    /// <summary>A collection at the candidate's port whose device's bus or address can't be read: refused, the reason logged once.</summary>
    private PanelDeviceStatus Unproven(PanelCandidate device)
    {
        lock (_lock)
        {
            if (_logged.Add("unproven:" + PanelOf(device)))
                logger.Warning("Panel device {Device}: its HID device's bus and address can't be read, so it can't be told from another in its place; it isn't opened", PanelOf(device));
        }
        return PanelDeviceStatus.Busy;
    }

    /// <summary>
    /// The collection <paramref name="id"/> for <paramref name="device"/> to open: where the OS gives each collection's
    /// port, one at its port (with its bus and address); where it doesn't, one of the physical device given to this panel,
    /// taken again for it when its last collection closed (a driver trying a panel's collections in turn keeps its
    /// device), never one of another device: a device another panel has answers <see cref="PanelDeviceStatus.Busy"/>,
    /// any other <see cref="PanelDeviceStatus.Gone"/>.
    /// </summary>
    public PanelDeviceStatus ForCollection(PanelCandidate device, IReadOnlyList<HidEntry> all, string id, out HidEntry? entry)
    {
        entry = null;
        var same = all.Where(e => e.VendorId == device.VendorId && e.ProductId == device.ProductId).ToList();
        if (same.FirstOrDefault(e => e.Path == id) is not { } found)
            return PanelDeviceStatus.Gone;
        if (same.Any(e => e.PortPath != null))
        {
            if (found.PortPath != device.PortPath)
                return PanelDeviceStatus.Gone;
            if (!HasAddress(found))
                return Unproven(device);
            if (!IsAt(found, device))
                return PanelDeviceStatus.Gone;
            entry = found;
            return PanelDeviceStatus.Ok;
        }

        var panel = PanelOf(device);
        lock (_lock)
        {
            foreach (var gone in _reserved.Where(r => r.Value.Of(device.VendorId, device.ProductId) && r.Value.Handles == 0
                && same.All(e => e.DeviceKey != r.Value.DeviceKey)).Select(r => r.Key).ToList())
                _reserved.Remove(gone);

            if (_reserved.TryGetValue(panel, out var own) && own.Of(device.VendorId, device.ProductId))
            {
                if (own.DeviceKey != found.DeviceKey)
                    return PanelDeviceStatus.Gone; // a collection of another device than its own
                entry = found;
                return PanelDeviceStatus.Ok;
            }
            if (own is { Handles: > 0 })
                return PanelDeviceStatus.Busy;
            if (_reserved.Any(r => r.Key != panel && r.Value.Of(device.VendorId, device.ProductId) && r.Value.DeviceKey == found.DeviceKey))
                return PanelDeviceStatus.Busy;
            if (_given.TryGetValue(panel, out var last) && last != found.DeviceKey)
                return PanelDeviceStatus.Gone; // its device is another: never an unrelated one
            _reserved[panel] = new Reservation(device.VendorId, device.ProductId, found.DeviceKey);
            _given[panel] = found.DeviceKey;
            entry = found;
            return PanelDeviceStatus.Ok;
        }
    }

    /// <summary>
    /// Whether the collection <paramref name="opened"/> is, in a listing made after it was opened, still at the path it
    /// was opened by with the candidate's ids, port, physical device, bus and address (as far as the OS tells them; where
    /// it gives a port, a bus or address it can't read now answers false): a
    /// device that took its place since answers false. Windows tells no address: a same-id device in the same port
    /// without a serial is the same model in the same place, taken as the same panel.
    /// </summary>
    public static bool IsStill(HidEntry opened, IReadOnlyList<HidEntry> now, PanelCandidate device) =>
        now.FirstOrDefault(e => e.Path == opened.Path && e.VendorId == device.VendorId && e.ProductId == device.ProductId) is { } current
        && current.PortPath == opened.PortPath && (current.PortPath is null || current.PortPath == device.PortPath)
        && current.DeviceKey == opened.DeviceKey && IsAt(current, device);

    private static List<HidEntry> Passing(IEnumerable<HidEntry> entries, PanelHidFilter filter) =>
        [.. entries.Where(e => (filter.Interface is not { } i || e.Interface == i)
                && (filter.MinOutputReport is not { } least || e.MaxOutputReportLength >= least)
                && (filter.UsagePage is not { } page || e.UsagePage == page))
            .OrderBy(e => e.Interface).ThenBy(e => e.Path, StringComparer.Ordinal)];

    /// <summary>
    /// Holds <paramref name="device"/>'s reservation of <paramref name="entry"/>'s device while a collection of it is open;
    /// disposing the hold releases it once no other is open (a collection found by its port needs none).
    /// </summary>
    public IDisposable Hold(PanelCandidate device, HidEntry entry)
    {
        var panel = PanelOf(device);
        lock (_lock)
        {
            if (!_reserved.TryGetValue(panel, out var reservation) || reservation.DeviceKey != entry.DeviceKey)
                return Released.Instance;
            reservation.Handles++;
            return new Holding(this, panel, reservation);
        }
    }

    private void Release(string panel, Reservation reservation)
    {
        lock (_lock)
        {
            reservation.Handles--;
            if (reservation.Handles <= 0 && _reserved.TryGetValue(panel, out var current) && ReferenceEquals(current, reservation))
                _reserved.Remove(panel);
        }
    }

    private sealed class Holding(HidChooser owner, string panel, Reservation reservation) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(panel, reservation);
        }
    }

    private sealed class Released : IDisposable
    {
        public static readonly Released Instance = new();

        public void Dispose()
        {
        }
    }

    public static PanelHidCollection CollectionOf(HidEntry entry) =>
        new(entry.Path, entry.Interface, entry.UsagePage, entry.Usage, entry.MaxInputReportLength, entry.MaxOutputReportLength, entry.MaxFeatureReportLength);

    /// <summary>
    /// Linux: the USB port path and interface of a <c>hidraw</c> device, from its sysfs device link's target
    /// (<c>…/usb1/1-2/1-2.3/1-2.3:1.0/0003:0416:5302.0001</c>: port <c>1-2.3</c>, interface 0); null when it isn't a USB one.
    /// </summary>
    public static (string Port, int Interface)? LinuxLocation(string sysfsDevicePath)
    {
        var match = LinuxInterface().Matches(sysfsDevicePath).LastOrDefault();
        return match is null ? null : (match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// macOS: the USB port path and interface of an IOKit HID device, from its IORegistry path: the USB device's node is
    /// named with its location id (<c>…@14200000</c>: bus 0x14 = 20, ports 2), as libusb numbers it (bus the top byte, a
    /// port a nibble each from the top, to the first 0), and the interface node <c>IOUSBHostInterface@N</c>.
    /// </summary>
    public static (string Port, int Interface)? MacLocation(string ioRegistryPath)
    {
        var usbInterface = MacInterface().Match(ioRegistryPath);
        if (!usbInterface.Success)
            return null;
        // The USB device's node is the last one with a location id before its interface's.
        var device = MacDevice().Matches(ioRegistryPath[..usbInterface.Index]).LastOrDefault();
        if (device is null)
            return null;
        var location = uint.Parse(device.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (PortPathOfLocation(location), int.Parse(usbInterface.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Linux: the sysfs folder of a <c>hidraw</c> device's USB device (with its <c>busnum</c> and <c>devnum</c>), from its
    /// device link's target (<c>…/usb1/1-2/1-2.3/1-2.3:1.0/0003:0416:5302.0001</c>: <c>…/usb1/1-2/1-2.3</c>); null when it isn't a USB one.
    /// </summary>
    public static string? LinuxUsbDevice(string sysfsDevicePath)
    {
        var match = LinuxInterface().Matches(sysfsDevicePath).LastOrDefault();
        // The interface's folder ({port}:{config}.{interface}) is in its device's.
        return match is null || match.Index == 0 ? null : sysfsDevicePath[..match.Index];
    }

    /// <summary>macOS: the IORegistry path of an IOKit HID device's USB device (its node before <c>IOUSBHostInterface@N</c>, with its <c>USB Address</c>); null when it has none.</summary>
    public static string? MacUsbDevice(string ioRegistryPath)
    {
        var usbInterface = MacInterface().Match(ioRegistryPath);
        return usbInterface.Success && usbInterface.Index > 1 ? ioRegistryPath[..(usbInterface.Index - 1)] : null;
    }

    /// <summary>A macOS USB location id as libusb's port path: <c>bus-p1.p2…</c>.</summary>
    public static string PortPathOfLocation(uint location)
    {
        var path = new System.Text.StringBuilder().Append(location >> 24);
        for (var i = 0; i < 6; i++)
        {
            var port = (location >> (20 - 4 * i)) & 0xF;
            if (port == 0)
                break;
            path.Append(i == 0 ? '-' : '.').Append(port);
        }
        return path.ToString();
    }

    /// <summary>
    /// Windows: the physical USB device a HID collection belongs to, from its device node's ids up the tree (its own first):
    /// the first USB device's (<c>USB\VID_…&amp;PID_…\…</c>) that isn't one interface of a composite device (<c>&amp;MI_</c>);
    /// null when there is none (a collection not on USB).
    /// </summary>
    public static string? WindowsUsbParent(IEnumerable<string> ancestorIds) =>
        ancestorIds.FirstOrDefault(id => id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows: a HID path's USB interface (<c>&amp;mi_01</c>), 0 when it names none (a device with one interface).</summary>
    public static int WindowsInterface(string path)
    {
        var match = WindowsMi().Match(path);
        return match.Success ? int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegexAttribute(@"/(\d+-\d+(?:\.\d+)*):\d+\.(\d+)(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxInterface();

    [GeneratedRegexAttribute(@"@([0-9a-fA-F]{8})(?=/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MacDevice();

    [GeneratedRegexAttribute(@"IOUSBHostInterface@(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MacInterface();

    [GeneratedRegexAttribute(@"[&#]mi_([0-9a-fA-F]{2})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsMi();
}
