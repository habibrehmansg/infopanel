using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The conformance suite against each MS9132 family's driver over the kit's fakes: the Jonsbo DS339 (its
/// panel's EDID the DS339's) and the VMAX 4.6" (its own). The probe's writes are held to the family's queries: the EDID
/// reads, 1.4.x's startup block and its power-off, each as the feature report it sets.
/// </summary>
public sealed class Ms9132ConformanceTests
{
    private static void NoWait(TimeSpan _, CancellationToken stop) => stop.ThrowIfCancellationRequested();

    private static Func<PanelTraceEntry, bool> ProbeQueries() =>
        Conformance.ProbeWrites([.. JonsboMs9132.Startup.Select(r => Ms9132Protocol.Feature(r)), Ms9132Protocol.Feature(JonsboMs9132.PowerOff)]);

    [Fact]
    public void TheJonsboDriver_PassesTheConformanceSuite_OnADs339()
    {
        Assert.Empty(Conformance.Check(() =>
        {
            var devices = new Ms9132Devices();
            var fake = Ms9132Fake.Add(devices, EdidSamples.Ds339);
            return new ConformanceSetup(new JonsboDriver(devices) { Wait = NoWait }, Ms9132Fake.Candidate, fake.Device) { ProbeWrite = ProbeQueries() };
        }));
    }

    [Fact]
    public void TheVmaxDriver_PassesTheConformanceSuite_OnAVmax46()
    {
        Assert.Empty(Conformance.Check(() =>
        {
            var devices = new Ms9132Devices();
            var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
            return new ConformanceSetup(new VmaxDriver(devices) { Wait = NoWait }, Ms9132Fake.Candidate, fake.Device) { ProbeWrite = ProbeQueries() };
        }));
    }
}
