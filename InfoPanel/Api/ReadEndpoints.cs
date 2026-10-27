using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Persistence;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using SkiaSharp;
using System.Reflection;

namespace InfoPanel.Api;

/// <summary>A listed sensor; <c>status</c> is its <see cref="SensorStatus"/> now. A binding names it by <c>key</c>.</summary>
public sealed record SensorListEntry(string Key, string Source, string Group, string Name, string Unit, SensorStatus Status);

/// <summary>A listed sensor that has a reading; <c>status</c> is <c>live</c> or <c>stale</c> (the last value).</summary>
public sealed record SensorValue(string Key, double Now, double Min, double Max, double Avg, string Unit, string? Text, IReadOnlyList<double>? History, SensorStatus Status);

/// <summary>
/// The engine's session, fonts and sensors (spec/docs/api.md, "Session"). Everything here is under <c>/api</c> and
/// needs a token with the <c>read</c> scope.
/// </summary>
public static class ReadEndpoints
{
    /// <summary>The engine API's version (spec/docs/api.md): 2 since documents are the 2.0 format.</summary>
    public const int Protocol = 2;

    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", (DataPaths paths, Workspace workspace) => ApiResults.Json(new Session
        {
            EngineVersion = EngineVersion(),
            Protocol = Protocol,
            Instance = workspace.Instance,
            Platform = Platform(),
            DataDir = paths.Root,
            FormatSupport = FormatSupportOf(workspace),
        })).RequireScope(ApiScope.Read);

        api.MapGet("/fonts", () => Fonts()).RequireScope(ApiScope.Read);

        api.MapGet("/sensors", (SensorHub hub) =>
            hub.GetSensors()
                .Select(s => new SensorListEntry(s.Key.ToString(), s.SourceId, s.Group, s.Name, s.Unit, hub.GetStatus(s.Key)))
                .ToList()).RequireScope(ApiScope.Read);

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

                // A reading that aged out between the read and this check is reported as stale, the state it was read in.
                var status = hub.GetStatus(sensor.Key) is SensorStatus.Live ? SensorStatus.Live : SensorStatus.Stale;
                values.Add(new SensorValue(sensor.Key.ToString(), Finite(reading.ValueNow), Finite(reading.ValueMin),
                    Finite(reading.ValueMax), Finite(reading.ValueAvg), reading.Unit ?? "", text, samples, status));
            }

            return values;
        }).RequireScope(ApiScope.Read);
    }

    /// <summary>What the engine reads: the formats, their newest minor version, the capabilities it has and its limits.</summary>
    public static FormatSupport FormatSupportOf(Workspace workspace) => new()
    {
        Document = new FormatVersion { Formats = ["infopanel.document/1"], Version = "1.0", Capabilities = [.. workspace.Support.DocumentCapabilities.Order(StringComparer.Ordinal)] },
        Scene = new FormatVersion { Formats = ["infopanel.scene/1"], Version = "1.0", Capabilities = [.. workspace.Support.SceneCapabilities.Order(StringComparer.Ordinal)] },
        Limits = workspace.Limits.ToDictionary(),
    };

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
