using InfoPanel.BeadaPanel.StatusLink;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace InfoPanel.BeadaPanel
{
    class BeadaPanelParser
    {
        public static BeadaPanelInfo? ParsePanelInfoResponse(byte[] responseBuffer)
        {
            if (responseBuffer == null || responseBuffer.Length < 100)
            {
                Trace.WriteLine("Invalid or incomplete response buffer.");
                return null;
            }

            // Validate protocol
            string protocol = Encoding.ASCII.GetString(responseBuffer, 0, 11);
            if (protocol != "STATUS-LINK")
            {
                Trace.WriteLine("Invalid protocol header.");
                return null;
            }

            // Validate message type
            byte type = responseBuffer[12];
            if (type != (byte)StatusLinkMessageType.GetPanelInfo)
            {
                Trace.WriteLine($"Unexpected message type: {type}");
                return null;
            }

            // Extract payload
            byte[] payload = new byte[80];
            Array.Copy(responseBuffer, 20, payload, 0, 80);

            // Parse fields
            ushort firmwareVersion = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0));
            byte panelLinkVersion = payload[2];
            byte statusLinkVersion = payload[3];
            byte platform = payload[4];
            byte modelByte = payload[5];
            string serialNumber = Encoding.ASCII.GetString(payload, 6, 64).Trim('\0').Trim();
            ushort resX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(70));
            ushort resY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(72));
            uint storageSizeKB = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(74));
            byte maxBrightness = payload[78];
            byte currentBrightness = payload[79];

            BeadaPanelModel? model = Enum.IsDefined(typeof(BeadaPanelModel), modelByte)
                ? (BeadaPanelModel)modelByte
                : null;

            BeadaPanelModelInfo? modelInfo = model.HasValue
                ? BeadaPanelModelDatabase.GetInfo(model.Value)
                : null;

            return new BeadaPanelInfo
            {
                FirmwareVersion = firmwareVersion,
                PanelLinkVersion = panelLinkVersion,
                StatusLinkVersion = statusLinkVersion,
                Platform = platform,
                SerialNumber = serialNumber,
                ResolutionX = resX,
                ResolutionY = resY,
                StorageSizeKB = storageSizeKB,
                MaxBrightness = maxBrightness,
                CurrentBrightness = currentBrightness,
                ModelId = model,
                ModelInfo = modelInfo
            };
        }
    }
}
