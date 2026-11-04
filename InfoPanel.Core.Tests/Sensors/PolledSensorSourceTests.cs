using InfoPanel.Enums;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

public class PolledSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sampler whose sensors read what the test puts in <see cref="Next"/>.</summary>
    private sealed class ScriptedSampler(params string[] ids) : ISensorSampler
    {
        public SensorSample[] Next = ids.Select(_ => new SensorSample { Outcome = SampleOutcome.Skipped }).ToArray();
        public readonly List<TimeSpan> Elapsed = [];
        public Exception? Throw;
        public bool Disposed;
        /// <summary>When set, discovery waits for it.</summary>
        public ManualResetEventSlim? HoldDiscovery;
        /// <summary>When set, sampling (after the first) waits for it.</summary>
        public ManualResetEventSlim? HoldSample;
        public readonly ManualResetEventSlim InDiscovery = new();
        public readonly ManualResetEventSlim InSample = new();

        public IReadOnlyList<SensorDefinition> Discover()
        {
            InDiscovery.Set();
            HoldDiscovery?.Wait(Patience);
            return ids.Select(id => new SensorDefinition(id, "Group", "Name " + id, "u")).ToList();
        }

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
        {
            if (Elapsed.Count > 0 && HoldSample != null)
            {
                InSample.Set();
                HoldSample.Wait(Patience);
            }

            Elapsed.Add(elapsed);
            if (Throw != null)
                throw Throw;
            Next.CopyTo(samples);
        }

        public void Set(int i, double value) => Next[i] = new SensorSample { Value = value, Outcome = SampleOutcome.Ok };

        public void Set(int i, SampleOutcome outcome) => Next[i] = new SensorSample { Outcome = outcome };

        public void Dispose() => Disposed = true;
    }

    private static readonly PolledSensorSourceOptions Options = new()
    {
        Interval = TimeSpan.FromSeconds(1),
        StaleAfter = TimeSpan.FromSeconds(5),
        UnavailableAfter = TimeSpan.FromSeconds(30),
    };

    private static (PolledSensorSource Source, ScriptedSampler Sampler, TimerTimeProvider Time) Create(params string[] ids)
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sampler = new ScriptedSampler(ids);
        return (new PolledSensorSource("test.polled", () => sampler, Options, time), sampler, time);
    }

    [Fact]
    public void Keys_AreUnderTheSourcePrefix_AndListedAfterOpen()
    {
        var (source, _, _) = Create("cpu/load", "fans/0/speed");
        Assert.Empty(source.GetSensors());

        source.Open();

        Assert.Equal(["plugin:/test.polled/cpu/load", "plugin:/test.polled/fans/0/speed"], source.GetSensors().Select(s => s.Key.ToString()));
        Assert.All(source.GetSensors(), s => Assert.Equal("test.polled", s.SourceId));
        Assert.True(source.Handles(SensorKey.Plugin("/test.polled/anything")));
        Assert.False(source.Handles(SensorKey.Plugin("/test.polledx/cpu/load")));
        Assert.False(source.Handles(SensorKey.Libre("/test.polled/cpu/load")));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(SensorKey.Plugin("/test.polled/not-listed")));
    }

    [Fact]
    public void Status_LiveThenStaleThenUnavailable_AsSamplesStop()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 10);
        source.Open();
        var key = source.KeyOf("a");

        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        Assert.True(source.TryRead(key, out var reading));
        Assert.Equal(10, reading.ValueNow);
        Assert.Equal("u", reading.Unit);

        // Polls stop (a hung read): live up to StaleAfter, stale (last value) up to UnavailableAfter, then unavailable.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SensorStatus.Stale, source.GetStatus(key));
        Assert.True(source.TryRead(key, out reading));
        Assert.Equal(10, reading.ValueNow);
        time.Advance(TimeSpan.FromSeconds(24));
        Assert.Equal(SensorStatus.Stale, source.GetStatus(key));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
        Assert.False(source.TryRead(key, out _));

        // A fresh sample brings it back, keeping min/max/avg over every good sample.
        sampler.Set(0, 20);
        source.Poll();
        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        Assert.True(source.TryRead(key, out reading));
        Assert.Equal((20, 10, 20, 15), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void FailedRead_IsStaleAtOnce_ThenUnavailableWhenItKeepsFailing()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 42);
        source.Open();
        var key = source.KeyOf("a");

        sampler.Set(0, SampleOutcome.Failed);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(key));
        Assert.True(source.TryRead(key, out var reading));
        Assert.Equal(42, reading.ValueNow);

        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
        }

        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
        Assert.False(source.TryRead(key, out _));
    }

    [Fact]
    public void NonFiniteValue_CountsAsAFailedRead()
    {
        var (source, sampler, _) = Create("a");
        sampler.Next[0] = new SensorSample { Value = double.NaN, Outcome = SampleOutcome.Ok };
        source.Open();

        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(source.KeyOf("a")));
    }

    [Fact]
    public void NeverSampled_IsUnavailable_AndSkippedRoundsKeepTheState()
    {
        var (source, sampler, time) = Create("rate", "value");
        sampler.Set(1, 1);
        source.Open();

        // A rate's first round has no delta: skipped, so there is no reading yet.
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(source.KeyOf("rate")));
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("value")));

        sampler.Set(0, 5);
        sampler.Set(1, SampleOutcome.Skipped);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("rate")));
        // Skipped (a reader polled every other second): still live from its earlier sample.
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("value")));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1)], sampler.Elapsed);
    }

    [Fact]
    public void PermissionDenied_HasNoReading_AndClearsOnAGoodRead()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 1);
        source.Open();
        var key = source.KeyOf("a");

        sampler.Set(0, SampleOutcome.PermissionDenied);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.PermissionDenied, source.GetStatus(key));
        Assert.False(source.TryRead(key, out _));

        var hub = new SensorHub([source]);
        Assert.Equal(SensorStatus.PermissionDenied, hub.GetStatus(key));
        Assert.False(hub.TryRead(key, out _));

        sampler.Set(0, 2);
        source.Poll();
        Assert.Equal(SensorStatus.Live, hub.GetStatus(key));
    }

    [Fact]
    public void Unavailable_DropsTheReadingAtOnce_FailedOrSkippedRoundsAfterKeepItUnavailable_AndAGoodReadStartsAfresh()
    {
        var (source, sampler, time) = Create("a", "b");
        sampler.Set(0, 10);
        sampler.Set(1, 1);
        source.Open();
        sampler.Set(0, 20);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        var key = source.KeyOf("a");
        Assert.True(source.TryRead(key, out var reading));
        Assert.Equal((20, 10, 20, 15), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));

        // Unavailable: no reading from this round, not the last value gone stale; the other sensor reads on.
        sampler.Set(0, SampleOutcome.Unavailable);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
        Assert.False(source.TryRead(key, out _));
        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Number, false), (state.Status, state.Type, state.HasReading));
        Assert.Equal(SensorStatus.Unavailable, new SensorHub([source]).GetStatus(key));
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("b")));

        // A failed or skipped round after it never brings the old value back.
        foreach (var outcome in new[] { SampleOutcome.Failed, SampleOutcome.Skipped, SampleOutcome.Unavailable })
        {
            sampler.Set(0, outcome);
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
            Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
            Assert.False(source.TryRead(key, out _));
        }

        // A good read is live at once, its statistics from it alone.
        sampler.Set(0, 5);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
        Assert.True(source.TryRead(key, out reading));
        Assert.Equal((5, 5, 5, 5), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        sampler.Set(0, 7);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.True(source.TryRead(key, out reading));
        Assert.Equal((7, 5, 7, 6), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void AValueMarkedStale_IsKept_InTheStatisticsToo_AndReadsStale_AFirstOneToo()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 10);
        sampler.Next[0].MarkStale();
        source.Open();
        var key = source.KeyOf("a");

        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Stale, 10, 10, 10, 10), (state.Status, state.Reading.ValueNow, state.Reading.ValueMin, state.Reading.ValueMax, state.Reading.ValueAvg));

        // A good value after it reads live; the statistics hold both.
        sampler.Set(0, 20);
        Assert.False(sampler.Next[0].Stale);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Live, 20, 10, 20, 15), (state.Status, state.Reading.ValueNow, state.Reading.ValueMin, state.Reading.ValueMax, state.Reading.ValueAvg));

        // Given stale again, it reads stale with that value; given every round, it doesn't age out.
        sampler.Set(0, 30);
        sampler.Next[0].MarkStale();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            time.Advance(TimeSpan.FromSeconds(20));
            source.Poll();
        }

        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Stale, 30, 10, 30), (state.Status, state.Reading.ValueNow, state.Reading.ValueMin, state.Reading.ValueMax));
    }

    [Fact]
    public void AValueKeptStale_RoundAfterRound_IsNoNewReading_SoTheStatisticsHoldStill()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 10);
        source.Open();
        var key = source.KeyOf("a");
        sampler.Set(0, 20);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Live, 20, 10, 20, 15), (state.Status, state.Reading.ValueNow, state.Reading.ValueMin, state.Reading.ValueMax, state.Reading.ValueAvg));

        // The last value kept and marked stale every round, as a source does while it can't read: no new readings.
        sampler.Next[0].MarkStale();
        foreach (var _ in Enumerable.Range(0, 10))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
        }

        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Stale, 20, 10, 20, 15), (state.Status, state.Reading.ValueNow, state.Reading.ValueMin, state.Reading.ValueMax, state.Reading.ValueAvg));
    }

    [Fact]
    public void Unavailable_DropsTheSourcesOwnStatisticsToo()
    {
        var (source, sampler, time) = Create("a");
        sampler.Next[0].Set(50, 1, 99, 40);
        source.Open();
        var key = source.KeyOf("a");
        Assert.True(source.TryRead(key, out var reading));
        Assert.Equal((50, 1, 99, 40), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));

        sampler.Next[0].SetUnavailable();
        Assert.Equal((SampleOutcome.Unavailable, false, (string?)null), (sampler.Next[0].Outcome, sampler.Next[0].HasStatistics, sampler.Next[0].Text));
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.False(source.TryRead(key, out _));

        sampler.Set(0, 3);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.True(source.TryRead(key, out reading));
        Assert.Equal((3, 3, 3, 3), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void Unavailable_ARead_WhileTheNextRoundSamples_SeesTheCommittedState_NeverHalfARound()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 10);
        source.Open();
        var key = source.KeyOf("a");

        // The round that says unavailable, held inside its sampling: the last committed reading still reads, live.
        sampler.Set(0, SampleOutcome.Unavailable);
        sampler.HoldSample = new ManualResetEventSlim();
        time.Advance(TimeSpan.FromSeconds(1));
        var poll = Task.Run(source.Poll, Ct);
        Assert.True(sampler.InSample.Wait(Patience, Ct));
        Assert.True(source.TryRead(key, out var reading));
        Assert.Equal(10, reading.ValueNow);
        sampler.HoldSample.Set();
        poll.Wait(Ct);
        Assert.False(source.TryRead(key, out _));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(key));
    }

    [Fact]
    public void SamplerThatThrows_FailsTheRound_AndTheNextRoundRecovers()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 1);
        source.Open();
        var key = source.KeyOf("a");

        sampler.Throw = new InvalidOperationException("boom");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(key));

        sampler.Throw = null;
        sampler.Set(0, 3);
        source.Poll();
        Assert.Equal(SensorStatus.Live, source.GetStatus(key));
    }

    [Fact]
    public void DiscoveryThatThrows_ListsNothing_AndNeverThrows()
    {
        var time = new TimerTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new PolledSensorSource("test.broken", () => throw new DllNotFoundException("no IOKit"), Options, time);

        source.Open();
        source.Poll();

        Assert.Empty(source.GetSensors());
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(SensorKey.Plugin("/test.broken/a")));
    }

    [Fact]
    public void DuplicateIds_AreListedOnce()
    {
        var (source, sampler, _) = Create("a", "a", "b");
        sampler.Set(0, 1);
        sampler.Set(1, 99);
        sampler.Set(2, 2);
        source.Open();

        Assert.Equal(2, source.GetSensors().Count);
        Assert.True(source.TryRead(source.KeyOf("a"), out var a));
        Assert.Equal(1, a.ValueNow);
        Assert.True(source.TryRead(source.KeyOf("b"), out var b));
        Assert.Equal(2, b.ValueNow);
    }

    [Fact]
    public async Task StartAsync_PollsOnTheTimer_AndStopClosesTheSampler()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 1);

        await source.StartAsync(Ct);
        Assert.True(source.IsRunning);
        Assert.Equal(1, source.Polls);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await WaitFor(() => source.Polls == 2 + i);
        }

        await source.StopAsync(Ct);
        Assert.False(source.IsRunning);
        Assert.True(sampler.Disposed);
        Assert.Equal(0, time.TimerCount);
    }

    [Fact]
    public async Task DisposeDuringStart_StartsNoLoop_AndClosesTheSampler()
    {
        var (source, sampler, time) = Create("a");
        sampler.HoldDiscovery = new ManualResetEventSlim();

        var start = source.StartAsync(Ct);
        Assert.True(sampler.InDiscovery.Wait(Patience, Ct));
        source.Dispose();
        Assert.False(sampler.Disposed, "not while discovery is running");
        sampler.HoldDiscovery.Set();
        await start;

        Assert.False(source.IsRunning);
        Assert.True(sampler.Disposed);
        Assert.Equal(0, time.TimerCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(Ct));
    }

    [Fact]
    public async Task StopThatTimesOut_IsReapedByTheNextStart()
    {
        var (source, sampler, time) = Create("a");
        sampler.Set(0, 1);
        await source.StartAsync(Ct);

        // A poll that blocks (a hung OS call) outlasts the stop's wait.
        sampler.HoldSample = new ManualResetEventSlim();
        // The fake timer runs the poll inline on the advancing thread, so advance from another one.
        var advance = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1)), Ct);
        Assert.True(sampler.InSample.Wait(Patience, Ct));
        using (var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.StopAsync(shortWait.Token));

        sampler.HoldSample.Set();
        await advance;
        await WaitFor(() => sampler.Disposed);

        // The loop ended after the stop gave up; a new start reaps it and runs a new loop (discovering again).
        sampler.HoldSample = null;
        sampler.Disposed = false;
        await source.StartAsync(Ct);
        Assert.True(source.IsRunning);
        await source.StopAsync(Ct);
        Assert.False(source.IsRunning);
        Assert.True(sampler.Disposed);
    }

    /// <summary>
    /// A sampler whose catalogue the test changes: <see cref="Catalogue"/> is what the next <see cref="Discover"/>
    /// returns, <see cref="Values"/> what each round reads by id (absent ids fail), and <see cref="Change"/> bumps the
    /// version as a sampler does after a round that found a new catalogue.
    /// </summary>
    private sealed class ChangingSampler : ISensorSampler
    {
        public List<string> Catalogue = [];
        public readonly Dictionary<string, (double Value, double? Min, double? Max, double? Avg)> Values = [];
        public long Version;
        public int Rounds;
        public int Discoveries;
        private List<string> _handedOut = [];

        /// <summary>When set, <see cref="Discover"/> throws it.</summary>
        public Exception? DiscoverThrows;

        public IReadOnlyList<SensorDefinition> Discover()
        {
            Discoveries++;
            if (DiscoverThrows != null)
                throw DiscoverThrows;
            _handedOut = [.. Catalogue];
            return _handedOut.Select(id => new SensorDefinition(id, "Group " + id, "Name " + id, "unit " + id)).ToList();
        }

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
        {
            Rounds++;
            Assert.Equal(_handedOut.Count, samples.Length);
            for (var i = 0; i < samples.Length; i++)
            {
                if (!Values.TryGetValue(_handedOut[i], out var v))
                    samples[i].Outcome = SampleOutcome.Failed;
                else if (v.Min is { } min)
                    samples[i].Set(v.Value, min, v.Max!.Value, v.Avg!.Value);
                else
                    samples[i].Set(v.Value);
            }
        }

        public long CatalogueVersion => Version;

        /// <summary>The catalogue becomes <paramref name="ids"/> after the next round.</summary>
        public void Change(params string[] ids)
        {
            Catalogue = [.. ids];
            Version++;
        }

        public void Dispose()
        {
        }
    }

    private static (PolledSensorSource Source, ChangingSampler Sampler, TimerTimeProvider Time) CreateChanging(SensorKeySpace? keySpace = null, params string[] ids)
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sampler = new ChangingSampler { Catalogue = [.. ids] };
        return (new PolledSensorSource("test.changing", () => sampler, Options, time, keySpace), sampler, time);
    }

    [Fact]
    public async Task Interval_IsEachSourcesOwn()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sampler = new ScriptedSampler("a");
        sampler.Set(0, 1);
        var fast = new PolledSensorSource("test.fast", () => sampler, new PolledSensorSourceOptions { Interval = TimeSpan.FromMilliseconds(500) }, time);

        await fast.StartAsync(Ct);
        Assert.Equal(1, fast.Polls);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitFor(() => fast.Polls == 2);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitFor(() => fast.Polls == 3);
        await fast.StopAsync(Ct);
        Assert.Equal(TimeSpan.FromMilliseconds(500), fast.Options.Interval);
    }

    [Fact]
    public void KeySpaceOfAType_HandlesEveryKeyOfIt_AndKeysAreTheDefinitionIds()
    {
        var (source, sampler, _) = CreateChanging(SensorKeySpace.Of(SensorType.HwInfo), "-1:4026532608:0:16777216", "1:7:0:3");
        sampler.Values["-1:4026532608:0:16777216"] = (52.5, null, null, null);
        source.Open();

        Assert.Equal(["hwinfo:-1:4026532608:0:16777216", "hwinfo:1:7:0:3"], source.GetSensors().Select(s => s.Key.ToString()));
        Assert.Equal(SensorKey.HwInfo(-1, 4026532608, 0, 16777216), source.KeyOf("-1:4026532608:0:16777216"));
        Assert.True(source.Handles(SensorKey.HwInfo(-1, 1, 0, 1)));
        Assert.False(source.Handles(SensorKey.Plugin("/test.changing/a")));
        Assert.False(source.Handles(SensorKey.Libre("/amdcpu/0/load/0")));
        Assert.True(source.TryRead(SensorKey.HwInfo(-1, 4026532608, 0, 16777216), out var reading));
        Assert.Equal(52.5, reading.ValueNow);

        // A key this computer lacks is the source's, so even a simulating hub answers unavailable for it.
        var hub = new SensorHub([source], new SensorHubOptions { SimulateUnknown = true });
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.HwInfo(-1, 99, 0, 99)));
        Assert.False(hub.TryRead(SensorKey.HwInfo(-1, 99, 0, 99), out _));
        Assert.Equal(SensorStatus.Simulated, hub.GetStatus(SensorKey.Libre("/amdcpu/0/load/0")));
    }

    [Fact]
    public void KeySpaceOfPluginType_IsRefused_ForItNeedsAPrefix()
    {
        Assert.Throws<ArgumentException>(() => SensorKeySpace.Of(SensorType.Plugin));
        Assert.Equal(SensorKey.Plugin("/x.y/cpu/load"), SensorKeySpace.Plugin("x.y").KeyOf("cpu/load"));
    }

    [Fact]
    public void ChangedCatalogue_IsRediscovered_CarryingTheSlotsOfKeysThatStay()
    {
        var (source, sampler, time) = CreateChanging(null, "a", "b");
        sampler.Values["a"] = (1, null, null, null);
        sampler.Values["b"] = (10, null, null, null);
        source.Open();
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["b"] = (20, null, null, null);
        source.Poll();

        // A new device: c comes, b stays; the change is seen after the round, which still filled a and b.
        sampler.Change("b", "c");
        sampler.Values["b"] = (30, null, null, null);
        sampler.Values["c"] = (100, null, null, null);
        sampler.Values.Remove("a");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(2, sampler.Discoveries);

        var b = source.KeyOf("b");
        Assert.True(source.TryRead(b, out var reading));
        Assert.Equal((30, 10, 30, 20), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        Assert.Equal("unit b", reading.Unit);
        // c is listed at once and reads from the next round.
        Assert.Equal(["plugin:/test.changing/b", "plugin:/test.changing/c", "plugin:/test.changing/a"], source.GetSensors().Select(s => s.Key.ToString()));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(source.KeyOf("c")));
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("c")));
        Assert.True(source.TryRead(b, out reading));
        Assert.Equal((30, 10, 30, 22.5), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void AKeyGoneFromTheCatalogue_IsStale_ThenUnavailable_ThenUnlisted()
    {
        var (source, sampler, time) = CreateChanging(null, "a", "b");
        sampler.Values["a"] = (1, null, null, null);
        sampler.Values["b"] = (2, null, null, null);
        source.Open();

        sampler.Change("b");
        sampler.Values.Remove("a");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();

        var a = source.KeyOf("a");
        Assert.Equal(SensorStatus.Stale, source.GetStatus(a));
        Assert.True(source.TryRead(a, out var reading));
        Assert.Equal(1, reading.ValueNow);
        Assert.Contains(source.GetSensors(), s => s.Key == a);

        for (var i = 0; i < 29; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
        }

        Assert.Equal(SensorStatus.Stale, source.GetStatus(a));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(a));
        source.Poll();
        Assert.DoesNotContain(source.GetSensors(), s => s.Key == a);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(a));
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("b")));
    }

    [Fact]
    public void AKeyBackWithin30Seconds_KeepsItsStatistics()
    {
        var (source, sampler, time) = CreateChanging(null, "a");
        sampler.Values["a"] = (4, null, null, null);
        source.Open();
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["a"] = (8, null, null, null);
        source.Poll();

        // The source restarts: nothing for a while, then the same key again.
        sampler.Values.Remove("a");
        sampler.Change();
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(source.KeyOf("a")));
        sampler.Change("a");
        time.Advance(TimeSpan.FromSeconds(10));
        source.Poll();
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["a"] = (6, null, null, null);
        source.Poll();

        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("a")));
        Assert.True(source.TryRead(source.KeyOf("a"), out var reading));
        Assert.Equal((6, 4, 8, 6), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        Assert.Single(source.GetSensors());
        // Found again through discovery (empty, then back), not through a catalogue that never changed.
        Assert.Equal(3, sampler.Discoveries);
    }

    [Fact]
    public void EmptyCatalogue_IsStillPolled_SoSensorsThatAppearLaterAreFound()
    {
        var (source, sampler, time) = CreateChanging(null);
        source.Open();
        Assert.Empty(source.GetSensors());
        Assert.Equal(1, sampler.Rounds);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(2, sampler.Rounds);

        sampler.Change("late");
        sampler.Values["late"] = (7, null, null, null);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();

        Assert.Equal(["plugin:/test.changing/late"], source.GetSensors().Select(s => s.Key.ToString()));
        Assert.Equal(("Group late", "Name late", "unit late"), (source.GetSensors()[0].Group, source.GetSensors()[0].Name, source.GetSensors()[0].Unit));
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("late")));
    }

    [Fact]
    public void StatisticsTheSourceGives_AreReadInsteadOfTheBasesOwn()
    {
        var (source, sampler, time) = CreateChanging(null, "hw", "own");
        sampler.Values["hw"] = (50, 30, 90, 41.5);
        sampler.Values["own"] = (50, null, null, null);
        source.Open();
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["hw"] = (60, 30, 91, 41.6);
        sampler.Values["own"] = (60, null, null, null);
        source.Poll();

        Assert.True(source.TryRead(source.KeyOf("hw"), out var hw));
        Assert.Equal((60, 30, 91, 41.6), (hw.ValueNow, hw.ValueMin, hw.ValueMax, hw.ValueAvg));
        Assert.True(source.TryRead(source.KeyOf("own"), out var own));
        Assert.Equal((60, 50, 60, 55), (own.ValueNow, own.ValueMin, own.ValueMax, own.ValueAvg));

        // A non-finite statistic: the value counts, the base's own statistics stand in.
        var sample = new SensorSample();
        sample.Set(5, double.NaN, 9, 7);
        Assert.Equal((SampleOutcome.Ok, 5d, false), (sample.Outcome, sample.Value, sample.HasStatistics));
        sample.Set(5, 1, 9, 7);
        Assert.Equal((true, 1d, 9d, 7d), (sample.HasStatistics, sample.Min, sample.Max, sample.Avg));
    }

    [Fact]
    public void SwitchingFromTheSourcesStatistics_ToTheBasesOwn_ReadsTheBasesOwnHistory_NotAMixOfBoth()
    {
        var (source, sampler, time) = CreateChanging(null, "a");
        sampler.Values["a"] = (50, 30, 90, 41.5);
        source.Open();
        Assert.True(source.TryRead(source.KeyOf("a"), out var reading));
        Assert.Equal((50, 30, 90, 41.5), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));

        // The source stops giving statistics (a non-finite one): the base's own, over every value it read.
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["a"] = (60, double.NaN, 91, 41.6);
        source.Poll();
        Assert.True(source.TryRead(source.KeyOf("a"), out reading));
        Assert.Equal((60, 50, 60, 55), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));

        // And back: the source's again, the base's kept on the side.
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["a"] = (70, 30, 92, 42);
        source.Poll();
        Assert.True(source.TryRead(source.KeyOf("a"), out reading));
        Assert.Equal((70, 30, 92, 42), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Values["a"] = (40, null, null, null);
        source.Poll();
        Assert.True(source.TryRead(source.KeyOf("a"), out reading));
        Assert.Equal((40, 40, 70, 55), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void ARemovedKeyPastUnavailable_ThatReturnsAtARediscovery_StartsAfresh()
    {
        var (source, sampler, time) = CreateChanging(null, "a", "b");
        sampler.Values["a"] = (4, null, null, null);
        sampler.Values["b"] = (1, null, null, null);
        source.Open();
        sampler.Change("b");
        sampler.Values.Remove("a");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(SensorStatus.Stale, source.GetStatus(source.KeyOf("a")));

        // Polling stalls past a's 30 s; the round that ends it finds a back in the catalogue.
        time.Advance(TimeSpan.FromSeconds(30));
        sampler.Change("a", "b");
        source.Poll();
        sampler.Values["a"] = (8, null, null, null);
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();

        Assert.True(source.TryRead(source.KeyOf("a"), out var reading));
        Assert.Equal((8, 8, 8, 8), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
    }

    [Fact]
    public void RemovedKeys_AreUnlistedOnceUnavailable_EvenWhileRediscoveryKeepsFailing()
    {
        var (source, sampler, time) = CreateChanging(null, "a", "b");
        sampler.Values["a"] = (4, null, null, null);
        sampler.Values["b"] = (1, null, null, null);
        source.Open();
        sampler.Change("b");
        sampler.Values.Remove("a");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Contains(source.GetSensors(), s => s.Key == source.KeyOf("a"));

        // The catalogue changes again, but discovering it fails every round from now on.
        sampler.Change("b", "c");
        sampler.DiscoverThrows = new InvalidOperationException("discovery broke");
        for (var i = 0; i < 31; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
        }

        Assert.DoesNotContain(source.GetSensors(), s => s.Key == source.KeyOf("a"));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(source.KeyOf("a")));
        Assert.Contains(source.GetSensors(), s => s.Key == source.KeyOf("b"));
    }

    [Fact]
    public async Task ARestart_OpensAFreshSampler_WithEmptyState_AsBeforeCataloguesCouldChange()
    {
        // A sampler per start, as the macOS source makes one: its CPU load skips its first round (no delta yet).
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var samplers = new List<ScriptedSampler>();
        var source = new PolledSensorSource("test.restart", () =>
        {
            var s = new ScriptedSampler("load");
            samplers.Add(s);
            return s;
        }, Options, time);

        samplers.Clear();
        await source.StartAsync(Ct);
        samplers[0].Set(0, 25);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => source.Polls == 2);
        Assert.Equal(SensorStatus.Live, source.GetStatus(source.KeyOf("load")));
        await source.StopAsync(Ct);

        // Started again at once: the fresh sampler's first round is skipped, so nothing is read yet.
        await source.StartAsync(Ct);
        Assert.Equal(2, samplers.Count);
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(source.KeyOf("load")));
        Assert.False(source.TryRead(source.KeyOf("load"), out _));
        Assert.Equal(["plugin:/test.restart/load"], source.GetSensors().Select(s => s.Key.ToString()));

        // Its statistics start again from its first value.
        samplers[1].Set(0, 75);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => source.Polls == 4);
        Assert.True(source.TryRead(source.KeyOf("load"), out var reading));
        Assert.Equal((75, 75, 75, 75), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));
        await source.StopAsync(Ct);
    }

    /// <summary>
    /// A sampler of number and text sensors whose catalogue the test changes: <see cref="Catalogue"/> is what the next
    /// <see cref="Discover"/> returns, <see cref="Values"/> what each round reads by id (a double or a string, as the
    /// sensor's kind, or an outcome; null fails; absent ids fail), and <see cref="Change"/> bumps the version.
    /// </summary>
    private sealed class TextSampler : ISensorSampler
    {
        public List<(string Id, SensorKind Kind)> Catalogue = [];
        public readonly Dictionary<string, object?> Values = [];
        public long Version;
        private List<(string Id, SensorKind Kind)> _handedOut = [];

        public IReadOnlyList<SensorDefinition> Discover()
        {
            _handedOut = [.. Catalogue];
            return _handedOut.Select(c => new SensorDefinition(c.Id, "Group", "Name " + c.Id, "unit " + c.Id, c.Kind)).ToList();
        }

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                if (!Values.TryGetValue(_handedOut[i].Id, out var v))
                    samples[i].Outcome = SampleOutcome.Failed;
                else if (v is double d)
                    samples[i].Set(d);
                else if (v is SampleOutcome outcome)
                    samples[i].Outcome = outcome;
                else
                    samples[i].SetText((string?)v);
            }
        }

        public long CatalogueVersion => Version;

        public void Change(params (string Id, SensorKind Kind)[] catalogue)
        {
            Catalogue = [.. catalogue];
            Version++;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CollectingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private static (PolledSensorSource Source, TextSampler Sampler, TimerTimeProvider Time) CreateText(Serilog.ILogger? log, params (string Id, SensorKind Kind)[] catalogue)
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var sampler = new TextSampler { Catalogue = [.. catalogue] };
        return (new PolledSensorSource("test.text", () => sampler, Options, time, log: log), sampler, time);
    }

    [Fact]
    public void Unavailable_ATextSensor_DropsItsTextAtOnce_StillTypedText_AndReadsAgainFromTheNextText()
    {
        var (source, sampler, time) = CreateText(null, ("t", SensorKind.Text));
        sampler.Values["t"] = "first";
        source.Open();
        var key = source.KeyOf("t");

        sampler.Values["t"] = SampleOutcome.Unavailable;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text, false), (state.Status, state.Type, state.HasReading));

        sampler.Values["t"] = SampleOutcome.Failed;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.False(source.ReadState(key).HasReading);

        sampler.Values["t"] = "second";
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Live, SensorKind.Text, "second"), (state.Status, state.Type, state.Reading.ValueText));
    }

    [Fact]
    public void Unavailable_ThenGoneFromTheCatalogue_IsUnlistedAtOnce_NeverStaleWithTheOldValue()
    {
        var (source, sampler, time) = CreateText(null, ("speed", SensorKind.Number), ("name", SensorKind.Number));
        sampler.Values["speed"] = 1000d;
        sampler.Values["name"] = 1d;
        source.Open();
        var speed = source.KeyOf("speed");
        var name = source.KeyOf("name");

        sampler.Values["speed"] = SampleOutcome.Unavailable;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.False(source.TryRead(speed, out _));

        // Both leave the catalogue: the one with a reading is kept stale until it is unavailable; the other goes at once.
        sampler.Change();
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal([name], source.GetSensors().Select(s => s.Key));
        Assert.Equal(SensorStatus.Stale, source.GetStatus(name));
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
            Assert.False(source.TryRead(speed, out _));
            Assert.Equal(SensorStatus.Unavailable, source.GetStatus(speed));
        }
    }

    [Fact]
    public void ATextSensor_ReadsItsText_TypedText_WithNoStatistics()
    {
        var (source, sampler, time) = CreateText(null, ("ip", SensorKind.Text), ("load", SensorKind.Number));
        sampler.Values["ip"] = "203.0.113.7";
        sampler.Values["load"] = 12.5;
        source.Open();
        var ip = source.KeyOf("ip");

        var state = source.ReadState(ip);
        Assert.Equal((SensorStatus.Live, SensorKind.Text), (state.Status, state.Type));
        Assert.Equal("203.0.113.7", state.Reading.ValueText);
        Assert.Equal(SensorKind.Text, SensorState.TypeOf(state.Reading));
        Assert.Equal(["unit ip", "unit load"], source.GetSensors().Select(s => s.Unit));

        // A second text: still no statistics (min, max, avg and now stay zero), and the reading is the sampler's string.
        var next = "198.51.100." + 9;
        sampler.Values["ip"] = next;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.True(source.TryRead(ip, out var reading));
        Assert.Same(next, reading.ValueText);
        Assert.Equal((0, 0, 0, 0), (reading.ValueNow, reading.ValueMin, reading.ValueMax, reading.ValueAvg));

        // The number beside it reads as before.
        var load = source.ReadState(source.KeyOf("load"));
        Assert.Equal((SensorStatus.Live, SensorKind.Number, 12.5, (string?)null), (load.Status, load.Type, load.Reading.ValueNow, load.Reading.ValueText));
    }

    [Fact]
    public void ATextSensor_WithoutAReading_IsStillTypedText()
    {
        var (source, sampler, time) = CreateText(null, ("never", SensorKind.Text), ("null", SensorKind.Text), ("denied", SensorKind.Text));
        sampler.Values["null"] = null;
        sampler.Values["denied"] = "x";
        source.Open();

        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text), Of(source.ReadState(source.KeyOf("never"))));
        // Null is a failed read, not an empty text.
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text), Of(source.ReadState(source.KeyOf("null"))));
        Assert.False(source.TryRead(source.KeyOf("null"), out _));

        // Refused by the system: no reading, still text.
        sampler.Values["denied"] = SampleOutcome.PermissionDenied;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal((SensorStatus.PermissionDenied, SensorKind.Text), Of(source.ReadState(source.KeyOf("denied"))));
        Assert.False(source.TryRead(source.KeyOf("denied"), out _));

        // A key the source doesn't list is a number, as before.
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Number), Of(source.ReadState(source.KeyOf("not-listed"))));

        static (SensorStatus, SensorKind) Of(SensorState s) => (s.Status, s.Type);
    }

    [Fact]
    public void AStaleTextSensor_KeepsItsLastText_ThenIsUnavailable()
    {
        var (source, sampler, time) = CreateText(null, ("t", SensorKind.Text));
        sampler.Values["t"] = "first";
        source.Open();
        var key = source.KeyOf("t");

        sampler.Values["t"] = null;
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Stale, SensorKind.Text, "first"), (state.Status, state.Type, state.Reading.ValueText));

        // Polls stop: stale with the last text up to 30 s, then unavailable, still typed text.
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("first", source.ReadState(key).Reading.ValueText);
        time.Advance(TimeSpan.FromSeconds(1));
        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text, (string?)null), (state.Status, state.Type, state.Reading.ValueText));

        // A text again brings it back live.
        sampler.Values["t"] = "second";
        source.Poll();
        state = source.ReadState(key);
        Assert.Equal((SensorStatus.Live, "second"), (state.Status, state.Reading.ValueText));
    }

    [Fact]
    public void ALongText_IsCutAt4096Characters_WithOneWarningPerKey()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var (source, sampler, time) = CreateText(log, ("long", SensorKind.Text), ("edge", SensorKind.Text), ("other", SensorKind.Text));
        sampler.Values["long"] = new string('a', 4096) + "bcd";
        sampler.Values["edge"] = new string('e', 4096);
        sampler.Values["other"] = new string('o', 5000);
        source.Open();
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
        }

        Assert.True(source.TryRead(source.KeyOf("long"), out var reading));
        Assert.Equal(new string('a', 4096), reading.ValueText);
        Assert.True(source.TryRead(source.KeyOf("edge"), out reading));
        Assert.Equal(4096, reading.ValueText!.Length);
        Assert.True(source.TryRead(source.KeyOf("other"), out reading));
        Assert.Equal(4096, reading.ValueText!.Length);

        // Four rounds of two long texts: one warning for each, none for the one exactly at the bound.
        var warnings = events.Where(e => e.Level == Serilog.Events.LogEventLevel.Warning).Select(e => e.RenderMessage()).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("plugin:/test.text/long", StringComparison.Ordinal) && w.Contains("4096", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("plugin:/test.text/other", StringComparison.Ordinal));
    }

    private sealed class WarningCounter : Serilog.Core.ILogEventSink
    {
        public int Count;

        // Counts and keeps nothing: an event holds its key.
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            if (logEvent.Level == Serilog.Events.LogEventLevel.Warning)
                Interlocked.Increment(ref Count);
        }
    }

    [Fact]
    public void KeyChurn_KeepsNoWarningStateForPrunedKeys_AndAReturningKeyWarnsOnceMore()
    {
        var warnings = new WarningCounter();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(warnings).CreateLogger();
        var (source, sampler, time) = CreateText(log);
        var longText = new string('a', 5000);
        source.Open();

        // Each round a fresh key comes with a long text (cut, one warning), then goes and expires.
        const int Rounds = 50;
        var keys = new List<WeakReference>(Rounds);
        for (var r = 0; r < Rounds; r++)
        {
            var id = "k" + r;
            sampler.Values.Clear();
            sampler.Values[id] = longText;
            sampler.Change((id, SensorKind.Text));
            time.Advance(TimeSpan.FromSeconds(31));
            source.Poll();
            time.Advance(TimeSpan.FromSeconds(1));
            source.Poll();
            Assert.Equal(PolledSensorSource.MaxTextChars, source.ReadState(source.KeyOf(id)).Reading.ValueText!.Length);
            keys.Add(TrackKey(source, source.KeyOf(id)));
        }

        sampler.Values.Clear();
        sampler.Change();
        time.Advance(TimeSpan.FromSeconds(31));
        source.Poll();
        Assert.Empty(source.GetSensors());
        Assert.Equal(Rounds, warnings.Count);

        // Nothing of a pruned key is kept: the source holds no state for it.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.DoesNotContain(keys, k => k.IsAlive);

        // A key that returns after its slot was pruned warns once more.
        sampler.Values["k0"] = longText;
        sampler.Change(("k0", SensorKind.Text));
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal(PolledSensorSource.MaxTextChars, source.ReadState(source.KeyOf("k0")).Reading.ValueText!.Length);
        Assert.Equal(Rounds + 1, warnings.Count);
    }

    /// <summary>A weak reference to the id string of the listed key equal to <paramref name="key"/> (the source's own).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference TrackKey(PolledSensorSource source, SensorKey key) =>
        new(source.GetSensors().Single(s => s.Key == key).Key.Id);

    [Fact]
    public void ACutText_NeverEndsInHalfASurrogatePair()
    {
        var (source, sampler, _) = CreateText(null, ("emoji", SensorKind.Text));
        sampler.Values["emoji"] = new string('a', 4095) + "\U0001F600" + "z";
        source.Open();

        Assert.True(source.TryRead(source.KeyOf("emoji"), out var reading));
        Assert.Equal(new string('a', 4095), reading.ValueText);
    }

    [Fact]
    public void ATextSensor_IsCarriedThroughRediscovery_LikeANumber()
    {
        var (source, sampler, time) = CreateText(null, ("ip", SensorKind.Text), ("speed", SensorKind.Number));
        sampler.Values["ip"] = "10.0.0.2";
        sampler.Values["speed"] = 100.0;
        source.Open();

        // A new interface comes; ip stays (its text read this round is kept), speed goes.
        sampler.Change(("ip", SensorKind.Text), ("name", SensorKind.Text));
        sampler.Values.Remove("speed");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        var ip = source.ReadState(source.KeyOf("ip"));
        Assert.Equal((SensorStatus.Live, SensorKind.Text, "10.0.0.2"), (ip.Status, ip.Type, ip.Reading.ValueText));
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text), (source.ReadState(source.KeyOf("name")).Status, source.ReadState(source.KeyOf("name")).Type));

        // The interface goes: its text is stale (last text kept) until it is unavailable, then unlisted.
        sampler.Change(("name", SensorKind.Text));
        sampler.Values["name"] = "en0";
        sampler.Values.Remove("ip");
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        ip = source.ReadState(source.KeyOf("ip"));
        Assert.Equal((SensorStatus.Stale, SensorKind.Text, "10.0.0.2"), (ip.Status, ip.Type, ip.Reading.ValueText));
        Assert.Equal("en0", source.ReadState(source.KeyOf("name")).Reading.ValueText);

        // Back before it is unavailable: carried again, and live with its new text.
        sampler.Change(("ip", SensorKind.Text), ("name", SensorKind.Text));
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        Assert.Equal((SensorStatus.Stale, "10.0.0.2"), (source.GetStatus(source.KeyOf("ip")), source.ReadState(source.KeyOf("ip")).Reading.ValueText));
        sampler.Values["ip"] = "10.0.0.3";
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        ip = source.ReadState(source.KeyOf("ip"));
        Assert.Equal((SensorStatus.Live, SensorKind.Text, "10.0.0.3"), (ip.Status, ip.Type, ip.Reading.ValueText));
    }

    [Fact]
    public void AKeyWhoseKindChanges_AtARediscovery_StartsAfresh()
    {
        var (source, sampler, time) = CreateText(null, ("x", SensorKind.Number));
        sampler.Values["x"] = 5.0;
        source.Open();

        sampler.Change(("x", SensorKind.Text));
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();

        // The number isn't read as text: no reading until the first text.
        var state = source.ReadState(source.KeyOf("x"));
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text), (state.Status, state.Type));
        sampler.Values["x"] = "five";
        time.Advance(TimeSpan.FromSeconds(1));
        source.Poll();
        state = source.ReadState(source.KeyOf("x"));
        Assert.Equal((SensorStatus.Live, SensorKind.Text, "five"), (state.Status, state.Type, state.Reading.ValueText));
    }

    [Fact]
    public void ASampleOfTheWrongKind_IsAFailedRead()
    {
        var (source, sampler, _) = CreateText(null, ("t", SensorKind.Text), ("n", SensorKind.Number));
        sampler.Values["t"] = 1.0;
        sampler.Values["n"] = "one";
        source.Open();

        Assert.Equal((SensorStatus.Unavailable, SensorKind.Text), (source.GetStatus(source.KeyOf("t")), source.ReadState(source.KeyOf("t")).Type));
        Assert.Equal((SensorStatus.Unavailable, SensorKind.Number), (source.GetStatus(source.KeyOf("n")), source.ReadState(source.KeyOf("n")).Type));
    }

    [Fact]
    public void ATableDefinition_IsSkipped_WithAWarning()
    {
        var events = new List<Serilog.Events.LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        var (source, sampler, _) = CreateText(log, ("table", SensorKind.Table), ("n", SensorKind.Number));
        sampler.Values["n"] = 3.0;
        source.Open();

        Assert.Equal(["plugin:/test.text/n"], source.GetSensors().Select(s => s.Key.ToString()));
        Assert.Equal(3, source.ReadState(source.KeyOf("n")).Reading.ValueNow);
        Assert.Single(events, e => e.Level == Serilog.Events.LogEventLevel.Warning);
    }

    [Fact]
    public void ReadingAText_AllocatesNothing()
    {
        var (source, sampler, _) = CreateText(null, ("t", SensorKind.Text));
        sampler.Values["t"] = "steady";
        source.Open();
        var key = source.KeyOf("t");
        // A live text, so the measure is of a read that returns one (an unavailable read allocates nothing either).
        var state = source.ReadState(key);
        Assert.Equal((SensorStatus.Live, SensorKind.Text, "steady"), (state.Status, state.Type, state.Reading.ValueText));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            source.ReadState(key);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>
    /// The most a wait on the source's own threads lasts, and a hold the test releases: a sanity limit, never the behaviour
    /// under test (the source's times are on the manual clock). Its loop and its start's discovery run on the thread pool,
    /// which in a full run under parallel gates can be short of threads for seconds (blocking waits like these, and
    /// program launches that stall on this Mac's endpoint protection); 5 s didn't always cover a start. Each wait still
    /// ends as soon as its condition holds, and each hold as soon as the test releases it.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5, Ct);
        }
    }
}
