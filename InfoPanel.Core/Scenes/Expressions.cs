using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using InfoPanel.Spec;

namespace InfoPanel.Scenes;

/// <summary>
/// One input's reading for a tick, with its history and the value its binding's statistic selects. A table reads its
/// first <paramref name="tableRows"/> rows, the reading runtime's limit (spec/docs/bindings.md, "Tables"): every
/// <c>table</c>, <c>cell</c> and <c>rows</c> of the tick reads <see cref="Table"/>, never the reading's own table.
/// </summary>
internal sealed class InputSnap
{
    public static readonly InputSnap Unbound = new(SceneReading.Unbound, "now", []);

    private readonly SceneTable _table;

    public InputSnap(SceneReading reading, string stat, IReadOnlyList<double> history, int? tableRows = null)
    {
        Reading = reading;
        History = history;
        var available = reading.Available;
        _table = available ? reading.Table.First(tableRows ?? SpecLimits.Default.TableRows) : SceneTable.Empty;
        Value = !available ? 0 : stat switch { "min" => reading.Min, "max" => reading.Max, "avg" => reading.Avg, _ => reading.Now };
        Status = reading.Status switch
        {
            SceneInputStatus.Unbound => "unbound",
            SceneInputStatus.Live => "live",
            SceneInputStatus.Stale => "stale",
            SceneInputStatus.Denied => "denied",
            _ => "unavailable",
        };
    }

    public SceneReading Reading { get; }
    public double Value { get; }
    public string Status { get; }
    public IReadOnlyList<double> History { get; }
    public bool Available => Reading.Available;
    public double Now => Available ? Reading.Now : 0;
    public double Min => Available ? Reading.Min : 0;
    public double Max => Available ? Reading.Max : 0;
    public double Avg => Available ? Reading.Avg : 0;
    public string Unit => Available ? Reading.Unit : "";
    public string Text => Available ? Reading.Text : "";
    public SceneTable Table => _table;

    /// <summary>
    /// The samples a <c>series</c> or a <c>history</c> reads, oldest first (spec/docs/bindings.md, "Samples"): without a
    /// column, the input's history; with one, the numbers of that column of <see cref="Table"/> (the tick's one reading,
    /// to the runtime's <c>tableRows</c>), one a row in row order, the last row newest, nothing filtered; none when the
    /// reading lacks the column. A view, not a copy: sample i is row i's number, the row made when first read and kept
    /// with the reading, so every reader of the column in the tick reads the same rows.
    /// </summary>
    public IReadOnlyList<double> Samples(SampleColumn? column)
    {
        if (column is null) return History;
        var table = _table;
        var c = column.Name is null ? column.Index : table.ColumnOf(column.Name);
        return c < 0 || c >= table.Columns.Count || table.Rows.Count == 0 ? [] : new ColumnSamples(table, c);
    }

    /// <summary>A column of a table reading as samples (<see cref="Samples"/>).</summary>
    private sealed class ColumnSamples(SceneTable table, int column) : IReadOnlyList<double>
    {
        public int Count => table.Rows.Count;

        public double this[int index] => (uint)index < (uint)Count ? table.Number(index, column) : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<double> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return table.Number(i, column);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// A table input's column read as samples (spec/docs/bindings.md, "Samples"): by <see cref="Name"/> when it is set, the
/// first column with it, else by <see cref="Index"/>.
/// </summary>
internal sealed record SampleColumn(int Index, string? Name)
{
    public static SampleColumn? Of(ColumnRef? column) => column is null ? null : new(column.Index ?? -1, column.Name);
}

/// <summary>The evaluation context of one item for one tick.</summary>
internal sealed class Ev
{
    public required Val[] Values;
    public required Val[] State;
    public required InputSnap[] Inputs;
    public required SceneClock Clock;
    public required double[] MeasureW;
    public required double[] MeasureH;
    public double BoxW, BoxH;
    /// <summary>The item's rotation on the canvas in degrees: its own and its groups', summed (what its bounds report).</summary>
    public double Rotation;
    public double Dt;
    public int Work;
    public int WorkLimit = int.MaxValue;
    public int TextLength = 4096;
    public ItemDiagnostics? Diagnostics;

    public string Cut(string s) => SceneText.Truncate(s, TextLength);

    public TimeZoneInfo ZoneOf(string? name)
    {
        if (name is null) return Clock.LocalZone;
        if (SceneTime.Zone(name) is { } zone) return zone;
        Diagnostics?.Add("time.zone", $"Unknown time zone '{name}'; the local zone is used.");
        return Clock.LocalZone;
    }
}

/// <summary>A compiled expression: a tree of typed operations, evaluated once per tick.</summary>
internal abstract class CExpr
{
    public Ty Type;

    public abstract Val Eval(Ev e);

