using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoPanel.Spec.Schema;

/// <summary>A compiled (sub)schema: a boolean or an object holding one <see cref="Rule"/> per keyword.</summary>
internal abstract class SchemaNode
{
    protected SchemaNode(string id, string pointer) { Id = id; Pointer = pointer; }
    public string Id { get; }
    public string Pointer { get; }

    /// <summary>Validates <paramref name="v"/>, counting one step of the run's work budget; nothing once it is spent.</summary>
    public void Validate(JsonElement v, InstancePath path, Errs errs)
    {
        if (errs.Run.Step()) Check(v, path, errs);
    }

    protected abstract void Check(JsonElement v, InstancePath path, Errs errs);
    /// <summary>Schemas applied to the same instance value (used to find reference loops).</summary>
    public virtual IEnumerable<SchemaNode> SameInstance => [];
}

internal sealed class BoolNode : SchemaNode
{
    public BoolNode(string id, string pointer, bool value) : base(id, pointer) { Value = value; }
    public bool Value { get; }
    protected override void Check(JsonElement v, InstancePath path, Errs errs)
    {
        if (!Value) errs.Add(new Err(path, "false", "No value is allowed here.", $"{Id}#{Pointer}"));
    }
}

internal sealed class ObjectNode : SchemaNode
{
    public ObjectNode(string id, string pointer) : base(id, pointer) { }
    public Rule[] Rules { get; set; } = [];
    protected override void Check(JsonElement v, InstancePath path, Errs errs)
    {
        foreach (var r in Rules) r.Apply(v, path, errs);
    }
    public override IEnumerable<SchemaNode> SameInstance => Rules.SelectMany(r => r.SameInstance);
}

/// <summary>One keyword of one schema object.</summary>
internal abstract class Rule
{
    protected Rule(string keyword, string schemaPath) { Keyword = keyword; SchemaPath = schemaPath; }
    public string Keyword { get; }
    public string SchemaPath { get; }
    public abstract void Apply(JsonElement v, InstancePath path, Errs errs);
    public virtual IEnumerable<SchemaNode> SameInstance => [];
    protected void Fail(Errs errs, InstancePath path, string message, string? keyword = null) =>
        errs.Add(new Err(path, keyword ?? Keyword, message, SchemaPath));
}

internal sealed class TypeRule : Rule
{
    public TypeRule(string sp, string[] types) : base("type", sp) { Types = types; }
    public string[] Types { get; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        foreach (var t in Types) if (Json.Matches(t, v)) return;
        Fail(errs, path, $"Expected {string.Join(" or ", Types)} but found {Json.TypeName(v)}.");
    }
}

internal sealed class EnumRule : Rule
{
    private readonly JsonElement _values;
    public EnumRule(string sp, JsonElement values) : base("enum", sp) { _values = values; }
    public JsonElement[] Values => _values.EnumerateArray().ToArray();
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        foreach (var e in _values.EnumerateArray()) if (Json.DeepEquals(e, v)) return;
        Fail(errs, path, "Value must be one of: " + string.Join(", ", _values.EnumerateArray().Select(e => e.GetRawText())) + ".");
    }
}

internal sealed class ConstRule : Rule
{
    public ConstRule(string sp, JsonElement value) : base("const", sp) { Value = value; }
    public JsonElement Value { get; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (!Json.DeepEquals(Value, v)) Fail(errs, path, $"Value must be {Value.GetRawText()}.");
    }
}

internal sealed class NumberRule : Rule
{
    private readonly double _limit;
    private readonly string _text;
    public NumberRule(string keyword, string sp, JsonElement limit) : base(keyword, sp)
    {
        _limit = JsonNumber.Value(limit);
        _text = limit.GetRawText();
    }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Number) return;
        var c = JsonNumber.Value(v).CompareTo(_limit);
        var (ok, text) = Keyword switch
        {
            "minimum" => (c >= 0, "greater than or equal to"),
            "maximum" => (c <= 0, "less than or equal to"),
            "exclusiveMinimum" => (c > 0, "greater than"),
            _ => (c < 0, "less than"),
        };
        if (!ok) Fail(errs, path, $"Value must be {text} {_text}.");
    }
}

internal sealed class LengthRule : Rule
{
    private readonly int _limit;
    private readonly bool _isMin;
    public LengthRule(string keyword, string sp, int limit, bool isMin) : base(keyword, sp) { _limit = limit; _isMin = isMin; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.String) return;
        var n = Json.CodePoints(v.GetString()!);
        if (_isMin ? n < _limit : n > _limit)
            Fail(errs, path, $"String must be {(_isMin ? "at least" : "at most")} {_limit} characters long.");
    }
}

