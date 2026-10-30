using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The engine with plugins on: a test engine whose bundled folder holds the test plugin starts a real host for it (the
// built engine in its host mode), and its sensors read through the API and a scene's inputs under 1.x's keys. Test
// engines otherwise start no host.
public sealed class PluginEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Counter = "plugin:/test-fixture/readings/counter";

    private readonly string _root = PluginFixtures.NewTempRoot("ip-pe-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task Test_engines_start_no_plugin_host()
    {
        using var factory = new EngineFactory();
        using var client = factory.CreateApiClient();

        var sensors = await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct);

        var manager = factory.Services.GetRequiredService<PluginHostManager>();
        Assert.False(manager.Options.Enabled);
        Assert.Empty(manager.Runners);
        Assert.DoesNotContain(sensors!, s => s.GetProperty("source").GetString() == PluginSensorSource.SourceId);
    }

    [Fact]
    public async Task The_test_plugins_number_text_and_table_read_through_the_API_and_a_scene_under_1x_keys()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);

        var sensors = await Listed(client);

        var mine = sensors.Where(s => s.GetProperty("source").GetString() == PluginSensorSource.SourceId).ToArray();
        Assert.Equal(
            [Counter, "plugin:/test-fixture/readings/status", "plugin:/test-fixture/readings/processes", "plugin:/test-fixture/eph-value"],
            mine.Select(s => s.GetProperty("key").GetString()).ToArray());
        Assert.Equal(["Test Fixture › Readings", "Test Fixture › Readings", "Test Fixture › Readings", "Test Fixture › Ephemeral"],
            mine.Select(s => s.GetProperty("group").GetString()).ToArray());
        Assert.Equal("ticks", mine[0].GetProperty("unit").GetString());

        var values = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!.ToDictionary(v => v.GetProperty("key").GetString()!);
        Assert.Equal("live", values[Counter].GetProperty("status").GetString());
        Assert.Equal("ok", values["plugin:/test-fixture/readings/status"].GetProperty("text").GetString());
        Assert.Equal("alpha, 12.5%", values["plugin:/test-fixture/readings/processes"].GetProperty("text").GetString());
        Assert.Equal(7, values["plugin:/test-fixture/eph-value"].GetProperty("now").GetDouble());

        var inputs = engine.Services.GetRequiredService<SceneRuntimeOptions>().Inputs;
        var table = inputs.Read("plugin:/test-fixture/readings/processes", "table");
        Assert.Equal(SceneInputStatus.Live, table.Status);
        Assert.Equal(["Name", "Load"], table.Table.Columns);
        Assert.Equal(["alpha", "12.5%"], table.Table.Rows[0]);
        Assert.Equal(["beta", "40.0%"], table.Table.Rows[1]);

        // Its log and its settings are in the data folder, nowhere else.
        Assert.Single(Directory.GetFiles(Path.Combine(factory.DataDir, "logs", "plugins"), "InfoPanel.TestFixture-*.log"));
        Assert.True(File.Exists(Path.Combine(factory.DataDir, "plugin-settings", "InfoPanel.TestFixture", "test-fixture.json")));
    }

    [Fact]
    public async Task A_killed_host_comes_back_and_its_sensors_are_stale_meanwhile_and_stopping_the_engine_ends_it()
    {
        using var factory = new EngineFactory();
        var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        await Listed(client);
        var hub = engine.Services.GetRequiredService<SensorHub>();
        var runner = Assert.Single(engine.Services.GetRequiredService<PluginHostManager>().Runners);
        var first = runner.ProcessId!.Value;
        using (var host = Process.GetProcessById(first))
            TestContext.Current.TestOutputHelper?.WriteLine($"plugin host {first}: working set {host.WorkingSet64 / 1024 / 1024} MiB, private {host.PrivateMemorySize64 / 1024 / 1024} MiB");

        using (var host = Process.GetProcessById(first))
            host.Kill();

        await Eventually.Until(() => hub.GetStatus(SensorKey.Plugin("/test-fixture/readings/counter")) == SensorStatus.Stale, "the sensor is stale while its host restarts");
        await Until(() => runner.ProcessId is { } pid && pid != first && hub.GetStatus(SensorKey.Plugin("/test-fixture/readings/counter")) == SensorStatus.Live,
            "a new host runs and the sensor is live again");
        var second = runner.ProcessId!.Value;

        engine.Dispose();

        Assert.True(Exited(second), "the host is gone once the engine stopped");
    }

    [Fact]
    public async Task A_plugin_that_throws_at_load_is_reported_and_its_host_runs_on()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "throw-load"));
        using var client = Client(engine);
        var runner = await Runner(engine);

        await Until(() => runner.State == PluginHostState.Running, "the host runs");
        await Until(() => runner.Errors.Any(e => e.Contains("load failed on request")), "the load failure is reported");
        Assert.Equal(PluginHostState.Running, runner.State);
    }

    [Fact]
    public async Task A_host_that_keeps_exiting_is_restarted_three_times_then_fails_with_what_it_said()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "exit-after-ms=200"));
        using var client = Client(engine);
        var runner = await Runner(engine);

        await Until(() => runner.State == PluginHostState.Failed, "the package fails", TimeSpan.FromSeconds(40));
        Assert.Contains("code 3", runner.Reason);
        Assert.Contains("fixture: exiting on request", runner.Reason);
    }

    [Fact]
    public async Task A_host_listing_20000_entries_has_16384_listed()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "entries=20000"));
        using var client = Client(engine);
        await Listed(client);

        Assert.Equal(16_384, engine.Services.GetRequiredService<PluginSensorSource>().GetSensors().Count);
    }

    [Fact]
    public async Task A_host_writing_without_newlines_is_read_in_chunks_and_only_its_stderrs_end_is_kept()
    {
        var package = PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "spew-bytes=8388608");
        var pipeName = PluginHostConnection.NewPipeName();
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = new PluginProcessLauncher().Start(new PluginHostCommand(pipeName, PluginFixtures.DllOf(package),
            Path.Combine(_root, "logs"), Path.Combine(_root, "settings"), "information"));
        try
        {
            await pipe.WaitForConnectionAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            using var rpc = new JsonRpc(pipe);
            rpc.AddLocalRpcTarget<IPluginClientCallback>(new PluginHostModeTests.RecordingCallback(), null);
            rpc.StartListening();
            await rpc.InvokeWithCancellationAsync<List<PluginMetadataDto>>(nameof(IPluginHostService.InitializeAsync), null, Ct)
                .WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // Its plugin writes 8 MiB to stdout and 8 MiB to stderr with no newline, then "spew-end", and runs on: what it
            // wrote is read as it comes, not held for a newline that never comes, and only stderr's last 4 KiB are kept.
            await Eventually.Until(() => process.ErrorTail.EndsWith("spew-end", StringComparison.Ordinal), "stderr's end is read while the host runs");
            Assert.False(process.HasExited);
            Assert.InRange(process.ErrorTail.Length, 1, 4096);
            Assert.Matches("^e+spew-end$", process.ErrorTail);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task A_packages_settings_folder_linked_into_the_1x_folder_fails_it_and_nothing_is_written_there()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        using var factory = new EngineFactory();
        var assets = Path.Combine(factory.V1Dir, "assets");
        Directory.CreateDirectory(assets);
        var settings = Path.Combine(factory.DataDir, "plugin-settings");
        Directory.CreateDirectory(settings);
        Directory.CreateSymbolicLink(Path.Combine(settings, PluginFixtures.FixtureFolder), assets);
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        var runner = await Runner(engine);

        await Until(() => runner.State == PluginHostState.Failed, "the package fails");
        Assert.Contains("is a link", runner.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(assets));
    }

    private static WebApplicationFactory<Program> WithPlugins(EngineFactory factory, string package) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", Path.GetDirectoryName(package)!);
        });

    private static HttpClient Client(WebApplicationFactory<Program> engine)
    {
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        return client;
    }

    private static async Task<PluginHostRunner> Runner(WebApplicationFactory<Program> engine)
    {
        var manager = engine.Services.GetRequiredService<PluginHostManager>();
        await Until(() => manager.Runners.Count == 1, "the package's host starts");
        return manager.Runners[0];
    }

    /// <summary>The sensor list once the test plugin's counter is in it.</summary>
    private static async Task<JsonElement[]> Listed(HttpClient client)
    {
        JsonElement[] sensors = [];
        await Until(() =>
        {
            sensors = client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct).GetAwaiter().GetResult()!;
            return sensors.Any(s => s.GetProperty("key").GetString() == Counter && s.GetProperty("status").GetString() == "live");
        }, "the test plugin's sensors are listed, live");
        return sensors;
    }

    private static async Task Until(Func<bool> condition, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(50, Ct);
        }
    }

    private static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
