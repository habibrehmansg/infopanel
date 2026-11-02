using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Network;
using InfoPanel.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

// The engine's own sources that read on every OS (the clock, the drives, the network): registered outside mock mode and a
// replay, none in either; the network's public address lookup is left out under --offline, and follows the engine's
// settings (sources.network.publicIp) as they change.
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

    [Fact]
    public void TheNetwork_FollowsThePublicAddressSetting_FromTheEnginesSettings_AsItChanges()
    {
        var data = Path.Combine(Path.GetTempPath(), "infopanel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new DataPaths(data);
            File.WriteAllText(paths.SettingsFile, """{"format":"infopanel.settings/1","sources":{"network":{"publicIp":false}}}""");
            var settings = SettingsStore.Open(paths);
            var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton(settings);
            services.AddBuiltInSources(new EngineOptions());

            using var provider = services.BuildServiceProvider();
            var network = Assert.IsType<NetworkSource>(provider.GetServices<ISensorSource>().Single(s => s.Id == "infopanel.network"));

            Assert.False(network.PublicIp);
            Assert.False(network.Lookup!.Enabled);
            settings.Apply(JsonDocument.Parse("""{"sources":{"network":{"publicIp":true}}}""").RootElement);
            Assert.True(network.PublicIp);
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
