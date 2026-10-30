using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Documents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// Unsaved changes across a restart, end to end on real Kestrel (spec/docs/document.md, "Revisions"): a change committed
/// through the API, then an orderly stop, then an engine on the same folder: the list entry, the document and the
/// <c>/ws</c> <c>doc</c> event carry <c>recovered</c>, at revision 2 and dirty with the committed content; undo goes back
/// to the saved version. A revert that answered before the stop brings nothing back. Every answer goes through the
/// factory's contract check, every socket message through the socket's.
/// </summary>
public sealed partial class RecoveryEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")]
    private static partial Regex Timestamp();

    private static async Task<(string Id, string Tag)> CreateMoved(HttpClient api)
    {
        var id = await CreateAsync(api, Doc("Recovery", 200, 100, Scene("a", 10, 10), Scene("b", 50, 10)), Ct);
        var tag = await LiveEngine.Tag(api, id, Ct);
        using var commit = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", LiveEngine.Commit(tag, 1, null, Place("a", 77, 10)), Ct);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        return (id, tag);
    }

    /// <summary>
    /// Waits until the stopped engine's orderly stop is done: the host disposes its workspace as it finishes stopping, on
    /// its own thread; a second disposal returns once that one has (or does it).
    /// </summary>
    private static void Stopped(Workspace workspace) => workspace.Dispose();

    private static double XOfA(JsonElement documentResponse) =>
        documentResponse.GetProperty("document").GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == "a").GetProperty("placement").GetProperty("x").GetDouble();

    [Fact]
    public async Task ChangesCommittedBeforeAnOrderlyStop_ComeBackAsOneUndoableStep_WithRecovered()
    {
        using var factory = new EngineFactory();
        string id;
        Workspace stopped;
        using (var first = LiveEngine.Start(factory))
        {
            stopped = first.Services.GetRequiredService<Workspace>();
            using var api = first.Client();
            (id, _) = await CreateMoved(api);
        }
        Stopped(stopped);
        Assert.True(File.Exists(Path.Combine(factory.DataDir, "profiles", id, "recovery.json")));

        using var second = LiveEngine.Start(factory);
        using var client = second.Client();
        var entry = (await client.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        Assert.Equal((2, true), (entry.GetProperty("rev").GetInt64(), entry.GetProperty("dirty").GetBoolean()));
        var recovered = entry.GetProperty("recovered").GetString()!;
        Assert.Matches(Timestamp(), recovered);

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        Assert.Equal(new DocState(2, true, true, false, recovered), DocState.From(state));
        Assert.Equal(77, XOfA(state));

        await using (var socket = await second.Subscribe(id, Ct))
        {
            await socket.Until(() => socket.Docs.Count > 0, TimeSpan.FromSeconds(30), "the first doc event", Ct);
            Assert.Equal(new DocState(2, true, true, false, recovered), socket.Docs[0]);
        }

        var tag = state.GetProperty("instance").GetString()!;
        using (var undo = await LiveEngine.PostBytes(client, $"/api/profiles/{id}/undo", LiveEngine.History(tag, 2), Ct))
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undone = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        // Still recovered: only a save or a revert ends it (a client shows it only while dirty).
        Assert.Equal(new DocState(3, false, false, true, recovered), DocState.From(undone));
        Assert.Equal(10, XOfA(undone));
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "profiles", id, "recovery.json")));

        // Saved: recovered ends.
        using (var redo = await LiveEngine.PostBytes(client, $"/api/profiles/{id}/redo", LiveEngine.History(tag, 3), Ct))
            Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
        Assert.Equal(recovered, (await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct)).GetProperty("recovered").GetString());
        using (var save = await client.PostAsync($"/api/profiles/{id}/save", null, Ct))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        Assert.False(saved.TryGetProperty("recovered", out _));
        Assert.False(saved.GetProperty("dirty").GetBoolean());
    }

    /// <summary>
    /// Restored, undone back to the saved content (clean, still recovered), then reverted: the revert takes no step but
    /// ends recovered, and the socket is told so.
    /// </summary>
    [Fact]
    public async Task ARevertOfARestoredDocumentAlreadyClean_TellsTheSocketRecoveredEnded()
    {
        using var factory = new EngineFactory();
        string id;
        Workspace stopped;
        using (var first = LiveEngine.Start(factory))
        {
            stopped = first.Services.GetRequiredService<Workspace>();
            using var api = first.Client();
            (id, _) = await CreateMoved(api);
        }
        Stopped(stopped);

        using var second = LiveEngine.Start(factory);
        using var client = second.Client();
        var state = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var recovered = state.GetProperty("recovered").GetString()!;
        var tag = state.GetProperty("instance").GetString()!;
        await using var socket = await second.Subscribe(id, Ct);
        await socket.Until(() => socket.Docs.Count > 0, TimeSpan.FromSeconds(30), "the first doc event", Ct);

        using (var undo = await LiveEngine.PostBytes(client, $"/api/profiles/{id}/undo", LiveEngine.History(tag, 2), Ct))
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undone = new DocState(3, false, false, true, recovered);
        await socket.Until(() => socket.Docs[^1] == undone, TimeSpan.FromSeconds(30), "the undo's doc event", Ct);

        using (var revert = await LiveEngine.PostBytes(client, $"/api/profiles/{id}/revert", LiveEngine.History(tag, 3), Ct))
            Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        Assert.Equal(new DocState(3, false, false, true), DocState.From(await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct)));
        await socket.Until(() => socket.Docs[^1] == new DocState(3, false, false, true), TimeSpan.FromSeconds(30), "a doc event without recovered", Ct);
    }

    [Fact]
    public async Task ARevertAnsweredBeforeTheStop_BringsNothingBack()
    {
        using var factory = new EngineFactory();
        string id;
        Workspace stopped;
        using (var first = LiveEngine.Start(factory))
        {
            stopped = first.Services.GetRequiredService<Workspace>();
            using var api = first.Client();
            (id, var tag) = await CreateMoved(api);
            using var revert = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/revert", LiveEngine.History(tag, 2), Ct);
            Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        }
        Stopped(stopped);
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "profiles", id, "recovery.json")));

        using var second = LiveEngine.Start(factory);
        using var client = second.Client();
        var state = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        Assert.Equal(new DocState(1, false, false, false), DocState.From(state));
        Assert.False(state.TryGetProperty("recovered", out _));
        Assert.Equal(10, XOfA(state));
        var entry = (await client.GetFromJsonAsync<JsonElement>("/api/profiles", Ct)).EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        Assert.False(entry.TryGetProperty("recovered", out _));
    }
}
