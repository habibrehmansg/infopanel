using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InfoPanel.Spec.Schema;

/// <summary>JSON helpers shared by the schema nodes: type tests, number comparison, deep equality, pointer escaping.</summary>
internal static class Json
{
    /// <summary>Escapes one reference token for an RFC 6901 JSON pointer.</summary>
    public static string Esc(string token) => token.Replace("~", "~0").Replace("/", "~1");

    public static string TypeName(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.Array => "array",
        _ => "object",
    };

    /// <summary>Does the JSON value have the given JSON Schema <c>type</c>? Integral numbers (<c>1.0</c>) are integers.</summary>
    public static bool Matches(string type, JsonElement v) => type switch
    {
        "null" => v.ValueKind == JsonValueKind.Null,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && IsInteger(v),
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    /// <summary>A null set means "any type".</summary>
    public static bool Allows(HashSet<string>? types, JsonElement v)
    {
        if (types == null) return true;
        foreach (var t in types) if (Matches(t, v)) return true;
        return false;
    }

    public static bool IsInteger(JsonElement v) => JsonNumber.IsInteger(v);

    public static double ToDouble(JsonElement v) =>
        v.TryGetDouble(out var x) ? x : double.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Compares two JSON numbers by value (<see cref="JsonNumber"/>).</summary>
    public static int CompareNumbers(JsonElement a, JsonElement b) => JsonNumber.Compare(a, b);

    /// <summary>Deep JSON equality: numbers by value, object property order irrelevant.</summary>
    public static bool DeepEquals(JsonElement a, JsonElement b)
    {
        long visited = 0;
        return DeepEquals(a, b, ref visited);
    }

    /// <summary>As <see cref="DeepEquals(JsonElement, JsonElement)"/>, counting the pairs of values compared in <paramref name="visited"/>.</summary>
    public static bool DeepEquals(JsonElement a, JsonElement b, ref long visited)
    {
        visited++;
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return CompareNumbers(a, b) == 0;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using (var ea = a.EnumerateArray().GetEnumerator())
                using (var eb = b.EnumerateArray().GetEnumerator())
                    while (ea.MoveNext() && eb.MoveNext())
                        if (!DeepEquals(ea.Current, eb.Current, ref visited)) return false;
                return true;
            case JsonValueKind.Object:
                if (a.GetPropertyCount() != b.GetPropertyCount()) return false;
                foreach (var p in a.EnumerateObject())
                    if (!b.TryGetProperty(p.Name, out var bv) || !DeepEquals(p.Value, bv, ref visited)) return false;
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// A hash consistent with <see cref="DeepEquals(JsonElement, JsonElement)"/>, seeded per process (<see cref="HashCode"/>),
    /// so content can't be built to collide. Numbers hash by both halves of their double value (-0 as 0), which is the
    /// same for every spelling of one value: numbers that differ only past a double's precision still share a hash, which
    /// is why every comparison is charged by the caller.
    /// </summary>
    public static int Hash(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                var x = ToDouble(v);
                if (x == 0) x = 0;
                var bits = BitConverter.DoubleToInt64Bits(x);
                return HashCode.Combine(JsonValueKind.Number, (int)bits, (int)(bits >> 32));
            case JsonValueKind.String:
                return HashCode.Combine(JsonValueKind.String, string.GetHashCode(v.GetString(), StringComparison.Ordinal));
            case JsonValueKind.Array:
                var h = new HashCode();
                h.Add(JsonValueKind.Array);
                foreach (var e in v.EnumerateArray()) h.Add(Hash(e));
                return h.ToHashCode();
            case JsonValueKind.Object:
                var sum = 0;
                foreach (var p in v.EnumerateObject())
                    sum = unchecked(sum + HashCode.Combine(string.GetHashCode(p.Name, StringComparison.Ordinal), Hash(p.Value)));
                return HashCode.Combine(JsonValueKind.Object, sum);
            default:
                return HashCode.Combine(v.ValueKind);
        }
    }

    /// <summary>Number of Unicode code points in a string (a surrogate pair counts once).</summary>
    public static int CodePoints(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }
}

/// <summary>
/// JSON numbers by their exact value, read from their text, as JSON Schema compares them: a decimal holds 28 digits and a
/// double 17, so neither decides every number (1e-100 isn't 0, and 1.00000000000000000000000000001 isn't 1 or an
/// integer). The schema validator's <c>type: integer</c>, bounds and equality, and whatever must decide a number as it
/// does, read numbers through this.
/// </summary>
public static class JsonNumber
{
    /// <summary>Whether <paramref name="v"/> (a JSON number) is a whole number, in any spelling (1, 1.0, 1e2, 1e400).</summary>
    public static bool IsInteger(JsonElement v)
    {
        if (v.TryGetInt64(out _))
            return true;
        var text = JsonMarshal.GetRawUtf8Value(v);
        if (IsDecimal(text) && v.TryGetDecimal(out var d))
            return d == decimal.Truncate(d);
        return Exact.Parse(text).IsInteger;
    }

    /// <summary>Compares two JSON numbers by value.</summary>
    public static int Compare(JsonElement a, JsonElement b)
    {
        if (a.TryGetInt64(out var x) && b.TryGetInt64(out var y))
            return x.CompareTo(y);
        var left = JsonMarshal.GetRawUtf8Value(a);
        var right = JsonMarshal.GetRawUtf8Value(b);
        if (IsDecimal(left) && IsDecimal(right) && a.TryGetDecimal(out var p) && b.TryGetDecimal(out var q))
            return p.CompareTo(q);
        return Exact.Compare(Exact.Parse(left), Exact.Parse(right));
    }