    public double Num(Ev e) => Eval(e).N;
    public bool Bool(Ev e) => Eval(e).N != 0;
    public string Str(Ev e) => Eval(e).S ?? "";
    public uint Col(Ev e) => Eval(e).C;
}

internal sealed class ConstExpr(Val value, Ty type) : CExpr
{
    public Val Value { get; } = value;

    public ConstExpr Init()
    {
        Type = type;
        return this;
    }

    public override Val Eval(Ev e) => Value;
}

internal enum Op : byte
{
    Add, Mul, Min, Max, Sub, Div, Mod, Pow, Atan2, Neg, Abs, Floor, Ceil, Round, Sqrt, Exp, Log, Sin, Cos,
    Clamp, Norm, Lerp, LerpColor, Lt, Le, Gt, Ge, And, Or, Not, If, Concat, Upper, Lower, Rgba, Alpha,
}

internal sealed class CallExprC(Op op, CExpr[] args) : CExpr
{
    /// <summary>String-producing operations count 16 units of work (spec/docs/expressions.md, "Cost").</summary>
    public const int StringWork = 16;

    public override Val Eval(Ev e)
    {
        e.Work++;
        var a = args;
        switch (op)
        {
            case Op.Add:
            {
                var s = a[0].Num(e);
                for (var i = 1; i < a.Length; i++) s += a[i].Num(e);
                return Val.Num(s);
            }
            case Op.Mul:
            {
                var s = a[0].Num(e);
                for (var i = 1; i < a.Length; i++) s *= a[i].Num(e);
                return Val.Num(s);
            }
            case Op.Min:
            {
                var s = a[0].Num(e);
                for (var i = 1; i < a.Length; i++) s = Math.Min(s, a[i].Num(e));
                return Val.Num(s);
            }
            case Op.Max:
            {
                var s = a[0].Num(e);
                for (var i = 1; i < a.Length; i++) s = Math.Max(s, a[i].Num(e));
                return Val.Num(s);
            }
            case Op.Sub: return Val.Num(a[0].Num(e) - a[1].Num(e));
            case Op.Div:
            {
                var x = a[0].Num(e);
                var y = a[1].Num(e);
                return Val.Num(y == 0 ? 0 : x / y);
            }
            case Op.Mod:
            {
                var x = a[0].Num(e);
                var y = a[1].Num(e);
                return Val.Num(y == 0 ? 0 : x - y * Math.Floor(x / y));
            }
            case Op.Pow: return Val.Num(Math.Pow(a[0].Num(e), a[1].Num(e)));
            case Op.Atan2:
            {
                var y = a[0].Num(e);
                var x = a[1].Num(e);
                return Val.Num(SceneMath.Deg(Math.Atan2(y, x)));
            }
            case Op.Neg: return Val.Num(-a[0].Num(e));
            case Op.Abs: return Val.Num(Math.Abs(a[0].Num(e)));
            case Op.Floor: return Val.Num(Math.Floor(a[0].Num(e)));
            case Op.Ceil: return Val.Num(Math.Ceiling(a[0].Num(e)));
            case Op.Round: return Val.Num(Math.Round(a[0].Num(e), MidpointRounding.AwayFromZero));
            case Op.Sqrt: return Val.Num(Math.Sqrt(a[0].Num(e)));
            case Op.Exp: return Val.Num(Math.Exp(a[0].Num(e)));
            case Op.Log: return Val.Num(Math.Log(a[0].Num(e)));
            case Op.Sin: return Val.Num(SceneMath.SinDeg(a[0].Num(e)));
            case Op.Cos: return Val.Num(SceneMath.CosDeg(a[0].Num(e)));
            case Op.Clamp:
            {
                var x = a[0].Num(e);
                var lo = a[1].Num(e);
                var hi = a[2].Num(e);
                return Val.Num(Math.Min(Math.Max(x, lo), hi));
            }
            case Op.Norm:
            {
                var x = a[0].Num(e);
                var lo = a[1].Num(e);
                var hi = a[2].Num(e);
                return Val.Num(hi == lo ? 0 : (x - lo) / (hi - lo));
            }
            case Op.Lerp:
            {
                var x = a[0].Num(e);
                var y = a[1].Num(e);
                var t = a[2].Num(e);
                return Val.Num(x + (y - x) * t);
            }
            case Op.LerpColor:
            {
                var x = a[0].Col(e);
                var y = a[1].Col(e);
                var t = a[2].Num(e);
                return Val.Col(SceneColor.Lerp(x, y, t));
            }
            case Op.Lt: { var x = a[0].Num(e); var y = a[1].Num(e); return Val.Bool(x < y); }
            case Op.Le: { var x = a[0].Num(e); var y = a[1].Num(e); return Val.Bool(x <= y); }
            case Op.Gt: { var x = a[0].Num(e); var y = a[1].Num(e); return Val.Bool(x > y); }
            case Op.Ge: { var x = a[0].Num(e); var y = a[1].Num(e); return Val.Bool(x >= y); }
            case Op.And:
            {
                // Every operand is evaluated (no short circuit), so the work of a tick doesn't depend on values.
                var r = true;
                for (var i = 0; i < a.Length; i++) r &= a[i].Bool(e);
                return Val.Bool(r);
            }
            case Op.Or:
            {
                var r = false;
                for (var i = 0; i < a.Length; i++) r |= a[i].Bool(e);
                return Val.Bool(r);
            }
            case Op.Not: return Val.Bool(!a[0].Bool(e));
            case Op.If:
            {
                var c = a[0].Bool(e);
                var x = a[1].Eval(e);
                var y = a[2].Eval(e);
                return c ? x : y;
            }
            case Op.Concat:
            {
                e.Work += StringWork - 1;
                if (a.Length == 1) return Val.Str(e.Cut(a[0].Str(e)));
                var sb = new StringBuilder();
                for (var i = 0; i < a.Length; i++)
                {
                    sb.Append(a[i].Str(e));
                    if (sb.Length > 2 * e.TextLength + 2) break; // the rest is cut anyway; keeps memory bounded
                }
                return Val.Str(e.Cut(sb.ToString()));
            }
            case Op.Upper: e.Work += StringWork - 1; return Val.Str(e.Cut(a[0].Str(e).ToUpperInvariant()));
            case Op.Lower: e.Work += StringWork - 1; return Val.Str(e.Cut(a[0].Str(e).ToLowerInvariant()));
            case Op.Rgba:
            {
                var r = a[0].Num(e);
                var g = a[1].Num(e);
                var b = a[2].Num(e);
                var al = a[3].Num(e);
                return Val.Col(SceneColor.Make(SceneColor.Channel(r), SceneColor.Channel(g), SceneColor.Channel(b), SceneColor.Alpha(al)));
            }
            case Op.Alpha:
            {
                var c = a[0].Col(e);
                var al = a[1].Num(e);
                return Val.Col(SceneColor.WithAlpha(c, al));
            }
            default: return default;
        }
    }
}

internal enum EqMode : byte { Number, Bool, String, Color }

internal sealed class EqExprC(bool negate, EqMode mode, CExpr a, CExpr b) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        var x = a.Eval(e);
        var y = b.Eval(e);
        var eq = mode switch
        {
            EqMode.Number => x.N == y.N,
            EqMode.Bool => x.N == y.N,
            EqMode.String => string.Equals(x.S ?? "", y.S ?? "", StringComparison.Ordinal),
            _ => x.C == y.C,
        };
        return Val.Bool(eq != negate);
    }
}

