using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel.PanelLink
{
    using System;
    using System.Buffers.Binary;
    using System.Text;

    public class PanelLinkMessage
    {
        private static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("PANEL-LINK");
        private const byte Version = 1;
        private const int HeaderLength = 268;
        private const int TotalLength = 270;

        public PanelLinkMessageType Type { get; set; }
        public byte[] FormatString { get; set; } = [];

        public PanelLinkMessage() { }

        public byte[] ToBuffer()
        {
            byte[] buffer = new byte[TotalLength];

            // Protocol name
            Array.Copy(ProtocolName, 0, buffer, 0, ProtocolName.Length);

            // Version and Type
            buffer[10] = Version;
            buffer[11] = (byte)Type;

            // Only include format string for types 1 and 5
            if (Type == PanelLinkMessageType.LegacyCommand1 || Type == PanelLinkMessageType.StartMediaStream)
            {
                if (FormatString.Length > 256)
                    throw new ArgumentException("Format string too long");

                Array.Copy(FormatString, 0, buffer, 12, FormatString.Length);
            }

            // Checksum over first 268 bytes
            ushort checksum = CalculateChecksum(buffer, HeaderLength);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(HeaderLength), checksum);

            return buffer;
        }

        public static byte[] BuildFormatString(BeadaPanelInfo panelInfo, bool writeThroughMode=true)
        {
            string format = $"image/x-raw, format=BGR16, width={panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX}, height={panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY}, framerate=0/1";

            if (!writeThroughMode)
            {
                format = $"video/x-raw, format=RGB16, width={panelInfo.ResolutionX}, height={panelInfo.ResolutionY}, framerate=0/1";
            }

            return Encoding.ASCII.GetBytes(format);
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
