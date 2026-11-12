using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace InfoPanel.Panels.Usb;

/// <summary>
/// libusb-1.0, loaded by full path and called through function pointers: the engine's own binding, as FFmpeg's
/// (<see cref="InfoPanel.Images.Video.FFmpeg"/>), no package. Looked for beside the engine, then in the folder
/// <see cref="FolderVariable"/> names, then on macOS in Homebrew's folders, then on Linux the system's library; the
/// loader's own search never runs for a bare name, except Linux's system library, which is the one meant. Not found:
/// USB panels are unsupported (<see cref="Load"/> answers null and why) and nothing else changes.
/// </summary>
public sealed unsafe class LibUsb
{
    /// <summary>The variable that names a folder holding the libusb library (a development build, say).</summary>
    public const string FolderVariable = "INFOPANEL_LIBUSB_DIR";

    /// <summary>The system library's name on Linux, loaded through the system's search.</summary>
    public const string LinuxSystemLibrary = "libusb-1.0.so.0";

    private LibUsb(string path) => Path = path;

    /// <summary>The library loaded: a full path, or <see cref="LinuxSystemLibrary"/>.</summary>
    public string Path { get; }

    /// <summary>libusb's version, <c>major.minor.micro</c>.</summary>
    public string Version { get; private set; } = "";

    internal delegate* unmanaged[Cdecl]<void**, int> libusb_init;
    internal delegate* unmanaged[Cdecl]<void*, void> libusb_exit;
    internal delegate* unmanaged[Cdecl]<void*, void***, nint> libusb_get_device_list;
    internal delegate* unmanaged[Cdecl]<void**, int, void> libusb_free_device_list;
    internal delegate* unmanaged[Cdecl]<void*, LibUsbDeviceDescriptor*, int> libusb_get_device_descriptor;
    internal delegate* unmanaged[Cdecl]<void*, byte> libusb_get_bus_number;
    internal delegate* unmanaged[Cdecl]<void*, byte> libusb_get_device_address;
    internal delegate* unmanaged[Cdecl]<void*, byte*, int, int> libusb_get_port_numbers;
    internal delegate* unmanaged[Cdecl]<void*, void**, int> libusb_open;
    internal delegate* unmanaged[Cdecl]<void*, void> libusb_close;
    internal delegate* unmanaged[Cdecl]<void*, void*> libusb_get_device;
    internal delegate* unmanaged[Cdecl]<void*, int*, int> libusb_get_configuration;
    internal delegate* unmanaged[Cdecl]<void*, int, int> libusb_set_configuration;
    internal delegate* unmanaged[Cdecl]<void*, int, int> libusb_claim_interface;
    internal delegate* unmanaged[Cdecl]<void*, int, int> libusb_release_interface;
    internal delegate* unmanaged[Cdecl]<void*, byte, byte*, int, int> libusb_get_string_descriptor_ascii;
    internal delegate* unmanaged[Cdecl]<void*, byte, byte*, int, int*, uint, int> libusb_bulk_transfer;
    internal delegate* unmanaged[Cdecl]<LibUsbVersion*> libusb_get_version;
    internal delegate* unmanaged[Cdecl]<void*, LibUsbConfigDescriptor**, int> libusb_get_active_config_descriptor;
    internal delegate* unmanaged[Cdecl]<void*, byte, LibUsbConfigDescriptor**, int> libusb_get_config_descriptor;
    internal delegate* unmanaged[Cdecl]<LibUsbConfigDescriptor*, void> libusb_free_config_descriptor;

    /// <summary>
    /// The places looked in, in order: beside the engine (<paramref name="engineFolder"/>), the folder
    /// <paramref name="variableFolder"/> names, then on macOS <c>/opt/homebrew/lib</c> and <c>/usr/local/lib</c>, and on
    /// Linux <see cref="LinuxSystemLibrary"/>. Each folder gives its full library path for this OS.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string engineFolder, string? variableFolder, OSPlatform os)
    {
        var folders = new List<string> { engineFolder };
        if (!string.IsNullOrEmpty(variableFolder))
            folders.Add(variableFolder);
        if (os == OSPlatform.OSX)
        {
            folders.Add("/opt/homebrew/lib");
            folders.Add("/usr/local/lib");
        }
        var candidates = new List<string>();
        foreach (var folder in folders)
            candidates.Add(System.IO.Path.Combine(folder, FileName(os)));
        if (os == OSPlatform.Linux)
            candidates.Add(LinuxSystemLibrary);
        return candidates;
    }

    /// <summary>This process's candidates (<see cref="Candidates"/> for the engine's folder, the variable and this OS).</summary>
    public static IReadOnlyList<string> DefaultCandidates() =>
        Candidates(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(FolderVariable), CurrentOs());

