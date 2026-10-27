using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using InfoPanel.Scenes;
using InfoPanel.Spec;

namespace InfoPanel.Documents;

/// <summary>
/// One version of a document, immutable: its content (<see cref="Document"/>, with no <c>rev</c> members) and the
/// revision <see cref="Rev"/> it is. Every commit, undo, redo and revert makes a new generation. Items a change didn't
/// touch are the same records as in the generation before it, shared by reference, and so are their checked and
/// compiled scenes, so a generation costs only what changed. Undo, redo and revert make a new generation over an
/// earlier generation's content (<see cref="HasSameContent"/>). Engine code must never mutate a generation or anything
/// reachable from it (its records, their lists and JSON values): every reader and later generation shares them.
/// </summary>
public sealed class Generation
{
    internal Generation(long rev, GenerationContent content)
    {
        Rev = rev;
        Content = content;
    }

    public long Rev { get; }

    /// <summary>The document's content: no <c>rev</c> member on the document, its profile or any item.</summary>
    public Document Document => Content.Document;

    /// <summary>The limits and engine support the content was checked with.</summary>
    public SpecLimits Limits => Content.Limits;

    public EngineSupport Support => Content.Support;

    /// <summary>The items drawn as placeholders (unsupported, invalid or budget), in document order.</summary>
    public IReadOnlyList<ItemCheck> Problems => Content.Problems;

    internal GenerationContent Content { get; }

    /// <summary>True when both are the same content (the same records), whatever their revisions.</summary>
    public bool HasSameContent(Generation other) => ReferenceEquals(Content, other.Content);

    /// <summary>
    /// Checks a stored or imported document (<see cref="DocumentValidator"/>; each scene on its own, once per distinct
    /// scene) and makes it a generation at <paramref name="rev"/>. Its <c>rev</c> members are dropped. The check's items
    /// include a scene that passed validation but can't be compiled (drawn as invalid). No generation when the document
    /// is unsupported or invalid as a whole.
    /// </summary>
    public static GenerationLoad Load(JsonElement document, long rev = 1, SpecLimits? limits = null, EngineSupport? support = null) =>
        Load(document, rev, limits ?? SpecLimits.Default, support ?? EngineSupport.All, null);

    /// <summary>
    /// <see cref="Load(JsonElement, long, SpecLimits?, EngineSupport?)"/>, reusing <paramref name="known"/> scenes by their
    /// JSON text; a null <paramref name="rev"/> takes the document's own <c>rev</c> (0 without one).
    /// </summary>
    internal static GenerationLoad Load(JsonElement document, long? rev, SpecLimits limits, EngineSupport support, SceneChecker? known)
    {
        var scenes = new SceneChecker(limits, support, known);
        var check = DocumentValidator.Check(document, limits, support, scenes.Check);
        // Every scene was looked up by the check: the earlier load's scenes are no longer needed, and keeping them would
        // keep every load before it alive too.
        scenes.Forget();
        if (check.Status != CheckStatus.Valid || check.Document is not { } doc) return new GenerationLoad(check, null, scenes);

        var entries = ImmutableDictionary.CreateBuilder<string, ItemEntry>(StringComparer.Ordinal);
        Item Build(Item item, string? parent)
        {
            switch (item)
            {
                case GroupItem g:
                {
                    var children = new Item[g.Children.Count];
                    for (var i = 0; i < children.Length; i++) children[i] = Build(g.Children[i], g.Id);
                    var group = g with { Rev = null, Children = children };
                    entries[g.Id] = new ItemEntry(group, parent, DesignFacts.Of(group, null));
                    return group;
                }
                case SceneItem s:
                {
                    var scene = s with { Rev = null };
                    entries[s.Id] = new ItemEntry(scene, parent, DesignFacts.Of(scene, scenes.Facts(s.Scene)));
                    return scene;
                }
                default:
                    throw new InvalidOperationException($"Unknown item kind {item.GetType().Name}.");
            }
        }
        var items = new Item[doc.Items.Count];
        for (var i = 0; i < items.Length; i++) items[i] = Build(doc.Items[i], null);
        var content = GenerationContent.Create(doc with { Rev = null, Profile = doc.Profile with { Rev = null }, Items = items },
            entries.ToImmutable(), limits, support, null);

        // A scene the validator passed that the runtime can't compile is drawn as invalid: report it with the others.
        var late = content.Problems.Where(p => p.Problem == "invalid" && !check.Items.Any(i => i.Id == p.Id)).ToList();
        if (late.Count > 0) check = check with { Items = [.. check.Items, .. late] };
        return new GenerationLoad(check, new Generation(rev ?? doc.Rev ?? 0, content), scenes);
    }
}

