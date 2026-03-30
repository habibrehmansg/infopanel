using HidSharp;
using Serilog;
using System;
using System.Linq;
using System.Text;

namespace InfoPanel.ThermaltakePanel
{
    /// <summary>
    /// HID communication for Thermaltake LCD panels (VID 264A).
    /// Protocol: text-based HTTP-like POST commands over 1024-byte HID reports with checksum.
    /// Same BY OEM protocol as ASRock panels (VID 26CE).
    /// The HID descriptor declares NO report IDs, so byte[0] must be 0x00 (null report ID).
    /// </summary>
    public class ThermaltakeHidDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ThermaltakeHidDevice>();

        private const int WIRE_PACKET_SIZE = 1024;
        private const int REPORT_SIZE = WIRE_PACKET_SIZE + 1; // +1 for null report ID
        private const int IMAGE_HEADER_SIZE = 24;
        private const int IMAGE_PAYLOAD_PER_CHUNK = WIRE_PACKET_SIZE - IMAGE_HEADER_SIZE; // 1000

        // Framing: 5A 00 [len] [content] [checksum] 5A 00
        private const byte FRAME_MARKER = 0x5A;
        private const byte FRAME_ZERO = 0x00;
        private const int FRAME_OVERHEAD = 5; // 5A 00 [len] ... [checksum] 5A 00

        private HidStream? _stream;
        private bool _disposed;
        private int _seqNumber = 100;

        public bool IsOpen => _stream != null;

        /// <summary>
        /// Opens a Thermaltake LCD HID device by VID/PID.
        /// </summary>
        public static ThermaltakeHidDevice? Open(int vendorId, int productId)
        {
            Logger.Information("ThermaltakeHidDevice: Scanning for VID={Vid:X4} PID={Pid:X4}", vendorId, productId);

            var deviceList = DeviceList.Local;
            var hidDevices = deviceList.GetHidDevices(vendorId, productId).ToList();

            if (hidDevices.Count == 0)
            {
                Logger.Warning("ThermaltakeHidDevice: No HID device found");
                return null;
            }

            foreach (var device in hidDevices)
            {
                try
                {
                    if (device.GetMaxOutputReportLength() < REPORT_SIZE)
                    {
                        Logger.Debug("ThermaltakeHidDevice: Skipping {Path}, MaxOut={MaxOut} < {Required}",
                            device.DevicePath, device.GetMaxOutputReportLength(), REPORT_SIZE);
                        continue;
                    }

                    var stream = device.Open();
                    stream.WriteTimeout = 5000;
                    stream.ReadTimeout = 2000;

                    Logger.Information("ThermaltakeHidDevice: Opened {Path}", device.DevicePath);
                    return new ThermaltakeHidDevice { _stream = stream };
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "ThermaltakeHidDevice: Failed to open {Path}", device.DevicePath);
                }
            }

