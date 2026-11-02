using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
/// width are black (Y 16, Cb and Cr 128). Where the CPU has vectors, sixteen pixels of a row pair at a time go through
/// the same integer arithmetic in them (bit for bit the scalar path's bytes): AVX2's multiply-adds on x64, else 128-bit
/// vectors; the rest of a row, and CPUs without vectors, take the scalar path.
/// </summary>
internal static class Nv12
{
    /// <summary>Whether <see cref="Write(ReadOnlySpan{byte}, int, int, Span{byte}, Nv12Layout)"/> takes the vector path.</summary>
    internal static bool Vectorised => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    /// <summary>
    /// Writes <paramref name="rgba"/>, <paramref name="width"/> × <paramref name="height"/> packed RGBA, into
    /// <paramref name="nv12"/> as <paramref name="layout"/> lays it out; every byte of the layout's size is written. The
    /// layout must be the frame's even-padded size.
    /// </summary>
    public static void Write(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> nv12, Nv12Layout layout) =>
        Write(rgba, width, height, nv12, layout, Vectorised);

    /// <summary>
    /// As <see cref="Write(ReadOnlySpan{byte}, int, int, Span{byte}, Nv12Layout)"/>, the vector path taken or not as
    /// <paramref name="vectorised"/> says (the tests compare the two; true only where <see cref="Vectorised"/> is).
    /// </summary>
    internal static unsafe void Write(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> nv12, Nv12Layout layout, bool vectorised)
    {
        vectorised &= Vectorised;
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
                var i = 0;
                if (vectorised)
                {
                    i = Avx2.IsSupported ? SixteenAtATimeAvx2(s0, s1, y0, y1, c, pairs) : SixteenAtATime(s0, s1, y0, y1, c, pairs);
                    s0 += i * 8;
                    if (s1 != null)
                        s1 += i * 8;
                    y0 += i * 2;
                    y1 += i * 2;
                    c += i * 2;
                }

                for (; i < pairs; i++, s0 += 8, y0 += 2, y1 += 2, c += 2)
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

    /// <summary>
    /// The vector path over one row pair: <paramref name="pairs"/> 2 × 2 blocks from <paramref name="s0"/> (and
    /// <paramref name="s1"/>, the second row, or null for black padding) are converted eight at a time, sixteen pixels a
    /// row, with <see cref="Luma"/>'s and <see cref="Chroma"/>'s integer arithmetic; returns how many blocks were done (a
    /// multiple of eight), the rest left to the scalar path. One method with no calls: a Debug build inlines nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int SixteenAtATime(byte* s0, byte* s1, byte* y0, byte* y1, byte* c, int pairs)
    {
        // Each pixel a little-endian uint: R in the low byte, then G, B and A.
        var low = Vector128.Create(0xFFu);
        // Luma's rounding and its 16, added before the shift: (n + 32768 + 16 × 65536) >> 16 = ((n + 32768) >> 16) + 16.
        var lumaBias = Vector128.Create(32768u + (16u << 16));
        // R and B (or G and A) of a pixel in two 16-bit fields: sums of four stay below 1,021 a field.
        var pairMask = Vector128.Create(0x00FF_00FFu);
        var lowWord = Vector128.Create(0xFFFF_FFFFul);
        var field = Vector128.Create(0xFFFF);
        var chromaRound = Vector128.Create(131072);
        var neutral = Vector128.Create(128);
        var chromaMin = Vector128.Create(16);
        var chromaMax = Vector128.Create(240);
        var none = Vector128<uint>.Zero;
        var done = 0;
        for (; done + 8 <= pairs; done += 8, s0 += 64, y0 += 16, y1 += 16, c += 16)
        {
            // t: four pixels of the first row a vector; u: the second's, or black.
            var t0 = Vector128.Load((uint*)s0);
            var t1 = Vector128.Load((uint*)(s0 + 16));
            var t2 = Vector128.Load((uint*)(s0 + 32));
            var t3 = Vector128.Load((uint*)(s0 + 48));
            var u0 = none;
            var u1 = none;
            var u2 = none;
            var u3 = none;
            if (s1 != null)
            {
                u0 = Vector128.Load((uint*)s1);
                u1 = Vector128.Load((uint*)(s1 + 16));
                u2 = Vector128.Load((uint*)(s1 + 32));
                u3 = Vector128.Load((uint*)(s1 + 48));
                s1 += 64;
            }

            // Luma: (11966 R + 40254 G + 4064 B + the bias) >> 16, at most 235, narrowed to bytes.
            Vector128.Store(Vector128.Narrow(
                Vector128.Narrow(
                    ((t0 & low) * 11966u + ((t0 >> 8) & low) * 40254u + ((t0 >> 16) & low) * 4064u + lumaBias) >> 16,
                    ((t1 & low) * 11966u + ((t1 >> 8) & low) * 40254u + ((t1 >> 16) & low) * 4064u + lumaBias) >> 16),
                Vector128.Narrow(
                    ((t2 & low) * 11966u + ((t2 >> 8) & low) * 40254u + ((t2 >> 16) & low) * 4064u + lumaBias) >> 16,
                    ((t3 & low) * 11966u + ((t3 >> 8) & low) * 40254u + ((t3 >> 16) & low) * 4064u + lumaBias) >> 16)), y0);
            Vector128.Store(Vector128.Narrow(
                Vector128.Narrow(
                    ((u0 & low) * 11966u + ((u0 >> 8) & low) * 40254u + ((u0 >> 16) & low) * 4064u + lumaBias) >> 16,
                    ((u1 & low) * 11966u + ((u1 >> 8) & low) * 40254u + ((u1 >> 16) & low) * 4064u + lumaBias) >> 16),
                Vector128.Narrow(
                    ((u2 & low) * 11966u + ((u2 >> 8) & low) * 40254u + ((u2 >> 16) & low) * 4064u + lumaBias) >> 16,
                    ((u3 & low) * 11966u + ((u3 >> 8) & low) * 40254u + ((u3 >> 16) & low) * 4064u + lumaBias) >> 16)), y1);

            // Each block's sums, R and B in one lane's two 16-bit fields and G (with A) in another's: the two rows'
            // pixels added, then each 64-bit lane's two pixels added, then narrowed to one lane a block (blocks 0–3 from
            // vectors 0 and 1, 4–7 from 2 and 3).
            var rb0 = ((t0 & pairMask) + (u0 & pairMask)).AsUInt64();
            var rb1 = ((t1 & pairMask) + (u1 & pairMask)).AsUInt64();
            var rb2 = ((t2 & pairMask) + (u2 & pairMask)).AsUInt64();
            var rb3 = ((t3 & pairMask) + (u3 & pairMask)).AsUInt64();
            var ga0 = (((t0 >> 8) & pairMask) + ((u0 >> 8) & pairMask)).AsUInt64();
            var ga1 = (((t1 >> 8) & pairMask) + ((u1 >> 8) & pairMask)).AsUInt64();
            var ga2 = (((t2 >> 8) & pairMask) + ((u2 >> 8) & pairMask)).AsUInt64();
            var ga3 = (((t3 >> 8) & pairMask) + ((u3 >> 8) & pairMask)).AsUInt64();
            var rbLow = Vector128.Narrow((rb0 & lowWord) + (rb0 >> 32), (rb1 & lowWord) + (rb1 >> 32)).AsInt32();
            var rbHigh = Vector128.Narrow((rb2 & lowWord) + (rb2 >> 32), (rb3 & lowWord) + (rb3 >> 32)).AsInt32();
            var gLow = Vector128.Narrow((ga0 & lowWord) + (ga0 >> 32), (ga1 & lowWord) + (ga1 >> 32)).AsInt32() & field;
            var gHigh = Vector128.Narrow((ga2 & lowWord) + (ga2 >> 32), (ga3 & lowWord) + (ga3 >> 32)).AsInt32() & field;

            // Cb and Cr: 128 + ((… + 131072) >> 18), clamped to 16–240; each block's two bytes Cb then Cr.
            Vector128.Store(Vector128.Narrow(
                (Vector128.Min(Vector128.Max(neutral + (((rbLow & field) * -6597 + gLow * -22187 + (rbLow >> 16) * 28784 + chromaRound) >> 18), chromaMin), chromaMax)
                 | (Vector128.Min(Vector128.Max(neutral + (((rbLow & field) * 28784 + gLow * -26148 + (rbLow >> 16) * -2636 + chromaRound) >> 18), chromaMin), chromaMax) << 8)).AsUInt32(),
                (Vector128.Min(Vector128.Max(neutral + (((rbHigh & field) * -6597 + gHigh * -22187 + (rbHigh >> 16) * 28784 + chromaRound) >> 18), chromaMin), chromaMax)
                 | (Vector128.Min(Vector128.Max(neutral + (((rbHigh & field) * 28784 + gHigh * -26148 + (rbHigh >> 16) * -2636 + chromaRound) >> 18), chromaMin), chromaMax) << 8)).AsUInt32()).AsByte(), c);
        }

        return done;
    }

    /// <summary>
    /// <see cref="SixteenAtATime"/> with AVX2, in about a third of its operations (what a Debug build pays for): each
    /// 128-bit lane's four pixels are shuffled into 16-bit R, G, B, G per pixel, two pixels a vector, so one multiply-add
    /// of pairs (11966, 20127) and (4064, 20127) and one horizontal add give a pixel's luma sum (G's 40254 split in two
    /// halves, each within a short); the same shuffled vectors, the two rows added, give each block's chroma sums by
    /// multiply-adds with Cb's and Cr's weights (G's split likewise) and two more horizontal adds. Neither Cb nor Cr can
    /// leave 16–240 (the weights of each sum to zero and the largest, 28784, gives 112 at a sum of 1020), so the
    /// scalar path's clamp changes nothing and is left out.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int SixteenAtATimeAvx2(byte* s0, byte* s1, byte* y0, byte* y1, byte* c, int pairs)
    {
        const byte Z = 0x80; // A shuffle index that writes zero.
        // Per 128-bit lane: pixels 0 and 1 of its four as 16-bit R, G, B, G; then pixels 2 and 3.
        var firstTwo = Vector256.Create(Vector128.Create(0, Z, 1, Z, 2, Z, 1, Z, 4, Z, 5, Z, 6, Z, 5, Z));
        var lastTwo = Vector256.Create(Vector128.Create(8, Z, 9, Z, 10, Z, 9, Z, 12, Z, 13, Z, 14, Z, 13, Z));
        var lumaWeights = Vector256.Create((short)11966, 20127, 4064, 20127, 11966, 20127, 4064, 20127, 11966, 20127, 4064, 20127, 11966, 20127, 4064, 20127);
        var cbWeights = Vector256.Create((short)-6597, -11094, 28784, -11093, -6597, -11094, 28784, -11093, -6597, -11094, 28784, -11093, -6597, -11094, 28784, -11093);
        var crWeights = Vector256.Create((short)28784, -13074, -2636, -13074, 28784, -13074, -2636, -13074, 28784, -13074, -2636, -13074, 28784, -13074, -2636, -13074);
        // The roundings with luma's 16 and chroma's 128 added before the shifts (exact: a multiple of the divisor).
        var lumaBias = Vector256.Create(32768 + (16 << 16));
        var chromaBias = Vector256.Create(131072 + (128 << 18));
        // The packs below interleave 32-bit groups across the two lanes; these put them back in pixel and block order.
        var rows = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
        var blocks = Vector256.Create(0, 1, 4, 5, 2, 3, 6, 7);
        var none = Vector256<byte>.Zero;
        var done = 0;
        for (; done + 8 <= pairs; done += 8, s0 += 64, y0 += 16, y1 += 16, c += 16)
        {
            // t: the first row's sixteen pixels in two vectors; u: the second row's, or black.
            var t0 = Avx.LoadVector256(s0);
            var t1 = Avx.LoadVector256(s0 + 32);
            var u0 = none;
            var u1 = none;
            if (s1 != null)
            {
                u0 = Avx.LoadVector256(s1);
                u1 = Avx.LoadVector256(s1 + 32);
                s1 += 64;
            }

            // a: pixels 0, 1 | 4, 5 of a vector's eight (blocks 0 | 2); b: pixels 2, 3 | 6, 7 (blocks 1 | 3).
            var ta0 = Avx2.Shuffle(t0, firstTwo).AsInt16();
            var tb0 = Avx2.Shuffle(t0, lastTwo).AsInt16();
            var ta1 = Avx2.Shuffle(t1, firstTwo).AsInt16();
            var tb1 = Avx2.Shuffle(t1, lastTwo).AsInt16();
            var ua0 = Avx2.Shuffle(u0, firstTwo).AsInt16();
            var ub0 = Avx2.Shuffle(u0, lastTwo).AsInt16();
            var ua1 = Avx2.Shuffle(u1, firstTwo).AsInt16();
            var ub1 = Avx2.Shuffle(u1, lastTwo).AsInt16();

            // Luma, pixels 0–7 and 8–15 of each row, packed to bytes: the pack's 32-bit groups permuted back to the
            // first row's sixteen bytes, then the second's.
            var luma = Avx2.PackUnsignedSaturate(
                Avx2.PackUnsignedSaturate(
                    Avx2.ShiftRightLogical(Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(ta0, lumaWeights), Avx2.MultiplyAddAdjacent(tb0, lumaWeights)) + lumaBias, 16),
                    Avx2.ShiftRightLogical(Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(ta1, lumaWeights), Avx2.MultiplyAddAdjacent(tb1, lumaWeights)) + lumaBias, 16)).AsInt16(),
                Avx2.PackUnsignedSaturate(
                    Avx2.ShiftRightLogical(Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(ua0, lumaWeights), Avx2.MultiplyAddAdjacent(ub0, lumaWeights)) + lumaBias, 16),
                    Avx2.ShiftRightLogical(Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(ua1, lumaWeights), Avx2.MultiplyAddAdjacent(ub1, lumaWeights)) + lumaBias, 16)).AsInt16());
            var ordered = Avx2.PermuteVar8x32(luma.AsInt32(), rows).AsByte();
            ordered.GetLower().Store(y0);
            ordered.GetUpper().Store(y1);

            // Chroma: each 128-bit lane of a sum is one block's column sums; its multiply-add's four 32-bit lanes sum to
            // the block's numerator. Three horizontal adds leave blocks 0, 1, 4, 5 | 2, 3, 6, 7.
            var a0 = ta0 + ua0;
            var b0 = tb0 + ub0;
            var a1 = ta1 + ua1;
            var b1 = tb1 + ub1;
            var cb = Avx2.ShiftRightArithmetic(Avx2.HorizontalAdd(
                Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(a0, cbWeights), Avx2.MultiplyAddAdjacent(b0, cbWeights)),
                Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(a1, cbWeights), Avx2.MultiplyAddAdjacent(b1, cbWeights))) + chromaBias, 18);
            var cr = Avx2.ShiftRightArithmetic(Avx2.HorizontalAdd(
                Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(a0, crWeights), Avx2.MultiplyAddAdjacent(b0, crWeights)),
                Avx2.HorizontalAdd(Avx2.MultiplyAddAdjacent(a1, crWeights), Avx2.MultiplyAddAdjacent(b1, crWeights))) + chromaBias, 18);
            var pairsInOrder = Avx2.PermuteVar8x32(cb | Avx2.ShiftLeftLogical(cr, 8), blocks);
            Sse41.PackUnsignedSaturate(pairsInOrder.GetLower(), pairsInOrder.GetUpper()).AsByte().Store(c);
        }

        return done;
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
