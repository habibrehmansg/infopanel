using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.Spec;

// Engine API bodies that carry the document format (spec/schemas/api.schema.json). Data only.

[SpecDef("urn:infopanel:api:1#/$defs/commitRequest")]
public sealed record CommitRequest
{
    public required string OpId { get; init; }
    public string? Instance { get; init; }
    public string? CoalesceKey { get; init; }
    public required IReadOnlyList<CommitOp> Ops { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SetPlacementOp), "setPlacement")]
[JsonDerivedType(typeof(SetDesignOp), "setDesign")]
[JsonDerivedType(typeof(SetBindingsOp), "setBindings")]
[JsonDerivedType(typeof(SetAttributesOp), "setAttributes")]
[JsonDerivedType(typeof(InsertOp), "insert")]
[JsonDerivedType(typeof(RemoveOp), "remove")]
[JsonDerivedType(typeof(MoveOp), "move")]
[JsonDerivedType(typeof(ProfileOp), "profile")]
public abstract record CommitOp;

[SpecDef("urn:infopanel:api:1#/$defs/setPlacementOp")]
public sealed record SetPlacementOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
    public required Placement Placement { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/setDesignOp")]
public sealed record SetDesignOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
    public Layout? Layout { get; init; }
    public ItemSource? Source { get; init; }
    public required JsonElement Scene { get; init; }
    public JsonElement? Origin { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/setBindingsOp")]
public sealed record SetBindingsOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
    public required IReadOnlyDictionary<string, Binding> Bindings { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/setAttributesOp")]
public sealed record SetAttributesOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
    public required ItemAttributes Set { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/setAttributesOp/properties/set")]
public sealed record ItemAttributes
{
    public string? Name { get; init; }
    public bool? Visible { get; init; }
    public bool? Locked { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/insertOp")]
public sealed record InsertOp : CommitOp
{
    public required Item Item { get; init; }
    public string? ParentId { get; init; }
    public int? Index { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/removeOp")]
public sealed record RemoveOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/moveOp")]
public sealed record MoveOp : CommitOp
{
    public required string Id { get; init; }
    public long? BaseRev { get; init; }
    public string? ParentId { get; init; }
    public required int Index { get; init; }
    public Placement? Placement { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/profileOp")]
public sealed record ProfileOp : CommitOp
{
    public long? BaseRev { get; init; }
    public required ProfileFields Set { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/profileOp/properties/set")]
public sealed record ProfileFields
{
    public string? Name { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Background { get; init; }
    public bool? StatsOverlay { get; init; }
    public WindowSettings? Window { get; init; }
    public IReadOnlyList<string>? TriggerApps { get; init; }
    public IReadOnlyList<Guide>? Guides { get; init; }
    public JsonElement? Editor { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/commitResult")]
public sealed record CommitResult
{
    public required long Rev { get; init; }
    public required IReadOnlyDictionary<string, long> Items { get; init; }
    public IReadOnlyList<string>? Removed { get; init; }
    public long? ProfileRev { get; init; }
    public required bool Dirty { get; init; }
    public required bool CanUndo { get; init; }
    public required bool CanRedo { get; init; }
    public bool? Replayed { get; init; }
    public IReadOnlyList<CommitWarning>? Warnings { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/commitWarning")]
public sealed record CommitWarning
{
    public required int Op { get; init; }
    public string? Id { get; init; }
    public required string Code { get; init; }
    public string? Message { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/conflictProblem")]
public sealed record ConflictProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    public required string Code { get; init; }
    public required long Rev { get; init; }
    public required IReadOnlyList<Conflict> Conflicts { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/conflict")]
public sealed record Conflict
{
    public required int Op { get; init; }
    public string? Id { get; init; }
    /// <summary><c>changed</c>, <c>missing</c> or <c>instance</c>.</summary>
    public required string Reason { get; init; }
    public long? Rev { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/invalidProblem")]
public sealed record InvalidProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    public required string Code { get; init; }
    public required IReadOnlyList<ValidationErrorBody> Errors { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/validationError")]
public sealed record ValidationErrorBody
{
    public required string Path { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/itemBounds")]
public sealed record ItemBounds
{
    public required string Id { get; init; }
    public required BoundsRect Frame { get; init; }
    public required BoundsRect Ink { get; init; }
    public required double Rotation { get; init; }
    public string? Problem { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/rect")]
public sealed record BoundsRect
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/formatSupport")]
public sealed record FormatSupport
{
    public required FormatVersion Document { get; init; }
    public required FormatVersion Scene { get; init; }
    public required IReadOnlyDictionary<string, double> Limits { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/formatVersion")]
public sealed record FormatVersion
{
    public required IReadOnlyList<string> Formats { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
}
