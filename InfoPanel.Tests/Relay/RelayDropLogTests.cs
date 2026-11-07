using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using InfoPanel.Service;
using Serilog.Events;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// The line the client logs once when a connection ends: why (the service's close status, with its description only when
/// it is one the relay is known to send; the exception's type and codes, never its message; or "cancelled" when the engine
/// ended it), how old the connection was and how long since the last message it received, on the wall clock, with how long
/// the computer slept when the two clocks part. The clocks are faked, so the ages are exact.
/// </summary>
public sealed class RelayDropLogTests
{
    private const string Marker = "the connection ended";

    /// <summary>What the client logged during a test: the drop lines, and every event of any kind.</summary>
    internal sealed class Captured
    {
        private readonly ConcurrentQueue<LogEvent> _lines = new();
        private readonly ConcurrentQueue<LogEvent> _all = new();

        public IReadOnlyCollection<LogEvent> Lines => _lines;

        public Action<LogEvent> Sink => e =>
        {
            _all.Enqueue(e);
            if (e.MessageTemplate.Text.Contains(Marker, StringComparison.Ordinal))
                _lines.Enqueue(e);
        };

        public void Deconstruct(out IReadOnlyCollection<LogEvent> lines, out Action<LogEvent> sink) => (lines, sink) = (Lines, Sink);

        /// <summary>No event the client logged carries <paramref name="secret"/>: not in its rendered text, its exception, or any property.</summary>
        public void AssertNowhere(string secret)
        {
            Assert.NotEmpty(_all);
            foreach (var e in _all)
            {
                Assert.DoesNotContain(secret, e.RenderMessage(), StringComparison.Ordinal);
                Assert.DoesNotContain(secret, e.MessageTemplate.Text, StringComparison.Ordinal);
                Assert.True(e.Exception is null || !e.Exception.ToString().Contains(secret, StringComparison.Ordinal),
                    $"An exception of an event carries the secret: {e.MessageTemplate.Text}");
                foreach (var property in e.Properties)
                    Assert.DoesNotContain(secret, property.Value.ToString(), StringComparison.Ordinal);
            }
        }
    }

    internal static Captured Capture() => new();

    internal static string Text(LogEvent e, string name) => ((ScalarValue)e.Properties[name]).Value!.ToString()!;

    private static double Seconds(LogEvent e, string name) => (double)((ScalarValue)e.Properties[name]).Value!;

