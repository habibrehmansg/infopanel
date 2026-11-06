using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// One process as read in one round: its pid, its name, the CPU time it has used since it started, the memory it uses
/// (Windows: its private working set; macOS: its physical footprint, Activity Monitor's Memory) and when it started
/// (any value the system keeps per process, so a pid used again by a new process isn't taken for the old one).
/// </summary>
public readonly record struct ProcessReading(int Pid, string Name, TimeSpan CpuTime, long MemoryBytes, ulong Started = 0);

/// <summary>
/// What <see cref="SystemInfoSource"/> reads from the operating system, a call per reading, each read when asked; any
/// call may throw (its readings fail that round). Called from one thread at a time.
/// </summary>
public interface IProcessSampler
{
    /// <summary>The logical CPUs: the CPU time a second of wall time holds, in seconds.</summary>
    int ProcessorCount { get; }

    /// <summary>The time since the computer started, its sleep included.</summary>
    TimeSpan Uptime();

    /// <summary>How many processes run now, every user's.</summary>
    int ProcessCount();

    /// <summary>The threads of every process; null where they can't all be read (macOS: other users' processes).</summary>
    int? ThreadCount();

    /// <summary>The handles every process holds; null where the system has none (macOS).</summary>
    int? HandleCount();

    /// <summary>The CPUs' busy share since the previous call, in %; null on the first call (no interval yet).</summary>
    double? CpuUsage();

    /// <summary>
    /// The CPUs' utility since the previous call (Windows' frequency-scaled "% Processor Utility"), in %; null where the
    /// system has none (macOS), which reads CPU Usage for it.
    /// </summary>
    double? CpuUtility();

    /// <summary>The memory the processes use, in bytes (Windows: their private working sets; macOS: app memory).</summary>
    long MemoryUsage();

    /// <summary>The compressed memory, in bytes (Windows: the Memory Compression process's; macOS: the compressor's).</summary>
    long MemoryCompression();

    /// <summary>The processes it can read (macOS: this user's only), each with its CPU time so far and its memory.</summary>
    IReadOnlyList<ProcessReading> Processes();
}
