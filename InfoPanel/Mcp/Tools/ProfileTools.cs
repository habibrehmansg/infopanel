using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Text;
using InfoPanel.Output;
using InfoPanel.Security;
using InfoPanel.Spec;

namespace InfoPanel.Mcp.Tools;

/// <summary>Finds the profile a tool names: by id, else by exact name.</summary>
public static class ProfileNames
{
    /// <summary>The profile's open document.</summary>
    /// <exception cref="ToolFailure">
    /// <c>notFound</c>, <c>ambiguous</c> (several profiles have the name: their ids are listed), or the profile's status
    /// (<c>unreadable</c>, <c>unsupported</c>) when it can't be opened.
    /// </exception>
    public static ProfileDocument Find(Workspace workspace, string given)
    {
        var entries = workspace.List();
        var entry = entries.FirstOrDefault(e => e.Id == given);
        if (entry == null)
        {
            var named = entries.Where(e => e.Name == given).ToList();
            if (named.Count > 1)
                throw new ToolFailure("ambiguous", $"{named.Count} profiles are named {given} (ambiguous): {string.Join(", ", named.Select(e => e.Id))}. Name one by its id.");
            entry = named.FirstOrDefault();
        }
        if (entry == null)
            throw new ToolFailure("notFound", $"There's no profile {given} (notFound). list_profiles names them.");

        var found = workspace.Find(entry.Id);
        if (!found.Found)
            throw new ToolFailure("notFound", $"There's no profile {given} (notFound). list_profiles names them.");
        if (found.Problem is { } problem)
            throw new ToolFailure(problem.Code, $"{entry.Name ?? entry.Id} can't be opened: {problem.Code} ({problem.Problem.Message}).");
        return found.Document!;
    }
}

/// <summary><c>list_profiles</c>: every profile with its size, status and whether it has unsaved changes.</summary>
public sealed class ListProfilesTool(Workspace workspace) : McpTool
{
    public override string Name => "list_profiles";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "List profiles";
    public override JsonObject InputSchema => Schema();

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var profiles = new JsonArray();
        foreach (var entry in workspace.List())
        {
            var profile = new JsonObject { ["id"] = entry.Id, ["name"] = entry.Name, ["status"] = entry.Status };
            if (entry.Width is { } w && entry.Height is { } h)
            {
                profile["width"] = w;
                profile["height"] = h;
            }
            if (entry.Dirty is { } dirty)
                profile["unsaved"] = dirty;
            if (entry.Problem is { } problem)
                profile["problem"] = $"{problem.Code}: {problem.Message}";
            profiles.Add(profile);
        }

        return Task.FromResult(ToolResult.Text(profiles.Count == 0
            ? "There are no profiles."
            : $"{profiles.Count} profile{(profiles.Count == 1 ? "" : "s")}:\n{profiles.ToJsonString()}"));
    }
}

/// <summary>
/// <c>get_profile</c>: a profile's summary (name, size, revision and its items as a tree: position, box, visibility,
/// bindings, what made each), listed within <see cref="MaxReplyBytes"/> as sent and then cut with "… and N more items";
/// with <c>item</c>, that item's scene and source too, each cut at 256 KiB. For an AI app that takes less text
/// (<see cref="McpCall.MaxChars"/>), the list stops within that, and an item's parts are cut at a quarter of it.
/// </summary>
public sealed class GetProfileTool(Workspace workspace) : McpTool
{
    public const int MaxPartBytes = 256 * 1024;

    /// <summary>
    /// The most a reply's text takes as it is sent: JSON-escaped into the channel's line (and the MCP line, which escapes
    /// it the same way), where a name of 480 × é takes 7 bytes a letter. The items are listed until the next one would pass
    /// it, then "… and N more items"; an item's scene and source (each cut at <see cref="MaxPartBytes"/>) always fit.
    /// </summary>
    public const int MaxReplyBytes = 4 * 1024 * 1024;

    public override string Name => "get_profile";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "Read a profile";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("item", String("An item's id or exact name: its scene and source are added."), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var document = ProfileNames.Find(workspace, arguments.String("profile")!);
        var state = document.Read();
        var wanted = arguments.String("item");
        return Task.FromResult(ToolResult.Text(Describe(document.Id, state, wanted is null ? null : Resolve(state.Document, wanted), arguments.Call.MaxChars, sourcedWhole: true).ToJsonString()));
    }

