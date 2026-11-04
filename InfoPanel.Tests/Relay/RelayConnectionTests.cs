using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InfoPanel.Service;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// The connection AI apps elsewhere use (docs/2.0/relay.md), against a fake relay on a loopback port: the upgrade's
/// answers, the close codes, the waits (clocks faked: a wait is a timer the test sees armed, then passes), keepalive,
/// <c>bye</c>, redirects, and admission.
/// </summary>
public sealed class RelayConnectionTests
{
    [Fact]
    public async Task Connects_WithTheCredentialOnlyInTheUpgrade_SaysHello_AndIsConnectedOnWelcome()
    {
        await using var rig = await StartAsync();
        var peer = await rig.Relay.NextPeerAsync(Ct);
        var hello = await peer.ExpectAsync("hello", Ct);
        Assert.Equal(1, hello["protocol"]!.GetValue<int>());
        Assert.Equal("2.0.0-test", hello["engineVersion"]!.GetValue<string>());
        // What the engine takes beyond the protocol: designs the service produced (docs/2.0/relay.md, "Designs").
        Assert.Equal("""["design"]""", hello["features"]!.ToJsonString());
        Assert.Equal(RelayState.Connecting, rig.Client.State);
        Assert.Equal(["Bearer " + Credential], rig.Relay.Authorizations);
        Assert.Equal([RelayOptions.DefaultPath], rig.Relay.Paths);

        await peer.SendAsync(new JsonObject { ["type"] = "welcome" });
        // After welcome the engine reads the relay's clock: a nonce of 128 random bits, base64url.
        var time = await peer.ExpectAsync("time", Ct);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", time["nonce"]!.GetValue<string>());
        await rig.WaitForAsync(RelayState.Connected);
        // The credential is never in a frame.
        Assert.DoesNotContain(Credential, hello.ToJsonString() + time.ToJsonString());
    }

    [Fact]
    public async Task Admission_DispatchesOnlyItsTypes_AnythingElseIsAnsweredAndNothingCloses()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();

