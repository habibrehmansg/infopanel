using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Spec;

/// <summary>
/// The format schemas from <c>spec/schemas</c>, embedded in Core. <see cref="Strict"/> is the published set.
/// <see cref="StructureOnly"/> is the same set with the scene schema replaced by an envelope (an object with a string
/// <c>format</c>), so a document or commit can be checked without its scenes, which <see cref="SceneValidator"/> then
/// checks one by one: one unreadable scene makes one placeholder, not an unreadable document.
/// </summary>
public static class SpecSchemas
{
    public const string Common = "urn:infopanel:common:1";
    public const string Expression = "urn:infopanel:expression:1";
    public const string Scene = "urn:infopanel:scene:1";
    public const string Document = "urn:infopanel:document:1";
    public const string Api = "urn:infopanel:api:1";

    /// <summary>The 1.x items of a 1.x import's answer, generated from the 1.x kind manifest (spec/tools/gen-v1-items.mjs).</summary>
    public const string V1Item = "urn:infopanel:v1item:1";

    public const string SceneFormat = "infopanel.scene/1";

    /// <summary>The profile archive (<c>.infopanel2</c>), named by its manifest.</summary>
    public const string ArchiveFormat = "infopanel.archive/1";

    /// <summary>The profile archive's manifest.</summary>
    public const string Archive = "urn:infopanel:archive:1";
    public const string DocumentFormat = "infopanel.document/1";

    public const string CommitRequest = Api + "#/$defs/commitRequest";

    private const string SceneEnvelope =
        """{"$id":"urn:infopanel:scene:1","type":"object","required":["format"],"properties":{"format":{"type":"string"}}}""";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(LoadTexts);
    private static readonly Lazy<SchemaSet> StrictSet = new(() => SchemaSet.Load(Texts.Value.Values));
    private static readonly Lazy<SchemaSet> StructureSet = new(() =>
        SchemaSet.Load(Texts.Value.Where(t => t.Key != "scene.schema.json").Select(t => t.Value).Append(SceneEnvelope)));

    /// <summary>The published schemas.</summary>
    public static SchemaSet Strict => StrictSet.Value;

    /// <summary>The schemas with scenes checked only for being an object with a string <c>format</c>.</summary>
    public static SchemaSet StructureOnly => StructureSet.Value;

    /// <summary>The schema files by name (<c>scene.schema.json</c>, ...), as embedded.</summary>
    public static IReadOnlyDictionary<string, string> Files => Texts.Value;

    private static IReadOnlyDictionary<string, string> LoadTexts()
    {
        const string prefix = "InfoPanel.Spec.Schemas.";
        var assembly = typeof(SpecSchemas).Assembly;
        var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            texts[name[prefix.Length..]] = reader.ReadToEnd();
        }
        if (texts.Count == 0) throw new InvalidOperationException("The format schemas are not embedded.");
        return texts;
    }
}
