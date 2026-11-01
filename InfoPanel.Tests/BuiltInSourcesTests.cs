using InfoPanel.Hosting;
using InfoPanel.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

// The engine's own sources that read on every OS (the clock, the drives): registered outside mock mode and a replay, none in
// either.
public class BuiltInSourcesTests
{
    [Theory]
    [InlineData(false, false, new[] { "infopanel.clock", "infopanel.drives" })]
    [InlineData(true, false, new string[0])]
    [InlineData(false, true, new string[0])]
    public void The_clock_and_the_drives_are_registered_outside_mock_mode_and_a_replay(bool mock, bool replay, string[] expected)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddBuiltInSources(new EngineOptions { Mock = mock, ReplaySensors = replay ? "recording" : null });

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetServices<ISensorSource>().Select(s => s.Id));
    }
}
