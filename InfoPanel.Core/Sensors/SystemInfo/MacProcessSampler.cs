using InfoPanel.Sensors.MacOS;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// macOS, through public calls and as the normal user (no permission prompt): the uptime from <c>kern.boottime</c>
/// (sleep included, as Windows counts it); the processes from <c>proc_listallpids</c> (every user's); the CPUs' busy
/// share from the host's CPU ticks (<c>host_processor_info</c>, as the macOS source's CPU Load); app memory (internal less
/// purgeable pages) and the compressor's pages from <c>host_statistics64</c>, as Activity Monitor's App Memory and
/// Compressed; and each process's name (its executable's whole file name from <c>proc_pidpath</c>, else
/// <c>proc_name</c>'s, which the kernel cuts at 31 bytes), CPU time (user and system, converted with
/// <c>mach_timebase_info</c>) and physical footprint (Activity Monitor's Memory) from <c>proc_pid_rusage</c>. An
/// unprivileged process reads only its own user's processes so (the others', root's included, are refused): the
/// tables list this user's processes, and Thread Count, which would be a partial sum, isn't read. macOS has no handles
/// and no CPU utility counter.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacProcessSampler : IProcessSampler
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private const int RusageInfoV0 = 0;
    // proc_name's buffer: the kernel's 2 × MAXCOMLEN name and its terminator, with room.
    private const int NameBytes = 64;
    // proc_pidpath's buffer: PROC_PIDPATHINFO_MAXSIZE (4 × MAXPATHLEN).
    private const int PathBytes = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timeval
    {
        public long Sec;
        public int Usec;
        private int _pad;
    }

    /// <summary><c>struct rusage_info_v0</c> (sys/resource.h); times in Mach absolute time units.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RusageV0
    {
        public fixed byte Uuid[16];
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PkgIdleWkups;
        public ulong InterruptWkups;
        public ulong Pageins;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysFootprint;
        public ulong ProcStartAbstime;
        public ulong ProcExitAbstime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebase
    {
        public uint Numer;
        public uint Denom;
    }

    // Its errno is read where it fails (libproc's wrapper returns 0 then).
    [DllImport(Lib, SetLastError = true)] private static extern int proc_listallpids(int* buffer, int bufferSize);
    [DllImport(Lib)] private static extern int proc_name(int pid, byte* buffer, uint bufferSize);
    [DllImport(Lib)] private static extern int proc_pidpath(int pid, byte* buffer, uint bufferSize);
    [DllImport(Lib)] private static extern int proc_pid_rusage(int pid, int flavor, RusageV0* buffer);
    [DllImport(Lib)] private static extern int mach_timebase_info(MachTimebase* info);

    private static readonly MacProcesses.ListAll NativeListAll = ListAllPids;

    private readonly uint _numer = 1;
    private readonly uint _denom = 1;
    private readonly double _pageSize;
    private int[] _pids = new int[1024];
    private uint[] _ticks = [];
    private bool _primed;

    public MacProcessSampler()
    {
        MachTimebase timebase;
        if (mach_timebase_info(&timebase) == 0)
            (_numer, _denom) = (timebase.Numer, timebase.Denom);
        _pageSize = LibSystem.TryGetInt64("hw.pagesize\0"u8, out var page) && page > 0 ? page : 16384;
    }

    public int ProcessorCount => Environment.ProcessorCount;

    /// <summary>Activity Monitor's: a busy thread reads 100 %.</summary>
    public bool ProcessCpuPerCpu => true;

    public TimeSpan Uptime()
    {
        if (!LibSystem.TryGet<Timeval>("kern.boottime\0"u8, out var boot))
            throw new InvalidOperationException("kern.boottime can't be read");
        var booted = DateTimeOffset.FromUnixTimeSeconds(boot.Sec).AddTicks(boot.Usec * 10L);
        var uptime = DateTimeOffset.UtcNow - booted;
        return uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
    }

    public int ProcessCount() => ListPids().Length;

    public int? ThreadCount() => null;

    public int? HandleCount() => null;

    public double? CpuUsage()
    {
        if (LibSystem.host_processor_info(LibSystem.HostSelf, LibSystem.ProcessorCpuLoadInfo, out var count, out var info, out var infoCount) != 0)
            throw new InvalidOperationException("host_processor_info failed");
        try
        {
            var ticks = new ReadOnlySpan<uint>((void*)info, (int)infoCount);
            var cpus = Math.Min((int)count, ticks.Length / LibSystem.CpuStateMax);
            var n = cpus * LibSystem.CpuStateMax;
            if (!_primed || _ticks.Length != n)
            {
                _ticks = ticks[..n].ToArray();
                _primed = true;
                return null;
            }

            double busy = 0, total = 0;
            for (var o = 0; o < n; o += LibSystem.CpuStateMax)
            {
                // Counters are 32-bit and wrap; unchecked subtraction gives the delta across a wrap.
                double user = unchecked(ticks[o + LibSystem.CpuStateUser] - _ticks[o + LibSystem.CpuStateUser]);
                double system = unchecked(ticks[o + LibSystem.CpuStateSystem] - _ticks[o + LibSystem.CpuStateSystem]);
                double idle = unchecked(ticks[o + LibSystem.CpuStateIdle] - _ticks[o + LibSystem.CpuStateIdle]);
                double nice = unchecked(ticks[o + LibSystem.CpuStateNice] - _ticks[o + LibSystem.CpuStateNice]);
                busy += user + system + nice;
                total += user + system + nice + idle;
            }

            ticks[..n].CopyTo(_ticks);
            return total > 0 ? busy / total * 100 : 0;
        }
        finally
        {
            LibSystem.vm_deallocate(LibSystem.TaskSelf, (nuint)info, infoCount * sizeof(uint));
        }
    }

    public double? CpuUtility() => null;

    public long MemoryUsage()
    {
        var vm = VmInfo();
        return (long)(Math.Max(0, (long)vm.InternalPageCount - vm.PurgeableCount) * _pageSize);
    }

    public long MemoryCompression() => (long)(VmInfo().CompressorPageCount * _pageSize);

    public IReadOnlyList<ProcessReading> Processes()
    {
        var pids = ListPids();
        var list = new List<ProcessReading>(pids.Length);
        var name = stackalloc byte[NameBytes];
        var path = stackalloc byte[PathBytes];
        foreach (var pid in pids)
        {
            if (pid <= 0)
                continue;
            RusageV0 usage;
            // Refused (another user's process) or gone since it was listed: left out.
            if (proc_pid_rusage(pid, RusageInfoV0, &usage) != 0)
                continue;
            // The executable's whole name from its path; the kernel's short name only where the path can't be read.
            var pathLength = proc_pidpath(pid, path, PathBytes);
            var length = proc_name(pid, name, NameBytes);
            var processName = MacProcesses.Name(new ReadOnlySpan<byte>(path, Math.Clamp(pathLength, 0, PathBytes)), new ReadOnlySpan<byte>(name, Math.Clamp(length, 0, NameBytes)));
            if (processName.Length == 0)
                continue;
            var cpu = MacProcesses.CpuTime(usage.UserTime + usage.SystemTime, _numer, _denom);
            list.Add(new ProcessReading(pid, processName, cpu, (long)usage.PhysFootprint, usage.ProcStartAbstime));
        }

        return list;
    }

    /// <summary>
    /// A process's physical footprint (Activity Monitor's Memory), in bytes, from the same <c>proc_pid_rusage</c> read as
    /// <see cref="Processes"/>; 0 when it can't be read (gone, or another user's).
    /// </summary>
    internal static long Footprint(int pid)
    {
        RusageV0 usage;
        return pid > 0 && proc_pid_rusage(pid, RusageInfoV0, &usage) == 0 ? (long)usage.PhysFootprint : 0;
    }

    private VmStatistics64 VmInfo()
    {
        VmStatistics64 vm;
        var count = (uint)(sizeof(VmStatistics64) / sizeof(int));
        if (LibSystem.host_statistics64(LibSystem.HostSelf, LibSystem.HostVmInfo64, &vm, ref count) != 0)
            throw new InvalidOperationException("host_statistics64 failed");
        return vm;
    }

    private ReadOnlySpan<int> ListPids() => MacProcesses.ListPids(NativeListAll, ref _pids);

    private static int ListAllPids(Span<int> buffer, out int errno)
    {
        int got;
        fixed (int* p = buffer)
            got = proc_listallpids(p, buffer.Length * sizeof(int));
        errno = got > 0 ? 0 : Marshal.GetLastPInvokeError();
        return got;
    }
}

