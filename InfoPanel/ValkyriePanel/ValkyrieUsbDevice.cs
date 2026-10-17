using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.IO;

namespace InfoPanel.ValkyriePanel
{
    internal sealed class ValkyrieUsbDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ValkyrieUsbDevice>();
        private UsbDevice? _usb;
        private IUsbDevice? _wholeDevice;
        private UsbEndpointWriter? _writer;
        private ValkyrieHidDevice? _hid;
        private readonly byte[] _frame = new byte[ValkyrieFrameProtocol.FrameLength];
        private long _framesSent;

        private ValkyrieUsbDevice() { }

        public static ValkyrieUsbDevice Open(string deviceId, string serial)
        {
            if (string.IsNullOrWhiteSpace(serial) || !string.Equals(deviceId, ValkyriePanelHelper.DeviceId(serial), StringComparison.Ordinal))
                throw new IOException("V360 requires a matching USB serial number. Scan for the device again.");
            var hid = ValkyriePanelHelper.FindHid(serial)
                ?? throw new IOException("V360 HID interface is missing or its serial number is ambiguous.");

            foreach (UsbRegistry registry in UsbDevice.AllDevices)
            {
                if (!ValkyriePanelHelper.IsDisplayInterface(registry)) continue;
                var candidate = new ValkyrieUsbDevice();
                try
                {
                    candidate._usb = registry.Device;
                    if (candidate._usb == null || !string.Equals(candidate._usb.Info.SerialString, serial, StringComparison.Ordinal))
                    {
                        candidate.Dispose();
                        continue;
                    }
                    bool endpointFound = false;
                    foreach (var config in candidate._usb.Configs)
                        foreach (var iface in config.InterfaceInfoList)
                            foreach (var endpoint in iface.EndpointInfoList)
                                if (iface.Descriptor.InterfaceID == 3 && (byte)endpoint.Descriptor.EndpointID == 0x04)
                                    endpointFound = true;
                    if (!endpointFound) throw new IOException("V360 display endpoint 0x04 on interface 3 was not found.");
                    if (candidate._usb is IUsbDevice whole)
                    {
                        if (!whole.ClaimInterface(3)) throw new IOException("V360 display interface 3 is busy.");
                        candidate._wholeDevice = whole;
                    }
                    candidate._writer = candidate._usb.OpenEndpointWriter(WriteEndpointID.Ep04);
                    candidate._hid = new ValkyrieHidDevice(hid);
                    if (!candidate._hid.IsV360()) throw new IOException("The selected MS9132 device is not a 320x240 V360 panel.");
                    ValkyrieV360Protocol.Initialize(candidate.Command);
                    return candidate;
                }
                catch (Exception ex)
                {
                    candidate.Dispose();
                    Logger.Debug(ex, "Valkyrie: unable to open display interface {Path}", registry.DevicePath);
                }
            }
            throw new IOException("V360 display interface is unavailable. Close Myth.Cool and use a WinUSB-compatible driver on interface 3; keep interface 0 on its HID driver.");
        }

        private void Command(byte[] payload, bool readReply) => _hid!.Command(payload, readReply);

        public void SendFrame(byte[] pixels)
        {
            if (_writer == null) throw new ObjectDisposedException(nameof(ValkyrieUsbDevice));
            ValkyrieFrameProtocol.PackFrame(pixels, _frame);
            for (int offset = 0; offset < _frame.Length;)
            {
                int length = Math.Min(65536, _frame.Length - offset);
                var error = _writer.Write(_frame, offset, length, 1000, out int written);
                if (error != ErrorCode.None || written != length)
                    throw new IOException($"V360 frame write failed: {error}, {written}/{length} bytes at {offset}.");
                offset += written;
            }
            if (_framesSent == 0) ValkyrieV360Protocol.PostFrame(Command);
            else if (_framesSent % ValkyrieV360Protocol.CommitEveryFrames == 0) Command([0xB5, 0x00, 0x32], true);
            _framesSent++;
        }

        public void Dispose()
        {
            try { _writer?.Dispose(); }
            finally
            {
                _writer = null;
                try { _hid?.Dispose(); }
                finally
                {
                    _hid = null;
                    try { _wholeDevice?.ReleaseInterface(3); }
                    finally
                    {
                        _wholeDevice = null;
                        _usb?.Close();
                        _usb = null;
                    }
                }
            }
        }
    }
}
