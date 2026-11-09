using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

// The reader is Windows-only for the product; each test skips elsewhere before it calls it.
#pragma warning disable CA1416

/// <summary>
/// The system information source's Windows reader (<see cref="WinProcessSampler"/>), on this PC, Windows only (skipped
/// elsewhere): the uptime, the process count as the process list has it, threads and handles read, the CPUs' usage from
/// a second call and their utility, the processes' memory and the compressed memory, and every process with its id,
/// name (this test's own among them), CPU time, private working set and a start that stays the same; and the source
/// over it, its 15 entries read live, with a busy process first in Top CPU Usage.
/// </summary>
public sealed class WinProcessSamplerTests
{
    /// <summary>A <c>Process V2</c> read the test sets: each read takes <see cref="ReadMs"/> on the clock and gives the next snapshot.</summary>
    private sealed class FakeCategory
    {
        public long Now;
        public long ReadMs;
        /// <summary>What a count read takes, if not <see cref="ReadMs"/>.</summary>
        public long? CountMs;
        public int Reads;
        public int CountReads;
        public double BusySeconds;

        public IReadOnlyList<WinProcessSampler.CategoryInstance> Read()
        {
            Reads++;
            Now += ReadMs;
            // Each snapshot differs: the busy process's memory is the read's number, in MB.
            long mb = 1024 * 1024;
            return
            [
                new("_Total", (100 + Reads) * mb, null, 0, null),
                new("busy:10", Reads * mb, TimeSpan.FromSeconds(BusySeconds).Ticks, 10, 1000),
                new("idle:11", 100 * mb, 0, 11, 1001),
                new("Memory Compression:12", 8 * mb, 0, 12, 1002),
            ];
        }

        public (int, int, int) Counts()
        {
            CountReads++;
            Now += CountMs ?? ReadMs;
            return (3 + CountReads, 30, 300);
        }
    }

    [Fact]
    public void ProcessShares_AreOfTheMachinesProcessors_NotThoseTheEngineMayRunOn()
    {
        // 16 logical processors on the machine; the engine may be held to fewer (run with DOTNET_PROCESSOR_COUNT=1).
        var category = new FakeCategory();
        var win = new WinProcessSampler(() => 16, category.Counts, category.Read);

        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        using var source = SystemInfoSource.Create(win, time);
        source.Open();
        // A second later, busy has run one CPU's worth: a sixteenth of the machine.
        category.BusySeconds = 1;
        category.Now += 1_000;
        time.Advance(SystemInfoSource.Interval);
        source.Poll();

        var top = source.ReadState(SystemInfoSource.KeyOf("processes", "top-cpu-usage")).Reading.ValueTable!;
        Assert.Equal("busy", ((PluginText)top.Rows[0][0]).Value);
        Assert.Equal(6.25f, ((PluginSensor)top.Rows[0][1]).Value);
    }

    [Fact]
    public void ASlowRead_StillServesTheRestOfItsRound_FromItsOneSnapshot_AndTheNextRoundReadsAfresh()
    {
        // Each read takes 600 ms, more than half the poll interval.
        var category = new FakeCategory { ReadMs = 600 };
        var win = new WinProcessSampler(() => 4, category.Counts, category.Read);

        // One round, in the source's order: the counts, then the memory and the processes from one category read.
        var counts = (win.ProcessCount(), win.ThreadCount(), win.HandleCount());
        var memory = win.MemoryUsage();
        var compressed = win.MemoryCompression();
        var processes = win.Processes();
        Assert.Equal((1, 1), (category.CountReads, category.Reads));
        Assert.Equal((4, 30, 300), counts);
        Assert.Equal(101L * 1024 * 1024, memory);
        Assert.Equal(1L * 1024 * 1024, Assert.Single(processes, p => p.Name == "busy").MemoryBytes);
        Assert.Equal(8L * 1024 * 1024, compressed);
        Assert.Equal(["Memory Compression", "busy", "idle"], processes.Select(p => p.Name).Order(StringComparer.Ordinal));

        // The next round reads afresh.
        win.BeginRound();
        Assert.Equal(5, win.ProcessCount());
        Assert.Equal(2L * 1024 * 1024, Assert.Single(win.Processes(), p => p.Name == "busy").MemoryBytes);
        Assert.Equal((2, 2), (category.CountReads, category.Reads));
    }

    [Fact]
    public void EachPoll_ReadsAFreshSnapshot_ForAllItsKeys_HoweverSoonAfterTheLastReadItStarts()
    {
        // Polls start a second apart; the counts take 10 ms and the category read 600 ms, so the next poll starts 400 ms
        // after the last read ended. The poll opens each round itself, so no time decides which read it gets.
        var category = new FakeCategory { ReadMs = 600, CountMs = 10 };
        var win = new WinProcessSampler(() => 4, category.Counts, category.Read);
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        using var source = SystemInfoSource.Create(win, time);
        source.Open();

        InfoPanel.Models.SensorReading Reading(string entry) => source.ReadState(SystemInfoSource.KeyOf("processes", entry)).Reading;
        for (var poll = 1; poll <= 4; poll++)
        {
            // Busy runs one CPU-second between each two poll starts: a quarter of the machine's 4 processors.
            category.BusySeconds = poll;
            category.Now = poll * 1_000;
            time.Advance(SystemInfoSource.Interval);
            source.Poll();

            // One fresh read of each for this poll (Open polled once), and every key of the poll from it.
            var read = poll + 1;
            Assert.Equal((read, read), (category.CountReads, category.Reads));
            Assert.Equal((double)(3 + read), Reading("process-count").ValueNow);
            Assert.Equal((double)(100 + read), Reading("memory-usage").ValueNow);
            var cpu = Reading("top-cpu-usage").ValueTable!;
            Assert.Equal("busy", ((PluginText)cpu.Rows[0][0]).Value);
            Assert.Equal(25f, ((PluginSensor)cpu.Rows[0][1]).Value);
            var memory = Reading("top-memory-usage").ValueTable!.Rows.Cast<DataRow>().Single(r => ((PluginText)r[0]).Value == "busy");
            Assert.Equal((float)read, ((PluginSensor)memory[3]).Value);
        }
    }

