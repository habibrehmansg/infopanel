using InfoPanel.Preview.Video;
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

    private sealed class Fakes : IDisposable
    {
        private readonly List<MediaFoundationH264> _made = [];
        private readonly Action<FakeEncoder>? _beforeProbe;
        private readonly Action<FakeEncoder>? _afterProbe;

        /// <param name="beforeProbe">The encoder's behaviour before each backend's probe.</param>
        /// <param name="afterProbe">The encoder's behaviour after each backend's probe passed.</param>
        public Fakes(string? deviceKey = Key, Action<FakeEncoder>? beforeProbe = null, Action<FakeEncoder>? afterProbe = null)
        {
            Entry = Catalog.Add("Encoder", deviceKey: deviceKey);
            _beforeProbe = beforeProbe;
            _afterProbe = afterProbe;
        }

        public FakeMfCatalog Catalog { get; } = new();

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
            var backend = new MediaFoundationH264(Catalog, log);
            lock (_made)
                _made.Add(backend);
            Assert.Equal(H264Probe.Available, backend.Probe());
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
        MediaFoundationRestartChecks.OnOneActivation(fakes.Bed);
        MediaFoundationRestartChecks.Fallback(fakes.Bed);
    }

    /// <summary>The whole identity unknown: re-use is never eligible, and every start takes three activations (the candidate, its probe, the final one).</summary>
    [Fact]
    public void WithoutTheWholeIdentity_BothChecksCountThreeActivationsAStart_AndPass()
    {
        using var fakes = new Fakes(deviceKey: null);
        MediaFoundationRestartChecks.OnOneActivation(fakes.Bed);
        MediaFoundationRestartChecks.Fallback(fakes.Bed);
    }

    [Fact]
    public void WithoutForcedKeyframes_BothChecksTakeTheFallback_AndPass()
    {
        using var fakes = new Fakes(beforeProbe: e => e.IgnoreForce = true);
        MediaFoundationRestartChecks.OnOneActivation(fakes.Bed);
        MediaFoundationRestartChecks.Fallback(fakes.Bed);
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
        MediaFoundationRestartChecks.OnOneActivation(fakes.Bed);
    }

    /// <summary>
    /// Forced keyframes not working and every session's output malformed (its SPS another size): every frame fails,
    /// while the activations, the cleanup and the logs look like the fallback's. Both checks must fail it.
    /// </summary>
    [Fact]
    public void WhenEveryFrameFails_BothChecksFail()
    {
        using var fakes = new Fakes(beforeProbe: e => (e.IgnoreForce, e.SpsSize) = (true, null), afterProbe: e => e.SpsSize = (336, 208));
        var one = Assert.ThrowsAny<Exception>(() => MediaFoundationRestartChecks.OnOneActivation(fakes.Bed));
        Assert.Contains("a restart at 128x128: Failed", one.Message, StringComparison.Ordinal);
        var fallback = Assert.ThrowsAny<Exception>(() => MediaFoundationRestartChecks.Fallback(fakes.Bed));
        Assert.Contains("a restart at 128x128: Failed", fallback.Message, StringComparison.Ordinal);
    }
}
