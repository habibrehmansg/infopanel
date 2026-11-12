using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using InfoPanel.Spec;
using SkiaSharp;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace InfoPanel.Api;

/// <summary>
/// The engine's session, fonts and sensors (spec/docs/api.md, "Session", "Fonts and sensors"). Everything here is under
/// <c>/api</c> and needs a token with the <c>read</c> scope. Bodies are the spec model (<c>session</c>, <c>sensorEntry</c>,
/// <c>sensorValue</c>), written with the format's JSON options like every other API body. One action is the session
/// token's: installing the PawnIO driver (<c>POST /api/sensors/sources/libre/driver</c>, spec/docs/api.md "The PawnIO
/// driver") and reading all sensors (<c>POST</c> and <c>DELETE /api/sensors/sources/libre/elevation</c>, "Reading all
/// sensors"), beside the sources' states they change.
/// </summary>
public static partial class ReadEndpoints
{
    /// <summary>The engine API's version (spec/docs/api.md): 2 since documents are the 2.0 format.</summary>
    public const int Protocol = 2;

    /// <summary>
    /// The PawnIO driver as the Libre source and the bundled installer have it now: <c>installing</c> while an install runs
    /// and after one that ended well until the source has read the driver again and its new helper said hello, read in the
    /// order that can't give the state from before the install (<see cref="PawnIoInstaller.Report"/>).
    /// </summary>
    private static LibreDriverBody DriverOf(LibreSource? libre, PawnIoInstaller? installer)
    {
        var driver = PawnIoInstaller.Report(installer, () => libre?.DriverSettling == true,
            () => libre?.Driver ?? new PawnIoReading(PawnIoDriver.Unknown, null));
        return new LibreDriverBody
        {
            State = driver.State,
            Version = driver.Version,
            Installer = installer?.Available,
            LastInstall = installer?.LastInstall is { } last ? new LibreDriverInstall { Outcome = last.Outcome, Code = last.Code } : null,
        };
    }

    /// <summary>"Read all sensors" as the Libre source's elevation has it now.</summary>
    private static LibreElevationBody ElevationOf(LibreElevation elevation)
    {
        var report = elevation.Report();
        return new LibreElevationBody
        {
            State = report.State,
            Reason = report.Reason,
            LastChange = report.LastChange is { } last ? new LibreElevationChangeBody { Outcome = last.Outcome, Code = last.Code } : null,
            Fallback = report.Fallback is { } fallback ? new SensorSourceReason { Code = fallback.Code, Message = fallback.Message } : null,
        };
    }

    /// <summary>Sets up (<paramref name="on"/>) or removes the sensor helper's task: 202 with the state <c>settingUp</c>; 409 while a change runs, or when there is nothing to set up here.</summary>
    private static IResult ChangeElevation(IServiceProvider services, bool on)
    {
        var elevation = services.GetService<LibreElevation>();
        return elevation?.Start(on) switch
        {
            ElevationStart.Started => ApiResults.Json(ElevationOf(elevation) with { State = LibreElevation.SettingUp, Reason = null, Fallback = null }, StatusCodes.Status202Accepted),
            ElevationStart.Busy => Conflict("Reading all sensors is being set up or turned off already."),
            _ => Conflict(elevation?.Report() is { Reason: { } reason } ? $"Reading all sensors can't be set up here ({reason})." : "Reading all sensors can't be set up here."),
        };
    }

