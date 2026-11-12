using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoPanel.Panels;

/// <summary>
/// A family's own settings: read from its <c>panel.json</c> (<see cref="Parse"/>), a panel's values checked against them
/// (<see cref="Apply"/>: an unknown key, a value of the wrong type, out of range or not one of its values is refused),
/// kept in <c>devices.json</c> as the values set (a setting not set takes its default, which the driver reads from its own
/// <c>panel.json</c>), and handed to the driver's <c>Open</c> as <c>PanelOpenOptions.FamilySettings</c>. Every change
/// closes the panel's session and opens it again with them.
/// </summary>
public static partial class PanelFamilySettings
{
    /// <summary>The most settings a family declares.</summary>
    public const int MaxSettings = 32;

    /// <summary>The most values an <c>enum</c> setting has.</summary>
    public const int MaxValues = 64;

    /// <summary>The longest <c>string</c> setting, in characters.</summary>
    public const int MaxStringChars = 64;

    /// <summary>The types a setting may have.</summary>
    public static readonly IReadOnlyList<string> Types = ["boolean", "enum", "integer", "string", "model"];

    [GeneratedRegex(@"\A[a-z][A-Za-z0-9]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// A family's <c>settings</c> from its <c>panel.json</c> (<paramref name="at"/> names the family, for the error): at most
    /// <see cref="MaxSettings"/>, each <c>id</c> (a lower-case letter, then at most 31 letters and digits; unique),
    /// <c>name</c> (1 to 64 characters), <c>type</c> (<see cref="Types"/>), <c>default</c> of its type (an <c>enum</c>'s one
    /// of its <c>values</c>, each <c>id</c> and <c>name</c> of 1 to 64 characters; an <c>integer</c>'s within its
    /// <c>min</c> and <c>max</c>; a <c>model</c>'s null, its id <c>model</c>, once), and an optional <c>v1Field</c> (1 to 64).
    /// Null, with <paramref name="error"/>, for anything else.
    /// </summary>
    public static IReadOnlyList<PanelFamilySetting>? Parse(JsonElement declared, string at, out string? error)
    {
        error = null;
        if (declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() > MaxSettings)
            return Fail($"{at}.settings isn't a list of at most {MaxSettings}", out error);
        var settings = new List<PanelFamilySetting>();
        foreach (var setting in declared.EnumerateArray())
        {
            var here = $"{at}.settings[{settings.Count}]";
            if (setting.ValueKind != JsonValueKind.Object || Text(setting, "id") is not { } id || !IdPattern().IsMatch(id))
                return Fail($"{here}.id isn't a lower-case letter followed by at most 31 letters and digits", out error);
            if (settings.Any(s => s.Id == id))
                return Fail($"{at}.settings has {id} twice", out error);
            if (Name(setting, "name") is not { } name)
                return Fail($"{here}.name isn't 1 to 64 characters", out error);
            if (Text(setting, "type") is not { } type || !Types.Contains(type))
                return Fail($"{here}.type isn't one of {string.Join(", ", Types)}", out error);
            string? v1Field = null;
            if (setting.TryGetProperty("v1Field", out var field) && (v1Field = Name(setting, "v1Field")) == null)
                return Fail($"{here}.v1Field isn't 1 to 64 characters", out error);
            setting.TryGetProperty("default", out var fallback);
            var parsed = new PanelFamilySetting(id, name, type, fallback.ValueKind == JsonValueKind.Undefined ? Null : fallback.Clone()) { V1Field = v1Field };
            switch (type)
            {
                case "boolean" when fallback.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    return Fail($"{here}.default isn't true or false", out error);
                case "string" when fallback.ValueKind != JsonValueKind.String || fallback.GetString()!.EnumerateRunes().Count() > MaxStringChars:
                    return Fail($"{here}.default isn't a string of at most {MaxStringChars} characters", out error);
                case "model" when id != "model" || fallback.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null):
                    return Fail($"{here} is a model setting: its id is model and its default null", out error);
                case "enum":
                    if (!setting.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is 0 or > MaxValues)
                        return Fail($"{here}.values isn't a list of 1 to {MaxValues}", out error);
                    var list = new List<PanelFamilySettingValue>();
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Object || Name(value, "id") is not { } valueId || Name(value, "name") is not { } valueName || list.Any(v => v.Id == valueId))
                            return Fail($"{here}.values has a value without an id and a name of 1 to 64 characters, or an id twice", out error);
                        list.Add(new PanelFamilySettingValue(valueId, valueName));
                    }
                    if (fallback.ValueKind != JsonValueKind.String || list.All(v => v.Id != fallback.GetString()))
                        return Fail($"{here}.default isn't one of its values", out error);
                    parsed = parsed with { Values = list };
                    break;
                case "integer":
                    if (Int(setting, "min") is not { } min || Int(setting, "max") is not { } max || min > max)
                        return Fail($"{here} has no integer min and max, min no more than max", out error);
                    if (Integer(fallback) is not { } number || number < min || number > max)
                        return Fail($"{here}.default isn't an integer from its min to its max", out error);
                    parsed = parsed with { Default = IntegerElement(number), Min = min, Max = max };
                    break;
            }
            settings.Add(parsed);
        }
        return settings;
    }

    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    private static IReadOnlyList<PanelFamilySetting>? Fail(string why, out string? error)
    {
        error = why;
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Name(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 } text && text.EnumerateRunes().Count() <= 64 ? text : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? Integer(value) : null;

    /// <summary>A refused member of a change: its key and why.</summary>
    public sealed record Refusal(string Key, string Message);

    /// <summary>
    /// <paramref name="change"/> (an object: each member a setting's id and its new value, or null to put it back to its
    /// default) over <paramref name="saved"/> (the values set, as <c>devices.json</c> keeps them; null: none), checked
    /// against the family's <paramref name="settings"/> and <paramref name="models"/> (a <c>model</c> setting's values):
    /// the values set after it (null: none), or the refusals and nothing changed. An integer is kept as the integer however
    /// it was spelled (<c>5e1</c>, <c>50.0</c>: 50). The declared settings' values come first, in the family's order; a
    /// stored value of a setting the family no longer declares is kept after them, as it was, until a change removes it
    /// (a package update doesn't lose it), or sets it to null (the one value taken for a setting the family doesn't
    /// declare). A change that changes nothing (an empty one, the same values) gives
    /// <paramref name="saved"/> back as it was, so it reopens nothing.
    /// </summary>
    public static string? Apply(string? saved, JsonElement change, IReadOnlyList<PanelFamilySetting> settings, IReadOnlyCollection<string> models, out IReadOnlyList<Refusal> refused)
    {
        var refusals = new List<Refusal>();
        var before = Read(saved);
        var values = new Dictionary<string, JsonElement>(before, StringComparer.Ordinal);
        foreach (var member in change.EnumerateObject())
        {
            if (settings.FirstOrDefault(s => s.Id == member.Name) is not { } setting)
            {
                // A value kept of a setting the family no longer declares goes with null; nothing else is taken for it.
                if (member.Value.ValueKind == JsonValueKind.Null && values.Remove(member.Name))
                    continue;
                refusals.Add(new Refusal(member.Name, $"The family has no setting '{member.Name}'."));
                continue;
            }
            var value = member.Value;
            if (value.ValueKind == JsonValueKind.Null)
            {
                values.Remove(member.Name);
                continue;
            }
            if (Problem(setting, value, models) is { } problem)
            {
                refusals.Add(new Refusal(member.Name, problem));
                continue;
            }
            values[member.Name] = setting.Type == "integer" ? IntegerElement(Integer(value)!.Value) : value.Clone();
        }
        // The values kept of settings no longer declared count too: past the limit the change is refused, nothing changed.
        if (refusals.Count == 0 && values.Count > MaxSettings && values.Count > before.Count)
        {
            var added = change.EnumerateObject().Select(m => m.Name).FirstOrDefault(k => !before.ContainsKey(k) && values.ContainsKey(k)) ?? "";
            refusals.Add(new Refusal(added, $"A panel keeps at most {MaxSettings} family settings' values, those of settings its family no longer declares included: put one back to its default (null) first."));
        }
        refused = refusals;
        if (refusals.Count > 0)
            return saved;
        if (values.Count == before.Count && values.All(v => before.TryGetValue(v.Key, out var was) && JsonElement.DeepEquals(was, v.Value)))
            return saved; // nothing changed
        // The declared ones in the family's order, so the file reads the same whatever order the changes came in; then
        // the ones it no longer declares, in their stored order.
        var ordered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var setting in settings)
            if (values.TryGetValue(setting.Id, out var value))
                ordered[setting.Id] = value;
        foreach (var (key, value) in values)
            ordered.TryAdd(key, value);
        return Write(ordered);
    }

    /// <summary>A JSON number as the integer it is, however it is spelled (<c>5e1</c>, <c>50.0</c>; the format's numbers, <c>SpecJson.ReadInt32</c>); null for anything else.</summary>
    public static int? Integer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return null;
        try
        {
            return InfoPanel.Spec.SpecJson.ReadInt32(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement IntegerElement(int number)
    {
        using var json = JsonDocument.Parse(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return json.RootElement.Clone();
    }

    /// <summary>Why <paramref name="value"/> isn't one <paramref name="setting"/> takes, or null when it is.</summary>
    public static string? Problem(PanelFamilySetting setting, JsonElement value, IReadOnlyCollection<string> models) => setting.Type switch
    {
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"'{setting.Id}' is true or false.",
        "enum" => value.ValueKind == JsonValueKind.String && setting.Values.Any(v => v.Id == value.GetString()) ? null
            : $"'{setting.Id}' is one of {string.Join(", ", setting.Values.Select(v => v.Id))}.",
        "integer" => Integer(value) is { } number && number >= setting.Min && number <= setting.Max ? null
            : $"'{setting.Id}' is an integer from {setting.Min} to {setting.Max}.",
        "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.EnumerateRunes().Count() <= MaxStringChars ? null
            : $"'{setting.Id}' is a string of at most {MaxStringChars} characters.",
        "model" => value.ValueKind == JsonValueKind.String && models.Contains(value.GetString()!) ? null
            : $"'{setting.Id}' is one of the family's models.",
        _ => $"'{setting.Id}' has a type this engine doesn't know.",
    };

    /// <summary>The values set, by id, from their stored JSON object (null or anything else: none).</summary>
    public static Dictionary<string, JsonElement> Read(string? saved)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (saved == null)
            return values;
        try
        {
            using var json = JsonDocument.Parse(saved);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var member in json.RootElement.EnumerateObject())
                    values[member.Name] = member.Value.Clone();
        }
        catch (JsonException)
        {
        }
        return values;
    }

    /// <summary>The values as the stored JSON object, compact; null when there are none.</summary>
    public static string? Write(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count == 0)
            return null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The values set as a JSON object for the driver's <c>Open</c>, each stored value checked against the setting the
    /// family declares now: one it doesn't take (a setting retired and declared again with another type) is the declared
    /// default for the session, its id in <paramref name="mismatched"/>, the stored value left as it is; a value of a
    /// setting the family doesn't declare goes as stored. An empty object when none.
    /// </summary>
    public static JsonElement ForOpen(string? saved, IReadOnlyList<PanelFamilySetting> settings, IReadOnlyCollection<string> models, out IReadOnlyList<string> mismatched)
    {
        var wrong = new List<string>();
        var values = Read(saved);
        foreach (var setting in settings)
        {
            if (!values.TryGetValue(setting.Id, out var value) || Problem(setting, value, models) is null)
                continue;
            wrong.Add(setting.Id);
            if (setting.Default.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                values.Remove(setting.Id);
            else
                values[setting.Id] = setting.Default;
        }
        mismatched = wrong;
        return ElementOf(wrong.Count == 0 ? saved : Write(values));
    }

    /// <summary>The values set as a JSON object for the driver's <c>Open</c> (an empty object when none).</summary>
    public static JsonElement ElementOf(string? saved)
    {
        using var json = JsonDocument.Parse(saved ?? "{}");
        return json.RootElement.Clone();
    }
}
