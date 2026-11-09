using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// Opt-in (INFOPANEL_PLUGIN_TESTS=1): the bundled Extras on this machine, run in a plugin host, so the suite doesn't run it
// by default. Its PluginInfo.ini says Platforms=windows: on Windows its host runs within 10 s and the host's memory goes to
// the test output; elsewhere it is unsupported and no host starts.
public sealed class BundledExtrasTests : IDisposable
{
    private readonly string _root = PluginFixtures.NewTempRoot("ip-extras-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    // Not opt-in: the built Extras' plugin classes, read from its metadata (no plugin is created). The engine reads
    // HWiNFO's gadget values (infopanel.hwinfo-gadget), the clock (infopanel.clock), the drives (infopanel.drives), the
    // network (infopanel.network), the weather (infopanel.weather), the volume (infopanel.volume) and the system
    // information (infopanel.system-info) itself, so none of the HWiNFO Registry, Clock, Drive Info, Network Info,
    // Weather, Volume Info and System Info plugins is bundled any more: the Extras hold no plugin.
    [Fact]
    public void The_bundled_Extras_hold_no_plugin_not_the_HWiNFO_Registry_Clock_Drive_Info_Network_Info_Weather_Volume_Info_or_System_Info_ones()
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

            Assert.Empty(plugins);
        }

        var bytes = File.ReadAllBytes(dll);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("hwinfo-registry-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("clock-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("drive-info-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("network-info-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("weather-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("volume-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("NAudio")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("system-info-plugin")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("PerformanceCounter")));
    }

    [Fact]
    public async Task The_bundled_Extras_start_within_10_seconds_and_list_no_Clock_Drive_Info_Network_Info_Weather_Volume_Info_or_System_Info()
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
        var manager = engine.Services.GetRequiredService<PluginHostManager>();
        if (!OperatingSystem.IsWindows())
        {
            // Windows only: unsupported here, and no host starts.
            while (manager.Runners.Count == 0)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The engine didn't find Extras within 10 s");
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            var extras = Assert.Single(manager.Runners);
            Assert.False(manager.IsSupported(extras));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Equal(PluginHostState.Stopped, extras.State);
            Assert.Null(extras.ProcessId);
            return;
        }

        while (manager.Runners.SingleOrDefault() is not { State: PluginHostState.Running })
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The Extras host didn't run within 10 s; listed: {source.GetSensors().Count}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var runner = Assert.Single(manager.Runners);
        using var host = Process.GetProcessById(runner.ProcessId!.Value);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Extras host {host.Id}: {source.GetSensors().Count} sensors after {clock.Elapsed.TotalSeconds:0.0} s; working set {host.WorkingSet64 / 1024 / 1024} MiB");
        // The clock, the drives, the network, the weather, the volume and the system information are the engine's own
        // sources now: no plugin lists them.
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/clock-plugin/", StringComparison.Ordinal));
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/drive-info-plugin/", StringComparison.Ordinal));
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/network-info-plugin/", StringComparison.Ordinal));
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/weather-plugin/", StringComparison.Ordinal));
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/volume-plugin/", StringComparison.Ordinal));
        Assert.DoesNotContain(source.GetSensors(), s => s.Key.Id.StartsWith("/system-info-plugin/", StringComparison.Ordinal));
    }
}
