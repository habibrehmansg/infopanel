using System;
using System.Globalization;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>
/// SVG 1.1 path data (M L H V C S Q T A Z, absolute and relative, implicit repeats, exponents, compact arc flags) into
/// an <see cref="SKPath"/>, with the grammar <see cref="Spec.PathData"/> validates. Bounded: at most
/// <paramref name="maxSegments"/> segments are read; anything after a syntax error is ignored.
/// </summary>
internal static class PathParser
{
    public static SKPath Parse(string d, int maxSegments = 65_536)
    {
        var path = new SKPath();
        var i = 0;
        var segments = 0;
        float cx = 0, cy = 0;          // current point
        float sx = 0, sy = 0;          // subpath start
        float lcx = 0, lcy = 0;        // last control point (for S and T)
        var last = ' ';
        var command = ' ';

        while (true)
        {
            SkipSpaceComma(d, ref i);
            if (i >= d.Length) break;
            var c = d[i];
            if (IsCommand(c))
            {
                command = c;
                i++;
            }
            else if (command == ' ' || !StartsNumber(c))
            {
                break; // invalid; validation refuses it
            }
            else if (command is 'M') command = 'L';
            else if (command is 'm') command = 'l';
            else if (command is 'Z' or 'z') break;

            if (++segments > maxSegments) break;
            var rel = char.IsLower(command);
            var ox = rel ? cx : 0;
            var oy = rel ? cy : 0;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    if (!Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    cx = ox + x; cy = oy + y; sx = cx; sy = cy;
                    path.MoveTo(cx, cy);
                    break;
                }
                case 'L':
                {
                    if (!Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    cx = ox + x; cy = oy + y;
                    path.LineTo(cx, cy);
                    break;
                }
                case 'H':
                {
                    if (!Num(d, ref i, out var x)) return path;
                    cx = ox + x;
                    path.LineTo(cx, cy);
                    break;
                }
                case 'V':
                {
                    if (!Num(d, ref i, out var y)) return path;
                    cy = (rel ? cy : 0) + y;
                    path.LineTo(cx, cy);
                    break;
                }
                case 'C':
                {
                    if (!Num(d, ref i, out var x1) || !Num(d, ref i, out var y1) || !Num(d, ref i, out var x2) || !Num(d, ref i, out var y2)
                        || !Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    path.CubicTo(ox + x1, oy + y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'S':
                {
                    if (!Num(d, ref i, out var x2) || !Num(d, ref i, out var y2) || !Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    var reflect = char.ToUpperInvariant(last) is 'C' or 'S';
                    var x1 = reflect ? 2 * cx - lcx : cx;
                    var y1 = reflect ? 2 * cy - lcy : cy;
                    path.CubicTo(x1, y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'Q':
                {
                    if (!Num(d, ref i, out var x1) || !Num(d, ref i, out var y1) || !Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    path.QuadTo(ox + x1, oy + y1, ox + x, oy + y);
                    lcx = ox + x1; lcy = oy + y1;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'T':
                {
                    if (!Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    var reflect = char.ToUpperInvariant(last) is 'Q' or 'T';
                    var x1 = reflect ? 2 * cx - lcx : cx;
                    var y1 = reflect ? 2 * cy - lcy : cy;
                    path.QuadTo(x1, y1, ox + x, oy + y);
                    lcx = x1; lcy = y1;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'A':
                {
                    if (!Num(d, ref i, out var rx) || !Num(d, ref i, out var ry) || !Num(d, ref i, out var rotation)
                        || !Flag(d, ref i, out var large) || !Flag(d, ref i, out var sweep)
                        || !Num(d, ref i, out var x) || !Num(d, ref i, out var y)) return path;
                    var tx = ox + x;
                    var ty = oy + y;
                    if (rx == 0 || ry == 0) path.LineTo(tx, ty);
                    else path.ArcTo(new SKPoint(Math.Abs(rx), Math.Abs(ry)), rotation,
                        large ? SKPathArcSize.Large : SKPathArcSize.Small,
                        sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, new SKPoint(tx, ty));
                    cx = tx; cy = ty;
                    break;
                }
                case 'Z':
                    path.Close();
                    cx = sx; cy = sy;
                    break;
                default:
                    return path;
            }
            last = command;
        }
        return path;
    }

    private static bool IsCommand(char c) => "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;

    private static bool StartsNumber(char c) => c is (>= '0' and <= '9') or '+' or '-' or '.';

    private static void SkipSpaceComma(string d, ref int i)
    {
        while (i < d.Length && (d[i] is ' ' or '\t' or '\n' or '\r' or '\f' or ',')) i++;
    }

    private static bool Flag(string d, ref int i, out bool flag)
    {
        SkipSpaceComma(d, ref i);
        flag = false;
        if (i >= d.Length || d[i] is not ('0' or '1')) return false;
        flag = d[i] == '1';
        i++;
        return true;
    }

    private static bool Num(string d, ref int i, out float value)
    {
        SkipSpaceComma(d, ref i);
        value = 0;
        var start = i;
        if (i < d.Length && d[i] is '+' or '-') i++;
        var digits = 0;
        while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; digits++; }
        if (i < d.Length && d[i] == '.')
        {
            i++;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; digits++; }
        }
        if (digits == 0)
        {
            i = start;
            return false;
        }
        if (i < d.Length && d[i] is 'e' or 'E')
        {
            var mark = i;
            i++;
            if (i < d.Length && d[i] is '+' or '-') i++;
            var expDigits = 0;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; expDigits++; }
            if (expDigits == 0) i = mark;
        }
        if (!double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        value = SceneMath.F(v);
        return true;
    }
}
