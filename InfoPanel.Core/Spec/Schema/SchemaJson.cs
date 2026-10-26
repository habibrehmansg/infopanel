using System;
using System.Collections.Generic;
using System.Globalization;
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

    public static bool IsInteger(JsonElement v)
    {
        if (v.TryGetDecimal(out var d)) return d == decimal.Truncate(d);
        var x = ToDouble(v);
        return double.IsFinite(x) && x == Math.Floor(x);
    }

    public static double ToDouble(JsonElement v) =>
        v.TryGetDouble(out var x) ? x : double.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Compares two JSON numbers as decimals when both fit, else as doubles.</summary>
    public static int CompareNumbers(JsonElement a, JsonElement b) =>
        a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x.CompareTo(y) : ToDouble(a).CompareTo(ToDouble(b));

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
    /// so content can't be built to collide. Numbers hash by both halves of their double value (-0 as 0): numbers that
    /// differ only past a double's precision still share a hash, which is why every comparison is charged by the caller.
    /// </summary>
    public static int Hash(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                var x = v.TryGetDecimal(out var d) ? (double)d : ToDouble(v);
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
