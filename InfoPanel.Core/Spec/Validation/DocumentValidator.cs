using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InfoPanel.Spec;

/// <summary>An item a reader draws as a placeholder: <see cref="Problem"/> is unsupported, invalid or budget.</summary>
public sealed record ItemCheck(string Path, string Id, string Problem, IReadOnlyList<SpecError> Errors);

/// <summary>
/// The outcome of <see cref="DocumentValidator.Check"/>. A document whose <see cref="Status"/> is valid loads; its
/// <see cref="Items"/> lists the items drawn as placeholders (each scene is checked on its own). Unsupported: the
/// document needs a newer engine and isn't opened for editing. Invalid: it can't be loaded (<see cref="Errors"/>).
/// </summary>
public sealed record DocumentCheck(CheckStatus Status, Document? Document, IReadOnlyList<SpecError> Errors, IReadOnlyList<ItemCheck> Items);

/// <summary>The outcome of <see cref="CommitValidator.Check"/>: errors make it a 422; warnings go in the result.</summary>
public sealed record CommitCheck(CommitRequest? Commit, IReadOnlyList<SpecError> Errors, IReadOnlyList<CommitWarning> Warnings);

/// <summary>Checks a stored or imported document (spec/docs/validation.md).</summary>
public static class DocumentValidator
{
    public static DocumentCheck Check(JsonElement document, SpecLimits? limits = null, EngineSupport? support = null) =>
        Check(document, limits, support, null);

    /// <summary>
    /// <see cref="Check(JsonElement, SpecLimits?, EngineSupport?)"/> with each scene checked by <paramref name="checkScene"/>
    /// (null: <see cref="SceneValidator.Check"/>), so that a caller can keep or share the scene checks.
    /// </summary>
    internal static DocumentCheck Check(JsonElement document, SpecLimits? limits, EngineSupport? support, Func<JsonElement, SceneCheck>? checkScene)
    {
        limits ??= SpecLimits.Default;
        if (ValidationText.CheckJson(document, limits.JsonDepth) is { } json)
            return new DocumentCheck(CheckStatus.Invalid, null, ValidationText.Cap(json), []);
        return CheckRead(document, limits, support, checkScene);
    }

    /// <summary>
    /// <see cref="Check(JsonElement, SpecLimits?, EngineSupport?, Func{JsonElement, SceneCheck}?)"/> after the JSON-level
    /// pass, for a document that passed it as received, before anything of it was replaced (a create's or an import's
    /// document before its id is, a stored one before its folder's id is given it).
    /// </summary>
    internal static DocumentCheck CheckRead(JsonElement document, SpecLimits? limits, EngineSupport? support, Func<JsonElement, SceneCheck>? checkScene)
    {
        limits ??= SpecLimits.Default;
        support ??= EngineSupport.Base;

        if (ValidationText.CheckFormat(document, "document", support.DocumentCapabilities) is { } pre)
            return new DocumentCheck(pre.Status, null, [pre.Error], []);

        // Measured as it will be stored: without its revision metadata.
        var bytes = CanonicalJson.DocumentSize(document);
        if (bytes > limits.DocumentBytes)
            return Invalid([new SpecError("", "limit.documentBytes", $"The document is {bytes} bytes; the limit is {limits.DocumentBytes}.")]);

        // The item count before the schema, so that nothing below runs over more items than the limit allows.
        if (document.TryGetProperty("items", out var rawItems) && CountItems(rawItems, limits.Items) is var count && count > limits.Items)
            return Invalid([Over("/items", "items", count, limits.Items)]);

        var schema = SpecSchemas.StructureOnly.ValidateChecked(document, SpecSchemas.Document);
        if (!schema.IsValid) return Invalid(ValidationText.FromSchema(schema).ToList());

        Document parsed;
        try
        {
            parsed = SpecJson.Read<Document>(document);
        }
        catch (JsonException ex)
        {
            return Invalid([new SpecError("", "schema", $"The document passed the schema but couldn't be read: {ex.Message}")]);
        }

        var errors = new List<SpecError>();
        if (parsed.Profile.Editor is { } editor && ValidationText.CanonicalBytes(editor) is var editorBytes && editorBytes > limits.EditorBytes)
            errors.Add(Over("/profile/editor", "editorBytes", editorBytes, limits.EditorBytes));

        var tree = new ItemTreeRules(limits, support, new HashSet<string>(StringComparer.Ordinal), checkScene);
        tree.Walk(parsed.Items, "/items", 1);
        errors.AddRange(tree.Errors);
        if (tree.Count > limits.Items) errors.Add(Over("/items", "items", tree.Count, limits.Items));
        if (errors.Count > 0) return Invalid(errors);

        // Aggregate budgets: valid scenes are admitted in document order (pre-order); one that would take the document
        // over a budget is drawn as a placeholder instead, and later ones may still fit.
        var items = new List<ItemCheck>(tree.Problems);
        int nodes = 0, work = 0, text = 0, effects = 0;
        foreach (var (path, id, cost) in tree.Costs)
        {
            var over = nodes + cost.Nodes > limits.DocumentNodes ? Over(path, "documentNodes", nodes + cost.Nodes, limits.DocumentNodes)
                : work + cost.Work > limits.DocumentWork ? Over(path, "documentWork", work + cost.Work, limits.DocumentWork)
                : text + cost.TextNodes > limits.DocumentTextNodes ? Over(path, "documentTextNodes", text + cost.TextNodes, limits.DocumentTextNodes)
                : effects + cost.EffectCost > limits.DocumentEffectCost ? Over(path, "documentEffectCost", effects + cost.EffectCost, limits.DocumentEffectCost)
                : null;
            if (over is not null)
            {
                items.Add(new ItemCheck(path, id, "budget", [over]));
                continue;
            }
            nodes += cost.Nodes;
            work += cost.Work;
            text += cost.TextNodes;
            effects += cost.EffectCost;
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new DocumentCheck(CheckStatus.Valid, parsed, [], items);

        static DocumentCheck Invalid(IReadOnlyList<SpecError> errors) => new(CheckStatus.Invalid, null, ValidationText.Cap(errors), []);
    }

    /// <summary>
    /// The items in a raw JSON list of items, groups' children included, counted without recursion; stops counting once
    /// over <paramref name="max"/>. Members of the wrong shape are left to the schema.
    /// </summary>
    internal static long CountItems(JsonElement top, int max)
    {
        if (top.ValueKind != JsonValueKind.Array) return 0;
        long count = 0;
        var pending = new Stack<JsonElement>();
        pending.Push(top);
        while (pending.Count > 0 && count <= max)
            foreach (var item in pending.Pop().EnumerateArray())
            {
                count++;
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("children", out var children)
                    && children.ValueKind == JsonValueKind.Array)
                    pending.Push(children);
            }
        return count;
    }

