using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InfoPanel.Api;
using InfoPanel.Documents;
using InfoPanel.Security;
using InfoPanel.Service;
using InfoPanel.Spec;

namespace InfoPanel.Mcp.Tools;

/// <summary>Finds an item a tool names: by id, else by exact name.</summary>
public static class ItemNames
{
    /// <exception cref="ToolFailure"><c>notFound</c>, or <c>ambiguous</c> naming the ids.</exception>
    public static Item Find(Document document, string wanted)
    {
        var all = Flatten(document.Items).ToList();
        var item = all.FirstOrDefault(i => i.Id == wanted);
        if (item != null)
            return item;
        var named = all.Where(i => i.Name == wanted).ToList();
        if (named.Count > 1)
            throw new ToolFailure("ambiguous", $"{named.Count} items of {document.Profile.Name} are named {wanted} (ambiguous): {string.Join(", ", named.Select(i => i.Id))}. Name one by its id.");
        return named.FirstOrDefault()
            ?? throw new ToolFailure("notFound", $"There's no item {wanted} in {document.Profile.Name} (notFound). get_profile lists its items.");
    }

    public static IEnumerable<Item> Flatten(IEnumerable<Item> items)
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

    /// <summary>How an answer names an item: its name and id, or its id.</summary>
    public static string Label(Item item) => item.Name is { Length: > 0 } name ? $"\"{name}\" ({item.Id})" : item.Id;
}

/// <summary>
/// An item a tool names, found once: by id or exact name on the first attempt, then by the id it had (Codex p2 #1), so
/// a retry after a conflict acts on the item the tool found, never on another one given its name in between.
/// </summary>
public sealed class PinnedItem(string wanted)
{
    private string? _id;
    private string? _label;

    /// <exception cref="ToolFailure">As <see cref="ItemNames.Find"/>; on a retry, <c>notFound</c> when the item is gone.</exception>
    public Item Find(Document document)
    {
        if (_id == null)
        {
            var item = ItemNames.Find(document, wanted);
            (_id, _label) = (item.Id, ItemNames.Label(item));
            return item;
        }
        return ItemNames.Flatten(document.Items).FirstOrDefault(i => i.Id == _id)
            ?? throw new ToolFailure("notFound", $"{_label} was removed from {document.Profile.Name} while I worked (notFound): nothing was changed. get_profile lists its items.");
    }
}

/// <summary>
/// A profile a tool names, found once (<see cref="ProfileNames.Find"/>) and carried to the commit, never looked up by its
/// name again: the document object (so its instance: a profile deleted and created again under its id is another
/// document, and a commit names its document's instance), and, for a change that must not land in a profile renamed
/// away meanwhile (<see cref="Find"/>'s <c>holdName</c>) when it was found by its name, the revision of that name, which
/// every rename stamps anew, a rename back included (<see cref="ProfileDocument.ProfileName"/>). <see cref="IsStill"/>
/// checks both; every check before a write and every commit attempt asks it under the document's lock
/// (<see cref="ProfileChanges.Apply(string, FoundProfile, McpCall, Func{DocumentState, JsonArray}, Action?, Func{ConflictProblem, bool}?)"/>).
/// </summary>
public sealed class FoundProfile
{
    private FoundProfile(ProfileDocument document, string given, long? nameRev)
    {
        Document = document;
        Given = given;
        Instance = document.Instance;
        NameRev = nameRev;
    }

    public ProfileDocument Document { get; }

    /// <summary>The profile as the tool's arguments name it.</summary>
    public string Given { get; }

    /// <summary>The document's instance when it was found.</summary>
    public string Instance { get; }

    /// <summary>The revision of the name it was found by; null when renames don't matter (found by id, or not held by name).</summary>
    public long? NameRev { get; }

    /// <param name="holdName">Whether a rename of the profile, once found by its name, refuses the change.</param>
    /// <exception cref="ToolFailure">As <see cref="ProfileNames.Find"/>; <c>notFound</c> when it was renamed away as it was found.</exception>
    public static FoundProfile Find(Workspace workspace, string given, bool holdName)
    {
        var document = ProfileNames.Find(workspace, given);
        if (!holdName || document.Id == given)
            return new FoundProfile(document, given, null);
        // The name and its revision read together: a rename between the list and this read is seen here.
        var (name, rev) = document.ProfileName;
        if (name != given)
            throw ProfileChanges.Renamed(given);
        return new FoundProfile(document, given, rev);
    }

    /// <summary>
    /// Refuses unless the document is still the profile found: the same instance, and, when its name is held, no rename
    /// since. (A document deleted since is disposed, which a commit sees under its lock as it starts.)
    /// </summary>
    /// <exception cref="ToolFailure"><c>notFound</c>: replaced, or renamed.</exception>
    public void IsStill()
    {
        if (Document.Instance != Instance)
            throw ProfileChanges.Replaced(Given);
        if (NameRev is { } rev && Document.ProfileName.Rev != rev)
            throw ProfileChanges.Renamed(Given);
    }
}

/// <summary>A change a tool made: the document, the profile's name, the commit's result and what became of the save.</summary>
public sealed record ToolChange(ProfileDocument Document, string Name, CommitResult Result, bool Changed, string Saving);

