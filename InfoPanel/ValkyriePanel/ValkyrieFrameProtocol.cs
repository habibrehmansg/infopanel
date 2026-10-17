using System;

namespace InfoPanel.ValkyriePanel
{
    /// <summary>Pure EDID and frame operations shared by the device and protocol tests.</summary>
    public static class ValkyrieFrameProtocol
    {
        public const int FrameLength = ValkyrieV360Protocol.PixelDataLength + 16;

        public static bool IsV360Edid(Func<int, byte> read)
        {
            ReadOnlySpan<byte> header = [0, 255, 255, 255, 255, 255, 255, 0];
            for (int i = 0; i < header.Length; i++)
                if (read(i) != header[i]) return false;
            if ((read(54) | read(55)) == 0) return false;
            int width = read(56) | ((read(58) & 0xF0) << 4);
            int height = read(59) | ((read(61) & 0xF0) << 4);
            return width == ValkyrieV360Protocol.Width && height == ValkyrieV360Protocol.Height;
        }

        public static void PackFrame(ReadOnlySpan<byte> pixels, Span<byte> frame)
        {
            if (pixels.Length != ValkyrieV360Protocol.PixelDataLength || frame.Length != FrameLength)
                throw new ArgumentException("V360 frames require exactly 320x240 UYVY pixels and a 16-byte envelope.");
            ReadOnlySpan<byte> prefix = [0xFF, 0, 0, 0, 0, 0x14, 0, 0xF0];
            ReadOnlySpan<byte> suffix = [0xFF, 0xC0, 0, 0, 0, 0, 0, 0];
            prefix.CopyTo(frame);
            pixels.CopyTo(frame[8..]);
            suffix.CopyTo(frame[^8..]);
        }

        // Rgba8888 -> UYVY422, BT.601 limited range. Brightness is applied in software.
        public static void ConvertRgba(ReadOnlySpan<byte> source, int rowBytes, Span<byte> output, int brightness)
        {
            const int width = ValkyrieV360Protocol.Width, height = ValkyrieV360Protocol.Height;
            if (rowBytes < width * 4 || source.Length < (height - 1) * (long)rowBytes + width * 4
                || output.Length != ValkyrieV360Protocol.PixelDataLength)
                throw new ArgumentException("Invalid 320x240 RGBA frame or UYVY output size.");
            int scale = Math.Clamp(brightness, 0, 100) * 256 / 100;
            int offset = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x += 2)
                {
                    int i = y * rowBytes + x * 4;
                    int r1 = source[i] * scale >> 8, g1 = source[i + 1] * scale >> 8, b1 = source[i + 2] * scale >> 8;
                    int r2 = source[i + 4] * scale >> 8, g2 = source[i + 5] * scale >> 8, b2 = source[i + 6] * scale >> 8;
                    int y1 = (16 << 16) + 16763 * r1 + 32904 * g1 + 6391 * b1;
                    int y2 = (16 << 16) + 16763 * r2 + 32904 * g2 + 6391 * b2;
                    int u = ((128 << 17) - 9676 * (r1 + r2) - 18996 * (g1 + g2) + 28672 * (b1 + b2)) >> 1;
                    int v = ((128 << 17) + 28672 * (r1 + r2) - 24009 * (g1 + g2) - 4663 * (b1 + b2)) >> 1;
                    output[offset++] = (byte)(u >> 16);
                    output[offset++] = (byte)(y1 >> 16);
                    output[offset++] = (byte)(v >> 16);
                    output[offset++] = (byte)(y2 >> 16);
                }
            }
        }
    }
}
