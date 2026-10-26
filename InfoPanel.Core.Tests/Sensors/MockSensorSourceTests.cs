using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using System.Data;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

public class MockSensorSourceTests
{
    private static SensorReading Read(MockSensorSource source, SensorKey key)
    {
        Assert.True(source.TryRead(key, out var reading), $"no reading for {key}");
        return reading;
    }

    [Fact]
    public void Catalog_MatchesWebClient()
    {
        Assert.Equal(35, MockSensorCatalog.Sensors.Count);
        Assert.Equal(35, MockSensorCatalog.Sensors.Select(s => s.Key).Distinct().Count());
        Assert.Equal(7, MockSensorCatalog.Sensors.Select(s => s.Group).Distinct().Count());
        Assert.Equal("plugin:/infopanel.mock/gpu/gpu-temp", MockSensorCatalog.KeyOf("gpu.temp").ToString());
        Assert.Equal("plugin:/infopanel.mock/rtss/fps-low1", MockSensorCatalog.KeyOf("fps.low1").ToString());
        Assert.All(MockSensorCatalog.Sensors, s => Assert.InRange(s.Base, s.Min, s.Max));
    }

    [Fact]
    public void ListsThe35SensorsPlusTableAndText()
    {
        using var source = new MockSensorSource(1);
        var sensors = source.GetSensors();

        Assert.Equal(37, sensors.Count);
        Assert.All(sensors, s => Assert.Equal(MockSensorCatalog.SourceId, s.SourceId));
        Assert.All(sensors, s => Assert.True(source.Handles(s.Key)));
        Assert.Contains(sensors, s => s.Key == MockSensorCatalog.TopProcessesKey);
        Assert.Contains(sensors, s => s.Key == MockSensorCatalog.NowPlayingKey);
    }

    /// <summary>Reference values from the web client's own <c>step</c> with mulberry32(42), 120 then 125 steps.</summary>
    [Fact]
    public void Walk_IsAFaithfulPortOfWebClient()
    {
        using var source = new MockSensorSource(42);

        AssertReading(Read(source, MockSensorCatalog.KeyOf("cpu.temp")), 64.32841969999882, 57.02481121353978, 66.29491636900846, 62.00695855339382);
        AssertReading(Read(source, MockSensorCatalog.KeyOf("gpu.memclock")), 1313, 1313, 1313, 1313);
        AssertReading(Read(source, MockSensorCatalog.KeyOf("net.down")), 23042.23985398939, 133.34937753736529, 61532.91976123117, 13967.943913492314);
        AssertReading(Read(source, MockSensorCatalog.KeyOf("frametime")), 5.730282187304262, 5.730282187304262, 8.128927518631333, 6.982297210677517);

        for (var i = 0; i < 5; i++)
            source.Tick();

        AssertReading(Read(source, MockSensorCatalog.KeyOf("cpu.temp")), 64.60882805415389, 57.02481121353978, 66.29491636900846, 62.052854056439585);
        AssertReading(Read(source, MockSensorCatalog.KeyOf("net.down")), 10975.73412136035, 133.34937753736529, 61532.91976123117, 14042.553594994717);

        static void AssertReading(SensorReading r, double now, double min, double max, double avg)
        {
            Assert.Equal(now, r.ValueNow, 1e-9);
            Assert.Equal(min, r.ValueMin, 1e-9);
            Assert.Equal(max, r.ValueMax, 1e-9);
            Assert.Equal(avg, r.ValueAvg, 1e-9);
        }
    }

    [Fact]
    public void SameSeed_GivesSameValues_DifferentSeed_Differs()
    {
        using var a = new MockSensorSource(7);
        using var b = new MockSensorSource(7);
        using var c = new MockSensorSource(8);
        for (var i = 0; i < 10; i++)
        {
            a.Tick();
            b.Tick();
            c.Tick();
        }

        var differs = false;
        foreach (var def in MockSensorCatalog.Sensors)
        {
            Assert.Equal(Read(a, def.Key).ValueNow, Read(b, def.Key).ValueNow);
            Assert.Equal(a.GetBackfill(def.Key), b.GetBackfill(def.Key));
            differs |= Read(a, def.Key).ValueNow != Read(c, def.Key).ValueNow;
        }

        Assert.True(differs);
        Assert.Equal(Read(a, MockSensorCatalog.NowPlayingKey).ValueText, Read(b, MockSensorCatalog.NowPlayingKey).ValueText);
        Assert.Equal(Read(a, MockSensorCatalog.TopProcessesKey).ValueText, Read(b, MockSensorCatalog.TopProcessesKey).ValueText);
    }

