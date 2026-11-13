using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using InfoPanel.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugin SDK as plugins meet it in the engine: the SDK and its host on .NET 10 with a plugin built as 1.x plugins are
// (net8.0, against 1.x's SDK) loading, reading, taking a setting and running an action; a number a plugin can't read
// (NaN) unavailable while its neighbours stay live; and PluginLog's lines in the package's log.
public sealed class PluginSdkEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string V1Folder = "InfoPanel.V1Fixture";
    private const string V1Id = "v1-fixture";
    private const string Counter = "plugin:/v1-fixture/readings/counter";
    private const string Unknown = "plugin:/v1-fixture/readings/unknown";
    private const string Maybe = "plugin:/v1-fixture/readings/maybe";
    private const string Marks = "plugin:/v1-fixture/readings/marks";
    private const string Label = "plugin:/v1-fixture/readings/label";

    private readonly string _root = PluginFixtures.NewTempRoot("ip-sdk-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task A_plugin_built_for_1x_on_net8_loads_in_the_net10_host_reads_takes_a_setting_and_runs_an_action()
    {
        // The SDK the host hands every plugin is the .NET 10 build; the plugin was built for .NET 8 against 1.x's SDK,
        // whose copy it carries.
        Assert.Equal(".NETCoreApp,Version=v10.0", typeof(IPlugin).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var package = PluginFixtures.Copy(V1Folder, Path.Combine(_root, "bundled"));
        Assert.Contains("\".NETCoreApp,Version=v8.0\"", File.ReadAllText(Path.Combine(package, V1Folder + ".deps.json")));
        Assert.True(File.Exists(Path.Combine(package, "InfoPanel.Plugins.dll")));

        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, package);
        using var client = Client(engine);
        var runner = await Runner(engine);

        await Until(async () => (await Values(client)).TryGetValue(Counter, out var counter)
            && counter.GetProperty("status").GetString() == "live" && counter.GetProperty("now").GetDouble() >= 1,
            "its own task's counter reads live");
        var values = await Values(client);
        Assert.Equal("v1", values[Label].GetProperty("text").GetString());
        Assert.Equal(42, values[Maybe].GetProperty("now").GetDouble());
        Assert.Equal(0, values[Marks].GetProperty("now").GetDouble());

        var (outcome, problem, _) = await runner.ApplySettingAsync(V1Id, "label", JsonDocument.Parse("\"set live\"").RootElement.Clone());
        Assert.Equal((PluginCall.Done, null), (outcome, problem));
        Assert.Equal(PluginCall.Done, await runner.RunActionAsync(V1Id, "Mark"));
        await Until(async () =>
        {
            var now = await Values(client);
            return now[Label].TryGetProperty("text", out var text) && text.GetString() == "set live"
                && now[Marks].TryGetProperty("now", out var marks) && marks.GetDouble() == 1;
        }, "the setting and the action show in its readings");
        Assert.Empty(runner.ErrorLog);
    }

    [Fact]
    public async Task A_number_a_plugin_cant_read_is_unavailable_while_its_neighbours_stay_live()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.Copy(V1Folder, Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        var runner = await Runner(engine);
        await Until(async () => (await Values(client)).ContainsKey(Counter), "the plugin reads live");

        // NaN from the start: listed, unavailable, and no reading (never 0).
        var statuses = await Statuses(client);
        var values = await Values(client);
        Assert.Equal("unavailable", statuses[Unknown]);
        Assert.False(values.ContainsKey(Unknown));
        Assert.Equal(("live", 42d), (statuses[Maybe], values[Maybe].GetProperty("now").GetDouble()));

        // A number that becomes NaN goes unavailable; the counter beside it stays live and keeps counting; back to a
        // number, it reads again.
        Assert.Equal(PluginCall.Done, (await runner.ApplySettingAsync(V1Id, "known", JsonDocument.Parse("false").RootElement.Clone())).Outcome);
        await Until(async () => (await Statuses(client))[Maybe] == "unavailable", "Maybe reads unavailable");
        var before = (await Values(client))[Counter].GetProperty("now").GetDouble();
        await Until(async () =>
        {
            var now = await Values(client);
            return now.TryGetValue(Counter, out var counter) && counter.GetProperty("status").GetString() == "live"
                && counter.GetProperty("now").GetDouble() > before && !now.ContainsKey(Maybe);
        }, "the counter counts on, live, beside it");

        Assert.Equal(PluginCall.Done, (await runner.ApplySettingAsync(V1Id, "known", JsonDocument.Parse("true").RootElement.Clone())).Outcome);
        await Until(async () => (await Values(client)).TryGetValue(Maybe, out var maybe) && maybe.GetProperty("status").GetString() == "live"
            && maybe.GetProperty("now").GetDouble() == 42, "Maybe reads 42 again");
    }

    [Fact]
    public async Task PluginLog_lines_reach_the_packages_log_at_the_engines_level()
    {
        using var factory = new EngineFactory();
        using var engine = WithPlugins(factory, PluginFixtures.CopyFixture(Path.Combine(_root, "bundled")));
        using var client = Client(engine);
        await Runner(engine);
        var logs = Path.Combine(factory.DataDir, "logs", "plugins");

        // The engine logs at Information: the plugin's Information line is kept, its Debug line isn't.
        string text = "";
        await Until(() =>
        {
            text = string.Concat(Directory.Exists(logs) ? Directory.GetFiles(logs, "InfoPanel.TestFixture-*.log").Select(ReadShared) : []);
            return text.Contains(StartedLine, StringComparison.Ordinal);
        }, "the plugin's line is in its package's log");
        Assert.DoesNotContain(DebugLine, text, StringComparison.Ordinal);
    }

    // The test plugin's lines (TestFixturePlugin.StartedLine and DebugLine).
    private const string StartedLine = "fixture: started";
    private const string DebugLine = "fixture: a detail at debug";

    /// <summary>A log file the host may still be writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Every listed sensor's status, by key.</summary>
    private static async Task<Dictionary<string, string>> Statuses(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct))!.ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("status").GetString()!);

    /// <summary>The readings of the sensors that have one, by key.</summary>
    private static async Task<Dictionary<string, JsonElement>> Values(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors/values", Ct))!.ToDictionary(v => v.GetProperty("key").GetString()!);

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
        await Until(() => manager.Runners.Count == 1 && manager.Runners[0].State == PluginHostState.Running, "the package's host runs");
        return manager.Runners[0];
    }

    /// <summary>A sanity limit, as PluginEngineTests' (a host's start can stall for tens of seconds under full runs).</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static Task Until(Func<bool> condition, string what) => Until(() => Task.FromResult(condition()), what);

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(50, Ct);
        }
    }
}
