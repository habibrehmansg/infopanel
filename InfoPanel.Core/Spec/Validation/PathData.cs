using System;

namespace InfoPanel.Spec;

/// <summary>
/// Checks SVG path data (the <c>d</c> of path nodes and clips) without building a path: the grammar of SVG 1.1 path data
/// (commands M L H V C S Q T A Z in both cases, implicit repeats, numbers with exponents, arc flags with or without
/// separators; the first command must be a moveto) and counts its segments (each command and each implicit repeat).
/// </summary>
public static class PathData
{
    /// <summary>The number of segments, or -1 when <paramref name="d"/> isn't valid path data.</summary>
    public static int CountSegments(string d) => Scan(d, null, out _);

    /// <summary>
    /// The path data re-serialised from its parsed commands (each segment with its command letter, numbers in the SVG
    /// profile's canonical form, <see cref="SvgValues.Num"/>), or null when it isn't valid path data or has a number the
    /// profile doesn't take; <paramref name="numbers"/> counts the numbers and flags read.
    /// </summary>
    internal static string? Rebuild(string d, out int numbers)
    {
        var sb = new System.Text.StringBuilder(d.Length);
        return Scan(d, sb, out numbers) < 0 ? null : sb.ToString();
    }

    private static int Scan(string d, System.Text.StringBuilder? sb, out int numbers)
    {
        numbers = 0;
        var i = 0;
        var segments = 0;
        var first = true;
        SkipSpace(d, ref i);
        while (i < d.Length)
        {
            var command = d[i];
            var arity = Arity(command);
            if (arity < 0) return -1;
            if (first && command is not ('M' or 'm')) return -1;
            first = false;
            i++;
            segments++;
            if (arity == 0)
            {
                sb?.Append(command);
                SkipSpace(d, ref i);
                continue;
            }

            var repeats = 0;
            while (true)
            {
                var mark = i;
                if (repeats > 0) SkipCommaSpace(d, ref i); else SkipSpace(d, ref i);
                if (i >= d.Length || !StartsNumber(d[i]))
                {
                    if (repeats == 0 || d.AsSpan(mark, i - mark).Contains(',')) return -1;
                    break;
                }
                if (repeats > 0) segments++;
                // An implicit repeat of a moveto is a lineto.
                sb?.Append(repeats == 0 ? command : command switch { 'M' => 'L', 'm' => 'l', _ => command });
                for (var k = 0; k < arity; k++)
                {
                    if (k > 0) SkipCommaSpace(d, ref i);
                    var isFlag = command is 'A' or 'a' && k is 3 or 4;
                    var flagAt = i;
                    double value = 0;
                    if (isFlag ? !ReadFlag(d, ref i) : !ReadNumber(d, ref i, out value)) return -1;
                    numbers++;
                    if (sb is null) continue;
                    if (k > 0) sb.Append(' ');
                    if (isFlag) sb.Append(d[flagAt]);
                    else if (SvgValues.Num(value) is { } text) sb.Append(text);
                    else return -1;
                }
                repeats++;
            }
        }
        return first ? -1 : segments;
    }

    private static int Arity(char c) => c switch
    {
        'M' or 'm' or 'L' or 'l' or 'T' or 't' => 2,
        'H' or 'h' or 'V' or 'v' => 1,
        'C' or 'c' => 6,
        'S' or 's' or 'Q' or 'q' => 4,
        'A' or 'a' => 7,
        'Z' or 'z' => 0,
        _ => -1,
    };

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static void SkipSpace(string d, ref int i)
    {
        while (i < d.Length && IsSpace(d[i])) i++;
    }

    private static void SkipCommaSpace(string d, ref int i)
    {
        SkipSpace(d, ref i);
        if (i < d.Length && d[i] == ',') { i++; SkipSpace(d, ref i); }
    }

    private static bool StartsNumber(char c) => c is >= '0' and <= '9' or '+' or '-' or '.';

    private static bool ReadFlag(string d, ref int i)
    {
        if (i < d.Length && d[i] is '0' or '1') { i++; return true; }
        return false;
    }

    private static bool ReadNumber(string d, ref int i, out double value) => SvgValues.ReadNumber(d, ref i, out value);
}