/// <summary>
/// How a tool changes a profile (spec/docs/commit.md, through <see cref="ProfileDocument"/>, the commit endpoint's own
/// path, so every check and limit is the API's): one call is one commit and one undo step, built from the document as
/// read, every op carrying that revision as its <c>baseRev</c> and the commit the document's tag. A conflict (another
/// client changed what the ops write in between) reads again and rebuilds once, then is an error; the rebuild is on the
/// profile's document found the first time and on the items found then (<see cref="PinnedItem"/>), never on a profile or
/// item given their names in between, and a document deleted or replaced meanwhile is an error. Saving: the change is
/// saved, guarded by its own revision, only when the profile had no unsaved changes just before the commit applied
/// (decided under the commit's lock); otherwise it is left for the editor to save, and the answer says so.
/// </summary>
public sealed partial class ProfileChanges(Workspace workspace, Func<Action<string>?> hook)
{
    public const int MaxErrorsShown = 20;

    public Workspace Workspace => workspace;

    /// <summary>A test seam: the host's step hook (<see cref="McpToolHost.StepHook"/>).</summary>
    public void Step(string step) => hook()?.Invoke(step);

    /// <summary>
    /// Every write a tool makes passes here just before it is made (a commit, on every attempt; a profile created; a
    /// blob stored): the step hook's <c>write</c>, then its <c>locking</c> as the write goes to take its lock. The call's
    /// cutoff is not asked here but under that lock (<see cref="McpCall.BeforeWrite"/>, the write's guard), so a cutoff
    /// that trips while the write waits for its lock still stops it.
    /// </summary>
    public void BeforeWrite()
    {
        Step("write");
        Step("locking");
    }

    /// <summary>A change to the profile <paramref name="profile"/> names, found once (renames don't refuse it).</summary>
    /// <exception cref="ToolFailure">The profile isn't there or can't be opened, the ops can't be built, the engine refused them, or a conflict twice.</exception>
    /// <exception cref="ChangeCutOffException">A call from elsewhere whose cutoff tripped before a commit: nothing was changed.</exception>
    public ToolChange Apply(string tool, string profile, McpCall call, Func<DocumentState, JsonArray> build) =>
        Apply(tool, FoundProfile.Find(workspace, profile, holdName: false), call, build, null);

    /// <summary>
    /// A change to <paramref name="found"/>'s document, never to one found again by its name: every check before a write
    /// and every commit attempt asks, under the document's lock, that it is still the profile found
    /// (<see cref="FoundProfile.IsStill"/>), else the call is refused (<c>notFound</c>) with nothing changed.
    /// </summary>
    /// <exception cref="ToolFailure">The profile was replaced or renamed, the ops can't be built, the engine refused them, or a conflict twice.</exception>
    /// <exception cref="ChangeCutOffException">A call from elsewhere whose cutoff tripped before a commit: nothing was changed.</exception>
    /// <remarks>
    /// <paramref name="beforeWrites"/>, when given, runs once, before the first commit: only once the ops are built and
    /// the document would take them as it is now (<see cref="ProfileDocument.Check"/>, the commit's own checks) and, for a
    /// call from elsewhere, the web-image rule passes every op (<see cref="WebImageRule"/>): a conflict there reads again
    /// and rebuilds, as a commit's does, and any other refusal is the call's error, with nothing written. A design's files
    /// are stored there, so a refused insertion leaves none stored; the commit asks the rule again on what it finds.
    /// <paramref name="isStale"/>, when given, picks out a conflict on a design's own op (the precheck's or a commit's):
    /// that answers <see cref="Stale"/> at once, with nothing changed and no retry.
    /// </remarks>
    public ToolChange Apply(string tool, FoundProfile found, McpCall call, Func<DocumentState, JsonArray> build, Action? beforeWrites,
        Func<ConflictProblem, bool>? isStale = null)
    {
        var (document, profile) = (found.Document, found.Given);
        void Identity(Document _) => found.IsStill();
        var stored = beforeWrites is null;
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                Step("retry");
                if (!workspace.IsOpen(document))
                    throw Replaced(profile);
            }
            DocumentState state;
            CommitOutcome outcome;
            bool wasClean;
            try
            {
                state = document.Read();
                Step("read");
                var ops = build(state);
                var body = new JsonObject
                {
                    ["opId"] = Guid.NewGuid().ToString("N"),
                    ["instance"] = document.Instance,
                    ["baseRev"] = state.Generation.Rev,
                    ["ops"] = ops,
                };
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                // Until beforeWrites has run, every attempt is first checked as its commit would be, changing nothing
                // (the profile still the one found included): any refusal stands for the commit's, so a conflict reads
                // again and rebuilds and anything else is the call's error, before anything is stored.
                var refused = stored ? null : document.Check(bytes, Identity);
                if (refused is not null)
                {
                    outcome = refused;
                    wasClean = false;
                }
                else
                {
                    if (!stored)
                    {
                        // The rules for a call from elsewhere too, on every op (every item of a batch, given as a design or
                        // a scene), on the document as read: a change the commit's guard would refuse stores nothing first.
                        if (call.FromElsewhere)
                            RefuseWebImages(state.Document, ops);
                        beforeWrites!();
                        stored = true;
                    }

                    BeforeWrite();
                    // Under the commit's lock, on the document as the commit finds it, on every attempt: the profile still
                    // the one found, the cutoff, and for a call from elsewhere the web-image rule (a design changed since
                    // the read can't slip a binding change past it).
                    outcome = document.Commit(bytes, current =>
                    {
                        Identity(current);
                        call.BeforeWrite();
                        if (call.FromElsewhere)
                            RefuseWebImages(current, ops);
                    }, out wasClean);
                }
            }
            catch (ObjectDisposedException)
            {
                throw attempt == 0 ? Gone(profile) : Replaced(profile);
            }

