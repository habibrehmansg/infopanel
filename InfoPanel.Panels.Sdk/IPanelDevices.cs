using System;
using System.Collections.Generic;
using System.Threading;

namespace InfoPanel.Panels.Sdk;

/// <summary>
/// How a driver reaches its device: the one way in for a panel package, which references nothing but this SDK. The
/// engine (and the panel host) implement it over the OS's USB, HID and serial ports; the test kit
/// (<c>InfoPanel.Panels.Testing</c>) fakes it and records every transfer. A driver gets it through its public
/// constructor (one parameter of this type); a driver whose constructor takes nothing has no devices.
/// <para>
/// Every call answers a <see cref="PanelDeviceStatus"/> and never throws for the device's sake (a call on a handle
/// already disposed throws <see cref="ObjectDisposedException"/>). There is no reset, kernel-driver detach, alternate
/// setting or control transfer: a handle claims, transfers and lets go. Handles may be used from any thread, one call at
/// a time.
/// </para>
/// <para>
/// No call waits without end. Every call that can wait on the device (an open, a transfer, a report) takes a timeout in
/// milliseconds, from 1 to <see cref="MaxTimeoutMs"/> (any other value throws <see cref="ArgumentOutOfRangeException"/>
/// before anything reaches the device: there is no "wait forever"), and answers <see cref="PanelDeviceStatus.Timeout"/>
/// once it has passed; and a <see cref="CancellationToken"/> (the driver's own: a probe's or a session's stop): a call
/// whose token is cancelled before it starts throws <see cref="OperationCanceledException"/> and reaches nothing, and one
/// cancelled while it waits ends at once and throws it too, having sent or read an unknown part of its bytes (as a
/// timeout may have). Disposing a handle while a call on it waits (from another thread) ends that call at once with
/// <see cref="PanelDeviceStatus.Io"/>, and the dispose returns once it has ended; every call after it throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
public interface IPanelDevices
{
    /// <summary>The longest timeout a call takes, in milliseconds (60 s).</summary>
    public const int MaxTimeoutMs = 60_000;

    /// <summary>
    /// The device's descriptors as the OS has them cached (no I/O to the device): its class codes, its
    /// <c>bcdDevice</c>, and each interface of its active configuration with its class, subclass, protocol and
    /// endpoints. <see cref="PanelDeviceStatus.Gone"/> when it isn't listed any more.
    /// </summary>
    PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor);

    /// <summary>
    /// Opens the device and claims interface <paramref name="interfaceNumber"/> of its active configuration for bulk
    /// transfers, within <paramref name="timeoutMs"/>; disposing the handle releases the interface and closes the device.
    /// <see cref="PanelDeviceStatus.Busy"/> when another program (or another handle) holds it,
    /// <see cref="PanelDeviceStatus.Access"/> when the OS refuses (permissions, another OS driver bound to it),
    /// <see cref="PanelDeviceStatus.NotSupported"/> when it has no such interface.
    /// </summary>
    PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default);

    /// <summary>
    /// The device's HID collections that <paramref name="filter"/> passes, in interface order, as the OS has them listed
    /// (no I/O to the device); none when no collection passes. A device can have several that pass one filter (the same
    /// interface, usage page and report lengths): a driver whose start fails on one opens the next with
    /// <see cref="OpenHid(PanelCandidate, PanelHidCollection, int, out IPanelHid?, CancellationToken)"/>.
    /// <see cref="PanelDeviceStatus.Gone"/> when it isn't listed any more.
    /// </summary>
    PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections);

    /// <summary>
    /// Opens the first HID collection of the device that <paramref name="filter"/> passes (the first
    /// <see cref="ListHid"/> gives), within <paramref name="timeoutMs"/>; <see cref="PanelDeviceStatus.NotSupported"/> when
    /// none does.
    /// </summary>
    PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the HID collection <paramref name="collection"/> of the device, as <see cref="ListHid"/> gave it, within
    /// <paramref name="timeoutMs"/>; <see cref="PanelDeviceStatus.Gone"/> when the device, or that collection, isn't
    /// listed any more.
    /// </summary>
    PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the serial port of the device itself (a USB CDC port, found by the device's own location, never by
    /// "the first port"), set to <paramref name="options"/>, within <paramref name="timeoutMs"/>;
    /// <see cref="PanelDeviceStatus.NotSupported"/> when the device has none.
    /// </summary>
    PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> unless <paramref name="timeoutMs"/> is 1 to
    /// <see cref="MaxTimeoutMs"/>: the check every implementation makes before a call reaches the device.
    /// </summary>
    public static void CheckTimeout(int timeoutMs)
    {
        if (timeoutMs is < 1 or > MaxTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, $"A timeout is 1 to {MaxTimeoutMs} ms.");
    }
}

/// <summary>A device's cached descriptors: its class, subclass and protocol, its release (<c>bcdDevice</c>), and the interfaces of its active configuration.</summary>
public sealed record PanelUsbDescriptor(
    byte DeviceClass,
    byte DeviceSubClass,
    byte DeviceProtocol,
    ushort BcdDevice,
    IReadOnlyList<PanelUsbInterface> Interfaces);

/// <summary>One interface of the active configuration (its first alternate setting, the one a claim uses): its number, class, subclass, protocol and endpoints.</summary>
public sealed record PanelUsbInterface(
    int Number,
    byte Class,
    byte SubClass,
    byte Protocol,
    IReadOnlyList<PanelUsbEndpoint> Endpoints);

/// <summary>An endpoint's kind of transfer.</summary>
public enum PanelEndpointType { Control, Isochronous, Bulk, Interrupt }

