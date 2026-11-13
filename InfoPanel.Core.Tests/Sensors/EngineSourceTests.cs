using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The engine's own usage (<c>infopanel.engine</c>, "InfoPanel engine"): two numbers, its process's CPU as the operating
/// system's own tool counts it (per CPU off Windows, a share of the whole machine on Windows; averaged over the last
/// rounds, as System Info's tables are) and its memory in MB as that tool shows it, polled as System Info is, on a fake
/// process and on this test's own.
/// </summary>
public sealed class EngineSourceTests : IDisposable
{
    private const long MB = 1024 * 1024;

    /// <summary>A process the test sets: its CPU time so far, how its CPU counts and its memory; a part set to throw throws.</summary>
    private sealed class FakeProcess : IEngineProcess
    {
        public int Processors = 4;
        public bool PerCpu;
        public TimeSpan Cpu;
        public long Memory = 210 * MB;
        public bool Throws;

        public int ProcessorCount => Processors;
        public bool CpuPerCpu => PerCpu;
        public TimeSpan CpuTime() => Throws ? throw new InvalidOperationException("refused") : Cpu;
        public long MemoryBytes() => Throws ? throw new InvalidOperationException("refused") : Memory;
    }

    private readonly FakeProcess _process = new();
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    private PolledSensorSource? _source;

    public void Dispose() => _source?.Dispose();

    /// <summary>The source over <paramref name="process"/>, discovered and polled once.</summary>
    private PolledSensorSource Open(IEngineProcess process)
    {
        _source = EngineSource.Create(process, _time);
        _source.Open();
        return _source;
    }

    /// <summary>One poll, an interval after the last.</summary>
    private void Round()
    {
        _time.Advance(EngineSource.Interval);
        _source!.Poll();
    }

    private SensorState State(string id) => _source!.ReadState(EngineSource.KeyOf(id));

    private double Number(string id)
    {
        var state = State(id);
        Assert.True(state.HasReading, id);
        Assert.Equal(SensorKind.Number, state.Type);
        return state.Reading.ValueNow;
    }

    [Fact]
    public void Lists_ExactlyCpuAndMemory_WithTheirUnits_UnderTheSharedKeyBuilder()
    {
        Open(_process);
        var sensors = _source!.GetSensors();

        Assert.Equal([EngineSource.KeyOf("cpu"), EngineSource.KeyOf("memory")], sensors.Select(s => s.Key));
        Assert.Equal([SensorKeySpace.Plugin("infopanel.engine").KeyOf("cpu"), SensorKeySpace.Plugin("infopanel.engine").KeyOf("memory")],
            sensors.Select(s => s.Key));
        Assert.All(sensors, s => Assert.Equal("infopanel.engine", s.SourceId));
        Assert.All(sensors, s => Assert.Equal("InfoPanel engine", s.Group));
        Assert.Equal(["CPU", "Memory"], sensors.Select(s => s.Name));
        Assert.Equal(["%", " MB"], sensors.Select(s => s.Unit));
        Assert.Equal(("infopanel.engine", "InfoPanel engine"), (EngineSource.SourceId, EngineSource.Name));
        Assert.Equal(SystemInfoSource.Interval, _source.Options.Interval);
        Assert.True(_source.Handles(EngineSource.KeyOf("other")));
        Assert.False(_source.Handles(SystemInfoSource.KeyOf("processes", "cpu-usage")));
    }

    [Fact]
    public void Memory_ReadsAtOnce_InMB_AndCpu_FromTheSecondRound_AsAShareOfEveryCpu()
    {
        Open(_process);

        Assert.Equal(210, Number("memory"));
        Assert.False(State("cpu").HasReading);

        // 2 s of CPU time in 1 s on 4 CPUs: half the machine.
        _process.Cpu += TimeSpan.FromSeconds(2);
        _process.Memory = 300 * MB;
        Round();

        Assert.Equal(50, Number("cpu"), 6);
        Assert.Equal(300, Number("memory"));
        Assert.Equal(SensorStatus.Live, State("cpu").Status);
    }

    [Fact]
    public void Cpu_IsTheAverageOfTheLastRounds_AsSystemInfosTables()
    {
        Open(_process);
        double[] seconds = [0.4, 0.8, 1.2, 2.0];
        var shown = new List<double>();
        foreach (var s in seconds)
        {
            _process.Cpu += TimeSpan.FromSeconds(s);
            Round();
            shown.Add(Number("cpu"));
        }

        // Each round's share: 10, 20, 30, 50 %; shown, the average of the last three at most.
        Assert.Equal(3, SystemInfoSource.CpuRounds);
        Assert.Equal([10, 15, 20, 100 / 3d], shown, (a, b) => Math.Abs(a - b) < 1e-6);
    }

