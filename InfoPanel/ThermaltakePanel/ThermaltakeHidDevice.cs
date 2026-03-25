using HidSharp;
using Serilog;
using System;
using System.Linq;
using System.Text;

namespace InfoPanel.ThermaltakePanel
{
    /// <summary>
    /// HID communication for Thermaltake LCD panels (VID 264A).
    /// Protocol: text-based HTTP-like POST commands over 1024-byte HID reports.
    /// The HID descriptor declares NO report IDs, so byte[0] must be 0x00 (null report ID).
    /// </summary>
    public class ThermaltakeHidDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ThermaltakeHidDevice>();

        private const int WIRE_PACKET_SIZE = 1024;
        private const int REPORT_SIZE = WIRE_PACKET_SIZE + 1; // +1 for null report ID
        private const int IMAGE_HEADER_SIZE = 24;
        private const int IMAGE_PAYLOAD_PER_CHUNK = WIRE_PACKET_SIZE - IMAGE_HEADER_SIZE; // 1000

        // Exact handshake packets captured from TT RGB Plus
        private static readonly byte[] CONN_PACKET = HexToBytes(
            "5a0048504f535420636f6e6e20310d0a5365714e756d6265723d3131320d0a" +
            "436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e677468" +
            "3d3234300d0a0d0a075a00");

        private static readonly byte[] REALTIME_ENABLE_PACKET = HexToBytes(
            "5a0061504f5354207265616c74696d65446973706c617920310d0a5365714e" +
            "756d6265723d3131320d0a436f6e74656e74547970653d6a736f6e0d0a436f" +
            "6e74656e744c656e6774683d31350d0a0d0a7b22656e61626c65223a747275" +
            "657d085a00");

        private static readonly byte[] REALTIME_DISABLE_PACKET = HexToBytes(
            "5a0062504f5354207265616c74696d65446973706c617920310d0a5365714e" +
            "756d6265723d3131320d0a436f6e74656e74547970653d6a736f6e0d0a436f" +
            "6e74656e744c656e6774683d31360d0a0d0a7b22656e61626c65223a66616c" +
            "73657d035a00");

        private HidStream? _stream;
        private bool _disposed;

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
            if (!SendPacketAndCheckResponse(CONN_PACKET, "conn"))
                return false;

            // POST realtimeDisplay enable
            if (!SendPacketAndCheckResponse(REALTIME_ENABLE_PACKET, "realtimeDisplay"))
                return false;

            Logger.Information("ThermaltakeHidDevice: Handshake complete");
            return true;
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
                SendPacketAndCheckResponse(REALTIME_DISABLE_PACKET, "realtimeDisplay disable");
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
                    // Response format: [00 report_id] [5A] [flags] [len] [text...]
                    // Extract all printable ASCII from the entire buffer
                    var sb = new StringBuilder();
                    for (int i = 1; i < bytesRead; i++)
                    {
                        byte b = buffer[i];
                        if (b >= 32 && b <= 126)
                            sb.Append((char)b);
                        else if (b == 13 || b == 10)
                            sb.Append((char)b);
                        // Don't break on nulls - response has nulls mixed with text (5A 00 len format)
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

        private static byte[] HexToBytes(string hex)
        {
            return Enumerable.Range(0, hex.Length / 2)
                .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                .ToArray();
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
