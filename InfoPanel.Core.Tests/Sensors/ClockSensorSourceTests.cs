using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The clock source (<c>infopanel.clock</c>): the 1.x Clock plugin's eight numbers under keys of its own, computed at
/// each read from the time provider's local time (its zone, daylight saving included), always live with min, max and
/// average the value now; no other key is its own, and the plugin's keys are never answered.
/// </summary>
public class ClockSensorSourceTests
{
    private static readonly string[] Ids = ["day", "month", "year", "12-hour", "24-hour", "minute", "second", "pm"];

    /// <summary>A zone eight hours ahead of UTC, with no daylight saving.</summary>
    private static readonly TimeZoneInfo PlusEight = TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test+8", "Test+8");

    private static ManualTimeProvider At(int year, int month, int day, int hour, int minute, int second, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new ManualTimeProvider(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime(), zone);
    }

    private static double Read(ClockSensorSource source, string id)
    {
        Assert.True(source.TryRead(ClockSensorSource.KeyOf(id), out var reading), id);
        return reading.ValueNow;
    }

    /// <summary>Every entry now, in <see cref="Ids"/>' order.</summary>
    private static double[] ReadAll(ClockSensorSource source) => Ids.Select(id => Read(source, id)).ToArray();

    [Fact]
    public void Lists_the_eight_entries_in_1x_s_order_under_its_own_keys_in_the_group_Clock()
    {
        var sensors = new ClockSensorSource(At(2026, 11, 1, 9, 0, 0)).GetSensors();

        Assert.Equal(Ids.Select(id => $"infopanel:/clock/{id}"), sensors.Select(s => s.Key.ToString()));
        Assert.Equal(["Day", "Month", "Year", "12 Hour", "24 Hour", "Minute", "Second", "PM"], sensors.Select(s => s.Name));
        Assert.All(sensors, s => Assert.Equal(("infopanel.clock", "Clock", ""), (s.SourceId, s.Group, s.Unit)));
        Assert.Equal("infopanel.clock", new ClockSensorSource(TimeProvider.System).Id);
    }

    [Theory]
    // day, month, year, 12-hour, 24-hour, minute, second, pm
    [InlineData(0, 0, 0, 12, 0, 0, 0, 0)]
    [InlineData(11, 59, 59, 11, 11, 59, 59, 0)]
    [InlineData(12, 0, 0, 12, 12, 0, 0, 1)]
    [InlineData(13, 5, 7, 1, 13, 5, 7, 1)]
    [InlineData(23, 59, 59, 11, 23, 59, 59, 1)]
    public void The_hour_on_both_clocks_and_PM_follow_1x_s_rules(int hour, int minute, int second, int h12, int h24, int m, int s, int pm)
    {
        var source = new ClockSensorSource(At(2026, 3, 14, hour, minute, second));

        Assert.Equal([14, 3, 2026, h12, h24, m, s, pm], ReadAll(source));
    }

    [Fact]
    public void Each_read_is_computed_from_the_clock_then_with_nothing_started()
    {
        var time = At(2026, 12, 31, 23, 59, 59);
        var source = new ClockSensorSource(time);

        Assert.Equal([31, 12, 2026, 11, 23, 59, 59, 1], ReadAll(source));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([1, 1, 2027, 12, 0, 0, 0, 0], ReadAll(source));
    }

    [Fact]
    public void The_29th_of_February_of_a_leap_year()
    {
        var time = At(2028, 2, 28, 23, 59, 59);
        var source = new ClockSensorSource(time);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([29, 2, 2028], ReadAll(source)[..3]);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal([1, 3, 2028], ReadAll(source)[..3]);
    }

    [Fact]
    public void Reads_the_providers_local_zone_not_UTC()
    {
        // 16:30 UTC on the 1st is 00:30 on the 2nd eight hours ahead.
        var source = new ClockSensorSource(new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 16, 30, 0, TimeSpan.Zero), PlusEight));

        Assert.Equal([2, 1, 2026, 12, 0, 30, 0, 0], ReadAll(source));
    }

    [Fact]
    public void Daylight_saving_follows_the_zones_rules_New_Yorks_fall_back_hour_comes_twice()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 2026-11-01: clocks go back from 02:00 EDT to 01:00 EST, at 06:00 UTC.
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 5, 59, 59, TimeSpan.Zero), newYork);
        var source = new ClockSensorSource(time);

        Assert.Equal([1, 11, 2026, 1, 1, 59, 59, 0], ReadAll(source));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([1, 11, 2026, 1, 1, 0, 0, 0], ReadAll(source));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal([1, 11, 2026, 2, 2, 0, 0, 0], ReadAll(source));
    }

    [Fact]
    public void Always_live_with_min_max_and_average_the_value_now_through_the_hub()
    {
        var hub = new SensorHub([new ClockSensorSource(At(2026, 11, 1, 13, 5, 7))]);

        foreach (var id in Ids)
        {
            var state = hub.ReadState(ClockSensorSource.KeyOf(id));
            Assert.Equal((SensorStatus.Live, SensorKind.Number), (state.Status, state.Type));
            var r = state.Reading;
            Assert.Equal((r.ValueNow, r.ValueNow, r.ValueNow, ""), (r.ValueMin, r.ValueMax, r.ValueAvg, r.Unit));
        }
        Assert.Equal(7, hub.ReadState(ClockSensorSource.KeyOf("second")).Reading.ValueNow);
        Assert.Equal(new SourceStateEntry("infopanel.clock", SourceState.Running, 8), Assert.Single(hub.SourceStates()));
    }

    [Fact]
    public void An_unknown_entry_under_its_prefix_is_its_own_and_unavailable_and_no_other_key_is()
    {
        var source = new ClockSensorSource(At(2026, 11, 1, 9, 0, 0));
        var weekday = SensorKey.Plugin("/infopanel.clock/weekday");

        Assert.True(source.Handles(weekday));
        Assert.Equal(SensorStatus.Unavailable, source.GetStatus(weekday));
        Assert.False(source.TryRead(weekday, out _));
        Assert.False(source.Handles(SensorKey.Plugin("/infopanel.clockx/second")));
        Assert.False(source.Handles(SensorKey.Plugin("/infopanel.macos/cpu/load")));
        Assert.False(source.Handles(new SensorKey(InfoPanel.Enums.SensorType.Libre, "/infopanel.clock/second")));
    }

    [Fact]
    public void The_1x_plugins_keys_are_never_answered()
    {
        var source = new ClockSensorSource(At(2026, 11, 1, 9, 0, 0));
        var hub = new SensorHub([source]);

        foreach (var id in Ids)
        {
            var old = SensorKey.Plugin($"/clock-plugin/clock/{id}");
            Assert.False(source.Handles(old), id);
            Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(old));
            Assert.False(hub.TryRead(old, out _));
        }
        Assert.DoesNotContain(hub.GetSensors(), s => s.Key.Id.StartsWith("/clock-plugin/", StringComparison.Ordinal));
    }
}