internal enum InputField : byte { Value, Now, Min, Max, Avg, Unit, Text, Status, Available, Rows }

internal sealed class InputExprC(int input, InputField field) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        var s = input < e.Inputs.Length ? e.Inputs[input] : InputSnap.Unbound;
        return field switch
        {
            InputField.Value => Val.Num(s.Value),
            InputField.Now => Val.Num(s.Now),
            InputField.Min => Val.Num(s.Min),
            InputField.Max => Val.Num(s.Max),
            InputField.Avg => Val.Num(s.Avg),
            InputField.Unit => Val.Str(e.Cut(s.Unit)),
            InputField.Text => Val.Str(e.Cut(s.Text)),
            InputField.Status => Val.Str(s.Status),
            InputField.Rows => Val.Num(s.Table.Rows.Count),
            _ => Val.Bool(s.Available),
        };
    }
}

/// <summary>
/// <c>cell</c>: one cell of a table input's reading for the tick, its text (16 units of work, cut to
/// <c>textLength</c>) or its number (1 unit); <paramref name="name"/> picks the column by name, else
/// <paramref name="column"/> by index. A row or column the reading lacks reads "" or 0 (spec/docs/expressions.md,
/// "Reading text and tables").
/// </summary>
internal sealed class CellExprC(int input, CExpr row, int column, string? name, bool number) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += number ? 1 : CallExprC.StringWork;
        var r = Row(row.Num(e));
        var table = input < e.Inputs.Length ? e.Inputs[input].Table : SceneTable.Empty;
        var c = name is null ? column : table.ColumnOf(name);
        return number ? Val.Num(table.Number(r, c)) : Val.Str(e.Cut(table.Text(r, c)));
    }

    /// <summary>A row number rounded half away from zero; -1 (no row) for one outside the ints.</summary>
    internal static int Row(double value)
    {
        var rounded = Math.Round(SceneMath.Finite(value), MidpointRounding.AwayFromZero);
        return rounded is >= 0 and <= int.MaxValue ? (int)rounded : -1;
    }
}