    /// <summary>
    /// A <c>read</c> frame's answer (docs/2.0/relay.md, "Designs"), from one snapshot of the document: its tag, revision
    /// and id, the item as the document holds it, and with <paramref name="detail"/> this tool's text for the item, a
    /// sourced item's without its scene and source, cut to <paramref name="maxChars"/>.
    /// </summary>
    /// <exception cref="ToolFailure">No such profile or item (notFound), or a name more than one item has (ambiguous).</exception>
    internal JsonObject ReadItem(string profile, string wanted, bool detail, int? maxChars)
    {
        var document = ProfileNames.Find(workspace, profile);
        var state = document.Read();
        var item = Resolve(state.Document, wanted);
        var answer = new JsonObject
        {
            ["instance"] = document.Instance,
            ["rev"] = state.Generation.Rev,
            ["profile"] = document.Id,
            ["item"] = JsonSerializer.SerializeToNode(item, SpecJson.Options),
        };
        if (detail)
        {
            var summary = Describe(document.Id, state, item, maxChars, sourcedWhole: false);
            answer["detail"] = maxChars is { } chars ? Shrink(summary, chars) : summary.ToJsonString();
        }

        return answer;
    }

    /// <summary>The longest a text of the detail is kept at each round of <see cref="Shrink"/>, the ids apart.</summary>
    private static readonly int[] ShortenedTo = [64, 16];

    /// <summary>
    /// <paramref name="summary"/>'s text held to <paramref name="maxChars"/> characters by its structure, never cut as text,
    /// so it still parses: when it is longer, a <c>cut</c> member says so, and in turn the list of items is left out, then
    /// the item's scene and source (the read's item holds them whole), then every text but the ids is shortened (names,
    /// what made the item, each binding's key), then the item's bindings are left out, until it fits. The ids, the size
    /// and the revision stay whole: for any <c>maxChars</c> the relay sends (at least
    /// <see cref="InfoPanel.Service.RelayFrames.MinMaxChars"/>), the last round fits.
    /// </summary>
    internal static string Shrink(JsonObject summary, int maxChars)
    {
        var whole = summary.ToJsonString();
        if (whole.Length <= maxChars)
            return whole;

        summary["cut"] = $"The detail is {whole.Length} characters, more than this AI app takes ({maxChars}): the list of items, " +
            "the item's scene and source, then long texts and its bindings are left out or shortened. The read's item holds it whole.";
        var item = summary["item"] as JsonObject;
        List<Action> rounds =
        [
            () =>
            {
                summary.Remove("items");
                summary.Remove("more");
            },
            () =>
            {
                item?.Remove("scene");
                item?.Remove("source");
            },
            .. ShortenedTo.Select(length => (Action)(() => Shorten(summary, length))),
            () => item?.Remove("bindings"),
        ];
        var text = whole;
        foreach (var round in rounds)
        {
            round();
            text = summary.ToJsonString();
            if (text.Length <= maxChars)
                break;
        }

        return text;
    }

