using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.SystemInfo;

/// <summary>
/// The memory a process uses as its operating system's own tool shows it, in bytes: on macOS its physical footprint
/// (Activity Monitor's Memory column, graphics memory the process owns included: <c>proc_pid_rusage</c>'s
/// <c>ri_phys_footprint</c>, as System Info's process tables read it), on Windows its private working set (Task
/// Manager's Memory column: <c>GetProcessMemoryInfo</c> with <c>PROCESS_MEMORY_COUNTERS_EX2</c>, Windows 10 version 1809
/// and later, before which Windows has none), on Linux its resident set (<c>top</c>'s RES). Any process, the engine's
/// own or one it started.
/// </summary>
public static class ProcessMemory
{
    /// <summary><c>PROCESS_MEMORY_COUNTERS_EX2</c> (psapi.h; Windows 10 1809 and later), pinned by the tests to the 64-bit headers' layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCountersEx2 counters, uint size);

    /// <summary>
    /// <paramref name="process"/>'s memory, as its OS's tool shows it; 0 when it can't be read (the process has exited
    /// or is refused to this one, or Windows is older than 1809).
    /// </summary>
    public static long Of(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (OperatingSystem.IsMacOS())
                return MacProcessSampler.Footprint(process.Id);
            if (OperatingSystem.IsWindows())
                return PrivateWorkingSet(process.SafeHandle);
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Exited, or never started, or its handle refused.
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long PrivateWorkingSet(SafeProcessHandle handle)
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        // Before 1809 the call refuses this size, or leaves the private working set 0: nothing read either way.
        if (K32GetProcessMemoryInfo(handle, ref counters, counters.Cb) == 0)
            return 0;
        return (long)counters.PrivateWorkingSetSize;
    }
}
