using LibUsbDotNet;
using LibUsbDotNet.Main;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using SkiaSharp;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace InfoPanel.TuringPanel
{
    public enum TuringDeviceError
    {
        None,
        DeviceNotFound,
        InitializationFailed,
        CommunicationTimeout,
        InvalidResponse,
        FileNotFound,
        UnsupportedFileType,
        UploadFailed,
        ConversionFailed
    }

    public class TuringDeviceConfig
    {
        public int CommandTimeout { get; set; } = 2000;
        public int MaxRetries { get; set; } = 20;
        public int ChunkSize { get; set; } = 1048576; // 1MB
        public bool EnableLogging { get; set; } = true;
        public string? FFmpegPath { get; set; } = null; // Auto-detect if null
    }

    public class TuringDeviceResult<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public TuringDeviceError Error { get; set; }
        public string? ErrorMessage { get; set; }

        public static TuringDeviceResult<T> CreateSuccess(T data)
        {
            return new TuringDeviceResult<T> { Success = true, Data = data, Error = TuringDeviceError.None };
        }

        public static TuringDeviceResult<T> CreateError(TuringDeviceError error, string? message = null)
        {
            return new TuringDeviceResult<T> { Success = false, Error = error, ErrorMessage = message };
        }
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
        private const int VENDOR_ID = 0x1cbe;
        private const int PRODUCT_ID = 0x0088;
        private const int CMD_PACKET_SIZE = 500;
        private const int FULL_PACKET_SIZE = 512;
        private static readonly byte[] DES_KEY_BYTES = Encoding.ASCII.GetBytes("slv3tuzx");
        private static readonly byte[] MAGIC_BYTES = { 161, 26 };

        private readonly BufferedBlockCipher _cipher;
        private readonly TuringDeviceConfig _config;

        private UsbDevice? _device;
        private UsbEndpointReader? _reader;
        private UsbEndpointWriter? _writer;
        private bool _disposed = false;

        public bool IsConnected => _device != null && !_device.IsOpen == false;
        public TuringDeviceConfig Config => _config;

        public TuringDevice() : this(new TuringDeviceConfig())
        {
        }

        public TuringDevice(TuringDeviceConfig config)
        {
            _config = config ?? new TuringDeviceConfig();
            _cipher = new BufferedBlockCipher(new CbcBlockCipher(new DesEngine()));
        }

        public TuringDeviceResult<bool> Initialize()
        {
            if (_config.EnableLogging)
                Debug.WriteLine("Initializing Turing Device...");

            try
            {
                UsbDeviceFinder finder = new UsbDeviceFinder(VENDOR_ID, PRODUCT_ID);
                _device = UsbDevice.OpenUsbDevice(finder);

                if (_device == null)
                {
                    var error = "Device not found. Please ensure the Turing device is connected.";
                    if (_config.EnableLogging)
                        Debug.WriteLine(error);
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.DeviceNotFound, error);
                }

                if (_config.EnableLogging)
                    Debug.WriteLine("Device found.");

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
                    if (_config.EnableLogging)
                        Debug.WriteLine(error);
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.InitializationFailed, error);
                }

                if (_config.EnableLogging)
                    Debug.WriteLine("Device initialized successfully.");
                return TuringDeviceResult<bool>.CreateSuccess(true);
            }
            catch (Exception ex)
            {
                var error = $"Error initializing device: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.InitializationFailed, error);
            }
        }

        public TuringDeviceResult<bool> Initialize(UsbRegistry usbRegistry)
        {
            if (_config.EnableLogging)
                Debug.WriteLine("Initializing Turing Device from registry...");

            try
            {
                _device = usbRegistry.Device;

                if (_device == null)
                {
                    var error = "Failed to open device from registry.";
                    if (_config.EnableLogging)
                        Debug.WriteLine(error);
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.DeviceNotFound, error);
                }

                if (_config.EnableLogging)
                {
                    Debug.WriteLine("Device found from registry.");
                    var deviceId = usbRegistry.DeviceProperties["DeviceID"] as string;
                    if (!string.IsNullOrEmpty(deviceId))
                        Debug.WriteLine($"Device ID: {deviceId}");
                }

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
                    if (_config.EnableLogging)
                        Debug.WriteLine(error);
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.InitializationFailed, error);
                }

                if (_config.EnableLogging)
                    Debug.WriteLine("Device initialized successfully.");
                return TuringDeviceResult<bool>.CreateSuccess(true);
            }
            catch (Exception ex)
            {
                var error = $"Error initializing device: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.InitializationFailed, error);
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
        public TuringDeviceResult<bool> SendSyncCommand()
        {
            if (_config.EnableLogging)
                Debug.WriteLine("Sending Sync Command (ID 10)...");

            byte[] cmdPacket = BuildCommandPacketHeader(10);
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            return success
                ? TuringDeviceResult<bool>.CreateSuccess(true)
                : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to send sync command");
        }

        public TuringDeviceResult<bool> SendRestartDeviceCommand()
        {
            if (_config.EnableLogging)
                Debug.WriteLine("Sending Restart Command (ID 11)...");

            byte[] cmdPacket = BuildCommandPacketHeader(11);
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            return success
                ? TuringDeviceResult<bool>.CreateSuccess(true)
                : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to send restart command");
        }

        public TuringDeviceResult<bool> SendBrightnessCommand(byte brightness)
        {
            if (_config.EnableLogging)
            {
                Debug.WriteLine($"Sending Brightness Command (ID 14)...");
                Debug.WriteLine($"  Brightness = {brightness}");
            }

            byte[] cmdPacket = BuildCommandPacketHeader(14);
            cmdPacket[8] = brightness;
            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            return success
                ? TuringDeviceResult<bool>.CreateSuccess(true)
                : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to send brightness command");
        }

        public TuringDeviceResult<bool> SendSaveSettingsCommand(byte brightness = 102, byte startup = 0, byte rotation = 0, byte sleep = 0, byte offline = 0)
        {
            if (_config.EnableLogging)
            {
                Debug.WriteLine($"Sending Save Settings Command (ID 125)...");
                Debug.WriteLine($"  Brightness:     {brightness}");
                Debug.WriteLine($"  Startup Mode:   {startup}");
                Debug.WriteLine($"  Rotation:       {rotation}");
                Debug.WriteLine($"  Sleep Timeout:  {sleep}");
                Debug.WriteLine($"  Offline Mode:   {offline}");
            }

            byte[] cmdPacket = BuildCommandPacketHeader(125);
            cmdPacket[8] = brightness;
            cmdPacket[9] = startup;
            cmdPacket[10] = 0; // reserved
            cmdPacket[11] = rotation;
            cmdPacket[12] = sleep;
            cmdPacket[13] = offline;

            bool success = WriteToDevice(EncryptCommandPacket(cmdPacket));

            return success
                ? TuringDeviceResult<bool>.CreateSuccess(true)
                : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to send save settings command");
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
                    Debug.WriteLine($"Write Error: {ec}");
                    return false;
                }

                // Debug.WriteLine($"Wrote {transferLength} bytes to device.");

                // Read the response with improved error handling
                byte[] readBuffer = new byte[512];
                ec = _reader.Read(readBuffer, timeout, out transferLength);

                // Handle different error conditions
                if (ec == ErrorCode.IoTimedOut)
                {
                    Debug.WriteLine("USB read operation timed out - device may not be responding");
                    return false;
                }
                else if (ec != ErrorCode.None)
                {
                    Debug.WriteLine($"Read Error: {ec}");
                    return false;
                }

                if (transferLength > 0)
                {
                    // Debug.WriteLine($"Read {transferLength} bytes from device");

                    // Copy only the actual data received
                    response = new byte[transferLength];
                    Array.Copy(readBuffer, response, transferLength);

                    // Log the raw response for debugging purposes if length is small
                    if (transferLength <= 32)
                    {
                        Debug.WriteLine($"Response bytes: {BitConverter.ToString(response)}");
                    }
                }
                else
                {
                    Debug.WriteLine("No data received from device");
                    return false;  // Changed: Treat zero-length responses as failures
                }

                // Flush any remaining data from the buffer
                // This is crucial for reliable communications and matches the Python implementation
                ReadFlush();

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing to device: {ex.Message}");
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
                        Debug.WriteLine($"Flushed {transferLength} bytes from device buffer");
                    }
                    else
                    {
                        // Other error occurred, stop flushing
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during read flush: {ex.Message}");
                    break;
                }
            }
        }

        // Convenience methods
        public void DelaySync()
        {
            SendSyncCommand();
            Thread.Sleep(200);
        }

        public TuringDeviceResult<bool> ClearScreen()
        {
            try
            {
                SendClearImageCommand();
                return TuringDeviceResult<bool>.CreateSuccess(true);
            }
            catch (Exception ex)
            {
                var error = $"Failed to clear screen: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, error);
            }
        }

        // File listing method with improved return type
        public TuringDeviceResult<List<string>> ListFiles(string path)
        {
            if (_config.EnableLogging)
                Debug.WriteLine($"Sending List Storage Command (ID 99) for path: {path}");

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

            for (int i = 0; i < _config.MaxRetries; i++)
            {
                byte[] response;
                if (WriteToDevice(encryptedPacket, _config.CommandTimeout, out response))
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
                            if (_config.EnableLogging)
                                Debug.WriteLine("Buffer overflow prevented. Increase buffer size for larger directory listings.");
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
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<List<string>>.CreateError(TuringDeviceError.InvalidResponse, error);
            }

            try
            {
                string decodedString = Encoding.UTF8.GetString(receiveBuffer, 0, receiveOffset);
                string[] files = decodedString.Split(new string[] { "file:" }, StringSplitOptions.None);

                var fileList = new List<string>();
                if (files.Length > 1)
                {
                    string[] filenames = files[files.Length - 1].TrimEnd('/').Split('/');
                    foreach (string filename in filenames)
                    {
                        if (!string.IsNullOrWhiteSpace(filename))
                        {
                            fileList.Add(filename);
                        }
                    }
                }

                if (_config.EnableLogging)
                {
                    if (fileList.Count > 0)
                    {
                        Debug.WriteLine("Files found:");
                        foreach (string file in fileList)
                        {
                            Debug.WriteLine($"  {file}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("No files found or format unexpected");
                    }
                }

                return TuringDeviceResult<List<string>>.CreateSuccess(fileList);
            }
            catch (Exception ex)
            {
                var error = $"Failed to decode received data: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<List<string>>.CreateError(TuringDeviceError.InvalidResponse, error);
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
                    if (_config.EnableLogging)
                        Debug.WriteLine($"Error during disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
        public TuringDeviceResult<StorageInfo> GetStorageInfo()
        {
            if (_config.EnableLogging)
                Debug.WriteLine("Sending Refresh Storage Command (ID 100)...");

            byte[] cmdPacket = BuildCommandPacketHeader(100);
            byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);

            byte[] response;
            if (!WriteToDevice(encryptedPacket, _config.CommandTimeout, out response))
            {
                var error = "Invalid or incomplete response from device";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<StorageInfo>.CreateError(TuringDeviceError.InvalidResponse, error);
            }

            if (response == null || response.Length < 20)
            {
                var error = "Invalid or incomplete response from device";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<StorageInfo>.CreateError(TuringDeviceError.InvalidResponse, error);
            }

            try
            {
                uint total = BitConverter.ToUInt32(response, 8);
                uint used = BitConverter.ToUInt32(response, 12);
                uint valid = BitConverter.ToUInt32(response, 16);

                var storageInfo = new StorageInfo
                {
                    TotalBytes = total,
                    UsedBytes = used,
                    ValidBytes = valid
                };

                if (_config.EnableLogging)
                {
                    Debug.WriteLine($"  Card Total: {storageInfo.FormattedTotal}");
                    Debug.WriteLine($"  Card Used:  {storageInfo.FormattedUsed}");
                    Debug.WriteLine($"  Card Valid: {storageInfo.FormattedValid}");
                }

                return TuringDeviceResult<StorageInfo>.CreateSuccess(storageInfo);
            }
            catch (Exception ex)
            {
                var error = $"Error parsing storage information: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<StorageInfo>.CreateError(TuringDeviceError.InvalidResponse, error);
            }
        }
        public void SendListStorageCommand(string path)
        {
            Debug.WriteLine($"Sending List Storage Command (ID 99) for path: {path}");

            byte[] pathBytes = Encoding.ASCII.GetBytes(path);
            int length = pathBytes.Length;

            byte[] packet = BuildCommandPacketHeader(99);

            packet[8] = (byte)((length >> 24) & 0xFF);
            packet[9] = (byte)((length >> 16) & 0xFF);
            packet[10] = (byte)((length >> 8) & 0xFF);
            packet[11] = (byte)(length & 0xFF);

            // Zero out bytes 12-15
            for (int i = 12; i < 16; i++)
                packet[i] = 0;

            // Copy the path bytes starting at position 16
            Buffer.BlockCopy(pathBytes, 0, packet, 16, length);

            byte[] encryptedPacket = EncryptCommandPacket(packet);

            // Buffer for receiving chunked responses
            byte[] receiveBuffer = new byte[10240];
            int receiveOffset = 0;
            const int maxTries = 20; // Matching Python implementation

            for (int i = 0; i < maxTries; i++)
            {
                byte[] response;
                if (WriteToDevice(encryptedPacket, 2000, out response))
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
                            Debug.WriteLine("Buffer overflow prevented. Increase buffer size for larger directory listings.");
                            break;
                        }
                    }
                    else
                    {
                        if (i > 0) // Only log warning if we've received some data
                        {
                            Debug.WriteLine($"No response in chunk {i}");
                        }
                        break;
                    }
                }
                else
                {
                    if (i > 0) // Only log warning if we've received some data
                    {
                        Debug.WriteLine($"No response in chunk {i}");
                    }
                    break;
                }
            }

            if (receiveOffset == 0)
            {
                Debug.WriteLine("No data received.");
                return;
            }

            try
            {
                // Decode received data as UTF-8, matching Python implementation
                string decodedString = Encoding.UTF8.GetString(receiveBuffer, 0, receiveOffset);
                string[] files = decodedString.Split(new string[] { "file:" }, StringSplitOptions.None);

                if (files.Length > 1)
                {
                    Debug.WriteLine("Files found:");
                    string[] filenames = files[files.Length - 1].TrimEnd('/').Split('/');
                    foreach (string filename in filenames)
                    {
                        if (!string.IsNullOrWhiteSpace(filename))
                        {
                            Debug.WriteLine($"  {filename}");
                        }
                    }
                }
                else
                {
                    Debug.WriteLine("No files found or format unexpected");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to decode received data: {ex.Message}");
            }
        }
        private string FormatBytes(uint bytes)
        {
            if (bytes > 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} GB";
            else
                return $"{bytes / 1024.0:F2} MB";
        }
        private TuringDeviceResult<string> ConvertMp4ToH264(string mp4Path)
        {
            string inputPath = Path.GetFullPath(mp4Path);
            string outputPath = inputPath + ".h264"; // Match Python: filename.mp4.h264

            if (File.Exists(outputPath))
            {
                if (_config.EnableLogging)
                    Debug.WriteLine($"{Path.GetFileName(outputPath)} already exists. Skipping extraction.");
                return TuringDeviceResult<string>.CreateSuccess(outputPath);
            }

            string ffmpegPath = _config.FFmpegPath ?? Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                var error = "ffmpeg.exe not found. Please ensure ffmpeg is available.";
                if (_config.EnableLogging)
                    Debug.WriteLine($"Error: {error}");
                return TuringDeviceResult<string>.CreateError(TuringDeviceError.ConversionFailed, error);
            }

            if (_config.EnableLogging)
                Debug.WriteLine($"Extracting H.264 from {Path.GetFileName(inputPath)}...");

            try
            {
                // First try to copy the stream directly (fastest, best quality)
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{inputPath}\" -c:v copy -bsf:v h264_mp4toannexb -an -f h264 \"{outputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            if (_config.EnableLogging)
                                Debug.WriteLine($"Done. Saved as {Path.GetFileName(outputPath)} (stream copied)");
                            return TuringDeviceResult<string>.CreateSuccess(outputPath);
                        }
                        else
                        {
                            // If copy failed, try re-encoding with quality settings
                            if (_config.EnableLogging)
                                Debug.WriteLine("Stream copy failed, trying re-encode with quality settings...");

                            if (File.Exists(outputPath))
                                File.Delete(outputPath);

                            startInfo.Arguments = $"-y -i \"{inputPath}\" -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -bsf:v h264_mp4toannexb -an -f h264 \"{outputPath}\"";

                            using (var process2 = Process.Start(startInfo))
                            {
                                if (process2 != null)
                                {
                                    process2.WaitForExit();

                                    if (process2.ExitCode == 0)
                                    {
                                        if (_config.EnableLogging)
                                            Debug.WriteLine($"Done. Saved as {Path.GetFileName(outputPath)} (re-encoded)");
                                        return TuringDeviceResult<string>.CreateSuccess(outputPath);
                                    }
                                    else
                                    {
                                        var error = $"FFmpeg re-encode failed with exit code {process2.ExitCode}";
                                        if (_config.EnableLogging)
                                            Debug.WriteLine(error);
                                        return TuringDeviceResult<string>.CreateError(TuringDeviceError.ConversionFailed, error);
                                    }
                                }
                            }
                        }
                    }

                    var processError = "Failed to start ffmpeg process.";
                    if (_config.EnableLogging)
                        Debug.WriteLine($"Error: {processError}");
                    return TuringDeviceResult<string>.CreateError(TuringDeviceError.ConversionFailed, processError);
                }
            }
            catch (Exception ex)
            {
                var error = $"Error running ffmpeg: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<string>.CreateError(TuringDeviceError.ConversionFailed, error);
            }
        }
        public TuringDeviceResult<bool> UploadFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var error = $"File '{filePath}' not found.";
                if (_config.EnableLogging)
                    Debug.WriteLine($"Error: {error}");
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.FileNotFound, error);
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
                    var conversionResult = ConvertMp4ToH264(filePath);
                    if (!conversionResult.Success)
                    {
                        return TuringDeviceResult<bool>.CreateError(conversionResult.Error, conversionResult.ErrorMessage);
                    }
                    actualFilePath = conversionResult.Data!;
                }

                devicePath = $"/tmp/sdcard/mmcblk0p1/video/{Path.GetFileName(actualFilePath)}";
            }
            else
            {
                var error = $"Unsupported file type: {extension}. Supported types: .png, .mp4, .h264";
                if (_config.EnableLogging)
                {
                    Debug.WriteLine($"Error: Unsupported file type: {extension}");
                    Debug.WriteLine("Supported file types: .png, .mp4, .h264");
                }
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UnsupportedFileType, error);
            }

            if (!OpenFileForWriting(devicePath))
            {
                var error = "Failed to open file on device for writing.";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UploadFailed, error);
            }

            if (!WriteFileContents(actualFilePath))
            {
                var error = "Failed to write file contents to device.";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UploadFailed, error);
            }

            if (_config.EnableLogging)
                Debug.WriteLine("Upload completed successfully.");
            return TuringDeviceResult<bool>.CreateSuccess(true);
        }

        private bool OpenFileForWriting(string devicePath)
        {
            Debug.WriteLine($"Opening file for writing: {devicePath}");

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
            Debug.WriteLine($"Writing file contents from: {filePath}");

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
                            Debug.WriteLine("Failed to write chunk to device.");
                            return false;
                        }

                        // Update progress
                        totalSent += bytesRead;
                        int progress = (int)((totalSent * 100) / fileSize);
                        if (progress != lastProgress)
                        {
                            Debug.WriteLine($"Upload progress: {progress}%");
                            lastProgress = progress;
                        }
                    }
                }

                Debug.WriteLine("File upload complete.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing file contents: {ex.Message}");
                return false;
            }
        }// Updated play methods with better naming and automatic content type detection
        public TuringDeviceResult<bool> PlayFile(string filePath)
        {
            // Automatically select the appropriate play method based on file type
            string extension = Path.GetExtension(filePath).ToLower();

            try
            {
                // Match Python's play-select implementation
                // First, stop any existing playback
                var stopResult = StopPlay();
                if (!stopResult.Success && _config.EnableLogging)
                    Debug.WriteLine($"Warning: Failed to stop playback: {stopResult.ErrorMessage}");

                // Set brightness to 32
                var brightnessResult = SendBrightnessCommand(32);
                if (!brightnessResult.Success && _config.EnableLogging)
                    Debug.WriteLine($"Warning: Failed to set brightness: {brightnessResult.ErrorMessage}");

                if (extension == ".h264")
                {
                    // For H264 files, follow Python's sequence
                    PlayVideoWithCommand(filePath, 98); // First play attempt
                }

                // Send command 111 and 112 sequence
                byte[] cmdPacket111 = BuildCommandPacketHeader(111);
                WriteToDevice(EncryptCommandPacket(cmdPacket111));

                byte[] cmdPacket112 = BuildCommandPacketHeader(112);
                WriteToDevice(EncryptCommandPacket(cmdPacket112));

                // Clear the image
                SendClearImageCommand();

                bool finalSuccess = false;
                if (extension == ".h264")
                {
                    // For H264, use command 110
                    finalSuccess = PlayVideoWithCommand(filePath, 110);
                }
                else if (extension == ".png")
                {
                    // For PNG, use command 113
                    finalSuccess = PlayImageWithCommand(filePath, 113);
                }
                else
                {
                    var error = $"Unsupported file type: {extension}. Supported types: .png, .h264";
                    if (_config.EnableLogging)
                    {
                        Debug.WriteLine($"Unsupported file type: {extension}");
                        Debug.WriteLine("Supported file types: .png, .h264");
                    }
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UnsupportedFileType, error);
                }

                if (_config.EnableLogging)
                    Debug.WriteLine("File playback complete.");

                return finalSuccess
                    ? TuringDeviceResult<bool>.CreateSuccess(true)
                    : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to play file");
            }
            catch (Exception ex)
            {
                var error = $"Error playing file: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, error);
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
                Debug.WriteLine($"Unsupported file type: {extension}");
                Debug.WriteLine("Supported file types: .png, .h264");
                return false;
            }
        }
        private bool PlayImageWithCommand(string filePath, byte commandId)
        {
            string devicePath = $"/tmp/sdcard/mmcblk0p1/img/{Path.GetFileName(filePath)}";
            Debug.WriteLine($"Playing image with command ID {commandId}: {devicePath}");
            return SendPlayCommand(devicePath, commandId);
        }

        private bool PlayVideoWithCommand(string filePath, byte commandId)
        {
            string devicePath = $"/tmp/sdcard/mmcblk0p1/video/{Path.GetFileName(filePath)}";
            Debug.WriteLine($"Playing video with command ID {commandId}: {devicePath}");
            return SendPlayCommand(devicePath, commandId);
        }

        private bool SendPlayCommand(string devicePath, byte commandId)
        {
            Debug.WriteLine($"Sending Play Command (ID {commandId}) for path: {devicePath}");

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

            return WriteToDevice(EncryptCommandPacket(packet));
        }
        public TuringDeviceResult<bool> StopPlay()
        {
            try
            {
                if (_config.EnableLogging)
                    Debug.WriteLine("Sending Stop Play Commands (ID 111 and 114)");

                // Send first stop command (ID 111)
                byte[] cmdPacket1 = BuildCommandPacketHeader(111);
                bool success1 = WriteToDevice(EncryptCommandPacket(cmdPacket1));

                // Send second stop command (ID 114)
                byte[] cmdPacket2 = BuildCommandPacketHeader(114);
                bool success2 = WriteToDevice(EncryptCommandPacket(cmdPacket2));

                bool success = success1 && success2;
                return success
                    ? TuringDeviceResult<bool>.CreateSuccess(true)
                    : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to send stop play commands");
            }
            catch (Exception ex)
            {
                var error = $"Error stopping playback: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, error);
            }
        }
        public TuringDeviceResult<bool> DeleteFile(string filePath)
        {
            try
            {
                if (_config.EnableLogging)
                    Debug.WriteLine($"Deleting file: {filePath}");

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
                    if (_config.EnableLogging)
                    {
                        Debug.WriteLine($"Error: {error}");
                    }
                    return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UnsupportedFileType, error);
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
                return success
                    ? TuringDeviceResult<bool>.CreateSuccess(true)
                    : TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, "Failed to delete file");
            }
            catch (Exception ex)
            {
                var error = $"Error deleting file: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.CommunicationTimeout, error);
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

        public TuringDeviceResult<bool> SendImage(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                var error = $"Image file '{imagePath}' not found.";
                if (_config.EnableLogging)
                    Debug.WriteLine($"Error: {error}");
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.FileNotFound, error);
            }

            try
            {
                if (_config.EnableLogging)
                    Debug.WriteLine($"Loading image: {imagePath}");

                using (SKBitmap bitmap = SKBitmap.Decode(imagePath))
                {
                    if (bitmap == null)
                    {
                        var error = "Failed to load image.";
                        if (_config.EnableLogging)
                            Debug.WriteLine($"Error: {error}");
                        return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UnsupportedFileType, error);
                    }

                    if (_config.EnableLogging)
                        Debug.WriteLine($"Image loaded: {bitmap.Width}x{bitmap.Height}");

                    byte[] pngData = EncodePng(bitmap);
                    if (!SendPngBytes(pngData))
                    {
                        var error = "Failed to send image data to device.";
                        if (_config.EnableLogging)
                            Debug.WriteLine($"Error: {error}");
                        return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UploadFailed, error);
                    }

                    if (_config.EnableLogging)
                        Debug.WriteLine("Image sent successfully.");
                    return TuringDeviceResult<bool>.CreateSuccess(true);
                }
            }
            catch (Exception ex)
            {
                var error = $"Error sending image: {ex.Message}";
                if (_config.EnableLogging)
                    Debug.WriteLine(error);
                return TuringDeviceResult<bool>.CreateError(TuringDeviceError.UploadFailed, error);
            }
        }

        public TuringDeviceResult<bool> ClearImage()
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
            if (_config.EnableLogging)
                Debug.WriteLine($"Sending Clear Image Command (ID 102) - {imgSize} bytes");

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
            return success
                ? TuringDeviceResult<bool>.CreateSuccess(true)
                : TuringDeviceResult<bool>.CreateError(TuringDeviceError.UploadFailed, "Failed to send clear image command to device");
        }

        private byte[] EncodePng(SKBitmap bitmap)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                bitmap.Encode(SKEncodedImageFormat.Png, 100).SaveTo(memStream);
                return memStream.ToArray();
            }
        }

        // Wrapper methods to match Program.cs expectations
        public TuringDeviceResult<bool> RestartDevice()
        {
            return SendRestartDeviceCommand();
        }

        public TuringDeviceResult<bool> SetBrightness(byte brightness)
        {
            return SendBrightnessCommand(brightness);
        }

        public TuringDeviceResult<StorageInfo> RefreshStorageInfo()
        {
            return GetStorageInfo();
        }

        public TuringDeviceResult<List<string>> ListStorageFiles(string type)
        {
            string path = type.ToLower() switch
            {
                "image" => "/tmp/sdcard/mmcblk0p1/img/",
                "video" => "/tmp/sdcard/mmcblk0p1/video/",
                _ => throw new ArgumentException($"Unsupported storage type: {type}")
            };
            return ListFiles(path);
        }

        public TuringDeviceResult<bool> PlayVideoFile(string filePath)
        {
            return PlayFile(filePath);
        }

        public TuringDeviceResult<bool> StopPlayback()
        {
            return StopPlay();
        }
    }
}
