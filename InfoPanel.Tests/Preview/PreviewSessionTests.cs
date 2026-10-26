using System.Net.WebSockets;
using System.Text.Json.Nodes;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Output;
using InfoPanel.Preview;
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
        var renderer = factory.Services.GetRequiredService<ProfileRenderer>();
        var states = new ProfileRenderStates();
        _render = new RenderService(Workspace, renderer, states, new ThumbnailRenderer(renderer, states), TimeProvider.System,
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

    /// <summary>A saved 120×80 profile with one shape.</summary>
    private Guid NewProfile(string name)
    {
        var id = Workspace.Create(name, 120, 80).Id;
        Workspace.Open(id)!.AddItem("shape");
        Assert.NotNull(Workspace.Save(id));
        return id;
    }

    private static long Revision(FakeWebSocket.Sent doc) => doc.Json.GetProperty("revision").GetInt64();

    private static bool Dirty(FakeWebSocket.Sent doc) => doc.Json.GetProperty("dirty").GetBoolean();

    private static ItemPatch MoveTo(ProfileDocument doc, int x) => new(doc.Items[0].Guid, new JsonObject { ["x"] = x });

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
        await socket.NextAsync(m => m.IsText("doc") && m.Json.GetProperty("profileId").GetGuid() == b);

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
    public async Task TheDocMessage_IsTheStateWhenSent_SoASaveIsNeverOvertakenByAnOlderSnapshotOfTheSameRevision()
    {
        var id = NewProfile("Doc slot");
        var doc = Workspace.Open(id)!;
        var (socket, _, run) = await Start();
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id });
        Assert.False(Dirty(await socket.NextAsync(m => m.IsText("doc"))));

        // The patch's doc (dirty) is on the wire when the save lands at the same revision.
        var dirtyOnTheWire = socket.PauseWhen(m => m.IsText("doc") && Dirty(m));
        var revision = doc.PatchItems([MoveTo(doc, 30)]).Revision;
        Assert.Equal(revision, Revision(await dirtyOnTheWire));
        Assert.Equal(revision, Workspace.Save(id)!.Revision);
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
        var revision = doc.PatchItems([MoveTo(doc, 40)]).Revision;
        Assert.NotNull(Workspace.Save(id));
        Workspace.Create("Error flood, too", 10, 10);
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
    private sealed class RelaySink(Guid profileId, IFrameSink target) : IFrameSink
    {
        private int _relayed;

        public Guid ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(1, 30);

        public int Relayed => Volatile.Read(ref _relayed);

        public void OnFrame(RenderedFrame frame)
        {
            target.OnFrame(frame);
            Interlocked.Increment(ref _relayed);
        }
    }
}