/// <summary>
/// <c>parse</c>: the only conversion from text (spec/docs/expressions.md, "Reading text and tables"). As a number, a
/// plain decimal (<c>-?[0-9]+(\.[0-9]+)?</c>) as the double nearest it; as a colour, #RRGGBB or #RRGGBBAA; any other
/// text, and a decimal whose nearest double isn't finite, gives <c>else</c>. Both operands are evaluated, so the work
/// doesn't depend on the text; it counts 16, as a string operation.
/// </summary>
internal sealed class ParseExprC(bool color, CExpr value, CExpr otherwise) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += CallExprC.StringWork;
        var text = value.Str(e);
        var fallback = otherwise.Eval(e);
        if (color) return Val.Col(TryColor(text, out var c) ? c : fallback.C);
        return Val.Num(TryNumber(text, out var n) ? n : fallback.N);
    }

    /// <summary>A plain decimal's nearest double; false for any other text and for one whose nearest double isn't finite.</summary>
    internal static bool TryNumber(string text, out double value)
    {
        value = 0;
        var start = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var point = -1;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '.' && point < 0) point = i;
            else if (ch is < '0' or > '9') return false;
        }
        var end = point < 0 ? text.Length : point;
        if (end == start || point == text.Length - 1) return false; // no integer digits, or none after the point
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value))
        {
            value = 0;
            return false;
        }
        return true;
    }

    /// <summary>#RRGGBB or #RRGGBBAA, hexadecimal digits in either case; false for anything else.</summary>
    internal static bool TryColor(string text, out uint rgba)
    {
        rgba = 0;
        if (text.Length is not (7 or 9) || text[0] != '#') return false;
        for (var i = 1; i < text.Length; i++)
            if (!char.IsAsciiHexDigit(text[i])) return false;
        return SceneColor.TryParse(text, out rgba);
    }
}

internal enum HistoryFn : byte { Min, Max, Avg, First, Last, Count, Percentile, Share }

/// <summary>
/// <c>history</c> over the last <paramref name="count"/> samples (<see cref="InputSnap.Samples"/>): a number input's
/// history, or a table input's <paramref name="column"/>. It counts 1, and 1 more per 64 samples, or per 8 for a
/// percentile, which sorts them (spec/docs/expressions.md, "Cost"); its operands are evaluated whatever the samples, so
/// the work doesn't depend on them. A sample that isn't finite reads 0, as every number does.
/// </summary>
internal sealed class HistoryExprC(int input, HistoryFn fn, int count, SampleColumn? column = null, CExpr? p = null, CExpr? lo = null, CExpr? hi = null) : CExpr
{
    public override Val Eval(Ev e)
    {
        var n = Math.Clamp(count, 1, 4096);
        e.Work += 1 + (fn == HistoryFn.Percentile ? (n + 7) / 8 : (n + 63) / 64);
        var percent = p is null ? 0 : Math.Clamp(p.Num(e), 0, 100);
        var low = lo?.Num(e);
        var high = hi?.Num(e);
        var samples = input < e.Inputs.Length ? e.Inputs[input].Samples(column) : [];
        var take = Math.Min(n, samples.Count);
        if (take == 0) return Val.Num(0);
        var start = samples.Count - take;
        switch (fn)
        {
            case HistoryFn.First: return Val.Num(samples[start]);
            case HistoryFn.Last: return Val.Num(samples[^1]);
            case HistoryFn.Count: return Val.Num(take);
            case HistoryFn.Percentile: return Val.Num(Percentile(samples, start, take, percent));
            case HistoryFn.Share: return Val.Num(Share(samples, start, take, low, high));
        }
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        for (var i = start; i < samples.Count; i++)
        {
            var v = SceneMath.Finite(samples[i]);
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        return Val.Num(fn switch { HistoryFn.Min => min, HistoryFn.Max => max, _ => sum / take });
    }

    /// <summary>
    /// The nearest-rank percentile <paramref name="percent"/> (0–100) of <paramref name="take"/> samples from
    /// <paramref name="start"/>: sorted upwards, the sample at rank ceil(p × n ÷ 100), at least 1, with p × n computed
    /// first, so a whole p gives the exact rank.
    /// </summary>
    internal static double Percentile(IReadOnlyList<double> samples, int start, int take, double percent)
    {
        var sorted = ArrayPool<double>.Shared.Rent(take);
        try
        {
            for (var i = 0; i < take; i++) sorted[i] = SceneMath.Finite(samples[start + i]);
            Array.Sort(sorted, 0, take);
            var rank = (int)Math.Clamp(Math.Ceiling(percent * take / 100), 1, take);
            return sorted[rank - 1];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sorted);
        }
    }

