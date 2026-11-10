using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// <c>GET /api/sensors/sources</c>: every source the engine runs, in the hub's order, listed with no sensors too, its
/// state and reason, valid against the schema; read with any token; the plugins' source off under <c>--no-plugins</c>,
/// else running; a replaying engine runs Windows's sources from the recording, HWiNFO's state following it and HWiNFO
/// Gadget not set up.
/// </summary>
public sealed class SensorSourceEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient Client(WebApplicationFactory<Program> engine, string? token = EngineFactory.Token)
    {
        var client = engine.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<JsonElement>> Sources(HttpClient client)
    {
        using var response = await client.GetAsync("/api/sensors/sources", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        var list = body.EnumerateArray().ToList();
        Assert.All(list, s => Assert.True(ReadEndpointTests.Valid(s, "sensorSource")));
        return list;
    }

    private static string? Reason(JsonElement source) => source.TryGetProperty("reason", out var reason) ? reason.GetProperty("code").GetString() : null;

    [Fact]
    public async Task AMockEngine_ListsTheMockSource_Running_AndThePluginsSource_OffUnderNoPlugins_InTheHubsOrder()
    {
        using var client = factory.CreateApiClient();
        var sources = await Sources(client);

        Assert.Equal(["infopanel.mock", "infopanel.engine", "plugins"], sources.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("running", sources[0].GetProperty("state").GetString());
        Assert.False(sources[0].TryGetProperty("reason", out _));
        Assert.Equal(37, sources[0].GetProperty("sensors").GetInt32());
        Assert.Equal(["id", "state", "sensors"], sources[0].EnumerateObject().Select(p => p.Name));
        // The engine's own usage, in mock mode too: its process's CPU and memory.
        Assert.Equal(("running", 2), (sources[1].GetProperty("state").GetString(), sources[1].GetProperty("sensors").GetInt32()));
        // The plugins' source runs no host here (--no-plugins): off, saying so, listed with none.
        Assert.Equal(("off", "disabled", 0), (sources[2].GetProperty("state").GetString(), Reason(sources[2]), sources[2].GetProperty("sensors").GetInt32()));
        Assert.Equal("Plugins are turned off for this engine", sources[2].GetProperty("reason").GetProperty("message").GetString());

        var hub = factory.Services.GetRequiredService<SensorHub>();
        Assert.Equal(hub.Sources.Select(s => s.Id), sources.Select(s => s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AnEngineThatRunsPlugins_ListsThePluginsSource_Running_WithNoneFound()
    {
        var bundled = Path.Combine(Path.GetTempPath(), "infopanel-sources-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundled);
        try
        {
            using var engine = new EngineFactory();
            using var withPlugins = engine.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Engine:NoPlugins", "false");
                builder.UseSetting("Engine:BundledPlugins", bundled);
            });
            using var client = Client(withPlugins);
            var plugins = (await Sources(client)).Single(s => s.GetProperty("id").GetString() == "plugins");

            Assert.Equal(("running", null, 0), (plugins.GetProperty("state").GetString(), Reason(plugins), plugins.GetProperty("sensors").GetInt32()));
        }
        finally
        {
            Directory.Delete(bundled, recursive: true);
        }
    }

    [Fact]
    public async Task EveryToken_ReadsIt_AndNoTokenIs401()
    {
        using var engine = new EngineFactory();
        using var session = Client(engine);
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" } })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "sources-" + scopes[0], scopes }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var client = Client(engine, token);
            Assert.Equal("infopanel.mock", (await Sources(client))[0].GetProperty("id").GetString());
        }

        using var anonymous = Client(engine, token: null);
        using var response = await anonymous.GetAsync("/api/sensors/sources", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AReplayingEngine_RunsHWiNFOFromTheRecording_AndHWiNFOGadget_NotSetUp()
    {
        using var engine = new EngineFactory();
        using var replaying = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:MockSeed", "");
            builder.UseSetting("Engine:ReplaySensors", HwInfoFixture.Folder);
        });
        using var client = Client(replaying);
        var sources = await Sources(client);

        Assert.Equal(["hwinfo", "infopanel.hwinfo-gadget", "infopanel.engine", "plugins"], sources.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("running", sources[0].GetProperty("state").GetString());
        Assert.True(sources[0].GetProperty("sensors").GetInt32() > 100);
        Assert.Equal(("off", "notSetUp", 0), (sources[1].GetProperty("state").GetString(), Reason(sources[1]), sources[1].GetProperty("sensors").GetInt32()));
        Assert.Equal("HWiNFO isn't reporting any reading to gadgets", sources[1].GetProperty("reason").GetProperty("message").GetString());
        // The gadget source is listed with no sensors: none of the recording's is its.
        var sensors = await client.GetFromJsonAsync<JsonElement>("/api/sensors", Ct);
        Assert.DoesNotContain(sensors.EnumerateArray(), s => s.GetProperty("source").GetString() == "infopanel.hwinfo-gadget");
    }

    [Fact]
    public async Task ARecordingWhoseHWiNFOStopsMidway_IsNotRunning_ThenRunningAgainAsItLoops()
    {
        // The recording's first frame, then rounds with no frame: HWiNFO's shared memory gone, as when HWiNFO quits.
        var folder = Path.Combine(Path.GetTempPath(), "infopanel-replay-stops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "hwinfo"));
        File.Copy(Path.Combine(HwInfoFixture.Folder, "hwinfo", "000000.local.bin.gz"), Path.Combine(folder, "hwinfo", "000000.local.bin.gz"));
        File.WriteAllText(Path.Combine(folder, "recording.json"), """{"format": "infopanel.sensor-recording/1", "intervalMs": 500, "scrubbed": true, "sources": ["hwinfo"], "rounds": 8}""");
        try
        {
            using var engine = new EngineFactory();
            using var replaying = engine.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Engine:MockSeed", "");
                builder.UseSetting("Engine:ReplaySensors", folder);
            });
            using var client = Client(replaying);

            // A loop is 4 s: both states come within it, each with HWiNFO's reason when it isn't running.
            var seen = new HashSet<(string?, string?)>();
            for (var i = 0; i < 100 && seen.Count < 2; i++)
            {
                var hwinfo = (await Sources(client))[0];
                Assert.Equal("hwinfo", hwinfo.GetProperty("id").GetString());
                seen.Add((hwinfo.GetProperty("state").GetString(), Reason(hwinfo)));
                await Task.Delay(100, Ct);
            }

            Assert.Equal([("running", null), ("unavailable", "notRunning")], seen.OrderBy(s => s.Item1));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AReplayingEngine_RunsLibreFromTheRecording_AfterHWiNFOs_AndItsSettingTurnsItOffAndOn()
    {
        using var engine = new EngineFactory();
        using var replaying = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:MockSeed", "");
            builder.UseSetting("Engine:ReplaySensors", LibreFixture.Folder);
        });
        using var client = Client(replaying);
        var sources = await Sources(client);

        // The PC's sources in their start order: HWiNFO (not recorded: not running), its gadget values, then Libre.
        Assert.Equal(["hwinfo", "infopanel.hwinfo-gadget", "libre", "infopanel.engine", "plugins"], sources.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(("unavailable", "notRunning"), (sources[0].GetProperty("state").GetString(), Reason(sources[0])));
        // Recorded without the PawnIO driver: reading, limited.
        Assert.Equal(("limited", "noDriver", 199), (sources[2].GetProperty("state").GetString(), Reason(sources[2]), sources[2].GetProperty("sensors").GetInt32()));
        Assert.Equal(InfoPanel.Sensors.Libre.LibreSource.NoDriver.Message, sources[2].GetProperty("reason").GetProperty("message").GetString());
        var sensors = await client.GetFromJsonAsync<JsonElement>("/api/sensors", Ct);
        Assert.Contains(sensors.EnumerateArray(), s => s.GetProperty("key").GetString() == "libre:/amdcpu/0/load/0" && s.GetProperty("source").GetString() == "libre");

        // sources.libre.enabled applies at the source's next round (every 2 s): off, then reading again.
        await SetLibre(client, false);
        Assert.Equal(("off", "disabled"), await LibreState(client, s => s.Item1 == "off"));
        await SetLibre(client, true);
        Assert.Equal(("limited", "noDriver"), await LibreState(client, s => s.Item1 == "limited"));
    }

    private static async Task SetLibre(HttpClient client, bool enabled)
    {
        using var content = new StringContent("{\"sources\":{\"libre\":{\"enabled\":" + (enabled ? "true" : "false") + "}}}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync("/api/settings", content, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(string?, string?)> LibreState(HttpClient client, Func<(string?, string?), bool> until)
    {
        (string?, string?) state = default;
        for (var i = 0; i < 100; i++)
        {
            var libre = (await Sources(client)).Single(s => s.GetProperty("id").GetString() == "libre");
            state = (libre.GetProperty("state").GetString(), Reason(libre));
            if (until(state))
                break;
            await Task.Delay(100, Ct);
        }

        return state;
    }
}
