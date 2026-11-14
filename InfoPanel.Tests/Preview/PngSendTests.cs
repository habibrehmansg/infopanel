using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// A <c>png</c> subscription's frames on the engine host, from the render loop to the socket: the sink takes a reference to
/// the frame's pooled PNG instead of a copy, every sink of the frame sends the same array, and the reference goes back once
/// the send has ended, whether it completed, failed or was cancelled. Ticked by hand with a manual clock.
/// </summary>
public sealed class PngSendTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromTicks((TimeSpan.TicksPerSecond + 59) / 60);

    private readonly EngineFactory _base = new();
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private WebApplicationFactory<Program>? _factory;

    public ValueTask InitializeAsync()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(new RenderServiceOptions(AutoTick: false));
        }));
        _ = _factory.Services;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        await _base.DisposeAsync();
    }

    private RenderService Render => _factory!.Services.GetRequiredService<RenderService>();

    private Workspace Workspace => _factory!.Services.GetRequiredService<Workspace>();

    /// <summary>
    /// A 1280×720 profile: a diagonal gradient (a PNG far larger than the allocation bound) under a bar that grows with
    /// time, so every tick has new pixels.
    /// </summary>
    private string NewProfile()
    {
        var scene = JsonNode.Parse("""
            {"format": "infopanel.scene/1",
             "state": {"t": {"init": 0, "next": {"op": "add", "args": [{"op": "state", "name": "t"}, {"op": "dt"}]}}},
             "nodes": [
               {"kind": "rect", "w": 1280, "h": 720, "fill": {"type": "linear", "x1": 0, "y1": 0, "x2": 1280, "y2": 720, "stops": [
                 {"at": 0, "color": "#0ea5e9"}, {"at": 0.3, "color": "#7f1d1d"}, {"at": 0.6, "color": "#22c55e"}, {"at": 1, "color": "#fde68a"}]}},
               {"kind": "rect", "w": {"op": "mul", "args": [{"op": "state", "name": "t"}, 10]}, "h": 20, "fill": "#ffffff"}]}
            """)!.AsObject();
        return TestDocs.Create(Workspace, TestDocs.Doc("Gradient", 1280, 720, TestDocs.Scene("grow", 0, 0, scene)));
    }

    private void Tick(string profileId)
    {
        _clock.Advance(Step);
        Render.Tick(profileId);
    }

    private static WebPreviewSink Sink(string profileId) => new(profileId, new FrameRequest(1, 60, IncludeBounds: false), () => { }, (_, _) => { });

    /// <summary>Whether a reference to <paramref name="png"/> is still held (probed by taking and giving back one more).</summary>
    private static bool Held(PngLease png)
    {
        try
        {
            png.AddRef();
        }
        catch (InvalidOperationException)
        {
            return false; // given back: the probe leaves it so
        }

        png.Release();
        return true;
    }

    /// <summary>A 60 fps sink that keeps the frame's PNG object (not a reference to it), to probe it later.</summary>
    private sealed class Capture(string profileId) : IFrameSink
    {
        public string ProfileId => profileId;

        public FrameRequest Request { get; } = new(1, 60, IncludeBounds: false);

        public PngLease? Last { get; private set; }

        public void OnFrame(RenderedFrame frame)
        {
            var png = frame.LeasePng();
            png.Release();
            Last = png;
        }
    }

    /// <summary>Counts what the wrapped sink's <see cref="IFrameSink.OnFrame"/> allocates on its thread.</summary>
    private sealed class Measured(WebPreviewSink sink) : IFrameSink
    {
        public long Bytes;
        public int Frames;

        public string ProfileId => sink.ProfileId;

        public FrameRequest Request => sink.Request;

        public void OnFrame(RenderedFrame frame)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            sink.OnFrame(frame);
            Bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            Frames++;
        }
    }

    [Fact]
    public void AStreamOfPngFrames_ThroughTheSink_DoesNotAllocateAnArrayPerFrame()
    {
        var id = NewProfile();
        var sink = Sink(id);
        var measured = new Measured(sink);
        Assert.True(Render.Attach(measured));

        var length = 0;
        void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Tick(id);
                while (sink.TryTakeFrame(out var frame))
                {
                    length = frame.Payload.Length;
                    frame.Sent();
                }
            }
        }

        Run(10); // warmed: the pool holds an array of the PNG's size
        (measured.Bytes, measured.Frames) = (0, 0);
        Run(100);

        var perFrame = measured.Bytes / (double)measured.Frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"MEASURED {perFrame:F0} B per OnFrame over {measured.Frames} frames, PNG {length} B");
        Assert.True(measured.Frames >= 50, $"{measured.Frames} frames in 100 ticks");
        Assert.True(length > 85_000, $"the PNG is {length} B");
        Assert.True(perFrame < 8 * 1024, $"{perFrame:F0} B allocated per OnFrame for a {length} B PNG");
        Render.Detach(measured);
    }

    [Fact]
    public void TwoSinksOfOneFrame_SendOneArray_GivenBackOnceBothSent_AndRetiringGivesBackWhatIsQueued()
    {
        var id = NewProfile();
        var capture = new Capture(id);
        var a = Sink(id);
        var b = Sink(id);
        Assert.True(Render.Attach(capture));
        Assert.True(Render.Attach(a));
        Assert.True(Render.Attach(b));

        Tick(id); // the frame is closed: only the two sinks hold its PNG
        var png = capture.Last!;
        Assert.True(a.TryTakeFrame(out var fromA));
        Assert.True(b.TryTakeFrame(out var fromB));
        Assert.True(MemoryMarshal.TryGetArray(fromA.Payload, out var arrayA));
        Assert.True(MemoryMarshal.TryGetArray(fromB.Payload, out var arrayB));
        Assert.Same(arrayA.Array, arrayB.Array);
        Assert.True(MemoryMarshal.TryGetArray(png.Memory, out var pooled));
        Assert.Same(pooled.Array, arrayA.Array);

        fromA.Sent();
        Assert.True(Held(png), "given back while the second sink's send is under way");
        fromB.Sent();
        Assert.False(Held(png), "not given back once both sends ended");

        // The next frame stays queued in a: retiring it gives it back.
        Tick(id);
        var next = capture.Last!;
        Assert.NotSame(png, next);
        Assert.True(b.TryTakeFrame(out var sent));
        sent.Sent();
        Assert.True(Held(next));
        a.Retire();
        Assert.False(Held(next), "a retired sink's queued frame is given back");

        b.Retire();
        Render.Detach(capture);
        Render.Detach(a);
        Render.Detach(b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFrameWhoseSendFailsOrIsCancelled_IsGivenBack_AfterTheSendEnds(bool cancelled)
    {
        var id = NewProfile();
        var socket = new FakeWebSocket();
        using var aborted = new CancellationTokenSource();
        var session = new PreviewSession(socket, Workspace, Render, NullLogger.Instance);
        var run = session.RunAsync(aborted.Token, CancellationToken.None);
        Assert.True((await socket.NextAsync()).IsText("hello"));
        await socket.ReceiveAsync(new { type = "subscribe", profileId = id, maxFps = 60 });
        await socket.NextAsync(m => m.IsText("doc"));
        var capture = new Capture(id);
        Assert.True(Render.Attach(capture));

        var paused = socket.PauseWhen(m => m.Type == WebSocketMessageType.Binary);
        Tick(id);
        await paused;
        var png = capture.Last!;
        Assert.True(Held(png), "given back while its send is under way");

        if (cancelled)
            await aborted.CancelAsync();
        else
            socket.Fail(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        socket.ReceiveClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(Held(png), $"not given back after a {(cancelled ? "cancelled" : "failed")} send");
        Render.Detach(capture);
    }
}
