using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Serilog.Core;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The <c>service.address</c> setting (spec/docs/api.md, "Settings"): an origin, <c>https</c>, or <c>http</c> on a loopback
/// host, stored in its normal form; <c>null</c> sets it back to the default; anything else, and any other member of
/// <c>service</c> (a credential included), is refused.
/// </summary>
public sealed class ServiceAddressSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File_ => _temp.Combine("settings.json");

    private SettingsStore Open() => SettingsStore.Open(File_, _temp.Path, Logger.None, faults: null);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheAddress_IsNullByDefault_StoredInItsNormalForm_AndSetBackWithNull()
    {
        var store = Open();
        Assert.Null(store.Current.Settings.Service.Address);

        var set = store.Apply(Change("""{"service":{"address":"HTTPS://Service.Example:443/"}}"""));

        Assert.Equal("https://service.example", set.Settings.Service.Address);
        Assert.Equal("https://service.example", JsonNode.Parse(File.ReadAllText(File_))!["service"]!["address"]!.GetValue<string>());
        Assert.Equal("https://service.example", Open().Current.Settings.Service.Address);

        var reset = store.Apply(Change("""{"service":{"address":null}}"""));

        Assert.Null(reset.Settings.Service.Address);
        Assert.False(JsonNode.Parse(File.ReadAllText(File_))!.AsObject().ContainsKey("service"));
    }

    [Theory]
    [InlineData("""{"service":{"address":"http://service.example"}}""", "/service/address")]
    [InlineData("""{"service":{"address":"https://service.example/api"}}""", "/service/address")]
    [InlineData("""{"service":{"address":443}}""", "/service/address")]
    [InlineData("""{"service":{"credential":"abc"}}""", "/service/credential")]
    [InlineData("""{"service":"https://service.example"}""", "/service")]
    public void AnythingElse_IsRefused_AndNothingIsStored(string change, string path)
    {
        var store = Open();

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(Change(change)));

        Assert.Equal([(path, "schema")], refused.Errors.Select(e => (e.Path, e.Code)));
        Assert.False(File.Exists(File_));
        Assert.Equal([(path, "schema")], SettingsStore.CheckChange(Change(change), SpecLimits.Default).Select(e => (e.Path, e.Code)));
    }

    [Fact]
    public void AStoredAddressThatIsntOne_GetsTheDefault_AndTheNextChangeDropsIt()
    {
        File.WriteAllText(File_, """{"format":"infopanel.settings/1","service":{"address":"http://service.example"}}""");

        var store = Open();

        Assert.Null(store.Current.Settings.Service.Address);
        store.Apply(Change("""{"frameRate":20}"""));
        Assert.False((JsonNode.Parse(File.ReadAllText(File_))!["service"] as JsonObject)?.ContainsKey("address") ?? false);
    }
}
