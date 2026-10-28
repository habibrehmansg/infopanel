using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Documents;

/// <summary>An item's field groups (spec/docs/commit.md): the parts whose last change a commit's <c>baseRev</c> is checked against.</summary>
[Flags]
public enum ItemField
{
    None = 0,
    Placement = 1,
    Design = 2,
    Bindings = 4,
    Name = 8,
    Visible = 16,
    Locked = 32,
    /// <summary>Its parent and index.</summary>
    Position = 64,
    /// <summary>A group's membership: an item inserted into it, moved into or out of it, or removed from it.</summary>
    Children = 128,
    All = 255,
}

/// <summary>The profile's fields, each its own field group.</summary>
[Flags]
public enum ProfileField
{
    None = 0,
    Name = 1,
    Width = 2,
    Height = 4,
    Background = 8,
    StatsOverlay = 16,
    Window = 32,
    TriggerApps = 64,
    Guides = 128,
    Editor = 256,
    Locale = 512,
    All = 1023,
}

/// <summary>
/// One document's field revisions: for every item, the revision at which each field group last changed, and for the
/// profile each field's. Kept outside the generations (an undo makes a new revision, so the revisions of the content it
/// goes back to are not the ones that content had). An item's <c>rev</c> is the latest of its field groups'. Not
/// thread-safe: the document uses it under its lock.
/// </summary>
public sealed class FieldRevisions
{
    private const int ItemSlots = 8, ProfileSlots = 10;

    private static readonly string[] ItemNames = ["placement", "design", "bindings", "name", "visible", "locked", "position", "children"];
    private static readonly string[] ProfileNames = ["name", "width", "height", "background", "statsOverlay", "window", "triggerApps", "guides", "editor", "locale"];

    private readonly Dictionary<string, long[]> _items = new(StringComparer.Ordinal);
    private readonly long[] _profile = new long[ProfileSlots];

    /// <summary>Every field of <paramref name="generation"/> at its revision.</summary>
    public FieldRevisions(Generation generation)
    {
        foreach (var id in generation.Content.Entries.Keys) _items[id] = Filled(ItemSlots, generation.Rev);
        Array.Fill(_profile, generation.Rev);
    }

    /// <summary>The revision at which the item last changed; 0 for an unknown id.</summary>
    public long ItemRev(string id) => _items.TryGetValue(id, out var revs) ? revs.Max() : 0;

    public long ProfileRev => _profile.Max();

    public long FieldRev(string id, ItemField field) => _items.TryGetValue(id, out var revs) ? revs[Slot((int)field)] : 0;

    public long FieldRev(ProfileField field) => _profile[Slot((int)field)];

    /// <summary>The field groups <paramref name="item"/> has: all of them for a group, all but <c>children</c> for a scene item.</summary>
    public static ItemField Tracked(Item item) => item is GroupItem ? ItemField.All : ItemField.All & ~ItemField.Children;

    /// <summary>The field groups among <paramref name="fields"/> that changed after <paramref name="baseRev"/>.</summary>
    public ItemField ChangedAfter(string id, ItemField fields, long baseRev)
    {
        if (!_items.TryGetValue(id, out var revs)) return ItemField.None;
        var changed = ItemField.None;
        for (var i = 0; i < ItemSlots; i++)
            if (((int)fields & (1 << i)) != 0 && revs[i] > baseRev) changed |= (ItemField)(1 << i);
        return changed;
    }

    public ProfileField ChangedAfter(ProfileField fields, long baseRev)
    {
        var changed = ProfileField.None;
        for (var i = 0; i < ProfileSlots; i++)
            if (((int)fields & (1 << i)) != 0 && _profile[i] > baseRev) changed |= (ProfileField)(1 << i);
        return changed;
    }

    /// <summary>Stamps every changed field group of <paramref name="changes"/> with <paramref name="rev"/>; drops removed items.</summary>
    internal void Apply(ChangeSet changes, long rev)
    {
        foreach (var id in changes.Removed) _items.Remove(id);
        foreach (var (id, fields) in changes.Items)
        {
            if (!_items.TryGetValue(id, out var revs) || changes.Added.Contains(id))
            {
                _items[id] = Filled(ItemSlots, rev);
                continue;
            }
            for (var i = 0; i < ItemSlots; i++)
                if (((int)fields & (1 << i)) != 0) revs[i] = rev;
        }
        for (var i = 0; i < ProfileSlots; i++)
            if (((int)changes.Profile & (1 << i)) != 0) _profile[i] = rev;
    }