internal sealed class PatternRule : Rule
{
    private readonly Regex _regex;
    private readonly string _pattern;
    public PatternRule(string sp, string pattern, Regex regex) : base("pattern", sp) { _pattern = pattern; _regex = regex; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.String) return;
        try
        {
            if (!_regex.IsMatch(v.GetString()!)) Fail(errs, path, $"String must match the pattern {_pattern}.");
        }
        catch (RegexMatchTimeoutException)
        {
            Fail(errs, path, $"Matching the pattern {_pattern} timed out.");
        }
    }
}

/// <summary><c>minItems</c>, <c>maxItems</c>, <c>minProperties</c> and <c>maxProperties</c>.</summary>
internal sealed class CountRule : Rule
{
    private readonly JsonValueKind _kind;
    private readonly int _limit;
    private readonly bool _isMin;
    public CountRule(string keyword, string sp, JsonValueKind kind, int limit, bool isMin) : base(keyword, sp)
    {
        _kind = kind; _limit = limit; _isMin = isMin;
    }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != _kind) return;
        var n = _kind == JsonValueKind.Array ? v.GetArrayLength() : v.GetPropertyCount();
        if (_isMin ? n < _limit : n > _limit)
            Fail(errs, path, $"{(_kind == JsonValueKind.Array ? "Array" : "Object")} must have {(_isMin ? "at least" : "at most")} {_limit} {(_kind == JsonValueKind.Array ? "items" : "properties")}.");
    }
}

internal sealed class UniqueRule : Rule
{
    public UniqueRule(string sp) : base("uniqueItems", sp) { }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Array) return;
        // Items are bucketed by a per-process seeded hash (equal items share one); every comparison inside a bucket is
        // charged to the run's work budget by the values it compares, as it compares them, so items that share a hash
        // can't make this quadratic for free, nor one comparison run past the budget.
        var seen = new Dictionary<int, List<(JsonElement El, int Index)>>();
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            var h = Json.Hash(item);
            if (!seen.TryGetValue(h, out var bucket)) seen[h] = bucket = [];
            foreach (var (el, index) in bucket)
            {
                var equal = Json.DeepEquals(el, item, errs.Run);
                if (errs.Run.Exhausted) return;
                if (!equal) continue;
                Fail(errs, path, $"Items at indexes {index} and {i} are equal.");
                return;
            }
            bucket.Add((item, i++));
        }
    }
}

internal sealed class RequiredRule : Rule
{
    public RequiredRule(string sp, string[] names) : base("required", sp) { Names = names; }
    public string[] Names { get; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Object) return;
        foreach (var n in Names)
            if (!v.TryGetProperty(n, out _)) Fail(errs, path, $"Missing required property \"{n}\".");
    }
}

internal sealed class PropertiesRule : Rule
{
    public PropertiesRule(string sp, (string Name, SchemaNode Node)[] props) : base("properties", sp) { Props = props; }
    public (string Name, SchemaNode Node)[] Props { get; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Object) return;
        foreach (var (name, node) in Props)
            if (v.TryGetProperty(name, out var pv)) node.Validate(pv, path.Prop(name), errs);
    }
}

internal sealed class AdditionalPropertiesRule : Rule
{
    private readonly SchemaNode _node;
    private readonly HashSet<string> _known;
    public AdditionalPropertiesRule(string sp, SchemaNode node, HashSet<string> known) : base("additionalProperties", sp)
    {
        _node = node; _known = known;
    }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Object) return;
        foreach (var p in v.EnumerateObject())
        {
            var name = p.Name;
            if (_known.Contains(name)) continue;
            // Reading an instance's property name costs work by its length, and the name is shared by every path through it.
            if (!errs.Run.ChargeName(name.Length)) return;
            var at = path.Prop(errs.Run.Intern(name));
            if (_node is BoolNode { Value: false }) Fail(errs, at, $"Unexpected property \"{name}\".");
            else _node.Validate(p.Value, at, errs);
        }
    }
}

internal sealed class PropertyNamesRule : Rule
{
    private readonly SchemaNode _node;
    public PropertyNamesRule(string sp, SchemaNode node) : base("propertyNames", sp) { _node = node; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Object) return;
        foreach (var p in v.EnumerateObject())
        {
            var name = p.Name;
            if (!errs.Run.ChargeName(name.Length)) return;
            using var doc = JsonDocument.Parse("\"" + JsonEncodedText.Encode(name) + "\"");
            var inner = errs.Child();
            _node.Validate(doc.RootElement, InstancePath.Root, inner);
            if (inner.Count > 0)
                Fail(errs, path.Prop(errs.Run.Intern(name)), $"Property name \"{name}\" is not allowed: {inner.Items[0].Message}");
        }
    }
}

