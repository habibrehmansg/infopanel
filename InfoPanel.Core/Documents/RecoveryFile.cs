using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;

namespace InfoPanel.Documents;

/// <summary>
/// The recovery file, <c>profiles/{id}/recovery.json</c> (docs/2.0/data-folder.md, "Unsaved changes"): one JSON object,
/// <c>{"format":"infopanel.recovery/1","base":"{64 hex}","written":"{UTC to the second}","document":{…}}</c>, where
/// <c>document</c> is the current generation exactly as a save writes it, embedded verbatim, and <c>base</c> the SHA-256
/// of the saved <c>document.json</c> bytes it was made against, as plain hex. Members it doesn't know are ignored.
/// </summary>
internal static class RecoveryFile
{
    public const string Format = "infopanel.recovery/1";

    /// <summary>The <c>base</c> of a recovery made against <paramref name="saved"/>: its SHA-256 in lower-case hex.</summary>
    public static string BaseOf(ReadOnlySpan<byte> saved) => Convert.ToHexStringLower(SHA256.HashData(saved));

    /// <summary>The file's bytes: <paramref name="document"/> (canonical, as saved) wrapped with its base and the time.</summary>
    public static byte[] Build(string @base, DateTimeOffset written, ReadOnlySpan<byte> document)
    {
        var head = Encoding.UTF8.GetBytes(
            $"{{\"format\":\"{Format}\",\"base\":\"{@base}\",\"written\":\"{Written(written)}\",\"document\":");
        var bytes = new byte[head.Length + document.Length + 1];
        head.CopyTo(bytes, 0);
        document.CopyTo(bytes.AsSpan(head.Length));
        bytes[^1] = (byte)'}';
        return bytes;
    }

    /// <summary><paramref name="time"/> in UTC to the second, as the file and the API write it.</summary>
    public static string Written(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the file's wrapper: its <c>base</c>, <c>written</c> and <c>document</c> (an object), or why it can't be used
    /// (<paramref name="problem"/>). The document's own checks are the caller's. The returned <see cref="JsonDocument"/>
    /// owns the document element.
    /// </summary>
    public static JsonDocument? Read(byte[] bytes, out string @base, out DateTimeOffset written, out JsonElement document, out string? problem)
    {
        @base = "";
        written = default;
        document = default;
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(bytes, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            problem = $"it isn't JSON ({ex.Message})";
            return null;
        }

        // Strings are read with JsonText.TryGetString: one that isn't Unicode text (an unpaired surrogate) parses, but
        // GetString throws on it; such a file is one that can't be used, like any other.
        var root = json.RootElement;
        string? hex = null;
        problem = root.ValueKind != JsonValueKind.Object ? "it isn't a JSON object"
            : !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ? "it has no format"
            : JsonText.TryGetString(format) != Format ? $"its format is {format.GetRawText()}, not \"{Format}\""
            : !root.TryGetProperty("base", out var b) || (hex = JsonText.TryGetString(b)) is not { Length: 64 }
              || !AtomicFile.IsLowerHex(hex) ? "its base isn't 64 lower-case hex digits"
            : !root.TryGetProperty("written", out var w) || JsonText.TryGetString(w) is not { } text || !TryParseWritten(text, out written)
                ? "its written time can't be read"
            : !root.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object ? "it has no document"
            : null;
        if (problem is not null)
        {
            json.Dispose();
            return null;
        }

        @base = hex!;
        document = root.GetProperty("document");
        return json;
    }

    private static bool TryParseWritten(string text, out DateTimeOffset written) =>
        DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out written);
}
