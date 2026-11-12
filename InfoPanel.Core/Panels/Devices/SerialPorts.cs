using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Devices;

/// <summary>The OS's serial ports: a USB device's own port found by its identity, opened, and the opened port checked to be it.</summary>
public interface ISerialSystem
{
    /// <summary>
    /// The serial port of <paramref name="device"/> itself: <see cref="PanelDeviceStatus.Ok"/> with its name;
    /// <see cref="PanelDeviceStatus.Gone"/> when the device isn't there any more (other ids, another bus or address, or
    /// none); <see cref="PanelDeviceStatus.Busy"/> when the OS can't tell it from identical devices (Windows, several
    /// without serial numbers); <see cref="PanelDeviceStatus.NotSupported"/> when it has no port the OS shows.
    /// <paramref name="serialNumber"/> gives the device's USB serial number (null when it can't be read), asked only when
    /// the OS has nothing else to tell it apart by. Stops early, answering anything, once <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    PanelDeviceStatus Find(PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken, out string? port);

    /// <summary>Opens <paramref name="port"/> with DTR and RTS off, writing nothing. A dispose of the port while a call waits ends that call.</summary>
    PanelDeviceStatus Open(string port, int baud, out IOpenedSerial? serial);

    /// <summary>
    /// Whether the port <paramref name="serial"/> has open is <paramref name="device"/>'s own, from what the OS says of
    /// the opened port: <see cref="PanelDeviceStatus.Ok"/>; <see cref="PanelDeviceStatus.Gone"/> when it is another
    /// device's; any other status when it can't be told (the port is then not used).
    /// </summary>
    PanelDeviceStatus Verify(IOpenedSerial serial, PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken);
}

/// <summary>An opened serial port whose control lines are set once it is known to be the device's.</summary>
public interface IOpenedSerial : IPanelSerial
{
    /// <summary>Sets DTR and RTS.</summary>
    PanelDeviceStatus SetLines(bool dtr, bool rts);
}

/// <summary>
/// A USB device's serial port, found from the device's own identity, never by "the first port" (a wrong match would
/// write to another device): on Linux in sysfs (the device at its port path with its ids, bus and address, then
/// <c>/sys/bus/usb/devices/{port path}:{config}.{interface}/tty/…</c>, or a <c>ttyUSB</c> node beside it); on macOS in the
/// IORegistry (the USB device whose location id is its port path, with its ids and address, then the
/// <c>IOCalloutDevice</c> below it); on Windows in the device tree (the present USB device of its ids, told apart from
/// identical ones by its serial number, which Windows makes its instance id, the only one only in the candidate's own
/// port, and the COM port below it; several that can't be told apart are refused). And the opened port checked to be the device's: on Linux the open descriptor's
/// character device up through sysfs to its USB device; on macOS the descriptor's device number against the device's
/// callout node's; on Windows the COM name against the device's. Each is a pure function over what the OS gives, so the
/// tests give fake tables.
/// </summary>
public static partial class SerialPortFinder
{
    /// <summary>Linux: the <c>/dev</c> node of <paramref name="device"/>, read from the sysfs tree at <paramref name="sysfsRoot"/> (<c>/sys</c>).</summary>
    public static PanelDeviceStatus Linux(string sysfsRoot, PanelCandidate device, out string? node)
    {
        node = null;
        var portPath = device.PortPath;
        if (!UsbPortPath().IsMatch(portPath))
            return PanelDeviceStatus.NotSupported;
        var devices = Path.Combine(sysfsRoot, "bus", "usb", "devices");
        // The device at the port is the candidate: its ids, bus and address (a new address once it is plugged in again).
        if (!IsLinuxDevice(Path.Combine(devices, portPath), device))
            return PanelDeviceStatus.Gone;
        // The device's interfaces are siblings named {port}:{config}.{interface}.
        foreach (var interfaceDir in Directory.EnumerateDirectories(devices, portPath + ":*").OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(interfaceDir);
            if (!name.StartsWith(portPath + ":", StringComparison.Ordinal))
                continue;
            var tty = Path.Combine(interfaceDir, "tty");
            var found = Directory.Exists(tty) ? Directory.EnumerateDirectories(tty).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault()
                : Directory.EnumerateDirectories(interfaceDir, "tty*").Select(Path.GetFileName).Where(n => n != "tty").OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
            if (found is { Length: > 0 } && TtyName().IsMatch(found))
            {
                node = "/dev/" + found;
                return PanelDeviceStatus.Ok;
            }
        }
        return PanelDeviceStatus.NotSupported;
    }

