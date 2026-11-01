using InfoPanel.Preview.Video;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>RGBA to NV12: BT.709, limited range, odd sides and row padding black, into the layout an encoder negotiated.</summary>
public class Nv12Tests
{
    /// <summary>
    /// The colour swatches a Windows hand run drew, with the Y, Cb and Cr that Media Foundation's Video Processor MFT gave
    /// for each (RGB32 to NV12, BT.709 limited range).
    /// </summary>
    public static TheoryData<string, byte, byte, byte, int, int, int> Swatches => new()
    {
        { "panel", 24, 26, 32, 38, 131, 127 },
        { "grey", 128, 128, 128, 126, 128, 128 },
        { "white", 235, 235, 235, 218, 127, 128 },
        { "red", 200, 40, 40, 80, 112, 198 },
        { "green", 40, 180, 80, 139, 98, 71 },
        { "blue", 40, 90, 220, 92, 190, 101 },
        { "orange", 250, 160, 30, 162, 61, 172 },
        { "teal", 20, 160, 170, 128, 146, 66 },
    };

    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }

        return rgba;
    }

    /// <summary>Seeded noise, the alpha byte too.</summary>
    private static byte[] Noise(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        new Random(7).NextBytes(rgba);
        return rgba;
    }

    /// <summary>A buffer of the layout's size, filled with a byte no conversion writes in the padding.</summary>
    private static byte[] Dirty(Nv12Layout layout) => Enumerable.Repeat((byte)0xAA, layout.Size).ToArray();

    /// <summary>BT.709 limited range from the mean of the given pixels, in floating point.</summary>
    private static (double Y, double Cb, double Cr) Reference(params (int R, int G, int B)[] pixels)
    {
        double r = pixels.Average(p => p.R), g = pixels.Average(p => p.G), b = pixels.Average(p => p.B);
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return (16 + 219 * y / 255, 128 + 224 * (b - y) / 1.8556 / 255, 128 + 224 * (r - y) / 1.5748 / 255);
    }

    [Theory]
    [MemberData(nameof(Swatches))]
    public void TheSwatches_GiveTheVideoProcessorsYCbCr_WithinOne(string name, byte r, byte g, byte b, int y, int cb, int cr)
    {
        var layout = Nv12Layout.Packed(4, 2);
        var nv12 = Dirty(layout);
        Nv12.Write(Solid(4, 2, r, g, b), 4, 2, nv12, layout);
        for (var i = 0; i < 8; i++)
            Assert.True(Math.Abs(nv12[i] - y) <= 1, $"{name}: Y {nv12[i]}, the Video Processor's {y}");
        for (var i = 8; i < 12; i += 2)
        {
            Assert.True(Math.Abs(nv12[i] - cb) <= 1, $"{name}: Cb {nv12[i]}, the Video Processor's {cb}");
            Assert.True(Math.Abs(nv12[i + 1] - cr) <= 1, $"{name}: Cr {nv12[i + 1]}, the Video Processor's {cr}");
        }
    }

    [Fact]
    public void OddSides_ArePaddedBlack_AndAlphaIsIgnored()
    {
        // 3 × 3 red, transparent: the picture is 4 × 4, its last column and row black.
        var layout = new Nv12Layout(4, 4, 4);
        var nv12 = Dirty(layout);
        Nv12.Write(Solid(3, 3, 200, 40, 40, a: 0), 3, 3, nv12, layout);

        var opaque = Dirty(layout);
        Nv12.Write(Solid(3, 3, 200, 40, 40), 3, 3, opaque, layout);
        Assert.Equal(opaque, nv12);

        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            Assert.Equal(x < 3 && y < 3 ? 80 : 16, nv12[y * 4 + x]);

        // Each 2 × 2 block's chroma is its four pixels' mean, the padding's black among them.
        (int, int, int) red = (200, 40, 40), black = (0, 0, 0);
        var blocks = new[]
        {
            Reference(red, red, red, red), Reference(red, black, red, black),
            Reference(red, red, black, black), Reference(red, black, black, black),
        };
        for (var i = 0; i < 4; i++)
        {
            var at = 16 + (i / 2) * 4 + (i % 2) * 2;
            Assert.True(Math.Abs(nv12[at] - blocks[i].Cb) <= 1, $"block {i}: Cb {nv12[at]}, expected {blocks[i].Cb:F1}");
            Assert.True(Math.Abs(nv12[at + 1] - blocks[i].Cr) <= 1, $"block {i}: Cr {nv12[at + 1]}, expected {blocks[i].Cr:F1}");
        }
    }

    [Fact]
    public void ANegotiatedLayout_PutsTheChromaPlaneAtPitchTimesHeight_WithEachRowsPaddingBlack()
    {
        const int w = 322, h = 194, pitch = 384;
        var rgba = Noise(w, h);
        var packedLayout = Nv12Layout.Packed(w, h);
        var packed = new byte[packedLayout.Size];
        Nv12.Write(rgba, w, h, packed, packedLayout);

        var layout = new Nv12Layout(w, h, pitch);
        Assert.Equal(pitch * h, layout.ChromaOffset);
        Assert.Equal(pitch * h * 3 / 2, layout.Size);
        var nv12 = Dirty(layout);
        Nv12.Write(rgba, w, h, nv12, layout);

        for (var y = 0; y < h; y++)
        {
            Assert.Equal(packed.AsSpan(y * w, w), nv12.AsSpan(y * pitch, w));
            Assert.All(nv12[(y * pitch + w)..((y + 1) * pitch)], b => Assert.Equal(16, b));
        }

        for (var y = 0; y < h / 2; y++)
        {
            Assert.Equal(packed.AsSpan(packedLayout.ChromaOffset + y * w, w), nv12.AsSpan(layout.ChromaOffset + y * pitch, w));
            Assert.All(nv12[(layout.ChromaOffset + y * pitch + w)..(layout.ChromaOffset + (y + 1) * pitch)], b => Assert.Equal(128, b));
        }
    }

    [Fact]
    public void APitchEqualToTheWidth_GivesThePackedBytes()
    {
        const int w = 322, h = 194;
        var rgba = Noise(w, h);
        var layout = Nv12Layout.Packed(w, h);
        Assert.Equal((w, h, w, w * h, w * h * 3 / 2), (layout.Width, layout.Height, layout.Pitch, layout.ChromaOffset, layout.Size));
        var nv12 = Dirty(layout);
        Nv12.Write(rgba, w, h, nv12, layout);
        Assert.Equal(PackedReference(rgba, w, h), nv12);
    }

    [Fact]
    public void AFrameThatIsntTheLayoutsSize_OrABufferTooSmall_IsRefused()
    {
        var rgba = Solid(6, 4, 1, 2, 3);
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, 6, 4, new byte[100], Nv12Layout.Packed(8, 4)));
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, 6, 4, new byte[35], Nv12Layout.Packed(6, 4)));
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba.AsSpan(1), 6, 4, new byte[36], Nv12Layout.Packed(6, 4)));
        Assert.Throws<ArgumentException>(() => new Nv12Layout(6, 4, 5));
        Assert.Throws<ArgumentException>(() => new Nv12Layout(5, 4, 6));
    }

    [Fact]
    public void ALayoutWhoseSizeArithmeticOverflows_IsRefused_AndOneJustInsideTheLimitIsNot()
    {
        const int n = 2_147_483_646;
        Assert.Throws<ArgumentException>(() => new Nv12Layout(n, n, n));
        Assert.Throws<ArgumentException>(() => new Nv12Layout(2, n, 2));
        Assert.Throws<ArgumentException>(() => new Nv12Layout(2, n, n));
        Assert.Throws<ArgumentException>(() => new Nv12Layout(2, 715_827_884, 2));

        // Pitch × height × 3 / 2 is int.MaxValue − 1 here.
        var layout = new Nv12Layout(2, 715_827_882, 2);
        Assert.Equal(1_431_655_764, layout.ChromaOffset);
        Assert.Equal(2_147_483_646, layout.Size);
    }

    [Fact]
    public void AWriteWhoseSizesOverflow_IsRefusedBeforeAnyUnsafeCode()
    {
        const int n = 2_147_483_646;
        var rgba = Solid(2, 2, 1, 2, 3);
        var small = Nv12Layout.Packed(2, 2);
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, n, n, new byte[small.Size], small));
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, int.MaxValue, int.MaxValue, new byte[small.Size], small));
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, 2, 2, new byte[small.Size], default));

        // Sizes that match a layout just inside the limit still have to bring the bytes.
        var big = new Nv12Layout(2, 715_827_882, 2);
        Assert.Throws<ArgumentException>(() => Nv12.Write(rgba, 2, 715_827_882, new byte[small.Size], big));
    }

    /// <summary>
    /// The packed conversion the Windows hand run checked against the Video Processor MFT (even sides, pitch the width):
    /// BT.709 limited range in 16.16 fixed point, chroma from each 2 × 2 block's sum.
    /// </summary>
    private static byte[] PackedReference(byte[] rgba, int w, int h)
    {
        var nv12 = new byte[w * h * 3 / 2];
        static byte Luma(int r, int g, int b) => (byte)(16 + ((11966 * r + 40254 * g + 4064 * b + 32768) >> 16));
        for (var y = 0; y < h; y += 2)
        for (var x = 0; x < w; x += 2)
        {
            int R(int dy, int dx) => rgba[((y + dy) * w + x + dx) * 4];
            int G(int dy, int dx) => rgba[((y + dy) * w + x + dx) * 4 + 1];
            int B(int dy, int dx) => rgba[((y + dy) * w + x + dx) * 4 + 2];
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                nv12[(y + dy) * w + x + dx] = Luma(R(dy, dx), G(dy, dx), B(dy, dx));
            int r = R(0, 0) + R(0, 1) + R(1, 0) + R(1, 1), g = G(0, 0) + G(0, 1) + G(1, 0) + G(1, 1), b = B(0, 0) + B(0, 1) + B(1, 0) + B(1, 1);
            var c = w * h + (y / 2) * w + x;
            nv12[c] = (byte)Math.Clamp(128 + ((-6597 * r - 22187 * g + 28784 * b + 131072) >> 18), 16, 240);
            nv12[c + 1] = (byte)Math.Clamp(128 + ((28784 * r - 26148 * g - 2636 * b + 131072) >> 18), 16, 240);
        }

        return nv12;
    }
}
