using System.Text.Json.Nodes;
using InfoPanel.Service;

namespace InfoPanel.Mcp.Tools;

/// <summary>Where a tool call came from.</summary>
public enum McpCallOrigin
{
    /// <summary>An AI app on this computer, over the local channel.</summary>
    ThisComputer,

    /// <summary>An AI app elsewhere, through the configured service's relay (docs/2.0/relay.md).</summary>
    Elsewhere,
}

/// <summary>
/// A tool call's context (docs/2.0/relay.md, "The engine's own rules"): where it came from, the most text its AI app takes
/// (<see cref="MaxChars"/>, null for no limit beyond the tools' own), and, for a call from elsewhere, its cutoff for
/// changes, which every place a tool writes asks under that write's lock, just before it writes (<see cref="BeforeWrite"/>).
/// </summary>
public sealed class McpCall
{
    private readonly RelayCutoff? _cutoff;

    private McpCall(McpCallOrigin origin, int? maxChars, RelayCutoff? cutoff, JsonObject? design, IReadOnlyList<JsonObject?>? designs,
        IReadOnlyList<string>? files, JsonObject? basedOn = null, IReadOnlyList<JsonObject?>? basedOnList = null)
    {
        Origin = origin;
        MaxChars = maxChars;
        _cutoff = cutoff;
        Design = design;
        Designs = designs;
        Files = files;
        BasedOn = basedOn;
        BasedOnList = basedOnList;
    }

    /// <summary>A call from an AI app on this computer: no cutoff, no limit beyond the tools' own, never a design.</summary>
    public static McpCall ThisComputer { get; } = new(McpCallOrigin.ThisComputer, null, null, null, null, null);

    /// <summary>A call from elsewhere, with its relay context: the only kind that brings a design or what a read found.</summary>
    public static McpCall Elsewhere(RelayCallContext context) => new(McpCallOrigin.Elsewhere, context.MaxChars, context.Cutoff,
        context.Design is { } design ? Copy(design) : null,
        context.Designs is { } designs ? [.. designs.Select(d => d is { } one ? Copy(one) : null)] : null,
        context.Files,
        context.BasedOn is { } basedOn ? Copy(basedOn) : null,
        context.BasedOnList is { } list ? [.. list.Select(b => b is { } one ? Copy(one) : null)] : null);

    private static JsonObject Copy(System.Text.Json.JsonElement design) =>
        JsonNode.Parse(design.GetRawText(), documentOptions: InfoPanel.Spec.SpecJson.DocumentOptions)!.AsObject();

    /// <summary>
    /// A design the service produced (<c>layout</c>, <c>source</c>, <c>scene</c>, <c>origin</c>; docs/2.0/relay.md, "Designs"):
    /// only on a call from elsewhere, only for <see cref="McpToolHost.DesignTools"/>. A copy per call; null without one.
    /// </summary>
    public JsonObject? Design { get; }

    /// <summary>
    /// <c>add_items</c>' designs, one per item in order, null for an item given as a scene: only on a call from elsewhere.
    /// A copy per call; null without them.
    /// </summary>
    public IReadOnlyList<JsonObject?>? Designs { get; }

    /// <summary>The texts the design needs stored, each under its SHA-256, or null.</summary>
    public IReadOnlyList<string>? Files { get; }

    /// <summary>
    /// What <c>update_item</c>'s change without a design was built on (<c>instance</c>, <c>rev</c>, <c>item</c>; docs/2.0/relay.md,
    /// "A change based on a read"): only on a call from elsewhere. A copy per call; null without one.
    /// </summary>
    public JsonObject? BasedOn { get; }

    /// <summary>
    /// <c>update_items</c>' <c>basedOn</c>, one per entry in order, null for an entry not based on a read: only on a call
    /// from elsewhere. A copy per call; null without it.
    /// </summary>
    public IReadOnlyList<JsonObject?>? BasedOnList { get; }

