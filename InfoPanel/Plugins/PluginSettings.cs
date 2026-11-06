using System.Text.Json;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Spec;

namespace InfoPanel.Plugins;

/// <summary>
/// A plugin's settings as the API answers them (<c>pluginSetting</c>), and the checks a change passes before its plugin is
/// asked to apply it (spec/docs/api.md "Plugin settings"). No message here ever holds a value: settings may be secrets.
/// </summary>
public static class PluginSettings
{
    /// <summary>The longest text a <c>string</c> setting takes, in characters.</summary>
    public const int StringChars = 4096;

    /// <summary>The API's type for a setting's SDK type (<c>PluginConfigType</c>'s name), or null for one it doesn't know.</summary>
    public static string? TypeOf(string? sdkType) => sdkType switch
    {
        "String" => "string",
        "Integer" => "integer",
        "Double" => "number",
        "Boolean" => "boolean",
        "Choice" => "choice",
        _ => null,
    };

    /// <summary>Every setting the host answered, as the API gives them; one of a type the API doesn't know is left out.</summary>
    public static List<PluginSettingBody> Bodies(IEnumerable<PluginConfigPropertyDto>? properties) =>
        [.. (properties ?? []).Where(p => p != null && !string.IsNullOrEmpty(p.Key) && TypeOf(p.Type) != null).Select(Body)];

    private static PluginSettingBody Body(PluginConfigPropertyDto p) => new()
    {
        Key = p.Key,
        Name = p.DisplayName ?? "",
        Description = p.Description,
        Type = TypeOf(p.Type)!,
        Value = ValueOf(p.Value),
        Min = Finite(p.MinValue),
        Max = Finite(p.MaxValue),
        Step = Finite(p.Step),
        Options = p.Options?.Where(o => o != null).ToList(),
    };

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    /// <summary>A value as the host sent it (over JSON-RPC: a JSON token, or a number, text or boolean), as a JSON string, number or boolean; anything else null.</summary>
    private static JsonElement? ValueOf(object? value)
    {
        JsonElement element;
        try
        {
            element = value switch
            {
                null => default,
                Newtonsoft.Json.Linq.JToken token => JsonDocument.Parse(token.ToString(Newtonsoft.Json.Formatting.None)).RootElement.Clone(),
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(value, value.GetType()),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String or JsonValueKind.True or JsonValueKind.False => element,
            JsonValueKind.Number when element.TryGetDouble(out var d) && double.IsFinite(d) => element,
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="setting"/> takes <paramref name="value"/>: null and the value to send the plugin (a text, a
    /// whole number as an <see cref="int"/>, a number as a <see cref="double"/>, a boolean), or why not, never quoting it.
    /// </summary>
    public static (object? Value, string? Problem) Check(PluginSettingBody setting, JsonElement value)
    {
        var key = setting.Key;
        switch (setting.Type)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                    return (null, $"The setting {key} takes text.");
                var text = value.GetString()!;
                return text.EnumerateRunes().Count() > StringChars
                    ? (null, $"The setting {key} takes text of at most {StringChars} characters.")
                    : (text, null);

            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var whole) || !double.IsFinite(whole)
                    || whole != Math.Floor(whole) || whole < int.MinValue || whole > int.MaxValue)
                    return (null, $"The setting {key} takes a whole number from {int.MinValue} to {int.MaxValue}.");
                return Range(setting, whole) ?? ((int)whole, null);

            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    return (null, $"The setting {key} takes a finite number.");
                return Range(setting, number) ?? (number, null);

            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (value.GetBoolean(), null)
                    : (null, $"The setting {key} takes true or false.");

            case "choice":
                return value.ValueKind == JsonValueKind.String && (setting.Options ?? []).Contains(value.GetString()!, StringComparer.Ordinal)
                    ? (value.GetString(), null)
                    : (null, $"The setting {key} takes one of its options.");

            default:
                return (null, $"The setting {key} can't be changed.");
        }
    }

    private static (object?, string?)? Range(PluginSettingBody setting, double value) =>
        setting.Min is { } min && value < min || setting.Max is { } max && value > max
            ? (null, $"The setting {setting.Key} takes a value from {Show(setting.Min)} to {Show(setting.Max)}.")
            : null;

    private static string Show(double? bound) =>
        bound is { } b ? b.ToString(System.Globalization.CultureInfo.InvariantCulture) : "any";
}
