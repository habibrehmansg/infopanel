// V1 oracle, test-only: the packet code of InfoPanel 1.4.x c71f0f00, InfoPanel/ThermalrightPanel/HidPanelDevice.cs, copied
// verbatim from the lines cited above each part, but for these changes: log lines are left out; the HID stream's write is
// a capture into Reports; a read's bytes are passed in; the RGB565 frame's 1 ms pause every 32 packets is left out
// (timing, not bytes).
namespace InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle
{
    public sealed class V1HidPanelDevice
    {
        // :18-20
        private static readonly byte[] MAGIC_BYTES = { 0xDA, 0xDB, 0xDC, 0xDD };
        private const int PACKET_SIZE = 512;
        private const int HEADER_JPEG_OFFSET = 20;

        public readonly List<byte[]> Reports = new();

        // :73-95
        public bool SendInit()
        {
            var packet = new byte[PACKET_SIZE];
            Array.Copy(MAGIC_BYTES, 0, packet, 0, 4);
            packet[12] = 0x01;
            WritePacket(packet);
            return true;
        }

        // :101-130, with the read's buffer and length passed in.
        public static byte[]? ReadInitResponse(byte[] buffer, int bytesRead)
        {
            if (bytesRead > 0)
            {
                var response = new byte[bytesRead - 1];
                Array.Copy(buffer, 1, response, 0, response.Length);
                return response;
            }
            return null;
        }

        // :137-190
        public bool SendJpegFrame(byte[] jpegData, int width, int height)
        {
            var header = new byte[PACKET_SIZE];
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);
            header[4] = 0x02;
            BitConverter.GetBytes((ushort)width).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)height).CopyTo(header, 10);
            header[12] = 0x02;
            BitConverter.GetBytes(jpegData.Length).CopyTo(header, 16);
            int firstChunkSize = Math.Min(jpegData.Length, PACKET_SIZE - HEADER_JPEG_OFFSET);
            Array.Copy(jpegData, 0, header, HEADER_JPEG_OFFSET, firstChunkSize);
            WritePacket(header);

            int offset = firstChunkSize;
            while (offset < jpegData.Length)
            {
                var chunk = new byte[PACKET_SIZE];
                int chunkSize = Math.Min(jpegData.Length - offset, PACKET_SIZE);
                Array.Copy(jpegData, offset, chunk, 0, chunkSize);
                WritePacket(chunk);
                offset += chunkSize;
            }
            return true;
        }

        // :197-245
        public bool SendRgb565Frame(byte[] rgb565Data, int width, int height)
        {
            var header = new byte[PACKET_SIZE];
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);
            header[4] = 0x02;
            header[6] = 0x01;
            BitConverter.GetBytes((ushort)width).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)height).CopyTo(header, 10);
            header[12] = 0x02;
            BitConverter.GetBytes(rgb565Data.Length).CopyTo(header, 16);
            int firstChunkSize = Math.Min(rgb565Data.Length, PACKET_SIZE - HEADER_JPEG_OFFSET);
            Array.Copy(rgb565Data, 0, header, HEADER_JPEG_OFFSET, firstChunkSize);
            WritePacket(header);

            int offset = firstChunkSize;
            while (offset < rgb565Data.Length)
            {
                var chunk = new byte[PACKET_SIZE];
                int chunkSize = Math.Min(rgb565Data.Length - offset, PACKET_SIZE);
                Array.Copy(rgb565Data, offset, chunk, 0, chunkSize);
                WritePacket(chunk);
                offset += chunkSize;
            }
            return true;
        }

        // :250-260
        private void WritePacket(byte[] data)
        {
            var report = new byte[data.Length + 1];
            report[0] = 0x00;
            Array.Copy(data, 0, report, 1, data.Length);
            Reports.Add(report);
        }
    }
}
