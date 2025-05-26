using System;
using System.Text;

namespace InfoPanel.AX206Panel
{
    public class AX206PanelInfo
    {
        public const int DefaultWidth = 480;
        public const int DefaultHeight = 320;
        public const int DefaultWidthMM = 70;    // Approximate physical size
        public const int DefaultHeightMM = 52;   // Approximate physical size
        
        public int Width { get; init; } = DefaultWidth;
        public int Height { get; init; } = DefaultHeight;
        public int WidthMM { get; init; } = DefaultWidthMM;
        public int HeightMM { get; init; } = DefaultHeightMM;
        public byte MaxBrightness { get; init; } = 7;  // AX206 supports 0-7 brightness levels
        public byte CurrentBrightness { get; init; } = 7;

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== AX206 Panel Info ===");
            sb.AppendLine($"Model              : AX206 3.5\"");
            sb.AppendLine($"Resolution         : {Width} x {Height}");
            sb.AppendLine($"Physical Size (mm) : {WidthMM} x {HeightMM}");
            sb.AppendLine($"Brightness         : {CurrentBrightness} / {MaxBrightness}");

            return sb.ToString();
        }
    }
}