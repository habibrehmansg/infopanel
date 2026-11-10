using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// Windows, as the 1.x System Info plugin read it: the uptime from <see cref="Environment.TickCount64"/> (sleep
/// included); the process, thread and handle counts from <see cref="Process.GetProcesses()"/> (a process whose threads
/// or handles can't be read is left out of those sums); the CPUs' busy share and utility from the
/// <c>Processor Information</c> category's <c>_Total</c> (<c>% Processor Time</c>, and the frequency-scaled
/// <c>% Processor Utility</c>); and each process's name (its instance's, before the first <c>:</c>), id, CPU time
/// (<c>% Processor Time</c>'s raw value, in 100 ns units), private working set and start (<c>Elapsed Time</c>'s raw
/// value) from one read of the <c>Process V2</c> category, whose <c>_Total</c> gives the memory the processes use and
/// whose <c>Memory Compression</c> instance the compressed memory. A process's CPU share is of the machine's logical
/// processors, every processor group's active ones (as the process times count them), not
/// <see cref="Environment.ProcessorCount"/>'s, which are those this process may run on. A round's readings come from
/// one process list and one category read, each read at its round's first reading and kept until
/// <see cref="BeginRound"/> opens the next, however long a read takes or however soon the next round starts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinProcessSampler : IProcessSampler
{
    private const string ProcessCategory = "Process V2";
    private const string Total = "_Total";
    private const string MemoryCompressionName = "Memory Compression";
    private const ushort AllProcessorGroups = 0xFFFF;

    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);

    private PerformanceCounter? _usage;
    private PerformanceCounter? _utility;
    private bool _usageRead;
    private PerformanceCounterCategory? _category;

    private readonly Lazy<int> _processors;
    private readonly Func<(int Count, int Threads, int Handles)> _readCounts;
    private readonly Func<IReadOnlyList<CategoryInstance>> _readCategory;

    // This round's reads, null until its first reading asks for them.
    private (int Count, int Threads, int Handles)? _counts;
    private IReadOnlyList<CategoryInstance>? _read;

    public WinProcessSampler() : this(null, null, null)
    {
    }

    /// <summary>
    /// The seams the tests set (each <see langword="null"/> is this PC's): the processor count, the process list's
    /// counts and the <c>Process V2</c> read.
    /// </summary>
    internal WinProcessSampler(Func<int>? processors, Func<(int Count, int Threads, int Handles)>? readCounts,
        Func<IReadOnlyList<CategoryInstance>>? readCategory)
    {
        _processors = new Lazy<int>(() => Math.Max(1, (processors ?? MachineProcessors)()));
        _readCounts = readCounts ?? ReadCounts;
        _readCategory = readCategory ?? ReadCategory;
    }

    /// <summary>One instance of a <c>Process V2</c> read: its private working set, and its CPU time (100 ns units), process id and start where the read has them.</summary>
    internal readonly record struct CategoryInstance(string Instance, long PrivateBytes, long? CpuTicks, long? Pid, long? Started);

    /// <summary>The machine's logical processors, read once.</summary>
    public int ProcessorCount => _processors.Value;

    /// <summary>The next reading reads the process list and the category afresh, once each for the round.</summary>
    public void BeginRound()
    {
        _counts = null;
        _read = null;
    }

    public TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public int ProcessCount() => Counts().Count;

    public int? ThreadCount() => Counts().Threads;

    public int? HandleCount() => Counts().Handles;

    public double? CpuUsage()
    {
        _usage ??= new PerformanceCounter("Processor Information", "% Processor Time", Total);
        var usage = _usage.NextValue();
        // A counter's first value has no interval (it reads 0).
        if (!_usageRead)
        {
            _usageRead = true;
            return null;
        }

        return usage;
    }

    public double? CpuUtility()
    {
        _utility ??= new PerformanceCounter("Processor Information", "% Processor Utility", Total);
        return _utility.NextValue();
    }

    public long MemoryUsage() => Memory(Category(), Total);

    public long MemoryCompression() => Memory(Category(), MemoryCompressionName);

    public IReadOnlyList<ProcessReading> Processes()
    {
        var read = Category();
        var list = new List<ProcessReading>(read.Count);
        foreach (var instance in read)
        {
            if (instance.Instance == Total || instance is not { Pid: { } id, CpuTicks: { } cpu })
                continue;
            list.Add(new ProcessReading((int)id, NameOf(instance.Instance), TimeSpan.FromTicks(cpu), instance.PrivateBytes,
                instance.Started is { } start ? unchecked((ulong)start) : 0));
        }

        return list;
    }

    /// <summary>A <c>Process V2</c> instance's process name: <c>{name}:{id}</c>'s name, as the 1.x plugin split it.</summary>
    internal static string NameOf(string instance) => instance.Split(':', 2)[0];

    /// <summary>The working sets of the instances named <paramref name="name"/>, summed (<c>_Total</c>'s is every process's).</summary>
    private static long Memory(IReadOnlyList<CategoryInstance> read, string name)
    {
        long bytes = 0;
        foreach (var instance in read)
        {
            if (NameOf(instance.Instance) == name)
                bytes += instance.PrivateBytes;
        }

        return bytes;
    }

    /// <summary>This round's process list counts, read at its first reading (one that throws is read again when asked).</summary>
    private (int Count, int Threads, int Handles) Counts() => _counts ??= _readCounts();

    /// <summary>This round's <c>Process V2</c> read, read at its first reading (one that throws is read again when asked).</summary>
    private IReadOnlyList<CategoryInstance> Category() => _read ??= _readCategory();

    /// <summary>Every processor group's active logical processors (<see cref="Environment.ProcessorCount"/>'s if it can't be read).</summary>
    internal static int MachineProcessors()
    {
        var count = GetActiveProcessorCount(AllProcessorGroups);
        return count > 0 ? (int)count : Environment.ProcessorCount;
    }

    /// <summary>The process list's length and its processes' threads and handles, summed.</summary>
    private static (int Count, int Threads, int Handles) ReadCounts()
    {
        var processes = Process.GetProcesses();
        int threads = 0, handles = 0;
        foreach (var process in processes)
        {
            try
            {
                threads += process.Threads.Count;
                handles += process.HandleCount;
            }
            catch (Exception)
            {
                // Gone, or not readable (as the 1.x plugin, it counts for nothing).
            }
            finally
            {
                process.Dispose();
            }
        }

        return (processes.Length, threads, handles);
    }

    /// <summary>One read of the <c>Process V2</c> category: every instance with a private working set.</summary>
    private IReadOnlyList<CategoryInstance> ReadCategory()
    {
        _category ??= new PerformanceCounterCategory(ProcessCategory);
        var read = _category.ReadCategory();
        var cpu = read["% Processor Time"];
        var memory = read["Working Set - Private"];
        var ids = read["Process ID"];
        var started = read["Elapsed Time"];
        var list = new List<CategoryInstance>(memory.Count);
        foreach (InstanceData data in memory.Values)
        {
            var name = data.InstanceName;
            list.Add(new CategoryInstance(name, data.RawValue, cpu[name]?.RawValue, ids[name]?.RawValue, started[name]?.RawValue));
        }

        return list;
    }
}
