using InfoPanel.Preview.Video;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The 200-restart checks' own logic (<see cref="MediaFoundationRestartChecks"/>), on any OS over the fakes, in every
/// mode an encoder can put them in: re-use; the whole identity unknown (three activations a start); forced keyframes
/// not working; re-use refused during the warm-up (its retyping or its drain); and every frame failing, which both
/// checks must reject. The fake catalog's live objects stand in for the process's handles; fewer restarts than on a
/// PC, the same logic.
/// </summary>
public sealed class MediaFoundationRestartCheckTests
{
    private const string Key = @"PCI\VEN_1002&DEV_1900&SUBSYS_00000000&REV_C1\4&1";
    private const string OneActivation = "the check on one activation";
    private const string Fallback = "the fallback's check";

    private sealed class Fakes : IDisposable
    {
        private readonly List<MediaFoundationH264> _made = [];
        private readonly Action<FakeEncoder>? _beforeProbe;
        private readonly Action<FakeEncoder>? _afterProbe;

        private readonly TimeSpan _probeCap;

        /// <param name="beforeProbe">The encoder's behaviour before each backend's probe.</param>
        /// <param name="afterProbe">The encoder's behaviour after each backend's probe passed.</param>
        /// <param name="probeCap">Each backend's probe's real-time cap.</param>
        public Fakes(string? deviceKey = Key, Action<FakeEncoder>? beforeProbe = null, Action<FakeEncoder>? afterProbe = null, TimeSpan? probeCap = null)
        {
            Entry = Catalog.Add("Encoder", deviceKey: deviceKey);
            Time = new MarkedTime(new ManualTime(DateTimeOffset.UnixEpoch), Catalog);
            _beforeProbe = beforeProbe;
            _afterProbe = afterProbe;
            _probeCap = probeCap ?? TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// <paramref name="check"/> over <see cref="Bed"/>, capped in real time (its sessions' starts and the probes
        /// inside them aren't bounded by the clock), rethrowing what it throws.
        /// </summary>
        public void Check(Action<RestartBed> check, string name) => RealTimeCap.Run(() => check(Bed), name, TimeSpan.FromMinutes(2));

        public FakeMfCatalog Catalog { get; } = new();

        /// <summary>
        /// The backends' clock, which never moves: their bounds (a teardown's waits for the flush, the session's thread
        /// and the last event's handover, a kept activation's time) end only with what they wait for, never with the
        /// machine's load. On the real clock a thread unscheduled past such a 250 ms wait let the teardown shut down an
        /// activation it would have kept, and the next start activated again. A wait that never ends still fails the
        /// check, by its real-time caps (each restart's teardown, the reap).
        /// </summary>
        public MarkedTime Time { get; }

        public FakeMfEntry Entry { get; }

        public RestartBed Bed => new()
        {
            Backend = Backend,
            Compacted = () => (Catalog.Live, 0),
            Resources = () => (Catalog.Live, 0),
            Wrappers = () => 0,
            Threads = () => 0,
            Managed = () => 0,
            Catalog = () => Catalog,
            WarmUp = 9,
            Restarts = 30,
        };

        /// <summary>A backend over the fakes whose probe passed, the ones before it stopped first (one at a time, as in a run).</summary>
        private MediaFoundationH264 Backend(ILogger log, bool refuseReuse)
        {
            Reap();
            if (refuseReuse)
                Entry.Encoder.TypesAfterEndStreamingHr = Mf.InvalidMediaType;
            _beforeProbe?.Invoke(Entry.Encoder);
            var backend = new MediaFoundationH264(Catalog, log, Time);
            lock (_made)
                _made.Add(backend);
            Assert.Equal(H264Probe.Available, RealTimeCap.Run(backend.Probe, "the backend's probe", _probeCap));
            _afterProbe?.Invoke(Entry.Encoder);
            return backend;
        }

        private void Reap()
        {
            MediaFoundationH264[] made;
            lock (_made)
            {
                made = [.. _made];
                _made.Clear();
            }

            foreach (var backend in made)
                Assert.True(backend.ReapAsync(TimeSpan.FromSeconds(5), CancellationToken.None).Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        public void Dispose() => Reap();
    }

    [Fact]
    public void AnEncoderThatReuses_PassesBothChecks()
    {
        using var fakes = new Fakes();
        fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation);
        fakes.Check(MediaFoundationRestartChecks.Fallback, Fallback);
    }

    /// <summary>
    /// Every session's thread slow to end, as a loaded machine leaves it: its last step waits until its teardown has
    /// either given it up (a shutdown) or waits for it on the backend's clock. Both checks still pass: a healthy session's
    /// activation is kept however long its thread takes in real time.
    /// </summary>
    [Fact]
    public void AnEncoderThatReuses_PassesBothChecks_WithEverySessionsThreadSlowToEnd()
    {
        using var fakes = new Fakes();
        fakes.Catalog.OnLeave = role =>
        {
            if (role != "session")
                return;
            // Since this thread's start: the teardown may have begun before the thread reached its last step.
            var thread = Environment.CurrentManagedThreadId;
            var start = fakes.Catalog.Calls.Last(c => c.Name == "Enter" && c.Detail == "session" && c.Thread == thread).Seq;
            // Capped in real time, so a teardown that never reaches either does not hang the run (the check then fails).
            SpinWait.SpinUntil(() => MarkedTime.GaveUpOrWaits(fakes.Catalog, start, notOn: thread), TimeSpan.FromSeconds(10));
        };
        fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation);
        fakes.Check(MediaFoundationRestartChecks.Fallback, Fallback);
    }

