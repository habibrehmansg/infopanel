using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfoPanel.Spec;

/// <summary>
/// Every acceptance limit of format version 1, and their one home in code (spec/docs/limits.md lists each once, with the
/// reason for its number): scenes, documents, commits, request bodies, the profile list and the tokens, the engine settings,
/// media, archives, the 1.x import and its record, and the preview socket's messages.
/// Every engine that reads infopanel.scene/1, infopanel.document/1 and infopanel.archive/1 accepts everything within them; the numbers never
/// change within major version 1. Each member's camelCase name is the limit's name in docs, error codes
/// (<c>limit.{name}</c>, where its refusal has a code of that form) and the session's <c>limits</c> of the format it
/// belongs to (<see cref="FormatOf"/>). Other options take their values from here (<c>ImportLimits</c>). Tests may tighten
/// a limit to keep an example small (<see cref="With"/>).
/// </summary>
public sealed record SpecLimits
{
    public static SpecLimits Default { get; } = new();

    // Per scene.
    public int SceneBytes { get; init; } = 1_048_576;
    public int Nodes { get; init; } = 2_000;
    public int Depth { get; init; } = 24;
    public int Inputs { get; init; } = 32;
    public int Values { get; init; } = 256;
    public int StateCells { get; init; } = 64;
    public int ExprDepth { get; init; } = 32;
    public int ExprWork { get; init; } = 20_000;
    public int PathCommands { get; init; } = 8_192;
    public int Geometry { get; init; } = 32_768;
    public int TextNodes { get; init; } = 256;
    public int SeriesNodes { get; init; } = 32;
    public int TableNodes { get; init; } = 8;
    public int MediaNodes { get; init; } = 64;
    public int Sequence { get; init; } = 256;
    public int EffectCost { get; init; } = 64;

    /// <summary>Compose paints nested in one another (one inside another's <c>dst</c> or <c>src</c>), the outermost at 1.</summary>
    public int ComposeDepth { get; init; } = 16;

    // Per JSON value: every document and request body read, and every document a commit would make.
    public int JsonDepth { get; init; } = 256;

    // Per document.
    public int DocumentBytes { get; init; } = 16_777_216;
    public int Items { get; init; } = 2_000;
    /// <summary>Item levels: a top-level item is at level 1, a group's children one level below it.</summary>
    public int ItemDepth { get; init; } = 8;
    public int Bindings { get; init; } = 32;
    public int SourceBytes { get; init; } = 262_144;
    public int OriginBytes { get; init; } = 4_096;
    public int EditorBytes { get; init; } = 65_536;
    public int DocumentNodes { get; init; } = 20_000;
    public int DocumentWork { get; init; } = 200_000;
    public int DocumentTextNodes { get; init; } = 1_000;
    public int DocumentEffectCost { get; init; } = 512;

    // Per commit.
    public int CommitBytes { get; init; } = 16_777_216;
    public int Ops { get; init; } = 1_000;

    // Per request body, as received (whitespace around the JSON value included); a commit's is commitBytes, a create's is
    // read up to documentBytes plus the store's read allowance.

    /// <summary>The body of an undo, redo, revert or duplicate.</summary>
    public int RequestBytes { get; init; } = 65_536;

    /// <summary>The body of a token create.</summary>
    public int TokenRequestBytes { get; init; } = 4_096;

    // The profile list and the integration tokens, as the engine stores them: written only within these, and read only
    // within them.

    /// <summary>The stored profile list's bytes; a create or duplicate that would take it over is refused.</summary>
    public int ProfileListBytes { get; init; } = 16_777_216;

    /// <summary>The stored integration tokens' bytes; a token create that would take them over is refused.</summary>
    public int TokensBytes { get; init; } = 16_777_216;

    // The engine settings (InfoPanel.Settings.SettingsStore): a change's body, the settings it makes, the client preferences.

    /// <summary>The body of a settings change, as received.</summary>
    public int SettingsRequestBytes { get; init; } = 524_288;

    /// <summary>The settings' nesting as <c>jsonDepth</c> counts it, stored and in a change; a client's preferences sit two levels down.</summary>
    public int SettingsDepth { get; init; } = 64;

    /// <summary>One client's preferences, on their canonical form.</summary>
    public int SettingsClientBytes { get; init; } = 65_536;

