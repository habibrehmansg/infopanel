using System.Net.WebSockets;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Scenes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// <see cref="PreviewSession"/> interleavings, made deterministic: a <see cref="FakeWebSocket"/> holds the send loop
/// on the wire while the test changes state, and a <see cref="RenderService"/> of the test's own ticks only by hand.
/// </summary>
public sealed class PreviewSessionTests(EngineFactory factory) : IClassFixture<EngineFactory>, IAsyncLifetime
{
    private RenderService _render = null!;

    private Workspace Workspace => factory.Services.GetRequiredService<Workspace>();

    public ValueTask InitializeAsync()
    {
        _render = new RenderService(Workspace, factory.Services.GetRequiredService<SceneRuntimeOptions>(), TimeProvider.System,
            new RenderServiceOptions(AutoTick: false));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _render.DisposeAsync();

    private async Task<(FakeWebSocket Socket, PreviewSession Session, Task Run)> Start()
    {
        var socket = new FakeWebSocket();
        var session = new PreviewSession(socket, Workspace, _render, NullLogger.Instance);
        var run = session.RunAsync(CancellationToken.None, CancellationToken.None);
        Assert.True((await socket.NextAsync()).IsText("hello"));
        return (socket, session, run);
    }

    private static async Task Stop(FakeWebSocket socket, Task run)
    {
        socket.ReceiveClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A saved 120×80 profile with one shape, <c>shape</c>.</summary>
    private string NewProfile(string name) => TestDocs.Create(Workspace, TestDocs.Doc(name, 120, 80, TestDocs.Scene("shape")));

    private static long Revision(FakeWebSocket.Sent doc) => doc.Json.GetProperty("rev").GetInt64();

    private static bool Dirty(FakeWebSocket.Sent doc) => doc.Json.GetProperty("dirty").GetBoolean();

    private static long MoveTo(ProfileDocument doc, int x) => TestDocs.Commit(doc, TestDocs.Place("shape", x, 0)).Rev;

    private static (string Code, string Path) CodeAndPath(FakeWebSocket.Sent error) =>
        (error.Json.GetProperty("code").GetString()!, error.Json.GetProperty("path").GetString()!);

    [Fact]
    public async Task ASubscribeTheEngineCantStart_LeavesTheCurrentSubscriptionAsItWas()
    {
        var id = NewProfile("Kept subscription");
        var unsupported = TestDocs.NewId();
        var folder = Path.Combine(factory.DataDir, "profiles", unsupported);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), """{"format":"infopanel.document/9"}""");
        Workspace.Documents.Open(); // lists the folder, as the next start would
        var (socket, session, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        await socket.NextAsync(m => m.IsText("doc"));
        var sink = session.CurrentSink!;

        // No such profile, and one that can't be opened: each answered, and the subscription goes on as it was.
        foreach (var (other, code) in new[] { ("6f1b8a3e-0000-4000-8000-000000000000", "profile.missing"), (unsupported, "profile.unsupported") })
        {
            await socket.ReceiveAsync(new { type = "subscribe", profileId = other });
            Assert.Equal((code, "/profileId"), CodeAndPath(await socket.NextAsync(m => m.IsText("error"))));
            Assert.Same(sink, session.CurrentSink);
            Assert.True(_render.IsRendering(id));
        }

        var revision = MoveTo(Workspace.Open(id)!, 30);
        Assert.Equal(revision, Revision(await socket.NextAsync(m => m.IsText("doc"))));
        Assert.Equal(1, _render.Tick(id));
        Assert.Equal(id, (await socket.NextAsync(m => m.Type == WebSocketMessageType.Binary)).Frame.ProfileId);

        await Stop(socket, run);
        Assert.True(Workspace.Delete(unsupported));
    }

    [Fact]
    public async Task ASubscribeWhileTheEngineStops_CantBePreviewed()
    {
        var id = NewProfile("Stopping");
        var (socket, session, run) = await Start();
        await _render.DisposeAsync();

        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        Assert.Equal(("profile.unrenderable", "/profileId"), CodeAndPath(await socket.NextAsync(m => m.IsText("error"))));
        Assert.Null(session.CurrentSink);
        await Stop(socket, run);
    }

    [Fact]
    public async Task AViewOrUnsubscribeWithAMemberItDoesntDefine_GetsAnError_AndChangesNothing()
    {
        // Client messages are closed (spec/docs/versioning.md): a refused message is not acted on in part.
        var id = NewProfile("Closed messages");
        var (socket, session, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 });
        await socket.NextAsync(m => m.IsText("doc"));
        var sink = session.CurrentSink!;
        var request = sink.Request;

        await socket.ReceiveTextAsync("""{"type":"view","scale":2,"maxFps":5,"bounds":false}""");
        var error = await socket.NextAsync(m => m.IsText("error"));
        Assert.Equal("Unknown member \"bounds\" in view.", error.Json.GetProperty("message").GetString());
        Assert.Equal(("schema", "/bounds"), CodeAndPath(error));
        Assert.Equal(request, sink.Request);

        await socket.ReceiveTextAsync("""{"type":"unsubscribe","all":true}""");
        error = await socket.NextAsync(m => m.IsText("error"));
        Assert.Equal("Unknown member \"all\" in unsubscribe.", error.Json.GetProperty("message").GetString());
        Assert.Same(sink, session.CurrentSink);
        Assert.True(_render.IsRendering(id));

        await Stop(socket, run);
    }