    /// <summary>The share of <paramref name="take"/> samples from <paramref name="start"/> with lo ≤ v &lt; hi; an absent bound is open.</summary>
    internal static double Share(IReadOnlyList<double> samples, int start, int take, double? lo, double? hi)
    {
        var inside = 0;
        for (var i = start; i < start + take; i++)
        {
            var v = SceneMath.Finite(samples[i]);
            if ((lo is not { } l || v >= l) && (hi is not { } h || v < h)) inside++;
        }
        return (double)inside / take;
    }
}

/// <summary><c>time</c>: a field of the tick's clock, or of the instant <paramref name="at"/> gives when it is set.</summary>
internal sealed class TimeExprC(TimeField field, string? zone, CExpr? at = null) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        if (at is not null) return Val.Num(SceneTime.Field(field, SceneTime.Instant(at.Num(e)), e.ZoneOf(zone)));
        if (field == TimeField.Elapsed) return Val.Num(e.Clock.Elapsed);
        return Val.Num(SceneTime.Field(field, e.Clock, e.ZoneOf(zone)));
    }
}

/// <summary><c>formatTime</c>: the tick's date and time, or those of the instant <paramref name="at"/> gives when it is set.</summary>
internal sealed class FormatTimeExprC(PatternToken[] tokens, string? locale, string? zone, CExpr? at = null) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += CallExprC.StringWork;
        var culture = e.Clock.Format.Culture;
        if (locale is not null)
        {
            if (SceneTime.Culture(locale) is { } c) culture = c;
            else e.Diagnostics?.Add("time.locale", $"Unknown locale '{locale}'; the regional format's names are used.");
        }
        var now = at is null ? e.Clock.Now : SceneTime.Instant(at.Num(e));
        return Val.Str(e.Cut(SceneTime.Format(tokens, SceneTime.Local(now, e.ZoneOf(zone)), culture)));
    }
}

/// <summary>
/// <c>format</c>; a separator the scene doesn't give is the tick's regional format's (spec/docs/expressions.md, "Regional
/// format"). A <c>value</c> that is a string at run time is a number given as text (<see cref="FormatText"/>).
/// </summary>
internal sealed class FormatExprC(CExpr value, CExpr? decimals, CExpr? group, string? decimalSeparator, string? groupSeparator) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += CallExprC.StringWork;
        var v = value.Eval(e);
        if (v.S is { } text)
        {
            var grouped = group?.Bool(e) ?? false;
            return Val.Str(e.Cut(FormatText(text, grouped, decimalSeparator ?? e.Clock.Format.DecimalSeparator, groupSeparator ?? e.Clock.Format.GroupSeparator)));
        }
        var d = decimals is null ? 0 : (int)Math.Clamp(Math.Round(decimals.Num(e), MidpointRounding.AwayFromZero), 0, 6);
        var g = group?.Bool(e) ?? false;
        return Val.Str(e.Cut(FormatNumber(v.N, d, g, decimalSeparator ?? e.Clock.Format.DecimalSeparator, groupSeparator ?? e.Clock.Format.GroupSeparator)));
    }

    /// <summary>
    /// A number given as text: a plain decimal (<c>-?[0-9]+(\.[0-9]+)?</c>) keeps its own digits, with
    /// <paramref name="groupSeparator"/> between groups of three integer digits when <paramref name="group"/> and
    /// <paramref name="decimalSeparator"/> for its point; any other text is returned unchanged.
    /// </summary>
    internal static string FormatText(string text, bool group, string decimalSeparator, string groupSeparator)
    {
        var start = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var point = -1;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.' && point < 0) point = i;
            else if (c is < '0' or > '9') return text;
        }
        var end = point < 0 ? text.Length : point;
        if (end == start || point == text.Length - 1) return text; // no integer digits, or none after the point
        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, start);
        AppendInteger(sb, text.AsSpan(start, end - start), group, groupSeparator);
        if (point >= 0) sb.Append(decimalSeparator).Append(text, point + 1, text.Length - point - 1);
        return sb.ToString();
    }

    /// <summary>Integer digits, with <paramref name="groupSeparator"/> between groups of three when <paramref name="group"/>.</summary>
    private static void AppendInteger(StringBuilder sb, ReadOnlySpan<char> integer, bool group, string groupSeparator)
    {
        if (!group || integer.Length <= 3)
        {
            sb.Append(integer);
            return;
        }
        var first = integer.Length % 3;
        if (first > 0) sb.Append(integer[..first]);
        for (var i = first; i < integer.Length; i += 3)
        {
            if (i > 0) sb.Append(groupSeparator);
            sb.Append(integer.Slice(i, 3));
        }
    }

    internal static string FormatNumber(double v, int decimals, bool group, string decimalSeparator, string groupSeparator)
    {
        var r = SceneMath.Finite(Math.Round(v, decimals, MidpointRounding.AwayFromZero));
        var negative = r < 0;
        var digits = Math.Abs(r).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var point = digits.IndexOf('.');
        var integer = point < 0 ? digits : digits[..point];
        var fraction = point < 0 ? "" : digits[(point + 1)..];
        if (negative && integer.TrimStart('0').Length == 0 && fraction.TrimStart('0').Length == 0) negative = false;
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        AppendInteger(sb, integer, group, groupSeparator);
        if (fraction.Length > 0) sb.Append(decimalSeparator).Append(fraction);
        return sb.ToString();
    }
}