    /// <summary>The clients whose preferences the settings keep.</summary>
    public int SettingsClients { get; init; } = 16;

    /// <summary>The settings as stored; a change that would take them over is refused, and a larger file isn't read.</summary>
    public int SettingsBytes { get; init; } = 1_048_576;

    // Per media asset, from its header (MediaProbe), before decoding.

    /// <summary>A picture's bytes; a larger one is stored (within <see cref="VideoBytes"/>) and refused as media.</summary>
    public int MediaBytes { get; init; } = 33_554_432;

    /// <summary>A video file's bytes, and any stored blob's: a larger blob isn't stored.</summary>
    public int VideoBytes { get; init; } = 268_435_456;
    public int SvgBytes { get; init; } = 1_048_576;
    public int MediaSide { get; init; } = 8_192;
    public long MediaArea { get; init; } = 16_777_216;
    public int MediaFrames { get; init; } = 1_000;
    public double MediaDuration { get; init; } = 600;
    public long MediaFrameArea { get; init; } = 268_435_456;
    public int SvgElements { get; init; } = 10_000;

    /// <summary>Largest magnitude of a number in an SVG; a value beyond it refuses the file (it isn't clamped).</summary>
    public double SvgNumber { get; init; } = 1_000_000_000;

    /// <summary>Deepest element nesting in an SVG file, the root at 1.</summary>
    public int SvgDepth { get; init; } = 32;

    /// <summary>Deepest drawn nesting in an SVG: elements inside elements plus the <c>use</c>, <c>clip-path</c> and <c>mask</c> references they draw through, the root at 1.</summary>
    public int SvgDrawnDepth { get; init; } = 64;

    /// <summary>Most <c>href</c> references an SVG gradient inherits through.</summary>
    public int SvgGradientChain { get; init; } = 4;

    // Per archive (a profile archive, or a 1.x export), and per import request.

    /// <summary>The archive's file (<c>archive.expanded</c> over it).</summary>
    public long ArchiveBytes { get; init; } = 536_870_912;

    /// <summary>Entries in an archive (<c>archive.entries</c>).</summary>
    public int ArchiveEntries { get; init; } = 10_000;

    /// <summary>Bytes one request decompresses or reads, counted as they are read; a 1.x folder's assets too (<c>archive.expanded</c>).</summary>
    public long ExpandedBytes { get; init; } = 536_870_912;

    /// <summary>An entry's name, in UTF-8 bytes (<c>archive.name</c>).</summary>
    public int EntryNameBytes { get; init; } = 1_024;

    /// <summary>An entry's central directory record: 46 fixed bytes, its name, extra field and comment (<c>archive.entries</c>).</summary>
    public int EntryRecordBytes { get; init; } = 4_096;

    /// <summary>A profile archive's <c>manifest.json</c>, as stored (<c>limit.manifestBytes</c>, unread over it).</summary>
    public int ManifestBytes { get; init; } = 65_536;

    // Per 1.x import: what the engine reads of 1.x files, and what an import answer lists.

    /// <summary>A 1.x <c>Profile.xml</c>, <c>profiles.xml</c> or <c>settings.xml</c>.</summary>
    public int V1ProfileXmlBytes { get; init; } = 4_194_304;

    /// <summary>A 1.x items file (<c>DisplayItems.xml</c>, <c>profiles/{guid}.xml</c>).</summary>
    public int V1ItemsXmlBytes { get; init; } = 16_777_216;

    /// <summary>Element nesting in any 1.x XML file, the root element at 1.</summary>
    public int V1XmlDepth { get; init; } = 64;

    /// <summary>Elements in any 1.x XML file.</summary>
    public int V1XmlElements { get; init; } = 500_000;

    /// <summary>Profiles read from a 1.x <c>profiles.xml</c>; the rest are left out with a warning.</summary>
    public int V1Profiles { get; init; } = 1_000;

    /// <summary>Devices read per device family of a 1.x <c>settings.xml</c>; the rest are left out with a warning.</summary>
    public int V1Devices { get; init; } = 64;

    /// <summary>Fields read per 1.x device; the rest are left out with a warning.</summary>
    public int V1DeviceFields { get; init; } = 64;

