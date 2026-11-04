using System.Text.Json.Nodes;
using InfoPanel.Service;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// Never late (docs/2.0/relay.md): a call is refused unrun when it was sent over 10 s ago on the relay's clock, read by a
/// <c>time</c> challenge that counts only when it came back within 2 s on both of the engine's clocks; a reading is
/// renewed when over 30 s old on either, and void once the wall clock is stepped; and a call's commit deadline runs on
/// both clocks, so neither a sleep nor a step extends it. Both clocks are faked (<see cref="TwoClocks"/>): a sleep moves
/// the wall clock alone, as macOS stops Stopwatch in sleep.
/// </summary>
public sealed class RelayTimeTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public async Task ACallHeldAcrossASleep_IsRefusedLate_WithTheWelcomeHeldAcrossItToo()
    {
        await using var rig = await StartAsync();
        var peer = await rig.Relay.NextPeerAsync(Ct);
        await peer.ExpectAsync("hello", Ct);

        // The relay welcomed the engine and sent a call 1 s later; the computer slept for an hour before either arrived.
        var sentAt = rig.Clocks.RelayNow + 1000;
        rig.Clocks.Sleep(Hour);
        await peer.SendAsync(new JsonObject { ["type"] = "welcome" });
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        await peer.SendAsync(rig.Call("held", "add_item", sentAt: sentAt));
        Assert.Equal("""{"type":"late","id":"held"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(rig.FakeTools!.Calls);

        // A call sent now runs.
        await peer.SendAsync(rig.Call("fresh", "list_profiles"));
        Assert.Equal("fresh", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATimeAnswerHeldAcrossASleep_DoesntCount_TheEngineAsksAgain_AndTheCallIsLate()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(31));

        // A call arrives; the engine asks the time; the relay answers at once, but the computer sleeps before the answer
        // arrives, and the relay sends a second call during the sleep.
        await peer.SendAsync(rig.Call("before", "add_item"));
        var question = await peer.ExpectAsync("time", Ct);
        var answeredAt = rig.Clocks.RelayNow;
        rig.Clocks.Sleep(Hour / 2);
        var duringSleep = rig.Clocks.RelayNow;
        rig.Clocks.Sleep(Hour / 2);
        await peer.SendAsync(new JsonObject { ["type"] = "time", ["nonce"] = question["nonce"]!.GetValue<string>(), ["now"] = answeredAt });
        await peer.SendAsync(rig.Call("during", "add_item", sentAt: duringSleep));

        // The held answer came back an hour later on the wall clock: void. The engine asks again.
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        var answers = new[] { await peer.NextAsync(Ct), await peer.NextAsync(Ct) }.Select(f => f.ToJsonString()).Order().ToList();
        Assert.Equal(["""{"type":"late","id":"before"}""", """{"type":"late","id":"during"}"""], answers);
        Assert.Empty(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AReadingOlderThan30sOnEitherClock_IsRenewedBeforeACall()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();

        // 29 s old: used as it is.
        rig.Clocks.Advance(TimeSpan.FromSeconds(29));
        await peer.SendAsync(rig.Call("c1", "list_profiles"));
        Assert.Equal("c1", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());

        // Over 30 s on both clocks: a fresh reading before the call runs.
        rig.Clocks.Advance(TimeSpan.FromSeconds(2));
        await peer.SendAsync(rig.Call("c2", "list_profiles"));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("c2", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());

        // Over 30 s on the wall clock alone (a sleep): renewed too.
        rig.Clocks.Sleep(TimeSpan.FromSeconds(31));
        await peer.SendAsync(rig.Call("c3", "list_profiles"));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("c3", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
        Assert.Equal(3, rig.FakeTools!.Calls.Count);
    }

    [Fact]
    public async Task AReadingIsVoidedByABackwardWallStep_TheCallSentAtT1AndDeliveredAtT26IsLate()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        var sentAt = rig.Clocks.RelayNow + 1000;

        // 26 s pass; meanwhile the wall clock is set back 60 s. The reading is 26 s old on Stopwatch, -34 s on the wall clock.
        rig.Clocks.Advance(TimeSpan.FromSeconds(26));
        rig.Clocks.StepWall(TimeSpan.FromSeconds(-60));
        await peer.SendAsync(rig.Call("t26", "add_item", sentAt: sentAt));

        // Void: the engine asks again, and the relay's clock says 25 s have passed since the call was sent.
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("""{"type":"late","id":"t26"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AWallStepJustAfterTheReadingIsChecked_DoesntReachTheCall_SentAtT1AndDeliveredAtT26ItIsLate()
    {
        // The wall clock is set back 60 s just after the call's reading was checked (good: 26 s old, both clocks agreeing).
        RelayRig? started = null;
        var stepped = 0;
        await using var rig = started = await StartAsync(options: new RelayOptions
        {
            EngineVersion = "2.0.0-test",
            LoopbackOnly = true,
            Step = step =>
            {
                if (step == "checked" && Interlocked.Exchange(ref stepped, 1) == 0)
                    started!.Clocks.StepWall(TimeSpan.FromSeconds(-60));
            },
        });
        var peer = await rig.ConnectAsync();
        var sentAt = rig.Clocks.RelayNow + 1000;
        rig.Clocks.Advance(TimeSpan.FromSeconds(26));

        // What the reading was checked against decides: the relay's clock says the call was sent 25 s ago.
        await peer.SendAsync(rig.Call("t26", "add_item", sentAt: sentAt));
        Assert.Equal("""{"type":"late","id":"t26"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Equal(1, stepped);
        Assert.Empty(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AWallStepBackJustAfterTheReadingIsChecked_DoesntGiveTheCallMoreTime()
    {
        // A call sent 5 s before it arrives has 55 s left, by the clocks as its reading was checked; the wall clock set
        // back 30 s just after gives it no more: the 55 s run out on Stopwatch.
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        RelayRig? started = null;
        var stepped = 0;
        await using var rig = started = await StartAsync(tools, new RelayOptions
        {
            EngineVersion = "2.0.0-test",
            LoopbackOnly = true,
            Step = step =>
            {
                if (step == "checked" && Interlocked.Exchange(ref stepped, 1) == 0)
                    started!.Clocks.StepWall(TimeSpan.FromSeconds(-30));
            },
        });
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(rig.Call("back", "add_item", sentAt: rig.Clocks.RelayNow - 5000));
        var back = await CutoffOf(tools);
        rig.Clocks.AdvanceMonotonic(TimeSpan.FromSeconds(54));
        Assert.False(back.IsTripped);
        rig.Clocks.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.True(back.IsTripped);
        Assert.Equal("deadline", back.Reason);

        tools.Hold.SetResult();
        await peer.ExpectAsync("result", Ct);
    }

    [Fact]
    public async Task ACallJoiningAReadingInFlight_ChecksItWhenItComes_SoAWallStepMeanwhileVoidsIt()
    {
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await StartAsync(options: new RelayOptions
        {
            EngineVersion = "2.0.0-test",
            LoopbackOnly = true,
            Step = step =>
            {
                if (step == "joined")
                    joined.TrySetResult();
            },
        });

        // The first reading's answer arrives at T=0 and is good, but its continuation is held (in the disposal of the
        // reading's 2 s timer), before the reading is kept.
        using var hold = rig.Clocks.HoldDispose(RelayClock.ReadingRoundTrip);
        var peer = await rig.ConnectAsync();
        await hold.Disposing.WaitAsync(Limit, Ct);

        // T=26, the wall clock set back 60 s; a call sent at T=1 arrives and joins the reading still in flight, before the
        // reading is let go.
        var sentAt = rig.Clocks.RelayNow + 1000;
        rig.Clocks.Advance(TimeSpan.FromSeconds(26));
        rig.Clocks.StepWall(TimeSpan.FromSeconds(-60));
        await peer.SendAsync(rig.Call("joined", "add_item", sentAt: sentAt));
        await joined.Task.WaitAsync(Limit, Ct);
        hold.Release();

        // The reading it gets is checked as it comes: void (the wall clock went back), so the engine asks again, and the
        // relay's clock says the call was sent 25 s ago.
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("""{"type":"late","id":"joined"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AForwardWallStep_VoidsTheReadingToo()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(5));
        rig.Clocks.StepWall(TimeSpan.FromSeconds(2));
        await peer.SendAsync(rig.Call("c", "list_profiles"));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("c", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACallWithoutAReading_AfterTheReadingIsAskedTwice_IsLate()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(31));
        await peer.SendAsync(rig.Call("c", "list_profiles"));

        // Unanswered: asked once more after 2 s, then refused.
        await peer.ExpectAsync("time", Ct);
        await rig.Clocks.TimerArmedAsync(RelayClock.ReadingRoundTrip).WaitAsync(RelayRig.Limit, Ct);
        rig.Clocks.Advance(RelayClock.ReadingRoundTrip);
        await peer.ExpectAsync("time", Ct);
        await rig.Clocks.TimerArmedAsync(RelayClock.ReadingRoundTrip).WaitAsync(RelayRig.Limit, Ct);
        rig.Clocks.Advance(RelayClock.ReadingRoundTrip);
        Assert.Equal("""{"type":"late","id":"c"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(rig.FakeTools!.Calls);
    }

    [Fact]
    public async Task AnAnswerWithAnotherNonce_IsntAReading()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        rig.Clocks.Advance(TimeSpan.FromSeconds(31));
        await peer.SendAsync(rig.Call("c", "list_profiles"));
        await peer.ExpectAsync("time", Ct);
        await peer.SendAsync(new JsonObject { ["type"] = "time", ["nonce"] = "not-the-nonce", ["now"] = rig.Clocks.RelayNow });
        await FlushAsync(peer);
        Assert.Empty(rig.FakeTools!.Calls);
        await rig.Clocks.TimerArmedAsync(RelayClock.ReadingRoundTrip).WaitAsync(RelayRig.Limit, Ct);
        rig.Clocks.Advance(RelayClock.ReadingRoundTrip);
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.Equal("c", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCommitDeadline_RunsOnBothClocks_SoNeitherAStepNorASleepExtendsIt()
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();

        // The wall clock set back an hour: Stopwatch still runs the 60 s out.
        await peer.SendAsync(rig.Call("stepped", "add_item"));
        var stepped = await CutoffOf(tools);
        rig.Clocks.StepWall(-Hour);
        rig.Clocks.AdvanceMonotonic(TimeSpan.FromSeconds(59));
        Assert.False(stepped.IsTripped);
        rig.Clocks.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.True(stepped.IsTripped);
        Assert.Equal("deadline", stepped.Reason);

        // A sleep (Stopwatch stopped): the wall clock runs it out.
        rig.Clocks.StepWall(Hour);
        await peer.SendAsync(rig.Call("slept", "add_item"));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        var slept = await CutoffOf(tools);
        rig.Clocks.Sleep(TimeSpan.FromSeconds(59));
        Assert.False(slept.IsTripped);
        rig.Clocks.Sleep(TimeSpan.FromSeconds(1));
        Assert.True(slept.IsTripped);

        // Set from the relay's clock: a call sent 5 s before it arrived has 55 s left.
        await peer.SendAsync(rig.Call("sent-earlier", "add_item", sentAt: rig.Clocks.RelayNow - 5000));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        var earlier = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(54));
        Assert.False(earlier.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(earlier.IsTripped);

        tools.Hold.SetResult();
        for (var i = 0; i < 3; i++)
            await peer.ExpectAsync("result", Ct);
    }

    [Fact]
    public async Task ACallSentAheadOfTheRelaysClock_RunsWithAtMost60s()
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();

        // Sent "10 minutes from now" on the relay's clock: not late, and its deadline is still 60 s, not 11 minutes.
        await peer.SendAsync(rig.Call("ahead", "add_item", sentAt: rig.Clocks.RelayNow + 600_000));
        var ahead = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(59));
        Assert.False(ahead.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(ahead.IsTripped);
        Assert.Equal("deadline", ahead.Reason);

        tools.Hold.SetResult();
        await peer.ExpectAsync("result", Ct);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task ASentAtNoTimeHolds_IsAnsweredMalformed_AndNeverRuns(long sentAt)
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        await peer.SendTextAsync($$"""{"type":"call","id":"extreme","tool":"add_item","arguments":{},"sentAt":{{sentAt}}}""");
        Assert.Equal("""{"type":"error","code":"malformed","id":"extreme"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(rig.FakeTools!.Calls);

        // Still open: a call sent now runs.
        await peer.SendAsync(rig.Call("now", "list_profiles"));
        Assert.Equal("now", (await peer.ExpectAsync("result", Ct))["id"]!.GetValue<string>());
    }

    private static async Task<RelayCutoff> CutoffOf(FakeToolHost tools)
    {
        var id = await tools.Started.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        return tools.Calls.Single(c => c.Context.Id == id).Context.Cutoff;
    }
}