    [Fact]
    public void Real_ThisPc_ReadsTheUptime_TheCounts_TheCpu_TheMemory_AndEveryProcess_WithItsIdNameCpuTimeAndStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows reader.");
        var win = new WinProcessSampler();

        Assert.InRange(win.Uptime().TotalMilliseconds, Environment.TickCount64 - 5_000, Environment.TickCount64 + 5_000);
        var listed = Process.GetProcesses();
        foreach (var p in listed)
            p.Dispose();
        Assert.InRange(win.ProcessCount(), Math.Max(2, listed.Length - 50), listed.Length + 50);
        Assert.True(win.ThreadCount() > win.ProcessCount());
        Assert.True(win.HandleCount() > win.ThreadCount());

        // The first call has no interval yet.
        Assert.Null(win.CpuUsage());
        Assert.NotNull(win.CpuUtility());
        Thread.Sleep(600);
        Assert.InRange(win.CpuUsage()!.Value, 0, 100);
        Assert.True(win.CpuUtility() >= 0);

        var memory = (double)win.MemoryUsage();
        Assert.True(win.MemoryCompression() >= 0);
        var first = win.Processes();
        // One read of the category for the round: the processes' memory is their working sets', summed.
        var sum = (double)first.Sum(p => p.MemoryBytes);
        Assert.InRange(memory, sum * 0.99, sum * 1.01);

        using var self = Process.GetCurrentProcess();
        var me = Assert.Single(first, p => p.Pid == Environment.ProcessId);
        Assert.Equal(self.ProcessName, me.Name);
        Assert.True(me.CpuTime > TimeSpan.Zero);
        Assert.True(me.MemoryBytes > 0);
        Assert.NotEqual(0UL, me.Started);
        Assert.Contains(first, p => p is { Pid: 4, Name: "System" });
        Assert.DoesNotContain(first, p => p.Name == "_Total");

        Thread.Sleep(600);
        win.BeginRound();
        var again = Assert.Single(win.Processes(), p => p.Pid == Environment.ProcessId);
        Assert.Equal(me.Started, again.Started);
        Assert.True(again.CpuTime >= me.CpuTime);
    }

    [Fact]
    public void Real_TheSourceOnThisPc_ReadsItsFifteenEntriesLive_AndPutsABusyProcessFirstInTopCpuUsage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows reader.");
        var time = new TimerTimeProvider(DateTimeOffset.UtcNow);
        using var source = SystemInfoSource.CreateSystem(time);
        source.Open();

        // Half the CPUs busy in this process for two rounds a second apart.
        using var stop = new CancellationTokenSource();
        var spinners = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2))
            .Select(_ => new Thread(() => { while (!stop.IsCancellationRequested) { } }) { IsBackground = true }).ToList();
        spinners.ForEach(t => t.Start());
        try
        {
            for (var round = 0; round < 2; round++)
            {
                Thread.Sleep(1_000);
                time.Advance(SystemInfoSource.Interval);
                source.Poll();
            }
        }
        finally
        {
            stop.Cancel();
            spinners.ForEach(t => t.Join());
        }

        var keys = source.GetSensors().Select(s => s.Key).ToList();
        Assert.Equal(SystemInfoSource.Entries.Select(e => e.Id), keys.Select(k => k.Id["/infopanel.system-info/".Length..]));
        Assert.All(keys, k => Assert.Equal(SensorStatus.Live, source.ReadState(k).Status));

        SensorState State(string container, string entry) => source.ReadState(SystemInfoSource.KeyOf(container, entry));
        Assert.Matches(new Regex(@"^\d+:\d\d:\d\d:\d\d$"), State("uptime", "formatted").Reading.ValueText);
        Assert.True(State("processes", "thread-count").Reading.ValueNow > 0);
        Assert.True(State("processes", "handle-count").Reading.ValueNow > 0);
        Assert.True(State("processes", "memory-usage").Reading.ValueNow > 0);

        using var self = Process.GetCurrentProcess();
        var top = State("processes", "top-cpu-usage").Reading.ValueTable!;
        Assert.Equal(["Process Name", "Usage", "Utility", "Memory"], top.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(self.ProcessName, ((PluginText)top.Rows[0][0]).Value);
        Assert.True(((PluginSensor)top.Rows[0][1]).Value > 10);
        Assert.Equal("%", ((PluginSensor)top.Rows[0][1]).Unit);
        Assert.Equal(" MB", ((PluginSensor)top.Rows[0][3]).Unit);
    }
}
