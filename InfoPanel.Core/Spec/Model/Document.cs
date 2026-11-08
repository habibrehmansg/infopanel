using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.Spec;

// The document format, infopanel.document/1 (spec/schemas/document.schema.json). Data only, like Scene.cs. An item's
// scene is kept as the JSON it arrived as (SceneItem.Scene): a scene the engine can't read is stored unchanged and
// drawn as a placeholder (docs/unsupported.md), so the document never needs to parse it to be loaded.

// The engine API returns a document in the same shape with every rev set and scenes as stored (responseDocument).
[SpecDef("urn:infopanel:document:1", "urn:infopanel:api:1#/$defs/responseDocument")]
public sealed record Document
{
    public required string Format { get; init; }
    public IReadOnlyList<string>? Requires { get; init; }
    public required string Id { get; init; }
    public long? Rev { get; init; }
    public required DocumentProfile Profile { get; init; }
    public required IReadOnlyList<Item> Items { get; init; }
}

/// <summary>
/// A document as a create takes it (importDocument): <see cref="Id"/> and <see cref="Rev"/> optional and ignored (the
/// create's own id replaces the one), scenes as stored. The engine reads it as JSON (<c>Workspace.Create</c>).
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/importDocument")]
public sealed record ImportDocument
{
    public required string Format { get; init; }
    public IReadOnlyList<string>? Requires { get; init; }
    public string? Id { get; init; }
    public long? Rev { get; init; }
    public required DocumentProfile Profile { get; init; }
    public required IReadOnlyList<Item> Items { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/profile")]
public sealed record DocumentProfile
{
    public long? Rev { get; init; }
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string? Background { get; init; }
    public bool? StatsOverlay { get; init; }
    /// <summary>The regional format numbers and dates are drawn in, a BCP 47 tag; null: the PC's.</summary>
    public string? Locale { get; init; }
    public WindowSettings? Window { get; init; }
    public IReadOnlyList<string>? TriggerApps { get; init; }
    public IReadOnlyList<Guide>? Guides { get; init; }
    public JsonElement? Editor { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/windowSettings")]
public sealed record WindowSettings
{
    public required bool Enabled { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public DisplayMatch? Display { get; init; }
    /// <summary>The window's places on other displays it was on, the most recent first (at most 8); null: none.</summary>
    public IReadOnlyList<WindowSpot>? Spots { get; init; }
    /// <summary>Hide the window when none of its displays is connected (null: the default, true); false: the main display.</summary>
    public bool? Strict { get; init; }
    /// <summary><c>normal</c> (null: the default), <c>top</c> or <c>desktop</c>: where the window sits among the computer's other windows.</summary>
    public string? Level { get; init; }
    /// <summary>Clicks pass through the window to whatever is beneath it (null: the default, false); it wins over <see cref="Draggable"/> and <see cref="Resizable"/>, whose values are kept.</summary>
    public bool? ClickThrough { get; init; }
    public bool? Draggable { get; init; }
    public bool? Resizable { get; init; }

    /// <summary>
    /// Equal members, <see cref="Spots"/> spot by spot (a record compares a list by reference): a window committed again
    /// with the same places is no change.
    /// </summary>
    public bool Equals(WindowSettings? other) =>
        other is not null && Enabled == other.Enabled && X == other.X && Y == other.Y && Equals(Display, other.Display)
        && (ReferenceEquals(Spots, other.Spots) || (Spots is not null && other.Spots is not null && Spots.SequenceEqual(other.Spots)))
        && Strict == other.Strict && Level == other.Level && ClickThrough == other.ClickThrough
        && Draggable == other.Draggable && Resizable == other.Resizable;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Display);
        hash.Add(Spots?.Count);
        hash.Add(Strict);
        hash.Add(Level);
        hash.Add(ClickThrough);
        hash.Add(Draggable);
        hash.Add(Resizable);
        return hash.ToHashCode();
    }
}

/// <summary>A place the window had on another display: <see cref="X"/>, <see cref="Y"/> pixels from its top-left (null: 0).</summary>
[SpecDef("urn:infopanel:document:1#/$defs/windowSpot")]
public sealed record WindowSpot
{
    public required DisplayMatch Display { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/displayMatch")]
public sealed record DisplayMatch
{
    /// <summary>The monitor's EDID maker and product code (<c>DEL40F4</c>), matched before the name and size; null: none.</summary>
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/guide")]
public sealed record Guide
{
    /// <summary><c>x</c> (a vertical line) or <c>y</c> (a horizontal one).</summary>
    public required string Axis { get; init; }
    public required double At { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SceneItem), "scene")]
[JsonDerivedType(typeof(GroupItem), "group")]
public abstract record Item
{
    public required string Id { get; init; }
    public long? Rev { get; init; }
    public string? Name { get; init; }
    public bool? Visible { get; init; }
    public bool? Locked { get; init; }
    public required Placement Placement { get; init; }
    public JsonElement? Origin { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/sceneItem", "urn:infopanel:api:1#/$defs/responseSceneItem",
    "urn:infopanel:api:1#/$defs/importSceneItem")]
public sealed record SceneItem : Item
{
    public Layout? Layout { get; init; }
    public IReadOnlyDictionary<string, Binding>? Bindings { get; init; }
    public ItemSource? Source { get; init; }
    /// <summary>The scene as received; read it with <see cref="SceneValidator"/>.</summary>
    public required JsonElement Scene { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/groupItem", "urn:infopanel:api:1#/$defs/responseGroupItem",
    "urn:infopanel:api:1#/$defs/importGroupItem")]
public sealed record GroupItem : Item
{
    public required IReadOnlyList<Item> Children { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/placement")]
public sealed record Placement
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public double? Rotation { get; init; }
    /// <summary>[x, y] in local coordinates.</summary>
    public IReadOnlyList<double>? Pivot { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/layout")]
public sealed record Layout
{
    public double? W { get; init; }
    public double? H { get; init; }
}

[SpecDef("urn:infopanel:document:1#/$defs/binding")]
public sealed record Binding
{
    public required string Key { get; init; }
    public string? Stat { get; init; }
}

/// <summary>An item's source envelope: <see cref="Format"/> with inline <see cref="Data"/>, or an opaque <see cref="Ref"/>. The schema allows exactly one.</summary>
[SpecDef("urn:infopanel:document:1#/$defs/inlineSource", "urn:infopanel:document:1#/$defs/refSource")]
public sealed record ItemSource
{
    public string? Format { get; init; }
    public string? Ref { get; init; }
    /// <summary>
    /// Opaque, kept as received: <see cref="JsonValueKind.Undefined"/> (the default) when absent, a JSON null when it
    /// was written as null, so the two stay apart when the source is written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; init; }
    public IReadOnlyList<string>? Deps { get; init; }
}
