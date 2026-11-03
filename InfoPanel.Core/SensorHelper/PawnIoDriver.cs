using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The PawnIO driver's state, as the sensor helper reports it in <c>hello</c>'s <c>driver</c> and the engine reads it
/// itself: the uninstall entry's <c>DisplayVersion</c> (<see cref="UninstallKey"/>, in the 64-bit view of
/// <c>HKEY_LOCAL_MACHINE</c>, which any user may read), as 1.4.x read it. LibreHardwareMonitorLib 0.9.6 reads ring0
/// through PawnIO from version <see cref="Minimum"/> on (1.4.x's minimum).
/// </summary>
public static class PawnIoDriver
{
    /// <summary>No uninstall entry, or one whose version can't be read: as 1.4.x, not installed.</summary>
    public const string Missing = "missing";

    /// <summary>Installed below <see cref="Minimum"/>.</summary>
    public const string Outdated = "outdated";

    /// <summary>Installed at <see cref="Minimum"/> or later.</summary>
    public const string Installed = "installed";

    /// <summary>
    /// Installed, but the sensor helper can't open it: without administrator rights (LibreHardwareMonitorLib opens PawnIO
    /// only with them; <see cref="OfHelper"/>), or Windows blocked it.
    /// </summary>
    public const string NotLoaded = "notLoaded";

    /// <summary>Not read: a helper that doesn't read it, or no registry to read (a recording's helper says what it said).</summary>
    public const string Unknown = "unknown";

    /// <summary>The key PawnIO's installer writes its uninstall entry under, in <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>The value the installed version is in.</summary>
    public const string VersionValue = "DisplayVersion";

    /// <summary>The oldest PawnIO LibreHardwareMonitor uses (1.4.x's minimum).</summary>
    public static readonly Version Minimum = new(2, 0, 0, 0);

    /// <summary>The driver's state and version from the registry's <c>DisplayVersion</c> (null: none, or unreadable).</summary>
    public static PawnIoReading Read(IPawnIoRegistry registry)
    {
        string? value;
        try
        {
            value = registry.DisplayVersion();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            value = null;
        }

        return Of(value);
    }

    /// <summary>
    /// The state the sensor helper says in <c>hello</c>, from the registry's <paramref name="reading"/> and whether it runs
    /// with administrator rights: <see cref="Installed"/> without them is <see cref="NotLoaded"/>, since
    /// LibreHardwareMonitorLib can't open PawnIO without them (found on a PC with PawnIO 2.2.0: 179 sensors unelevated,
    /// 263 elevated); anything else is the registry's state. The engine compares registry readings only, so this never
    /// starts a helper again by itself.
    /// </summary>
    public static string OfHelper(PawnIoReading reading, bool elevated) =>
        reading.State == Installed && !elevated ? NotLoaded : reading.State;

    /// <summary>
    /// The state a <c>DisplayVersion</c> stands for: none, or one that isn't a version, is <see cref="Missing"/> (1.4.x
    /// treated both as not installed); below <see cref="Minimum"/> <see cref="Outdated"/>; else <see cref="Installed"/>.
    /// Parts left out count as 0 (<c>2.0.0</c> is 2.0.0.0).
    /// </summary>
    public static PawnIoReading Of(string? displayVersion)
    {
        if (Parse(displayVersion) is not { } version)
            return PawnIoReading.None;
        var text = displayVersion!.Trim();
        return new PawnIoReading(version < Minimum ? Outdated : Installed, text);
    }

    /// <summary>A dotted version of two to four whole numbers, its missing parts 0; null for anything else.</summary>
    public static Version? Parse(string? text)
    {
        if (text is null || !Version.TryParse(text.Trim(), out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
}

/// <summary>The driver's state (<see cref="PawnIoDriver"/>'s names) and its installed version as the registry gives it.</summary>
public sealed record PawnIoReading(string State, string? Version)
{
    public static readonly PawnIoReading None = new(PawnIoDriver.Missing, null);
}

/// <summary>Where the driver's installed version is read: Windows' registry, a fake in the tests.</summary>
public interface IPawnIoRegistry
{
    /// <summary>The uninstall entry's <c>DisplayVersion</c> as a string, or null when there is no entry or no such string.</summary>
    string? DisplayVersion();
}

/// <summary>The registry's 64-bit view of <c>HKEY_LOCAL_MACHINE</c>, read without administrator rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPawnIoRegistry : IPawnIoRegistry
{
    public string? DisplayVersion()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(PawnIoDriver.UninstallKey, writable: false);
        return key?.GetValue(PawnIoDriver.VersionValue) as string;
    }
}
