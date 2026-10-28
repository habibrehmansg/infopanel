using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoPanel.Spec;

/// <summary>What a scene costs, for the document's aggregate limits.</summary>
public sealed record SceneCost(int Nodes, int Work, int TextNodes, int EffectCost)
{
    public static SceneCost Zero { get; } = new(0, 0, 0, 0);
}

/// <summary>The outcome of <see cref="SceneValidator.Check"/>; <see cref="Scene"/> is set when the scene is valid.</summary>
public sealed record SceneCheck(CheckStatus Status, Scene? Scene, IReadOnlyList<SpecError> Errors, SceneCost Cost);

/// <summary>
/// Checks a scene in the order every reader uses (spec/docs/validation.md): the JSON-level pass, the format and
/// <c>requires</c> (unsupported stops here), the size, the collection counts, the schema, then the rules and limits:
/// references, types, evaluation contexts, counts and costs.
/// </summary>
public static class SceneValidator
{
    public static SceneCheck Check(JsonElement scene, SpecLimits? limits = null, EngineSupport? support = null)
    {
        limits ??= SpecLimits.Default;
        if (ValidationText.CheckJson(scene, limits.JsonDepth) is { } json)
            return new SceneCheck(CheckStatus.Invalid, null, json, SceneCost.Zero);
        return CheckRead(scene, limits, support);
    }

    /// <summary>
    /// <see cref="Check"/> after the JSON-level pass, for a scene inside content that passed it (a document or a commit):
    /// that pass runs once, on what was received, never again on a part of it.
    /// </summary>
    internal static SceneCheck CheckRead(JsonElement scene, SpecLimits? limits = null, EngineSupport? support = null)
    {
        limits ??= SpecLimits.Default;
        support ??= EngineSupport.Base;

        if (ValidationText.CheckFormat(scene, "scene", support.SceneCapabilities) is { } pre)
            return new SceneCheck(pre.Status, null, [pre.Error], SceneCost.Zero);

        var bytes = ValidationText.Utf8Bytes(scene);
        if (bytes > limits.SceneBytes)
            return Invalid(new SpecError("", "limit.sceneBytes", $"The scene is {bytes} bytes; the limit is {limits.SceneBytes}."));

        // Collection counts before the schema, so that no check below runs over more than the limits allow.
        var counts = Counts(scene, limits);
        if (counts.Count > 0) return new SceneCheck(CheckStatus.Invalid, null, counts, SceneCost.Zero);

        var schema = SpecSchemas.Strict.ValidateChecked(scene, SpecSchemas.Scene);
        if (!schema.IsValid) return new SceneCheck(CheckStatus.Invalid, null, ValidationText.FromSchema(schema).ToList(), SceneCost.Zero);

        Scene parsed;
        try
        {
            parsed = SpecJson.Read<Scene>(scene);
        }
        catch (JsonException ex)
        {
            return Invalid(new SpecError("", "schema", $"The scene passed the schema but couldn't be read: {ex.Message}"));
        }

        var rules = new Rules(parsed, limits);
        rules.Run();
        return rules.Errors.Count == 0
            ? new SceneCheck(CheckStatus.Valid, parsed, [], rules.Cost)
            : new SceneCheck(CheckStatus.Invalid, null, ValidationText.Cap(rules.Errors), rules.Cost);

        static SceneCheck Invalid(SpecError error) => new(CheckStatus.Invalid, null, [error], SceneCost.Zero);
    }

    /// <summary>
    /// The counted collections of a scene, read from the raw JSON before the schema: <c>inputs</c>, <c>values</c>,
    /// <c>state</c> and the nodes (through <c>children</c>, without recursion). Members of the wrong shape are left to the schema.
    /// </summary>
    internal static List<SpecError> Counts(JsonElement scene, SpecLimits limits)
    {
        var errors = new List<SpecError>();
        Members("inputs", "inputs", limits.Inputs);
        Members("values", "values", limits.Values);
        Members("state", "stateCells", limits.StateCells);
        if (scene.TryGetProperty("nodes", out var top) && top.ValueKind == JsonValueKind.Array)
        {
            long nodes = 0;
            var pending = new Stack<JsonElement>();
            pending.Push(top);
            while (pending.Count > 0 && nodes <= limits.Nodes)
                foreach (var node in pending.Pop().EnumerateArray())
                {
                    nodes++;
                    if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("children", out var children)
                        && children.ValueKind == JsonValueKind.Array)
                        pending.Push(children);
                }
            if (nodes > limits.Nodes) errors.Add(DocumentValidator.Over("/nodes", "nodes", nodes, limits.Nodes));
        }
        return errors;

