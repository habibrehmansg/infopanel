using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Devices;

/// <summary>
/// <see cref="IHidSystem"/> over HidSharp: the OS's HID devices by ids (listing reads no report and writes nothing), each
/// collection with its USB port and its USB device's bus and address where the OS tells them (Linux, from
/// <c>hidraw</c>'s sysfs link; macOS, from the IORegistry path and the USB device's <c>USB Address</c>), its interface
/// (Windows, from the path's <c>mi_</c>) and its physical USB device (Windows, from the device tree).
/// <para>
/// Opens take HidSharp's exclusivity layer (<see cref="OpenOption.Exclusive"/>, a lock across processes that every panel
/// host takes, not interruptible), so InfoPanel's own hosts never share a panel. HidSharp offers no OS share mode (on
/// Windows it opens a collection shared for reading and writing, as 1.4.x's panels did, and it has no IOKit seize
/// option): another program driving the same panel at the same time isn't kept out by the OS, as in 1.4.x.
/// </para>
/// </summary>
public sealed class HidSharpSystem(ILogger logger) : IHidSystem
{
    public IReadOnlyList<HidEntry> List(ushort vendorId, ushort productId)
    {
        var entries = new List<HidEntry>();
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Debug(ex, "HID devices {Ids} can't be listed", $"{vendorId:x4}:{productId:x4}");
            return entries;
        }
        foreach (var device in devices)
        {
            try
            {
                var (port, usbInterface, bus, address) = LocationOf(device.DevicePath);
                var (page, usage) = UsageOf(device);
                entries.Add(new HidEntry(device.DevicePath, (ushort)device.VendorID, (ushort)device.ProductID, SerialOf(device), port, usbInterface,
                    page, usage, device.GetMaxInputReportLength(), device.GetMaxOutputReportLength(), device.GetMaxFeatureReportLength(),
                    OperatingSystem.IsWindows() ? WindowsHid.ParentOf(device.DevicePath) : null, bus, address));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.Debug(ex, "HID device {Path} can't be described", device.DevicePath);
            }
        }
        return entries;
    }

    private static string? SerialOf(HidDevice device)
    {
        try
        {
            return device.GetSerialNumber() is { Length: > 0 } serial ? serial : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static (ushort Page, ushort Usage) UsageOf(HidDevice device)
    {
        try
        {
            var usage = device.GetReportDescriptor().DeviceItems.FirstOrDefault()?.Usages.GetAllValues().FirstOrDefault() ?? 0;
            return ((ushort)(usage >> 16), (ushort)usage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    private static (string? Port, int Interface, byte? Bus, byte? Address) LocationOf(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var link = Path.Combine("/sys/class/hidraw", Path.GetFileName(path), "device");
            var target = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target == null || HidChooser.LinuxLocation(target) is not { } linux)
                return (null, 0, null, null);
            var usb = HidChooser.LinuxUsbDevice(target);
            return (linux.Port, linux.Interface, usb is null ? null : SmallNumber(Path.Combine(usb, "busnum")), usb is null ? null : SmallNumber(Path.Combine(usb, "devnum")));
        }
        if (OperatingSystem.IsMacOS())
        {
            if (HidChooser.MacLocation(path) is not { } mac)
                return (null, 0, null, null);
            byte? bus = byte.TryParse(mac.Port.AsSpan(0, mac.Port.IndexOf('-')), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : null;
            return (mac.Port, mac.Interface, bus, MacUsbAddress(path));
        }
        return (null, HidChooser.WindowsInterface(path), null, null);
    }

    /// <summary>A sysfs number file's value as a byte (a bus or an address), or null.</summary>
    private static byte? SmallNumber(string file)
    {
        try
        {
            return byte.TryParse(File.ReadAllText(file).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>macOS: the <c>USB Address</c> of the USB device a HID device's IORegistry path is under, or null.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static unsafe byte? MacUsbAddress(string hidPath)
    {
        if (HidChooser.MacUsbDevice(hidPath) is not { } devicePath)
            return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(devicePath + "\0");
        uint entry;
        fixed (byte* p = bytes)
            entry = InfoPanel.Sensors.MacOS.IOKit.IORegistryEntryFromPath(0, p);
        if (entry == 0)
            return null;
        var key = InfoPanel.Sensors.MacOS.CF.Str("USB Address");
        try
        {
            return InfoPanel.Sensors.MacOS.IOKit.TryIntProperty(entry, key, out var value) && value is >= 0 and <= 255 ? (byte)value : null;
        }
        finally
        {
            InfoPanel.Sensors.MacOS.CF.CFRelease(key);
            InfoPanel.Sensors.MacOS.IOKit.IOObjectRelease(entry);
        }
    }

    /// <summary>HidSharp's open: its exclusivity layer, not interruptible, waiting at most <paramref name="timeoutMs"/> for another holder.</summary>
    public static OpenConfiguration OpenOptions(int timeoutMs)
    {
        var options = new OpenConfiguration();
        options.SetOption(OpenOption.Exclusive, true);
        options.SetOption(OpenOption.Interruptible, false);
        options.SetOption(OpenOption.Transient, false);
        options.SetOption(OpenOption.TimeoutIfInterruptible, timeoutMs);
        options.SetOption(OpenOption.TimeoutIfTransient, timeoutMs);
        return options;
    }

    /// <summary>
    /// A call with no timeout of its own (HidSharp's feature reports) kept to <paramref name="timeoutMs"/>: one still
    /// waiting then is ended by <paramref name="interrupt"/> (the stream closed) and answers <see cref="PanelDeviceStatus.Timeout"/>.
    /// </summary>
    internal static PanelDeviceStatus Bounded(int timeoutMs, Func<PanelDeviceStatus> call, Action interrupt)
    {
        var running = Task.Run(call);
        if (running.Wait(timeoutMs))
            return running.Result;
        try
        {
            interrupt();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // closing is all it can do
        }
        return PanelDeviceStatus.Timeout;
    }

    public PanelDeviceStatus Open(HidEntry entry, int timeoutMs, out IHidStream? stream)
    {
        stream = null;
        var device = DeviceList.Local.GetHidDevices(entry.VendorId, entry.ProductId).FirstOrDefault(d => d.DevicePath == entry.Path);
        if (device is null)
            return PanelDeviceStatus.Gone;
        try
        {
            if (!device.TryOpen(OpenOptions(timeoutMs), out HidStream opened))
                return PanelDeviceStatus.Busy;
            stream = new Stream(opened);
            return PanelDeviceStatus.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            return PanelDeviceStatus.Access;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return PanelDeviceStatus.Busy;
        }
    }

    private sealed class Stream(HidStream stream) : IHidStream
    {
        private static PanelDeviceStatus Run(Action call)
        {
            try
            {
                call();
                return PanelDeviceStatus.Ok;
            }
            catch (TimeoutException)
            {
                return PanelDeviceStatus.Timeout;
            }
            catch (ObjectDisposedException)
            {
                return PanelDeviceStatus.Io;
            }
            catch (IOException)
            {
                return PanelDeviceStatus.Io;
            }
        }

        public PanelDeviceStatus Write(byte[] report, int timeoutMs) => Run(() =>
        {
            stream.WriteTimeout = timeoutMs;
            stream.Write(report);
        });

        public PanelDeviceStatus Read(byte[] buffer, int timeoutMs, out int read)
        {
            var got = 0;
            var status = Run(() =>
            {
                stream.ReadTimeout = timeoutMs;
                got = stream.Read(buffer, 0, buffer.Length);
            });
            read = got;
            return status;
        }

        public PanelDeviceStatus SetFeature(byte[] report, int timeoutMs) => Bounded(timeoutMs, () => Run(() => stream.SetFeature(report)), stream.Dispose);

        public PanelDeviceStatus GetFeature(byte[] buffer, int timeoutMs, out int read)
        {
            var status = Bounded(timeoutMs, () => Run(() => stream.GetFeature(buffer)), stream.Dispose);
            read = status == PanelDeviceStatus.Ok ? buffer.Length : 0;
            return status;
        }

        public void Dispose() => stream.Dispose();
    }
}

/// <summary>Windows: a HID collection's physical USB device.</summary>
internal static class WindowsHid
{
    /// <summary>The instance id of the USB device a HID collection's interface path belongs to (<see cref="HidChooser.WindowsUsbParent"/>), or null.</summary>
    public static string? ParentOf(string interfacePath)
    {
        try
        {
            var size = 1024u;
            var buffer = new char[size / 2];
            var key = DEVPKEY_Device_InstanceId;
            if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0) != 0)
                return null;
            var instance = new string(buffer).TrimEnd('\0');
            if (CM_Locate_DevNodeW(out var node, instance, 0) != 0)
                return null;
            var ids = new List<string>();
            for (var depth = 0; depth < 8; depth++)
            {
                var id = new char[512];
                if (CM_Get_Device_IDW(node, id, id.Length, 0) != 0)
                    break;
                ids.Add(new string(id).TrimEnd('\0'));
                if (CM_Get_Parent(out var parent, node, 0) != 0)
                    break;
                node = parent;
            }
            return HidChooser.WindowsUsbParent(ids);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, [Out] char[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
}
