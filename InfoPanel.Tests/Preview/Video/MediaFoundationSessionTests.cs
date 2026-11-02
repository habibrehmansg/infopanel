using System.Diagnostics;
using InfoPanel.Preview.Video;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// Media Foundation's H.264 backend over <see cref="FakeMfCatalog"/>, on any OS, its bounds on a clock the test moves:
/// the choice (the walk, the hardware-URL and asynchrony checks, vendor-blind, a failing entry skipped), the identity
/// and its re-probes, the probe's one-at-a-time frames, the input layout, the start classes with Busy kept through a
/// start's probes, the session (credits, pairing, forced keyframes, the first output, abort, one teardown task, a
/// timed-out activation, the event thread's ready signal) and stream changes. Every behaviour is scripted as behaviour;
/// no assertion names a vendor.
/// </summary>
public class MediaFoundationSessionTests
{
    private const int W = 64;
    private const int H = 48;
    private const ulong V = 0x0020_0000_5226_07D1;
    private const ulong V2 = 0x0020_0000_5300_0001;
    private const ulong V3 = 0x0020_0000_5400_0001;
    private const string K = @"PCI\VEN_1002&DEV_1900&SUBSYS_00311F66&REV_C5\4&1234&0&0041";
    private const string K2 = @"PCI\VEN_10DE&DEV_2504&SUBSYS_00000000&REV_A1\4&5678&0&0008";
    private static readonly Guid C = new("ADC9BC80-0F41-46C6-AB75-D693D793597D");
    private static readonly Guid C2 = new("60F44560-5A20-4857-BFEF-D29773CB8040");

    // ---- the rig ----

    private sealed class Rig : IDisposable
    {
        private readonly List<Run> _runs = [];

        public Rig() => Backend = new MediaFoundationH264(Catalog, Log, Time);

        public FakeMfCatalog Catalog { get; } = new();

        public ManualTime Time { get; } = new(DateTimeOffset.UnixEpoch);

        public RecordingLogger Log { get; } = new();

        public MediaFoundationH264 Backend { get; }

        /// <summary>An entry with the whole identity (CLSID, URL, device key; its transform's version <see cref="V"/>).</summary>
        public FakeMfEntry Entry(string name = "Encoder", string? vendor = "VEN_1002", Guid? clsid = null, string? url = null, string? key = K) =>
            Catalog.Add(name, vendor, clsid ?? C, url ?? name, key);

        /// <summary>One trusted entry: listed, probed, passing.</summary>
        public FakeMfEntry Trusted(string name = "Encoder")
        {
            var entry = Entry(name);
            Assert.Equal(H264Probe.Available, Probe());
            return entry;
        }

        /// <summary>
        /// The backend's probe, which the clock never bounds here (it doesn't move): capped in real time, so a probe that
        /// never ends fails the test instead of hanging it.
        /// </summary>
        public H264Probe Probe() => Capped(Backend.Probe);

        public Run Start(int width = W, int height = H)
        {
            var run = new Run(width, height);
            run.Session = (MfSession)Capped(() => Backend.Start(new H264Config(width, height, 30, VideoStream.HardwareBitrate), run.Add));
            run.Port = Catalog.Ports[^1];
            lock (_runs)
                _runs.Add(run);
            return run;
        }

        /// <summary>A start that throws: its refusal.</summary>
        public H264StartException Refused(int width = W, int height = H) =>
            Assert.Throws<H264StartException>(() => Capped(() => Backend.Start(new H264Config(width, height, 30, VideoStream.HardwareBitrate), _ => { })));

