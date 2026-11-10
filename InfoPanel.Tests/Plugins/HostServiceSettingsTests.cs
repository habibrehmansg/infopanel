using System.Text.Json;
using InfoPanel.Plugins.Host;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugin host applies a plugin's stored settings at its start, key by key: a stored value whose JSON kind doesn't fit
// its setting's type is skipped, and the keys after it still load, so the next settings change saves them back unchanged.
// Each test runs the host in this process on a copy of the test plugin, with a settings folder of its own.
public sealed class HostServiceSettingsTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-hs-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task A_stored_value_that_doesnt_fit_its_type_is_skipped_and_the_keys_after_it_load_and_are_saved_back()
    {
        var package = PluginFixtures.CopyFixture(Path.Combine(_root, "plugins"));
        var settings = Path.Combine(_root, "settings");
        Directory.CreateDirectory(settings);
        var file = Path.Combine(settings, PluginFixtures.FixturePluginId + ".json");
        // A double kept for an Integer setting and a number for a String one, before the keys that fit.
        File.WriteAllText(file, """{"count": 1.5, "label": 7, "apiKey": "k", "enabled": false}""");
        var host = new HostService(PluginFixtures.DllOf(package), settings);

        try
        {
            await host.InitializeAsync();
            // Any settings change saves every setting's current value: the stored ones must have loaded to be kept.
            await host.ApplyConfigAsync(PluginFixtures.FixturePluginId, "mode", "b");

            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal("k", saved.RootElement.GetProperty("apiKey").GetString());
            Assert.False(saved.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("b", saved.RootElement.GetProperty("mode").GetString());
            var loaded = (await host.GetConfigPropertiesAsync(PluginFixtures.FixturePluginId)).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal("k", loaded["apiKey"]);
            Assert.Equal(false, loaded["enabled"]);
            // The two that didn't fit keep their defaults.
            Assert.Equal(3, loaded["count"]);
            Assert.Equal("fixture", loaded["label"]);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
}