    /// <summary>Whether the call brings a design, designs or files.</summary>
    public bool BringsDesign => Design is not null || Designs is not null || Files is not null;

    /// <summary>Whether the call brings at least one design.</summary>
    public bool HasDesign => Design is not null || Designs?.Any(d => d is not null) == true;

    public McpCallOrigin Origin { get; }

    public bool FromElsewhere => Origin == McpCallOrigin.Elsewhere;

    /// <summary>The most characters of text the AI app takes, or null.</summary>
    public int? MaxChars { get; }

    /// <summary>
    /// Asked under the lock of every write a tool makes, just before it is made (a commit, a profile created, a blob
    /// stored): a call from elsewhere whose cutoff has tripped stops here, its change unmade. Under that lock, a cutoff
    /// that trips while the write waits for it is seen; one that trips once the write holds it comes after the change.
    /// </summary>
    /// <exception cref="ChangeCutOffException">The cutoff tripped: <c>cancel</c>, the connection closed, or the deadline passed.</exception>
    public void BeforeWrite()
    {
        if (_cutoff is { IsTripped: true })
            throw new ChangeCutOffException(_cutoff.Reason ?? "cancel");
    }
}

/// <summary>A call from elsewhere stopped before its write (<see cref="McpCall.BeforeWrite"/>): nothing was changed.</summary>
public sealed class ChangeCutOffException(string reason) : Exception($"The change was stopped before it was made ({reason}).")
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The rule for web images from elsewhere (docs/2.0/relay.md): until the engine can hold a provider's fetches for such a
/// change to public addresses, a change from elsewhere may not insert or set a scene that holds a media source with a
/// <c>provider</c>, nor change the bindings of an item whose scene holds one (which could re-address it). Moving,
/// hiding, renaming or removing such an item is fine.
/// </summary>
public static class WebImageRule
{
    public const string Code = "webImage";

    public const string Refusal = "An AI app can't add or change a picture from a web address (webImage): nothing was changed. " +
        "add_asset stores a picture to show instead; moving, hiding or removing such an item works.";

    /// <summary>
    /// Whether a commit op inserts or sets a scene that holds a provider's media source: an <c>insert</c>'s item (a scene
    /// item, or a group's items at any depth), or a <c>setDesign</c>'s scene.
    /// </summary>
    public static bool InsertsOrSetsProvider(JsonObject op) => Kind(op, "op") switch
    {
        "insert" => ItemHoldsProvider(op["item"]),
        "setDesign" => HoldsProvider(op["scene"]),
        _ => false,
    };

    private static bool ItemHoldsProvider(JsonNode? item) => item is JsonObject o && Kind(o, "kind") switch
    {
        "scene" => HoldsProvider(o["scene"]),
        "group" => o["children"] is JsonArray children && children.Any(ItemHoldsProvider),
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="scene"/> has a media node whose source is a provider's (<c>{"provider", "ref"}</c>), among its
    /// nodes or in its groups' children at any depth; nothing else in a scene names a provider (an input or a value
    /// called <c>provider</c> is not one).
    /// </summary>
    public static bool HoldsProvider(JsonNode? scene) => scene is JsonObject o && NodesHoldProvider(o["nodes"]);

    private static bool NodesHoldProvider(JsonNode? nodes) => nodes is JsonArray array && array.Any(n => n is JsonObject node && Kind(node, "kind") switch
    {
        "media" => node["source"] is JsonObject source && source.ContainsKey("provider"),
        "group" => NodesHoldProvider(node["children"]),
        _ => false,
    });

    private static string? Kind(JsonObject o, string member) => o[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Whether a stored scene holds a provider's media source.</summary>
    public static bool HoldsProvider(System.Text.Json.JsonElement scene) =>
        HoldsProvider(JsonNode.Parse(scene.GetRawText(), documentOptions: InfoPanel.Spec.SpecJson.DocumentOptions));
}
