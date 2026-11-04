using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Serilog.Core;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The <c>service.aiApps</c> setting (spec/docs/api.md, "AI apps elsewhere"): off by default, <c>true</c> or <c>false</c>,
/// kept in <c>settings.json</c> beside the address; <c>null</c> sets it back to off; anything else is refused.
/// </summary>
public sealed class AiAppsSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File_ => _temp.Combine("settings.json");

    private SettingsStore Open() => SettingsStore.Open(File_, _temp.Path, Logger.None, faults: null);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AiApps_IsOffByDefault_KeptWhenTurnedOn_BesideTheAddress_AndOffAgainWithNull()
    {
        var store = Open();
        Assert.False(store.Current.Settings.Service.AiApps);

        store.Apply(Change("""{"service":{"address":"https://service.example"}}"""));
        var on = store.Apply(Change("""{"service":{"aiApps":true}}"""));

        Assert.True(on.Settings.Service.AiApps);
        Assert.Equal("https://service.example", on.Settings.Service.Address);
        Assert.True(JsonNode.Parse(File.ReadAllText(File_))!["service"]!["aiApps"]!.GetValue<bool>());
        Assert.True(Open().Current.Settings.Service.AiApps);

        var off = store.Apply(Change("""{"service":{"aiApps":null}}"""));
        Assert.False(off.Settings.Service.AiApps);
        Assert.Equal("https://service.example", off.Settings.Service.Address);
        Assert.False(((JsonObject)JsonNode.Parse(File.ReadAllText(File_))!["service"]!).ContainsKey("aiApps"));
    }

    [Theory]
    [InlineData("""{"service":{"aiApps":"on"}}""")]
    [InlineData("""{"service":{"aiApps":1}}""")]
    public void AnythingButABoolean_IsRefused_AndNothingIsStored(string change)
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(change)));

        Assert.Equal([("/service/aiApps", "schema")], refused.Errors.Select(e => (e.Path, e.Code)));
        Assert.False(File.Exists(File_));
        Assert.Equal([("/service/aiApps", "schema")], SettingsStore.CheckChange(Change(change), SpecLimits.Default).Select(e => (e.Path, e.Code)));
    }

    [Fact]
    public void AStoredValueThatIsntABoolean_IsOff()
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","service":{"aiApps":"yes"}}""");

        Assert.False(Open().Current.Settings.Service.AiApps);
    }
}