    [Fact]
    public void Values_StayInRange_WithConsistentStats()
    {
        using var source = new MockSensorSource(1234);
        for (var i = 0; i < MockSensorSource.HistoryLength + 50; i++)
            source.Tick();

        foreach (var def in MockSensorCatalog.Sensors)
        {
            var r = Read(source, def.Key);
            var history = source.GetBackfill(def.Key);

            Assert.Equal(def.Unit, r.Unit);
            Assert.All(history, v => Assert.InRange(v, def.Min, def.Max));
            Assert.Equal(MockSensorSource.HistoryLength, history.Count);
            Assert.Equal(r.ValueNow, history[^1]);
            Assert.True(r.ValueMin <= r.ValueAvg + 1e-9, def.Id);
            Assert.True(r.ValueAvg <= r.ValueMax + 1e-9, def.Id);
            Assert.True(r.ValueMin <= history.Min() + 1e-9, def.Id);
            Assert.True(r.ValueMax >= history.Max() - 1e-9, def.Id);
        }
    }

    [Fact]
    public void Backfills120Samples()
    {
        using var source = new MockSensorSource(5);

        Assert.All(MockSensorCatalog.Sensors, def => Assert.Equal(MockSensorSource.BackfillSamples, source.GetBackfill(def.Key).Count));
        Assert.Equal(0, source.Ticks);

        source.Tick();
        Assert.All(MockSensorCatalog.Sensors, def => Assert.Equal(MockSensorSource.BackfillSamples + 1, source.GetBackfill(def.Key).Count));
        Assert.Empty(source.GetBackfill(MockSensorCatalog.TopProcessesKey));
    }

    [Fact]
    public void TopProcesses_IsAPluginTable()
    {
        using var source = new MockSensorSource(3);
        var reading = Read(source, MockSensorCatalog.TopProcessesKey);

        var table = Assert.IsType<DataTable>(reading.ValueTable);
        Assert.Equal(MockSensorCatalog.TopProcessesFormat, reading.ValueTableFormat);
        Assert.False(string.IsNullOrEmpty(reading.ValueText));
        Assert.Equal(["Process", "CPU", "Memory"], table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(8, table.Rows.Count);
        foreach (DataRow row in table.Rows)
        {
            Assert.IsType<PluginText>(row[0]);
            Assert.IsType<PluginSensor>(row[1]);
            Assert.IsType<PluginSensor>(row[2]);
        }

        var cpu = table.Rows.Cast<DataRow>().Select(r => ((PluginSensor)r[1]).Value).ToList();
        Assert.Equal(cpu.OrderByDescending(v => v), cpu);
    }

    [Fact]
    public void NowPlaying_IsAText()
    {
        using var source = new MockSensorSource(3);
        var reading = Read(source, MockSensorCatalog.NowPlayingKey);

        Assert.False(string.IsNullOrEmpty(reading.ValueText));
        Assert.Null(reading.ValueTable);
    }

    [Fact]
    public void UnknownMockKey_IsHandledButHasNoReading()
    {
        using var source = new MockSensorSource(3);
        var key = SensorKey.Plugin("/infopanel.mock/gpu/nope");

        Assert.True(source.Handles(key));
        Assert.False(source.TryRead(key, out _));
        Assert.False(source.Handles(SensorKey.Plugin("/other/gpu/gpu-temp")));
        Assert.False(source.Handles(SensorKey.Libre("/infopanel.mock/gpu/gpu-temp")));
    }

    [Fact]
    public async Task StartAsync_TicksEverySecond()
    {
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var source = new MockSensorSource(9, time);
        await source.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, source.Ticks);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.Until(() => source.Ticks == 1);

        await source.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentStartAndStop_LeaveAtMostOneLoop_AndStopAlwaysEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var source = new MockSensorSource(9, time);

        for (var round = 0; round < 200; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(
                () => i % 2 == 0 ? source.StartAsync(ct) : source.StopAsync(ct), ct)))
                .WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.Equal(source.IsRunning ? 1 : 0, time.TimerCount);
        }

        await source.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(source.IsRunning);
        Assert.Equal(0, time.TimerCount);

        var ticks = source.Ticks;
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ticks, source.Ticks);
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(5);
        }
    }
}
