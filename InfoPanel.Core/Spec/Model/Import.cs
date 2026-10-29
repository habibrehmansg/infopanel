using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InfoPanel.Editing;

namespace InfoPanel.Spec;

// The 1.x import and the profile archive: the import's answers and the migration's request, written and read with
// SpecJson.Options (spec/docs/import.md). Data only. The items of an envelope are the 1.x item JSON (ItemNode), whose
// grammar is the generated urn:infopanel:v1item:1.

/// <summary>
/// A problem found while reading 1.x data or an archive, inside an otherwise readable answer. <see cref="Path"/> is a
/// JSON pointer into the answer; <see cref="File"/> and <see cref="Line"/> give the 1.x location when known.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/importDiagnostic")]
public sealed record ImportDiagnostic
{
    /// <summary><c>error</c>, <c>warning</c> or <c>info</c>.</summary>
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Path { get; init; }
    public required string Message { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
}

/// <summary>
/// One 1.x profile, read without installing anything (the envelope): its properties, its items as the 1.x item JSON
/// (the kind manifest's <c>props</c>, gauge frames under <c>images</c>, <c>meta.sensor</c> with the binding's status
/// now), its guides, its assets mapped to stored blobs, and what was found on the way.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Profile")]
public sealed record V1Profile
{
    /// <summary><c>folder</c> (a 1.x data folder) or <c>archive</c> (a 1.x <c>.infopanel</c> file).</summary>
    public required string Source { get; init; }

    /// <summary>The 1.x Guid as stored (lower case). A client that creates a profile from it chooses the profile's id.</summary>
    public required string Id { get; init; }

    /// <summary>Every member of the 1.x profile model, camelCase (<see cref="ItemJson.ProfileProps"/>; its members are <see cref="V1ProfileProps"/>'s).</summary>
    public required JsonObject Profile { get; init; }