        // An unknown type, with its id echoed; another without one.
        await peer.SendAsync(new JsonObject { ["type"] = "open", ["id"] = "a1", ["path"] = "/api/settings" });
        Assert.Equal("""{"type":"error","code":"unsupported","id":"a1"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendAsync(new JsonObject { ["type"] = "request", ["method"] = "PATCH", ["path"] = "/api/settings" });
        Assert.Equal("""{"type":"error","code":"unsupported"}""", (await peer.NextAsync(Ct)).ToJsonString());
        // An id that isn't base64url is never echoed.
        await peer.SendAsync(new JsonObject { ["type"] = "open", ["id"] = "<script>" });
        Assert.Equal("""{"type":"error","code":"unsupported"}""", (await peer.NextAsync(Ct)).ToJsonString());

        // A binary frame, text that isn't JSON, JSON without a type, a call missing what it needs, one nested too deep.
        await peer.SendBinaryAsync([1, 2, 3]);
        Assert.Equal("""{"type":"error","code":"unsupported"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync("not json");
        Assert.Equal("""{"type":"error","code":"malformed"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync("""{"id":"x"}""");
        Assert.Equal("""{"type":"error","code":"malformed"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendSendAtLessCallAsync();
        Assert.Equal("""{"type":"error","code":"malformed","id":"c0"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync("""{"type":"call","id":"c1","tool":"list_profiles","sentAt":1,"arguments":""" + new string('[', 300) + new string(']', 300) + "}");
        Assert.Equal("""{"type":"error","code":"malformed"}""", (await peer.NextAsync(Ct)).ToJsonString());

        // Still open: a call runs and answers.
        await peer.SendAsync(rig.Call("c2", "list_profiles"));
        var result = await peer.ExpectAsync("result", Ct);
        Assert.Equal("c2", result["id"]!.GetValue<string>());
        Assert.Equal("ran list_profiles", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(RelayState.Connected, rig.Client.State);
        Assert.Single(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task TextThatDoesntDecode_IsAnsweredMalformed_AndNothingCloses()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();

        // Lone surrogates in the type, in an id, in a tool's name and in the arguments.
        await peer.SendTextAsync("""{"type":"\uD800"}""");
        Assert.Equal("""{"type":"error","code":"malformed"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync("""{"type":"cancel","id":"\uDC00"}""");
        Assert.Equal("""{"type":"error","code":"malformed"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync("""{"type":"call","id":"c1","tool":"\uD800","sentAt":1}""");
        Assert.Equal("""{"type":"error","code":"malformed","id":"c1"}""", (await peer.NextAsync(Ct)).ToJsonString());
        await peer.SendTextAsync($$$"""{"type":"call","id":"c2","tool":"get_profile","sentAt":{{{rig.Clocks.RelayNow}}},"arguments":{"profile":"\uD800"}}""");
        Assert.Equal("""{"type":"error","code":"malformed","id":"c2"}""", (await peer.NextAsync(Ct)).ToJsonString());

        // Still open: a call runs and answers.
        await peer.SendAsync(rig.Call("c3", "list_profiles"));
        Assert.Equal("c3", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
        Assert.Equal(RelayState.Connected, rig.Client.State);
        Assert.Single(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AFrameOver9MiB_ClosesWith1009_AndTheEngineConnectsAgainAfterItsBackoff()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        var big = "{\"type\":\"note\",\"text\":\"" + new string('x', RelayFrames.MaxFrameBytes) + "\"}";
        await peer.SendTextAsync(big);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(RelayRig.Limit, Ct);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        await rig.ConnectAsync();
    }

    [Fact]
    public async Task Unauthorized_MarksTheCredentialRejected_AndNeverRetries_UntilAnotherIsProvided()
    {
        await using var rig = await StartAsync(before: r => r.Relay.Upgrade = context =>
        {
            if (context.Request.Headers.Authorization.ToString() != "Bearer replacement")
            {
                context.Response.StatusCode = 401;
                return true;
            }

            return false;
        });

        await rig.SettledAsync(RelayState.Off);
        Assert.Equal(ServiceCredentialState.Rejected, rig.Credentials.Status.Credential);
        Assert.Equal(0, rig.Clocks.Armed); // no retry waiting
        rig.Clocks.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, rig.Relay.Requests);

        rig.Credentials.Put("replacement");
        var peer = await rig.ConnectAsync();
        Assert.Equal(2, rig.Relay.Requests);
        Assert.Equal("Bearer replacement", rig.Relay.Authorizations.Last());
        Assert.False(peer.HasFrame);
    }

    [Fact]
    public async Task Close4401_MarksTheCredentialRejected()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        await peer.CloseAsync(RelayClient.ClosedRemoved);
        await rig.SettledAsync(RelayState.Off);
        Assert.Equal(ServiceCredentialState.Rejected, rig.Credentials.Status.Credential);
        Assert.Equal(0, rig.Clocks.Armed);
    }

    [Fact]
    public async Task Close4409_IsReplaced_WithNoReconnect_UntilTheSettingIsTurnedOffAndOn()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        await peer.CloseAsync(RelayClient.ClosedReplaced);
        await rig.SettledAsync(RelayState.Replaced);
        Assert.Equal(0, rig.Clocks.Armed);
        rig.Clocks.Advance(TimeSpan.FromHours(1));
        rig.Client.NetworkChanged();
        rig.Client.ConfigChanged();
        await rig.SettledAsync(RelayState.Replaced);
        Assert.Equal(1, rig.Relay.Requests);
        Assert.False(rig.Relay.HasPeer);

        rig.Client.SetEnabled(false);
        await rig.SettledAsync(RelayState.Off);
        rig.Client.SetEnabled(true);
        await rig.ConnectAsync();
        Assert.Equal(2, rig.Relay.Requests);
    }

    [Fact]
    public async Task Close4426_IsOutdated_WithNoReconnect_EvenTurnedOffAndOn()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        await peer.CloseAsync(RelayClient.ClosedOutdated);
        await rig.SettledAsync(RelayState.Outdated);
        Assert.Equal(0, rig.Clocks.Armed);

        rig.Client.SetEnabled(false);
        await rig.SettledAsync(RelayState.Off);
        rig.Client.SetEnabled(true);
        await rig.SettledAsync(RelayState.Outdated);
        rig.Clocks.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, rig.Relay.Requests);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task TooManyRequests_WaitsForRetryAfter_ANetworkChangeDoesntCutItShort(int status)
    {
        await using var rig = await StartAsync(before: r => r.Relay.Upgrade = context =>
        {
            if (r.Relay.Requests > 1)
                return false;
            context.Response.StatusCode = status;
            context.Response.Headers.RetryAfter = "120";
            return true;
        });

        await rig.WaitForAsync(RelayState.Waiting);
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(120)).WaitAsync(RelayRig.Limit, Ct);
        rig.Client.NetworkChanged();
        rig.Clocks.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(1, rig.Relay.Requests);
        Assert.Equal(RelayState.Waiting, rig.Client.State);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        await rig.ConnectAsync();
        Assert.Equal(2, rig.Relay.Requests);
    }

    [Fact]
    public async Task Close4429_WaitsTenMinutes()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        await peer.CloseAsync(RelayClient.ClosedWait);
        await rig.WaitForAsync(RelayState.Waiting);
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromMinutes(10)).WaitAsync(RelayRig.Limit, Ct);
        rig.Clocks.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Relay.Requests);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        await rig.ConnectAsync();
    }

    [Fact]
    public async Task Failures_BackOffFrom1sDoublingTo60s_AndANetworkChangeCutsAWaitShort()
    {
        await using var rig = await StartAsync(before: r => r.Relay.Upgrade = context =>
        {
            context.Response.StatusCode = 500;
            return true;
        });

        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(seconds)).WaitAsync(RelayRig.Limit, Ct);
            var attempts = rig.Relay.Requests;
            Assert.Equal(RelayState.Connecting, rig.Client.State);
            rig.Clocks.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            Assert.Equal(attempts, rig.Relay.Requests);
            rig.Clocks.Advance(TimeSpan.FromMilliseconds(1));
        }

        // A network change: tried again at once, and the backoff starts again from 1 s.
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(60)).WaitAsync(RelayRig.Limit, Ct);
        var before = rig.Relay.Requests;
        rig.Client.NetworkChanged();
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(RelayRig.Limit, Ct);
        Assert.Equal(before + 1, rig.Relay.Requests);
    }

    [Theory]
    [InlineData(0.0, 800)]
    [InlineData(1.0, 1200)]
    public async Task TheBackoffsJitter_IsFrom80To120PercentOfIt(double jitter, int milliseconds)
    {
        await using var rig = await StartAsync(jitter: jitter, before: r => r.Relay.Upgrade = context =>
        {
            context.Response.StatusCode = 502;
            return true;
        });

        await rig.Clocks.TimerArmedAsync(TimeSpan.FromMilliseconds(milliseconds)).WaitAsync(RelayRig.Limit, Ct);
    }

    [Fact]
    public async Task KeepAlive_Pings20sWithA10sTimeout_AndADeadLinkIsDropped()
    {
        var defaults = new RelayOptions { EngineVersion = "x" };
        Assert.Equal((TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)), (defaults.KeepAliveInterval, defaults.KeepAliveTimeout));

        // A relay that never reads answers no ping: the engine's keepalive drops the link and it backs off to connect again.
        await using var rig = await StartAsync(options: new RelayOptions
        {
            EngineVersion = "2.0.0-test",
            LoopbackOnly = true,
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            KeepAliveTimeout = TimeSpan.FromMilliseconds(100),
        }, before: r => r.Relay.Silent = true);
        var peer = await rig.Relay.NextPeerAsync(Ct);
        await rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)).WaitAsync(RelayRig.Limit, Ct);
        Assert.Equal(RelayState.Connecting, rig.Client.State);
        peer.StartReading();
    }

    [Fact]
    public async Task TurningItOff_SendsBye_ClosesNormally_AndConnectsNoMore()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Client.SetEnabled(false);
        await peer.ExpectAsync("bye", Ct);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        Assert.Equal("bye", peer.CloseDescription);
        await rig.SettledAsync(RelayState.Off);
        rig.Clocks.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, rig.Relay.Requests);
    }

    [Fact]
    public async Task AnotherCredential_ClosesTheConnection_WithoutBye_AndConnectsWithTheNewOne()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Credentials.Put("another");
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        Assert.True(string.IsNullOrEmpty(peer.CloseDescription));
        Assert.False(peer.HasFrame); // no bye: the relay keeps this computer
        await rig.ConnectAsync();
        Assert.Equal("Bearer another", rig.Relay.Authorizations.Last());

        // Removing it closes the connection too, and none is made.
        var current = rig.Relay.Requests;
        rig.Credentials.Delete();
        await rig.SettledAsync(RelayState.Off);
        Assert.Equal(current, rig.Relay.Requests);
    }

    [Fact]
    public async Task AnAddressChange_WhoseSettingsWriteFails_AfterTheCredentialWasRemoved_ClosesTheConnection()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();

        // The settings' change of address removes the credential first (Changing), then its write fails: the address
        // stays, and neither AddressChanged nor the settings' Changed is raised.
        using (rig.Credentials.AddressChanging("http://127.0.0.1:9"))
        {
        }

        Assert.Equal(ServiceCredentialState.Absent, rig.Credentials.Status.Credential);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await peer.Closed.WaitAsync(RelayRig.Limit, Ct));
        await rig.SettledAsync(RelayState.Off);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ARedirect_IsNeverFollowed_SoTheCredentialNeverGoesThere(int status)
    {
        await using var elsewhere = await FakeRelay.StartAsync();
        await using var rig = await StartAsync(before: r => r.Relay.Upgrade = context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = elsewhere.Address + RelayOptions.DefaultPath;
            return true;
        });

        // Either the engine backs off (the redirect was its answer), or it reached the other address.
        await Task.WhenAny(rig.Clocks.TimerArmedAsync(TimeSpan.FromSeconds(1)), elsewhere.NextPeerAsync(Ct)).WaitAsync(RelayRig.Limit, Ct);
        Assert.Equal(0, elsewhere.Requests);
        Assert.DoesNotContain(elsewhere.Authorizations, a => a?.Contains(Credential) == true);
        Assert.Equal(1, rig.Relay.Requests);
        Assert.Equal(RelayState.Connecting, rig.Client.State);
    }

    [Fact]
    public async Task Offline_AnAddressOffThisComputer_IsNeverConnectedTo()
    {
        await using var rig = await StartAsync(address: _ => "https://service.example");
        await rig.SettledAsync(RelayState.Off);
        Assert.Equal(0, rig.Relay.Requests);
        Assert.Equal(ServiceCredentialState.Present, rig.Credentials.Status.Credential);
    }

    [Fact]
    public async Task NoCredential_OrTheSettingOff_ConnectsNot()
    {
        await using var off = await StartAsync(enabled: false);
        await off.SettledAsync(RelayState.Off);
        await using var none = await StartAsync(address: _ => null);
        await none.SettledAsync(RelayState.Off);
        Assert.Equal(0, off.Relay.Requests + none.Relay.Requests);
    }

    [Fact]
    public async Task AtMost8CallsRunAtOnce_ANinthAnswersBusy()
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();
        for (var i = 0; i < 8; i++)
        {
            await peer.SendAsync(rig.Call($"c{i}", "list_profiles"));
            await tools.Started.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        }

        await peer.SendAsync(rig.Call("c8", "list_profiles"));
        Assert.Equal("""{"type":"error","code":"busy","id":"c8"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Equal(8, rig.Client.Running);

        tools.Hold.SetResult();
        var answered = new HashSet<string>();
        for (var i = 0; i < 8; i++)
            answered.Add((await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
        Assert.Equal(8, answered.Count);
    }
}

/// <summary>
/// Closing, with calls in flight: the relay's close with one result's send stalled and others queued behind it, the
/// engine's own end with the relay's close answer withheld, and an old connection's close after the setting was turned
/// off and on.
/// </summary>
public sealed class RelayClosingTests
{
    [Fact]
    public async Task TheRelayClosing_WithAResultsSendStalledAndOthersQueued_EndsEveryCall_AndShutdownIsBounded()
    {
        var tools = new AnsweredByTest();
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();
        string[] large = ["large-1", "large-2", "large-3"];
        foreach (var id in large.Append("small"))
        {
            await peer.SendAsync(rig.Call(id, "list_profiles"));
            await tools.Started.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        }

        Assert.Equal(4, rig.Client.Running);

        // The relay stops reading. Three answers of 8 MiB each, more than the connection's buffers hold, then a small one:
        // each answer's send starts on this thread, so by the last one a send is stalled and the others wait behind it.
        peer.PauseReading();
        var text = new string('x', 8 * 1024 * 1024);
        foreach (var id in large)
            tools.Answer(id, text);
        tools.Answer("small", "small");
        Assert.Equal(4, rig.Client.Running);

        // The relay closes. Every call ends (nothing left holding the engine's capacity), and stopping the engine's side
        // doesn't wait on a send that can't finish.
        await peer.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
        await rig.Client.DisposeAsync().AsTask().WaitAsync(Limit, Ct);
        Assert.Equal(0, rig.Client.Running);
        peer.ResumeReading();
    }

    [Theory]
    [InlineData("turned off")]
    [InlineData("credential removed")]
    public async Task EndingTheConnection_TripsEveryCallsCutoffAtOnce_WithoutWaitingForTheRelaysCloseAnswer(string how)
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();
        await peer.SendAsync(rig.Call("edit", "add_item"));
        var id = await tools.Started.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        var cutoff = tools.Calls.Single(c => c.Context.Id == id).Context.Cutoff;

        // The relay won't answer the engine's close: the connection stays open, closing.
        peer.PauseReading();
        if (how == "turned off")
            rig.Client.SetEnabled(false);
        else
            rig.Credentials.Delete();

        // An edit paused before its write can't make it now, while the close is still unanswered.
        Assert.True(cutoff.IsTripped);
        Assert.Equal("closed", cutoff.Reason);

        peer.ResumeReading();
        tools.Hold.SetResult();
        await rig.SettledAsync(RelayState.Off);
    }

    [Fact]
    public async Task AnOldConnections4409_ArrivingAfterTheSettingWasTurnedOffAndOn_DoesntStopTheNewOne()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();

        // Turned off and on while the old connection is still closing (the relay hasn't answered its close)...
        peer.PauseReading();
        rig.Client.SetEnabled(false);
        rig.Client.SetEnabled(true);

        // ...which then answers 4409: that was about the old connection; the engine connects again.
        await peer.CloseAsync(RelayClient.ClosedReplaced);
        await rig.ConnectAsync();
        Assert.Equal(RelayState.Connected, rig.Client.State);
        Assert.Equal(2, rig.Relay.Requests);
    }

    [Fact]
    public async Task TheSettingTurnedOffAndOn_AsA4409IsTakenIn_StartsANewConnection()
    {
        // The setting is turned off and on just as the 4409 is taken in: from the line it logs (a sink that writes
        // synchronously, on the client's thread), after the 4409 was found to be about the connection in effect.
        RelayRig? started = null;
        var turned = 0;
        await using var rig = started = await StartAsync(log: e =>
        {
            if (e.MessageTemplate.Text.Contains("another engine took over", StringComparison.Ordinal) && Interlocked.Exchange(ref turned, 1) == 0)
            {
                started!.Client.SetEnabled(false);
                started.Client.SetEnabled(true);
            }
        });
        var peer = await rig.ConnectAsync();

        // The turn came after the 4409: it clears it, and the engine connects again.
        await peer.CloseAsync(RelayClient.ClosedReplaced);
        await rig.ConnectAsync();
        Assert.Equal(1, turned);
        Assert.Equal(RelayState.Connected, rig.Client.State);
        Assert.Equal(2, rig.Relay.Requests);
    }

    /// <summary>
    /// A tool host whose calls each answer when the test says (<see cref="Answer"/>): the answer completes the call's task
    /// on the test's thread, so what the relay client does with it until its first wait has happened by the time
    /// <see cref="Answer"/> returns.
    /// </summary>
    private sealed class AnsweredByTest : IRelayToolHost
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayToolAnswer>> _pending = new();

        public Channel<string> Started { get; } = Channel.CreateUnbounded<string>();

        public Task<RelayToolAnswer> CallAsync(string tool, JsonElement arguments, RelayCallContext context, CancellationToken ct)
        {
            // Continuations run on the thread that answers.
            var answer = new TaskCompletionSource<RelayToolAnswer>();
            _pending[context.Id] = answer;
            Started.Writer.TryWrite(context.Id);
            return answer.Task;
        }

        public void Answer(string id, string text) => _pending[id].SetResult(new RelayToolAnswer(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = false,
        }, Cancelled: false));
    }
}

internal static class RelayPeerCalls
{
    /// <summary>A call frame without <c>sentAt</c>.</summary>
    public static Task SendSendAtLessCallAsync(this RelayPeer peer) =>
        peer.SendTextAsync("""{"type":"call","id":"c0","tool":"list_profiles","arguments":{}}""");
}
