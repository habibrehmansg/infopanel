using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// The engine's own process as <see cref="EngineSource"/> reads it, a call per reading, each read when asked; any
/// reading may throw (its readings fail that round). Called from one thread at a time.
/// </summary>
public interface IEngineProcess
{
    /// <summary>
    /// The machine's logical CPUs, as the OS's own tool counts them (<see cref="EngineSource.MachineProcessors"/>): the CPU
    /// time a second of wall time holds, in seconds.
    /// </summary>
    int ProcessorCount { get; }

    /// <summary>
    /// How the OS's own tool counts the process's CPU: <see langword="true"/> per CPU (Activity Monitor, <c>top</c>: a
    /// busy CPU is 100 %, up to 100 × <see cref="ProcessorCount"/>), <see langword="false"/> as a share of the whole
    /// machine (Task Manager: 0–100 %). System Info's tables count so too (<see cref="IProcessSampler.ProcessCpuPerCpu"/>).
    /// </summary>
    bool CpuPerCpu { get; }

    /// <summary>The CPU time the process has used since it started, user and system.</summary>
    TimeSpan CpuTime();

    /// <summary>
    /// The process's memory as the OS's own tool shows it, in bytes (<see cref="ProcessMemory"/>: the physical footprint on
    /// macOS, the private working set on Windows, the resident set on Linux); 0 or less when the read failed without
    /// throwing.
    /// </summary>
    long MemoryBytes();
}

/// <summary>
/// The engine's own usage, <c>infopanel.engine</c> ("InfoPanel engine"): a <see cref="PolledSensorSource"/> polled at
/// System Info's interval, on every operating system and in mock mode and a replay too (it reads the engine's own
/// process, not the computer's sensors), with two numbers under <c>infopanel:/engine/{entry}</c>: <c>cpu</c>, the
/// process's CPU time since the last round as the OS's own tool counts it (per CPU on macOS and Linux, as Activity
/// Monitor and <c>top</c> do: 100 % is one busy CPU, up to 100 × the CPUs; on Windows a share of every CPU's, the whole
/// machine, 0–100 %, as Task Manager does; System Info's tables count each so), averaged over the last
/// <see cref="SystemInfoSource.CpuRounds"/> rounds as System Info's tables are, read from the second round; and
/// <c>memory</c>, its memory as that tool shows it (<see cref="ProcessMemory"/>), in megabytes, in System Info's units.
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
    /// The engine's own process: its CPU time through .NET (<see cref="Environment.CpuUsage"/>, about this process alone),
    /// over the machine's logical CPUs as the OS counts them (<see cref="MachineProcessors"/>), per CPU off Windows; its memory
    /// through <see cref="ProcessMemory"/>.
    /// </summary>
    public static IEngineProcess CurrentProcess() => new Current();

    /// <summary>
    /// The machine's logical CPUs as the OS's own tool counts them: on Windows every processor group's; on macOS
    /// <c>hw.logicalcpu</c>; on Linux the online CPUs (<c>sysconf(_SC_NPROCESSORS_ONLN)</c>). Never
    /// <see cref="Environment.ProcessorCount"/> where the OS answers, which follows <c>DOTNET_PROCESSOR_COUNT</c>, the
    /// start-up affinity and a Linux CPU quota, and would cap a per-CPU reading below what the OS's tool shows.
    /// </summary>
    internal static int MachineProcessors()
    {
        if (OperatingSystem.IsWindows())
            return WinProcessSampler.MachineProcessors();
        if (OperatingSystem.IsMacOS() && InfoPanel.Sensors.MacOS.LibSystem.TryGetInt64("hw.logicalcpu\0"u8, out var mac) && mac > 0)
            return (int)mac;
        if (OperatingSystem.IsLinux() && sysconf(ScNprocessorsOnlnLinux) is var online and > 0)
            return (int)online;
        return Environment.ProcessorCount;
    }

    /// <summary><c>_SC_NPROCESSORS_ONLN</c> on Linux (glibc and musl).</summary>
    private const int ScNprocessorsOnlnLinux = 84;

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern long sysconf(int name);

    private sealed class Current : IEngineProcess
    {
        private readonly Process _self = Process.GetCurrentProcess();

        public int ProcessorCount { get; } = Math.Max(1, MachineProcessors());

        public bool CpuPerCpu { get; } = !OperatingSystem.IsWindows();

        public TimeSpan CpuTime() => Environment.CpuUsage.TotalTime;

        public long MemoryBytes() => ProcessMemory.Of(_self);
    }
}

/// <summary>
/// Reads <see cref="EngineSource"/>'s two entries each round from an <see cref="IEngineProcess"/>. A reading that throws
/// fails that round, logged once while it fails; the CPU's next good round starts a new interval. A memory of 0 or less
/// fails the memory alone that round, logged once while it fails.
/// </summary>
internal sealed class EngineSampler(IEngineProcess process, ILogger? log) : ISensorSampler
{
    private readonly IEngineProcess _process = process ?? throw new ArgumentNullException(nameof(process));
    private readonly ILogger _log = (log ?? Log.Logger).ForContext<EngineSampler>();
    private readonly Queue<double> _recent = new();
    private TimeSpan? _lastCpu;
    private bool _failing;
    private bool _memoryFailing;

    public IReadOnlyList<SensorDefinition> Discover() =>
        EngineSource.Entries.Select(e => new SensorDefinition(e.Id, EngineSource.Name, e.Name, e.Unit, SensorKind.Number)).ToList();

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        if (samples.Length < EngineSource.Entries.Count)
            return;

        TimeSpan cpu;
        long memory;
        try
        {
            cpu = _process.CpuTime();
            memory = _process.MemoryBytes();
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

        // The CPU from the second round on: the share of every CPU's time this process used since the last (0–100), and
        // per CPU that share times the CPUs (0–100 × the CPUs), as the OS's own tool counts it.
        var cpus = Math.Max(1, _process.ProcessorCount);
        var capacity = elapsed.TotalSeconds * cpus;
        if (_lastCpu is { } last && cpu >= last && capacity > 0)
        {
            var share = Math.Clamp((cpu - last).TotalSeconds / capacity * 100, 0, 100);
            _recent.Enqueue(_process.CpuPerCpu ? share * cpus : share);
            if (_recent.Count > SystemInfoSource.CpuRounds)
                _recent.Dequeue();
            samples[0].Set(_recent.Average());
        }
        _lastCpu = cpu;

        // A process always uses memory: 0 or less is a read that failed without throwing (refused by the OS, or Windows
        // before 1809, which has no private working set), so the memory fails that round rather than reading a live 0,
        // logged once while it fails.
        if (memory > 0)
        {
            samples[1].Set(memory / 1024d / 1024d);
            _memoryFailing = false;
        }
        else
        {
            samples[1].Outcome = SampleOutcome.Failed;
            if (!_memoryFailing)
            {
                _memoryFailing = true;
                _log.Information("InfoPanel engine: the engine's memory can't be read here; it reads as unavailable");
            }
        }
    }

    public void Dispose()
    {
    }
}