    /// <summary>Every text in <paramref name="node"/> but the ids and the <c>cut</c> note, longer than <paramref name="length"/>, cut to it with "…".</summary>
    private static void Shorten(JsonNode? node, int length)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (key is "id" or "cut")
                        continue;
                    if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (s.Length > length)
                            o[key] = s[..(char.IsHighSurrogate(s[length - 1]) ? length - 1 : length)] + "…";
                    }
                    else
                    {
                        Shorten(value, length);
                    }
                }
                break;
            case JsonArray a:
                foreach (var element in a)
                    Shorten(element, length);
                break;
        }
    }

    /// <summary>The item of <paramref name="doc"/> with the id <paramref name="wanted"/>, else the one item of that name.</summary>
    /// <exception cref="ToolFailure">None (notFound), or more than one of the name (ambiguous).</exception>
    private static Item Resolve(Document doc, string wanted)
    {
        var all = Flatten(doc.Items).ToList();
        var item = all.FirstOrDefault(i => i.Id == wanted);
        if (item == null)
        {
            var named = all.Where(i => i.Name == wanted).ToList();
            if (named.Count > 1)
                throw new ToolFailure("ambiguous", $"{named.Count} items of {doc.Profile.Name} are named {wanted} (ambiguous): {string.Join(", ", named.Select(i => i.Id))}. Name one by its id.");
            item = named.FirstOrDefault();
        }
        if (item == null)
            throw new ToolFailure("notFound", $"There's no item {wanted} in {doc.Profile.Name} (notFound). get_profile lists its items.");
        return item;
    }

    /// <summary>
    /// This tool's answer for the profile <paramref name="id"/> as <paramref name="state"/> holds it, with <paramref name="item"/>'s
    /// detail when given: its scene and source, but a sourced item's only when <paramref name="sourcedWhole"/>. Its text is
    /// written without serializer options: a node's writer allows 1,000 levels, past the format's 256.
    /// </summary>
    private static JsonObject Describe(string id, DocumentState state, Item? item, int? maxChars, bool sourcedWhole)
    {
        var doc = state.Document;
        var reply = maxChars is { } chars ? Math.Min(MaxReplyBytes, chars) : MaxReplyBytes;
        var part = maxChars is { } partChars ? Math.Min(MaxPartBytes, partChars / 4) : MaxPartBytes;
        var summary = new JsonObject
        {
            ["id"] = id,
            ["name"] = doc.Profile.Name,
            ["width"] = doc.Profile.Width,
            ["height"] = doc.Profile.Height,
            ["rev"] = state.Generation.Rev,
            ["unsaved"] = state.Dirty,
        };
        if (doc.Profile.Background is { } background)
            summary["background"] = background;
        var items = new JsonArray();
        summary["items"] = items;

        if (item is not null)
        {
            var detail = Summary(item);
            if (item is SceneItem scene && (sourcedWhole || scene.Source is null))
            {
                detail["scene"] = Cut(scene.Scene.GetRawText(), "scene", part);
                if (scene.Source is { } source)
                    detail["source"] = Cut(JsonSerializer.Serialize(source, SpecJson.Options), "source", part);
            }
            summary["item"] = detail;
        }

        // The items within what is left of the reply's bytes as sent, the room for the note kept.
        long budget = reply - Sent(summary.ToJsonString()) - 256;
        var listed = Fill(items, doc.Items, ref budget);
        var total = Flatten(doc.Items).Count();
        if (listed < total)
        {
            summary["more"] = reply < MaxReplyBytes
                ? $"… and {total - listed} more items: the list stops at {reply} characters, the most this AI app takes. Ask for one with item."
                : $"… and {total - listed} more items: the list stops at {MaxReplyBytes / (1024 * 1024)} MiB. Ask for one with item.";
        }

        return summary;
    }

    /// <summary>
    /// Adds the summaries of <paramref name="items"/> (a group's children inside it) while each fits in
    /// <paramref name="budget"/>, the bytes left as sent; the first that doesn't stops the list (budget −1). How many were added.
    /// </summary>
    private static int Fill(JsonArray target, IEnumerable<Item> items, ref long budget)
    {
        var listed = 0;
        foreach (var item in items)
        {
            var node = Summary(item);
            if (item is GroupItem)
                node["children"] = new JsonArray();
            var cost = Sent(node.ToJsonString()) + 1; // and its comma
            if (cost > budget)
            {
                budget = -1;
                return listed;
            }

            budget -= cost;
            target.Add(node);
            listed++;
            if (item is GroupItem group)
            {
                listed += Fill(node["children"]!.AsArray(), group.Children, ref budget);
                if (budget < 0)
                    return listed;
            }
        }
        return listed;
    }

    /// <summary>The bytes JSON text takes once escaped as a string, as the channel's line and the MCP line send it.</summary>
    private static long Sent(string json) => JsonSerializer.SerializeToUtf8Bytes(json).Length - 2;

    private static IEnumerable<Item> Flatten(IEnumerable<Item> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item is GroupItem group)
            {
                foreach (var child in Flatten(group.Children))
                    yield return child;
            }
        }
    }

    /// <summary>One item's summary, without its children (a group's are added by <see cref="Fill"/>).</summary>
    private static JsonObject Summary(Item item)
    {
        var summary = new JsonObject
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["kind"] = item is GroupItem ? "group" : "scene",
            ["x"] = item.Placement.X,
            ["y"] = item.Placement.Y,
        };
        if (item is SceneItem { Layout: { } layout })
        {
            if (layout.W is { } w)
                summary["w"] = w;
            if (layout.H is { } h)
                summary["h"] = h;
        }
        if (item.Placement.Rotation is { } rotation && rotation != 0)
            summary["rotation"] = rotation;
        summary["visible"] = item.Visible ?? true;
        if (item.Locked == true)
            summary["locked"] = true;

        switch (item)
        {
            case SceneItem scene:
                if (scene.Bindings is { Count: > 0 } bindings)
                    summary["bindings"] = new JsonObject(bindings.Select(b =>
                        KeyValuePair.Create(b.Key, (JsonNode?)(b.Value.Stat is { } stat ? $"{b.Value.Key} ({stat})" : b.Value.Key))));
                summary["madeWith"] = scene.Source switch
                {
                    { Format: { } format } => format,
                    { Ref: { } reference } => $"ref {reference}",
                    _ => "scene",
                };
                break;
        }
        return summary;
    }

    /// <summary>The JSON text as a JSON value when it is within <paramref name="cap"/>; else its first bytes as a string, said to be cut.</summary>
    private static JsonNode Cut(string json, string what, int cap)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= cap)
            return JsonNode.Parse(json, documentOptions: SpecJson.DocumentOptions)!; // any depth the format allows
        var cut = json[..Math.Min(json.Length, cap)];
        return $"(the {what} is {bytes} bytes; its first {cap} bytes follow, cut) {cut}";
    }
}

