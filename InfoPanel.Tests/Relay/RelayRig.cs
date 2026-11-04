using System.Text.Json.Nodes;
using InfoPanel.Service;
using Serilog;
using Xunit;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// A relay client wired to a fake relay on its own loopback port, a credential in memory and the two clocks faked, with
/// the jitter fixed (0.5: every backoff wait exactly its nominal length).
/// </summary>
public sealed class RelayRig : IAsyncDisposable
{
    public const string Credential = "relay-test-credential";

    private RelayRig(FakeRelay relay, TwoClocks clocks, ServiceCredentials credentials, IRelayToolHost tools, RelayClient client)
    {
        Relay = relay;
        Clocks = clocks;
        Credentials = credentials;
        Tools = tools;
        Client = client;
        client.Settled += state => _settles.Writer.TryWrite(state);
    }

    private readonly System.Threading.Channels.Channel<RelayState> _settles = System.Threading.Channels.Channel.CreateUnbounded<RelayState>();

    /// <summary>
    /// Completes at the client's next settle (<see cref="RelayClient.Settled"/>, every one recorded from its start) in
    /// <paramref name="state"/>: by then it has acted on everything before, so a test can check what it didn't do.
    /// </summary>
    public async Task SettledAsync(RelayState state)
    {
        while (await _settles.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct) != state)
        {
        }
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The longest a test waits for anything (real time): a guard so a broken engine fails a test, never hangs it.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public FakeRelay Relay { get; }

    public TwoClocks Clocks { get; }

    public ServiceCredentials Credentials { get; }

    public IRelayToolHost Tools { get; }

    public FakeToolHost? FakeTools => Tools as FakeToolHost;

    public RelayClient Client { get; }

    /// <summary>The address the credentials are for: the fake relay's, unless a test changes it.</summary>
    public string? Address { get; set; }

    /// <param name="before">Sets the relay up before the client starts (its answers to the upgrade, whether it reads).</param>
    /// <param name="log">The client's log, as it writes it (synchronously, on the thread that logs).</param>
    public static async Task<RelayRig> StartAsync(IRelayToolHost? tools = null, RelayOptions? options = null, double jitter = 0.5,
        bool enabled = true, Func<FakeRelay, string?>? address = null, Action<RelayRig>? before = null, Action<Serilog.Events.LogEvent>? log = null)
    {
        var relay = await FakeRelay.StartAsync();
        var clocks = new TwoClocks(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        RelayRig? rig = null;
        var credentials = new ServiceCredentials(new MemoryCredentialStore(), () => rig?.Address, new LoggerConfiguration().CreateLogger());
        tools ??= new FakeToolHost();
        var logger = new LoggerConfiguration();
        if (log is not null)
            logger.WriteTo.Sink(new LogSink(log));
        var client = new RelayClient(credentials, tools, options ?? new RelayOptions { EngineVersion = "2.0.0-test", LoopbackOnly = true }, clocks,
            logger.CreateLogger(), () => jitter);
        rig = new RelayRig(relay, clocks, credentials, tools, client) { Address = address is null ? relay.Address : address(relay) };
        if (rig.Address is not null)
            credentials.Put(Credential);
        before?.Invoke(rig);
        client.SetEnabled(enabled);
        client.Start();
        return rig;
    }

    /// <summary>Completes once the client's state is <paramref name="state"/>.</summary>
    public Task WaitForAsync(RelayState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(RelayState now)
        {
            if (now == state)
                reached.TrySetResult();
        }

        Client.StateChanged += OnChange;
        if (Client.State == state)
            reached.TrySetResult();
        return reached.Task.WaitAsync(RelayRig.Limit, Ct).ContinueWith(t =>
        {
            Client.StateChanged -= OnChange;
            t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Takes the next connection, reads its <c>hello</c>, welcomes it and answers the engine's first reading with the
    /// relay's true time: then the client is connected with a fresh reading.
    /// </summary>
    public async Task<RelayPeer> ConnectAsync()
    {
        var peer = await Relay.NextPeerAsync(Ct);
        var hello = await peer.ExpectAsync("hello", Ct);
        Assert.Equal(RelayFrames.Protocol, hello["protocol"]!.GetValue<int>());
        await peer.SendAsync(new JsonObject { ["type"] = "welcome" });
        await peer.AnswerTimeAsync(Clocks.RelayNow, Ct);
        await WaitForAsync(RelayState.Connected);
        // The answer is dispatched (its arrival read off both clocks) before the test moves them.
        await FlushAsync(peer);
        return peer;
    }

    /// <summary>A call frame, sent now on the relay's clock unless <paramref name="sentAt"/> says otherwise.</summary>
    public JsonObject Call(string id, string tool, JsonObject? arguments = null, long? sentAt = null, int? maxChars = null)
    {
        var call = new JsonObject
        {
            ["type"] = "call",
            ["id"] = id,
            ["tool"] = tool,
            ["arguments"] = arguments ?? new JsonObject(),
            ["sentAt"] = sentAt ?? Clocks.RelayNow,
        };
        if (maxChars is { } chars)
            call["maxChars"] = chars;
        return call;
    }

    /// <summary>
    /// Sends a frame the engine answers at once (an unknown type) and reads that answer: every frame sent before it has
    /// been dispatched by then.
    /// </summary>
    public static async Task FlushAsync(RelayPeer peer)
    {
        await peer.SendAsync(new JsonObject { ["type"] = "flush", ["id"] = "flush" });
        var answer = await peer.ExpectAsync("error", Ct);
        Assert.Equal("flush", answer["id"]!.GetValue<string>());
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Relay.DisposeAsync();
    }

    /// <summary>A log sink that hands each event to a test as it is written.</summary>
    private sealed class LogSink(Action<Serilog.Events.LogEvent> write) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => write(logEvent);
    }
}
