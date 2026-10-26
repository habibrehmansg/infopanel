using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InfoPanel.LianLiPanel
{
    internal interface ILianLiUsbScreenDevice : IDisposable
    {
        bool Sync();
        bool StopMedia();
        bool SetBrightness(byte value);
        bool DrawJpeg(byte[] imageBytes);
    }

    /// <summary>
    /// Lian Li's LCD USB protocol expects the encrypted command packet and image
    /// payload as one bulk transfer. Sending them separately can make these panels
    /// accept a frame briefly and then stop responding.
    /// </summary>
    public sealed class LianLiUsbScreenDevice : ILianLiUsbScreenDevice
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiUsbScreenDevice>();
        private static readonly byte[] KeyIv = "slv3tuzx"u8.ToArray();
        private static readonly DateTime AppStartTime = DateTime.Today.AddDays(-1.0);

        private const int CommandSize = 500;
        private const int PacketSize = 512;
        private const int TimeoutMs = 2000;
        private const int ImageAckTimeoutMs = 200;
        private const int MaxImagePayload = 512_000;
        private const byte GetVersionCommand = 10;
        private const byte SetBrightnessCommand = 14;
        private const byte SetFrameRateCommand = 15;
        private const byte SyncClockCommand = 51;
        private const byte StopClockCommand = 52;
        private const byte PushJpegCommand = 101;
        private const byte PushPngCommand = 102;
        private const byte QueryBlockCommand = 122;
        private const byte StopMediaCommand = 123;

        private readonly UsbDevice _usbDevice;
        private readonly UsbEndpointReader _reader;
        private readonly UsbEndpointWriter _writer;
        private readonly DES _des;
        private readonly byte[] _commandBuffer = new byte[CommandSize];
        private readonly byte[] _readBuffer = new byte[PacketSize];
        private bool _disposed;

        public LianLiUsbScreenDevice(UsbDevice usbDevice)
        {
            _usbDevice = usbDevice ?? throw new ArgumentNullException(nameof(usbDevice));

            if (_usbDevice is IUsbDevice wholeUsbDevice)
            {
                wholeUsbDevice.SetConfiguration(1);
                wholeUsbDevice.ClaimInterface(0);
            }

            _reader = _usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);
            _writer = _usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);

            _des = DES.Create();
            _des.Mode = CipherMode.CBC;
            _des.Padding = PaddingMode.PKCS7;
            _des.Key = KeyIv;
            _des.IV = KeyIv;
        }

        public bool Sync() => RequestResponse(GetVersionCommand);

        public bool SetBrightness(byte value)
        {
            PrepareCommandHeader(SetBrightnessCommand);
            _commandBuffer[8] = value;
            return RequestResponsePrepared();
        }

        public bool StopMedia() => RequestResponse(StopMediaCommand);

        public bool SyncClockOnly()
        {
            var now = DateTime.Now;

            PrepareCommandHeader(SyncClockCommand);
            _commandBuffer[8] = (byte)(now.Year >> 8);
            _commandBuffer[9] = (byte)(now.Year & 0xFF);
            _commandBuffer[10] = (byte)now.Month;
            _commandBuffer[11] = (byte)now.Day;
            _commandBuffer[12] = (byte)now.Hour;
            _commandBuffer[13] = (byte)now.Minute;
            _commandBuffer[14] = (byte)now.Second;
            _commandBuffer[15] = 2;

            return RequestResponsePrepared();
        }

        public bool StopClock()
        {
            PrepareCommandHeader(StopClockCommand);
            _commandBuffer[8] = 0;
            return RequestResponsePrepared();
        }

        public bool DrawPngLayer(byte[] imageBytes)
        {
            if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

            return DrawImageLayer(PushPngCommand, imageBytes);
        }

        public bool DrawJpegLayer(byte[] imageBytes)
        {
            if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

            return DrawImageLayer(PushJpegCommand, imageBytes);
        }

        public bool DrawJpeg(byte[] imageBytes)
        {
            if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

            // Main frames must target the JPEG/background layer. The PNG path is
            // used only for clearing the overlay during initialization.
            return DrawJpegLayer(imageBytes);
        }

        /// <summary>Set the panel frame rate. The Linux driver initializes this panel to 30 FPS.</summary>
        public bool SetFrameRate(byte fps)
        {
            PrepareCommandHeader(SetFrameRateCommand);
            _commandBuffer[8] = fps;
            return RequestResponsePrepared();
        }

        /// <summary>
        /// Query the device frame buffer level (cmd 122 / 0x7A).
        /// Returns null when no response could be read.
        /// </summary>
        public byte? QueryBlock()
        {
            PrepareCommandHeader(QueryBlockCommand);
            if (!RequestResponsePrepared(null, out var hasResponse) || !hasResponse)
            {
                return null;
            }

            return _readBuffer[8];
        }

        /// <summary>
        /// Poll QueryBlock every 2ms until the device buffer level drains to
        /// <paramref name="threshold"/> or below. Mirrors the Linux driver's
        /// wait_buffer() (max ~2s).
        /// </summary>
        private void WaitForBufferDrain(byte threshold)
        {
            for (var i = 0; i < 1000; i++)
            {
                var level = QueryBlock();
                if (level == null || level <= threshold)
                {
                    return;
                }

                System.Threading.Thread.Sleep(2);
            }

            Logger.Debug("LianLi buffer drain wait timed out after 2s");
        }

        private bool DrawImageLayer(byte commandId, byte[] imageBytes)
        {
            if (imageBytes.Length > MaxImagePayload)
            {
                Logger.Warning(
                    "LianLi image payload {Length} exceeds device limit {Limit}, dropping frame",
                    imageBytes.Length,
                    MaxImagePayload);
                return false;
            }

            PrepareCommandHeader(commandId);
            BinaryPrimitives.WriteInt32BigEndian(_commandBuffer.AsSpan(8, 4), imageBytes.Length);
            var ok = RequestResponsePrepared(imageBytes, out var hasResponse);

            // Flow control: response byte [8] is the device buffer level. The
            // Linux driver waits for it to drain below 2 whenever it exceeds 3.
            if (ok && hasResponse && _readBuffer[8] > 3)
            {
                WaitForBufferDrain(2);
            }

            return ok;
        }

        private bool RequestResponse(byte commandId)
        {
            PrepareCommandHeader(commandId);
            return RequestResponsePrepared();
        }

        private void PrepareCommandHeader(byte commandId)
        {
            Array.Clear(_commandBuffer, 0, _commandBuffer.Length);
            _commandBuffer[0] = commandId;
            _commandBuffer[2] = 26;
            _commandBuffer[3] = 109;
            var timestamp = unchecked((int)(DateTime.UtcNow - AppStartTime).TotalMilliseconds);
            BinaryPrimitives.WriteInt32LittleEndian(_commandBuffer.AsSpan(4, 4), timestamp);
        }

        private byte[] EncryptCommandPacket()
        {
            var encryptedCommand = _des.EncryptCbc(_commandBuffer, KeyIv, PaddingMode.PKCS7);
            var packet = new byte[PacketSize];
            Buffer.BlockCopy(encryptedCommand, 0, packet, 0, Math.Min(encryptedCommand.Length, PacketSize - 2));
            packet[510] = 161;
            packet[511] = 26;
            return packet;
        }

        private bool RequestResponsePrepared(byte[]? payload = null)
        {
            return RequestResponsePrepared(payload, out _);
        }

        private bool RequestResponsePrepared(byte[]? payload, out bool hasResponse)
        {
            hasResponse = false;
            var commandPacket = EncryptCommandPacket();
            byte[] writeBuffer;

            if (payload == null || payload.Length == 0)
            {
                writeBuffer = commandPacket;
            }
            else
            {
                writeBuffer = new byte[PacketSize + payload.Length];
                Buffer.BlockCopy(commandPacket, 0, writeBuffer, 0, PacketSize);
                Buffer.BlockCopy(payload, 0, writeBuffer, PacketSize, payload.Length);
            }

            try
            {
                _reader.ReadFlush();

                var errorCode = _writer.Write(writeBuffer, TimeoutMs, out var transferred);
                if (errorCode != ErrorCode.None || transferred != writeBuffer.Length)
                {
                    Logger.Warning(
                        "LianLi USB write failed: {ErrorCode}, transferred {Transferred}/{Expected}",
                        errorCode,
                        transferred,
                        writeBuffer.Length);
                    return false;
                }

                Array.Clear(_readBuffer, 0, _readBuffer.Length);
                errorCode = _reader.Read(_readBuffer, payload == null ? TimeoutMs : ImageAckTimeoutMs, out transferred);
                if (payload != null && errorCode == ErrorCode.IoTimedOut)
                {
                    // Lian Li's reference WinUsb.Read ignores the read result and returns
                    // the zero-filled 512-byte buffer even when no response is available.
                    // Image pushes commonly render successfully without a readable ACK.
                    return true;
                }

                if (errorCode != ErrorCode.None || transferred != PacketSize)
                {
                    Logger.Warning(
                        "LianLi USB read failed: {ErrorCode}, transferred {Transferred}/{Expected}",
                        errorCode,
                        transferred,
                        PacketSize);
                    return false;
                }

                hasResponse = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLi USB request failed");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _reader?.Dispose();
                _writer?.Dispose();

                if (_usbDevice != null)
                {
                    if (_usbDevice.IsOpen)
                    {
                        if (_usbDevice is IUsbDevice wholeUsbDevice)
                        {
                            wholeUsbDevice.ReleaseInterface(0);
                            wholeUsbDevice.ResetDevice();
                        }

                        _usbDevice.Close();
                    }
                }

                UsbDevice.Exit();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error during LianLi device disposal");
            }
            finally
            {
                _des.Dispose();
            }
        }
    }
}