            Logger.Error("ThermaltakeHidDevice: Could not open any device");
            return null;
        }

        /// <summary>
        /// Performs the full handshake: conn, realtimeDisplay enable.
        /// Returns true if all commands received 200 OK responses.
        /// </summary>
        public bool Handshake()
        {
            Logger.Information("ThermaltakeHidDevice: Starting handshake");

            // POST conn
            var connPacket = BuildCommandPacket("POST conn 1", hasBody: false);
            if (!SendPacketAndCheckResponse(connPacket, "conn"))
                return false;

            // POST realtimeDisplay enable
            var enablePacket = BuildCommandPacket("POST realtimeDisplay 1",
                body: "{\"enable\":true}", contentType: "json");
            if (!SendPacketAndCheckResponse(enablePacket, "realtimeDisplay"))
                return false;

            Logger.Information("ThermaltakeHidDevice: Handshake complete");
            return true;
        }

        /// <summary>
        /// Sets the hardware brightness (0-100).
        /// </summary>
        public bool SetBrightness(int value)
        {
            var packet = BuildCommandPacket("POST brightness 1",
                body: $"{{\"value\":{value}}}", contentType: "json");
            return SendPacketAndCheckResponse(packet, "brightness");
        }

        /// <summary>
        /// Sends a JPEG image frame as chunked image data.
        /// </summary>
        public void SendJpegFrame(byte[] jpegData)
        {
            if (_stream == null) throw new InvalidOperationException("Device not open");

            int totalChunks = (jpegData.Length + IMAGE_PAYLOAD_PER_CHUNK - 1) / IMAGE_PAYLOAD_PER_CHUNK;

            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                int offset = chunk * IMAGE_PAYLOAD_PER_CHUNK;
                int payloadSize = Math.Min(IMAGE_PAYLOAD_PER_CHUNK, jpegData.Length - offset);

                var wireData = new byte[WIRE_PACKET_SIZE];
                wireData[0] = 0x5C;  // Image magic
                wireData[1] = 0x03;
                wireData[2] = 0xFD;  // CMD: image data
                wireData[3] = 0x00;  // area index
                wireData[4] = (byte)((totalChunks >> 8) & 0xFF); // total chunks BE16
                wireData[5] = (byte)(totalChunks & 0xFF);
                wireData[6] = (byte)((chunk >> 8) & 0xFF);       // chunk index BE16
                wireData[7] = (byte)(chunk & 0xFF);
                wireData[8] = 0x01;  // flag
                // bytes 9-23: zeros (already)
                Array.Copy(jpegData, offset, wireData, IMAGE_HEADER_SIZE, payloadSize);

                WritePacket(wireData);
            }
        }

        /// <summary>
        /// Disables realtime display mode (returns LCD to standby).
        /// </summary>
        public void DisableRealtimeDisplay()
        {
            try
            {
                var disablePacket = BuildCommandPacket("POST realtimeDisplay 1",
                    body: "{\"enable\":false}", contentType: "json");
                SendPacketAndCheckResponse(disablePacket, "realtimeDisplay disable");
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ThermaltakeHidDevice: Failed to disable realtime display (device may be disconnected)");
            }
        }

        /// <summary>
        /// Reads a response from the device. Returns the ASCII text portion, or null on timeout.
        /// </summary>
        public string? ReadResponse()
        {
            if (_stream == null) return null;

            try
            {
                var buffer = new byte[REPORT_SIZE];
                int bytesRead = _stream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 4)
                {
                    var sb = new StringBuilder();
                    for (int i = 1; i < bytesRead; i++)
                    {
                        byte b = buffer[i];
                        if (b >= 32 && b <= 126)
                            sb.Append((char)b);
                        else if (b == 13 || b == 10)
                            sb.Append((char)b);
                    }
                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ThermaltakeHidDevice: Read error");
            }

            return null;
        }

        /// <summary>
        /// Builds a framed command packet with SeqNumber, Date, and checksum.
        /// Frame: 5A 00 [len] [content] [checksum] 5A 00
        /// len = content_length + 5
        /// checksum = (len + SUM(content)) mod 256
        /// </summary>
        private byte[] BuildCommandPacket(string command, string? body = null, string? contentType = null, bool hasBody = true)
        {
            var sb = new StringBuilder();
            sb.Append(command);
            sb.Append("\r\n");
            sb.AppendFormat("SeqNumber={0}\r\n", _seqNumber++);
            sb.AppendFormat("Date={0}\r\n", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (hasBody && contentType != null)
            {
                sb.AppendFormat("ContentType={0}\r\n", contentType);
                sb.AppendFormat("ContentLength={0}\r\n", body?.Length ?? 0);
            }

            sb.Append("\r\n");

            if (hasBody && body != null)
                sb.Append(body);

            byte[] content = Encoding.ASCII.GetBytes(sb.ToString());
            byte lenByte = (byte)(content.Length + FRAME_OVERHEAD);

            // Compute checksum: (lenByte + SUM(content)) mod 256
            int sum = lenByte;
            for (int i = 0; i < content.Length; i++)
                sum += content[i];
            byte checksum = (byte)(sum & 0xFF);

            // Build wire data: 5A 00 [len] [content] [checksum] 5A 00
            var wireData = new byte[WIRE_PACKET_SIZE];
            wireData[0] = FRAME_MARKER;
            wireData[1] = FRAME_ZERO;
            wireData[2] = lenByte;
            Array.Copy(content, 0, wireData, 3, content.Length);
            wireData[3 + content.Length] = checksum;
            wireData[3 + content.Length + 1] = FRAME_MARKER;
            wireData[3 + content.Length + 2] = FRAME_ZERO;

            return wireData;
        }

        private bool SendPacketAndCheckResponse(byte[] wireData, string commandName)
        {
            WritePacket(wireData);

            System.Threading.Thread.Sleep(50);

            var response = ReadResponse();
            if (response != null && response.Contains("200"))
            {
                Logger.Information("ThermaltakeHidDevice: {Command} OK", commandName);
                return true;
            }

            Logger.Warning("ThermaltakeHidDevice: {Command} failed, response: {Response}", commandName, response ?? "(none)");
            return false;
        }

        /// <summary>
        /// Writes a 1024-byte packet to the HID device, prepending the 0x00 null report ID.
        /// </summary>
        private void WritePacket(byte[] wireData)
        {
            if (_stream == null) throw new InvalidOperationException("Device not open");

            var report = new byte[REPORT_SIZE];
            report[0] = 0x00; // Null report ID (device has no report IDs in descriptor)
            Array.Copy(wireData, 0, report, 1, Math.Min(wireData.Length, WIRE_PACKET_SIZE));

            _stream.Write(report, 0, report.Length);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { DisableRealtimeDisplay(); } catch { }
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
