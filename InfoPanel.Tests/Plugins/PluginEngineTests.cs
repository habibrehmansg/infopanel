using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
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

    // The test plugin's picture (its image mode: a 1 × 1 PNG in an image entry) is an image sensor: listed as one, its
    // reading the picture's reference, its bytes answered by the picture request, as they are, also while its host
    // restarts and after.
    [Fact]
    public async Task The_test_plugins_picture_is_an_image_sensor_answered_by_the_picture_request_also_across_a_restart()
    {
        const string cover = "plugin:/test-fixture/pictures/cover";
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "image"));
        using var client = Client(engine);
        await Listed(client);
        JsonElement[] sensors = [];
        await Until(async () =>
        {
            sensors = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct))!;
            return sensors.Any(s => s.GetProperty("key").GetString() == cover && s.GetProperty("status").GetString() == "live");
        }, "the test plugin's picture is listed, live");

        var listed = sensors.Single(s => s.GetProperty("key").GetString() == cover);
        Assert.Equal(("image", "Test Fixture › Pictures", PluginSensorSource.SourceId),
            (listed.GetProperty("type").GetString(), listed.GetProperty("group").GetString(), listed.GetProperty("source").GetString()));
        var values = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!.ToDictionary(v => v.GetProperty("key").GetString()!);
        var reference = values[cover].GetProperty("text").GetString()!;
        Assert.Equal(PluginPictures.ReferenceOf(PluginPictures.Pixel), reference);
        await AssertPicture(client, cover, reference);

        var hub = engine.Services.GetRequiredService<SensorHub>();
        var runner = Assert.Single(engine.Services.GetRequiredService<PluginHostManager>().Runners);
        var first = runner.ProcessId!.Value;
        using (var host = Process.GetProcessById(first))
            host.Kill();
        var key = SensorKey.Plugin("/test-fixture/pictures/cover");
        await Until(() => runner.ProcessId is { } pid && pid != first && hub.GetStatus(key) == SensorStatus.Live, "a new host runs and its picture is live again");

        Assert.Equal(reference, hub.ReadState(key).Reading.ValueText);
        await AssertPicture(client, cover, reference);
    }

    /// <summary>The picture request answers the test plugin's PNG under <paramref name="reference"/>, as it is, not to be stored.</summary>
    private static async Task AssertPicture(HttpClient client, string key, string reference)
    {
        using var answer = await client.GetAsync($"/api/sensors/image?key={Uri.EscapeDataString(key)}&ref={reference}", Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("image/png", answer.Content.Headers.ContentType?.MediaType);
        Assert.True(answer.Headers.CacheControl?.NoStore);
        Assert.Equal(PluginPictures.Pixel, await answer.Content.ReadAsByteArrayAsync(Ct));
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
        using var second = HostProcess.Of(runner.ProcessId!.Value);

        engine.Dispose();

        Assert.True(await Ended(second), "the host is gone once the engine stopped");
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

    // A table too large for one message (20,000 rows of 10 cells of 64 characters, about 19 MB; or 1,000 rows of 64 cells of 1 KiB, 64 MB) is cut
    // by the host before it is sent, so the engine never closes the host for it and the package keeps running: its
    // table arrives (within the engine's 1,000 rows, a part when it alone would pass a message) and the cut is reported.
    [Theory]
    [InlineData(20_000, 10, 64)]
    [InlineData(1_000, 64, 1_024)]
    public async Task A_host_whose_table_is_too_large_for_one_message_keeps_running_and_sends_its_table_cut(int rows, int columns, int chars)
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"),
            $"table-rows={rows}", $"table-columns={columns}", $"table-cell-chars={chars}"));
        using var client = Client(engine);
        var runner = await Runner(engine);
        var inputs = engine.Services.GetRequiredService<SceneRuntimeOptions>().Inputs;
        const string table = "plugin:/test-fixture/readings/processes";

        // A host the engine closes for a message over its limit restarts, sends the same again, and the package fails.
        await Until(() => runner.State == PluginHostState.Failed
                || (runner.State == PluginHostState.Running && inputs.Read(table, "table").Table.Rows.Count > 0),
            "the package runs with its table, or fails");

        Assert.True(runner.State == PluginHostState.Running, $"the package is {runner.State}: {runner.Reason}");
        var read = inputs.Read(table, "table");
        Assert.Equal(columns, read.Table.Columns.Count);
        Assert.InRange(read.Table.Rows.Count, 1, PluginLimits.Default.TableRows);
        Assert.Equal("r0c0".PadRight(chars, 'x'), read.Table.Rows[0][0]);
        await Until(() => runner.Errors.Any(e => e.Contains("processes") && e.Contains("cut")), "the cut is reported");
    }

    // A text of 20 million characters passes the 16 MiB the engine reads in one message, in the catalogue and in the
    // first update: the host cuts it to the engine's 4,096 characters before it is sent, so the engine never closes the
    // host for it, the package keeps running, the text arrives cut and the cut is reported.
    [Fact]
    public async Task A_host_whose_text_is_too_large_for_one_message_keeps_running_and_sends_it_cut()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "text-chars=20000000"));
        using var client = Client(engine);
        var runner = await Runner(engine);
        var inputs = engine.Services.GetRequiredService<SceneRuntimeOptions>().Inputs;
        const string status = "plugin:/test-fixture/readings/status";

        // A host the engine closes for a message over its limit restarts, sends the same again, and the package fails.
        await Until(() => runner.State == PluginHostState.Failed
                || (runner.State == PluginHostState.Running && inputs.Read(status, "text").Text is { Length: > 0 }),
            "the package runs with its text, or fails");

        Assert.True(runner.State == PluginHostState.Running, $"the package is {runner.State}: {runner.Reason}");
        Assert.Equal(PluginLimits.Default.TextChars, inputs.Read(status, "text").Text.Length);
        await Until(() => runner.Errors.Any(e => e.Contains("status") && e.Contains("cut")), "the cut is reported");
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

    // ---- the packages at run time, with real hosts ----

    // Two packages of the test plugin, one plugin id between them: InfoPanel.TestFixturB is first in folder order.
    private const string Earlier = "InfoPanel.TestFixturB";
    private const string Later = PluginFixtures.FixtureFolder;

    [Theory]
    [InlineData(Earlier)]
    [InlineData(Later)]
    public async Task Of_two_packages_with_one_plugin_id_the_earlier_in_folder_order_keeps_it_whichever_starts_first(string startsFirst)
    {
        using var factory = new EngineFactory();
        var bundled = Path.Combine(_root, "bundled");
        PluginFixtures.CopyFixtureAs(bundled, Earlier, "id=dup");
        var package = PluginFixtures.CopyFixture(bundled, "id=dup");
        var then = startsFirst == Earlier ? Later : Earlier;
        // The other package is turned off at start, then on once the first one's host runs the plugin.
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Path.Combine(factory.DataDir, PluginStateFile.FileName), $$"""{"format":"infopanel.plugin-state/1","off":["{{then}}"]}""");
        using var engine = WithPlugins(factory, package);
        using var client = Client(engine);
        var manager = engine.Services.GetRequiredService<PluginHostManager>();
        await Until(() => manager.Runners.Count == 2, "both packages are found");
        var (a, b) = (manager.Find(Earlier)!, manager.Find(Later)!);
        var first = manager.Find(startsFirst)!;
        await Until(() => first.State == PluginHostState.Running && first.Plugins.Any(p => p.Id == "dup"), "the first package's host runs the plugin");
        using var firstHost = HostProcess.Of(first.ProcessId!.Value);

        Assert.Equal(PluginSwitch.Done, await manager.SetOnAsync(then, true));

        // The earlier package keeps the id; the later one fails with duplicateId, its host stopped, its choice untouched.
        await Until(() => a.State == PluginHostState.Running && a.Plugins.Any(p => p.Id == "dup") && b.State == PluginHostState.Failed,
            "the earlier package runs the plugin and the later one fails");
        Assert.Equal("duplicateId", b.ReasonCode);
        // The id it names is the first shared: the test plugin's, or the fixture's platform plugin's (both in both packages).
        Assert.Matches($@"\Aits plugin (dup|\S+-only-fixture) is provided by the plugin package {Earlier.Replace(".", @"\.")}, earlier in the list\z", b.Reason);
        Assert.True(manager.IsOn(b));
        Assert.Null(b.ProcessId);
        if (first == b)
            await Until(() => !firstHost.Runs(), "the later package's host is gone", TimeSpan.FromSeconds(15));
        var hub = engine.Services.GetRequiredService<SensorHub>();
        await Until(() => hub.GetStatus(SensorKey.Plugin("/dup/readings/counter")) == SensorStatus.Live, "the plugin's sensors read from the earlier package's host");
        await Eventually.Stays(() => b.State == PluginHostState.Failed && b.ProcessId == null, TimeSpan.FromSeconds(3), "the later package isn't restarted");
    }

    [Fact]
    public async Task An_action_goes_to_the_host_running_then_after_a_reload_and_a_restart_and_its_error_is_kept_with_its_time_and_plugin()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        await Listed(client);
        var manager = engine.Services.GetRequiredService<PluginHostManager>();
        var runner = await Runner(engine);

        var before = DateTimeOffset.UtcNow;
        await ReportsAnError(runner, 1);
        var error = runner.ErrorLog[0];
        Assert.Equal(PluginFixtures.FixturePluginId, error.Plugin);
        Assert.Contains("ReportError", error.Message);
        Assert.InRange(error.Time, before, DateTimeOffset.UtcNow);

        // Reloaded: a new host, and the action goes to it.
        var pid = runner.ProcessId!.Value;
        Assert.Equal(PluginSwitch.Done, await manager.ReloadAsync(PluginFixtures.FixtureFolder));
        await Until(() => runner.State == PluginHostState.Running && runner.ProcessId is { } p && p != pid, "a new host runs after the reload");
        await ReportsAnError(runner, 2);

        // Killed and restarted: the same.
        pid = runner.ProcessId!.Value;
        using (var host = Process.GetProcessById(pid))
            host.Kill();
        await Until(() => runner.State == PluginHostState.Running && runner.ProcessId is { } p && p != pid, "a new host runs after the restart");
        await ReportsAnError(runner, 3);
    }

    /// <summary>Runs the test plugin's "Report an error" and waits until the package has <paramref name="count"/> of its errors.</summary>
    private static async Task ReportsAnError(PluginHostRunner runner, int count)
    {
        Assert.Equal(PluginCall.Done, await runner.RunActionAsync(PluginFixtures.FixturePluginId, "ReportError"));
        await Until(() => runner.ErrorLog.Count(e => e.Plugin == PluginFixtures.FixturePluginId && e.Message.Contains(ReportedError)) == count,
            $"the plugin's error is kept ({count})");
    }

    private const string ReportedError = "fixture: an error on request";

    [Fact]
    public async Task Changing_the_Label_setting_changes_the_Status_reading_live()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        await Listed(client);
        var runner = await Runner(engine);

        var (outcome, problem, settings) = await runner.ApplySettingAsync(PluginFixtures.FixturePluginId, "label", JsonDocument.Parse("\"changed live\"").RootElement.Clone());

        Assert.Equal(PluginCall.Done, outcome);
        Assert.Null(problem);
        Assert.Equal("changed live", settings!.Single(s => s.Key == "label").Value!.Value.GetString());
        await Until(async () =>
        {
            var values = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!;
            return values.Any(v => v.GetProperty("key").GetString() == "plugin:/test-fixture/readings/status"
                && v.TryGetProperty("text", out var text) && text.GetString() == "changed live");
        }, "the Status reading is the new label");
    }

    [Fact]
    public async Task A_settings_value_a_plugin_puts_in_its_failure_reaches_no_log_list_or_error()
    {
        const string Secret = "s3cret-sentinel-7e41";
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "throw-apply"));
        using var client = Client(engine);
        await Listed(client);
        var runner = await Runner(engine);

        // The plugin's ApplyConfig throws with the value in its message.
        using var patch = await client.PatchAsJsonAsync($"/api/plugins/{PluginFixtures.FixtureFolder}/plugins/{PluginFixtures.FixturePluginId}/settings",
            new { key = "label", value = Secret }, Ct);
        var answer = await patch.Content.ReadAsStringAsync(Ct);
        await Until(() => runner.ErrorLog.Any(e => e.Plugin == PluginFixtures.FixturePluginId && e.Message.Contains("label", StringComparison.Ordinal)), "the failure is kept");

        Assert.DoesNotContain(Secret, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.ErrorLog, e => e.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/plugins", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/logs", Ct), StringComparison.Ordinal);
        // Neither the engine's log file nor the host's.
        var hostLogs = Directory.GetFiles(Path.Combine(factory.DataDir, "logs", "plugins"), "*.log");
        Assert.NotEmpty(hostLogs);
        foreach (var file in Directory.GetFiles(Path.Combine(factory.DataDir, "logs"), "*", SearchOption.AllDirectories))
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            Assert.DoesNotContain(Secret, await reader.ReadToEndAsync(Ct), StringComparison.Ordinal);
        }
        // The failure is kept with its kind and time, never its text.
        var failure = runner.ErrorLog.First(e => e.Plugin == PluginFixtures.FixturePluginId && e.Message.Contains("label", StringComparison.Ordinal));
        Assert.Contains(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_settings_value_a_plugin_puts_in_its_failure_at_start_reaches_no_log_list_or_error()
    {
        const string Secret = "s3cret-sentinel-5c92";
        using var factory = new EngineFactory();
        // A setting saved earlier, restored when the host starts: the plugin's ApplyConfig throws with the value.
        var saved = Path.Combine(factory.DataDir, "plugin-settings", PluginFixtures.FixtureFolder);
        Directory.CreateDirectory(saved);
        await File.WriteAllTextAsync(Path.Combine(saved, $"{PluginFixtures.FixturePluginId}.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["label"] = Secret }), Ct);
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "throw-apply"));
        using var client = Client(engine);
        await Listed(client);
        var runner = await Runner(engine);

        // The host logged the failed restore, with its key and kind.
        var hostLogs = Path.Combine(factory.DataDir, "logs", "plugins");
        await Until(async () => (await Logs(hostLogs)).Contains("Failed to apply stored config key", StringComparison.Ordinal), "the failed restore is logged");
        var hostLog = await Logs(hostLogs);
        Assert.Contains("Failed to apply stored config key label", hostLog, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), hostLog, StringComparison.Ordinal);

        Assert.DoesNotContain(runner.ErrorLog, e => e.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/plugins", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/logs", Ct), StringComparison.Ordinal);
        // Neither the engine's log file nor the host's.
        Assert.DoesNotContain(Secret, await Logs(Path.Combine(factory.DataDir, "logs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restored_settings_value_a_plugin_puts_in_its_settings_failure_at_start_reaches_no_log_list_or_error()
    {
        const string Secret = "s3cret-sentinel-a3d8";
        using var factory = new EngineFactory();
        // A setting saved earlier, restored when the host starts; once applied, the plugin's settings getter throws with it.
        var saved = Path.Combine(factory.DataDir, "plugin-settings", PluginFixtures.FixtureFolder);
        Directory.CreateDirectory(saved);
        await File.WriteAllTextAsync(Path.Combine(saved, $"{PluginFixtures.FixturePluginId}.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["label"] = Secret }), Ct);
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "throw-settings"));
        using var client = Client(engine);
        var runner = await Runner(engine);

        // The start's failure is reported and logged by the host.
        await Until(() => runner.ErrorLog.Any(e => e.Plugin == PluginFixtures.FixturePluginId), "the start's failure is reported");
        var hostLogs = Path.Combine(factory.DataDir, "logs", "plugins");
        await Until(async () => (await Logs(hostLogs)).Contains("Failed to initialize plugin", StringComparison.Ordinal), "the start's failure is logged");

        Assert.DoesNotContain(runner.ErrorLog, e => e.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/plugins", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, await client.GetStringAsync("/api/logs", Ct), StringComparison.Ordinal);
        // Neither the engine's log file nor the host's.
        Assert.DoesNotContain(Secret, await Logs(Path.Combine(factory.DataDir, "logs")), StringComparison.Ordinal);
        // The failure keeps its kind, in the error and the host's log.
        var failure = runner.ErrorLog.First(e => e.Plugin == PluginFixtures.FixturePluginId);
        Assert.Contains(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), await Logs(hostLogs), StringComparison.Ordinal);
    }

    /// <summary>Every log file under <paramref name="folder"/>, read while the engine and its hosts still write them.</summary>
    private static async Task<string> Logs(string folder)
    {
        if (!Directory.Exists(folder)) return "";
        var all = new StringBuilder();
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            all.Append(await reader.ReadToEndAsync(Ct));
        }
        return all.ToString();
    }

    [Fact]
    public async Task A_settings_call_the_host_never_answers_is_answered_within_10_seconds()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled"), "settings-hang"));
        using var client = Client(engine);
        await Listed(client);
        var runner = await Runner(engine);
        // Once the plugin has updated (its counter past 0), its settings never answer.
        await Until(async () =>
        {
            var values = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!;
            return values.Any(v => v.GetProperty("key").GetString() == Counter && v.TryGetProperty("now", out var now) && now.GetDouble() > 0);
        }, "the plugin has updated");

        var since = Stopwatch.StartNew();
        var (outcome, settings) = await runner.GetSettingsAsync(PluginFixtures.FixturePluginId).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(PluginCall.NoAnswer, outcome);
        Assert.Null(settings);
        Assert.InRange(since.Elapsed, TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(15));
        Assert.Equal(PluginHostState.Running, runner.State);
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
        // Each read awaited, never blocked on: a thread held for it is one the engine under test can't use.
        await Until(async () =>
        {
            sensors = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct))!;
            return sensors.Any(s => s.GetProperty("key").GetString() == Counter && s.GetProperty("status").GetString() == "live");
        }, "the test plugin's sensors are listed, live");
        return sensors;
    }

    /// <summary>
    /// The most a wait lasts by default: a sanity limit, not the behaviour under test. The host is the built engine as a
    /// child process, and under parallel full runs this Mac's program launches and file opens can stall for tens of seconds
    /// (its endpoint protection authorises each one), so a host's start (or its restart, after a connect the engine gave
    /// up on) took over 20 s. Each wait still ends as soon as its condition holds.
    /// </summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static Task Until(Func<bool> condition, string what, TimeSpan? within = null) =>
        Until(() => Task.FromResult(condition()), what, within);

    private static async Task Until(Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? Limit);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// How long a host may outlive the engine's stop, judged by the operating system: the stop waits for its host, so this
    /// is only slack for scheduling. Not <see cref="Limit"/>, which is patience for a start.
    /// </summary>
    private static readonly TimeSpan GoneWithin = TimeSpan.FromSeconds(5);

    /// <summary>Whether the host ends within <see cref="GoneWithin"/>, as the operating system tells (<see cref="HostProcess.Runs"/>).</summary>
    private static async Task<bool> Ended(HostProcess host)
    {
        var since = Stopwatch.StartNew();
        while (host.Runs())
        {
            if (since.Elapsed > GoneWithin)
                return false;
            await Task.Delay(50, Ct);
        }
        return true;
    }

    /// <summary>
    /// A host's process, identified while it runs, and whether it still runs, asked of the operating system, not of .NET:
    /// on Unix .NET learns of a child's end late (it handles a child's end only while no program launch of this process
    /// holds its start lock, and another test's launch can stall for seconds), and until it does, the ended child keeps
    /// its pid as a zombie. A pid another process takes after the host ended is never read as the host. Windows: the one
    /// handle opened while the host ran, kept and signalled at its end (no process takes the id while a handle to it is
    /// open). macOS (<c>proc_pidinfo</c>, which finds no zombie) and Linux (<c>/proc/&lt;pid&gt;/stat</c>): the start time
    /// read while the host ran, compared with the pid's at each look (another start is another process: the host is gone).
    /// </summary>
    private sealed class HostProcess : IDisposable
    {
        private readonly int _pid;
        private readonly Process? _handle;
        private readonly ulong _start;

        private HostProcess(int pid, Process? handle, ulong start)
        {
            _pid = pid;
            _handle = handle;
            _start = start;
        }

        public static HostProcess Of(int pid)
        {
            if (OperatingSystem.IsWindows())
            {
                var process = Process.GetProcessById(pid);
                _ = process.SafeHandle; // opened now, while the host runs, and kept for every look
                return new HostProcess(pid, process, 0);
            }

            return Look(pid) is { Zombie: false } seen ? new HostProcess(pid, null, seen.Start) : throw new InvalidOperationException($"the host {pid} isn't running");
        }

        public bool Runs() =>
            _handle != null ? !_handle.WaitForExit(0) : Look(_pid) is { } seen && seen.Start == _start && !seen.Zombie;

        /// <summary>
        /// The process at <paramref name="pid"/> now, its start time and whether it is a zombie; none when there is no
        /// process there, or one this user can't read (which isn't the host: the host is this user's).
        /// </summary>
        private static (ulong Start, bool Zombie)? Look(int pid)
        {
            if (OperatingSystem.IsMacOS())
            {
                var info = new byte[ProcBsdInfoSize];
                if (proc_pidinfo(pid, ProcPidTbsdInfo, 0, info, info.Length) <= 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is Esrch or Eperm ? null : throw new InvalidOperationException($"proc_pidinfo({pid}) failed: errno {error}");
                }
                var start = BitConverter.ToUInt64(info, 120) * 1_000_000 + BitConverter.ToUInt64(info, 128); // pbi_start_tvsec, pbi_start_tvusec
                return (start, BitConverter.ToUInt32(info, 4) == Szomb); // pbi_status
            }

            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); // from the third, the state
                return (ulong.Parse(fields[22 - 3], System.Globalization.CultureInfo.InvariantCulture), fields[0] is "Z" or "X"); // the 22nd, starttime
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) // no such file, or the process went as it was read
            {
                return null;
            }
        }

        public void Dispose() => _handle?.Dispose();
    }

    private const int ProcPidTbsdInfo = 3;     // PROC_PIDTBSDINFO
    private const int ProcBsdInfoSize = 136;   // sizeof(struct proc_bsdinfo)
    private const int Eperm = 1;
    private const int Esrch = 3;
    private const uint Szomb = 5;

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int bufferSize);
}
