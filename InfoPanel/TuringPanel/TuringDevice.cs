using LibUsbDotNet;
using LibUsbDotNet.Main;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Serilog;
using SkiaSharp;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace InfoPanel.TuringPanel
{
    public class TuringDeviceException : Exception
    {
        public TuringDeviceException(string message) : base(message) { }
        public TuringDeviceException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class StorageInfo
    {
        public uint TotalBytes { get; set; }
        public uint UsedBytes { get; set; }
        public uint ValidBytes { get; set; }

        public string FormattedTotal => FormatBytes(TotalBytes);
        public string FormattedUsed => FormatBytes(UsedBytes);
        public string FormattedValid => FormatBytes(ValidBytes);

        private static string FormatBytes(uint bytes)
        {
            if (bytes > 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} GB";
            else
                return $"{bytes / 1024.0:F2} MB";
        }
    }

    public class TuringDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<TuringDevice>();
        
        private const int CMD_PACKET_SIZE = 500;
        private const int FULL_PACKET_SIZE = 512;
        private const int COMMAND_TIMEOUT = 2000;
        private const int MAX_RETRIES = 20;
        private static readonly byte[] DES_KEY_BYTES = Encoding.ASCII.GetBytes("slv3tuzx");
        private static readonly byte[] MAGIC_BYTES = { 161, 26 };

        private readonly BufferedBlockCipher _cipher;
        private string? _ffmpegPath;

        private UsbDevice? _device;
        private UsbEndpointReader? _reader;
        private UsbEndpointWriter? _writer;
        private bool _disposed = false;

        public bool IsConnected => _device != null && !_device.IsOpen == false;

        public TuringDevice(string? ffmpegPath = null)
        {
            _ffmpegPath = ffmpegPath;
            _cipher = new BufferedBlockCipher(new CbcBlockCipher(new DesEngine()));
        }

        public bool Initialize(UsbRegistry usbRegistry)
        {
            Logger.Debug("Initializing Turing Device from registry...");

            try
            {
                _device = usbRegistry.Device;

                if (_device == null)
                {
                    var error = "Failed to open device from registry.";
                    Logger.Error(error);
                    throw new TuringDeviceException(error);
                }

                Logger.Information("Device found from registry.");
                var deviceId = usbRegistry.DeviceProperties["DeviceID"] as string;
                if (!string.IsNullOrEmpty(deviceId))
                    Logger.Debug("Device ID: {DeviceId}", deviceId);

                if (_device is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                _reader = _device.OpenEndpointReader(ReadEndpointID.Ep01);
                _writer = _device.OpenEndpointWriter(WriteEndpointID.Ep01);

                if (_reader == null || _writer == null)
                {
                    var error = "Failed to open USB endpoints.";
                    Logger.Error(error);
                    throw new TuringDeviceException(error);
                }

                Logger.Information("Device initialized successfully.");
                return true;
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = "Error initializing device";
                Logger.Error(ex, error);
                throw new TuringDeviceException(error, ex);
            }
        }

        public byte[] BuildCommandPacketHeader(byte commandId)
        {
            byte[] packet = ArrayPool<byte>.Shared.Rent(CMD_PACKET_SIZE);
            try
            {
                Array.Clear(packet, 0, CMD_PACKET_SIZE);

                packet[0] = commandId;
                packet[2] = 0x1A;
                packet[3] = 0x6D;

                // Optimize timestamp calculation - avoid creating two DateTimeOffset objects
                DateTime today = DateTime.UtcNow.Date;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long dayStart = new DateTimeOffset(today).ToUnixTimeMilliseconds();
                long timestamp = now - dayStart;

                BinaryPrimitives.WriteUInt32LittleEndian(
                    packet.AsSpan(4, sizeof(uint)),
                    unchecked((uint)timestamp));

                // Create a copy to return (since we need to return the rented array)
                byte[] result = new byte[CMD_PACKET_SIZE];
                Buffer.BlockCopy(packet, 0, result, 0, CMD_PACKET_SIZE);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        public byte[] EncryptWithDES(byte[] data)
        {
            KeyParameter keyParam = new KeyParameter(DES_KEY_BYTES);
            _cipher.Init(true, new ParametersWithIV(keyParam, DES_KEY_BYTES));

            int paddedLen = (data.Length + 7) & ~7;    // round up to multiple of 8
            byte[] padded = ArrayPool<byte>.Shared.Rent(paddedLen);
            try
            {
                Array.Clear(padded, 0, paddedLen);     // Ensure padding bytes are zeroed
                data.CopyTo(padded, 0);

                int outputSize = _cipher.GetOutputSize(paddedLen);
                byte[] encrypted = ArrayPool<byte>.Shared.Rent(outputSize);
                try
                {
                    int len = _cipher.ProcessBytes(padded, 0, paddedLen, encrypted, 0);
                    int finalLen = len + _cipher.DoFinal(encrypted, len);

                    // Return only the actual encrypted data
                    byte[] result = new byte[finalLen];
                    Buffer.BlockCopy(encrypted, 0, result, 0, finalLen);
                    return result;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(encrypted);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(padded);
            }
        }

        public byte[] EncryptCommandPacket(byte[] data)
        {
            byte[] encrypted = EncryptWithDES(data);

            byte[] finalPacket = ArrayPool<byte>.Shared.Rent(FULL_PACKET_SIZE);
            Array.Clear(finalPacket, 0, FULL_PACKET_SIZE);

            Buffer.BlockCopy(encrypted, 0, finalPacket, 0, Math.Min(encrypted.Length, FULL_PACKET_SIZE - 2));

            // Add magic bytes at the end
            finalPacket[FULL_PACKET_SIZE - 2] = MAGIC_BYTES[0];  // 161
            finalPacket[FULL_PACKET_SIZE - 1] = MAGIC_BYTES[1];  // 26

            // Create a copy to return (since we need to return the rented array)
            byte[] result = new byte[FULL_PACKET_SIZE];
            Buffer.BlockCopy(finalPacket, 0, result, 0, FULL_PACKET_SIZE);

            ArrayPool<byte>.Shared.Return(finalPacket);
            return result;
        }
        public bool SendSyncCommand()
        {
            Logger.Debug("Sending Sync Command (ID 10)...");

            byte[] cmdPacket = BuildCommandPacketHeader(10);
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            if (!success)
                throw new TuringDeviceException("Failed to send sync command");

            return true;
        }

        public bool SendRestartDeviceCommand()
        {
            Logger.Debug("Sending Restart Command (ID 11)...");

            byte[] cmdPacket = BuildCommandPacketHeader(11);
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            if (!success)
                throw new TuringDeviceException("Failed to send restart command");

            return true;
        }

        public bool SendBrightnessCommand(byte brightness)
        {
            Logger.Debug("Sending Brightness Command (ID 14) with brightness {Brightness}", brightness);

            byte[] cmdPacket = BuildCommandPacketHeader(14);
            cmdPacket[8] = brightness;
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            if (!success)
                throw new TuringDeviceException("Failed to send brightness command");

            return true;
        }

        public bool SendSaveSettingsCommand(byte brightness = 102, byte startup = 0, byte rotation = 0, byte sleep = 0, byte offline = 0)
        {
            Logger.Debug("Sending Save Settings Command (ID 125) with Brightness={Brightness}, StartupMode={Startup}, Rotation={Rotation}, Sleep={Sleep}, Offline={Offline}", 
                brightness, startup, rotation, sleep, offline);

            byte[] cmdPacket = BuildCommandPacketHeader(125);
            cmdPacket[8] = brightness;
            cmdPacket[9] = startup;
            cmdPacket[10] = 0; // reserved
            cmdPacket[11] = rotation;
            cmdPacket[12] = sleep;
            cmdPacket[13] = offline;

            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            if (!success)
                throw new TuringDeviceException("Failed to send save settings command");

            return true;
        }
        public void SendClearImageCommand()
        {
            // Minimal transparent PNG for 480x1920 (copied from Python clear_image)
            byte[] imgData = new byte[] {
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x01, 0xe0, 0x00, 0x00, 0x07, 0x80, 0x08, 0x06, 0x00, 0x00, 0x00, 0x16, 0xf0, 0x84,
                0xf5, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xae, 0xce, 0x1c, 0xe9, 0x00, 0x00,
                0x00, 0x04, 0x67, 0x41, 0x4d, 0x41, 0x00, 0x00, 0xb1, 0x8f, 0x0b, 0xfc, 0x61, 0x05, 0x00, 0x00,
                0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0e, 0xc3, 0x00, 0x00, 0x0e, 0xc3, 0x01, 0xc7,
                0x6f, 0xa8, 0x64, 0x00, 0x00, 0x0e, 0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0x5e, 0xed, 0xc1, 0x01,
                0x0d, 0x00, 0x00, 0x00, 0xc2, 0xa0, 0xf7, 0x4f, 0x6d, 0x0f, 0x07, 0x14, 0x00, 0x00, 0x00, 0x00,
            };
            // Add 3568 zero bytes
            Array.Resize(ref imgData, imgData.Length + 3568);
            // Add PNG end chunk
            byte[] endChunk = new byte[] {
                0x00, 0xf0, 0x66, 0x4a, 0xc8, 0x00, 0x01, 0x11, 0x9d, 0x82, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x49,
                0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
            };
            Array.Resize(ref imgData, imgData.Length + endChunk.Length);
            Array.Copy(endChunk, 0, imgData, imgData.Length - endChunk.Length, endChunk.Length);

            // Use the SendPngBytes method to send the clear image
            SendPngBytes(imgData);
        }
        public bool WriteToDevice(byte[] data, int timeout = 2000)
        {
            return WriteToDevice(data, timeout, out _);
        }
        public bool WriteToDevice(byte[] data, int timeout, out byte[] response)
        {
            response = Array.Empty<byte>();
            if (_writer == null || _reader == null)
                return false;

            try
            {
                // Write the data
                int transferLength = 0;
                ErrorCode ec = _writer.Write(data, timeout, out transferLength);

                if (ec != ErrorCode.None)
                {
                    Logger.Warning("Write Error: {ErrorCode}", ec);
                    return false;
                }

                // Logger.Debug($"Wrote {transferLength} bytes to device.");

                // Read the response with improved error handling
                byte[] readBuffer = new byte[512];
                ec = _reader.Read(readBuffer, timeout, out transferLength);

                // Handle different error conditions
                if (ec == ErrorCode.IoTimedOut)
                {
                    Logger.Warning("USB read operation timed out - device may not be responding");
                    return false;
                }
                else if (ec != ErrorCode.None)
                {
                    Logger.Warning("Read Error: {ErrorCode}", ec);
                    return false;
                }

                if (transferLength > 0)
                {
                    // Logger.Debug($"Read {transferLength} bytes from device");

                    // Copy only the actual data received
                    response = new byte[transferLength];
                    Array.Copy(readBuffer, response, transferLength);

                    // Log the raw response for debugging purposes if length is small
                    if (transferLength <= 32)
                    {
                        Logger.Verbose("Response bytes: {ResponseBytes}", BitConverter.ToString(response));
                    }
                }
                else
                {
                    Logger.Warning("No data received from device");
                    return false;  // Changed: Treat zero-length responses as failures
                }

                // Flush any remaining data from the buffer using native ReadFlush
                // This matches the original implementation (GClass14.cs:148)
                if (_reader != null)
                {
                    _reader.ReadFlush();
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error writing to device");
                return false;
            }
        }
        public void ReadFlush(int maxAttempts = 5)
        {
            if (_reader == null)
                return;

            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    byte[] readBuffer = new byte[512];
                    int transferLength;
                    ErrorCode ec = _reader.Read(readBuffer, 100, out transferLength);  // Short timeout for flushing

                    if (ec == ErrorCode.IoTimedOut || transferLength == 0)
                        break;  // Normal exit condition - no more data to flush

                    if (ec == ErrorCode.None && transferLength > 0)
                    {
                        Logger.Verbose("Flushed {ByteCount} bytes from device buffer", transferLength);
                    }
                    else
                    {
                        // Other error occurred, stop flushing
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error during read flush");
                    break;
                }
            }
        }


        public bool ClearScreen()
        {
            try
            {
                SendClearImageCommand();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to clear screen");
                throw new TuringDeviceException("Failed to clear screen", ex);
            }
        }

        // File listing method with improved return type
        public List<string> ListFiles(string path)
        {
            Logger.Debug("Sending List Storage Command (ID 99) for path: {Path}", path);

            byte[] pathBytes = Encoding.ASCII.GetBytes(path);
            int length = pathBytes.Length;

            byte[] packet = BuildCommandPacketHeader(99);

            packet[8] = (byte)((length >> 24) & 0xFF);
            packet[9] = (byte)((length >> 16) & 0xFF);
            packet[10] = (byte)((length >> 8) & 0xFF);
            packet[11] = (byte)(length & 0xFF);

            for (int i = 12; i < 16; i++)
                packet[i] = 0;

            Buffer.BlockCopy(pathBytes, 0, packet, 16, length);

            byte[] encryptedPacket = EncryptCommandPacket(packet);

            byte[] receiveBuffer = new byte[10240];
            int receiveOffset = 0;

            for (int i = 0; i < MAX_RETRIES; i++)
            {
                byte[] response;
                if (WriteToDevice(encryptedPacket, COMMAND_TIMEOUT, out response))
                {
                    if (response != null && response.Length > 0)
                    {
                        int chunkSize = response.Length;
                        if (receiveOffset + chunkSize <= receiveBuffer.Length)
                        {
                            Buffer.BlockCopy(response, 0, receiveBuffer, receiveOffset, chunkSize);
                            receiveOffset += chunkSize;
                        }
                        else
                        {
                            Logger.Warning("Buffer overflow prevented. Increase buffer size for larger directory listings.");
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (receiveOffset == 0)
            {
                var error = "No data received from device";
                Logger.Debug(error);
                throw new TuringDeviceException(error);
            }

            try
            {
                // Remove null bytes from the response
                string decodedString = Encoding.UTF8.GetString(receiveBuffer, 0, receiveOffset).TrimEnd('\0');
                
                var fileList = new List<string>();
                
                // Check for the expected format "result:dir:file:"
                if (decodedString.Contains("result:dir:file:"))
                {
                    // Extract the file list part after "result:dir:file:"
                    int startIndex = decodedString.IndexOf("result:dir:file:") + "result:dir:file:".Length;
                    if (startIndex < decodedString.Length)
                    {
                        string filesPart = decodedString.Substring(startIndex).TrimEnd('/');
                        string[] filenames = filesPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        
                        foreach (string filename in filenames)
                        {
                            string trimmedFilename = filename.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmedFilename))
                            {
                                fileList.Add(trimmedFilename);
                            }
                        }
                    }
                }
                else if (decodedString.Contains("file:"))
                {
                    // Fallback to old format for compatibility
                    string[] files = decodedString.Split(new string[] { "file:" }, StringSplitOptions.None);
                    if (files.Length > 1)
                    {
                        string[] filenames = files[files.Length - 1].TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        foreach (string filename in filenames)
                        {
                            string trimmedFilename = filename.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmedFilename))
                            {
                                fileList.Add(trimmedFilename);
                            }
                        }
                    }
                }

                if (fileList.Count > 0)
                {
                    Logger.Debug("Files found:");
                    foreach (string file in fileList)
                    {
                        Logger.Debug("  Found file: {FileName}", file);
                    }
                }
                else
                {
                    Logger.Debug("No files found or format unexpected");
                }

                return fileList;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to decode received data");
                throw new TuringDeviceException("Failed to decode received data", ex);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try
                {
                    if (_reader != null)
                    {
                        _reader.Dispose();
                        _reader = null;
                    }

                    if (_writer != null)
                    {
                        _writer.Dispose();
                        _writer = null;
                    }

                    if (_device != null)
                    {
                        if (_device is IUsbDevice wholeUsbDevice)
                        {
                            wholeUsbDevice.ReleaseInterface(0);
                        }

                        _device.Close();
                        _device = null;
                    }

                    UsbDevice.Exit();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error during disposal");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
        public StorageInfo GetStorageInfo()
        {
            Logger.Debug("Sending Refresh Storage Command (ID 100)...");

            byte[] cmdPacket = BuildCommandPacketHeader(100);
            byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);

            byte[] response;
            if (!WriteToDevice(encryptedPacket, COMMAND_TIMEOUT, out response))
            {
                var error = "Invalid or incomplete response from device";
                Logger.Error(error);
                throw new TuringDeviceException(error);
            }

            if (response == null || response.Length < 20)
            {
                var error = "Invalid or incomplete response from device";
                Logger.Error(error);
                throw new TuringDeviceException(error);
            }

            try
            {
                // Validate response status byte (matches GClass14.cs:547 - checks byte 1 == 200)
                // Note: Some devices may use byte 8 instead, so check both
                if (response.Length > 1 && response[1] != 200 && response.Length > 8 && response[8] != 200)
                {
                    Logger.Warning("Storage info response may be invalid - status byte check failed. Response: {Response}",
                        BitConverter.ToString([.. response.Take(20)]));
                }

                uint total = BitConverter.ToUInt32(response, 8);
                uint used = BitConverter.ToUInt32(response, 12);
                uint valid = BitConverter.ToUInt32(response, 16);

                // Additional validation: if all values are 0, the response is likely invalid
                if (total == 0 && used == 0 && valid == 0)
                {
                    var error = "Received invalid storage data (all zeros) - device may not be responding properly";
                    Logger.Warning(error);
                    throw new TuringDeviceException(error);
                }

                var storageInfo = new StorageInfo
                {
                    TotalBytes = total,
                    UsedBytes = used,
                    ValidBytes = valid
                };

                Logger.Information("Storage info: Total={Total}, Used={Used}, Valid={Valid}", 
                    storageInfo.FormattedTotal, storageInfo.FormattedUsed, storageInfo.FormattedValid);

                return storageInfo;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error parsing storage information");
                throw new TuringDeviceException("Error parsing storage information", ex);
            }
        }
        public string ConvertMp4ToH264(string mp4Path)
        {
            string inputPath = Path.GetFullPath(mp4Path);
            string outputPath = inputPath + ".h264"; // Match Python: filename.mp4.h264

            if (File.Exists(outputPath))
            {
                Logger.Debug("{FileName} already exists. Skipping extraction.", Path.GetFileName(outputPath));
                return outputPath;
            }

            string ffmpegPath = _ffmpegPath ?? Path.Combine(Directory.GetCurrentDirectory(), "FFmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                var error = "ffmpeg.exe not found. Please ensure ffmpeg is available.";
                Logger.Debug("Error: {Error}", error);
                throw new TuringDeviceException(error);
            }

            Logger.Information("Extracting H.264 from {FileName}...", Path.GetFileName(inputPath));

            try
            {
                // First try to copy the stream directly (fastest, best quality)
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{inputPath}\" -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -bsf:v h264_mp4toannexb -an -f h264 \"{outputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Logger.Information("Done. Saved as {FileName} (stream copied)", Path.GetFileName(outputPath));
                        return outputPath;
                    }
                }

                var processError = "Failed to start ffmpeg process.";
                Logger.Debug("Error: {Error}", processError);
                throw new TuringDeviceException(processError);
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error running ffmpeg");
                throw new TuringDeviceException("Error running ffmpeg", ex);
            }
        }

        public bool UploadFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var error = $"File '{filePath}' not found.";
                Logger.Debug("Error: {Error}", error);
                throw new TuringDeviceException(error);
            }

            string devicePath;
            string actualFilePath = filePath;
            string extension = Path.GetExtension(filePath).ToLower();

            if (extension == ".png")
            {
                devicePath = $"/tmp/sdcard/mmcblk0p1/img/{Path.GetFileName(filePath)}";
            }
            else if (extension == ".mp4" || extension == ".h264")
            {
                if (extension == ".mp4")
                {
                    actualFilePath = ConvertMp4ToH264(filePath);
                }

                devicePath = $"/tmp/sdcard/mmcblk0p1/video/{Path.GetFileName(actualFilePath)}";
            }
            else
            {
                var error = $"Unsupported file type: {extension}. Supported types: .png, .mp4, .h264";
                Logger.Debug("Error: Unsupported file type: {Extension}", extension);
                Logger.Debug("Supported file types: .png, .mp4, .h264");
                throw new TuringDeviceException(error);
            }

            if (!OpenFileForWriting(devicePath))
            {
                var error = "Failed to open file on device for writing.";
                Logger.Error(error);
                throw new TuringDeviceException(error);
            }

            if (!WriteFileContents(actualFilePath))
            {
                var error = "Failed to write file contents to device.";
                Logger.Error(error);
                throw new TuringDeviceException(error);
            }

            Logger.Information("Upload completed successfully.");
            return true;
        }

        private bool OpenFileForWriting(string devicePath)
        {
            Logger.Debug("Opening file for writing: {DevicePath}", devicePath);

            byte[] pathBytes = Encoding.ASCII.GetBytes(devicePath);
            int length = pathBytes.Length;

            byte[] packet = BuildCommandPacketHeader(38);

            packet[8] = (byte)((length >> 24) & 0xFF);
            packet[9] = (byte)((length >> 16) & 0xFF);
            packet[10] = (byte)((length >> 8) & 0xFF);
            packet[11] = (byte)(length & 0xFF);

            // Zero out bytes 12-15
            for (int i = 12; i < 16; i++)
                packet[i] = 0;

            // Copy the path bytes to the packet starting at position 16
            Buffer.BlockCopy(pathBytes, 0, packet, 16, length);

            return WriteToDevice(EncryptCommandPacket(packet));
        }
        private bool WriteFileContents(string filePath)
        {
            Logger.Debug("Writing file contents from: {FilePath}", filePath);

            const int CHUNK_SIZE = 1048576; // 1MB chunks
            const int HEADER_SIZE = 512;
            const int TOTAL_BUFFER_SIZE = HEADER_SIZE + CHUNK_SIZE;
            long totalSent = 0;
            int lastProgress = -1;

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    long fileSize = fs.Length;
                    byte[] dataBuffer = new byte[CHUNK_SIZE];
                    int bytesRead;

                    while ((bytesRead = fs.Read(dataBuffer, 0, dataBuffer.Length)) > 0)
                    {
                        // Create command packet for this chunk
                        byte[] cmdPacket = BuildCommandPacketHeader(39);

                        // Set chunk size in bytes 8-11 (always CHUNK_SIZE)
                        cmdPacket[8] = (byte)((CHUNK_SIZE >> 24) & 0xFF);
                        cmdPacket[9] = (byte)((CHUNK_SIZE >> 16) & 0xFF);
                        cmdPacket[10] = (byte)((CHUNK_SIZE >> 8) & 0xFF);
                        cmdPacket[11] = (byte)(CHUNK_SIZE & 0xFF);

                        // Set actual bytes read in bytes 12-15
                        cmdPacket[12] = (byte)((bytesRead >> 24) & 0xFF);
                        cmdPacket[13] = (byte)((bytesRead >> 16) & 0xFF);
                        cmdPacket[14] = (byte)((bytesRead >> 8) & 0xFF);
                        cmdPacket[15] = (byte)(bytesRead & 0xFF);

                        // Set last chunk flag in byte 16 if this is the last chunk
                        if (fs.Position == fileSize)
                        {
                            cmdPacket[16] = 1;
                        }

                        // Create buffer matching Python implementation
                        byte[] buffer = new byte[TOTAL_BUFFER_SIZE];
                        Array.Copy(dataBuffer, 0, buffer, 0, bytesRead);

                        // Encrypt the command packet
                        byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);

                        // Combine encrypted packet with buffer data
                        byte[] fullPayload = new byte[encryptedPacket.Length + buffer.Length];
                        Buffer.BlockCopy(encryptedPacket, 0, fullPayload, 0, encryptedPacket.Length);
                        Buffer.BlockCopy(buffer, 0, fullPayload, encryptedPacket.Length, buffer.Length);

                        // Send the chunk
                        if (!WriteToDevice(fullPayload))
                        {
                            Logger.Error("Failed to write chunk to device.");
                            return false;
                        }

                        // Update progress
                        totalSent += bytesRead;
                        int progress = (int)((totalSent * 100) / fileSize);
                        if (progress != lastProgress)
                        {
                            Logger.Verbose("Upload progress: {Progress}%", progress);
                            lastProgress = progress;
                        }
                    }
                }

                Logger.Information("File upload complete.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error writing file contents");
                return false;
            }
        }

        // Play file from device storage
        // Sequence: Clear → Set Brightness → Play command
        // Uses command 110 for H264 videos, command 113 for PNG images (GClass14.cs:532-588)
        public bool PlayFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();

            try
            {
                Logger.Debug("Playing file from device storage: {FilePath}", filePath);

                // 1. Clear screen to remove old content
                try
                {
                    SendClearImageCommand();
                    Logger.Debug("Screen cleared");
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to clear screen, continuing anyway");
                }

                // Small delay for clear command to process
                Thread.Sleep(100);

                // 2. Set brightness to ensure screen is visible (not brightness 0)
                try
                {
                    SendBrightnessCommand(100); // Full brightness for playback
                    Logger.Debug("Brightness set to 100");
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to set brightness, continuing anyway");
                }

                // Small delay for brightness command to process
                Thread.Sleep(100);

                // 3. Send commands 111 and 112 to prepare device for playback
                // These commands may be needed to switch the device into playback mode
                try
                {
                    Logger.Debug("Sending preparation commands (111, 112)");
                    byte[] cmdPacket111 = BuildCommandPacketHeader(111);
                    WriteToDevice(EncryptCommandPacket(cmdPacket111));
                    Thread.Sleep(50);

                    byte[] cmdPacket112 = BuildCommandPacketHeader(112);
                    WriteToDevice(EncryptCommandPacket(cmdPacket112));
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to send preparation commands, continuing anyway");
                }

                // 4. Send play command based on file type
                bool success;
                if (extension == ".h264")
                {
                    // Command 110 for video playback (GClass14.cs:561-588)
                    Logger.Debug("Sending play command (110) for video");
                    success = PlayVideoWithCommand(filePath, 110);
                }
                else if (extension == ".png")
                {
                    // Command 113 for image playback (GClass14.cs:532-559)
                    Logger.Debug("Sending play command (113) for image");
                    success = PlayImageWithCommand(filePath, 113);
                }
                else
                {
                    var error = $"Unsupported file type: {extension}. Supported types: .png, .h264";
                    Logger.Debug("Unsupported file type: {Extension}", extension);
                    throw new TuringDeviceException(error);
                }

                if (!success)
                {
                    throw new TuringDeviceException("Play command failed");
                }

                Logger.Information("File playback initiated successfully");
                return true;
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error playing file");
                throw new TuringDeviceException("Error playing file", ex);
            }
        }

        // Play images specifically
        public bool PlayImage(string filePath)
        {
            return PlayImageWithCommand(filePath, 98); // Command ID 98 for images
        }

        // Play videos specifically
        public bool PlayVideo(string filePath)
        {
            // Python implementation uses play_file3 with ID 113 for specific video playback
            return PlayVideoWithCommand(filePath, 113);
        }

        // Alternative play method for compatibility issues
        public bool PlayFileAlternative(string filePath)
        {
            // Alternative play method (ID 110) if other methods don't work
            string extension = Path.GetExtension(filePath).ToLower();

            if (extension == ".png")
            {
                return PlayImageWithCommand(filePath, 110);
            }
            else if (extension == ".h264")
            {
                return PlayVideoWithCommand(filePath, 110);
            }
            else
            {
                Logger.Debug($"Unsupported file type: {extension}");
                Logger.Debug("Supported file types: .png, .h264");
                return false;
            }
        }
        private bool PlayImageWithCommand(string filePath, byte commandId)
        {
            string devicePath = $"/tmp/sdcard/mmcblk0p1/img/{Path.GetFileName(filePath)}";
            Logger.Debug("Playing image with command ID {CommandId}: {DevicePath}", commandId, devicePath);
            return SendPlayCommand(devicePath, commandId);
        }

        private bool PlayVideoWithCommand(string filePath, byte commandId)
        {
            string devicePath = $"/tmp/sdcard/mmcblk0p1/video/{Path.GetFileName(filePath)}";
            Logger.Debug("Playing video with command ID {CommandId}: {DevicePath}", commandId, devicePath);
            return SendPlayCommand(devicePath, commandId);
        }

        private bool SendPlayCommand(string devicePath, byte commandId)
        {
            Logger.Debug("Sending Play Command (ID {CommandId}) for path: {DevicePath}", commandId, devicePath);

            byte[] pathBytes = Encoding.ASCII.GetBytes(devicePath);
            int length = pathBytes.Length;

            byte[] packet = BuildCommandPacketHeader(commandId);

            packet[8] = (byte)((length >> 24) & 0xFF);
            packet[9] = (byte)((length >> 16) & 0xFF);
            packet[10] = (byte)((length >> 8) & 0xFF);
            packet[11] = (byte)(length & 0xFF);

            // Zero out bytes 12-15
            for (int i = 12; i < 16; i++)
                packet[i] = 0;

            // Copy the path bytes to the packet starting at position 16
            Buffer.BlockCopy(pathBytes, 0, packet, 16, length);

            // Send command and get response
            // Note: Play commands for device storage may not return standard status codes
            byte[] response;
            if (!WriteToDevice(EncryptCommandPacket(packet), COMMAND_TIMEOUT, out response))
            {
                Logger.Warning("Play command {CommandId} failed - no response from device", commandId);
                return false;
            }

            // Log response for debugging, but don't enforce strict validation
            // Play commands may work even without standard success codes
            if (response != null && response.Length > 8)
            {
                Logger.Debug("Play command {CommandId} response: byte[0]={B0}, byte[1]={B1}, byte[8]={B8}",
                    commandId, response[0], response[1], response[8]);
            }
            else if (response != null)
            {
                Logger.Debug("Play command {CommandId} response: length={Length}", commandId, response.Length);
            }

            // Accept any response as success - actual playback errors will show as exceptions
            Logger.Information("Play command {CommandId} sent successfully", commandId);
            return true;
        }
        public bool StopPlay()
        {
            try
            {
                Logger.Debug("Sending Stop Play Commands (ID 111 and 114)");

                // Send first stop command (ID 111)
                byte[] cmdPacket1 = BuildCommandPacketHeader(111);
                bool success1 = WriteToDevice(EncryptCommandPacket(cmdPacket1));

                // Send second stop command (ID 114)
                byte[] cmdPacket2 = BuildCommandPacketHeader(114);
                bool success2 = WriteToDevice(EncryptCommandPacket(cmdPacket2));

                bool success = success1 && success2;
                if (!success)
                    throw new TuringDeviceException("Failed to send stop play commands");

                return true;
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error stopping playback");
                throw new TuringDeviceException("Error stopping playback", ex);
            }
        }
        public bool DeleteFile(string filePath)
        {
            try
            {
                Logger.Information("Deleting file: {FilePath}", filePath);

                string devicePath;
                string extension = Path.GetExtension(filePath).ToLower();

                if (extension == ".png")
                {
                    devicePath = $"/tmp/sdcard/mmcblk0p1/img/{Path.GetFileName(filePath)}";
                }
                else if (extension == ".h264")
                {
                    devicePath = $"/tmp/sdcard/mmcblk0p1/video/{Path.GetFileName(filePath)}";
                }
                else
                {
                    var error = $"Unsupported file type for deletion: {extension}. Supported types: .png, .h264";
                    Logger.Debug("Error: {Error}", error);
                    throw new TuringDeviceException(error);
                }

                byte[] pathBytes = Encoding.ASCII.GetBytes(devicePath);
                int length = pathBytes.Length;

                byte[] packet = BuildCommandPacketHeader(42);

                packet[8] = (byte)((length >> 24) & 0xFF);
                packet[9] = (byte)((length >> 16) & 0xFF);
                packet[10] = (byte)((length >> 8) & 0xFF);
                packet[11] = (byte)(length & 0xFF);

                // Zero out bytes 12-15
                for (int i = 12; i < 16; i++)
                    packet[i] = 0;

                // Copy the path bytes to the packet starting at position 16
                Buffer.BlockCopy(pathBytes, 0, packet, 16, length);

                bool success = WriteToDevice(EncryptCommandPacket(packet));
                if (!success)
                    throw new TuringDeviceException("Failed to delete file");

                return true;
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deleting file");
                throw new TuringDeviceException("Error deleting file", ex);
            }
        }

        public bool SendPngBytes(byte[] pngData)
        {
            int imgSize = pngData.Length;
            byte[] cmdPacket = BuildCommandPacketHeader(102);

            // Set image size in the packet (big-endian)
            cmdPacket[8] = (byte)((imgSize >> 24) & 0xFF);
            cmdPacket[9] = (byte)((imgSize >> 16) & 0xFF);
            cmdPacket[10] = (byte)((imgSize >> 8) & 0xFF);
            cmdPacket[11] = (byte)(imgSize & 0xFF);

            // Encrypt the command packet
            byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);

            // Combine the encrypted packet with the image data
            byte[] fullPayload = new byte[encryptedPacket.Length + pngData.Length];
            Buffer.BlockCopy(encryptedPacket, 0, fullPayload, 0, encryptedPacket.Length);
            Buffer.BlockCopy(pngData, 0, fullPayload, encryptedPacket.Length, pngData.Length);
            // Write the payload to the device
            return WriteToDevice(fullPayload);
        }

        public bool SendImage(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                var error = $"Image file '{imagePath}' not found.";
                Logger.Debug("Error: {Error}", error);
                throw new TuringDeviceException(error);
            }

            try
            {
                Logger.Debug("Loading image: {ImagePath}", imagePath);

                using (SKBitmap bitmap = SKBitmap.Decode(imagePath))
                {
                    if (bitmap == null)
                    {
                        var error = "Failed to load image.";
                        Logger.Debug("Error: {Error}", error);
                        throw new TuringDeviceException(error);
                    }

                    Logger.Debug("Image loaded: {Width}x{Height}", bitmap.Width, bitmap.Height);

                    byte[] pngData = EncodePng(bitmap);
                    if (!SendPngBytes(pngData))
                    {
                        var error = "Failed to send image data to device.";
                        Logger.Debug("Error: {Error}", error);
                        throw new TuringDeviceException(error);
                    }

                    Logger.Information("Image sent successfully.");
                    return true;
                }
            }
            catch (TuringDeviceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error sending image");
                throw new TuringDeviceException("Error sending image", ex);
            }
        }

        public bool ClearImage()
        {
            byte[] imgData = new byte[] {
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x01, 0xe0, 0x00, 0x00, 0x07, 0x80, 0x08, 0x06, 0x00, 0x00, 0x00, 0x16, 0xf0, 0x84,
                0xf5, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xae, 0xce, 0x1c, 0xe9, 0x00, 0x00,
                0x00, 0x04, 0x67, 0x41, 0x4d, 0x41, 0x00, 0x00, 0xb1, 0x8f, 0x0b, 0xfc, 0x61, 0x05, 0x00, 0x00,
                0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0e, 0xc3, 0x00, 0x00, 0x0e, 0xc3, 0x01, 0xc7,
                0x6f, 0xa8, 0x64, 0x00, 0x00, 0x0e, 0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0x5e, 0xed, 0xc1, 0x01,
                0x0d, 0x00, 0x00, 0x00, 0xc2, 0xa0, 0xf7, 0x4f, 0x6d, 0x0f, 0x07, 0x14, 0x00, 0x00, 0x00, 0x00,
            };

            byte[] paddingZeros = new byte[3568];
            for (int i = 0; i < paddingZeros.Length; i++)
            {
                paddingZeros[i] = 0x00;
            }

            byte[] footer = new byte[] {
                0x00, 0xf0, 0x66, 0x4a, 0xc8, 0x00, 0x01, 0x11, 0x9d, 0x82, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x49,
                0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
            };

            byte[] fullImgData = new byte[imgData.Length + paddingZeros.Length + footer.Length];
            Buffer.BlockCopy(imgData, 0, fullImgData, 0, imgData.Length);
            Buffer.BlockCopy(paddingZeros, 0, fullImgData, imgData.Length, paddingZeros.Length);
            Buffer.BlockCopy(footer, 0, fullImgData, imgData.Length + paddingZeros.Length, footer.Length);

            int imgSize = fullImgData.Length;
            Logger.Debug("Sending Clear Image Command (ID 102) - {ImageSize} bytes", imgSize);

            byte[] cmdPacket = BuildCommandPacketHeader(102);
            cmdPacket[8] = (byte)((imgSize >> 24) & 0xFF);
            cmdPacket[9] = (byte)((imgSize >> 16) & 0xFF);
            cmdPacket[10] = (byte)((imgSize >> 8) & 0xFF);
            cmdPacket[11] = (byte)(imgSize & 0xFF);

            byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);
            byte[] fullPayload = new byte[encryptedPacket.Length + fullImgData.Length];
            Buffer.BlockCopy(encryptedPacket, 0, fullPayload, 0, encryptedPacket.Length);
            Buffer.BlockCopy(fullImgData, 0, fullPayload, encryptedPacket.Length, fullImgData.Length);

            bool success = WriteToDevice(fullPayload);
            if (!success)
                throw new TuringDeviceException("Failed to send clear image command to device");

            return true;
        }

        private byte[] EncodePng(SKBitmap bitmap)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                bitmap.Encode(SKEncodedImageFormat.Png, 100).SaveTo(memStream);
                return memStream.ToArray();
            }
        }

    }
}
