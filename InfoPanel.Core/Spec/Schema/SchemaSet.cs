using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InfoPanel.Spec.Schema;

/// <summary>A loaded, immutable, thread-safe set of JSON Schema (draft 2020-12 subset) documents.</summary>
public sealed class SchemaSet
{
    /// <summary>
    /// Instances nested deeper than this many arrays/objects are rejected with a <c>depth</c> error, unless a validation
    /// asks for less (the format's <c>jsonDepth</c> limit).
    /// </summary>
    public const int MaxInstanceDepth = 256;

    private readonly IReadOnlyList<JsonDocument> _docs; // keeps the schema JSON alive
    private readonly Dictionary<(string Id, string Pointer), SchemaNode> _nodes;

    internal SchemaSet(IReadOnlyList<JsonDocument> docs, IReadOnlyCollection<string> ids, Dictionary<(string Id, string Pointer), SchemaNode> nodes)
    {
        _docs = docs;
        Ids = ids;
        _nodes = nodes;
    }

    /// <summary>The <c>$id</c> of every loaded document.</summary>
    public IReadOnlyCollection<string> Ids { get; }

    /// <summary>
    /// Loads schema documents; each text is one document whose root has an absolute <c>$id</c>. Every keyword, value shape and
    /// <c>$ref</c> is checked here.
    /// </summary>
    /// <exception cref="SchemaException">A document is invalid JSON, lacks or repeats <c>$id</c>, uses an unsupported keyword
    /// or a value of the wrong shape, or has a <c>$ref</c> that does not resolve.</exception>
    public static SchemaSet Load(IEnumerable<string> schemaJsonTexts) => new SchemaLoader().Load(schemaJsonTexts);

    /// <summary>
    /// Steps of work (one schema applied to one value) allowed per value of the instance, plus <see cref="BaseWork"/>. Real
    /// content needs a small fraction of it; content over it gets one <c>complexity</c> error instead of a result.
    /// </summary>
    public const int WorkPerValue = 64;

    /// <summary>Steps of work every validation may take, whatever the instance's size.</summary>
    public const int BaseWork = 100_000;

    /// <summary>
    /// Validates an instance against a schema, collecting errors: every error is counted, the first
    /// <see cref="MaxErrors"/> are returned, and a last <c>more</c> error says how many more there were. The instance is
    /// read first as the formats read JSON (<see cref="CheckJson"/>); when that finds anything, its errors are the result
    /// and the schema isn't applied.
    /// </summary>
    /// <param name="instance">The JSON value to check.</param>
    /// <param name="schemaRef">An absolute id (<c>urn:infopanel:scene:1</c>) or id plus fragment (<c>urn:infopanel:api:1#/$defs/commitRequest</c>).</param>
    /// <param name="maxDepth">The deepest nesting accepted (<see cref="CheckJson"/>); at most <see cref="MaxInstanceDepth"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="schemaRef"/> names no loaded schema.</exception>
    public SchemaValidationResult Validate(JsonElement instance, string schemaRef, int maxDepth = MaxInstanceDepth)
    {
        var node = Find(schemaRef) ?? throw new ArgumentException($"Unknown schema reference \"{schemaRef}\".", nameof(schemaRef));
        var json = CheckJson(instance, maxDepth);
        return json.IsValid ? Apply(node, instance) : json;
    }

    /// <summary>
    /// The schema alone, for an instance that already passed <see cref="CheckJson"/>, on its own or inside the content
    /// that was checked (a scene inside its document or commit, a create's envelope inside its body): the JSON-level pass
    /// runs once, first, and never again inside the schema.
    /// </summary>
    internal SchemaValidationResult ValidateChecked(JsonElement instance, string schemaRef)
    {
        var node = Find(schemaRef) ?? throw new ArgumentException($"Unknown schema reference \"{schemaRef}\".", nameof(schemaRef));
        return Apply(node, instance);
    }

    private static SchemaValidationResult Apply(SchemaNode node, JsonElement instance)
    {
        var sp = node.Id + "#" + node.Pointer;
        var run = new SchemaRun(BaseWork + WorkPerValue * Values(instance));
        var errs = new Errs(run);
        node.Validate(instance, InstancePath.Root, errs);
        if (run.Exhausted)
            return new SchemaValidationResult([new SchemaError("", "complexity",
                $"The value is too complex to validate (over {run.Budget} steps).", sp)]);

        // Only the errors returned get a pointer string.
        var steps = run.Work;
        var kept = errs.Items.Select(e => e.ToError()).Distinct()
            .OrderBy(e => e.InstancePath, StringComparer.Ordinal).ThenBy(e => e.Keyword, StringComparer.Ordinal).ToList();
        if (errs.Count > errs.Items.Count)
            kept.Add(new SchemaError("", "more", $"{errs.Count - errs.Items.Count} more errors not listed.", sp));
        return new SchemaValidationResult(kept) { Steps = steps, Budget = run.Budget };
    }

