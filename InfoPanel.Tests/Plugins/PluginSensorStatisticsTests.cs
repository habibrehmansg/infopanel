using InfoPanel.Plugins;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The SDK's PluginSensor: a value that isn't finite (NaN, a plugin's "I don't know") is its value, but is left out of its
// minimum, maximum and average, so one unknown reading doesn't spoil them for the next 60; and values may be set from
// any thread.
public class PluginSensorStatisticsTests
{
    [Fact]
    public void A_value_that_isnt_finite_is_the_value_but_is_left_out_of_the_minimum_maximum_and_average()
    {
        var sensor = new PluginSensor("s", "S", 10, "V");
        sensor.Value = 20;

        foreach (var unknown in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            sensor.Value = unknown;

            Assert.Equal(unknown, sensor.Value);
            Assert.Equal((10f, 20f, 20f), (sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg));
        }
    }

    [Fact]
    public void The_average_after_a_value_that_isnt_finite_is_the_finite_values_average()
    {
        var sensor = new PluginSensor("s", "S", 0) { SampleWindow = 3 };
        sensor.Value = 2;
        sensor.Value = float.NaN;
        sensor.Value = 4;

        Assert.Equal((0f, 4f, 3f), (sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg));

        // The window counts finite values only: 2 leaves it once three more have come.
        sensor.Value = 6;
        sensor.Value = float.NaN;
        sensor.Value = 8;
        Assert.Equal(6f, sensor.ValueAvg);
    }

    [Fact]
    public void A_sensor_made_without_a_value_takes_its_first_finite_value_as_its_minimum_maximum_and_average()
    {
        var sensor = new PluginSensor("u", "Unknown", float.NaN, "V");
        Assert.True(float.IsNaN(sensor.Value));

        sensor.Value = 5;
        Assert.Equal((5f, 5f, 5f, 5f), (sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg));

        sensor.Value = 3;
        Assert.Equal((3f, 5f, 4f), (sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg));
    }

    [Fact]
    public async Task Values_set_from_several_threads_at_once_keep_the_statistics_whole()
    {
        var sensor = new PluginSensor("s", "S", 1);
        using var start = new Barrier(4);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 200_000; i++)
                sensor.Value = (i + w) % 3 == 0 ? float.NaN : 1 + (i % 2);
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(writers);

        sensor.Value = 2;
        Assert.Equal((1f, 2f), (sensor.ValueMin, sensor.ValueMax));
        Assert.InRange(sensor.ValueAvg, 1f, 2f);
    }
}