    internal static SpecError Over(string path, string name, long actual, long limit) =>
        new(path, "limit." + name, $"{actual} is over the limit of {limit} ({name}).");

    /// <summary>The rules for a tree of items (a document's, or an inserted one): ids, nesting, sizes and each scene.</summary>
    internal sealed class ItemTreeRules(SpecLimits limits, EngineSupport support, HashSet<string> ids,
        Func<JsonElement, SceneCheck>? checkScene = null)
    {
        public List<SpecError> Errors { get; } = [];
        public List<ItemCheck> Problems { get; } = [];
        public List<(string Path, string Id, SceneCost Cost)> Costs { get; } = [];
        public int Count { get; private set; }

        public void Walk(IReadOnlyList<Item> items, string path, int depth)
        {
            for (var i = 0; i < items.Count; i++) WalkItem(items[i], $"{path}/{i}", depth);
        }

        public void WalkItem(Item item, string p, int depth)
        {
            Count++;
            if (!ids.Add(item.Id)) Errors.Add(new SpecError(p + "/id", "id.duplicate", $"Another item has the id '{item.Id}'."));
            if (depth > limits.ItemDepth) Errors.Add(Over(p, "itemDepth", depth, limits.ItemDepth));
            if (item.Origin is { } origin && ValidationText.CanonicalBytes(origin) is var originBytes && originBytes > limits.OriginBytes)
                Errors.Add(Over(p + "/origin", "originBytes", originBytes, limits.OriginBytes));

            switch (item)
            {
                case GroupItem group:
                    Walk(group.Children, p + "/children", depth + 1);
                    break;
                case SceneItem scene:
                    if ((scene.Bindings?.Count ?? 0) > limits.Bindings)
                        Errors.Add(Over(p + "/bindings", "bindings", scene.Bindings!.Count, limits.Bindings));
                    CheckSource(scene.Source, p + "/source", limits, Errors);
                    var check = checkScene?.Invoke(scene.Scene) ?? SceneValidator.CheckRead(scene.Scene, limits, support);
                    if (check.Status == CheckStatus.Valid) Costs.Add((p, item.Id, check.Cost));
                    else Problems.Add(new ItemCheck(p, item.Id, check.Status == CheckStatus.Unsupported ? "unsupported" : "invalid",
                        check.Errors.Select(e => e.Under(p + "/scene")).ToList()));
                    break;
            }
        }
    }

    internal static void CheckSource(ItemSource? source, string path, SpecLimits limits, List<SpecError> errors)
    {
        if (source is null) return;
        var bytes = CanonicalJson.Size(source);
        if (bytes > limits.SourceBytes) errors.Add(Over(path, "sourceBytes", bytes, limits.SourceBytes));
    }
}

/// <summary>
/// The checks of a commit body that need no document (spec/docs/commit.md): its size and schema, op count, and each
/// new scene and inserted item. Conflicts, missing items and the document's budgets are checked when it is applied.
/// </summary>
public static class CommitValidator
{
    public static CommitCheck Check(JsonElement commit, SpecLimits? limits = null, EngineSupport? support = null) =>
        Check(commit, limits, support, null);