internal enum BoxField { W, H, Rotation }

internal sealed class BoxExprC(BoxField field) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        return Val.Num(field switch { BoxField.W => e.BoxW, BoxField.H => e.BoxH, _ => e.Rotation });
    }
}

internal sealed class MeasureExprC(int textNode, bool width) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        if (textNode < 0 || textNode >= e.MeasureW.Length) return Val.Num(0);
        return Val.Num(width ? e.MeasureW[textNode] : e.MeasureH[textNode]);
    }
}

internal sealed class ValueRefC(int index) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        return index < e.Values.Length ? e.Values[index] : default;
    }
}

internal sealed class StateRefC(int index) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        return index < e.State.Length ? e.State[index] : default;
    }
}

internal sealed class DtExprC : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        return Val.Num(e.Dt);
    }
}

internal sealed class StepsExprC(CExpr value, CExpr[] at, CExpr[] outs) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        var v = value.Num(e);
        var i = 0;
        for (var k = 0; k < at.Length; k++)
            if (v >= at[k].Num(e)) i++;
        // Evaluate every output (no side effects), so the work doesn't depend on the value; arity is validated.
        Val result = default;
        for (var k = 0; k < outs.Length; k++)
        {
            var o = outs[k].Eval(e);
            if (k == Math.Min(i, outs.Length - 1)) result = o;
        }
        return result;
    }
}

/// <summary>
/// The names a scene's expressions refer to, resolved to indexes: inputs (in declaration order), named values and state
/// cells (with their inferred types), and text nodes by id (for <c>measure</c>).
/// </summary>
internal sealed class ExprScope
{
    public required IReadOnlyDictionary<string, int> Inputs;
    public required IReadOnlyList<string> InputTypes;
    public required Dictionary<string, int> Values;
    public required Ty[] ValueTypes;
    public required Dictionary<string, int> Cells;
    public required Ty[] CellTypes;
    public required IReadOnlyDictionary<string, int> TextNodes;
    public int TextLength = 4096;

    /// <summary>Text nodes some expression measures (filled while compiling).</summary>
    public readonly HashSet<int> Measured = [];
}

/// <summary>
/// Compiles <see cref="Expr"/> trees into <see cref="CExpr"/>s. Types follow the validator's inference
/// (<see cref="SceneValidator"/>): they only decide how <c>eq</c>/<c>ne</c> compare and whether <c>lerp</c> mixes colours,
/// because a colour-like string literal carries both its text and its colour. Content the validator would refuse compiles
/// to constants rather than failing, so the runtime never throws on what it is given.
/// </summary>
internal sealed class ExprCompiler(ExprScope scope)
{
    private static readonly Dictionary<string, Op> Ops = new(StringComparer.Ordinal)
    {
        ["add"] = Op.Add, ["mul"] = Op.Mul, ["min"] = Op.Min, ["max"] = Op.Max, ["sub"] = Op.Sub, ["div"] = Op.Div,
        ["mod"] = Op.Mod, ["pow"] = Op.Pow, ["atan2"] = Op.Atan2, ["neg"] = Op.Neg, ["abs"] = Op.Abs, ["floor"] = Op.Floor,
        ["ceil"] = Op.Ceil, ["round"] = Op.Round, ["sqrt"] = Op.Sqrt, ["exp"] = Op.Exp, ["log"] = Op.Log, ["sin"] = Op.Sin,
        ["cos"] = Op.Cos, ["clamp"] = Op.Clamp, ["norm"] = Op.Norm, ["lerp"] = Op.Lerp, ["lt"] = Op.Lt, ["le"] = Op.Le,
        ["gt"] = Op.Gt, ["ge"] = Op.Ge, ["and"] = Op.And, ["or"] = Op.Or, ["not"] = Op.Not, ["if"] = Op.If,
        ["concat"] = Op.Concat, ["upper"] = Op.Upper, ["lower"] = Op.Lower, ["rgba"] = Op.Rgba, ["alpha"] = Op.Alpha,
    };

    private static readonly int[] Arity = BuildArity();