/// <summary>
/// <c>render_preview</c>: a profile drawn as a PNG, as it shows now (the live frame's tick when it is on show), and the
/// preview checks (<see cref="PreviewChecks"/>) on the same tick, with the text layers' thresholds read on every call.
/// </summary>
public sealed class RenderPreviewTool(Workspace workspace, RenderService render, McpText text) : McpTool
{
    public const int MaxWidth = 1200;

    public override string Name => "render_preview";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "Preview a profile";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("width", Integer($"The picture's width in pixels (the profile's own, at most {MaxWidth}, when absent).", Thumbnail.MinWidth, MaxWidth), false));

    /// <summary>How many times a picture over the AI app's <c>maxChars</c> is drawn again, narrower, before the answer is left to the limit.</summary>
    internal const int FitTries = 3;

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var document = ProfileNames.Find(workspace, arguments.String("profile")!);
        var thresholds = text.Checks();
        ToolResult result;
        try
        {
            // One generation and one tick for the picture, its size and the checks (Codex p2 #6), a narrower picture too.
            result = render.Inspect(document, (snapshot, generation) =>
            {
                var shown = generation.Document.Profile;
                var asked = arguments.Integer("width");
                var width = asked ?? Math.Clamp(shown.Width, Thumbnail.MinWidth, MaxWidth);
                var checks = PreviewChecks.Run(snapshot, generation.Document, thresholds).Text;
                var answer = Answer(Thumbnail.RenderPng(snapshot, width), width);

                // Over what the AI app takes: the same tick drawn narrower, its base64 scaled to the room the text leaves,
                // a few times at most, never below the least width. A picture still over it is refused by the limit. Each
                // try is narrower than the width the last was drawn, not asked: a tall profile's is less, its height capped,
                // and after the first try the width is scaled by the room itself, not its square root (a tall picture's data
                // does not go with its area).
                if (arguments.Call.MaxChars is { } maxChars)
                {
                    var drawn = Thumbnail.Size(snapshot.Width, snapshot.Height, width).Width;
                    for (var tries = 0; tries < FitTries && drawn > Thumbnail.MinWidth && Characters(answer) > maxChars; tries++)
                    {
                        var room = maxChars - Math.Max(McpToolHost.TextRoom, Text(answer).Length);
                        var size = Image(answer).Length;
                        var fit = Math.Max(0, room) / (double)size;
                        // The first try assumes the picture's data goes with its area; once one has been drawn that is
                        // known, not assumed: a tall picture's height is capped, so its data goes about with its width.
                        var narrower = Math.Clamp((int)(drawn * (tries == 0 ? Math.Sqrt(fit) : fit) * 0.9), Thumbnail.MinWidth, drawn - 1);
                        answer = Answer(Thumbnail.RenderPng(snapshot, narrower), narrower);
                        drawn = Thumbnail.Size(snapshot.Width, snapshot.Height, narrower).Width;
                    }
                }
                return answer;

                ToolResult Answer(byte[] png, int drawn)
                {
                    var (pw, ph, _) = Thumbnail.Size(shown.Width, shown.Height, drawn);
                    var smaller = drawn < width
                        ? $" (drawn smaller than {(asked is null ? $"{width} wide" : $"the {width} asked")}, to fit what this AI app takes)"
                        : "";
                    return new ToolResult(
                    [
                        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
                        new JsonObject { ["type"] = "text", ["text"] = $"{shown.Name}: a {pw}×{ph} picture{smaller} of the {shown.Width}×{shown.Height} profile.\n\n{checks}" },
                    ], false, "ok");
                }
            });
        }
        catch (ObjectDisposedException)
        {
            throw new ToolFailure("notFound", $"There's no profile {arguments.String("profile")} any more (notFound). list_profiles names them.");
        }

        return Task.FromResult(result);
    }

    private static string Image(ToolResult answer) => answer.Content[0]!["data"]!.GetValue<string>();

    private static string Text(ToolResult answer) => answer.Content[1]!["text"]!.GetValue<string>();

    /// <summary>What the answer costs the AI app in characters, as the limit counts it: the picture's data and the text.</summary>
    private static int Characters(ToolResult answer) => Image(answer).Length + Text(answer).Length;
}