/// <summary>
/// An endpoint: its address (bit 7 set for IN, device to host: <c>0x81</c>), its kind of transfer, and its largest
/// packet in bytes.
/// </summary>
public sealed record PanelUsbEndpoint(byte Address, PanelEndpointType Type, int MaxPacketSize)
{
    /// <summary>Whether data comes from the device on it.</summary>
    public bool IsIn => (Address & 0x80) != 0;
}

/// <summary>
/// A claimed USB interface. Disposing it releases the interface and closes the device; nothing is sent then (a
/// driver's screen-off is its own last writes).
/// </summary>
public interface IPanelUsb : IDisposable
{
    /// <summary>The claimed interface's endpoints.</summary>
    IReadOnlyList<PanelUsbEndpoint> Endpoints { get; }

    /// <summary>The device's serial number string, read when it was opened (null: it has none, or it couldn't be read).</summary>
    string? SerialNumber { get; }

    /// <summary>
    /// Writes <paramref name="data"/> to the OUT endpoint <paramref name="endpoint"/> in one bulk transfer, within
    /// <paramref name="timeoutMs"/>: <see cref="PanelDeviceStatus.Ok"/> only when every byte went (any other status may
    /// follow a part of them sent). With <paramref name="zeroLengthPacketIfFull"/>, a write whose length is a non-zero
    /// multiple of the endpoint's packet size is followed by a zero-length packet, which some firmware needs to see the
    /// end of a transfer (and some stops reading at: never send one unless the device asks for it); the write is
    /// <see cref="PanelDeviceStatus.Ok"/> only when that packet went too.
    /// </summary>
    PanelDeviceStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs, bool zeroLengthPacketIfFull = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one bulk transfer from the IN endpoint <paramref name="endpoint"/> into <paramref name="buffer"/>, within
    /// <paramref name="timeoutMs"/>; <paramref name="read"/> is the bytes it holds. The transfer ends when the buffer is
    /// full or a packet shorter than the endpoint's packet size comes; what the device sends after stays for the next
    /// read. <see cref="PanelDeviceStatus.Timeout"/> when nothing came; <see cref="PanelDeviceStatus.Io"/> when a packet
    /// came that is longer than what is left of the buffer (an overflow, its bytes lost), which a buffer of whole packets
    /// never meets.
    /// </summary>
    PanelDeviceStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which of a device's HID collections to open: by its interface number, by its output report being at least this many
/// bytes (the report id byte included), and by its top-level usage page; a null part passes any.
/// </summary>
public sealed record PanelHidFilter(int? Interface = null, int? MinOutputReport = null, ushort? UsagePage = null);

/// <summary>
/// One HID collection of a device, as <see cref="IPanelDevices.ListHid"/> lists it: its <see cref="Id"/> (the OS's own
/// name for it, which tells it from the device's other collections while the device stays plugged in; only for
/// <see cref="IPanelDevices.OpenHid(PanelCandidate, PanelHidCollection, int, out IPanelHid?, CancellationToken)"/> and
/// the log), its interface number, its top-level usage page and usage, and its longest reports, the report id byte
/// included.
/// </summary>
public sealed record PanelHidCollection(
    string Id,
    int Interface,
    ushort UsagePage,
    ushort Usage,
    int MaxInputReportLength,
    int MaxOutputReportLength,
    int MaxFeatureReportLength);

/// <summary>An opened HID collection. Every report's first byte is its report id (0 when the device uses none).</summary>
public interface IPanelHid : IDisposable
{
    /// <summary>The collection it opened.</summary>
    PanelHidCollection Collection { get; }

    /// <summary>The longest input report, the report id byte included.</summary>
    int MaxInputReportLength { get; }

    /// <summary>The longest output report, the report id byte included.</summary>
    int MaxOutputReportLength { get; }

    /// <summary>The longest feature report, the report id byte included.</summary>
    int MaxFeatureReportLength { get; }

    /// <summary>Writes one output report (at most <see cref="MaxOutputReportLength"/> bytes) within <paramref name="timeoutMs"/>.</summary>
    PanelDeviceStatus Write(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>Reads one input report into <paramref name="buffer"/> within <paramref name="timeoutMs"/>; <see cref="PanelDeviceStatus.Timeout"/> when none came.</summary>
    PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default);

    /// <summary>Sends a feature report within <paramref name="timeoutMs"/>.</summary>
    PanelDeviceStatus SetFeature(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>Gets the feature report whose id is <paramref name="buffer"/>'s first byte into it within <paramref name="timeoutMs"/>; <paramref name="read"/> is its length.</summary>
    PanelDeviceStatus GetFeature(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default);
}

/// <summary>A serial port's settings: its rate in bits a second, and whether DTR and RTS are raised once it is open.</summary>
public sealed record PanelSerialOptions(int Baud, bool Dtr = false, bool Rts = false);

/// <summary>An opened serial port (eight data bits, no parity, one stop bit).</summary>
public interface IPanelSerial : IDisposable
{
    /// <summary>The port's name, for the log (<c>COM5</c>, <c>/dev/cu.usbmodem1101</c>).</summary>
    string PortName { get; }

    /// <summary>Writes every byte of <paramref name="data"/> within <paramref name="timeoutMs"/> (any status but <see cref="PanelDeviceStatus.Ok"/> may follow a part of them sent).</summary>
    PanelDeviceStatus Write(ReadOnlySpan<byte> data, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads what has come, at least one byte and at most <paramref name="buffer"/>'s length, waiting at most
    /// <paramref name="timeoutMs"/> for the first; <see cref="PanelDeviceStatus.Timeout"/> when nothing came.
    /// </summary>
    PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default);

    /// <summary>Drops what has come and not been read; it doesn't wait on the device.</summary>
    PanelDeviceStatus DiscardInput();
}