    /// <summary>The names of the field groups, in the order the spec lists them (conflicts' <c>fields</c>).</summary>
    public static IReadOnlyList<string> Names(ItemField fields) => Names((int)fields, ItemNames);

    public static IReadOnlyList<string> Names(ProfileField fields) => Names((int)fields, ProfileNames);

    private static List<string> Names(int bits, string[] names)
    {
        var list = new List<string>();
        for (var i = 0; i < names.Length; i++)
            if ((bits & (1 << i)) != 0) list.Add(names[i]);
        return list;
    }

    private static int Slot(int flag) => System.Numerics.BitOperations.TrailingZeroCount(flag);

    private static long[] Filled(int n, long value)
    {
        var a = new long[n];
        Array.Fill(a, value);
        return a;
    }
}

/// <summary>
/// What changed between two contents, field group by field group: what a commit, undo, redo or revert stamps with its
/// new revision, and the bytes its history step is charged.
/// </summary>
internal sealed class ChangeSet
{
    /// <summary>Changed and added items with their changed field groups, in the order found.</summary>
    public Dictionary<string, ItemField> Items { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Added { get; } = new(StringComparer.Ordinal);

    public List<string> Removed { get; } = [];

    public ProfileField Profile { get; set; }

    /// <summary>The serialized size of the records on both sides of the change (what a history step keeps alive, at most).</summary>
    public long Bytes { get; set; }

    public bool IsEmpty => Items.Count == 0 && Removed.Count == 0 && Profile == ProfileField.None;

    /// <summary>
    /// The changes from <paramref name="from"/> to <paramref name="to"/>. With <paramref name="candidates"/> (a commit's
    /// ops: the field groups each op wrote), only those are compared and kept when their value differs; without, every
    /// item and profile field is (undo, redo, revert), skipping unchanged items by reference.
    /// </summary>
    public static ChangeSet Diff(GenerationContent from, GenerationContent to, Candidates? candidates = null)
    {
        var changes = new ChangeSet();
        if (ReferenceEquals(from, to)) return changes;
        var siblings = new SiblingIndex(from, to);

        void Item(string id, ItemField fields)
        {
            from.Entries.TryGetValue(id, out var a);
            to.Entries.TryGetValue(id, out var b);
            if (a is null && b is null) return;
            if (a is null)
            {
                changes.Items[id] = ItemField.All;
                changes.Added.Add(id);
                changes.Bytes += b!.Bytes;
                return;
            }
            if (b is null)
            {
                changes.Removed.Add(id);
                changes.Bytes += a.Bytes;
                return;
            }
            var changed = Compare(a, b, fields, siblings);
            if (changed == ItemField.None) return;
            changes.Items[id] = changed;
            changes.Bytes += a.OwnBytes + b.OwnBytes + (ReferenceEquals(a.Design, b.Design) ? 0 : a.Design.Bytes + b.Design.Bytes);
        }

        if (candidates is not null)
        {
            foreach (var (id, fields) in candidates.Items) Item(id, fields);
            changes.Profile = CompareProfile(from.Document.Profile, to.Document.Profile, candidates.Profile);
        }
        else
        {
            if (!ReferenceEquals(from.Entries, to.Entries))
            {
                foreach (var (id, b) in to.Entries)
                {
                    if (from.Entries.TryGetValue(id, out var a) && ReferenceEquals(a, b))
                    {
                        // The same record under the same parent: only its index among its siblings can differ.
                        if (siblings.Moved(id, a.ParentId, b.ParentId)) changes.Items[id] = ItemField.Position;
                        continue;
                    }
                    Item(id, ItemField.All);
                }
                foreach (var (id, a) in from.Entries)
                    if (!to.Entries.ContainsKey(id)) Item(id, ItemField.All);
            }
            changes.Profile = CompareProfile(from.Document.Profile, to.Document.Profile, ProfileField.All);
        }
        if (changes.Profile != ProfileField.None) changes.Bytes += from.ProfileBytes + to.ProfileBytes;
        return changes;
    }

    /// <summary>The field groups among <paramref name="fields"/> whose values differ.</summary>
    private static ItemField Compare(ItemEntry a, ItemEntry b, ItemField fields, SiblingIndex siblings)
    {
        var changed = ItemField.None;
        var x = a.Item;
        var y = b.Item;
        if (fields.HasFlag(ItemField.Placement) && !SamePlacement(x.Placement, y.Placement)) changed |= ItemField.Placement;
        if (fields.HasFlag(ItemField.Design) && !SameDesign(a, b)) changed |= ItemField.Design;
        if (fields.HasFlag(ItemField.Bindings) && !SameBindings((x as SceneItem)?.Bindings, (y as SceneItem)?.Bindings)) changed |= ItemField.Bindings;
        if (fields.HasFlag(ItemField.Name) && x.Name != y.Name) changed |= ItemField.Name;
        if (fields.HasFlag(ItemField.Visible) && x.Visible != y.Visible) changed |= ItemField.Visible;
        if (fields.HasFlag(ItemField.Locked) && x.Locked != y.Locked) changed |= ItemField.Locked;
        if (fields.HasFlag(ItemField.Position) && siblings.Moved(x.Id, a.ParentId, b.ParentId)) changed |= ItemField.Position;
        if (fields.HasFlag(ItemField.Children) && x is GroupItem g && y is GroupItem h && !SameMembers(g.Children, h.Children))
            changed |= ItemField.Children;
        return changed;
    }

    private static ProfileField CompareProfile(DocumentProfile a, DocumentProfile b, ProfileField fields)
    {
        if (ReferenceEquals(a, b) || fields == ProfileField.None) return ProfileField.None;
        var changed = ProfileField.None;
        if (fields.HasFlag(ProfileField.Name) && a.Name != b.Name) changed |= ProfileField.Name;
        if (fields.HasFlag(ProfileField.Width) && a.Width != b.Width) changed |= ProfileField.Width;
        if (fields.HasFlag(ProfileField.Height) && a.Height != b.Height) changed |= ProfileField.Height;
        if (fields.HasFlag(ProfileField.Background) && a.Background != b.Background) changed |= ProfileField.Background;
        if (fields.HasFlag(ProfileField.StatsOverlay) && a.StatsOverlay != b.StatsOverlay) changed |= ProfileField.StatsOverlay;
        if (fields.HasFlag(ProfileField.Window) && !Equals(a.Window, b.Window)) changed |= ProfileField.Window;
        if (fields.HasFlag(ProfileField.TriggerApps) && !SameList(a.TriggerApps, b.TriggerApps)) changed |= ProfileField.TriggerApps;
        if (fields.HasFlag(ProfileField.Guides) && !SameList(a.Guides, b.Guides)) changed |= ProfileField.Guides;
        if (fields.HasFlag(ProfileField.Editor) && !SameJson(a.Editor, b.Editor)) changed |= ProfileField.Editor;
        if (fields.HasFlag(ProfileField.Locale) && a.Locale != b.Locale) changed |= ProfileField.Locale;
        return changed;
    }

    /// <summary>
    /// Equal content, item by item (exact dirty state): the same profile, the same items under the same parents in
    /// the same order, and equal fields. Unchanged items are skipped by reference; scenes and the other JSON members
    /// compare as JSON (<see cref="Json.DeepEquals(JsonElement, JsonElement)"/>: member order and the spelling of numbers
    /// don't count, and numbers compare as the doubles nearest their text, spec/docs/commit.md).
    /// </summary>
    public static bool SameContent(GenerationContent a, GenerationContent b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Entries.Count != b.Entries.Count) return false;
        if (CompareProfile(a.Document.Profile, b.Document.Profile, ProfileField.All) != ProfileField.None) return false;
        if (!SameOrder(a.Document.Items, b.Document.Items)) return false;
        if (ReferenceEquals(a.Entries, b.Entries)) return true;
        foreach (var (id, x) in b.Entries)
        {
            if (!a.Entries.TryGetValue(id, out var y)) return false;
            if (ReferenceEquals(x, y)) continue;
            if (x.ParentId != y.ParentId) return false;
            var p = x.Item;
            var q = y.Item;
            if (p.GetType() != q.GetType() || !SamePlacement(p.Placement, q.Placement) || !SameDesign(x, y) || p.Name != q.Name
                || p.Visible != q.Visible || p.Locked != q.Locked || !SameBindings((p as SceneItem)?.Bindings, (q as SceneItem)?.Bindings))
                return false;
            if (p is GroupItem g && q is GroupItem h && !SameOrder(g.Children, h.Children)) return false;
        }
        return true;
    }

