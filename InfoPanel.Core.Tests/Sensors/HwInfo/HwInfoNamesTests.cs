using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// <see cref="HwInfoNames"/>: the reading a binding goes to by name, and what each lookup costs, counted (an import holds
/// the shared import gate while it matches every binding).
/// </summary>
public class HwInfoNamesTests
{
    private const int Local = -1;

    private static SensorInfo Reading(int remote, uint sensorId, uint instance, uint readingId, string name) =>
        new(SensorKey.HwInfo(remote, sensorId, instance, readingId), HwInfoSource.SourceId, $"Sensor {sensorId}", name, "°C");

    [Fact]
    public void Match_PrefersTheFirstInASensorWithTheBindingsId_ElseTheFirst()
    {
        var names = HwInfoNames.Of([
            Reading(Local, 10, 0, 1, "Temperature"),
            Reading(Local, 20, 0, 1, "Temperature"),
            Reading(Local, 20, 1, 2, "Temperature"),
            Reading(0, 20, 0, 1, "Temperature"),
        ]);

        Assert.Equal(SensorKey.HwInfo(Local, 20, 0, 1), names.Match(Local, 20, "Temperature")?.Key);
        Assert.Equal(SensorKey.HwInfo(Local, 10, 0, 1), names.Match(Local, 99, "Temperature")?.Key);
        Assert.Equal(SensorKey.HwInfo(0, 20, 0, 1), names.Match(0, 99, "Temperature")?.Key);
        Assert.Null(names.Match(1, 20, "Temperature"));
        Assert.Null(names.Match(Local, 20, "Load"));
        Assert.Null(names.Match(Local, 20, null));
    }

    /// <summary>
    /// A large catalogue of equal names, each in its own sensor, and many bindings whose sensor id none of them has: each
    /// lookup costs a bounded number of probes, not one per equal-name reading.
    /// </summary>
    [Fact]
    public void Match_LargeEqualNameCatalogue_EachBindingTakesBoundedProbes()
    {
        const int Readings = 5_000, Bindings = 5_000;
        var names = HwInfoNames.Of(Enumerable.Range(0, Readings).Select(i => Reading(Local, (uint)i, 0, 1, "Temperature")));

        for (var i = 0; i < Bindings; i++)
            Assert.Equal(SensorKey.HwInfo(Local, 0, 0, 1), names.Match(Local, uint.MaxValue - (uint)i, "Temperature")?.Key);
        Assert.Equal(SensorKey.HwInfo(Local, Readings - 1, 0, 1), names.Match(Local, Readings - 1, "Temperature")?.Key);

        Assert.InRange(names.Probes, 1, 2L * (Bindings + 1));
    }
}