        void Members(string member, string limit, int max)
        {
            if (scene.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.Object && v.GetPropertyCount() > max)
                errors.Add(DocumentValidator.Over("/" + member, limit, v.GetPropertyCount(), max));
        }
    }

    [Flags]
    private enum Ty { None = 0, Number = 1, String = 2, Bool = 4, Color = 8, Any = 15 }

    /// <summary>Where an expression is evaluated, which decides what it may read.</summary>
    private enum Ctx { Node, TextLayout, StateInit, StateNext, Value }

    private readonly record struct Info(Ty Type, bool State, bool Measure)
    {
        public static Info Of(Ty type) => new(type, false, false);
        public Info With(Info other) => new(Type, State || other.State, Measure || other.Measure);
    }

    private static readonly Regex ColorLiteral = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> NumberOps =
    [
        "neg", "abs", "floor", "ceil", "round", "sqrt", "exp", "log", "sin", "cos",
        "sub", "div", "mod", "pow", "atan2", "add", "mul", "min", "max", "clamp", "norm",
    ];

    /// <summary>
    /// What <c>concat</c>, <c>upper</c>, <c>lower</c>, <c>format</c> and <c>formatTime</c> each count towards a scene's
    /// expression work (every other operation counts 1): a string result may hold up to <see cref="SpecLimits.TextLength"/>
    /// scalar values (docs/expressions.md, "Cost").
    /// </summary>
    public const int StringOpWork = 16;

    private static readonly HashSet<string> StringOps = ["concat", "upper", "lower"];

    private sealed class Rules(Scene scene, SpecLimits limits)
    {
        public List<SpecError> Errors { get; } = [];
        public SceneCost Cost => new(_nodes, _work, _textNodes, _effectCost);

        private readonly Dictionary<string, SceneNode> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Info> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stateValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Ty> _cells = new(StringComparer.Ordinal);
        private readonly HashSet<string> _measured = new(StringComparer.Ordinal);
        private readonly List<(string Id, string Path)> _measureLayouts = [];
        private int _nodes, _textNodes, _series, _tables, _media, _geometry, _effectCost, _work;
        private bool _depthReported, _exprDepthReported, _quiet;

        private IReadOnlyDictionary<string, InputDecl> Inputs => scene.Inputs ?? EmptyInputs;
        private static readonly Dictionary<string, InputDecl> EmptyInputs = new();

        public void Run()
        {
            // Counts first: nothing below runs on more values, cells or inputs than the limits allow.
            if ((scene.Inputs?.Count ?? 0) > limits.Inputs) Limit("/inputs", "inputs", scene.Inputs!.Count, limits.Inputs);
            if ((scene.Values?.Count ?? 0) > limits.Values) Limit("/values", "values", scene.Values!.Count, limits.Values);
            if ((scene.State?.Count ?? 0) > limits.StateCells) Limit("/state", "stateCells", scene.State!.Count, limits.StateCells);
            if (Errors.Count > 0) return;

            CollectIds(scene.Nodes, "/nodes", 1);

            // Values in dependency order, found without recursion through references; values on a cycle read as any type.
            var order = ValueOrder();
            foreach (var name in order)
                if (!_stateValues.Contains(name)) _values[name] = Infer(scene.Values![name], $"/values/{name}", Ctx.Value, 0);
            CellTypes();
            foreach (var name in order)
                if (_stateValues.Contains(name)) _values[name] = Infer(scene.Values![name], $"/values/{name}", Ctx.Value, 0);

            WalkNodes(scene.Nodes, "/nodes");

            // Two phases per tick: a text node is laid out before the measure-dependent values unless its layout reads
            // one, so measuring a text node whose layout depends on a measurement is a cycle.
            foreach (var (id, path) in _measureLayouts)
                if (_measured.Contains(id))
                    Error(path, "cycle.measure", $"Text node '{id}' is measured, so its layout can't depend on a measurement.");

            if (_nodes > limits.Nodes) Limit("/nodes", "nodes", _nodes, limits.Nodes);
            if (_textNodes > limits.TextNodes) Limit("/nodes", "textNodes", _textNodes, limits.TextNodes);
            if (_series > limits.SeriesNodes) Limit("/nodes", "seriesNodes", _series, limits.SeriesNodes);
            if (_tables > limits.TableNodes) Limit("/nodes", "tableNodes", _tables, limits.TableNodes);
            if (_media > limits.MediaNodes) Limit("/nodes", "mediaNodes", _media, limits.MediaNodes);
            if (_geometry > limits.Geometry) Limit("/nodes", "geometry", _geometry, limits.Geometry);
            if (_effectCost > limits.EffectCost) Limit("/nodes", "effectCost", _effectCost, limits.EffectCost);
            if (_work > limits.ExprWork) Limit("", "exprWork", _work, limits.ExprWork);
        }

        private void Error(string path, string code, string message)
        {
            if (!_quiet) Errors.Add(new SpecError(path, code, message));
        }

        private void Limit(string path, string name, long actual, long limit) =>
            Error(path, "limit." + name, $"{actual} is over the limit of {limit} ({name}).");

        private void CollectIds(IReadOnlyList<SceneNode> nodes, string path, int depth)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var p = $"{path}/{i}";
                if (node.Id is { } id && !_ids.TryAdd(id, node))
                    Error(p + "/id", "id.duplicate", $"Another node of this scene has the id '{id}'.");
                if (depth > limits.Depth && !_depthReported)
                {
                    _depthReported = true;
                    Limit(p, "depth", depth, limits.Depth);
                }
                if (node is GroupNode group) CollectIds(group.Children, p + "/children", depth + 1);
            }
        }

        // ---- values and state ----------------------------------------------------------------------------

        /// <summary>
        /// The named values in dependency order (each after the values it reads), by an iterative depth-first search;
        /// reports each cycle once (<c>cycle.value</c>, at the reference that closes it) and leaves its values out, so they
        /// read as any type. Also finds the values that depend on state, directly or through other values.
        /// </summary>
        private List<string> ValueOrder()
        {
            var order = new List<string>();
            if (scene.Values is not { } values) return order;
            var refs = new Dictionary<string, List<(string Name, string Path)>>(StringComparer.Ordinal);
            var readsState = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, expr) in values)
            {
                var list = new List<(string, string)>();
                if (References(expr, $"/values/{name}", list)) readsState.Add(name);
                refs[name] = list;
            }

            var done = new HashSet<string>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var cyclic = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<(string Name, int Next)>();
            foreach (var root in values.Keys)
            {
                if (done.Contains(root)) continue;
                stack.Push((root, 0));
                onStack.Add(root);
                while (stack.Count > 0)
                {
                    var (name, next) = stack.Pop();
                    var list = refs[name];
                    if (next < list.Count)
                    {
                        stack.Push((name, next + 1));
                        var (target, path) = list[next];
                        if (!values.ContainsKey(target) || done.Contains(target)) continue;
                        if (onStack.Contains(target))
                        {
                            Error(path, "cycle.value", $"Value '{target}' depends on itself.");
                            foreach (var (n, _) in stack) { cyclic.Add(n); if (n == target) break; }
                            continue;
                        }
                        stack.Push((target, 0));
                        onStack.Add(target);
                        continue;
                    }
                    onStack.Remove(name);
                    done.Add(name);
                    if (cyclic.Contains(name)) continue;
                    if (readsState.Contains(name) || list.Any(r => _stateValues.Contains(r.Name))) _stateValues.Add(name);
                    order.Add(name);
                }
            }
            return order;
        }

        /// <summary>Adds the value references in an expression to <paramref name="refs"/>; true when it reads state directly.</summary>
        private static bool References(Expr expr, string path, List<(string Name, string Path)> refs)
        {
            switch (expr)
            {
                case ValueRefExpr v: refs.Add((v.Name, path)); return false;
                case StateRefExpr: return true;
                case CallExpr c:
                {
                    var state = false;
                    for (var i = 0; i < c.Args.Count; i++) state |= References(c.Args[i], $"{path}/args/{i}", refs);
                    return state;
                }
                case StepsExpr st:
                {
                    var state = References(st.Value, path + "/value", refs);
                    for (var i = 0; i < st.At.Count; i++) state |= References(st.At[i], $"{path}/at/{i}", refs);
                    for (var i = 0; i < st.Out.Count; i++) state |= References(st.Out[i], $"{path}/out/{i}", refs);
                    return state;
                }
                case FormatExpr f:
                {
                    var state = References(f.Value, path + "/value", refs);
                    if (f.Decimals is not null) state |= References(f.Decimals, path + "/decimals", refs);
                    if (f.Group is not null) state |= References(f.Group, path + "/group", refs);
                    return state;
                }
                default: return false;
            }
        }

        /// <summary>Counts the operation objects in an expression.</summary>
        private static int Ops(Expr expr) => expr switch
        {
            CallExpr c => 1 + c.Args.Sum(Ops),
            StepsExpr st => 1 + Ops(st.Value) + st.At.Sum(Ops) + st.Out.Sum(Ops),
            FormatExpr f => 1 + Ops(f.Value) + (f.Decimals is null ? 0 : Ops(f.Decimals)) + (f.Group is null ? 0 : Ops(f.Group)),
            NumberLit or StringLit or BoolLit => 0,
            _ => 1,
        };

        /// <summary>
        /// Each state cell's type: the common type of its <c>init</c> and <c>next</c> (a mismatch is an error), where a
        /// <c>next</c> reads the cells' types. Found by narrowing from the <c>init</c> types until nothing changes; uses of
        /// a cell are checked against the result.
        /// </summary>
        private void CellTypes()
        {
            if (scene.State is not { } state) return;
            var init = new Dictionary<string, Ty>(StringComparer.Ordinal);
            foreach (var (name, cell) in state)
            {
                var t = Infer(cell.Init, $"/state/{name}/init", Ctx.StateInit, 0).Type;
                init[name] = t;
                _cells[name] = t;
            }

            // Narrow quietly. The types only lose bits, so this ends; it is skipped for scenes over the work limit.
            var ops = state.Values.Sum(c => Ops(c.Next));
            if (ops <= limits.ExprWork)
            {
                _quiet = true;
                for (var round = 0; round <= 4 * state.Count; round++)
                {
                    var changed = false;
                    foreach (var (name, cell) in state)
                    {
                        var next = Infer(cell.Next, $"/state/{name}/next", Ctx.StateNext, 0).Type;
                        var t = (init[name] & next) == 0 ? init[name] : init[name] & next;
                        if (t != _cells[name]) { _cells[name] = t; changed = true; }
                    }
                    if (!changed) break;
                }
                _quiet = false;
            }

            var final = new Dictionary<string, Ty>(StringComparer.Ordinal);
            foreach (var (name, cell) in state)
            {
                var path = $"/state/{name}/next";
                var next = Infer(cell.Next, path, Ctx.StateNext, 0).Type;
                var common = init[name] & next;
                if (common == 0)
                    Error(path, "type.mismatch", $"State cell '{name}': next is {Show(next)} but init is {Show(init[name])}.");
                final[name] = common == 0 ? Ty.Any : common;
            }
            foreach (var (name, t) in final) _cells[name] = t;
        }

        // ---- expressions -----------------------------------------------------------------------------------

        private Info Expect(Expr? expr, string path, Ty expected, Ctx ctx)
        {
            if (expr is null) return Info.Of(expected);
            var info = Infer(expr, path, ctx, 0);
            if ((info.Type & expected) == 0)
                Error(path, "type.mismatch", $"Expected {Show(expected)}, found {Show(info.Type)}.");
            return info;
        }

        private Info Infer(Expr expr, string path, Ctx ctx, int depth)
        {
            switch (expr)
            {
                case NumberLit: return Info.Of(Ty.Number);
                case BoolLit: return Info.Of(Ty.Bool);
                case StringLit s: return Info.Of(ColorLiteral.IsMatch(s.Value) ? Ty.String | Ty.Color : Ty.String);
            }

            if (!_quiet) _work += expr is FormatExpr or FormatTimeExpr || expr is CallExpr { Op: var op } && StringOps.Contains(op) ? StringOpWork : 1;
            depth++;
            if (depth > limits.ExprDepth && !_exprDepthReported && !_quiet)
            {
                _exprDepthReported = true;
                Limit(path, "exprDepth", depth, limits.ExprDepth);
            }

            Info Arg(Expr e, string p, Ty expected)
            {
                var info = Infer(e, p, ctx, depth);
                if ((info.Type & expected) == 0)
                    Error(p, "type.mismatch", $"Expected {Show(expected)}, found {Show(info.Type)}.");
                return info;
            }

            switch (expr)
            {
                case CallExpr call:
                {
                    var args = call.Args;
                    string P(int i) => $"{path}/args/{i}";
                    if (NumberOps.Contains(call.Op))
                    {
                        var flags = Info.Of(Ty.Number);
                        for (var i = 0; i < args.Count; i++) flags = flags.With(Arg(args[i], P(i), Ty.Number));
                        return flags;
                    }
                    switch (call.Op)
                    {
                        case "lerp":
                        {
                            var first = Infer(args[0], P(0), ctx, depth);
                            var color = (first.Type & Ty.Color) != 0 && (first.Type & Ty.Number) == 0;
                            var t = color ? Ty.Color : Ty.Number;
                            if ((first.Type & t) == 0) Error(P(0), "type.mismatch", $"Expected {Show(t)}, found {Show(first.Type)}.");
                            return Info.Of(t).With(first).With(Arg(args[1], P(1), t)).With(Arg(args[2], P(2), Ty.Number));
                        }
                        case "lt" or "le" or "gt" or "ge":
                            return Info.Of(Ty.Bool).With(Arg(args[0], P(0), Ty.Number)).With(Arg(args[1], P(1), Ty.Number));
                        case "eq" or "ne":
                        {
                            var a = Infer(args[0], P(0), ctx, depth);
                            var b = Infer(args[1], P(1), ctx, depth);
                            if ((a.Type & b.Type) == 0)
                                Error(path, "type.mismatch", $"Can't compare {Show(a.Type)} with {Show(b.Type)}.");
                            return Info.Of(Ty.Bool).With(a).With(b);
                        }
                        case "and" or "or" or "not":
                        {
                            var flags = Info.Of(Ty.Bool);
                            for (var i = 0; i < args.Count; i++) flags = flags.With(Arg(args[i], P(i), Ty.Bool));
                            return flags;
                        }
                        case "concat" or "upper" or "lower":
                        {
                            var flags = Info.Of(Ty.String);
                            for (var i = 0; i < args.Count; i++) flags = flags.With(Arg(args[i], P(i), Ty.String));
                            return flags;
                        }
                        case "rgba":
                        {
                            var flags = Info.Of(Ty.Color);
                            for (var i = 0; i < args.Count; i++) flags = flags.With(Arg(args[i], P(i), Ty.Number));
                            return flags;
                        }
                        case "alpha":
                            return Info.Of(Ty.Color).With(Arg(args[0], P(0), Ty.Color)).With(Arg(args[1], P(1), Ty.Number));
                        case "if":
                        {
                            var cond = Arg(args[0], P(0), Ty.Bool);
                            var a = Infer(args[1], P(1), ctx, depth);
                            var b = Infer(args[2], P(2), ctx, depth);
                            var t = a.Type & b.Type;
                            if (t == 0) Error(path, "type.mismatch", $"The branches are {Show(a.Type)} and {Show(b.Type)}.");
                            return Info.Of(t == 0 ? Ty.Any : t).With(cond).With(a).With(b);
                        }
                        default:
                            Error(path, "schema", $"Unknown operation '{call.Op}'.");
                            return Info.Of(Ty.Any);
                    }
                }
                case StepsExpr steps:
                {
                    var flags = Arg(steps.Value, path + "/value", Ty.Number);
                    for (var i = 0; i < steps.At.Count; i++) flags = flags.With(Arg(steps.At[i], $"{path}/at/{i}", Ty.Number));
                    var t = Ty.Any;
                    for (var i = 0; i < steps.Out.Count; i++)
                    {
                        var o = Infer(steps.Out[i], $"{path}/out/{i}", ctx, depth);
                        flags = flags.With(o);
                        t &= o.Type;
                    }
                    if (t == 0) Error(path + "/out", "type.mismatch", "The outputs have different types.");
                    if (steps.Out.Count != steps.At.Count + 1)
                        Error(path + "/out", "steps.arity", $"{steps.At.Count} thresholds need {steps.At.Count + 1} outputs, not {steps.Out.Count}.");
                    return new Info(t == 0 ? Ty.Any : t, flags.State, flags.Measure);
                }
                case FormatExpr f:
                {
                    var flags = Info.Of(Ty.String).With(Arg(f.Value, path + "/value", Ty.Number));
                    if (f.Decimals is not null) flags = flags.With(Arg(f.Decimals, path + "/decimals", Ty.Number));
                    if (f.Group is not null) flags = flags.With(Arg(f.Group, path + "/group", Ty.Bool));
                    return flags;
                }
                case FormatTimeExpr ft:
                    if (!TimePattern.IsValid(ft.Pattern))
                        Error(path + "/pattern", "time.pattern", $"'{ft.Pattern}' is not a valid time pattern.");
                    return Info.Of(Ty.String);
                case TimeExpr: return Info.Of(Ty.Number);
                case BoxExpr: return Info.Of(Ty.Number);
                case DtExpr:
                    if (ctx != Ctx.StateNext)
                        Error(path, "context.dt", "dt is only allowed directly in a state cell's next.");
                    return Info.Of(Ty.Number);
                case InputExpr input:
                    return Info.Of(InputField(input, path));
                case HistoryExpr history:
                    if (!_quiet) _work += (history.Count + 63) / 64;
                    RequireInput(history.Input, "number", path + "/input");
                    return Info.Of(Ty.Number);
                case MeasureExpr measure:
                    if (!_ids.TryGetValue(measure.Node, out var target) || target is not TextNode)
                        Error(path + "/node", "ref.node", $"The scene has no text node '{measure.Node}'.");
                    else _measured.Add(measure.Node);
                    if (ctx is Ctx.StateInit or Ctx.StateNext)
                        Error(path, "context.measure", "A measurement can't affect state.");
                    return new Info(Ty.Number, false, true);
                case ValueRefExpr v:
                {
                    if (scene.Values is null || !scene.Values.ContainsKey(v.Name))
                    {
                        Error(path, "ref.value", $"The scene has no value '{v.Name}'.");
                        return Info.Of(Ty.Any);
                    }
                    // Not known yet: on a cycle (reported), or a value that reads state read by a state cell.
                    var info = _values.TryGetValue(v.Name, out var known) ? known : new Info(Ty.Any, _stateValues.Contains(v.Name), false);
                    if (info.State && ctx is Ctx.StateInit or Ctx.StateNext)
                        Error(path, "context.state", $"Value '{v.Name}' depends on state, so a state cell can't read it.");
                    if (info.Measure && ctx is Ctx.StateInit or Ctx.StateNext)
                        Error(path, "context.measure", $"Value '{v.Name}' depends on a measurement, which can't affect state.");
                    return info;
                }
                case StateRefExpr s:
                {
                    if (scene.State is null || !scene.State.ContainsKey(s.Name))
                    {
                        Error(path, "ref.state", $"The scene has no state cell '{s.Name}'.");
                        return Info.Of(Ty.Any);
                    }
                    if (ctx == Ctx.StateInit)
                    {
                        Error(path, "context.state", "A state cell's init can't read state.");
                        return Info.Of(Ty.Any);
                    }
                    return new Info(_cells.GetValueOrDefault(s.Name, Ty.Any), true, false);
                }
                default:
                    Error(path, "schema", $"Unknown expression {expr.GetType().Name}.");
                    return Info.Of(Ty.Any);
            }
        }

        private Ty InputField(InputExpr input, string path)
        {
            var field = input.Field ?? "value";
            switch (field)
            {
                case "value" or "now" or "min" or "max" or "avg":
                    RequireInput(input.Name, "number", path);
                    return Ty.Number;
                case "unit":
                    RequireInput(input.Name, "number", path);
                    return Ty.String;
                case "text":
                    RequireInput(input.Name, "text|table", path);
                    return Ty.String;
                case "available":
                    RequireInput(input.Name, null, path);
                    return Ty.Bool;
                default:
                    RequireInput(input.Name, null, path);
                    return Ty.String;
            }
        }

        private void RequireInput(string name, string? types, string path)
        {
            if (!Inputs.TryGetValue(name, out var decl))
            {
                Error(path, "ref.input", $"The scene declares no input '{name}'.");
                return;
            }
            if (types is not null && !types.Split('|').Contains(decl.Type))
                Error(path, "type.input", $"Input '{name}' is a {decl.Type} input; this needs {types.Replace("|", " or ")}.");
        }

        private static string Show(Ty t) => t switch
        {
            Ty.Number => "number",
            Ty.String => "string",
            Ty.Bool => "bool",
            Ty.Color => "color",
            Ty.String | Ty.Color => "string or color",
            Ty.None => "nothing",
            _ => "any type",
        };

        // ---- nodes -----------------------------------------------------------------------------------------

        private void WalkNodes(IReadOnlyList<SceneNode> nodes, string path)
        {
            for (var i = 0; i < nodes.Count; i++) WalkNode(nodes[i], $"{path}/{i}");
        }

        private Info Num(Expr? e, string path, Ctx ctx = Ctx.Node) => Expect(e, path, Ty.Number, ctx);

        private void WalkNode(SceneNode node, string p)
        {
            _nodes++;
            Expect(node.Visible, p + "/visible", Ty.Bool, Ctx.Node);
            Num(node.Opacity, p + "/opacity");
            switch (node)
            {
                case GroupNode g:
                    if (g.Transform is { } steps)
                        for (var i = 0; i < steps.Count; i++) WalkTransform(steps[i], $"{p}/transform/{i}");
                    if (g.Clip is { } clip) WalkClip(clip, p + "/clip");
                    if (g.Effects is { } effects)
                        for (var i = 0; i < effects.Count; i++) WalkEffect(effects[i], $"{p}/effects/{i}");
                    WalkNodes(g.Children, p + "/children");
                    break;
                case RectNode r:
                    Num(r.X, p + "/x"); Num(r.Y, p + "/y"); Num(r.W, p + "/w"); Num(r.H, p + "/h");
                    WalkRadii(r.R, p + "/r");
                    WalkPaint(r.Fill, p + "/fill");
                    WalkStroke(r.Stroke, p + "/stroke");
                    break;
                case EllipseNode e:
                    Num(e.Cx, p + "/cx"); Num(e.Cy, p + "/cy"); Num(e.Rx, p + "/rx"); Num(e.Ry, p + "/ry");
                    WalkPaint(e.Fill, p + "/fill");
                    WalkStroke(e.Stroke, p + "/stroke");
                    break;
                case ArcNode a:
                    Num(a.Cx, p + "/cx"); Num(a.Cy, p + "/cy"); Num(a.R, p + "/r"); Num(a.Inner, p + "/inner");
                    Num(a.Start, p + "/start"); Num(a.Sweep, p + "/sweep");
                    WalkPaint(a.Fill, p + "/fill");
                    WalkStroke(a.Stroke, p + "/stroke");
                    break;
                case LineNode l:
                    Num(l.X1, p + "/x1"); Num(l.Y1, p + "/y1"); Num(l.X2, p + "/x2"); Num(l.Y2, p + "/y2");
                    WalkStroke(l.Stroke, p + "/stroke");
                    break;
                case PathNode path:
                    WalkPathData(path.D, p + "/d");
                    WalkPaint(path.Fill, p + "/fill");
                    WalkStroke(path.Stroke, p + "/stroke");
                    break;
                case PolylineNode poly:
                    for (var i = 0; i < poly.Points.Count; i++)
                    {
                        Num(poly.Points[i][0], $"{p}/points/{i}/0");
                        Num(poly.Points[i][1], $"{p}/points/{i}/1");
                    }
                    if (poly.Points.Count > limits.PathCommands) Limit(p + "/points", "pathCommands", poly.Points.Count, limits.PathCommands);
                    _geometry += poly.Points.Count;
                    WalkPaint(poly.Fill, p + "/fill");
                    WalkStroke(poly.Stroke, p + "/stroke");
                    break;
                case TextNode t:
                    WalkText(t, p);
                    break;
                case SeriesNode s:
                    _series++;
                    RequireInput(s.Input, "number", p + "/input");
                    Num(s.X, p + "/x"); Num(s.Y, p + "/y"); Num(s.W, p + "/w"); Num(s.H, p + "/h");
                    Num(s.Min, p + "/min"); Num(s.Max, p + "/max"); Num(s.Pitch, p + "/pitch"); Num(s.BarWidth, p + "/barWidth");
                    WalkPaint(s.Fill, p + "/fill");
                    WalkStroke(s.Stroke, p + "/stroke");
                    break;
                case TableNode tb:
                    _tables++;
                    RequireInput(tb.Input, "table", p + "/input");
                    Num(tb.X, p + "/x"); Num(tb.Y, p + "/y"); Num(tb.RowHeight, p + "/rowHeight"); Num(tb.Gap, p + "/gap");
                    for (var i = 0; i < tb.Columns.Count; i++)
                    {
                        Num(tb.Columns[i].W, $"{p}/columns/{i}/w");
                        Expect(tb.Columns[i].Title, $"{p}/columns/{i}/title", Ty.String, Ctx.Node);
                    }
                    Num(tb.Font.Size, p + "/font/size");
                    Num(tb.HeaderFont?.Size, p + "/headerFont/size");
                    WalkPaint(tb.Fill, p + "/fill");
                    WalkPaint(tb.HeaderFill, p + "/headerFill");
                    WalkStroke(tb.Stroke, p + "/stroke");
                    break;
                case MediaNode m:
                    _media++;
                    Num(m.X, p + "/x"); Num(m.Y, p + "/y"); Num(m.W, p + "/w"); Num(m.H, p + "/h"); Num(m.Speed, p + "/speed");
                    WalkMediaSource(m.Source, p + "/source");
                    break;
            }
        }

        private void WalkText(TextNode t, string p)
        {
            _textNodes++;
            Num(t.X, p + "/x");
            Num(t.Y, p + "/y");
            string? measurePath = null;
            void Layout(Expr? e, string path, Ty type)
            {
                if (Expect(e, path, type, Ctx.TextLayout).Measure) measurePath ??= path;
            }
            Layout(t.W, p + "/w", Ty.Number);
            Layout(t.H, p + "/h", Ty.Number);
            if (t.Text.Plain is { } plain) Layout(plain, p + "/text", Ty.String);
            if (t.Text.Spans is { } spans)
                for (var i = 0; i < spans.Count; i++)
                {
                    var sp = $"{p}/text/{i}";
                    Layout(spans[i].Text, sp + "/text", Ty.String);
                    Layout(spans[i].Font?.Size, sp + "/font/size", Ty.Number);
                    WalkPaint(spans[i].Fill, sp + "/fill");
                }
            Layout(t.Font.Size, p + "/font/size", Ty.Number);
            WalkPaint(t.Fill, p + "/fill");
            WalkStroke(t.Stroke, p + "/stroke");
            Num(t.Marquee?.Speed, p + "/marquee/speed");
            Num(t.Marquee?.Gap, p + "/marquee/gap");
            Layout(t.Fit?.Min, p + "/fit/min", Ty.Number);
            Layout(t.LineHeight, p + "/lineHeight", Ty.Number);
            Layout(t.LetterSpacing, p + "/letterSpacing", Ty.Number);
            if (t.Id is { } id && measurePath is not null) _measureLayouts.Add((id, measurePath));
            if (t.W is null && (t.Overflow == "marquee" || t.Wrap == "word" || t.Fit is not null))
                Error(p, "text.box", "Marquee, word wrap and fit need a box width (w).");
        }

        private void WalkRadii(CornerRadii? r, string p)
        {
            if (r is null) return;
            if (r.All is { } all) Num(all, p);
            if (r.Corners is { } corners)
                for (var i = 0; i < corners.Count; i++) Num(corners[i], $"{p}/{i}");
        }

        private void WalkPathData(string d, string p)
        {
            var segments = PathData.CountSegments(d);
            if (segments < 0)
            {
                Error(p, "path.syntax", "Not valid SVG path data.");
                return;
            }
            if (segments > limits.PathCommands) Limit(p, "pathCommands", segments, limits.PathCommands);
            _geometry += segments;
        }

        private void WalkPaint(Paint? paint, string p)
        {
            switch (paint)
            {
                case null: return;
                case SolidPaint s: Expect(s.Color, p, Ty.Color, Ctx.Node); return;
                case LinearPaint l:
                    Num(l.X1, p + "/x1"); Num(l.Y1, p + "/y1"); Num(l.X2, p + "/x2"); Num(l.Y2, p + "/y2");
                    WalkStops(l.Stops, p + "/stops");
                    return;
                case RadialPaint r:
                    Num(r.Cx, p + "/cx"); Num(r.Cy, p + "/cy"); Num(r.R, p + "/r");
                    WalkStops(r.Stops, p + "/stops");
                    return;
                case SweepPaint s:
                    Num(s.Cx, p + "/cx"); Num(s.Cy, p + "/cy"); Num(s.Start, p + "/start"); Num(s.End, p + "/end");
                    WalkStops(s.Stops, p + "/stops");
                    return;
                case ComposePaint c:
                    WalkPaint(c.Dst, p + "/dst");
                    WalkPaint(c.Src, p + "/src");
                    return;
            }
        }

        private void WalkStops(IReadOnlyList<GradientStop> stops, string p)
        {
            for (var i = 0; i < stops.Count; i++)
            {
                Num(stops[i].At, $"{p}/{i}/at");
                Expect(stops[i].Color, $"{p}/{i}/color", Ty.Color, Ctx.Node);
            }
        }

        private void WalkStroke(Stroke? s, string p)
        {
            if (s is null) return;
            WalkPaint(s.Paint, p + "/paint");
            Num(s.Width, p + "/width");
            if (s.Dash is { } dash)
                for (var i = 0; i < dash.Count; i++) Num(dash[i], $"{p}/dash/{i}");
            Num(s.DashOffset, p + "/dashOffset");
            Num(s.TrimStart, p + "/trimStart");
            Num(s.TrimEnd, p + "/trimEnd");
        }

        private void WalkTransform(TransformStep step, string p)
        {
            switch (step)
            {
                case TranslateStep t: Num(t.X, p + "/x"); Num(t.Y, p + "/y"); break;
                case RotateStep r: Num(r.Angle, p + "/angle"); Num(r.Cx, p + "/cx"); Num(r.Cy, p + "/cy"); break;
                case ScaleStep s: Num(s.X, p + "/x"); Num(s.Y, p + "/y"); Num(s.Cx, p + "/cx"); Num(s.Cy, p + "/cy"); break;
                case SkewStep k: Num(k.X, p + "/x"); Num(k.Y, p + "/y"); break;
            }
        }

        private void WalkClip(ClipShape clip, string p)
        {
            switch (clip)
            {
                case RectClip r: Num(r.X, p + "/x"); Num(r.Y, p + "/y"); Num(r.W, p + "/w"); Num(r.H, p + "/h"); WalkRadii(r.R, p + "/r"); break;
                case EllipseClip e: Num(e.Cx, p + "/cx"); Num(e.Cy, p + "/cy"); Num(e.Rx, p + "/rx"); Num(e.Ry, p + "/ry"); break;
                case ArcClip a:
                    Num(a.Cx, p + "/cx"); Num(a.Cy, p + "/cy"); Num(a.R, p + "/r"); Num(a.Inner, p + "/inner");
                    Num(a.Start, p + "/start"); Num(a.Sweep, p + "/sweep");
                    break;
                case PathClip path: WalkPathData(path.D, p + "/d"); break;
            }
        }

        private void WalkEffect(Effect effect, string p)
        {
            Expr sigma;
            switch (effect)
            {
                case BlurEffect b: sigma = b.Sigma; break;
                case ShadowEffect s:
                    sigma = s.Sigma;
                    Num(s.Dx, p + "/dx"); Num(s.Dy, p + "/dy");
                    Expect(s.Color, p + "/color", Ty.Color, Ctx.Node);
                    break;
                case GlowEffect g:
                    sigma = g.Sigma;
                    Expect(g.Color, p + "/color", Ty.Color, Ctx.Node);
                    break;
                default: return;
            }
            Num(sigma, p + "/sigma");
            _effectCost += EffectCost(sigma);
        }

        /// <summary>1 + ceil(sigma ÷ 8) for a literal sigma (clamped to 0–64); 9, the most, for a computed one.</summary>
        internal static int EffectCost(Expr sigma) =>
            1 + (sigma is NumberLit n ? (int)Math.Ceiling(Math.Clamp(n.Value, 0, 64) / 8) : 8);

        private void WalkMediaSource(MediaSource source, string p)
        {
            if (source.Provider is not null)
            {
                if (scene.Requires?.Contains("media.provider") != true)
                    Error(p, "requires.missing", "A provider source needs \"media.provider\" in requires.");
                Expect(source.Ref, p + "/ref", Ty.String, Ctx.Node);
            }
            if (source.Sequence is { } sequence)
            {
                if (sequence.Count > limits.Sequence) Limit(p + "/sequence", "sequence", sequence.Count, limits.Sequence);
                Num(source.Index, p + "/index");
            }
        }
    }
}