    internal static bool SamePlacement(Placement a, Placement b) =>
        ReferenceEquals(a, b) || (a.X.Equals(b.X) && a.Y.Equals(b.Y) && Nullable.Equals(a.Rotation, b.Rotation) && SameList(a.Pivot, b.Pivot));

    private static bool SameDesign(ItemEntry a, ItemEntry b)
    {
        if (ReferenceEquals(a.Design, b.Design)) return true;
        if (!SameJson(a.Item.Origin, b.Item.Origin)) return false;
        if (a.Item is not SceneItem x || b.Item is not SceneItem y) return a.Item.GetType() == b.Item.GetType();
        if (!Equals(x.Layout, y.Layout) || !SameSource(x.Source, y.Source)) return false;
        var s = a.Design.Scene!;
        var t = b.Design.Scene!;
        return ReferenceEquals(s, t) || Json.DeepEquals(s.Scene, t.Scene);
    }

    private static bool SameSource(ItemSource? a, ItemSource? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Format == b.Format && a.Ref == b.Ref && SameList(a.Deps, b.Deps)
            && a.Data.ValueKind == b.Data.ValueKind && (a.Data.ValueKind == JsonValueKind.Undefined || Json.DeepEquals(a.Data, b.Data));
    }

    private static bool SameBindings(IReadOnlyDictionary<string, Binding>? a, IReadOnlyDictionary<string, Binding>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        foreach (var (name, binding) in a)
            if (!b.TryGetValue(name, out var other) || !Equals(binding, other)) return false;
        return true;
    }

