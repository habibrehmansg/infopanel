using InfoPanel.Plugins;
using InfoPanel.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// The system information source, <c>infopanel.system-info</c> ("System Info"): a <see cref="PolledSensorSource"/> over
/// <see cref="SystemInfoSampler"/>, polled every second (the 1.x System Info plugin's interval). It lists the 1.x
/// plugin's entries (<see cref="Entries"/>) under its container and entry ids, <c>infopanel:/system-info/{container}/{entry}</c>:
/// the uptime as five texts in the plugin's formats, the processes' counts, CPU and memory as numbers in its units, and
/// three tables of processes (a row per process name, its instances summed) in its columns, cells and default format,
/// the busiest first, leaving out the names the engine settings' <c>sources.systemInfo.excludedProcesses</c> lists (by
/// default the plugin's, <see cref="DefaultExcluded"/>; a change applies from the next round) and cut at
/// <see cref="MaxRows"/> rows. The engine never answers the plugin's keys.
/// </summary>
public static class SystemInfoSource
{
    public const string SourceId = "infopanel.system-info";

    /// <summary>The source's name.</summary>
    public const string Name = "System Info";

    public const string UptimeGroup = "Uptime";
    public const string ProcessesGroup = "Processes";

    /// <summary>The units of the plugin's numbers: a percentage, and megabytes with a leading space, as 1.x wrote them.</summary>
    public const string PercentUnit = "%";
    public const string MemoryUnit = " MB";

    /// <summary>A table's Memory cell of 1,024 MB or more reads in gigabytes, in this unit.</summary>
    public const string GigabyteUnit = " GB";

    /// <summary>The tables' CPU figures are the average of this many rounds (the last 3 s at the source's interval).</summary>
    public const int CpuRounds = 3;

    /// <summary>The tables' default format (the 1.x plugin's): the name 200 wide, then Usage, Utility and Memory.</summary>
    public const string TableFormat = "0:200|1:60|2:70|3:100";

    /// <summary>A table keeps this many rows (a plugin's table limit).</summary>
    public const int MaxRows = 1000;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The names the tables leave out unless the settings say otherwise (<c>sources.systemInfo.excludedProcesses</c>),
    /// compared exactly (case too), as the 1.x plugin's default list.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcluded => SystemInfoSettings.Default.ExcludedProcesses;

    /// <summary>The entries in the order the source lists them (the 1.x plugin's): id (<c>{container}/{entry}</c>), group, name, kind and unit.</summary>
    public static IReadOnlyList<(string Id, string Group, string Name, SensorKind Kind, string Unit)> Entries { get; } =
    [
        ("uptime/formatted", UptimeGroup, "Formatted", SensorKind.Text, ""),
        ("uptime/days", UptimeGroup, "Days", SensorKind.Text, ""),
        ("uptime/hours", UptimeGroup, "Hours", SensorKind.Text, ""),
        ("uptime/minutes", UptimeGroup, "Minutes", SensorKind.Text, ""),
        ("uptime/seconds", UptimeGroup, "Seconds", SensorKind.Text, ""),
        ("processes/process-count", ProcessesGroup, "Process Count", SensorKind.Number, ""),
        ("processes/thread-count", ProcessesGroup, "Thread Count", SensorKind.Number, ""),
        ("processes/handle-count", ProcessesGroup, "Handle Count", SensorKind.Number, ""),
        ("processes/cpu-usage", ProcessesGroup, "CPU Usage", SensorKind.Number, PercentUnit),
        ("processes/cpu-utility", ProcessesGroup, "CPU Utility", SensorKind.Number, PercentUnit),
        ("processes/memory-usage", ProcessesGroup, "Memory Usage", SensorKind.Number, MemoryUnit),
        ("processes/memory-compression", ProcessesGroup, "Memory Compression", SensorKind.Number, MemoryUnit),
        ("processes/top-cpu-usage", ProcessesGroup, "Top CPU Usage", SensorKind.Table, ""),
        ("processes/top-cpu-utility", ProcessesGroup, "Top CPU Utility", SensorKind.Table, ""),
        ("processes/top-memory-usage", ProcessesGroup, "Top Memory Usage", SensorKind.Table, ""),
    ];

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>The key of an entry: <c>infopanel:/system-info/{container}/{entry}</c>.</summary>
    public static SensorKey KeyOf(string container, string entry) => Space.KeyOf(container + "/" + entry);

