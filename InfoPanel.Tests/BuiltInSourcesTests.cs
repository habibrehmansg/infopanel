using InfoPanel.Hosting;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Network;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

// The engine's own sources that read on every OS (the clock, the drives, the network): registered outside mock mode and a
// replay, none in either; the network's public address lookup is left out under --offline.
public class BuiltInSourcesTests
{
    [Theory]
    [InlineData(false, false, new[] { "infopanel.clock", "infopanel.drives", "infopanel.network" })]
    [InlineData(true, false, new string[0])]
    [InlineData(false, true, new string[0])]
    public void The_clock_the_drives_and_the_network_are_registered_outside_mock_mode_and_a_replay(bool mock, bool replay, string[] expected)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddBuiltInSources(new EngineOptions { Mock = mock, ReplaySensors = replay ? "recording" : null });

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetServices<ISensorSource>().Select(s => s.Id));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void The_network_has_a_public_address_lookup_unless_offline(bool offline, bool lookup)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddBuiltInSources(new EngineOptions { Offline = offline });

        using var provider = services.BuildServiceProvider();
        var network = Assert.IsType<NetworkSource>(provider.GetServices<ISensorSource>().Single(s => s.Id == "infopanel.network"));

        Assert.Equal(lookup, network.Lookup != null);
    }
}
