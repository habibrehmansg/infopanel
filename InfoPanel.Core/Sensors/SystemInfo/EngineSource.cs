using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// The engine's own process as <see cref="EngineSource"/> reads it, a call per reading, each read when asked; any
/// reading may throw (its readings fail that round). Called from one thread at a time.
/// </summary>
public interface IEngineProcess
{
    /// <summary>The machine's logical CPUs, as System Info counts them: the CPU time a second of wall time holds, in seconds.</summary>
    int ProcessorCount { get; }

    /// <summary>The CPU time the process has used since it started, user and system.</summary>
    TimeSpan CpuTime();

    /// <summary>
    /// The process's resident memory, in bytes: its working set on Windows, its resident set size on macOS and Linux;
    /// 0 or less when the read failed without throwing.
    /// </summary>
    long ResidentBytes();
}

/// <summary>
/// The engine's own usage, <c>infopanel.engine</c> ("InfoPanel engine"): a <see cref="PolledSensorSource"/> polled at
/// System Info's interval, on every operating system and in mock mode and a replay too (it reads the engine's own
/// process, not the computer's sensors), with two numbers under <c>plugin:/infopanel.engine/{entry}</c>: <c>cpu</c>, the
/// process's CPU time since the last round as a share of every CPU's (the whole machine, 0–100 %, as System Info's
/// Windows tables count it), averaged over the last <see cref="SystemInfoSource.CpuRounds"/> rounds as System Info's
/// tables are, read from the second round; and <c>memory</c>, its resident memory in megabytes, in System Info's units.
/// </summary>
public static class EngineSource
{
    public const string SourceId = "infopanel.engine";

    /// <summary>The source's name, and its sensors' group.</summary>
    public const string Name = "InfoPanel engine";

    /// <summary>System Info's: the CPU figure is the average of the last 3 s.</summary>
    public static readonly TimeSpan Interval = SystemInfoSource.Interval;

    /// <summary>The entries in the order the source lists them: id, name and unit.</summary>
    public static IReadOnlyList<(string Id, string Name, string Unit)> Entries { get; } =
    [
        ("cpu", "CPU", SystemInfoSource.PercentUnit),
        ("memory", "Memory", SystemInfoSource.MemoryUnit),
    ];

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>The key of an entry, from the engine's key builder.</summary>
    public static SensorKey KeyOf(string entry) => Space.KeyOf(entry);

    /// <summary>A source over <paramref name="process"/> (a test's, or <see cref="CurrentProcess"/>).</summary>
    public static PolledSensorSource Create(IEngineProcess process, TimeProvider? time = null, ILogger? log = null) =>
        new(SourceId, () => new EngineSampler(process, log), new PolledSensorSourceOptions { Interval = Interval }, time, Space, log);

    /// <summary>The source over the engine's own process.</summary>
    public static PolledSensorSource CreateSystem(TimeProvider? time = null) => Create(CurrentProcess(), time);

    /// <summary>
    /// The engine's own process, read through .NET (<see cref="Environment.CpuUsage"/>, <see cref="Environment.WorkingSet"/>,
    /// each a call about this process alone), over the logical CPUs System Info counts (on Windows every processor group's).
    /// </summary>
    public static IEngineProcess CurrentProcess() => new Current();

    private sealed class Current : IEngineProcess
    {
        public int ProcessorCount { get; } = Math.Max(1, OperatingSystem.IsWindows() ? WinProcessSampler.MachineProcessors() : Environment.ProcessorCount);

        public TimeSpan CpuTime() => Environment.CpuUsage.TotalTime;

        public long ResidentBytes() => Environment.WorkingSet;
    }
}

/// <summary>
/// Reads <see cref="EngineSource"/>'s two entries each round from an <see cref="IEngineProcess"/>. A reading that throws
/// fails that round, logged once while it fails; the CPU's next good round starts a new interval. A resident memory of 0
/// or less fails the memory alone that round.
/// </summary>
internal sealed class EngineSampler(IEngineProcess process, ILogger? log) : ISensorSampler
{
    private readonly IEngineProcess _process = process ?? throw new ArgumentNullException(nameof(process));
    private readonly ILogger _log = (log ?? Log.Logger).ForContext<EngineSampler>();
    private readonly Queue<double> _recent = new();
    private TimeSpan? _lastCpu;
    private bool _failing;

    public IReadOnlyList<SensorDefinition> Discover() =>
        EngineSource.Entries.Select(e => new SensorDefinition(e.Id, EngineSource.Name, e.Name, e.Unit, SensorKind.Number)).ToList();

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        if (samples.Length < EngineSource.Entries.Count)
            return;

        TimeSpan cpu;
        long resident;
        try
        {
            cpu = _process.CpuTime();
            resident = _process.ResidentBytes();
            if (_failing)
            {
                _failing = false;
                _log.Information("InfoPanel engine: reading the engine's process works again");
            }
        }
        catch (Exception ex)
        {
            if (!_failing)
            {
                _failing = true;
                _log.Warning(ex, "InfoPanel engine: reading the engine's process failed: {Message}", ex.Message);
            }
            _lastCpu = null;
            _recent.Clear();
            samples[0].Outcome = SampleOutcome.Failed;
            samples[1].Outcome = SampleOutcome.Failed;
            return;
        }

        // The CPU from the second round on: the share of every CPU's time this process used since the last.
        var capacity = elapsed.TotalSeconds * Math.Max(1, _process.ProcessorCount);
        if (_lastCpu is { } last && cpu >= last && capacity > 0)
        {
            _recent.Enqueue(Math.Clamp((cpu - last).TotalSeconds / capacity * 100, 0, 100));
            if (_recent.Count > SystemInfoSource.CpuRounds)
                _recent.Dequeue();
            samples[0].Set(_recent.Average());
        }
        _lastCpu = cpu;

        // A process always has resident memory: 0 or less is a read that failed without throwing (.NET's working set
        // reads 0 when the OS refuses it), so the memory fails that round rather than reading a live 0.
        if (resident > 0)
            samples[1].Set(resident / 1024d / 1024d);
        else
            samples[1].Outcome = SampleOutcome.Failed;
    }

    public void Dispose()
    {
    }
}