    /// <summary>The library's file name on <paramref name="os"/>.</summary>
    public static string FileName(OSPlatform os) =>
        os == OSPlatform.Windows ? "libusb-1.0.dll"
        : os == OSPlatform.OSX ? "libusb-1.0.dylib"
        : "libusb-1.0.so.0";

    private static OSPlatform CurrentOs() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;

    /// <summary>
    /// The first of <paramref name="candidates"/> that loads and has every function this binding calls; null, with
    /// <paramref name="why"/>, when none does. A full path that doesn't exist is passed over without loading anything, and
    /// so is a relative one (but <see cref="LinuxSystemLibrary"/>).
    /// </summary>
    public static LibUsb? Load(IEnumerable<string> candidates, out string why)
    {
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            // Only a full path is loaded (a relative one would load from the working directory); Linux's system library
            // is the one bare name, found by the system's loader.
            if (!System.IO.Path.IsPathFullyQualified(candidate) && candidate != LinuxSystemLibrary)
            {
                reasons.Add($"{candidate}: not a full path, skipped");
                continue;
            }
            var isPath = System.IO.Path.IsPathFullyQualified(candidate);
            if (isPath && !File.Exists(candidate))
            {
                reasons.Add($"{candidate}: not found");
                continue;
            }
            var loaded = TryLoad(candidate, out var reason);
            if (loaded is not null)
            {
                why = "";
                return loaded;
            }
            reasons.Add($"{candidate}: {reason}");
        }
        why = reasons.Count == 0 ? "no place to look for libusb-1.0" : string.Join("; ", reasons);
        return null;
    }

    /// <summary>
    /// <see cref="Load"/>, and its one log line: where the library came from, or that USB panels are unsupported and why.
    /// </summary>
    public static LibUsb? LoadAndLog(IEnumerable<string> candidates, ILogger logger)
    {
        var library = Load(candidates, out var why);
        if (library is null)
            logger.Information("USB panels are unsupported: libusb-1.0 wasn't found ({Reason})", why);
        else
            logger.Information("libusb {Version} loaded from {Path}", library.Version, library.Path);
        return library;
    }

    private static LibUsb? TryLoad(string candidate, out string reason)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(candidate, out handle))
            {
                reason = "can't be loaded";
                return null;
            }
            var u = new LibUsb(candidate);
            u.libusb_init = (delegate* unmanaged[Cdecl]<void**, int>)Export(handle, "libusb_init");
            u.libusb_exit = (delegate* unmanaged[Cdecl]<void*, void>)Export(handle, "libusb_exit");
            u.libusb_get_device_list = (delegate* unmanaged[Cdecl]<void*, void***, nint>)Export(handle, "libusb_get_device_list");
            u.libusb_free_device_list = (delegate* unmanaged[Cdecl]<void**, int, void>)Export(handle, "libusb_free_device_list");
            u.libusb_get_device_descriptor = (delegate* unmanaged[Cdecl]<void*, LibUsbDeviceDescriptor*, int>)Export(handle, "libusb_get_device_descriptor");
            u.libusb_get_bus_number = (delegate* unmanaged[Cdecl]<void*, byte>)Export(handle, "libusb_get_bus_number");
            u.libusb_get_device_address = (delegate* unmanaged[Cdecl]<void*, byte>)Export(handle, "libusb_get_device_address");
            u.libusb_get_port_numbers = (delegate* unmanaged[Cdecl]<void*, byte*, int, int>)Export(handle, "libusb_get_port_numbers");
            u.libusb_open = (delegate* unmanaged[Cdecl]<void*, void**, int>)Export(handle, "libusb_open");
            u.libusb_close = (delegate* unmanaged[Cdecl]<void*, void>)Export(handle, "libusb_close");
            u.libusb_get_device = (delegate* unmanaged[Cdecl]<void*, void*>)Export(handle, "libusb_get_device");
            u.libusb_get_configuration = (delegate* unmanaged[Cdecl]<void*, int*, int>)Export(handle, "libusb_get_configuration");
            u.libusb_set_configuration = (delegate* unmanaged[Cdecl]<void*, int, int>)Export(handle, "libusb_set_configuration");
            u.libusb_claim_interface = (delegate* unmanaged[Cdecl]<void*, int, int>)Export(handle, "libusb_claim_interface");
            u.libusb_release_interface = (delegate* unmanaged[Cdecl]<void*, int, int>)Export(handle, "libusb_release_interface");
            u.libusb_get_string_descriptor_ascii = (delegate* unmanaged[Cdecl]<void*, byte, byte*, int, int>)Export(handle, "libusb_get_string_descriptor_ascii");
            u.libusb_bulk_transfer = (delegate* unmanaged[Cdecl]<void*, byte, byte*, int, int*, uint, int>)Export(handle, "libusb_bulk_transfer");
            u.libusb_get_version = (delegate* unmanaged[Cdecl]<LibUsbVersion*>)Export(handle, "libusb_get_version");
            u.libusb_get_active_config_descriptor = (delegate* unmanaged[Cdecl]<void*, LibUsbConfigDescriptor**, int>)Export(handle, "libusb_get_active_config_descriptor");
            u.libusb_get_config_descriptor = (delegate* unmanaged[Cdecl]<void*, byte, LibUsbConfigDescriptor**, int>)Export(handle, "libusb_get_config_descriptor");
            u.libusb_free_config_descriptor = (delegate* unmanaged[Cdecl]<LibUsbConfigDescriptor*, void>)Export(handle, "libusb_free_config_descriptor");
            var v = u.libusb_get_version();
            u.Version = v is null ? "?" : $"{v->Major}.{v->Minor}.{v->Micro}";
            reason = "";
            return u;
        }
        catch (Exception e) when (e is LibUsbLoadException or BadImageFormatException)
        {
            if (handle != IntPtr.Zero)
                NativeLibrary.Free(handle);
            reason = e.Message;
            return null;
        }
    }

    private static IntPtr Export(IntPtr library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new LibUsbLoadException($"no function {name}");

    /// <summary>A libusb return code as a <see cref="UsbStatus"/>.</summary>
    public static UsbStatus Status(int code) => code switch
    {
        >= 0 => UsbStatus.Ok,
        -3 => UsbStatus.Access,         // LIBUSB_ERROR_ACCESS
        -4 => UsbStatus.NoDevice,       // LIBUSB_ERROR_NO_DEVICE
        -6 => UsbStatus.Busy,           // LIBUSB_ERROR_BUSY
        -7 => UsbStatus.Timeout,        // LIBUSB_ERROR_TIMEOUT
        -12 => UsbStatus.NotSupported,  // LIBUSB_ERROR_NOT_SUPPORTED
        _ => UsbStatus.Io,              // IO, INVALID_PARAM, NOT_FOUND, OVERFLOW, PIPE, INTERRUPTED, NO_MEM, OTHER
    };

    private sealed class LibUsbLoadException(string message) : Exception(message);
}

