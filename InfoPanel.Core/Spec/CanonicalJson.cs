using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Spec;

/// <summary>
/// The formats' one canonical form (spec/docs/limits.md#sizes): RFC 8785 (JCS) JSON in UTF-8. Members sorted by their
/// names' UTF-16 code units, no whitespace, strings with only the escapes JCS gives, every number as the double nearest
/// its text (<c>-0</c> as 0; spec/docs/validation.md#numbers) in ECMAScript's shortest form. The one writer of it: every
/// content size (<c>sceneBytes</c>, <c>originBytes</c>, <c>editorBytes</c>, <c>sourceBytes</c>, <c>documentBytes</c>) is
/// its length (a document's without its revision metadata, <see cref="DocumentSize"/>), the store writes documents in it, and a state cell's definition is keyed by it (spec/docs/identity.md).
/// A value written here has passed the JSON-level pass (spec/docs/validation.md#json): no repeated member name, no lone
/// surrogate and no number that rounds to ±∞, which have no canonical form.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>Deeper than any value a reader accepts (<c>jsonDepth</c> is 256); bounds the recursion.</summary>
    private const int MaxDepth = 512;

    /// <summary>
    /// For tests: told of each value this thread measures or writes whole (not of the values inside it), so a test can
    /// count how often a commit measures a scene. Null unless a test sets it.
    /// </summary>
    [ThreadStatic]
    internal static Action<JsonElement>? Measuring;

    /// <summary>The canonical form of <paramref name="value"/>, in UTF-8.</summary>
    public static byte[] Utf8(JsonElement value)
    {
        Measuring?.Invoke(value);
        var output = new Output(keep: true);
        Write(value, output, 0);
        return output.ToArray();
    }

    /// <summary>The canonical form of a model value as <see cref="SpecJson"/> writes it.</summary>
    public static byte[] Utf8<T>(T value) => Utf8(SpecJson.Write(value));

    /// <summary>The length of <paramref name="value"/>'s canonical form in bytes, counted without writing it.</summary>
    public static long Size(JsonElement value)
    {
        Measuring?.Invoke(value);
        var output = new Output(keep: false);
        Write(value, output, 0);
        return output.Length;
    }

    /// <summary>The length of a model value's canonical form in bytes.</summary>
    public static long Size<T>(T value) => Size(SpecJson.Write(value));

    /// <summary>
    /// A document's size (<c>documentBytes</c>): the length of its canonical form without its revision metadata, the form
    /// it is stored in. That is the <c>rev</c> member of the document, of <c>profile</c> and of every item (groups'
    /// children included), and nothing else: a <c>rev</c> inside an opaque member (<c>scene</c>, <c>source</c>,
    /// <c>origin</c>, <c>editor</c>) is content and is measured.
    /// </summary>
    public static long DocumentSize(JsonElement document)
    {
        Measuring?.Invoke(document);
        var output = new Output(keep: false);
        Write(document, output, 0, Revisions.Document);
        return output.Length;
    }

    /// <summary>Where a value sits in a document, for <see cref="DocumentSize"/>: which of its members are revision metadata.</summary>
    private enum Revisions
    {
        /// <summary>None: content, written whole.</summary>
        None,
        /// <summary>The document: its <c>rev</c>; <c>profile</c> and <c>items</c> hold more.</summary>
        Document,
        /// <summary>The profile: its <c>rev</c>.</summary>
        Profile,
        /// <summary>An array of items (<c>items</c>, a group's <c>children</c>).</summary>
        Items,
        /// <summary>An item: its <c>rev</c>; a group's <c>children</c> hold more.</summary>
        Item,
    }

    /// <summary>The canonical form of <paramref name="value"/> as text (a state cell's key).</summary>
    public static string Write(JsonElement value) => Encoding.UTF8.GetString(Utf8(value));

    private static void Write(JsonElement e, Output output, int depth, Revisions revisions = Revisions.None)
    {
        if (depth > MaxDepth) throw new InvalidOperationException("JSON nested too deeply.");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(e, output, depth, revisions);
                break;
            case JsonValueKind.Array:
                output.Put((byte)'[');
                var first = true;
                var each = revisions == Revisions.Items ? Revisions.Item : Revisions.None;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first) output.Put((byte)',');
                    first = false;
                    Write(item, output, depth + 1, each);
                }
                output.Put((byte)']');
                break;
            case JsonValueKind.String:
                WriteString(Unquoted(JsonMarshal.GetRawUtf8Value(e)), e, output);
                break;
            case JsonValueKind.Number:
                WriteNumber(JsonMarshal.GetRawUtf8Value(e), output);
                break;
            case JsonValueKind.True:
                output.Put("true"u8);
                break;
            case JsonValueKind.False:
                output.Put("false"u8);
                break;
            case JsonValueKind.Null:
                output.Put("null"u8);
                break;
            default:
                throw new ArgumentException($"{e.ValueKind} is not a JSON value.", nameof(e));
        }
    }

    private static void WriteObject(JsonElement e, Output output, int depth, Revisions revisions)
    {
        var count = e.GetPropertyCount();
        var names = new string[count];
        var members = new JsonProperty[count];
        var i = 0;
        foreach (var p in e.EnumerateObject())
        {
            if (revisions != Revisions.None && p.NameEquals("rev"))
                continue;
            names[i] = p.Name;
            members[i++] = p;
        }
        if (i < count)
        {
            count = i;
            Array.Resize(ref names, count);
            Array.Resize(ref members, count);
        }
        // Ordinal is by UTF-16 code units, as RFC 8785 sorts (not by code points: U+1F600 sorts before U+FB33).
        Array.Sort(names, members, StringComparer.Ordinal);

        output.Put((byte)'{');
        for (i = 0; i < count; i++)
        {
            if (i > 0) output.Put((byte)',');
            WriteName(Unquoted(JsonMarshal.GetRawUtf8PropertyName(members[i])), names[i], output);
            output.Put((byte)':');
            Write(members[i].Value, output, depth + 1, Inner(revisions, names[i]));
        }
        output.Put((byte)'}');
    }

    /// <summary>Where a member of a value at <paramref name="revisions"/> sits (<see cref="DocumentSize"/>).</summary>
    private static Revisions Inner(Revisions revisions, string name) => (revisions, name) switch
    {
        (Revisions.Document, "profile") => Revisions.Profile,
        (Revisions.Document, "items") or (Revisions.Item, "children") => Revisions.Items,
        _ => Revisions.None,
    };

    /// <summary>A string or member name's raw text without its quotes, escapes undecoded.</summary>
    private static ReadOnlySpan<byte> Unquoted(ReadOnlySpan<byte> raw) =>
        raw.Length >= 2 && raw[0] == (byte)'"' ? raw[1..^1] : raw;

    /// <summary>
    /// A string as received without an escape is already canonical (JSON has no unescaped quote, backslash or control
    /// character in a string, and the JSON-level pass held it to UTF-8): its bytes are copied. Otherwise it is decoded and
    /// written with JCS's escapes.
    /// </summary>
    private static void WriteString(ReadOnlySpan<byte> raw, JsonElement e, Output output)
    {
        if (raw.IndexOf((byte)'\\') < 0)
        {
            output.Put((byte)'"');
            output.Put(raw);
            output.Put((byte)'"');
        }
        else WriteEscaped(e.GetString()!, output);
    }

    private static void WriteName(ReadOnlySpan<byte> raw, string name, Output output)
    {
        if (raw.IndexOf((byte)'\\') < 0)
        {
            output.Put((byte)'"');
            output.Put(raw);
            output.Put((byte)'"');
        }
        else WriteEscaped(name, output);
    }

    /// <summary><c>"</c> and <c>\</c> escaped, the C0 controls as <c>\b \t \n \f \r</c> or a lower-case <c>\u00xx</c>; everything else as it is.</summary>
    private static void WriteEscaped(string s, Output output)
    {
        output.Put((byte)'"');
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c >= 0x20 && c != '"' && c != '\\') continue;
            output.PutText(s.AsSpan(start, i - start));
            start = i + 1;
            output.Put((byte)'\\');
            switch (c)
            {
                case '"': output.Put((byte)'"'); break;
                case '\\': output.Put((byte)'\\'); break;
                case '\b': output.Put((byte)'b'); break;
                case '\f': output.Put((byte)'f'); break;
                case '\n': output.Put((byte)'n'); break;
                case '\r': output.Put((byte)'r'); break;
                case '\t': output.Put((byte)'t'); break;
                default:
                    output.Put((byte)'u');
                    output.Put((byte)'0');
                    output.Put((byte)'0');
                    output.Put(Hex[c >> 4]);
                    output.Put(Hex[c & 0xF]);
                    break;
            }
        }
        output.PutText(s.AsSpan(start));
        output.Put((byte)'"');
    }

    private static ReadOnlySpan<byte> Hex => "0123456789abcdef"u8;

    private static void WriteNumber(ReadOnlySpan<byte> raw, Output output)
    {
        if (IsCanonical(raw))
        {
            output.Put(raw);
            return;
        }
        var d = JsonNumber.Parse(raw);
        if (!double.IsFinite(d)) throw new InvalidOperationException("A number that rounds to ±∞ has no canonical form.");
        // .NET's "R" is the shortest round-trip text, as ECMAScript's; where it writes a plain decimal (no exponent), so
        // does ECMAScript, the same text. Otherwise the digits are placed as ECMAScript places them.
        Span<char> text = stackalloc char[32];
        if (d != 0 && d.TryFormat(text, out var length, "R", CultureInfo.InvariantCulture) && text[..length].IndexOfAny('E', 'e') < 0)
            output.PutAscii(text[..length]);
        else output.PutText(Number(d));
    }

    /// <summary>
    /// Whether a number's text is already its double's canonical form, so it needs no conversion: a decimal without an
    /// exponent, a trailing zero after the point or a sign on zero, with at most 15 significant digits (each such decimal
    /// is the shortest text of its double, IEEE 754's 15-digit round trip) and at most 5 zeros after the point before
    /// them (from 1e-6 up, ECMAScript writes it without an exponent). Anything else is converted.
    /// </summary>
    internal static bool IsCanonical(ReadOnlySpan<byte> raw)
    {
        var i = raw.Length > 0 && raw[0] == (byte)'-' ? 1 : 0;
        if (i >= raw.Length) return false;
        int whole = 0, zeros = 0;
        if (raw[i] == (byte)'0') i++;
        else
            while (i < raw.Length && IsDigit(raw[i]))
            {
                whole++;
                i++;
            }
        if (i == raw.Length) return whole == 0 ? raw.Length == 1 : whole <= 15;
        if (raw[i] != (byte)'.' || raw[^1] == (byte)'0') return false;
        var fraction = raw.Length - i - 1;
        for (var k = i + 1; k < raw.Length; k++)
        {
            if (!IsDigit(raw[k])) return false;
            if (whole == 0 && zeros == k - i - 1 && raw[k] == (byte)'0') zeros++;
        }
        return whole == 0 ? zeros <= 5 && fraction - zeros <= 15 : whole + fraction <= 15;

        static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
    }

    /// <summary>A double as ECMAScript's Number.prototype.toString writes it (RFC 8785, section 3.2.2.3).</summary>
    internal static string Number(double d)
    {
        if (!double.IsFinite(d)) throw new ArgumentOutOfRangeException(nameof(d), d, "A number that isn't finite has no canonical form.");
        if (d == 0) return "0";
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

    /// <summary>Where the canonical form goes: a growing buffer, or only its length (<c>keep</c> false).</summary>
    private sealed class Output(bool keep)
    {
        private byte[] _buffer = keep ? new byte[256] : [];

        public long Length { get; private set; }

        public void Put(byte b)
        {
            if (keep)
            {
                Ensure(1);
                _buffer[Length] = b;
            }
            Length++;
        }

        public void Put(ReadOnlySpan<byte> bytes)
        {
            if (keep)
            {
                Ensure(bytes.Length);
                bytes.CopyTo(_buffer.AsSpan((int)Length));
            }
            Length += bytes.Length;
        }

        /// <summary>ASCII text, one byte a character.</summary>
        public void PutAscii(ReadOnlySpan<char> text)
        {
            if (keep)
            {
                Ensure(text.Length);
                var target = _buffer.AsSpan((int)Length, text.Length);
                for (var i = 0; i < text.Length; i++) target[i] = (byte)text[i];
            }
            Length += text.Length;
        }

        /// <summary>Text, in UTF-8.</summary>
        public void PutText(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;
            if (keep)
            {
                Ensure(Encoding.UTF8.GetMaxByteCount(text.Length));
                Length += Encoding.UTF8.GetBytes(text, _buffer.AsSpan((int)Length));
            }
            else Length += Encoding.UTF8.GetByteCount(text);
        }

        private void Ensure(int more)
        {
            var needed = Length + more;
            if (needed <= _buffer.Length) return;
            if (needed > Array.MaxLength) throw new InvalidOperationException("The canonical form is too large for one buffer.");
            Array.Resize(ref _buffer, (int)Math.Min(Array.MaxLength, Math.Max(needed, 2L * _buffer.Length)));
        }

        public byte[] ToArray() => _buffer.AsSpan(0, (int)Length).ToArray();
    }
}
