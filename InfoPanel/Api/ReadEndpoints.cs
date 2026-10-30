using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using SkiaSharp;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace InfoPanel.Api;

/// <summary>
/// The engine's session, fonts and sensors (spec/docs/api.md, "Session", "Fonts and sensors"). Everything here is under
/// <c>/api</c> and needs a token with the <c>read</c> scope. Bodies are the spec model (<c>session</c>, <c>sensorEntry</c>,
/// <c>sensorValue</c>), written with the format's JSON options like every other API body.
/// </summary>
public static partial class ReadEndpoints
{
    /// <summary>The engine API's version (spec/docs/api.md): 2 since documents are the 2.0 format.</summary>
    public const int Protocol = 2;

    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", (DataPaths paths, Workspace workspace, RegionalFormat regional, EngineRuntime runtime, EngineOptions options, SceneRuntimeOptions scenes) => ApiResults.Json(new Session
        {
            EngineVersion = EngineVersion(),
            EngineBuild = EngineBuild(),
            Protocol = Protocol,
            Instance = workspace.Instance,
            Started = Timestamp(runtime.Started),
            Features = Features,
            Platform = Platform(),
            OsVersion = OsVersion(),
            Arch = Arch(),
            DataDir = paths.Root,
            // Mock mode (--mock, --mock-seed): simulated sensors, for the run.
            Mock = options.Mock,
            // The PC's regional format, read once at start-up (the format a profile without a locale draws with).
            Locale = new SessionLocale
            {
                Tag = regional.Tag,
                DecimalSeparator = regional.DecimalSeparator,
                GroupSeparator = regional.GroupSeparator,
            },
            // The media providers configured on the engine, by name (spec/docs/providers.md).
            Media = new SessionMedia { Providers = [.. scenes.Providers.Keys.Order(StringComparer.Ordinal)] },
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
            [SpecSchemas.ArchiveFormat] = Supported(workspace, SpecSchemas.ArchiveFormat, "1.0", InfoPanel.Import.ProfileArchive.Capabilities),
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

    /// <summary>The assembly metadata key the build is recorded under (InfoPanel/EngineBuild.targets).</summary>
    internal const string BuildMetadataKey = "InfoPanel.Build";

    private static readonly string Build = BuildOf(typeof(ReadEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == BuildMetadataKey)?.Value);

    private static readonly string Os = ReadOsVersion(Platform(), () => Environment.OSVersion.Version, LinuxRelease);

    /// <summary>
    /// The engine's build (spec/docs/api.md, "Session"): the abbreviated commit it was built from, <c>-dirty</c> when the
    /// build had uncommitted changes, as the build recorded it (InfoPanel/EngineBuild.targets); <c>unknown</c> when it recorded none.
    /// </summary>
    internal static string EngineBuild() => Build;

    /// <summary>A recorded build as the session reports it: a commit id with or without <c>-dirty</c>, else <c>unknown</c>.</summary>
    internal static string BuildOf(string? recorded) => recorded is not null && BuildPattern().IsMatch(recorded) ? recorded : "unknown";

    [GeneratedRegex(@"^[0-9a-f]{7,64}(-dirty)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();

    /// <summary>The operating system's version as it reports it (spec/docs/api.md, "Session"), read once.</summary>
    internal static string OsVersion() => Os;

    /// <summary>
    /// Reads the operating system's version (<paramref name="version"/>: .NET's <c>Environment.OSVersion</c>; on Linux
    /// also <paramref name="linuxRelease"/>, the kernel release) and never throws: a query that fails counts as no answer,
    /// so the session reports <c>unknown</c> (on Linux, whichever of the two did answer) rather than failing.
    /// </summary>
    internal static string ReadOsVersion(string platform, Func<Version> version, Func<string?> linuxRelease)
    {
        var release = platform == "linux" ? Answer(linuxRelease) : null;
        return OsVersionOf(platform, Answer(version), release);

        static T? Answer<T>(Func<T> query) where T : class?
        {
            try
            {
                return query();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The operating system's version as it writes it. <paramref name="version"/> is .NET's <c>Environment.OSVersion</c>:
    /// macOS's product version (major.minor.patch; the Mac writes 26.1 for 26.1.0) and Windows's major.minor.build.revision
    /// (Windows writes 10.0.26100). Linux's is its kernel release as the kernel writes it (<paramref name="linuxRelease"/>),
    /// or the version .NET read from it when that isn't usable. <c>unknown</c> without a version (null: none could be read).
    /// </summary>
    internal static string OsVersionOf(string platform, Version? version, string? linuxRelease)
    {
        if (platform == "linux" && linuxRelease?.Trim() is { Length: > 0 and <= 64 } release && release.All(c => c is > ' ' and <= '~'))
            return release;
        if (version is null || (version.Major <= 0 && version.Minor <= 0))
            return "unknown";
        return platform switch
        {
            "macos" => version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}",
            "windows" => version.Build >= 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}",
            _ => version.ToString(),
        };
    }

    /// <summary>The kernel release (<c>uname -r</c>), or null when it can't be read.</summary>
    private static string? LinuxRelease()
    {
        try
        {
            return File.ReadAllText("/proc/sys/kernel/osrelease");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The processor architecture the engine runs as (spec/docs/api.md, "Session").</summary>
    internal static string Arch() => ArchOf(RuntimeInformation.ProcessArchitecture);

    internal static string ArchOf(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => "other",
    };

    /// <summary>The schema's <c>timestamp</c>: RFC 3339 in UTC, to the second (cut, not rounded).</summary>
    internal static string Timestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
