using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Sensors.SystemInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The memory a process uses as its operating system's own tool shows it (<see cref="ProcessMemory"/>): on macOS the
/// physical footprint (Activity Monitor's Memory), on Windows the private working set (Task Manager's Memory), on Linux
/// the resident set (<c>top</c>'s RES); each live test on its own OS, and the Windows structure's layout on any.
/// </summary>
public sealed class ProcessMemoryTests
{
    /// <summary>
    /// The reader, and the engine's reading (<paramref name="measure"/>), between two instants of
    /// <paramref name="reference"/>: each within 5 % of the range they give.
    /// </summary>
    private static void AssertNear(Func<long> measure, Func<long> reference)
    {
        var before = reference();
        var read = ProcessMemory.Of(Self.Value);
        var after = reference();
        var measured = measure();

        Assert.True(before > 0 && after > 0, $"the reference read {before} and {after}");
        Assert.True(read > 0 && measured > 0, $"the reader read {read} and {measured}");
        var low = Math.Min(before, after) * 0.95;
        var high = Math.Max(before, after) * 1.05;
        Assert.InRange((double)read, low, high);
        Assert.InRange((double)measured, low, high);
    }

    private static readonly Lazy<Process> Self = new(Process.GetCurrentProcess);

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void OnMacOS_ItIsThePhysicalFootprint_AsSystemInfosProcessTablesReadIt()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS reader.");
        var mac = new MacProcessSampler();
        long Footprint() => mac.Processes().Single(p => p.Pid == Environment.ProcessId).MemoryBytes;

        AssertNear(() => EngineSource.CurrentProcess().MemoryBytes(), Footprint);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void OnWindows_ItIsThePrivateWorkingSet_AsTheProcessV2CounterReadsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows reader.");
        var win = new WinProcessSampler();
        long PrivateWorkingSet()
        {
            win.BeginRound();
            return win.Processes().Single(p => p.Pid == Environment.ProcessId).MemoryBytes;
        }

        AssertNear(() => EngineSource.CurrentProcess().MemoryBytes(), PrivateWorkingSet);
    }

    [Fact]
    public void OnLinux_ItIsTheResidentSet()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The Linux reader.");

        AssertNear(() => EngineSource.CurrentProcess().MemoryBytes(), () => Environment.WorkingSet);
    }

    [Fact]
    public void AnotherProcess_IsReadToo_NotOnlyThisOne()
    {
        // A child process the test starts and ends: the reader takes any process, not only the engine's own.
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "30");
        startInfo.RedirectStandardOutput = true;
        using var child = Process.Start(startInfo)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            long read;
            while ((read = ProcessMemory.Of(child)) <= 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.True(read > 0);
            // A small program, far smaller than this test host.
            Assert.True(read < ProcessMemory.Of(Self.Value));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public void AProcessThatHasExited_ReadsNothing()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/usr/bin/true");
        using var child = Process.Start(startInfo)!;
        child.WaitForExit();

        Assert.Equal(0, ProcessMemory.Of(child));
    }

    [Fact]
    public void TheWindowsCounters_HaveTheirWindowsLayout()
    {
        // PROCESS_MEMORY_COUNTERS_EX2 (psapi.h, Windows 10 1809 and later) as the 64-bit headers lay it out.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(96, Unsafe.SizeOf<ProcessMemory.ProcessMemoryCountersEx2>());
        Assert.Equal(0, Offset(nameof(ProcessMemory.ProcessMemoryCountersEx2.Cb)));
        Assert.Equal(16, Offset(nameof(ProcessMemory.ProcessMemoryCountersEx2.WorkingSetSize)));
        Assert.Equal(72, Offset(nameof(ProcessMemory.ProcessMemoryCountersEx2.PrivateUsage)));
        Assert.Equal(80, Offset(nameof(ProcessMemory.ProcessMemoryCountersEx2.PrivateWorkingSetSize)));
        Assert.Equal(88, Offset(nameof(ProcessMemory.ProcessMemoryCountersEx2.SharedCommitUsage)));
    }

    private static int Offset(string field) => (int)Marshal.OffsetOf<ProcessMemory.ProcessMemoryCountersEx2>(field);
}
