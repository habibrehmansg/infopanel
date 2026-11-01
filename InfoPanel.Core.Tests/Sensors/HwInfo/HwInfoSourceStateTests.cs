using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// The HWiNFO source's state from the images a test builds (<see cref="HwInfoImageBuilder"/>) behind fake mappings and
/// a fake process check: running while the local mapping reads; no mapping (or <c>DEAD</c>) is <c>notRunning</c>, or
/// <c>sharedMemoryOff</c> while HWiNFO runs; a poll time that stops is <c>stopped</c>; a remote doesn't change it.
/// </summary>
public class HwInfoSourceStateTests
{
    private const string Local = HwInfoSampler.LocalName;

    private static readonly TimeSpan Round = HwInfoSource.Interval;

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private sealed class Rig
    {
        public readonly FakeHwInfoMemory Memory = new();
        public readonly TimerTimeProvider Time = new(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        public readonly List<LogEvent> Events = [];
        public readonly PolledSensorSource Source;

        /// <summary>Whether HWiNFO's process runs, as the sampler's process check sees it (not by default).</summary>
        public bool Running;

        public Exception? CheckFails;

        public int Checks;

        public Rig(bool withCheck = true)
        {
            var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(Events)).CreateLogger();
            Func<bool>? check = withCheck
                ? () =>
                {
                    Checks++;
                    return CheckFails is { } e ? throw e : Running;
                }
                : null;
            Source = new PolledSensorSource(
                HwInfoSource.SourceId,
                () => new HwInfoSampler(Memory, Time, log: log, hwinfoRunning: check),
                HwInfoSource.DefaultOptions,
                Time,
                SensorKeySpace.Of(InfoPanel.Enums.SensorType.HwInfo));
        }

        public void Poll(int rounds = 1)
        {
            for (var i = 0; i < rounds; i++)
            {
                Time.Advance(Round);
                Source.Poll();
            }
        }

        public SourceState State => Source.State;

        public int Logged(LogEventLevel level, string text)
        {
            lock (Events)
                return Events.Count(e => e.Level == level && e.RenderMessage().Contains(text, StringComparison.Ordinal));
        }
    }

    private static HwInfoImageBuilder Pc(long pollTime = 1_793_508_823)
    {
        var b = new HwInfoImageBuilder { PollTime = pollTime, PollingPeriodMs = 2000 };
        b.AddSensor(0xF0000300, 0, "CPU [#0]: AMD Ryzen 7 8845HS");
        b.AddReading(0, 0x1000000, "CPU (Tctl/Tdie)", "°C", 52.125, 33, 90.75, 40.5);
        return b;
    }

    private static SourceState Unavailable(SourceReason reason) => new(SourceStateKind.Unavailable, reason);

    [Fact]
    public void TheReasons_AreHWiNFOsThree_InEnglish()
    {
        Assert.Equal(("notRunning", "HWiNFO isn't running"), (HwInfoSource.NotRunning.Code, HwInfoSource.NotRunning.Message));
        Assert.Equal(("sharedMemoryOff", "HWiNFO's shared memory is off"), (HwInfoSource.SharedMemoryOff.Code, HwInfoSource.SharedMemoryOff.Message));
        Assert.Equal(("stopped", "HWiNFO stopped updating: turn Shared Memory Support on again"), (HwInfoSource.Stopped.Code, HwInfoSource.Stopped.Message));
    }

    [Fact]
    public void WhileTheLocalMappingReads_ItIsRunning_AndHWiNFOsProcessIsNeverAsked()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        Assert.Equal(SourceState.Starting, rig.State);
        rig.Source.Open();
        Assert.Equal(SourceState.Running, rig.State);
        var pc = Pc();
        for (var i = 0; i < 20; i++)
        {
            pc.PollTime++;
            rig.Memory.Set(Local, pc.Build());
            rig.Poll();
            Assert.Equal(SourceState.Running, rig.State);
        }