    private static int[] BuildArity()
    {
        var a = new int[64];
        foreach (var op in new[] { Op.Sub, Op.Div, Op.Mod, Op.Pow, Op.Atan2, Op.Lt, Op.Le, Op.Gt, Op.Ge, Op.Alpha }) a[(int)op] = 2;
        foreach (var op in new[] { Op.Neg, Op.Abs, Op.Floor, Op.Ceil, Op.Round, Op.Sqrt, Op.Exp, Op.Log, Op.Sin, Op.Cos, Op.Not, Op.Upper, Op.Lower }) a[(int)op] = 1;
        foreach (var op in new[] { Op.Clamp, Op.Norm, Op.Lerp, Op.LerpColor, Op.If }) a[(int)op] = 3;
        a[(int)Op.Rgba] = 4;
        return a;
    }

    public static ConstExpr Const(Val v, Ty t) => new ConstExpr(v, t).Init();

    public static readonly CExpr Zero = Const(Val.Num(0), Ty.Number);

    /// <summary>An expression, or <paramref name="fallback"/> when absent.</summary>
    public CExpr Compile(Expr? expr, CExpr fallback) => expr is null ? fallback : Compile(expr);

    public CExpr Number(Expr? expr, double fallback) => expr is null ? Const(Val.Num(fallback), Ty.Number) : Compile(expr);

    public CExpr Compile(Expr expr, int depth = 0)
    {
        if (depth > 256) return Zero;
        switch (expr)
        {
            case NumberLit n: return Const(Val.Num(n.Value), Ty.Number);
            case BoolLit b: return Const(Val.Bool(b.Value), Ty.Bool);
            case StringLit s:
            {
                var text = SceneText.Truncate(s.Value, scope.TextLength);
                return SceneColor.TryParse(s.Value, out var c) ? Const(Val.StrCol(text, c), Ty.String | Ty.Color) : Const(Val.Str(text), Ty.String);
            }
            case CallExpr call: return CompileCall(call, depth);
            case InputExpr input: return CompileInput(input);
            case HistoryExpr h:
            {
                var fn = h.Fn switch
                {
                    "min" => HistoryFn.Min, "max" => HistoryFn.Max, "avg" => HistoryFn.Avg, "first" => HistoryFn.First, "last" => HistoryFn.Last,
                    "percentile" => HistoryFn.Percentile, "share" => HistoryFn.Share, _ => HistoryFn.Count,
                };
                // p only with a percentile, lo and hi only with a share (the schema refuses them elsewhere): any other is ignored.
                var p = fn == HistoryFn.Percentile && h.P is not null ? Compile(h.P, depth + 1) : null;
                var lo = fn == HistoryFn.Share && h.Lo is not null ? Compile(h.Lo, depth + 1) : null;
                var hi = fn == HistoryFn.Share && h.Hi is not null ? Compile(h.Hi, depth + 1) : null;
                return Typed(new HistoryExprC(scope.Inputs.TryGetValue(h.Input, out var i) ? i : int.MaxValue, fn, h.Count, SampleColumn.Of(h.Column), p, lo, hi), Ty.Number);
            }
            case TimeExpr t:
            {
                // elapsed takes no at (the schema refuses it): one given anyway is ignored.
                var field = SceneTime.ParseField(t.Field);
                var at = t.At is null || field == TimeField.Elapsed ? null : Compile(t.At, depth + 1);
                return Typed(new TimeExprC(field, t.Zone, at), Ty.Number);
            }
            case FormatTimeExpr f:
                return Typed(new FormatTimeExprC(SceneTime.ParsePattern(f.Pattern), f.Locale, f.Zone, f.At is null ? null : Compile(f.At, depth + 1)), Ty.String);
            case FormatExpr f:
                return Typed(new FormatExprC(Compile(f.Value, depth + 1),
                    f.Decimals is null ? null : Compile(f.Decimals, depth + 1),
                    f.Group is null ? null : Compile(f.Group, depth + 1),
                    f.DecimalSeparator, f.GroupSeparator), Ty.String);
            case BoxExpr b:
                return Typed(new BoxExprC(b.Field switch { "w" => BoxField.W, "rotation" => BoxField.Rotation, _ => BoxField.H }), Ty.Number);
            case MeasureExpr m:
            {
                var n = scope.TextNodes.TryGetValue(m.Node, out var k) ? k : -1;
                if (n >= 0) scope.Measured.Add(n);
                return Typed(new MeasureExprC(n, m.Field == "w"), Ty.Number);
            }
            case DtExpr: return Typed(new DtExprC(), Ty.Number);
            case ParseExpr p:
            {
                var color = p.As == "color";
                return Typed(new ParseExprC(color, Compile(p.Value, depth + 1), Compile(p.Else, depth + 1)), color ? Ty.Color : Ty.Number);
            }
            case CellExpr c:
            {
                var number = c.Field == "number";
                var input = scope.Inputs.TryGetValue(c.Input, out var ci) ? ci : int.MaxValue;
                return Typed(new CellExprC(input, Compile(c.Row, depth + 1), c.Index ?? -1, c.Name, number), number ? Ty.Number : Ty.String);
            }
            case ValueRefExpr v:
                return scope.Values.TryGetValue(v.Name, out var vi) ? Typed(new ValueRefC(vi), scope.ValueTypes[vi]) : Zero;
            case StateRefExpr s:
                return scope.Cells.TryGetValue(s.Name, out var si) ? Typed(new StateRefC(si), scope.CellTypes[si]) : Zero;
            case StepsExpr steps:
            {
                var at = new CExpr[steps.At.Count];
                for (var i = 0; i < at.Length; i++) at[i] = Compile(steps.At[i], depth + 1);
                if (steps.Out.Count == 0) return Zero;
                var outs = new CExpr[steps.Out.Count];
                var t = Ty.Any;
                for (var i = 0; i < outs.Length; i++)
                {
                    outs[i] = Compile(steps.Out[i], depth + 1);
                    t &= outs[i].Type;
                }
                return Typed(new StepsExprC(Compile(steps.Value, depth + 1), at, outs), t == 0 ? Ty.Any : t);
            }
            default: return Zero;
        }
    }