    /// <summary>Connected at 0 s, a message at 40 s, and 20 s of silence: at 60 s the connection is 60 s old, 20 s since the last message.</summary>
    private static async Task<RelayPeer> ConnectedSilentForAsync(RelayRig rig)
    {
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(40));
        await FlushAsync(peer);
        rig.Clocks.Advance(TimeSpan.FromSeconds(20));
        return peer;
    }

    [Fact]
    public async Task AServerClose_LogsItsStatusAndADescriptionTheRelayIsKnownToSend_TheAgeAndTheTimeSinceTheLastMessage()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        await peer.CloseAsync(4409, "replaced");
        await rig.WaitForAsync(RelayState.Replaced);

        var line = Assert.Single(lines);
        Assert.Equal("closed 4409 (replaced)", Text(line, "Reason"));
        Assert.Equal(60, Seconds(line, "Age"));
        Assert.Equal(20, Seconds(line, "Idle"));
        Assert.False(line.Properties.ContainsKey("Asleep"));
        Assert.Contains("closed 4409 (replaced)", line.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerClose_WithAnyOtherDescription_LogsItsStatus_AndWithholdsTheDescription()
    {
        var captured = Capture();
        var (lines, sink) = captured;
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        await peer.CloseAsync(1001, "Bearer " + Credential);
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);

        captured.AssertNowhere(Credential);
        var line = Assert.Single(lines);
        Assert.Equal("closed 1001 (description withheld)", Text(line, "Reason"));
        Assert.DoesNotContain(Credential, line.RenderMessage(), StringComparison.Ordinal);
        Assert.Equal(60, Seconds(line, "Age"));
        Assert.Equal(20, Seconds(line, "Idle"));
    }

    [Fact]
    public async Task AFailure_LogsTheExceptionsTypeAndCodes_TheAgeAndTheTimeSinceTheLastMessage()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        peer.Abort();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);

        var line = Assert.Single(lines);
        Assert.Matches(@"^WebSocketException \([A-Za-z]+(, [A-Za-z]+)?, 0x[0-9A-F]{8}\)$", Text(line, "Reason"));
        Assert.Equal(60, Seconds(line, "Age"));
        Assert.Equal(20, Seconds(line, "Idle"));
    }

    [Fact]
    public async Task AFailure_WhoseMessageCarriesAnAddressWithACredential_LogsOnlyItsTypeAndCodes()
    {
        var captured = Capture();
        var (lines, sink) = captured;
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        // The receive loop reads the wall clock as the next message arrives: the failure is thrown there.
        rig.Clocks.FailNextWallRead = new WebSocketException(WebSocketError.Faulted, "couldn't reach wss://relay.example/svc?token=" + Credential);
        await peer.SendAsync(new JsonObject { ["type"] = "flush", ["id"] = "flush" });
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);

        var line = Assert.Single(lines);
        Assert.Matches(@"^WebSocketException \(Faulted, 0x[0-9A-F]{8}\)$", Text(line, "Reason"));
        captured.AssertNowhere(Credential);
        captured.AssertNowhere("relay.example");
    }

    [Fact]
    public async Task AFrameOver9MiB_WhoseCloseFails_StillLogsOneLine_WithTooBig()
    {
        var (lines, sink) = Capture();
        // The client logs the line, then closes with 1009. The client's test seam names the point between the two ("tooBig"):
        // the step throws there, as a close on a dead socket does, so the close is attempted and fails, every time.
        var reached = 0;
        await using var rig = await StartAsync(log: sink, options: new RelayOptions
        {
            EngineVersion = "2.0.0-test",
            LoopbackOnly = true,
            Step = step =>
            {
                if (step == "tooBig")
                {
                    Interlocked.Increment(ref reached);
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }
            },
        });
        var peer = await rig.ConnectAsync();

        // One byte over the limit: the client stops reading there.
        try
        {
            await peer.SendTextAsync(new string('x', RelayFrames.MaxFrameBytes + 1));
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // The client dropped the connection while the rest was still being sent.
        }

        // The failed close ends the connection: the client retries on its timer, and the relay saw no 1009.
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);
        Assert.NotEqual(WebSocketCloseStatus.MessageTooBig, await peer.Closed.WaitAsync(Limit, Ct));
        Assert.Equal(1, reached);
        Assert.Equal("too big", Text(Assert.Single(lines), "Reason"));
    }

    [Fact]
    public async Task ACancellation_LogsCancelled_TheAgeAndTheTimeSinceTheLastMessage()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        await ConnectedSilentForAsync(rig);

        rig.Client.SetEnabled(false);
        await rig.SettledAsync(RelayState.Off);

        var line = Assert.Single(lines);
        Assert.Equal("cancelled", Text(line, "Reason"));
        Assert.Equal(60, Seconds(line, "Age"));
        Assert.Equal(20, Seconds(line, "Idle"));
    }

    [Fact]
    public async Task AConnectionTheComputerSleptThrough_IsAgedOnTheWallClock_AndSaysHowLongItSlept()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        // An hour asleep: the wall clock moves on, the running clock (Stopwatch's, which macOS stops in sleep) doesn't.
        rig.Clocks.Sleep(TimeSpan.FromHours(1));
        peer.Abort();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);

        var line = Assert.Single(lines);
        Assert.Equal(3660, Seconds(line, "Age"));
        Assert.Equal(3620, Seconds(line, "Idle"));
        Assert.Equal(3600, Seconds(line, "Asleep"));
        Assert.Contains("asleep about 3600 s", line.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWallClockSetBack_ShowsTheRunningTime_AndNoSleep()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        var peer = await ConnectedSilentForAsync(rig);

        rig.Clocks.StepWall(TimeSpan.FromHours(-1));
        peer.Abort();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);

        var line = Assert.Single(lines);
        Assert.Equal(60, Seconds(line, "Age"));
        Assert.Equal(20, Seconds(line, "Idle"));
        Assert.False(line.Properties.ContainsKey("Asleep"));
    }

    [Fact]
    public async Task ADrop_LogsOnce_NotAgainWhileTheClientWaitsToConnect_AndAgainOnlyWithTheNextDrop_WithItsOwnAge()
    {
        var (lines, sink) = Capture();
        await using var rig = await StartAsync(log: sink);
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(50));

        peer.Abort();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit, Ct);
        var first = Assert.Single(lines);
        Assert.Equal(50, Seconds(first, "Age"));

        // The wait passes and the client connects again, at 51 s: still the one line, the new connection hasn't ended.
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        var second = await rig.Relay.NextPeerAsync(Ct);
        await second.ExpectAsync("hello", Ct);
        Assert.Single(lines);

        // Its drop 7 s later, before any message from the relay, is the second line, with its own age (the second wait is
        // twice the first).
        rig.Clocks.Advance(TimeSpan.FromSeconds(7));
        second.Abort();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(2)).WaitAsync(Limit, Ct);
        Assert.Equal(2, lines.Count);
        var last = lines.Last();
        Assert.Equal(7, Seconds(last, "Age"));
        Assert.Equal(7, Seconds(last, "Idle"));
    }
}
