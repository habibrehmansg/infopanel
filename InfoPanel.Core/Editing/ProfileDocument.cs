using InfoPanel.Models;
using InfoPanel.Sensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

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
/// One open profile: the <see cref="Models.Profile"/> and its items, the engine's copy that rendering and the API
/// read. Everything that reads or changes it holds <see cref="SyncRoot"/>. <see cref="Revision"/> starts at 1;
/// M5 adds the editing operations, undo/redo and dirty tracking (until then it is never dirty).
/// </summary>
public sealed class ProfileDocument
{
    private readonly List<DisplayItem> _items;

    public ProfileDocument(Profile profile, IEnumerable<DisplayItem> items)
    {
        Profile = profile;
        _items = items.ToList();
        foreach (var item in _items)
            item.SetProfile(profile);
    }

    public object SyncRoot { get; } = new();

    public Guid Id => Profile.Guid;

    public Profile Profile { get; }

    /// <summary>The top-level items in draw order. Read under <see cref="SyncRoot"/>.</summary>
    public IReadOnlyList<DisplayItem> Items => _items;

    public long Revision { get; private set; } = 1;

    public bool Dirty => false;

    public bool CanUndo => false;

    public bool CanRedo => false;

    public ProfileSummary ToSummary()
    {
        lock (SyncRoot)
            return new ProfileSummary(Profile.Guid, Profile.Name, Profile.Width, Profile.Height, Revision, Dirty);
    }

    public ProfileDoc ToDoc(SensorHub sensors)
    {
        lock (SyncRoot)
        {
            return new ProfileDoc(
                Profile.Guid.ToString(),
                Revision,
                Dirty,
                CanUndo,
                CanRedo,
                ItemJson.ProfileProps(Profile),
                _items.Select(item => ItemJson.ToNode(item, sensors)).ToList());
        }
    }
}