    /// <summary>A 1.x setting's value, in characters; a longer one is left out with a warning.</summary>
    public int V1SettingLength { get; init; } = 1_024;

    /// <summary>1.x hotkeys read; the rest are left out with a warning.</summary>
    public int V1Hotkeys { get; init; } = 256;

    /// <summary>Diagnostics one import answer lists; one last <c>more</c> diagnostic counts the rest.</summary>
    public int Diagnostics { get; init; } = 1_000;

    /// <summary>Profile outcomes in the 1.x migration record.</summary>
    public int RecordOutcomes { get; init; } = 1_000;

    /// <summary>The migration record, in bytes as stored.</summary>
    public int RecordBytes { get; init; } = 1_048_576;

    /// <summary>A profile outcome's reason in the record, in characters (the schema's <c>maxLength</c>: <c>schema</c>).</summary>
    public int RecordReasonLength { get; init; } = 256;

    // Per preview socket message.

    /// <summary>A client message on the preview socket, in UTF-8 bytes; a longer one closes the socket (1009).</summary>
    public int SocketMessageBytes { get; init; } = 4_096;

    // At run time (not validation): every string-producing operation (concat, upper, lower, format, formatTime, and
    // input text fields) cuts its result to this many Unicode scalar values, at the operation, before the result is
    // used anywhere, comparisons included (docs/expressions.md, "Numbers and strings").
    public int TextLength { get; init; } = 4_096;

    /// <summary>These limits with some replaced, by name (<c>{"nodes": 8}</c>).</summary>
    public SpecLimits With(IReadOnlyDictionary<string, double> overrides)
    {
        var node = JsonSerializer.SerializeToNode(this, SpecJson.Options)!.AsObject();
        foreach (var (name, value) in overrides)
        {
            if (!node.ContainsKey(name)) throw new KeyNotFoundException($"No limit named '{name}'.");
            node[name] = JsonValue.Create(value);
        }
        return node.Deserialize<SpecLimits>(SpecJson.Options)!;
    }

    /// <summary>Every limit by name.</summary>
    public IReadOnlyDictionary<string, double> ToDictionary()
    {
        var result = new SortedDictionary<string, double>(System.StringComparer.Ordinal);
        foreach (var (name, value) in JsonSerializer.SerializeToNode(this, SpecJson.Options)!.AsObject())
            result[name] = value!.GetValue<double>();
        return result;
    }

    /// <summary>The limits of one format, by name, as the session reports them under its format string.</summary>
    public IReadOnlyDictionary<string, double> ToDictionary(string format)
    {
        var result = new SortedDictionary<string, double>(System.StringComparer.Ordinal);
        foreach (var (name, value) in ToDictionary())
            if (FormatOf(name) == format) result[name] = value;
        return result;
    }

    /// <summary>
    /// The format a limit belongs to (spec/docs/limits.md): the scene, media and run-time limits are the scene format's;
    /// the archive limits the profile archive's; every other limit (nesting, documents, commits, request bodies, the profile
    /// list and the tokens, the engine settings, the socket's messages, the 1.x import and its record) is the document format's.
    /// </summary>
    public static string FormatOf(string name) =>
        SceneLimits.Contains(name) ? SpecSchemas.SceneFormat : ArchiveLimits.Contains(name) ? SpecSchemas.ArchiveFormat : SpecSchemas.DocumentFormat;

    private static readonly HashSet<string> ArchiveLimits = new(System.StringComparer.Ordinal)
    {
        "archiveBytes", "archiveEntries", "expandedBytes", "entryNameBytes", "entryRecordBytes", "manifestBytes",
    };

    private static readonly HashSet<string> SceneLimits = new(System.StringComparer.Ordinal)
    {
        // Scene limits.
        "sceneBytes", "nodes", "depth", "inputs", "values", "stateCells", "exprDepth", "exprWork", "pathCommands", "geometry",
        "textNodes", "seriesNodes", "tableNodes", "mediaNodes", "sequence", "effectCost", "composeDepth",
        // Media limits.
        "mediaBytes", "videoBytes", "svgBytes", "mediaSide", "mediaArea", "mediaFrames", "mediaDuration", "mediaFrameArea", "svgElements",
        "svgNumber", "svgDepth", "svgDrawnDepth", "svgGradientChain",
        // Run-time limits.
        "textLength",
    };
}
