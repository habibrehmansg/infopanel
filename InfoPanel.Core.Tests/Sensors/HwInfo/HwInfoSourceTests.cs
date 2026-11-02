using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// The HWiNFO source over images a test builds (<see cref="HwInfoImageBuilder"/>) behind fake named mappings: keys,
/// order, names, units, HWiNFO's statistics, freshness and remotes.
/// </summary>
public class HwInfoSourceTests
{
    private const string Local = HwInfoSampler.LocalName;

    private static string Remote(int i) => HwInfoSampler.RemotePrefix + i;

    private static readonly TimeSpan Round = HwInfoSource.Interval;

    private sealed class Rig
    {
        public readonly FakeHwInfoMemory Memory = new();
        public readonly TimerTimeProvider Time = new(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        public readonly PolledSensorSource Source;

        public Rig() => Source = HwInfoSource.Create(Memory, Time);

        /// <summary>One poll, half a second on.</summary>
        public void Poll(int rounds = 1)
        {
            for (var i = 0; i < rounds; i++)
            {
                Time.Advance(Round);
                Source.Poll();
            }
        }

        public SensorStatus Status(string key) => Source.GetStatus(Key(key));

        public SensorReading Read(string key)
        {
            Assert.True(Source.TryRead(Key(key), out var reading), key);
            return reading;
        }

        public string[] Keys => Source.GetSensors().Select(s => s.Key.ToString()).ToArray();
    }

    private static SensorKey Key(string text)
    {
        Assert.True(SensorKey.TryParse(text, out var key), text);
        return key;
    }

    /// <summary>A PC like the owner's: three sensors, the second instance of a sensor id, readings in HWiNFO's order.</summary>
    private static HwInfoImageBuilder Pc(long pollTime = 1_793_508_823, string? host = null)
    {
        var prefix = host == null ? "" : $"[{host}] ";
        var b = new HwInfoImageBuilder { PollTime = pollTime };
        b.AddSensor(0xF0000301, 0, prefix + "System: Example SER8");
        b.AddSensor(0xF0000300, 0, prefix + "CPU [#0]: AMD Ryzen 7 8845HS");
        b.AddSensor(0xF0000301, 1, prefix + "Memory Timings");
        b.AddReading(0, 0x8000000, "Virtual Memory Committed", "MB", 9000, 8000, 12000, 9500);
        b.AddReading(1, 0x1000000, "CPU (Tctl/Tdie)", "°C", 52.125, 33, 90.75, 40.5489143988278);
        b.AddReading(2, 0x7000000, "Memory Clock", "MHz", 2800, 2795, 2805, 2800.5);
        b.AddReading(1, 0x2000000, "Total CPU Usage", "%", 7.5, 0, 100, 12.25);
        return b;
    }

    [Fact]
    public void Source_IsHwinfo_PolledEveryHalfSecond_AnsweringEveryHwinfoKey()
    {
        var rig = new Rig();
        Assert.Equal("hwinfo", rig.Source.Id);
        Assert.Equal(TimeSpan.FromMilliseconds(500), rig.Source.Options.Interval);
        Assert.True(rig.Source.Handles(SensorKey.HwInfo(-1, 1, 0, 1)));
        Assert.True(rig.Source.Handles(SensorKey.HwInfo(3, 1, 0, 1)));
        Assert.False(rig.Source.Handles(SensorKey.Plugin("/hwinfo/x")));
        Assert.False(rig.Source.Handles(SensorKey.Libre("/amdcpu/0/load/0")));
    }

    [Fact]
    public void Keys_FollowTheTupleRule_RemoteSensorIdInstanceReadingId_InHWiNFOsOrder_WithNamesGroupsAndUnits()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();

        Assert.Equal(
            [
                ("hwinfo:-1:4026532609:0:134217728", "System: Example SER8", "Virtual Memory Committed", "MB"),
                ("hwinfo:-1:4026532608:0:16777216", "CPU [#0]: AMD Ryzen 7 8845HS", "CPU (Tctl/Tdie)", "°C"),
                ("hwinfo:-1:4026532609:1:117440512", "Memory Timings", "Memory Clock", "MHz"),
                ("hwinfo:-1:4026532608:0:33554432", "CPU [#0]: AMD Ryzen 7 8845HS", "Total CPU Usage", "%"),
            ],
            rig.Source.GetSensors().Select(s => (s.Key.ToString(), s.Group, s.Name, s.Unit)));
        Assert.All(rig.Source.GetSensors(), s => Assert.Equal("hwinfo", s.SourceId));
        Assert.All(rig.Source.GetSensors(), s => Assert.True(SensorHub.IsWellFormed(s.Key)));
        // Values are there from the first poll, which start-up takes with discovery.
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026532608:0:16777216"));
    }