        Assert.Equal(0, rig.Checks);
    }

    [Fact]
    public void NoMapping_AndHWiNFONotRunning_IsNotRunning()
    {
        var rig = new Rig();
        rig.Source.Open();
        Assert.Equal(Unavailable(HwInfoSource.NotRunning), rig.State);
        Assert.Equal(0, rig.Source.GetSensors().Count);
    }

    [Fact]
    public void NoMapping_WhileHWiNFORuns_IsSharedMemoryOff()
    {
        var rig = new Rig { Running = true };
        rig.Source.Open();
        Assert.Equal(Unavailable(HwInfoSource.SharedMemoryOff), rig.State);

        // HWiNFO quits: within the process check's 5 s, not running.
        rig.Running = false;
        rig.Poll(10);
        Assert.Equal(Unavailable(HwInfoSource.NotRunning), rig.State);
    }

    [Fact]
    public void WithoutAProcessCheck_NoMappingIsNotRunning()
    {
        var rig = new Rig(withCheck: false);
        rig.Source.Open();
        Assert.Equal(Unavailable(HwInfoSource.NotRunning), rig.State);
    }

    [Theory]
    [InlineData(false, "notRunning")]
    [InlineData(true, "sharedMemoryOff")]
    public void AMappingThatSaysItEnded_Dead_SaysWhetherHWiNFORuns(bool running, string reason)
    {
        var rig = new Rig { Running = running };
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        Assert.Equal(SourceState.Running, rig.State);

        // HWiNFO quits, or Shared Memory Support is turned off: it marks the mapping DEAD (the view this engine holds
        // keeps it there), then it goes.
        rig.Memory.Set(Local, pc.BuildDead());
        rig.Poll();
        Assert.Equal(SourceStateKind.Unavailable, rig.State.Kind);
        Assert.Equal(reason, rig.State.Reason?.Code);
        rig.Memory.Remove(Local);
        rig.Poll(4);
        Assert.Equal(reason, rig.State.Reason?.Code);

        // HWiNFO starts again (or shared memory is turned on): running at the next round.
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void APollTimeThatStops_IsStopped_UntilItMovesAgain()
    {
        var rig = new Rig { Running = true };
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();

        // HWiNFO's period is 2 s, so frozen means over max(5 s, 6 s): running at 6 s, stopped after.
        rig.Poll(12);
        Assert.Equal(SourceState.Running, rig.State);
        rig.Poll();
        Assert.Equal(Unavailable(HwInfoSource.Stopped), rig.State);
        // Stopped the whole time the frozen mapping stays (opened again each round), whatever the process says.
        rig.Running = false;
        rig.Poll(80);
        Assert.Equal(Unavailable(HwInfoSource.Stopped), rig.State);

        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(SourceState.Running, rig.State);
    }

    [Fact]
    public void AStoppedMappingThatGoes_IsNotRunning()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();
        rig.Poll(13);
        Assert.Equal(Unavailable(HwInfoSource.Stopped), rig.State);

        rig.Memory.Remove(Local);
        rig.Poll();
        Assert.Equal(Unavailable(HwInfoSource.NotRunning), rig.State);
    }

    [Fact]
    public void HWiNFOsProcess_IsAskedAtMostEvery5Seconds_OnlyWhileTheMappingIsntThere()
    {
        var rig = new Rig();
        rig.Source.Open();
        var first = rig.Checks;
        Assert.InRange(first, 1, 2);
        // 30 s of rounds, two a second: at most one check per 5 s.
        rig.Poll(60);
        Assert.InRange(rig.Checks - first, 5, 6);

        rig.Memory.Set(Local, Pc().Build());
        rig.Poll();
        var reading = rig.Checks;
        var pc = Pc();
        for (var i = 0; i < 20; i++)
        {
            pc.PollTime++;
            rig.Memory.Set(Local, pc.Build());
            rig.Poll();
        }

        Assert.Equal(reading, rig.Checks);
    }

    [Fact]
    public void AProcessCheckThatThrows_CountsAsNotRunning_LoggedOnce()
    {
        var rig = new Rig { Running = true, CheckFails = new InvalidOperationException("no processes") };
        rig.Source.Open();
        rig.Poll(30);
        Assert.Equal(Unavailable(HwInfoSource.NotRunning), rig.State);
        Assert.Equal(1, rig.Logged(LogEventLevel.Warning, "checking whether HWiNFO runs failed"));
    }

    [Fact]
    public void ARemoteThatGoes_LeavesTheSourceRunning()
    {
        var rig = new Rig();
        var pc = Pc();
        var remote = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Memory.Set(HwInfoSampler.RemotePrefix + 1, remote.Build());
        rig.Source.Open();
        Assert.Equal(SourceState.Running, rig.State);

        // The remote PC's HWiNFO ends (DEAD), then its mapping freezes where it was: this PC's state is the local one's.
        rig.Memory.Set(HwInfoSampler.RemotePrefix + 1, remote.BuildDead());
        for (var i = 0; i < 10; i++)
        {
            pc.PollTime++;
            rig.Memory.Set(Local, pc.Build());
            rig.Poll();
            Assert.Equal(SourceState.Running, rig.State);
        }

        rig.Memory.Set(HwInfoSampler.RemotePrefix + 1, remote.Build());
        for (var i = 0; i < 30; i++)
        {
            pc.PollTime++;
            rig.Memory.Set(Local, pc.Build());
            rig.Poll();
            Assert.Equal(SourceState.Running, rig.State);
        }
    }

    [Fact]
    public void ARefusedMapping_IsUnavailable_WithoutAReason()
    {
        var rig = new Rig { Running = true };
        rig.Memory.Deny = true;
        rig.Source.Open();
        Assert.Equal(SourceState.Unavailable, rig.State);
    }
}
