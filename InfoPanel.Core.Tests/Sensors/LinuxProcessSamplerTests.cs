using System.Data;
using System.Text.Json;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;
using InfoPanel.Settings;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The system information source's Linux reader (<see cref="LinuxProcessSampler"/>) on a <c>/proc</c> the test writes into a
/// folder of its own (nothing committed): the uptime from <c>/proc/uptime</c>; the CPUs' busy share from <c>/proc/stat</c>
/// (the first reading has none; one after a counter went back has none); app memory and the compressed memory from
/// <c>/proc/meminfo</c>; this user's processes with their full names (the executable's, else the kernel's), CPU time,
/// start and memory, another user's left out of them but counted; a process that exits while it is read left out; and the
/// source over it (<see cref="SystemInfoSource"/>): the tables from the second round, a new process in them from the
/// next, a CPU counter that went back leaving its process out of that round, and the settings' exclusion list applied.
/// Unix only (the executable links).
/// </summary>
public sealed class LinuxProcessSamplerTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const string Me = "1000";
    private const int Processors = 4;

    private readonly TempDirectory _temp = new();
    private readonly string _proc;
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private PolledSensorSource? _source;

    public LinuxProcessSamplerTests()
    {
        _proc = _temp.Combine("proc");
        Directory.CreateDirectory(_proc);
        File.WriteAllText(Path.Combine(_proc, "uptime"), "93784.52 270000.10\n");
        WriteCpu(1000, 0, 500, 8000, 10, 5, 5, 0);
        File.WriteAllText(Path.Combine(_proc, "meminfo"),
            "MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:   10384000 kB\nZswap:              2048 kB\n");
        Directory.CreateDirectory(Path.Combine(_proc, "self"));
        File.WriteAllText(Path.Combine(_proc, "self", "status"), Status("source", Me, 1, 0, 0));
    }

    public void Dispose()
    {
        _source?.Dispose();
        _temp.Dispose();
    }

    private LinuxProcessSampler Sampler() => new(_proc, null, Processors);

    private void WriteCpu(ulong user, ulong nice, ulong system, ulong idle, ulong iowait, ulong irq, ulong softirq, ulong steal) =>
        File.WriteAllText(Path.Combine(_proc, "stat"),
            $"cpu  {user} {nice} {system} {idle} {iowait} {irq} {softirq} {steal} 0 0\ncpu0 1 2 3 4 5 6 7 8 0 0\nintr 1 2 3\nbtime 1700000000\n");

    private static string Status(string name, string uid, int threads, long anonKb, long shmemKb, bool split = true, string? effective = null) =>
        $"Name:\t{name}\nState:\tS (sleeping)\nUid:\t{uid}\t{effective ?? uid}\t{effective ?? uid}\t{effective ?? uid}\nThreads:\t{threads}\n" +
        (split ? $"VmRSS:\t{anonKb + shmemKb + 9999} kB\nRssAnon:\t{anonKb} kB\nRssFile:\t9999 kB\nRssShmem:\t{shmemKb} kB\n" : $"VmRSS:\t{anonKb} kB\n");

    /// <summary>The kernel's stat line: the name in brackets, then state, ppid ... utime (14), stime (15) ... starttime (22).</summary>
    private static string StatLine(int pid, string comm, ulong utime, ulong stime, ulong start) =>
        $"{pid} ({comm}) S 1 {pid} {pid} 0 -1 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 3 0 {start} 123456 789 18446744073709551615 0 0\n";

    /// <summary>A process folder; <paramref name="exe"/> is where its executable link leads (none: no link, as a process this user may not inspect).</summary>
    private void AddProcess(int pid, string comm, string uid = Me, ulong utime = 0, ulong stime = 0, ulong start = 5000, int threads = 2,
        long anonKb = 100 * 1024, long shmemKb = 0, string? exe = null, bool split = true, string? effective = null)
    {
        var dir = Path.Combine(_proc, pid.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status"), Status(comm, uid, threads, anonKb, shmemKb, split, effective));
        File.WriteAllText(Path.Combine(dir, "stat"), StatLine(pid, comm, utime, stime, start));
        if (exe is not null)
            File.CreateSymbolicLink(Path.Combine(dir, "exe"), exe);
    }

    /// <summary>The process runs on: its user and system ticks are now these.</summary>
    private void SetTicks(int pid, string comm, ulong utime, ulong stime, ulong start = 5000) =>
        File.WriteAllText(Path.Combine(_proc, pid.ToString(), "stat"), StatLine(pid, comm, utime, stime, start));

    private PolledSensorSource Open(SettingsStore? settings = null)
    {
        _source = SystemInfoSource.Create(Sampler(), _time, settings: settings);
        _source.Open();
        return _source;
    }

    private void Round()
    {
        _time.Advance(SystemInfoSource.Interval);
        _source!.Poll();
    }

    private SensorState State(string id) => _source!.ReadState(SystemInfoSource.KeyOf(id.Split('/')[0], id.Split('/')[1]));

    private string[] TableNames(string id)
    {
        var state = State(id);
        Assert.True(state.HasReading, id);
        return state.Reading.ValueTable!.Rows.Cast<DataRow>().Select(r => ((PluginText)r[0]).Value).ToArray();
    }

    private PluginSensor Cell(string id, string name, int column) =>
        (PluginSensor)State(id).Reading.ValueTable!.Rows.Cast<DataRow>().Single(r => ((PluginText)r[0]).Value == name)[column];

    // ---- The system ----

    [Fact]
    public void Uptime_IsTheFirstNumberOfProcUptime()
    {
        var sampler = Sampler();

        Assert.Equal(TimeSpan.FromSeconds(93784.52), sampler.Uptime());
        Assert.Equal(("1:02:03:04", "01", "02", "03", "04"), SystemInfoSource.UptimeTexts(sampler.Uptime()));
    }

    [Fact]
    public void Uptime_NoNumber_Throws()
    {
        File.WriteAllText(Path.Combine(_proc, "uptime"), "soon\n");

        Assert.Throws<InvalidOperationException>(() => Sampler().Uptime());
    }

    [Fact]
    public void CpuUsage_HasNoneTheFirstTime_ThenTheBusyShareOfTheTicksBetween()
    {
        var sampler = Sampler();
        Assert.Null(sampler.CpuUsage());

        // Busy: user + nice + system + irq + softirq + steal; idle: idle + iowait. 300 busy of 1000 ticks.
        WriteCpu(1100, 0, 600, 8600, 110, 55, 55, 0);
        Assert.Equal(30.0, sampler.CpuUsage());
        // Nothing passed: 0.
        Assert.Equal(0.0, sampler.CpuUsage());
        Assert.Null(sampler.CpuUtility());
    }

    [Fact]
    public void CpuUsage_AfterACounterWentBack_HasNone_ThenReadsAgain()
    {
        var sampler = Sampler();
        Assert.Null(sampler.CpuUsage());
        // The counters start over (a wrap, a restart): there is no interval to read.
        WriteCpu(10, 0, 5, 80, 1, 0, 0, 0);
        Assert.Null(sampler.CpuUsage());
        WriteCpu(60, 0, 5, 130, 1, 0, 0, 0);
        Assert.Equal(50.0, sampler.CpuUsage());
    }

    [Fact]
    public void CpuUsage_AfterIowaitWentBack_HasNone_ThenReadsAgain()
    {
        // iowait is not monotonic (the kernel's docs): it can fall while the total, which holds it, rises less than busy.
        WriteCpu(1000, 0, 500, 8000, 1000, 5, 5, 0);
        var sampler = Sampler();
        Assert.Null(sampler.CpuUsage());
        // Busy up 100, idle + iowait down 50: the total is up 50, which would read 200 %.
        WriteCpu(1100, 0, 500, 8000, 950, 5, 5, 0);
        Assert.Null(sampler.CpuUsage());
        // From that reading on: busy up 50 of 100 ticks.
        WriteCpu(1150, 0, 500, 8050, 950, 5, 5, 0);
        Assert.Equal(50.0, sampler.CpuUsage());
    }

    [Fact]
    public void CpuUsage_WithNoCpuLine_Throws()
    {
        File.WriteAllText(Path.Combine(_proc, "stat"), "intr 1 2 3\n");

        Assert.Throws<InvalidOperationException>(() => Sampler().CpuUsage());
    }

    [Fact]
    public void Memory_IsTheTotalLessWhatIsAvailable_AndTheCompressedIsZswap()
    {
        var sampler = Sampler();

        Assert.Equal((16_384_000L - 10_384_000L) * 1024, sampler.MemoryUsage());
        Assert.Equal(2048L * 1024, sampler.MemoryCompression());
    }

    [Fact]
    public void Memory_WithoutZswap_CompressedIsZero_AndWithoutAvailable_UsageThrows()
    {
        File.WriteAllText(Path.Combine(_proc, "meminfo"), "MemTotal:       16384000 kB\nMemFree:         1000000 kB\n");
        var sampler = Sampler();

        Assert.Equal(0, sampler.MemoryCompression());
        Assert.Throws<InvalidOperationException>(() => sampler.MemoryUsage());
    }

    [Fact]
    public void HandlesAndUtility_AreNotThere_AndTheTablesCountPerCpu()
    {
        var sampler = Sampler();

        Assert.Null(sampler.HandleCount());
        Assert.Null(sampler.CpuUtility());
        Assert.True(sampler.ProcessCpuPerCpu);
        Assert.Equal(Processors, sampler.ProcessorCount);
    }

    // ---- The processes ----

    [Fact]
    public void Processes_ListThisUsersWithFullNameCpuTimeStartAndMemory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the executable links.");
        // The kernel cuts comm at 15 bytes; the executable's file name is whole. A name may hold spaces and brackets.
        AddProcess(100, "a-very-long-pro", utime: 250, stime: 50, start: 7777, exe: "/opt/app/a-very-long-process-name", anonKb: 200 * 1024, shmemKb: 50 * 1024);
        AddProcess(101, "Web Content (x)", utime: 0, stime: 1);
        var sampler = Sampler();

        var list = sampler.Processes().OrderBy(p => p.Pid).ToList();

        Assert.Equal(2, list.Count);
        // utime + stime ticks at 100 a second.
        Assert.Equal(new ProcessReading(100, "a-very-long-process-name", TimeSpan.FromSeconds(3), 250 * MB, 7777), list[0]);
        // No link to read: the kernel's name, brackets and all.
        Assert.Equal(new ProcessReading(101, "Web Content (x)", TimeSpan.FromMilliseconds(10), 100 * MB, 5000), list[1]);
    }

    [Fact]
    public void Processes_ADeletedExecutable_IsNamedWithoutTheKernelsSuffix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the executable links.");
        AddProcess(100, "updated", exe: "/usr/bin/updated (deleted)");

        Assert.Equal("updated", Assert.Single(Sampler().Processes()).Name);
    }

    [Fact]
    public void Processes_WithoutTheKernelsSplit_UseTheWholeResidentSize()
    {
        AddProcess(100, "old-kernel", anonKb: 64 * 1024, split: false);

        Assert.Equal(64 * MB, Assert.Single(Sampler().Processes()).MemoryBytes);
    }

    [Fact]
    public void Processes_AnotherUsersAreLeftOut_ButCounted_AndTheirThreadsSummed()
    {
        AddProcess(100, "mine", threads: 3);
        AddProcess(200, "theirs", uid: "0", threads: 5);
        AddProcess(201, "other", uid: "1001", threads: 7);
        var sampler = Sampler();

        Assert.Equal([100], sampler.Processes().Select(p => p.Pid));
        Assert.Equal(3, sampler.ProcessCount());
        Assert.Equal(15, sampler.ThreadCount());
    }

    [Fact]
    public void Processes_AFolderThatEmptiedWhileRead_IsLeftOut_AndNotCounted()
    {
        AddProcess(100, "mine", threads: 3);
        // Exited after it was listed: its folder is gone empty, or its stat is, or its numbers are torn.
        Directory.CreateDirectory(Path.Combine(_proc, "101"));
        AddProcess(102, "half");
        File.Delete(Path.Combine(_proc, "102", "stat"));
        AddProcess(103, "torn");
        File.WriteAllText(Path.Combine(_proc, "103", "stat"), "103 (torn) S 1\n");
        // Not a process.
        Directory.CreateDirectory(Path.Combine(_proc, "sys"));
        var sampler = Sampler();

        Assert.Equal([100], sampler.Processes().Select(p => p.Pid));
        // 102 and 103 had a status: counted as listed; 101 had none.
        Assert.Equal(3, sampler.ProcessCount());
    }

    /// <summary>The folder stays, its <c>status</c> is refused: what <c>hidepid=1</c> does to another user's process.</summary>
    private void RefuseStatus(int pid) =>
        File.SetUnixFileMode(Path.Combine(_proc, pid.ToString(), "status"), UnixFileMode.None);

    [Fact]
    public void Processes_AFolderWhoseStatusIsRefused_IsCounted_AndTheThreadTotalIsPartial()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "Unix, not as root: a file mode that refuses.");
        AddProcess(100, "mine", threads: 3);
        AddProcess(200, "theirs", uid: "0", threads: 5);
        RefuseStatus(200);
        var sampler = Sampler();

        Assert.Equal([100], sampler.Processes().Select(p => p.Pid));
        Assert.Equal(2, sampler.ProcessCount());
        Assert.Null(sampler.ThreadCount());
    }

    [Fact]
    public void Source_ARefusedStatus_CountsTheProcess_AndHasNoThreadTotal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "Unix, not as root: a file mode that refuses.");
        AddProcess(100, "mine", threads: 3);
        AddProcess(200, "theirs", uid: "0", threads: 5);
        RefuseStatus(200);
        Open();

        Assert.Equal(2, State("processes/process-count").Reading.ValueNow);
        Assert.Equal(SensorStatus.Unavailable, State("processes/thread-count").Status);
    }

    [Fact]
    public void Processes_WithoutRefusals_TheThreadTotalIsWhole()
    {
        AddProcess(100, "mine", threads: 3);
        AddProcess(200, "theirs", uid: "0", threads: 5);

        Assert.Equal(8, Sampler().ThreadCount());
    }

    [Fact]
    public void Processes_AStatusThatVanishedAfterTheListing_IsNotCounted_AndTheThreadTotalStaysWhole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the links.");
        AddProcess(100, "mine", threads: 3);
        // The folder was listed, then the process exited: opening its status finds nothing (a link to nowhere, as a race leaves).
        Directory.CreateDirectory(Path.Combine(_proc, "101"));
        File.CreateSymbolicLink(Path.Combine(_proc, "101", "status"), Path.Combine(_proc, "101", "gone"));
        var sampler = Sampler();

        Assert.Equal([100], sampler.Processes().Select(p => p.Pid));
        Assert.Equal(1, sampler.ProcessCount());
        Assert.Equal(3, sampler.ThreadCount());
    }

    [Fact]
    public void Processes_AStatThatVanishedAfterTheStatus_IsCounted_ButLeftOut()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the links.");
        AddProcess(100, "mine", threads: 3);
        AddProcess(101, "leaving", threads: 4);
        File.Delete(Path.Combine(_proc, "101", "stat"));
        File.CreateSymbolicLink(Path.Combine(_proc, "101", "stat"), Path.Combine(_proc, "101", "gone"));
        var sampler = Sampler();

        Assert.Equal([100], sampler.Processes().Select(p => p.Pid));
        Assert.Equal(2, sampler.ProcessCount());
        Assert.Equal(7, sampler.ThreadCount());
    }

    [Fact]
    public void Processes_AStatusWithMissingFields_IsReadAsFarAsItGoes()
    {
        // No Uid: whose it is can't be told, so it is counted but not listed. No Threads and no memory lines: zero of each.
        AddProcess(100, "mine", threads: 3);
        AddProcess(101, "no-uid", threads: 2);
        File.WriteAllText(Path.Combine(_proc, "101", "status"), "Name:\tno-uid\nThreads:\t2\nVmRSS:\t4096 kB\n");
        AddProcess(102, "bare");
        File.WriteAllText(Path.Combine(_proc, "102", "status"), $"Name:\tbare\nUid:\t{Me}\t{Me}\t{Me}\t{Me}\n");
        AddProcess(103, "empty");
        File.WriteAllText(Path.Combine(_proc, "103", "status"), "");
        var sampler = Sampler();

        var list = sampler.Processes().OrderBy(p => p.Pid).ToList();

        Assert.Equal([100, 102], list.Select(p => p.Pid));
        Assert.Equal(0, list[1].MemoryBytes);
        Assert.Equal(4, sampler.ProcessCount());
        Assert.Equal(5, sampler.ThreadCount());
    }

    [Fact]
    public void Processes_TheRealUserDecides_NotTheEffectiveOne()
    {
        AddProcess(100, "real-me", uid: Me, effective: "0");
        AddProcess(101, "effective-me", uid: "0", effective: Me);
        AddProcess(102, "all-me");

        Assert.Equal([100, 102], Sampler().Processes().Select(p => p.Pid).Order());
    }

    [Fact]
    public void Processes_ANameWithANewlineAndBrackets_IsKeptWhole()
    {
        AddProcess(100, "plain");
        File.WriteAllText(Path.Combine(_proc, "100", "stat"), StatLine(100, "two\nlines) (x", 10, 20, 5000));

        var process = Assert.Single(Sampler().Processes());

        Assert.Equal("two\nlines) (x", process.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(300), process.CpuTime);
        Assert.Equal(5000UL, process.Started);
    }

    [Fact]
    public void Round_IsReadOnceUntilTheNextBegins()
    {
        AddProcess(100, "mine");
        var sampler = Sampler();
        sampler.BeginRound();
        Assert.Equal(1, sampler.ProcessCount());

        AddProcess(101, "second");
        Assert.Single(sampler.Processes());

        sampler.BeginRound();
        Assert.Equal(2, sampler.ProcessCount());
        Assert.Equal(2, sampler.Processes().Count);
    }

    // ---- The source over it ----

    [Fact]
    public void Source_ReadsTheUptimeAndCounts()
    {
        AddProcess(100, "mine", threads: 3);
        AddProcess(200, "theirs", uid: "0", threads: 5);
        Open();

        Assert.Equal("1:02:03:04", State("uptime/formatted").Reading.ValueText);
        Assert.Equal(2, State("processes/process-count").Reading.ValueNow);
        Assert.Equal(8, State("processes/thread-count").Reading.ValueNow);
        Assert.Equal(SensorStatus.Unavailable, State("processes/handle-count").Status);
        Assert.Equal(5859.375, State("processes/memory-usage").Reading.ValueNow);
        Assert.Equal(2, State("processes/memory-compression").Reading.ValueNow);
    }

    [Fact]
    public void Source_TheFirstSampleHasNoCpu_NewProcessesJoinTheNextRound_AndTheTablesAreCpuPerCpuAndFullNames()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix: the executable links.");
        AddProcess(100, "busy", utime: 1000, exe: "/usr/lib/a-busy-process-with-a-long-name", anonKb: 200 * 1024, shmemKb: 56 * 1024);
        AddProcess(101, "idle", utime: 10, anonKb: 3 * 1024 * 1024);
        AddProcess(200, "theirs", uid: "0", utime: 5000);
        Open();

        // The first round: no CPU interval yet, no table, no usage.
        Assert.False(State("processes/top-cpu-usage").HasReading);
        Assert.Equal(SensorStatus.Unavailable, State("processes/cpu-usage").Status);

        // A second of one CPU's time: 100 ticks. A process born now has no interval yet.
        SetTicks(100, "busy", 1100, 0);
        AddProcess(102, "newborn", utime: 50);
        WriteCpu(1100, 0, 600, 8600, 110, 55, 55, 0);
        Round();

        Assert.Equal(["a-busy-process-with-a-long-name", "idle"], TableNames("processes/top-cpu-usage"));
        // One CPU's second in a second of four CPUs, per CPU: 100 %.
        Assert.Equal(100.0, Cell("processes/top-cpu-usage", "a-busy-process-with-a-long-name", 1).Value, 0.01);
        Assert.Equal(0.0, Cell("processes/top-cpu-usage", "idle", 1).Value, 0.01);
        Assert.Equal(30.0, State("processes/cpu-usage").Reading.ValueNow);
        // Utility reads usage, as on macOS.
        Assert.Equal(100.0, Cell("processes/top-cpu-utility", "a-busy-process-with-a-long-name", 2).Value, 0.01);
        // Memory: anonymous and shared pages, 256 MB; a table cell of 1,024 MB or more reads in GB.
        Assert.Equal(["idle", "a-busy-process-with-a-long-name"], TableNames("processes/top-memory-usage"));
        var memory = Cell("processes/top-memory-usage", "a-busy-process-with-a-long-name", 3);
        Assert.Equal((256f, " MB"), (memory.Value, memory.Unit));
        var big = Cell("processes/top-memory-usage", "idle", 3);
        Assert.Equal((3f, " GB"), (big.Value, big.Unit));

        // The next round: the newborn has its interval.
        SetTicks(102, "newborn", 150, 0);
        Round();
        Assert.Contains("newborn", TableNames("processes/top-cpu-usage"));
    }

    [Fact]
    public void Source_AProcessWhoseCpuTimeWentBack_OrWhosePidIsUsedAgain_IsLeftOutThatRound()
    {
        AddProcess(100, "wrapped", utime: 1000);
        AddProcess(101, "reborn", utime: 1000, start: 5000);
        AddProcess(102, "steady", utime: 1000);
        Open();

        SetTicks(100, "wrapped", 10, 0);
        SetTicks(101, "reborn", 2000, 0, start: 9000);
        SetTicks(102, "steady", 1100, 0);
        Round();
        Assert.Equal(["steady"], TableNames("processes/top-cpu-usage"));

        // Both have an interval again from the next round.
        SetTicks(100, "wrapped", 110, 0);
        SetTicks(101, "reborn", 2100, 0, start: 9000);
        SetTicks(102, "steady", 1200, 0);
        Round();
        Assert.Equal(["reborn", "steady", "wrapped"], TableNames("processes/top-cpu-usage").Order().ToArray());
    }

    [Fact]
    public void Source_TheExclusionSettingIsApplied_FromTheNextRound()
    {
        AddProcess(100, "node", utime: 100);
        AddProcess(101, "worker", utime: 100);
        AddProcess(102, "Idle", utime: 100);
        var settings = SettingsStore.Open(_temp.Combine("settings.json"), _temp.Path, Serilog.Core.Logger.None, faults: null);
        Open(settings);

        SetTicks(100, "node", 200, 0);
        SetTicks(101, "worker", 200, 0);
        SetTicks(102, "Idle", 200, 0);
        Round();
        // The default list: Idle is out.
        Assert.Equal(["node", "worker"], TableNames("processes/top-cpu-usage").Order().ToArray());

        settings.Apply(JsonDocument.Parse("""{"sources":{"systemInfo":{"excludedProcesses":["node"]}}}""").RootElement);
        SetTicks(100, "node", 300, 0);
        SetTicks(101, "worker", 300, 0);
        SetTicks(102, "Idle", 300, 0);
        Round();
        Assert.Equal(["Idle", "worker"], TableNames("processes/top-cpu-usage").Order().ToArray());
        Assert.Equal(["Idle", "worker"], TableNames("processes/top-memory-usage").Order().ToArray());
    }

    [Fact]
    public void Source_APartThatFails_FailsOnlyItsReadings()
    {
        AddProcess(100, "mine", utime: 100);
        Open();
        File.Delete(Path.Combine(_proc, "uptime"));
        Round();

        Assert.Equal(SensorStatus.Stale, State("uptime/formatted").Status);
        Assert.Equal(1, State("processes/process-count").Reading.ValueNow);
    }
}
