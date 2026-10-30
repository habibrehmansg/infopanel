using System.Diagnostics;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// Opt-in (INFOPANEL_PLUGIN_TESTS=1): the bundled Extras on this machine. Its Network Info plugin looks up the public IP
// on the network, so the suite doesn't run it by default. Clock and Drive Info list their sensors within 10 s; the
// Windows-only plugins are skipped elsewhere; the host's memory goes to the test output.
public sealed class BundledExtrasTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-extras-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task The_bundled_Extras_start_and_Clock_and_Drive_Info_list_sensors_within_10_seconds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PLUGIN_TESTS") == "1", "Set INFOPANEL_PLUGIN_TESTS=1 to run the bundled Extras.");
        var bundled = Path.Combine(_root, "bundled");
        PluginFixtures.Copy("InfoPanel.Extras", bundled);

        using var factory = new EngineFactory();
        using var engine = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", bundled);
        });
        using var client = engine.CreateClient();
        var source = engine.Services.GetRequiredService<PluginSensorSource>();

        var clock = Stopwatch.StartNew();
        while (!(source.GetSensors().Any(s => s.Key.Id.StartsWith("/clock-plugin/", StringComparison.Ordinal))
                 && source.GetSensors().Any(s => s.Key.Id.StartsWith("/drive-info-plugin/", StringComparison.Ordinal))))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Clock and Drive Info didn't list sensors within 10 s; listed: {source.GetSensors().Count}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var runner = Assert.Single(engine.Services.GetRequiredService<PluginHostManager>().Runners);
        using var host = Process.GetProcessById(runner.ProcessId!.Value);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Extras host {host.Id}: {source.GetSensors().Count} sensors after {clock.Elapsed.TotalSeconds:0.0} s; working set {host.WorkingSet64 / 1024 / 1024} MiB");
        Assert.Contains(source.GetSensors(), s => s.Group == "Clock › Clock" && s.Name == "Second");
    }
}