    /// <summary>
    /// <see cref="Check(JsonElement, SpecLimits?, EngineSupport?)"/> with each new scene checked by <paramref name="checkScene"/>
    /// (null: <see cref="SceneValidator.Check"/>), so that the engine applying the commit keeps the scene checks.
    /// </summary>
    internal static CommitCheck Check(JsonElement commit, SpecLimits? limits, EngineSupport? support, Func<JsonElement, SceneCheck>? checkScene)
    {
        limits ??= SpecLimits.Default;
        support ??= EngineSupport.Base;

        // The request's size cap first, on the bytes received, then the JSON-level pass (spec/docs/validation.md).
        var bytes = ValidationText.ReceivedBytes(commit);
        if (bytes > limits.CommitBytes)
            return Fail(DocumentValidator.Over("", "commitBytes", bytes, limits.CommitBytes));
        if (ValidationText.CheckJson(commit, limits.JsonDepth) is { } json)
            return new CommitCheck(null, ValidationText.Cap(json), []);

        // The op count before the schema, so that no check below runs over more ops than the limit allows.
        if (commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("ops", out var rawOps)
            && rawOps.ValueKind == JsonValueKind.Array && rawOps.GetArrayLength() > limits.Ops)
            return Fail(DocumentValidator.Over("/ops", "ops", rawOps.GetArrayLength(), limits.Ops));
        if (commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("ops", out rawOps) && rawOps.ValueKind == JsonValueKind.Array)
        {
            // Inserted items, children included: no more than a document may hold.
            var index = 0;
            foreach (var op in rawOps.EnumerateArray())
            {
                if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    var count = 1 + (item.TryGetProperty("children", out var children) ? DocumentValidator.CountItems(children, limits.Items) : 0);
                    if (count > limits.Items) return Fail(DocumentValidator.Over($"/ops/{index}/item", "items", count, limits.Items));
                }
                index++;
            }
        }

        var schema = SpecSchemas.StructureOnly.ValidateChecked(commit, SpecSchemas.CommitRequest);
        if (!schema.IsValid) return new CommitCheck(null, ValidationText.FromSchema(schema).ToList(), []);

        CommitRequest parsed;
        try
        {
            parsed = SpecJson.Read<CommitRequest>(commit);
        }
        catch (JsonException ex)
        {
            return Fail(new SpecError("", "schema", $"The commit passed the schema but couldn't be read: {ex.Message}"));
        }

        var errors = new List<SpecError>();
        var warnings = new List<CommitWarning>();
        if (parsed.Ops.Count > limits.Ops) errors.Add(DocumentValidator.Over("/ops", "ops", parsed.Ops.Count, limits.Ops));

        for (var i = 0; i < parsed.Ops.Count; i++)
        {
            var p = $"/ops/{i}";
            switch (parsed.Ops[i])
            {
                case SetDesignOp design:
                    DocumentValidator.CheckSource(design.Source, p + "/source", limits, errors);
                    if (design.Origin is { } origin && ValidationText.CanonicalBytes(origin) is var originBytes && originBytes > limits.OriginBytes)
                        errors.Add(DocumentValidator.Over(p + "/origin", "originBytes", originBytes, limits.OriginBytes));
                    Scene(design.Scene, p + "/scene", i, design.Id);
                    break;
                case SetBindingsOp bindings when bindings.Bindings.Count > limits.Bindings:
                    errors.Add(DocumentValidator.Over(p + "/bindings", "bindings", bindings.Bindings.Count, limits.Bindings));
                    break;
                case InsertOp insert:
                {
                    var tree = new DocumentValidator.ItemTreeRules(limits, support, new HashSet<string>(StringComparer.Ordinal), checkScene);
                    tree.WalkItem(insert.Item, p + "/item", 1);
                    errors.AddRange(tree.Errors);
                    foreach (var problem in tree.Problems)
                    {
                        if (problem.Problem == "unsupported")
                            warnings.Add(new CommitWarning { Op = i, Id = problem.Id, Code = "unsupported", Message = problem.Errors[0].Message });
                        else errors.AddRange(problem.Errors);
                    }
                    break;
                }
                case ProfileOp profile when profile.Set.Editor is { } editor && ValidationText.CanonicalBytes(editor) is var editorBytes && editorBytes > limits.EditorBytes:
                    errors.Add(DocumentValidator.Over(p + "/set/editor", "editorBytes", editorBytes, limits.EditorBytes));
                    break;
            }
        }
        return new CommitCheck(errors.Count == 0 ? parsed : null, ValidationText.Cap(errors), warnings);

        void Scene(JsonElement scene, string path, int op, string id)
        {
            var check = checkScene?.Invoke(scene) ?? SceneValidator.CheckRead(scene, limits, support);
            if (check.Status == CheckStatus.Unsupported)
                warnings.Add(new CommitWarning { Op = op, Id = id, Code = "unsupported", Message = check.Errors[0].Message });
            else if (check.Status == CheckStatus.Invalid)
                errors.AddRange(check.Errors.Select(e => e.Under(path)));
        }

        static CommitCheck Fail(SpecError error) => new(null, [error], []);
    }
}
