using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Serilog;

namespace InfoPanel.Panels.Devices;

/// <summary>
/// The engine's <see cref="IPanelDevices"/>, the one way a hosted driver reaches its device: USB over the engine's own
/// <see cref="IUsbBackend"/> (libusb) and <see cref="UsbClaim"/>'s rules (configuration 1 set only when none is, the
/// interface asked for claimed, never a reset or a kernel-driver detach, so a device another program holds answers
/// <see cref="PanelDeviceStatus.Busy"/> or <see cref="PanelDeviceStatus.Access"/> with nothing written; transfers only on
/// the claimed interface's bulk endpoints, in the call's direction); HID through <see cref="IHidSystem"/> (HidSharp,
/// with its lock across processes) and <see cref="HidChooser"/> (every assignment reserved; the collection opened
/// checked again to be the candidate's device before anything is written); a serial port through
/// <see cref="ISerialSystem"/> (System.IO.Ports, the port found by the device's own identity, opened with DTR and RTS
/// off, checked to be the device's, and only then its lines set). A part the host has none of answers
/// <see cref="PanelDeviceStatus.NotSupported"/>.
/// <para>
/// One deadline a call, taken when it is called: an open (its descriptor lookup, listing and port finder included) and
/// every transfer spend only what is left of the caller's timeout, the wait for another call on the same handle too (a
/// cancel while it waits throws), and an open past it answers <see cref="PanelDeviceStatus.Timeout"/> (one that ends
/// late is disposed, never handed out); a cancelled token throws, an open's late handle disposed too. Reads wait in slices of
/// <see cref="ReadSlice"/>, so a cancellation or a dispose ends them within one. A dispose answers the calls waiting for
/// their turn <see cref="PanelDeviceStatus.Io"/> at once (a caller's own cancel still throws), and ends the call under way:
/// it closes a HID stream or a serial port at once (the call answers <see cref="PanelDeviceStatus.Io"/>); a USB write
/// (libusb's synchronous transfer, which has no cancel) ends at its own timeout, answering
/// <see cref="PanelDeviceStatus.Io"/>, and the device is closed then (a new open of it meanwhile answers
/// <see cref="PanelDeviceStatus.Busy"/>). A dispose returns within <see cref="DisposeWait"/> either way.
/// </para>
/// </summary>
public sealed class EnginePanelDevices : IPanelDevices
{
    /// <summary>The longest a read waits in one go before it looks at its token and its handle again.</summary>
    public static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest a dispose waits for the call under way to end.</summary>
    public static readonly TimeSpan DisposeWait = TimeSpan.FromMilliseconds(200);

    /// <summary>The configuration a device with none is set to before a claim (every family's, as 1.4.x set it).</summary>
    public const int Configuration = 1;

    private readonly IUsbBackend? _usb;
    private readonly IHidSystem? _hid;
    private readonly ISerialSystem? _serial;
    private readonly ILogger _logger;
    private readonly HidChooser _chooser;

    public EnginePanelDevices(IUsbBackend? usb, IHidSystem? hid, ISerialSystem? serial, ILogger logger)
    {
        _usb = usb;
        _hid = hid;
        _serial = serial;
        _logger = logger;
        _chooser = new HidChooser(logger);
    }

    /// <summary>Each call's deadline from its timeout (the tests script one).</summary>
    internal Func<int, DeviceDeadline> Deadlines { get; init; } = timeoutMs => new DeviceDeadline(timeoutMs);

    internal static UsbDeviceId UsbIdOf(PanelCandidate device) => new(device.VendorId, device.ProductId, device.Bus, device.Address, device.PortPath);

    internal static PanelDeviceStatus StatusOf(UsbStatus status) => status switch
    {
        UsbStatus.Ok => PanelDeviceStatus.Ok,
        UsbStatus.Busy => PanelDeviceStatus.Busy,
        UsbStatus.Access => PanelDeviceStatus.Access,
        UsbStatus.NoDevice => PanelDeviceStatus.Gone,
        UsbStatus.NotSupported => PanelDeviceStatus.NotSupported,
        UsbStatus.Timeout => PanelDeviceStatus.Timeout,
        _ => PanelDeviceStatus.Io,
    };