internal sealed class PrefixItemsRule : Rule
{
    private readonly SchemaNode[] _nodes;
    public PrefixItemsRule(string sp, SchemaNode[] nodes) : base("prefixItems", sp) { _nodes = nodes; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Array) return;
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (i >= _nodes.Length) break;
            _nodes[i].Validate(item, path.Item(i), errs);
            i++;
        }
    }
}

internal sealed class ItemsRule : Rule
{
    private readonly SchemaNode _node;
    private readonly int _prefix;
    public ItemsRule(string sp, SchemaNode node, int prefix) : base("items", sp) { _node = node; _prefix = prefix; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        if (v.ValueKind != JsonValueKind.Array) return;
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (i >= _prefix) _node.Validate(item, path.Item(i), errs);
            i++;
        }
    }
}

internal sealed class RefRule : Rule
{
    public RefRule(string sp, SchemaNode target) : base("$ref", sp) { Target = target; }
    public SchemaNode Target { get; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs) => Target.Validate(v, path, errs);
    public override IEnumerable<SchemaNode> SameInstance => [Target];
}

internal sealed class AllOfRule : Rule
{
    private readonly SchemaNode[] _nodes;
    public AllOfRule(string sp, SchemaNode[] nodes) : base("allOf", sp) { _nodes = nodes; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        foreach (var n in _nodes) n.Validate(v, path, errs);
    }
    public override IEnumerable<SchemaNode> SameInstance => _nodes;
}

internal sealed class NotRule : Rule
{
    private readonly SchemaNode _node;
    public NotRule(string sp, SchemaNode node) : base("not", sp) { _node = node; }
    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        var inner = errs.Child();
        _node.Validate(v, path, inner);
        if (inner.Count == 0) Fail(errs, path, "Value must not match the schema.");
    }
    public override IEnumerable<SchemaNode> SameInstance => [_node];
}

/// <summary>
/// <c>anyOf</c> / <c>oneOf</c>. Branches are tried only when their (resolved) <c>type</c> accepts the instance and, for
/// discriminated unions, when their <c>const</c> discriminator equals the instance's value. A failure reports the single
/// remaining branch's own errors, a discriminator error, or a generic union error.
/// </summary>
internal sealed class UnionRule : Rule
{
    private readonly SchemaNode[] _branches;
    private readonly bool _one;
    private HashSet<string>?[] _types = [];
    private string? _disc;
    private JsonElement[]?[] _allowed = []; // per branch: the discriminator values it accepts; null = no discriminator

    public UnionRule(string keyword, string sp, SchemaNode[] branches) : base(keyword, sp)
    {
        _branches = branches;
        _one = keyword == "oneOf";
    }

    public override IEnumerable<SchemaNode> SameInstance => _branches;

    /// <summary>Resolves branch types and the discriminator once every reference is bound.</summary>
    public void Prepare()
    {
        _types = _branches.Select(TypesOf).ToArray();
        _allowed = new JsonElement[]?[_branches.Length];
        var objects = Enumerable.Range(0, _branches.Length).Where(i => _types[i] == null || _types[i]!.Contains("object")).ToList();
        if (objects.Count < 2) return;
        var maps = objects.Select(i => Discriminators(_branches[i])).ToList();
        var name = maps.SelectMany(m => m.Keys).FirstOrDefault();
        if (name == null) return;
        _disc = name;
        for (var k = 0; k < objects.Count; k++)
            if (maps[k].TryGetValue(name, out var values)) _allowed[objects[k]] = values;
    }

    public override void Apply(JsonElement v, InstancePath path, Errs errs)
    {
        // The same union on the same instance value gives the same errors. Inside another union's trial a value can be
        // tried again and again (an expression inside an expression inside ...), so there each result on an object or
        // array is kept for the run: nested unions then cost linear, not exponential, time.
        var run = errs.Run;
        if (run.Trials == 0 || v.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            ApplyOnce(v, path, errs);
            return;
        }
        var key = (this, path);
        if (run.Unions.TryGetValue(key, out var known))
        {
            errs.AddAll(known);
            return;
        }
        var own = errs.Child();
        ApplyOnce(v, path, own);
        if (!run.Exhausted) run.Unions[key] = own.Count == 0 ? run.NoErrors : own;
        errs.AddAll(own);
    }

