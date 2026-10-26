using Serilog;
using System;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.JonsboPanel
{
    /// <summary>
    /// Serial communication for Jonsbo displays (DS916 family, "HLVMAX" firmware).
    /// Protocol (reverse-engineered from the OEM JONSBO-AIO app + USB capture):
    ///   * USB CDC ACM virtual COM port. Line coding is ignored by the firmware —
    ///     the OEM app opens at 9600 baud and streams ~120 KB frames regardless.
    ///   * Handshake: write F0 A5 5A 0F, device replies with a 26-byte ASCII identity:
    ///       "VMAXB160462*1920S261301227"
    ///        [7-char model][width]*[height][serial]
    ///     Resolution is in the panel's NATIVE (portrait) orientation.
    ///   * Frames: one raw JFIF JPEG per frame written straight to the port.
    ///     No envelope, no size header, no checksum, no keep-alive, no terminator.
    ///     JPEG dimensions must match the native portrait resolution.
    /// </summary>
    public sealed partial class JonsboSerialDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<JonsboSerialDevice>();

        private static readonly byte[] HandshakeCommand = [0xF0, 0xA5, 0x5A, 0x0F];

        private const int BaudRate = 9600; // nominal only — CDC bulk ignores it (matches OEM app)
        private const int ReadTimeoutMs = 2000;
        private const int WriteTimeoutMs = 5000;

        private SerialPort? _port;
        private bool _disposed;

        public bool IsOpen => _port?.IsOpen == true;
        public string PortName { get; private set; } = string.Empty;

        public class DeviceIdentity
        {
            public string Model { get; set; } = "";   // 7-char prefix, e.g. "VMAXB16"
            public string Serial { get; set; } = "";  // e.g. "S261301227"
            public int Width { get; set; }            // native portrait width  (462 on DS916)
            public int Height { get; set; }           // native portrait height (1920 on DS916)
            public string Raw { get; set; } = "";
        }

        /// <summary>Opens the COM port. The baud rate is irrelevant to the firmware.</summary>
        public static JonsboSerialDevice? Open(string comPort)
        {
            var dev = new JonsboSerialDevice();
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
                dev._port.DiscardInBuffer();

                Logger.Information("JonsboSerialDevice: Opened {Port}", comPort);
                return dev;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JonsboSerialDevice: Failed to open {Port}", comPort);
                dev.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Sends the F0 A5 5A 0F handshake and parses the ASCII identity reply.
        /// The OEM app retries after 1 s on silence; callers should do the same.
        /// </summary>
        public DeviceIdentity? GetIdentity()
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Port not open");

            _port.DiscardInBuffer();
            _port.Write(HandshakeCommand, 0, HandshakeCommand.Length);

            // Reply observed as a single 26-byte burst ~4 ms after the command.
            // Read until the port goes quiet (or a sane cap) to tolerate length variations.
            var buffer = new byte[64];
            int total = 0;
            try
            {
                int b = _port.ReadByte(); // blocks up to ReadTimeout for the first byte
                if (b < 0) return null;
                buffer[total++] = (byte)b;

                var deadline = DateTime.UtcNow.AddMilliseconds(300);
                while (total < buffer.Length && DateTime.UtcNow < deadline)
                {
                    if (_port.BytesToRead > 0)
                    {
                        int n = _port.Read(buffer, total, Math.Min(_port.BytesToRead, buffer.Length - total));
                        if (n > 0) total += n;
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                }
            }
            catch (TimeoutException)
            {
                Logger.Debug("JonsboSerialDevice: No identity reply from {Port}", PortName);
                return null;
            }

            string raw = Encoding.ASCII.GetString(buffer, 0, total).Trim('\0', '\r', '\n', ' ');
            Logger.Information("JonsboSerialDevice: Identity reply ({Len} bytes): {Raw}", total, raw);

            // "VMAXB16" + "0462*1920" + "S261301227"
            var match = IdentityRegex().Match(raw);
            if (!match.Success)
            {
                Logger.Warning("JonsboSerialDevice: Unrecognized identity format: {Raw}", raw);
                return null;
            }

            return new DeviceIdentity
            {
                Model = match.Groups[1].Value,
                Width = int.Parse(match.Groups[2].Value),
                Height = int.Parse(match.Groups[3].Value),
                Serial = match.Groups[4].Value,
                Raw = raw,
            };
        }

        /// <summary>
        /// Sends one frame: the raw JPEG bytes, nothing else. The JPEG must be encoded at
        /// the panel's native (portrait) resolution.
        /// </summary>
        public void SendJpegFrame(byte[] jpeg)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Port not open");
            _port.Write(jpeg, 0, jpeg.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_port != null)
                {
                    // No close/stop command is known for this firmware; the panel falls back
                    // to its boot animation when frames stop arriving.
                    if (_port.IsOpen)
                    {
                        try { _port.Close(); } catch { }
                    }
                    _port.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "JonsboSerialDevice: Dispose error");
            }
            _port = null;
        }

        [GeneratedRegex(@"^(.{7})(\d{3,4})\*(\d{3,4})(\S*)")]
        private static partial Regex IdentityRegex();
    }
}