/// <summary>
/// <see cref="MacProcessSampler"/>'s arithmetic, apart from its native calls (a test's): the pid listing over
/// <c>proc_listallpids</c>, and CPU time from Mach absolute time units.
/// </summary>
internal static class MacProcesses
{
    /// <summary>
    /// <c>proc_listallpids</c>: lists every process's pid into <paramref name="buffer"/> (an empty one: the count's size
    /// hint) and returns how many it listed; where it failed, 0 or less (libproc's wrapper turns a failed call into 0),
    /// with <paramref name="errno"/>.
    /// </summary>
    internal delegate int ListAll(Span<int> buffer, out int errno);

    /// <summary>How many times a full buffer is listed again, each time twice the size, before what it holds is read.</summary>
    internal const int Regrowths = 3;

    /// <summary>
    /// Every process's pid now, into <paramref name="buffer"/>: the second call's count (the first's is a size hint, a
    /// little over). A count of 0 is a failed call, never no processes (this one runs): it throws, so the readings fail
    /// that round. A full buffer may have been cut short: it is listed again, twice the size, at most
    /// <see cref="Regrowths"/> times, then what the last listing holds is read.
    /// </summary>
    internal static ReadOnlySpan<int> ListPids(ListAll list, ref int[] buffer)
    {
        var hint = list(default, out var errno);
        if (hint <= 0)
            throw Failed(errno);
        if (buffer.Length < hint + 64)
            buffer = new int[hint + 256];
        for (var regrowth = 0; ; regrowth++)
        {
            var got = list(buffer, out errno);
            if (got <= 0)
                throw Failed(errno);
            if (got < buffer.Length || regrowth == Regrowths)
                return buffer.AsSpan(0, Math.Min(got, buffer.Length));
            buffer = new int[buffer.Length * 2];
        }
    }

