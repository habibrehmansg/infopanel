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

        // Sent "10 minutes from now" on the relay's clock: not late, and its deadline is still 60 s, not 11 minutes. Its
        // sentAt contradicts the reading, so the engine reads the relay's clock again, which still says now.
        await peer.SendAsync(rig.Call("ahead", "add_item", sentAt: rig.Clocks.RelayNow + 600_000));
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        var ahead = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(59));
        Assert.False(ahead.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(ahead.IsTripped);
        Assert.Equal("deadline", ahead.Reason);

        tools.Hold.SetResult();
        await peer.ExpectAsync("result", Ct);
    }

    [Fact]
    public async Task ACallsDeadline_ShortensItsTime_ButNeverExtendsItPastSentAtAnd60s()
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();

        // A deadline 20 s after it was sent: the call's change has 20 s, not 60.
        var shortened = rig.Call("shortened", "add_item");
        shortened["deadline"] = rig.Clocks.RelayNow + 20_000;
        await peer.SendAsync(shortened);
        var early = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(19));
        Assert.False(early.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(early.IsTripped);
        Assert.Equal("deadline", early.Reason);

        // One 10 minutes after it was sent, 5 s before it arrived: what is left of sentAt + 60 s, 55 s, not 60 or 10
        // minutes.
        var later = rig.Call("later", "add_item", sentAt: rig.Clocks.RelayNow - 5000);
        later["deadline"] = rig.Clocks.RelayNow - 5000 + 600_000;
        await peer.SendAsync(later);
        var extended = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(54));
        Assert.False(extended.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(extended.IsTripped);

        // One already past as it arrives: no time at all for a change, though a read may still answer.
        var passed = rig.Call("passed", "add_item", sentAt: rig.Clocks.RelayNow - 3000);
        passed["deadline"] = rig.Clocks.RelayNow - 1000;
        await peer.SendAsync(passed);
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        Assert.True((await CutoffOf(tools)).IsTripped);

        tools.Hold.SetResult();
        for (var i = 0; i < 3; i++)
            await peer.ExpectAsync("result", Ct);
    }

    [Fact]
    public async Task ACallsShortenedDeadline_HoldsWhenOnlyTheRelaysClockMoved()
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();

        // The relay's clock stepped 60 s ahead of the reading still in use: a call sent then, with a deadline 20 s after
        // it was sent, has 20 s, not the 60 its timestamps would give on the reading. Its sentAt contradicts the reading,
        // so the engine reads the relay's clock again; the relay answers with the clock the reading had: still 20 s.
        var sentAt = rig.Clocks.RelayNow + 60_000;
        var stepped = rig.Call("stepped", "add_item", sentAt: sentAt);
        stepped["deadline"] = sentAt + 20_000;
        await peer.SendAsync(stepped);
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow, Ct);
        var cutoff = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(19));
        Assert.False(cutoff.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cutoff.IsTripped);
        Assert.Equal("deadline", cutoff.Reason);

        tools.Hold.SetResult();
        await peer.ExpectAsync("result", Ct);
    }

    /// <summary>
    /// The relay's clock stepped 60 s ahead of the reading the engine holds, and the call took 5 s to arrive: its
    /// <c>sentAt</c> is ahead of the relay's clock by that reading, which it contradicts, so the engine reads the relay's
    /// clock again and the call gets what is left of its absolute cutoff on it: 15 s of a deadline 20 s after it was sent,
    /// 55 s of <c>sentAt</c> + 60 s; never the 20 or 60 s the stale reading would give.
    /// </summary>
    [Theory]
    [InlineData(20_000, 15)]
    [InlineData(null, 55)]
    public async Task ACallWhoseSentAtContradictsTheReading_ReadsTheRelaysClockAgain_AndKeepsItsAbsoluteCutoffPastADeliveryDelay(int? deadline, int left)
    {
        var tools = new FakeToolHost { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var rig = await StartAsync(tools);
        var peer = await rig.ConnectAsync();

        // From here on the relay's clock is 60 s ahead of the reading; the call is sent, and arrives 5 s later.
        const long Stepped = 60_000;
        var sentAt = rig.Clocks.RelayNow + Stepped;
        var call = rig.Call("delayed", "add_item", sentAt: sentAt);
        if (deadline is { } span)
            call["deadline"] = sentAt + span;
        rig.Clocks.Advance(TimeSpan.FromSeconds(5));
        await peer.SendAsync(call);
        await peer.AnswerTimeAsync(rig.Clocks.RelayNow + Stepped, Ct);

        var cutoff = await CutoffOf(tools);
        rig.Clocks.Advance(TimeSpan.FromSeconds(left - 1));
        Assert.False(cutoff.IsTripped);
        rig.Clocks.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cutoff.IsTripped);
        Assert.Equal("deadline", cutoff.Reason);

        tools.Hold.SetResult();
        await peer.ExpectAsync("result", Ct);
    }

    [Fact]
    public async Task ACallsDesignAndFiles_ReachTheToolHostInItsContext()
    {
        await using var rig = await StartAsync();
        var peer = await rig.ConnectAsync();
        var call = rig.Call("d", "add_item", new JsonObject { ["profile"] = "P" });
        call["design"] = JsonNode.Parse("""{"source":{"format":"example.widget/1","data":{},"deps":[]},"scene":{"format":"infopanel.scene/1"}}""");
        call["files"] = new JsonArray("one");
        await peer.SendAsync(call);
        await peer.ExpectAsync("result", Ct);
        var context = rig.FakeTools!.Calls.Single().Context;
        Assert.Equal(call["design"]!.ToJsonString(), context.Design!.Value.GetRawText());
        Assert.Equal(["one"], context.Files);

        // A call without them has neither.
        await peer.SendAsync(rig.Call("plain", "list_profiles"));
        await peer.ExpectAsync("result", Ct);
        var plain = rig.FakeTools.Calls.Single(c => c.Context.Id == "plain").Context;
        Assert.Null(plain.Design);
        Assert.Null(plain.Files);
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
