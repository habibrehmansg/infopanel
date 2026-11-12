// V1 oracle, test-only: the protocol code of InfoPanel 1.4.x c71f0f00, InfoPanel/ThermaltakePanel/ThermaltakeHidDevice.cs,
// copied verbatim from the lines cited above each part, but for these changes: log lines are left out; the HID stream's
// write is a capture into Reports; a read's bytes come from Replies (a report and its length, as HidStream.Read gave
// them; none queued is a read that timed out); the Date header's clock is Now (1.4.x read DateTimeOffset.UtcNow); the
// 50 ms sleep before each reply is read is left out (timing, not bytes). No device code, nothing from Windows.
using System.Text;

namespace InfoPanel.Core.Tests.Panels.Thermaltake.V1Oracle
{
    public sealed class V1ThermaltakeHidDevice
    {
        // :19-27
        private const int WIRE_PACKET_SIZE = 1024;
        private const int REPORT_SIZE = WIRE_PACKET_SIZE + 1; // +1 for null report ID
        private const int IMAGE_HEADER_SIZE = 24;
        private const int IMAGE_PAYLOAD_PER_CHUNK = WIRE_PACKET_SIZE - IMAGE_HEADER_SIZE; // 1000

        // Framing: 5A 00 [len] [content] [checksum] 5A 00
        private const byte FRAME_MARKER = 0x5A;
        private const byte FRAME_ZERO = 0x00;
        private const int FRAME_OVERHEAD = 5; // 5A 00 [len] ... [checksum] 5A 00

        // :31
        private int _seqNumber = 100;

        /// <summary>Every report written, in order (the HID stream's writes).</summary>
        public readonly List<byte[]> Reports = new();

        /// <summary>The reads' reports and lengths, in order; an empty queue is a read that timed out.</summary>
        public readonly Queue<(byte[] Buffer, int Read)> Replies = new();

        /// <summary>The Date header's clock, in Unix milliseconds.</summary>
        public Func<long> Now = () => 0;

        // :83-100
        public bool Handshake()
        {
            // POST conn
            var connPacket = BuildCommandPacket("POST conn 1", hasBody: false);
            if (!SendPacketAndCheckResponse(connPacket, "conn"))
                return false;

            // POST realtimeDisplay enable
            var enablePacket = BuildCommandPacket("POST realtimeDisplay 1",
                body: "{\"enable\":true}", contentType: "json");
            if (!SendPacketAndCheckResponse(enablePacket, "realtimeDisplay"))
                return false;

            return true;
        }

        // :115-141
        public void SendJpegFrame(byte[] jpegData)
        {
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

        // :146-158
        public void DisableRealtimeDisplay()
        {
            try
            {
                var disablePacket = BuildCommandPacket("POST realtimeDisplay 1",
                    body: "{\"enable\":false}", contentType: "json");
                SendPacketAndCheckResponse(disablePacket, "realtimeDisplay disable");
            }
            catch (Exception)
            {
            }
        }

        // :163-193, the read's report and length taken from Replies.
        public string? ReadResponse()
        {
            try
            {
                if (Replies.Count == 0)
                    throw new TimeoutException();
                var (buffer, bytesRead) = Replies.Dequeue();

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
            catch (Exception)
            {
            }

            return null;
        }

        // :201-240, the Date from Now.
        public byte[] BuildCommandPacket(string command, string? body = null, string? contentType = null, bool hasBody = true)
        {
            var sb = new StringBuilder();
            sb.Append(command);
            sb.Append("\r\n");
            sb.AppendFormat("SeqNumber={0}\r\n", _seqNumber++);
            sb.AppendFormat("Date={0}\r\n", Now());

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

        // :242-257, without the 50 ms sleep.
        private bool SendPacketAndCheckResponse(byte[] wireData, string commandName)
        {
            WritePacket(wireData);

            var response = ReadResponse();
            if (response != null && response.Contains("200"))
            {
                return true;
            }

            return false;
        }

        // :262-271, the stream's write captured.
        private void WritePacket(byte[] wireData)
        {
            var report = new byte[REPORT_SIZE];
            report[0] = 0x00; // Null report ID (device has no report IDs in descriptor)
            Array.Copy(wireData, 0, report, 1, Math.Min(wireData.Length, WIRE_PACKET_SIZE));

            Reports.Add(report);
        }
    }
}
