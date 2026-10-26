using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Persistence;
using InfoPanel.Security;
using InfoPanel.Sensors;
using SkiaSharp;
using System.Reflection;
using System.Text.Json.Nodes;

namespace InfoPanel.Api;

public sealed record SessionInfo(string EngineVersion, int Protocol, string DataDir, string Platform);

public sealed record SensorListEntry(string Key, string Source, string Group, string Name, string Unit, JsonObject Binding);

public sealed record SensorValue(string Key, double Now, double Min, double Max, double Avg, string Unit, string? Text, IReadOnlyList<double>? History);

/// <summary>The read side of the REST API (plan §6.3). Everything here is under <c>/api</c> and needs the token.</summary>
public static class ReadEndpoints
{
    public const int Protocol = 1;
    public const int DefaultThumbnailWidth = 320;

    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", (DataPaths paths) =>
            new SessionInfo(EngineVersion(), Protocol, paths.Root, Platform()));

        api.MapGet("/kinds", () => ItemKinds.BuildManifest());

        api.MapGet("/fonts", () => Fonts());

        api.MapGet("/sensors", (SensorHub hub) =>
            hub.GetSensors()
                .Select(s => new SensorListEntry(s.Key.ToString(), s.SourceId, s.Group, s.Name, s.Unit, ItemJson.BindingProps(s.Key, s.Name)))
                .ToList());

        api.MapGet("/sensors/values", (int? history, SensorHub hub, SensorHistory sensorHistory) =>
        {
            var count = Math.Clamp(history ?? 0, 0, SensorHistory.MaxSamples);
            var values = new List<SensorValue>();
            foreach (var sensor in hub.GetSensors())
            {
                if (!hub.TryRead(sensor.Key, out var reading))
                    continue;

                var text = reading.ValueTable != null || reading.ValueText != null ? reading.ValueText ?? "" : null;
                IReadOnlyList<double>? samples = null;
                if (count > 0 && text == null)
                {
                    var all = sensorHistory.GetSamples(sensor.Key);
                    samples = all.Skip(Math.Max(0, all.Count - count)).ToList();
                }

                values.Add(new SensorValue(sensor.Key.ToString(), Finite(reading.ValueNow), Finite(reading.ValueMin),
                    Finite(reading.ValueMax), Finite(reading.ValueAvg), reading.Unit ?? "", text, samples));
            }

            return values;
        });

        api.MapGet("/profiles", (Workspace workspace) => workspace.ListProfiles());

        api.MapGet("/profiles/{id}", (string id, Workspace workspace, SensorHub hub) =>
            Open(workspace, id) is { } doc ? Results.Ok(doc.ToDoc(hub)) : Problems.NotFound($"No profile '{id}'."));

        api.MapGet("/profiles/{id}/thumbnail", (string id, int? w, Workspace workspace, ThumbnailRenderer thumbnails, HttpContext context) =>
        {
            if (Open(workspace, id) is not { } doc)
                return Problems.NotFound($"No profile '{id}'.");

            var png = thumbnails.RenderPng(doc, w ?? DefaultThumbnailWidth);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(png, "image/png");
        });
    }

    private static ProfileDocument? Open(Workspace workspace, string id) =>
        Guid.TryParse(id, out var guid) ? workspace.Open(guid) : null;

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>Registered families (bundled Inter, <c>{data}/fonts</c>) first, then the system's, each sorted, without duplicates.</summary>
    internal static List<string> Fonts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var family in FontRegistry.GetFamilies().Order(StringComparer.OrdinalIgnoreCase)
                     .Concat(SKFontManager.Default.GetFontFamilies().Order(StringComparer.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith('.') && seen.Add(family))
                result.Add(family);
        }

        return result;
    }

    internal static string EngineVersion()
    {
        var version = typeof(ReadEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ReadEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    internal static string Platform() =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
}