    /// <summary>Compares a JSON number with a whole number by value.</summary>
    public static int Compare(JsonElement a, long b)
    {
        if (a.TryGetInt64(out var x))
            return x.CompareTo(b);
        var text = JsonMarshal.GetRawUtf8Value(a);
        if (IsDecimal(text) && a.TryGetDecimal(out var d))
            return d.CompareTo(b);
        Span<byte> digits = stackalloc byte[24];
        b.TryFormat(digits, out var length, default, CultureInfo.InvariantCulture);
        return Exact.Compare(Exact.Parse(text), Exact.Parse(digits[..length]));
    }

    /// <summary>
    /// Whether a decimal holds the number written as <paramref name="text"/> exactly: no exponent, at most 28 digits after
    /// the point and at most 28 from the first one that isn't zero. The common case, and the cheap one.
    /// </summary>
    private static bool IsDecimal(ReadOnlySpan<byte> text)
    {
        if (text.IndexOfAny((byte)'e', (byte)'E') >= 0)
            return false;
        var point = text.IndexOf((byte)'.');
        if (point >= 0 && text.Length - point - 1 > 28)
            return false;
        var first = text.IndexOfAnyExcept("-0."u8);
        return first < 0 || text.Length - first - (point > first ? 1 : 0) <= 28;
    }

    /// <summary>
    /// A number as ±0.<see cref="Digits"/> × 10^<see cref="Magnitude"/>: its significant digits as written, from the first
    /// one that isn't zero to the last (with the point when it falls between them; nothing for zero), and the power of ten
    /// just above them.
    /// </summary>
    private readonly ref struct Exact(int sign, ReadOnlySpan<byte> digits, BigInteger magnitude)
    {
        /// <summary>-1, 0 or 1.</summary>
        public int Sign { get; } = sign;

        public bool IsZero => Sign == 0;

        public ReadOnlySpan<byte> Digits { get; } = digits;

        public BigInteger Magnitude { get; } = magnitude;

        /// <summary>How many significant digits there are.</summary>
        public int Count => Digits.Length - (Digits.Contains((byte)'.') ? 1 : 0);

        public bool IsInteger => IsZero || Magnitude >= Count;

        /// <summary>Reads a number as JSON writes it (<c>-?int(.frac)?([eE][+-]?exp)?</c>; the reader has checked it).</summary>
        public static Exact Parse(ReadOnlySpan<byte> text)
        {
            var e = text.IndexOfAny((byte)'e', (byte)'E');
            var mantissa = e < 0 ? text : text[..e];
            var first = mantissa.IndexOfAnyExcept("-0."u8);
            if (first < 0)
                return new Exact(0, [], BigInteger.Zero);
            var last = mantissa.LastIndexOfAnyExcept("0."u8);

            var exponent = BigInteger.Zero;
            if (e >= 0)
            {
                var digits = text[(e + 1)..];
                var negative = digits[0] == (byte)'-';
                if (digits[0] is (byte)'-' or (byte)'+')
                    digits = digits[1..];
                exponent = digits.Length <= 18 ? long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture)
                    : BigInteger.Parse(Encoding.ASCII.GetString(digits), NumberStyles.None, CultureInfo.InvariantCulture);
                if (negative)
                    exponent = -exponent;
            }

            // The integral digits from the first significant one, or minus the zeros after the point before it.
            var point = mantissa.IndexOf((byte)'.');
            var integralEnd = point < 0 ? mantissa.Length : point;
            var above = first < integralEnd ? integralEnd - first : -(first - point - 1);
            return new Exact(mantissa[0] == (byte)'-' ? -1 : 1, mantissa[first..(last + 1)], above + exponent);
        }

        public static int Compare(Exact a, Exact b)
        {
            if (a.Sign != b.Sign)
                return a.Sign.CompareTo(b.Sign);
            if (a.IsZero)
                return 0;
            var magnitude = a.Magnitude.CompareTo(b.Magnitude);
            if (magnitude == 0)
                magnitude = CompareDigits(a.Digits, b.Digits);
            return a.Sign * magnitude;
        }

        /// <summary>
        /// Significant digits in order, the point skipped: equal up to the shorter, the longer is larger (its last digit
        /// isn't zero).
        /// </summary>
        private static int CompareDigits(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Split(a, out var a1, out var a2);
            Split(b, out var b1, out var b2);
            while (true)
            {
                if (a1.IsEmpty)
                {
                    a1 = a2;
                    a2 = [];
                }

                if (b1.IsEmpty)
                {
                    b1 = b2;
                    b2 = [];
                }

                if (a1.IsEmpty || b1.IsEmpty)
                    return (a1.IsEmpty ? 0 : 1) - (b1.IsEmpty ? 0 : 1);
                var n = Math.Min(a1.Length, b1.Length);
                var c = a1[..n].SequenceCompareTo(b1[..n]);
                if (c != 0)
                    return Math.Sign(c);
                a1 = a1[n..];
                b1 = b1[n..];
            }
        }

        private static void Split(ReadOnlySpan<byte> digits, out ReadOnlySpan<byte> before, out ReadOnlySpan<byte> after)
        {
            var point = digits.IndexOf((byte)'.');
            before = point < 0 ? digits : digits[..point];
            after = point < 0 ? [] : digits[(point + 1)..];
        }
    }
}