    /// <summary>
    /// The size of the work budget: one value per value, and one more per <see cref="SchemaRun.CharsPerStep"/> characters
    /// of a property name. Without recursion (the instance's depth was checked before, but this needs no bound).
    /// </summary>
    private static long Values(JsonElement instance)
    {
        long values = 0;
        var pending = new Stack<JsonElement>();
        pending.Push(instance);
        while (pending.TryPop(out var v))
        {
            values++;
            if (v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray()) pending.Push(item);
            else if (v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                {
                    values += JsonMarshal.GetRawUtf8PropertyName(p).Length / SchemaRun.CharsPerStep;
                    pending.Push(p.Value);
                }
        }
        return values;
    }

    /// <summary>
    /// The JSON-level pass every format runs before anything else (spec/docs/validation.md#json), in one pass over the
    /// content as received, every repeated member included: its nesting (deeper than <paramref name="maxDepth"/>, at
    /// most <see cref="MaxInstanceDepth"/>: the one error <c>depth</c> at the root, and nothing else), then together a
    /// <c>json</c> error at each number whose text rounds to ±∞, a <c>duplicateKey</c> error at each object that repeats
    /// a member name, and a <c>json</c> error at each string that isn't Unicode text (a lone surrogate escape or bytes
    /// that aren't UTF-8; a member name's at its object, whose value is then not read). Ordered by path; the first
    /// <see cref="MaxErrors"/> listed, then a <c>more</c> error.
    /// </summary>
    public static SchemaValidationResult CheckJson(JsonElement instance, int maxDepth = MaxInstanceDepth)
    {
        var pass = new JsonPass(Math.Min(maxDepth, MaxInstanceDepth));
        return pass.Walk(instance, 1) ? pass.Result() : pass.Deep();
    }

    /// <summary>
    /// <see cref="CheckJson"/> for a request whose root object holds content checked on its own (a create's
    /// <c>{id, document}</c>): the root's member names are read as there, and each member's value is a root of its own,
    /// whose nesting is counted from it and reported at it (<c>depth</c> at <c>/document</c>). Every occurrence of a
    /// repeated member is read. Any other value: <see cref="CheckJson"/>.
    /// </summary>
    public static SchemaValidationResult CheckJsonMembers(JsonElement root, int maxDepth = MaxInstanceDepth)
    {
        if (root.ValueKind != JsonValueKind.Object) return CheckJson(root, maxDepth);
        var pass = new JsonPass(Math.Min(maxDepth, MaxInstanceDepth));
        return pass.Members(root, 0) ? pass.Result() : pass.Deep();
    }

    /// <summary>Errors returned by one validation before the last <c>more</c> error.</summary>
    public const int MaxErrors = Errs.Keep;

    /// <summary>
    /// One JSON-level pass (<see cref="CheckJson"/>). The path is a stack of segments, made a pointer only for an error, so
    /// that reading content allocates nothing per value (a body of millions of values is read before any count limit).
    /// </summary>
    private sealed class JsonPass(int max)
    {
        private readonly List<Err> _errors = [];
        private readonly List<(JsonProperty? Member, int Index)> _path = [];
        private long _count;
        private InstancePath _deepRoot = InstancePath.Root;

        private InstancePath Path(int length)
        {
            var path = InstancePath.Root;
            for (var k = 0; k < length; k++)
                path = _path[k].Member is { } member ? path.Prop(member.Name) : path.Item(_path[k].Index);
            return path;
        }

        private void Add(string keyword, string message)
        {
            if (_count++ < MaxErrors) _errors.Add(new Err(Path(_path.Count), keyword, message, ""));
        }

        public SchemaValidationResult Result()
        {
            var listed = _errors.Select(e => e.ToError()).OrderBy(e => e.InstancePath, StringComparer.Ordinal).ToList();
            if (_count > _errors.Count)
                listed.Add(new SchemaError("", "more", $"{_count - _errors.Count} more errors not listed.", ""));
            return new SchemaValidationResult(listed);
        }

        /// <summary>The one <c>depth</c> error, at the root whose nesting went over (the content's, or a member's for <see cref="CheckJsonMembers"/>).</summary>
        public SchemaValidationResult Deep() =>
            new([new Err(_deepRoot, "depth", $"Value is nested deeper than {max} levels.", "").ToError()]);

        /// <summary>False when the value nests deeper than the bound, counted from <paramref name="depth"/>.</summary>
        public bool Walk(JsonElement v, int depth)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!JsonNumber.TryRead(v, out _))
                        Add("json", "The number rounds to infinity as a 64-bit double: not a number these formats read.");
                    return true;
                case JsonValueKind.String:
                    if (!JsonText.IsScalarText(JsonMarshal.GetRawUtf8Value(v)))
                        Add("json", "The string isn't Unicode text: it holds a lone surrogate or bytes that aren't UTF-8.");
                    return true;
                case JsonValueKind.Array:
                {
                    if (depth > max) return false;
                    var i = 0;
                    foreach (var item in v.EnumerateArray())
                    {
                        _path.Add((null, i++));
                        var ok = Walk(item, depth + 1);
                        _path.RemoveAt(_path.Count - 1);
                        if (!ok) return false;
                    }
                    return true;
                }
                case JsonValueKind.Object:
                    return depth <= max && Members(v, depth);
                default:
                    return true;
            }
        }

        /// <summary>
        /// An object's members, every occurrence of a repeated one included: a <c>duplicateKey</c> error at the object for
        /// each repeated name, a <c>json</c> error at it for each name that isn't Unicode text (its value left unread: no
        /// path can name it), and each value walked at <paramref name="depth"/> + 1 (0: each value a root of its own).
        /// </summary>
        public bool Members(JsonElement v, int depth)
        {
            HashSet<string>? seen = null, reported = null;
            var several = v.GetPropertyCount() > 1;
            foreach (var p in v.EnumerateObject())
            {
                if (!JsonText.IsScalarText(JsonMarshal.GetRawUtf8PropertyName(p)))
                {
                    Add("json", "A member name isn't Unicode text: it holds a lone surrogate or bytes that aren't UTF-8.");
                    continue;
                }
                if (several && p.Name is var name && !(seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(name)
                    && (reported ??= new HashSet<string>(StringComparer.Ordinal)).Add(name))
                    Add("duplicateKey", $"Duplicate property \"{name}\".");
                _path.Add((p, 0));
                var ok = Walk(p.Value, depth + 1);
                if (!ok && depth == 0) _deepRoot = Path(1);
                _path.RemoveAt(_path.Count - 1);
                if (!ok) return false;
            }
            return true;
        }
    }

    private SchemaNode? Find(string schemaRef)
    {
        var hash = schemaRef.IndexOf('#');
        var id = hash < 0 ? schemaRef : schemaRef[..hash];
        var frag = hash < 0 ? "" : schemaRef[(hash + 1)..];
        if (frag.Length > 0 && frag[0] != '/') return null;
        var ptr = string.Concat((frag.Length == 0 ? [] : frag[1..].Split('/'))
            .Select(s => "/" + Json.Esc(Uri.UnescapeDataString(s).Replace("~1", "/").Replace("~0", "~"))));
        return _nodes.GetValueOrDefault((id, ptr));
    }
}

