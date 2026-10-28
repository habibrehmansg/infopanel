using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Tests.Preview;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests;

/// <summary>
/// Each document's lifetime tag, <c>instance</c> (spec/docs/api.md, "A document's tag"; commit.md): new every time the
/// engine creates a profile or first reads it in a run, carried by every profile entry (one that can't be opened too),
/// every document answer and every <c>doc</c> event, named by every change, and required by <c>DELETE</c>. So a request
/// built on a profile that was deleted and created again under the same id is refused instead of acting on the new one.
/// The session's <c>instance</c> (and <c>hello</c>'s) is the run's.
/// </summary>
public class DocumentTagTests(SeededEngineFactory factory) : IClassFixture<SeededEngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> Get(HttpClient api, string path) => await Read(await api.GetAsync(path, Ct), HttpStatusCode.OK);

    private static async Task<JsonElement> Create(HttpClient api, string id, JsonObject document) =>
        await Read(await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["document"] = document.DeepClone() }), Ct), HttpStatusCode.Created);

    private static string Tag(JsonElement body)
    {
        Assert.True(body.TryGetProperty("instance", out var tag), $"No instance in {body.GetRawText()}");
        return tag.GetString()!;
    }

    /// <summary>The document's tag as <c>GET /api/profiles/{id}</c> answers it.</summary>
    private static async Task<string> DocTag(HttpClient api, string id) => Tag(await Get(api, $"/api/profiles/{id}"));

    private static Task<HttpResponseMessage> Delete(HttpClient api, string id, string query) => api.DeleteAsync($"/api/profiles/{id}{query}", Ct);

    private static string With(string tag) => "?instance=" + Uri.EscapeDataString(tag);

    /// <summary>
    /// A 409 with one conflict, the whole request's, <c>reason: "instance"</c>: <c>conflictProblem</c> to a commit, undo,
    /// redo or revert (with a <c>rev</c>), <c>deleteConflictProblem</c> to a DELETE.
    /// </summary>
    private static async Task<JsonElement> InstanceConflict(HttpResponseMessage response, string definition = "conflictProblem")
    {
        var problem = await Read(response, HttpStatusCode.Conflict);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Valid(problem, definition));
        Assert.Equal("""[{"reason":"instance"}]""", problem.GetProperty("conflicts").GetRawText());
        return problem;
    }

    private static async Task<bool> Listed(HttpClient api, string id) =>
        (await Get(api, "/api/profiles")).EnumerateArray().Any(e => e.GetProperty("id").GetString() == id);

    [Fact]
    public async Task ALostCommitsRetry_AfterItsProfileIsDeletedAndCreatedAgain_Is409Instance()
    {
        using var api = factory.CreateApiClient();
        var id = NewId();
        var document = Doc("Before", 200, 100, Scene("a", 10, 10));
        await Create(api, id, document);
        var first = await DocTag(api, id);

        // A commit whose answer is lost: the client will retry these bytes.
        var bytes = Encoding.UTF8.GetBytes(CommitBody(first, 1, Place("a", 50, 50, baseRev: 1)).ToJsonString());
        using (var applied = await api.PostAsync($"/api/profiles/{id}/commit", new ByteArrayContent(bytes), Ct))
            await Read(applied, HttpStatusCode.OK);

        // Meanwhile the profile is deleted and created again under the same id, at revision 1 with no history.
        await Read(await Delete(api, id, With(first)), HttpStatusCode.NoContent);
        await Create(api, id, document);
        var second = await DocTag(api, id);

        // The retry names the document it was built on: refused, and the new profile is as it was created.
        await InstanceConflict(await api.PostAsync($"/api/profiles/{id}/commit", new ByteArrayContent(bytes), Ct));
        var now = await Get(api, $"/api/profiles/{id}");
        Assert.Equal((1L, false), (now.GetProperty("rev").GetInt64(), now.GetProperty("dirty").GetBoolean()));
        Assert.NotEqual(first, second);
        Assert.Equal(10, now.GetProperty("document").GetProperty("items")[0].GetProperty("placement").GetProperty("x").GetDouble());

        // So is an undo, redo or revert built on the earlier document.
        var history = Json(new JsonObject { ["instance"] = first, ["baseRev"] = 1 });
        foreach (var step in new[] { "undo", "redo", "revert" })
            await InstanceConflict(await api.PostAsync($"/api/profiles/{id}/{step}", history, Ct));
    }

    [Fact]
    public async Task ADelayedDelete_WithTheOldTag_Is409Instance_AndKeepsTheProfileCreatedAgain()
    {
        using var api = factory.CreateApiClient();
        var id = NewId();
        var document = Doc("Kept", 200, 100, Scene("a"));
        await Create(api, id, document);
        var first = await DocTag(api, id);
        await Read(await Delete(api, id, With(first)), HttpStatusCode.NoContent);
        await Create(api, id, document);
        var second = await DocTag(api, id);

        var problem = await InstanceConflict(await Delete(api, id, With(first)), "deleteConflictProblem");
        Assert.Equal(1, problem.GetProperty("rev").GetInt64());
        Assert.True(await Listed(api, id));
        Assert.Equal(second, await DocTag(api, id));

        // Its own tag deletes it; deleting again is 404, whatever the tag.
        await Read(await Delete(api, id, With(second)), HttpStatusCode.NoContent);
        await Read(await Delete(api, id, With(second)), HttpStatusCode.NotFound);
        Assert.False(await Listed(api, id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?instance=")]
    [InlineData("?instance=a&instance=a")]
    [InlineData("?other=a")]
    public async Task ADeleteWithoutOneTag_Is400_AndDeletesNothing(string query)
    {
        using var api = factory.CreateApiClient();
        var id = NewId();
        await Create(api, id, Doc("Untouched", 200, 100, Scene("a")));
        var tag = await DocTag(api, id);
        var problem = await Read(await Delete(api, id, query), HttpStatusCode.BadRequest);
        Assert.True(Valid(problem, "problem"));
        Assert.True(await Listed(api, id));

        // Checked before anything else, as a query parameter is: a profile that doesn't exist is 400 too.
        await Read(await Delete(api, NewId(), query), HttpStatusCode.BadRequest);
        // A tag longer than any tag (64 characters) is malformed, not stale.
        await Read(await Delete(api, id, With(new string('a', 65))), HttpStatusCode.BadRequest);
        await Read(await Delete(api, id, With(tag)), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task EveryEntry_CarriesItsTag_AndACantOpenProfilesTagDeletesIt()
    {
        using var api = factory.CreateApiClient();
        var entries = (await Get(api, "/api/profiles")).EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        foreach (var entry in entries.Values)
            Assert.True(Valid(entry, "profileEntry"));
        Assert.Equal(await DocTag(api, SeededEngineFactory.BadScene), Tag(entries[SeededEngineFactory.BadScene]));

        // A profile the engine can't open has a tag too, the same in every listing of this run, and only it deletes the profile.
        foreach (var id in new[] { SeededEngineFactory.Unsupported, SeededEngineFactory.NotJson })
        {
            var tag = Tag(entries[id]);
            Assert.Equal(tag, Tag((await Get(api, "/api/profiles")).EnumerateArray().Single(e => e.GetProperty("id").GetString() == id)));
            var problem = await InstanceConflict(await Delete(api, id, With(tag + "0")), "deleteConflictProblem");
            Assert.False(problem.TryGetProperty("rev", out _), "A profile that can't be opened has no revision.");
            Assert.True(await Listed(api, id));

            await Read(await Delete(api, id, With(tag)), HttpStatusCode.NoContent);
            Assert.False(await Listed(api, id));
            Assert.False(Directory.Exists(Path.Combine(factory.DataDir, "profiles", id)));
        }
    }

    [Fact]
    public async Task TheTag_IsTheDocumentsNotTheRuns_TheSameWhileItLives_AndNewForEveryProfile()
    {
        using var api = factory.CreateApiClient();
        var run = Tag(await Get(api, "/api/session"));
        var a = NewId();
        var b = NewId();
        var c = NewId();
        var createdA = await Create(api, a, Doc("A", 200, 100, Scene("s")));
        var createdB = await Read(await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = b, ["name"] = "B", ["width"] = 10, ["height"] = 10 }), Ct),
            HttpStatusCode.Created);
        var createdC = await Read(await api.PostAsync($"/api/profiles/{a}/duplicate", Json(new JsonObject { ["id"] = c }), Ct), HttpStatusCode.Created);
        var tagA = await DocTag(api, a);
        var tags = new[] { run, tagA, await DocTag(api, b), await DocTag(api, c), await DocTag(api, SeededEngineFactory.RefSource) };
        Assert.True(tags.Distinct().Count() == tags.Length, $"The run's instance and four documents' tags, not all different: {string.Join(", ", tags)}");

        // The create and duplicate answers carry the tag the document has.
        Assert.Equal(tags[1..4], new[] { Tag(createdA), Tag(createdB), Tag(createdC) });

        // Reading, listing and changing the document keep its tag.
        using (var changed = await api.PostAsync($"/api/profiles/{a}/commit", Json(CommitBody(tagA, 1, Place("s", 5, 5))), Ct))
            await Read(changed, HttpStatusCode.OK);
        Assert.Equal(tagA, Tag(await Get(api, $"/api/profiles/{a}")));
        Assert.Equal(tagA, Tag(entriesOf(await Get(api, "/api/profiles"), a)));

        // The run's instance isn't the document's: a commit naming it is refused.
        await InstanceConflict(await api.PostAsync($"/api/profiles/{a}/commit", Json(CommitBody(run, 2, Place("s", 6, 6))), Ct));

        static JsonElement entriesOf(JsonElement list, string id) => list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Hello_CarriesTheRunsInstance()
    {
        using var api = factory.CreateApiClient();
        var run = Tag(await Get(api, "/api/session"));
        await using var socket = await PreviewClient.ConnectAsync(factory, Ct);
        var hello = await socket.ReceiveTextAsync("hello", Ct);
        Assert.Equal($$"""{"type":"hello","protocol":2,"instance":"{{run}}"}""", hello.GetRawText());
    }

    [Fact]
    public async Task DocEvents_CarryTheDocumentsTag()
    {
        using var api = factory.CreateApiClient();
        var id = NewId();
        await Create(api, id, Doc("Watched", 200, 100, Scene("a")));
        var tag = await DocTag(api, id);
        await using var socket = await PreviewClient.ConnectAsync(factory, Ct);
        await socket.ReceiveTextAsync("hello", Ct);
        await socket.SendAsync(new { type = "subscribe", profileId = id }, Ct);
        var doc = await socket.ReceiveTextAsync("doc", Ct);
        Assert.True(Valid(doc, "docEvent"));
        Assert.Equal(tag, Tag(doc));

        using (var changed = await api.PostAsync($"/api/profiles/{id}/commit", Json(CommitBody(tag, 1, Place("a", 5, 5))), Ct))
            await Read(changed, HttpStatusCode.OK);
        Assert.Equal(tag, Tag(await socket.ReceiveTextAsync("doc", Ct, d => d.GetProperty("rev").GetInt64() == 2)));
    }
}
