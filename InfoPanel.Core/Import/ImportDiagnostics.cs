using System.Collections.Generic;
using InfoPanel.Spec;

namespace InfoPanel.Import;

/// <summary>
/// The diagnostics of one answer, in the order they were found: at most <see cref="ImportLimits.Diagnostics"/>, then one
/// last <c>more</c> diagnostic counting the rest (as validation errors are capped).
/// </summary>
public sealed class ImportDiagnostics(int max)
{
    public const string Error = "error", Warning = "warning", Info = "info";

    private readonly List<ImportDiagnostic> _listed = [];
    private long _hidden;

    public void Add(string severity, string code, string path, string message, string? file = null, int? line = null)
    {
        if (_listed.Count < max)
            _listed.Add(new ImportDiagnostic { Severity = severity, Code = code, Path = path, Message = message, File = file, Line = line });
        else
            _hidden++;
    }

    /// <summary>Whether a diagnostic with <paramref name="code"/> was listed.</summary>
    public bool Has(string code) => _listed.Exists(d => d.Code == code);

    public IReadOnlyList<ImportDiagnostic> ToList()
    {
        var result = new List<ImportDiagnostic>(_listed);
        if (_hidden > 0)
            result.Add(new ImportDiagnostic { Severity = Info, Code = "more", Path = "", Message = $"{_hidden} more diagnostics not listed." });
        return result;
    }
}