    public PanelDeviceStatus Describe(PanelCandidate device, out PanelUsbDescriptor? descriptor)
    {
        descriptor = null;
        if (_usb is null)
            return PanelDeviceStatus.NotSupported;
        var status = _usb.Describe(UsbIdOf(device), out var cached);
        if (status != UsbStatus.Ok || cached is null)
            return StatusOf(status == UsbStatus.Ok ? UsbStatus.Io : status);
        descriptor = new PanelUsbDescriptor(cached.DeviceClass, cached.DeviceSubClass, cached.DeviceProtocol, cached.BcdDevice,
            [.. cached.Interfaces.Select(i => new PanelUsbInterface(i.Number, i.Class, i.SubClass, i.Protocol, EndpointsOf(i)))]);
        return PanelDeviceStatus.Ok;
    }

    private static IReadOnlyList<PanelUsbEndpoint> EndpointsOf(UsbInterfaceDescriptor descriptor) =>
        [.. descriptor.Endpoints.Select(e => new PanelUsbEndpoint(e.Address, (PanelEndpointType)e.Type, e.MaxPacketSize))];

    public PanelDeviceStatus OpenUsb(PanelCandidate device, int interfaceNumber, int timeoutMs, out IPanelUsb? usb, CancellationToken cancellationToken = default)
    {
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        usb = null;
        if (_usb is null)
            return PanelDeviceStatus.NotSupported;

        var backend = _usb;
        return DeviceOpen.Run<IPanelUsb>(timeoutMs, cancellationToken, token =>
        {
            // The interface's endpoints from the cached descriptors, which every transfer is checked against: a device whose
            // descriptors can't be read, or without that interface, is refused before it is opened.
            var described = backend.Describe(UsbIdOf(device), out var cached);
            if (described != UsbStatus.Ok || cached is null)
                return (StatusOf(described == UsbStatus.Ok ? UsbStatus.Io : described), null);
            if (cached.Interfaces.FirstOrDefault(i => i.Number == interfaceNumber) is not { } claimed)
                return (PanelDeviceStatus.NotSupported, null);
            var endpoints = EndpointsOf(claimed);
            if (token.IsCancellationRequested)
                return (PanelDeviceStatus.Timeout, null);
            var status = UsbClaim.Open(backend, UsbIdOf(device), Configuration, interfaceNumber, out var opened);
            if (status != UsbStatus.Ok || opened is null)
            {
                _logger.Debug("Panel device {Device}: interface {Interface} not claimed ({Status})", UsbIdOf(device), interfaceNumber, status);
                return (StatusOf(status == UsbStatus.Ok ? UsbStatus.Io : status), null);
            }
            string? serial;
            try
            {
                serial = opened.ReadSerialNumber();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                serial = null;
            }
            return (PanelDeviceStatus.Ok, new ClaimedUsb(opened, endpoints, serial, Deadlines));
        }, out usb);
    }

    public PanelDeviceStatus ListHid(PanelCandidate device, PanelHidFilter filter, out IReadOnlyList<PanelHidCollection> collections)
    {
        collections = [];
        if (_hid is null)
            return PanelDeviceStatus.NotSupported;
        var status = _chooser.Collections(device, _hid.List(device.VendorId, device.ProductId), filter, out var entries);
        collections = [.. entries.Select(HidChooser.CollectionOf)];
        return status;
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidFilter filter, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        hid = null;
        if (_hid is null)
            return PanelDeviceStatus.NotSupported;
        var system = _hid;
        var deadline = new DeviceDeadline(timeoutMs);
        // The listing and the choice too within the open's deadline.
        return DeviceOpen.Run<IPanelHid>(timeoutMs, cancellationToken, token =>
        {
            var status = _chooser.Collections(device, system.List(device.VendorId, device.ProductId), filter, out var entries);
            if (status != PanelDeviceStatus.Ok)
                return (status, null);
            return entries.Count == 0 ? (PanelDeviceStatus.NotSupported, null) : OpenEntry(system, device, entries[0], deadline, token);
        }, out hid);
    }

    public PanelDeviceStatus OpenHid(PanelCandidate device, PanelHidCollection collection, int timeoutMs, out IPanelHid? hid, CancellationToken cancellationToken = default)
    {
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        hid = null;
        if (_hid is null)
            return PanelDeviceStatus.NotSupported;
        var system = _hid;
        var deadline = new DeviceDeadline(timeoutMs);
        return DeviceOpen.Run<IPanelHid>(timeoutMs, cancellationToken, token =>
        {
            // The collection asked for, within the physical device given to this panel (taken again when it was let go).
            var status = _chooser.ForCollection(device, system.List(device.VendorId, device.ProductId), collection.Id, out var entry);
            return status != PanelDeviceStatus.Ok || entry is null ? (status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Gone : status, null)
                : OpenEntry(system, device, entry, deadline, token);
        }, out hid);
    }