    [Fact]
    public async Task ALateFrameOfAReplacedSubscription_NeverDisplacesTheCurrentOnesFrame()
    {
        var a = NewProfile("Late A");
        var b = NewProfile("Late B");
        var (socket, session, run) = await Start();

        await socket.ReceiveAsync(new { type = "subscribe", profileId = a });
        await socket.NextAsync(m => m.IsText("doc"));
        var replaced = session.CurrentSink!;
        var relay = new RelaySink(a, replaced); // keeps A rendering, and hands the replaced sink A's frames
        Assert.True(_render.Attach(relay));

        await socket.ReceiveAsync(new { type = "subscribe", profileId = b });
        await socket.NextAsync(m => m.IsText("doc") && m.Json.GetProperty("profileId").GetString() == b);

        // Hold the send loop on the wire, queue B's only frame, then give A's replaced sink a late frame.
        var blocked = socket.PauseWhen(m => m.IsText("error"));
        await socket.ReceiveTextAsync("""{"type":"dance"}""");
        await blocked;
        Assert.Equal(1, _render.Tick(b));
        Assert.Equal(1, _render.Tick(a));
        Assert.Equal(1, relay.Relayed);
        socket.Resume();

        var frame = await socket.NextAsync(m => m.Type == WebSocketMessageType.Binary);
        Assert.Equal(b, frame.Frame.ProfileId);

        _render.Detach(relay);
        await Stop(socket, run);
        Assert.DoesNotContain(socket.Drain(), m => m.Type == WebSocketMessageType.Binary && m.Frame.ProfileId == a);
    }

    [Fact]
    public async Task AProfileDeletedAndCreatedAgainWhileASubscribeRuns_EndsTheSubscriptionAsADeleteDoes()
    {
        var id = NewProfile("Created again");
        var (socket, session, run) = await Start();
        // After the subscribe found the document, before its frames are attached: deleted, and the id created again.
        session.Found = found =>
        {
            session.Found = null;
            Assert.True(Workspace.Delete(found));
            var again = TestDocs.Doc("Replacement", 120, 80, TestDocs.Scene("shape"));
            Assert.Equal(201, Workspace.Create(System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject { ["id"] = found, ["document"] = again }.ToJsonString())).Status);
        };

        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        var error = await socket.NextAsync(m => m.IsText("error"));
        Assert.Equal($"Profile '{id}' was deleted.", error.Json.GetProperty("message").GetString());
        // It answers the subscribe, at the profile it named.
        Assert.Equal(("profile.deleted", "/profileId"), CodeAndPath(error));
        Assert.Null(session.CurrentSink);
        Assert.False(_render.IsRendering(id));

