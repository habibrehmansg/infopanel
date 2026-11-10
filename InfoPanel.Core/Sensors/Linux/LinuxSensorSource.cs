using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// The Linux sensor source: a <see cref="PolledSensorSource"/> over <see cref="LinuxSensorSampler"/>, polled once a
/// second, its devices found again every 10 s. Keys are <c>infopanel:/linux/{id}</c>, the id the Linux fork of
/// InfoPanel 1.x gives the sensor (<see cref="LinuxSensorIds"/>), so its profiles' bindings name these keys:
/// <c>infopanel:/linux/hwmon/v1/it8696/platform+it87.2624/fan1</c>,
/// <c>infopanel:/linux/thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp</c>. It reads sysfs as the normal user:
/// what the OS refuses reads as <see cref="SensorStatus.PermissionDenied"/>, what isn't there is not listed or reads as
/// <see cref="SensorStatus.Unavailable"/>.
/// <para>
/// Every sensor found is listed, but only the ones in use are read (<see cref="LinuxDemand"/>): a sensor is in use while
/// a consumer read it (<see cref="TryRead"/>, <see cref="ReadState"/>: a shown profile's and a client's value reads) or
/// used its samples (<see cref="NoteUse"/>, a graph's draw) within <see cref="LinuxDemand.UseWindow"/>, as the weather is
/// asked for only then. A listing, a status and a peek (<see cref="GetStatus"/>, <see cref="PeekState"/>,
/// <see cref="TryPeek"/>: <c>/api/sensors</c>, the history's own sampling) aren't uses. So a disk's <c>drivetemp</c>,
/// whose read resets the disk's spin-down timer, or a slow SMBus chip is read only while something shows it; a sensor
/// coming into use reads from the next poll on.
/// </para>
/// </summary>
public sealed class LinuxSensorSource : ISensorSource, ISensorStatusSource, ISensorSourceState, IDisposable
{
    public const string SourceId = "infopanel.linux";

    public const string KeyPrefix = "/" + SourceId + "/";

    /// <summary>
    /// The sysfs tree's folder in place of <see cref="DefaultSysfs"/> (a full path; Linux only), for tests and a check
    /// in a container on a tree made for it; logged when set.
    /// </summary>
    public const string SysfsVariable = "INFOPANEL_LINUX_SYSFS";

    public const string DefaultSysfs = "/sys";

    private readonly PolledSensorSource _polled;
    private readonly LinuxDemand _demand;

    private LinuxSensorSource(Sysfs sysfs, TimeProvider time, PolledSensorSourceOptions? options, ILogger? log)
    {
        _demand = new LinuxDemand(time);
        _polled = new PolledSensorSource(SourceId, () => new LinuxSensorSampler(sysfs, _demand, log), options, time, log: log);
    }

    /// <summary>The key of a sensor by its id (<c>hwmon/v1/…</c> → <c>infopanel:/linux/hwmon/v1/…</c>).</summary>
    public static SensorKey KeyOf(string id) => SensorKey.Plugin(KeyPrefix + id);

    /// <summary>A source over <paramref name="sysfs"/> (a test's tree, or <see cref="Sysfs.Disk"/>).</summary>
    public static LinuxSensorSource Create(Sysfs sysfs, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(sysfs);
        return new LinuxSensorSource(sysfs, time ?? TimeProvider.System, options, log);
    }

    /// <summary>The source over this computer's sysfs: <c>/sys</c>, or the folder <see cref="SysfsVariable"/> names.</summary>
    [SupportedOSPlatform("linux")]
    public static LinuxSensorSource CreateSystem(TimeProvider? time = null) =>
        Create(Sysfs.Disk(SysfsRoot(Environment.GetEnvironmentVariable(SysfsVariable), Log.Logger)), time);

    /// <summary>The sysfs folder: <paramref name="variable"/> when it is a full path (logged), else <see cref="DefaultSysfs"/>.</summary>
    internal static string SysfsRoot(string? variable, ILogger log)
    {
        if (string.IsNullOrEmpty(variable))
            return DefaultSysfs;
        if (!Path.IsPathFullyQualified(variable))
        {
            log.Warning("Linux sensors: {Variable} is {Value}, not a full path; reading {Default}", SysfsVariable, variable, DefaultSysfs);
            return DefaultSysfs;
        }

        log.Information("Linux sensors: reading the sysfs tree at {Root} ({Variable})", variable, SysfsVariable);
        return variable;
    }

    public string Id => _polled.Id;

    public SourceState State => _polled.State;

    public bool Handles(SensorKey key) => _polled.Handles(key);

    public IReadOnlyList<SensorInfo> GetSensors() => _polled.GetSensors();

    /// <summary>A consumer's read: puts the sensor in use.</summary>
    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        _demand.Note(key);
        return _polled.TryRead(key, out reading);
    }

    /// <summary>A consumer's read: puts the sensor in use.</summary>
    public SensorState ReadState(SensorKey key)
    {
        _demand.Note(key);
        return _polled.ReadState(key);
    }

    /// <summary>A peek isn't a use: <see cref="TryRead"/> without putting the sensor in use (the history's own sampling).</summary>
    public bool TryPeek(SensorKey key, out SensorReading reading) => _polled.TryRead(key, out reading);

    /// <summary>A peek isn't a use: <see cref="ReadState"/> without putting the sensor in use (listing the sensors).</summary>
    public SensorState PeekState(SensorKey key) => _polled.ReadState(key);

    /// <summary>A status isn't a use.</summary>
    public SensorStatus GetStatus(SensorKey key) => _polled.GetStatus(key);

    /// <summary>A consumer uses the sensor's samples (a graph's draw): puts it in use, as a read does.</summary>
    public void NoteUse(SensorKey key) => _demand.Note(key);

    public Task StartAsync(CancellationToken ct) => _polled.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _polled.StopAsync(ct);

    public void Dispose() => _polled.Dispose();

    /// <summary>Discovers the sensors and takes the first poll. For tests.</summary>
    internal void Open() => _polled.Open();

    /// <summary>Takes one poll. For tests.</summary>
    internal void Poll() => _polled.Poll();
}
