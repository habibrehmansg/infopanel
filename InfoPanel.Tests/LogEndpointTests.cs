using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;

namespace InfoPanel.Tests;

/// <summary>
/// The engine's recent log entries over the API (spec/docs/api.md, "Logs"): <c>GET /api/logs?after={seq}</c>, the
/// session token's only, answers the run's kept entries after <c>after</c>, oldest first, with the newest <c>seq</c>.
/// </summary>
public sealed class LogEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? MediaType)> Send(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/api/logs" + query, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default, response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonElement> Page(HttpClient client, string query = "")
    {
        var (status, body, _) = await Send(client, query);
        Assert.True(status == HttpStatusCode.OK, $"GET /api/logs{query}: {(int)status} {body}");
        Assert.True(Valid(body, "logPage"));
        return body;
    }

    private static List<(long Seq, string Message, string Level, string Source)> Entries(JsonElement page) =>
        [.. page.GetProperty("entries").EnumerateArray().Select(e => (
            e.GetProperty("seq").GetInt64(), e.GetProperty("message").GetString()!, e.GetProperty("level").GetString()!, e.GetProperty("source").GetString()!))];

    private static ILogger Logger(EngineFactory factory) =>
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InfoPanel.Tests.LogProbe");

    [Fact]
    public async Task TheEntries_AreWhatTheEngineLogged_OldestFirst_WithTheNewestSeq()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var marker = Guid.NewGuid().ToString("N");
        var logger = Logger(factory);
        logger.LogInformation("first {Marker}", marker);
        logger.LogWarning("second {Marker}", marker);
        logger.LogError(new InvalidOperationException("broken " + marker), "third {Marker}", marker);

        var page = await Page(api);
        var entries = Entries(page);

        // Oldest first, one seq after another while nothing was dropped, and latest the newest one's.
        Assert.NotEmpty(entries);
        Assert.Equal(1, entries[0].Seq);
        Assert.Equal(Enumerable.Range(1, entries.Count).Select(i => (long)i), entries.Select(e => e.Seq));
        Assert.Equal(entries[^1].Seq, page.GetProperty("latest").GetInt64());
        var ours = entries.Where(e => e.Message.Contains(marker)).ToList();
        Assert.Equal([$"first {marker}", $"second {marker}", $"third {marker}"], ours.Select(e => e.Message));
        Assert.Equal(["information", "warning", "error"], ours.Select(e => e.Level));
        Assert.All(ours, e => Assert.Equal("InfoPanel.Tests.LogProbe", e.Source));

        // The entries are the buffer's, member for member.
        var recent = factory.Services.GetRequiredService<RecentLog>().After(0).Single(e => e.Message == $"third {marker}");
        var third = page.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("message").GetString() == $"third {marker}");
        Assert.Equal(recent.Seq, third.GetProperty("seq").GetInt64());
        Assert.Equal(recent.Exception, third.GetProperty("exception").GetString());
        Assert.Contains("broken " + marker, recent.Exception);
        Assert.Equal(recent.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture), third.GetProperty("time").GetString());
        // An entry without an error has no exception member.
        var first = page.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("message").GetString() == $"first {marker}");
        Assert.False(first.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task After_AnswersOnlyTheNewerEntries()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var marker = Guid.NewGuid().ToString("N");
        var logger = Logger(factory);

        logger.LogInformation("before {Marker}", marker);
        var latest = (await Page(api)).GetProperty("latest").GetInt64();
        logger.LogInformation("after-1 {Marker}", marker);
        logger.LogInformation("after-2 {Marker}", marker);

        var page = await Page(api, $"?after={latest}");
        var entries = Entries(page);
        Assert.All(entries, e => Assert.True(e.Seq > latest, $"{e.Seq} isn't after {latest}"));
        Assert.Equal(latest + 1, entries[0].Seq);
        Assert.Equal([$"after-1 {marker}", $"after-2 {marker}"], entries.Where(e => e.Message.Contains(marker)).Select(e => e.Message));

        // After one of them: only those after it.
        var first = entries.Single(e => e.Message == $"after-1 {marker}").Seq;
        var rest = Entries(await Page(api, $"?after={first}"));
        Assert.Equal(first + 1, rest[0].Seq);
        Assert.DoesNotContain(rest, e => e.Message == $"after-1 {marker}");
        Assert.Contains(rest, e => e.Message == $"after-2 {marker}");

        // 0 is every kept entry, as no after is.
        var all = Entries(await Page(api, "?after=0"));
        Assert.Equal(1, all[0].Seq);
        Assert.Contains(all, e => e.Message == $"before {marker}");
    }

    [Fact]
    public async Task After_BeyondTheLatest_AnswersNoEntries()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var latest = (await Page(api)).GetProperty("latest").GetInt64();

        foreach (var after in new[] { latest + 1_000_000, 9_007_199_254_740_991 })
        {
            var page = await Page(api, $"?after={after}");
            Assert.Equal(0, page.GetProperty("entries").GetArrayLength());
            Assert.True(page.GetProperty("latest").GetInt64() >= latest);
        }
    }

    [Theory]
    [InlineData("?after=-1")]
    [InlineData("?after=x")]
    [InlineData("?after=9007199254740992")]
    [InlineData("?after=99999999999999999999")]
    [InlineData("?after=1.5")]
    [InlineData("?after=1e3")]
    [InlineData("?after=%2B1")]
    [InlineData("?after=%201")]
    [InlineData("?after=0x10")]
    [InlineData("?after=")]
    [InlineData("?after=1&after=2")]
    public async Task AnAfterThatIsntAWholeNumberInRange_Is400(string query)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();

        var (status, body, mediaType) = await Send(api, query);

        Assert.True(status == HttpStatusCode.BadRequest, $"{query}: {(int)status} {body}");
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(body, "problem"));
    }

    [Fact]
    public async Task OnlyTheSessionToken_ReadsTheEntries()
    {
        using var factory = new EngineFactory();
        using var session = factory.CreateApiClient();
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" }, new[] { "read", "edit" } })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "logs-" + string.Join("-", scopes), scopes }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var client = factory.CreateRawClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var (status, body, mediaType) = await Send(client);
            Assert.True(status == HttpStatusCode.Forbidden, $"{string.Join("+", scopes)}: {(int)status}");
            Assert.Equal("application/problem+json", mediaType);
            Assert.True(Valid(body, "scopeProblem"));
            Assert.Equal("session", body.GetProperty("scope").GetString());
        }

        using var anonymous = factory.CreateRawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous)).Status);
    }

    [Fact]
    public async Task TheSessionToken_IsNeverInTheEntries()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!recent.After(0).Any(e => e.Message.Contains("/auth/<token>")) && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);

        using var response = await api.GetAsync("/api/logs", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var messages = JsonDocument.Parse(text).RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("message").GetString()!).ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(messages, m => m.Contains("/auth/<token>"));
        Assert.DoesNotContain(EngineFactory.Token, text);
        Assert.DoesNotContain(messages, m => m.Contains(EngineFactory.Token));
    }

    /// <summary>
    /// A browser that can't be opened at start: the error names what was started, the sign-in link with the token (on
    /// Windows .NET puts the file name, the link, in it). It reaches the console only: the log file, the kept entries
    /// and their answer get a failure line without it.
    /// </summary>
    [Fact]
    public async Task ABrowserThatCantBeOpened_KeepsTheTokenOutOfTheLogFile_TheEntries_AndTheirAnswer()
    {
        using var engine = new EngineFactory();
        var attempts = new List<string>();
        using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:NoBrowser", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(sp => new BrowserLauncher(
                sp.GetRequiredService<ILogger<BrowserLauncher>>(),
                command =>
                {
                    // What was started, as .NET's launch error names it, in the error and the one it wraps; nothing opens.
                    var started = string.Join(" ", [command.FileName, .. command.ArgumentList]);
                    lock (attempts)
                        attempts.Add(started);
                    throw new Win32Exception($"An error occurred trying to start process '{started}'. The system cannot find the file specified.",
                        new InvalidOperationException($"No handler for {started}"));
                })));
        });
        using var api = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var logs = Path.Combine(engine.DataDir, "logs");
        string File() => string.Join("\n", Directory.Exists(logs) ? Directory.GetFiles(logs).Select(ReadShared) : []);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(recent.After(0).Any(e => e.Message.Contains("Could not open the browser")) && File().Contains("Could not open the browser"))
            && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);

        // The engine tried to open the sign-in link, the token in it, and failed.
        lock (attempts)
            Assert.Contains(attempts, a => a.Contains($"/auth/{EngineFactory.Token}"));
        // The log file says it couldn't, and holds no token.
        var file = File();
        Assert.Contains("Could not open the browser", file);
        Assert.DoesNotContain(EngineFactory.Token, file);
        // Nor does any kept entry, message, exception or source.
        var entries = recent.After(0);
        Assert.Contains(entries, e => e.Level == "warning" && e.Message.Contains("Could not open the browser"));
        Assert.All(entries, e => Assert.DoesNotContain(EngineFactory.Token, $"{e.Message}\n{e.Exception}\n{e.Source}"));
        // Nor their answer.
        using var response = await api.GetAsync("/api/logs", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Could not open the browser", text);
        Assert.DoesNotContain(EngineFactory.Token, text);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
