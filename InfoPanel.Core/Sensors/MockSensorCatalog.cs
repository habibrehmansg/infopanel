using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Sensors;

/// <summary>
/// One mock sensor: a row of <see cref="MockSensorCatalog"/> with its group, name, unit, range and walk parameters.
/// <see cref="Id"/> is the short id (<c>gpu.temp</c>); <see cref="Key"/> is
/// <c>plugin:/infopanel.mock/{GroupSlug}/{Id with '.' as '-'}</c>.
/// </summary>
public sealed record MockSensorDef(
    string Id,
    string GroupSlug,
    string Group,
    string Name,
    string Unit,
    double Min,
    double Max,
    double Base,
    double Jitter,
    int Precision,
    bool Bursty)
{
    public SensorKey Key { get; } = SensorKey.Plugin(MockSensorCatalog.KeyPrefix + GroupSlug + "/" + Id.Replace('.', '-'));
}

/// <summary>
/// The mock sensor catalog, laid out like HWiNFO's sensor tree: 35 sensors in groups, each with a name, unit,
/// range and walk parameters, plus a process table and a text sensor.
/// </summary>
public static class MockSensorCatalog
{
    /// <summary>The source id, also the first segment of every mock key.</summary>
    public const string SourceId = "infopanel.mock";

    public const string KeyPrefix = "/" + SourceId + "/";

    public const string SystemGroupSlug = "system";

    public const string SystemGroup = "System";

    /// <summary>A table of the busiest processes (columns Process, CPU, Memory), like a plugin table.</summary>
    public static readonly SensorKey TopProcessesKey = SensorKey.Plugin(KeyPrefix + SystemGroupSlug + "/top-processes");

    public const string TopProcessesName = "Top Processes";

    /// <summary>The table's default format (column:width pairs), as a plugin's <c>PluginTable.DefaultFormat</c>.</summary>
    public const string TopProcessesFormat = "0:150|1:70|2:90";

    /// <summary>A text sensor (string value) that changes every few seconds.</summary>
    public static readonly SensorKey NowPlayingKey = SensorKey.Plugin(KeyPrefix + SystemGroupSlug + "/now-playing");

    public const string NowPlayingName = "Now Playing";

    private static MockSensorDef[] Group(string slug, string name, params (string Id, string Name, string Unit, double Min, double Max, double Base, double Jitter, int Precision, bool Bursty)[] rows) =>
        rows.Select(r => new MockSensorDef(r.Id, slug, name, r.Name, r.Unit, r.Min, r.Max, r.Base, r.Jitter, r.Precision, r.Bursty)).ToArray();

    /// <summary>The 35 numeric sensors in catalog order (the walk consumes random numbers in this order).</summary>
    public static IReadOnlyList<MockSensorDef> Sensors { get; } =
    [
        .. Group("cpu", "CPU [#0]: AMD Ryzen 9 7950X3D",
            ("cpu.temp", "CPU (Tctl/Tdie)", "°C", 30, 95, 62, 2.5, 1, false),
            ("cpu.ccd1", "CPU CCD1 (Tdie)", "°C", 30, 95, 58, 2.5, 1, false),
            ("cpu.load", "Total CPU Usage", "%", 0, 100, 32, 8, 1, false),
            ("cpu.power", "CPU Package Power", "W", 20, 230, 96, 10, 1, false),
            ("cpu.clock", "Core Clocks (avg)", "MHz", 3000, 5700, 4850, 120, 0, false),
            ("cpu.vcore", "CPU Core Voltage (SVI3 TFN)", "V", 0.8, 1.4, 1.18, 0.03, 3, false)),
        .. Group("gpu", "GPU [#0]: NVIDIA GeForce RTX 4090",
            ("gpu.temp", "GPU Temperature", "°C", 30, 90, 61, 1.5, 1, false),
            ("gpu.hotspot", "GPU Hot Spot Temperature", "°C", 35, 105, 72, 1.8, 1, false),
            ("gpu.load", "GPU Core Load", "%", 0, 100, 78, 9, 1, false),
            ("gpu.power", "GPU Power", "W", 30, 450, 310, 25, 1, false),
            ("gpu.clock", "GPU Clock", "MHz", 210, 2790, 2610, 60, 0, false),
            ("gpu.memclock", "GPU Memory Clock", "MHz", 405, 1313, 1313, 0, 0, false),
            ("gpu.memused", "GPU Memory Allocated", "MB", 0, 24564, 11800, 300, 0, false),
            ("gpu.memload", "GPU Memory Usage", "%", 0, 100, 48, 1.5, 1, false),
            ("gpu.fan1", "GPU Fan1", "RPM", 0, 3000, 1450, 40, 0, false),
            ("gpu.fan2", "GPU Fan2", "RPM", 0, 3000, 1460, 40, 0, false),
            ("gpu.fan3", "GPU Fan3", "RPM", 0, 3000, 1440, 40, 0, false)),
        .. Group("memory", "Memory",
            ("mem.used", "Physical Memory Used", "MB", 0, 65536, 22800, 250, 0, false),
            ("mem.avail", "Physical Memory Available", "MB", 0, 65536, 42700, 250, 0, false),
            ("mem.load", "Physical Memory Load", "%", 0, 100, 35, 0.6, 1, false)),
        .. Group("motherboard", "ASUS ROG CROSSHAIR X670E HERO (Nuvoton NCT6799D)",
            ("mb.temp", "Motherboard", "°C", 20, 70, 38, 0.4, 1, false),
            ("mb.cpufan", "CPU Fan", "RPM", 0, 2500, 1180, 30, 0, false),
            ("mb.pump", "AIO Pump", "RPM", 0, 4000, 2420, 25, 0, false),
            ("mb.fan2", "Chassis Fan 2", "RPM", 0, 2000, 890, 20, 0, false),
            ("mb.fan3", "Chassis Fan 3", "RPM", 0, 2000, 905, 20, 0, false),
            ("mb.fan4", "Chassis Fan 4", "RPM", 0, 2000, 870, 20, 0, false)),
        .. Group("drive", "S.M.A.R.T.: Samsung SSD 990 PRO 2TB",
            ("drive.temp", "Drive Temperature", "°C", 25, 80, 44, 0.6, 0, false),
            ("drive.read", "Read Rate", "MB/s", 0, 7000, 40, 60, 1, true),
            ("drive.write", "Write Rate", "MB/s", 0, 6500, 25, 40, 1, true),
            ("drive.activity", "Total Activity", "%", 0, 100, 4, 3, 1, true)),
        .. Group("network", "Network: Intel Ethernet Controller I226-V",
            ("net.down", "Current DL rate", "KB/s", 0, 120000, 1800, 900, 1, true),
            ("net.up", "Current UP rate", "KB/s", 0, 40000, 260, 150, 1, true)),
        .. Group("rtss", "RTSS",
            ("fps", "Framerate", "FPS", 0, 240, 144, 6, 0, false),
            ("fps.low1", "Framerate 1% Low", "FPS", 0, 240, 98, 5, 0, false),
            ("frametime", "Frame Time", "ms", 2, 40, 6.9, 0.5, 1, false)),
    ];

    /// <summary>A sensor by its short id (<c>gpu.temp</c>), or null.</summary>
    public static MockSensorDef? ById(string id) => Sensors.FirstOrDefault(s => s.Id == id);

    /// <summary>The key of a numeric sensor by its short id (<c>gpu.temp</c> → <c>plugin:/infopanel.mock/gpu/gpu-temp</c>).</summary>
    public static SensorKey KeyOf(string id) =>
        ById(id)?.Key ?? throw new KeyNotFoundException($"No mock sensor '{id}'.");
}