            if (outcome.Conflict != null || outcome.Expired != null)
            {
                // A conflict on a design's own op: the engine can't rebuild a design it didn't produce, so it answers at
                // once that nothing changed (stale); the service may build it again on what the document now holds.
                if (outcome.Conflict is { } conflict && isStale?.Invoke(conflict) == true)
                    throw new ToolFailure(Stale, "The item's design changed since this change was prepared (stale): nothing was changed.");
                if (attempt == 0)
                    continue;
                throw new ToolFailure("conflict", "Someone changed this profile while I worked. Read it again with get_profile and retry.");
            }
            if (outcome.Invalid is { } invalid)
                throw Refused(tool, invalid.Errors);

            var result = outcome.Result!;
            var name = state.Document.Profile.Name;
            Step("committed");
            var changed = result.Rev != state.Generation.Rev;
            return new ToolChange(document, name, result, changed, changed ? Save(document, name, result.Rev, wasClean) : "");
        }
    }

    /// <summary>The outcome of a design whose base no longer holds inside the same document (docs/2.0/relay.md, "Designs").</summary>
    public const string Stale = "stale";

    /// <summary>A design built on another document or item: the call ends, nothing changed.</summary>
    public static ToolFailure BasedElsewhere() =>
        new("basedOn", "The profile was replaced or the item changed since this change was prepared; nothing was changed.");

    /// <summary>
    /// A change from elsewhere that would insert or set a scene holding a provider's media source, or change the bindings
    /// of an item whose scene holds one, is refused (<see cref="WebImageRule"/>); anything else passes.
    /// </summary>
    private static void RefuseWebImages(Document document, JsonArray ops)
    {
        foreach (var op in ops.OfType<JsonObject>())
        {
            var refused = op["op"]?.GetValue<string>() switch
            {
                "insert" or "setDesign" => WebImageRule.InsertsOrSetsProvider(op),
                "setBindings" => op["id"]?.GetValue<string>() is { } id
                    && ItemNames.Flatten(document.Items).FirstOrDefault(i => i.Id == id) is SceneItem scene && WebImageRule.HoldsProvider(scene.Scene),
                _ => false,
            };
            if (refused)
                throw new ToolFailure(WebImageRule.Code, WebImageRule.Refusal);
        }
    }

    /// <summary>The save after a change: only when the profile was clean as the commit applied, and only at its revision.</summary>
    private static string Save(ProfileDocument document, string name, long rev, bool wasClean)
    {
        if (!wasClean)
            return $"Not saved: {name} has unsaved changes from the editor; save there.";
        try
        {
            var saved = document.Save(new HistoryRequest { Instance = document.Instance, BaseRev = rev });
            return saved.Result != null
                ? "Saved."
                : $"Changed, but not saved: the editor changed {name} at the same moment; save there.";
        }
        catch (ObjectDisposedException)
        {
            return $"Changed, but not saved: {name} was deleted at the same moment.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change applied: never an error, which would invite a retry and a second copy (Codex p2 #2).
            return $"Changed, but not saved: saving failed ({ex.Message}). Save it in the editor.";
        }
    }

    private static ToolFailure Gone(string profile) =>
        new("notFound", $"There's no profile {profile} any more (notFound). list_profiles names them.");

    internal static ToolFailure Replaced(string profile) =>
        new("notFound", $"The profile {profile} was deleted or replaced while I worked (notFound): nothing was changed. list_profiles names the profiles now.");

    internal static ToolFailure Renamed(string profile) =>
        new("notFound", $"The profile {profile} was renamed while I worked (notFound): nothing was changed. list_profiles names the profiles now.");

    /// <summary>The engine's 422 as a tool's error: each problem's path (as the tool's arguments name it), code and message.</summary>
    public static ToolFailure Refused(string tool, IReadOnlyList<ValidationErrorBody> errors)
    {
        // add_items' op i inserts its item i: the path names that item.
        var shown = errors.Take(MaxErrorsShown).Select(e => $"{(tool == AddItemsTool.ToolName ? ItemsPath(e.Path) : ArgumentPath(e.Path))} {e.Code}: {e.Message}");
        var more = errors.Count > MaxErrorsShown ? $"; and {errors.Count - MaxErrorsShown} more" : "";
        return new ToolFailure("invalid",
            $"The engine refused the change: {errors.Count} problem{(errors.Count == 1 ? "" : "s")}: {string.Join("; ", shown)}{more}. Fix them and call {tool} again; get_design_guide has the format.");
    }

    /// <summary>A path in the request the tool built, as the caller's arguments name it: <c>/ops/0/item/scene/nodes/0/w</c> is <c>scene/nodes/0/w</c>.</summary>
    private static string ArgumentPath(string path)
    {
        var trimmed = RequestPrefix().Replace(path, "").TrimStart('/');
        return trimmed.Length == 0 ? "(the profile)" : trimmed;
    }

    [GeneratedRegex(@"^(/ops/\d+(/item|/set)?|/document(/profile)?)")]
    private static partial Regex RequestPrefix();

    /// <summary>A path in add_items' request as its arguments name it: <c>/ops/3/item/scene/nodes/0/w</c> is <c>items/3/scene/nodes/0/w</c>.</summary>
    private static string ItemsPath(string path)
    {
        var match = OpPrefix().Match(path);
        return match.Success ? $"items/{match.Groups[1].Value}{path[match.Length..]}" : ArgumentPath(path);
    }

    [GeneratedRegex(@"^/ops/(\d+)(/item)?")]
    private static partial Regex OpPrefix();

    /// <summary>A new item id, unused in <paramref name="document"/>.</summary>
    public static string NewItemId(Document document) => NewItemId(document, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>A new item id, unused in <paramref name="document"/> and not among <paramref name="taken"/>, which it joins.</summary>
    public static string NewItemId(Document document, ISet<string> taken)
    {
        var used = ItemNames.Flatten(document.Items).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var id = "ai-" + Guid.NewGuid().ToString("N")[..10];
            if (!used.Contains(id) && taken.Add(id))
                return id;
        }
    }

    /// <summary>Bindings as the tool takes them (input → a sensor key, or <c>{key, stat}</c>) as the document holds them.</summary>
    public static JsonObject Bindings(JsonObject given)
    {
        var bindings = new JsonObject();
        foreach (var (input, value) in given)
        {
            bindings[input] = value switch
            {
                JsonValue text when text.TryGetValue<string>(out var key) => new JsonObject { ["key"] = key },
                JsonObject binding => binding.DeepClone(),
                _ => throw new ToolFailure("arguments", $"The binding for {input} must be a sensor key (text, from list_sensors) or {{\"key\", \"stat\"}} (arguments)."),
            };
        }
        return bindings;
    }

    public static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, SpecJson.Options)!;

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>What the commit result warns about (content kept but not drawn), for the answer.</summary>
    public static string Warnings(CommitResult result) =>
        result.Warnings is { Count: > 0 } warnings
            ? " The engine keeps it but can't draw all of it: " + string.Join("; ", warnings.Take(MaxErrorsShown).Select(w => w.Message ?? w.Code)) + "."
            : "";
}

