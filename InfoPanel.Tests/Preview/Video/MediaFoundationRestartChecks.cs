using System.Diagnostics;
using InfoPanel.Preview.Video;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// Where the restart checks run: the real encoder (<see cref="MediaFoundationTests"/>, the process's handles and bytes)
/// or the fakes (<see cref="MediaFoundationRestartCheckTests"/>, the fake catalog's live objects as its handles), so the
/// checks' own logic is held to every mode an encoder can put them in, those the PC's encoder never shows included.
/// </summary>
internal sealed class RestartBed
{
    /// <summary>A backend whose probe has passed; the flag: every re-use's renegotiation is refused.</summary>
    public required Func<ILogger, bool, MediaFoundationH264> Backend { get; init; }

    /// <summary>Handles and private bytes after an aggressive, compacting collection.</summary>
    public required Func<(long Handles, long Bytes)> Compacted { get; init; }

    /// <summary>Handles and private bytes after a settle.</summary>
    public required Func<(long Handles, long Bytes)> Resources { get; init; }

    /// <summary>The port's wrappers not yet given back.</summary>
    public required Func<long> Wrappers { get; init; }

    public required Func<int> Threads { get; init; }

    /// <summary>The managed heap after a full collection.</summary>
    public required Func<long> Managed { get; init; }

    /// <summary>The catalog the bare cycles run on (a new one each call).</summary>
    public required Func<IMfCatalog> Catalog { get; init; }

    public int WarmUp { get; init; } = 100;

    public int Restarts { get; init; } = 200;
}

