using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Linq;
using System.Threading;

namespace InfoPanel.VmaxPanel
{
    public sealed class VmaxUsbDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<VmaxUsbDevice>();

        private static readonly byte[] FramePrefix = [0xFF, 0x00, 0x00, 0x00, 0x00, 0x14, 0x03, 0xC0];
        private static readonly byte[] FrameSuffix = [0xFF, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static readonly byte[] StreamingStatusFeatureReport = [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static readonly byte[] StreamingPulseFeatureReport = [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static readonly byte[] DisplayEnableFeatureReport = [0xA6, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static readonly byte[][] StreamingStartupFeatureReports =
        [
            [0xB5, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF2, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xE0, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xE8, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xF0, 0x00, 0x00, 0x00, 0x00],
            [0xF5, 0x00, 0x1F, 0xF8, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x07, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC4, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xB0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xB0, 0xDF, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xA0, 0xE4, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xC6, 0xA0, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x38, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x5C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x68, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x6C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x74, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x78, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC0, 0x7C, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x01, 0x01, 0x40, 0x03, 0xC0, 0x11, 0x00],
            [0xB5, 0xC5, 0x56, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x02, 0xB2, 0x00, 0x01, 0x40, 0x03, 0xC0],
            [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00],
        ];

        private const int InterfaceNumber = 3;
        private const int UsbWriteTimeoutMs = 1000;
        private const int UsbWriteChunkSize = 65536;
        private const int StreamingInitDelayMs = 25;

        private UsbDevice? _usbDevice;
        private UsbEndpointWriter? _writer;
        private IUsbDevice? _wholeUsbDevice;
        private HidStream? _hidStream;
        private DateTime _lastStreamingStatusPolled = DateTime.MinValue;
        private byte[]? _frameBuffer;
        private bool _hasSentFrame;
        private bool? _currentScreenSwitch;
        private bool _disposed;

        private VmaxUsbDevice() { }

        public static VmaxUsbDevice? Open(string deviceId)
        {
            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid != VmaxPanelModelDatabase.VMAX_VENDOR_ID
                    || deviceReg.Pid != VmaxPanelModelDatabase.VMAX_PRODUCT_ID_46INCH)
                    continue;

                deviceReg.DeviceProperties.TryGetValue("DeviceID", out var registryDeviceIdValue);
                var registryDeviceId = registryDeviceIdValue as string;
                if (!string.IsNullOrEmpty(deviceId)
                    && !IsMatchingDevice(deviceId, registryDeviceId, deviceReg.DevicePath))
                    continue;

                var usbDevice = deviceReg.Device;
                if (usbDevice == null)
                {
                    Logger.Warning("VmaxUsbDevice: Could not open {DeviceId}", registryDeviceId ?? deviceReg.DevicePath);
                    continue;
                }

                var device = new VmaxUsbDevice { _usbDevice = usbDevice };

                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    device._wholeUsbDevice = wholeUsbDevice;
                    var claimed = wholeUsbDevice.ClaimInterface(InterfaceNumber);
                    Logger.Information(
                        "VmaxUsbDevice: ClaimInterface({Interface})={Claimed}",
                        InterfaceNumber,
                        claimed);
                }

                device._hidStream = InitializeStreamingMode();

                var writeEndpoint = FindWriteEndpoint(usbDevice);
                device._writer = usbDevice.OpenEndpointWriter(writeEndpoint);
                Logger.Information("VmaxUsbDevice: Opened {DeviceId} using endpoint 0x{Endpoint:X2}", registryDeviceId ?? deviceReg.DevicePath, (byte)writeEndpoint);
                return device;
            }

            Logger.Warning(
                "VmaxUsbDevice: No VMAX device found for {DeviceId}. The device may need a WinUSB/libusb-compatible driver on interface {InterfaceNumber}.",
                deviceId,
                InterfaceNumber);
            return null;
        }

        private static bool IsMatchingDevice(string requestedDeviceId, string? registryDeviceId, string? devicePath)
        {
            if (string.Equals(registryDeviceId, requestedDeviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(devicePath, requestedDeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

            return requestedDeviceId.StartsWith(
                $@"USB\VID_{VmaxPanelModelDatabase.VMAX_VENDOR_ID:X4}&PID_{VmaxPanelModelDatabase.VMAX_PRODUCT_ID_46INCH:X4}",
                StringComparison.OrdinalIgnoreCase);
        }

        private static WriteEndpointID FindWriteEndpoint(UsbDevice usbDevice)
        {
            WriteEndpointID endpoint = WriteEndpointID.Ep04;

            foreach (var config in usbDevice.Configs)
            {
                foreach (var iface in config.InterfaceInfoList)
                {
                    foreach (var ep in iface.EndpointInfoList)
                    {
                        var endpointId = (byte)ep.Descriptor.EndpointID;
                        if ((endpointId & 0x80) != 0)
                            continue;

                        Logger.Information(
                            "VmaxUsbDevice: Found OUT endpoint 0x{Endpoint:X2} on interface {Interface}",
                            endpointId,
                            iface.Descriptor.InterfaceID);

                        if (endpointId == 0x04)
                            return (WriteEndpointID)endpointId;

                        endpoint = (WriteEndpointID)endpointId;
                    }
                }
            }

            return endpoint;
        }

        private static HidStream InitializeStreamingMode()
        {
            var hidDevices = DeviceList.Local
                .GetHidDevices(VmaxPanelModelDatabase.VMAX_VENDOR_ID, VmaxPanelModelDatabase.VMAX_PRODUCT_ID_46INCH)
                .ToList();

            Logger.Information("VmaxUsbDevice: Found {Count} HID interface(s) for streaming init", hidDevices.Count);

            foreach (var hidDevice in hidDevices)
            {
                try
                {
                    Logger.Information(
                        "VmaxUsbDevice: Trying HID init via {Path}, FeatureLen={FeatureLength}, InputLen={InputLength}, OutputLen={OutputLength}",
                        hidDevice.DevicePath,
                        hidDevice.GetMaxFeatureReportLength(),
                        hidDevice.GetMaxInputReportLength(),
                        hidDevice.GetMaxOutputReportLength());

                    var stream = hidDevice.Open();
                    stream.WriteTimeout = UsbWriteTimeoutMs;
                    stream.ReadTimeout = UsbWriteTimeoutMs;

                    try
                    {
                        byte[] response = [];
                        foreach (var featureReport in StreamingStartupFeatureReports)
                        {
                            response = SendFeatureReport(stream, featureReport);
                            ApplyStartupDelay(featureReport);
                        }

                        for (var i = 0; i < 42; i++)
                        {
                            response = SendFeatureReport(stream, StreamingPulseFeatureReport);
                            Thread.Sleep(2);
                        }

                        response = SendFeatureReport(stream, DisplayEnableFeatureReport);
                        Thread.Sleep(StreamingInitDelayMs);
                        response = SendFeatureReport(stream, StreamingStatusFeatureReport);

                        Logger.Information(
                            "VmaxUsbDevice: Streaming startup init completed with {CommandCount} feature reports, last response {Response}",
                            StreamingStartupFeatureReports.Length + 44,
                            BitConverter.ToString(response));
                        Thread.Sleep(StreamingInitDelayMs);
                        return stream;
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "VmaxUsbDevice: HID streaming init failed for {Path}", hidDevice.DevicePath);
                }
            }

            throw new InvalidOperationException("VMAX streaming init failed: no HID interface accepted the feature report.");
        }

        private static byte[] SendFeatureReport(HidStream stream, byte[] payload)
        {
            var report = new byte[payload.Length + 1];
            report[0] = 0x00;
            Buffer.BlockCopy(payload, 0, report, 1, payload.Length);

            stream.SetFeature(report);

            var response = new byte[report.Length];
            response[0] = 0x00;
            stream.GetFeature(response);
            return response;
        }

        private static void ApplyStartupDelay(byte[] featureReport)
        {
            if (IsFeatureReport(featureReport, 0xA6, 0x07, 0x01))
            {
                Thread.Sleep(105);
                return;
            }

            if (IsFeatureReport(featureReport, 0xA6, 0x05, 0x00))
            {
                Thread.Sleep(15);
                return;
            }

            if (IsFeatureReport(featureReport, 0xB5, 0x00, 0x32))
            {
                Thread.Sleep(108);
                return;
            }

            Thread.Sleep(1);
        }

        private static bool IsFeatureReport(byte[] featureReport, byte first, byte second, byte third)
        {
            return featureReport.Length >= 3
                && featureReport[0] == first
                && featureReport[1] == second
                && featureReport[2] == third;
        }

        public void SendRgb888Frame(byte[] rgbData)
        {
            SendRgb888Frame(rgbData, rgbData.Length);
        }

        public void SendRgb888Frame(byte[] rgbData, int rgbDataLength)
        {
            if (_writer == null) throw new InvalidOperationException("VMAX USB device is not open.");
            if (rgbDataLength < 0 || rgbDataLength > rgbData.Length)
                throw new ArgumentOutOfRangeException(nameof(rgbDataLength));

            var frameLength = FramePrefix.Length + rgbDataLength + FrameSuffix.Length;
            if (_frameBuffer == null || _frameBuffer.Length != frameLength)
            {
                _frameBuffer = new byte[frameLength];
                Buffer.BlockCopy(FramePrefix, 0, _frameBuffer, 0, FramePrefix.Length);
                Buffer.BlockCopy(FrameSuffix, 0, _frameBuffer, FramePrefix.Length + rgbDataLength, FrameSuffix.Length);
            }

            Buffer.BlockCopy(rgbData, 0, _frameBuffer, FramePrefix.Length, rgbDataLength);

            var offset = 0;
            while (offset < _frameBuffer.Length)
            {
                var writeSize = Math.Min(UsbWriteChunkSize, _frameBuffer.Length - offset);
                var result = _writer.Write(_frameBuffer, offset, writeSize, UsbWriteTimeoutMs, out var bytesWritten);
                if (result != ErrorCode.None || bytesWritten != writeSize)
                {
                    throw new InvalidOperationException($"VMAX USB write failed: {result}, wrote {bytesWritten}/{writeSize} bytes at offset {offset}/{_frameBuffer.Length}.");
                }

                offset += bytesWritten;
            }

            _hasSentFrame = true;
            PollStreamingStatusIfDue();
        }

        public void SetScreenSwitch(bool enabled)
        {
            if (_hidStream == null || !_hasSentFrame || _currentScreenSwitch == enabled)
                return;

            try
            {
                byte[] response = [];
                foreach (var featureReport in GetScreenSwitchFeatureReports(enabled))
                {
                    response = SendFeatureReport(_hidStream, featureReport);
                    ApplyStartupDelay(featureReport);
                }

                _currentScreenSwitch = enabled;
                Logger.Information(
                    "VmaxUsbDevice: ScreenSwitch set to {Enabled}, last response {Response}",
                    enabled,
                    BitConverter.ToString(response));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "VmaxUsbDevice: ScreenSwitch command failed; continuing frame transfer.");
            }
        }

        private static byte[][] GetScreenSwitchFeatureReports(bool enabled) =>
        [
            [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB6, 0xF0, 0x05, enabled ? (byte)0x10 : (byte)0x00, 0x00, 0x00, 0x00, 0x00],
            [0xA6, 0x05, enabled ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
        ];

        private void PollStreamingStatusIfDue()
        {
            if (_hidStream == null)
                return;

            var now = DateTime.UtcNow;
            if ((now - _lastStreamingStatusPolled).TotalMilliseconds < 500)
                return;

            _lastStreamingStatusPolled = now;

            try
            {
                var response = SendFeatureReport(_hidStream, StreamingStatusFeatureReport);
                Logger.Debug("VmaxUsbDevice: Streaming status response {Response}", BitConverter.ToString(response));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "VmaxUsbDevice: Streaming status poll failed; continuing frame transfer.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _writer?.Dispose();
            _writer = null;

            _hidStream?.Dispose();
            _hidStream = null;

            try
            {
                _wholeUsbDevice?.ReleaseInterface(InterfaceNumber);
            }
            catch { }

            _usbDevice?.Close();
            _usbDevice = null;
        }
    }
}
