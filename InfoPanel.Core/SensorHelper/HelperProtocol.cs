using System;
using System.Collections.Generic;

namespace InfoPanel.SensorHelper;

/// <summary>
/// The sensor helper's protocol: what the engine and <c>InfoPanel.SensorHelper</c> (the process that hosts
/// LibreHardwareMonitorLib) say to each other over one named pipe. Each message is a frame: a 4-byte little-endian
/// length, then that many bytes of UTF-8 JSON (<see cref="HelperFraming"/>). The set is closed (<see cref="HelperCodec"/>):
/// the engine asks <c>hello</c>, <c>catalogue</c>, <c>sample</c> and <c>configure</c>; the helper answers each with the
/// same type, or <c>error</c>. No message writes to hardware or carries a path or code: the requests hold a protocol
/// number, a switch and a number of seconds, and the helper's tree (<see cref="ISensorTree"/>) only reads.
/// </summary>
/// <remarks>
/// This file and the others the helper links (all of this folder but <c>SensorHelperClient.cs</c>) build for the
/// helper's net8.0 too, so they use nothing newer than net8.0's System.Text.Json.
/// </remarks>
public static class HelperProtocol
{
    /// <summary>The protocol's version, sent in <c>hello</c> both ways; any other is refused.</summary>
    public const int Version = 1;

    /// <summary>The largest frame either side reads or writes: 1 MiB of JSON.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>The most sensors a catalogue lists or a sample holds values for.</summary>
    public const int MaxSensors = 16_384;

    /// <summary>The most hardware a catalogue lists.</summary>
    public const int MaxHardware = MaxSensors;

    /// <summary>
    /// The nesting depth a message is read at: the engine's <c>jsonDepth</c> (<c>EngineJson.MaxDepth</c>, which a test
    /// holds this to). The deepest message the protocol has nests three levels.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>The storage interval <c>configure</c> takes, in seconds: the <c>sources.libre.storageInterval</c> setting's range.</summary>
    public const int MinStorageInterval = 1;

    /// <inheritdoc cref="MinStorageInterval"/>
    public const int MaxStorageInterval = 300;

    /// <summary>What the helper reads with until the engine configures it: storage on, every 30 s (1.4.x's defaults).</summary>
    public const bool DefaultStorage = true;

    /// <inheritdoc cref="DefaultStorage"/>
    public const int DefaultStorageInterval = 30;

    /// <summary>How long the engine waits for a sample's answer before it gives the helper up.</summary>
    public static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the engine waits for any other answer: a catalogue or a configure can open LibreHardwareMonitor's
    /// computer, which takes seconds.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether <paramref name="name"/> may name the helper's pipe: 1 to 64 ASCII letters, digits, '.', '_' or '-', so
    /// it is a name and never a path.
    /// </summary>
    public static bool IsPipeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return false;

        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return false;
        }

        return name is not ("." or "..");
    }
}

/// <summary>
/// The unit of each LibreHardwareMonitor sensor type, by the type's name: 1.4.x's table (<c>LibreMonitor.cs</c>), with
/// the types 0.9.6 has that 1.4.x's table left out (SmallData "MB", TimeSpan "s", Timing "ns") and Throughput as
/// "B/s", the bytes per second LibreHardwareMonitor reports (1.4.x labelled the same number "KB/s"). A type not in the
/// table has no unit, as in 1.4.x.
/// </summary>
public static class HelperUnits
{
    private static readonly Dictionary<string, string> Units = new(StringComparer.Ordinal)
    {
        ["Voltage"] = "V",
        ["Current"] = "A",
        ["Clock"] = "MHz",
        ["Temperature"] = "°C",
        ["Load"] = "%",
        ["Fan"] = "RPM",
        ["Flow"] = "L/h",
        ["Control"] = "%",
        ["Level"] = "%",
        ["Factor"] = "1",
        ["Power"] = "W",
        ["Data"] = "GB",
        ["SmallData"] = "MB",
        ["Frequency"] = "Hz",
        ["Energy"] = "mWh",
        ["Noise"] = "dBA",
        ["Conductivity"] = "µS/cm",
        ["Humidity"] = "%",
        ["Throughput"] = "B/s",
        ["TimeSpan"] = "s",
        ["Timing"] = "ns",
    };

    /// <summary>The unit of sensors of type <paramref name="sensorType"/> (LibreHardwareMonitor's <c>SensorType</c> name).</summary>
    public static string Of(string sensorType) => Units.TryGetValue(sensorType, out var unit) ? unit : "";
}

/// <summary>
/// The hardware groups the helper opens LibreHardwareMonitor's computer with: those 1.4.x opened (<c>LibreMonitor.cs</c>),
/// storage by the setting; the power supplies, batteries and power monitors 1.4.x left closed stay closed.
/// </summary>
public sealed record HelperGroups(
    bool Cpu,
    bool Gpu,
    bool Memory,
    bool Motherboard,
    bool Controller,
    bool Network,
    bool Storage,
    bool Psu,
    bool Battery,
    bool PowerMonitor)
{
    /// <summary>The groups with storage on or off.</summary>
    public static HelperGroups For(bool storage) =>
        new(Cpu: true, Gpu: true, Memory: true, Motherboard: true, Controller: true, Network: true, Storage: storage,
            Psu: false, Battery: false, PowerMonitor: false);
}

/// <summary>A message that breaks the protocol: a frame or a message either side refuses. <see cref="Code"/> names why.</summary>
public class HelperProtocolException(string code, string message) : Exception(message)
{
    /// <summary><c>truncated</c>, <c>tooLarge</c>, <c>malformed</c>, <c>unexpected</c>, <c>protocol</c>, <c>closed</c> or an error answer's code.</summary>
    public string Code { get; } = code;
}

/// <summary>An <c>error</c> the helper answered with, its code and message as sent.</summary>
public sealed class HelperErrorException(string code, string message) : HelperProtocolException(code, message);
