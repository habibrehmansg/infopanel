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
/// with <c>item</c>, that item's scene and source too, each cut at 256 KiB.
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
        var doc = state.Document;
        var summary = new JsonObject
        {
            ["id"] = document.Id,
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

        if (arguments.String("item") is { } wanted)
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

            var detail = Summary(item);
            if (item is SceneItem scene)
            {
                detail["scene"] = Cut(scene.Scene.GetRawText(), "scene");
                if (scene.Source is { } source)
                    detail["source"] = Cut(JsonSerializer.Serialize(source, SpecJson.Options), "source");
            }
            summary["item"] = detail;
        }

        // The items within what is left of the reply's bytes as sent, the room for the note kept.
        long budget = MaxReplyBytes - Sent(summary.ToJsonString()) - 256;
        var listed = Fill(items, doc.Items, ref budget);
        var total = Flatten(doc.Items).Count();
        if (listed < total)
            summary["more"] = $"… and {total - listed} more items: the list stops at {MaxReplyBytes / (1024 * 1024)} MiB. Ask for one with item.";

        // Written without serializer options: a node's writer allows 1,000 levels, past the format's 256.
        return Task.FromResult(ToolResult.Text(summary.ToJsonString()));
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

    /// <summary>The JSON text as a JSON value when it is within the cap; else its first bytes as a string, said to be cut.</summary>
    private static JsonNode Cut(string json, string what)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= MaxPartBytes)
            return JsonNode.Parse(json, documentOptions: SpecJson.DocumentOptions)!; // any depth the format allows
        var cut = json[..Math.Min(json.Length, MaxPartBytes)];
        return $"(the {what} is {bytes} bytes; its first {MaxPartBytes} bytes follow, cut) {cut}";
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

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var document = ProfileNames.Find(workspace, arguments.String("profile")!);
        var thresholds = text.Checks();
        DocumentProfile profile;
        int w, h;
        byte[] png;
        PreviewCheckReport checks;
        try
        {
            // One generation and one tick for the picture, its size and the checks (Codex p2 #6).
            (profile, w, h, png, checks) = render.Inspect(document, (snapshot, generation) =>
            {
                var shown = generation.Document.Profile;
                var width = arguments.Integer("width") ?? Math.Clamp(shown.Width, Thumbnail.MinWidth, MaxWidth);
                var (pw, ph, _) = Thumbnail.Size(shown.Width, shown.Height, width);
                return (shown, pw, ph, Thumbnail.RenderPng(snapshot, width), PreviewChecks.Run(snapshot, generation.Document, thresholds));
            });
        }
        catch (ObjectDisposedException)
        {
            throw new ToolFailure("notFound", $"There's no profile {arguments.String("profile")} any more (notFound). list_profiles names them.");
        }

        return Task.FromResult(new ToolResult(
        [
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "text", ["text"] = $"{profile.Name}: a {w}×{h} picture of the {profile.Width}×{profile.Height} profile.\n\n{checks.Text}" },
        ], false, "ok"));
    }
}
