using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
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

    // Not opt-in: the built Extras' plugin classes, read from its metadata (no plugin is created). The engine reads
    // HWiNFO's gadget values itself (infopanel.hwinfo-gadget), so the HWiNFO Registry plugin isn't bundled any more.
    [Fact]
    public void The_bundled_Extras_hold_six_plugins_and_not_the_HWiNFO_Registry_one()
    {
        var dll = PluginFixtures.DllOf(PluginFixtures.BuiltFolder("InfoPanel.Extras"));
        using (var pe = new PEReader(File.OpenRead(dll)))
        {
            var metadata = pe.GetMetadataReader();
            var plugins = new List<string>();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (type.BaseType.Kind != HandleKind.TypeReference)
                    continue;
                var baseType = metadata.GetTypeReference((TypeReferenceHandle)type.BaseType);
                if (metadata.GetString(baseType.Namespace) == "InfoPanel.Plugins" && metadata.GetString(baseType.Name) == "BasePlugin")
                    plugins.Add(metadata.GetString(type.Name));
            }

            Assert.Equal(["ClockPlugin", "DriveInfoPlugin", "NetworkInfoPlugin", "SystemInfoPlugin", "VolumePlugin", "WeatherPlugin"], plugins.Order(StringComparer.Ordinal));
        }

        var bytes = File.ReadAllBytes(dll);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("hwinfo-registry-plugin")));
    }

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
