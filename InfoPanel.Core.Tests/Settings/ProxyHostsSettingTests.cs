using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Serilog.Core;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The <c>proxy.hosts</c> setting (spec/docs/api.md, "Where requests come from"): the names a reverse proxy on this
/// computer reaches the engine by, at most 8 lower-case DNS names of two labels or more, each at most 253 characters; no
/// IP literal, no wildcard, no trailing dot, no port; none by default. The store's own check, which a change and the file
/// both go through, agrees with the schema's.
/// </summary>
public sealed class ProxyHostsSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File_ => _temp.Combine("settings.json");

    private SettingsStore Open() => SettingsStore.Open(File_, _temp.Path, Logger.None, faults: null);

    private static JsonElement Change(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private JsonNode? StoredHosts() => JsonNode.Parse(File.ReadAllText(File_))!["proxy"]?["hosts"];

    [Fact]
    public void Names_AreKept_AsGiven_AndRemovedWithNull()
    {
        var store = Open();
        var label63 = new string('a', 63);
        var name253 = string.Join('.', label63, label63, label63, new string('b', 61));
        var names = new[] { "pc.tailnet.ts.net", "a.b", "xn--bcher-kva.example", "1pc.example", name253, "c.d", "e.f", "g.h" };
        var json = JsonSerializer.Serialize(names);

        Assert.Empty(SettingsStore.CheckChange(Change($$$"""{"proxy":{"hosts":{{{json}}}}}"""), SpecLimits.Default));
        store.Apply(Change($$$"""{"proxy":{"hosts":{{{json}}}}}"""));

        Assert.Equal(names, StoredHosts()!.AsArray().Select(n => n!.GetValue<string>()));

        // Kept across a start and another change.
        Open().Apply(Change("""{"frameRate":20}"""));
        Assert.Equal(names, StoredHosts()!.AsArray().Select(n => n!.GetValue<string>()));

        Open().Apply(Change("""{"proxy":{"hosts":null}}"""));
        Assert.Null(JsonNode.Parse(File.ReadAllText(File_))!["proxy"]);
    }

    [Theory]
    [InlineData("PC.tailnet.ts.net")]
    [InlineData("100.64.0.1")]
    [InlineData("127.1")]
    [InlineData("1.2.3.4")]
    [InlineData("0x7f.1")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("fd7a::1")]
    [InlineData("localhost")]
    [InlineData("pc")]
    [InlineData("*.tailnet.ts.net")]
    [InlineData("pc.tailnet.ts.net.")]
    [InlineData(".pc.ts.net")]
    [InlineData("pc..ts.net")]
    [InlineData("-pc.ts.net")]
    [InlineData("pc-.ts.net")]
    [InlineData("pc.ts.1")]
    [InlineData("pc.ts.-net")]
    [InlineData("pc.ts.net:443")]
    [InlineData("pc.ts.net/")]
    [InlineData("pć.ts.net")]
    [InlineData("pc_1.ts.net")]
    [InlineData(" pc.ts.net")]
    [InlineData("")]
    public void ANameThatIsntOne_IsRefused_AndNothingIsStored(string name)
    {
        var store = Open();
        var change = Change($$$"""{"proxy":{"hosts":["proxy.example", {{{JsonSerializer.Serialize(name)}}}]}}""");

        var refused = Assert.Throws<SettingsChangeException>(() => store.Apply(change));

        Assert.Equal([("/proxy/hosts/1", "schema")], refused.Errors.Select(e => (e.Path, e.Code)));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void TooLongANameOrLabel_IsRefused()
    {
        var label63 = new string('a', 63);
        var name254 = string.Join('.', label63, label63, label63, new string('b', 62));
        Assert.Equal(254, name254.Length);

        foreach (var name in new[] { name254, new string('a', 64) + ".example", "example." + new string('a', 64) })
        {
            Assert.Equal([("/proxy/hosts/0", "schema")],
                SettingsStore.CheckChange(Change($$$"""{"proxy":{"hosts":["{{{name}}}"]}}"""), SpecLimits.Default).Select(e => (e.Path, e.Code)));
        }
    }

    [Theory]
    [InlineData("""["a.example","b.example","c.example","d.example","e.example","f.example","g.example","h.example","i.example"]""", "/proxy/hosts")]
    [InlineData("""["a.example","a.example"]""", "/proxy/hosts")]
    [InlineData("\"a.example\"", "/proxy/hosts")]
    [InlineData("[1]", "/proxy/hosts/0")]
    [InlineData("[null]", "/proxy/hosts/0")]
    [InlineData("""{"hosts":[]}""", "/proxy/hosts")]
    public void AListThatIsntOne_IsRefused(string hosts, string path)
    {
        var refused = Assert.Throws<SettingsChangeException>(() => Open().Apply(Change($$$"""{"proxy":{"hosts":{{{hosts}}}}}""")));

        Assert.Equal([(path, "schema")], refused.Errors.Select(e => (e.Path, e.Code)));
    }

    [Theory]
    [InlineData("""{"proxy":{"names":[]}}""", "/proxy/names")]
    [InlineData("""{"proxy":[]}""", "/proxy")]
    public void AnythingElseUnderProxy_IsRefused(string change, string path)
    {
        Assert.Equal([(path, "schema")], SettingsStore.CheckChange(Change(change), SpecLimits.Default).Select(e => (e.Path, e.Code)));
    }

    [Theory]
    [InlineData("""["proxy.example","100.64.0.1"]""")]
    [InlineData("""["Proxy.example"]""")]
    [InlineData("""["a.example","a.example"]""")]
    [InlineData("""["a.example","b.example","c.example","d.example","e.example","f.example","g.example","h.example","i.example"]""")]
    [InlineData("\"proxy.example\"")]
    public void AStoredListThatIsntOne_IsDropped_ByTheNextChange(string hosts)
    {
        File.WriteAllText(File_, $$$"""{"format":"infopanel.settings/1","proxy":{"hosts":{{{hosts}}}}}""");

        Open().Apply(Change("""{"frameRate":20}"""));

        Assert.Null(StoredHosts());
        Assert.Equal(20, JsonNode.Parse(File.ReadAllText(File_))!["frameRate"]!.GetValue<int>());
    }
}