    private static bool IsLinuxDevice(string usbDevice, PanelCandidate device) =>
        Hex(Path.Combine(usbDevice, "idVendor")) == device.VendorId && Hex(Path.Combine(usbDevice, "idProduct")) == device.ProductId
        && Decimal(Path.Combine(usbDevice, "busnum")) == device.Bus && Decimal(Path.Combine(usbDevice, "devnum")) == device.Address;

    /// <summary>
    /// Linux: whether the character device <paramref name="major"/>:<paramref name="minor"/> (an open port's, from its
    /// descriptor) is <paramref name="device"/>'s: <c>/sys/dev/char/{major}:{minor}</c> followed up to the USB device it
    /// belongs to, which must be at the candidate's port path with its ids, bus and address; else
    /// <see cref="PanelDeviceStatus.Gone"/>.
    /// </summary>
    public static PanelDeviceStatus LinuxOwner(string sysfsRoot, uint major, uint minor, PanelCandidate device)
    {
        DirectoryInfo? at;
        try
        {
            at = new DirectoryInfo(Path.Combine(sysfsRoot, "dev", "char", $"{major}:{minor}")).ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo;
        }
        catch (IOException)
        {
            return PanelDeviceStatus.Gone;
        }
        // Up from the tty to the first folder that is a USB device (it has a bus and an address).
        for (var depth = 0; at != null && depth < 16; depth++, at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "busnum")) && File.Exists(Path.Combine(at.FullName, "idVendor")))
                return at.Name == device.PortPath && IsLinuxDevice(at.FullName, device) ? PanelDeviceStatus.Ok : PanelDeviceStatus.Gone;
        }
        return PanelDeviceStatus.Gone;
    }

    private static string? ReadSmall(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? Hex(string path) => long.TryParse(ReadSmall(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? Decimal(string path) => long.TryParse(ReadSmall(path), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// macOS: the callout device (<c>/dev/cu.…</c>) below the USB device at <paramref name="device"/>'s location with its
    /// ids (and its address, where the tree gives it), in <c>ioreg -a -r -c IOUSBHostDevice -l</c>'s XML.
    /// </summary>
    public static PanelDeviceStatus MacOS(XDocument ioreg, PanelCandidate device, out string? port)
    {
        port = null;
        var atLocation = ioreg.Descendants("dict").Where(d => Integer(d, "locationID") is { } location && HidChooser.PortPathOfLocation((uint)location) == device.PortPath).ToList();
        // ioreg -r can show a device again inside a hub's subtree: every node at the location must be the candidate.
        if (atLocation.Count == 0 || atLocation.Any(d => Integer(d, "idVendor") != device.VendorId || Integer(d, "idProduct") != device.ProductId
            || (Integer(d, "USB Address") is { } address && address != device.Address)))
            return PanelDeviceStatus.Gone;
        var self = atLocation[0];
        port = self.Descendants("dict").Select(d => Text(d, "IOCalloutDevice")).FirstOrDefault(t => t is { Length: > 0 });
        return port is null ? PanelDeviceStatus.NotSupported : PanelDeviceStatus.Ok;
    }

    /// <summary>
    /// macOS: whether the port opened as device number <paramref name="opened"/> (its descriptor's) is
    /// <paramref name="device"/>'s: the candidate's callout node, found in <paramref name="ioreg"/> read after the open,
    /// has that device number (<paramref name="deviceNumberOf"/>, the node's <c>stat</c>); else <see cref="PanelDeviceStatus.Gone"/>.
    /// </summary>
    public static PanelDeviceStatus MacOSOwner(XDocument ioreg, PanelCandidate device, long opened, Func<string, long?> deviceNumberOf)
    {
        var found = MacOS(ioreg, device, out var callout);
        if (found != PanelDeviceStatus.Ok || callout is null)
            return found == PanelDeviceStatus.NotSupported ? PanelDeviceStatus.Gone : found;
        return deviceNumberOf(callout) == opened ? PanelDeviceStatus.Ok : PanelDeviceStatus.Gone;
    }

    private static XElement? ValueOf(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static long? Integer(XElement dict, string key) =>
        ValueOf(dict, key) is { Name.LocalName: "integer" } value && long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string? Text(XElement dict, string key) => ValueOf(dict, key) is { Name.LocalName: "string" } value ? value.Value : null;

    /// <summary>
    /// A present Windows USB device as the device tree gives it: its instance id (<c>USB\VID_1A86&amp;PID_5722\…</c>),
    /// whether Windows made that id from the device's serial number (its unique-id capability), the COM ports below it,
    /// and its location path (SetupAPI's <c>…#USBROOT(0)#USB(2)#USB(3)</c>, null when the system gives none).
    /// </summary>
    public sealed record WindowsUsbDevice(string InstanceId, bool UniqueId, IReadOnlyList<string> Ports, string? LocationPath = null)
    {
        /// <summary>The device's serial number, when Windows made its instance id from it; else null.</summary>
        public string? Serial => UniqueId && InstanceId.LastIndexOf('\\') is var at and >= 0 && at < InstanceId.Length - 1 ? InstanceId[(at + 1)..] : null;

        public bool Of(ushort vendorId, ushort productId) =>
            InstanceId.StartsWith($@"USB\VID_{vendorId:X4}&PID_{productId:X4}\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reason a Windows candidate is refused when identical devices can't be told apart.</summary>
    public const string WindowsIdenticalDevices = "several identical devices without serial numbers; unplug the others or use one at a time";

    /// <summary>The reason a Windows candidate is refused when the system doesn't say which USB port its device is in.</summary>
    public const string WindowsNoPort = "the system doesn't say which USB port the device is in";

    /// <summary>
    /// Windows: the USB port numbers of a device's location path (SetupAPI's, <c>PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USB(3)</c>:
    /// its <c>USB(n)</c> hops after <c>USBROOT</c>, the root hub's port first, as libusb numbers a port path); null when it has none
    /// or anything else follows <c>USBROOT</c>.
    /// </summary>
    public static IReadOnlyList<int>? WindowsPortChain(string? locationPath)
    {
        if (string.IsNullOrEmpty(locationPath))
            return null;
        var hops = locationPath.Split('#');
        var root = Array.FindIndex(hops, h => h.StartsWith("USBROOT(", StringComparison.OrdinalIgnoreCase));
        if (root < 0 || root == hops.Length - 1)
            return null;
        var ports = new List<int>();
        foreach (var hop in hops.AsSpan(root + 1))
        {
            var match = WindowsUsbHop().Match(hop);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                return null;
            ports.Add(port);
        }
        return ports;
    }

    /// <summary>The port numbers of a libusb port path (<c>1-2.3</c>: 2, then 3); null when it isn't one.</summary>
    private static IReadOnlyList<int>? PortNumbersOf(string portPath)
    {
        if (!UsbPortPath().IsMatch(portPath))
            return null;
        var ports = new List<int>();
        foreach (var part in portPath[(portPath.IndexOf('-') + 1)..].Split('.'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                return null;
            ports.Add(port);
        }
        return ports;
    }

    /// <summary>
    /// Windows: the COM port of <paramref name="device"/>, among the present USB devices <paramref name="devices"/>: the
    /// only one of its ids when it is in the candidate's port (its location path's port chain is the candidate's port
    /// path's) and, when it has a serial number, that is the candidate's (<paramref name="serialNumber"/>, asked then)
    /// where the candidate's can be read; else, of several, the one whose serial number is the candidate's (asked then).
    /// Several that can't be told apart, or one whose port the system doesn't give, answer <see cref="PanelDeviceStatus.Busy"/>
    /// with <paramref name="refusal"/> saying why, never a guess; none of its ids (or none with its serial, or the only
    /// one in another port), <see cref="PanelDeviceStatus.Gone"/>; a device with no COM port, or more than one,
    /// <see cref="PanelDeviceStatus.NotSupported"/>.
    /// </summary>
    public static PanelDeviceStatus Windows(IEnumerable<WindowsUsbDevice> devices, PanelCandidate device, Func<string?> serialNumber, out string? port, out string? refusal)
    {
        port = null;
        refusal = null;
        var same = devices.Where(d => d.Of(device.VendorId, device.ProductId)).ToList();
        if (same.Count == 0)
            return PanelDeviceStatus.Gone;
        WindowsUsbDevice it;
        if (same.Count == 1)
        {
            // The only device of the candidate's ids present is the candidate only in the candidate's own port (another
            // that replaced it elsewhere is not; one of the same ids in that very port is the same model in the same place,
            // taken as the same panel), and, where both serial numbers are known, with the candidate's.
            it = same[0];
            if (WindowsPortChain(it.LocationPath) is not { } chain || PortNumbersOf(device.PortPath) is not { } candidatePorts)
            {
                refusal = WindowsNoPort;
                return PanelDeviceStatus.Busy;
            }
            if (!chain.SequenceEqual(candidatePorts))
                return PanelDeviceStatus.Gone;
            if (it.Serial is { } itsSerial && serialNumber() is { Length: > 0 } serial && !string.Equals(itsSerial, serial, StringComparison.OrdinalIgnoreCase))
                return PanelDeviceStatus.Gone;
        }
        else
        {
            var serial = serialNumber();
            var bySerial = serial is { Length: > 0 } ? same.Where(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase)).ToList() : [];
            if (bySerial.Count == 1)
                it = bySerial[0];
            else if (bySerial.Count == 0 && serial is { Length: > 0 } && same.All(d => d.Serial != null))
                return PanelDeviceStatus.Gone; // each has a serial number, none the candidate's
            else
            {
                refusal = WindowsIdenticalDevices;
                return PanelDeviceStatus.Busy;
            }
        }
        if (it.Ports.Count != 1)
            return PanelDeviceStatus.NotSupported;
        port = it.Ports[0];
        return PanelDeviceStatus.Ok;
    }

    /// <summary>Windows: whether the COM port opened as <paramref name="opened"/> is <paramref name="device"/>'s, by the device tree read after the open.</summary>
    public static PanelDeviceStatus WindowsOwner(IEnumerable<WindowsUsbDevice> devices, PanelCandidate device, Func<string?> serialNumber, string opened)
    {
        var found = Windows(devices, device, serialNumber, out var port, out _);
        if (found != PanelDeviceStatus.Ok)
            return found == PanelDeviceStatus.NotSupported ? PanelDeviceStatus.Gone : found;
        return string.Equals(port, opened, StringComparison.OrdinalIgnoreCase) ? PanelDeviceStatus.Ok : PanelDeviceStatus.Gone;
    }

    [GeneratedRegex(@"\A\d+-\d+(\.\d+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsbPortPath();

    [GeneratedRegex(@"\Atty[A-Za-z]+\d+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TtyName();

    [GeneratedRegex(@"\AUSB\((\d{1,3})\)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUsbHop();
}

/// <summary>The raw port an opened serial port drives: System.IO.Ports' <see cref="SerialPort"/> in the engine, a fake in the tests.</summary>
internal interface ISerialLine
{
    string PortName { get; }

    bool IsOpen { get; }

    /// <summary>Writes all of <paramref name="data"/>; <see cref="TimeoutException"/> past <paramref name="timeoutMs"/>.</summary>
    void Write(byte[] data, int timeoutMs);

    /// <summary>Reads what has come, at least a byte; <see cref="TimeoutException"/> past <paramref name="timeoutMs"/>.</summary>
    int Read(byte[] buffer, int timeoutMs);

    /// <summary>The bytes written that haven't gone out yet (the OS's output queue).</summary>
    int BytesToWrite { get; }

    void DiscardInput();

    void DiscardOutput();

    void SetLines(bool dtr, bool rts);

    /// <summary>Closes the port. It may wait (System.IO.Ports flushes what is queued first).</summary>
    void Close();
}

/// <summary>
/// An open port: one call at a time, each within its one deadline (taken at the call's start: the wait for another call
/// included, a cancel while it waits throws, and the port gets only what is left); a read in slices; a dispose answers
/// the calls waiting for their turn <see cref="PanelDeviceStatus.Io"/> at once, gives what was written
/// <see cref="DrainWait"/> to go out (a screen-off written just before), throws away what is still queued both ways and
/// closes the port on a worker, ending the call under way, and returns within <see cref="EnginePanelDevices.DisposeWait"/>
/// whatever the close does (the port is unusable after).
/// </summary>
internal sealed class OpenedSerial(ISerialLine port, Func<int, DeviceDeadline>? deadlines = null) : IOpenedSerial
{
    /// <summary>How long a dispose waits for the output queue to empty before it throws away what is left: within <see cref="EnginePanelDevices.DisposeWait"/>.</summary>
    internal static readonly TimeSpan DrainWait = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _call = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private readonly Func<int, DeviceDeadline> _deadlines = deadlines ?? (ms => new DeviceDeadline(ms));
    private volatile bool _disposed;
    private int _closed;

    public string PortName => port.PortName;

    internal ISerialLine Line => port;

    private PanelDeviceStatus Call(int timeoutMs, CancellationToken cancellationToken, Func<int, DeviceDeadline, PanelDeviceStatus> call)
    {
        IPanelDevices.CheckTimeout(timeoutMs);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = _deadlines(timeoutMs);
        var entered = DeviceCall.Enter(_call, deadline, cancellationToken, _disposing.Token, out var left);
        if (entered != PanelDeviceStatus.Ok)
            return entered;
        try
        {
            if (_disposed)
                return PanelDeviceStatus.Io;
            try
            {
                var status = call(left, deadline);
                if (_disposed)
                    return PanelDeviceStatus.Io;
                cancellationToken.ThrowIfCancellationRequested();
                return status;
            }
            catch (TimeoutException)
            {
                if (_disposed)
                    return PanelDeviceStatus.Io;
                cancellationToken.ThrowIfCancellationRequested();
                return PanelDeviceStatus.Timeout;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return _disposed ? PanelDeviceStatus.Io : port.IsOpen ? PanelDeviceStatus.Io : PanelDeviceStatus.Gone;
            }
        }
        finally
        {
            _call.Release();
        }
    }

    public PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var bytes = data.ToArray();
        return Call(timeoutMs, cancellationToken, (left, _) =>
        {
            port.Write(bytes, left);
            return PanelDeviceStatus.Ok;
        });
    }

    public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
    {
        var into = new byte[buffer.Length];
        var got = 0;
        var status = Call(timeoutMs, cancellationToken, (_, deadline) =>
        {
            // Waiting for the first byte in slices, looking at the token and the handle between.
            while (true)
            {
                var wait = Math.Min(deadline.RemainingMs, (int)EnginePanelDevices.ReadSlice.TotalMilliseconds);
                if (wait <= 0)
                    return PanelDeviceStatus.Timeout;
                try
                {
                    got = port.Read(into, wait);
                    return PanelDeviceStatus.Ok;
                }
                catch (TimeoutException) when (!_disposed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        });
        into.AsSpan(0, got).CopyTo(buffer);
        read = got;
        return status;
    }

    public PanelDeviceStatus DiscardInput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            port.DiscardInput();
            return PanelDeviceStatus.Ok;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return PanelDeviceStatus.Io;
        }
    }

    public PanelDeviceStatus SetLines(bool dtr, bool rts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            port.SetLines(dtr, rts);
            return PanelDeviceStatus.Ok;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return port.IsOpen ? PanelDeviceStatus.Io : PanelDeviceStatus.Gone;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _disposing.Cancel(); // the calls waiting for their turn answer Io at once
        var bound = new DeviceDeadline((int)EnginePanelDevices.DisposeWait.TotalMilliseconds);
        // What was written gets a short drain (the last write, a screen-off, may still be queued), then what is queued
        // either way is thrown away, so the close has nothing to flush to a device that stalls; the close (System.IO.Ports
        // flushes before it closes) runs on a thread of its own (one that hangs holds no pool thread), ending the call
        // under way, and is waited for only within the bound.
        var closing = new Thread(() =>
        {
            Drain();
            try
            {
                port.DiscardOutput();
                port.DiscardInput();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // closing is what is left
            }
            try
            {
                port.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // nothing more to do
            }
        })
        { IsBackground = true, Name = "Serial close" };
        closing.Start();
        closing.Join(bound.RemainingMs);
        if (bound.RemainingMs > 0 && _call.Wait(bound.RemainingMs))
            _call.Release();
    }

    /// <summary>Waits, at most <see cref="DrainWait"/>, for the output queue to empty; a port that can't say ends it.</summary>
    private void Drain()
    {
        var drain = new DeviceDeadline((int)DrainWait.TotalMilliseconds);
        while (true)
        {
            try
            {
                if (port.BytesToWrite <= 0)
                    return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
            {
                return;
            }
            var left = drain.RemainingMs;
            if (left <= 0)
                return;
            Thread.Sleep(Math.Min(left, 5));
        }
    }
}

/// <summary>The wait for a handle's call lock within a call's one deadline.</summary>
internal static class DeviceCall
{
    /// <summary>
    /// Takes <paramref name="call"/> within what is left of <paramref name="deadline"/>: <see cref="PanelDeviceStatus.Ok"/>
    /// when taken, with the time then left in <paramref name="left"/> (at least 1 ms; none left is a timeout, the lock let
    /// go), the token looked at again; <see cref="PanelDeviceStatus.Timeout"/> once the deadline has passed;
    /// <see cref="PanelDeviceStatus.Io"/> at once when the handle is disposed (<paramref name="disposed"/>) while it waits.
    /// The caller's own cancel while it waits throws, before a dispose.
    /// </summary>
    public static PanelDeviceStatus Enter(SemaphoreSlim call, DeviceDeadline deadline, CancellationToken cancellationToken, CancellationToken disposed, out int left)
    {
        left = 0;
        var wait = deadline.RemainingMs;
        if (wait <= 0)
            return PanelDeviceStatus.Timeout;
        if (!call.Wait(0))
        {
            using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed);
            try
            {
                if (!call.Wait(wait, either.Token))
                    return PanelDeviceStatus.Timeout;
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PanelDeviceStatus.Io; // the handle disposed while the call waited
            }
        }
        left = deadline.RemainingMs;
        if (left > 0 && !cancellationToken.IsCancellationRequested)
            return PanelDeviceStatus.Ok;
        call.Release();
        cancellationToken.ThrowIfCancellationRequested();
        return PanelDeviceStatus.Timeout;
    }
}

/// <summary>
/// <see cref="ISerialSystem"/> over <c>System.IO.Ports</c>, the port found by <see cref="SerialPortFinder"/> for this OS
/// (macOS's IORegistry read through <c>ioreg</c>, killed when the call's token ends; Linux's <c>/sys</c>; Windows' device
/// tree through SetupAPI), and the opened port checked through its descriptor (Linux, macOS) or its name (Windows).
/// </summary>
public sealed class SerialPorts(ILogger logger) : ISerialSystem
{
    /// <summary>The longest <c>ioreg</c> is waited for, within the call's own deadline.</summary>
    public static readonly TimeSpan IoregTimeout = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _refused = [];

    public PanelDeviceStatus Find(PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken, out string? port)
    {
        port = null;
        try
        {
            if (OperatingSystem.IsLinux())
                return SerialPortFinder.Linux("/sys", device, out port);
            if (OperatingSystem.IsMacOS())
                return Ioreg(cancellationToken) is { } tree ? SerialPortFinder.MacOS(tree, device, out port) : PanelDeviceStatus.Timeout;
            if (OperatingSystem.IsWindows())
            {
                var status = SerialPortFinder.Windows(WindowsPorts.Devices(device.VendorId, device.ProductId), device, serialNumber, out port, out var refusal);
                if (refusal is not null)
                {
                    lock (_refused)
                    {
                        if (_refused.Add(device.PortPath))
                            logger.Warning("Panel device {Device}: its serial port isn't opened: {Reason}", device.PortPath, refusal);
                    }
                }
                return status;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Debug(ex, "The serial port of {Device} can't be looked for", device.PortPath);
        }
        return PanelDeviceStatus.NotSupported;
    }

    private XDocument? Ioreg(CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", ["-a", "-r", "-c", "IOUSBHostDevice", "-l"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process is null)
            return null;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(IoregTimeout);
        try
        {
            process.WaitForExitAsync(wait.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            logger.Debug("ioreg didn't answer within the call's deadline");
            return null;
        }
        return XDocument.Parse(output.GetAwaiter().GetResult(), LoadOptions.None);
    }

    public PanelDeviceStatus Open(string port, int baud, out IOpenedSerial? serial)
    {
        serial = null;
        // DTR and RTS off from the open on (the OS's own open may still raise DTR on Linux): set once the port is known to be the device's.
        var opened = new SerialPort(port, baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false };
        try
        {
            opened.Open();
            serial = new OpenedSerial(new PortLine(opened));
            return PanelDeviceStatus.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            opened.Dispose();
            // Windows refuses a port another program has open this way; elsewhere it is the permissions.
            return OperatingSystem.IsWindows() ? PanelDeviceStatus.Busy : PanelDeviceStatus.Access;
        }
        catch (FileNotFoundException)
        {
            opened.Dispose();
            return PanelDeviceStatus.Gone;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            opened.Dispose();
            logger.Debug(ex, "Serial port {Port} can't be opened", port);
            return PanelDeviceStatus.Io;
        }
    }

    public PanelDeviceStatus Verify(IOpenedSerial serial, PanelCandidate device, Func<string?> serialNumber, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SerialPortFinder.WindowsOwner(WindowsPorts.Devices(device.VendorId, device.ProductId), device, serialNumber, serial.PortName);
            if ((serial as OpenedSerial)?.Line is not PortLine { Descriptor: { } fd })
                return PanelDeviceStatus.NotSupported;
            if (OperatingSystem.IsLinux())
                return SerialNative.LinuxDeviceOf(fd) is { } number ? SerialPortFinder.LinuxOwner("/sys", number.Major, number.Minor, device) : PanelDeviceStatus.NotSupported;
            if (OperatingSystem.IsMacOS())
            {
                if (SerialNative.MacDeviceOf(fd) is not { } opened)
                    return PanelDeviceStatus.NotSupported;
                return Ioreg(cancellationToken) is { } tree ? SerialPortFinder.MacOSOwner(tree, device, opened, SerialNative.MacDeviceOf) : PanelDeviceStatus.Timeout;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Debug(ex, "Serial port {Port} can't be checked", serial.PortName);
        }
        return PanelDeviceStatus.NotSupported;
    }

    /// <summary>A <see cref="SerialPort"/> as an <see cref="ISerialLine"/>, with its descriptor (Unix) for the check.</summary>
    private sealed class PortLine(SerialPort port) : ISerialLine
    {
        public string PortName => port.PortName;

        public bool IsOpen => port.IsOpen;

        /// <summary>The open descriptor (Unix), from System.IO.Ports' stream, which gives no public way to it; null when it can't be had.</summary>
        public int? Descriptor
        {
            get
            {
                try
                {
                    var stream = port.BaseStream;
                    return stream.GetType().GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(stream) is SafeHandle { IsInvalid: false, IsClosed: false } handle
                        ? (int)handle.DangerousGetHandle() : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or FieldAccessException or TargetException)
                {
                    return null;
                }
            }
        }

        public void Write(byte[] data, int timeoutMs)
        {
            port.WriteTimeout = timeoutMs;
            port.Write(data, 0, data.Length);
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            port.ReadTimeout = timeoutMs;
            return port.Read(buffer, 0, buffer.Length);
        }

        public int BytesToWrite => port.BytesToWrite;

        public void DiscardInput() => port.DiscardInBuffer();

        public void DiscardOutput() => port.DiscardOutBuffer();

        public void SetLines(bool dtr, bool rts)
        {
            port.DtrEnable = dtr;
            port.RtsEnable = rts;
        }

        public void Close() => port.Dispose();
    }
}

/// <summary>The device number of an open descriptor or a path (Linux: <c>statx</c>; macOS: <c>fstat</c> and <c>stat</c>).</summary>
internal static class SerialNative
{
    /// <summary>Linux: the open descriptor's character device, from <c>statx</c> (<c>stx_rdev_major</c> and <c>_minor</c> at offsets 128 and 132).</summary>
    public static (uint Major, uint Minor)? LinuxDeviceOf(int fd)
    {
        var buffer = new byte[256];
        try
        {
            if (statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buffer) != 0)
                return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        return (BitConverter.ToUInt32(buffer, 128), BitConverter.ToUInt32(buffer, 132));
    }

    /// <summary>macOS: the open descriptor's device number, <c>st_rdev</c> at offset 24 of the 64-bit-inode <c>struct stat</c>.</summary>
    public static long? MacDeviceOf(int fd)
    {
        var buffer = new byte[256];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat_inode64(fd, buffer) : fstat(fd, buffer);
        return result == 0 ? BitConverter.ToInt32(buffer, 24) : null;
    }

    /// <summary>macOS: the device number of the node at <paramref name="path"/>.</summary>
    public static long? MacDeviceOf(string path)
    {
        var buffer = new byte[256];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? stat_inode64(path, buffer) : stat(path, buffer);
        return result == 0 ? BitConverter.ToInt32(buffer, 24) : null;
    }

    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(int fd, [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int stat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}

/// <summary>Windows: the present USB devices of some ids and the COM ports (the Ports class) below each, through SetupAPI and CfgMgr.</summary>
internal static class WindowsPorts
{
    public static IReadOnlyList<SerialPortFinder.WindowsUsbDevice> Devices(ushort vendorId, ushort productId)
    {
        var result = new List<SerialPortFinder.WindowsUsbDevice>();
        if (!OperatingSystem.IsWindows())
            return result;
        var prefix = $@"USB\VID_{vendorId:X4}&PID_{productId:X4}\";

        // The USB devices of those ids now present (not one interface of a composite device: its id has &MI_ before the
        // backslash), each with whether its instance id is its serial number, and its location path.
        var devices = new Dictionary<string, (bool Unique, string? Location)>(StringComparer.OrdinalIgnoreCase);
        var set = SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1))
            return result;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (IdOf(info.DevInst) is { } id && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    devices[id] = ((Capabilities(set, ref info) & CM_DEVCAP_UNIQUEID) != 0, LocationPath(set, ref info));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        // Each COM port's USB device: the first USB device above it that isn't one interface of a composite device.
        var ports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var portsClass = new Guid("4d36e978-e325-11ce-bfc1-08002be10318"); // GUID_DEVCLASS_PORTS
        set = SetupDiGetClassDevsW(ref portsClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == new IntPtr(-1))
            return result;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (HidChooser.WindowsUsbParent(AncestorIds(info.DevInst)) is not { } parent || !devices.ContainsKey(parent))
                    continue;
                var key = SetupDiOpenDevRegKey(set, ref info, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
                if (key == new IntPtr(-1))
                    continue;
                using var handle = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(key, ownsHandle: true);
                using var registry = Microsoft.Win32.RegistryKey.FromHandle(handle);
                if (registry.GetValue("PortName") is string name && name.Length > 0)
                {
                    if (!ports.TryGetValue(parent, out var list))
                        ports[parent] = list = [];
                    list.Add(name);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        foreach (var (id, (unique, location)) in devices)
            result.Add(new SerialPortFinder.WindowsUsbDevice(id, unique, ports.TryGetValue(id, out var list) ? list : [], location));
        return result;
    }

    /// <summary>The device's location path on USB (SPDRP_LOCATION_PATHS, a list of strings: the one through <c>USBROOT</c>); null when it has none.</summary>
    private static string? LocationPath(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, SPDRP_LOCATION_PATHS, out _, buffer, (uint)buffer.Length, out var required))
            return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length) & ~1);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.Contains("#USBROOT(", StringComparison.OrdinalIgnoreCase));
    }

    private static string? IdOf(uint node)
    {
        var id = new char[512];
        return CM_Get_Device_IDW(node, id, id.Length, 0) == 0 ? new string(id).TrimEnd('\0') : null;
    }

    /// <summary>A device node's id and its ancestors', its own first (at most 8).</summary>
    private static List<string> AncestorIds(uint node)
    {
        var ids = new List<string>();
        for (var depth = 0; depth < 8; depth++)
        {
            if (IdOf(node) is not { } id)
                break;
            ids.Add(id);
            if (CM_Get_Parent(out var parent, node, 0) != 0)
                break;
            node = parent;
        }
        return ids;
    }

    private static uint Capabilities(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new byte[4];
        return SetupDiGetDeviceRegistryPropertyW(set, ref info, SPDRP_CAPABILITIES, out _, buffer, (uint)buffer.Length, out _) ? BitConverter.ToUInt32(buffer, 0) : 0;
    }

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, SPDRP_CAPABILITIES = 0xF, SPDRP_LOCATION_PATHS = 0x23, CM_DEVCAP_UNIQUEID = 0x10;
    private const uint DICS_FLAG_GLOBAL = 0x1, DIREG_DEV = 0x1, KEY_READ = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA info, uint scope, uint profile, uint keyType, uint access);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
}
