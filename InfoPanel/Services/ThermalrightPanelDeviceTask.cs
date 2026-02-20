using InfoPanel.Extensions;
using System.Linq;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ThermalrightPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ThermalrightPanelDeviceTask>();

        // ChiZhu Tech USBDISPLAY Protocol constants
        // Based on USB capture analysis of TRCC software at boot
        private static readonly byte[] MAGIC_BYTES = { 0x12, 0x34, 0x56, 0x78 };
        private const int HEADER_SIZE = 64;
        private const int COMMAND_DISPLAY = 0x02;
        private const int JPEG_QUALITY = 85;

        // Trofeo protocol constants (DA DB DC DD magic, 512-byte packets)
        private static readonly byte[] TROFEO_MAGIC_BYTES = { 0xDA, 0xDB, 0xDC, 0xDD };
        private const int TROFEO_PACKET_SIZE = 512;
        private const int TROFEO_HEADER_JPEG_OFFSET = 20;

        // Default resolution (updated after device identification)
        private const int DEFAULT_WIDTH = 480;
        private const int DEFAULT_HEIGHT = 480;

        // Delay before returning after a device open failure (prevents rapid retry storm)
        private const int OPEN_FAILURE_BACKOFF_MS = 10000;

        private readonly ThermalrightPanelDevice _device;
        private int _panelWidth = DEFAULT_WIDTH;
        private int _panelHeight = DEFAULT_HEIGHT;
        private ThermalrightPanelModelInfo? _detectedModel;

        public ThermalrightPanelDevice Device => _device;

        public ThermalrightPanelDeviceTask(ThermalrightPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Initialize from device's ModelInfo if available (handles unique VID/PID devices like Trofeo Vision)
            if (_device.ModelInfo != null)
            {
                _panelWidth = _device.ModelInfo.RenderWidth;
                _panelHeight = _device.ModelInfo.RenderHeight;
                _detectedModel = _device.ModelInfo;
            }
        }

        /// <summary>
        /// Builds the 64-byte init command for the ChiZhu Tech USB Display protocol.
        /// Based on actual USB capture: magic + zeros + 0x01 at offset 56
        /// </summary>
        private byte[] BuildInitCommand()
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-55: All zeros (already zeroed)

            // Bytes 56-59: Init flag = 0x01 (critical!)
            BitConverter.GetBytes(1).CopyTo(header, 56);

            // Bytes 60-63: Zero (already zeroed)

            return header;
        }

        /// <summary>
        /// Builds a 64-byte display header for the ChiZhu Tech USB Display protocol.
        /// Uses cmd=0x02 for JPEG, cmd=0x03 for RGB565 (PM=32 panels).
        /// </summary>
        private byte[] BuildDisplayHeader(int dataSize)
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // cmd=0x03 for RGB565 panels, 0x02 for JPEG
            int cmd = _detectedModel?.PixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian
                ? 0x03 : COMMAND_DISPLAY;

            // Bytes 4-7: Command
            BitConverter.GetBytes(cmd).CopyTo(header, 4);

            // Bytes 8-11: Width
            BitConverter.GetBytes(_panelWidth).CopyTo(header, 8);

            // Bytes 12-15: Height
            BitConverter.GetBytes(_panelHeight).CopyTo(header, 12);

            // Bytes 16-55: Zero padding (already zeroed)

            // Bytes 56-59: Command repeated
            BitConverter.GetBytes(cmd).CopyTo(header, 56);

            // Bytes 60-63: Data size (little-endian)
            BitConverter.GetBytes(dataSize).CopyTo(header, 60);

            return header;
        }

        public byte[] GenerateFrameBuffer()
        {
            var pixelFormat = _detectedModel?.PixelFormat ?? ThermalrightPixelFormat.Jpeg;
            return pixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian
                ? GenerateRgb565Buffer()
                : GenerateJpegBuffer();
        }

        private byte[] GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                SKBitmap encodeBitmap = resizedBitmap;
                SKBitmap? dimmed = null;
                try
                {
                    if (_device.Brightness < 100)
                    {
                        dimmed = ApplyBrightness(resizedBitmap);
                        encodeBitmap = dimmed;
                    }
                    using var image = SKImage.FromBitmap(encodeBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);
                    return data.ToArray();
                }
                finally
                {
                    dimmed?.Dispose();
                }
            }

            // No profile — black JPEG as keepalive
            return _blackFrame ??= GenerateBlackJpeg();
        }

        private byte[] GenerateRgb565Buffer()
        {
            var profileGuid = _device.ProfileGuid;
            bool bigEndian = _detectedModel?.PixelFormat == ThermalrightPixelFormat.Rgb565BigEndian;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                SKBitmap convertBitmap = resizedBitmap;
                SKBitmap? dimmed = null;
                try
                {
                    if (_device.Brightness < 100)
                    {
                        dimmed = ApplyBrightness(resizedBitmap);
                        convertBitmap = dimmed;
                    }
                    using var rgb565Bitmap = convertBitmap.Copy(SKColorType.Rgb565);
                    var bytes = rgb565Bitmap.Bytes;
                    if (bigEndian) SwapRgb565Endianness(bytes);
                    return bytes;
                }
                finally
                {
                    dimmed?.Dispose();
                }
            }

            // No profile — black RGB565 as keepalive (0x0000 is black in both endiannesses)
            return _blackFrame ??= new byte[_panelWidth * _panelHeight * 2];
        }

        /// <summary>
        /// Software brightness: dims the image by scaling RGB channels via color matrix.
        /// Always returns a new bitmap (caller must dispose).
        /// </summary>
        private SKBitmap ApplyBrightness(SKBitmap source)
        {
            float scale = Math.Clamp(_device.Brightness, 0, 100) / 100f;

            var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint();
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                scale, 0,     0,     0, 0,
                0,     scale, 0,     0, 0,
                0,     0,     scale, 0, 0,
                0,     0,     0,     1, 0
            ]);
            canvas.DrawBitmap(source, 0, 0, paint);
            return result;
        }

        /// <summary>
        /// Byte-swaps each 16-bit pixel in-place for big-endian RGB565.
        /// Required for 320x320 panels.
        /// </summary>
        private static void SwapRgb565Endianness(byte[] data)
        {
            for (int i = 0; i < data.Length - 1; i += 2)
                (data[i], data[i + 1]) = (data[i + 1], data[i]);
        }

        private byte[]? _blackFrame;

        private byte[] GenerateBlackJpeg()
        {
            using var bitmap = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
            bitmap.Erase(SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);
            return data.ToArray();
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var transportType = _device.ModelInfo?.TransportType ?? ThermalrightTransportType.WinUsb;
            var protocolType = _device.ModelInfo?.ProtocolType ?? ThermalrightProtocolType.ChiZhu;
            Logger.Information("ThermalrightPanelDevice {Device}: Using {Transport} transport, {Protocol} protocol", _device, transportType, protocolType);

            if (transportType == ThermalrightTransportType.Scsi)
                await DoWorkScsiAsync(token);
            else if (transportType == ThermalrightTransportType.Hid)
                await DoWorkHidAsync(token);
            else
                await DoWorkWinUsbAsync(token);
        }

        /// <summary>
        /// Finds the matching UsbRegistry for this device by scanning all connected USB devices.
        /// When matchDeviceId is false, returns the first device with matching VID/PID
        /// (used for bulk interface discovery on composite devices).
        /// </summary>
        private UsbRegistry? FindUsbRegistry(int vendorId, int productId, bool matchDeviceId = true)
        {
            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    if (!matchDeviceId)
                        return deviceReg;

                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                    // Match by DeviceId if we have one, otherwise take first match
                    if (string.IsNullOrEmpty(_device.DeviceId) ||
                        (deviceId != null && deviceId.Equals(_device.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return deviceReg;
                    }
                }
            }
            return null;
        }

        private async Task DoWorkScsiAsync(CancellationToken token)
        {
            try
            {
                // Use DevicePath (e.g. \\.\PhysicalDrive1) stored during discovery
                var devicePath = _device.DeviceLocation;
                Logger.Information("ThermalrightPanelDevice {Device}: Opening SCSI device at {Path}...", _device, devicePath);

                using var scsiDevice = ScsiPanelDevice.Open(devicePath);
                if (scsiDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open SCSI device", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open SCSI device. Make sure:\n" +
                        "1. The device is connected\n" +
                        "2. No other application is using the device\n" +
                        "3. Try running as Administrator");
                    await Task.Delay(OPEN_FAILURE_BACKOFF_MS, token);
                    return;
                }

                Logger.Information("ThermalrightPanelDevice {Device}: SCSI device opened, polling...", _device);

                // Poll device to detect resolution and boot status
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var pollResponse = scsiDevice.Poll();
                    if (pollResponse == null)
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: SCSI poll failed (attempt {Attempt}/5)",
                            _device, attempt + 1);
                        await Task.Delay(1000, token);
                        continue;
                    }

                    // Check if device is still booting
                    if (ScsiPanelDevice.IsDeviceBooting(pollResponse))
                    {
                        Logger.Information("ThermalrightPanelDevice {Device}: Device still booting, waiting 3s...", _device);
                        await Task.Delay(3000, token);
                        continue;
                    }

                    // Resolve resolution from poll byte[0]
                    var pollByte = pollResponse[0];
                    Logger.Information("ThermalrightPanelDevice {Device}: SCSI poll byte: 0x{PollByte:X2} ('{Char}')",
                        _device, pollByte, (char)pollByte);

                    var resolution = ThermalrightPanelModelDatabase.GetResolutionFromScsiPollByte(pollByte);
                    if (resolution != null)
                    {
                        _panelWidth = resolution.Value.Width;
                        _panelHeight = resolution.Value.Height;
                        Logger.Information("ThermalrightPanelDevice {Device}: Detected resolution {Width}x{Height}",
                            _device, _panelWidth, _panelHeight);
                    }
                    else
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: Unknown poll byte 0x{PollByte:X2}, using default {Width}x{Height}",
                            _device, pollByte, _panelWidth, _panelHeight);
                    }
                    break;
                }

                // Initialize display controller
                Logger.Information("ThermalrightPanelDevice {Device}: Sending SCSI init...", _device);
                if (!scsiDevice.Init())
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: SCSI init failed", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "SCSI display init failed");
                    return;
                }

                Logger.Information("ThermalrightPanelDevice {Device}: SCSI init complete, starting render loop ({Width}x{Height})...",
                    _device, _panelWidth, _panelHeight);

                // Run the shared render-send loop with SCSI frame sender
                await RunRenderSendLoop(frameData =>
                {
                    if (!scsiDevice.SendFrame(frameData))
                        throw new Exception("SCSI frame send failed");
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: SCSI error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
        }

        private async Task DoWorkWinUsbAsync(CancellationToken token)
        {
            try
            {
                var vendorId = _device.ModelInfo?.VendorId ?? ThermalrightPanelModelDatabase.THERMALRIGHT_VENDOR_ID;
                var productId = _device.ModelInfo?.ProductId ?? ThermalrightPanelModelDatabase.THERMALRIGHT_PRODUCT_ID;
                Logger.Information("ThermalrightPanelDevice {Device}: Opening device via LibUsbDotNet (VID={Vid:X4} PID={Pid:X4})...",
                    _device, vendorId, productId);

                // Find the matching UsbRegistry
                var usbRegistry = FindUsbRegistry(vendorId, productId);
                if (usbRegistry == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: USB device not found", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "USB device not found. Make sure:\n" +
                        "1. The device is connected\n" +
                        "2. No other application is using the device");
                    await Task.Delay(OPEN_FAILURE_BACKOFF_MS, token);
                    return;
                }

                using var usbDevice = usbRegistry.Device;
                if (usbDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open USB device. Make sure:\n" +
                        "1. No other application is using the device\n" +
                        "2. Try running as Administrator");
                    await Task.Delay(OPEN_FAILURE_BACKOFF_MS, token);
                    return;
                }

                // Claim the interface (required for WinUSB devices)
                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }
                else
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Device is {Type}, SetConfiguration/ClaimInterface skipped",
                        _device, usbDevice.GetType().Name);
                }

                Logger.Information("ThermalrightPanelDevice {Device}: Device opened successfully!", _device);

                // Enumerate endpoints to discover correct addresses
                WriteEndpointID writeEp = WriteEndpointID.Ep01;
                ReadEndpointID readEp = ReadEndpointID.Ep01;
                bool foundWrite = false, foundRead = false;

                foreach (var config in usbDevice.Configs)
                {
                    foreach (var iface in config.InterfaceInfoList)
                    {
                        Logger.Information("ThermalrightPanelDevice {Device}: Interface {Iface}, endpoints: {Count}",
                            _device, iface.Descriptor.InterfaceID, iface.EndpointInfoList.Count);

                        foreach (var ep in iface.EndpointInfoList)
                        {
                            var addr = (byte)ep.Descriptor.EndpointID;
                            var isOut = (addr & 0x80) == 0;
                            Logger.Information("ThermalrightPanelDevice {Device}:   EP 0x{Addr:X2} ({Dir}, {Type})",
                                _device, addr, isOut ? "OUT" : "IN", ep.Descriptor.Attributes & 0x03);

                            if (isOut && !foundWrite)
                            {
                                writeEp = (WriteEndpointID)addr;
                                foundWrite = true;
                            }
                            else if (!isOut && !foundRead)
                            {
                                readEp = (ReadEndpointID)addr;
                                foundRead = true;
                            }
                        }
                    }
                }

                Logger.Information("ThermalrightPanelDevice {Device}: Using write EP 0x{WEp:X2}, read EP 0x{REp:X2}",
                    _device, (byte)writeEp, (byte)readEp);

                using var writer = usbDevice.OpenEndpointWriter(writeEp);
                using var reader = usbDevice.OpenEndpointReader(readEp);

                var protocolType = _device.ModelInfo?.ProtocolType ?? ThermalrightProtocolType.ChiZhu;

                if (protocolType == ThermalrightProtocolType.TrofeoBulk)
                {
                    await DoWinUsbTrofeoBulkProtocol(writer, reader, token);
                }
                else if (protocolType == ThermalrightProtocolType.Trofeo)
                {
                    await DoWinUsbTrofeoProtocol(writer, reader, token);
                }
                else
                {
                    await DoWinUsbChiZhuProtocol(writer, reader, token);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("ThermalrightPanelDevice {Device}: Task cancelled", _device);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: Error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }

        /// <summary>
        /// ChiZhu Tech protocol over WinUSB bulk: 12 34 56 78 magic, 64-byte headers, SSCRM identifier.
        /// </summary>
        private async Task DoWinUsbChiZhuProtocol(UsbEndpointWriter writer, UsbEndpointReader reader, CancellationToken token)
        {
            // Send initialization command (magic + zeros + 0x01 at offset 56)
            var initCommand = BuildInitCommand();

            // Boot detection: device responds A1A2A3A4 while still booting — retry up to 5 times
            ErrorCode ec = ErrorCode.None;
            int bytesRead = 0;
            var responseBuffer = new byte[1024]; // ChiZhu init response is up to 1024 bytes (PM at [24], SUB at [36])
            const int MAX_BOOT_RETRIES = 5;

            for (int bootAttempt = 0; bootAttempt < MAX_BOOT_RETRIES; bootAttempt++)
            {
                if (bootAttempt > 0)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Device booting (A1A2A3A4), waiting 3s (attempt {N}/{Max})",
                        _device, bootAttempt + 1, MAX_BOOT_RETRIES);
                    await Task.Delay(3000, token);
                }

                Logger.Information("ThermalrightPanelDevice {Device}: Sending ChiZhu init command (64 bytes)", _device);
                ec = writer.Write(initCommand, 5000, out int initWritten);
                if (ec != ErrorCode.None)
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: Init command failed: {Error}", _device, ec);
                    _device.UpdateRuntimeProperties(errorMessage: $"Init command failed: {ec}");
                    return;
                }
                Logger.Information("ThermalrightPanelDevice {Device}: Init command sent ({Bytes} bytes)", _device, initWritten);

                ec = reader.Read(responseBuffer, 5000, out bytesRead);

                // Check for boot indicator: bytes 4-7 == A1 A2 A3 A4
                if (ec == ErrorCode.None && bytesRead >= 8 &&
                    responseBuffer[4] == 0xA1 && responseBuffer[5] == 0xA2 &&
                    responseBuffer[6] == 0xA3 && responseBuffer[7] == 0xA4)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Device is still booting (A1A2A3A4)", _device);
                    continue;
                }

                break; // Not booting — proceed
            }

            if (ec == ErrorCode.None && bytesRead > 0)
            {
                var responseHex = BitConverter.ToString(responseBuffer, 0, Math.Min(bytesRead, 32)).Replace("-", "");
                Logger.Information("ThermalrightPanelDevice {Device}: Device response ({Bytes} bytes): {Hex}",
                    _device, bytesRead, responseHex);

                // Parse PM byte at offset 24 and SUB at offset 36 (ChiZhu 1024-byte response)
                byte? pm = bytesRead >= 25 ? responseBuffer[24] : null;
                byte? sub = bytesRead >= 37 ? responseBuffer[36] : null;

                if (pm.HasValue)
                    Logger.Information("ThermalrightPanelDevice {Device}: ChiZhu PM byte at [24]: 0x{PM:X2} ({PMDec})", _device, pm.Value, pm.Value);
                if (sub.HasValue)
                    Logger.Information("ThermalrightPanelDevice {Device}: ChiZhu SUB byte at [36]: 0x{SUB:X2} ({SUBDec})", _device, sub.Value, sub.Value);

                if (bytesRead >= 12)
                {
                    var deviceIdentifier = System.Text.Encoding.ASCII.GetString(responseBuffer, 4, 8).TrimEnd('\0');
                    Logger.Information("ThermalrightPanelDevice {Device}: Device identifier: {Id}", _device, deviceIdentifier);

                    _detectedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(deviceIdentifier, sub);
                    if (_detectedModel != null)
                    {
                        _panelWidth = _detectedModel.RenderWidth;
                        _panelHeight = _detectedModel.RenderHeight;
                        _device.Model = _detectedModel.Model;
                        Logger.Information("ThermalrightPanelDevice {Device}: Detected {Model} - using {Width}x{Height}",
                            _device, _detectedModel.Name, _panelWidth, _panelHeight);
                    }
                    else
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: Unknown identifier '{Id}', using default {Width}x{Height}",
                            _device, deviceIdentifier, _panelWidth, _panelHeight);
                    }
                }

                // PM=0x20 on ChiZhu bulk (87AD:70DB) indicates 320x320 RGB565 big-endian variant
                if (pm.HasValue && pm.Value == ThermalrightPanelModelDatabase.CHIZHU_320X320_PM_BYTE &&
                    ThermalrightPanelModelDatabase.Models.TryGetValue(ThermalrightPanelModel.ChiZhuVision320x320, out var chizhuModel))
                {
                    _detectedModel = chizhuModel;
                    _panelWidth = chizhuModel.RenderWidth;
                    _panelHeight = chizhuModel.RenderHeight;
                    _device.Model = chizhuModel.Model;
                    Logger.Information("ThermalrightPanelDevice {Device}: PM 0x{PM:X2} -> {Model} ({Width}x{Height})",
                        _device, pm.Value, chizhuModel.Name, _panelWidth, _panelHeight);
                }
            }
            else
            {
                Logger.Warning("ThermalrightPanelDevice {Device}: No response from device (ec={Error}), using default {Width}x{Height}",
                    _device, ec, _panelWidth, _panelHeight);
            }

            UpdateDeviceDisplayName();
            await Task.Delay(100, token);

            await RunRenderSendLoop(frameData =>
            {
                var header = BuildDisplayHeader(frameData.Length);
                var packet = new byte[HEADER_SIZE + frameData.Length];
                Array.Copy(header, 0, packet, 0, HEADER_SIZE);
                Array.Copy(frameData, 0, packet, HEADER_SIZE, frameData.Length);

                var writeEc = writer.Write(packet, 5000, out int bytesWritten);
                if (writeEc != ErrorCode.None)
                    throw new Exception($"USB write failed: {writeEc}");

                // ZLP: USB bulk requires a zero-length packet when total size is a multiple of max packet size (512)
                if (packet.Length % 512 == 0)
                    writer.Write(Array.Empty<byte>(), 1000, out _);

                // 15ms inter-frame delay required by ChiZhu bulk protocol
                Thread.Sleep(15);
            }, token);
        }

        /// <summary>
        /// Trofeo protocol over WinUSB bulk: DA DB DC DD magic, 512-byte chunked packets (no HID report ID prefix).
        /// Used by Trofeo Vision 9.16" which presents as USB bulk device rather than HID.
        /// </summary>
        private async Task DoWinUsbTrofeoProtocol(UsbEndpointWriter writer, UsbEndpointReader reader, CancellationToken token)
        {
            // Send Trofeo init command (512 bytes: DA DB DC DD magic, 0x01 at byte 12)
            var initPacket = new byte[TROFEO_PACKET_SIZE];
            Array.Copy(TROFEO_MAGIC_BYTES, 0, initPacket, 0, 4);
            initPacket[12] = 0x01;

            Logger.Information("ThermalrightPanelDevice {Device}: Sending Trofeo init command (512 bytes)", _device);
            var ec = writer.Write(initPacket, 1000, out int initWritten);
            bool initSent = ec == ErrorCode.None;
            if (!initSent)
            {
                Logger.Warning("ThermalrightPanelDevice {Device}: Trofeo init command failed: {Error} — continuing without init", _device, ec);
            }
            else
            {
                Logger.Information("ThermalrightPanelDevice {Device}: Trofeo init sent ({Bytes} bytes)", _device, initWritten);
            }

            // Read init response (only if init was sent successfully)
            if (initSent)
            {
                var responseBuffer = new byte[TROFEO_PACKET_SIZE];
                ec = reader.Read(responseBuffer, 5000, out int bytesRead);
                if (ec == ErrorCode.None && bytesRead > 0)
                {
                    Logger.Information("ThermalrightPanelDevice {Device}: Trofeo response ({Bytes} bytes): {Hex}",
                        _device, bytesRead, BitConverter.ToString(responseBuffer, 0, Math.Min(bytesRead, 36)).Replace("-", " "));

                    // Parse PM byte for resolution detection
                    if (bytesRead >= 6)
                    {
                        var pm = responseBuffer[5];
                        Logger.Information("ThermalrightPanelDevice {Device}: Trofeo PM byte: 0x{PM:X2} ({PMDec})", _device, pm, pm);

                        var resolution = ThermalrightPanelModelDatabase.GetResolutionFromPM(pm);
                        if (resolution != null)
                        {
                            _panelWidth = resolution.Value.Width;
                            _panelHeight = resolution.Value.Height;
                            Logger.Information("ThermalrightPanelDevice {Device}: PM {PM} -> {Width}x{Height} ({Size})",
                                _device, pm, _panelWidth, _panelHeight, resolution.Value.SizeName);
                        }
                    }
                }
                else
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: No Trofeo init response (ec={Error}), using default {Width}x{Height}",
                        _device, ec, _panelWidth, _panelHeight);
                }
            }

            UpdateDeviceDisplayName();
            await Task.Delay(100, token);

            // Run render+send loop with Trofeo frame format over bulk USB
            var width = _panelWidth;
            var height = _panelHeight;
            await RunRenderSendLoop(jpegData =>
            {
                SendTrofeoFrameOverBulk(writer, jpegData, width, height);
            }, token);
        }

        /// <summary>
        /// Trofeo Bulk protocol for 9.16" (VID 0416, PID 5408).
        /// Init: 2048-byte packet (02 FF ... 01 ...), response 512 bytes.
        /// Frame: 4096-byte USB transfers, each containing 8 × 512-byte sub-packets.
        /// Each sub-packet has a 16-byte header + 496 bytes of JPEG data.
        /// No response/ACK between frame writes — write-only stream.
        /// Protocol decoded from TRCC USB capture analysis.
        /// </summary>
        private async Task DoWinUsbTrofeoBulkProtocol(UsbEndpointWriter writer, UsbEndpointReader reader, CancellationToken token)
        {
            const int INIT_PACKET_SIZE = 2048;
            const int USB_TRANSFER_SIZE = 4096;    // Each USB bulk write
            const int SUB_PACKET_SIZE = 512;       // Sub-packets within each transfer
            const int SUB_HEADER_SIZE = 16;        // Header per sub-packet
            const int SUB_DATA_SIZE = 496;         // Data per sub-packet (512 - 16)
            const int SUBS_PER_TRANSFER = 8;       // 4096 / 512
            const int RESPONSE_SIZE = 512;

            // Reset pipes to clear stale state from previous failed attempts
            writer.Reset();
            reader.Reset();

            // Send init command: 2048 bytes, byte[0]=0x02, byte[1]=0xFF, byte[8]=0x01
            var initPacket = new byte[INIT_PACKET_SIZE];
            initPacket[0] = 0x02;
            initPacket[1] = 0xFF;
            initPacket[8] = 0x01;

            Logger.Information("ThermalrightPanelDevice {Device}: Sending TrofeoBulk init ({Size} bytes)", _device, INIT_PACKET_SIZE);

            // USB capture shows TRCC submits both read and write IRPs concurrently.
            // The device may require a pending IN transfer before it completes the OUT.
            // Submit the read first (async), then the write.
            var responseBuffer = new byte[RESPONSE_SIZE];
            ErrorCode readEc = ErrorCode.None;
            int readBytes = 0;

            var readTask = Task.Run(() =>
            {
                readEc = reader.Read(responseBuffer, 10000, out readBytes);
            });

            // Brief delay to ensure the read IRP reaches the USB stack
            await Task.Delay(50, token);

            var ec = writer.Write(initPacket, 5000, out int initWritten);
            if (ec != ErrorCode.None)
            {
                Logger.Error("ThermalrightPanelDevice {Device}: TrofeoBulk init write failed: {Error}", _device, ec);
                _device.UpdateRuntimeProperties(errorMessage: $"TrofeoBulk init failed: {ec}");
                return;
            }
            Logger.Information("ThermalrightPanelDevice {Device}: TrofeoBulk init sent ({Bytes} bytes)", _device, initWritten);

            // Wait for read to complete
            await readTask;
            if (readEc == ErrorCode.None && readBytes > 0)
            {
                var responseHex = BitConverter.ToString(responseBuffer, 0, Math.Min(readBytes, 32)).Replace("-", " ");
                Logger.Information("ThermalrightPanelDevice {Device}: TrofeoBulk response ({Bytes} bytes): {Hex}",
                    _device, readBytes, responseHex);

                // Parse resolution from init response: bytes 24-27 = LE32 width, bytes 28-31 = LE32 height
                if (readBytes >= 32)
                {
                    int reportedWidth = BitConverter.ToInt32(responseBuffer, 24);
                    int reportedHeight = BitConverter.ToInt32(responseBuffer, 28);

                    if (reportedWidth > 0 && reportedWidth <= 4096 && reportedHeight > 0 && reportedHeight <= 4096)
                    {
                        _panelWidth = reportedWidth;
                        _panelHeight = reportedHeight;
                        Logger.Information("ThermalrightPanelDevice {Device}: Device reports resolution {Width}x{Height}",
                            _device, reportedWidth, reportedHeight);
                    }
                }
            }
            else
            {
                Logger.Warning("ThermalrightPanelDevice {Device}: No TrofeoBulk response (ec={Error}), continuing anyway", _device, readEc);
            }

            UpdateDeviceDisplayName();
            await Task.Delay(100, token);

            // The WinUSB backend requires a pending IN IRP at all times for OUT writes to complete.
            // A background reader keeps an IRP pending; a SemaphoreSlim signals when an ACK arrives
            // so the frame loop waits for the device to finish processing before sending the next frame.
            var ackSemaphore = new SemaphoreSlim(0, 1);
            var readerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var backgroundReader = Task.Run(() =>
            {
                var readBuf = new byte[RESPONSE_SIZE];
                while (!readerCts.Token.IsCancellationRequested)
                {
                    var ec2 = reader.Read(readBuf, 1000, out int bytesRead);
                    if (ec2 == ErrorCode.None && bytesRead > 0)
                    {
                        // Signal that an ACK was received (don't block if already signaled)
                        try { ackSemaphore.Release(); } catch (SemaphoreFullException) { }
                    }
                }
            }, readerCts.Token);

            try
            {
                await RunRenderSendLoop(jpegData =>
                {
                    TrofeoBulkWriteFrame(writer, jpegData, USB_TRANSFER_SIZE, SUB_PACKET_SIZE, SUB_HEADER_SIZE, SUB_DATA_SIZE, SUBS_PER_TRANSFER);
                    // Wait for device ACK before next frame — flow control
                    if (!ackSemaphore.Wait(500))
                        Logger.Warning("ThermalrightPanelDevice {Device}: Frame ACK timeout, continuing", _device);
                }, token);
            }
            finally
            {
                readerCts.Cancel();
                try { await backgroundReader; } catch { }
            }
        }

        /// <summary>
        /// Write a single frame using TrofeoBulk sub-packet framing.
        /// Matches TRCC's ThreadSendDeviceDataLY: chunks are padded to a multiple of 4,
        /// written in 4096-byte (or trailing 2048-byte) bursts.
        /// ACK synchronization is handled by the caller via SemaphoreSlim.
        /// </summary>
        private void TrofeoBulkWriteFrame(UsbEndpointWriter writer,
            byte[] jpegData, int usbTransferSize, int subPacketSize, int subHeaderSize, int subDataSize, int subsPerTransfer)
        {
            int totalChunks = (jpegData.Length + subDataSize - 1) / subDataSize;
            int lastChunkDataSize = jpegData.Length % subDataSize;
            if (lastChunkDataSize == 0) lastChunkDataSize = subDataSize;

            // Pad total chunks to multiple of 4 (TRCC pads the USB buffer to fill complete 4096-byte transfers)
            int paddedChunks = totalChunks;
            int remainder = paddedChunks % 4;
            if (remainder != 0)
                paddedChunks += 4 - remainder;

            int totalUsbBytes = paddedChunks * subPacketSize;

            // Build the entire padded buffer with sub-packet headers + data
            var buffer = new byte[totalUsbBytes];
            var jpegSizeBytes = BitConverter.GetBytes(jpegData.Length);
            var totalChunksBytes = BitConverter.GetBytes((ushort)totalChunks);

            int jpegOffset = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                int off = i * subPacketSize;
                int dataSize = (i == totalChunks - 1) ? lastChunkDataSize : subDataSize;

                buffer[off + 0] = 0x01;     // Frame command
                buffer[off + 1] = 0xFF;     // Protocol marker
                jpegSizeBytes.CopyTo(buffer, off + 2);               // Total JPEG size LE32
                buffer[off + 6] = (byte)(dataSize & 0xFF);           // This chunk's data size LE16
                buffer[off + 7] = (byte)((dataSize >> 8) & 0xFF);
                buffer[off + 8] = 0x01;     // Command type (LY)
                totalChunksBytes.CopyTo(buffer, off + 9);            // Total chunk count LE16
                buffer[off + 11] = (byte)(i & 0xFF);                 // Chunk index LE16
                buffer[off + 12] = (byte)((i >> 8) & 0xFF);

                Array.Copy(jpegData, jpegOffset, buffer, off + subHeaderSize, dataSize);
                jpegOffset += dataSize;
            }
            // Padding chunks (beyond totalChunks) are left as zeros

            // Write in 4096-byte bursts; trailing remainder as 2048 (TRCC minimum burst)
            int writeOffset = 0;
            int bytesRemaining = totalUsbBytes;
            while (bytesRemaining > 0)
            {
                int writeSize = (bytesRemaining >= usbTransferSize) ? usbTransferSize : 2048;
                var writeEc = writer.Write(buffer, writeOffset, writeSize, 100, out _);
                if (writeEc != ErrorCode.None)
                    throw new Exception($"USB write failed: {writeEc}");
                writeOffset += usbTransferSize;
                bytesRemaining -= usbTransferSize;
            }

        }

        /// <summary>
        /// Sends a single frame using the Trofeo DA DB DC DD protocol over USB bulk.
        /// 20-byte header (magic, cmd=0x02, width/height, type, payload_len) + frame data in 512-byte chunks.
        /// Reused by DoWinUsbTrofeoProtocol and hybrid HID+Bulk mode.
        /// </summary>
        private static void SendTrofeoFrameOverBulk(
            UsbEndpointWriter writer, byte[] frameData, int width, int height,
            ThermalrightPixelFormat pixelFormat = ThermalrightPixelFormat.Jpeg)
        {
            var header = new byte[TROFEO_PACKET_SIZE];
            Array.Copy(TROFEO_MAGIC_BYTES, 0, header, 0, 4);
            header[4] = 0x02; // Frame command

            if (pixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian)
                header[6] = 0x01; // RGB565 format flag

            BitConverter.GetBytes((ushort)width).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)height).CopyTo(header, 10);
            header[12] = 0x02; // Frame type
            BitConverter.GetBytes(frameData.Length).CopyTo(header, 16);

            int firstChunkSize = Math.Min(frameData.Length, TROFEO_PACKET_SIZE - TROFEO_HEADER_JPEG_OFFSET);
            Array.Copy(frameData, 0, header, TROFEO_HEADER_JPEG_OFFSET, firstChunkSize);

            var writeEc = writer.Write(header, 5000, out _);
            if (writeEc != ErrorCode.None)
                throw new Exception($"USB bulk write failed: {writeEc}");

            int offset = firstChunkSize;
            while (offset < frameData.Length)
            {
                var chunk = new byte[TROFEO_PACKET_SIZE];
                int chunkSize = Math.Min(frameData.Length - offset, TROFEO_PACKET_SIZE);
                Array.Copy(frameData, offset, chunk, 0, chunkSize);

                writeEc = writer.Write(chunk, 5000, out _);
                if (writeEc != ErrorCode.None)
                    throw new Exception($"USB bulk write failed: {writeEc}");

                offset += chunkSize;
            }
        }

        private async Task DoWorkHidAsync(CancellationToken token)
        {
            try
            {
                var vendorId = _device.ModelInfo?.VendorId ?? 0;
                var productId = _device.ModelInfo?.ProductId ?? 0;
                Logger.Information("ThermalrightPanelDevice {Device}: Opening device via HID (VID={Vid:X4} PID={Pid:X4})...",
                    _device, vendorId, productId);

                using var hidDevice = HidPanelDevice.Open(vendorId, productId);

                if (hidDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open HID device", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open HID device. Make sure:\n" +
                        "1. The device is connected\n" +
                        "2. No other application is using the device");
                    await Task.Delay(OPEN_FAILURE_BACKOFF_MS, token);
                    return;
                }

                Logger.Information("ThermalrightPanelDevice {Device}: HID device opened successfully!", _device);

                // HID init with retry: up to 3 attempts, 500ms between
                byte[]? response = null;
                bool initOk = false;
                for (int attempt = 1; attempt <= 3 && !initOk; attempt++)
                {
                    if (attempt > 1)
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: HID init retry {Attempt}/3", _device, attempt);
                        await Task.Delay(500, token);
                    }

                    // 50ms pre-init delay
                    await Task.Delay(50, token);

                    if (!hidDevice.SendInit())
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: HID init send failed (attempt {Attempt}/3)", _device, attempt);
                        continue;
                    }

                    // 200ms post-init delay before reading response
                    await Task.Delay(200, token);

                    response = hidDevice.ReadInitResponse();
                    if (response != null)
                        initOk = true;
                    else
                        Logger.Warning("ThermalrightPanelDevice {Device}: No HID init response (attempt {Attempt}/3)", _device, attempt);
                }

                if (!initOk)
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: HID init failed after 3 attempts", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "HID init failed after 3 attempts");
                    return;
                }

                // Read init response to determine panel model from PM byte and identifier
                if (response != null && response.Length >= 6)
                {
                    // Byte[5] = PM (Product Mode) - primary discriminator for Trofeo HID panels
                    // Byte[4] = Sub byte - secondary discriminator (e.g. PM 0x3A sub 0 = FW SE, sub !=0 = LM26)
                    var pm = response[5];
                    var sub = response[4];
                    Logger.Information("ThermalrightPanelDevice {Device}: HID PM byte: 0x{PM:X2} ({PMDec}), sub byte: 0x{Sub:X2} ({SubDec})",
                        _device, pm, pm, sub, sub);

                    var pmModel = ThermalrightPanelModelDatabase.GetModelByPM(pm, sub);
                    if (pmModel != null)
                    {
                        _detectedModel = pmModel;
                        _panelWidth = pmModel.RenderWidth;
                        _panelHeight = pmModel.RenderHeight;
                        _device.Model = pmModel.Model;
                        Logger.Information("ThermalrightPanelDevice {Device}: PM 0x{PM:X2} -> {Model} ({Width}x{Height})",
                            _device, pm, pmModel.Name, _panelWidth, _panelHeight);
                    }
                    else
                    {
                        // Fall back to resolution-only lookup
                        var resolution = ThermalrightPanelModelDatabase.GetResolutionFromPM(pm);
                        if (resolution != null)
                        {
                            _panelWidth = resolution.Value.Width;
                            _panelHeight = resolution.Value.Height;
                            Logger.Information("ThermalrightPanelDevice {Device}: PM 0x{PM:X2} -> {Width}x{Height} ({Size})",
                                _device, pm, _panelWidth, _panelHeight, resolution.Value.SizeName);
                        }
                        else
                        {
                            Logger.Warning("ThermalrightPanelDevice {Device}: Unknown PM value 0x{PM:X2}, using default {Width}x{Height}",
                                _device, pm, _panelWidth, _panelHeight);
                        }
                    }

                    // Log identifier for diagnostics; use for model detection if PM didn't resolve it
                    if (response.Length >= 28)
                    {
                        var identifierBytes = new byte[Math.Min(8, response.Length - 20)];
                        Array.Copy(response, 20, identifierBytes, 0, identifierBytes.Length);
                        // Strip non-printable chars (some panels append BEL/0x07 after the identifier)
                        var identifier = new string(System.Text.Encoding.ASCII.GetString(identifierBytes)
                            .Where(c => c >= ' ').ToArray());
                        Logger.Information("ThermalrightPanelDevice {Device}: HID device identifier: {Id}", _device, identifier);

                        if (_detectedModel == null)
                        {
                            var identifiedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier);
                            if (identifiedModel != null)
                            {
                                _detectedModel = identifiedModel;
                                _device.Model = identifiedModel.Model;
                                Logger.Information("ThermalrightPanelDevice {Device}: Identified as {Model} via HID identifier", _device, identifiedModel.Name);
                            }
                            else
                            {
                                Logger.Warning("ThermalrightPanelDevice {Device}: Unknown HID identifier '{Id}'", _device, identifier);
                            }
                        }
                    }
                }

                UpdateDeviceDisplayName();

                await Task.Delay(100, token); // Small delay after init

                // --- Attempt hybrid HID+Bulk upgrade ---
                // 0416:5302 is a composite USB device: HID interface (auto-driver) + vendor-specific bulk interface.
                // TRCC uses HID for init only, then switches to bulk for frame data (higher throughput).
                // If the bulk interface has a WinUSB driver installed, we can do the same.
                UsbEndpointWriter? bulkWriter = null;
                UsbDevice? bulkDevice = null;
                bool useBulk = false;

                try
                {
                    var bulkRegistry = FindUsbRegistry(
                        ThermalrightPanelModelDatabase.TROFEO_VENDOR_ID,
                        ThermalrightPanelModelDatabase.TROFEO_PRODUCT_ID_686,
                        matchDeviceId: false);

                    if (bulkRegistry != null)
                    {
                        bulkDevice = bulkRegistry.Device;
                        if (bulkDevice != null)
                        {
                            if (bulkDevice is IUsbDevice wholeBulkDevice)
                            {
                                wholeBulkDevice.SetConfiguration(1);
                                wholeBulkDevice.ClaimInterface(0);
                            }

                            // Find write endpoint (prefer EP2 OUT as TRCC uses it)
                            WriteEndpointID bulkWriteEp = WriteEndpointID.Ep02;
                            foreach (var config in bulkDevice.Configs)
                            {
                                foreach (var iface in config.InterfaceInfoList)
                                {
                                    foreach (var ep in iface.EndpointInfoList)
                                    {
                                        var addr = (byte)ep.Descriptor.EndpointID;
                                        if ((addr & 0x80) == 0) // OUT endpoint
                                        {
                                            bulkWriteEp = (WriteEndpointID)addr;
                                            break;
                                        }
                                    }
                                }
                            }

                            bulkWriter = bulkDevice.OpenEndpointWriter(bulkWriteEp);
                            useBulk = true;

                            Logger.Information(
                                "ThermalrightPanelDevice {Device}: Bulk upgrade successful! Using EP 0x{Ep:X2} for frame data",
                                _device, (byte)bulkWriteEp);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Information(
                        "ThermalrightPanelDevice {Device}: Bulk interface not available ({Message}), using HID fallback",
                        _device, ex.Message);
                    (bulkDevice as IDisposable)?.Dispose();
                    bulkDevice = null;
                    bulkWriter = null;
                    useBulk = false;
                }

                if (!useBulk)
                {
                    Logger.Information(
                        "ThermalrightPanelDevice {Device}: No WinUSB driver on bulk interface, continuing with HID transport",
                        _device);
                }

                var width = _panelWidth;
                var height = _panelHeight;
                var pixelFormat = _detectedModel?.PixelFormat ?? ThermalrightPixelFormat.Jpeg;

                try
                {
                    if (useBulk && bulkWriter != null)
                    {
                        // Bulk mode: send frames over USB bulk (matches TRCC USBLCDNEW.exe behavior)
                        await RunRenderSendLoop(frameData =>
                        {
                            SendTrofeoFrameOverBulk(bulkWriter, frameData, width, height, pixelFormat);
                            Thread.Sleep(1); // 1ms inter-frame delay (from TRCC)
                        }, token);
                    }
                    else
                    {
                        // HID fallback: send frames as HID output reports (existing behavior)
                        await RunRenderSendLoop(frameData =>
                        {
                            bool ok = pixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian
                                ? hidDevice.SendRgb565Frame(frameData, width, height)
                                : hidDevice.SendJpegFrame(frameData, width, height);
                            if (!ok) throw new Exception("HID frame send failed");
                            Thread.Sleep(1); // 1ms inter-frame delay
                        }, token);
                    }
                }
                finally
                {
                    bulkWriter?.Dispose();
                    (bulkDevice as IDisposable)?.Dispose();
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("ThermalrightPanelDevice {Device}: Task cancelled", _device);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: Error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }

        private void UpdateDeviceDisplayName()
        {
            var modelName = _detectedModel?.Name ?? "Panel";
            _device.RuntimeProperties.Name = $"Thermalright {modelName} ({_panelWidth}x{_panelHeight})";
            Logger.Information("ThermalrightPanelDevice {Device}: Connected to {Name}, rendering at {RenderW}x{RenderH}",
                _device, modelName, _panelWidth, _panelHeight);
        }

        /// <summary>
        /// Shared render+send loop used by both WinUSB and HID protocols.
        /// The sendFrame action receives JPEG data and handles protocol-specific sending.
        /// </summary>
        private async Task RunRenderSendLoop(Action<byte[]> sendFrame, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? _latestFrame = null;
            AutoResetEvent _frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Thermalright-Render-{_device.DeviceLocation}";
                var stopwatch = new Stopwatch();

                while (!renderToken.IsCancellationRequested)
                {
                    stopwatch.Restart();
                    var frame = GenerateFrameBuffer();
                    Interlocked.Exchange(ref _latestFrame, frame);
                    _frameAvailable.Set();

                    var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                    var adaptiveFrameTime = 0;

                    var elapsedMs = (int)stopwatch.ElapsedMilliseconds;

                    if (elapsedMs < targetFrameTime)
                    {
                        adaptiveFrameTime = targetFrameTime - elapsedMs;
                    }

                    if (adaptiveFrameTime > 0)
                    {
                        await Task.Delay(adaptiveFrameTime, token);
                    }
                }
            }, renderToken);

            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Thermalright-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();

                    while (!token.IsCancellationRequested)
                    {
                        if (_frameAvailable.WaitOne(100))
                        {
                            var jpegData = Interlocked.Exchange(ref _latestFrame, null);
                            if (jpegData != null)
                            {
                                stopwatch.Restart();

                                sendFrame(jpegData);

                                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "ThermalrightPanelDevice {Device}: Error in send task", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                }
                finally
                {
                    renderCts.Cancel();
                }
            }, token);

            await Task.WhenAll(renderTask, sendTask);
        }
    }
}