/// <summary>The outcome of <see cref="Generation.Load(JsonElement, long, SpecLimits?, EngineSupport?)"/>: the check, and the generation when the document is valid.</summary>
public sealed record GenerationLoad(DocumentCheck Check, Generation? Generation)
{
    internal GenerationLoad(DocumentCheck check, Generation? generation, SceneChecker scenes) : this(check, generation) => Scenes = scenes;

    /// <summary>The distinct scenes checked, by JSON text, for a later load to reuse.</summary>
    internal SceneChecker? Scenes { get; }
}

/// <summary>
/// A generation's content: the document tree, an index of its items by id, their aggregate budget admission and its
/// size. Shared by every generation with this content.
/// </summary>
internal sealed class GenerationContent
{
    private IReadOnlyList<ItemCheck>? _problems;
    private long _profileBytes = -1;
    private int _jsonDepth = -1;

    private GenerationContent(Document document, ImmutableDictionary<string, ItemEntry> entries, SpecLimits limits, EngineSupport support,
        IReadOnlyDictionary<string, SpecError> overBudget, long itemBytes, Envelope envelope)
    {
        Document = document;
        Entries = entries;
        Limits = limits;
        Support = support;
        OverBudget = overBudget;
        ItemBytes = itemBytes;
        EnvelopeBytes = envelope.Bytes;
        EnvelopeDepth = envelope.Depth;
    }

    public Document Document { get; }

    /// <summary>Every item, groups' children included, by id.</summary>
    public ImmutableDictionary<string, ItemEntry> Entries { get; }

    public SpecLimits Limits { get; }

    public EngineSupport Support { get; }

    /// <summary>Valid scenes that would take the document over an aggregate budget, by id (spec/docs/limits.md): drawn as <c>budget</c> placeholders.</summary>
    public IReadOnlyDictionary<string, SpecError> OverBudget { get; }

    /// <summary>
    /// The document's size (<c>documentBytes</c>): exactly the bytes of <see cref="Document"/> as compact JSON
    /// (<see cref="SpecJson.Options"/>, no <c>rev</c> members), summed from each item's cached size and the separators
    /// between the top-level items.
    /// </summary>
    public long Bytes => EnvelopeBytes + ItemBytes + Math.Max(0, Document.Items.Count - 1);

    /// <summary>The sum of every item's <see cref="ItemEntry.Bytes"/>.</summary>
    public long ItemBytes { get; }

    /// <summary>The serialized size of the document with no items.</summary>
    public long EnvelopeBytes { get; }

    /// <summary>The JSON nesting depth of the document with no items (its profile's editor data included).</summary>
    public int EnvelopeDepth { get; }

    /// <summary>
    /// The document's JSON nesting depth (<c>jsonDepth</c>) as serialized: the deeper of its envelope and each item's own
    /// depth where it sits (the document is level 1, its items list 2, a top-level item 3, and each group above an item
    /// adds two levels, the item and its children list), from each item's cached depth, without recursion.
    /// </summary>
    public int JsonDepth => _jsonDepth >= 0 ? _jsonDepth : _jsonDepth = MeasureDepth();

    /// <summary>The serialized size of the profile.</summary>
    public long ProfileBytes => _profileBytes >= 0 ? _profileBytes : _profileBytes = SizeOf(Document.Profile);

    public IReadOnlyList<ItemCheck> Problems => _problems ??= FindProblems();

    /// <summary>
    /// A content with its budgets admitted (or <paramref name="overBudget"/>, when the caller knows nothing that
    /// affects them changed) and its size summed (or <paramref name="itemBytes"/> and <paramref name="envelope"/>).
    /// </summary>
    public static GenerationContent Create(Document document, ImmutableDictionary<string, ItemEntry> entries, SpecLimits limits,
        EngineSupport support, IReadOnlyDictionary<string, SpecError>? overBudget, long? itemBytes = null, Envelope? envelope = null)
    {
        overBudget ??= Admit(document, entries, limits);
        if (itemBytes is not { } sum)
        {
            sum = 0;
            foreach (var (_, entry) in entries) sum += entry.Bytes;
        }
        return new GenerationContent(document, entries, limits, support, overBudget, sum, envelope ?? EnvelopeOf(document));
    }

