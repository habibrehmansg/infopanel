using System.Buffers.Binary;
using System.IO.Compression;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// The two pictures Lian Li's start clears its layers with (1.4's <c>LianLiPanelDeviceTask.cs:87-95,107-112</c>): a
/// transparent PNG and a black JPEG of the panel's size. 1.4 encoded them with SkiaSharp; the package has no image
/// library, so it writes them itself: not the same bytes, the same pictures.
/// </summary>
internal static class SolidImages
{
    /// <summary>A PNG of <paramref name="width"/> × <paramref name="height"/>, eight-bit RGBA, every pixel transparent black.</summary>
    public static byte[] TransparentPng(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits a channel
        header[9] = 6; // RGBA
        Chunk(png, "IHDR"u8, header);

        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width * 4)]; // filter 0, then the row's pixels, all zero
            for (var y = 0; y < height; y++)
                zlib.Write(row);
        }
        Chunk(png, "IDAT"u8, pixels.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// A baseline JPEG of <paramref name="width"/> × <paramref name="height"/>, one grey channel, every pixel black: with
    /// every quantiser 1, each 8 × 8 block's only coefficient is its DC, −1024 (the level −128 shifted), so the first
    /// block codes the difference −1024 and every later one a difference of 0 and the end of its block.
    /// </summary>
    public static byte[] BlackJpeg(int width, int height)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]); // SOI
        jpeg.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]); // JFIF

        jpeg.Write([0xFF, 0xDB, 0x00, 0x43, 0x00]); // DQT: table 0, eight-bit, every quantiser 1
        jpeg.Write(Enumerable.Repeat((byte)1, 64).ToArray());

        jpeg.Write([0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]); // SOF0, one component

        // DC table 0: category 0 is "0", category 11 is "10". AC table 0: only the end of block, "0".
        jpeg.Write([0xFF, 0xC4, 0x00, 0x15, 0x00, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x0B]);
        jpeg.Write([0xFF, 0xC4, 0x00, 0x14, 0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);

        jpeg.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]); // SOS

        var blocks = (long)((width + 7) / 8) * ((height + 7) / 8);
        var bits = new BitWriter(jpeg);
        bits.Put(0b10, 2);             // DC: category 11
        bits.Put(0b01111111111, 11);   // −1024 as eleven bits (−1024 + 2047)
        bits.Put(0, 1);                // AC: end of block
        for (long i = 1; i < blocks; i++)
            bits.Put(0b00, 2);         // DC difference 0, end of block
        bits.Finish();

        jpeg.Write([0xFF, 0xD9]); // EOI
        return jpeg.ToArray();
    }

    private sealed class BitWriter(Stream output)
    {
        private int _byte;
        private int _count;

        public void Put(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _byte = (_byte << 1) | ((value >> i) & 1);
                if (++_count == 8)
                    Emit();
            }
        }

        public void Finish()
        {
            if (_count == 0)
                return;
            for (; _count < 8; _count++)
                _byte = (_byte << 1) | 1; // padded with ones
            Emit();
        }

        private void Emit()
        {
            output.WriteByte((byte)_byte);
            if (_byte == 0xFF)
                output.WriteByte(0x00); // stuffed
            _byte = 0;
            _count = 0;
        }
    }
}