/// <summary>
/// <c>list_fonts</c>: the font families scenes can name (as <c>GET /api/fonts</c>), the ones InfoPanel bundles first and
/// marked (there on every computer), the rest only when they match <c>query</c> or none is given: a computer can have
/// hundreds, and each costs the AI context (triage p2 #10).
/// </summary>
public sealed class ListFontsTool : McpTool
{
    public const int MaxQueryCharacters = 200;

    public override string Name => "list_fonts";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "List fonts";
    public override JsonObject InputSchema => Schema(
        ("query", String("Only the families whose name holds this text, in any case (all of them when absent).", MaxQueryCharacters), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var bundled = InfoPanel.Drawing.FontRegistry.GetBundledFamilies();
        var all = ReadEndpoints.Fonts();
        var query = arguments.String("query") is { Length: > 0 } given ? given : null;
        var fonts = all.Where(f => bundled.Contains(f)).Concat(all.Where(f => !bundled.Contains(f)))
            .Where(f => query == null || f.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(f => bundled.Contains(f) ? $"{f} (bundled: on every computer)" : f)
            .ToList();
        var fallback = $"a family that isn't installed is drawn in {string.Join(", ", bundled.Order(StringComparer.OrdinalIgnoreCase))}";
        if (fonts.Count == 0)
            return Task.FromResult(ToolResult.Text($"No font family holds \"{query}\" ({all.Count} installed; {fallback}). Try a shorter query, or none."));
        var heading = query == null
            ? $"{fonts.Count} font families, the bundled ones first ({fallback}):"
            : $"{fonts.Count} of {all.Count} font families hold \"{query}\" ({fallback}):";
        return Task.FromResult(ToolResult.Text($"{heading}\n{string.Join("\n", fonts)}"));
    }
}

/// <summary><c>create_profile</c>: an empty profile (as <c>POST /api/profiles</c>), stored and saved at once.</summary>
public sealed class CreateProfileTool(ProfileChanges changes) : McpTool
{
    public override string Name => "create_profile";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Create a profile";
    public override JsonObject InputSchema => Schema(
        ("name", String("The profile's name."), true),
        ("width", Integer("The canvas width in pixels.", 1, 16384), true),
        ("height", Integer("The canvas height in pixels.", 1, 16384), true),
        ("background", String("The canvas colour, #rrggbb or #rrggbbaa (black when absent).", 9), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var profile = new JsonObject
        {
            ["name"] = arguments.String("name"),
            ["width"] = arguments.Integer("width"),
            ["height"] = arguments.Integer("height"),
        };
        if (arguments.String("background") is { } background)
            profile["background"] = background;
        var body = new JsonObject
        {
            ["id"] = id,
            ["document"] = new JsonObject { ["format"] = "infopanel.document/1", ["id"] = id, ["profile"] = profile, ["items"] = new JsonArray() },
        };

        changes.BeforeWrite();
        var outcome = changes.Workspace.Create(Encoding.UTF8.GetBytes(body.ToJsonString()), arguments.Call.BeforeWrite);
        if (outcome.Invalid is { } invalid)
            throw ProfileChanges.Refused(Name, invalid.Errors);
        if (outcome.Entry is not { } entry)
            throw new ToolFailure("exists", $"The profile couldn't be created ({outcome.Status}). Try again.");
        return Task.FromResult(ToolResult.Text(
            $"Created {entry.Name} (id {entry.Id}), {profile["width"]}×{profile["height"]}. Saved. Each item added next is a change of its own, saved as it is made: " +
            "a build that stops halfway leaves a saved, unfinished profile (delete it in the editor)."));
    }
}

/// <summary><c>add_item</c>: an item that draws the scene given, inserted as one commit.</summary>
public sealed class AddItemTool(ProfileChanges changes) : McpTool
{
    public const double MaxCoordinate = 100_000;

    public override string Name => "add_item";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Add an item";
    public override JsonObject InputSchema => Schema([("profile", String("The profile's id or exact name."), true), .. ItemMembers()]);

    /// <summary>An item's members as add_item takes them (and each of add_items' items): all but the profile.</summary>
    internal static (string Name, JsonObject Schema, bool Required)[] ItemMembers() =>
    [
        ("scene", Object("The item's scene (format infopanel.scene/1): get_design_guide topic scenes has the format."), true),
        ("x", Number("The item's left edge on the canvas, in pixels (0 when absent).", -MaxCoordinate, MaxCoordinate), false),
        ("y", Number("The item's top edge on the canvas, in pixels (0 when absent).", -MaxCoordinate, MaxCoordinate), false),
        ("width", Number("The item's layout box width, for a scene that reads its box.", 0, MaxCoordinate), false),
        ("height", Number("The item's layout box height, for a scene that reads its box.", 0, MaxCoordinate), false),
        ("name", String("The item's name, as the editor's layers show it."), false),
        ("bindings", Object("The scene's inputs bound to sensors: input name to a sensor key from list_sensors, or to {\"key\", \"stat\"}."), false),
        ("group", String("A group's id or exact name to add the item into (the top level when absent)."), false),
        ("index", Integer("Where among its siblings, 0 first (the end, drawn on top, when absent).", 0, 2000), false),
    ];

    /// <summary>An item's schema as add_items' list takes it: <see cref="ItemMembers"/>, closed.</summary>
    internal static JsonObject ItemSchema() => Schema(ItemMembers());

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        string? id = null;
        var group = arguments.String("group") is { } wantedGroup ? new PinnedItem(wantedGroup) : null;
        // A design from elsewhere (docs/2.0/relay.md, "Designs"): the profile found, then every file and dependency checked,
        // then the insertion built and checked against the profile, before anything is stored; then its files stored, each
        // behind the cutoff, and the item committed to the profile first found (refused if it was replaced or renamed away
        // meanwhile), behind the cutoff too.
        var design = arguments.Call.Design;
        // Found once, before anything else: the change goes to this document, a design's only while the name still names
        // it (a rename away and back included).
        var profile = FoundProfile.Find(changes.Workspace, arguments.String("profile")!, holdName: design is not null);
        IReadOnlyList<byte[]>? files = null;
        if (design is not null)
        {
            files = Designs.Preflight(changes.Workspace, [design], arguments.Call.Files);
            changes.Step("preflight");
        }

        var change = changes.Apply(Name, profile, arguments.Call, state =>
        {
            id = ProfileChanges.NewItemId(state.Document);
            return [Insert(arguments, design, state.Document, group, id)];
        }, files is null ? null : () => Designs.Store(changes, arguments.Call, files));

        var label = arguments.String("name") is { } n ? $"\"{n}\" ({id})" : id;
        return Task.FromResult(ToolResult.Text(
            $"Added {label} to {change.Name} at {ProfileChanges.Number(arguments.Double("x") ?? 0)}, {ProfileChanges.Number(arguments.Double("y") ?? 0)}.{ProfileChanges.Warnings(change.Result)} {change.Saving}"));
    }

    /// <summary>
    /// The <c>insert</c> op for one item as <paramref name="arguments"/> give it (<see cref="ItemMembers"/>), the new id
    /// <paramref name="id"/>: the design's box, scene, source and origin when it has <paramref name="design"/>, else the
    /// scene and box given; into <paramref name="group"/>'s group when given.
    /// </summary>
    /// <exception cref="ToolFailure">The group isn't there (<c>notFound</c>, <c>ambiguous</c>) or isn't a group (<c>notAGroup</c>).</exception>
    internal static JsonObject Insert(ToolArguments arguments, JsonObject? design, Document document, PinnedItem? group, string id)
    {
        var item = new JsonObject
        {
            ["kind"] = "scene",
            ["id"] = id,
            ["placement"] = new JsonObject { ["x"] = arguments.Double("x") ?? 0, ["y"] = arguments.Double("y") ?? 0 },
        };
        if (arguments.String("name") is { } name)
            item["name"] = name;
        if (design is not null)
        {
            // As the design gives them: its box, scene, source and origin.
            if (design["layout"] is { } layout)
                item["layout"] = layout.DeepClone();
        }
        else if (arguments.Has("width") || arguments.Has("height"))
        {
            var layout = new JsonObject();
            if (arguments.Double("width") is { } lw)
                layout["w"] = lw;
            if (arguments.Double("height") is { } lh)
                layout["h"] = lh;
            item["layout"] = layout;
        }
        if (arguments.Object("bindings") is { } bindings)
            item["bindings"] = ProfileChanges.Bindings(bindings);
        if (design is not null)
        {
            item["scene"] = design["scene"]!.DeepClone();
            item["source"] = design["source"]!.DeepClone();
            if (design["origin"] is { } origin)
                item["origin"] = origin.DeepClone();
        }
        else
        {
            item["scene"] = arguments.Object("scene");
        }

        var op = new JsonObject { ["op"] = "insert", ["item"] = item };
        if (group != null)
        {
            var parent = group.Find(document);
            if (parent is not GroupItem)
                throw new ToolFailure("notAGroup", $"{ItemNames.Label(parent)} isn't a group (notAGroup): an item goes into a group or the top level.");
            op["parentId"] = parent.Id;
        }
        if (arguments.Integer("index") is { } index)
            op["index"] = index;
        return op;
    }
}

/// <summary>
/// <c>add_items</c>: up to <see cref="MaxItems"/> items, each as <c>add_item</c> adds one, inserted in one commit and one
/// undo step, all or nothing. From elsewhere (docs/2.0/relay.md, "Designs"), the call's designs give a design per item in
/// order (null for an item given as a scene), all checked at once before anything is stored.
/// </summary>
public sealed class AddItemsTool(ProfileChanges changes) : McpTool
{
    public const string ToolName = "add_items";

    /// <summary>The most items one call adds.</summary>
    public const int MaxItems = 50;

    public override string Name => ToolName;
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Add items";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("items", List($"The items, in order, each as add_item takes one (without profile): 1 to {MaxItems}.", 1, MaxItems, AddItemTool.ItemSchema()), true));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var elements = arguments.List("items")!;
        var designs = arguments.Call.Designs;
        if (designs is not null && designs.Count != elements.Count)
            throw new ToolFailure("design", $"add_items brought {designs.Count} designs for {elements.Count} items (design): one per item, null for an item given as a scene. Nothing was stored or changed.");

        // Each item's arguments as add_item checks them; a design stands for its item's scene and box.
        var schema = AddItemTool.ItemSchema();
        var items = new List<ToolArguments>();
        var problems = new List<string>();
        for (var i = 0; i < elements.Count; i++)
        {
            try
            {
                items.Add(ToolArguments.Check($"item {i}", schema, elements[i], designs?[i] is null ? null : RelayFrames.DesignGives).For(arguments.Call));
            }
            catch (ToolFailure failure)
            {
                problems.Add(failure.Message);
            }
        }
        if (problems.Count > 0)
        {
            throw new ToolFailure("arguments",
                $"add_items can't take these items (arguments): {string.Join(" ", problems.Take(ProfileChanges.MaxErrorsShown))}{(problems.Count > ProfileChanges.MaxErrorsShown ? $" And {problems.Count - ProfileChanges.MaxErrorsShown} more." : "")} Nothing was changed.");
        }

        // Found once, before anything else, as add_item's: with a design among them, only while the name still names it.
        var brought = designs?.OfType<JsonObject>().ToList() ?? [];
        var profile = FoundProfile.Find(changes.Workspace, arguments.String("profile")!, holdName: brought.Count > 0);
        IReadOnlyList<byte[]>? files = null;
        if (brought.Count > 0)
        {
            files = Designs.Preflight(changes.Workspace, brought, arguments.Call.Files);
            changes.Step("preflight");
        }

        var groups = items.Select(a => a.String("group") is { } wanted ? new PinnedItem(wanted) : null).ToList();
        var ids = new string[items.Count];
        var change = changes.Apply(Name, profile, arguments.Call, state =>
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var ops = new JsonArray();
            for (var i = 0; i < items.Count; i++)
            {
                ids[i] = ProfileChanges.NewItemId(state.Document, taken);
                ops.Add(AddItemTool.Insert(items[i], designs?[i], state.Document, groups[i], ids[i]));
            }
            return ops;
        }, files is null ? null : () => Designs.Store(changes, arguments.Call, files));

        var labels = items.Select((a, i) => a.String("name") is { } n ? $"\"{n}\" ({ids[i]})" : ids[i]);
        return Task.FromResult(ToolResult.Text(
            $"Added {items.Count} items to {change.Name} in one change: {string.Join(", ", labels)}.{ProfileChanges.Warnings(change.Result)} {change.Saving}"));
    }
}

