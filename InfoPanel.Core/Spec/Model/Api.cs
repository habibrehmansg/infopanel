using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.Spec;

// Engine API bodies that carry the document format, and the token bodies (spec/schemas/api.schema.json). Data only.

[SpecDef("urn:infopanel:api:1#/$defs/session")]
public sealed record Session
{
    public required string EngineVersion { get; init; }
    public required int Protocol { get; init; }
    public required string Instance { get; init; }
    /// <summary><c>macos</c>, <c>windows</c>, <c>linux</c> or <c>other</c>.</summary>
    public required string Platform { get; init; }
    public required string DataDir { get; init; }
    public required FormatSupport FormatSupport { get; init; }
}

/// <summary>
/// One profile in the list (the schema's <c>profileEntry</c>, a union of one definition per status).
/// <see cref="Status"/> is <c>ok</c> (<see cref="Name"/>, <see cref="Width"/>, <see cref="Height"/>, <see cref="Rev"/>
/// and <see cref="Dirty"/> set, no <see cref="Problem"/>), <c>unsupported</c> (<see cref="Problem"/> set, and
/// <see cref="Name"/> when the document has one) or <c>unreadable</c> (only <see cref="Problem"/>).
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/profileEntryOk", "urn:infopanel:api:1#/$defs/profileEntryUnsupported",
    "urn:infopanel:api:1#/$defs/profileEntryUnreadable")]