    private static bool SameJson(JsonElement? a, JsonElement? b) =>
        a is null ? b is null : b is not null && Json.DeepEquals(a.Value, b.Value);

    private static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));

    private static bool SameOrder(IReadOnlyList<Item> a, IReadOnlyList<Item> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id) return false;
        return true;
    }

    private static bool SameMembers(IReadOnlyList<Item> a, IReadOnlyList<Item> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        var ids = a.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return b.All(i => ids.Contains(i.Id));
    }

    /// <summary>
    /// Whether an item's position changed: another parent, or another index among the siblings present on both sides
    /// (an item inserted or removed next to it doesn't move it; a reorder does).
    /// </summary>
    private sealed class SiblingIndex(GenerationContent from, GenerationContent to)
    {
        private readonly Dictionary<string, (Dictionary<string, int> From, Dictionary<string, int> To)> _parents = new(StringComparer.Ordinal);

        public bool Moved(string id, string? fromParent, string? toParent)
        {
            if (fromParent != toParent) return true;
            var a = from.ChildList(fromParent);
            var b = to.ChildList(toParent);
            if (ReferenceEquals(a, b)) return false;
            var key = fromParent ?? "";
            if (!_parents.TryGetValue(key, out var index))
            {
                var inA = a.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                var inB = b.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                index = (Common(a, inB), Common(b, inA));
                _parents[key] = index;
            }
            return index.From.TryGetValue(id, out var i) != index.To.TryGetValue(id, out var j) || i != j;
        }

        private static Dictionary<string, int> Common(IReadOnlyList<Item> list, HashSet<string> other)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in list)
                if (other.Contains(item.Id)) map[item.Id] = map.Count;
            return map;
        }
    }
}

/// <summary>The field groups a commit's ops wrote, and the positions of the siblings in the lists it reordered: what <see cref="ChangeSet.Diff"/> compares for it.</summary>
internal sealed class Candidates
{
    public Dictionary<string, ItemField> Items { get; } = new(StringComparer.Ordinal);

    public ProfileField Profile { get; set; }

    public void Add(string? id, ItemField fields)
    {
        if (id is null) return;
        Items[id] = Items.GetValueOrDefault(id) | fields;
    }
}