    private static CExpr Typed(CExpr e, Ty t)
    {
        e.Type = t;
        return e;
    }

    private CExpr CompileInput(InputExpr input)
    {
        if (!scope.Inputs.TryGetValue(input.Name, out var index)) return Zero;
        var field = input.Field switch
        {
            "now" => InputField.Now,
            "min" => InputField.Min,
            "max" => InputField.Max,
            "avg" => InputField.Avg,
            "unit" => InputField.Unit,
            "text" => InputField.Text,
            "status" => InputField.Status,
            "available" => InputField.Available,
            "rows" => InputField.Rows,
            _ => InputField.Value,
        };
        var type = field switch
        {
            InputField.Unit or InputField.Text or InputField.Status => Ty.String,
            InputField.Available => Ty.Bool,
            _ => Ty.Number,
        };
        return Typed(new InputExprC(index, field), type);
    }

    private CExpr CompileCall(CallExpr call, int depth)
    {
        if (call.Op is "eq" or "ne") return CompileEq(call, depth);
        if (!Ops.TryGetValue(call.Op, out var op)) return Zero;
        var args = new CExpr[call.Args.Count];
        for (var i = 0; i < args.Length; i++) args[i] = Compile(call.Args[i], depth + 1);
        var need = Arity[(int)op];
        if ((need > 0 && args.Length != need) || args.Length == 0) return Fallback(op);

        switch (op)
        {
            case Op.Lerp:
            {
                var first = args[0].Type;
                var color = (first & Ty.Color) != 0 && (first & Ty.Number) == 0;
                return Typed(new CallExprC(color ? Op.LerpColor : Op.Lerp, args), color ? Ty.Color : Ty.Number);
            }
            case Op.If:
            {
                var t = args[1].Type & args[2].Type;
                return Typed(new CallExprC(op, args), t == 0 ? Ty.Any : t);
            }
        }
        var type = op switch
        {
            Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.And or Op.Or or Op.Not => Ty.Bool,
            Op.Concat or Op.Upper or Op.Lower => Ty.String,
            Op.Rgba or Op.Alpha => Ty.Color,
            _ => Ty.Number,
        };
        return Typed(new CallExprC(op, args), type);
    }

    private static CExpr Fallback(Op op) => op switch
    {
        Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.And or Op.Or or Op.Not => Const(Val.Bool(false), Ty.Bool),
        Op.Concat or Op.Upper or Op.Lower => Const(Val.Str(""), Ty.String),
        Op.Rgba or Op.Alpha => Const(Val.Col(0), Ty.Color),
        _ => Zero,
    };

    /// <summary><c>eq</c> and <c>ne</c>: compared as the operands' common type (a string when either reading fits).</summary>
    public CExpr CompileEq(CallExpr call, int depth)
    {
        if (call.Args.Count != 2) return Const(Val.Bool(false), Ty.Bool);
        var a = Compile(call.Args[0], depth + 1);
        var b = Compile(call.Args[1], depth + 1);
        var t = a.Type & b.Type;
        var mode = (t & Ty.Number) != 0 ? EqMode.Number
            : (t & Ty.Bool) != 0 ? EqMode.Bool
            : (t & Ty.String) != 0 ? EqMode.String
            : (t & Ty.Color) != 0 ? EqMode.Color
            : EqMode.String;
        return Typed(new EqExprC(call.Op == "ne", mode, a, b), Ty.Bool);
    }
}