    private static IResult Conflict(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

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
            // The port it listens on, for a client served from a proxy's own origin, which can't read it from its address.
            Port = runtime.Port,
            // Mock mode (--mock, --mock-seed) or a replayed recording (--replay-sensors): not this computer's sensors, for the run.
            Mock = options.SensorsNotReal,
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
                .Select(s =>
                {
                    // The status and the type from one call into the source, as /sensors/values reads them, but passively:
                    // listing puts no sensor in use (a public address isn't looked up for it).
                    var state = hub.PeekState(s.Key);
                    return new SensorEntry
                    {
                        Key = s.Key.ToString(),
                        Source = s.SourceId,
                        Group = s.Group,
                        Name = s.Name,
                        Unit = s.Unit,
                        Status = state.Status,
                        Type = SensorEntry.TypeName(state.Type),
                    };
                })
                .ToList())).RequireScope(ApiScope.Read);

        // Every source the engine runs, in the hub's order, listed with no sensors too: its state now and why; Libre's with
        // the PawnIO driver.
        api.MapGet("/sensors/sources", (SensorHub hub, IServiceProvider services) =>
        {
            var libre = services.GetService<LibreSource>();
            var installer = services.GetService<PawnIoInstaller>();
            var elevation = services.GetService<LibreElevation>();
            return ApiResults.Json(
                hub.SourceStates()
                    .Select(s => new SensorSourceBody
                    {
                        Id = s.Id,
                        State = s.State.Kind,
                        Reason = s.State.Reason is { } reason ? new SensorSourceReason { Code = reason.Code, Message = reason.Message } : null,
                        Sensors = s.Sensors,
                        Driver = s.Id == LibreSource.SourceId && libre is not null ? DriverOf(libre, installer) : null,
                        Elevation = s.Id == LibreSource.SourceId && elevation is not null ? ElevationOf(elevation) : null,
                    })
                    .ToList());
        }).RequireScope(ApiScope.Read);

        // Installs the PawnIO driver from the engine's own bundled installer (nothing in the request chooses what runs);
        // one at a time, its tail included (until the Libre source has read the driver it installed); 409 meanwhile, or
        // without that installer (not Windows, a replay, no file, another hash).
        api.MapPost("/sensors/sources/libre/driver", (IServiceProvider services) =>
        {
            var libre = services.GetService<LibreSource>();
            var installer = services.GetService<PawnIoInstaller>();
            return installer?.Start(() => libre?.DriverSettling == true) switch
            {
                PawnIoStart.Started => ApiResults.Json(DriverOf(libre, installer) with { State = "installing" }, StatusCodes.Status202Accepted),
                PawnIoStart.Busy => Conflict("An install of the PawnIO driver runs already, or the engine is still reading the driver it installed."),
                _ => Conflict("The engine has no PawnIO installer to run here."),
            };
        }).RequireScope(ApiScope.Session);

        // "Read all sensors": sets up the sensor helper's task, run as SYSTEM, through this install's own helper run with
        // runas (one Windows prompt), or removes it; one change at a time; 409 meanwhile, or when there is nothing to set up
        // here (not Windows, a replay, the engine elevated, or not installed under Program Files).
        api.MapPost("/sensors/sources/libre/elevation", (IServiceProvider services) => ChangeElevation(services, on: true)).RequireScope(ApiScope.Session);
        api.MapDelete("/sensors/sources/libre/elevation", (IServiceProvider services) => ChangeElevation(services, on: false)).RequireScope(ApiScope.Session);

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
                    // A table's columns, first rows, row count and suggested layout, from the reading the text is from.
                    Table = reading.ValueTable is { } table ? SensorTable.Of(table, reading.ValueTableFormat) : null,
                    History = samples,
                    Status = status,
                });
            }

            return ApiResults.Json(values);
        }).RequireScope(ApiScope.Read);

        // An image sensor's picture (spec/docs/api.md, "Sensor pictures"): the bytes its source holds under the reference,
        // inspected as an asset is and answered as they are. No read of the sensor (nothing is started for it), no fetch,
        // no copy, nothing decoded, and nothing logged of the key, the reference or the bytes.
        api.MapGet("/sensors/image", (HttpRequest request, SensorHub hub, HttpContext context) =>
        {
            if (QueryOf(request, "key", MaxImageKeyChars) is not { } keyText || QueryOf(request, "ref", PolledSensorSource.MaxReferenceChars) is not { } reference)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request",
                    detail: $"Give key (1–{MaxImageKeyChars} characters) and ref (1–{PolledSensorSource.MaxReferenceChars} characters) once each: an image sensor's key and the reference its reading names.");
            }

            if (!SensorKey.TryParse(keyText, out var key) || !hub.TryGetImage(key, reference, out var bytes)
                || bytes.Length > MaxImageBytes || ImageTypeOf(bytes.Span) is not { } type)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found",
                    detail: "No picture is held for this sensor under this reference.");
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(bytes, type);
        }).RequireScope(ApiScope.Read);
    }

    /// <summary>The longest key <c>GET /api/sensors/image</c> takes.</summary>
    public const int MaxImageKeyChars = 512;

    /// <summary>The largest picture <c>GET /api/sensors/image</c> answers: 8 MiB, a source's own cap (a cover's).</summary>
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The query parameter <paramref name="name"/>, given once, of 1 to <paramref name="maxChars"/> characters, counted as the
    /// spec counts a <c>maxLength</c>, in Unicode code points; else null.
    /// </summary>
    private static string? QueryOf(HttpRequest request, string name, int maxChars) =>
        request.Query.TryGetValue(name, out var values) && values.Count == 1 && values[0] is { Length: > 0 } value
            && value.Length <= 2 * maxChars && value.EnumerateRunes().Count() <= maxChars ? value : null;

    /// <summary>
    /// The media type of a picture an image sensor's request answers: a PNG, JPEG, GIF or WebP within the media limits, as
    /// an asset's bytes are inspected (from the header, nothing decoded); null for anything else.
    /// </summary>
    private static string? ImageTypeOf(ReadOnlySpan<byte> bytes) =>
        MediaValidator.Check(bytes) is { Errors.Count: 0, Format: { } format }
            ? format switch
            {
                MediaFormat.Png => "image/png",
                MediaFormat.Jpeg => "image/jpeg",
                MediaFormat.Gif => "image/gif",
                MediaFormat.WebP => "image/webp",
                _ => null,
            }
            : null;

    /// <summary>
    /// The optional API features the engine has (spec/docs/api.md, "Session"): every operation added after this version of
    /// the API comes under a name here: <c>plugins</c>, the plugins' requests (<see cref="PluginEndpoints"/>), listed whether or
    /// not the engine hosts plugins; <c>devices</c>, the USB panel devices' requests and message (<see cref="DeviceEndpoints"/>),
    /// listed whether or not the engine drives panels; <c>simulated.text</c>, a <c>simulated</c> subscription that makes up
    /// text and tables as well as numbers (spec/docs/api.md, "Simulated sensors"), so a client can say every value is
    /// made up; <c>sensors.image</c>, an image sensor's picture by request (<c>GET /api/sensors/image</c>).
    /// </summary>
    public static IReadOnlyList<string> Features { get; } = ["plugins", "devices", "simulated.text", "sensors.image"];

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
