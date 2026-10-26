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
