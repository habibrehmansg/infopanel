using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoPanel.Spec.Schema;

/// <summary>Parses schema documents and compiles them into <see cref="SchemaNode"/> trees, checking every keyword and <c>$ref</c>.</summary>
internal sealed class SchemaLoader
{
    private const string Draft = "https://json-schema.org/draft/2020-12/schema";
    private static readonly HashSet<string> TypeNames = ["null", "boolean", "object", "array", "number", "integer", "string"];

    private readonly Dictionary<string, JsonElement> _roots = new(StringComparer.Ordinal);
    private readonly List<JsonDocument> _docs = [];
    private readonly Dictionary<(string Id, string Pointer), SchemaNode> _nodes = [];
    private readonly List<UnionRule> _unions = [];

    public SchemaSet Load(IEnumerable<string> texts)
    {
        var n = 0;
        foreach (var text in texts)
        {
            n++;
            var label = $"document {n}";
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 }); }
            catch (JsonException ex) { throw new SchemaException(label, "", "Not valid JSON: " + ex.Message); }
            _docs.Add(doc);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new SchemaException(label, "", "The root must be an object with a string \"$id\".");
            var id = idEl.GetString()!;
            if (!Uri.TryCreate(id, UriKind.Absolute, out _) || id.Contains('#'))
                throw new SchemaException(id, "/$id", "\"$id\" must be an absolute URI without a fragment.");
            if (!_roots.TryAdd(id, root)) throw new SchemaException(id, "/$id", "Duplicate \"$id\".");
        }

        foreach (var (id, root) in _roots) Compile(id, "", root);
        CheckLoops();
        foreach (var u in _unions) u.Prepare();
        return new SchemaSet(_docs, _roots.Keys.ToArray(), _nodes);
    }

    private SchemaNode Compile(string id, string ptr, JsonElement el)
    {
        if (_nodes.TryGetValue((id, ptr), out var cached)) return cached;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return _nodes[(id, ptr)] = new BoolNode(id, ptr, el.ValueKind == JsonValueKind.True);
        if (el.ValueKind != JsonValueKind.Object)
            throw new SchemaException(id, ptr, "A schema must be an object or a boolean.");

        var node = new ObjectNode(id, ptr);
        _nodes[(id, ptr)] = node; // registered before the keywords so recursive $refs find it
        var rules = new List<Rule>();
        foreach (var p in el.EnumerateObject()) Keyword(id, ptr, el, p, rules);
        node.Rules = rules.ToArray();
        return node;
    }

    private void Keyword(string id, string ptr, JsonElement schema, JsonProperty p, List<Rule> rules)
    {
        var name = p.Name;
        var v = p.Value;
        var kp = ptr + "/" + Json.Esc(name);
        var sp = id + "#" + kp;
        SchemaException Bad(string msg) => new(id, kp, msg);
        void Expect(JsonValueKind kind, string what)
        {
            if (v.ValueKind != kind) throw Bad($"\"{name}\" must be {what}.");
        }
        SchemaNode[] Subs()
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) throw Bad($"\"{name}\" must be a non-empty array of schemas.");
            var i = 0;
            return v.EnumerateArray().Select(e => Compile(id, kp + "/" + i++, e)).ToArray();
        }
        int NonNegInt()
        {
            if (v.ValueKind != JsonValueKind.Number || !JsonNumber.TryReadInteger(v, out var d) || d < 0 || d > int.MaxValue)
                throw Bad($"\"{name}\" must be a non-negative integer.");
            return (int)d;
        }

        switch (name)
        {
            case "$schema":
                if (v.ValueKind != JsonValueKind.String || v.GetString() != Draft) throw Bad($"\"$schema\" must be \"{Draft}\".");
                break;
            case "$id":
                if (ptr != "") throw Bad("\"$id\" is only allowed at the root of a schema document.");
                break;
            case "$comment" or "title" or "description":
                Expect(JsonValueKind.String, "a string");
                break;
            case "default":
                break;
            case "examples":
                Expect(JsonValueKind.Array, "an array");
                break;
            case "deprecated" or "readOnly" or "writeOnly":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad($"\"{name}\" must be a boolean.");
                break;
            case "$defs":
                Expect(JsonValueKind.Object, "an object of schemas");
                foreach (var d in v.EnumerateObject()) Compile(id, kp + "/" + Json.Esc(d.Name), d.Value);
                break;
            case "$ref":
                Expect(JsonValueKind.String, "a string");
                rules.Add(new RefRule(sp, ResolveRef(id, v.GetString()!, kp)));
                break;
            case "allOf":
                rules.Add(new AllOfRule(sp, Subs()));
                break;
            case "anyOf" or "oneOf":
                var union = new UnionRule(name, sp, Subs());
                _unions.Add(union);
                rules.Add(union);
                break;
            case "not":
                rules.Add(new NotRule(sp, Compile(id, kp, v)));
                break;
            case "properties":
                Expect(JsonValueKind.Object, "an object of schemas");
                rules.Add(new PropertiesRule(sp, v.EnumerateObject().Select(x => (x.Name, Compile(id, kp + "/" + Json.Esc(x.Name), x.Value))).ToArray()));
                break;
            case "additionalProperties":
                var known = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                    ? props.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                    : [];
                var extra = Compile(id, kp, v);
                if (extra is not BoolNode { Value: true }) rules.Add(new AdditionalPropertiesRule(sp, extra, known));
                break;
            case "propertyNames":
                rules.Add(new PropertyNamesRule(sp, Compile(id, kp, v)));
                break;
            case "items":
                var prefix = schema.TryGetProperty("prefixItems", out var pi) && pi.ValueKind == JsonValueKind.Array ? pi.GetArrayLength() : 0;
                rules.Add(new ItemsRule(sp, Compile(id, kp, v), prefix));
                break;
            case "prefixItems":
                rules.Add(new PrefixItemsRule(sp, Subs()));
                break;
            case "type":
                var types = new List<string>();
                if (v.ValueKind == JsonValueKind.String) types.Add(v.GetString()!);
                else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                    foreach (var t in v.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.String) throw Bad("\"type\" array entries must be strings.");
                        types.Add(t.GetString()!);
                    }
                else throw Bad("\"type\" must be a type name or a non-empty array of type names.");
                foreach (var t in types)
                    if (!TypeNames.Contains(t)) throw Bad($"Unknown type \"{t}\".");
                if (types.Distinct().Count() != types.Count) throw Bad("\"type\" lists a type twice.");
                rules.Add(new TypeRule(sp, types.ToArray()));
                break;
            case "enum":
                Expect(JsonValueKind.Array, "an array");
                rules.Add(new EnumRule(sp, v));
                break;
            case "const":
                rules.Add(new ConstRule(sp, v));
                break;
            case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                Expect(JsonValueKind.Number, "a number");
                rules.Add(new NumberRule(name, sp, v));
                break;
            case "minLength" or "maxLength":
                rules.Add(new LengthRule(name, sp, NonNegInt(), name == "minLength"));
                break;
            case "pattern":
                Expect(JsonValueKind.String, "a string");
                var pattern = v.GetString()!;
                // Linear-time matching whatever the pattern: no backtracking (and so no backreferences or lookarounds).
                try { rules.Add(new PatternRule(sp, pattern, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)))); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw Bad("Invalid regular expression: " + ex.Message); }
                break;
            case "minItems" or "maxItems":
                rules.Add(new CountRule(name, sp, JsonValueKind.Array, NonNegInt(), name == "minItems"));
                break;
            case "minProperties" or "maxProperties":
                rules.Add(new CountRule(name, sp, JsonValueKind.Object, NonNegInt(), name == "minProperties"));
                break;
            case "uniqueItems":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("\"uniqueItems\" must be a boolean.");
                if (v.ValueKind == JsonValueKind.True) rules.Add(new UniqueRule(sp));
                break;
            case "required":
                Expect(JsonValueKind.Array, "an array of strings");
                var names = v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Bad("\"required\" entries must be strings.")).ToArray();
                if (names.Distinct().Count() != names.Length) throw Bad("\"required\" lists a property twice.");
                rules.Add(new RequiredRule(sp, names));
                break;
            default:
                throw Bad($"Unsupported keyword \"{name}\".");
        }
    }

    /// <summary>Binds <c>$ref</c> (<c>#/ptr</c>, <c>id</c> or <c>id#/ptr</c>) to a compiled node.</summary>
    private SchemaNode ResolveRef(string id, string reference, string kp)
    {
        var hash = reference.IndexOf('#');
        var target = hash < 0 ? reference : hash == 0 ? id : reference[..hash];
        var frag = hash < 0 ? "" : reference[(hash + 1)..];
        SchemaException Bad(string why) => new(id, kp, $"$ref \"{reference}\" does not resolve: {why}.");

        if (!_roots.TryGetValue(target, out var el)) throw Bad($"no schema with $id \"{target}\" is loaded");
        if (frag.Length > 0 && frag[0] != '/') throw Bad("only JSON pointer fragments are supported");

        var norm = "";
        foreach (var raw in frag.Length == 0 ? [] : frag[1..].Split('/'))
        {
            var seg = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(seg, out var next)) el = next;
            else if (el.ValueKind == JsonValueKind.Array && int.TryParse(seg, out var i) && i >= 0 && i < el.GetArrayLength()) el = el[i];
            else throw Bad($"the pointer \"{frag}\" leads nowhere");
            norm += "/" + Json.Esc(seg);
        }
        return Compile(target, norm, el);
    }

    /// <summary>Rejects reference cycles that never descend into the instance (they would recurse forever).</summary>
    private void CheckLoops()
    {
        var state = new Dictionary<SchemaNode, bool>(); // false = in progress, true = done
        foreach (var n in _nodes.Values) Visit(n);

        void Visit(SchemaNode n)
        {
            if (state.TryGetValue(n, out var done))
            {
                if (!done) throw new SchemaException(n.Id, n.Pointer, "$ref loop that does not descend into the instance.");
                return;
            }
            state[n] = false;
            foreach (var c in n.SameInstance) Visit(c);
            state[n] = true;
        }
    }
}