    [Fact]
    public void Values_AreHWiNFOsOwn_NowMinMaxAndAverage()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();

        var cpu = rig.Read("hwinfo:-1:4026532608:0:16777216");
        Assert.Equal((52.125, 33, 90.75, 40.5489143988278, "°C"), (cpu.ValueNow, cpu.ValueMin, cpu.ValueMax, cpu.ValueAvg, cpu.Unit));

        // HWiNFO's session statistics, not the engine's: a reading of 60 with HWiNFO's max at 95 and average 41.
        pc.Readings[1] = pc.Readings[1] with { Value = 60, Max = 95, Avg = 41 };
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        cpu = rig.Read("hwinfo:-1:4026532608:0:16777216");
        Assert.Equal((60, 33, 95, 41), (cpu.ValueNow, cpu.ValueMin, cpu.ValueMax, cpu.ValueAvg));
    }

    [Fact]
    public void AKeyHWiNFODoesntHave_IsUnavailable_NeverSimulated()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();
        var hub = new SensorHub([rig.Source], new SensorHubOptions { SimulateUnknown = true });

        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.HwInfo(-1, 0xF0000300, 0, 0x5000000)));
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.HwInfo(2, 0xF0000300, 0, 0x1000000)));
        Assert.Equal(SensorStatus.Live, hub.GetStatus(SensorKey.HwInfo(-1, 0xF0000300, 0, 0x1000000)));
    }

    [Fact]
    public void HWiNFOAppearingAfterStart_IsFound()
    {
        var rig = new Rig();
        rig.Source.Open();
        Assert.Empty(rig.Keys);
        rig.Poll(3);
        Assert.Empty(rig.Keys);

        rig.Memory.Set(Local, Pc().Build());
        rig.Poll();
        Assert.Equal(4, rig.Keys.Length);
        rig.Poll();
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026532608:0:16777216"));
    }

    [Fact]
    public void TheCatalogue_IsReadAgainEveryRound_NotOnlyWhenTheMappingOpens()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();

        // The same mapping, still open: a sensor renamed and a reading reordered.
        pc.Sensors[2] = pc.Sensors[2] with { Name = "Memory Timings (renamed)" };
        (pc.Readings[2], pc.Readings[3]) = (pc.Readings[3], pc.Readings[2]);
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();

        Assert.Equal(1, rig.Memory.Opens);
        Assert.Equal("Memory Timings (renamed)", rig.Source.GetSensors().Single(s => s.Name == "Memory Clock").Group);
        Assert.Equal(["Virtual Memory Committed", "CPU (Tctl/Tdie)", "Total CPU Usage", "Memory Clock"], rig.Source.GetSensors().Select(s => s.Name));
        Assert.Equal(7.5, rig.Read("hwinfo:-1:4026532608:0:33554432").ValueNow);
        Assert.Equal(2800, rig.Read("hwinfo:-1:4026532609:1:117440512").ValueNow);
    }

    [Fact]
    public void AGrowingImage_AddsKeys_AndTheOldOnesKeepTheirState()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        rig.Poll(2);

        // A USB device plugged in: a sensor and two readings more, appended; the image grows.
        pc.AddSensor(0xF0000500, 0, "Drive: Example SSD [D:]");
        pc.AddReading(3, 0x1000000, "Drive Temperature", "°C", 41);
        pc.AddReading(3, 0x6000000, "Read Rate", "MB/s", 0.5);
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(6, rig.Keys.Length);
        rig.Poll();

        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026533120:0:16777216"));
        Assert.Equal(41, rig.Read("hwinfo:-1:4026533120:0:16777216").ValueNow);
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026532608:0:16777216"));
        Assert.Equal(52.125, rig.Read("hwinfo:-1:4026532608:0:16777216").ValueNow);
    }

    [Fact]
    public void Dead_IsStale_ThenUnavailable_ThenTheListEmpties()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";

        rig.Memory.Set(Local, pc.BuildDead());
        rig.Poll();
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
        Assert.Equal(52.125, rig.Read(cpu).ValueNow);
        Assert.Equal(0, rig.Memory.OpenViews);

        rig.Poll(59);
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
        rig.Poll(2);
        Assert.Equal(SensorStatus.Unavailable, rig.Status(cpu));
        Assert.Empty(rig.Keys);
    }

    [Fact]
    public void ARestartWithin30Seconds_KeepsTheReadings_StaleMeanwhile()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";

        // HWiNFO exits (DEAD), and is gone for five seconds.
        rig.Memory.Set(Local, pc.BuildDead());
        rig.Poll();
        rig.Memory.Remove(Local);
        rig.Poll(10);
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
        Assert.Equal(4, rig.Keys.Length);

        // Started again: listed again at once, its values from the next round.
        pc.PollTime += 100;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(4, rig.Keys.Length);
        rig.Poll();
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
        Assert.Equal(4, rig.Keys.Length);
    }

    [Fact]
    public void AFrozenPollTime_IsStale_ThenUnavailable_AndLiveAgainWhenItMoves()
    {
        var rig = new Rig();
        var pc = Pc();
        pc.PollingPeriodMs = 2000;
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";

        // HWiNFO's period is 2 s, so frozen means over max(5 s, 6 s): live at 6 s, failed after.
        rig.Poll(12);
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
        rig.Poll();
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
        Assert.Equal(52.125, rig.Read(cpu).ValueNow);
        // Withdrawn from the catalogue (plan 2.2): listed while stale, unlisted once unavailable, though the mapping,
        // opened again each round, is still there.
        rig.Poll(60);
        Assert.Equal(SensorStatus.Unavailable, rig.Status(cpu));
        Assert.Empty(rig.Keys);

        // Polling resumes: listed again at once, its values from the next round.
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(4, rig.Keys.Length);
        rig.Poll();
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
    }

    [Fact]
    public void AFrozenMapping_StaysWithdrawn_ItsFrozenTimeKept_LocalOrRemote_UntilItMoves()
    {
        var rig = new Rig();
        var local = Pc();
        var remote = Pc(host: "PC1");
        rig.Memory.Set(Local, local.Build());
        rig.Memory.Set(Remote(1), remote.Build());
        rig.Source.Open();
        Assert.Equal(8, rig.Keys.Length);

        // Both freeze; reopening the same frozen image (every round, and the remote's at each probe) never makes it
        // fresh again.
        rig.Poll(200);
        Assert.Empty(rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("hwinfo:-1:4026532608:0:16777216"));
        Assert.Equal(SensorStatus.Unavailable, rig.Status("hwinfo:1:4026532608:0:16777216"));

        // The remote moves on alone: only its keys come back.
        remote.PollTime++;
        rig.Memory.Set(Remote(1), remote.Build());
        rig.Poll(2);
        Assert.Equal(4, rig.Keys.Length);
        Assert.All(rig.Keys, k => Assert.StartsWith("hwinfo:1:", k));
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:1:4026532608:0:16777216"));
    }

    [Fact]
    public void ARepartitionedImage_WithTheSameCatalogueBytes_IsReadAgain()
    {
        // One sensor and 35 readings, then 44 sensors and one reading made of the very same catalogue bytes
        // (44 × 136 + 172 = 136 + 35 × 172): the key both share is element 34 in the first and element 0 in the second.
        var many = new HwInfoImageBuilder();
        many.AddSensor(0xF0000300, 0, "CPU [#0]: AMD Ryzen 7 8845HS");
        for (var i = 0; i < 35; i++)
            many.AddReading(0, (uint)(0x1000000 + i), "Reading " + i, "°C", i, 0, 100, 50);
        var manyImage = many.Build();
        Assert.Equal(HwInfoImageState.Ok, HwInfoLayout.ReadHeader(manyImage, manyImage.Length, out var h, out _));
        var stream = new List<byte>(HwInfoLayout.SensorElement(manyImage, h, 0)[..HwInfoLayout.SensorCatalogueBytes].ToArray());
        var bytes = new byte[HwInfoLayout.ReadingCatalogueBytes];
        for (var i = 0; i < 35; i++)
        {
            HwInfoLayout.CatalogueBytesOf(HwInfoLayout.ReadingElement(manyImage, h, i), bytes, out var written);
            stream.AddRange(bytes[..written]);
        }

        Assert.Equal(6156, stream.Count);
        var few = new byte[48 + 44 * 392 + 460];
        manyImage.AsSpan(0, 48).CopyTo(few);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(few.AsSpan(28), 44);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(few.AsSpan(32), 48 + 44 * 392);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(few.AsSpan(40), 1);
        for (var s = 0; s < 44; s++)
            stream.GetRange(s * 136, 136).ToArray().CopyTo(few, 48 + s * 392);
        var reading = stream.GetRange(44 * 136, 172).ToArray();
        var at = 48 + 44 * 392;
        reading.AsSpan(0, 140).CopyTo(few.AsSpan(at));
        reading.AsSpan(140, 16).CopyTo(few.AsSpan(at + 268));
        reading.AsSpan(156, 16).CopyTo(few.AsSpan(at + 444));
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(few.AsSpan(at + 284), 999);

        var rig = new Rig();
        rig.Memory.Set(Local, few);
        rig.Source.Open();
        var shared = "hwinfo:-1:4026532608:0:16777250";
        Assert.Equal(999, rig.Read(shared).ValueNow);

        many.PollTime++;
        rig.Memory.Set(Local, many.Build());
        rig.Poll();
        Assert.Equal(34, rig.Read(shared).ValueNow);
        rig.Poll();
        Assert.Equal(35, rig.Keys.Length);
        Assert.Equal(34, rig.Read(shared).ValueNow);
    }

    private sealed class CollectingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    [Fact]
    public void AMappingThisUserMayNotRead_WarnsOnce_UntilItGoesOrReads()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var memory = new FakeHwInfoMemory();
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        using var sampler = new HwInfoSampler(memory, time, log: log);
        int Warnings() => events.Count(e => e.Level == Serilog.Events.LogEventLevel.Warning);
        void Rounds(int n)
        {
            for (var i = 0; i < n; i++)
            {
                time.Advance(Round);
                sampler.Sample([], Round);
            }
        }

        memory.Set(Local, Pc().Build());
        memory.Deny = true;
        sampler.Discover();
        Rounds(20);
        Assert.Equal(1, Warnings());

        // It goes (a state change), and comes back still refused: warned again, once.
        memory.Deny = false;
        memory.Remove(Local);
        Rounds(3);
        memory.Set(Local, Pc().Build());
        memory.Deny = true;
        Rounds(20);
        Assert.Equal(2, Warnings());

        memory.Deny = false;
        Rounds(1);
        Assert.Contains(events, e => e.Level == Serilog.Events.LogEventLevel.Information && e.RenderMessage().Contains("reads again", StringComparison.Ordinal));
        Assert.Equal(2, Warnings());
    }

    [Fact]
    public void ARemoteThisUserMayNotRead_WarnsOnce_AcrossItsProbes_UntilItGoesOrReads()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var memory = new FakeHwInfoMemory();
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        using var sampler = new HwInfoSampler(memory, time, log: log);
        var local = Pc();
        int Warnings() => events.Count(e => e.Level == Serilog.Events.LogEventLevel.Warning);
        void Rounds(int n)
        {
            // The local mapping reads every round (its poll time moves), so the remotes are probed every fifth.
            for (var i = 0; i < n; i++)
            {
                local.PollTime++;
                memory.Set(Local, local.Build());
                time.Advance(Round);
                sampler.Sample([], Round);
            }
        }

        memory.Set(Local, local.Build());
        memory.Set(Remote(1), Pc(host: "PC1").Build());
        memory.Denied.Add(Remote(1));
        sampler.Discover();
        Rounds(40);
        Assert.Equal(1, Warnings());
        Assert.Contains("remote 1", events.Single(e => e.Level == Serilog.Events.LogEventLevel.Warning).RenderMessage(), StringComparison.Ordinal);
        Assert.Empty(sampler.Remotes);

        // It goes (a state change: not there, so nothing to refuse), and comes back still refused: warned again, once.
        memory.Remove(Remote(1));
        memory.Denied.Clear();
        Rounds(10);
        memory.Set(Remote(1), Pc(host: "PC1").Build());
        memory.Denied.Add(Remote(1));
        Rounds(40);
        Assert.Equal(2, Warnings());

        // Readable: found at the next probe, and no new warning.
        memory.Denied.Clear();
        Rounds(5);
        Assert.Equal([1], sampler.Remotes.ToArray());
        Assert.Equal(2, Warnings());
    }

    [Fact]
    public void AFrozenMapping_WhoseReopenFails_StaysFrozen_NeverRevivedAsLive()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";
        rig.Poll(80);
        Assert.Empty(rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status(cpu));

        // One reopen is refused, then the very same frozen image opens again: still frozen, never fresh.
        rig.Memory.Deny = true;
        rig.Poll();
        rig.Memory.Deny = false;
        for (var i = 0; i < 20; i++)
        {
            rig.Poll();
            Assert.Empty(rig.Keys);
            Assert.Equal(SensorStatus.Unavailable, rig.Status(cpu));
        }

        // Its poll time moves: back.
        var pc = Pc(1_793_508_824);
        rig.Memory.Set(Local, pc.Build());
        rig.Poll(2);
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
    }

    [Fact]
    public void AFrozenRemote_WhoseReopenFails_StaysFrozen_ThroughItsProbes()
    {
        var rig = new Rig();
        var local = Pc();
        rig.Memory.Set(Local, local.Build());
        rig.Memory.Set(Remote(1), Pc(host: "PC1").Build());
        rig.Source.Open();
        var remoteCpu = "hwinfo:1:4026532608:0:16777216";
        void Rounds(int n)
        {
            // The local mapping stays live (and so the remotes are probed); the remote's poll time never moves.
            for (var i = 0; i < n; i++)
            {
                local.PollTime++;
                rig.Memory.Set(Local, local.Build());
                rig.Poll();
            }
        }

        Rounds(80);
        Assert.Equal(4, rig.Keys.Length);
        Assert.All(rig.Keys, k => Assert.StartsWith("hwinfo:-1:", k));
        Assert.Equal(SensorStatus.Unavailable, rig.Status(remoteCpu));

        rig.Memory.Denied.Add(Remote(1));
        Rounds(1);
        rig.Memory.Denied.Clear();
        for (var i = 0; i < 12; i++)
        {
            Rounds(1);
            Assert.DoesNotContain(rig.Keys, k => k.StartsWith("hwinfo:1:", StringComparison.Ordinal));
            Assert.Equal(SensorStatus.Unavailable, rig.Status(remoteCpu));
        }
    }

    [Fact]
    public void ARemoteThatEnds_WhileItsWarningStands_IsKeptForTheWarningOnly_ItsBuffersLetGo()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var memory = new FakeHwInfoMemory();
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        using var sampler = new HwInfoSampler(memory, time, log: log);
        var local = Pc();
        var remote = Pc(host: "PC1");
        int Warnings() => events.Count(e => e.Level == Serilog.Events.LogEventLevel.Warning);
        void Rounds(int n)
        {
            // The local mapping reads every round (its poll time moves); the remote's poll time never moves.
            for (var i = 0; i < n; i++)
            {
                local.PollTime++;
                memory.Set(Local, local.Build());
                time.Advance(Round);
                sampler.Sample([], Round);
            }
        }

        memory.Set(Local, local.Build());
        memory.Set(Remote(1), remote.Build());
        sampler.Discover();
        Assert.Equal([1], sampler.Remotes.ToArray());

        // Frozen: withdrawn and warned about, kept with its frozen time.
        Rounds(20);
        Assert.Equal([1], sampler.Remotes.ToArray());
        Assert.Equal(1, Warnings());

        // It ends (DEAD) while its warning stands: kept among the refused for that warning, not for anything it read.
        memory.Set(Remote(1), remote.BuildDead());
        for (var i = 0; i < 12; i++)
        {
            Rounds(1);
            Assert.Empty(sampler.Remotes);
            Assert.Equal(0, sampler.RefusedBufferBytes);
        }

        Assert.Equal(1, Warnings());

        // A new one under its index reads at the next probe, warned about no more.
        remote.PollTime++;
        memory.Set(Remote(1), remote.Build());
        Rounds(5);
        Assert.Equal([1], sampler.Remotes.ToArray());
        Assert.Equal(1, Warnings());
    }

    /// <summary>An observer (a recording, say) whose next round end throws, once armed.</summary>
    private sealed class FailingObserver : HwInfoSampler.IObserver
    {
        public bool FailNext;
        public int Copies;
        public int Rounds;

        public void Copied(int remote, ReadOnlySpan<byte> image) => Copies++;

        public void RoundEnded()
        {
            Rounds++;
            if (FailNext)
            {
                FailNext = false;
                throw new UnauthorizedAccessException("recording.json is read-only");
            }
        }
    }

    [Fact]
    public void AFailingObserver_NeverHoldsBackTheCatalogue_ReadingsReorderedThatRound_KeepTheirValues_AndItIsLetGo()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var memory = new FakeHwInfoMemory();
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 1, 4, 53, 43, TimeSpan.Zero));
        var observer = new FailingObserver();
        var source = new PolledSensorSource(HwInfoSource.SourceId, () => new HwInfoSampler(memory, time, observer, log: log),
            HwInfoSource.DefaultOptions, time, SensorKeySpace.Of(SensorType.HwInfo));
        var pc = Pc();
        memory.Set(Local, pc.Build());
        source.Open();
        var tctl = Key("hwinfo:-1:4026532608:0:16777216");
        var usage = Key("hwinfo:-1:4026532608:0:33554432");

        // Two readings swap places in HWiNFO's order in the round the observer fails.
        (pc.Readings[1], pc.Readings[3]) = (pc.Readings[3], pc.Readings[1]);
        observer.FailNext = true;
        var roundsBefore = observer.Rounds;
        for (var i = 0; i < 3; i++)
        {
            pc.PollTime++;
            memory.Set(Local, pc.Build());
            time.Advance(Round);
            source.Poll();
            Assert.True(source.TryRead(tctl, out var t));
            Assert.Equal(52.125, t.ValueNow);
            Assert.True(source.TryRead(usage, out var u));
            Assert.Equal(7.5, u.ValueNow);
            Assert.Equal(SensorStatus.Live, source.GetStatus(tctl));
        }

        // Logged once; the observer isn't called again (a recording stops), the source reads on.
        Assert.Single(events, e => e.Level == Serilog.Events.LogEventLevel.Error && e.Exception is UnauthorizedAccessException);
        Assert.Equal(roundsBefore + 1, observer.Rounds);
    }

    [Fact]
    public void AHeaderThatChanges_BetweenItsReadAndTheCopy_IsNeverMisread()
    {
        var rig = new Rig();
        var small = Pc();
        small.Slack = 2 * 460;
        var big = Pc();
        big.AddReading(1, 0x3000000, "CPU Package Power", "W", 15.5, 5, 40, 12);
        rig.Memory.Set(Local, small.Build());
        rig.Source.Open();
        Assert.Equal(4, rig.Keys.Length);

        // Grown after the header was read: the copy is short of what its own header names, so the round fails.
        big.PollTime++;
        var reads = rig.Memory.Reads;
        rig.Memory.BeforeRead = n =>
        {
            if (n == reads + 2)
                rig.Memory.Set(Local, big.Build());
        };
        rig.Poll();
        rig.Memory.BeforeRead = null;
        Assert.Equal(SensorStatus.Stale, rig.Status("hwinfo:-1:4026532608:0:16777216"));
        Assert.Equal(4, rig.Keys.Length);
        rig.Poll(2);
        Assert.Equal(5, rig.Keys.Length);
        Assert.Equal(15.5, rig.Read("hwinfo:-1:4026532608:0:50331648").ValueNow);

        // Shrunk after the header was read (the mapping keeps its size): the copy's own header is read, so the
        // smaller catalogue, and the values in it, are right in that round.
        small.PollTime = big.PollTime + 1;
        small.Readings[1] = small.Readings[1] with { Value = 61.25 };
        var shrunk = small.Build();
        Array.Resize(ref shrunk, big.Build().Length);
        reads = rig.Memory.Reads;
        rig.Memory.BeforeRead = n =>
        {
            if (n == reads + 2)
                rig.Memory.Set(Local, shrunk);
        };
        rig.Poll();
        rig.Memory.BeforeRead = null;
        Assert.Equal(61.25, rig.Read("hwinfo:-1:4026532608:0:16777216").ValueNow);
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026532608:0:16777216"));
        Assert.Equal(SensorStatus.Stale, rig.Status("hwinfo:-1:4026532608:0:50331648"));
    }

    [Fact]
    public void DuplicateTuples_AreListedOnce_TheFirstReadingInHWiNFOsOrderRead()
    {
        var rig = new Rig();
        var b = new HwInfoImageBuilder();
        b.AddSensor(0xF0000300, 0, "CPU [#0]: AMD Ryzen 7 8845HS");
        // The same sensor id and instance twice (HWiNFO lists a device again): its readings make the same tuples.
        b.AddSensor(0xF0000300, 0, "CPU [#0]: AMD Ryzen 7 8845HS (again)");
        b.AddReading(0, 0x1000000, "CPU (Tctl/Tdie)", "°C", 52, 33, 90, 40);
        b.AddReading(0, 0x1000000, "CPU (Tctl/Tdie) twice", "°C", 99, 99, 99, 99);
        b.AddReading(1, 0x1000000, "CPU (Tctl/Tdie) on the copy", "°C", 98, 98, 98, 98);
        b.AddReading(1, 0x2000000, "Total CPU Usage", "%", 7.5, 0, 100, 12);
        rig.Memory.Set(Local, b.Build());
        rig.Source.Open();

        Assert.Equal(["hwinfo:-1:4026532608:0:16777216", "hwinfo:-1:4026532608:0:33554432"], rig.Keys);
        Assert.Equal(52, rig.Read("hwinfo:-1:4026532608:0:16777216").ValueNow);
        Assert.Equal("CPU (Tctl/Tdie)", rig.Source.GetSensors()[0].Name);
        rig.Poll();
        Assert.Equal(52, rig.Read("hwinfo:-1:4026532608:0:16777216").ValueNow);
        Assert.Equal(7.5, rig.Read("hwinfo:-1:4026532608:0:33554432").ValueNow);
    }

    [Fact]
    public void TheCopyBuffer_NeverGrowsPast32MiB()
    {
        const int MiB = 1024 * 1024;
        Assert.Equal(100, HwInfoSampler.Grown(0, 100));
        Assert.Equal(2000, HwInfoSampler.Grown(1000, 1500));
        Assert.Equal(32 * MiB, HwInfoSampler.Grown(20 * MiB, 21 * MiB));
        Assert.Equal(32 * MiB, HwInfoSampler.Grown(32 * MiB - 1, 32 * MiB));
        Assert.Equal(5 * MiB, HwInfoSampler.Grown(1 * MiB, 5 * MiB));
    }

    [Fact]
    public void AFrozenPollTime_WithAShortPeriod_FailsAfterFiveSeconds()
    {
        var rig = new Rig();
        var pc = Pc();
        pc.PollingPeriodMs = 500;
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";

        rig.Poll(10);
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
        rig.Poll();
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
    }

    [Fact]
    public void HWiNFOKilled_WithoutDEAD_LetsGoOfTheMapping_AndTheListEmpties()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();

        // The name goes, but the open view still reads the last image, frozen.
        rig.Memory.Remove(Local);
        rig.Poll(13);
        Assert.Equal(SensorStatus.Stale, rig.Status("hwinfo:-1:4026532608:0:16777216"));
        Assert.Equal(0, rig.Memory.OpenViews);
        rig.Poll(61);
        Assert.Empty(rig.Keys);
    }

    [Fact]
    public void ARefusedImage_FailsTheRound_AndTheNextGoodOneReads()
    {
        var rig = new Rig();
        var pc = Pc();
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var cpu = "hwinfo:-1:4026532608:0:16777216";

        pc.ReadingCountOverride = 5000;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(SensorStatus.Stale, rig.Status(cpu));
        Assert.Equal(4, rig.Keys.Length);

        pc.ReadingCountOverride = null;
        pc.PollTime++;
        rig.Memory.Set(Local, pc.Build());
        rig.Poll();
        Assert.Equal(SensorStatus.Live, rig.Status(cpu));
    }

    [Fact]
    public void AMappingThisUserMayNotRead_ListsNothing_AndNeverThrows()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Memory.Deny = true;
        rig.Source.Open();
        rig.Poll(3);
        Assert.Empty(rig.Keys);
    }

    [Fact]
    public void Remotes_FromOneUpToTheFirstGap_ListedAfterTheLocalReadings()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Memory.Set(Remote(1), Pc(host: "PC1").Build());
        rig.Memory.Set(Remote(2), Pc(host: "PC2").Build());
        rig.Memory.Set(Remote(4), Pc(host: "PC4").Build());
        rig.Source.Open();

        Assert.Equal(12, rig.Keys.Length);
        Assert.Equal(["-1", "-1", "-1", "-1", "1", "1", "1", "1", "2", "2", "2", "2"], rig.Keys.Select(k => k.Split(':')[1]));
        Assert.Equal("[PC1] CPU [#0]: AMD Ryzen 7 8845HS", rig.Source.GetSensors().Single(s => s.Key.ToString() == "hwinfo:1:4026532608:0:16777216").Group);
        Assert.DoesNotContain(Remote(0), rig.Memory.Asked);
        Assert.DoesNotContain(Remote(4), rig.Memory.Asked);
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:2:4026532608:0:16777216"));
    }

    [Fact]
    public void Remotes_AreProbedAgainEveryFiveRounds()
    {
        var rig = new Rig();
        rig.Memory.Set(Local, Pc().Build());
        rig.Source.Open();
        Assert.Equal(1, rig.Memory.Asked.Count(n => n == Remote(1)));

        rig.Memory.Set(Remote(1), Pc(host: "PC1").Build());
        // Open took rounds 0 and 1; the next probe is round 5.
        rig.Poll(3);
        Assert.Equal(4, rig.Keys.Length);
        rig.Poll();
        Assert.Equal(8, rig.Keys.Length);
        Assert.Equal(2, rig.Memory.Asked.Count(n => n == Remote(1)));
    }

    [Fact]
    public void ARemoteDying_FailsOnlyItsKeys()
    {
        var rig = new Rig();
        var local = Pc();
        rig.Memory.Set(Local, local.Build());
        rig.Memory.Set(Remote(1), Pc(host: "PC1").Build());
        rig.Memory.Set(Remote(2), Pc(host: "PC2").Build());
        rig.Source.Open();

        rig.Memory.Set(Remote(1), Pc(host: "PC1").BuildDead());
        for (var i = 0; i < 4; i++)
        {
            local.PollTime++;
            rig.Memory.Set(Local, local.Build());
            rig.Poll();
        }

        Assert.Equal(SensorStatus.Stale, rig.Status("hwinfo:1:4026532608:0:16777216"));
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:2:4026532608:0:16777216"));
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:-1:4026532608:0:16777216"));

        for (var i = 0; i < 62; i++)
        {
            local.PollTime++;
            rig.Memory.Set(Local, local.Build());
            rig.Memory.Set(Remote(2), Pc(local.PollTime, host: "PC2").Build());
            rig.Poll();
        }

        Assert.DoesNotContain(rig.Keys, k => k.StartsWith("hwinfo:1:", StringComparison.Ordinal));
        Assert.Equal(8, rig.Keys.Length);
        Assert.Equal(SensorStatus.Live, rig.Status("hwinfo:2:4026532608:0:16777216"));
    }

    [Fact]
    public void Remotes_AreProbedOnlyWhileTheLocalMappingIsThere()
    {
        // As 1.x: HWiNFO makes the remote mappings, so without the local one there is nothing to probe.
        var rig = new Rig();
        rig.Memory.Set(Remote(1), Pc(host: "PC1").Build());
        rig.Source.Open();
        rig.Poll(10);
        Assert.Empty(rig.Keys);
        Assert.DoesNotContain(Remote(1), rig.Memory.Asked);

        rig.Memory.Set(Local, Pc().Build());
        rig.Poll(4);
        Assert.Equal(8, rig.Keys.Length);
        Assert.Contains(rig.Keys, k => k.StartsWith("hwinfo:1:", StringComparison.Ordinal));
    }

    [Fact]
    public void ARemotesKeys_CarryItsIndex_AndTheSameReadingOnTwoPCs_AreTwoSensors()
    {
        var rig = new Rig();
        var local = Pc();
        var remote = Pc(host: "PC1");
        remote.Readings[1] = remote.Readings[1] with { Value = 71.5 };
        rig.Memory.Set(Local, local.Build());
        rig.Memory.Set(Remote(1), remote.Build());
        rig.Source.Open();

        Assert.Equal(52.125, rig.Read("hwinfo:-1:4026532608:0:16777216").ValueNow);
        Assert.Equal(71.5, rig.Read("hwinfo:1:4026532608:0:16777216").ValueNow);
        Assert.Equal(["[PC1] System: Example SER8", "[PC1] CPU [#0]: AMD Ryzen 7 8845HS", "[PC1] Memory Timings", "[PC1] CPU [#0]: AMD Ryzen 7 8845HS"],
            rig.Source.GetSensors().Where(s => s.Key.Id.StartsWith("1:", StringComparison.Ordinal)).Select(s => s.Group));
    }

    [Fact]
    public void ARoundCopiesOnlyTheUsedBytes_OnceAMapping()
    {
        var rig = new Rig();
        var pc = Pc();
        pc.Slack = 1024 * 1024;
        rig.Memory.Set(Local, pc.Build());
        rig.Source.Open();
        var reads = rig.Memory.Reads;
        rig.Poll();

        // A header read and one copy per round, local only (no remote probe this round).
        Assert.Equal(2, rig.Memory.Reads - reads);
    }
}
