// V1 oracle, test-only: the pure parts of InfoPanel 1.4.x c71f0f00, InfoPanel/Services/ThermalrightPanelDeviceTask.cs,
// each copied verbatim from the lines cited above it, but for these changes: log lines and runtime-property writes are
// left out; a USB write is a capture into Writes (LibUsbDotNet's writer.Write(buffer, offset, count, …) becomes
// Write(buffer, offset, count)); a read's reply is passed in as (responseBuffer, bytesRead); the task's device is the
// small V1Device below; the unsafe pointer loops of the RGB565 conversion read the same bytes through a span.
using System.Buffers;
using SkiaSharp;

namespace InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle
{
    /// <summary>The fields of 1.4.x's ThermalrightPanelDevice the protocol code reads.</summary>
    public sealed class V1Device
    {
        public ThermalrightPanelModel Model { get; set; }
        public ThermalrightPanelModelInfo? ModelInfo => ThermalrightPanelModelDatabase.Models.TryGetValue(Model, out var info) ? info : null;
    }

    public sealed class V1DeviceTask
    {
        // :34-41
        private static readonly byte[] MAGIC_BYTES = { 0x12, 0x34, 0x56, 0x78 };
        private const int HEADER_SIZE = 64;
        private const int COMMAND_DISPLAY = 0x02;
        private static readonly byte[] TROFEO_MAGIC_BYTES = { 0xDA, 0xDB, 0xDC, 0xDD };
        private const int TROFEO_PACKET_SIZE = 512;
        private const int TROFEO_HEADER_JPEG_OFFSET = 20;

        // :44-45
        private const int DEFAULT_WIDTH = 480;
        private const int DEFAULT_HEIGHT = 480;

        public readonly V1Device _device;
        public int _panelWidth = DEFAULT_WIDTH;
        public int _panelHeight = DEFAULT_HEIGHT;
        public ThermalrightPanelModelInfo? _detectedModel;
        public int _maxJpegSize;
        public int _flickerFixCropHeight;

        public readonly List<byte[]> Writes = new();

        // :65-76
        public V1DeviceTask(V1Device device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if (_device.ModelInfo != null)
            {
                _panelWidth = _device.ModelInfo.RenderWidth;
                _panelHeight = _device.ModelInfo.RenderHeight;
                _detectedModel = _device.ModelInfo;
            }
        }

        private void Write(byte[] buffer, int offset, int count) => Writes.Add(buffer.AsSpan(offset, count).ToArray());

        // :82-97
        public byte[] BuildInitCommand()
        {
            var header = new byte[HEADER_SIZE];
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);
            BitConverter.GetBytes(1).CopyTo(header, 56);
            return header;
        }

