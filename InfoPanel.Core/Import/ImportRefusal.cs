using System;
using System.Collections.Generic;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>
/// An import refused as a whole (the host answers 422 <c>invalidProblem</c>): an archive that breaks a guard
/// (<c>archive.*</c>), a 1.x archive without its profile (<c>import.archive</c>) or with an unreadable one
/// (<c>import.xml</c>), 1.x data this engine doesn't import (<c>import.version</c>), or a 2.0 archive whose contents
/// break a limit or the formats (<c>limit.*</c>, document errors under <c>/document</c>). An entry's error has the
/// path <c>/entries/{name}</c> (the name escaped as a JSON pointer token); the others <c>""</c> or their own path.
/// </summary>
public sealed class ImportRefusal : Exception
{
    public ImportRefusal(string code, string message, string path = "", Exception? inner = null)
        : this([new SpecError(path, code, message)], inner)
    {
    }

    public ImportRefusal(IReadOnlyList<SpecError> errors, Exception? inner = null)
        : base(errors.Count > 0 ? errors[0].Message : "The import was refused.", inner)
    {
        Errors = errors;
    }

    /// <summary>At least one error; the first is the reason.</summary>
    public IReadOnlyList<SpecError> Errors { get; }

    public string Code => Errors[0].Code;

    /// <summary>An entry's error path: <c>/entries/</c> and the name as a JSON pointer token.</summary>
    public static string EntryPath(string name) => "/entries/" + name.Replace("~", "~0").Replace("/", "~1");

    public static ImportRefusal Entry(string code, string name, string message, Exception? inner = null) =>
        new(code, message, EntryPath(name), inner);
}
