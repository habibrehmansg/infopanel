using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel.StatusLink
{
    public class StatusLinkMessage
    {
        private static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("STATUS-LINK");
        private const byte Version = 1;
        private const byte Reserved = 0;
        private const ushort SequenceNumber = 0;

        public StatusLinkMessageType Type { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public byte[] ToBuffer()
        {
            int headerLength = 20;
            int totalLength = headerLength + Payload.Length;
            byte[] buffer = new byte[totalLength];

            // Protocol name
            Array.Copy(ProtocolName, 0, buffer, 0, ProtocolName.Length);

            // Header fields
            buffer[11] = Version;
            buffer[12] = (byte)Type;
            buffer[13] = Reserved;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), SequenceNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), (ushort)totalLength);

            // Checksum placeholder (will be written at 18-19)
            // Payload will be written after header
            if (Payload.Length > 0)
            {
                Array.Copy(Payload, 0, buffer, headerLength, Payload.Length);
            }

            // Calculate checksum over header only (first 18 bytes)
            ushort checksum = CalculateChecksum(buffer, 18);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(18), checksum);

            return buffer;
        }

        private static ushort CalculateChecksum(byte[] buffer, int length)
        {
            uint sum = 0;
            for (int i = 0; i < length; i += 2)
            {
                ushort word = i + 1 < length
                    ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i))
                    : buffer[i]; // odd byte at end
                sum += word;
            }

            // Fold to 16 bits
            sum = (sum >> 16) + (sum & 0xFFFF);
            sum += sum >> 16;
            return (ushort)~sum;
        }
    }
}
