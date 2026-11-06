using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The plugins through the engine API, over fake hosts (FakePluginHosts) in a test engine: no plugin process runs, and the
// folder opener only records.
public sealed class PluginEndpointTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = PluginFixtures.NewTempRoot("ip-pep-");

    public void Dispose() => PluginFixtures.DeleteQuietly(_root);

    [Fact]
    public async Task A_plugins_number_text_and_table_are_listed_with_their_types_while_it_runs_and_once_it_is_stopped()
    {
        var launcher = new FakeLauncher(() => new FakeHost
        {
            Entries = () =>
            [
                new EntryDto { Id = "v", Name = "Value", Type = "sensor", SensorValue = new SensorValueDto { Value = 1, Unit = "u" } },
                new EntryDto { Id = "t", Name = "Text", Type = "text", TextValue = "ok" },
                new EntryDto { Id = "p", Name = "Processes", Type = "table", TableValue = new TableValueDto { Columns = ["Name"], Rows = [[new TableCellDto { Type = "text", TextValue = "alpha" }]] } },
            ],
        });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.Fake");
        using var client = Client(engine);

        var listed = await Until(client, list => list.Count(s => s.GetProperty("status").GetString() == "live") == 3, "the fake plugin's three entries are live");
        Assert.Equal(["number", "text", "table"], listed.Select(s => s.GetProperty("type").GetString()).ToArray());

        // Stopped, its entries read nothing, and each still says what it would read.
        await engine.Services.GetRequiredService<PluginHostManager>().Runners[0].StopAsync(TimeSpan.Zero);
        var stopped = await Until(client, list => list.All(s => s.GetProperty("status").GetString() == "unavailable"), "the entries are unavailable");
        Assert.Equal(["number", "text", "table"], stopped.Select(s => s.GetProperty("type").GetString()).ToArray());
    }

    // ---- the requests ----

    private static List<PluginConfigPropertyDto> Settings() =>
    [
        new() { Key = "count", DisplayName = "Count", Type = "Integer", Value = 3L, MinValue = 0, MaxValue = 100, Step = 1 },
        new() { Key = "apiKey", DisplayName = "API key", Type = "String", Value = SecretValue },
    ];

    private const string SecretValue = "a-secret-the-list-never-holds";

    /// <summary>A platform that isn't this one, for a package that doesn't run here.</summary>
    private static string Elsewhere => PluginPlatforms.Current == "windows" ? "macos" : "windows";

    [Fact]
    public async Task The_list_is_every_tokens_and_gives_each_package_its_state_plugins_and_errors_but_never_a_settings_value()
    {
        var launcher = new FakeLauncher(() => new FakeHost { Actions = ["Refresh"], Settings = Settings() });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A", "InfoPanel.W");
        File.WriteAllText(Path.Combine(_root, "bundled", "InfoPanel.W", "PluginInfo.ini"), $"[PluginInfo]\nName=Elsewhere\nPlatforms={Elsewhere}\n");
        using var session = Client(engine);
        var a = await RunningRunner(engine, "InfoPanel.A");
        using var reader = Client(engine, await Token(session, "read"));

        var list = await Get(reader, "/api/plugins", 200);

        Assert.True(list.GetProperty("enabled").GetBoolean());
        var packages = list.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(["InfoPanel.A", "InfoPanel.W"], packages.Select(p => p.GetProperty("folder").GetString()).ToArray());
        var (pa, pw) = (packages[0], packages[1]);
        Assert.Equal("running", pa.GetProperty("state").GetString());
        Assert.True(pa.GetProperty("on").GetBoolean());
        Assert.True(pa.GetProperty("bundled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pa.GetProperty("reason").ValueKind);
        var plugin = Assert.Single(pa.GetProperty("plugins").EnumerateArray());
        Assert.Equal("fake", plugin.GetProperty("id").GetString());
        Assert.True(plugin.GetProperty("configurable").GetBoolean());
        Assert.Equal("""[{"method":"Refresh","name":"Do Refresh"}]""", plugin.GetProperty("actions").GetRawText());
        Assert.Equal(1, pa.GetProperty("sensors").GetInt32());
        Assert.Equal("Elsewhere", pw.GetProperty("name").GetString());
        Assert.Equal("unsupported", pw.GetProperty("state").GetString());
        Assert.Equal("platform", pw.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal([Elsewhere], pw.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()).ToArray());

        // Neither the list nor anything else read by this token holds a setting's value; only the session token reads them.
        Assert.DoesNotContain(SecretValue, list.GetRawText());
        await Send(reader, "GET", "/api/plugins/InfoPanel.A/plugins/fake/settings", 403);
        Assert.Contains(SecretValue, (await Get(session, "/api/plugins/InfoPanel.A/plugins/fake/settings", 200)).GetRawText());
        Assert.Equal(PluginHostState.Running, a.State);
    }

    [Fact]
    public async Task On_and_off_are_written_then_the_host_stopped_or_started_and_reload_starts_it_afresh()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A", "InfoPanel.W");
        File.WriteAllText(Path.Combine(_root, "bundled", "InfoPanel.W", "PluginInfo.ini"), $"[PluginInfo]\nPlatforms={Elsewhere}\n");
        using var session = Client(engine);
        var a = await RunningRunner(engine, "InfoPanel.A");
        var state = Path.Combine(factory.DataDir, PluginStateFile.FileName);

        var off = await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 200, """{"on":false}""");
        Assert.False(off.GetProperty("on").GetBoolean());
        Assert.Equal("off", off.GetProperty("state").GetString());
        Assert.Equal(PluginHostState.Stopped, a.State);
        Assert.Contains("\"InfoPanel.A\"", File.ReadAllText(state));
        await Send(session, "POST", "/api/plugins/InfoPanel.A/reload", 409);
        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/fake/actions/Refresh", 409);
        await Send(session, "GET", "/api/plugins/InfoPanel.A/plugins/fake/settings", 409);

        var on = await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 200, """{"on":true}""");
        Assert.True(on.GetProperty("on").GetBoolean());
        Assert.Contains(on.GetProperty("state").GetString(), new[] { "starting", "running" });
        Assert.DoesNotContain("\"InfoPanel.A\"", File.ReadAllText(state));
        await Eventually.Until(() => a.State == PluginHostState.Running, "the host runs again");

        var reloaded = await Send(session, "POST", "/api/plugins/InfoPanel.A/reload", 202);
        Assert.Equal("starting", reloaded.GetProperty("state").GetString());
        await Eventually.Until(() => a.State == PluginHostState.Running && launcher.Started.Count == 3, "a third host runs");

        // Refusals: no such package, a package that doesn't run here, a body that isn't a choice.
        await Send(session, "PUT", "/api/plugins/InfoPanel.None/on", 404, """{"on":true}""");
        await Send(session, "POST", "/api/plugins/InfoPanel.None/reload", 404);
        await Send(session, "PUT", "/api/plugins/InfoPanel.W/on", 409, """{"on":false}""");
        await Send(session, "POST", "/api/plugins/InfoPanel.W/reload", 409);
        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 422, "{}");
        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 422, """{"on":"no"}""");
        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 422, "not json");
        Assert.Equal(PluginHostState.Running, a.State);
    }

    [Fact]
    public async Task A_choice_that_cant_be_written_is_500_and_changes_nothing()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        var a = await RunningRunner(engine, "InfoPanel.A");
        // A folder where the file goes: it can't be replaced.
        Directory.CreateDirectory(Path.Combine(factory.DataDir, PluginStateFile.FileName));

        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 500, """{"on":false}""");

        Assert.Equal(PluginHostState.Running, a.State);
        Assert.True((await Get(session, "/api/plugins", 200)).GetProperty("packages")[0].GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task A_state_file_the_engine_cant_hold_is_409_to_on_and_off()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Path.Combine(factory.DataDir, PluginStateFile.FileName), """{"format":"infopanel.plugin-state/9"}""");
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");

        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 409, """{"on":false}""");
    }

    [Fact]
    public async Task An_action_the_plugin_lists_is_sent_and_no_other()
    {
        var launcher = new FakeLauncher(() => new FakeHost { Actions = ["Refresh"] });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");
        var host = launcher.Started[0].Host;

        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/fake/actions/Refresh", 202);
        await Eventually.Until(() => host.ActionsRun.Count == 1, "the host runs it");
        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/fake/actions/Dispose", 404);
        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/other/actions/Refresh", 404);
        await Send(session, "POST", "/api/plugins/InfoPanel.None/plugins/fake/actions/Refresh", 404);
        await Eventually.Stays(() => host.ActionsRun.Count == 1, TimeSpan.FromMilliseconds(200), "nothing else is sent");
        Assert.Equal(("fake", "Refresh"), host.ActionsRun[0]);
    }

    [Fact]
    public async Task Settings_are_read_and_changed_through_the_host_and_a_value_they_dont_take_is_422_without_it()
    {
        var launcher = new FakeLauncher(c => c.PluginDll.Contains("InfoPanel.A.") ? new FakeHost { Settings = Settings() } : new FakeHost { PluginId = "plain" });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A", "InfoPanel.B");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");
        await RunningRunner(engine, "InfoPanel.B");

        using (var response = await session.GetAsync("/api/plugins/InfoPanel.A/plugins/fake/settings", Ct))
        {
            Assert.Equal(200, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var settings = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.Equal(["count", "apiKey"], settings.EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToArray());
            Assert.Equal(3, settings[0].GetProperty("value").GetInt32());
        }

        var changed = await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 200, """{"key":"count","value":7}""");
        Assert.Equal(7, changed[0].GetProperty("value").GetInt32());
        var refused = await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 422, """{"key":"count","value":101}""");
        var error = Assert.Single(refused.GetProperty("errors").EnumerateArray());
        Assert.Equal(("setting.value", "/value"), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
        Assert.DoesNotContain("101", refused.GetRawText());
        await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 422, """{"key":"count"}""");
        await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 422, """{"key":"count","value":1,"more":1}""");
        await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 404, """{"key":"nope","value":1}""");
        await Send(session, "GET", "/api/plugins/InfoPanel.B/plugins/plain/settings", 404);
        await Send(session, "GET", "/api/plugins/InfoPanel.A/plugins/other/settings", 404);
        await Send(session, "GET", "/api/plugins/InfoPanel.None/plugins/fake/settings", 404);
        var applied = launcher.Started.Single(p => p.Command.PluginDll.Contains("InfoPanel.A.", StringComparison.Ordinal)).Host.Applied.Single();
        Assert.Equal(("fake", "count"), (applied.Plugin, applied.Key));
    }

    [Fact]
    public async Task A_setting_whose_key_GET_lists_is_changed_by_that_key_however_long()
    {
        var key = new string('k', 300);
        var launcher = new FakeLauncher(() => new FakeHost { Settings = [new() { Key = key, DisplayName = "Long", Type = "String", Value = "a" }] });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");

        var listed = await Get(session, "/api/plugins/InfoPanel.A/plugins/fake/settings", 200);
        Assert.Equal(key, listed[0].GetProperty("key").GetString());
        var changed = await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 200, $$"""{"key":"{{key}}","value":"b"}""");
        Assert.Equal("b", changed[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_package_plugin_or_setting_named_with_URL_sensitive_characters_is_matched_exactly_or_404()
    {
        var launcher = new FakeLauncher(() => new FakeHost { Actions = ["Refresh"], Settings = Settings() });
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");
        var host = launcher.Started[0].Host;

        foreach (var folder in new[] { "..%2FInfoPanel.A", "InfoPanel.A%2F", "InfoPanel.A%2F..%2FInfoPanel.A", "%2E%2E", "Info%20Panel.A", "InfoPanel.A%20" })
        {
            await Send(session, "POST", $"/api/plugins/{folder}/open", 404);
            await Send(session, "POST", $"/api/plugins/{folder}/reload", 404);
            await Send(session, "GET", $"/api/plugins/{folder}/plugins/fake/settings", 404);
        }
        foreach (var id in new[] { "..%2Ffake", "fake%2F", "fake%2F..%2Ffake", "%2E%2E", "fa%20ke", "fake%20" })
        {
            await Send(session, "GET", $"/api/plugins/InfoPanel.A/plugins/{id}/settings", 404);
            await Send(session, "POST", $"/api/plugins/InfoPanel.A/plugins/{id}/actions/Refresh", 404);
        }
        foreach (var key in new[] { "../count", "count/", "count ", "%63ount" })
            await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 404, JsonSerializer.Serialize(new { key, value = 1 }));
        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/fake/actions/Refresh%2F..%2FDispose", 404);

        Assert.Empty(factory.Folders.Opened);
        Assert.Empty(host.ActionsRun);
        Assert.Empty(host.Applied);
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task A_file_where_the_plugins_folder_goes_is_answered_500_and_left_as_it_is()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        Directory.CreateDirectory(factory.DataDir);
        var plugins = Path.Combine(factory.DataDir, "plugins");
        File.WriteAllText(plugins, "a file, not a folder");

        await Send(session, "POST", "/api/folders/plugins/open", 500);

        Assert.Equal("a file, not a folder", File.ReadAllText(plugins));
        Assert.Empty(factory.Folders.Opened);
    }

    [Fact]
    public async Task A_packages_folder_and_the_plugins_folder_open_through_the_folder_opener()
    {
        var launcher = new FakeLauncher(() => new FakeHost());
        using var factory = new EngineFactory();
        using var engine = WithFakePlugins(factory, launcher, "InfoPanel.A");
        using var session = Client(engine);
        await RunningRunner(engine, "InfoPanel.A");
        var plugins = Path.Combine(factory.DataDir, "plugins");
        Assert.False(Directory.Exists(plugins));

        await Send(session, "POST", "/api/plugins/InfoPanel.A/open", 204);
        await Send(session, "POST", "/api/plugins/InfoPanel.None/open", 404);
        await Send(session, "POST", "/api/folders/plugins/open", 204);

        Assert.Equal([Path.Combine(_root, "bundled", "InfoPanel.A"), plugins], factory.Folders.Opened);
        Assert.True(Directory.Exists(plugins));
    }

    [Fact]
    public async Task The_plugins_folder_is_never_created_or_opened_through_a_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        var elsewhere = Path.Combine(factory.Root, "elsewhere");
        Directory.CreateDirectory(factory.DataDir);
        Directory.CreateSymbolicLink(Path.Combine(factory.DataDir, "plugins"), elsewhere);

        await Send(session, "POST", "/api/folders/plugins/open", 500);

        Assert.False(Directory.Exists(elsewhere));
        Assert.Empty(factory.Folders.Opened);
    }

    [Fact]
    public async Task Without_plugins_the_list_says_so_and_every_package_is_404()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();

        Assert.Equal("""{"enabled":false,"packages":[]}""", (await Get(session, "/api/plugins", 200)).GetRawText());
        await Send(session, "PUT", "/api/plugins/InfoPanel.A/on", 404, """{"on":true}""");
        await Send(session, "POST", "/api/plugins/InfoPanel.A/reload", 404);
        await Send(session, "POST", "/api/plugins/InfoPanel.A/open", 404);
        await Send(session, "POST", "/api/plugins/InfoPanel.A/plugins/fake/actions/Refresh", 404);
        await Send(session, "GET", "/api/plugins/InfoPanel.A/plugins/fake/settings", 404);
        await Send(session, "PATCH", "/api/plugins/InfoPanel.A/plugins/fake/settings", 404, """{"key":"count","value":1}""");
        // The plugins folder still opens: a user adds plugins there whether or not this run hosts them.
        await Send(session, "POST", "/api/folders/plugins/open", 204);
    }

    // ---- helpers ----

    private static async Task<PluginHostRunner> RunningRunner(WebApplicationFactory<Program> engine, string folder)
    {
        var manager = engine.Services.GetRequiredService<PluginHostManager>();
        await Eventually.Until(() => manager.Find(folder) is { State: PluginHostState.Running }, $"{folder}'s host runs");
        return manager.Find(folder)!;
    }

    private static async Task<string> Token(HttpClient session, string scope)
    {
        var made = await Send(session, "POST", "/api/tokens", 201, $$"""{"name":"plugins-{{scope}}","scopes":["{{scope}}"]}""");
        return made.GetProperty("token").GetString()!;
    }

    private static Task<JsonElement> Get(HttpClient client, string path, int status) => Send(client, "GET", path, status);

    private static async Task<JsonElement> Send(HttpClient client, string method, string path, int status, string? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = body == null ? null : new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode == status, $"{method} {path}: expected {status}, got {(int)response.StatusCode} {text}");
        return text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
    }

    /// <summary>A test engine with plugins on: a fake package per name in its bundled folder, each host from <paramref name="launcher"/>.</summary>
    internal WebApplicationFactory<Program> WithFakePlugins(EngineFactory factory, FakeLauncher launcher, params string[] packages)
    {
        var bundled = Path.Combine(_root, "bundled");
        foreach (var package in packages)
        {
            Directory.CreateDirectory(Path.Combine(bundled, package));
            File.WriteAllText(Path.Combine(bundled, package, package + ".dll"), "not loaded: the hosts are fakes");
        }

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoPlugins", "false");
            builder.UseSetting("Engine:BundledPlugins", bundled);
            builder.ConfigureTestServices(s => s.AddSingleton<IPluginProcessLauncher>(launcher));
        });
    }

    internal static HttpClient Client(WebApplicationFactory<Program> engine, string token = EngineFactory.Token)
    {
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The plugins source's sensors once <paramref name="condition"/> holds of them.</summary>
    private static async Task<List<JsonElement>> Until(HttpClient client, Func<List<JsonElement>, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Eventually.Limit;
        while (true)
        {
            var list = (await client.GetFromJsonAsync<JsonElement[]>("/api/sensors", Ct))!
                .Where(s => s.GetProperty("source").GetString() == PluginSensorSource.SourceId).ToList();
            if (list.Count > 0 && condition(list))
                return list;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting until {what}");
            await Task.Delay(20, Ct);
        }
    }
}
