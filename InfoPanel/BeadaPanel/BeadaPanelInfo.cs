using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfoPanel.BeadaPanel
{
    public class BeadaPanelInfo
    {
        public ushort FirmwareVersion { get; init; }
        public byte PanelLinkVersion { get; init; }
        public byte StatusLinkVersion { get; init; }
        public byte Platform { get; init; }
        public string SerialNumber { get; init; } = string.Empty;
        public int ResolutionX { get; init; }
        public int ResolutionY { get; init; }
        public uint StorageSizeKB { get; init; }
        public byte MaxBrightness { get; init; }
        public byte CurrentBrightness { get; init; }
        public BeadaPanelModel? ModelId { get; init; }
        public BeadaPanelModelInfo? ModelInfo { get; init; }

        int DecodeBCD(ushort bcd)
        {
            int hundreds = (bcd >> 8) & 0xF;
            int tens = (bcd >> 4) & 0xF;
            int ones = bcd & 0xF;
            return hundreds * 100 + tens * 10 + ones;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== BeadaPanel Info ===");
            sb.AppendLine($"Firmware Version     : {(int)FirmwareVersion}");
            sb.AppendLine($"PanelLink Version    : {PanelLinkVersion}");
            sb.AppendLine($"StatusLink Version   : {StatusLinkVersion}");
            sb.AppendLine($"Platform             : {Platform}");
            sb.AppendLine($"Model Code           : {(ModelId.HasValue ? ((byte)ModelId.Value).ToString() : "Unknown")}");
            sb.AppendLine($"Model Name           : {ModelInfo?.Name ?? "Unknown"}");
            sb.AppendLine($"Serial Number        : {SerialNumber}");
            sb.AppendLine($"Reported Resolution  : {ResolutionX} x {ResolutionY}");
            sb.AppendLine($"Storage Size         : {StorageSizeKB} KB");
            sb.AppendLine($"Brightness           : {CurrentBrightness} / {MaxBrightness}");

            if (ModelInfo != null)
            {
                sb.AppendLine($"Physical Size (mm)   : {ModelInfo.WidthMM} x {ModelInfo.HeightMM}");
                sb.AppendLine($"Native Resolution    : {ModelInfo.Width} x {ModelInfo.Height}");
            }

            return sb.ToString();
        }
    }
}