/// <summary>The 200-restart checks, over a <see cref="RestartBed"/>.</summary>
internal static class MediaFoundationRestartChecks
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Refusal = "re-using the encoder's activation was refused";

    // The refusal's consequence, logged once: re-use off for the run.
    private const string ReuseOff = "activations aren't kept for the rest of the run";

    private static void Note(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    internal static byte[] Solid(int width, int height, (byte R, byte G, byte B) colour)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.R, colour.G, colour.B, 255);
        return pixels;
    }

    /// <summary>Each frame in turn on one session (the first forced), each outcome awaited before the next: a held frame fails.</summary>
    internal static List<H264Outcome> Encode(IH264Backend backend, int width, int height, IReadOnlyList<byte[]> frames, Func<int, TimeSpan>? gapBefore = null,
        Func<int, bool>? forced = null)
    {
        var outcomes = new List<H264Outcome>();
        var arrived = new SemaphoreSlim(0);
        var session = backend.Start(new H264Config(width, height, 30, VideoStream.HardwareBitrate), outcome =>
        {
            lock (outcomes)
                outcomes.Add(outcome);
            arrived.Release();
        });
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (gapBefore?.Invoke(i) is { } gap && gap > TimeSpan.Zero)
                    Thread.Sleep(gap);
                Assert.Equal(H264Submit.Accepted, session.Submit(frames[i], width, height, i, forced?.Invoke(i) ?? i == 0, out var problem));
                Assert.Null(problem);
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), $"frame {i}'s outcome arrived without another frame");
            }
        }
        finally
        {
            Assert.True(session.Abort().Wait(Quarantine.Bound, Ct), "the session tears down");
        }

        lock (outcomes)
            return [.. outcomes];
    }

    /// <summary>One restart: a session at <paramref name="side"/> a side, whose one frame must come out encoded.</summary>
    private static void Restart(IH264Backend backend, int side)
    {
        foreach (var outcome in Encode(backend, side, side, [Solid(side, side, (1, 2, 3))]))
            Assert.True(outcome.Kind == H264OutcomeKind.Encoded, $"a restart at {side}x{side}: {outcome.Kind} ({outcome.Problem})");
    }

    private static int Side(int i) => 128 + (i % 3) * 2;

    internal static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int Refusals(RecordingLogger log) => log.Messages.Count(m => m.Contains(Refusal, StringComparison.Ordinal));

    /// <summary>Activations a start takes on the fallback: the trusted one, or, its whole identity unknown, a candidate, its mandatory probe and the final one.</summary>
    private static int PerStart(MediaFoundationH264 backend) => backend.Trusted is { Complete: true } ? 1 : 3;

    /// <summary>
    /// 200 restarts over three sizes on one activation: the first session's activation is kept, and each next start
    /// renegotiates it, so the restarts activate nothing more, and the process's handles and private bytes stay flat: at
    /// most +10 handles and 5 MB over the 200 (an activation a restart, as without re-use, keeps about 3 handles and
    /// 12–121 KB each on a real encoder), with every wrapper given back, the threads within 10 and the managed heap within
    /// 5 MB. Every restart's frame comes out encoded. The mode is read after the warm-up: where the probe doesn't allow
    /// re-use (the whole identity unknown, or forced keyframes not working) nothing is tried or refused; where it does and
    /// the encoder refused a re-use during the warm-up (its drain or its retyping), the refusal is logged once and re-use
    /// is off (logged once too). Either way the restarts then take the fallback, held to <see cref="FallbackRestarts"/>'s
    /// contract, and nothing else is logged refused or failed.
    /// </summary>
    public static void OnOneActivation(RestartBed bed)
    {
        var log = new RecordingLogger();
        var backend = bed.Backend(log, false);
        var eligible = backend.ReuseEligible;
        Note($"re-use {(eligible ? "eligible" : "not eligible")} at the probe: forced keyframes {(backend.ChangesLive ? "work" : "don't work")}, identity {backend.Trusted}");
        // A warm-up of 100 restarts over the three sizes first: what the process allocates once isn't growth with
        // restarts. Measured on a real encoder, private bytes rise 10–12 MB over a run's first 100 restarts (the
        // allocators' working sets) and then stay flat: −0.8 to −1.6 MB over the next 100.
        for (var i = 0; i < bed.WarmUp; i++)
            Restart(backend, Side(i));
        var reuse = backend.ReuseEligible;
        Assert.Equal(reuse, backend.Parked != null);
        if (!reuse)
        {
            var refusals = Refusals(log);
            Note($"after the warm-up: the fallback ({refusals} refusal logged), forced keyframes {(backend.ChangesLive ? "work" : "don't work")}");
            // Never eligible: nothing tried, nothing refused. Eligible, then off: one refusal logged, or forcing turned off.
            if (eligible)
                Assert.True(refusals == 1 || (refusals == 0 && !backend.ChangesLive), $"re-use turned off in the warm-up with {refusals} refusals logged");
            else
                Assert.Equal(0, refusals);
            FallbackRestarts(bed, backend, log);
            Assert.DoesNotContain(log.Messages, m => !m.Contains(Refusal, StringComparison.Ordinal) && !m.Contains(ReuseOff, StringComparison.Ordinal)
                && (m.Contains("refused", StringComparison.Ordinal) || m.Contains("failed", StringComparison.Ordinal)));
            return;
        }

        Settle();
        var (threads, live, activations) = (bed.Threads(), bed.Wrappers(), backend.Activations);
        var managedBefore = bed.Managed();
        var (handles, bytes) = bed.Compacted();
        long half = 0;
        for (var i = 0; i < bed.Restarts; i++)
        {
            Restart(backend, Side(i));
            if (i == bed.Restarts / 2 - 1)
                half = bed.Compacted().Bytes - bytes;
        }

        var (handlesAfter, bytesAfter) = bed.Compacted();
        Note($"private bytes after {bed.Restarts / 2} restarts {half:+#;-#;0}, after {bed.Restarts} {bytesAfter - bytes:+#;-#;0}");
        WrappersGivenBack(bed, live);
        var managedAfter = bed.Managed();
        var threadsAfter = bed.Threads();
        Note($"threads {threads} -> {threadsAfter}");
        Note($"{bed.Restarts} restarts on one activation: activations {backend.Activations - activations:+#;-#;0}, handles {handlesAfter - handles:+#;-#;0} (held to +10), private bytes {bytesAfter - bytes:+#;-#;0} (held to 5 MB)");
        Note($"managed heap {managedBefore} -> {managedAfter} ({managedAfter - managedBefore:+#;-#;0}, held to 5 MB)");
        Assert.DoesNotContain(log.Messages, m => m.Contains("refused", StringComparison.Ordinal) || m.Contains("failed", StringComparison.Ordinal));
        Assert.InRange(threadsAfter - threads, int.MinValue, 10);
        Assert.InRange(managedAfter - managedBefore, long.MinValue, 5L << 20);
        Assert.Equal(activations, backend.Activations);
        Assert.NotNull(backend.Parked);
        Assert.InRange(handlesAfter - handles, long.MinValue, 10);
        Assert.InRange(bytesAfter - bytes, long.MinValue, 5L << 20);
    }

    /// <summary>
    /// The fallback's restarts, the encoder refusing every renegotiation: the warm-up's second start has its re-use
    /// refused and a new activation starts, after which nothing is kept, so each restart takes new activations, as
    /// without re-use. Where the probe allowed re-use the refusal is logged once; where it doesn't, nothing is kept, so no
    /// re-use is tried or refused. Held to <see cref="FallbackRestarts"/>'s contract.
    /// </summary>
    public static void Fallback(RestartBed bed)
    {
        var log = new RecordingLogger();
        var backend = bed.Backend(log, true);
        var eligible = backend.ReuseEligible;
        Note($"re-use {(eligible ? "eligible, refused" : "not eligible")}: forced keyframes {(backend.ChangesLive ? "work" : "don't work")}, identity {backend.Trusted}");
        // Sizes every hardware encoder takes: 128 a side is the smallest some accept (AMD's). The second start's re-use
        // of the first one's activation is refused, and a new activation starts: nothing is kept from then on.
        Restart(backend, 128);
        Restart(backend, 130);
        Assert.Null(backend.Parked);
        FallbackRestarts(bed, backend, log);
        Assert.Equal(eligible ? 1 : 0, Refusals(log));
    }

    /// <summary>
    /// 200 restarts on the fallback give back every wrapper, thread and handle within what the encoder itself keeps, and
    /// the managed heap: an encoder may keep a few handles per activation after its transform and activation object are
    /// released (seen on a real one: a registry key, an event and a wait packet per activation, with nothing of the
    /// engine's involved). Each restart takes <see cref="PerStart"/> activations (three where the whole identity is
    /// unknown: the candidate, its mandatory probe and the final one), and each frame comes out encoded. So the 200
    /// restarts and bare cycles of the port's raw calls (activate, the types, three frames, shut down, release; no session
    /// code), as many a restart as its activations, interleave, restart then bare cycles, each step's handle change summed
    /// into its own total, so both totals sample the same period. A GC settle (collect, wait for the finalisers, collect)
    /// comes before every read, since handles that finalisers free land in whichever step runs. The restarts' handles may
    /// grow by no more than the bare total (negative as 0) plus 50 an activation a restart, a quarter of a handle an
    /// activation: one handle a session keeps per restart (+200) still fails by four times, while two settled windows of
    /// identical work differ by up to about 22 on a real encoder. The memory check is the managed heap, which holds none
    /// of the encoder's native memory: after the settle it grows by at most 5 MB. Private bytes are printed, not asserted:
    /// they measure the encoder's and the allocator's memory more than the engine's (a native object the engine kept is
    /// caught by the wrapper count, the native-reference test and the handle check). (Were every restart's re-use tried and refused, the kept activation
    /// would outlive each bare cycle, and an activation shut down after another came and went keeps about one handle more
    /// on a real encoder: measured +812 against bare +592, and +596 when shut down at once.)
    /// </summary>
    private static void FallbackRestarts(RestartBed bed, MediaFoundationH264 backend, RecordingLogger log)
    {
        var perStart = PerStart(backend);
        Settle();
        var (threads, live, activations) = (bed.Threads(), bed.Wrappers(), backend.Activations);
        long restartHandles = 0, restartBytes = 0, bareHandles = 0, bareBytes = 0;
        long managedBefore;
        using (var bare = new BareCycles(bed.Catalog(), backend.Trusted!))
        {
            bare.Cycle(128);
            managedBefore = bed.Managed();
            for (var i = 0; i < bed.Restarts; i++)
            {
                var side = Side(i);
                var (h, b) = bed.Resources();
                Restart(backend, side);
                var (h2, b2) = bed.Resources();
                restartHandles += h2 - h;
                restartBytes += b2 - b;

                (h, b) = bed.Resources();
                for (var k = 0; k < perStart; k++)
                    bare.Cycle(side);
                (h2, b2) = bed.Resources();
                bareHandles += h2 - h;
                bareBytes += b2 - b;
            }
        }

        WrappersGivenBack(bed, live);
        Settle();
        var managedAfter = bed.Managed();
        var threadsAfter = bed.Threads();
        var activationsTaken = (double)bed.Restarts * perStart;
        Note($"threads {threads} -> {threadsAfter}");
        Note($"{bed.Restarts} restarts ({perStart} activation{(perStart == 1 ? "" : "s")} each) interleaved with {perStart * bed.Restarts} bare cycles of the port's raw calls: handles restarts {restartHandles:+#;-#;0}, bare {bareHandles:+#;-#;0} (the restarts' are held to max(bare, 0) + {50 * perStart} = {Math.Max(bareHandles, 0) + 50 * perStart})");
        Note($"private bytes (printed, not asserted) restarts {restartBytes:+#;-#;0} ({restartBytes / activationsTaken:+#.0;-#.0;0} per activation), bare {bareBytes:+#;-#;0} ({bareBytes / activationsTaken:+#.0;-#.0;0} per activation)");
        Note($"managed heap {managedBefore} -> {managedAfter} ({managedAfter - managedBefore:+#;-#;0}, held to 5 MB)");
        Assert.Equal(activations + perStart * bed.Restarts, backend.Activations);
        Assert.Null(backend.Parked);
        Assert.InRange(threadsAfter - threads, int.MinValue, 10);
        Assert.InRange(restartHandles, long.MinValue, Math.Max(bareHandles, 0) + 50 * perStart);
        Assert.InRange(managedAfter - managedBefore, long.MinValue, 5L << 20);
    }

    private static void WrappersGivenBack(RestartBed bed, long live)
    {
        var watch = Stopwatch.StartNew();
        while (bed.Wrappers() > live && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(20);
        Assert.Equal(live, bed.Wrappers());
    }

    /// <summary>
    /// Bare cycles on the trusted entry, built from the port's raw calls on this thread (events on a thread of their
    /// own): activate, the types, three frames in and out, shut down, release. Each cycle must encode its three frames.
    /// </summary>
    internal sealed class BareCycles : IDisposable
    {
        private readonly IMfCatalog _catalog;
        private readonly IReadOnlyList<IMfEntry> _entries;
        private readonly IMfEntry _entry;

        public BareCycles(IMfCatalog catalog, MfIdentity trusted)
        {
            _catalog = catalog;
            Assert.True(_catalog.EnterThread("test") >= 0);
            try
            {
                Assert.True(_catalog.Startup() >= 0);
                Assert.True(_catalog.Enumerate(MediaFoundationH264.EnumFlags, out var entries) >= 0);
                _entries = entries;
                _entry = entries.First(e => e.GetString(Mf.HardwareUrl) == trusted.HardwareUrl);
            }
            catch
            {
                _catalog.LeaveThread();
                throw;
            }
        }

        public void Cycle(int side) => BareCycle(_catalog, _entry, side);

        public void Dispose()
        {
            foreach (var e in _entries)
                e.Dispose();
            _catalog.LeaveThread();
        }
    }

    private static void BareCycle(IMfCatalog catalog, IMfEntry entry, int side)
    {
        const int frames = 3;
        var port = entry.CreatePort();
        Thread? reader = null;
        try
        {
            Assert.Equal(0, port.Activate());
            Assert.True(port.SetUInt32(Mf.TransformAsyncUnlock, 1) >= 0);
            Assert.True(port.SetOutputType(new MfVideoType(Mf.H264, side, side)
            {
                FrameRate = 30, Bitrate = 2_000_000, Profile = Mf.ProfileBaseline, InterlaceMode = Mf.InterlaceProgressive,
            }) >= 0, "the output type");
            Assert.True(port.SetInputType(new MfVideoType(Mf.Nv12, side, side) { FrameRate = 30, DefaultStride = side, InterlaceMode = Mf.InterlaceProgressive }) >= 0, "the input type");
            Assert.True(port.GetInputStreamInfo(out var input) >= 0);
            Assert.True(port.GetOutputStreamInfo(out var outputInfo) >= 0);
            Assert.True(port.ProcessMessage(Mf.MessageNotifyBeginStreaming) >= 0);
            Assert.True(port.ProcessMessage(Mf.MessageNotifyStartOfStream) >= 0);

            var events = new System.Collections.Concurrent.BlockingCollection<MfEvent>();
            reader = new Thread(() =>
            {
                catalog.EnterThread("events");
                try
                {
                    while (port.NextEvent(out var e) >= 0)
                        events.Add(e);
                }
                finally
                {
                    catalog.LeaveThread();
                    events.CompleteAdding();
                }
            }) { IsBackground = true };
            reader.Start();
            var nv12 = new byte[side * side * 3 / 2];
            int inputs = 0, outputs = 0;
            while (outputs < frames)
            {
                Assert.True(events.TryTake(out var e, TimeSpan.FromSeconds(5)), $"an event within 5 s ({inputs} in, {outputs} out)");
                if (e.Type == Mf.EventNeedInput && inputs < frames)
                {
                    Assert.True(port.CreateInputBuffer(Math.Max(nv12.Length, input.Size), Math.Max(input.Alignment, 1), out var buffer) >= 0);
                    using (buffer)
                    {
                        Assert.True(buffer!.Write(nv12) >= 0);
                        Assert.True(port.ProcessInput(buffer, inputs * 333_333L, 333_333, discontinuity: false) >= 0);
                    }

                    inputs++;
                }
                else if (e.Type == Mf.EventHaveOutput)
                {
                    var hr = port.ProcessOutput(MfOutputAllocation.Of(outputInfo), out var output);
                    Assert.True(hr >= 0 || hr == Mf.TransformNeedMoreInput, $"ProcessOutput {Mf.Hr(hr)}");
                    if (output != null)
                        outputs++;
                }
            }
        }
        finally
        {
            port.Shutdown();
            Assert.True(reader?.Join(TimeSpan.FromSeconds(2)) ?? true, "GetEvent ends with the shutdown");
            port.Release();
        }
    }
}
