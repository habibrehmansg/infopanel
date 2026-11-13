using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using InfoPanel.Spec;

namespace InfoPanel.Settings;

/// <summary>How much the engine logs: the least severe level written to the log file.</summary>
public enum EngineLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>A range of whole numbers with its default.</summary>
public readonly record struct SettingRange(int Min, int Max, int Default)
{
    public bool Contains(long value) => value >= Min && value <= Max;

    public int Clamp(long value) => (int)Math.Clamp(value, Min, Max);
}

/// <summary>
/// The limits of the engine settings (<see cref="SettingsStore"/>): each setting's range and default (1.4.x's), the
/// settings file's size, and the client preferences'. The store, the 1.x settings reader's clamp and the API's checks
/// read them from here; the sizes and counts are the format's settings limits (<see cref="SpecLimits"/>, their one home,
/// spec/docs/limits.md "Settings limits").
/// </summary>
public static class SettingsLimits
{
    /// <summary>The settings file's format string: its major version.</summary>
    public const string Format = "infopanel.settings/1";

    /// <summary>
    /// Frames a second for every output without its own rate. 1.4.x: <c>Models/Settings.cs</c> <c>TargetFrameRate</c>
    /// 15; its slider 1–60 (<c>Views/Pages/SettingsPage.xaml</c>).
    /// </summary>
    public static readonly SettingRange FrameRate = new(1, 60, 15);

    /// <summary>
    /// Milliseconds between the sensor history's samples. 1.4.x: <c>TargetGraphUpdateRate</c> 1000; its slider 100–3000.
    /// </summary>
    public static readonly SettingRange GraphRate = new(100, 3000, 1000);

    /// <summary>
    /// Seconds between the Libre source's storage reads. 1.4.x: <c>LibreHardwareMonitorStorageInterval</c> 30; its box
    /// 1–300.
    /// </summary>
    public static readonly SettingRange StorageInterval = new(1, 300, 30);

    /// <summary>The log level when none is set: 1.4.x's level outside debug builds.</summary>
    public const EngineLogLevel LogLevel = EngineLogLevel.Information;

    /// <summary>The largest settings file the engine reads, and so the largest it writes (<c>settingsBytes</c>).</summary>
    public static int MaxFileBytes => SpecLimits.Default.SettingsBytes;

    /// <summary>The most clients that keep preferences (<c>settingsClients</c>).</summary>
    public static int Clients => SpecLimits.Default.SettingsClients;

    /// <summary>The longest client name; names are 1 or more of <c>A–Z a–z 0–9 . _ -</c>.</summary>
    public const int ClientNameLength = 64;

    /// <summary>The largest preferences value one client keeps, in bytes on its canonical form (<c>settingsClientBytes</c>).</summary>
    public static int ClientBytes => SpecLimits.Default.SettingsClientBytes;

    /// <summary>A log level's name in the settings: <c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c>.</summary>
    public static string NameOf(EngineLogLevel level) => level switch
    {
        EngineLogLevel.Debug => "debug",
        EngineLogLevel.Information => "information",
        EngineLogLevel.Warning => "warning",
        EngineLogLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>The log level named <paramref name="name"/> exactly (lower case), or null.</summary>
    public static EngineLogLevel? LogLevelNamed(string? name) => name switch
    {
        "debug" => EngineLogLevel.Debug,
        "information" => EngineLogLevel.Information,
        "warning" => EngineLogLevel.Warning,
        "error" => EngineLogLevel.Error,
        _ => null,
    };

    /// <summary>The most names <c>proxy.hosts</c> holds.</summary>
    public const int ProxyHosts = 8;

    /// <summary>The longest name in <c>proxy.hosts</c>, and the longest of a name's labels.</summary>
    public const int ProxyHostLength = 253, ProxyHostLabelLength = 63;

    /// <summary>
    /// Whether <paramref name="name"/> may be one of <c>proxy.hosts</c>: a DNS name in lower case, as the schema's
    /// <c>proxyHost</c> pattern has it: two labels or more, each 1–63 of <c>a–z 0–9 -</c> neither starting nor ending with
    /// <c>-</c>, the last starting with a letter (so no IP literal), at most 253 characters, no trailing dot. No wildcard,
    /// port, upper case or other text (an internationalised name in its <c>xn--</c> form); <c>localhost</c>, one label, isn't one.
    /// </summary>
    public static bool IsProxyHost(string name)
    {
        if (name.Length is 0 or > ProxyHostLength)
            return false;

        var labels = name.Split('.');
        if (labels.Length < 2)
            return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > ProxyHostLabelLength || label[0] == '-' || label[^1] == '-')
                return false;
            foreach (var c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                    return false;
            }
        }

