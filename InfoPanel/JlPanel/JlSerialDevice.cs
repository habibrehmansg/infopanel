using Serilog;
using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace InfoPanel.JlPanel
{
    /// <summary>
    /// Serial communication for Jungle Leopard / Hongtai cooler displays.
    /// Protocol (reverse-engineered from `Jungle Leopard Display.exe`):
    ///   * USB CDC ACM at 2 000 000 baud, no flow control.
    ///   * Frame: 0x55 0xAA [len_LE16] [cmd] [payload] [chk_LE16]
    ///     - len = total frame size (= payload length + 7)
    ///     - chk = u16 sum of all preceding bytes (NOT a CRC), little-endian.
    ///   * Live JPEG envelope (firmware >= 2.8):
    ///     [size_LE32] [JPEG bytes] [sum_LE16]
    ///     - sum = u16 sum of size_field + JPEG bytes.
    ///   * Frame terminator: FF D9 FF D9 (two JPEG EOI markers back-to-back).
    ///   * Pipeline reset: FF D9 FF D9 00 00 00 00.
    ///   * Keep-alive: send cmd 0x11 (refresh) at least every 1500 ms.
    /// </summary>
    public sealed class JlSerialDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<JlSerialDevice>();

        public const byte CMD_RESTART          = 0x01;
        public const byte CMD_SET_LIGHT        = 0x03;
        public const byte CMD_GET_DEVICE_INFO  = 0x06;
        public const byte CMD_KEEP_ALIVE       = 0x11; // "live frame ack" / refresh
        public const byte CMD_SET_REGION       = 0x20;
        public const byte CMD_CLOSE            = 0x21;

        private const int BaudRate = 2_000_000;
        private const int ReadTimeoutMs = 2000;
        private const int WriteTimeoutMs = 5000;

        private static readonly byte[] PipelineReset =
            [0xFF, 0xD9, 0xFF, 0xD9, 0x00, 0x00, 0x00, 0x00];

        private static readonly byte[] FrameTerminator =
            [0xFF, 0xD9, 0xFF, 0xD9];

        private SerialPort? _port;
        private bool _disposed;

        public bool IsOpen => _port?.IsOpen == true;
        public string PortName { get; private set; } = string.Empty;

        public class DeviceInfo
        {
            public int Status { get; set; }
            public string? Uid { get; set; }
            public string? Model { get; set; }
            public string? Version { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Angle { get; set; }
            public string? Region { get; set; }
        }

        /// <summary>
        /// Opens the COM port at 2 Mbaud and performs the device-clear handshake.
        /// </summary>
        public static JlSerialDevice? Open(string comPort)
        {
            var dev = new JlSerialDevice();
            try
            {
                dev._port = new SerialPort(comPort, BaudRate)
                {
                    ReadTimeout = ReadTimeoutMs,
                    WriteTimeout = WriteTimeoutMs,
                    DtrEnable = true,
                    RtsEnable = true,
                    Handshake = Handshake.None,
                };
                dev._port.Open();
                dev.PortName = comPort;

                // Pipeline reset: flush any pending frame state before we start talking.
                System.Threading.Thread.Sleep(200);
                dev._port.Write(PipelineReset, 0, PipelineReset.Length);
                System.Threading.Thread.Sleep(200);
                dev._port.DiscardInBuffer();

                Logger.Information("JlSerialDevice: Opened {Port} @ {Baud}", comPort, BaudRate);
                return dev;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JlSerialDevice: Failed to open {Port}", comPort);
                dev.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Builds a command frame: 0x55 0xAA [len_LE16] [cmd] [payload] [chk_LE16].
        /// </summary>
        public static byte[] BuildFrame(byte cmd, ReadOnlySpan<byte> payload)
        {
            int totalLen = 7 + payload.Length; // magic(2) + len(2) + cmd(1) + payload + chk(2)
            var frame = new byte[totalLen];
            frame[0] = 0x55;
            frame[1] = 0xAA;
            frame[2] = (byte)(totalLen & 0xFF);
            frame[3] = (byte)((totalLen >> 8) & 0xFF);
            frame[4] = cmd;
            payload.CopyTo(frame.AsSpan(5));

            ushort sum = 0;
            for (int i = 0; i < totalLen - 2; i++) sum += frame[i];
            frame[totalLen - 2] = (byte)(sum & 0xFF);
            frame[totalLen - 1] = (byte)((sum >> 8) & 0xFF);
            return frame;
        }

        /// <summary>Sends a framed command and discards any response.</summary>
        public void SendCommand(byte cmd, ReadOnlySpan<byte> payload = default)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Port not open");
            var frame = BuildFrame(cmd, payload);
            _port.Write(frame, 0, frame.Length);
        }

        /// <summary>
        /// Sends a framed command and reads back one response frame.
        /// Returns the unwrapped payload bytes (excluding magic, length, cmd, checksum), or null on timeout.
        /// </summary>
        public byte[]? SendCommandAndReadResponse(byte cmd, ReadOnlySpan<byte> payload = default)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Port not open");
            SendCommand(cmd, payload);
            return ReadFrame();
        }

        /// <summary>Reads one response frame and returns the payload bytes (without magic/length/cmd/checksum).</summary>
        public byte[]? ReadFrame()
        {
            if (_port == null || !_port.IsOpen) return null;
            try
            {
                // Sync to magic 0x55 0xAA
                int b1 = _port.ReadByte();
                while (b1 != 0x55)
                {
                    if (b1 < 0) return null;
                    b1 = _port.ReadByte();
                }
                int b2 = _port.ReadByte();
                if (b2 != 0xAA) return null;

                int lenLo = _port.ReadByte();
                int lenHi = _port.ReadByte();
                int totalLen = (lenHi << 8) | lenLo;
                if (totalLen < 7 || totalLen > 64 * 1024) return null;

                int payloadLen = totalLen - 7;
                int cmd = _port.ReadByte();
                _ = cmd; // not needed by callers

                var payload = new byte[payloadLen];
                int read = 0;
                while (read < payloadLen)
                {
                    int n = _port.Read(payload, read, payloadLen - read);
                    if (n <= 0) return null;
                    read += n;
                }

                // Discard checksum bytes (we don't validate response checksums)
                _ = _port.ReadByte();
                _ = _port.ReadByte();

                return payload;
            }
            catch (TimeoutException) { return null; }
            catch (Exception ex)
            {
                Logger.Debug(ex, "JlSerialDevice: ReadFrame error");
                return null;
            }
        }

        /// <summary>
        /// Calls cmd 0x06 (getDeviceInfo). The response payload is JSON.
        /// </summary>
        public DeviceInfo? GetDeviceInfo()
        {
            var payload = SendCommandAndReadResponse(CMD_GET_DEVICE_INFO);
            if (payload == null || payload.Length < 5) return null;

            try
            {
                string json = Encoding.UTF8.GetString(payload).Trim('\0');
                var info = JsonSerializer.Deserialize<DeviceInfo>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return info;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JlSerialDevice: Failed to parse getDeviceInfo response");
                return null;
            }
        }

        /// <summary>Sends the 0x11 keep-alive (live frame ack).</summary>
        public void SendKeepAlive() => SendCommand(CMD_KEEP_ALIVE);

        /// <summary>Sets backlight brightness (0..100).</summary>
        public void SetBrightness(int brightness)
        {
            byte clamped = (byte)Math.Clamp(brightness, 0, 100);
            SendCommand(CMD_SET_LIGHT, [clamped]);
        }

        /// <summary>
        /// Sends one live JPEG frame using the v2.8+ envelope:
        ///   [size_LE32] [JPEG bytes] [sum_LE16]   followed by FF D9 FF D9 terminator.
        /// </summary>
        public void SendJpegFrame(byte[] jpeg)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Port not open");

            // Refresh the live pipeline before each frame.
            SendKeepAlive();

            int envelopeSize = 4 + jpeg.Length + 2;
            var envelope = new byte[envelopeSize];

            // Size field (LE32) covers JPEG bytes only.
            envelope[0] = (byte)(jpeg.Length & 0xFF);
            envelope[1] = (byte)((jpeg.Length >> 8) & 0xFF);
            envelope[2] = (byte)((jpeg.Length >> 16) & 0xFF);
            envelope[3] = (byte)((jpeg.Length >> 24) & 0xFF);

            Buffer.BlockCopy(jpeg, 0, envelope, 4, jpeg.Length);

            // Sum: size_field bytes + JPEG bytes.
            ushort sum = 0;
            for (int i = 0; i < 4 + jpeg.Length; i++) sum += envelope[i];
            envelope[envelopeSize - 2] = (byte)(sum & 0xFF);
            envelope[envelopeSize - 1] = (byte)((sum >> 8) & 0xFF);

            _port.Write(envelope, 0, envelope.Length);
            // Frame terminator (two JPEG EOI markers back-to-back).
            _port.Write(FrameTerminator, 0, FrameTerminator.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_port != null)
                {
                    if (_port.IsOpen)
                    {
                        try { SendCommand(CMD_CLOSE); } catch { /* best effort */ }
                        try { _port.Close(); } catch { }
                    }
                    _port.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "JlSerialDevice: Dispose error");
            }
            _port = null;
        }
    }
}