    private static InvalidOperationException Failed(int errno) => new($"proc_listallpids failed (errno {errno})");

    /// <summary>
    /// A process's name: its executable's file name, the last component of its <paramref name="path"/>
    /// (<c>proc_pidpath</c>'s, whole), else, where the path can't be read or names no file, the kernel's
    /// <paramref name="shortName"/> (<c>proc_name</c>'s, cut at 31 bytes).
    /// </summary>
    internal static string Name(ReadOnlySpan<byte> path, ReadOnlySpan<byte> shortName)
    {
        var file = path[(path.LastIndexOf((byte)'/') + 1)..];
        return System.Text.Encoding.UTF8.GetString(file.IsEmpty ? shortName : file);
    }

    /// <summary>
    /// The CPU time <paramref name="ticks"/> Mach absolute time units hold, at <paramref name="numer"/>/<paramref name="denom"/>
    /// ns a unit (1 where none was read), in whole integers: a double of 125/3 (Apple silicon's) loses a 100 ns tick.
    /// </summary>
    internal static TimeSpan CpuTime(ulong ticks, uint numer, uint denom)
    {
        if (denom == 0)
            (numer, denom) = (1, 1);
        return TimeSpan.FromTicks((long)((UInt128)ticks * numer / ((UInt128)denom * 100)));
    }
}