        // :103-134
        public byte[] BuildDisplayHeader(int dataSize)
        {
            var header = new byte[HEADER_SIZE];
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);
            int cmd = _detectedModel?.PixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian
                ? 0x03 : COMMAND_DISPLAY;
            BitConverter.GetBytes(cmd).CopyTo(header, 4);
            BitConverter.GetBytes(_panelWidth).CopyTo(header, 8);
            BitConverter.GetBytes(_panelHeight).CopyTo(header, 12);
            BitConverter.GetBytes(0x02).CopyTo(header, 56);
            BitConverter.GetBytes(dataSize).CopyTo(header, 60);
            return header;
        }

        // :177-185, the flicker fix's crop, with `cropHeight` passed in.
        public static SKBitmap Crop(SKBitmap encodeBitmap, int _panelWidth, int cropHeight)
        {
            var cropped = new SKBitmap(_panelWidth, cropHeight, encodeBitmap.ColorType, encodeBitmap.AlphaType);
            using var canvas = new SKCanvas(cropped);
            canvas.DrawBitmap(encodeBitmap, 0, 0);
            return cropped;
        }

        // :203-218, the adaptive quality loop, with the encoder a function of the quality; returns the qualities tried.
        public List<int> AdaptiveQualities(int quality, Func<int, int> encodedLength)
        {
            var tried = new List<int>();
            int resultLength = encodedLength(quality);
            if (_maxJpegSize > 0 && resultLength > _maxJpegSize)
            {
                for (quality -= 5; quality >= 50 && resultLength > _maxJpegSize; quality -= 5)
                {
                    tried.Add(quality);
                    resultLength = encodedLength(quality);
                }
            }
            return tried;
        }

        // :298-305 and :329-330, the mask's degrees and resource name. 1.4's LCD_ROTATION is passed as degrees.
        public static string MaskResourceName(ThermalrightDisplayMask mask, int rotationDegrees, int maskRotationOffset)
        {
            int degrees = rotationDegrees switch
            {
                90 => 90,
                180 => 180,
                270 => 270,
                _ => 0
            };
            degrees = (degrees + maskRotationOffset) % 360;
            string prefix = mask == ThermalrightDisplayMask.RoundedLeft ? "mask_rounded_left" : "mask_rounded_all";
            string resourceName = $"InfoPanel.Resources.Overlays.{prefix}_{degrees}.png";
            return resourceName;
        }

        // :350-391, reading the bitmap's bytes through a span in place of the pointer loops.
        public static byte[] ConvertRgba8888ToRgb565(SKBitmap bitmap, bool bigEndian)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int srcRowBytes = bitmap.RowBytes;
            byte[] dst = new byte[width * height * 2];
            ReadOnlySpan<byte> srcBase = bitmap.GetPixelSpan();

            for (int y = 0; y < height; y++)
            {
                var srcRow = srcBase.Slice(y * srcRowBytes);
                var dstRow = dst.AsSpan(y * width * 2);
                for (int x = 0; x < width; x++)
                {
                    byte r = srcRow[x * 4];
                    byte g = srcRow[x * 4 + 1];
                    byte b = srcRow[x * 4 + 2];

                    byte hi = (byte)((r & 0xF8) | (g >> 5));
                    byte lo = (byte)(((g & 0x1C) << 3) | (b >> 3));

                    if (bigEndian)
                    {
                        dstRow[x * 2] = hi;
                        dstRow[x * 2 + 1] = lo;
                    }
                    else
                    {
                        dstRow[x * 2] = lo;
                        dstRow[x * 2 + 1] = hi;
                    }
                }
            }
            return dst;
        }

        // :729-841, the ChiZhu init reply's boot check and three-pass detection (the reply passed in: the last of the
        // boot loop's tries, which is parsed whatever it is); returns whether it says the device is booting. The serial
        // and DEV_INFO fields are returned too.
        public bool ChiZhuReply(byte[] responseBuffer, int bytesRead, out string? serialOut, out byte flagsOut, out (byte, byte, byte) dispOut)
        {
            serialOut = null;
            flagsOut = 0;
            dispOut = default;
            bool booting = false;
            if (bytesRead >= 8 &&
                responseBuffer[4] == 0xA1 && responseBuffer[5] == 0xA2 &&
                responseBuffer[6] == 0xA3 && responseBuffer[7] == 0xA4)
            {
                booting = true;
            }

            if (bytesRead > 0)
            {
                byte? pm = bytesRead >= 25 ? responseBuffer[24] : null;
                byte? sub = bytesRead >= 29 ? responseBuffer[28] : null;

                string? deviceIdentifier = null;
                if (bytesRead >= 12)
                {
                    int idLen = Math.Min(16, bytesRead - 4);
                    deviceIdentifier = System.Text.Encoding.ASCII.GetString(responseBuffer, 4, idLen).TrimEnd('\0');
                }

                if (!string.IsNullOrEmpty(deviceIdentifier) && sub.HasValue)
                {
                    _detectedModel = ThermalrightPanelModelDatabase.GetModelByIdentifierAndSub(deviceIdentifier, sub.Value);
                    if (_detectedModel != null)
                    {
                        _panelWidth = _detectedModel.RenderWidth;
                        _panelHeight = _detectedModel.RenderHeight;
                        _device.Model = _detectedModel.Model;
                    }
                }

                if (_detectedModel == null && pm.HasValue && sub.HasValue)
                {
                    var chizhuModel = ThermalrightPanelModelDatabase.GetModelByChiZhuPM(pm.Value, sub.Value);
                    if (chizhuModel != null)
                    {
                        _detectedModel = chizhuModel;
                        _panelWidth = chizhuModel.RenderWidth;
                        _panelHeight = chizhuModel.RenderHeight;
                        _device.Model = chizhuModel.Model;
                    }
                }

                if (_detectedModel == null && !string.IsNullOrEmpty(deviceIdentifier))
                {
                    _detectedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(deviceIdentifier);
                    if (_detectedModel != null)
                    {
                        _panelWidth = _detectedModel.RenderWidth;
                        _panelHeight = _detectedModel.RenderHeight;
                        _device.Model = _detectedModel.Model;
                    }
                }

                if (bytesRead >= 37 && responseBuffer[17] == 0x10)
                {
                    var serial = BitConverter.ToString(responseBuffer, 21, 16).Replace("-", "");
                    serialOut = serial;
                }

                byte flags = bytesRead >= 21 ? responseBuffer[20] : (byte)0;
                byte disp1 = bytesRead >= 33 ? responseBuffer[32] : (byte)0;
                byte disp2 = bytesRead >= 37 ? responseBuffer[36] : (byte)0;
                byte disp3 = bytesRead >= 41 ? responseBuffer[40] : (byte)0;
                flagsOut = flags;
                dispOut = (disp1, disp2, disp3);
            }
            return booting;
        }

        // :853-867, one ChiZhu frame; returns whether a zero-length packet followed.
        public bool ChiZhuFrame(byte[] frameData)
        {
            var header = BuildDisplayHeader(frameData.Length);
            int packetSize = HEADER_SIZE + frameData.Length;
            var packet = ArrayPool<byte>.Shared.Rent(packetSize);
            try
            {
                Array.Copy(header, 0, packet, 0, HEADER_SIZE);
                Array.Copy(frameData, 0, packet, HEADER_SIZE, frameData.Length);
                Write(packet, 0, packetSize);
                if (packetSize % 512 == 0)
                {
                    Write(Array.Empty<byte>(), 0, 0);
                    return true;
                }
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        // :980-983, TrofeoBulk's (LY) init packet.
        public static byte[] TrofeoBulkInit()
        {
            const int INIT_PACKET_SIZE = 2048;
            var initPacket = new byte[INIT_PACKET_SIZE];
            initPacket[0] = 0x02;
            initPacket[1] = 0xFF;
            initPacket[8] = 0x01;
            return initPacket;
        }

        // :1027-1118, TrofeoBulk's init reply (passed in) and the model variant it makes. Returns whether the header was
        // as expected and the serial.
        public bool TrofeoBulkReply(byte[] responseBuffer, int readBytes, out string? serialOut)
        {
            bool headerOk = true;
            serialOut = null;
            if (readBytes > 0)
            {
                if (readBytes >= 9)
                {
                    if (responseBuffer[0] != 0x03 || responseBuffer[1] != 0xFF || responseBuffer[8] != 0x01)
                    {
                        headerOk = false;
                    }
                }

                if (readBytes >= 20)
                {
                    var serial = BitConverter.ToString(responseBuffer, 16, 4).Replace("-", "");
                    serialOut = serial;
                }

                if (readBytes >= 32)
                {
                    int reportedWidth = BitConverter.ToInt32(responseBuffer, 24);
                    int reportedHeight = BitConverter.ToInt32(responseBuffer, 28);

                    if (reportedWidth > 0 && reportedWidth <= 4096 && reportedHeight > 0 && reportedHeight <= 4096)
                    {
                        _panelWidth = reportedWidth;
                        _panelHeight = reportedHeight;
                    }
                }
            }

            byte? trofeoB20 = readBytes >= 21 ? (byte?)responseBuffer[20] : null;

            if (trofeoB20 == 0x05
                && ThermalrightPanelModelDatabase.Models.TryGetValue(ThermalrightPanelModel.TrofeoVision113, out var v113Model))
            {
                _detectedModel = v113Model;
                _device.Model = v113Model.Model;
                _panelWidth = v113Model.RenderWidth;
                _panelHeight = v113Model.RenderHeight;
            }
            else if (_device.Model == ThermalrightPanelModel.TrofeoVision113 && _device.ModelInfo != null)
            {
                _panelWidth = _device.ModelInfo.RenderWidth;
                _panelHeight = _device.ModelInfo.RenderHeight;
            }
            else if (_panelHeight != 480 && _device.Model == ThermalrightPanelModel.TrofeoVision916
                && ThermalrightPanelModelDatabase.Models.TryGetValue(ThermalrightPanelModel.TrofeoVision916V2, out var v2Model))
            {
                _detectedModel = v2Model;
                _device.Model = v2Model.Model;
                _panelWidth = v2Model.RenderWidth;
                _panelHeight = v2Model.RenderHeight;
            }
            else if (_device.Model == ThermalrightPanelModel.TrofeoVision916V2 && _device.ModelInfo != null)
            {
                _panelWidth = _device.ModelInfo.RenderWidth;
                _panelHeight = _device.ModelInfo.RenderHeight;
            }

            if (_panelHeight == 480 && _device.Model != ThermalrightPanelModel.TrofeoVision113)
            {
                _flickerFixCropHeight = 462;
            }
            return headerOk;
        }

        // :1139-1184, one TrofeoBulk (LY) frame in 4096-byte transfers.
        public void TrofeoBulkFrame(byte[] jpegData)
        {
            const int USB_TRANSFER_SIZE = 4096;
            const int SUB_PACKET_SIZE = 512;
            const int SUB_HEADER_SIZE = 16;
            const int SUB_DATA_SIZE = 496;

            int totalChunks = (jpegData.Length + SUB_DATA_SIZE - 1) / SUB_DATA_SIZE;
            int lastChunkDataSize = jpegData.Length % SUB_DATA_SIZE;
            if (lastChunkDataSize == 0) lastChunkDataSize = SUB_DATA_SIZE;
            int paddedChunks = totalChunks;
            int rem = paddedChunks % 4;
            if (rem != 0) paddedChunks += 4 - rem;
            int totalUsbBytes = paddedChunks * SUB_PACKET_SIZE;

            var buffer = ArrayPool<byte>.Shared.Rent(totalUsbBytes);
            try
            {
                Array.Clear(buffer, 0, totalUsbBytes);
                var jpegSizeBytes = BitConverter.GetBytes(jpegData.Length);
                var totalChunksBytes = BitConverter.GetBytes((ushort)totalChunks);

                int jpegOffset = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    int off = i * SUB_PACKET_SIZE;
                    int dataSize = (i == totalChunks - 1) ? lastChunkDataSize : SUB_DATA_SIZE;

                    buffer[off + 0] = 0x01;
                    buffer[off + 1] = 0xFF;
                    jpegSizeBytes.CopyTo(buffer, off + 2);
                    buffer[off + 6] = (byte)(dataSize & 0xFF);
                    buffer[off + 7] = (byte)((dataSize >> 8) & 0xFF);
                    buffer[off + 8] = 0x01; // LY command type
                    totalChunksBytes.CopyTo(buffer, off + 9);
                    buffer[off + 11] = (byte)(i & 0xFF);
                    buffer[off + 12] = (byte)((i >> 8) & 0xFF);
                    Array.Copy(jpegData, jpegOffset, buffer, off + SUB_HEADER_SIZE, dataSize);
                    jpegOffset += dataSize;
                }

                int writeOffset = 0;
                int bytesRemaining = totalUsbBytes;
                while (bytesRemaining > 0)
                {
                    int writeSize = (bytesRemaining >= USB_TRANSFER_SIZE) ? USB_TRANSFER_SIZE : bytesRemaining;
                    Write(buffer, writeOffset, writeSize);
                    writeOffset += USB_TRANSFER_SIZE;
                    bytesRemaining -= USB_TRANSFER_SIZE;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // :1213-1265, a Trofeo frame over bulk, the packets captured.
        public void SendTrofeoFrameOverBulk(byte[] frameData, int width, int height,
            ThermalrightPixelFormat pixelFormat = ThermalrightPixelFormat.Jpeg)
        {
            var header = ArrayPool<byte>.Shared.Rent(TROFEO_PACKET_SIZE);
            try
            {
                Array.Clear(header, 0, TROFEO_PACKET_SIZE);
                Array.Copy(TROFEO_MAGIC_BYTES, 0, header, 0, 4);
                header[4] = 0x02;

                if (pixelFormat is ThermalrightPixelFormat.Rgb565 or ThermalrightPixelFormat.Rgb565BigEndian)
                    header[6] = 0x01;

                BitConverter.GetBytes((ushort)width).CopyTo(header, 8);
                BitConverter.GetBytes((ushort)height).CopyTo(header, 10);
                header[12] = 0x02;
                BitConverter.GetBytes(frameData.Length).CopyTo(header, 16);

                int firstChunkSize = Math.Min(frameData.Length, TROFEO_PACKET_SIZE - TROFEO_HEADER_JPEG_OFFSET);
                Array.Copy(frameData, 0, header, TROFEO_HEADER_JPEG_OFFSET, firstChunkSize);

                Write(header, 0, TROFEO_PACKET_SIZE);

                int offset = firstChunkSize;
                while (offset < frameData.Length)
                {
                    var chunk = ArrayPool<byte>.Shared.Rent(TROFEO_PACKET_SIZE);
                    try
                    {
                        Array.Clear(chunk, 0, TROFEO_PACKET_SIZE);
                        int chunkSize = Math.Min(frameData.Length - offset, TROFEO_PACKET_SIZE);
                        Array.Copy(frameData, offset, chunk, 0, chunkSize);
                        Write(chunk, 0, TROFEO_PACKET_SIZE);
                        offset += chunkSize;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }
        }

        // :1288-1292, LY1's init packet.
        public static byte[] TrofeoBulkLy1Init()
        {
            const int INIT_PACKET_SIZE = 512;
            var initPacket = new byte[INIT_PACKET_SIZE];
            initPacket[0] = 0x02;
            initPacket[1] = 0xFF;
            initPacket[8] = 0x01;
            return initPacket;
        }

        // :1323-1360, LY1's init reply (passed in, `readEc == ErrorCode.None && readBytes > 0` read as readBytes > 0, as
        // TrofeoBulkReply does): only the size it reports is taken (no model change, no crop). Returns whether the header
        // was as expected and the serial.
        public bool TrofeoBulkLy1Reply(byte[] responseBuffer, int readBytes, out string? serialOut)
        {
            bool headerOk = true;
            serialOut = null;
            if (readBytes > 0)
            {
                if (readBytes >= 9)
                {
                    if (responseBuffer[0] != 0x03 || responseBuffer[1] != 0xFF || responseBuffer[8] != 0x01)
                    {
                        headerOk = false;
                    }
                }

                if (readBytes >= 20)
                {
                    var serial = BitConverter.ToString(responseBuffer, 16, 4).Replace("-", "");
                    serialOut = serial;
                }

                if (readBytes >= 32)
                {
                    int reportedWidth = BitConverter.ToInt32(responseBuffer, 24);
                    int reportedHeight = BitConverter.ToInt32(responseBuffer, 28);
                    if (reportedWidth > 0 && reportedWidth <= 4096 && reportedHeight > 0 && reportedHeight <= 4096)
                    {
                        _panelWidth = reportedWidth;
                        _panelHeight = reportedHeight;
                    }
                }
            }
            return headerOk;
        }

        // :1400-1459, one LY1 frame; the write takes all that is left in one transfer.
        public void TrofeoBulkLY1WriteFrame(byte[] jpegData, int subPacketSize, int subHeaderSize, int subDataSize)
        {
            int totalChunks = jpegData.Length / subDataSize + 1;
            int lastChunkDataSize = jpegData.Length % subDataSize;
            if (lastChunkDataSize == 0)
            {
                lastChunkDataSize = subDataSize;
                totalChunks = jpegData.Length / subDataSize;
            }

            int totalBytes = totalChunks * subPacketSize;

            var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                Array.Clear(buffer, 0, totalBytes);
                var jpegSizeBytes = BitConverter.GetBytes(jpegData.Length);
                var totalChunksBytes = BitConverter.GetBytes((ushort)totalChunks);

                int jpegOffset = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    int off = i * subPacketSize;
                    int dataSize = (i == totalChunks - 1) ? lastChunkDataSize : subDataSize;

                    buffer[off + 0] = 0x01;
                    buffer[off + 1] = 0xFF;
                    jpegSizeBytes.CopyTo(buffer, off + 2);
                    buffer[off + 6] = (byte)(dataSize & 0xFF);
                    buffer[off + 7] = (byte)((dataSize >> 8) & 0xFF);
                    buffer[off + 8] = 0x02;
                    totalChunksBytes.CopyTo(buffer, off + 9);
                    buffer[off + 11] = (byte)(i & 0xFF);
                    buffer[off + 12] = (byte)((i >> 8) & 0xFF);

                    Array.Copy(jpegData, jpegOffset, buffer, off + subHeaderSize, dataSize);
                    jpegOffset += dataSize;
                }

                int writeOffset = 0;
                int remaining = totalBytes;
                while (remaining > 0)
                {
                    Write(buffer, writeOffset, remaining);
                    int transferred = remaining;
                    writeOffset += transferred;
                    remaining -= transferred;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // :1477-1481, ALi's init packet.
        public static byte[] AliInit()
        {
            const int INIT_HEADER_SIZE = 16;
            const int INIT_PAYLOAD_SIZE = 1024;
            byte[] initHeader = { 0xF5, 0x00, 0x01, 0x00, 0xBC, 0xFF, 0xB6, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 };
            var initPacket = new byte[INIT_HEADER_SIZE + INIT_PAYLOAD_SIZE];
            Array.Copy(initHeader, 0, initPacket, 0, INIT_HEADER_SIZE);
            return initPacket;
        }

        // :1508-1546, ALi's init reply (passed in): the model and frame size; returns the frame size and the serial.
        public int AliReply(byte[] responseBuffer, int readBytes, out string? serialOut)
        {
            serialOut = null;
            int frameSize = 204800;
            if (readBytes > 0)
            {
                byte deviceType = responseBuffer[0];
                if (deviceType == 54)
                {
                    _panelWidth = 320;
                    _panelHeight = 240;
                    frameSize = 153600;
                    _device.Model = ThermalrightPanelModel.AliVision320x240;
                }
                else
                {
                    _panelWidth = 320;
                    _panelHeight = 320;
                    frameSize = 204800;
                    _device.Model = ThermalrightPanelModel.AliVision320x320;
                }

                if (ThermalrightPanelModelDatabase.Models.TryGetValue(_device.Model, out var aliModel))
                    _detectedModel = aliModel;

                if (readBytes >= 14)
                {
                    var serial = BitConverter.ToString(responseBuffer, 10, 4).Replace("-", "");
                    serialOut = serial;
                }
            }
            return frameSize;
        }

        // :1555-1576, one ALi frame, as 1.4.x has it: the packet is rented uncleared, so a frame shorter than the frame
        // size would send what the pool held (never the case there: frames are always the frame size). Parity vectors
        // use frames of at least the frame size; the port's zero padding of a short frame has its own test.
        public void AliFrame(byte[] frameData, int frameSize)
        {
            const int FRAME_HEADER_SIZE = 16;
            byte[] frameHeader = { 0xF5, 0x01, 0x01, 0x00, 0xBC, 0xFF, 0xB6, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            BitConverter.GetBytes(frameSize).CopyTo(frameHeader, 12);
            int capturedFrameSize = frameSize;

            int totalSize = FRAME_HEADER_SIZE + capturedFrameSize;
            var packet = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                Array.Copy(frameHeader, 0, packet, 0, FRAME_HEADER_SIZE);
                int copySize = Math.Min(frameData.Length, capturedFrameSize);
                Array.Copy(frameData, 0, packet, FRAME_HEADER_SIZE, copySize);
                Write(packet, 0, totalSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        // :1650-1735, the HID init reply (passed in, the report id already stripped as HidPanelDevice.ReadInitResponse
        // does): magic and ACK checks, the model and size, the serial.
        public void HidReply(byte[]? response, out bool magicOk, out bool ackOk, out string? serialOut)
        {
            magicOk = true;
            ackOk = true;
            serialOut = null;
            if (response != null && response.Length >= 4)
            {
                if (response[0] != 0xDA || response[1] != 0xDB || response[2] != 0xDC || response[3] != 0xDD)
                {
                    magicOk = false;
                }
                if (response.Length >= 13 && response[12] != 0x01)
                {
                    ackOk = false;
                }
            }

            if (response != null && response.Length >= 6)
            {
                var pm = response[5];
                var sub = response[4];

                var pmModel = ThermalrightPanelModelDatabase.GetModelByPM(pm, sub);
                if (pmModel != null)
                {
                    _detectedModel = pmModel;
                    _panelWidth = pmModel.RenderWidth;
                    _panelHeight = pmModel.RenderHeight;
                    _device.Model = pmModel.Model;
                }
                else
                {
                    var resolution = ThermalrightPanelModelDatabase.GetResolutionFromPM(pm);
                    if (resolution != null)
                    {
                        _panelWidth = resolution.Value.Width;
                        _panelHeight = resolution.Value.Height;
                    }
                }

                if (response.Length >= 28)
                {
                    var identifierBytes = new byte[Math.Min(8, response.Length - 20)];
                    Array.Copy(response, 20, identifierBytes, 0, identifierBytes.Length);
                    var identifier = new string(System.Text.Encoding.ASCII.GetString(identifierBytes)
                        .Where(c => c >= ' ').ToArray());

                    if (_detectedModel == null)
                    {
                        var identifiedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier);
                        if (identifiedModel != null)
                        {
                            _detectedModel = identifiedModel;
                            _device.Model = identifiedModel.Model;
                        }
                    }
                }

                if (response.Length >= 35 && response[15] == 0x10)
                {
                    var serial = BitConverter.ToString(response, 19, 16).Replace("-", "");
                    serialOut = serial;
                }
            }
        }

        // :1817-1827 (HID: a JPEG model's cap), :1129 (TrofeoBulk) and :1369 (LY1): the JPEG cap a session sets.
        public static int MaxJpegSize(ThermalrightPanelModelInfo model)
        {
            if (model.TransportType == ThermalrightTransportType.Hid)
                return model.PixelFormat == ThermalrightPixelFormat.Jpeg ? 400_000 : 0;
            if (model.TransportType == ThermalrightTransportType.WinUsb)
            {
                if (model.ProtocolType == ThermalrightProtocolType.TrofeoBulk) return 230_000;
                if (model.ProtocolType == ThermalrightProtocolType.TrofeoBulkLY1) return 230_000;
            }
            return 0;
        }
    }
}
