using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using Serilog;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace InfoPanel.BeadaPanel
{
    class BeadaPanelParser
    {
        private static readonly ILogger Logger = Log.ForContext<BeadaPanelParser>();
        public static BeadaPanelInfo? ParsePanelInfoResponse(byte[] responseBuffer)
        {
            if (responseBuffer == null || responseBuffer.Length < 100)
            {
                Logger.Warning("Invalid or incomplete response buffer");
                return null;
            }

            // Validate protocol
            string protocol = Encoding.ASCII.GetString(responseBuffer, 0, 11);
            if (protocol != "STATUS-LINK")
            {
                Logger.Warning("Invalid protocol header in BeadaPanel response");
                return null;
            }

            // Validate message type
            byte type = responseBuffer[12];
            if (type != (byte)StatusLinkMessageType.GetPanelInfo)
            {
                Logger.Warning("Unexpected message type in BeadaPanel response: {Type}", type);
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

            if (!serialNumber.IsAlphanumeric())
            {
                serialNumber = string.Empty;
            }

            ushort resX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(70));
            ushort resY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(72));
            uint storageSizeKB = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(74));
            byte maxBrightness = payload[78];
            byte currentBrightness = payload[79];

            if(!Enum.TryParse<BeadaPanelModel>(modelByte.ToString(), out BeadaPanelModel model))
            {
                Log.Warning("Invalid model byte in BeadaPanel response: {ModelByte}", modelByte);
                return null;
            }

            if(!BeadaPanelModelDatabase.Models.TryGetValue(model, out BeadaPanelModelInfo? modelInfo))
            {
                Log.Warning("BeadaPanel model not recognized: {Model}", model);
                return null;
            }

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
                Model = model,
                ModelInfo = modelInfo
            };
        }
    }
}