    [Fact]
    public void Cpu_PerCpu_IsTheShareTimesTheCpus_AsActivityMonitorAndTopCountIt_AndTheShareModeIsUnchanged()
    {
        // The review's measure: 0.17 s of CPU time in 1 s on 14 CPUs is 17 % of one core, 1.21 % of the machine.
        _process.Processors = 14;
        _process.PerCpu = true;
        Open(_process);
        _process.Cpu += TimeSpan.FromSeconds(0.17);
        Round();
        Assert.Equal(17, Number("cpu"), 6);

        var share = new FakeProcess { Processors = 14 };
        _source!.Dispose();
        Open(share);
        share.Cpu += TimeSpan.FromSeconds(0.17);
        Round();
        Assert.Equal(17 / 14d, Number("cpu"), 6);
    }

    [Fact]
    public void Cpu_PerCpu_GoesPast100_AndStaysWithin0And100TimesTheCpus()
    {
        _process.Processors = 14;
        _process.PerCpu = true;
        Open(_process);

        // Nearly three busy CPUs: past 100.
        _process.Cpu += TimeSpan.FromSeconds(2.8);
        Round();
        Assert.Equal(280, Number("cpu"), 6);

        // More CPU time than the interval holds on every CPU (a late round): 100 × the CPUs at most, averaged with 280.
        _process.Cpu += TimeSpan.FromSeconds(20);
        Round();
        Assert.Equal((280 + 1400) / 2d, Number("cpu"), 6);
    }

    [Fact]
    public void Cpu_StaysWithin0And100()
    {
        _process.Processors = 1;
        Open(_process);

        // More CPU time than the interval holds (a late round): the whole machine at most.
        _process.Cpu += TimeSpan.FromSeconds(5);
        Round();

        Assert.Equal(100, Number("cpu"));
    }

    [Fact]
    public void AProcessThatCantBeRead_FailsItsReadings()
    {
        Open(_process);
        _process.Throws = true;
        Round();

        Assert.Equal(SensorStatus.Stale, State("memory").Status);
        Assert.NotEqual(SensorStatus.Live, State("cpu").Status);
    }

    [Fact]
    public void AMemoryReadingOf0_IsAFailedRead_TheMemoryIsUnavailable_NotLive0()
    {
        // A memory read gives 0 without throwing when the OS refuses it (Linux, /proc/self/status unreadable; Windows
        // before 1809, which has no private working set).
        _process.Memory = 0;
        Open(_process);

        Assert.Equal(SensorStatus.Unavailable, State("memory").Status);
        Assert.False(State("memory").HasReading);

        // After a good reading, a 0 doesn't replace it with a live 0: the reading goes stale; the CPU reads on.
        _process.Memory = 210 * MB;
        Round();
        Assert.Equal(210, Number("memory"));

        _process.Memory = 0;
        _process.Cpu += TimeSpan.FromSeconds(2);
        Round();

        Assert.NotEqual(SensorStatus.Live, State("memory").Status);
        Assert.Equal(210, State("memory").Reading.ValueNow);
        // The rounds' shares, 0 and 50 %, averaged.
        Assert.Equal(25, Number("cpu"), 6);
        Assert.Equal(SensorStatus.Live, State("cpu").Status);
    }

