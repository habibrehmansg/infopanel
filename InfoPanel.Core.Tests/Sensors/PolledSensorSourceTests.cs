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
            HoldDiscovery?.Wait(TimeSpan.FromSeconds(10));
            return ids.Select(id => new SensorDefinition(id, "Group", "Name " + id, "u")).ToList();
        }

        public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
        {
            if (Elapsed.Count > 0 && HoldSample != null)
            {
                InSample.Set();
                HoldSample.Wait(TimeSpan.FromSeconds(10));
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
        Assert.True(sampler.InDiscovery.Wait(TimeSpan.FromSeconds(5)));
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
        Assert.True(sampler.InSample.Wait(TimeSpan.FromSeconds(5)));
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

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5, Ct);
        }
    }
}