/// <summary><c>struct libusb_device_descriptor</c> (USB 2.0 §9.6.1, 18 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbDeviceDescriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public ushort bcdUSB;
    public byte bDeviceClass;
    public byte bDeviceSubClass;
    public byte bDeviceProtocol;
    public byte bMaxPacketSize0;
    public ushort idVendor;
    public ushort idProduct;
    public ushort bcdDevice;
    public byte iManufacturer;
    public byte iProduct;
    public byte iSerialNumber;
    public byte bNumConfigurations;
}

/// <summary><c>struct libusb_endpoint_descriptor</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LibUsbEndpointDescriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public byte bEndpointAddress;
    public byte bmAttributes;
    public ushort wMaxPacketSize;
    public byte bInterval;
    public byte bRefresh;
    public byte bSynchAddress;
    public byte* extra;
    public int extra_length;
}

/// <summary><c>struct libusb_interface_descriptor</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LibUsbInterfaceDescriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public byte bInterfaceNumber;
    public byte bAlternateSetting;
    public byte bNumEndpoints;
    public byte bInterfaceClass;
    public byte bInterfaceSubClass;
    public byte bInterfaceProtocol;
    public byte iInterface;
    public LibUsbEndpointDescriptor* endpoint;
    public byte* extra;
    public int extra_length;
}

/// <summary><c>struct libusb_interface</c>: an interface's alternate settings.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LibUsbInterface
{
    public LibUsbInterfaceDescriptor* altsetting;
    public int num_altsetting;
}

/// <summary><c>struct libusb_config_descriptor</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LibUsbConfigDescriptor
{
    public byte bLength;
    public byte bDescriptorType;
    public ushort wTotalLength;
    public byte bNumInterfaces;
    public byte bConfigurationValue;
    public byte iConfiguration;
    public byte bmAttributes;
    public byte MaxPower;
    public LibUsbInterface* @interface;
    public byte* extra;
    public int extra_length;
}

/// <summary><c>struct libusb_version</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbVersion
{
    public ushort Major;
    public ushort Minor;
    public ushort Micro;
    public ushort Nano;
    public IntPtr Rc;
    public IntPtr Describe;
}
