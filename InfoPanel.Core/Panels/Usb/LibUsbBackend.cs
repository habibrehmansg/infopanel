using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace InfoPanel.Panels.Usb;

/// <summary>
/// <see cref="IUsbBackend"/> over libusb-1.0: one libusb context for the engine's run. It calls no reset and no
/// kernel-driver detach (libusb's automatic detach stays off, its default), so another program's hold on a device is
/// never broken.
/// </summary>
public sealed unsafe class LibUsbBackend : IUsbBackend
{
    private readonly LibUsb _lib;
    private readonly Lock _lock = new();
    private void* _context;
    private int _open;
    private bool _disposed;

    private LibUsbBackend(LibUsb lib, void* context)
    {
        _lib = lib;
        _context = context;
    }

    /// <summary>A backend over <paramref name="lib"/>; null, with <paramref name="why"/>, when libusb won't start.</summary>
    public static LibUsbBackend? Create(LibUsb lib, out string why)
    {
        void* context = null;
        var code = lib.libusb_init(&context);
        if (code < 0)
        {
            why = $"libusb_init failed ({code})";
            return null;
        }
        why = "";
        return new LibUsbBackend(lib, context);
    }

    public IReadOnlyList<UsbDeviceId> ListDevices()
    {
        var result = new List<UsbDeviceId>();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            void** list = null;
            var count = _lib.libusb_get_device_list(_context, &list);
            if (count < 0 || list is null)
                return result;
            try
            {
                for (nint i = 0; i < count; i++)
                {
                    var dev = list[i];
                    LibUsbDeviceDescriptor descriptor;
                    if (_lib.libusb_get_device_descriptor(dev, &descriptor) < 0)
                        continue;
                    result.Add(new UsbDeviceId(descriptor.idVendor, descriptor.idProduct, _lib.libusb_get_bus_number(dev),
                        _lib.libusb_get_device_address(dev), PortPath(dev)));
                }
            }
            finally
            {
                _lib.libusb_free_device_list(list, 1);
            }
        }
        return result;
    }

    private string PortPath(void* dev)
    {
        var ports = stackalloc byte[8]; // USB 3.0 allows seven tiers below the root
        var bus = _lib.libusb_get_bus_number(dev);
        var n = _lib.libusb_get_port_numbers(dev, ports, 8);
        var path = new StringBuilder().Append(bus);
        for (var i = 0; i < n; i++)
            path.Append(i == 0 ? '-' : '.').Append(ports[i]);
        return path.ToString();
    }

    public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
    {
        opened = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            void** list = null;
            var count = _lib.libusb_get_device_list(_context, &list);
            if (count < 0 || list is null)
                return LibUsb.Status((int)count);
            try
            {
                for (nint i = 0; i < count; i++)
                {
                    var dev = list[i];
                    if (_lib.libusb_get_bus_number(dev) != device.Bus || _lib.libusb_get_device_address(dev) != device.Address)
                        continue;
                    // Another device at the listed bus and address (the listed one went) is never opened.
                    LibUsbDeviceDescriptor descriptor;
                    if (_lib.libusb_get_device_descriptor(dev, &descriptor) < 0 || descriptor.idVendor != device.VendorId
                        || descriptor.idProduct != device.ProductId || PortPath(dev) != device.PortPath)
                        return UsbStatus.NoDevice;
                    void* handle = null;
                    var code = _lib.libusb_open(dev, &handle);
                    if (code < 0)
                        return LibUsb.Status(code);
                    _open++;
                    opened = new Device(this, handle);
                    return UsbStatus.Ok;
                }
                return UsbStatus.NoDevice;
            }
            finally
            {
                // An opened handle keeps its own reference to the device.
                _lib.libusb_free_device_list(list, 1);
            }
        }
    }

    private void Closed()
    {
        lock (_lock)
        {
            _open--;
            if (_disposed && _open == 0)
                Exit();
        }
    }

    /// <summary>Ends the libusb context once every opened device is closed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_open == 0)
                Exit();
        }
    }

    private void Exit()
    {
        if (_context is null)
            return;
        _lib.libusb_exit(_context);
        _context = null;
    }

    private sealed class Device(LibUsbBackend owner, void* handle) : IUsbDevice
    {
        private readonly List<int> _claimed = [];
        private void* _handle = handle;

        private LibUsb Lib => owner._lib;

        public UsbStatus GetConfiguration(out int configuration)
        {
            int value = 0;
            var code = Lib.libusb_get_configuration(Handle(), &value);
            configuration = value;
            return LibUsb.Status(code);
        }

        public UsbStatus SetConfiguration(int configuration) => LibUsb.Status(Lib.libusb_set_configuration(Handle(), configuration));

        public UsbStatus ClaimInterface(int interfaceNumber)
        {
            var status = LibUsb.Status(Lib.libusb_claim_interface(Handle(), interfaceNumber));
            if (status == UsbStatus.Ok)
                _claimed.Add(interfaceNumber);
            return status;
        }

        public UsbStatus ReleaseInterface(int interfaceNumber)
        {
            _claimed.Remove(interfaceNumber);
            return LibUsb.Status(Lib.libusb_release_interface(Handle(), interfaceNumber));
        }

        public string? ReadSerialNumber()
        {
            // The descriptor's string index comes from the cached device descriptor; reading the string is one control
            // transfer, which a device another program holds still answers.
            var buffer = stackalloc byte[256];
            var dev = Lib.libusb_get_device(Handle());
            if (dev is null)
                return null;
            LibUsbDeviceDescriptor descriptor;
            if (Lib.libusb_get_device_descriptor(dev, &descriptor) < 0 || descriptor.iSerialNumber == 0)
                return null;
            var n = Lib.libusb_get_string_descriptor_ascii(Handle(), descriptor.iSerialNumber, buffer, 256);
            return n <= 0 ? null : Encoding.ASCII.GetString(buffer, n);
        }

        public UsbStatus BulkWrite(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
        {
            if (data.IsEmpty)
                throw new ArgumentException("A bulk write is never empty (no zero-length packet).", nameof(data));
            int transferred = 0;
            int code;
            fixed (byte* p = data)
                code = Lib.libusb_bulk_transfer(Handle(), endpoint, p, data.Length, &transferred, (uint)timeoutMs);
            var status = LibUsb.Status(code);
            return status == UsbStatus.Ok && transferred != data.Length ? UsbStatus.Io : status;
        }

        public UsbStatus BulkRead(byte endpoint, Span<byte> buffer, int timeoutMs, out int read)
        {
            int transferred = 0;
            int code;
            fixed (byte* p = buffer)
                code = Lib.libusb_bulk_transfer(Handle(), endpoint, p, buffer.Length, &transferred, (uint)timeoutMs);
            read = transferred;
            return LibUsb.Status(code);
        }

        private void* Handle()
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }

        public void Dispose()
        {
            if (_handle is null)
                return;
            foreach (var i in _claimed)
                Lib.libusb_release_interface(_handle, i);
            _claimed.Clear();
            Lib.libusb_close(_handle);
            _handle = null;
            owner.Closed();
        }
    }
}