    /// <summary>
    /// Opens <paramref name="entry"/> held for <paramref name="device"/>, then lists again to check, before anything is
    /// written, that the collection opened is still the candidate's device (its port, bus and address where the OS
    /// tells them): another device that took its place answers <see cref="PanelDeviceStatus.Gone"/>, closed at once.
    /// </summary>
    private (PanelDeviceStatus, IPanelHid?) OpenEntry(IHidSystem system, PanelCandidate device, HidEntry entry, DeviceDeadline deadline, CancellationToken token)
    {
        if (token.IsCancellationRequested || deadline.RemainingMs is var left && left <= 0)
            return (PanelDeviceStatus.Timeout, null);
        var hold = _chooser.Hold(device, entry);
        var status = system.Open(entry, left, out var stream);
        if (status != PanelDeviceStatus.Ok || stream is null)
        {
            stream?.Dispose();
            hold.Dispose();
            return (status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, null);
        }
        if (!HidChooser.IsStill(entry, system.List(device.VendorId, device.ProductId), device))
        {
            stream.Dispose();
            hold.Dispose();
            _logger.Debug("Panel device {Device}: another device took its place before its HID collection was opened", UsbIdOf(device));
            return (PanelDeviceStatus.Gone, null);
        }
        return (PanelDeviceStatus.Ok, new OpenedHid(HidChooser.CollectionOf(entry), stream, hold, Deadlines));
    }

    public PanelDeviceStatus OpenSerial(PanelCandidate device, PanelSerialOptions options, int timeoutMs, out IPanelSerial? serial, CancellationToken cancellationToken = default)
    {
        IPanelDevices.CheckTimeout(timeoutMs);
        cancellationToken.ThrowIfCancellationRequested();
        serial = null;
        if (_serial is null)
            return PanelDeviceStatus.NotSupported;
        var system = _serial;
        Func<string?> serialNumber = () => UsbSerialOf(device);
        var deadline = new DeviceDeadline(timeoutMs);
        return DeviceOpen.Run<IPanelSerial>(timeoutMs, cancellationToken, token =>
        {
            // Only the device's own port, found by its identity: never "the first port".
            var found = system.Find(device, serialNumber, token, out var port);
            if (token.IsCancellationRequested)
                return (PanelDeviceStatus.Timeout, null);
            if (found != PanelDeviceStatus.Ok || port is null)
            {
                _logger.Debug("Panel device {Device}: no serial port of its own found ({Status})", UsbIdOf(device), found);
                return (found == PanelDeviceStatus.Ok ? PanelDeviceStatus.NotSupported : found, null);
            }
            // Opened with DTR and RTS off and nothing written; the opened port checked to be the device's (another that took
            // the name meanwhile is closed at once: gone); only then its lines set as the session asks.
            var status = system.Open(port, options.Baud, out var opened);
            if (status != PanelDeviceStatus.Ok || opened is null)
            {
                opened?.Dispose();
                return (status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, null);
            }
            var verified = token.IsCancellationRequested ? PanelDeviceStatus.Timeout : system.Verify(opened, device, serialNumber, token);
            if (verified != PanelDeviceStatus.Ok)
            {
                opened.Dispose();
                _logger.Debug("Panel device {Device}: the port {Port} opened isn't its own ({Status})", UsbIdOf(device), port, verified);
                return (token.IsCancellationRequested ? PanelDeviceStatus.Timeout : verified, null);
            }
            // The check may have taken past the deadline or the caller cancelled during it: the caller no longer waits, so
            // the lines stay as they are and the port is closed.
            if (token.IsCancellationRequested || deadline.Passed)
            {
                opened.Dispose();
                return (PanelDeviceStatus.Timeout, null);
            }
            var lines = opened.SetLines(options.Dtr, options.Rts);
            if (lines != PanelDeviceStatus.Ok)
            {
                opened.Dispose();
                return (lines, null);
            }
            return (PanelDeviceStatus.Ok, opened);
        }, out serial);
    }

