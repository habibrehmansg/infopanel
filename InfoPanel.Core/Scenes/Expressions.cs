using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using InfoPanel.Spec;

namespace InfoPanel.Scenes;

/// <summary>One input's reading for a tick, with its history and the value its binding's statistic selects.</summary>
internal sealed class InputSnap
{
    public static readonly InputSnap Unbound = new(SceneReading.Unbound, "now", []);

    public InputSnap(SceneReading reading, string stat, IReadOnlyList<double> history)
    {
        Reading = reading;
        History = history;
        var available = reading.Available;
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
    public SceneTable Table => Available ? Reading.Table : SceneTable.Empty;
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

internal enum InputField : byte { Value, Now, Min, Max, Avg, Unit, Text, Status, Available }

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
            _ => Val.Bool(s.Available),
        };
    }
}

internal enum HistoryFn : byte { Min, Max, Avg, First, Last, Count }

internal sealed class HistoryExprC(int input, HistoryFn fn, int count) : CExpr
{
    public override Val Eval(Ev e)
    {
        var n = Math.Clamp(count, 1, 4096);
        e.Work += 1 + (n + 63) / 64;
        var samples = input < e.Inputs.Length ? e.Inputs[input].History : [];
        var take = Math.Min(n, samples.Count);
        if (take == 0) return Val.Num(0);
        var start = samples.Count - take;
        switch (fn)
        {
            case HistoryFn.First: return Val.Num(samples[start]);
            case HistoryFn.Last: return Val.Num(samples[^1]);
            case HistoryFn.Count: return Val.Num(take);
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
}

internal sealed class TimeExprC(TimeField field, string? zone) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        if (field == TimeField.Elapsed) return Val.Num(e.Clock.Elapsed);
        return Val.Num(SceneTime.Field(field, e.Clock, e.ZoneOf(zone)));
    }
}

internal sealed class FormatTimeExprC(PatternToken[] tokens, string? locale, string? zone) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += CallExprC.StringWork;
        var culture = e.Clock.Locale;
        if (locale is not null)
        {
            if (SceneTime.Culture(locale) is { } c) culture = c;
            else e.Diagnostics?.Add("time.locale", $"Unknown locale '{locale}'; the engine's locale is used.");
        }
        return Val.Str(e.Cut(SceneTime.Format(tokens, SceneTime.Local(e.Clock, e.ZoneOf(zone)), culture)));
    }
}

internal sealed class FormatExprC(CExpr value, CExpr? decimals, CExpr? group, string decimalSeparator, string groupSeparator) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work += CallExprC.StringWork;
        var v = value.Num(e);
        var d = decimals is null ? 0 : (int)Math.Clamp(Math.Round(decimals.Num(e), MidpointRounding.AwayFromZero), 0, 6);
        var g = group?.Bool(e) ?? false;
        return Val.Str(e.Cut(FormatNumber(v, d, g, decimalSeparator, groupSeparator)));
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
        if (group && integer.Length > 3)
        {
            var first = integer.Length % 3;
            if (first > 0) sb.Append(integer, 0, first);
            for (var i = first; i < integer.Length; i += 3)
            {
                if (i > 0) sb.Append(groupSeparator);
                sb.Append(integer, i, 3);
            }
        }
        else
        {
            sb.Append(integer);
        }
        if (fraction.Length > 0) sb.Append(decimalSeparator).Append(fraction);
        return sb.ToString();
    }
}

internal sealed class BoxExprC(bool width) : CExpr
{
    public override Val Eval(Ev e)
    {
        e.Work++;
        return Val.Num(width ? e.BoxW : e.BoxH);
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
                var fn = h.Fn switch { "min" => HistoryFn.Min, "max" => HistoryFn.Max, "avg" => HistoryFn.Avg, "first" => HistoryFn.First, "last" => HistoryFn.Last, _ => HistoryFn.Count };
                return Typed(new HistoryExprC(scope.Inputs.TryGetValue(h.Input, out var i) ? i : int.MaxValue, fn, h.Count), Ty.Number);
            }
            case TimeExpr t: return Typed(new TimeExprC(SceneTime.ParseField(t.Field), t.Zone), Ty.Number);
            case FormatTimeExpr f: return Typed(new FormatTimeExprC(SceneTime.ParsePattern(f.Pattern), f.Locale, f.Zone), Ty.String);
            case FormatExpr f:
                return Typed(new FormatExprC(Compile(f.Value, depth + 1),
                    f.Decimals is null ? null : Compile(f.Decimals, depth + 1),
                    f.Group is null ? null : Compile(f.Group, depth + 1),
                    f.DecimalSeparator ?? ".", f.GroupSeparator ?? ","), Ty.String);
            case BoxExpr b: return Typed(new BoxExprC(b.Field == "w"), Ty.Number);
            case MeasureExpr m:
            {
                var n = scope.TextNodes.TryGetValue(m.Node, out var k) ? k : -1;
                if (n >= 0) scope.Measured.Add(n);
                return Typed(new MeasureExprC(n, m.Field == "w"), Ty.Number);
            }
            case DtExpr: return Typed(new DtExprC(), Ty.Number);
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
