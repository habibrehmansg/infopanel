using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InfoPanel.Editing;

/// <summary>A profile document as the API sends it (plan §6.2).</summary>
public sealed record ProfileDoc(
    string Id,
    long Revision,
    bool Dirty,
    bool CanUndo,
    bool CanRedo,
    JsonObject Profile,
    IReadOnlyList<ItemNode> Items);

/// <summary>A profile in the list (<c>GET /api/profiles</c>).</summary>
public sealed record ProfileSummary(Guid Id, string Name, int Width, int Height, long Revision, bool Dirty);

/// <summary>
/// The answer to an edit (plan §6.2): the document's state after it, plus <see cref="Items"/> (the full nodes of the
/// items the edit touched: patched, added or duplicated) or <see cref="Doc"/> (the whole document, for structural
/// edits that remove or reorder, undo, redo, revert and profile patches).
/// </summary>
public sealed record MutationResult(
    long Revision,
    bool Dirty,
    bool CanUndo,
    bool CanRedo,
    IReadOnlyList<ItemNode>? Items = null,
    ProfileDoc? Doc = null);

/// <summary>One item's property set in <see cref="ProfileDocument.PatchItems"/>.</summary>
public sealed record ItemPatch(Guid Id, JsonObject Set);

/// <summary>What caused a <see cref="ProfileDocument.Changed"/> event.</summary>
public enum DocumentChange
{
    /// <summary>Item properties were patched.</summary>
    Items,

    /// <summary>An item was added, removed, moved or duplicated.</summary>
    Structure,

    /// <summary>Profile properties were patched.</summary>
    Profile,

    Undo,
    Redo,
    Revert,

    /// <summary>The document was saved: the revision is unchanged and it is no longer dirty.</summary>
    Saved,
}

public sealed class DocumentChangedEventArgs(long revision, DocumentChange change) : EventArgs
{
    public long Revision { get; } = revision;

    public DocumentChange Change { get; } = change;
}

/// <summary>
/// One open profile: the engine's copy of the <see cref="Models.Profile"/> and its items, which rendering and the
/// API read and edits change. Everything that reads or changes the model holds <see cref="SyncRoot"/>.
/// <list type="bullet">
/// <item><see cref="Revision"/> starts at 1 and goes up by one on every real change (an edit whose result is
/// identical to the state before it is a no-op: same revision, no history entry, no event).</item>
/// <item>Undo, redo and dirty state come from XML snapshots (<see cref="DocumentSnapshot"/>). <see cref="Dirty"/>
/// compares the current snapshot with the last saved one, ignoring view state (<see cref="ViewStateProperties"/>);
/// it is computed lazily and cached until the next change or save.</item>
/// <item><see cref="Changed"/> is raised after <see cref="SyncRoot"/> is released, once per revision change and once
/// per save.</item>
/// </list>
/// </summary>
public sealed class ProfileDocument
{
    private static readonly ILogger Logger = Log.ForContext<ProfileDocument>();

    /// <summary>
    /// Properties that are editor view state, not content: patched with <c>record:false</c>, they never enter the
    /// undo history, never make the document dirty and are kept as they are across undo, redo and revert.
    /// </summary>
    public static IReadOnlySet<string> ViewStateProperties { get; } = new HashSet<string>(StringComparer.Ordinal) { "isExpanded" };

