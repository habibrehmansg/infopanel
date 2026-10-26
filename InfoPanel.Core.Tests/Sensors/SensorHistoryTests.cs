using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

public class SensorHistoryTests
{
    private static readonly SensorKey Key = SensorKey.Plugin("/test/counter");
    private static readonly SensorKey TextKey = SensorKey.Plugin("/test/text");

    /// <summary>Each read of <see cref="Key"/> returns the next integer from 0; <see cref="TextKey"/> is text.</summary>
    private sealed class CountingReader : ISensorReader
    {
        public int Reads;

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            if (key == TextKey)
            {
                reading = new SensorReading("text");
                return true;
            }

            if (key == Key)
            {
                var v = Reads++;
                reading = new SensorReading(v, v, v, v, "");
                return true;
            }

            reading = default;
            return false;
        }
    }

    private static TimerTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task SamplesRequestedKeys_Every1000Ms()
    {
        var time = NewTime();
        var reader = new CountingReader();
        var history = new SensorHistory(reader, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Assert.Empty(history.GetSamples(Key));
        var run = history.RunAsync(cts.Token);

        time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(history.GetSamples(Key));

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.Until(() => history.GetSamples(Key).Count == 1);

        for (var expected = 2; expected <= 4; expected++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var n = expected;
            await Wait.Until(() => history.GetSamples(Key).Count == n);
        }

        // Read 0 was GetSamples checking the key has a reading before tracking it.
        Assert.Equal([1, 2, 3, 4], history.GetSamples(Key));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public void OnlyRequestedKeysAreSampled()
    {
        var reader = new CountingReader();
        var history = new SensorHistory(reader, NewTime());

        history.SampleNow();

        Assert.Equal(0, reader.Reads);
        Assert.Empty(history.TrackedKeys);
    }

    [Fact]
    public void KeepsAtMost4096_OldestFirst()
    {
        var history = new SensorHistory(new CountingReader(), NewTime());
        history.GetSamples(Key);

        for (var i = 0; i < SensorHistory.MaxSamples + 10; i++)
            history.SampleNow();

        var samples = history.GetSamples(Key);
        Assert.Equal(SensorHistory.MaxSamples, samples.Count);
        Assert.Equal(11, samples[0]); // read 0 was the tracking check
        Assert.Equal(SensorHistory.MaxSamples + 10, samples[^1]);
        Assert.True(samples.Zip(samples.Skip(1)).All(p => p.Second == p.First + 1));
    }

    [Fact]
    public void DropsKeysUntouchedFor10Minutes()
    {
        var time = NewTime();
        var history = new SensorHistory(new CountingReader(), time);
        history.GetSamples(Key);

        time.Advance(TimeSpan.FromMinutes(10));
        history.SampleNow();
        Assert.Contains(Key, history.TrackedKeys);

        history.GetSamples(Key);
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1));
        history.SampleNow();
        Assert.DoesNotContain(Key, history.TrackedKeys);

        Assert.Empty(history.GetSamples(Key));
    }

    [Fact]
    public void SeedsFromBackfill()
    {
        var history = new SensorHistory(new CountingReader(), NewTime(), key => key == Key ? [100, 101, 102] : []);

        Assert.Equal([100, 101, 102], history.GetSamples(Key));
        history.SampleNow();
        Assert.Equal([100, 101, 102, 1], history.GetSamples(Key)); // read 0 was the tracking check
    }

    [Fact]
    public void SeedsFromMockBackfill()
    {
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock]);
        var history = new SensorHistory(hub, NewTime(), hub.GetBackfill);

        Assert.Equal(MockSensorSource.BackfillSamples, history.GetSamples(MockSensorCatalog.KeyOf("gpu.load")).Count);
    }

    [Fact]
    public void MissingKeys_AreNotTracked()
    {
        var history = new SensorHistory(new CountingReader(), NewTime(), _ => [1, 2, 3]);
        var missing = SensorKey.Plugin("/test/missing");

        Assert.Empty(history.GetSamples(missing));
        Assert.DoesNotContain(missing, history.TrackedKeys);
        Assert.Equal(0, history.CapacityOf(missing));
    }

    [Fact]
    public void MissingKeysPastTheHubsSimulationCap_AreNotTracked()
    {
        var hub = new SensorHub([], new SensorHubOptions { MaxSimulatedKeys = 1 });
        var history = new SensorHistory(hub, NewTime());

        history.GetSamples(SensorKey.Plugin("/test/a"));
        history.GetSamples(SensorKey.Plugin("/test/b"));

        Assert.Equal([SensorKey.Plugin("/test/a")], history.TrackedKeys);
    }

    [Fact]
    public void TracksAtMostMaxSeries_EvictingTheLeastRecentlyRequested()
    {
        var time = NewTime();
        var hub = new SensorHub([], new SensorHubOptions { MaxSimulatedKeys = SensorHistory.MaxSeries + 10 });
        var history = new SensorHistory(hub, time);
        var keys = Enumerable.Range(0, SensorHistory.MaxSeries + 1).Select(i => SensorKey.Plugin($"/test/k{i}")).ToArray();

        foreach (var key in keys[..SensorHistory.MaxSeries])
        {
            history.GetSamples(key);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        history.GetSamples(keys[0]); // now the most recent
        time.Advance(TimeSpan.FromMilliseconds(1));
        history.GetSamples(keys[^1]);

        var tracked = history.TrackedKeys;
        Assert.Equal(SensorHistory.MaxSeries, tracked.Count);
        Assert.Contains(keys[0], tracked);
        Assert.Contains(keys[^1], tracked);
        Assert.DoesNotContain(keys[1], tracked);
    }

    [Fact]
    public void Buffers_GrowOnDemand()
    {
        var history = new SensorHistory(new CountingReader(), NewTime());
        history.GetSamples(Key);
        Assert.InRange(history.CapacityOf(Key), 1, 64);

        for (var i = 0; i < 100; i++)
            history.SampleNow();

        Assert.InRange(history.CapacityOf(Key), 100, 256);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (double)i), history.GetSamples(Key));
    }

    [Fact]
    public void TextAndMissingReadings_AddNoSample()
    {
        var history = new SensorHistory(new CountingReader(), NewTime());
        var missing = SensorKey.Plugin("/test/missing");
        history.GetSamples(TextKey);
        history.GetSamples(missing);

        history.SampleNow();

        Assert.Empty(history.GetSamples(TextKey));
        Assert.Empty(history.GetSamples(missing));
    }
}
