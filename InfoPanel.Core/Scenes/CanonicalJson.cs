using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InfoPanel.Scenes;

/// <summary>
/// RFC 8785 (JCS) canonical JSON: members sorted by their UTF-16 code units, no whitespace, strings with the minimal
/// escapes, numbers in the ECMAScript shortest form. A state cell's definition is keyed by it (spec/docs/identity.md).
/// </summary>
internal static class CanonicalJson
{
    public static string Write(JsonElement e)
    {
        var sb = new StringBuilder();
        Write(e, sb, 0);
        return sb.ToString();
    }

    private static void Write(JsonElement e, StringBuilder sb, int depth)
    {
        if (depth > 512) throw new InvalidOperationException("JSON nested too deeply.");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(p.Name, sb);
                    sb.Append(':');
                    Write(p.Value, sb, depth + 1);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (i++ > 0) sb.Append(',');
                    Write(item, sb, depth + 1);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: WriteString(e.GetString()!, sb); break;
            case JsonValueKind.Number: sb.Append(Number(e.GetDouble())); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            default: sb.Append("null"); break;
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>A double as ECMAScript's Number.prototype.toString writes it.</summary>
    internal static string Number(double d)
    {
        if (d == 0) return "0";
        if (!double.IsFinite(d)) return "null";
        // Shortest round-trip digits: "R" gives them; normalise to digits and a decimal exponent.
        var s = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var ePos = s.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(s[(ePos + 1)..], CultureInfo.InvariantCulture);
            s = s[..ePos];
        }
        var point = s.IndexOf('.');
        string digits;
        int n; // position of the decimal point relative to the digits
        if (point >= 0)
        {
            digits = s.Remove(point, 1);
            n = point + exp;
        }
        else
        {
            digits = s;
            n = s.Length + exp;
        }
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
            n--;
        }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0) return "0";
        var k = digits.Length;
        var sb = new StringBuilder();
        if (d < 0) sb.Append('-');
        if (k <= n && n <= 21) sb.Append(digits).Append('0', n - k);
        else if (0 < n && n <= 21) sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        else if (-6 < n && n <= 0) sb.Append("0.").Append('0', -n).Append(digits);
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
