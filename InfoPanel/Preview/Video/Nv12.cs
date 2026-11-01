namespace InfoPanel.Preview.Video;

/// <summary>
/// An NV12 picture in one contiguous buffer, as an encoder negotiated it: <see cref="Height"/> luma rows of
/// <see cref="Pitch"/> bytes, then half as many rows of interleaved Cb and Cr of the same pitch, starting at
/// <see cref="ChromaOffset"/> (pitch × height, Media Foundation's contiguous NV12). Both sides are even, the frame's
/// padded; the pitch is at least the width.
/// </summary>
internal readonly record struct Nv12Layout
{
    public Nv12Layout(int width, int height, int pitch)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException($"an NV12 picture's sides are even and positive, not {width} × {height}");
        if (pitch < width)
            throw new ArgumentException($"an NV12 row pitch of {pitch} is below the width, {width}");
        // pitch × height fits a long (both are below 2^31); the 3 / 2 is applied to the bound instead of the product.
        if ((long)pitch * height > MaxChromaOffset)
            throw new ArgumentException($"an NV12 picture of pitch {pitch} and height {height} is too large");
        Width = width;
        Height = height;
        Pitch = pitch;
    }

    /// <summary>The largest <see cref="ChromaOffset"/> whose <see cref="Size"/> (1.5 times it) fits an int, even.</summary>
    private const long MaxChromaOffset = int.MaxValue / 3 * 2;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes from one row's start to the next's, in both planes.</summary>
    public int Pitch { get; }

    /// <summary>Where the Cb and Cr plane starts.</summary>
    public int ChromaOffset => Pitch * Height;

    /// <summary>The whole picture's bytes: pitch × height × 3 / 2.</summary>
    public int Size => ChromaOffset + ChromaOffset / 2;

    /// <summary>The layout of a frame's even-padded size with no row padding.</summary>
    public static Nv12Layout Packed(int width, int height) => new((width + 1) & ~1, (height + 1) & ~1, (width + 1) & ~1);
}

/// <summary>
/// RGBA frames to NV12 on the CPU: the BT.709 matrix into limited range (Y 16–235, Cb and Cr 16–240), in 16.16 fixed
/// point, each 2 × 2 block's chroma from its four pixels' mean. Premultiplied RGBA's alpha is ignored (transparency shows
/// black). An odd side's padding column or row is black pixels, part of the edge blocks' mean; a row's bytes past the
/// width are black (Y 16, Cb and Cr 128).
/// </summary>
internal static class Nv12
{
    /// <summary>
    /// Writes <paramref name="rgba"/>, <paramref name="width"/> × <paramref name="height"/> packed RGBA, into
    /// <paramref name="nv12"/> as <paramref name="layout"/> lays it out; every byte of the layout's size is written. The
    /// layout must be the frame's even-padded size.
    /// </summary>
    public static unsafe void Write(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> nv12, Nv12Layout layout)
    {
        if (width <= 0 || height <= 0 || layout.Width != ((width + 1) & ~1) || layout.Height != ((height + 1) & ~1))
            throw new ArgumentException($"a {width} × {height} frame doesn't fit a {layout.Width} × {layout.Height} NV12 layout");
        // Divided, not multiplied: nothing here may wrap, whatever the sides.
        if (rgba.Length / 4 < (long)width * height)
            throw new ArgumentException($"{rgba.Length} bytes of RGBA for a {width} × {height} frame");
        if (nv12.Length < layout.Size)
            throw new ArgumentException($"{nv12.Length} bytes for an NV12 layout of {layout.Size}");

        var pitch = layout.Pitch;
        var stride = width * 4;
        var pairs = width / 2;
        fixed (byte* src = rgba)
        fixed (byte* dst = nv12)
        {
            for (var y = 0; y < layout.Height; y += 2)
            {
                // The first row of a pair is always the frame's; the second is padding when the height is odd.
                var s0 = src + (long)y * stride;
                var s1 = y + 1 < height ? s0 + stride : null;
                var y0 = dst + (long)y * pitch;
                var y1 = y0 + pitch;
                var c = dst + layout.ChromaOffset + (long)(y / 2) * pitch;
                for (var i = 0; i < pairs; i++, s0 += 8, y0 += 2, y1 += 2, c += 2)
                {
                    int r00 = s0[0], g00 = s0[1], b00 = s0[2];
                    int r01 = s0[4], g01 = s0[5], b01 = s0[6];
                    int r10 = 0, g10 = 0, b10 = 0, r11 = 0, g11 = 0, b11 = 0;
                    if (s1 != null)
                    {
                        r10 = s1[0];
                        g10 = s1[1];
                        b10 = s1[2];
                        r11 = s1[4];
                        g11 = s1[5];
                        b11 = s1[6];
                        s1 += 8;
                    }

                    y0[0] = Luma(r00, g00, b00);
                    y0[1] = Luma(r01, g01, b01);
                    y1[0] = Luma(r10, g10, b10);
                    y1[1] = Luma(r11, g11, b11);
                    Chroma(c, r00 + r01 + r10 + r11, g00 + g01 + g10 + g11, b00 + b01 + b10 + b11);
                }

                if ((width & 1) != 0)
                {
                    // The last block of an odd width: one column of the frame, one of black padding.
                    int r00 = s0[0], g00 = s0[1], b00 = s0[2];
                    int r10 = 0, g10 = 0, b10 = 0;
                    if (s1 != null)
                    {
                        r10 = s1[0];
                        g10 = s1[1];
                        b10 = s1[2];
                    }

                    y0[0] = Luma(r00, g00, b00);
                    y0[1] = Black;
                    y1[0] = Luma(r10, g10, b10);
                    y1[1] = Black;
                    Chroma(c, r00 + r10, g00 + g10, b00 + b10);
                    y0 += 2;
                    y1 += 2;
                    c += 2;
                }

                // The row padding past the width.
                var padding = pitch - layout.Width;
                new Span<byte>(y0, padding).Fill(Black);
                new Span<byte>(y1, padding).Fill(Black);
                new Span<byte>(c, padding).Fill(NeutralChroma);
            }
        }
    }

    /// <summary>Black's luma, 16.</summary>
    private const byte Black = 16;

    /// <summary>Cb and Cr of any grey, 128.</summary>
    private const byte NeutralChroma = 128;

    /// <summary>16 + 219 × (0.2126 R + 0.7152 G + 0.0722 B) / 255, rounded.</summary>
    private static byte Luma(int r, int g, int b) => (byte)(16 + ((11966 * r + 40254 * g + 4064 * b + 32768) >> 16));

    /// <summary>Cb and Cr from the sums of a block's four R, G and B values (black padding counted as zeros), rounded and clamped to 16–240.</summary>
    private static unsafe void Chroma(byte* c, int r, int g, int b)
    {
        c[0] = (byte)Math.Clamp(128 + ((-6597 * r - 22187 * g + 28784 * b + 131072) >> 18), 16, 240);
        c[1] = (byte)Math.Clamp(128 + ((28784 * r - 26148 * g - 2636 * b + 131072) >> 18), 16, 240);
    }
}
