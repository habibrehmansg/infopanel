using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InfoPanel.Spec.Schema;

/// <summary>JSON helpers shared by the schema nodes and the document's change detection: type tests, deep equality, hashing, pointer escaping.</summary>
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

    /// <summary>
    /// Deep JSON equality as the formats define it (spec/docs/validation.md#numbers): the same structure, strings and
    /// member names, object member order irrelevant, and numbers equal as the doubles nearest their text.
    /// </summary>
    public static bool DeepEquals(JsonElement a, JsonElement b) => Equal(a, b, null);

    /// <summary>
    /// As <see cref="DeepEquals(JsonElement, JsonElement)"/>, charging one step of <paramref name="run"/>'s work budget
    /// for each pair of values compared, as they are compared: false as soon as the budget is spent (the caller tells
    /// that apart by <see cref="SchemaRun.Exhausted"/>).
    /// </summary>
    internal static bool DeepEquals(JsonElement a, JsonElement b, SchemaRun run) => Equal(a, b, run);

    /// <summary>
    /// Compares each pair of values at most once, so the work is linear in the values' size: objects member by member in
    /// order while their names agree, then the members after that prefix by name through a dictionary (never the
    /// prefix again: comparing it twice doubled the work at every level of nested reordered objects).
    /// </summary>
    private static bool Equal(JsonElement a, JsonElement b, SchemaRun? run)
    {
        if (run is not null && !run.ChargeSteps(1)) return false;
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            // The same text is the same value (the common case, read without converting); otherwise by value.
            case JsonValueKind.Number:
                return JsonMarshal.GetRawUtf8Value(a).SequenceEqual(JsonMarshal.GetRawUtf8Value(b)) || JsonNumber.Value(a) == JsonNumber.Value(b);
            case JsonValueKind.String:
                return JsonMarshal.GetRawUtf8Value(a).SequenceEqual(JsonMarshal.GetRawUtf8Value(b))
                    || string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using (var ea = a.EnumerateArray().GetEnumerator())
                using (var eb = b.EnumerateArray().GetEnumerator())
                    while (ea.MoveNext() && eb.MoveNext())
                        if (!Equal(ea.Current, eb.Current, run)) return false;
                return true;
            case JsonValueKind.Object:
                var count = a.GetPropertyCount();
                if (count != b.GetPropertyCount()) return false;
                var matched = 0;
                using (var ea = a.EnumerateObject().GetEnumerator())
                using (var eb = b.EnumerateObject().GetEnumerator())
                {
                    while (ea.MoveNext() && eb.MoveNext())
                    {
                        if (!string.Equals(ea.Current.Name, eb.Current.Name, StringComparison.Ordinal)) break;
                        if (!Equal(ea.Current.Value, eb.Current.Value, run)) return false;
                        matched++;
                    }
                }
                if (matched == count) return true;
                // The first `matched` members are equal pairs; the rest of each object is compared by name.
                var byName = new Dictionary<string, JsonElement>(count - matched, StringComparer.Ordinal);
                foreach (var p in b.EnumerateObject().Skip(matched)) byName.TryAdd(p.Name, p.Value);
                foreach (var p in a.EnumerateObject().Skip(matched))
                    if (!byName.TryGetValue(p.Name, out var bv) || !Equal(p.Value, bv, run)) return false;
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// A hash consistent with <see cref="DeepEquals(JsonElement, JsonElement)"/>, seeded per process (<see cref="HashCode"/>),
    /// so content can't be built to collide. A number hashes by its double, which is the same for every spelling of it.
    /// </summary>
    public static int Hash(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                var bits = BitConverter.DoubleToInt64Bits(JsonNumber.Value(v));
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
/// JSON numbers as the formats read them (spec/docs/validation.md#numbers): a number is the IEEE 754 double nearest its
/// text, <c>-0</c> is 0, and text that rounds to ±∞ is not a number the formats accept. The schema validator's
/// <c>type: integer</c>, bounds and equality, and whatever must decide a number as it does, read numbers through this.
/// </summary>
public static class JsonNumber
{
    /// <summary>The double nearest the number's text (±∞ when it rounds there), <c>-0</c> as 0.</summary>
    public static double Value(JsonElement v) => Parse(JsonMarshal.GetRawUtf8Value(v));

    /// <summary>The double nearest the text of a JSON number (as JSON writes it), ±∞ when it rounds there, <c>-0</c> as 0.</summary>
    public static double Parse(ReadOnlySpan<byte> utf8)
    {
        var x = double.Parse(utf8, NumberStyles.Float, CultureInfo.InvariantCulture);
        return x == 0 ? 0 : x;
    }

    /// <summary>The number as a double (<see cref="Value"/>); false, with 0, when its text rounds to ±∞.</summary>
    public static bool TryRead(JsonElement v, out double value)
    {
        value = Value(v);
        if (double.IsFinite(value)) return true;
        value = 0;
        return false;
    }

    /// <summary>The number as a double when it is a whole one (1, 1.0, 1e2, 1.00000000000000001); false otherwise.</summary>
    public static bool TryReadInteger(JsonElement v, out double value) => TryRead(v, out value) && Math.Floor(value) == value;

    /// <summary>Whether <paramref name="v"/> (a JSON number) is a whole number as a double, in any spelling.</summary>
    public static bool IsInteger(JsonElement v) => TryReadInteger(v, out _);
}

/// <summary>
/// JSON strings as the formats read them (spec/docs/validation.md#json): Unicode text, a sequence of scalar values. A lone
/// surrogate escape (<c>"\ud800"</c>) or bytes that aren't UTF-8 are not.
/// </summary>
public static class JsonText
{
    /// <summary>
    /// Whether the raw text of a JSON string or member name (as received, escapes undecoded, with or without its quotes)
    /// is Unicode text: well-formed UTF-8, and every <c>\u</c> escape of a surrogate one half of a pair.
    /// </summary>
    public static bool IsScalarText(ReadOnlySpan<byte> raw)
    {
        if (!System.Text.Unicode.Utf8.IsValid(raw)) return false;
        var i = raw.IndexOf((byte)'\\');
        if (i < 0) return true;
        for (; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\') continue;
            i++; // the escaped character: a quote, a backslash, ... or u and four hex digits
            if (i >= raw.Length || raw[i] != (byte)'u') continue;
            var unit = Hex4(raw, i + 1);
            i += 4;
            if (unit is >= 0xDC00 and <= 0xDFFF) return false;
            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 6 >= raw.Length) return false;
                if (raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u' || Hex4(raw, i + 3) is < 0xDC00 or > 0xDFFF) return false;
                i += 6;
            }
        }
        return true;
    }

    /// <summary>
    /// The UTF-16 unit of four hex digits at <paramref name="at"/>; -1 when they aren't there (the parser has already
    /// refused a malformed escape, so this only guards the bounds).
    /// </summary>
    private static int Hex4(ReadOnlySpan<byte> raw, int at)
    {
        if (at + 4 > raw.Length) return -1;
        var unit = 0;
        for (var k = at; k < at + 4; k++)
        {
            var c = raw[k];
            var d = c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;
            if (d < 0) return -1;
            unit = unit * 16 + d;
        }
        return unit;
    }

    /// <summary>The string's value; null when it isn't Unicode text (<see cref="IsScalarText"/>), where <c>GetString</c> throws.</summary>
    public static string? TryGetString(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && IsScalarText(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(v)) ? v.GetString() : null;
}
