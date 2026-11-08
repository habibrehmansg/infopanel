using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Network;
using InfoPanel.Sensors.Weather;
using InfoPanel.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

// The engine's own sources (the clock, the drives, the network, the weather on every OS; the volume on macOS and
// Windows; the system information on macOS):
// registered outside mock mode and a replay, none in either; the network's public address lookup is left out under
// --offline, and follows the engine's settings (sources.network.publicIp) as they change; the weather follows
// sources.weather.
public class BuiltInSourcesTests
{
    [Theory]
    [InlineData(false, false, new[] { "infopanel.clock", "infopanel.drives", "infopanel.network", "infopanel.weather" })]
    [InlineData(true, false, new string[0])]
    [InlineData(false, true, new string[0])]
    public void The_clock_the_drives_the_network_the_weather_on_macOS_and_Windows_the_volume_and_on_macOS_the_system_information_are_registered_outside_mock_mode_and_a_replay(bool mock, bool replay, string[] everywhere)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddBuiltInSources(new EngineOptions { Mock = mock, ReplaySensors = replay ? "recording" : null });

        using var provider = services.BuildServiceProvider();

        string[] expected = everywhere.Length == 0 ? everywhere
            : OperatingSystem.IsMacOS() ? [.. everywhere, "infopanel.volume", "infopanel.system-info"]
            : OperatingSystem.IsWindows() ? [.. everywhere, "infopanel.volume"]
            : everywhere;
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

    [Fact]
    public void TheWeather_FollowsItsSettings_FromTheEnginesSettings_AsTheyChange_AndHasNoServiceWithoutAClient()
    {
        var data = Path.Combine(Path.GetTempPath(), "infopanel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new DataPaths(data);
            File.WriteAllText(paths.SettingsFile, """{"format":"infopanel.settings/1","sources":{"weather":{"enabled":false,"units":"imperial"}}}""");
            var settings = SettingsStore.Open(paths);
            var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton(settings);
            services.AddBuiltInSources(new EngineOptions());

            using var provider = services.BuildServiceProvider();
            var weather = Assert.IsType<WeatherSource>(provider.GetServices<ISensorSource>().Single(s => s.Id == "infopanel.weather"));

            Assert.True(weather.Imperial);
            Assert.Equal((SourceStateKind.Off, "disabled"), (weather.State.Kind, weather.State.Reason!.Code));
            settings.Apply(JsonDocument.Parse("""{"sources":{"weather":{"enabled":true,"units":"metric"}}}""").RootElement);
            Assert.False(weather.Imperial);
            Assert.Equal(SourceStateKind.Starting, weather.State.Kind);
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