        return labels[^1][0] is >= 'a' and <= 'z';
    }

    /// <summary>Whether <paramref name="names"/> may be <c>proxy.hosts</c>: at most 8 names, each one (<see cref="IsProxyHost"/>), none twice.</summary>
    public static bool AreProxyHosts(IReadOnlyCollection<string> names) =>
        names.Count <= ProxyHosts && names.All(IsProxyHost) && names.Distinct(StringComparer.Ordinal).Count() == names.Count;

    /// <summary>Whether <paramref name="name"/> is a valid client name.</summary>
    public static bool IsClientName(string name)
    {
        if (name.Length is 0 or > ClientNameLength)
            return false;
        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return false;
        }

        return true;
    }
}

/// <summary>The Libre source's settings: whether it runs, whether it reads storage, and how often (seconds).</summary>
public sealed record LibreSettings(bool Enabled, bool Storage, int StorageInterval)
{
    public static LibreSettings Default { get; } = new(true, true, SettingsLimits.StorageInterval.Default);
}

/// <summary>
/// The network source's settings: whether it looks up this network's public address (<c>sources.network.publicIp</c>;
/// <c>InfoPanel.Sensors.Network.NetworkSource</c>).
/// </summary>
public sealed record NetworkSettings(bool PublicIp)
{
    public static NetworkSettings Default { get; } = new(true);
}

/// <summary>The units the weather source reads in (<c>sources.weather.units</c>).</summary>
public enum WeatherUnits
{
    /// <summary>The PC's region's: metric unless its regional format's region uses imperial units.</summary>
    Auto,

    /// <summary>°C, hPa, m/s, mm.</summary>
    Metric,

    /// <summary>°F, inHg, mph, in.</summary>
    Imperial,
}

/// <summary>
/// The weather source's settings (<c>sources.weather</c>; <c>InfoPanel.Sensors.Weather.WeatherSource</c>): whether it
/// asks the configured service for the weather, the units it reads in, and the place it asks for (null: the place the
/// service finds for the request's network address).
/// </summary>
public sealed record WeatherSettings(bool Enabled, WeatherUnits Units, string? Place)
{
    public static WeatherSettings Default { get; } = new(true, WeatherUnits.Auto, null);

    /// <summary>The longest place, in UTF-16 code units.</summary>
    public const int PlaceLength = 200;

    /// <summary>Whether <paramref name="place"/> is a place the source may ask for: 1 to <see cref="PlaceLength"/> UTF-16 code units, not all white space.</summary>
    public static bool IsPlace(string? place) => place is { Length: > 0 and <= PlaceLength } && !string.IsNullOrWhiteSpace(place);

    /// <summary>A units setting's name (<c>auto</c>, <c>metric</c>, <c>imperial</c>).</summary>
    public static string NameOf(WeatherUnits units) => units switch
    {
        WeatherUnits.Metric => "metric",
        WeatherUnits.Imperial => "imperial",
        _ => "auto",
    };

    /// <summary>The units a name gives, or null for a name that isn't one.</summary>
    public static WeatherUnits? UnitsNamed(string? name) => name switch
    {
        "auto" => WeatherUnits.Auto,
        "metric" => WeatherUnits.Metric,
        "imperial" => WeatherUnits.Imperial,
        _ => null,
    };
}

/// <summary>
/// The system information source's settings (<c>sources.systemInfo</c>; <c>InfoPanel.Sensors.SystemInfo.SystemInfoSource</c>):
/// the process names its tables leave out, each compared with a process's name exactly (in its case, no white space
/// trimmed), as the 1.x System Info plugin compared them. By default the plugin's nine; an empty list leaves out none.
/// </summary>
public sealed record SystemInfoSettings(IReadOnlyList<string> ExcludedProcesses)
{
    /// <summary>The 1.x System Info plugin's list.</summary>
    public static SystemInfoSettings Default { get; } =
        new(ImmutableArray.Create("_Total", "Idle", "dwm", "csrss", "svchost", "lsass", "system", "spoolsv", "Memory Compression"));

    /// <summary>The most names the list holds.</summary>
    public const int MaxNames = 64;

    /// <summary>The longest name, in characters (Unicode code points, as the schema counts a string's length).</summary>
    public const int NameLength = 64;

    /// <summary>Whether <paramref name="name"/> may be in the list: 1 to <see cref="NameLength"/> characters, kept as given.</summary>
    public static bool IsName(string name) => Spec.Schema.Json.CodePoints(name) is >= 1 and <= NameLength;
}

