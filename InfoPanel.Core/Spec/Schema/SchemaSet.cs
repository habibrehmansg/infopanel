using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <see cref="MaxErrors"/> are returned, and a last <c>more</c> error says how many more there were.
    /// </summary>
    /// <param name="instance">The JSON value to check.</param>
    /// <param name="schemaRef">An absolute id (<c>urn:infopanel:scene:1</c>) or id plus fragment (<c>urn:infopanel:api:1#/$defs/commitRequest</c>).</param>
    /// <param name="maxDepth">The deepest nesting accepted (a value nested deeper gets one <c>depth</c> error and nothing else is
    /// checked); at most <see cref="MaxInstanceDepth"/>. Within it, a number whose text rounds to ±∞ as a double is a
    /// <c>json</c> error at the number (spec/docs/validation.md#numbers), and when there is one the schema isn't applied.</param>
    /// <exception cref="ArgumentException"><paramref name="schemaRef"/> names no loaded schema.</exception>
    public SchemaValidationResult Validate(JsonElement instance, string schemaRef, int maxDepth = MaxInstanceDepth)
    {
        var node = Find(schemaRef) ?? throw new ArgumentException($"Unknown schema reference \"{schemaRef}\".", nameof(schemaRef));
        var sp = node.Id + "#" + node.Pointer;
        var scan = new List<Err>();
        var infinite = new List<Err>();
        long values = 0, infinities = 0;
        if (!Scan(instance, InstancePath.Root, 1, Math.Min(maxDepth, MaxInstanceDepth), scan, infinite, sp, ref values, ref infinities))
            return new SchemaValidationResult(scan.Where(e => e.Keyword == "depth").Take(1).Select(e => e.ToError()).ToList());
        if (infinities > 0)
            return Infinite(infinite, infinities, sp);

        var run = new SchemaRun(BaseWork + WorkPerValue * values);
        var errs = new Errs(run);
        foreach (var e in scan) errs.Add(e);
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
    /// Only the numbers of <see cref="Validate"/>'s first pass, with no schema: a <c>json</c> error at each number whose
    /// text rounds to ±∞ (spec/docs/validation.md#numbers), the first <see cref="MaxErrors"/> listed, then a <c>more</c>
    /// error. For content checked before something of it is replaced (a create's document before its id is). An instance
    /// nested deeper than <paramref name="maxDepth"/> gives no errors here: its validation reports it.
    /// </summary>
    public static SchemaValidationResult CheckNumbers(JsonElement instance, int maxDepth = MaxInstanceDepth)
    {
        var scan = new List<Err>();
        var infinite = new List<Err>();
        long values = 0, infinities = 0;
        if (!Scan(instance, InstancePath.Root, 1, Math.Min(maxDepth, MaxInstanceDepth), scan, infinite, "", ref values, ref infinities) || infinities == 0)
            return new SchemaValidationResult([]);
        return Infinite(infinite, infinities, "");
    }

    private static SchemaValidationResult Infinite(List<Err> infinite, long infinities, string sp)
    {
        var errors = infinite.Select(e => e.ToError()).OrderBy(e => e.InstancePath, StringComparer.Ordinal).ToList();
        if (infinities > infinite.Count)
            errors.Add(new SchemaError("", "more", $"{infinities - infinite.Count} more errors not listed.", sp));
        return new SchemaValidationResult(errors);
    }

    /// <summary>Errors returned by one validation before the last <c>more</c> error.</summary>
    public const int MaxErrors = Errs.Keep;

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

    /// <summary>
    /// One pass over the instance: depth guard, duplicate property names, numbers that round to ±∞ (the first
    /// <see cref="MaxErrors"/> in <paramref name="infinite"/>, all counted in <paramref name="infinities"/>), and the size
    /// of the work budget (one value per value, and one more per <see cref="SchemaRun.CharsPerStep"/> characters of a
    /// property name). False when the instance is too deep. Paths are segment chains: no pointer string is built unless
    /// an error is reported.
    /// </summary>
    private static bool Scan(JsonElement v, InstancePath path, int depth, int max, List<Err> errs, List<Err> infinite, string sp,
        ref long values, ref long infinities)
    {
        values++;
        if (v.ValueKind == JsonValueKind.Number && !JsonNumber.TryRead(v, out _) && infinities++ < MaxErrors)
            infinite.Add(new Err(path, "json", "The number rounds to infinity as a 64-bit double: not a number these formats read.", sp));
        if (v.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return true;
        if (depth > max)
        {
            errs.Add(new Err(path, "depth", $"Value is nested deeper than {max} levels.", sp));
            return false;
        }
        if (v.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in v.EnumerateArray())
                if (!Scan(item, path.Item(i++), depth + 1, max, errs, infinite, sp, ref values, ref infinities)) return false;
            return true;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            var name = p.Name;
            values += name.Length / SchemaRun.CharsPerStep;
            if (!seen.Add(name) && reported.Add(name) && errs.Count < MaxErrors)
                errs.Add(new Err(path, "duplicateKey", $"Duplicate property \"{name}\".", sp));
            if (!Scan(p.Value, path.Prop(name), depth + 1, max, errs, infinite, sp, ref values, ref infinities)) return false;
        }
        return true;
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
/// <param name="Keyword">The failing keyword (<c>type</c>, <c>required</c>, <c>duplicateKey</c>, <c>depth</c>, ...), or
/// <c>json</c> for a number that rounds to ±∞.</param>
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
