using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Sensors;
using InfoPanel.Settings;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;

namespace InfoPanel.Tests;

/// <summary>
/// The engine settings over the API (spec/docs/api.md, "Settings"): <c>GET</c> and <c>PATCH /api/settings</c>, the
/// session token's only; the <c>settings</c> socket message, to sockets of the session token only; and the session's
/// <c>mock</c>. Each test has an engine of its own, since a change is kept in its data folder.
/// </summary>
public sealed class SettingsEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? MediaType)> Send(HttpClient client, HttpMethod method, string? json = null)
    {
        using var request = new HttpRequestMessage(method, "/api/settings") { Content = json == null ? null : Body(json) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default, response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonElement> Get(HttpClient client)
    {
        var (status, body, _) = await Send(client, HttpMethod.Get);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Valid(body, "engineSettings"));
        return body;
    }

    private static async Task<JsonElement> Patch(HttpClient client, string json)
    {
        var (status, body, _) = await Send(client, HttpMethod.Patch, json);
        Assert.True(status == HttpStatusCode.OK, $"PATCH {json}: {(int)status} {body}");
        Assert.True(Valid(body, "engineSettings"));
        return body;
    }

    private static async Task<JsonElement> Refused(HttpClient client, string json, HttpStatusCode expected, string definition)
    {
        var (status, body, mediaType) = await Send(client, HttpMethod.Patch, json);
        Assert.True(status == expected, $"PATCH {json}: expected {(int)expected}, got {(int)status} {body}");
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(body, definition));
        return body;
    }

    private static string Settings(EngineFactory factory) => Path.Combine(factory.DataDir, "settings.json");

    // ---- reading and changing -------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_AnswersTheDefaults_StatusOk_AndVersion0()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var settings = await Get(api);

        Assert.Equal("ok", settings.GetProperty("status").GetString());
        Assert.False(settings.TryGetProperty("problem", out _));
        Assert.Equal(0, settings.GetProperty("version").GetInt64());
        Assert.Equal(15, settings.GetProperty("frameRate").GetInt32());
        Assert.Equal(1000, settings.GetProperty("graphRate").GetInt32());
        Assert.Equal("information", settings.GetProperty("logLevel").GetString());
        var libre = settings.GetProperty("sources").GetProperty("libre");
        Assert.Equal((true, true, 30), (libre.GetProperty("enabled").GetBoolean(), libre.GetProperty("storage").GetBoolean(), libre.GetProperty("storageInterval").GetInt32()));
        Assert.Empty(settings.GetProperty("clients").EnumerateObject());
        Assert.False(File.Exists(Settings(factory)));
    }

    [Fact]
    public async Task Patch_AnswersTheNewValues_AndCountsOnlyChangesThatChangeSomething()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var changed = await Patch(api, """{"frameRate":30,"sources":{"libre":{"storageInterval":60}},"clients":{"web":{"theme":"dark"}}}""");

        Assert.Equal(1, changed.GetProperty("version").GetInt64());
        Assert.Equal(30, changed.GetProperty("frameRate").GetInt32());
        Assert.Equal(60, changed.GetProperty("sources").GetProperty("libre").GetProperty("storageInterval").GetInt32());
        Assert.Equal("dark", changed.GetProperty("clients").GetProperty("web").GetProperty("theme").GetString());

        // The same change again changes nothing: the same version, the same answer, nothing written.
        var stored = File.ReadAllBytes(Settings(factory));
        var again = await Patch(api, """{"frameRate":30,"sources":{"libre":{"storageInterval":60}},"clients":{"web":{"theme":"dark"}}}""");
        Assert.Equal(1, again.GetProperty("version").GetInt64());
        Assert.True(JsonElement.DeepEquals(changed, again));
        Assert.Equal(stored, File.ReadAllBytes(Settings(factory)));
        Assert.True(JsonElement.DeepEquals(changed, await Get(api)));
        Assert.Equal(1, factory.Services.GetRequiredService<SettingsStore>().Current.Version);
    }

    [Fact]
    public async Task Patch_MergesMemberByMember_AndNullSetsASettingBackToItsDefault()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await Patch(api, """{"frameRate":30,"graphRate":250,"logLevel":"debug","sources":{"libre":{"enabled":false,"storage":false}},"clients":{"web":1,"tray":2}}""");

        // Only what the change names: a setting left out keeps its value; null sets one back; a client's null removes it.
        var merged = await Patch(api, """{"graphRate":null,"sources":{"libre":{"storage":null}},"clients":{"tray":null}}""");

        Assert.Equal(2, merged.GetProperty("version").GetInt64());
        Assert.Equal(30, merged.GetProperty("frameRate").GetInt32());
        Assert.Equal(1000, merged.GetProperty("graphRate").GetInt32());
        Assert.Equal("debug", merged.GetProperty("logLevel").GetString());
        var libre = merged.GetProperty("sources").GetProperty("libre");
        Assert.Equal((false, true), (libre.GetProperty("enabled").GetBoolean(), libre.GetProperty("storage").GetBoolean()));
        Assert.Equal(["web"], merged.GetProperty("clients").EnumerateObject().Select(c => c.Name));
        Assert.True(JsonElement.DeepEquals(merged, await Get(api)));

        // A restart reads what was stored; the version starts again at 0.
        var store = SettingsStore.Open(factory.Services.GetRequiredService<InfoPanel.Persistence.DataPaths>());
        Assert.Equal((30, 1000, false), (store.Current.Settings.FrameRate, store.Current.Settings.GraphRate, store.Current.Settings.Libre.Enabled));
        Assert.Equal(0, store.Current.Version);
    }

    [Fact]
    public async Task AChange_IsAChangeToTheStoredSettings_AValueEqualToItsDefaultIsStored_AndNullRemovesIt()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await using var socket = await PreviewClient.ConnectAsync(factory, Ct);
        await socket.ReceiveTextAsync("hello", Ct);
        var file = Settings(factory);

        // The default, set: stored (it no longer follows the default) and counted, though the value in effect is the same.
        var set = await Patch(api, """{"frameRate":15}""");
        Assert.Equal((1L, 15), (set.GetProperty("version").GetInt64(), set.GetProperty("frameRate").GetInt32()));
        Assert.Equal(15, JsonNode.Parse(File.ReadAllText(file))!["frameRate"]!.GetValue<int>());
        Assert.Equal(1, (await socket.ReceiveTextAsync("settings", Ct)).GetProperty("version").GetInt64());

        // The same value again, null for a setting that isn't set, and {}: the stored settings stay as they were, so
        // nothing is written, the version stays and no settings message is sent.
        var stored = File.ReadAllBytes(file);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        foreach (var same in new[] { """{"frameRate":15}""", """{"frameRate":15.0}""", """{"graphRate":null}""", "{}" })
            Assert.Equal(1, (await Patch(api, same)).GetProperty("version").GetInt64());
        Assert.Equal(stored, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        // This engine sends a pending settings message ahead of a profiles message, so one sent for those changes would
        // come before the profile list's.
        using (var profile = await api.PostAsync("/api/profiles", Body($$"""{"id":"{{Guid.NewGuid()}}","name":"After","width":100,"height":50}"""), Ct))
            Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
        while (true)
        {
            var next = await socket.ReceiveAsync(Ct);
            Assert.Null(next.CloseStatus);
            var type = next.Json?.GetProperty("type").GetString();
            Assert.NotEqual("settings", type);
            if (type == "profiles")
                break;
        }

        // null removes the value set: stored and counted, though the value in effect is still the default.
        var removed = await Patch(api, """{"frameRate":null}""");
        Assert.Equal((2L, 15), (removed.GetProperty("version").GetInt64(), removed.GetProperty("frameRate").GetInt32()));
        Assert.False(JsonNode.Parse(File.ReadAllText(file))!.AsObject().ContainsKey("frameRate"));
        Assert.Equal(2, (await socket.ReceiveTextAsync("settings", Ct)).GetProperty("version").GetInt64());
        Assert.Equal(2, factory.Services.GetRequiredService<SettingsStore>().Current.Version);
    }

    [Fact]
    public async Task AChange_TakesEffectAtOnce()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var history = factory.Services.GetRequiredService<SensorHistory>();

        await Patch(api, """{"graphRate":250}""");

        Assert.Equal(TimeSpan.FromMilliseconds(250), history.Interval);
    }

    // ---- refusals -----------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> BadChanges => new()
    {
        { """{"frameRate":0}""", "/frameRate", "schema" },
        { """{"frameRate":61}""", "/frameRate", "schema" },
        { """{"frameRate":30.5}""", "/frameRate", "schema" },
        { """{"frameRate":"30"}""", "/frameRate", "schema" },
        { """{"graphRate":99}""", "/graphRate", "schema" },
        { """{"graphRate":3001}""", "/graphRate", "schema" },
        { """{"logLevel":"verbose"}""", "/logLevel", "schema" },
        { """{"sources":{"libre":{"storageInterval":0}}}""", "/sources/libre/storageInterval", "schema" },
        { """{"sources":{"libre":{"storageInterval":301}}}""", "/sources/libre/storageInterval", "schema" },
        { """{"sources":{"libre":{"enabled":"yes"}}}""", "/sources/libre/enabled", "schema" },
        { """{"sources":null}""", "/sources", "schema" },
        { """{"theme":"dark"}""", "/theme", "schema" },
        { """{"sources":{"macos":{}}}""", "/sources/macos", "schema" },
        { """{"sources":{"libre":{"interval":30}}}""", "/sources/libre/interval", "schema" },
        { """{"clients":{"my client":{}}}""", "/clients/my client", "schema" },
        { """{"clients":null}""", "/clients", "schema" },
        { "[]", "", "schema" },
        { "{\"frameRate\":30", "", "json" },
        { """{"frameRate":1e400}""", "/frameRate", "json" },
        // The body as the formats read JSON: a repeated member is never resolved by keeping one; a string is Unicode text.
        { """{"frameRate":30,"frameRate":31}""", "", "schema" },
        { """{"logLevel":"\uD800"}""", "/logLevel", "json" },
        { """{"clients":{"web":""" + new string('[', 63) + "1" + new string(']', 63) + "}}", "", "limit.settingsDepth" },
        { """{"clients":{"web":""" + JsonSerializer.Serialize(new string('x', 64 * 1024 - 1)) + "}}", "/clients/web", "limit.settingsClientBytes" },
    };

    [Theory]
    [MemberData(nameof(BadChanges))]
    public async Task ABadChange_Is422_WithItsCodeAtItsPath_AndChangesNothing(string json, string path, string code)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var before = await Patch(api, """{"frameRate":20}""");
        var stored = File.ReadAllBytes(Settings(factory));

        var refused = await Refused(api, json, HttpStatusCode.UnprocessableEntity, "invalidProblem");

        var errors = refused.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("path").GetString(), e.GetProperty("code").GetString())).ToList();
        Assert.True(errors.Contains((path, code)), $"Expected {code} at '{path}', got: {string.Join("; ", errors)}");
        Assert.True(JsonElement.DeepEquals(before, await Get(api)));
        Assert.Equal(stored, File.ReadAllBytes(Settings(factory)));
    }

    [Fact]
    public async Task ABodyOverSettingsRequestBytes_Is422_AndUnread()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var limit = SpecLimits.Default.SettingsRequestBytes;
        var within = """{"frameRate":20}""" + new string(' ', limit - 16);
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(within));

        Assert.Equal(1, (await Patch(api, within)).GetProperty("version").GetInt64());
        var refused = await Refused(api, within + " ", HttpStatusCode.UnprocessableEntity, "invalidProblem");

        var error = Assert.Single(refused.GetProperty("errors").EnumerateArray());
        Assert.Equal(("", "limit.settingsRequestBytes"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task TooManyClients_Is422AtClients()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var sixteen = string.Join(",", Enumerable.Range(0, SpecLimits.Default.SettingsClients).Select(i => $"\"c{i}\":{i}"));
        await Patch(api, "{\"clients\":{" + sixteen + "}}");

        var refused = await Refused(api, """{"clients":{"one-more":1}}""", HttpStatusCode.UnprocessableEntity, "invalidProblem");

        var error = Assert.Single(refused.GetProperty("errors").EnumerateArray());
        Assert.Equal(("/clients", "limit.settingsClients"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    public static TheoryData<string, string, string, string> UnreadableFiles => new()
    {
        { "{ not json", "unreadable", "json", "" },
        { """{"format":"infopanel.settings/2","frameRate":30}""", "unsupported", "unsupported.format", "/format" },
        { """{"frameRate":30}""", "unreadable", "schema", "/format" },
    };

    [Theory]
    [MemberData(nameof(UnreadableFiles))]
    public async Task SettingsTheEngineCouldntRead_AreAnsweredWithTheirProblem_AndEveryChangeIs409(string content, string status, string code, string path)
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Settings(factory), content);
        var hash = SHA256.HashData(File.ReadAllBytes(Settings(factory)));
        using var api = factory.CreateApiClient();

        var settings = await Get(api);

        Assert.Equal(status, settings.GetProperty("status").GetString());
        var problem = settings.GetProperty("problem");
        Assert.Equal((code, path), (problem.GetProperty("code").GetString(), problem.GetProperty("path").GetString()));
        Assert.Equal((15, 0L), (settings.GetProperty("frameRate").GetInt32(), settings.GetProperty("version").GetInt64()));

        // A valid change and an invalid one alike: the settings' status answers first.
        foreach (var change in new[] { """{"frameRate":30}""", """{"frameRate":0}""", "not json", "{}" })
        {
            var refused = await Refused(api, change, HttpStatusCode.Conflict, "settingsStatusProblem");
            Assert.Equal(status, refused.GetProperty("code").GetString());
            Assert.True(JsonElement.DeepEquals(problem, refused.GetProperty("problem")));
        }

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Settings(factory))));
        Assert.True(JsonElement.DeepEquals(settings, await Get(api)));
    }

    [Fact]
    public async Task OnlyTheSessionToken_ReadsOrChangesTheSettings()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" }, new[] { "read", "edit" } })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "settings-" + string.Join("-", scopes), scopes }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var client = factory.CreateRawClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            foreach (var (method, json) in new[] { (HttpMethod.Get, (string?)null), (HttpMethod.Patch, """{"frameRate":30}""") })
            {
                var (status, body, mediaType) = await Send(client, method, json);
                Assert.True(status == HttpStatusCode.Forbidden, $"{method} with {string.Join("+", scopes)}: {(int)status}");
                Assert.Equal("application/problem+json", mediaType);
                Assert.True(Valid(body, "scopeProblem"));
                Assert.Equal("session", body.GetProperty("scope").GetString());
            }
        }

        using var anonymous = factory.CreateRawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Get)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Patch, """{"frameRate":30}""")).Status);
        Assert.Equal(0, (await Get(session)).GetProperty("version").GetInt64());
    }

    // ---- the settings message -------------------------------------------------------------------------------------

    [Fact]
    public async Task ASessionSocket_GetsSettingsAfterAChange_AnIntegrationSocketDoesnt()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "socket", scopes = new[] { "read", "edit" } }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
        await using var sessionSocket = await PreviewClient.ConnectAsync(factory, Ct);
        await using var integrationSocket = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + token);
        await sessionSocket.ReceiveTextAsync("hello", Ct);
        await integrationSocket.ReceiveTextAsync("hello", Ct);

        await Patch(session, """{"frameRate":30}""");

        var message = await sessionSocket.ReceiveTextAsync("settings", Ct);
        Assert.Equal(1, message.GetProperty("version").GetInt64());

        // The integration socket gets the profile list's change after the settings', and nothing before it: the engine
        // sends a settings message ahead of a profiles message, so one sent to this socket would come first.
        using (var profile = await session.PostAsync("/api/profiles", Body($$"""{"id":"{{Guid.NewGuid()}}","name":"After","width":100,"height":50}"""), Ct))
            Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
        while (true)
        {
            var next = await integrationSocket.ReceiveAsync(Ct);
            Assert.Null(next.CloseStatus);
            var type = next.Json?.GetProperty("type").GetString();
            Assert.NotEqual("settings", type);
            if (type == "profiles")
                break;
        }

        Assert.DoesNotContain("settings", integrationSocket.ReceivedTypes);
    }

    [Fact]
    public async Task ABurstOfChanges_SendsAtLeastTheNewest()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        await using var socket = await PreviewClient.ConnectAsync(factory, Ct);
        await socket.ReceiveTextAsync("hello", Ct);

        for (var rate = 20; rate < 40; rate++)
            await Patch(session, $$"""{"frameRate":{{rate}}}""");

        // Each message names a later version than the one before; the newest, 20, is among them.
        long seen = 0;
        while (seen < 20)
        {
            var version = (await socket.ReceiveTextAsync("settings", Ct)).GetProperty("version").GetInt64();
            Assert.True(version > seen, $"{version} after {seen}");
            seen = version;
        }

        Assert.Equal(20, seen);
    }

    // ---- mock mode ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSession_SaysMock_OnAnEngineInMockMode()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);

        Assert.True(Valid(session, "session"));
        Assert.True(session.GetProperty("mock").GetBoolean());
    }

    [Fact]
    public async Task TheSession_SaysNotMock_OnAnEngineStartedWithoutIt()
    {
        using var engine = new EngineFactory();
        // No --mock-seed: the platform's sources, here none (the test reads no sensor of the computer).
        using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:MockSeed", "");
            builder.ConfigureTestServices(services => services.RemoveAll<ISensorSource>());
        });
        using var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);

        Assert.True(Valid(session, "session"));
        Assert.False(session.GetProperty("mock").GetBoolean());
        Assert.Empty(factory.Services.GetServices<ISensorSource>());
    }
}
