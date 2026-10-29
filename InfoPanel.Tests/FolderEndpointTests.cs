using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;

namespace InfoPanel.Tests;

/// <summary>
/// Opening the engine's own folders (spec/docs/api.md, "Folders"): <c>POST /api/folders/{folder}/open</c>, the session
/// token's only, for <c>data</c> and <c>logs</c>. Every test engine has the recording opener (<see cref="EngineFactory.Folders"/>),
/// so no test opens a file manager.
/// </summary>
public sealed class FolderEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? MediaType)> Open(HttpClient client, string folder, string method = "POST")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/folders/{folder}/open");
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Data_AndLogs_OpenExactlyTheEnginesDataFolder_AndItsLogsFolder()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var data = await Open(api, "data");
        Assert.Equal(HttpStatusCode.NoContent, data.Status);
        Assert.Equal([factory.DataDir], factory.Folders.Opened);

        var logs = await Open(api, "logs");
        Assert.Equal(HttpStatusCode.NoContent, logs.Status);
        Assert.Equal([factory.DataDir, Path.Combine(factory.DataDir, "logs")], factory.Folders.Opened);

        // The data folder is the one the session reports.
        var session = await api.GetFromJsonAsync<JsonElement>("/api/session", Ct);
        Assert.Equal(session.GetProperty("dataDir").GetString(), factory.Folders.Opened[0]);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("profiles")]
    [InlineData("blobs")]
    [InlineData("Data")]
    [InlineData("LOGS")]
    [InlineData("data.")]
    [InlineData("%2E%2E")]
    [InlineData("%2Ftmp")]
    [InlineData("..%2Fdata")]
    public async Task AnyOtherFolder_Is404_AndOpensNothing(string folder)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var (status, body, mediaType) = await Open(api, folder);

        Assert.True(status == HttpStatusCode.NotFound, $"{folder}: {(int)status} {body}");
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(body, "problem"));
        Assert.Empty(factory.Folders.Opened);
    }

    [Fact]
    public async Task OnlyAPost_OpensAFolder()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        // A method the path doesn't take is 404 or 405 (spec/docs/api.md, "Errors").
        foreach (var method in new[] { "GET", "PUT", "DELETE" })
        {
            var (status, body, _) = await Open(api, "data", method);
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method}: {(int)status}");
            Assert.True(Valid(body, "problem"));
        }

        Assert.Empty(factory.Folders.Opened);
    }

    [Fact]
    public async Task OnlyTheSessionToken_OpensAFolder()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" }, new[] { "read", "edit" } })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "folders-" + string.Join("-", scopes), scopes }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var client = factory.CreateRawClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            foreach (var folder in new[] { "data", "logs" })
            {
                var (status, body, mediaType) = await Open(client, folder);
                Assert.True(status == HttpStatusCode.Forbidden, $"{folder} with {string.Join("+", scopes)}: {(int)status}");
                Assert.Equal("application/problem+json", mediaType);
                Assert.True(Valid(body, "scopeProblem"));
                Assert.Equal("session", body.GetProperty("scope").GetString());
            }
        }

        using var anonymous = factory.CreateRawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Open(anonymous, "data")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Open(anonymous, "logs")).Status);
        Assert.Empty(factory.Folders.Opened);
    }

    [Fact]
    public async Task ARepeatedRequest_Is204_AndStartsOneFileManager()
    {
        using var engine = new EngineFactory();
        var started = new List<string>();
        // The engine's own opener, its process start replaced (none of them exits, and nothing opens) and its clock still.
        var opener = new FolderOpener(command =>
        {
            lock (started)
                started.Add(command.ArgumentList[0]);
            return new TaskCompletionSource().Task;
        }, new ManualTime(DateTimeOffset.UnixEpoch));
        using var factory = engine.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IFolderOpener>(opener)));
        using var api = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        // A double click on each, and requests made at once: each answered 204, each folder started once.
        var answers = await Task.WhenAll(new[] { "data", "data", "logs", "logs", "data", "logs" }.Select(f => Open(api, f)));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.NoContent, a.Status));
        lock (started)
            Assert.Equal([engine.DataDir, Path.Combine(engine.DataDir, "logs")], started.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnOpenerThatFails_Is500_AndTheErrorIsLogged()
    {
        using var engine = new EngineFactory();
        var failing = new RecordingFolderOpener { Failure = new IOException("no file manager here") };
        using var factory = engine.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IFolderOpener>(failing)));
        using var api = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        var (status, body, mediaType) = await Open(api, "logs");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(body, "problem"));
        Assert.Equal([Path.Combine(engine.DataDir, "logs")], failing.Opened);
        var logged = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(logged, e => e.Level == "error" && e.Exception?.Contains("no file manager here") == true
            && e.Message.Contains(Path.Combine(engine.DataDir, "logs")));
    }
}
