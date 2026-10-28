using System.Text.Json;
using InfoPanel.Sensors;
using InfoPanel.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>What follows the engine settings on the engine host, live.</summary>
public sealed class SettingsWiringTests
{
    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheSensorHistory_SamplesAtTheGraphRate_AndFollowsAChange()
    {
        using var factory = new EngineFactory();
        var history = factory.Services.GetRequiredService<SensorHistory>();
        var settings = factory.Services.GetRequiredService<SettingsStore>();
        Assert.Equal(TimeSpan.FromMilliseconds(1000), history.Interval);

        settings.Apply(Change("""{"graphRate":250}"""));
        Assert.Equal(TimeSpan.FromMilliseconds(250), history.Interval);

        settings.Apply(Change("""{"graphRate":null}"""));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), history.Interval);
    }

    [Fact]
    public void AStoredGraphRate_IsTheHistorysFirstInterval()
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Path.Combine(factory.DataDir, "settings.json"), """{"format":"infopanel.settings/1","graphRate":3000}""");

        Assert.Equal(TimeSpan.FromMilliseconds(3000), factory.Services.GetRequiredService<SensorHistory>().Interval);
    }
}