    /// <summary>The serialized size and depth of <paramref name="document"/> without its items.</summary>
    public static Envelope EnvelopeOf(Document document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { Items = [] }, SpecJson.Options);
        return new Envelope(bytes.LongLength, SpecJson.Depth(bytes));
    }

    /// <summary>What the document without its items serializes to: its size and its JSON nesting depth.</summary>
    public readonly record struct Envelope(long Bytes, int Depth);

    private int MeasureDepth()
    {
        var depth = EnvelopeDepth;
        foreach (var (_, entry) in Entries)
        {
            var above = 2;
            for (var p = entry.ParentId; p is not null; p = Entries[p].ParentId) above += 2;
            depth = Math.Max(depth, above + entry.Depth);
        }
        return depth;
    }

    /// <summary>The list of items a group (or the top level, for null) holds, in drawing order.</summary>
    public IReadOnlyList<Item> ChildList(string? parentId) =>
        parentId is null ? Document.Items : Entries.TryGetValue(parentId, out var e) && e.Item is GroupItem g ? g.Children : [];

    /// <summary>
    /// Aggregate budgets, admitted in document order (pre-order) as <see cref="DocumentValidator"/> does: a valid scene
    /// that would take the document over one is drawn as a placeholder, and later ones may still fit. Costs come from
    /// each scene's cached check.
    /// </summary>
    private static Dictionary<string, SpecError> Admit(Document document, ImmutableDictionary<string, ItemEntry> entries, SpecLimits limits)
    {
        var over = new Dictionary<string, SpecError>(StringComparer.Ordinal);
        int nodes = 0, work = 0, text = 0, effects = 0;
        void Visit(IReadOnlyList<Item> items, string path)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var p = $"{path}/{i}";
                switch (items[i])
                {
                    case GroupItem g:
                        Visit(g.Children, p + "/children");
                        break;
                    case SceneItem s when entries[s.Id].Design.Scene is { Check.Status: CheckStatus.Valid } facts:
                    {
                        var cost = facts.Check.Cost;
                        var error = nodes + cost.Nodes > limits.DocumentNodes ? DocumentValidator.Over(p, "documentNodes", nodes + cost.Nodes, limits.DocumentNodes)
                            : work + cost.Work > limits.DocumentWork ? DocumentValidator.Over(p, "documentWork", work + cost.Work, limits.DocumentWork)
                            : text + cost.TextNodes > limits.DocumentTextNodes ? DocumentValidator.Over(p, "documentTextNodes", text + cost.TextNodes, limits.DocumentTextNodes)
                            : effects + cost.EffectCost > limits.DocumentEffectCost ? DocumentValidator.Over(p, "documentEffectCost", effects + cost.EffectCost, limits.DocumentEffectCost)
                            : null;
                        if (error is not null)
                        {
                            over[s.Id] = error;
                            break;
                        }
                        nodes += cost.Nodes;
                        work += cost.Work;
                        text += cost.TextNodes;
                        effects += cost.EffectCost;
                        break;
                    }
                }
            }
        }
        Visit(document.Items, "/items");
        return over;
    }

    private List<ItemCheck> FindProblems()
    {
        var problems = new List<ItemCheck>();
        void Visit(IReadOnlyList<Item> items, string path)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var p = $"{path}/{i}";
                if (items[i] is GroupItem g) Visit(g.Children, p + "/children");
                else if (Entries[items[i].Id].Design.Scene is { } facts)
                {
                    if (facts.Problem is { } problem) problems.Add(new ItemCheck(p, items[i].Id, problem, facts.ErrorsAt(p + "/scene")));
                    else if (OverBudget.TryGetValue(items[i].Id, out var error)) problems.Add(new ItemCheck(p, items[i].Id, "budget", [error with { Path = p }]));
                }
            }
        }
        Visit(Document.Items, "/items");
        return problems;
    }

    internal static long SizeOf<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, SpecJson.Options).LongLength;
}

/// <summary>
/// One item of a generation: its record (a group's with its current children), its parent (null at the top level) and
/// its design's cached facts. Replaced whenever the record or the parent changes; shared otherwise.
/// </summary>
internal sealed class ItemEntry(Item item, string? parentId, DesignFacts design, long ownBytes = -1, int ownDepth = -1)
{
    // Measured once, lazily; both are deterministic, so a thread that sees either unset measures again and writes the same.
    private long _ownBytes = ownBytes;
    private int _ownDepth = ownDepth;

    public Item Item { get; } = item;

    public string? ParentId { get; } = parentId;

    public DesignFacts Design { get; } = design;