    public V1TargetWindow? TargetWindow { get; init; }
    public required IReadOnlyList<ItemNode> Items { get; init; }
    public required IReadOnlyList<V1Guide> Guides { get; init; }
    public required IReadOnlyList<V1Asset> Assets { get; init; }
    public required IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// The members of an envelope's <c>profile</c>: every member of the 1.x profile model, camelCase, as
/// <see cref="ItemJson.ProfileProps"/> writes them (a string 1.x left null, or a number that isn't finite, is null). The
/// envelope carries them as that JSON object; this type is its definition's model, and the import's tests hold the two
/// equal.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1ProfileProps")]
public sealed record V1ProfileProps
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool ShowFps { get; init; }
    public required bool Drag { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? BackgroundColor { get; init; }
    public required bool Active { get; init; }
    public required bool OpenGL { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required double? FontScale { get; init; }
    public required bool Topmost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Font { get; init; }
    public required int FontSize { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Color { get; init; }
    public required int WindowX { get; init; }
    public required int WindowY { get; init; }
    public required bool Resize { get; init; }
    public required bool StrictWindowMatching { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? TriggerProcessNames { get; init; }
    public required bool IsSelected { get; init; }
}

/// <summary>The 1.x profile model's <c>TargetWindow</c>: the display it was placed on.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1TargetWindow")]
public sealed record V1TargetWindow
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string? DeviceName { get; init; }
}

/// <summary>A 1.4.x guide (an item type of its own there): data for a client, not an item.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Guide")]
public sealed record V1Guide
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Length { get; init; }
    public required string Color { get; init; }
    public required int Rotation { get; init; }

    /// <summary>As stored; 1.4.x's default <c>DisplayItem</c> when absent.</summary>
    public required string Name { get; init; }

    /// <summary>As stored: a guide in a hidden group keeps its own value (<see cref="Container"/> tells the group).</summary>
    public required bool Hidden { get; init; }

    /// <summary>1.4.x's <c>IsLocked</c>.</summary>
    public required bool Locked { get; init; }

    /// <summary>
    /// The JSON pointer of the group's children list the guide sat in (<c>/items/1/children</c>), in the envelope's
    /// items; null at the top level or when it can't be told.
    /// </summary>
    public string? Container { get; init; }
}

/// <summary>
/// One image file the items use, keyed by exactly the item props <c>filePath</c> and <c>relativePath</c>: the stored
/// <see cref="Blob"/>, or the <see cref="Code"/> of why nothing was stored (and, for <c>import.asset.media</c>, the
/// media check's or the limit's code as <see cref="Reason"/>).
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Asset")]
public sealed record V1Asset
{
    public required string Path { get; init; }
    public required bool Relative { get; init; }
    public BlobInfo? Blob { get; init; }
    public string? Code { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A profile of a 1.x folder's <c>profiles.xml</c>, in 1.x order.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1ProfileSummary")]
public sealed record V1ProfileSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool Active { get; init; }
}

/// <summary>
/// The user data of 1.x's <c>settings.xml</c>, each value as 1.4.x would use it (an absent element gives 1.4.x's
/// default). Nothing is applied by reading it.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Settings")]
public sealed record V1Settings
{
    /// <summary>The settings version (1.4.x writes 131, 1.2.x 115; 114 or none is 1.1.x data); null when it doesn't parse.</summary>
    public int? Version { get; init; }
    public required int TargetFrameRate { get; init; }
    public required int TargetGraphUpdateRate { get; init; }
    public required V1LibreSettings Libre { get; init; }
    public required V1ProgramSpecificSettings ProgramSpecific { get; init; }
    public required IReadOnlyList<V1Device> Devices { get; init; }

    /// <summary>Per device family, whether 1.x drove several devices of it.</summary>
    public required IReadOnlyDictionary<string, bool> MultiDevice { get; init; }

    public required IReadOnlyList<V1Hotkey> Hotkeys { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/v1LibreSettings")]
public sealed record V1LibreSettings
{
    public required bool Enabled { get; init; }
    public required bool Storage { get; init; }
    public required int StorageInterval { get; init; }
}

/// <summary>Whether profiles with trigger apps switched at all in 1.x, and whether they hid the others.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1ProgramSpecificSettings")]
public sealed record V1ProgramSpecificSettings
{
    public required bool Enabled { get; init; }
    public required bool HideOthers { get; init; }
}

/// <summary>
/// A panel device of one of 1.x's eight families: the 1.x profile it showed (null for none) and every simple element
/// of the device as stored, as strings.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Device")]
public sealed record V1Device
{
    public required string Family { get; init; }
    public string? Profile { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
}

/// <summary>A 1.x hotkey as stored: the keys, the device it targets and the 1.x profile it switches to (null for none).</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Hotkey")]
public sealed record V1Hotkey
{
    public required string Modifiers { get; init; }
    public required string Key { get; init; }
    public required string DeviceType { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceLocation { get; init; }
    public string? Profile { get; init; }
}

/// <summary>
/// The 1.x migration: <c>none</c> (no 1.x data), <c>pending</c> (1.x data and no record) or <c>done</c> (a record, or
/// one that can't be read); the folder's contents when 1.x data exists.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1Migration")]
public sealed record V1Migration
{
    public required string State { get; init; }
    public required string Folder { get; init; }
    public V1MigrationRecord? Record { get; init; }
    public IReadOnlyList<V1ProfileSummary>? Profiles { get; init; }
    public V1Settings? Settings { get; init; }
    public IReadOnlyList<ImportDiagnostic>? Diagnostics { get; init; }
}

/// <summary>What a migration did, as a client records it (<c>completed</c> and <c>folder</c> are the engine's).</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1MigrationRecord")]
public sealed record V1MigrationRecord
{
    public required string Completed { get; init; }
    public required string Folder { get; init; }
    public required IReadOnlyList<V1MigrationOutcome> Profiles { get; init; }

    /// <summary><c>imported</c> or <c>skipped</c>.</summary>
    public required string Settings { get; init; }
}

/// <summary>
/// <c>PUT /api/import/v1</c>: what a migration did, as a client reports it; the engine stores it as the run-once record
/// with the time and the folder.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1MigrationRequest")]
public sealed record V1MigrationRequest
{
    public required IReadOnlyList<V1MigrationOutcome> Profiles { get; init; }

    /// <summary><c>imported</c> or <c>skipped</c>.</summary>
    public required string Settings { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/v1MigrationOutcome")]
public sealed record V1MigrationOutcome
{
    /// <summary>The 1.x profile's Guid, lower case.</summary>
    public required string V1Id { get; init; }

    /// <summary>The profile created from it, or null when none was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Id { get; init; }

    /// <summary><c>imported</c>, <c>skipped</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// A profile archive opened and verified: its document (to create the profile with), the blobs it carried (stored), and
/// the ids the document refers to that are neither in the archive nor stored here.
/// </summary>
public sealed record ArchiveContents
{
    public required JsonElement Document { get; init; }
    public required IReadOnlyList<BlobInfo> Blobs { get; init; }
    public required IReadOnlyList<string> Missing { get; init; }
}
