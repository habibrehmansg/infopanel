using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InfoPanel.Spec;

/// <summary>
/// The value grammar of the SVG profile (spec/docs/svg.md, "Values"): each attribute and <c>style</c> property is parsed
/// by its own strict grammar and regenerated from the parsed form, so the rebuilt document holds only canonical values.
/// Every method returns the canonical text, or null when the value doesn't parse.
/// </summary>
internal static class SvgValues
{
    /// <summary>Largest magnitude of a number in the profile: <c>svgNumber</c>; a number beyond it doesn't parse.</summary>
    public static readonly double MaxMagnitude = SpecLimits.Default.SvgNumber;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------ numbers and lengths

    /// <summary>A finite number of magnitude at most <see cref="MaxMagnitude"/>, as at most 7 significant digits without an exponent.</summary>
    public static string? Num(double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) > MaxMagnitude) return null;
        return ((decimal)(float)v).ToString("0.############################", Inv);
    }

    /// <summary>
    /// Reads a number at <paramref name="i"/>: <c>[+-]? (digits [. digits?] | . digits) ([eE] [+-]? digits)?</c>.
    /// False (and <paramref name="i"/> unchanged) when there is none.
    /// </summary>
    public static bool ReadNumber(string s, ref int i, out double value)
    {
        value = 0;
        var start = i;
        if (i < s.Length && s[i] is '+' or '-') i++;
        var digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) { i = start; return false; }
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            var mark = i;
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            var expDigits = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; expDigits++; }
            if (expDigits == 0) i = mark;
        }
        if (double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, Inv, out value) && double.IsFinite(value)) return true;
        i = start;
        return false;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && IsSpace(s[i])) i++;
    }

    /// <summary>White space, then at most one comma and white space.</summary>
    private static void SkipCommaSpace(string s, ref int i)
    {
        SkipSpace(s, ref i);
        if (i < s.Length && s[i] == ',') { i++; SkipSpace(s, ref i); }
    }

    /// <summary>A whole value that is one number (white space around it allowed).</summary>
    private static bool WholeNumber(string s, out double v)
    {
        var i = 0;
        SkipSpace(s, ref i);
        if (!ReadNumber(s, ref i, out v)) return false;
        SkipSpace(s, ref i);
        return i == s.Length;
    }

    /// <summary>A number.</summary>
    public static string? Number(string s, double min = double.NegativeInfinity) =>
        WholeNumber(s, out var v) && v >= min ? Num(v) : null;

    /// <summary>A length: a number, unitless or in <c>px</c> (any case), or a percentage. Other units don't parse.</summary>
    public static string? Length(string s, bool nonNegative)
    {
        var t = s.Trim();
        var percent = t.EndsWith('%');
        if (percent) t = t[..^1];
        else if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
        var i = 0;
        if (!ReadNumber(t, ref i, out var v) || i != t.Length) return null;
        if (nonNegative && v < 0) return null;
        var n = Num(v);
        return n is null ? null : percent ? n + "%" : n;
    }

    /// <summary>An opacity: a number or a percentage, clamped to 0..1.</summary>
    public static string? Opacity(string s)
    {
        var t = s.Trim();
        var percent = t.EndsWith('%');
        if (percent) t = t[..^1];
        var i = 0;
        if (!ReadNumber(t, ref i, out var v) || i != t.Length) return null;
        if (percent) v /= 100;
        return Num(Math.Clamp(v, 0, 1));
    }

    /// <summary>A list of numbers separated by white space and commas; <paramref name="count"/> numbers read.</summary>
    public static string? Numbers(string s, out int count)
    {
        count = 0;
        var sb = new StringBuilder();
        var i = 0;
        SkipSpace(s, ref i);
        while (i < s.Length)
        {
            if (count > 0)
            {
                var end = i;
                SkipSpace(s, ref i);
                var comma = i < s.Length && s[i] == ',';
                if (comma) { i++; SkipSpace(s, ref i); }
                if (i >= s.Length)
                {
                    if (comma) return null; // a trailing comma
                    break;
                }
                // Without a separator, the next number must start with a sign or a point ("1-2", "0.5.5").
                if (i == end && s[i] is not ('+' or '-' or '.')) return null;
            }
            if (!ReadNumber(s, ref i, out var v)) return null;
            var n = Num(v);
            if (n is null) return null;
            if (count++ > 0) sb.Append(' ');
            sb.Append(n);
        }
        return sb.ToString();
    }

    /// <summary><c>points</c>: pairs of numbers (an even count).</summary>
    public static string? Points(string s, out int count)
    {
        var list = Numbers(s, out count);
        return list is not null && count % 2 == 0 ? list : null;
    }

    /// <summary>
    /// <c>viewBox</c>: four numbers, the width and height not negative as written. <paramref name="width"/> and <paramref name="height"/>
    /// are as rebuilt; <paramref name="writtenWidth"/> and <paramref name="writtenHeight"/> as written, before rounding.
    /// </summary>
    public static string? ViewBox(string s, out double width, out double height, out double writtenWidth, out double writtenHeight)
    {
        width = height = writtenWidth = writtenHeight = 0;
        var list = Numbers(s, out var count);
        if (list is null || count != 4) return null;
        var parts = list.Split(' ');
        width = double.Parse(parts[2], Inv);
        height = double.Parse(parts[3], Inv);
        // The list parsed, so the numbers read again one by one: four, each followed by white space and a comma at most.
        var i = 0;
        var written = new double[4];
        SkipSpace(s, ref i);
        for (var n = 0; n < 4; n++)
        {
            ReadNumber(s, ref i, out written[n]);
            SkipCommaSpace(s, ref i);
        }
        (writtenWidth, writtenHeight) = (written[2], written[3]);
        // The sign as written: rebuilt, a side such as -1e-29 would be 0 and pass.
        return writtenWidth < 0 || writtenHeight < 0 ? null : list;
    }

    /// <summary>A gradient stop's <c>offset</c>: a number or a percentage, clamped to 0..1.</summary>
    public static string? Offset(string s) => Opacity(s);

    /// <summary>
    /// A transform list: <c>matrix(6)</c>, <c>translate(1 or 2)</c>, <c>scale(1 or 2)</c>, <c>rotate(1 or 3)</c>,
    /// <c>skewX(1)</c>, <c>skewY(1)</c>, separated by white space or commas; names compared with case.
    /// </summary>
    public static string? Transform(string s)
    {
        var sb = new StringBuilder();
        var i = 0;
        var first = true;
        SkipSpace(s, ref i);
        while (i < s.Length)
        {
            if (!first)
            {
                SkipCommaSpace(s, ref i);
                if (i >= s.Length) return null;
            }
            var start = i;
            while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
            var name = s[start..i];
            (int A, int B, int C) arities = name switch
            {
                "matrix" => (6, 6, 6),
                "translate" or "scale" => (1, 2, 2),
                "rotate" => (1, 3, 3),
                "skewX" or "skewY" => (1, 1, 1),
                _ => (-1, -1, -1),
            };
            if (arities.A < 0) return null;
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != '(') return null;
            i++;
            SkipSpace(s, ref i);
            var args = new List<string>(6);
            while (i < s.Length && s[i] != ')')
            {
                if (args.Count > 0)
                {
                    SkipCommaSpace(s, ref i);
                    if (i < s.Length && s[i] == ')') return null;
                }
                if (!ReadNumber(s, ref i, out var v)) return null;
                var n = Num(v);
                if (n is null || args.Count == 6) return null;
                args.Add(n);
                SkipSpace(s, ref i);
            }
            if (i >= s.Length) return null;
            i++; // ')'
            if (args.Count != arities.A && args.Count != arities.B && args.Count != arities.C) return null;
            if (!first) sb.Append(' ');
            sb.Append(name).Append('(').AppendJoin(' ', args).Append(')');
            first = false;
            SkipSpace(s, ref i);
        }
        return sb.ToString();
    }

    /// <summary><c>preserveAspectRatio</c>: <c>none</c> or an alignment, optionally followed by <c>meet</c> or <c>slice</c>.</summary>
    public static string? AspectRatio(string s)
    {
        var parts = s.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return null;
        if (parts[0] is not ("none" or "xMinYMin" or "xMidYMin" or "xMaxYMin" or "xMinYMid" or "xMidYMid" or "xMaxYMid"
            or "xMinYMax" or "xMidYMax" or "xMaxYMax")) return null;
        if (parts.Length == 2 && parts[1] is not ("meet" or "slice")) return null;
        return string.Join(' ', parts);
    }

    /// <summary>One of <paramref name="words"/>, compared with case (structure attributes).</summary>
    public static string? Exact(string s, params string[] words)
    {
        var t = s.Trim();
        return Array.IndexOf(words, t) >= 0 ? t : null;
    }

    /// <summary>One of <paramref name="words"/> (lower case), compared without ASCII case (CSS keywords).</summary>
    public static string? Keyword(string s, params string[] words)
    {
        var t = s.Trim().ToLowerInvariant();
        return Array.IndexOf(words, t) >= 0 ? t : null;
    }

    // ------------------------------------------------------------ references and paint

    /// <summary>
    /// <c>url(#id)</c> at <paramref name="i"/>: <c>url</c> in any case, white space inside the parentheses, the target
    /// optionally quoted, the id an XML name. Leaves <paramref name="i"/> after the <c>)</c>.
    /// </summary>
    private static bool ReadUrl(string s, ref int i, out string id)
    {
        id = "";
        if (i + 4 > s.Length || !s.AsSpan(i, 4).Equals("url(", StringComparison.OrdinalIgnoreCase)) return false;
        i += 4;
        SkipSpace(s, ref i);
        char quote = i < s.Length && s[i] is '"' or '\'' ? s[i++] : '\0';
        if (i >= s.Length || s[i] != '#') return false;
        var start = ++i;
        while (i < s.Length && System.Xml.XmlConvert.IsNCNameChar(s[i])) i++;
        id = s[start..i];
        if (quote != '\0' && (i >= s.Length || s[i++] != quote)) return false;
        SkipSpace(s, ref i);
        if (!SvgProfile.IsName(id) || i >= s.Length || s[i] != ')') return false;
        i++;
        return true;
    }

    /// <summary><c>clip-path</c> and <c>mask</c>: <c>none</c> or <c>url(#id)</c> (<paramref name="id"/> null for none).</summary>
    public static string? Reference(string s, out string? id)
    {
        id = null;
        var t = s.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        var i = 0;
        if (!ReadUrl(t, ref i, out var target) || i != t.Length) return null;
        id = target;
        return $"url(#{target})";
    }

    /// <summary>
    /// <c>fill</c> and <c>stroke</c>: <c>none</c>, <c>currentColor</c>, a colour, or <c>url(#id)</c> optionally followed by
    /// a colour or <c>currentColor</c> (<paramref name="id"/> the gradient, or null).
    /// </summary>
    public static string? Paint(string s, out string? id)
    {
        id = null;
        var t = s.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        var i = 0;
        if (t.Length >= 4 && t.AsSpan(0, 4).Equals("url(", StringComparison.OrdinalIgnoreCase))
        {
            if (!ReadUrl(t, ref i, out var target)) return null;
            id = target;
            SkipSpace(t, ref i);
            if (i == t.Length) return $"url(#{target})";
            var fallback = ColorOrCurrent(t[i..]);
            return fallback is null ? null : $"url(#{target}) {fallback}";
        }
        return ColorOrCurrent(t);
    }

    /// <summary>A colour or <c>currentColor</c>.</summary>
    public static string? ColorOrCurrent(string s)
    {
        var t = s.Trim();
        return t.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? "currentColor" : Color(t);
    }

    // ------------------------------------------------------------ colours

    /// <summary>
    /// A colour: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>/<c>rgba()</c>,
    /// <c>hsl()</c>/<c>hsla()</c> (comma or space syntax, optional alpha), a CSS named colour or <c>transparent</c>.
    /// Emitted as <c>#rrggbb</c> when opaque, else as <c>rgba(r,g,b,a)</c> (the renderer doesn't read <c>#rrggbbaa</c>).
    /// </summary>
    public static string? Color(string s)
    {
        var t = s.Trim();
        if (!TryColor(t, out var r, out var g, out var b, out var a)) return null;
        if (a == 255) return $"#{r:x2}{g:x2}{b:x2}";
        var alpha = Num(Math.Round(a / 255.0, 5))!;
        return $"rgba({r},{g},{b},{alpha})";
    }

    private static bool TryColor(string t, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0;
        a = 255;
        if (t.Length == 0) return false;
        if (t[0] == '#')
        {
            var hex = t[1..];
            foreach (var c in hex) if (!char.IsAsciiHexDigit(c)) return false;
            int H(int k) => Convert.ToInt32(hex.Substring(k, 1), 16) * 17;
            int HH(int k) => Convert.ToInt32(hex.Substring(k, 2), 16);
            switch (hex.Length)
            {
                case 3: r = (byte)H(0); g = (byte)H(1); b = (byte)H(2); return true;
                case 4: r = (byte)H(0); g = (byte)H(1); b = (byte)H(2); a = (byte)H(3); return true;
                case 6: r = (byte)HH(0); g = (byte)HH(2); b = (byte)HH(4); return true;
                case 8: r = (byte)HH(0); g = (byte)HH(2); b = (byte)HH(4); a = (byte)HH(6); return true;
                default: return false;
            }
        }
        var open = t.IndexOf('(');
        if (open < 0)
        {
            var name = t.ToLowerInvariant();
            if (name == "transparent") { a = 0; return true; }
            if (!Named.TryGetValue(name, out var rgb)) return false;
            r = (byte)(rgb >> 16); g = (byte)(rgb >> 8); b = (byte)rgb;
            return true;
        }
        var fn = t[..open].ToLowerInvariant();
        if (fn is not ("rgb" or "rgba" or "hsl" or "hsla") || t[^1] != ')') return false;
        if (!ColorArgs(t[(open + 1)..^1], fn[0] == 'h', out var args, out var percent)) return false;
        double alpha = 1;
        if (args.Count == 4) alpha = Math.Clamp(percent[3] ? args[3] / 100 : args[3], 0, 1);
        else if (args.Count != 3) return false;
        if (fn[0] == 'r')
        {
            if (percent[0] != percent[1] || percent[1] != percent[2]) return false;
            byte C(int k) => (byte)Math.Round(Math.Clamp(percent[k] ? args[k] * 2.55 : args[k], 0, 255));
            r = C(0); g = C(1); b = C(2);
        }
        else
        {
            if (percent[0] || !percent[1] || !percent[2]) return false;
            Hsl(args[0], Math.Clamp(args[1] / 100, 0, 1), Math.Clamp(args[2] / 100, 0, 1), out r, out g, out b);
        }
        a = (byte)Math.Round(alpha * 255);
        return true;
    }

    /// <summary>
    /// The arguments of a colour function: three numbers separated by commas, optionally a fourth (the alpha); or three
    /// separated by white space, optionally followed by <c>/ alpha</c>. A hue (<paramref name="hue"/>) may end in <c>deg</c>.
    /// </summary>
    private static bool ColorArgs(string s, bool hue, out List<double> args, out List<bool> percent)
    {
        args = new List<double>(4);
        percent = new List<bool>(4);
        var i = 0;
        SkipSpace(s, ref i);
        bool? commas = null;
        var slash = false;
        while (i < s.Length)
        {
            if (args.Count > 0)
            {
                var end = i;
                SkipSpace(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ',')
                {
                    if (commas == false) return false;
                    commas = true;
                    i++;
                    SkipSpace(s, ref i);
                }
                else if (s[i] == '/')
                {
                    if (commas == true || args.Count != 3) return false;
                    slash = true;
                    i++;
                    SkipSpace(s, ref i);
                }
                else
                {
                    if (commas == true || i == end) return false;
                    commas = false;
                }
            }
            if (args.Count == 4 || !ReadNumber(s, ref i, out var v)) return false;
            var isPercent = i < s.Length && s[i] == '%';
            if (isPercent) i++;
            else if (hue && args.Count == 0 && i + 3 <= s.Length && s.AsSpan(i, 3).Equals("deg", StringComparison.OrdinalIgnoreCase)) i += 3;
            args.Add(v);
            percent.Add(isPercent);
        }
        if (args.Count == 4 && commas == false && !slash) return false;
        return args.Count is 3 or 4;
    }

    private static void Hsl(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        h = (h % 360 + 360) % 360 / 360;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        r = (byte)Math.Round(Channel(h + 1.0 / 3) * 255);
        g = (byte)Math.Round(Channel(h) * 255);
        b = (byte)Math.Round(Channel(h - 1.0 / 3) * 255);
    }

    /// <summary>The CSS named colours (CSS Color 4), lower case, as 0xRRGGBB.</summary>
    internal static readonly IReadOnlyDictionary<string, int> Named = BuildNamed(
        "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 " +
        "black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 " +
        "chartreuse 7fff00 chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff " +
        "darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 " +
        "darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 " +
        "darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f " +
        "darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 " +
        "dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc " +
        "ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f grey 808080 " +
        "honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa " +
        "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff " +
        "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 " +
        "lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 " +
        "lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 " +
        "mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 " +
        "mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 " +
        "midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 " +
        "oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa " +
        "palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f " +
        "pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f " +
        "royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee " +
        "sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa " +
        "springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 " +
        "violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32");

    private static Dictionary<string, int> BuildNamed(string table)
    {
        var parts = table.Split(' ');
        var map = new Dictionary<string, int>(parts.Length / 2, StringComparer.Ordinal);
        for (var k = 0; k < parts.Length; k += 2) map.Add(parts[k], Convert.ToInt32(parts[k + 1], 16));
        return map;
    }
}
