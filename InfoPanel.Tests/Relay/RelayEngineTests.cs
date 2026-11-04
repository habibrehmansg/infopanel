using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// The setting and its state on a test engine (docs/2.0/relay.md): <c>service.aiApps</c>, off by default and changed only
/// by the session token, opens the engine's own connection to a fake relay; <c>GET /api/service</c>'s <c>aiApps</c> says
/// how it stands; turning it off says <c>bye</c>.
/// </summary>
public sealed class RelayEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Get(HttpClient api, string path)
    {
        using var response = await api.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> Patch(HttpClient api, string body)
    {
        using var response = await api.PatchAsync("/api/settings", Body(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task Reaches(RelayClient client, RelayState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += now =>
        {
            if (now == state)
                reached.TrySetResult();
        };
        if (client.State == state)
            reached.TrySetResult();
        return reached.Task.WaitAsync(RelayRig.Limit, Ct);
    }

    [Fact]
    public async Task TheSetting_OpensTheEnginesConnection_GetServiceSaysHowItStands_AndTurningItOffSaysBye()
    {
        await using var relay = await FakeRelay.StartAsync();
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var client = factory.Services.GetRequiredService<RelayClient>();

        // Off by default.
        Assert.False((await Get(api, "/api/settings")).GetProperty("service").GetProperty("aiApps").GetBoolean());
        Assert.Equal("off", (await Get(api, "/api/service")).GetProperty("aiApps").GetString());

        // An address and a credential, the setting still off: no connection.
        await Patch(api, $$$"""{"service":{"address":"{{{relay.Address}}}"}}""");
        using (var put = await api.PutAsync("/api/service/credential", Body("""{"credential":"engine-credential"}"""), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("off", (await Get(api, "/api/service")).GetProperty("aiApps").GetString());

        // An integration token can't turn it on, even with edit.
        using (var created = await api.PostAsJsonAsync("/api/tokens", new { name = "ai-apps", scopes = new[] { "read", "edit" } }, Ct))
        {
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var integration = factory.CreateRawClient();
            integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var refused = await integration.PatchAsync("/api/settings", Body("""{"service":{"aiApps":true}}"""), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(0, relay.Requests);

        // On: the engine connects with its credential, and is connected once welcomed.
        var settings = await Patch(api, """{"service":{"aiApps":true}}""");
        Assert.True(settings.GetProperty("service").GetProperty("aiApps").GetBoolean());
        var peer = await relay.NextPeerAsync(Ct);
        await peer.ExpectAsync("hello", Ct);
        Assert.Equal("Bearer engine-credential", relay.Authorizations.Single());
        Assert.Equal("connecting", (await Get(api, "/api/service")).GetProperty("aiApps").GetString());
        await peer.SendAsync(new JsonObject { ["type"] = "welcome" });
        await Reaches(client, RelayState.Connected);
        Assert.Equal("connected", (await Get(api, "/api/service")).GetProperty("aiApps").GetString());

        // A call from elsewhere, on the engine's real clocks.
        await peer.AnswerTimeAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);
        await peer.SendAsync(new JsonObject
        {
            ["type"] = "call",
            ["id"] = "list",
            ["tool"] = "list_profiles",
            ["arguments"] = new JsonObject(),
            ["sentAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var result = await peer.ExpectAsync("result", Ct);
        Assert.False(result["result"]!["isError"]!.GetValue<bool>(), result.ToJsonString());

        // Off: bye, a normal close, and the state is off.
        await Patch(api, """{"service":{"aiApps":false}}""");
        await peer.ExpectAsync("bye", Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        await Reaches(client, RelayState.Off);
        Assert.Equal("off", (await Get(api, "/api/service")).GetProperty("aiApps").GetString());
        Assert.Equal(1, relay.Requests);
    }

    [Fact]
    public async Task ANewAddress_ClosesTheConnection_AndRemovesTheCredential()
    {
        await using var relay = await FakeRelay.StartAsync();
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var client = factory.Services.GetRequiredService<RelayClient>();
        await Patch(api, $$$"""{"service":{"address":"{{{relay.Address}}}","aiApps":true}}""");
        using (var put = await api.PutAsync("/api/service/credential", Body("""{"credential":"engine-credential"}"""), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var peer = await relay.NextPeerAsync(Ct);
        await peer.ExpectAsync("hello", Ct);
        await peer.SendAsync(new JsonObject { ["type"] = "welcome" });
        await Reaches(client, RelayState.Connected);

        await Patch(api, """{"service":{"address":"http://127.0.0.1:9"}}""");
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        await Reaches(client, RelayState.Off);
        var state = await Get(api, "/api/service");
        Assert.Equal("absent", state.GetProperty("credential").GetString());
        Assert.Equal("off", state.GetProperty("aiApps").GetString());
    }
}