    /// <summary>
    /// The uptime's five texts, as the 1.x plugin wrote them: <c>d:hh:mm:ss</c> (the days unpadded), then the days, hours,
    /// minutes and seconds, each at least two digits.
    /// </summary>
    public static (string Formatted, string Days, string Hours, string Minutes, string Seconds) UptimeTexts(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
            uptime = TimeSpan.Zero;
        var c = CultureInfo.InvariantCulture;
        return (string.Create(c, $"{uptime.Days}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}"),
            uptime.Days.ToString("D2", c), uptime.Hours.ToString("D2", c), uptime.Minutes.ToString("D2", c), uptime.Seconds.ToString("D2", c));
    }

    /// <summary>
    /// A source over <paramref name="processes"/> (a test's). With <paramref name="settings"/>, its tables leave out their
    /// <c>sources.systemInfo.excludedProcesses</c>, read at each round, so a change applies from the next; without, the default.
    /// </summary>
    public static PolledSensorSource Create(IProcessSampler processes, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null,
        SettingsStore? settings = null) =>
        new(SourceId, () => new SystemInfoSampler(processes, log, settings), options ?? new PolledSensorSourceOptions { Interval = Interval }, time, log: log);

    /// <summary>The source over this computer: <see cref="MacProcessSampler"/> on macOS, <see cref="WinProcessSampler"/> on Windows; with <paramref name="settings"/>, following them.</summary>
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public static PolledSensorSource CreateSystem(TimeProvider? time = null, SettingsStore? settings = null) =>
        Create(OperatingSystem.IsWindows() ? new WinProcessSampler() : new MacProcessSampler(), time, settings: settings);
}

/// <summary>
/// Reads <see cref="SystemInfoSource"/>'s entries each round from an <see cref="IProcessSampler"/>. A reading the system
/// doesn't have is unavailable (macOS: Thread Count and Handle Count); where it has no CPU utility, CPU Utility reads CPU
/// Usage, in the tables too. A process's CPU usage is the share of every CPU's time it used since the last round
/// (as 1.x's), or, where the system counts per CPU (<see cref="IProcessSampler.ProcessCpuPerCpu"/>, macOS), that share
/// times the CPUs' count, so it is read from the second round on, for the processes listed in both rounds; its utility
/// the system's utility in proportion (1.x's), or its usage. The tables show each name's figures averaged over its appearances
/// in the last <see cref="SystemInfoSource.CpuRounds"/> rounds, and its memory in megabytes, or gigabytes from 1,024 MB. Each call
/// to the system that throws fails its own readings that round, logged once while it fails.
/// </summary>
public sealed class SystemInfoSampler : ISensorSampler
{
    private enum Part { Uptime, ProcessCount, ThreadCount, HandleCount, Cpu, MemoryUsage, MemoryCompression, Processes }

    private sealed class Row(string name)
    {
        public readonly string Name = name;
        public double Usage;
        public double Utility;
        public long Memory;
    }

    private readonly IProcessSampler _system;
    private readonly ILogger _log;
    private readonly SettingsStore? _settings;
    // The names the tables leave out, and the settings' list they were made from (a new list at each change).
    private IReadOnlyList<string> _excludedFrom = SystemInfoSource.DefaultExcluded;
    private HashSet<string> _excluded = new(SystemInfoSource.DefaultExcluded, StringComparer.Ordinal);
    private readonly HashSet<Part> _failing = [];
    private readonly string[] _uptime = ["", "", "", "", ""];
    // The processes' CPU time at the last round, by pid, with their start.
    private Dictionary<int, (ulong Started, TimeSpan Cpu)> _last = [];
    // The tables' last rounds (at most CpuRounds), oldest first: each name's CPU figures (usage, utility) in it.
    private readonly Queue<Dictionary<string, (double Usage, double Utility)>> _recent = new();
    private bool _listed;

    public SystemInfoSampler(IProcessSampler system, ILogger? log = null, SettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        _system = system;
        _log = (log ?? Log.Logger).ForContext<SystemInfoSampler>();
        _settings = settings;
    }