    private void ApplyOnce(JsonElement v, InstancePath path, Errs errs)
    {
        var cand = Enumerable.Range(0, _branches.Length).Where(i => Json.Allows(_types[i], v)).ToList();
        var disc = v.ValueKind == JsonValueKind.Object ? _disc : null;
        JsonElement dv = default;
        var hasDisc = false;
        if (disc != null)
        {
            hasDisc = v.TryGetProperty(disc, out dv);
            // A branch without a discriminator of its own (e.g. a nested union) stays a candidate.
            cand = cand.Where(i => _allowed[i] == null || (hasDisc && _allowed[i]!.Any(a => Json.DeepEquals(a, dv)))).ToList();
        }

        var trials = new List<Errs>();
        var matched = 0;
        errs.Run.Trials++;
        foreach (var i in cand)
        {
            var e = errs.Child();
            _branches[i].Validate(v, path, e);
            if (e.Count == 0) matched++;
            trials.Add(e);
        }
        errs.Run.Trials--;
        if (_one ? matched == 1 : matched >= 1) return;
        if (matched > 1) { Fail(errs, path, "Value matches more than one alternative."); return; }

        if (disc != null && cand.Count == 0 && !hasDisc)
            Fail(errs, path, $"Missing required property \"{disc}\".", "required");
        else if (disc != null && cand.Count == 0)
        {
            var allowed = string.Join(", ", _allowed.Where(a => a != null).SelectMany(a => a!).Select(a => a.GetRawText()).Distinct());
            Fail(errs, path.Prop(disc), $"Value {dv.GetRawText()} is not one of: {allowed}.");
        }
        else
        {
            // A branch whose discriminator matched is the likely intent; branches without one (nested unions) only count when alone.
            var report = Enumerable.Range(0, cand.Count).Where(k => _allowed[cand[k]] != null).ToList();
            if (report.Count == 0) report = [.. Enumerable.Range(0, cand.Count)];
            if (report.Count == 1) errs.AddAll(trials[report[0]]);
            else Fail(errs, path, "Value does not match any of the alternatives.");
        }
    }

    /// <summary>The types a schema accepts, following <c>$ref</c>; null means unconstrained.</summary>
    private static HashSet<string>? TypesOf(SchemaNode n)
    {
        if (n is BoolNode b) return b.Value ? null : [];
        HashSet<string>? acc = null;
        foreach (var r in ((ObjectNode)n).Rules)
        {
            HashSet<string>? t = r switch { TypeRule tr => [.. tr.Types], RefRule rr => TypesOf(rr.Target), _ => null };
            if (t == null) continue;
            if (acc == null) { acc = t; continue; }
            var both = new HashSet<string>();
            foreach (var x in acc)
            {
                if (t.Contains(x)) both.Add(x);
                else if (x == "number" && t.Contains("integer")) both.Add("integer");
                else if (x == "integer" && t.Contains("number")) both.Add("integer");
            }
            acc = both;
        }
        return acc;
    }

    /// <summary>Properties a branch both requires and restricts with <c>const</c> or <c>enum</c> (through <c>$ref</c>), with their allowed values.</summary>
    private static Dictionary<string, JsonElement[]> Discriminators(SchemaNode n)
    {
        var required = new HashSet<string>();
        var values = new Dictionary<string, JsonElement[]>();
        Collect(n);
        return values.Where(kv => required.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        void Collect(SchemaNode node)
        {
            if (node is not ObjectNode o) return;
            foreach (var r in o.Rules)
                switch (r)
                {
                    case RequiredRule rq: required.UnionWith(rq.Names); break;
                    case PropertiesRule pr:
                        foreach (var (name, sub) in pr.Props)
                            if (ValuesOf(sub) is { } vs) values[name] = vs;
                        break;
                    case RefRule rf: Collect(rf.Target); break;
                }
        }
    }

    private static JsonElement[]? ValuesOf(SchemaNode n)
    {
        if (n is not ObjectNode o) return null;
        foreach (var r in o.Rules)
        {
            if (r is ConstRule c) return [c.Value];
            if (r is EnumRule e) return e.Values;
            if (r is RefRule rf && ValuesOf(rf.Target) is { } inner) return inner;
        }
        return null;
    }
}

/// <summary>The state of one <see cref="SchemaSet.Validate"/> call: its work budget and the union results already known.</summary>
internal sealed class SchemaRun(long budget)
{
    public long Budget { get; } = budget;
    public long Work { get; private set; }
    public bool Exhausted { get; private set; }
    public Dictionary<(UnionRule Rule, InstancePath Path), Errs> Unions { get; } = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    /// <summary>How many union trials are under way: results are kept only inside one.</summary>
    public int Trials { get; set; }
    /// <summary>The one kept result for every union application without errors.</summary>
    public Errs NoErrors => _noErrors ??= new Errs(this);
    private Errs? _noErrors;