    /// <summary>The serialized size of the item without its design and children (placement, bindings, attributes).</summary>
    public long OwnBytes
    {
        get
        {
            Measure();
            return _ownBytes;
        }
    }

    /// <summary>The JSON nesting depth of the item without its design and children, the item itself being level 1.</summary>
    public int OwnDepth
    {
        get
        {
            Measure();
            return _ownDepth;
        }
    }

    /// <summary>The item's JSON nesting depth without its children's records, the item itself being level 1.</summary>
    public int Depth => Math.Max(OwnDepth, Design.Depth);

    private void Measure()
    {
        if (_ownBytes >= 0 && _ownDepth >= 0) return;
        var bytes = Item switch
        {
            SceneItem s => JsonSerializer.SerializeToUtf8Bytes<Item>(s with { Scene = DesignFacts.EmptyObject, Source = null, Origin = null, Layout = null }, SpecJson.Options),
            GroupItem g => JsonSerializer.SerializeToUtf8Bytes<Item>(g with { Children = [], Origin = null }, SpecJson.Options),
            _ => [],
        };
        _ownDepth = SpecJson.Depth(bytes);
        _ownBytes = bytes.LongLength;
    }

    /// <summary>
    /// The serialized size of the item without its children's records: for a group, its own members with the separators
    /// between its children. The document's size is the sum of these over every item plus its envelope.
    /// </summary>
    public long Bytes => OwnBytes + Design.Bytes + (Item is GroupItem { Children.Count: > 1 } g ? g.Children.Count - 1 : 0);

    public ItemEntry With(Item item) => ReferenceEquals(item, Item) ? new(item, ParentId, Design, _ownBytes, _ownDepth) : new(item, ParentId, Design);

    /// <summary>The same record under another parent.</summary>
    public ItemEntry Under(string? parentId) => new(Item, parentId, Design, _ownBytes, _ownDepth);

    /// <summary>A group with new children: its own size and depth are unchanged.</summary>
    public ItemEntry WithChildren(GroupItem group) => new(group, ParentId, Design, _ownBytes, _ownDepth);
}

/// <summary>
/// The facts about an item's design field group (a scene item's layout, source, scene and origin; a group's origin),
/// computed once when the design is set and shared by every later record of the item until it changes.
/// </summary>
internal sealed class DesignFacts
{
    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private DesignFacts(SceneFacts? scene, long bytes, int depth)
    {
        Scene = scene;
        Bytes = bytes;
        Depth = depth;
    }

    /// <summary>The scene's facts; null for a group.</summary>
    public SceneFacts? Scene { get; }

    /// <summary>
    /// The serialized size the design's members add to the item's <see cref="ItemEntry.OwnBytes"/> (which holds an empty
    /// scene object and no layout, source or origin), exactly.
    /// </summary>
    public long Bytes { get; }

    /// <summary>How deeply the design's members nest within the item, the item itself being level 1.</summary>
    public int Depth { get; }

    public static DesignFacts Of(Item item, SceneFacts? scene)
    {
        // Each member's text (separator, name and value) doesn't depend on the item's other members, so what the members
        // add is the item with them less the item without them.
        long bytes = item switch
        {
            SceneItem { Source: null, Origin: null, Layout: null } => 0,
            SceneItem s => GenerationContent.SizeOf<Item>(s with { Scene = EmptyObject })
                - GenerationContent.SizeOf<Item>(s with { Scene = EmptyObject, Source = null, Origin = null, Layout = null }),
            GroupItem { Origin: null } => 0,
            GroupItem g => GenerationContent.SizeOf<Item>(g with { Children = [] }) - GenerationContent.SizeOf<Item>(g with { Children = [], Origin = null }),
            _ => 0,
        };
        if (item is SceneItem) bytes += scene!.Bytes - EmptyObjectBytes;

        // Each member's value is one level below the item.
        var depth = 1;
        if (item.Origin is { } origin) depth = Math.Max(depth, 1 + SpecJson.Depth(origin));
        if (item is SceneItem si)
        {
            depth = Math.Max(depth, 1 + scene!.Depth);
            if (si.Layout is not null) depth = Math.Max(depth, 1 + SpecJson.Depth(JsonSerializer.SerializeToUtf8Bytes(si.Layout, SpecJson.Options)));
            if (si.Source is not null) depth = Math.Max(depth, 1 + SpecJson.Depth(JsonSerializer.SerializeToUtf8Bytes(si.Source, SpecJson.Options)));
        }
        return new DesignFacts(scene, bytes, depth);
    }