    /// <summary>The names the tables leave out this round: the settings' list as it is now, matched exactly (ordinal).</summary>
    private HashSet<string> Excluded()
    {
        var list = _settings?.Current.Settings.SystemInfo.ExcludedProcesses ?? SystemInfoSource.DefaultExcluded;
        if (!ReferenceEquals(list, _excludedFrom))
        {
            _excluded = new HashSet<string>(list, StringComparer.Ordinal);
            _excludedFrom = list;
        }

        return _excluded;
    }

    public IReadOnlyList<SensorDefinition> Discover() =>
        SystemInfoSource.Entries.Select(e => new SensorDefinition(e.Id, e.Group, e.Name, e.Unit, e.Kind)).ToList();

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        if (samples.Length < SystemInfoSource.Entries.Count)
            return;

        // Every reading below is this poll's: a reader that shares one read of the system among them reads it afresh.
        _system.BeginRound();
        if (Try(Part.Uptime, _system.Uptime, out var uptime))
        {
            var (f, d, h, m, s) = SystemInfoSource.UptimeTexts(uptime);
            string[] texts = [f, d, h, m, s];
            for (var i = 0; i < texts.Length; i++)
            {
                // The same string while the text is unchanged.
                if (texts[i] != _uptime[i])
                    _uptime[i] = texts[i];
                samples[i].SetText(_uptime[i]);
            }
        }
        else
        {
            Fail(samples[..5]);
        }

        Count(ref samples[5], Part.ProcessCount, () => _system.ProcessCount());
        Count(ref samples[6], Part.ThreadCount, _system.ThreadCount);
        Count(ref samples[7], Part.HandleCount, _system.HandleCount);

        double? usage = null, utility = null;
        if (Try(Part.Cpu, () => (_system.CpuUsage(), _system.CpuUtility()), out var cpu))
        {
            (usage, utility) = cpu;
            if (usage is { } u)
            {
                samples[8].Set(u);
                samples[9].Set(utility ?? u);
            }
        }
        else
        {
            Fail(samples[8..10]);
        }

        Memory(ref samples[10], Part.MemoryUsage, _system.MemoryUsage);
        Memory(ref samples[11], Part.MemoryCompression, _system.MemoryCompression);

