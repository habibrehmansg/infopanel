using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>The package's <c>panel.json</c>, read once: the families' one table of matches and models, and what the drivers read of a model.</summary>
internal static class Ms9132JonsboManifest
{
    private static readonly Lazy<PanelManifest> Read = new(() => PanelManifest.Read(typeof(Ms9132JonsboManifest).Assembly));

    public static PanelManifestFamily Jonsbo => Read.Value.Family(JonsboDriver.FamilyId);

    public static PanelManifestFamily Vmax => Read.Value.Family(VmaxDriver.FamilyId);

    /// <summary>A model's <c>transport</c>: <c>serial</c> (the DS916) or <c>ms9132</c>.</summary>
    public static string? TransportOf(PanelManifestModel model) => Text(model, "transport");

    /// <summary>Whether <paramref name="model"/> is driven over the MS9132.</summary>
    public static bool OnMs9132(PanelManifestModel model) => TransportOf(model) == "ms9132";

    /// <summary>The family's first model on <paramref name="device"/>'s USB id and over <paramref name="transport"/>, or null.</summary>
    public static PanelManifestModel? ModelOf(PanelManifestFamily family, PanelCandidate device, string transport)
    {
        var usb = $"{device.VendorId:x4}:{device.ProductId:x4}";
        return family.Models.FirstOrDefault(m => Text(m, "usb") == usb && TransportOf(m) == transport);
    }

    /// <summary>The family's MS9132 model of a panel <paramref name="width"/> × <paramref name="height"/> (its EDID's size), or null.</summary>
    public static PanelManifestModel? Ms9132ModelOf(PanelManifestFamily family, int width, int height) =>
        family.Models.FirstOrDefault(m => OnMs9132(m) && m.Width == width && m.Height == height);

    /// <summary>A model's mode index (<c>vic</c>), or null.</summary>
    public static byte? VicOf(PanelManifestModel model) =>
        model.Json.TryGetProperty("vic", out var vic) && vic.ValueKind == JsonValueKind.Number && vic.TryGetByte(out var value) ? value : null;

    /// <summary>The model the family setting <c>model</c> names ("Try it as…"), or null.</summary>
    public static PanelManifestModel? TriedAs(PanelManifestFamily family, JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? family.Model(model.GetString()!) : null;

    private static string? Text(PanelManifestModel model, string name) =>
        model.Json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