/// <summary><c>update_item</c>: any of an item's placement, design, bindings, name and visibility, as one commit.</summary>
public sealed class UpdateItemTool(ProfileChanges changes) : McpTool
{
    public override string Name => "update_item";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Change an item";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("item", String("The item's id or exact name."), true),
        ("scene", Object("A new scene for it (format infopanel.scene/1)."), false),
        ("x", Number("Its new left edge, in pixels.", -AddItemTool.MaxCoordinate, AddItemTool.MaxCoordinate), false),
        ("y", Number("Its new top edge, in pixels.", -AddItemTool.MaxCoordinate, AddItemTool.MaxCoordinate), false),
        ("rotation", Number("Its rotation in degrees, clockwise.", -360, 360), false),
        ("width", Number("Its layout box width.", 0, AddItemTool.MaxCoordinate), false),
        ("height", Number("Its layout box height.", 0, AddItemTool.MaxCoordinate), false),
        ("bindings", Object("Its sensor bindings, replaced whole: input name to a sensor key, or to {\"key\", \"stat\"}."), false),
        ("name", String("Its new name."), false),
        ("visible", Boolean("Shown (true) or hidden (false)."), false));

    private static readonly string[] Changes = ["scene", "x", "y", "rotation", "width", "height", "bindings", "name", "visible"];

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        // A design from elsewhere (docs/2.0/relay.md, "Designs") names what it was built on: the document's tag, its
        // revision and the item. Another tag or item ends the call before anything is stored; the design's op is based on
        // that revision, the call's other ops on the one read, all in one commit.
        var brought = arguments.Call.Design;
        var basedOn = brought?["basedOn"] as JsonObject;
        if (brought is not null && basedOn is null)
            throw new ToolFailure("design", "update_item's design needs basedOn {instance, rev, item}: what it was built on (design). Nothing was stored or changed.");
        if (brought is null && !Changes.Any(arguments.Has))
            throw new ToolFailure("arguments", $"update_item has nothing to change (arguments): give any of {string.Join(", ", Changes)}.");

        string? label = null;
        var pinned = new PinnedItem(arguments.String("item")!);
        // Found once, before anything else: the change goes to this document, a design's only while the name still names
        // it (a rename away and back included), as add_item's.
        var profile = FoundProfile.Find(changes.Workspace, arguments.String("profile")!, holdName: brought is not null);
        IReadOnlyList<byte[]>? files = null;
        if (basedOn is not null)
        {
            if (profile.Instance != basedOn["instance"]!.GetValue<string>()
                || pinned.Find(profile.Document.Read().Document).Id != basedOn["item"]!.GetValue<string>())
                throw ProfileChanges.BasedElsewhere();
            files = Designs.Preflight(changes.Workspace, [brought!], arguments.Call.Files);
            changes.Step("preflight");
        }

        var designOp = -1;
        var change = changes.Apply(Name, profile, arguments.Call, state =>
        {
            var item = pinned.Find(state.Document);
            label = ItemNames.Label(item);
            var rev = state.Generation.Rev;
            var ops = new JsonArray();
            if (basedOn is not null)
            {
                if (item.Id != basedOn["item"]!.GetValue<string>() || item is not SceneItem)
                    throw ProfileChanges.BasedElsewhere();
                var set = new JsonObject { ["op"] = "setDesign", ["id"] = item.Id, ["baseRev"] = basedOn["rev"]!.GetValue<long>() };
                if (brought!["layout"] is { } layout)
                    set["layout"] = layout.DeepClone();
                set["scene"] = brought["scene"]!.DeepClone();
                set["source"] = brought["source"]!.DeepClone();
                designOp = ops.Count;
                ops.Add(set);
            }

            if (arguments.Has("x") || arguments.Has("y") || arguments.Has("rotation"))
            {
                var placement = ProfileChanges.Node(item.Placement).AsObject();
                if (arguments.Double("x") is { } x)
                    placement["x"] = x;
                if (arguments.Double("y") is { } y)
                    placement["y"] = y;
                if (arguments.Double("rotation") is { } rotation)
                    placement["rotation"] = rotation;
                ops.Add(new JsonObject { ["op"] = "setPlacement", ["id"] = item.Id, ["baseRev"] = rev, ["placement"] = placement });
            }

            var design = arguments.Has("scene") || arguments.Has("width") || arguments.Has("height");
            if ((design || arguments.Has("bindings")) && item is not SceneItem)
                throw new ToolFailure("op.target", $"{label} is a group (op.target): only x, y, rotation, name and visible change a group.");
            if (design && item is SceneItem scene)
            {
                var op = new JsonObject { ["op"] = "setDesign", ["id"] = item.Id, ["baseRev"] = rev };
                var layout = scene.Layout is { } kept ? ProfileChanges.Node(kept).AsObject() : new JsonObject();
                if (arguments.Double("width") is { } w)
                    layout["w"] = w;
                if (arguments.Double("height") is { } h)
                    layout["h"] = h;
                if (layout.Count > 0)
                    op["layout"] = layout;
                if (arguments.Object("scene") is { } given)
                {
                    op["scene"] = given; // a new scene: what made the old one (its source) no longer describes it
                }
                else
                {
                    op["scene"] = JsonNode.Parse(scene.Scene.GetRawText(), documentOptions: SpecJson.DocumentOptions);
                    if (scene.Source is { } source)
                        op["source"] = ProfileChanges.Node(source);
                }
                ops.Add(op);
            }

            if (arguments.Object("bindings") is { } bindings)
                ops.Add(new JsonObject { ["op"] = "setBindings", ["id"] = item.Id, ["baseRev"] = rev, ["bindings"] = ProfileChanges.Bindings(bindings) });

            if (arguments.Has("name") || arguments.Has("visible"))
            {
                var set = new JsonObject();
                if (arguments.String("name") is { } name)
                    set["name"] = name;
                if (arguments.Boolean("visible") is { } visible)
                    set["visible"] = visible;
                ops.Add(new JsonObject { ["op"] = "setAttributes", ["id"] = item.Id, ["baseRev"] = rev, ["set"] = set });
            }
            return ops;
        }, files is null ? null : () => Designs.Store(changes, arguments.Call, files),
            conflict => designOp >= 0 && conflict.Conflicts.Any(c => c.Op == designOp));

        var what = change.Changed ? $"Changed {label} in {change.Name}.{ProfileChanges.Warnings(change.Result)} {change.Saving}" : $"{label} already was as asked: nothing changed.";
        return Task.FromResult(ToolResult.Text(what));
    }
}