        if (Try(Part.Processes, _system.Processes, out var processes))
        {
            Tables(samples[12..15], processes, elapsed, usage, utility);
        }
        else
        {
            // The next round that reads them starts afresh: its interval would be longer than its elapsed time.
            _last = [];
            _recent.Clear();
            _listed = false;
            Fail(samples[12..15]);
        }
    }

    /// <summary>The three tables, from the second round on: each process's share since the last, summed per name.</summary>
    private void Tables(Span<SensorSample> tables, IReadOnlyList<ProcessReading> processes, TimeSpan elapsed, double? systemUsage, double? systemUtility)
    {
        var processors = Math.Max(1, _system.ProcessorCount);
        var perCpu = _system.ProcessCpuPerCpu;
        var capacity = elapsed.TotalSeconds * processors;
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        var next = new Dictionary<int, (ulong Started, TimeSpan Cpu)>(processes.Count);
        foreach (var p in processes)
        {
            if (p.Name is not { Length: > 0 } name)
                continue;
            next[p.Pid] = (p.Started, p.CpuTime);
            // A process new since the last round (or a pid used again) has no interval yet: left out this round, as 1.x.
            if (!_last.TryGetValue(p.Pid, out var last) || last.Started != p.Started || p.CpuTime < last.Cpu)
                continue;

            var share = capacity > 0 ? (p.CpuTime - last.Cpu).TotalSeconds / capacity * 100 : 0;
            // Shown per CPU where the system counts so (macOS): the share of every CPU times their count.
            var shown = perCpu ? share * processors : share;
            if (!rows.TryGetValue(name, out var row))
                rows.Add(name, row = new Row(name));
            row.Usage += shown;
            // 1.x's: the system's utility in the share this process has of its usage; without one, the usage.
            row.Utility += systemUtility is not { } utility ? shown
                : systemUsage is > 0 && share <= systemUsage ? share / systemUsage.Value * utility : 0;
            row.Memory += p.MemoryBytes;
        }

        _last = next;
        if (!_listed || elapsed <= TimeSpan.Zero)
        {
            // The first round has no interval: no table yet.
            _listed = true;
            return;
        }

        // Each name's CPU figures, the average of its appearances in the last CpuRounds rounds, so the order steadies.
        _recent.Enqueue(rows.Values.ToDictionary(r => r.Name, r => (r.Usage, r.Utility), StringComparer.Ordinal));
        if (_recent.Count > SystemInfoSource.CpuRounds)
            _recent.Dequeue();
        foreach (var row in rows.Values)
        {
            var figures = _recent.Where(round => round.ContainsKey(row.Name)).Select(round => round[row.Name]).ToList();
            row.Usage = figures.Average(f => f.Usage);
            row.Utility = figures.Average(f => f.Utility);
        }

        var excluded = Excluded();
        var all = rows.Values.Where(r => !excluded.Contains(r.Name)).ToList();
        tables[0].SetTable(Table(all.OrderByDescending(r => r.Usage).ThenBy(r => r.Name, StringComparer.Ordinal)), SystemInfoSource.TableFormat);
        tables[1].SetTable(Table(all.OrderByDescending(r => r.Utility).ThenBy(r => r.Name, StringComparer.Ordinal)), SystemInfoSource.TableFormat);
        tables[2].SetTable(Table(all.OrderByDescending(r => r.Memory).ThenBy(r => r.Name, StringComparer.Ordinal)), SystemInfoSource.TableFormat);
    }

    /// <summary>A new table of <paramref name="rows"/> (the first <see cref="SystemInfoSource.MaxRows"/>), in the 1.x plugin's columns and cells.</summary>
    private static DataTable Table(IEnumerable<Row> rows)
    {
        var table = new DataTable();
        table.Columns.Add("Process Name", typeof(PluginText));
        table.Columns.Add("Usage", typeof(PluginSensor));
        table.Columns.Add("Utility", typeof(PluginSensor));
        table.Columns.Add("Memory", typeof(PluginSensor));
        foreach (var row in rows.Take(SystemInfoSource.MaxRows))
        {
            table.Rows.Add(
                new PluginText("Process Name", row.Name),
                new PluginSensor("Usage", (float)row.Usage, SystemInfoSource.PercentUnit),
                new PluginSensor("Utility", (float)row.Utility, SystemInfoSource.PercentUnit),
                MemoryCell(row.Memory));
        }

        return table;
    }

    /// <summary>A Memory cell: megabytes, or gigabytes from 1,024 MB (as the megabytes cell would read, to one decimal) on.</summary>
    private static PluginSensor MemoryCell(long bytes)
    {
        var megabytes = Megabytes(bytes);
        // The cell's own value and rounding (PluginSensor.ToString), so a cell never reads 1024.0 MB.
        return Math.Round((float)megabytes, 1) >= 1024
            ? new PluginSensor("Memory", (float)(megabytes / 1024), SystemInfoSource.GigabyteUnit)
            : new PluginSensor("Memory", (float)megabytes, SystemInfoSource.MemoryUnit);
    }

    private static double Megabytes(long bytes) => bytes / 1024d / 1024d;

    private void Count(ref SensorSample sample, Part part, Func<int?> read)
    {
        if (!Try(part, read, out var count))
            sample.Outcome = SampleOutcome.Failed;
        else if (count is { } n)
            sample.Set(n);
        else
            sample.SetUnavailable();
    }

    private void Memory(ref SensorSample sample, Part part, Func<long> read)
    {
        if (Try(part, read, out var bytes))
            sample.Set(Megabytes(bytes));
        else
            sample.Outcome = SampleOutcome.Failed;
    }

    private static void Fail(Span<SensorSample> samples)
    {
        foreach (ref var s in samples)
            s.Outcome = SampleOutcome.Failed;
    }

    /// <summary>Reads one part; one that throws is logged once while it keeps failing.</summary>
    private bool Try<T>(Part part, Func<T> read, out T value)
    {
        try
        {
            value = read();
            if (_failing.Remove(part))
                _log.Information("System Info: reading {Part} works again", part);
            return true;
        }
        catch (Exception ex)
        {
            if (_failing.Add(part))
                _log.Warning(ex, "System Info: reading {Part} failed: {Message}", part, ex.Message);
            value = default!;
            return false;
        }
    }

    public void Dispose()
    {
    }
}