    /// <summary>Counts one schema applied to one value; false once the budget is spent.</summary>
    public bool Step() => Charge(1);

    /// <summary>Counts <paramref name="steps"/> steps of other work (such as the values compared by <c>uniqueItems</c>); false once the budget is spent.</summary>
    public bool ChargeSteps(long steps) => Charge(steps);

    /// <summary>Counts reading one instance property name: one step per <see cref="CharsPerStep"/> characters; false once the budget is spent.</summary>
    public bool ChargeName(int length) => Charge(length / CharsPerStep);

    /// <summary>Characters of a property name that cost one step of work; a name's add one value's worth of budget.</summary>
    public const int CharsPerStep = 64;

    private bool Charge(long steps)
    {
        if (Exhausted) return false;
        Work += steps;
        if (Work <= Budget) return true;
        Exhausted = true;
        return false;
    }

    /// <summary>
    /// The run's one instance of an instance property name, so that equal paths share their segments and compare by
    /// reference (the union results are kept by path). The caller has charged the name's length.
    /// </summary>
    public string Intern(string name)
    {
        if (_names.TryGetValue(name, out var known)) return known;
        _names.Add(name);
        return name;
    }
}

/// <summary>
/// An RFC 6901 pointer into the instance, kept as a chain of segments: going one level down costs one small object
/// whatever the path's length, and the pointer string is built only for an error that is reported.
/// </summary>
internal sealed class InstancePath : IEquatable<InstancePath>
{
    public static InstancePath Root { get; } = new(null, null, 0);

    private readonly InstancePath? _parent;
    private readonly string? _name; // null: an array index
    private readonly int _index;
    private readonly int _depth;
    private readonly int _hash;

    private InstancePath(InstancePath? parent, string? name, int index)
    {
        _parent = parent;
        _name = name;
        _index = index;
        _depth = parent is null ? 0 : parent._depth + 1;
        _hash = parent is null ? 0 : HashCode.Combine(parent._hash, name is null ? index : StringComparer.Ordinal.GetHashCode(name));
    }

    public InstancePath Prop(string name) => new(this, name, 0);
    public InstancePath Item(int index) => new(this, null, index);

    public bool Equals(InstancePath? other)
    {
        var a = this;
        var b = other;
        while (!ReferenceEquals(a, b))
        {
            if (a is null || b is null || a._hash != b._hash || a._depth != b._depth || a._index != b._index
                || !string.Equals(a._name, b._name, StringComparison.Ordinal))
                return false;
            a = a._parent;
            b = b._parent;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is InstancePath p && Equals(p);
    public override int GetHashCode() => _hash;

    /// <summary>The pointer string ("" for the root).</summary>
    public override string ToString()
    {
        if (_parent is null) return "";
        var segments = new string[_depth];
        for (var p = this; p._parent is not null; p = p._parent)
            segments[p._depth - 1] = p._name is null ? p._index.ToString(System.Globalization.CultureInfo.InvariantCulture) : Json.Esc(p._name);
        return "/" + string.Join('/', segments);
    }
}

/// <summary>One error as validation collects it; its path becomes a string only if the error is reported.</summary>
internal readonly record struct Err(InstancePath Path, string Keyword, string Message, string SchemaPath)
{
    public SchemaError ToError() => new(Path.ToString(), Keyword, Message, SchemaPath);
}

/// <summary>Collected errors: every one is counted, the first <see cref="Keep"/> are kept.</summary>
internal sealed class Errs(SchemaRun run)
{
    public const int Keep = 100;
    public SchemaRun Run { get; } = run;
    public List<Err> Items { get; } = [];
    public int Count { get; private set; }

    public void Add(Err error)
    {
        Count++;
        if (Items.Count < Keep) Items.Add(error);
    }

    public void AddAll(Errs other)
    {
        foreach (var e in other.Items)
        {
            if (Items.Count >= Keep) break;
            Items.Add(e);
        }
        Count += other.Count;
    }

    public Errs Child() => new(Run);
}