/// <summary><c>remove_item</c>: an item and everything in it, as one commit (the editor's Undo brings it back).</summary>
public sealed class RemoveItemTool(ProfileChanges changes) : McpTool
{
    public override string Name => "remove_item";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Remove an item";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("item", String("The item's id or exact name (a group goes with everything in it)."), true));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        string? label = null;
        var pinned = new PinnedItem(arguments.String("item")!);
        var change = changes.Apply(Name, arguments.String("profile")!, arguments.Call, state =>
        {
            var item = pinned.Find(state.Document);
            label = ItemNames.Label(item);
            return [new JsonObject { ["op"] = "remove", ["id"] = item.Id, ["baseRev"] = state.Generation.Rev }];
        });
        return Task.FromResult(ToolResult.Text($"Removed {label} from {change.Name} (the editor's Undo brings it back). {change.Saving}"));
    }
}

/// <summary><c>add_asset</c>: an SVG stored as a blob (as <c>POST /api/blobs</c>), for a scene's <c>media</c> node.</summary>
public sealed class AddAssetTool(ProfileChanges changes) : McpTool
{
    public const int MaxSvgCharacters = 8 * 1024 * 1024;

    public override string Name => "add_asset";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Add an SVG picture";
    public override JsonObject InputSchema => Schema(
        ("svg", String("The SVG document's text: shapes, groups, gradients, clip paths and masks; no text, styles, images, scripts or links.", MaxSvgCharacters), true));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(arguments.String("svg")!);
        var workspace = changes.Workspace;
        // Checked before anything is stored: only an SVG the engine can show is kept.
        var check = MediaValidator.Check(bytes, workspace.Limits);
        if (check.Errors.Count > 0 || check.Info is not { } media)
        {
            var error = check.Errors.FirstOrDefault();
            throw new ToolFailure(error?.Code ?? "media.format",
                $"That isn't a picture InfoPanel can show: {error?.Code ?? "media.format"} ({error?.Message ?? "the bytes aren't a media asset"}). Nothing was stored. get_design_guide topic scenes says what an SVG may hold.");
        }
        if (media.Format != MediaFormat.Svg)
            throw new ToolFailure("media.format", "add_asset takes SVG only (media.format). Nothing was stored.");

        changes.BeforeWrite();
        var stored = workspace.PutBlob(bytes, arguments.Call.BeforeWrite).Info;
        return Task.FromResult(ToolResult.Text(
            $"Stored the SVG as {stored.Id} ({media.Width}×{media.Height}, {stored.Bytes} bytes). Show it with a media node in a scene: " +
            $"{{\"kind\":\"media\",\"x\":0,\"y\":0,\"w\":{media.Width},\"h\":{media.Height},\"source\":{{\"asset\":\"{stored.Id}\"}}}}"));
    }
}

