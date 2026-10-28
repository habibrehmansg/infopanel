using System;
using System.Globalization;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>The four expression types (spec/docs/expressions.md) as flags: a colour-like string literal is both string and colour.</summary>
[Flags]
internal enum Ty : byte
{
    None = 0,
    Number = 1,
    String = 2,
    Bool = 4,
    Color = 8,
    Any = 15,
}

/// <summary>
/// An evaluated value. Every expression has a static type, so the reader knows which member to read: numbers and bools
/// (1 or 0) in <see cref="N"/>, strings in <see cref="S"/>, colours in <see cref="C"/> as 0xRRGGBBAA with straight alpha.
/// A colour-like string literal fills both <see cref="S"/> and <see cref="C"/>, so it serves wherever either is expected.
/// </summary>
internal readonly struct Val
{
    public readonly double N;
    public readonly string? S;
    public readonly uint C;

    private Val(double n, string? s, uint c)
    {
        N = n;
        S = s;
        C = c;
    }

    /// <summary>A number: finite (anything else reads 0) and with no sign on a zero (spec/docs/expressions.md, "Numbers and strings").</summary>
    public static Val Num(double n) => new(n == 0 || !double.IsFinite(n) ? 0 : n, null, 0);

    public static Val Bool(bool b) => new(b ? 1 : 0, null, 0);

    public static Val Str(string s) => new(0, s, 0);

    public static Val Col(uint rgba) => new(0, null, rgba);

    public static Val StrCol(string s, uint rgba) => new(0, s, rgba);

    public bool B => N != 0;

    public string Text => S ?? "";
}

/// <summary>Colours as 0xRRGGBBAA, straight alpha, sRGB.</summary>
internal static class SceneColor
{
    public const uint Black = 0x000000FF;
    public const uint Transparent = 0;

    public static byte R(uint c) => (byte)(c >> 24);
    public static byte G(uint c) => (byte)(c >> 16);
    public static byte B(uint c) => (byte)(c >> 8);
    public static byte A(uint c) => (byte)c;

    public static uint Make(int r, int g, int b, int a) =>
        ((uint)(byte)r << 24) | ((uint)(byte)g << 16) | ((uint)(byte)b << 8) | (byte)a;

    /// <summary>#RRGGBB or #RRGGBBAA; false for anything else.</summary>
    public static bool TryParse(string? s, out uint rgba)
    {
        rgba = 0;
        if (s is null || s.Length is not (7 or 9) || s[0] != '#') return false;
        if (!uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return false;
        rgba = s.Length == 7 ? (v << 8) | 0xFF : v;
        return true;
    }

    public static SKColor ToSk(uint c) => new(R(c), G(c), B(c), A(c));

    public static SKColor ToSk(uint c, float opacity)
    {
        var a = (int)Math.Round(A(c) * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero);
        return new SKColor(R(c), G(c), B(c), (byte)a);
    }

    /// <summary>A channel 0–255 from a number, clamped and rounded half away from zero.</summary>
    public static int Channel(double v) => (int)Math.Round(Math.Clamp(SceneMath.Finite(v), 0, 255), MidpointRounding.AwayFromZero);

    /// <summary>An alpha 0–255 from a fraction 0–1.</summary>
    public static int Alpha(double a) => (int)Math.Round(Math.Clamp(SceneMath.Finite(a), 0, 1) * 255, MidpointRounding.AwayFromZero);

    /// <summary>Per channel, straight alpha, sRGB; t clamped to 0–1.</summary>
    public static uint Lerp(uint a, uint b, double t)
    {
        t = Math.Clamp(SceneMath.Finite(t), 0, 1);
        static int L(byte x, byte y, double t) => (int)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);
        return Make(L(R(a), R(b), t), L(G(a), G(b), t), L(B(a), B(b), t), L(A(a), A(b), t));
    }

    public static uint WithAlpha(uint c, double a) => (c & 0xFFFFFF00) | (uint)Alpha(a);

    public static string Format(uint c) => "#" + c.ToString("x8", CultureInfo.InvariantCulture);
}

internal static class SceneMath
{
    /// <summary>A value that isn't finite reads as 0 (spec/docs/scene.md).</summary>
    public static double Finite(double x) => double.IsFinite(x) ? x : 0;

    /// <summary>
    /// A number as a 32-bit coordinate for drawing: finite and within ±1e9, so Skia never sees infinities from a value
    /// that is finite in 64 bits.
    /// </summary>
    public static float F(double x) => (float)Math.Clamp(Finite(x), -1e9, 1e9);

    public static double Deg(double radians) => radians * (180 / Math.PI);

    /// <summary>sin and cos of degrees, exact at multiples of 90°.</summary>
    public static double SinDeg(double x)
    {
        var r = x - 360 * Math.Floor(x / 360);
        if (r == 0 || r == 180) return 0;
        if (r == 90) return 1;
        if (r == 270) return -1;
        return Math.Sin(r * (Math.PI / 180));
    }

    public static double CosDeg(double x) => SinDeg(x + 90);
}

internal static class SceneText
{
    /// <summary>The first <paramref name="max"/> Unicode scalar values of <paramref name="s"/> (a lone surrogate counts as one).</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var count = 0;
        var i = 0;
        while (i < s.Length && count < max)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            count++;
        }
        return i >= s.Length ? s : s[..i];
    }
}