    private sealed class CollectingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => events.Add(logEvent);
    }

    [Fact]
    public void AMemoryThatCantBeRead_IsLoggedOnce_WhileItFails_AndAgainAfterItReadAgain()
    {
        var events = new List<Serilog.Events.LogEvent>();
        using var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        // Windows before 1809 has no private working set: every read gives 0.
        _process.Memory = 0;
        _source = EngineSource.Create(_process, _time, log);
        _source.Open();
        Round();
        Round();

        bool Memory(Serilog.Events.LogEvent e) => e.MessageTemplate.Text.Contains("memory can't be read", StringComparison.Ordinal);
        Assert.Single(events, Memory);
        Assert.Equal(SensorStatus.Unavailable, State("memory").Status);

        _process.Memory = 210 * MB;
        Round();
        _process.Memory = 0;
        Round();
        Assert.Equal(2, events.Count(Memory));
    }

    [Fact]
    public void ThisProcess_CountsItsCpuPerCpu_OffWindows_AndAsAShareOfTheMachine_OnWindows()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), EngineSource.CurrentProcess().CpuPerCpu);
    }

    /// <summary>Set in the child process <see cref="ThisProcess_CountsTheOsLogicalCpus_NotTheCpusTheRuntimeIsHeldTo"/> starts: the file it reports to.</summary>
    private const string CpuReportVariable = "INFOPANEL_TEST_ENGINE_CPUS_REPORT";

    [Fact]
    public void ThisProcess_CountsTheOsLogicalCpus_NotTheCpusTheRuntimeIsHeldTo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows counts every processor group's (WinProcessSampler's tests).");
        var os = OsLogicalCpus();
        Assert.SkipWhen(os < 2, "One CPU: the runtime can't be held to fewer.");

        // This test assembly run again, its one test below in a process the runtime holds to one CPU (as an affinity or a
        // Linux quota would): the engine still counts the CPUs the OS's own tool counts, as Activity Monitor and top do.
        var report = Path.Combine(Path.GetTempPath(), $"infopanel-engine-cpus-{Guid.NewGuid():N}.txt");
        var start = new System.Diagnostics.ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(EngineSourceTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(EngineSourceTests).FullName}.{nameof(ThisProcess_ReportsItsCpuCounts)}");
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        start.Environment[CpuReportVariable] = report;
        try
        {
            using var child = System.Diagnostics.Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            if (!child.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                child.Kill(entireProcessTree: true);
                Assert.Fail("the child run didn't end within a minute");
            }
            Assert.True(File.Exists(report), $"the child reported nothing: {output.Result}{errors.Result}");

            var counts = File.ReadAllText(report).Split(' ').Select(int.Parse).ToArray();
            Assert.Equal(1, counts[0]);
            Assert.Equal(os, counts[1]);
        }
        finally
        {
            File.Delete(report);
        }
    }

    /// <summary>
    /// The child run's test: this process's runtime count and the engine's, written to the file the variable names; in a
    /// run of its own, the engine counts at least the runtime's.
    /// </summary>
    [Fact]
    public void ThisProcess_ReportsItsCpuCounts()
    {
        var engine = EngineSource.CurrentProcess().ProcessorCount;
        if (Environment.GetEnvironmentVariable(CpuReportVariable) is { Length: > 0 } report)
        {
            File.WriteAllText(report, $"{Environment.ProcessorCount} {engine}");
            return;
        }

        Assert.InRange(engine, Environment.ProcessorCount, int.MaxValue);
    }

    /// <summary>The OS's own count of logical CPUs, from its own command: macOS <c>hw.logicalcpu</c>, Linux the online CPUs.</summary>
    private static int OsLogicalCpus()
    {
        var start = OperatingSystem.IsMacOS()
            ? new System.Diagnostics.ProcessStartInfo("/usr/sbin/sysctl", "-n hw.logicalcpu")
            : new System.Diagnostics.ProcessStartInfo("getconf", "_NPROCESSORS_ONLN");
        start.RedirectStandardOutput = true;
        using var command = System.Diagnostics.Process.Start(start)!;
        var text = command.StandardOutput.ReadToEnd();
        command.WaitForExit();
        return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The dotnet host running these tests.</summary>
    private static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
            return host;
        // The shared runtime is {dotnet root}/shared/Microsoft.NETCore.App/{version}/.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    }

    [Fact]
    public void ThisProcess_ReadsItsMemory_ThroughTheProcessMemoryReader()
    {
        var engine = EngineSource.CurrentProcess();
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        // The reader at two instants around the engine's own reading: a live process's memory moves between them.
        var before = ProcessMemory.Of(self);
        var read = engine.MemoryBytes();
        var after = ProcessMemory.Of(self);

        Assert.True(before > 0 && after > 0);
        Assert.InRange((double)read, Math.Min(before, after) * 0.95, Math.Max(before, after) * 1.05);
    }

    [Fact]
    public void ThisProcess_AfterAnInterval_ReadsCpuWithin0And100TimesTheCpus_AndMemoryAbove0()
    {
        var process = EngineSource.CurrentProcess();
        Open(process);
        // Some CPU time of this process's own between the rounds.
        var spin = DateTime.UtcNow.AddMilliseconds(50);
        while (DateTime.UtcNow < spin)
        {
        }

        Round();

        Assert.InRange(Number("cpu"), 0, 100 * process.ProcessorCount);
        Assert.True(Number("memory") > 0);
    }
}
