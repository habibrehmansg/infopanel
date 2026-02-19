using InfoPanel.Extensions;
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
        /// </summary>
        private byte[] BuildDisplayHeader(int jpegSize)
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-7: Command = 2 (display frame)
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 4);

            // Bytes 8-11: Width
            BitConverter.GetBytes(_panelWidth).CopyTo(header, 8);

            // Bytes 12-15: Height
            BitConverter.GetBytes(_panelHeight).CopyTo(header, 12);

            // Bytes 16-55: Zero padding (already zeroed)

            // Bytes 56-59: Command repeated = 2
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 56);

            // Bytes 60-63: JPEG size (little-endian)
            BitConverter.GetBytes(jpegSize).CopyTo(header, 60);

            return header;
        }

        public byte[]? GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                // Render to RGBA bitmap
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                // Resize to panel resolution with rotation
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                // Encode as JPEG
                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

                return data.ToArray();
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var transportType = _device.ModelInfo?.TransportType ?? ThermalrightTransportType.WinUsb;
            var protocolType = _device.ModelInfo?.ProtocolType ?? ThermalrightProtocolType.ChiZhu;
            Logger.Information("ThermalrightPanelDevice {Device}: Using {Transport} transport, {Protocol} protocol", _device, transportType, protocolType);

            if (transportType == ThermalrightTransportType.Hid)
                await DoWorkHidAsync(token);
            else
                await DoWorkWinUsbAsync(token);
        }

        /// <summary>
        /// Finds the matching UsbRegistry for this device by scanning all connected USB devices.
        /// </summary>
        private UsbRegistry? FindUsbRegistry(int vendorId, int productId)
        {
            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
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
            Logger.Information("ThermalrightPanelDevice {Device}: Sending ChiZhu init command (64 bytes)", _device);

            var ec = writer.Write(initCommand, 5000, out int initWritten);
            if (ec != ErrorCode.None)
            {
                Logger.Error("ThermalrightPanelDevice {Device}: Init command failed: {Error}", _device, ec);
                _device.UpdateRuntimeProperties(errorMessage: $"Init command failed: {ec}");
                return;
            }
            Logger.Information("ThermalrightPanelDevice {Device}: Init command sent ({Bytes} bytes)", _device, initWritten);

            // Read device response to identify panel type (SSCRM-V1, SSCRM-V3, etc.)
            var responseBuffer = new byte[64];
            ec = reader.Read(responseBuffer, 5000, out int bytesRead);
            if (ec == ErrorCode.None && bytesRead > 0)
            {
                var responseHex = BitConverter.ToString(responseBuffer, 0, Math.Min(bytesRead, 32)).Replace("-", "");
                Logger.Information("ThermalrightPanelDevice {Device}: Device response ({Bytes} bytes): {Hex}",
                    _device, bytesRead, responseHex);

                if (bytesRead >= 12)
                {
                    var deviceIdentifier = System.Text.Encoding.ASCII.GetString(responseBuffer, 4, 8).TrimEnd('\0');
                    Logger.Information("ThermalrightPanelDevice {Device}: Device identifier: {Id}", _device, deviceIdentifier);

                    _detectedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(deviceIdentifier);
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
            }
            else
            {
                Logger.Warning("ThermalrightPanelDevice {Device}: No response from device (ec={Error}), using default {Width}x{Height}",
                    _device, ec, _panelWidth, _panelHeight);
            }

            UpdateDeviceDisplayName();
            await Task.Delay(100, token);

            await RunRenderSendLoop(jpegData =>
            {
                var header = BuildDisplayHeader(jpegData.Length);
                var packet = new byte[HEADER_SIZE + jpegData.Length];
                Array.Copy(header, 0, packet, 0, HEADER_SIZE);
                Array.Copy(jpegData, 0, packet, HEADER_SIZE, jpegData.Length);

                var writeEc = writer.Write(packet, 5000, out int bytesWritten);
                if (writeEc != ErrorCode.None)
                {
                    throw new Exception($"USB write failed: {writeEc}");
                }
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
            var ec = writer.Write(initPacket, 5000, out int initWritten);
            if (ec != ErrorCode.None)
            {
                Logger.Error("ThermalrightPanelDevice {Device}: Trofeo init command failed: {Error}", _device, ec);
                _device.UpdateRuntimeProperties(errorMessage: $"Init command failed: {ec}");
                return;
            }
            Logger.Information("ThermalrightPanelDevice {Device}: Trofeo init sent ({Bytes} bytes)", _device, initWritten);

            // Read init response
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

            UpdateDeviceDisplayName();
            await Task.Delay(100, token);

            // Run render+send loop with Trofeo frame format over bulk USB (no HID report ID prefix)
            var width = _panelWidth;
            var height = _panelHeight;
            await RunRenderSendLoop(jpegData =>
            {
                // Build 512-byte header: magic, cmd=0x02, width/height, type=0x02, jpeg size, first JPEG chunk
                var header = new byte[TROFEO_PACKET_SIZE];
                Array.Copy(TROFEO_MAGIC_BYTES, 0, header, 0, 4);
                header[4] = 0x02; // Frame command
                BitConverter.GetBytes((ushort)width).CopyTo(header, 8);
                BitConverter.GetBytes((ushort)height).CopyTo(header, 10);
                header[12] = 0x02; // Frame type
                BitConverter.GetBytes(jpegData.Length).CopyTo(header, 16);

                int firstChunkSize = Math.Min(jpegData.Length, TROFEO_PACKET_SIZE - TROFEO_HEADER_JPEG_OFFSET);
                Array.Copy(jpegData, 0, header, TROFEO_HEADER_JPEG_OFFSET, firstChunkSize);

                var writeEc = writer.Write(header, 5000, out _);
                if (writeEc != ErrorCode.None)
                    throw new Exception($"USB write failed: {writeEc}");

                // Send remaining JPEG data in 512-byte chunks
                int offset = firstChunkSize;
                while (offset < jpegData.Length)
                {
                    var chunk = new byte[TROFEO_PACKET_SIZE];
                    int chunkSize = Math.Min(jpegData.Length - offset, TROFEO_PACKET_SIZE);
                    Array.Copy(jpegData, offset, chunk, 0, chunkSize);

                    writeEc = writer.Write(chunk, 5000, out _);
                    if (writeEc != ErrorCode.None)
                        throw new Exception($"USB write failed: {writeEc}");

                    offset += chunkSize;
                }
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

            // The device requires a pending IN transfer at all times to accept OUT writes.
            // Keep a background reader submitting read IRPs continuously.
            var readerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var backgroundReader = Task.Run(() =>
            {
                var readBuf = new byte[RESPONSE_SIZE];
                while (!readerCts.Token.IsCancellationRequested)
                {
                    reader.Read(readBuf, 1000, out _);
                }
            }, readerCts.Token);

            try
            {
                // Run render+send loop with sub-packet framing
                await RunRenderSendLoop(jpegData =>
                {
                    TrofeoBulkWriteFrame(writer, jpegData, USB_TRANSFER_SIZE, SUB_PACKET_SIZE, SUB_HEADER_SIZE, SUB_DATA_SIZE, SUBS_PER_TRANSFER);
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
        /// Each 4096-byte USB transfer contains 8 × 512-byte sub-packets.
        /// Each sub-packet: 16-byte header + 496 bytes of JPEG data.
        /// Header format (16 bytes):
        ///   [0]    0x01 frame command
        ///   [1]    0xFF protocol marker
        ///   [2-5]  LE32 total JPEG data size
        ///   [6-7]  LE16 data per sub-packet (496)
        ///   [8]    0x01 flag
        ///   [9-10] LE16 total sub-packet count
        ///   [11]   sub-packet sequence number (wraps at 256)
        ///   [12-15] zeros
        /// </summary>
        private void TrofeoBulkWriteFrame(UsbEndpointWriter writer, byte[] jpegData,
            int usbTransferSize, int subPacketSize, int subHeaderSize, int subDataSize, int subsPerTransfer)
        {
            int totalChunks = (jpegData.Length + subDataSize - 1) / subDataSize;
            int jpegOffset = 0;
            int chunkIndex = 0;

            // Pre-compute header fields that are constant for all sub-packets in this frame
            var jpegSizeBytes = BitConverter.GetBytes(jpegData.Length);
            var subDataSizeBytes = BitConverter.GetBytes((ushort)subDataSize);
            var totalChunksBytes = BitConverter.GetBytes((ushort)totalChunks);

            while (jpegOffset < jpegData.Length)
            {
                // Build one 4096-byte USB transfer containing up to 8 sub-packets
                var transfer = new byte[usbTransferSize];

                for (int sub = 0; sub < subsPerTransfer && jpegOffset < jpegData.Length; sub++)
                {
                    int off = sub * subPacketSize;

                    // 16-byte sub-packet header
                    transfer[off + 0] = 0x01;     // Frame command
                    transfer[off + 1] = 0xFF;     // Protocol marker
                    jpegSizeBytes.CopyTo(transfer, off + 2);       // Total JPEG size LE32
                    subDataSizeBytes.CopyTo(transfer, off + 6);    // Data per sub-packet LE16 (496)
                    transfer[off + 8] = 0x01;     // Flag
                    totalChunksBytes.CopyTo(transfer, off + 9);    // Total chunk count LE16
                    transfer[off + 11] = (byte)(chunkIndex & 0xFF); // Chunk sequence (wraps)
                    // Bytes 12-15 = zeros (already zeroed)

                    // Copy JPEG data after header
                    int dataSize = Math.Min(subDataSize, jpegData.Length - jpegOffset);
                    Array.Copy(jpegData, jpegOffset, transfer, off + subHeaderSize, dataSize);

                    jpegOffset += dataSize;
                    chunkIndex++;
                }

                var writeEc = writer.Write(transfer, 5000, out _);
                if (writeEc != ErrorCode.None)
                    throw new Exception($"USB write failed: {writeEc}");
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

                // Send HID init command
                if (!hidDevice.SendInit())
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: HID init failed", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "HID init command failed");
                    return;
                }

                // Read init response to determine panel resolution from PM byte
                var response = hidDevice.ReadInitResponse();
                if (response != null && response.Length >= 6)
                {
                    // Byte[5] = PM (Product Mode) - maps to resolution
                    var pm = response[5];
                    Logger.Information("ThermalrightPanelDevice {Device}: HID PM byte: 0x{PM:X2} ({PMDec})", _device, pm, pm);

                    var resolution = ThermalrightPanelModelDatabase.GetResolutionFromPM(pm);
                    if (resolution != null)
                    {
                        _panelWidth = resolution.Value.Width;
                        _panelHeight = resolution.Value.Height;
                        Logger.Information("ThermalrightPanelDevice {Device}: PM {PM} -> {Width}x{Height} ({Size})",
                            _device, pm, _panelWidth, _panelHeight, resolution.Value.SizeName);
                    }
                    else
                    {
                        Logger.Warning("ThermalrightPanelDevice {Device}: Unknown PM value 0x{PM:X2}, using default {Width}x{Height}",
                            _device, pm, _panelWidth, _panelHeight);
                    }

                    // Use identifier (bytes 20+) to refine model detection
                    if (response.Length >= 28)
                    {
                        var identifierBytes = new byte[Math.Min(8, response.Length - 20)];
                        Array.Copy(response, 20, identifierBytes, 0, identifierBytes.Length);
                        var identifier = System.Text.Encoding.ASCII.GetString(identifierBytes).TrimEnd('\0');
                        Logger.Information("ThermalrightPanelDevice {Device}: HID device identifier: {Id}", _device, identifier);

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

                UpdateDeviceDisplayName();

                await Task.Delay(100, token); // Small delay after init

                // Run the render+send loop using HID reports
                var width = _panelWidth;
                var height = _panelHeight;
                await RunRenderSendLoop(jpegData =>
                {
                    if (!hidDevice.SendJpegFrame(jpegData, width, height))
                    {
                        throw new Exception("HID frame send failed");
                    }
                }, token);
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

            _device.UpdateRuntimeProperties(isRunning: true);

            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Thermalright-Render-{_device.DeviceLocation}";
                var stopwatch = new Stopwatch();

                while (!renderToken.IsCancellationRequested)
                {
                    stopwatch.Restart();
                    var frame = GenerateJpegBuffer();

                    if (frame != null)
                    {
                        Interlocked.Exchange(ref _latestFrame, frame);
                        _frameAvailable.Set();
                    }

                    var targetFrameTime = 1000 / ConfigModel.Instance.Settings.TargetFrameRate;
                    var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime * 0.9), targetFrameTime);
                    var adaptiveFrameTime = 0;

                    var elapsedMs = (int)stopwatch.ElapsedMilliseconds;

                    if (elapsedMs < desiredFrameTime)
                    {
                        adaptiveFrameTime = desiredFrameTime - elapsedMs;
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