    private const int EmptyObjectBytes = 2;
}

/// <summary>
/// What the engine knows about one scene value: its check, cost and compiled form (with the limits it was checked
/// with) and its size. Computed once and shared by every item and generation holding it.
/// </summary>
internal sealed class SceneFacts
{
    private SceneFacts(JsonElement scene, SceneCheck check, CompiledScene? compiled, SpecError? compileError, long bytes)
    {
        Scene = scene;
        Check = check;
        Compiled = compiled;
        CompileError = compileError;
        Bytes = bytes;
        Depth = SpecJson.Depth(scene);
    }

    public JsonElement Scene { get; }

    public SceneCheck Check { get; }

    /// <summary>The compiled scene, when it is valid and compiles.</summary>
    public CompiledScene? Compiled { get; }

    /// <summary>Why a valid scene couldn't be compiled (it is drawn as invalid); the path is relative to the scene.</summary>
    public SpecError? CompileError { get; }

    /// <summary>The scene's serialized size as part of a document (compact, <see cref="SpecJson.Options"/>).</summary>
    public long Bytes { get; }

    /// <summary>The scene's JSON nesting depth (1 for an empty object).</summary>
    public int Depth { get; }

    /// <summary>unsupported or invalid (a placeholder), or null for a scene that draws.</summary>
    public string? Problem => Check.Status switch
    {
        CheckStatus.Unsupported => "unsupported",
        CheckStatus.Invalid => "invalid",
        _ => CompileError is null ? null : "invalid",
    };

    public IReadOnlyList<SpecError> ErrorsAt(string path) => CompileError is { } e ? [e.Under(path)] : Check.Errors.Select(e => e.Under(path)).ToList();

    public static SceneFacts Create(JsonElement scene, SceneCheck check, SpecLimits limits, long bytes)
    {
        CompiledScene? compiled = null;
        SpecError? error = null;
        if (check is { Status: CheckStatus.Valid, Scene: { } model })
        {
            try
            {
                compiled = CompiledScene.Compile(model, check.Cost, limits);
            }
            catch (SceneCompileException e)
            {
                error = new SpecError("", e.Code, e.Message);
            }
        }
        return new SceneFacts(scene, check, compiled, error, bytes);
    }
}

/// <summary>
/// Checks scenes for a document or a commit and keeps their facts by JSON text, so identical scenes are checked and
/// compiled once (and a later load can reuse an earlier one's).
/// </summary>
internal sealed class SceneChecker(SpecLimits limits, EngineSupport support, SceneChecker? known = null)
{
    private readonly Dictionary<string, SceneFacts> _facts = new(StringComparer.Ordinal);
    private SceneChecker? _known = known;

    /// <summary>A stored document's check: a valid scene that can't be compiled stays valid here and is drawn as invalid.</summary>
    public SceneCheck Check(JsonElement scene) => Facts(scene).Check;

    /// <summary>A commit's check: a scene that can't be compiled is invalid, with the reason at the scene's path.</summary>
    public SceneCheck CheckCompiles(JsonElement scene)
    {
        var facts = Facts(scene);
        return facts.CompileError is { } error ? facts.Check with { Status = CheckStatus.Invalid, Errors = [error] } : facts.Check;
    }

    public SceneFacts Facts(JsonElement scene)
    {
        var text = scene.GetRawText();
        if (_facts.TryGetValue(text, out var facts)) return facts;
        if (_known is { } known && known._facts.TryGetValue(text, out facts) && known.Matches(limits, support))
        {
            _facts[text] = facts;
            return facts;
        }
        facts = SceneFacts.Create(scene, SceneValidator.Check(scene, limits, support), limits, GenerationContent.SizeOf(scene));
        _facts[text] = facts;
        return facts;
    }

    /// <summary>Drops the earlier checker this one reused scenes from, so that a chain of loads keeps only the latest.</summary>
    public void Forget() => _known = null;

    /// <summary>Whether this checker checks with <paramref name="l"/> and <paramref name="s"/>.</summary>
    public bool Matches(SpecLimits l, EngineSupport s) => limits.Equals(l) && SameSupport(support, s);

    /// <summary>The same capabilities, whether or not the same instance.</summary>
    public static bool SameSupport(EngineSupport a, EngineSupport b) => ReferenceEquals(a, b)
        || (a.SceneCapabilities.SetEquals(b.SceneCapabilities) && a.DocumentCapabilities.SetEquals(b.DocumentCapabilities));
}