    /// <summary>The serialized view state (<c>GroupDisplayItem.IsExpanded</c>), stripped for the dirty comparison.</summary>
    private static readonly Regex ViewStateXml = new(@"^[ \t]*<IsExpanded>(?:true|false)</IsExpanded>\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public const int DuplicateOffset = 10;

    private readonly UndoHistory _history;
    private readonly SensorHub _sensors;
    private List<DisplayItem> _items;
    private DocumentSnapshot _snapshot;
    private DocumentSnapshot _saved;
    private long _revision = 1;
    private bool? _dirty;

    /// <param name="sensors">For the items' <c>meta.sensor</c> status; none when null.</param>
    /// <param name="time">The clock for undo coalescing.</param>
    public ProfileDocument(Profile profile, IEnumerable<DisplayItem> items, SensorHub? sensors = null, TimeProvider? time = null)
    {
        Id = profile.Guid;
        Profile = profile;
        _items = items.ToList();
        ItemTree.EnsureUniqueIds(_items);
        foreach (var item in _items)
            item.SetProfile(profile);

        _sensors = sensors ?? new SensorHub([]);
        _history = new UndoHistory(time ?? TimeProvider.System);
        _snapshot = TakeSnapshot();
        _saved = _snapshot;
    }

    /// <summary>Raised after every revision change and every save, outside <see cref="SyncRoot"/>.</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public object SyncRoot { get; } = new();

    public Guid Id { get; }

    /// <summary>The live profile. Undo, redo and revert replace the instance; read it under <see cref="SyncRoot"/>.</summary>
    public Profile Profile { get; private set; }

    /// <summary>The top-level items in draw order. Undo, redo and revert replace the list; read it under <see cref="SyncRoot"/>.</summary>
    public IReadOnlyList<DisplayItem> Items => _items;

    public long Revision
    {
        get { lock (SyncRoot) return _revision; }
    }

    public bool Dirty
    {
        get { lock (SyncRoot) return DirtyCore; }
    }

    public bool CanUndo
    {
        get { lock (SyncRoot) return _history.CanUndo; }
    }

    public bool CanRedo
    {
        get { lock (SyncRoot) return _history.CanRedo; }
    }

    /// <summary>The current state as XML (for tests and comparisons).</summary>
    public DocumentSnapshot Snapshot
    {
        get { lock (SyncRoot) return _snapshot; }
    }

    /// <summary>The state last saved (or loaded).</summary>
    public DocumentSnapshot SavedSnapshot
    {
        get { lock (SyncRoot) return _saved; }
    }

    private bool DirtyCore => _dirty ??=
        _snapshot.ProfileXml != _saved.ProfileXml
        || (_snapshot.ItemsXml != _saved.ItemsXml && WithoutViewState(_snapshot.ItemsXml) != WithoutViewState(_saved.ItemsXml));

    public ProfileSummary ToSummary()
    {
        lock (SyncRoot)
            return new ProfileSummary(Id, Profile.Name, Profile.Width, Profile.Height, _revision, DirtyCore);
    }

    public ProfileDoc ToDoc()
    {
        lock (SyncRoot)
            return ToDocCore();
    }

    /// <summary>A detached copy of the current profile and items (for export and duplication outside the lock), from the current snapshot.</summary>
    public (Profile Profile, List<DisplayItem> Items) CaptureState() => Read(Snapshot);

    /// <summary>A detached copy of the profile as last saved (its entry in <c>profiles.xml</c>).</summary>
    public Profile SavedProfile() => ProfileSerializer.ReadProfileSnapshot(SavedSnapshot.ProfileXml);

    /// <summary>Sets item properties, all or nothing. View-state-only patches (<see cref="ViewStateProperties"/>) never enter the history.</summary>
    /// <param name="record">False only for view-state patches; a false with any other key is refused.</param>
    /// <exception cref="EditException">An unknown item (404), a gauge frame (409) or an invalid set (400). Nothing changes.</exception>
    public MutationResult PatchItems(IReadOnlyList<ItemPatch> patches, string? coalesceKey = null, bool record = true)
    {
        var viewStateOnly = patches.Count > 0 && patches.All(p => p.Set.All(kv => ViewStateProperties.Contains(kv.Key)));
        if (!record && !viewStateOnly)
        {
            var key = patches.SelectMany(p => p.Set).Select(kv => kv.Key).FirstOrDefault(k => !ViewStateProperties.Contains(k)) ?? "record";
            throw new PatchException(key, $"record:false is only for view state ({string.Join(", ", ViewStateProperties)}).");
        }

        return Mutate(DocumentChange.Items, coalesceKey, record && !viewStateOnly, includeDoc: false, () =>
        {
            var validated = patches
                .Select(p =>
                {
                    var item = FindItem(p.Id);
                    return (Item: item, Values: ItemJson.Validate(item, p.Set));
                })
                .ToList();

            foreach (var (item, values) in validated)
            {
                foreach (var (property, value) in values)
                    property.Write(item, value);
            }

            return validated.Select(v => v.Item).Distinct().ToList();
        });
    }

    /// <summary>
    /// Adds a new item of <paramref name="kind"/> (<see cref="ItemFactory"/>) with <paramref name="set"/> applied, at
    /// <paramref name="index"/> (clamped; the end when null) in the group <paramref name="parentId"/> or the top level.
    /// </summary>
    /// <exception cref="EditException">A kind that can't be created or an invalid set (400), an unknown parent (404), a parent that isn't a group (409).</exception>
    public MutationResult AddItem(string kind, Guid? parentId = null, int? index = null, JsonObject? set = null, string? coalesceKey = null) =>
        Mutate(DocumentChange.Structure, coalesceKey, record: true, includeDoc: false, () =>
        {
            var item = ItemFactory.Create(kind, Profile);
            if (set != null)
                ItemJson.Apply(item, set);

            var target = ChildrenOf(parentId, "parentId");
            target.Insert(Math.Clamp(index ?? target.Count, 0, target.Count), item);
            return [item];
        });

    /// <summary>Removes an item (a group with its descendants).</summary>
    /// <exception cref="EditException">An unknown item (404) or a gauge frame (409).</exception>
    public MutationResult RemoveItem(Guid id) =>
        Mutate(DocumentChange.Structure, null, record: true, includeDoc: true, () =>
        {
            var (list, index) = Locate(id);
            list.RemoveAt(index);
            return null;
        });

    /// <summary>
    /// Moves an item into the group <paramref name="parentId"/> (the top level when null) at <paramref name="index"/>:
    /// its position in that list once the item has been taken out, clamped to the list.
    /// </summary>
    /// <exception cref="EditException">
    /// An unknown item or parent (404); a gauge frame, a parent that isn't a group, or a group into itself or one of its
    /// descendants (409).
    /// </exception>
    public MutationResult MoveItem(Guid id, Guid? parentId, int index) =>
        Mutate(DocumentChange.Structure, null, record: true, includeDoc: true, () =>
        {
            var (source, sourceIndex) = Locate(id);
            var item = source[sourceIndex];
            if (parentId is Guid pid && item is GroupDisplayItem group
                && (pid == id || ItemTree.Walk(group.DisplayItems).Any(d => d.Guid == pid)))
            {
                throw EditException.Conflict("A group can't be moved into itself or one of its descendants.", "parentId");
            }

            var target = ChildrenOf(parentId, "parentId");
            source.RemoveAt(sourceIndex);
            target.Insert(Math.Clamp(index, 0, target.Count), item);
            return null;
        });

    /// <summary>
    /// Inserts a deep copy of the item right after it: every property, a group's descendants and a gauge's frames, all
    /// with fresh ids, the same name, moved by <see cref="DuplicateOffset"/> px right and down (a group's descendants
    /// are moved; a group's own position isn't drawn).
    /// </summary>
    /// <exception cref="EditException">An unknown item (404) or a gauge frame (409).</exception>
    public MutationResult DuplicateItem(Guid id) =>
        Mutate(DocumentChange.Structure, null, record: true, includeDoc: false, () =>
        {
            var (list, index) = Locate(id);
            var copy = DeepCopy(list[index], Profile);
            Translate(copy, DuplicateOffset, DuplicateOffset);
            list.Insert(index + 1, copy);
            return [copy];
        });

    /// <summary>Sets profile properties; the result carries the whole document.</summary>
    /// <exception cref="PatchException">An unknown or read-only key, or a value that doesn't fit. Nothing changes.</exception>
    public MutationResult PatchProfile(JsonObject set, string? coalesceKey = null) =>
        Mutate(DocumentChange.Profile, coalesceKey, record: true, includeDoc: true, () =>
        {
            ItemJson.ApplyProfile(Profile, set);
            return null;
        });

    /// <summary>Goes back one history entry (view state is kept). The result always carries the document.</summary>
    public MutationResult Undo() => Step(DocumentChange.Undo, _history.Undo);

    /// <summary>Goes forward one history entry (view state is kept). The result always carries the document.</summary>
    public MutationResult Redo() => Step(DocumentChange.Redo, _history.Redo);

    /// <summary>
    /// Goes back to the last saved state as one undoable step (view state is kept). A clean document is left alone.
    /// The result always carries the document.
    /// </summary>
    public MutationResult Revert() =>
        Transaction(DocumentChange.Revert, includeDoc: true, () =>
        {
            if (!DirtyCore)
                return (false, null);

            var before = _snapshot;
            RestoreKeepingViewState(_saved);
            _history.Record(before, null);
            Commit();
            return (true, null);
        });

    /// <summary>
    /// Writes the document with <paramref name="write"/> (called under <see cref="SyncRoot"/> with the live profile
    /// and items) and makes the current state the saved one. Always writes, even when clean.
    /// </summary>
    public MutationResult Save(Action<Profile, IReadOnlyList<DisplayItem>> write)
    {
        MutationResult result;
        lock (SyncRoot)
        {
            write(Profile, _items);
            _saved = _snapshot;
            _dirty = null;
            result = Result(null, includeDoc: false);
        }

        RaiseChanged(DocumentChange.Saved, result.Revision);
        return result;
    }

    private MutationResult Step(DocumentChange change, Func<DocumentSnapshot, DocumentSnapshot?> step) =>
        Transaction(change, includeDoc: true, () =>
        {
            if (step(_snapshot) is not { } target)
                return (false, null);

            RestoreKeepingViewState(target);
            Commit();
            return (true, null);
        });

    /// <summary>
    /// Runs <paramref name="edit"/> as a <see cref="Transaction"/>. If the state changed, records the old state (when
    /// <paramref name="record"/>) and bumps the revision.
    /// </summary>
    private MutationResult Mutate(DocumentChange change, string? coalesceKey, bool record, bool includeDoc,
        Func<IReadOnlyList<DisplayItem>?> edit) =>
        Transaction(change, includeDoc, () =>
        {
            var touched = edit();
            AfterEdit?.Invoke();

            var after = TakeSnapshot();
            if (after == _snapshot)
                return (false, touched);

            if (record)
                _history.Record(_snapshot, coalesceKey);
            Commit(after);
            return (true, touched);
        });

    /// <summary>Test seam: runs inside every edit's transaction, after the edit is applied and before it is snapshotted.</summary>
    internal Action? AfterEdit { get; set; }

    /// <summary>
    /// Runs <paramref name="body"/> under the lock, all or nothing: if it, or reading back the result, throws, the
    /// model, snapshot, revision, dirty state and history are put back as they were and the exception is rethrown,
    /// so a failure never leaves a change that no revision or history entry accounts for. <see cref="Changed"/> is
    /// raised after the lock when the body reports a change.
    /// </summary>
    private MutationResult Transaction(DocumentChange change, bool includeDoc,
        Func<(bool Changed, IReadOnlyList<DisplayItem>? Touched)> body)
    {
        MutationResult result;
        bool changed;
        lock (SyncRoot)
        {
            var (profile, items, snapshot, revision, dirty) = (Profile, _items, _snapshot, _revision, _dirty);
            var history = _history.Save();
            try
            {
                (changed, var touched) = body();
                result = Result(touched, includeDoc);
            }
            catch
            {
                if (ReferenceEquals(Profile, profile) && ReferenceEquals(_items, items))
                {
                    // Edited in place: rebuild from the snapshot if anything changed (or can't even be snapshotted).
                    bool differs;
                    try
                    {
                        differs = TakeSnapshot() != snapshot;
                    }
                    catch
                    {
                        differs = true;
                    }

                    if (differs)
                        Restore(snapshot);
                }
                else
                {
                    // Undo, redo and revert replace the instances and leave the old ones untouched.
                    (Profile, _items) = (profile, items);
                }

                (_snapshot, _revision, _dirty) = (snapshot, revision, dirty);
                _history.Restore(history);
                throw;
            }
        }

        if (changed)
            RaiseChanged(change, result.Revision);
        return result;
    }

    private void Commit(DocumentSnapshot? snapshot = null)
    {
        _snapshot = snapshot ?? TakeSnapshot();
        _revision++;
        _dirty = null;
    }

    private MutationResult Result(IReadOnlyList<DisplayItem>? touched, bool includeDoc) =>
        new(_revision, DirtyCore, _history.CanUndo, _history.CanRedo,
            touched?.Select(item => ItemJson.ToNode(item, _sensors)).ToList(),
            includeDoc ? ToDocCore() : null);

    private ProfileDoc ToDocCore() =>
        new(Id.ToString(), _revision, DirtyCore, _history.CanUndo, _history.CanRedo,
            ItemJson.ProfileProps(Profile), _items.Select(item => ItemJson.ToNode(item, _sensors)).ToList());

    private void RaiseChanged(DocumentChange change, long revision)
    {
        var handlers = Changed;
        if (handlers == null)
            return;

        var args = new DocumentChangedEventArgs(revision, change);
        foreach (EventHandler<DocumentChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "A Changed handler of profile {ProfileGuid} failed", Id);
            }
        }
    }

    private DocumentSnapshot TakeSnapshot() =>
        new(ProfileSerializer.SnapshotProfile(Profile), ProfileSerializer.SnapshotItems(_items));

    private void Restore(DocumentSnapshot snapshot)
    {
        var (profile, items) = Read(snapshot);
        Profile = profile;
        _items = items;
    }

    /// <summary>Restores <paramref name="snapshot"/> but keeps each surviving group's current <c>IsExpanded</c>.</summary>
    private void RestoreKeepingViewState(DocumentSnapshot snapshot)
    {
        var expanded = ItemTree.Walk(_items).OfType<GroupDisplayItem>().ToDictionary(g => g.Guid, g => g.IsExpanded);
        Restore(snapshot);
        foreach (var group in ItemTree.Walk(_items).OfType<GroupDisplayItem>())
        {
            if (expanded.TryGetValue(group.Guid, out var isExpanded))
                group.IsExpanded = isExpanded;
        }
    }

    private static (Profile Profile, List<DisplayItem> Items) Read(DocumentSnapshot snapshot)
    {
        var profile = ProfileSerializer.ReadProfileSnapshot(snapshot.ProfileXml);
        var items = ProfileSerializer.ReadItems(new StringReader(snapshot.ItemsXml));
        foreach (var item in items)
            item.SetProfile(profile);
        return (profile, items);
    }

    private static string WithoutViewState(string itemsXml) => ViewStateXml.Replace(itemsXml, "");

    /// <summary>The list holding the item (top level or a group's children) and its index there.</summary>
    /// <exception cref="EditException">404 for an unknown id, 409 for a gauge frame.</exception>
    private (IList<DisplayItem> List, int Index) Locate(Guid id, string key = "id")
    {
        if (TryLocate(_items, id) is { } found)
            return found;

        if (ItemTree.Walk(_items).Any(i => i.Guid == id))
            throw EditException.Conflict("Gauge frames are read-only and can't be edited, moved or removed on their own.", key);
        throw EditException.NotFound($"No item '{id}' in this profile.", key);

        static (IList<DisplayItem>, int)? TryLocate(IList<DisplayItem> list, Guid id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Guid == id)
                    return (list, i);
                if (list[i] is GroupDisplayItem group && TryLocate(group.DisplayItems, id) is { } inGroup)
                    return inGroup;
            }

            return null;
        }
    }

    private DisplayItem FindItem(Guid id)
    {
        var (list, index) = Locate(id);
        return list[index];
    }

    /// <summary>The top-level list (null) or a group's children.</summary>
    private IList<DisplayItem> ChildrenOf(Guid? parentId, string key)
    {
        if (parentId is not Guid id)
            return _items;

        var (list, index) = Locate(id, key);
        return list[index] is GroupDisplayItem group
            ? group.DisplayItems
            : throw EditException.Conflict($"'{id}' is not a group.", key);
    }

    /// <summary>
    /// A faithful deep copy through the XML serializer (1.x's <c>GroupDisplayItem.Clone</c> copies only the name),
    /// with fresh ids for the item, its descendants and gauge frames.
    /// </summary>
    internal static DisplayItem DeepCopy(DisplayItem item, Profile profile)
    {
        var copy = ProfileSerializer.ReadItems(new StringReader(ProfileSerializer.SnapshotItems([item])))[0];
        foreach (var node in ItemTree.Walk([copy]))
            node.Guid = Guid.NewGuid();
        copy.SetProfile(profile);
        return copy;
    }

    /// <summary>Moves an item, or every descendant of a group, as the web client's <c>translate</c> does.</summary>
    private static void Translate(DisplayItem item, int dx, int dy)
    {
        if (item is GroupDisplayItem group)
        {
            foreach (var child in group.DisplayItems)
                Translate(child, dx, dy);
            return;
        }

        item.X += dx;
        item.Y += dy;
    }
}
