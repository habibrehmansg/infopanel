using System;
using System.IO;
using System.Text.Json;
using InfoPanel.Spec;

namespace InfoPanel.Documents;

/// <summary>
/// The draft's <c>window.topmost</c> in a document an earlier engine wrote itself (a stored profile, a recovery
/// checkpoint) or in any profile archive's document (the engine can't tell who made an archive), read as
/// <c>window.level</c>: a <c>profile.window</c> with
/// <c>topmost</c> and no <c>level</c> has <c>topmost: true</c> read as <c>level: "top"</c> and <c>topmost: false</c>
/// dropped (normal); any other <c>topmost</c> is left for the schema to refuse. Applied after the JSON-level pass and
/// before the document is loaded, never to what a client sends (closed: <c>topmost</c> there is a schema error). The
/// file itself is never rewritten: it keeps <c>topmost</c> until the next save writes the document (docs/2.0/data-folder.md).
/// </summary>
internal static class StoredTopmost
{
    /// <summary>The document with its window's <c>topmost</c> read as above; null when there is nothing to read.</summary>
    public static JsonDocument? Read(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("window", out var window) || window.ValueKind != JsonValueKind.Object
            || window.TryGetProperty("level", out _)
            || !window.TryGetProperty("topmost", out var topmost) || topmost.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Copy(writer, document, "profile", () => Copy(writer, profile, "window", () =>
            {
                writer.WriteStartObject();
                foreach (var member in window.EnumerateObject())
                {
                    if (!member.NameEquals("topmost"))
                        WriteRaw(writer, member);
                    else if (member.Value.ValueKind == JsonValueKind.True)
                        writer.WriteString("level", "top");
                }
                writer.WriteEndObject();
            }));
        }
        return JsonDocument.Parse(buffer.ToArray(), SpecJson.DocumentOptions);
    }

    /// <summary>The object <paramref name="value"/>, its member <paramref name="name"/>'s value written by <paramref name="write"/>.</summary>
    private static void Copy(Utf8JsonWriter writer, JsonElement value, string name, Action write)
    {
        writer.WriteStartObject();
        foreach (var member in value.EnumerateObject())
        {
            if (member.NameEquals(name))
            {
                writer.WritePropertyName(name);
                write();
            }
            else
            {
                WriteRaw(writer, member);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteRaw(Utf8JsonWriter writer, JsonProperty member)
    {
        writer.WritePropertyName(member.Name);
        writer.WriteRawValue(member.Value.GetRawText(), skipInputValidation: true);
    }
}