        // The replacement's changes reach no one here.
        MoveTo(Workspace.Open(id)!, 30);
        Assert.Equal(0, _render.Tick(id));
        await Stop(socket, run);
        Assert.DoesNotContain(socket.Drain(), m => m.Type == WebSocketMessageType.Binary || (m.IsText("doc") && Revision(m) > 1));
    }

    [Fact]
    public async Task AnEndedSubscriptionsError_NeverFollowsTheNextSubscriptionsDoc()
    {
        var a = NewProfile("Ended A");
        var b = NewProfile("Ended B");
        var (socket, session, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = a });
        await socket.NextAsync(m => m.IsText("doc"));

        // A is deleted, and its close callback held once A is no longer current, before its error is queued.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Ended = ended =>
        {
            session.Ended = null;
            held.SetResult();
            Assert.True(resume.Task.Wait(TimeSpan.FromSeconds(5)));
        };
        var deleting = Task.Run(() => Workspace.Delete(a));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.CurrentSink);

        // B's subscription starts and its doc goes out; then A's callback finishes.
        await socket.ReceiveAsync(new { type = "subscribe", profileId = b });
        await socket.NextAsync(m => m.IsText("doc") && m.Json.GetProperty("profileId").GetString() == b);
        resume.SetResult();
        Assert.True(await deleting.WaitAsync(TimeSpan.FromSeconds(5)));

        // The next error is the answer to the next message, not A's ending: a client would read it as B's.
        await socket.ReceiveTextAsync("""{"type":"dance"}""");
        Assert.Equal(("schema", "/type"), CodeAndPath(await socket.NextAsync(m => m.IsText("error"))));
        Assert.Equal(b, session.CurrentSink!.ProfileId);
        Assert.True(_render.IsRendering(b));

        await Stop(socket, run);
        Assert.DoesNotContain(socket.Drain(), m => m.IsText("error"));
    }

    [Fact]
    public async Task TheDocMessage_IsTheStateWhenSent_SoASaveIsNeverOvertakenByAnOlderSnapshotOfTheSameRevision()
    {
        var id = NewProfile("Doc slot");
        var doc = Workspace.Open(id)!;
        var (socket, _, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        Assert.False(Dirty(await socket.NextAsync(m => m.IsText("doc"))));

        // The patch's doc (dirty) is on the wire when the save lands at the same revision.
        var dirtyOnTheWire = socket.PauseWhen(m => m.IsText("doc") && Dirty(m));
        var revision = MoveTo(doc, 30);
        Assert.Equal(revision, Revision(await dirtyOnTheWire));
        Assert.Equal(revision, doc.Save().Rev);
        socket.Resume();

        Assert.True(Dirty(await socket.NextAsync(m => m.IsText("doc")))); // the one that was on the wire
        var last = await socket.NextAsync(m => m.IsText("doc"));
        Assert.Equal((revision, false), (Revision(last), Dirty(last)));

        await Stop(socket, run);
        Assert.DoesNotContain(socket.Drain(), m => m.IsText("doc")); // nothing older follows it
    }

    [Fact]
    public async Task AFloodOfErrors_NeverEvictsTheDocOrProfilesState()
    {
        var id = NewProfile("Error flood");
        var doc = Workspace.Open(id)!;
        var (socket, _, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        await socket.NextAsync(m => m.IsText("doc"));

        // While the send loop is stuck on the wire: an edit, its save, a new profile, then far more errors than the queue holds.
        var blocked = socket.PauseWhen(m => m.IsText("error"));
        await socket.ReceiveTextAsync("""{"type":"dance"}""");
        await blocked;
        var revision = MoveTo(doc, 40);
        doc.Save();
        NewProfile("Error flood, too");
        var version = Workspace.Version;
        for (var i = 0; i < 3 * PreviewSession.MaxQueuedErrors; i++)
            await socket.ReceiveTextAsync("""{"type":"dance"}""");
        socket.Resume();

        var sent = new List<FakeWebSocket.Sent>();
        while (sent.Count == 0 || !sent[^1].IsText("profiles"))
            sent.Add(await socket.NextAsync());

        // Errors are expendable and bounded (the one on the wire, then the newest the queue kept)...
        Assert.InRange(sent.Count(m => m.IsText("error")), 1, 1 + PreviewSession.MaxQueuedErrors);
        // ...the doc and profiles slots are not: one doc with the saved state, then the newest version.
        var docs = sent.Where(m => m.IsText("doc")).ToList();
        Assert.Equal((revision, false), (Revision(Assert.Single(docs)), Dirty(docs[0])));
        Assert.Equal(version, sent[^1].Json.GetProperty("version").GetInt64());

        await Stop(socket, run);
    }

    /// <summary>Hands every frame of its profile to another sink (here: a preview sink its socket has replaced).</summary>
    private sealed class RelaySink(string profileId, IFrameSink target) : IFrameSink
    {
        private int _relayed;

        public string ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(1, 30);

        public int Relayed => Volatile.Read(ref _relayed);

        public void OnFrame(RenderedFrame frame)
        {
            target.OnFrame(frame);
            Interlocked.Increment(ref _relayed);
        }
    }
}