    /// <summary>
    /// The device's USB serial number, for a port finder that can't tell identical devices apart otherwise (Windows): read
    /// through libusb (opened, never claimed; a device whose driver libusb can't open has none), null when it can't be.
    /// </summary>
    private string? UsbSerialOf(PanelCandidate device)
    {
        if (_usb is null || _usb.Open(UsbIdOf(device), out var opened) != UsbStatus.Ok || opened is null)
            return null;
        using (opened)
        {
            try
            {
                return opened.ReadSerialNumber();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A claimed interface: one call at a time, each within its one deadline (the wait for another call included, a
    /// cancel while it waits throws, the transfer gets only what is left); transfers only on its bulk endpoints in their
    /// direction; a read in slices, gathering what comes until its buffer is full, a short packet ends it or its deadline passes.
    /// </summary>
    private sealed class ClaimedUsb(IUsbDevice device, IReadOnlyList<PanelUsbEndpoint> endpoints, string? serial, Func<int, DeviceDeadline> deadlines) : IPanelUsb
    {
        private readonly SemaphoreSlim _call = new(1, 1);
        private readonly CancellationTokenSource _disposing = new();
        private volatile bool _disposed;
        private int _closed;

        public IReadOnlyList<PanelUsbEndpoint> Endpoints => endpoints;

        public string? SerialNumber => serial;

        /// <summary>The claimed interface's bulk endpoint at <paramref name="address"/> in that direction, else null.</summary>
        private PanelUsbEndpoint? BulkEndpoint(byte address, bool isIn) =>
            endpoints.FirstOrDefault(e => e.Address == address && e.Type == PanelEndpointType.Bulk && e.IsIn == isIn);

        public PanelDeviceStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs, bool zeroLengthPacketIfFull = false, CancellationToken cancellationToken = default)
        {
            IPanelDevices.CheckTimeout(timeoutMs);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (data.IsEmpty)
                throw new ArgumentException("A bulk write has at least one byte; a zero-length packet is asked for with zeroLengthPacketIfFull.", nameof(data));
            if (BulkEndpoint(endpoint, isIn: false) is not { } outEndpoint)
                return PanelDeviceStatus.NotSupported;
            var deadline = deadlines(timeoutMs);
            var entered = DeviceCall.Enter(_call, deadline, cancellationToken, _disposing.Token, out var left);
            if (entered != PanelDeviceStatus.Ok)
                return entered;
            try
            {
                if (_disposed)
                    return PanelDeviceStatus.Io;
                var status = StatusOf(device.BulkWrite(endpoint, data, left));
                if (_disposed)
                    return PanelDeviceStatus.Io;
                cancellationToken.ThrowIfCancellationRequested();
                // A zero-length packet only when asked, and only when the write filled its last packet, in what is left:
                // read once, and none left is a timeout without it (libusb takes a timeout of 0 as none at all).
                var packet = outEndpoint.MaxPacketSize > 0 ? outEndpoint.MaxPacketSize : 512;
                if (status == PanelDeviceStatus.Ok && zeroLengthPacketIfFull && data.Length % packet == 0)
                {
                    var rest = deadline.RemainingMs;
                    if (rest <= 0)
                        return PanelDeviceStatus.Timeout;
                    status = StatusOf(device.WriteZeroLengthPacket(endpoint, rest));
                    if (_disposed)
                        return PanelDeviceStatus.Io;
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return status;
            }
            finally
            {
                CloseIfDisposed();
                _call.Release();
            }
        }

        public PanelDeviceStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            IPanelDevices.CheckTimeout(timeoutMs);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            read = 0;
            if (BulkEndpoint(endpoint, isIn: true) is null)
                return PanelDeviceStatus.NotSupported;
            var deadline = deadlines(timeoutMs);
            var entered = DeviceCall.Enter(_call, deadline, cancellationToken, _disposing.Token, out var left);
            if (entered != PanelDeviceStatus.Ok)
                return entered;
            try
            {
                // left: the slices waited out too, so a backend that answers at once still ends.
                while (true)
                {
                    if (_disposed)
                        return PanelDeviceStatus.Io;
                    var wait = Math.Min(Math.Min(left, deadline.RemainingMs), (int)ReadSlice.TotalMilliseconds);
                    if (wait <= 0)
                        return read > 0 ? PanelDeviceStatus.Ok : PanelDeviceStatus.Timeout;
                    var status = device.BulkRead(endpoint, buffer[read..], wait, out var got);
                    read += got;
                    if (_disposed)
                        return PanelDeviceStatus.Io;
                    // Done: the buffer full or a short packet (libusb's Ok), or a failure.
                    if (status != UsbStatus.Timeout)
                        return StatusOf(status);
                    if (read == buffer.Length)
                        return PanelDeviceStatus.Ok;
                    cancellationToken.ThrowIfCancellationRequested();
                    left -= wait;
                }
            }
            finally
            {
                CloseIfDisposed();
                _call.Release();
            }
        }

        private void CloseIfDisposed()
        {
            if (_disposed && Interlocked.Exchange(ref _closed, 1) == 0)
                device.Dispose();
        }

        public void Dispose()
        {
            _disposed = true;
            _disposing.Cancel(); // the calls waiting for their turn answer Io at once
            // A read ends within a slice; a write at its own timeout (libusb's synchronous transfer can't be cancelled,
            // and closing the device under it isn't safe): that call closes the device when it ends.
            if (_call.Wait(DisposeWait))
            {
                try
                {
                    CloseIfDisposed();
                }
                finally
                {
                    _call.Release();
                }
            }
        }
    }

    /// <summary>
    /// An open HID collection: one call at a time, each within its one deadline (the wait for another call included, a
    /// cancel while it waits throws, the report gets only what is left); a read in slices; a dispose closes the stream
    /// at once, ending the call under way.
    /// </summary>
    private sealed class OpenedHid(PanelHidCollection collection, IHidStream stream, IDisposable hold, Func<int, DeviceDeadline> deadlines) : IPanelHid
    {
        private readonly SemaphoreSlim _call = new(1, 1);
        private readonly CancellationTokenSource _disposing = new();
        private volatile bool _disposed;
        private int _closed;

        public PanelHidCollection Collection => collection;

        public int MaxInputReportLength => collection.MaxInputReportLength;

        public int MaxOutputReportLength => collection.MaxOutputReportLength;

        public int MaxFeatureReportLength => collection.MaxFeatureReportLength;

        private PanelDeviceStatus Call(int timeoutMs, CancellationToken cancellationToken, Func<int, DeviceDeadline, PanelDeviceStatus> call)
        {
            IPanelDevices.CheckTimeout(timeoutMs);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = deadlines(timeoutMs);
            var entered = DeviceCall.Enter(_call, deadline, cancellationToken, _disposing.Token, out var left);
            if (entered != PanelDeviceStatus.Ok)
                return entered;
            try
            {
                if (_disposed)
                    return PanelDeviceStatus.Io;
                var status = call(left, deadline);
                if (_disposed)
                    return PanelDeviceStatus.Io;
                cancellationToken.ThrowIfCancellationRequested();
                return status;
            }
            finally
            {
                _call.Release();
            }
        }

        public PanelDeviceStatus Write(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (report.Length > MaxOutputReportLength)
                throw new ArgumentException($"An output report is at most {MaxOutputReportLength} bytes.", nameof(report));
            var bytes = report.ToArray();
            return Call(timeoutMs, cancellationToken, (left, _) => stream.Write(bytes, left));
        }

        public PanelDeviceStatus Read(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var into = new byte[buffer.Length];
            var got = 0;
            var status = Call(timeoutMs, cancellationToken, (_, deadline) =>
            {
                // A report is whole or not at all, so a read waits in slices, looking at its token and its handle between.
                while (true)
                {
                    var wait = Math.Min(deadline.RemainingMs, (int)ReadSlice.TotalMilliseconds);
                    if (wait <= 0)
                        return PanelDeviceStatus.Timeout;
                    var sliced = stream.Read(into, wait, out got);
                    if (sliced != PanelDeviceStatus.Timeout || got > 0 || _disposed)
                        return sliced;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });
            into.AsSpan(0, got).CopyTo(buffer);
            read = got;
            return status;
        }

        public PanelDeviceStatus SetFeature(ReadOnlySpan<byte> report, int timeoutMs, CancellationToken cancellationToken = default)
        {
            var bytes = report.ToArray();
            return Call(timeoutMs, cancellationToken, (left, _) => stream.SetFeature(bytes, left));
        }

        public PanelDeviceStatus GetFeature(Span<byte> buffer, int timeoutMs, out int read, CancellationToken cancellationToken = default)
        {
            var into = buffer.ToArray();
            var got = 0;
            var status = Call(timeoutMs, cancellationToken, (left, _) => stream.GetFeature(into, left, out got));
            into.AsSpan(0, got).CopyTo(buffer);
            read = got;
            return status;
        }

        public void Dispose()
        {
            _disposed = true;
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            _disposing.Cancel(); // the calls waiting for their turn answer Io at once
            stream.Dispose(); // ends the call under way, which answers Io
            if (_call.Wait(DisposeWait))
                _call.Release();
            hold.Dispose();
        }
    }
}