public sealed record ProfileEntry
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public string? Name { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? Rev { get; init; }
    public bool? Dirty { get; init; }
    public ValidationErrorBody? Problem { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/profileStatusProblem")]
public sealed record ProfileStatusProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    /// <summary><c>unsupported</c> or <c>unreadable</c>: the profile's status.</summary>
    public required string Code { get; init; }
    public required ValidationErrorBody Problem { get; init; }
}

/// <summary>
/// <c>POST /api/profiles</c> (the schema's <c>createProfileRequest</c>): <see cref="Id"/> with either <see cref="Name"/>,
/// <see cref="Width"/> and <see cref="Height"/> (an empty profile) or <see cref="Document"/>.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/createProfileEmpty", "urn:infopanel:api:1#/$defs/createProfileFromDocument")]
public sealed record CreateProfileRequest
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public Document? Document { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/duplicateProfileRequest")]
public sealed record DuplicateProfileRequest
{
    public required string Id { get; init; }
}

/// <summary>409: a profile with the requested id already exists.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/existsProblem")]
public sealed record ExistsProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    /// <summary>Always <c>exists</c>.</summary>
    public required string Code { get; init; }
}

/// <summary>The current document, as a <see cref="InfoPanel.Spec.Document"/> with every revision member set and scenes as stored.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/documentResponse")]
public sealed record DocumentResponse
{
    public required string Instance { get; init; }
    public required long Rev { get; init; }
    public required bool Dirty { get; init; }
    public required bool CanUndo { get; init; }
    public required bool CanRedo { get; init; }
    public required Document Document { get; init; }
}

/// <summary>A stored blob (the schema's <c>blobInfo</c>): exactly one of <see cref="Media"/> and <see cref="MediaError"/> is set.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/blobInfoMedia", "urn:infopanel:api:1#/$defs/blobInfoMediaError")]
public sealed record BlobInfo
{
    public required string Id { get; init; }
    public required long Bytes { get; init; }
    public MediaInfoBody? Media { get; init; }
    public ValidationErrorBody? MediaError { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/mediaInfo")]
public sealed record MediaInfoBody
{
    /// <summary><c>png</c>, <c>jpeg</c>, <c>gif</c>, <c>webp</c> or <c>svg</c>.</summary>
    public required string Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Frames { get; init; }
    public required double Duration { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/commitRequest")]
public sealed record CommitRequest
{
    public required string OpId { get; init; }
    public required string Instance { get; init; }
    /// <summary>The document revision the commit is based on: tells a retry from a new commit once older opIds are forgotten.</summary>
    public required long BaseRev { get; init; }
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
    public string? Locale { get; init; }
    public WindowSettings? Window { get; init; }
    public IReadOnlyList<string>? TriggerApps { get; init; }
    public IReadOnlyList<Guide>? Guides { get; init; }
    public JsonElement? Editor { get; init; }
}

/// <summary>The body of undo, redo and revert: they act only on <see cref="Instance"/> while the document is at <see cref="BaseRev"/>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/historyRequest")]
public sealed record HistoryRequest
{
    public required string Instance { get; init; }
    public required long BaseRev { get; init; }
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
    /// <summary>The conflicting op's index; null for a conflict of the whole request (<c>instance</c>, and every conflict of undo, redo or revert).</summary>
    public int? Op { get; init; }
    public string? Id { get; init; }
    /// <summary><c>changed</c>, <c>missing</c> or <c>instance</c>.</summary>
    public required string Reason { get; init; }
    public long? Rev { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
}

/// <summary>409: an unknown opId with a baseRev older than the engine's retry window.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/expiredProblem")]
public sealed record ExpiredProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    /// <summary>Always <c>expired</c>.</summary>
    public required string Code { get; init; }
    public required long Rev { get; init; }
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

[SpecDef("urn:infopanel:api:1#/$defs/frameHeader")]
public sealed record FrameHeader
{
    public required string ProfileId { get; init; }
    public required long Rev { get; init; }
    public required long Tick { get; init; }
    public required long FrameId { get; init; }
    public required string Encoding { get; init; }
    public required double Scale { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required int ProfileWidth { get; init; }
    public required int ProfileHeight { get; init; }
    public required IReadOnlyList<ItemBounds> ItemBounds { get; init; }
}

[SpecDef("urn:infopanel:api:1#/$defs/docEvent")]
public sealed record DocEvent
{
    /// <summary>Always <c>doc</c>.</summary>
    public required string Type { get; init; }
    public required string ProfileId { get; init; }
    public required long Rev { get; init; }
    public required bool Dirty { get; init; }
    public required bool CanUndo { get; init; }
    public required bool CanRedo { get; init; }
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

/// <summary><c>POST /api/tokens</c>: <see cref="Scopes"/> holds <c>read</c>, <c>edit</c> or both.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/createTokenRequest")]
public sealed record CreateTokenRequest
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
}

/// <summary>
/// An integration token (the schema's <c>tokenInfo</c>, as listed), or a new one (<c>createdToken</c>, with its secret
/// in <see cref="Token"/>, which only the create's answer carries).
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/tokenInfo", "urn:infopanel:api:1#/$defs/createdToken")]
public sealed record TokenInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Always <c>integration</c>.</summary>
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required string Created { get; init; }
    public string? LastUsed { get; init; }
    public string? Token { get; init; }
}

/// <summary>403: the token lacks <see cref="Scope"/> (<c>read</c>, <c>edit</c> or <c>session</c>).</summary>
[SpecDef("urn:infopanel:api:1#/$defs/scopeProblem")]
public sealed record ScopeProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public required int Status { get; init; }
    /// <summary>Always <c>scope</c>.</summary>
    public required string Code { get; init; }
    public required string Scope { get; init; }
}

/// <summary>
/// An error without a definition of its own (the schema's <c>problem</c>): what ASP.NET Core's problem details write for
/// the engine's HTTP layer (401, a refused origin, 404, a bad query parameter, 405, 413, 500).
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/problem")]
public sealed record ProblemBody
{
    public string? Type { get; init; }
    public required string Title { get; init; }
    public required int Status { get; init; }
    public string? Detail { get; init; }
    public string? TraceId { get; init; }
}

/// <summary>A listed sensor (<c>GET /api/sensors</c>); <see cref="Status"/> is its status now. A binding names it by <see cref="Key"/>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/sensorEntry")]
public sealed record SensorEntry
{
    public required string Key { get; init; }
    public required string Source { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required InfoPanel.Sensors.SensorStatus Status { get; init; }
}

/// <summary>
/// A listed sensor that has a reading (<c>GET /api/sensors/values</c>); <see cref="Status"/> is <c>live</c> or
/// <c>stale</c> (the last value). <see cref="Text"/> is set for a text or table sensor, <see cref="History"/> for a
/// number sensor when asked for.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/sensorValue")]
public sealed record SensorValue
{
    public required string Key { get; init; }
    public required double Now { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double Avg { get; init; }
    public required string Unit { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<double>? History { get; init; }
    public required InfoPanel.Sensors.SensorStatus Status { get; init; }
}

// The preview socket's text messages (the schema's clientMessage and serverMessage; docEvent is above).

/// <summary>The first message on <c>/ws</c> when the token wasn't offered as a subprotocol. <see cref="Type"/> is always <c>auth</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/authMessage")]
public sealed record AuthMessage
{
    public required string Type { get; init; }
    public required string Token { get; init; }
}

/// <summary>Subscribes to a profile's doc messages and frames. <see cref="Type"/> is always <c>subscribe</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/subscribeMessage")]
public sealed record SubscribeMessage
{
    public required string Type { get; init; }
    public required string ProfileId { get; init; }
    public double? Scale { get; init; }
    public int? MaxFps { get; init; }
}

/// <summary>Changes the subscription's scale or frame rate. <see cref="Type"/> is always <c>view</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/viewMessage")]
public sealed record ViewMessage
{
    public required string Type { get; init; }
    public double? Scale { get; init; }
    public int? MaxFps { get; init; }
}

/// <summary>Ends the subscription. <see cref="Type"/> is always <c>unsubscribe</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/unsubscribeMessage")]
public sealed record UnsubscribeMessage
{
    public required string Type { get; init; }
}

/// <summary>The engine's first message on <c>/ws</c>. <see cref="Type"/> is always <c>hello</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/helloMessage")]
public sealed record HelloMessage
{
    public required string Type { get; init; }
    public required int Protocol { get; init; }
}

/// <summary>The profile list changed (<see cref="Version"/> grows with each change). <see cref="Type"/> is always <c>profiles</c>.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/profilesMessage")]
public sealed record ProfilesMessage
{
    public required string Type { get; init; }
    public required long Version { get; init; }
}

/// <summary>
/// Something the engine couldn't do for the client; the connection stays open. <see cref="Type"/> is always <c>error</c>;
/// <see cref="Code"/> and <see cref="Path"/> are what a client acts on (spec/docs/api.md#errors-on-the-socket), <see cref="Message"/>
/// is for people.
/// </summary>
[SpecDef("urn:infopanel:api:1#/$defs/errorMessage")]
public sealed record ErrorMessage
{
    public required string Type { get; init; }
    public required string Code { get; init; }

    /// <summary>A JSON pointer into the client message the error answers: "" for the whole message, or when it answers none.</summary>
    public required string Path { get; init; }

    public required string Message { get; init; }
}
