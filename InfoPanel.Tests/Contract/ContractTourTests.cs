using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Hosting;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The tour: spec/openapi.json against the engine, from the spec's side. One engine over a seeded data folder is driven
/// through every response every operation declares and every message the socket defines; each response is checked by
/// the contract check like any other test's. The tour then fails on a declared (operation, status) it didn't see, unless
/// the allowlist below names it with a reason, and on an allowlist entry it did see (a stale entry is removed). It is
/// self-contained, so it holds whatever other tests run, in whatever order, filtered or not.
/// </summary>
public class ContractTourTests(SeededEngineFactory factory) : IClassFixture<SeededEngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiContract Contract => ApiContract.Instance;

    /// <summary>
    /// Declared responses the tour can't cause, each with its reason and where it applies. An entry that applies and that
    /// the tour sees fails as stale. Keep it short: a status a request can cause belongs in the tour.
    /// </summary>
    internal static readonly IReadOnlyList<(string Operation, int Status, string Reason, bool Applies)> Allowlist =
    [];

    private const string AbsentProfile = "00000000-0000-4000-8000-0000000000aa";
    private const string AbsentBlob = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string AbsentToken = "00000000000000000000000000000000";

    private HttpClient Client(string? token)
    {
        var client = factory.CreateRawClient();
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Send(HttpClient client, string method, string path, int status, HttpContent? body = null,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
        configure?.Invoke(request);
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode == status, $"{method} {path}: expected {status}, got {(int)response.StatusCode} {text}");
        return response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true && text.Length > 0
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
    }

    [Fact]
    public async Task EveryDeclaredResponse_AndEverySocketMessage_IsSeen()
    {
        // tokens.json within 530 bytes of its cap (tokensBytes, 16 MiB) before the engine starts: room for the tour's two
        // tokens (and their uses), not for a third.
        var pad = 16 * 1024 * 1024 - 530 - """{"tokens":[],"tour":""}""".Length;
        File.WriteAllText(Path.Combine(factory.DataDir, "tokens.json"), $$"""{"tokens":[],"tour":"{{new string('x', pad)}}"}""");
        using var session = Client(EngineFactory.Token);
        using var anonymous = Client(null);

        // Tokens, and a token of each scope.
        var read = await Send(session, "POST", "/api/tokens", 201, JsonContent.Create(new { name = "tour-read", scopes = new[] { "read" } }));
        var edit = await Send(session, "POST", "/api/tokens", 201, JsonContent.Create(new { name = "tour-edit", scopes = new[] { "edit" } }));
        using var reader = Client(read.GetProperty("token").GetString());
        using var editor = Client(edit.GetProperty("token").GetString());
        await Send(session, "POST", "/api/tokens", 422, Body("{}"));
        var full = await Send(session, "POST", "/api/tokens", 422, JsonContent.Create(new { name = "tour-full", scopes = new[] { "read" } }));
        Assert.Equal("limit.tokensBytes", full.GetProperty("errors")[0].GetProperty("code").GetString());
        await Send(session, "GET", "/api/tokens", 200);
        await Send(session, "DELETE", $"/api/tokens/{AbsentToken}", 404);

        // Every operation that needs a token: none is 401, and one without the scope 403.
        foreach (var op in Contract.Operations.Where(o => o.Scope != null && o.Path != "/ws"))
        {
            var path = op.Path.Replace("{id}", AbsentProfile).Replace("{v1Id}", AbsentProfile).Replace("{folder}", "data");
            HttpContent? Empty() => op.Method == "POST" ? Body("{}") : null;
            await Send(anonymous, op.Method, path, 401, Empty());
            if (op.Scope == "edit")
                await Send(reader, op.Method, path, 403, Empty());
            if (op.Scope == "session")
                await Send(editor, op.Method, path, 403, Empty());
        }

        // What needs no token.
        await Send(anonymous, "GET", "/healthz", 200);
        await Send(anonymous, "GET", $"/auth/{EngineFactory.Token}", 302);
        await Send(anonymous, "GET", "/auth/not-the-token", 401);

        // The engine, fonts and sensors.
        await Send(session, "GET", "/api/session", 200);
        await Send(session, "GET", "/api/fonts", 200);
        await Send(session, "GET", "/api/sensors", 200);
        await Send(session, "GET", "/api/sensors/values?history=5", 200);
        await Send(session, "GET", "/api/sensors/values?history=many", 400);
        // A query parameter is a whole number from -2^31 to 2^31 - 1 (then clamped); one past it is answered 400.
        await Send(session, "GET", "/api/sensors/values?history=2147483647", 200);
        await Send(session, "GET", "/api/sensors/values?history=2147483648", 400);
        await Send(session, "GET", "/api/sensors/values?history=-2147483648", 200);
        await Send(session, "GET", "/api/sensors/values?history=-2147483649", 400);

        // The engine settings, the session token's: read, changed, a change refused; refused while they can't be read
        // (an engine of their own, started on settings it can't read: its answers are checked and seen like this one's).
        await Send(session, "GET", "/api/settings", 200);
        await Send(session, "PATCH", "/api/settings", 200, Body("""{"frameRate":20}"""));
        await Send(session, "PATCH", "/api/settings", 422, Body("""{"frameRate":0}"""));
        var unreadable = Path.Combine(factory.Root, "settings-unreadable");
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "settings.json"), "{ not json");
        using (var refusing = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", unreadable)))
        using (var refusingSession = refusing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false }))
        {
            refusingSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
            await Send(refusingSession, "PATCH", "/api/settings", 409, Body("""{"frameRate":30}"""));
        }

        // The recent log entries and the engine's folders, the session token's; a folder the engine can't open (an
        // engine of its own, whose opener fails) is 500. No file manager opens: the test engines' openers only record.
        await Send(session, "GET", "/api/logs", 200);
        await Send(session, "GET", "/api/logs?after=1", 200);
        await Send(session, "GET", "/api/logs?after=-1", 400);
        await Send(session, "POST", "/api/folders/data/open", 204);
        await Send(session, "POST", "/api/folders/logs/open", 204);
        await Send(session, "POST", "/api/folders/profiles/open", 404);
        Assert.Equal([factory.DataDir, Path.Combine(factory.DataDir, "logs")], factory.Folders.Opened);
        var failingFolders = Path.Combine(factory.Root, "folders-failing");
        using (var failing = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", failingFolders)
                   .ConfigureTestServices(s => s.AddSingleton<IFolderOpener>(new RecordingFolderOpener { Failure = new IOException("no file manager") }))))
        using (var failingSession = failing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false }))
        {
            failingSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
            await Send(failingSession, "POST", "/api/folders/data/open", 500);
        }

        // The displays, every token's: none where the engine shows no desktop windows (this one); listed by an engine of
        // its own on a fake desktop, then 500 once that desktop fails to list them. No test needs a real display.
        Assert.False((await Send(reader, "GET", "/api/displays", 200)).GetProperty("supported").GetBoolean());
        using (var desktop = new FakeDesktopPlatform(new DisplayInfo("Built-in", new PointRect(0, 0, 1512, 982), 2)))
        using (var listing = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", Path.Combine(factory.Root, "displays"))
                   .ConfigureTestServices(s => s.AddSingleton<IDesktopPlatform>(desktop))))
        using (var listingSession = listing.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false }))
        {
            listingSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
            Assert.Equal(1, (await Send(listingSession, "GET", "/api/displays", 200)).GetProperty("displays").GetArrayLength());
            desktop.DisplaysFailure = new InvalidOperationException("no displays");
            await Send(listingSession, "GET", "/api/displays", 500);
        }

        // Profiles: create (from a document and empty), duplicate, list.
        var id = NewId();
        var document = Doc("Tour", 200, 100, Scene("a", 10, 10));
        document["id"] = id;
        await Send(session, "POST", "/api/profiles", 201, Json(new JsonObject { ["id"] = id, ["document"] = document }));
        await Send(session, "POST", "/api/profiles", 201, Body($$"""{"id":"{{NewId()}}","name":"Tour, empty","width":100,"height":50}"""));
        await Send(session, "POST", "/api/profiles", 409, Body($$"""{"id":"{{id}}","name":"Again","width":100,"height":50}"""));
        await Send(session, "POST", "/api/profiles", 422, Body("{}"));
        var copy = NewId();
        var copyTag = (await Send(session, "POST", $"/api/profiles/{id}/duplicate", 201, Body($$"""{"id":"{{copy}}"}"""))).GetProperty("instance").GetString()!;
        await Send(session, "POST", $"/api/profiles/{id}/duplicate", 409, Body($$"""{"id":"{{copy}}"}"""));
        await Send(session, "POST", $"/api/profiles/{SeededEngineFactory.Unsupported}/duplicate", 409, Body($$"""{"id":"{{NewId()}}"}"""));
        await Send(session, "POST", $"/api/profiles/{AbsentProfile}/duplicate", 404, Body($$"""{"id":"{{NewId()}}"}"""));
        await Send(session, "POST", $"/api/profiles/{id}/duplicate", 422, Body("""{"id":"Not-An-Id"}"""));
        await Send(session, "GET", "/api/profiles", 200);

        // A profile's document, export and thumbnail. Its changes name the document's tag.
        async Task<long> Rev() => (await Send(session, "GET", $"/api/profiles/{id}", 200)).GetProperty("rev").GetInt64();
        var instance = (await Send(session, "GET", $"/api/profiles/{id}", 200)).GetProperty("instance").GetString()!;
        await Send(session, "GET", $"/api/profiles/{AbsentProfile}", 404);
        await Send(session, "GET", $"/api/profiles/{SeededEngineFactory.Unsupported}", 409);
        await Send(session, "GET", $"/api/profiles/{id}/document", 200);
        await Send(session, "GET", $"/api/profiles/{SeededEngineFactory.Unsupported}/document", 409);
        if (!OperatingSystem.IsWindows())
            await Send(session, "GET", $"/api/profiles/{SeededEngineFactory.NoAccess}/document", 409);
        await Send(session, "GET", $"/api/profiles/{AbsentProfile}/document", 404);
        await Send(session, "GET", $"/api/profiles/{id}/archive", 200);
        await Send(session, "GET", $"/api/profiles/{SeededEngineFactory.Unsupported}/archive", 409);
        await Send(session, "GET", $"/api/profiles/{AbsentProfile}/archive", 404);
        await Send(session, "GET", $"/api/profiles/{id}/thumbnail?w=64", 200);
        await Send(session, "GET", $"/api/profiles/{id}/thumbnail?w=wide", 400);
        await Send(session, "GET", $"/api/profiles/{id}/thumbnail?w=2147483647", 200);
        await Send(session, "GET", $"/api/profiles/{id}/thumbnail?w=2147483648", 400);
        await Send(session, "GET", $"/api/profiles/{AbsentProfile}/thumbnail", 404);
        await Send(session, "GET", $"/api/profiles/{SeededEngineFactory.Unsupported}/thumbnail", 409);

        // Commits, undo, redo, revert and save, with their refusals.
        await Send(session, "POST", $"/api/profiles/{id}/commit", 200, Json(CommitBody(instance, await Rev(), Place("a", 20, 20))));
        await Send(session, "POST", $"/api/profiles/{id}/commit", 409, Json(CommitBody("another-instance", await Rev(), Place("a", 30, 30))));
        await Send(session, "POST", $"/api/profiles/{id}/commit", 422, Body("{}"));
        await Send(session, "POST", $"/api/profiles/{AbsentProfile}/commit", 404, Json(CommitBody(instance, 1, Place("a", 1, 1))));
        await Send(session, "POST", $"/api/profiles/{SeededEngineFactory.Unsupported}/commit", 409, Json(CommitBody(instance, 1, Place("a", 1, 1))));
        foreach (var step in new[] { "undo", "redo", "revert" })
        {
            await Send(session, "POST", $"/api/profiles/{id}/{step}", 200, JsonBytes(History(instance, await Rev())));
            await Send(session, "POST", $"/api/profiles/{id}/{step}", 409, JsonBytes(History("another-instance", await Rev())));
            await Send(session, "POST", $"/api/profiles/{id}/{step}", 422, Body("{}"));
            await Send(session, "POST", $"/api/profiles/{AbsentProfile}/{step}", 404, JsonBytes(History(instance, 1)));
            await Send(session, "POST", $"/api/profiles/{SeededEngineFactory.Unsupported}/{step}", 409, JsonBytes(History(instance, 1)));
        }

        await Send(session, "POST", $"/api/profiles/{id}/save", 200);
        await Send(session, "POST", $"/api/profiles/{id}/save", 200, JsonBytes(History(instance, await Rev())));
        await Send(session, "POST", $"/api/profiles/{id}/save", 409, JsonBytes(History("another-instance", await Rev())));
        await Send(session, "POST", $"/api/profiles/{id}/save", 422, Body("{}"));
        await Send(session, "POST", $"/api/profiles/{AbsentProfile}/save", 404);
        await Send(session, "POST", $"/api/profiles/{SeededEngineFactory.Unsupported}/save", 409);

        // Blobs.
        var blob = (await Send(session, "POST", "/api/blobs", 201, new ByteArrayContent([1, 2, 3, 4]))).GetProperty("id").GetString();
        await Send(session, "POST", "/api/blobs", 200, new ByteArrayContent([1, 2, 3, 4]));
        await Send(session, "POST", "/api/blobs", 413, new BlobUploadTests.GeneratedContent(SpecLimits.Default.VideoBytes + 1L, declareLength: true));
        await Send(session, "HEAD", $"/api/blobs/{blob}", 200);
        await Send(session, "HEAD", $"/api/blobs/{AbsentBlob}", 404);
        await Send(session, "GET", $"/api/blobs/{blob}", 200);
        await Send(session, "GET", $"/api/blobs/{AbsentBlob}", 404);

        // The 1.x import: the migration's state and record, a profile of the 1.x folder (1.1.x data for one request), a 1.x file.
        await Send(session, "GET", "/api/import/v1", 200);
        await Send(session, "PUT", "/api/import/v1", 200, Body($$"""{"profiles":[{"v1Id":"{{Import.V1Fixture.Desk}}","id":null,"outcome":"skipped"}],"settings":"skipped"}"""));
        await Send(session, "PUT", "/api/import/v1", 422, Body("{}"));
        await Send(session, "POST", $"/api/import/v1/profiles/{Import.V1Fixture.Desk}", 200);
        await Send(session, "POST", $"/api/import/v1/profiles/{AbsentProfile}", 404);
        var v1Settings = Path.Combine(factory.V1Dir, "settings.xml");
        var keptSettings = File.ReadAllBytes(v1Settings);
        File.WriteAllText(v1Settings, "<Settings><Version>114</Version></Settings>");
        await Send(session, "POST", $"/api/import/v1/profiles/{Import.V1Fixture.Desk}", 422);
        File.WriteAllBytes(v1Settings, keptSettings);
        await Send(session, "POST", "/api/import/v1/archive", 200, new ByteArrayContent(File.ReadAllBytes(Import.V1Fixture.AllKindsArchive)));
        await Send(session, "POST", "/api/import/v1/archive", 413, new ByteArrayContent(new byte[SeededEngineFactory.ArchiveBytes + 1]));
        await Send(session, "POST", "/api/import/v1/archive", 422, new ByteArrayContent("not a zip"u8.ToArray()));

        // A profile archive: one this engine exported, opened again.
        using (var exported = await session.GetAsync($"/api/profiles/{id}/archive", Ct))
            await Send(session, "POST", "/api/import/archive", 200, new ByteArrayContent(await exported.Content.ReadAsByteArrayAsync(Ct)));
        await Send(session, "POST", "/api/import/archive", 413, new ByteArrayContent(new byte[SeededEngineFactory.ArchiveBytes + 1]));
        await Send(session, "POST", "/api/import/archive", 422, new ByteArrayContent("not a zip"u8.ToArray()));

        // Deleting: with the profile's tag, required, and never another's.
        await Send(session, "DELETE", $"/api/profiles/{copy}", 400);
        await Send(session, "DELETE", $"/api/profiles/{copy}?instance={instance}", 409);
        await Send(session, "DELETE", $"/api/profiles/{copy}?instance={copyTag}", 204);
        await Send(session, "DELETE", $"/api/profiles/{AbsentProfile}?instance={copyTag}", 404);
        await Send(session, "DELETE", $"/api/tokens/{edit.GetProperty("id").GetString()}", 204);

        // The socket: refused without an upgrade or a valid token, then a session that sends and gets every message.
        await Send(anonymous, "GET", "/ws", 400, configure: r => r.Headers.Add("Sec-WebSocket-Protocol", $"infopanel.v1, auth.{EngineFactory.Token}"));
        await Send(anonymous, "GET", "/ws", 401, configure: r => r.Headers.Add("Sec-WebSocket-Protocol", "infopanel.v1, auth.not-the-token"));
        await using var socket = await PreviewClient.ConnectAsync(factory, Ct);
        await socket.ReceiveTextAsync("hello", Ct);
        await socket.SendAsync(new { type = "subscribe", profileId = id, scale = 0.5, maxFps = 10 }, Ct);
        await socket.ReceiveTextAsync("doc", Ct);
        await socket.ReceiveFrameAsync(Ct);
        await socket.SendAsync(new { type = "view", scale = 0.25 }, Ct);
        await socket.ReceiveFrameAsync(Ct, header => header.Scale == 0.25);
        // Video: an h264 subscription's frames (through the test engine's own encoder), and a keyframe asked for.
        await socket.SendAsync(new { type = "subscribe", profileId = id, scale = 0.5, maxFps = 10, encoding = "h264" }, Ct);
        await socket.ReceiveFrameAsync(Ct, header => header.Encoding == "h264" || header.Fallback != null);
        await socket.SendAsync(new { type = "keyframe" }, Ct);
        await socket.SendAsync(new { type = "view", scale = 0.25 }, Ct);
        await socket.ReceiveFrameAsync(Ct, header => header.Scale == 0.25);
        await Send(session, "POST", "/api/profiles", 201, Body($$"""{"id":"{{NewId()}}","name":"Tour, listed","width":100,"height":50}"""));
        await socket.ReceiveTextAsync("profiles", Ct);
        await Send(session, "PATCH", "/api/settings", 200, Body("""{"frameRate":25}"""));
        await socket.ReceiveTextAsync("settings", Ct);
        await socket.SendAsync(new { type = "subscribe", profileId = AbsentProfile }, Ct);
        await socket.ReceiveTextAsync("error", Ct);
        await socket.SendAsync(new { type = "unsubscribe" }, Ct);
        await using var byMessage = await PreviewClient.ConnectAsync(factory, Ct, EngineRuntime.WebSocketProtocol);
        await byMessage.SendAsync(new { type = "auth", token = EngineFactory.Token }, Ct);
        await byMessage.ReceiveTextAsync("hello", Ct);

        // What the tour saw against what the spec declares.
        var seen = factory.Contract.Seen.ToHashSet();
        var allowed = Allowlist.Where(a => a.Applies).Select(a => (a.Operation, a.Status)).ToHashSet();
        var declared = Contract.Operations.SelectMany(o => o.Responses.Keys.Select(status => (o.Key, status))).ToList();
        var unseen = declared.Where(d => !seen.Contains(d) && !allowed.Contains(d)).Select(d => $"{d.Key} {d.status}").ToList();
        Assert.True(unseen.Count == 0, $"Declared in spec/openapi.json, never seen by the tour (drive it, or allowlist it with a reason): {string.Join(", ", unseen)}");
        var stale = allowed.Where(seen.Contains).Select(a => $"{a.Operation} {a.Status}").ToList();
        Assert.True(stale.Count == 0, $"On the allowlist, but the tour saw it (remove the entry): {string.Join(", ", stale)}");
        Assert.Empty(factory.Contract.Violations);

        var (_, clientMessage, serverMessage, _) = Contract.Socket;
        var sent = socket.SentTypes.Concat(byMessage.SentTypes).ToHashSet();
        var received = socket.ReceivedTypes.Concat(byMessage.ReceivedTypes).ToHashSet();
        var unsent = ApiContract.MessageTypes(clientMessage).Where(t => !sent.Contains(t)).ToList();
        var unreceived = ApiContract.MessageTypes(serverMessage).Append("frame").Where(t => !received.Contains(t)).ToList();
        Assert.True(unsent.Count == 0, $"Client messages the spec defines, never sent by the tour: {string.Join(", ", unsent)}");
        Assert.True(unreceived.Count == 0, $"Server messages the spec defines, never received by the tour: {string.Join(", ", unreceived)}");
    }

    [Fact]
    public void TheAllowlist_NamesDeclaredResponses_WithAReason()
    {
        foreach (var (operation, status, reason, _) in Allowlist)
        {
            Assert.True(Contract.Find(operation)?.Responses.ContainsKey(status) == true, $"{operation} {status} isn't declared in spec/openapi.json.");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{operation} {status} has no reason.");
        }

        Assert.Equal(Allowlist.Count, Allowlist.Select(a => (a.Operation, a.Status)).Distinct().Count());

        // The allowlist may only shrink, and it is empty: a new entry fails here, so drive the response in the tour instead.
        Assert.True(Allowlist.Count == 0, "The allowlist is empty and may only shrink: drive the new response in the tour instead.");
    }

    private static byte[] History(string instance, long baseRev) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["instance"] = instance, ["baseRev"] = baseRev }.ToJsonString());

    private static ByteArrayContent JsonBytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
