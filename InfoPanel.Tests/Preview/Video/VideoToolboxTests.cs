using System.Diagnostics;
using System.Runtime.Versioning;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The Mac's hardware H.264 encoder, VideoToolbox called directly: on macOS the encoder must exist (skipped only with
/// <c>INFOPANEL_NO_HARDWARE_ENCODER=1</c>, a machine without one); its streams are decoded and parsed by ffmpeg
/// (Homebrew's; skipped without it, which the Mac the agents gate on has). Faults VideoToolbox doesn't produce on demand
/// are injected beneath the backend interface (<see cref="VideoToolboxFaults"/>). Frames are timed only as "helper start
/// to observed frame", with no budget: latency budgets live in the browser specs.
/// </summary>
[SupportedOSPlatform("macos")]
[Collection(VideoToolboxCollection.Name)]
public class VideoToolboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NoHardwareEncoder = "INFOPANEL_NO_HARDWARE_ENCODER";

    private static void SkipWithoutVideoToolbox()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable(NoHardwareEncoder) == "1", $"{NoHardwareEncoder}=1: this Mac has no hardware H.264 encoder.");
    }

    private static VideoToolboxH264 Backend(VideoToolboxFaults? faults = null, ILogger? logger = null) =>
        new(logger ?? NullLogger.Instance) { Faults = faults };

    /// <summary>Packed RGBA pixels of one colour (opaque).</summary>
    private static byte[] Solid(int width, int height, (byte R, byte G, byte B) colour)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.R;
            pixels[i + 1] = colour.G;
            pixels[i + 2] = colour.B;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>Encodes each frame in turn on one session (the first a keyframe), waiting for each one's outcome before the next.</summary>
    private static List<H264Outcome> Encode(IH264Backend backend, int width, int height, IReadOnlyList<byte[]> frames)
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
                Assert.Equal(H264Submit.Accepted, session.Submit(frames[i], width, height, i, i == 0, out var problem));
                Assert.Null(problem);
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), $"frame {i}'s outcome (helper start to observed frame) arrived without another frame");
            }
        }
        finally
        {
            Assert.True(session.Abort().Wait(Quarantine.Bound), "the session tears down");
        }

        lock (outcomes)
            return [.. outcomes];
    }

    /// <summary>Annex B for ffmpeg: the avcC's parameter sets, then each access unit's NAL units, with start codes.</summary>
    private static byte[] AnnexB(byte[] avcc, IEnumerable<byte[]> accessUnits)
    {
        using var stream = new MemoryStream();
        void Nal(ReadOnlySpan<byte> nal)
        {
            stream.Write([0, 0, 0, 1]);
            stream.Write(nal);
        }

        var at = 6;
        for (var i = avcc[5] & 0x1f; i > 0; i--)
        {
            var length = (avcc[at] << 8) | avcc[at + 1];
            Nal(avcc.AsSpan(at + 2, length));
            at += 2 + length;
        }

        for (var i = avcc[at++]; i > 0; i--)
        {
            var length = (avcc[at] << 8) | avcc[at + 1];
            Nal(avcc.AsSpan(at + 2, length));
            at += 2 + length;
        }

        foreach (var unit in accessUnits)
        {
            for (var offset = 0; offset < unit.Length;)
            {
                var length = (unit[offset] << 24) | (unit[offset + 1] << 16) | (unit[offset + 2] << 8) | unit[offset + 3];
                Nal(unit.AsSpan(offset + 4, length));
                offset += 4 + length;
            }
        }

        return stream.ToArray();
    }

    /// <summary>ffmpeg's decode of the stream as RGB (BT.709, limited range in): the frames' size and each frame's pixels.</summary>
    private static async Task<(int Width, int Height, List<byte[]> Frames)> Decode(byte[] annexB, int width, int height)
    {
        var folder = Directory.CreateTempSubdirectory("infopanel-vt-");
        var file = Path.Combine(folder.FullName, "stream.h264");
        await File.WriteAllBytesAsync(file, annexB, Ct);
        var info = new ProcessStartInfo(TestFfmpeg.Path,
        [
            "-hide_banner", "-loglevel", "error", "-xerror", "-f", "h264", "-i", file, "-fps_mode", "passthrough",
            "-vf", "scale=in_color_matrix=bt709:in_range=limited:out_range=full,format=rgb24", "-f", "rawvideo", "-",
        ])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ffmpeg = Process.Start(info)!;
        using var output = new MemoryStream();
        var copy = ffmpeg.StandardOutput.BaseStream.CopyToAsync(output, Ct);
        var stderr = ffmpeg.StandardError.ReadToEndAsync(Ct);
        await ffmpeg.WaitForExitAsync(Ct);
        await copy;
        Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg couldn't decode the stream: {await stderr}");
        File.Delete(file);
        Directory.Delete(folder.FullName);

        var frameBytes = width * height * 3;
        var bytes = output.ToArray();
        Assert.True(bytes.Length % frameBytes == 0, $"{bytes.Length} decoded bytes aren't whole {width}x{height} frames");
        return (width, height, Enumerable.Range(0, bytes.Length / frameBytes).Select(i => bytes.AsSpan(i * frameBytes, frameBytes).ToArray()).ToList());
    }

    private static (byte R, byte G, byte B) Centre(byte[] rgb, int width, int height)
    {
        var at = ((height / 2) * width + width / 2) * 3;
        return (rgb[at], rgb[at + 1], rgb[at + 2]);
    }

    private static byte[] Keyframe(List<H264Outcome> outcomes)
    {
        var key = outcomes[0];
        Assert.True(key.Keyframe, "the first frame is a keyframe");
        return H264Sps.RewriteForStream(key.Avcc!, out var problem) is var rewritten && problem == null ? rewritten : throw new Xunit.Sdk.XunitException(problem);
    }

    // ---- the encoder exists ----

    [Fact]
    public void TheHardwareEncoder_ExistsOnThisMac()
    {
        SkipWithoutVideoToolbox();
        Assert.True(Backend().Probe() == H264Probe.Available, $"VideoToolbox has no hardware H.264 encoder here; on a Mac without one, set {NoHardwareEncoder}=1.");
        var backends = H264Backends.ForThisComputer("auto", NullLogger.Instance);
        Assert.Equal(["VideoToolbox"], backends.Chain.Select(b => b.Name));
        Assert.Equal(H264Kind.Hardware, backends.Chain[0].Kind);
        Assert.Empty(H264Backends.ForThisComputer("software", NullLogger.Instance).Chain);
        Assert.Empty(H264Backends.ForThisComputer("off", NullLogger.Instance).Chain);
    }

    // ---- the stream ----

    [Fact]
    public async Task TheAvccSaysNoReordering_AndTheStreamDecodesToOneFramePerAccessUnit()
    {
        SkipWithoutVideoToolbox();
        TestFfmpeg.SkipWithout();
        const int w = 320, h = 192;
        var frames = Enumerable.Range(0, 12).Select(i => Solid(w, h, ((byte)(i * 20), 80, 160))).ToList();
        var outcomes = Encode(Backend(), w, h, frames);
        Assert.All(outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        Assert.All(outcomes.Skip(1), o => Assert.False(o.Keyframe));

        var avcc = Keyframe(outcomes);
        Assert.Equal(H264Sps.BaselineProfile, avcc[1]);
        var sps = await H264SpsTests.TraceSps(avcc, Ct);
        long Field(string name) => sps.Single(f => f.Name == name).Value;
        Assert.Equal(66, Field("profile_idc"));
        Assert.Equal(1, Field("bitstream_restriction_flag"));
        Assert.Equal(0, Field("max_num_reorder_frames"));
        Assert.Equal(Field("max_num_ref_frames"), Field("max_dec_frame_buffering"));
        Assert.Equal(1, Field("colour_primaries"));
        Assert.Equal(1, Field("transfer_characteristics"));
        Assert.Equal(1, Field("matrix_coefficients"));
        Assert.Equal(0, Field("video_full_range_flag"));

        // AVCC with 4-byte lengths, and no parameter sets in the stream itself.
        foreach (var outcome in outcomes)
        {
            for (var offset = 0; offset < outcome.Data!.Length;)
            {
                var length = (outcome.Data[offset] << 24) | (outcome.Data[offset + 1] << 16) | (outcome.Data[offset + 2] << 8) | outcome.Data[offset + 3];
                Assert.DoesNotContain(outcome.Data[offset + 4] & 0x1f, new[] { 7, 8 });
                offset += 4 + length;
            }
        }

        var decoded = await Decode(AnnexB(avcc, outcomes.Select(o => o.Data!)), w, h);
        Assert.Equal(frames.Count, decoded.Frames.Count);
    }

    [Fact]
    public async Task AnOddSize_IsPaddedToEvenSides_InBlack()
    {
        SkipWithoutVideoToolbox();
        TestFfmpeg.SkipWithout();
        const int w = 321, h = 241;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (255, 255, 255))]);
        var avcc = Keyframe(outcomes);
        // The stream is the frame padded to even sides; the header keeps the frame's own size (the client crops).
        var decoded = await Decode(AnnexB(avcc, outcomes.Select(o => o.Data!)), w + 1, h + 1);
        var frame = Assert.Single(decoded.Frames);
        Assert.True(frame[((h / 2) * (w + 1) + w / 2) * 3] > 230, "the picture is white");
        var padding = frame.AsSpan(((h / 2) * (w + 1) + w) * 3, 3).ToArray();
        Assert.All(padding, c => Assert.True(c < 40, $"the padding column is black ({string.Join(",", padding)})"));
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0x2a, 0x6f, 0xd0)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 255, 255)]
    [InlineData(16, 16, 16)]
    public async Task FlatColours_DecodeWithinFourLevels_ThroughBt709LimitedRange(byte r, byte g, byte b)
    {
        SkipWithoutVideoToolbox();
        TestFfmpeg.SkipWithout();
        const int w = 256, h = 160;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (r, g, b))]);
        var decoded = await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w, h);
        var (dr, dg, db) = Centre(decoded.Frames.Single(), w, h);
        Assert.True(Math.Abs(dr - r) <= 4 && Math.Abs(dg - g) <= 4 && Math.Abs(db - b) <= 4,
            $"#{r:x2}{g:x2}{b:x2} decoded as #{dr:x2}{dg:x2}{db:x2}");
    }

    [Fact]
    public async Task EachDecodedPicture_IsTheFrameItsHeaderNames()
    {
        SkipWithoutVideoToolbox();
        TestFfmpeg.SkipWithout();
        // Through the stream: the frames' headers, in the order sent, against what ffmpeg decodes from their access units.
        const int w = 128, h = 96;
        (byte, byte, byte)[] colours = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 255, 255), (0, 0, 0)];
        var backends = new H264Backends([Backend()], NullLogger.Instance);
        var skipped = new SkipCounter();
        using var stream = new VideoStream(backends, 30, () => { }, () => { }, skipped, NullLogger.Instance, TimeProvider.System);
        var sent = new List<EncodedFrame>();
        for (var tick = 1; tick <= colours.Length; tick++)
        {
            Assert.Null(stream.Route(w, h));
            stream.Offer(Solid(w, h, colours[tick - 1]), Header(tick, w, h));
            var watch = Stopwatch.StartNew();
            while (sent.Count < tick && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (stream.TryTake(out var frame))
                    sent.Add(frame);
                else
                    await Task.Delay(2, Ct);
            }
        }

        Assert.Equal(Enumerable.Range(1, colours.Length).Select(t => (long)t), sent.Select(f => f.Header.Tick));
        Assert.Equal(0, skipped.Peek);
        var avcc = Convert.FromBase64String(sent[0].Header.Description!);
        // The description the stream sends says "no reordering" (VideoToolbox's own doesn't).
        var sps = await H264SpsTests.TraceSps(avcc, Ct);
        Assert.Equal(1, sps.Single(f => f.Name == "bitstream_restriction_flag").Value);
        Assert.Equal(0, sps.Single(f => f.Name == "max_num_reorder_frames").Value);
        Assert.Equal(("hardware", $"avc1.{avcc[1]:X2}{avcc[2]:X2}{avcc[3]:X2}"), (sent[0].Header.Encoder, sent[0].Header.Codec));
        var decoded = await Decode(AnnexB(avcc, sent.Select(f => f.Payload.ToArray())), w, h);
        Assert.Equal(colours.Length, decoded.Frames.Count);
        for (var i = 0; i < colours.Length; i++)
        {
            var (r, g, b) = Centre(decoded.Frames[i], w, h);
            var (er, eg, eb) = colours[sent[i].Header.Tick - 1];
            Assert.True(Math.Abs(r - er) <= 8 && Math.Abs(g - eg) <= 8 && Math.Abs(b - eb) <= 8, $"frame {sent[i].Header.Tick} decoded as #{r:x2}{g:x2}{b:x2}");
        }
    }

    [Fact]
    public void AStillPanelsOneEdit_ArrivesWithoutASecondFrame()
    {
        SkipWithoutVideoToolbox();
        // Encode waits for each frame's outcome before submitting the next: a held frame would fail here.
        const int w = 640, h = 384;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (20, 20, 20)), Solid(w, h, (200, 20, 20))]);
        Assert.Equal(2, outcomes.Count);
        Assert.False(outcomes[1].Keyframe);
    }

    [Fact]
    public void TheLargestVideoSize_StartsASession()
    {
        SkipWithoutVideoToolbox();
        var outcomes = Encode(Backend(), VideoStream.MaxSide, 2304, [Solid(VideoStream.MaxSide, 2304, (40, 90, 200))]);
        Assert.Equal(H264OutcomeKind.Encoded, Assert.Single(outcomes).Kind);
    }

    [Fact]
    public void TwoHundredRestarts_LeaveNoSessionHandleOrFileBehind()
    {
        SkipWithoutVideoToolbox();
        var backend = Backend();
        Encode(backend, 64, 64, [Solid(64, 64, (1, 2, 3))]);
        var files = OpenFiles();
        var live = VideoToolboxSession.Live;
        for (var i = 0; i < 200; i++)
        {
            var size = 64 + (i % 3) * 2 + (i % 2);
            Encode(backend, size, size, [Solid(size, size, (1, 2, 3))]);
        }

        var watch = Stopwatch.StartNew();
        while (VideoToolboxSession.Live > live && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(20);
        Assert.Equal(live, VideoToolboxSession.Live);
        // Loose: other test classes open files too. A leaked session (its pool, IOSurfaces) would cost far more over 200.
        Assert.InRange(OpenFiles() - files, int.MinValue, 32);
    }

    private static int OpenFiles() => Directory.GetFileSystemEntries("/dev/fd").Length;

    // ---- beneath the interface: one outcome per submission, mandatory settings, hints ----

    [Fact]
    public async Task ExtractionThrows_ReleasesItsSlot_ThroughTheCallbacksBarrier()
    {
        SkipWithoutVideoToolbox();
        // R2-N1 through the real callback: reading the encoded buffer throws once; that frame's outcome is a failure,
        // its slot comes back, the session is replaced and a keyframe follows.
        var faults = new VideoToolboxFaults { ThrowOnRead = 1 };
        var backends = new H264Backends([Backend(faults)], NullLogger.Instance);
        var refreshes = 0;
        using var stream = new VideoStream(backends, 30, () => { }, () => Interlocked.Increment(ref refreshes), new SkipCounter(), NullLogger.Instance,
            TimeProvider.System);
        const int w = 96, h = 64;
        stream.Offer(Solid(w, h, (10, 20, 30)), Header(1, w, h));
        var watch = Stopwatch.StartNew();
        while ((Volatile.Read(ref refreshes) == 0 || stream.Occupied != 0) && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(5, Ct);
        Assert.Equal(0, stream.Occupied);
        Assert.Equal(0, stream.InFlight);
        Assert.Equal(1, Volatile.Read(ref refreshes));
        Assert.Equal(0, faults.ThrowOnRead);

        stream.Offer(Solid(w, h, (10, 20, 30)), Header(2, w, h));
        EncodedFrame? frame = null;
        while (frame == null && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!stream.TryTake(out var taken))
                await Task.Delay(5, Ct);
            else
                frame = taken;
        }

        Assert.NotNull(frame);
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(2, frame.Header.Tick);
    }

    [Theory]
    [InlineData("ProfileLevel")]
    [InlineData("AllowFrameReordering")]
    [InlineData("YCbCrMatrix")]
    public void ARefusedMandatorySetting_FailsTheStart(string setting)
    {
        SkipWithoutVideoToolbox();
        var refusal = Assert.Throws<H264StartException>(() =>
            Backend(new VideoToolboxFaults { Refuse = [setting] }).Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { }));
        Assert.Equal(H264StartProblem.Fault, refusal.Problem);
        Assert.Contains(setting, refusal.Message);
    }

    [Theory]
    [InlineData("UsingHardwareAcceleratedVideoEncoder")]
    [InlineData("AllowFrameReordering")]
    [InlineData("YCbCrMatrix")]
    public void AnEffectiveConfigurationThatDoesntHold_FailsTheStart(string property)
    {
        SkipWithoutVideoToolbox();
        var refusal = Assert.Throws<H264StartException>(() =>
            Backend(new VideoToolboxFaults { Misreport = [property] }).Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { }));
        Assert.Equal(H264StartProblem.Fault, refusal.Problem);
    }

    [Fact]
    public async Task ARefusedMandatorySetting_FallsBackToTheNextEncoder()
    {
        SkipWithoutVideoToolbox();
        var software = new FakeH264Backend("Software", H264Kind.Software);
        var backends = new H264Backends([Backend(new VideoToolboxFaults { Refuse = ["AllowFrameReordering"] }), software], NullLogger.Instance);
        using var stream = new VideoStream(backends, 30, () => { }, () => { }, new SkipCounter(), NullLogger.Instance, TimeProvider.System);
        stream.Offer(Solid(64, 64, (1, 2, 3)), Header(1, 64, 64));
        var watch = Stopwatch.StartNew();
        EncodedFrame? frame = null;
        while (frame == null && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (!stream.TryTake(out var taken))
                await Task.Delay(5, Ct);
            else
                frame = taken;
        }

        Assert.NotNull(frame);
        Assert.Equal("software", frame.Header.Encoder);
    }

    [Fact]
    public void ARefusedHint_StartsTheSession_AndIsLogged()
    {
        SkipWithoutVideoToolbox();
        var logger = new ListLogger();
        var outcomes = Encode(Backend(new VideoToolboxFaults { Refuse = ["RealTime", "AverageBitRate"] }, logger), 64, 64, [Solid(64, 64, (9, 9, 9))]);
        Assert.Equal(H264OutcomeKind.Encoded, Assert.Single(outcomes).Kind);
        Assert.Contains(logger.Messages, m => m.Contains("RealTime (not set", StringComparison.Ordinal) && m.Contains("AverageBitRate (not set", StringComparison.Ordinal));
    }

    /// <summary>Faults that fail a start at each step after its session exists, with every invalidation held (wedged).</summary>
    public static TheoryData<string> FailingStartSteps => ["configuration", "verification", "preparation"];

    private static VideoToolboxFaults FailingAt(string step, ManualResetEventSlim hold) => step switch
    {
        "configuration" => new VideoToolboxFaults { Refuse = ["ProfileLevel"], HoldInvalidate = hold },
        "verification" => new VideoToolboxFaults { Misreport = ["AllowFrameReordering"], HoldInvalidate = hold },
        _ => new VideoToolboxFaults { FailPrepare = 1, HoldInvalidate = hold },
    };

    /// <summary>
    /// The process's live sessions come back to at most <paramref name="live"/> (an earlier test's probe may still have
    /// been tearing its session down when it was read).
    /// </summary>
    private static void NoMoreLiveThan(long live)
    {
        var watch = Stopwatch.StartNew();
        while (VideoToolboxSession.Live > live && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Yield();
        Assert.InRange(VideoToolboxSession.Live, 0, live);
    }

    /// <summary>Runs <paramref name="start"/> on a thread of its own; whether it returned (or threw) within a second.</summary>
    private static (bool Returned, Exception? Thrown, IH264Session? Session) StartWithin(Func<IH264Session> start)
    {
        IH264Session? session = null;
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                session = start();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        }) { IsBackground = true };
        thread.Start();
        var returned = thread.Join(TimeSpan.FromSeconds(1));
        return (returned, thrown, session);
    }

    [Theory]
    [MemberData(nameof(FailingStartSteps))]
    public void AFailedStart_WithAWedgedInvalidation_ReturnsAtOnce_AndHandsItsTeardownBack(string step)
    {
        SkipWithoutVideoToolbox();
        var live = VideoToolboxSession.Live;
        using var hold = new ManualResetEventSlim(false);
        var backend = Backend(FailingAt(step, hold));
        try
        {
            var (returned, thrown, _) = StartWithin(() => backend.Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { }));
            Assert.True(returned, $"a start failing at {step} returned while its session's invalidation was wedged");
            var refusal = Assert.IsType<H264StartException>(thrown);
            Assert.Equal(H264StartProblem.Fault, refusal.Problem);
            Assert.NotNull(refusal.Teardown);
            Assert.False(refusal.Teardown.IsCompleted, "the teardown waits for the invalidation");

            hold.Set();
            Assert.True(refusal.Teardown.Wait(Quarantine.Bound), "the teardown ends once the invalidation returns");
            NoMoreLiveThan(live);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public void TheBgraRetry_WithTheRgbaSessionsInvalidationWedged_StartsAtOnce()
    {
        SkipWithoutVideoToolbox();
        var live = VideoToolboxSession.Live;
        using var hold = new ManualResetEventSlim(false);
        var backend = Backend(new VideoToolboxFaults { RefuseRgba = true, HoldInvalidate = hold });
        try
        {
            var (returned, thrown, session) = StartWithin(() => backend.Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { }));
            Assert.True(returned, "the start went on in BGRA while the RGBA session's invalidation was wedged");
            Assert.Null(thrown);
            var teardown = session!.Abort();
            hold.Set();
            Assert.True(teardown.Wait(Quarantine.Bound), "the session and the RGBA one tear down once invalidation returns");
            NoMoreLiveThan(live);
        }
        finally
        {
            hold.Set();
        }
    }

    /// <summary>Waits (real time, at most 5 s) until <paramref name="condition"/>.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(5, Ct);
    }

    [Fact]
    public async Task TheRgbaSessionsWedgedTeardown_IsQuarantinedAtTheBound_WhileTheBgraSessionRuns()
    {
        SkipWithoutVideoToolbox();
        // Only the first invalidation, the RGBA session's, is wedged: the stream's BGRA session runs, the wedge counts
        // once at the bound, and the BGRA session's own retirement isn't held by it (nor counted again).
        using var hold = new ManualResetEventSlim(false);
        var faults = new VideoToolboxFaults { RefuseRgba = true, HoldInvalidate = hold, HeldInvalidations = 0 };
        var backend = Backend(faults);
        var backends = new H264Backends([backend], NullLogger.Instance);
        try
        {
            // The probe's two sessions (RGBA, then BGRA) are torn down first, unheld; then the next invalidation is held.
            Assert.Equal(H264Probe.Available, await backends.ProbeAsync(backend));
            await Until(() => Volatile.Read(ref faults.Invalidating) >= 2);
            Assert.Equal(2, Volatile.Read(ref faults.Invalidating));
            Volatile.Write(ref faults.HeldInvalidations, 1);

            using var stream = new VideoStream(backends, 30, () => { }, () => { }, new SkipCounter(), NullLogger.Instance, TimeProvider.System);
            const int w = 64, h = 64;
            stream.Offer(Solid(w, h, (40, 50, 60)), Header(1, w, h));
            EncodedFrame? frame = null;
            await Until(() => stream.TryTake(out frame));
            Assert.NotNull(frame);
            Assert.Equal(0, backends.Quarantines(backend));

            await Until(() => backends.Quarantines(backend) == 1);
            Assert.Equal(1, backends.Quarantines(backend));
            stream.Offer(Solid(w, h, (40, 50, 60)), Header(2, w, h));
            frame = null;
            await Until(() => stream.TryTake(out frame));
            Assert.NotNull(frame);

            stream.Dispose();
            await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(1, backends.Quarantines(backend));
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task TheCallbackHandle_OutlivesTheRgbaSessionsInvalidation()
    {
        SkipWithoutVideoToolbox();
        using var hold = new ManualResetEventSlim(false);
        var backend = Backend(new VideoToolboxFaults { RefuseRgba = true, HoldInvalidate = hold, HeldInvalidations = 1 });
        try
        {
            var session = (VideoToolboxSession)backend.Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), _ => { });
            var released = Assert.Single(session.Released);
            Assert.False(released.IsCompleted, "the RGBA session's invalidation is held");
            Assert.True(session.Abort().Wait(Quarantine.Bound), "the BGRA session's own teardown doesn't wait for the RGBA one");
            Assert.True(session.CallbackHandleAlive, "the RGBA session can still call back");

            hold.Set();
            Assert.True(released.Wait(Quarantine.Bound));
            await Until(() => !session.CallbackHandleAlive);
            Assert.False(session.CallbackHandleAlive, "freed once every invalidation returned");
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task ALateCallback_WhileInvalidationIsHeld_FindsItsSessionsHandleAlive()
    {
        SkipWithoutVideoToolbox();
        using var hold = new ManualResetEventSlim(false);
        var faults = new VideoToolboxFaults { HoldInvalidate = hold };
        var outcomes = new List<H264Outcome>();
        try
        {
            var session = (VideoToolboxSession)Backend(faults).Start(new H264Config(64, 64, 30, VideoStream.HardwareBitrate), o =>
            {
                lock (outcomes)
                    outcomes.Add(o);
            });
            var refCon = session.CallbackRefCon;
            var teardown = session.Abort();
            // This session's invalidation, after any of a session the start let go (a Mac that refuses RGBA buffers).
            var invalidations = session.Released.Count + 1;
            await Until(() => Volatile.Read(ref faults.Invalidating) >= invalidations);
            Assert.Equal(invalidations, Volatile.Read(ref faults.Invalidating));
            Assert.False(teardown.IsCompleted, "the invalidation is held");

            // VideoToolbox may still call back until invalidation returns: the handle must still lead to the session.
            Assert.True(session.CallbackHandleAlive, "the callback handle is alive while invalidation is held");
            Assert.Equal(refCon, session.CallbackRefCon);
            VideoToolboxSession.CallBack(refCon, 7);
            lock (outcomes)
                Assert.Empty(outcomes);

            hold.Set();
            Assert.True(teardown.Wait(Quarantine.Bound));
            await Until(() => !session.CallbackHandleAlive);
            Assert.False(session.CallbackHandleAlive, "freed once invalidation returned");
        }
        finally
        {
            hold.Set();
        }
    }

    [Theory]
    [InlineData(-12915, "Busy")]
    [InlineData(-12904, "Busy")]
    [InlineData(-12908, "Unavailable")]
    [InlineData(-12911, "Unavailable")]
    public void AProbeKeepsItsRefusalsClass(int status, string expected)
    {
        SkipWithoutVideoToolbox();
        // Both pixel formats' creations fail with the status; the next probe finds the encoder.
        var backend = Backend(new VideoToolboxFaults { FailCreate = 2, FailCreateStatus = status });
        Assert.Equal(Enum.Parse<H264Probe>(expected), backend.Probe());
        Assert.Equal(H264Probe.Available, backend.Probe());
    }

    [Theory]
    [InlineData(-12908, "Unsupported")]
    [InlineData(-12902, "Unsupported")]
    [InlineData(-12915, "Busy")]
    [InlineData(-12904, "Busy")]
    [InlineData(-12911, "Fault")]
    [InlineData(-1, "Fault")]
    public void ARefusedStart_IsClassifiedByItsStatus(int status, string expected) =>
        Assert.Equal(Enum.Parse<H264StartProblem>(expected), VideoToolboxSession.Classify(status));

    private static FrameHeader Header(long tick, int width, int height) => new()
    {
        ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1, PixelWidth = width,
        PixelHeight = height, ProfileWidth = width, ProfileHeight = height, ItemBounds = [], EncodeMs = 0, Skipped = 0,
    };

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }
}

/// <summary>
/// VideoToolbox's tests run alone: they count the process's open files and sessions, and time the hardware encoder,
/// neither of which other test classes running beside them should disturb.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VideoToolboxCollection
{
    public const string Name = "VideoToolbox";
}
