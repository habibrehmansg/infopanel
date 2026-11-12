using System;
using System.Collections.Generic;

namespace InfoPanel.Panels.Usb;

/// <summary>
/// A USB device as the backend's device list shows it: what the OS has cached about it, read without any I/O to the
/// device. <see cref="Bus"/> and <see cref="Address"/> identify it for one engine run (the address changes when it is
/// plugged in again); <see cref="PortPath"/> is the bus and port chain (<c>1-2.3</c>), the same while it stays in one port.
/// </summary>
public sealed record UsbDeviceId(ushort VendorId, ushort ProductId, byte Bus, byte Address, string PortPath)
{
    public override string ToString() => $"{VendorId:x4}:{ProductId:x4} at {PortPath} (bus {Bus}, address {Address})";
}

/// <summary>What a USB call came to. The names follow libusb's errors, collapsed to what a caller acts on.</summary>
public enum UsbStatus
{
    Ok,
    /// <summary>Another program (or another engine) holds the device or its interface.</summary>
    Busy,
    /// <summary>The OS won't let this process open or claim it (permissions, a driver in the way).</summary>
    Access,
    /// <summary>The device went away (unplugged).</summary>
    NoDevice,
    /// <summary>The device, or its driver, can't do what was asked (on Windows: not bound to WinUSB).</summary>
    NotSupported,
    Timeout,
    /// <summary>Any other failure of a transfer or a call (I/O, a stalled endpoint, an overflow, a short write).</summary>
    Io,
}

/// <summary>
/// The engine's way to USB devices: the libusb backend (<see cref="LibUsbBackend"/>) in the engine, a fake in tests.
/// Deliberately small: it has no reset and no kernel-driver detach, so nothing above it can take a device from another
/// program (<see cref="UsbClaim"/> is the one way a device is opened for transfers).
/// <para>
/// It has no alternate-setting call either, and its bulk writes never send a zero-length packet. Both are deliberate:
/// on BeadaPanel firmware, a zero-length packet on the stream's endpoint, or an alternate setting selected after the
/// claim (a SET_INTERFACE request, even for setting 0), makes the panel stop reading both its stream and its status
/// endpoints, and it answers again only once it is unplugged and plugged in again. Keep both out of this seam and out
/// of every backend behind it.
/// </para>
/// </summary>
public interface IUsbBackend : IDisposable
{
    /// <summary>The devices connected now (no I/O to any of them).</summary>
    IReadOnlyList<UsbDeviceId> ListDevices();

    /// <summary>
    /// Opens a listed device; <see cref="UsbStatus.NoDevice"/> when it is gone, or its bus and address now hold a device
    /// with other ids or another port path.
    /// </summary>
    UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened);

    /// <summary>
    /// A listed device's descriptors as the OS has them cached (no I/O to the device): its class codes, release and the
    /// interfaces of its active configuration (its first, when it has none active), each with its endpoints;
    /// <see cref="UsbStatus.NoDevice"/> when it is gone. A backend that can't tell answers <see cref="UsbStatus.NotSupported"/>.
    /// </summary>
    UsbStatus Describe(UsbDeviceId device, out UsbDescriptor? descriptor)
    {
        descriptor = null;
        return UsbStatus.NotSupported;
    }
}

/// <summary>A device's cached descriptors (<see cref="IUsbBackend.Describe"/>).</summary>
public sealed record UsbDescriptor(byte DeviceClass, byte DeviceSubClass, byte DeviceProtocol, ushort BcdDevice, IReadOnlyList<UsbInterfaceDescriptor> Interfaces);

/// <summary>An interface's first alternate setting: its number, class codes and endpoints (address, transfer type 0–3 as bmAttributes' low bits, largest packet).</summary>
public sealed record UsbInterfaceDescriptor(int Number, byte Class, byte SubClass, byte Protocol, IReadOnlyList<(byte Address, int Type, int MaxPacketSize)> Endpoints);

/// <summary>An opened USB device. Its calls block; one thread uses it at a time.</summary>
public interface IUsbDevice : IDisposable
{
    /// <summary>The active configuration's value, 0 when the device is unconfigured.</summary>
    UsbStatus GetConfiguration(out int configuration);

    UsbStatus SetConfiguration(int configuration);

    UsbStatus ClaimInterface(int interfaceNumber);

    UsbStatus ReleaseInterface(int interfaceNumber);

    /// <summary>The device descriptor's serial number string, or null when it has none or it can't be read.</summary>
    string? ReadSerialNumber();

    /// <summary>
    /// A bulk transfer out of <paramref name="data"/> (never empty) on <paramref name="endpoint"/>; <see cref="UsbStatus.Ok"/>
    /// only when every byte went. No zero-length packet is added.
    /// </summary>
    UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs);

    /// <summary>A bulk transfer in on <paramref name="endpoint"/>, at most <paramref name="buffer"/>'s length.</summary>
    UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read);

    /// <summary>
    /// A zero-length packet on <paramref name="endpoint"/>: only the panel driver seam sends one, and only when a driver
    /// asks for it after a write that fills its last packet (never on BeadaPanel's path: see <see cref="IUsbBackend"/>).
    /// </summary>
    UsbStatus WriteZeroLengthPacket(byte endpoint, int timeoutMs) => UsbStatus.NotSupported;
}