        /// <summary>Runs <paramref name="call"/> within 10 s of real time (a cap, never a timing), rethrowing what it throws.</summary>
        private static T Capped<T>(Func<T> call)
        {
            var task = Task.Run(call);
            if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != task)
                Assert.Fail("The call didn't end within 10 s");
            return task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            lock (_runs)
            {
                foreach (var run in _runs)
                    _ = run.Session.Abort();
            }
        }
    }

    private sealed class Run(int width, int height)
    {
        private readonly List<H264Outcome> _outcomes = [];

        public MfSession Session { get; set; } = null!;

        public FakeMfPort Port { get; set; } = null!;

        public void Add(H264Outcome outcome)
        {
            lock (_outcomes)
                _outcomes.Add(outcome);
        }

        public IReadOnlyList<H264Outcome> Outcomes
        {
            get
            {
                lock (_outcomes)
                    return [.. _outcomes];
            }
        }

        public H264Submit Submit(long id, bool force = false, byte value = 100) =>
            Session.Submit(Pixels(width, height, value), width, height, id, force, out _);

        /// <summary>Submits frame <paramref name="id"/> and waits for its outcome.</summary>
        public async Task<H264Outcome> Encode(long id, bool force = false)
        {
            Assert.Equal(H264Submit.Accepted, Submit(id, force));
            await Eventually(() => Outcomes.Any(o => o.FrameId == id), $"frame {id}'s outcome");
            return Outcomes.Single(o => o.FrameId == id);
        }
    }

    private static byte[] Pixels(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }

    /// <summary>Waits (real time, at most 10 s) until <paramref name="condition"/>: the session's threads are real.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("Timed out waiting until " + what);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A real wait, when the test checks that something doesn't happen: the session's threads are real, so the absence
    /// of a call can only be seen after giving them time. Well under a second.
    /// </summary>
    private static Task Settle() => Task.Delay(60, TestContext.Current.CancellationToken);

    /// <summary>Waits until a timer is armed at <paramref name="at"/> on the rig's clock, then moves the clock there.</summary>
    private static async Task AdvanceTo(ManualTime time, TimeSpan at)
    {
        await Eventually(() => time.HasTimerDueAt(at), $"a timer at {at.TotalMilliseconds} ms");
        time.Advance(at - TimeSpan.FromTicks(time.GetTimestamp()));
    }

    /// <summary>When the latest thread of <paramref name="role"/> ended: its leave, after its enter, on its thread.</summary>
    private static long ThreadEnd(FakeMfCatalog catalog, string role)
    {
        var calls = catalog.Calls;
        var enter = calls.Last(c => c.Name == "Enter" && c.Detail == role);
        return calls.FirstOrDefault(c => c.Name == "Leave" && c.Seq > enter.Seq && c.Thread == enter.Thread)?.Seq ?? long.MaxValue;
    }

    private static IEnumerable<FakeMfCall> PortCalls(FakeMfCatalog catalog, FakeMfPort port) => catalog.Calls.Where(c => c.Port == port.Id);

    private static long SeqOf(FakeMfCatalog catalog, FakeMfPort port, string name, string? detail = null) =>
        PortCalls(catalog, port).First(c => c.Name == name && (detail == null || c.Detail == detail)).Seq;

    // ---- the probe ----

    [Fact]
    public async Task TheProbe_SendsThreeFramesAt322x194_EachOnlyAfterThePreviousOutput_AndTheForcedThirdTeachesForcing()
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        // Three credits at once (a deeper pipeline) and each output held until the test lets it go: only the probe's
        // own pacing keeps one frame in the encoder.
        entry.Encoder.InitialCredits = 3;
        entry.Encoder.CreditPerOutput = false;
        entry.Encoder.HoldOutputs = true;
        var probe = Task.Run(rig.Probe, TestContext.Current.CancellationToken);
        for (var i = 1; i <= 3; i++)
        {
            var n = i;
            await Eventually(() => entry.Ports.Count == 1 && entry.Ports[0].InputTimes.Count >= n, $"frame {n}'s input");
            await Settle();
            Assert.Equal(n, entry.Ports[0].InputTimes.Count);
            entry.Ports[0].ReleaseOutputs(1);
        }

        Assert.Equal(H264Probe.Available, await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var port = Assert.Single(entry.Ports);
        Assert.Equal((322, 194), (port.Output!.Width, port.Output.Height));
        var order = PortCalls(rig.Catalog, port).Where(c => c.Name is "ProcessInput" or "ProcessOutput").Select(c => c.Name).ToList();
        Assert.Equal(["ProcessInput", "ProcessOutput", "ProcessInput", "ProcessOutput", "ProcessInput", "ProcessOutput"], order);

        // The third is forced, just before its input.
        var calls = PortCalls(rig.Catalog, port).ToList();
        var force = Assert.Single(calls, c => c.Name == "SetCodecValue" && c.Detail == "FORCE=1");
        var inputs = calls.Where(c => c.Name == "ProcessInput").ToList();
        Assert.True(force.Seq > inputs[1].Seq && force.Seq < inputs[2].Seq);
        Assert.True(rig.Backend.ChangesLive);
        Assert.Equal(new MfIdentity(C, "Encoder", K, V), rig.Backend.Trusted);
        Assert.True(port.ShutDown && port.Released);
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Fact]
    public void AnEncoderThatIgnoresTheForcedThirdFrame_PassesTheProbe_WithForcingOff()
    {
        using var rig = new Rig();
        rig.Entry().Encoder.IgnoreForce = true;
        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.False(rig.Backend.ChangesLive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEncoderThatDoesntImplementForcing_PassesTheProbe_WithForcingOff_AndItsSessionsAreNeverForced(bool idrAnyway)
    {
        using var rig = new Rig();
        var e = rig.Entry().Encoder;
        e.RefuseCodec[Mf.CodecForceKeyFrame] = Mf.ENotImpl;
        // Its third frame an IDR of its own accord: still not a forced one.
        if (idrAnyway)
            e.KeyAt = 2;
        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.False(rig.Backend.ChangesLive);

        // A keyframe request is then the stream's restart, never a force that would fail the session.
        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(1, force: true)).Kind);
        Assert.DoesNotContain(PortCalls(rig.Catalog, run.Port), c => c.Detail == "FORCE=1");
    }

    [Fact]
    public void TheChoice_AsksForHardwareEntriesOnly_NeverActivatesOneWithoutAUrl_AndNeverConfiguresASynchronousTransform()
    {
        using var rig = new Rig();
        var noUrl = rig.Entry("No URL");
        noUrl.Attributes.Remove(Mf.HardwareUrl);
        var synchronous = rig.Entry("Synchronous", key: K2);
        synchronous.Encoder.Async = 0;
        var absent = rig.Entry("No async attribute", clsid: C2);
        absent.Encoder.Async = null;
        var good = rig.Entry("Good");

        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.All(rig.Catalog.EnumerateFlags, f => Assert.Equal(Mf.EnumHardware | Mf.EnumSortAndFilter, f));
        Assert.Empty(noUrl.Ports);
        foreach (var entry in new[] { synchronous, absent })
        {
            var port = Assert.Single(entry.Ports);
            Assert.True(port.Activated && port.ShutDown && port.Released, entry.Name);
            Assert.False(port.Configured, entry.Name + " was configured");
            Assert.Null(port.Output);
        }

        Assert.Equal("Good", rig.Backend.Trusted!.HardwareUrl);
        Assert.Single(good.Ports);
    }

    [Fact]
    public void AnEntryThatRefusesEveryOutputTypeWithoutADeviceManager_IsTornDown_AndTheNextIsChosen()
    {
        using var rig = new Rig();
        var needsDevice = rig.Entry("Needs a device manager", vendor: "VEN_1414", clsid: C2);
        needsDevice.Encoder.OutputTypeHr = _ => Mf.UnsupportedD3DType;
        var good = rig.Entry("Good");

        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.Equal("Good", rig.Backend.Trusted!.HardwareUrl);
        var first = Assert.Single(needsDevice.Ports);
        Assert.True(first.ShutDown && first.Released);
        Assert.True(SeqOf(rig.Catalog, first, "Release") < SeqOf(rig.Catalog, Assert.Single(good.Ports), "Activate"),
            "the first entry was torn down before the next was activated");
        Assert.DoesNotContain(rig.Catalog.Calls, c => c.Name == "ProcessMessage" && c.Detail == "SET_D3D_MANAGER");
        Assert.Contains(rig.Log.Messages, m => m.Contains("Needs a device manager") && m.Contains("trying the next"));
    }

    [Fact]
    public async Task AnEntryWhoseActivationHangs_EndsTheWalkUnavailableAtTheProbeBound_NothingBesideIt_AndIsCountedAbandoned()
    {
        using var rig = new Rig();
        var hold = new ManualResetEventSlim();
        var hung = rig.Entry("Hung", clsid: C2);
        hung.Encoder.HoldActivate = hold;
        var good = rig.Entry("Good");
        var abandoned = new List<Task>();
        try
        {
            var probe = Task.Run(() => rig.Backend.Probe(CancellationToken.None, abandoned.Add), TestContext.Current.CancellationToken);
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeBound);
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeBound + Quarantine.Bound);
            Assert.Equal(H264Probe.Unavailable, await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var teardown = Assert.Single(abandoned);
            Assert.False(teardown.IsCompleted);
            Assert.Empty(good.Ports);

            hold.Set();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var port = Assert.Single(hung.Ports);
            Assert.True(port.ShutDown && port.Released);
            Assert.Null(port.Output);
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public void NoEntry_IsUnavailable_ABusyBegin_IsBusy_AndNoMediaFoundation_NamesTheFeaturePack()
    {
        using (var rig = new Rig())
        {
            Assert.Equal(H264Probe.Unavailable, rig.Probe());
            Assert.Contains(rig.Log.Messages, m => m.Contains("No working hardware H.264 encoder"));
        }

        using (var rig = new Rig())
        {
            rig.Entry().Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
            Assert.Equal(H264Probe.Busy, rig.Probe());
            // Not cached by the backend: the next probe tries again.
            Assert.Equal(H264Probe.Busy, rig.Probe());
            Assert.Equal(2, rig.Catalog.Entries[0].Ports.Count);
            Assert.Null(rig.Backend.Trusted);
        }

        using (var rig = new Rig())
        {
            rig.Entry();
            rig.Catalog.Missing = true;
            Assert.Equal(H264Probe.Unavailable, rig.Probe());
            Assert.Contains(rig.Log.Messages, m => m.Contains("Media Feature Pack"));
        }
    }

    public static TheoryData<string> BrokenEntries => ["held", "wrong time", "Main", "no parameter sets", "only in the header", "336x208", "stream change to profile 100"];

    [Theory]
    [MemberData(nameof(BrokenEntries))]
    public async Task TheProbe_FailsAnEntryThatBreaksARule_AndTriesTheNext(string how)
    {
        using var rig = new Rig();
        var bad = rig.Entry("Bad", clsid: C2);
        var good = rig.Entry("Good");
        var e = bad.Encoder;
        switch (how)
        {
            case "held":
                e.HoldUntilNextInput = true;
                break;
            case "wrong time":
                e.WrongTimeAt = 1;
                break;
            case "Main":
                e.SpsProfile = 77;
                break;
            case "no parameter sets":
                e.IdrWithoutParameterSets = true;
                break;
            case "only in the header":
                e.ParameterSetsOnlyInHeader = true;
                e.OfferedHeader = Convert.FromHexString(AnnexBTests.AmdSequenceHeader);
                break;
            case "336x208":
                e.SpsSize = (336, 208);
                break;
            case "stream change to profile 100":
                e.StreamChanges = 1;
                e.ChangeTo = (w, h) => new MfVideoType(Mf.H264, w, h) { Profile = 100 };
                break;
        }

        var probe = Task.Run(rig.Backend.Probe, TestContext.Current.CancellationToken);
        if (how == "held")
        {
            // No second input goes in while the first frame's output is held: the attempt fails at its frame wait.
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeFrameWait);
        }

        Assert.Equal(H264Probe.Available, await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("Good", rig.Backend.Trusted!.HardwareUrl);
        var port = Assert.Single(bad.Ports);
        Assert.True(port.ShutDown && port.Released);
        if (how == "held")
            Assert.Single(port.InputTimes);
        Assert.Single(good.Ports);
        Assert.Contains(rig.Log.Messages, m => m.Contains("Bad") && m.Contains("trying the next"));
    }

    [Fact]
    public void AStreamChangeWithinTheRule_DoesntFailTheProbe()
    {
        using var rig = new Rig();
        rig.Entry().Encoder.StreamChanges = 1;
        Assert.Equal(H264Probe.Available, rig.Probe());
    }

    [Fact]
    public async Task TheWalk_PastTheProbeBound_IsUnavailable_TheEntriesAfterItUntried()
    {
        using var rig = new Rig();
        var held = Enumerable.Range(0, 6).Select(i => rig.Entry($"Held {i}", clsid: Guid.NewGuid())).ToList();
        foreach (var entry in held)
            entry.Encoder.HoldUntilNextInput = true;
        var good = rig.Entry("Good");

        var probe = Task.Run(rig.Backend.Probe, TestContext.Current.CancellationToken);
        for (var i = 0; i < held.Count; i++)
        {
            var entry = held[i];
            await Eventually(() => entry.Ports.Count == 1 && entry.Ports[0].InputTimes.Count == 1, $"entry {i}'s first frame");
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeFrameWait * (i + 1));
        }

        Assert.Equal(H264Probe.Unavailable, await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(good.Ports);
        Assert.Contains(rig.Log.Messages, m => m.Contains("not tried within the probe's bound"));
    }

    [Fact]
    public async Task Stopping_EndsTheProbeAtOnce_WithItsAttemptTornDown()
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        entry.Encoder.HoldUntilNextInput = true;
        using var stopping = new CancellationTokenSource();
        var probe = Task.Run(() => rig.Backend.Probe(stopping.Token, _ => { }), TestContext.Current.CancellationToken);
        await Eventually(() => entry.Ports.Count == 1 && entry.Ports[0].InputTimes.Count == 1, "the first frame");
        stopping.Cancel();
        Assert.Equal(H264Probe.Unavailable, await probe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Eventually(() => entry.Ports[0].Released, "the attempt is torn down");
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Fact]
    public async Task AnAttemptsTeardownPastTheBound_IsAbandoned_AndEndsTheWalk()
    {
        using var rig = new Rig();
        var hold = new ManualResetEventSlim();
        var wedged = rig.Entry("Wedged", clsid: C2);
        wedged.Encoder.IdrWithoutParameterSets = true;
        wedged.Encoder.HoldShutdown = hold;
        var good = rig.Entry("Good");
        var abandoned = new List<Task>();
        try
        {
            var probe = Task.Run(() => rig.Backend.Probe(CancellationToken.None, abandoned.Add), TestContext.Current.CancellationToken);
            await AdvanceTo(rig.Time, Quarantine.Bound);
            Assert.Equal(H264Probe.Unavailable, await probe.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            _ = Assert.Single(abandoned);
            Assert.Empty(good.Ports);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public void ASecondBackendOverTheSameCatalog_ProbesAgain()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var next = new MediaFoundationH264(rig.Catalog, NullLogger.Instance, rig.Time);
        Assert.Null(next.Trusted);
        Assert.Equal(H264Probe.Available, next.Probe());
        Assert.Equal(2, entry.Probes);
    }

    // ---- vendor-blind ----

    [Fact]
    public async Task OneScriptedCatalog_UnderEveryVendorId_GivesTheSameChoiceCallsAndOutcomes()
    {
        string? reference = null;
        foreach (var vendor in new[] { "VEN_1002", "VEN_8086", "VEN_10DE", "VEN_1414", null })
        {
            using var rig = new Rig();
            rig.Entry("Failing", vendor, clsid: C2).Encoder.IdrWithoutParameterSets = true;
            rig.Entry("Passing", vendor);
            Assert.Equal(H264Probe.Available, rig.Probe());
            var run = rig.Start();
            var outcomes = new List<H264Outcome>();
            for (var i = 0; i < 3; i++)
                outcomes.Add(await run.Encode(i, force: i == 2));

            var trace = string.Join("\n", [
                $"chose {rig.Backend.Trusted}", $"forces {rig.Backend.ChangesLive}",
                .. outcomes.Select(o => $"{o.FrameId} {o.Kind} key {o.Keyframe}"),
                .. rig.Catalog.Calls.Where(c => c.Port > 0 && c.Name != "Release" && c.Name != "Shutdown").Select(c => $"{c.Port} {c.Name} {c.Detail}"),
            ]);
            reference ??= trace;
            Assert.Equal(reference, trace);
            if (vendor != null)
                Assert.Contains(rig.Log.Messages, m => m.Contains(vendor));
        }
    }

    public static TheoryData<int[], bool> Orders()
    {
        var data = new TheoryData<int[], bool>();
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders)
        {
            data.Add(order, false);
            data.Add(order, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void MixedVendors_InAnyOrder_TheFirstPassingEntryInTheCatalogsOrderIsChosen(int[] order, bool failingAhead)
    {
        using var rig = new Rig();
        string[] vendors = ["VEN_1002", "VEN_8086", "VEN_10DE"];
        FakeMfEntry? failing = null;
        if (failingAhead)
        {
            failing = rig.Entry("Failing", "VEN_1414", clsid: C2);
            failing.Encoder.OutputTypeHr = _ => Mf.InvalidMediaType;
        }

        var entries = order.Select(i => rig.Entry($"Passing {vendors[i]}", vendors[i], clsid: Guid.NewGuid())).ToList();
        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.Equal(entries[0].Name, rig.Backend.Trusted!.HardwareUrl);
        Assert.Single(entries[0].Ports);
        Assert.All(entries.Skip(1), e => Assert.Empty(e.Ports));
        if (failing != null)
            Assert.Single(failing.Ports);
    }

    // ---- identity ----

    [Fact]
    public void AStart_UsesTheTrustedEntryByItsWholeIdentity_ProbingNothing()
    {
        using var rig = new Rig();
        var a = rig.Trusted("A");
        Assert.Equal(new MfIdentity(C, "A", K, V), rig.Backend.Trusted);
        var probes = a.Probes;

        // [A′ (A's CLSID and URL, device key K′), A]: A, with no probe.
        rig.Catalog.Entries.Clear();
        var other = rig.Catalog.Add("A′", "VEN_1002", C, "A", K2);
        rig.Catalog.Entries.Add(a);
        var run = rig.Start();
        Assert.Empty(other.Ports);
        Assert.Equal(probes, a.Probes);
        Assert.Same(a, run.Port.Entry);

        // [B (another CLSID, A's URL, key K, version V), A]: B isn't trusted; A, with no probe.
        rig.Catalog.Entries.Clear();
        var b = rig.Catalog.Add("B", "VEN_1002", C2, "A", K);
        rig.Catalog.Entries.Add(a);
        run = rig.Start();
        Assert.Empty(b.Ports);
        Assert.Equal(probes, a.Probes);
        Assert.Same(a, run.Port.Entry);
    }

    [Fact]
    public void AReplacementEntry_SharingTheKeyAndVersion_IsProbedBeforeItIsUsed()
    {
        using var rig = new Rig();
        rig.Trusted("A");

        // [A″ (A's CLSID, key K and version V, another hardware URL)] alone: probed first, then trusted.
        rig.Catalog.Entries.Clear();
        var replacement = rig.Catalog.Add("A″", "VEN_1002", C, "A2", K);
        var run = rig.Start();
        Assert.Equal(1, replacement.Probes);
        Assert.Same(replacement, run.Port.Entry);
        Assert.Equal(new MfIdentity(C, "A2", K, V), rig.Backend.Trusted);
    }

    [Fact]
    public void ANewDriverVersion_OnTheTransform_IsProbedAgain_AndTrustedAfter()
    {
        using var rig = new Rig();
        var a = rig.Trusted("A");
        var probes = a.Probes;
        a.Encoder.DriverVersion = V2;
        var run = rig.Start();
        Assert.Equal(probes + 1, a.Probes);
        Assert.Same(a, run.Port.Entry);
        Assert.Equal(new MfIdentity(C, "A", K, V2), rig.Backend.Trusted);

        // And now trusted: the next start probes nothing.
        rig.Start();
        Assert.Equal(probes + 1, a.Probes);
    }

    [Fact]
    public async Task AVersionThatChangesBetweenAStartsProbeAndItsActivation_IsProbedAgain_BeforeThatActivationIsUsed()
    {
        using var rig = new Rig();
        var a = rig.Trusted("A");
        var probes = a.Probes;
        var ports = a.Ports.Count;

        // The start's activation reads V2, so it probes; while that probe's first output is held, the driver becomes V3.
        a.Encoder.DriverVersion = V2;
        a.Encoder.HoldOutputs = true;
        var start = Task.Run(() => rig.Start(), TestContext.Current.CancellationToken);
        await Eventually(() => a.Ports.Count == ports + 2 && a.Ports[^1].InputTimes.Count == 1, "the start's probe's first frame");
        a.Encoder.DriverVersion = V3;
        a.Encoder.HoldOutputs = false;
        a.Ports[^1].ReleaseOutputs();
        var run = await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // V2's pass doesn't vouch for V3: V3 is probed before it is used, and trusted after.
        Assert.Equal(probes + 2, a.Probes);
        Assert.Equal(new MfIdentity(C, "A", K, V3), rig.Backend.Trusted);
        Assert.Equal(rig.Backend.Trusted, run.Session.Identity);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        Assert.All(a.Ports.SkipLast(1), p => Assert.True(p.ShutDown && p.Released));
    }

    [Fact]
    public async Task AStartThatTimesOutWhileItsProbesTeardownIsWedged_HandsBackATeardownThatWaitsForThatProbe()
    {
        using var rig = new Rig();
        var a = rig.Trusted("A");
        var hold = new ManualResetEventSlim();
        // The start's activation reads a new version, so it probes; the probe's first output never comes (it fails at
        // its frame wait, at 0.5 s), and its teardown's shutdown is wedged (the start's own activation already shut down).
        a.Encoder.DriverVersion = V2;
        a.Encoder.HoldUntilNextInput = true;
        var ports = a.Ports.Count;
        try
        {
            var start = Task.Run(() => rig.Backend.Start(new H264Config(W, H, 30, VideoStream.HardwareBitrate), _ => { }),
                TestContext.Current.CancellationToken);
            await Eventually(() => a.Ports.Count == ports + 2 && a.Ports[^1].InputTimes.Count == 1, "the start's probe's first frame");
            a.Encoder.HoldShutdown = hold;
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeFrameWait);
            // The probe waits for its teardown until 2.5 s; the start gives up at 2 s, before the probe has registered it.
            await Eventually(() => rig.Time.HasTimerDueAt(MediaFoundationH264.ProbeFrameWait + Quarantine.Bound), "the probe waits for its teardown");
            await AdvanceTo(rig.Time, MediaFoundationH264.StartBound);
            var refusal = await Assert.ThrowsAsync<H264StartException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(H264StartProblem.Fault, refusal.Problem);
            var teardown = refusal.Teardown!;

            // The probe's wait passes its bound and the setup ends: the start's teardown still waits for that probe.
            await AdvanceTo(rig.Time, MediaFoundationH264.ProbeFrameWait + Quarantine.Bound);
            await Settle();
            Assert.False(teardown.IsCompleted, "the start's teardown ended while its probe's shutdown was wedged");
            hold.Set();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.All(a.Ports, p => Assert.True(p.Released));
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task AnotherEntryAlone_IsProbedFirst_ItsIgnoredForceTurnsForcingOff_ForGood_AndItsFailureIsAFault()
    {
        using var rig = new Rig();
        rig.Trusted("A");
        Assert.True(rig.Backend.ChangesLive);

        // [B] alone (A's CLSID and URL, key K′): probed first; B ignores the forced third frame.
        rig.Catalog.Entries.Clear();
        var b = rig.Catalog.Add("B", "VEN_1002", C, "A", K2);
        b.Encoder.IgnoreForce = true;
        var run = rig.Start();
        Assert.Equal(1, b.Probes);
        Assert.Same(b, run.Port.Entry);
        Assert.False(rig.Backend.ChangesLive);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);

        // B forcing at its next probe (a new version) never turns forcing back on.
        b.Encoder.IgnoreForce = false;
        b.Encoder.DriverVersion = V2;
        rig.Start();
        Assert.Equal(2, b.Probes);
        Assert.False(rig.Backend.ChangesLive);

        // B failing its probe (another version again): a Fault.
        b.Encoder.IdrWithoutParameterSets = true;
        b.Encoder.DriverVersion = V;
        Assert.Equal(H264StartProblem.Fault, rig.Refused().Problem);
    }

    [Fact]
    public void NeitherTrusted_AStartProbesEachInOrder_AndStartsOnTheFirstThatPasses()
    {
        using var rig = new Rig();
        var failing = rig.Entry("Failing", clsid: C2);
        failing.Encoder.IdrWithoutParameterSets = true;
        var passing = rig.Entry("Passing");
        var run = rig.Start();
        Assert.Equal(1, failing.Probes);
        Assert.Equal(1, passing.Probes);
        Assert.Same(passing, run.Port.Entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEntryWithNoDeviceKey_OrATransformWithNoVersion_IsProbedAtEveryStart(bool noVersion)
    {
        using var rig = new Rig();
        var entry = rig.Entry(key: noVersion ? K : null);
        if (noVersion)
            entry.Encoder.DriverVersion = null;
        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.False(rig.Backend.Trusted!.Complete);
        for (var i = 1; i <= 3; i++)
        {
            rig.Start();
            Assert.Equal(1 + i, entry.Probes);
        }
    }

    [Fact]
    public void NoEntry_OrOnlyAFailingOne_IsAFault_AndABusyProbe_IsBusy()
    {
        using var rig = new Rig();
        Assert.Equal(H264StartProblem.Fault, rig.Refused().Problem);

        var entry = rig.Entry();
        entry.Encoder.IdrWithoutParameterSets = true;
        Assert.Equal(H264StartProblem.Fault, rig.Refused().Problem);

        entry.Encoder.IdrWithoutParameterSets = false;
        entry.Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
        Assert.Equal(H264StartProblem.Busy, rig.Refused().Problem);
    }

    // ---- Busy in a start's probe ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABusyBeginInAStartsProbe_IsBusy_NeverAFault_AndLeavesTheTrustAsItWas(bool noVersion)
    {
        using var rig = new Rig();
        var entry = rig.Entry(key: noVersion ? K : null);
        if (noVersion)
            entry.Encoder.DriverVersion = null;
        entry.Encoder.Device = new FakeMfDevice(1);
        Assert.Equal(H264Probe.Available, rig.Probe());
        var trusted = rig.Backend.Trusted;

        var first = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await first.Encode(0)).Kind);
        var refusal = rig.Refused();
        Assert.Equal(H264StartProblem.Busy, refusal.Problem);
        Assert.NotNull(refusal.Teardown);
        await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(trusted, rig.Backend.Trusted);
        Assert.True(rig.Backend.ChangesLive);
    }

    [Fact]
    public async Task TwoStartsProbingAtOnce_OnePassesAndStarts_TheOtherIsBusy_NeitherBindingTheOthersPass()
    {
        using var rig = new Rig();
        var entry = rig.Entry(key: null);
        entry.Encoder.Device = new FakeMfDevice(1);
        Assert.Equal(H264Probe.Available, rig.Probe());
        var trusted = rig.Backend.Trusted;

        // Both starts' probes take their begins together: one gets the chip's session, the other is refused.
        entry.Encoder.BeginBarrier = new Barrier(2);
        entry.Encoder.BeginsTogether = 2;
        var config = new H264Config(W, H, 30, VideoStream.HardwareBitrate);
        var starts = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try
            {
                return (rig.Backend.Start(config, _ => { }), (H264StartException?)null);
            }
            catch (H264StartException ex)
            {
                return ((IH264Session?)null, ex);
            }
        }, TestContext.Current.CancellationToken)).ToList();
        var results = await Task.WhenAll(starts);
        try
        {
            Assert.Single(results, r => r.Item1 != null);
            var busy = Assert.Single(results, r => r.Item2 != null).Item2!;
            Assert.Equal(H264StartProblem.Busy, busy.Problem);
            Assert.Equal(trusted, rig.Backend.Trusted);
            Assert.True(rig.Backend.ChangesLive);
        }
        finally
        {
            foreach (var (session, _) in results)
                _ = session?.Abort();
        }
    }

    // ---- input and pairing ----

    [Fact]
    public async Task ProcessInput_GoesOnlyAfterACredit_AQueuedSampleAtTheNextNeedInput_AndSubmitNeverCallsThePort()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.InitialCredits = 0;
        var run = rig.Start();
        var submitter = Environment.CurrentManagedThreadId;

        Assert.Equal(H264Submit.Accepted, run.Submit(0, force: true));
        await Settle();
        Assert.DoesNotContain(PortCalls(rig.Catalog, run.Port), c => c.Name == "ProcessInput");

        run.Port.SendNeedInput();
        await Eventually(() => run.Outcomes.Count == 1, "the frame's outcome");
        Assert.Equal(H264OutcomeKind.Encoded, run.Outcomes[0].Kind);
        Assert.True((await run.Encode(1)).Kind == H264OutcomeKind.Encoded);
        Assert.Equal(0, run.Port.InputsWithoutCredit);
        Assert.DoesNotContain(rig.Catalog.Calls, c => c.Port > 0 && c.Thread == submitter);
    }

    public static TheoryData<string> Mispairings => ["wrong time", "no time", "no frame"];

    [Theory]
    [MemberData(nameof(Mispairings))]
    public async Task AnOutputThatDoesntPair_FailsTheHeadOnce_AndLaterFramesAreRefused(string how)
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        switch (how)
        {
            case "wrong time":
                entry.Encoder.WrongTimeAt = 1;
                break;
            case "no time":
                entry.Encoder.NoTime = true;
                break;
            case "no frame":
                entry.Encoder.ExtraOutput = true;
                break;
        }

        var run = rig.Start();
        if (how != "no time")
            Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0)).Kind);
        var failing = how == "no time" ? 0 : 1;
        if (how == "no frame")
        {
            // Its output with no frame breaks the session: the next frame is refused.
            await Eventually(() => run.Submit(1) == H264Submit.Refused, "a refusal");
        }
        else
        {
            var outcome = await run.Encode(failing);
            Assert.Equal(H264OutcomeKind.Failed, outcome.Kind);
            Assert.Equal(H264Submit.Refused, run.Submit(failing + 1));
        }

        await Settle();
        Assert.True(run.Outcomes.Count(o => o.Kind == H264OutcomeKind.Failed) <= 1);
        Assert.DoesNotContain(run.Outcomes, o => o.Kind == H264OutcomeKind.Encoded && o.FrameId >= failing && how != "no frame");
    }

    [Fact]
    public async Task ExactlyOneOutcomePerSubmission_WhenAbortedQueued_InTheEncoder_OrWhileItsOutputIsRead()
    {
        // Queued: no credit.
        using (var rig = new Rig())
        {
            rig.Trusted().Encoder.InitialCredits = 0;
            var run = rig.Start();
            run.Submit(0);
            run.Submit(1);
            await run.Session.Abort().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            run.Port.SendNeedInput();
            await Settle();
            Assert.Equal([(0L, H264OutcomeKind.Aborted), (1L, H264OutcomeKind.Aborted)], run.Outcomes.Select(o => (o.FrameId, o.Kind)));
        }

        // In the encoder: its output held.
        using (var rig = new Rig())
        {
            rig.Trusted().Encoder.HoldOutputs = true;
            var run = rig.Start();
            run.Submit(0);
            await Eventually(() => run.Port.InputTimes.Count == 1, "the input");
            var teardown = run.Session.Abort();
            run.Port.ReleaseOutputs();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Settle();
            Assert.Equal([(0L, H264OutcomeKind.Aborted)], run.Outcomes.Select(o => (o.FrameId, o.Kind)));
        }

        // While its output is read: ProcessOutput held, then answering after the abort.
        using (var rig = new Rig())
        {
            var hold = new ManualResetEventSlim();
            rig.Trusted().Encoder.HoldProcessOutput = hold;
            var run = rig.Start();
            run.Submit(0);
            await Eventually(() => PortCalls(rig.Catalog, run.Port).Any(c => c.Name == "ProcessOutput"), "the output is being read");
            var teardown = run.Session.Abort();
            hold.Set();
            rig.Time.Advance(MediaFoundationH264.FlushLockWait);
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Settle();
            Assert.Equal([(0L, H264OutcomeKind.Aborted)], run.Outcomes.Select(o => (o.FrameId, o.Kind)));
        }
    }

    // ---- abort and teardown ----

    [Fact]
    public async Task Abort_ReportsEveryFrameAtOnce_ReturnsWithProcessOutputWedged_AndItsTeardownEndsOnlyAfterShutdownJoinsAndRelease()
    {
        using var rig = new Rig();
        var hold = new ManualResetEventSlim();
        rig.Trusted().Encoder.HoldProcessOutput = hold;
        try
        {
            var run = rig.Start();
            run.Submit(0, force: true);
            await Eventually(() => PortCalls(rig.Catalog, run.Port).Any(c => c.Name == "ProcessOutput"), "ProcessOutput is wedged");
            run.Submit(1);

            var watch = Stopwatch.StartNew();
            var teardown = run.Session.Abort();
            Assert.True(watch.ElapsedMilliseconds < 50, $"Abort took {watch.ElapsedMilliseconds} ms");
            Assert.Equal([H264OutcomeKind.Aborted, H264OutcomeKind.Aborted], run.Outcomes.Select(o => o.Kind));

            // The MFT lock is held by the wedged call: no flush within its wait, then the shutdown, which ends the call.
            await Settle();
            Assert.False(teardown.IsCompleted);
            await AdvanceTo(rig.Time, MediaFoundationH264.FlushLockWait);
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var calls = PortCalls(rig.Catalog, run.Port).ToList();
            Assert.DoesNotContain(calls, c => c.Detail is "FLUSH" or "DRAIN" or "END_OF_STREAM");
            var shutdown = calls.Single(c => c.Name == "Shutdown").Seq;
            var release = calls.Single(c => c.Name == "Release").Seq;
            Assert.True(ThreadEnd(rig.Catalog, "events") > shutdown, "the event thread ended after the shutdown");
            Assert.True(ThreadEnd(rig.Catalog, "events") < release && ThreadEnd(rig.Catalog, "session") < release, "both threads ended before the release");
            Assert.Equal(2, run.Outcomes.Count);
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task AWedgedShutdown_NeverEndsTheTeardown()
    {
        using var rig = new Rig();
        var hold = new ManualResetEventSlim();
        rig.Trusted().Encoder.HoldShutdown = hold;
        var run = rig.Start();
        var teardown = run.Session.Abort();
        await Settle();
        Assert.False(teardown.IsCompleted);
        hold.Set();
        await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Fact]
    public async Task TheTeardown_FlushesWhenTheLockIsFree_ThenShutsDownJoinsAndReleases_NeverDraining()
    {
        using var rig = new Rig();
        rig.Trusted();
        var run = rig.Start();
        await run.Encode(0, force: true);
        await run.Session.Abort().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var calls = PortCalls(rig.Catalog, run.Port).ToList();
        var flush = calls.Single(c => c.Detail == "FLUSH").Seq;
        var shutdown = calls.Single(c => c.Name == "Shutdown").Seq;
        var release = calls.Single(c => c.Name == "Release").Seq;
        Assert.True(flush < shutdown && shutdown < release);
        Assert.DoesNotContain(calls, c => c.Detail is "DRAIN" or "END_OF_STREAM");
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortWakesTheInputThread_IdleOrWithASampleAndNoCredit(bool queuedWithoutCredit)
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var run = rig.Start();
        await run.Encode(0, force: true);
        if (queuedWithoutCredit)
        {
            // Its credit taken by a frame whose output is held, then one more queued with none.
            entry.Encoder.HoldOutputs = true;
            run.Submit(1);
            await Eventually(() => run.Port.InputTimes.Count == 2, "the held frame's input");
            run.Submit(2);
            await Settle();
            Assert.Equal(2, run.Port.InputTimes.Count);
        }

        var mark = rig.Catalog.Mark("abort");
        var watch = Stopwatch.StartNew();
        var teardown = run.Session.Abort();
        await Eventually(() => teardown.IsCompleted, "the abort wakes the input thread and the teardown, which joins it, ends");
        Assert.True(watch.ElapsedMilliseconds < 100, $"the teardown took {watch.ElapsedMilliseconds} ms");
        var backends = new H264Backends([rig.Backend], NullLogger.Instance);
        Assert.True(await Quarantine.WatchAsync(teardown, rig.Backend, backends, rig.Time, NullLogger.Instance, TestContext.Current.CancellationToken));
        Assert.Equal(0, backends.Quarantines(rig.Backend));
        Assert.False(backends.IsGivenUp(rig.Backend));
        Assert.DoesNotContain(rig.Catalog.After(mark), c => c.Name == "ProcessInput" || c.Detail == "FORCE=1");
        var release = PortCalls(rig.Catalog, run.Port).Single(c => c.Name == "Release").Seq;
        Assert.InRange(ThreadEnd(rig.Catalog, "session"), mark, release);
        Assert.InRange(ThreadEnd(rig.Catalog, "events"), mark, release);
    }

    [Fact]
    public async Task AnInputThreadWokenInsideTheForce_ChecksTheAbortBeforeProcessInput()
    {
        using var rig = new Rig();
        var hold = new ManualResetEventSlim();
        var entry = rig.Trusted();
        var run = rig.Start();
        await run.Encode(0, force: true);
        entry.Encoder.HoldForce = hold;
        try
        {
            run.Submit(1, force: true);
            await Eventually(() => PortCalls(rig.Catalog, run.Port).Count(c => c.Detail == "FORCE=1") == 2, "the force is being sent");
            var mark = rig.Catalog.Mark("abort");
            var teardown = run.Session.Abort();
            hold.Set();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(rig.Catalog.After(mark), c => c.Name is "ProcessInput" or "CreateInputBuffer");
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task AStartWhoseEventThreadInitIsWedged_FailsAtTheBound_AndItsOneTeardownStaysOpenWhileThatThreadLives()
    {
        using var rig = new Rig();
        rig.Trusted();
        var enter = new ManualResetEventSlim();
        var shutdown = new ManualResetEventSlim();
        rig.Catalog.HoldEnter["events"] = enter;
        rig.Catalog.Entries[0].Encoder.HoldShutdown = shutdown;
        var eventThreads = rig.Catalog.Calls.Count(c => c.Name == "Enter" && c.Detail == "events");
        try
        {
            var start = Task.Run(() => rig.Backend.Start(new H264Config(W, H, 30, VideoStream.HardwareBitrate), _ => { }),
                TestContext.Current.CancellationToken);
            // The bound passes only once the new session's event thread is inside its wedged init, however late its worker.
            await Eventually(() => rig.Catalog.Calls.Count(c => c.Name == "Enter" && c.Detail == "events") == eventThreads + 1,
                "the event thread's init is entered");
            await AdvanceTo(rig.Time, MediaFoundationH264.StartBound);
            var refusal = await Assert.ThrowsAsync<H264StartException>(() => start);
            Assert.Equal(H264StartProblem.Fault, refusal.Problem);
            var teardown = refusal.Teardown!;
            await Settle();
            Assert.False(teardown.IsCompleted);

            // The stream counts it quarantined past the bound.
            var backends = new H264Backends([rig.Backend], NullLogger.Instance);
            var watch = Quarantine.WatchAsync(teardown, rig.Backend, backends, rig.Time, NullLogger.Instance, TestContext.Current.CancellationToken);
            await AdvanceTo(rig.Time, MediaFoundationH264.StartBound + Quarantine.Bound);
            Assert.False(await watch);
            Assert.Equal(1, backends.Quarantines(rig.Backend));

            // The shutdown let go, the teardown still waits for the event thread.
            shutdown.Set();
            await Settle();
            Assert.False(teardown.IsCompleted);
            enter.Set();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            enter.Set();
            shutdown.Set();
        }
    }

    [Fact]
    public async Task AStartWhoseEventThreadInitFails_IsAFaultAtOnce_ItsTeardownAfterTheShutdownJoinsAndRelease()
    {
        using var rig = new Rig();
        rig.Trusted();
        rig.Catalog.EnterHr["events"] = Mf.EFail;
        var refusal = rig.Refused();
        Assert.Equal(H264StartProblem.Fault, refusal.Problem);
        await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var port = rig.Catalog.Ports[^1];
        Assert.True(port.ShutDown && port.Released);
        var calls = PortCalls(rig.Catalog, port).ToList();
        Assert.True(calls.Single(c => c.Name == "Shutdown").Seq < calls.Single(c => c.Name == "Release").Seq);
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Fact]
    public async Task AStartFailingBeforeAnyNativeObject_HandsBackATeardownThatEndsAtOnce()
    {
        using var rig = new Rig();
        var refusal = rig.Refused();
        Assert.Equal(H264StartProblem.Fault, refusal.Problem);
        await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(rig.Catalog.Ports);
    }

    [Fact]
    public async Task AnActivationThatReturnsAfterTheStartBound_NeverConfiguresOrPublishes_AndIsShutDownAndReleased()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var hold = new ManualResetEventSlim();
        entry.Encoder.HoldActivate = hold;
        var outcomes = 0;
        var ports = entry.Ports.Count;
        try
        {
            var start = Task.Run(() => rig.Backend.Start(new H264Config(W, H, 30, VideoStream.HardwareBitrate), _ => Interlocked.Increment(ref outcomes)),
                TestContext.Current.CancellationToken);
            // The bound passes only once the start's activation is inside its hold, however late its worker.
            await Eventually(() => entry.Ports.Count == ports + 1 && PortCalls(rig.Catalog, entry.Last).Any(c => c.Name == "Activate"),
                "the start's activation is entered");
            await AdvanceTo(rig.Time, MediaFoundationH264.StartBound);
            var refusal = await Assert.ThrowsAsync<H264StartException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(H264StartProblem.Fault, refusal.Problem);
            var events = rig.Catalog.Calls.Count(c => c.Name == "Enter" && c.Detail == "events");

            hold.Set();
            await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var port = entry.Last;
            Assert.True(port.Activated && port.ShutDown && port.Released);
            Assert.False(port.Configured);
            Assert.Null(port.Output);
            Assert.Equal(events, rig.Catalog.Calls.Count(c => c.Name == "Enter" && c.Detail == "events"));
            Assert.Equal(0, Volatile.Read(ref outcomes));
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            hold.Set();
        }
    }

    // ---- the first output, forcing ----

    public static TheoryData<string> BadFirstOutputs => ["delta, forcing on", "delta, forcing off", "no parameter sets", "336x208"];

    [Theory]
    [MemberData(nameof(BadFirstOutputs))]
    public async Task ASessionsFirstOutput_MustBeAnIdrWithItsParameterSets_OfTheSessionsSize(string how)
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        entry.Encoder.IgnoreForce = how == "delta, forcing off";
        Assert.Equal(H264Probe.Available, rig.Probe());
        Assert.Equal(how != "delta, forcing off", rig.Backend.ChangesLive);
        switch (how)
        {
            case "delta, forcing on" or "delta, forcing off":
                entry.Encoder.FirstDelta = true;
                break;
            case "no parameter sets":
                entry.Encoder.IdrWithoutParameterSets = true;
                break;
            case "336x208":
                entry.Encoder.SpsSize = (336, 208);
                break;
        }

        var run = rig.Start(322, 194);
        var outcome = await run.Encode(0, force: true);
        Assert.Equal(H264OutcomeKind.Failed, outcome.Kind);
        Assert.Equal(H264Submit.Refused, run.Submit(1));
        Assert.DoesNotContain(run.Outcomes, o => o.Kind == H264OutcomeKind.Encoded);
    }

    [Fact]
    public async Task ALaterIdrWhoseNewSpsDescribesAnotherSize_Fails()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.SpsSize = (336, 208);
        entry.Encoder.SpsSizeFrom = 1;
        var run = rig.Start(322, 194);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        var outcome = await run.Encode(1, force: true);
        Assert.Equal(H264OutcomeKind.Failed, outcome.Kind);
        Assert.Contains("isn't the frame's size", outcome.Problem);
    }

    [Fact]
    public async Task AForce_GoesJustBeforeItsSamplesInput_AnIgnoredOneFails_AndTurnsForcingOffForGood()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var run = rig.Start();
        Assert.True((await run.Encode(0, force: true)).Keyframe);
        Assert.False((await run.Encode(1)).Keyframe);
        Assert.True((await run.Encode(2, force: true)).Keyframe);

        var calls = PortCalls(rig.Catalog, run.Port).ToList();
        var forces = calls.Where(c => c.Detail == "FORCE=1").ToList();
        var inputs = calls.Where(c => c.Name == "ProcessInput").ToList();
        Assert.Equal(2, forces.Count);
        Assert.True(forces[0].Seq < inputs[0].Seq && forces[1].Seq > inputs[1].Seq && forces[1].Seq < inputs[2].Seq);

        // An ignored force: a failure, and forcing off for the rest of the run.
        entry.Encoder.IgnoreForce = true;
        var ignored = await run.Encode(3, force: true);
        Assert.Equal(H264OutcomeKind.Failed, ignored.Kind);
        Assert.Contains("ignored a forced keyframe", ignored.Problem);
        Assert.False(rig.Backend.ChangesLive);

        var next = rig.Start();
        Assert.True((await next.Encode(0, force: true)).Keyframe);
        Assert.DoesNotContain(PortCalls(rig.Catalog, next.Port), c => c.Detail == "FORCE=1");
    }

    // ---- start and settings ----

    public static TheoryData<string, string> Stages => new()
    {
        { "output MF_E_INVALIDMEDIATYPE", "Unsupported" },
        { "output MF_E_TRANSFORM_TYPE_NOT_SET", "Unsupported" },
        { "output MF_E_UNSUPPORTED_D3D_TYPE", "Unsupported" },
        { "output E_INVALIDARG", "Unsupported" },
        { "output E_OUTOFMEMORY", "Busy" },
        { "output E_FAIL", "Fault" },
        { "input MF_E_INVALIDMEDIATYPE", "Unsupported" },
        { "input E_FAIL", "Fault" },
        { "begin MF_E_HW_MFT_FAILED_START_STREAMING", "Busy" },
        { "begin E_FAIL", "Fault" },
        { "async unlock refused", "Fault" },
        { "low latency refused", "Fault" },
        { "low latency reads back off", "Fault" },
        { "profile reads back 100", "Fault" },
        { "low latency GetValue E_NOTIMPL", "starts" },
        { "B-frames GetValue E_NOTIMPL", "starts" },
        { "B-frames refused", "starts" },
        { "GOP and the bit rate refused", "starts" },
    };

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task AStartsClass_FollowsTheStageAndItsHresult_AndRefusedHintsAreLogged(string stage, string expected)
    {
        using var rig = new Rig();
        var e = rig.Trusted().Encoder;
        switch (stage)
        {
            case "output MF_E_INVALIDMEDIATYPE":
                e.OutputTypeHr = _ => Mf.InvalidMediaType;
                break;
            case "output MF_E_TRANSFORM_TYPE_NOT_SET":
                e.OutputTypeHr = _ => Mf.TransformTypeNotSet;
                break;
            case "output MF_E_UNSUPPORTED_D3D_TYPE":
                e.OutputTypeHr = _ => Mf.UnsupportedD3DType;
                break;
            case "output E_INVALIDARG":
                e.OutputTypeHr = _ => Mf.EInvalidArg;
                break;
            case "output E_OUTOFMEMORY":
                e.OutputTypeHr = _ => Mf.EOutOfMemory;
                break;
            case "output E_FAIL":
                e.OutputTypeHr = _ => Mf.EFail;
                break;
            case "input MF_E_INVALIDMEDIATYPE":
                e.InputTypeHr = _ => Mf.InvalidMediaType;
                break;
            case "input E_FAIL":
                e.InputTypeHr = _ => Mf.EFail;
                break;
            case "begin MF_E_HW_MFT_FAILED_START_STREAMING":
                e.BeginHr = Mf.HwMftFailedStartStreaming;
                break;
            case "begin E_FAIL":
                e.BeginHr = Mf.EFail;
                break;
            case "async unlock refused":
                e.RefuseAttribute[Mf.TransformAsyncUnlock] = Mf.EFail;
                break;
            case "low latency refused":
                // Refused, and no read-back to fail it instead: the refusal alone is the Fault.
                e.RefuseCodec[Mf.CodecLowLatencyMode] = Mf.ENotImpl;
                e.CodecGetHr[Mf.CodecLowLatencyMode] = Mf.ENotImpl;
                break;
            case "low latency reads back off":
                e.CodecReadBack[Mf.CodecLowLatencyMode] = 0;
                break;
            case "profile reads back 100":
                e.ReadBackProfile = 100;
                break;
            case "low latency GetValue E_NOTIMPL":
                e.CodecGetHr[Mf.CodecLowLatencyMode] = Mf.ENotImpl;
                break;
            case "B-frames GetValue E_NOTIMPL":
                e.CodecGetHr[Mf.CodecBFrames] = Mf.ENotImpl;
                break;
            case "B-frames refused":
                e.RefuseCodec[Mf.CodecBFrames] = Mf.ENotImpl;
                break;
            case "GOP and the bit rate refused":
                e.RefuseCodec[Mf.CodecGopSize] = Mf.ENotImpl;
                e.RefuseCodec[Mf.CodecMeanBitRate] = Mf.ENotImpl;
                break;
        }

        if (expected != "starts")
        {
            var refusal = rig.Refused();
            Assert.Equal(expected, refusal.Problem.ToString());
            await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, rig.Catalog.Live);
            return;
        }

        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        if (stage.EndsWith("refused", StringComparison.Ordinal))
            Assert.Contains(rig.Log.Messages, m => m.Contains("refused hints"));
    }

    public static TheoryData<string> SetupStages =>
    [
        "Media Foundation's startup", "the listing", "the session thread's COM init", "activation", "async unlock", "a hint", "low latency", "low latency read-back", "output type",
        "output type read-back", "input type", "input layout", "input stream info", "output stream info", "begin", "start of stream",
        "the event thread's COM init",
    ];

    /// <summary>E_OUTOFMEMORY at one setup stage (on every port of the entry, and the catalog's startup, listing and thread inits).</summary>
    private static void OutOfMemoryAt(Rig rig, FakeEncoder e, string stage)
    {
        const int oom = Mf.EOutOfMemory;
        switch (stage)
        {
            case "Media Foundation's startup":
                rig.Catalog.StartupHr = oom;
                break;
            case "the listing":
                rig.Catalog.EnumerateHr = oom;
                break;
            case "the session thread's COM init":
                rig.Catalog.EnterHr["session"] = oom;
                break;
            case "activation":
                e.CallHr["Activate"] = oom;
                break;
            case "async unlock":
                e.RefuseAttribute[Mf.TransformAsyncUnlock] = oom;
                break;
            case "a hint":
                e.RefuseCodec[Mf.CodecGopSize] = oom;
                break;
            case "low latency":
                e.RefuseCodec[Mf.CodecLowLatencyMode] = oom;
                break;
            case "low latency read-back":
                e.CodecGetHr[Mf.CodecLowLatencyMode] = oom;
                break;
            case "output type":
                e.OutputTypeHr = _ => oom;
                break;
            case "output type read-back":
                e.CallHr["GetOutputCurrentType"] = oom;
                break;
            case "input type":
                e.InputTypeHr = _ => oom;
                break;
            case "input layout":
                e.CallHr["GetInputCurrentLayout"] = oom;
                break;
            case "input stream info":
                e.CallHr["GetInputStreamInfo"] = oom;
                break;
            case "output stream info":
                e.CallHr["GetOutputStreamInfo"] = oom;
                break;
            case "begin":
                e.BeginHr = oom;
                break;
            case "start of stream":
                e.CallHr["START_OF_STREAM"] = oom;
                break;
            case "the event thread's COM init":
                rig.Catalog.EnterHr["events"] = oom;
                break;
        }
    }

    [Theory]
    [MemberData(nameof(SetupStages))]
    public async Task OutOfMemory_AtAnySetupStage_IsBusy_InTheProbeAndAtAStart(string stage)
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        OutOfMemoryAt(rig, entry.Encoder, stage);
        // In the probe: Busy, which isn't cached, never Unavailable.
        Assert.Equal(H264Probe.Busy, rig.Probe());
        Assert.Null(rig.Backend.Trusted);

        // Free again: the probe passes; then a trusted start meeting it is Busy, never a Fault.
        entry.Encoder = new FakeEncoder();
        rig.Catalog.EnterHr.Clear();
        rig.Catalog.StartupHr = Mf.SOk;
        rig.Catalog.EnumerateHr = Mf.SOk;
        Assert.Equal(H264Probe.Available, rig.Probe());
        OutOfMemoryAt(rig, entry.Encoder, stage);
        var refusal = rig.Refused();
        Assert.Equal(H264StartProblem.Busy, refusal.Problem);
        await refusal.Teardown!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Catalog.Live);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("listing")]
    public void AnyOtherFailure_AtMediaFoundationsStartupOrListing_IsUnavailableInTheProbe_AndAFaultAtAStart(string stage)
    {
        using var rig = new Rig();
        rig.Entry();
        Assert.Equal(H264Probe.Available, rig.Probe());
        if (stage == "startup")
            rig.Catalog.StartupHr = Mf.EFail;
        else
            rig.Catalog.EnumerateHr = Mf.EFail;
        Assert.Equal(H264Probe.Unavailable, rig.Probe());
        Assert.Equal(H264StartProblem.Fault, rig.Refused().Problem);
    }

    [Fact]
    public void OutOfMemory_AtTheProbeThreadsComInit_IsBusy_AndAnyOtherFailureThereIsUnavailable()
    {
        using var rig = new Rig();
        rig.Entry();
        rig.Catalog.EnterHr["probe"] = Mf.EOutOfMemory;
        Assert.Equal(H264Probe.Busy, rig.Probe());
        rig.Catalog.EnterHr["probe"] = Mf.EFail;
        Assert.Equal(H264Probe.Unavailable, rig.Probe());
        rig.Catalog.EnterHr.Clear();
        Assert.Equal(H264Probe.Available, rig.Probe());
    }

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    public async Task AType_RefusedOnlyForItsColourAttributes_IsSetWithoutThem_KeepingItsFormatSizeAndProfile_AndTheHintIsLogged(string which)
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        Func<MfVideoType, int> noMatrix = t => t.Matrix != null ? Mf.InvalidMediaType : Mf.SOk;
        if (which == "output")
            entry.Encoder.OutputTypeHr = noMatrix;
        else
            entry.Encoder.InputTypeHr = noMatrix;
        Assert.Equal(H264Probe.Available, rig.Probe());

        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        var type = which == "output" ? run.Port.Output! : run.Port.Input!;
        Assert.Equal((which == "output" ? Mf.H264 : Mf.Nv12, W, H), (type.Subtype, type.Width, type.Height));
        Assert.Equal((uint?)null, type.Matrix);
        if (which == "output")
            Assert.Equal(Mf.ProfileBaseline, type.Profile);
        else
            Assert.Equal(W, type.DefaultStride);
        // The other type keeps its colour.
        Assert.Equal(Mf.MatrixBt709, (which == "output" ? run.Port.Input! : run.Port.Output!).Matrix);
        Assert.Contains(rig.Log.Messages, m => m.Contains("refused hints") && m.Contains($"BT.709 colour on the {which} type"));
    }

    [Fact]
    public void EveryStart_SetsTheOutputTypeBeforeTheInputType_AndNeverSendsADeviceManager()
    {
        using var rig = new Rig();
        rig.Trusted();
        var run = rig.Start();
        Assert.True(SeqOf(rig.Catalog, run.Port, "SetOutputType") < SeqOf(rig.Catalog, run.Port, "SetInputType"));
        Assert.True(SeqOf(rig.Catalog, run.Port, "SetUInt32", "ASYNC_UNLOCK") < SeqOf(rig.Catalog, run.Port, "SetOutputType"));
        Assert.True(SeqOf(rig.Catalog, run.Port, "SetInputType") < SeqOf(rig.Catalog, run.Port, "ProcessMessage", "BEGIN_STREAMING"));
        Assert.True(SeqOf(rig.Catalog, run.Port, "ProcessMessage", "BEGIN_STREAMING") < SeqOf(rig.Catalog, run.Port, "ProcessMessage", "START_OF_STREAM"));
        Assert.DoesNotContain(rig.Catalog.Calls, c => c.Detail == "SET_D3D_MANAGER");
        Assert.Equal(Mf.ProfileBaseline, run.Port.Output!.Profile);
        Assert.Equal(1u, run.Port.CodecSet[Mf.CodecLowLatencyMode]);
    }

    // ---- the input layout ----

    [Fact]
    public async Task ANegotiatedInputLayout_IsHonoured_StrideAlignmentAndMinimumSize()
    {
        using var rig = new Rig();
        var entry = rig.Entry();
        entry.Encoder.InputLayout = new MfInputLayout(322, 194, 384, null);
        entry.Encoder.InputInfo = (_, _) => new MfStreamInfo(0x7, 400_000, 64);
        Assert.Equal(H264Probe.Available, rig.Probe());

        var run = rig.Start(322, 194);
        var pixels = new byte[322 * 194 * 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 7);
        Assert.Equal(H264Submit.Accepted, run.Session.Submit(pixels, 322, 194, 0, true, out _));
        await Eventually(() => run.Outcomes.Count == 1, "the outcome");

        Assert.Equal((400_000, 64), Assert.Single(run.Port.InputBuffers));
        var bytes = Assert.Single(run.Port.InputBytes);
        Assert.Equal(384 * 194 * 3 / 2, bytes.Length);
        var expected = new byte[384 * 194 * 3 / 2];
        Nv12.Write(pixels, 322, 194, expected, new Nv12Layout(322, 194, 384));
        Assert.Equal(expected, bytes);
        Assert.All(bytes[322..384], b => Assert.Equal(16, b));
        Assert.All(bytes[(384 * 194 + 322)..(384 * 194 + 384)], b => Assert.Equal(128, b));
    }

    [Fact]
    public async Task TheDefaultLayout_StrideTheWidthAndAMinimumOfFourBytesAPixel_GivesPackedBuffersAllocatedAtThatMinimum()
    {
        using var rig = new Rig();
        rig.Trusted();
        var run = rig.Start(322, 194);
        await run.Encode(0, force: true);
        Assert.Equal((322 * 194 * 4, 1), Assert.Single(run.Port.InputBuffers));
        var bytes = Assert.Single(run.Port.InputBytes);
        var expected = new byte[322 * 194 * 3 / 2];
        Nv12.Write(Pixels(322, 194, 100), 322, 194, expected, Nv12Layout.Packed(322, 194));
        Assert.Equal(expected, bytes);
    }

    public static TheoryData<string> UnwritableLayouts => ["stride -336", "stride 320", "alignment 48", "frame 336x208", "sample size below"];

    private static void Unwritable(FakeEncoder e, string how, int width, int height)
    {
        switch (how)
        {
            case "stride -336":
                e.InputLayout = new MfInputLayout(width, height, -336, null);
                break;
            case "stride 320":
                e.InputLayout = new MfInputLayout(width, height, width - 2, null);
                break;
            case "alignment 48":
                e.InputInfo = (w, h) => new MfStreamInfo(0, w * h * 4, 48);
                break;
            case "frame 336x208":
                e.InputLayout = new MfInputLayout(336, 208, 336, null);
                break;
            case "sample size below":
                e.InputLayout = new MfInputLayout(width, height, width, width * height);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(UnwritableLayouts))]
    public void AnInputLayoutThisEngineDoesntWrite_FailsThatEntryInTheProbe_AndIsUnsupportedAtAStart(string how)
    {
        using (var rig = new Rig())
        {
            var bad = rig.Entry("Bad", clsid: C2);
            Unwritable(bad.Encoder, how, 322, 194);
            rig.Entry("Good");
            Assert.Equal(H264Probe.Available, rig.Probe());
            Assert.Equal("Good", rig.Backend.Trusted!.HardwareUrl);
            Assert.Contains(rig.Log.Messages, m => m.Contains("Bad") && m.Contains("input layout"));
        }

        using (var rig = new Rig())
        {
            var entry = rig.Trusted();
            Unwritable(entry.Encoder, how, W, H);
            var refusal = rig.Refused();
            Assert.Equal(H264StartProblem.Unsupported, refusal.Problem);
            Assert.Contains("input layout", refusal.Message);
        }
    }

    // ---- events and output samples ----

    [Fact]
    public async Task CreditsInABurst_LetSeveralInputsIn_AndTheirOutputsPair()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.InitialCredits = 3;
        entry.Encoder.CreditPerOutput = false;
        entry.Encoder.HoldOutputs = true;
        var run = rig.Start();
        for (var i = 0; i < 4; i++)
            Assert.Equal(H264Submit.Accepted, run.Submit(i, force: i == 0));
        await Eventually(() => run.Port.InputTimes.Count == 3, "three inputs");
        await Settle();
        Assert.Equal(3, run.Port.InputTimes.Count);

        run.Port.ReleaseOutputs();
        await Eventually(() => run.Outcomes.Count == 3, "three outcomes");
        Assert.Equal([0L, 1, 2], run.Outcomes.Select(o => o.FrameId));
        Assert.All(run.Outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        Assert.Equal(0, run.Port.InputsWithoutCredit);
    }

    [Fact]
    public async Task AnUnknownEvent_IsLoggedOnce_AndIgnored()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.ExtraEvents.AddRange([999u, 999u, 604u]);
        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(1)).Kind);
        Assert.Single(rig.Log.Messages, m => m.Contains("event 999"));
        Assert.Single(rig.Log.Messages, m => m.Contains("event 604"));
    }

    [Fact]
    public async Task AFailingStatus_OnAnEventOfAnyType_FailsTheSession()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        entry.Encoder.HoldOutputs = true;
        Assert.Equal(H264Submit.Accepted, run.Submit(1));
        await Eventually(() => run.Port.InputTimes.Count == 2, "frame 1 is in the encoder");

        // An event of a type the session doesn't know, with a failing status: a failure, not a log line.
        run.Port.Send(new MfEvent(999, Mf.EFail));
        await Eventually(() => run.Outcomes.Any(o => o.FrameId == 1), "frame 1's outcome");
        var outcome = run.Outcomes.Single(o => o.FrameId == 1);
        Assert.Equal(H264OutcomeKind.Failed, outcome.Kind);
        Assert.Contains("event 999", outcome.Problem);
        Assert.Equal(H264Submit.Refused, run.Submit(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputSamples_AreOursOfTheStreamInfosSizeWhenTheEncoderDoesntProvideThem_ReleasedAfterEachCopy(bool encoderProvides)
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        if (!encoderProvides)
            entry.Encoder.OutputInfo = new MfStreamInfo(0x7, 1_000_000, 16);
        var run = rig.Start();
        var live = rig.Catalog.Live;
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(i, force: i == 0)).Kind);
            Assert.Equal(live, rig.Catalog.Live);
        }

        if (encoderProvides)
            Assert.Empty(run.Port.OursMade);
        else
            Assert.All(run.Port.OursMade, m => Assert.Equal((1_000_000, 16), m));
        Assert.Equal(encoderProvides ? 0 : 50, run.Port.OursMade.Count);
    }

    [Fact]
    public async Task AnEncoderError_IsAFailure_AndEverySessionLeavesNothingLive()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.ErrorAfter = 1;
        entry.Encoder.HoldOutputs = false;
        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);
        await Eventually(() => run.Submit(99) == H264Submit.Refused, "the session is broken");
        await run.Session.Abort().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Catalog.Live);

        // Ten more sessions, each ended: nothing left.
        entry.Encoder.ErrorAfter = -1;
        for (var i = 0; i < 10; i++)
        {
            var next = rig.Start();
            await next.Encode(0, force: true);
            await next.Session.Abort().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, rig.Catalog.Live);
        }
    }

    // ---- stream changes ----

    [Fact]
    public async Task AStreamChange_ToH264BaselineOfTheSessionsSize_IsRenegotiated_AndTheIdrPairs()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        entry.Encoder.StreamChanges = 2;
        var run = rig.Start();
        var outcome = await run.Encode(0, force: true);
        Assert.Equal(H264OutcomeKind.Encoded, outcome.Kind);
        Assert.True(outcome.Keyframe);
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(1)).Kind);
        Assert.Equal(2, PortCalls(rig.Catalog, run.Port).Count(c => c.Name == "GetOutputAvailableType"));
        Assert.Equal(0, run.Port.StaleAllocations);
    }

    public static TheoryData<string> RefusedChanges => ["another size", "NV12", "profile 100", "a third change"];

    [Theory]
    [MemberData(nameof(RefusedChanges))]
    public async Task AStreamChange_OutsideTheRule_Fails(string how)
    {
        using var rig = new Rig();
        var e = rig.Trusted().Encoder;
        e.StreamChanges = how == "a third change" ? 3 : 1;
        e.ChangeTo = how switch
        {
            "another size" => (w, h) => new MfVideoType(Mf.H264, w + 16, h) { Profile = 66 },
            "NV12" => (w, h) => new MfVideoType(Mf.Nv12, w, h),
            "profile 100" => (w, h) => new MfVideoType(Mf.H264, w, h) { Profile = 100 },
            _ => null,
        };
        var run = rig.Start();
        var outcome = await run.Encode(0, force: true);
        Assert.Equal(H264OutcomeKind.Failed, outcome.Kind);
        Assert.Contains("stream change", outcome.Problem);
    }

    [Fact]
    public async Task AnAbortDuringAStreamChange_TouchesTheTransformNoMore_AndRenegotiatesNoFurther()
    {
        using var rig = new Rig();
        var e = rig.Trusted().Encoder;
        var inside = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        e.StreamChanges = 1;
        e.ChangeTo = (w, h) =>
        {
            inside.Set();
            gate.Wait();
            return new MfVideoType(Mf.H264, w, h) { Profile = Mf.ProfileBaseline };
        };
        try
        {
            var run = rig.Start();
            Assert.Equal(H264Submit.Accepted, run.Submit(0, force: true));
            await Eventually(() => inside.IsSet, "the stream change's output type is being read");

            // Aborted inside the renegotiation, which holds the MFT lock: no flush, then the shutdown.
            var mark = rig.Catalog.Mark("abort");
            var teardown = run.Session.Abort();
            await AdvanceTo(rig.Time, MediaFoundationH264.FlushLockWait);
            await Eventually(() => run.Port.ShutDown, "the shutdown");
            gate.Set();
            await teardown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.DoesNotContain(rig.Catalog.After(mark),
                c => c.Port == run.Port.Id && c.Name is "SetOutputType" or "GetOutputStreamInfo" or "GetOutputAvailableType" or "ProcessOutput");
            Assert.Equal([(0L, H264OutcomeKind.Aborted)], run.Outcomes.Select(o => (o.FrameId, o.Kind)));
            Assert.Equal(0, rig.Catalog.Live);
        }
        finally
        {
            gate.Set();
        }
    }

    public static TheoryData<uint, int, int, uint, int, int> Allocations => new()
    {
        { 0x103, 0, 0, 0x7, 1 << 20, 16 },
        { 0x7, 4096, 16, 0x103, 0, 0 },
        { 0x7, 4096, 16, 0x7, 1 << 21, 64 },
    };

    [Theory]
    [MemberData(nameof(Allocations))]
    public async Task AfterAStreamChange_TheOutputAllocationFollowsTheNewStreamInfo(uint flags, int size, int alignment, uint newFlags, int newSize, int newAlignment)
    {
        using var rig = new Rig();
        var e = rig.Trusted().Encoder;
        e.OutputInfo = new MfStreamInfo(flags, size, alignment);
        e.StreamChanges = 1;
        e.ChangeInfo = new MfStreamInfo(newFlags, newSize, newAlignment);
        var run = rig.Start();
        Assert.Equal(H264OutcomeKind.Encoded, (await run.Encode(0, force: true)).Kind);

        var calls = PortCalls(rig.Catalog, run.Port).ToList();
        var outputs = calls.Where(c => c.Name == "ProcessOutput").ToList();
        var expected = (newFlags & 0x300) != 0 ? "theirs" : $"ours {newSize}/{newAlignment}";
        Assert.Equal(expected, outputs[1].Detail);
        var set = calls.Last(c => c.Name == "SetOutputType").Seq;
        var info = calls.Last(c => c.Name == "GetOutputStreamInfo").Seq;
        Assert.True(set < info && info < outputs[1].Seq);
        Assert.Equal(0, run.Port.StaleAllocations);
    }

    // ---- the output type's sequence header: never read, the parameter sets come from the stream ----

    [Fact]
    public async Task AnOfferedSequenceHeader_ThatDiffersFromTheInBandSps_IsNeverReadIntoTheAvccOrTheUnits()
    {
        using var rig = new Rig();
        var entry = rig.Trusted();
        var header = Convert.FromHexString(AnnexBTests.AmdSequenceHeader);
        entry.Encoder.Sps = Convert.FromHexString(AnnexBTests.AmdSps);
        entry.Encoder.Pps = Convert.FromHexString(AnnexBTests.AmdPps);
        entry.Encoder.OfferedHeader = header;
        var run = rig.Start(1600, 960);

        var key = await run.Encode(0, force: true);
        Assert.Equal(H264OutcomeKind.Encoded, key.Kind);
        Assert.True(key.Keyframe);
        Assert.Equal(Convert.FromHexString(AnnexBTests.AmdAvcc), key.Avcc);
        var headerSps = Convert.ToHexString(header[4..36]);
        Assert.DoesNotContain(headerSps, Convert.ToHexString(key.Avcc!), StringComparison.Ordinal);
        Assert.DoesNotContain(headerSps, Convert.ToHexString(key.Data!), StringComparison.Ordinal);
        Assert.DoesNotContain(AnnexBTests.AmdSps, Convert.ToHexString(key.Data!), StringComparison.Ordinal);

        var delta = await run.Encode(1);
        Assert.Equal(H264OutcomeKind.Encoded, delta.Kind);
        Assert.DoesNotContain(headerSps, Convert.ToHexString(delta.Data!), StringComparison.Ordinal);
    }
}