/// <summary>A schema document could not be loaded. The message names the schema id and the JSON pointer.</summary>
public sealed class SchemaException : Exception
{
    /// <summary>Creates the exception; the message reads <c>{schemaId}#{pointer}: {message}</c>.</summary>
    public SchemaException(string schemaId, string pointer, string message) : base($"{schemaId}#{pointer}: {message}")
    {
        SchemaId = schemaId;
        Pointer = pointer;
    }

    /// <summary>The <c>$id</c> of the offending document (or "document N" when it has none).</summary>
    public string SchemaId { get; }

    /// <summary>JSON pointer into that document.</summary>
    public string Pointer { get; }
}

/// <summary>One validation failure.</summary>
/// <param name="InstancePath">RFC 6901 pointer into the instance ("" is the root).</param>
/// <param name="Keyword">The failing keyword (<c>type</c>, <c>required</c>, ...), or one of the JSON-level pass
/// (<see cref="SchemaSet.CheckJson"/>): <c>depth</c>, <c>duplicateKey</c>, or <c>json</c> for a number that rounds to ±∞
/// or a string that isn't Unicode text.</param>
/// <param name="Message">A short English sentence.</param>
/// <param name="SchemaPath"><c>{schema id}#{pointer into that schema}</c> of the failing keyword.</param>
public sealed record SchemaError(string InstancePath, string Keyword, string Message, string SchemaPath);

/// <summary>The outcome of <see cref="SchemaSet.Validate"/>.</summary>
public sealed class SchemaValidationResult
{
    internal SchemaValidationResult(IReadOnlyList<SchemaError> errors) { Errors = errors; }

    /// <summary>The steps of work the validation took, and its budget (for tests).</summary>
    internal long Steps { get; init; }
    internal long Budget { get; init; }

    /// <summary>True when there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Empty when valid; ordered by instance path, then keyword.</summary>
    public IReadOnlyList<SchemaError> Errors { get; }
}
