using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Hosting;
using InfoPanel.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// A credential provided in place of a <c>present</c> one for the same address (spec/docs/api.md, "The configured
/// service"): once the new one is kept, the engine asks the service, once and in the background, to forget the one it
/// replaced (<c>DELETE /svc/v1/device</c> with it as a Bearer token); the <c>PUT</c> doesn't wait for it, and nothing is
/// sent for a first credential, the same one again, a rejected one, a removal or a change of address. The service is a
/// fake on a loopback port; the test engines run offline, so a service elsewhere gets nothing.
/// </summary>
public sealed class ServiceReleaseEndpointTests : IAsyncDisposable
{
    private const string First = "first-credential.Fq1~+/==";
    private const string Second = "second-credential.Sq2~+/==";
    private const string Third = "third-credential.Tq3~+/==";

    private const string Told = "Told the configured service to forget the credential this engine replaced";
    private const string ReleaseLine = "credential this engine replaced";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly LocalHttpServer _service;
    private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the fake holds each <c>DELETE</c>'s answer until <see cref="_answer"/> is set (else answers at once).</summary>
    private volatile bool _hold;

    public ServiceReleaseEndpointTests()
    {
        _service = new LocalHttpServer(async (request, stream, ct) =>
        {
            // A request the tests use to have the first credential refused.
            if (request.Method == "GET" && request.Path == "/refuse")
            {
                await LocalHttpServer.Answer(stream, 401, [], "application/json");
                return;
            }

            if (_hold)
                await _answer.Task.WaitAsync(ct);
            await LocalHttpServer.Answer(stream, 204, [], null);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _answer.TrySetResult();
        await _service.DisposeAsync();
    }

    private string Address => $"http://127.0.0.1:{_service.Port}";

    private static StringContent Body(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string Sha16(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];

    private static async Task SetAddress(HttpClient api, string address)
    {
        using var response = await api.PatchAsync("/api/settings", Body(new { service = new { address } }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task Put(HttpClient api, string credential)
    {
        using var response = await api.PutAsync("/api/service/credential", Body(new { credential }), Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> State(HttpClient api)
    {
        using var response = await api.GetAsync("/api/service", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static RecentLogEntry[] ReleaseLines(EngineFactory factory) =>
        [.. factory.Services.GetRequiredService<RecentLog>().After(0).Where(e => e.Message.Contains(ReleaseLine))];

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10, Ct);
        }
    }

    private ServedRequest[] Deletes() => [.. _service.Requests.Where(r => r.Method == "DELETE")];

    [Fact]
    public async Task ACredentialReplaced_IsSentOnce_ToBeForgotten_WithItself_AndThePutDoesntWaitForIt()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        await Put(api, First);

        // The service holds its answer: the PUT answers all the same, at once.
        _hold = true;
        var clock = Stopwatch.StartNew();
        await Put(api, Second).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var took = clock.Elapsed;
        await Until(() => _service.Requests.Count == 1, "the request to forget the replaced credential");
        Assert.False(_answer.Task.IsCompleted);
        Assert.True(took < TimeSpan.FromSeconds(1), $"the PUT took {took}");
        Assert.Equal("present", (await State(api)).GetProperty("credential").GetString());

        _answer.SetResult();
        await Until(() => ReleaseLines(factory).Length == 1, "the line");
        var request = Assert.Single(_service.Requests);
        Assert.Equal(("DELETE", "/svc/v1/device", $"Bearer {First}"), (request.Method, request.Path, request.Header("authorization")));
        Assert.Equal(Told, Assert.Single(ReleaseLines(factory)).Message);
        var state = await State(api);
        Assert.Equal(("present", Sha16(Second)), (state.GetProperty("credential").GetString(), state.GetProperty("fingerprint").GetString()));
    }

    [Fact]
    public async Task NothingIsSent_ForAFirstCredential_TheSameAgain_ARejectedOne_ARemoval_OrAChangeOfAddress()
    {
        await using var other = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 204, [], null));
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);

        // A first credential, then the same one again.
        await Put(api, First);
        await Put(api, First);

        // Refused by the service (a request of the engine's answered 401): replacing it sends nothing.
        using (var refused = await factory.Services.GetRequiredService<ServiceClient>().SendAsync(HttpMethod.Get, "/refuse", ct: Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Response!.StatusCode);
        Assert.Equal("rejected", (await State(api)).GetProperty("credential").GetString());
        await Put(api, Second);

        // Removed: nothing sent, and the next one replaces none.
        using (var deleted = await api.DeleteAsync("/api/service/credential", Ct))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await Put(api, Third);

        // A change of address removes it: nothing to either address, and the first there replaces none.
        await SetAddress(api, $"http://127.0.0.1:{other.Port}");
        Assert.Equal("absent", (await State(api)).GetProperty("credential").GetString());
        await Put(api, First);

        // One replacement at last, to know the engine got this far: it alone is sent, and alone logged.
        await Put(api, Second);
        await Until(() => ReleaseLines(factory).Length >= 1, "the line");
        Assert.Equal(["GET /refuse"], _service.Requests.Select(r => $"{r.Method} {r.Path}"));
        var request = Assert.Single(other.Requests);
        Assert.Equal(("DELETE", "/svc/v1/device", $"Bearer {First}"), (request.Method, request.Path, request.Header("authorization")));
        Assert.Equal(Told, Assert.Single(ReleaseLines(factory)).Message);
    }

    [Fact]
    public async Task Offline_AServiceNotOnThisComputer_GetsNothing_AndTheLineSaysSo()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, "https://service.example");
        await Put(api, First);
        await Put(api, Second);

        await Until(() => ReleaseLines(factory).Length >= 1, "the line");
        var line = Assert.Single(ReleaseLines(factory));
        Assert.Equal(("warning", "Couldn't tell the configured service to forget the credential this engine replaced (not sent: offline)"),
            (line.Level, line.Message));
        Assert.Equal("present", (await State(api)).GetProperty("credential").GetString());
    }

    [Fact]
    public async Task TwoPutsRacing_EachPredecessorIsSentOnce()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        await SetAddress(api, Address);
        await Put(api, First);

        await Task.WhenAll(Put(api, Second), Put(api, Third));

        await Until(() => ReleaseLines(factory).Length >= 2, "two lines");
        var kept = (await State(api)).GetProperty("fingerprint").GetString();
        var lost = kept == Sha16(Second) ? Third : Second;
        Assert.Contains(kept, new[] { Sha16(Second), Sha16(Third) });
        var sent = Deletes().Select(r => r.Header("authorization")).Order().ToArray();
        Assert.Equal(new[] { $"Bearer {First}", $"Bearer {lost}" }.Order().ToArray(), sent);
        Assert.Equal(2, ReleaseLines(factory).Length);
    }

    [Fact]
    public async Task TheEngineStopping_WithARequestHeld_EndsIt_AndItsStopWaitsForItBriefly()
    {
        var factory = new EngineFactory();
        RecentLog log;
        TimeSpan took;
        try
        {
            using var api = factory.CreateApiClient();
            await SetAddress(api, Address);
            await Put(api, First);
            _hold = true;
            await Put(api, Second);
            await Until(() => Deletes().Length == 1, "the request to forget the replaced credential");
            log = factory.Services.GetRequiredService<RecentLog>();
        }
        finally
        {
            var clock = Stopwatch.StartNew();
            await factory.DisposeAsync();
            took = clock.Elapsed;
        }

        // The service still holds its answer: the engine's stop dropped the request, logged it, and didn't wait for the bound.
        Assert.False(_answer.Task.IsCompleted);
        Assert.True(took < ServiceRelease.Bound, $"the stop took {took}");
        var line = Assert.Single(log.After(0).Where(e => e.Message.Contains(ReleaseLine)));
        Assert.Equal(("warning", "Couldn't tell the configured service to forget the credential this engine replaced (dropped: the engine stops)"),
            (line.Level, line.Message));
    }
}
