using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Spec;

/// <summary>
/// One problem with a scene, document, commit or asset. <see cref="Path"/> is a JSON pointer into what was checked;
/// <see cref="Code"/> is from spec/docs/validation.md (<c>schema</c>, <c>limit.*</c>, <c>ref.*</c>, ...).
/// </summary>
public sealed record SpecError(string Path, string Code, string Message)
{
    public SpecError Under(string prefix) => this with { Path = prefix + Path };
}

/// <summary>The outcome of a check. Unsupported: needs a format or capability this engine lacks (kept, drawn as a placeholder).</summary>
public enum CheckStatus { Valid, Unsupported, Invalid }

/// <summary>What an engine supports beyond the base of each format's version 1.0.</summary>
public sealed record EngineSupport(IReadOnlySet<string> SceneCapabilities, IReadOnlySet<string> DocumentCapabilities)
{
    /// <summary>The optional capabilities defined by version 1.0 of the scene format (docs/versioning.md).</summary>
    public static IReadOnlySet<string> SceneCapabilities10 { get; } = new HashSet<string> { "media.provider" };

    /// <summary>Everything version 1.0 defines; what the examples are checked against.</summary>
    public static EngineSupport All { get; } = new(SceneCapabilities10, new HashSet<string>());

    /// <summary>The base only: no optional capabilities.</summary>
    public static EngineSupport Base { get; } = new(new HashSet<string>(), new HashSet<string>());
}

internal static class ValidationText
{
    internal static readonly Regex FormatString = new(@"^infopanel\.(scene|document)/([0-9]{1,4})\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static readonly Regex Capability = new(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// A content size (<c>sceneBytes</c>, <c>originBytes</c>, <c>editorBytes</c>, <c>sourceBytes</c>): the length of its
    /// canonical form (spec/docs/limits.md#sizes), however it was sent. A document's (<c>documentBytes</c>) leaves out its
    /// revision metadata: <see cref="CanonicalJson.DocumentSize"/>.
    /// </summary>
    internal static long CanonicalBytes(JsonElement e) => CanonicalJson.Size(e);

    /// <summary>A request's size cap (<c>commitBytes</c>): the bytes of the value as received.</summary>
    internal static long ReceivedBytes(JsonElement e) => System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(e).Length;

    /// <summary>
    /// A schema validation's or JSON-level pass's errors as checks report them: <c>schema</c> (a repeated member name
    /// included), except the count of the rest (<c>more</c>), a number that rounds to ±∞ or a string that isn't Unicode
    /// text (<c>json</c>), and a value nested too deeply, which is the one error <c>limit.jsonDepth</c> at the root of
    /// what was checked.
    /// </summary>
    internal static IEnumerable<SpecError> FromSchema(SchemaValidationResult result, string prefix = "") =>
        result.Errors.Select(e => e.Keyword switch
        {
            "more" => new SpecError(prefix + e.InstancePath, "more", e.Message),
            "json" => new SpecError(prefix + e.InstancePath, "json", e.Message),
            "depth" => new SpecError(prefix + e.InstancePath, "limit.jsonDepth", $"{e.Message.TrimEnd('.')} (jsonDepth)."),
            _ => new SpecError(prefix + e.InstancePath, "schema", $"{e.Message} ({e.Keyword})"),
        });

    /// <summary>
    /// The JSON-level pass (spec/docs/validation.md#json, <see cref="SchemaSet.CheckJson"/>) as a check reports it: null
    /// when the content may be read further, else its errors (at most <see cref="MaxErrors"/> and a <c>more</c>).
    /// </summary>
    internal static IReadOnlyList<SpecError>? CheckJson(JsonElement e, int maxDepth, string prefix = "")
    {
        var result = SchemaSet.CheckJson(e, maxDepth);
        return result.IsValid ? null : FromSchema(result, prefix).ToList();
    }

    /// <summary>
    /// The pre-check every reader does after the JSON-level pass and before the schema: a recognised format string of a major version this engine
    /// reads, and only capabilities it has. Null when the value may be validated further.
    /// </summary>
    internal static (CheckStatus Status, SpecError Error)? CheckFormat(JsonElement e, string kind, IReadOnlySet<string> capabilities)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return (CheckStatus.Invalid, new SpecError("", "schema", $"A {kind} is a JSON object."));
        if (!e.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
            return (CheckStatus.Invalid, new SpecError("/format", "schema", $"A {kind} needs a string \"format\"."));
        var match = FormatString.Match(format.GetString()!);
        if (!match.Success || match.Groups[1].Value != kind)
            return (CheckStatus.Invalid, new SpecError("/format", "schema", $"\"{format.GetString()}\" is not a {kind} format."));
        if (match.Groups[2].Value != "1")
            return (CheckStatus.Unsupported, new SpecError("/format", "unsupported.format",
                $"This engine reads infopanel.{kind}/1, not {format.GetString()}."));
        if (e.TryGetProperty("requires", out var requires) && requires.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var r in requires.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String && Capability.IsMatch(r.GetString()!) && !capabilities.Contains(r.GetString()!))
                    return (CheckStatus.Unsupported, new SpecError($"/requires/{index}", "unsupported.capability",
                        $"This engine doesn't have the capability '{r.GetString()}'."));
                index++;
            }
        }
        return null;
    }

    /// <summary>Errors a check reports: the first ones, then one <c>more</c> error counting the rest.</summary>
    internal const int MaxErrors = 100;

    /// <summary>
    /// At most <see cref="MaxErrors"/> errors, then, whenever there were more, one last <c>more</c> error counting the
    /// rest. A <c>more</c> error already in the list (a schema result, or a scene's capped errors inside a commit's)
    /// counts the errors it stands for, so the result never holds two and never drops a count.
    /// </summary>
    internal static IReadOnlyList<SpecError> Cap(IReadOnlyList<SpecError> errors)
    {
        var listed = new List<SpecError>(System.Math.Min(errors.Count, MaxErrors));
        long hidden = 0;
        foreach (var e in errors)
        {
            if (e.Code == "more") hidden += MoreCount(e);
            else if (listed.Count < MaxErrors) listed.Add(e);
            else hidden++;
        }
        if (hidden > 0) listed.Add(More(hidden));
        return listed;
    }

    /// <summary>The <c>more</c> error for <paramref name="count"/> errors not listed.</summary>
    internal static SpecError More(long count) => new("", "more", $"{count} more errors not listed.");

    private static long MoreCount(SpecError more)
    {
        var digits = more.Message.AsSpan();
        var end = digits.IndexOf(' ');
        return end > 0 && long.TryParse(digits[..end], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 1;
    }

    internal static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");
}
