using System.Text.Json;

namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// The family's settings for one panel (InfoPanel 1.4's per-device <c>DisplayMask</c> and <c>FlickerFix</c>, and the
/// model a panel is driven as): <see cref="Mask"/>, the overlay over a 6.67" panel's camera punch-hole (only the models
/// with <c>displayMask</c> draw it); <see cref="FlickerFix"/>, a 9.16" TrofeoBulk frame sent as its top 462 rows (only
/// where the start's detection offers the crop); <see cref="Model"/>, a model id of the family that the panel is driven
/// as, whatever its ids and reply say (a panel whose reply names another size; or one whose reply names no model).
/// </summary>
public sealed record ThermalrightSettings(ThermalrightMask Mask = ThermalrightMask.None, bool FlickerFix = false, string? Model = null)
{
    public const string MaskKey = "displayMask";
    public const string FlickerFixKey = "flickerFix";
    public const string ModelKey = "model";

    public static ThermalrightSettings Default { get; } = new();

    /// <summary>
    /// The settings in a family-settings object (the engine has checked them against <c>panel.json</c>'s <c>settings</c>;
    /// anything missing or of another type reads as its default): <c>displayMask</c> <c>None</c>, <c>RoundedLeft</c> or
    /// <c>RoundedAll</c> (1.4's own names, the values <c>panel.json</c> declares, so a 1.x panel's <c>DisplayMask</c> is
    /// one as it stands; read in any case), <c>flickerFix</c> true or false, <c>model</c> a model id (empty: none).
    /// </summary>
    public static ThermalrightSettings Read(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            return Default;
        var mask = settings.TryGetProperty(MaskKey, out var m) && m.ValueKind == JsonValueKind.String ? MaskOf(m.GetString()) : ThermalrightMask.None;
        var flickerFix = settings.TryGetProperty(FlickerFixKey, out var f) && f.ValueKind == JsonValueKind.True;
        var model = settings.TryGetProperty(ModelKey, out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } text ? text : null;
        return new ThermalrightSettings(mask, flickerFix, model);
    }

    /// <summary>A display mask by its name (<c>roundedLeft</c> or 1.4's <c>RoundedLeft</c>); anything else is none.</summary>
    public static ThermalrightMask MaskOf(string? name) => name?.ToLowerInvariant() switch
    {
        "roundedleft" => ThermalrightMask.RoundedLeft,
        "roundedall" => ThermalrightMask.RoundedAll,
        _ => ThermalrightMask.None,
    };
}