/// <summary>
/// <c>show_on_desktop</c>: the profile's desktop window turned on or off (a <c>profile</c> commit of <c>window.enabled</c>).
/// The answer says whether a window opens, from what this engine can do (Codex p2 #8): <paramref name="noWindow"/> is why
/// none opens here (<c>--no-windows</c>, or a platform without desktop windows), null when windows open.
/// </summary>
public sealed class ShowOnDesktopTool(ProfileChanges changes, string? noWindow) : McpTool
{
    public const string WindowsOff = "this engine was started with --no-windows; it opens once the engine is started without it.";

    public const string Unsupported = "this engine can't show desktop windows on this computer yet; the setting is kept for when it can.";

    public override string Name => "show_on_desktop";
    public override ApiScope Scope => ApiScope.Edit;
    public override string Title => "Show on the desktop";
    public override JsonObject InputSchema => Schema(
        ("profile", String("The profile's id or exact name."), true),
        ("show", Boolean("true to show it in a window of its own on the desktop (the default), false to close that window."), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var show = arguments.Boolean("show") ?? true;
        var change = changes.Apply(Name, arguments.String("profile")!, arguments.Call, state =>
        {
            var window = state.Document.Profile.Window is { } kept ? ProfileChanges.Node(kept).AsObject() : new JsonObject();
            window["enabled"] = show;
            return [new JsonObject { ["op"] = "profile", ["baseRev"] = state.Generation.Rev, ["set"] = new JsonObject { ["window"] = window } }];
        });

        string where;
        if (!show)
            where = change.Changed ? $"{change.Name}'s desktop window is closed." : $"{change.Name} wasn't on the desktop.";
        else if (noWindow != null)
            where = $"{change.Name} {(change.Changed ? "is now" : "already was")} set to show on the desktop, but no window opens: {noWindow}";
        else
            where = change.Changed ? $"{change.Name} is on the desktop, in a window of its own." : $"{change.Name} already was on the desktop.";
        return Task.FromResult(ToolResult.Text(change.Changed ? $"{where} {change.Saving}" : where));
    }
}
