using System.Globalization;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

public class SensorWalkTests
{
    private static readonly SensorKey HwInfoKey = SensorKey.HwInfo(-1, 0xF0000300, 0, 7);
    private static readonly SensorKey LibreKey = SensorKey.Libre("/amdcpu/0/load/0");
    private static readonly SensorKey PluginKey = SensorKey.Plugin("/someplugin/sensor");

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mock mode's values for keys no source handles, pinned as they were before the walk was shared: the start, the
    /// first five steps, and now, min, max and average after 120 steps, for a key of each kind, to the last bit.
    /// </summary>
    [Theory]
    [InlineData("hwinfo", "46.90474913921207 44.91785017307848 48.19827768672258 46.8718624969013 47.712675849157385 49.496392845470226 56.45897437730881 36.33141379777445 56.45897437730881 45.607798810981464")]
    [InlineData("libre", "68.64680411759764 68.22667416464537 71.07028988962993 70.40686262893026 74.07568535940652 73.5849649466857 73.22629867813743 61.40163323788926 77.06692337376407 69.10336255327742")]
    [InlineData("plugin", "50.88911366183311 50.06681272108108 46.33088762294501 43.77623095268384 42.93682229806017 40.288327220986176 50.92724174763108 39.48254745138876 59.11303815426493 50.83062166540105")]
    public void MockModes_walk_for_a_key_no_source_handles_is_pinned(string kind, string expected)
    {
        var key = kind switch { "hwinfo" => HwInfoKey, "libre" => LibreKey, _ => PluginKey };
        var hub = new SensorHub([], new SensorHubOptions { SimulateUnknown = true });
        var values = new List<string>();

        Assert.True(hub.TryRead(key, out var start));
        values.Add(R(start.ValueNow));
        for (var i = 1; i <= 120; i++)
        {
            hub.Tick();
            Assert.True(hub.TryRead(key, out var step));
            if (i <= 5)
                values.Add(R(step.ValueNow));
            if (i == 120)
                values.AddRange([R(step.ValueNow), R(step.ValueMin), R(step.ValueMax), R(step.ValueAvg)]);
        }

        var actual = string.Join(" ", values);
        Assert.True(actual == expected, $"Mock mode's walk for {key} moved: {actual}");
    }
}
