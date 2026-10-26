using System;

namespace InfoPanel.Editing;

/// <summary>Why an edit was refused. The host maps these to 400, 404 and 409.</summary>
public enum EditError
{
    /// <summary>The request is malformed: unknown or read-only key, wrong JSON type, bad enum name or colour.</summary>
    Invalid,

    /// <summary>A referenced item (or parent) does not exist in the document.</summary>
    NotFound,

    /// <summary>The edit is well-formed but impossible in the document's state (a group into itself, a gauge frame).</summary>
    Conflict,
}

/// <summary>An edit the document refused; nothing was changed.</summary>
public class EditException(EditError error, string message, string? key = null) : Exception(message)
{
    public EditError Error { get; } = error;

    /// <summary>The offending property or request field, when there is one.</summary>
    public string? Key { get; } = key;

    public static EditException NotFound(string message, string? key = null) => new(EditError.NotFound, message, key);

    public static EditException Conflict(string message, string? key = null) => new(EditError.Conflict, message, key);
}

/// <summary>A property set that can't be applied (plan §7 M5): the host answers 400 with the offending key.</summary>
public sealed class PatchException(string key, string message) : EditException(EditError.Invalid, message, key);
