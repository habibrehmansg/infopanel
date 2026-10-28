using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

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
/// read them from here.
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

    /// <summary>The largest settings file the engine reads, and so the largest it writes.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>The most clients that keep preferences.</summary>
    public const int Clients = 16;

    /// <summary>The longest client name; names are 1 or more of <c>A–Z a–z 0–9 . _ -</c>.</summary>
    public const int ClientNameLength = 64;

    /// <summary>The largest preferences value one client keeps, in bytes as the file stores it: compact JSON in UTF-8, escaped as the file writer escapes.</summary>
    public const int ClientBytes = 64 * 1024;

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
/// The engine settings in effect: each set value, or its default. <see cref="Clients"/> holds each client's own
/// preferences, opaque to the engine.
/// </summary>
public sealed record EngineSettings(
    int FrameRate,
    int GraphRate,
    EngineLogLevel LogLevel,
    LibreSettings Libre,
    IReadOnlyDictionary<string, JsonElement> Clients)
{
    public static EngineSettings Default { get; } = new(
        SettingsLimits.FrameRate.Default,
        SettingsLimits.GraphRate.Default,
        SettingsLimits.LogLevel,
        LibreSettings.Default,
        ImmutableDictionary<string, JsonElement>.Empty);

    /// <summary>The time between the sensor history's samples.</summary>
    public TimeSpan GraphInterval => TimeSpan.FromMilliseconds(GraphRate);
}

/// <summary>The settings in effect and how many effective changes this run made before them (from 0).</summary>
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