    /// <summary>The whole identity unknown: re-use is never eligible, and every start takes three activations (the candidate, its probe, the final one).</summary>
    [Fact]
    public void WithoutTheWholeIdentity_BothChecksCountThreeActivationsAStart_AndPass()
    {
        using var fakes = new Fakes(deviceKey: null);
        fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation);
        fakes.Check(MediaFoundationRestartChecks.Fallback, Fallback);
    }

    [Fact]
    public void WithoutForcedKeyframes_BothChecksTakeTheFallback_AndPass()
    {
        using var fakes = new Fakes(beforeProbe: e => e.IgnoreForce = true);
        fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation);
        fakes.Check(MediaFoundationRestartChecks.Fallback, Fallback);
    }

    /// <summary>
    /// Eligible at the probe, then the encoder refuses a re-use in the warm-up (its retyping, or its drain): the
    /// production fallback (a new activation, re-use off, the refusal logged once), which the check accepts as the fallback.
    /// </summary>
    [Theory]
    [InlineData("retyping")]
    [InlineData("drain")]
    public void ReuseRefusedInTheWarmUp_TheCheckHoldsTheFallbacksContract(string refused)
    {
        using var fakes = new Fakes(afterProbe: e =>
        {
            if (refused == "retyping")
                e.TypesAfterEndStreamingHr = Mf.InvalidMediaType;
            else
                e.CallHr["DRAIN"] = Mf.EFail;
        });
        fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation);
    }

    /// <summary>
    /// Forced keyframes not working and every session's output malformed (its SPS another size): every frame fails,
    /// while the activations, the cleanup and the logs look like the fallback's. Both checks must fail it.
    /// </summary>
    [Fact]
    public void WhenEveryFrameFails_BothChecksFail()
    {
        using var fakes = new Fakes(beforeProbe: e => (e.IgnoreForce, e.SpsSize) = (true, null), afterProbe: e => e.SpsSize = (336, 208));
        var one = Assert.ThrowsAny<Exception>(() => fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation));
        Assert.Contains("a restart at 128x128: Failed", one.Message, StringComparison.Ordinal);
        var fallback = Assert.ThrowsAny<Exception>(() => fakes.Check(MediaFoundationRestartChecks.Fallback, Fallback));
        Assert.Contains("a restart at 128x128: Failed", fallback.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A probe's shutdown that never returns, on the clock that never moves: no bound of the backend's ends the probe,
    /// and its real-time cap fails the check, naming the call, instead of hanging the run.
    /// </summary>
    [Fact]
    public void AProbeWedgedOnTheFrozenClock_FailsTheCheckByItsCap()
    {
        using var shutdown = new ManualResetEventSlim();
        using var fakes = new Fakes(beforeProbe: e => e.HoldShutdown = shutdown, probeCap: TimeSpan.FromSeconds(1));
        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => fakes.Check(MediaFoundationRestartChecks.OnOneActivation, OneActivation));
            Assert.Contains("the backend's probe didn't end within 1 s", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            shutdown.Set();
        }
    }
}
