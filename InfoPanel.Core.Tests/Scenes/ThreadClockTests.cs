using System.Diagnostics;
using InfoPanel.Scenes;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>The thread's processor clock the watchdog reads, on the OS the tests run on.</summary>
public sealed class ThreadClockTests
{
    [Fact]
    public void The_threads_processor_time_rises_while_it_runs_and_never_passes_the_wall_time()
    {
        // Windows' thread times may count in scheduler quanta (about 15.6 ms); the others' in far less.
        var granularity = TimeSpan.FromMilliseconds(OperatingSystem.IsWindows() ? 32 : 2);
        var wall = Stopwatch.StartNew();
        var before = ThreadClock.Now();
        Assert.NotNull(before);

        // Runs until the thread has used 20 ms, or 5 s have passed on the wall clock (a very busy machine).
        TimeSpan? now = before;
        var spins = 0L;
        while ((now - before)!.Value < TimeSpan.FromMilliseconds(20) && wall.Elapsed < TimeSpan.FromSeconds(5))
        {
            for (var i = 0; i < 10_000; i++) spins += i & 1;
            now = ThreadClock.Now();
        }
        var elapsed = wall.Elapsed;

        Assert.True(spins > 0);
        var used = (now - before)!.Value;
        Assert.True(used >= TimeSpan.FromMilliseconds(20), $"used {used.TotalMilliseconds} ms of processor time in {elapsed.TotalMilliseconds} ms");
        Assert.True(used <= elapsed + granularity, $"used {used.TotalMilliseconds} ms of processor time in {elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void A_32_bit_process_reads_no_thread_time()
    {
        // The Linux timespec is read as two 64-bit fields, which is wrong where they are 32-bit (ARM32): no figure then.
        Assert.Null(ThreadClock.Now(4));
        if (IntPtr.Size == 8) Assert.NotNull(ThreadClock.Now(8));
    }

    [Fact]
    public void The_threads_QoS_class_is_read_on_macOS_only()
    {
        if (OperatingSystem.IsMacOS()) Assert.False(string.IsNullOrEmpty(ThreadClock.QosClass()));
        else Assert.Null(ThreadClock.QosClass());
    }
}