/// <summary>
/// The media providers' settings: whether they may connect to this computer and the local network (spec/docs/providers.md).
/// </summary>
public sealed record MediaSettings(bool LocalNetwork)
{
    public static MediaSettings Default { get; } = new(true);
}

/// <summary>
/// Drawing (<c>rendering</c>): whether the engine draws frames with the GPU (<c>rendering.gpu</c>, off by default;
/// <c>InfoPanel.Output.Gpu.GpuRendering</c>).
/// </summary>
public sealed record RenderingSettings(bool Gpu)
{
    public static RenderingSettings Default { get; } = new(false);
}

/// <summary>
/// What the engine reports (<c>telemetry</c>): whether a release build that reports crashes sends them
/// (<c>telemetry.crashReports</c>, on by default). A build without a crash-report address sends nothing whatever it says.
/// <paramref name="CrashReportsEpoch"/> is not a setting: the engine's own record of the consent in effect
/// (<c>telemetry.crashReportsEpoch</c> in the file, never in the API), a random id (32 lower-case hex digits) the write
/// that turns crash reports on makes, the write that turns them off removes and every other write keeps
/// (<see cref="SettingsStore.CrashReportsEpoch"/> makes one when they are on without one). Null when there is none.
/// Crash reports keep their queue in a folder named by it, so nothing queued under an earlier consent is ever read.
/// </summary>
public sealed record TelemetrySettings(bool CrashReports, string? CrashReportsEpoch = null)
{
    public static TelemetrySettings Default { get; } = new(true);
}

/// <summary>
/// The configured service's settings: its address as set (<c>service.address</c>, an origin in its normal form,
/// <c>InfoPanel.Service.ServiceAddress</c>), or null when none is set and the engine's default applies; and whether AI
/// apps may use this engine's tools through the configured service (<c>service.aiApps</c>, off by default); and whether
/// a client on another computer may pair with this engine through a pass the configured service signed
/// (<c>service.pairing</c>, off by default; <c>POST /auth/pairing/pass</c>).
/// </summary>
public sealed record ServiceSettings(string? Address, bool AiApps = false, bool Pairing = false)
{
    public static ServiceSettings Default { get; } = new((string?)null);
}

/// <summary>
/// The names a reverse proxy on this computer reaches the engine by (<c>proxy.hosts</c>): the engine answers a request
/// whose <c>Host</c> is one of them as it answers a loopback name (<c>InfoPanel.Security.HostRule</c>). None by default.
/// </summary>
public sealed record ProxySettings(IReadOnlyList<string> Hosts)
{
    public static ProxySettings Default { get; } = new(ImmutableArray<string>.Empty);
}

/// <summary>
/// The engine settings in effect: each set value, or its default. <see cref="Clients"/> holds each client's own
/// preferences, opaque to the engine.
/// </summary>
public sealed record EngineSettings(
    int FrameRate,
    int GraphRate,
    EngineLogLevel LogLevel,
    LibreSettings Libre,
    NetworkSettings Network,
    WeatherSettings Weather,
    SystemInfoSettings SystemInfo,
    MediaSettings Media,
    RenderingSettings Rendering,
    ServiceSettings Service,
    ProxySettings Proxy,
    TelemetrySettings Telemetry,
    IReadOnlyDictionary<string, JsonElement> Clients)
{
    public static EngineSettings Default { get; } = new(
        SettingsLimits.FrameRate.Default,
        SettingsLimits.GraphRate.Default,
        SettingsLimits.LogLevel,
        LibreSettings.Default,
        NetworkSettings.Default,
        WeatherSettings.Default,
        SystemInfoSettings.Default,
        MediaSettings.Default,
        RenderingSettings.Default,
        ServiceSettings.Default,
        ProxySettings.Default,
        TelemetrySettings.Default,
        ImmutableDictionary<string, JsonElement>.Empty);

    /// <summary>The time between the sensor history's samples.</summary>
    public TimeSpan GraphInterval => TimeSpan.FromMilliseconds(GraphRate);
}

/// <summary>The settings in effect and how many changes to the stored settings this run made before them (from 0).</summary>
public sealed record SettingsSnapshot(EngineSettings Settings, long Version);

/// <summary>Whether the settings file could be read.</summary>
public enum SettingsStatus
{
    /// <summary>Read (or absent): changes are written to it.</summary>
    Ok,

    /// <summary>Not a settings file the engine can read: the defaults apply and it is never rewritten.</summary>
    Unreadable,

    /// <summary>Another major version's file: the defaults apply and it is never rewritten.</summary>
    Unsupported,
}
