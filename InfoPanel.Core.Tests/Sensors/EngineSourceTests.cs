using InfoPanel.Sensors;
using InfoPanel.Sensors.SystemInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The engine's own usage (<c>infopanel.engine</c>, "InfoPanel engine"): two numbers, its process's CPU as a share of the
/// whole machine (averaged over the last rounds, as System Info's tables are) and its resident memory in MB, polled as
/// System Info is, on a fake process and on this test's own.
/// </summary>
public sealed class EngineSourceTests : IDisposable
{
    private const long MB = 1024 * 1024;

    /// <summary>A process the test sets: its CPU time so far and its resident memory; a part set to throw throws.</summary>
    private sealed class FakeProcess : IEngineProcess
    {
        public int Processors = 4;
        public TimeSpan Cpu;
        public long Resident = 210 * MB;
        public bool Throws;

        public int ProcessorCount => Processors;
        public TimeSpan CpuTime() => Throws ? throw new InvalidOperationException("refused") : Cpu;
        public long ResidentBytes() => Throws ? throw new InvalidOperationException("refused") : Resident;
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
        _process.Resident = 300 * MB;
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
    public void AResidentReadingOf0_IsAFailedRead_TheMemoryIsUnavailable_NotLive0()
    {
        // .NET's working set reads 0 without throwing when the OS refuses it (Linux, /proc/self/status unreadable).
        _process.Resident = 0;
        Open(_process);

        Assert.Equal(SensorStatus.Unavailable, State("memory").Status);
        Assert.False(State("memory").HasReading);

        // After a good reading, a 0 doesn't replace it with a live 0: the reading goes stale; the CPU reads on.
        _process.Resident = 210 * MB;
        Round();
        Assert.Equal(210, Number("memory"));

        _process.Resident = 0;
        _process.Cpu += TimeSpan.FromSeconds(2);
        Round();

        Assert.NotEqual(SensorStatus.Live, State("memory").Status);
        Assert.Equal(210, State("memory").Reading.ValueNow);
        // The rounds' shares, 0 and 50 %, averaged.
        Assert.Equal(25, Number("cpu"), 6);
        Assert.Equal(SensorStatus.Live, State("cpu").Status);
    }

    [Fact]
    public void ThisProcess_AfterAnInterval_ReadsCpuWithin0And100_AndMemoryAbove0()
    {
        Open(EngineSource.CurrentProcess());
        // Some CPU time of this process's own between the rounds.
        var spin = DateTime.UtcNow.AddMilliseconds(50);
        while (DateTime.UtcNow < spin)
        {
        }

        Round();

        Assert.InRange(Number("cpu"), 0, 100);
        Assert.True(Number("memory") > 0);
    }
}
