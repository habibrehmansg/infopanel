using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using SkiaSharp;
using System.Reflection;

namespace InfoPanel.Api;

/// <summary>
/// The engine's session, fonts and sensors (spec/docs/api.md, "Session", "Fonts and sensors"). Everything here is under
/// <c>/api</c> and needs a token with the <c>read</c> scope. Bodies are the spec model (<c>session</c>, <c>sensorEntry</c>,
/// <c>sensorValue</c>), written with the format's JSON options like every other API body.
/// </summary>
public static class ReadEndpoints
{
    /// <summary>The engine API's version (spec/docs/api.md): 2 since documents are the 2.0 format.</summary>
    public const int Protocol = 2;

    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", (DataPaths paths, Workspace workspace, RegionalFormat regional) => ApiResults.Json(new Session
        {
            EngineVersion = EngineVersion(),
            Protocol = Protocol,
            Instance = workspace.Instance,
            Features = Features,
            Platform = Platform(),
            DataDir = paths.Root,
            // The PC's regional format, read once at start-up (the format a profile without a locale draws with).
            Locale = new SessionLocale
            {
                Tag = regional.Tag,
                DecimalSeparator = regional.DecimalSeparator,
                GroupSeparator = regional.GroupSeparator,
            },
            FormatSupport = FormatSupportOf(workspace),
        })).RequireScope(ApiScope.Read);

        api.MapGet("/fonts", () => ApiResults.Json(Fonts())).RequireScope(ApiScope.Read);

        api.MapGet("/sensors", (SensorHub hub) => ApiResults.Json(
            hub.GetSensors()
                .Select(s => new SensorEntry
                {
                    Key = s.Key.ToString(),
                    Source = s.SourceId,
                    Group = s.Group,
                    Name = s.Name,
                    Unit = s.Unit,
                    Status = hub.GetStatus(s.Key),
                })
                .ToList())).RequireScope(ApiScope.Read);

        api.MapGet("/sensors/values", (int? history, SensorHub hub, SensorHistory sensorHistory) =>
        {
            var count = Math.Clamp(history ?? 0, 0, SensorHistory.MaxSamples);
            var values = new List<SensorValue>();
            foreach (var sensor in hub.GetSensors())
            {
                // The status, type and reading from one call into the source: a poll between two calls could pair a stale value with live.
                var state = hub.ReadState(sensor.Key);
                if (!state.HasReading)
                    continue;

                var reading = state.Reading;
                var text = reading.ValueTable != null || reading.ValueText != null ? reading.ValueText ?? "" : null;
                IReadOnlyList<double>? samples = null;
                if (count > 0 && text == null)
                {
                    var all = sensorHistory.GetSamples(sensor.Key);
                    samples = all.Skip(Math.Max(0, all.Count - count)).ToList();
                }

                var status = state.Status is SensorStatus.Live ? SensorStatus.Live : SensorStatus.Stale;
                values.Add(new SensorValue
                {
                    Key = sensor.Key.ToString(),
                    Now = Finite(reading.ValueNow),
                    Min = Finite(reading.ValueMin),
                    Max = Finite(reading.ValueMax),
                    Avg = Finite(reading.ValueAvg),
                    Unit = reading.Unit ?? "",
                    Text = text,
                    History = samples,
                    Status = status,
                });
            }

            return ApiResults.Json(values);
        }).RequireScope(ApiScope.Read);
    }

    /// <summary>
    /// The optional API features the engine has (spec/docs/api.md, "Session"): every operation added after this version of
    /// the API comes under a name here. None yet.
    /// </summary>
    public static IReadOnlyList<string> Features { get; } = [];

    /// <summary>What the engine reads, by format string: each format's newest minor version, its capabilities the engine has and its limits.</summary>
    public static IReadOnlyDictionary<string, SupportedFormat> FormatSupportOf(Workspace workspace) =>
        new SortedDictionary<string, SupportedFormat>(StringComparer.Ordinal)
        {
            [SpecSchemas.DocumentFormat] = Supported(workspace, SpecSchemas.DocumentFormat, "1.0", workspace.Support.DocumentCapabilities),
            [SpecSchemas.SceneFormat] = Supported(workspace, SpecSchemas.SceneFormat, "1.0", workspace.Support.SceneCapabilities),
        };

    private static SupportedFormat Supported(Workspace workspace, string format, string version, IReadOnlySet<string> capabilities) => new()
    {
        Version = version,
        Capabilities = [.. capabilities.Order(StringComparer.Ordinal)],
        Limits = workspace.Limits.ToDictionary(format),
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
