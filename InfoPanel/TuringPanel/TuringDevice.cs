using LibUsbDotNet.Main;
using LibUsbDotNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Buffers;

namespace InfoPanel.TuringPanel
{
    public class TuringDevice : IDisposable
    {
        private const int VENDOR_ID = 0x1cbe;
        private const int PRODUCT_ID = 0x0088;
        private const int CMD_PACKET_SIZE = 500;
        private const int FULL_PACKET_SIZE = 512;
        private static readonly byte[] DES_KEY_BYTES = Encoding.ASCII.GetBytes("slv3tuzx");
        private static readonly byte[] MAGIC_BYTES = [161, 26];
        private readonly BufferedBlockCipher _cipher =
           new(new CbcBlockCipher(new DesEngine()));

        private UsbDevice? _device;
        private UsbEndpointReader? _reader;
        private UsbEndpointWriter? _writer;

        public bool Initialize()
        {
            Trace.WriteLine("Initializing Turing Device...");

            try
            {
                // Find and open the USB device
                UsbDeviceFinder finder = new UsbDeviceFinder(VENDOR_ID, PRODUCT_ID);
                _device = UsbDevice.OpenUsbDevice(finder);

                if (_device == null)
                {
                    Trace.WriteLine("Device not found.");
                    return false;
                }

                Trace.WriteLine("Device found.");
                // If this is a "whole" USB device (like a composite device), 
                // it needs to be properly configured first
                if (_device is IUsbDevice wholeUsbDevice)
                {
                    // Select the first configuration and claim interface 0
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                // Get endpoints for reading and writing
                _reader = _device.OpenEndpointReader(ReadEndpointID.Ep01);
                _writer = _device.OpenEndpointWriter(WriteEndpointID.Ep01);

                if (_reader == null || _writer == null)
                {
                    Console.WriteLine("Failed to open endpoints.");
                    return false;
                }

                Trace.WriteLine("Device initialized.");
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error initializing device: {ex.Message}");
                return false;
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

        public void SendSyncCommand()
        {
            byte[] cmdPacket = BuildCommandPacketHeader(10);
            WriteToDevice(EncryptCommandPacket(cmdPacket));
        }

        public void SendRestartDeviceCommand()
        {
            byte[] cmdPacket = BuildCommandPacketHeader(11);
            WriteToDevice(EncryptCommandPacket(cmdPacket));
        }

        public void SendBrightnessCommand(byte brightness)
        {
            byte[] cmdPacket = BuildCommandPacketHeader(14);
            cmdPacket[8] = brightness;
            WriteToDevice(EncryptCommandPacket(cmdPacket));
        }

        public void SendClearImageCommand()
        {
            // Minimal transparent PNG for 480x1920 (copied from Python clear_image)
            byte[] imgData = [
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x01, 0xe0, 0x00, 0x00, 0x07, 0x80, 0x08, 0x06, 0x00, 0x00, 0x00, 0x16, 0xf0, 0x84,
                0xf5, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xae, 0xce, 0x1c, 0xe9, 0x00, 0x00,
                0x00, 0x04, 0x67, 0x41, 0x4d, 0x41, 0x00, 0x00, 0xb1, 0x8f, 0x0b, 0xfc, 0x61, 0x05, 0x00, 0x00,
                0x00, 0x09, 0x70, 0x48, 0x59, 0x73, 0x00, 0x00, 0x0e, 0xc3, 0x00, 0x00, 0x0e, 0xc3, 0x01, 0xc7,
                0x6f, 0xa8, 0x64, 0x00, 0x00, 0x0e, 0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0x5e, 0xed, 0xc1, 0x01,
                0x0d, 0x00, 0x00, 0x00, 0xc2, 0xa0, 0xf7, 0x4f, 0x6d, 0x0f, 0x07, 0x14, 0x00, 0x00, 0x00, 0x00,
            ];
            // Add 3568 zero bytes
            Array.Resize(ref imgData, imgData.Length + 3568);
            // Add PNG end chunk
            byte[] endChunk = new byte[] {
                0x00, 0xf0, 0x66, 0x4a, 0xc8, 0x00, 0x01, 0x11, 0x9d, 0x82, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x49,
                0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
            };
            Array.Resize(ref imgData, imgData.Length + endChunk.Length);
            Array.Copy(endChunk, 0, imgData, imgData.Length - endChunk.Length, endChunk.Length);

            int imgSize = imgData.Length;
            byte[] cmdPacket = BuildCommandPacketHeader(102);
            cmdPacket[8] = (byte)((imgSize >> 24) & 0xFF);
            cmdPacket[9] = (byte)((imgSize >> 16) & 0xFF);
            cmdPacket[10] = (byte)((imgSize >> 8) & 0xFF);
            cmdPacket[11] = (byte)(imgSize & 0xFF);

            byte[] encryptedPacket = EncryptCommandPacket(cmdPacket);
            byte[] fullPayload = new byte[encryptedPacket.Length + imgData.Length];
            Buffer.BlockCopy(encryptedPacket, 0, fullPayload, 0, encryptedPacket.Length);
            Buffer.BlockCopy(imgData, 0, fullPayload, encryptedPacket.Length, imgData.Length);

            WriteToDevice(fullPayload);
        }

        public bool WriteToDevice(byte[] data, int timeout = 2000)
        {
            if (_writer == null || _reader == null)
                return false;

            try
            {
                // Write the data
                int transferLength = 0;
                ErrorCode ec = _writer.Write(data, timeout, out transferLength);

                if (ec != ErrorCode.None)
                {
                    Console.WriteLine($"Write Error: {ec}");
                    return false;
                }

                Console.WriteLine($"Wrote {transferLength} bytes to device.");

                // Read the response
                byte[] readBuffer = new byte[512];
                ec = _reader.Read(readBuffer, timeout, out transferLength);

                if (ec != ErrorCode.None && ec != ErrorCode.IoTimedOut)
                {
                    Console.WriteLine($"Read Error: {ec}");
                    return false;
                }

                if (transferLength > 0)
                {
                    Console.WriteLine($"Read {transferLength} bytes from device");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to device: {ex.Message}");
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
                    ErrorCode ec = _reader.Read(readBuffer, 200, out int transferLength);

                    if (ec == ErrorCode.IoTimedOut || transferLength == 0)
                        break;

                    Console.WriteLine($"Flushed {transferLength} bytes");
                }
                catch
                {
                    break;
                }
            }
        }

        public void DelaySync()
        {
            SendSyncCommand();
            Thread.Sleep(200);
        }

        public void Dispose()
        {
            // Clean up resources
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
                    // Release interface
                    wholeUsbDevice.ReleaseInterface(0);
                }

                _device.Close();
                _device = null;
            }

            // Force garbage collection to ensure device resources are released
            UsbDevice.Exit();
        }
    }
}
