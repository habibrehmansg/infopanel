using System.Diagnostics;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The software H.264 encoder, the user's ffmpeg run as a child process (libx264): the same checks as VideoToolbox's
/// through x264 (no reordering, one frame per access unit, odd sizes, colour, pairing, no hold, the largest size,
/// restarts), its restarts (a keyframe request, a frame rate change, an ffmpeg that dies), and its teardown: a restart
/// while a write to its input blocks, a reader whose output stays open, a reader that won't end. They use Homebrew's
/// ffmpeg and skip, saying why, without it (the Mac the agents gate on has it); stand-ins are shell scripts (Unix).
/// Frames are timed only as "helper start to observed frame", with no budget.
/// </summary>
[Collection(FfmpegCollection.Name)]
public class FfmpegH264Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FfmpegH264 Backend(FfmpegFaults? faults = null, ILogger? logger = null) =>
        new(FfmpegDiscovery.At(TestFfmpeg.Path), logger ?? NullLogger.Instance) { Faults = faults };

    /// <summary>Packed RGBA pixels of one colour (opaque).</summary>
    internal static byte[] Solid(int width, int height, (byte R, byte G, byte B) colour)
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
        var session = backend.Start(new H264Config(width, height, 30, VideoStream.SoftwareBitrate), outcome =>
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
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)), $"frame {i}'s outcome (helper start to observed frame) arrived without another frame");
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

    /// <summary>ffmpeg's decode of the stream as RGB (BT.709, limited range in): each frame's pixels.</summary>
    private static async Task<List<byte[]>> Decode(byte[] annexB, int width, int height)
    {
        var folder = Directory.CreateTempSubdirectory("infopanel-x264-");
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
        return Enumerable.Range(0, bytes.Length / frameBytes).Select(i => bytes.AsSpan(i * frameBytes, frameBytes).ToArray()).ToList();
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

    internal static FrameHeader Header(long tick, int width, int height) => new()
    {
        ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1, PixelWidth = width,
        PixelHeight = height, ProfileWidth = width, ProfileHeight = height, ItemBounds = [], EncodeMs = 0, Skipped = 0,
    };

    /// <summary>Waits (real time, at most <paramref name="seconds"/>) until <paramref name="condition"/>.</summary>
    private static async Task<bool> Until(Func<bool> condition, double seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
                return false;
            await Task.Delay(5, Ct);
        }

        return true;
    }

    /// <summary>The next frame the stream sends, waiting at most 10 s.</summary>
    private static async Task<EncodedFrame> Next(VideoStream stream)
    {
        EncodedFrame? frame = null;
        Assert.True(await Until(() => stream.TryTake(out frame)), "a frame is sent");
        return frame!;
    }

    internal static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---- the probe ----

    [Fact]
    public void TheProbe_FindsLibx264_AndLogsItsVersionAndPath()
    {
        TestFfmpeg.SkipWithout();
        var logger = new RecordingLogger();
        var backend = Backend(logger: logger);
        Assert.Equal(H264Probe.Available, backend.Probe());
        Assert.Equal(TestFfmpeg.Path, backend.Executable);
        Assert.False(string.IsNullOrEmpty(backend.Version));
        Assert.Contains(logger.Messages, m => m.Contains(TestFfmpeg.Path, StringComparison.Ordinal) && m.Contains(backend.Version!, StringComparison.Ordinal));
        Assert.Equal(("ffmpeg", H264Kind.Software, false, true), (backend.Name, backend.Kind, backend.ChangesLive, backend.GivesUpWhenTeardownHangs));
    }

    // ---- the stream x264 makes ----

    [Fact]
    public async Task TheAvccSaysNoReordering_AndTheStreamDecodesToOneFramePerAccessUnit()
    {
        TestFfmpeg.SkipWithout();
        const int w = 320, h = 192;
        var frames = Enumerable.Range(0, 12).Select(i => Solid(w, h, ((byte)(i * 20), 80, 160))).ToList();
        var outcomes = Encode(Backend(), w, h, frames);
        Assert.Equal(frames.Count, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => (long)i), outcomes.Select(o => o.FrameId));
        Assert.All(outcomes.Skip(1), o => Assert.False(o.Keyframe));

        // x264 already says "no reordering" and the stream's colour: the rewrite returns its record unchanged.
        var input = outcomes[0].Avcc!;
        var avcc = Keyframe(outcomes);
        Assert.Equal(input, avcc);
        Assert.Equal(H264Sps.BaselineProfile, avcc[1]);
        var sps = await H264SpsTests.TraceSps(avcc, Ct);
        long Field(string name) => sps.Single(f => f.Name == name).Value;
        Assert.Equal(66, Field("profile_idc"));
        Assert.Equal(1, Field("bitstream_restriction_flag"));
        Assert.Equal(0, Field("max_num_reorder_frames"));
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
                Assert.InRange(offset + 4 + length, 5, outcome.Data.Length);
                Assert.DoesNotContain(outcome.Data[offset + 4] & 0x1f, new[] { 7, 8 });
                offset += 4 + length;
            }
        }

        var decoded = await Decode(AnnexB(avcc, outcomes.Select(o => o.Data!)), w, h);
        Assert.Equal(frames.Count, decoded.Count);
    }

    [Fact]
    public async Task AnOddSize_IsPaddedToEvenSides_InBlack()
    {
        TestFfmpeg.SkipWithout();
        const int w = 321, h = 241;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (255, 255, 255))]);
        var avcc = Keyframe(outcomes);
        var decoded = await Decode(AnnexB(avcc, outcomes.Select(o => o.Data!)), w + 1, h + 1);
        var frame = Assert.Single(decoded);
        Assert.True(frame[((h / 2) * (w + 1) + w / 2) * 3] > 230, "the picture is white");
        var padding = frame.AsSpan(((h / 2) * (w + 1) + w) * 3, 3).ToArray();
        Assert.All(padding, c => Assert.True(c < 40, $"the padding column is black ({string.Join(",", padding)})"));
        var bottom = frame.AsSpan((h * (w + 1) + w / 2) * 3, 3).ToArray();
        Assert.All(bottom, c => Assert.True(c < 40, $"the padding row is black ({string.Join(",", bottom)})"));
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
        TestFfmpeg.SkipWithout();
        const int w = 256, h = 160;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (r, g, b))]);
        var decoded = await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w, h);
        var (dr, dg, db) = Centre(decoded.Single(), w, h);
        Assert.True(Math.Abs(dr - r) <= 4 && Math.Abs(dg - g) <= 4 && Math.Abs(db - b) <= 4,
            $"#{r:x2}{g:x2}{b:x2} decoded as #{dr:x2}{dg:x2}{db:x2}");
    }

    [Fact]
    public async Task EachDecodedPicture_IsTheFrameItsHeaderNames()
    {
        TestFfmpeg.SkipWithout();
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
            sent.Add(await Next(stream));
        }

        Assert.Equal(Enumerable.Range(1, colours.Length).Select(t => (long)t), sent.Select(f => f.Header.Tick));
        Assert.Equal(0, skipped.Peek);
        Assert.Equal([true, .. Enumerable.Repeat(false, colours.Length - 1)], sent.Select(f => f.Header.Keyframe));
        var avcc = Convert.FromBase64String(sent[0].Header.Description!);
        Assert.Equal(("software", $"avc1.{avcc[1]:X2}{avcc[2]:X2}{avcc[3]:X2}"), (sent[0].Header.Encoder, sent[0].Header.Codec));
        var decoded = await Decode(AnnexB(avcc, sent.Select(f => f.Payload.ToArray())), w, h);
        Assert.Equal(colours.Length, decoded.Count);
        for (var i = 0; i < colours.Length; i++)
        {
            var (r, g, b) = Centre(decoded[i], w, h);
            var (er, eg, eb) = colours[sent[i].Header.Tick - 1];
            Assert.True(Math.Abs(r - er) <= 8 && Math.Abs(g - eg) <= 8 && Math.Abs(b - eb) <= 8, $"frame {sent[i].Header.Tick} decoded as #{r:x2}{g:x2}{b:x2}");
        }
    }

    [Fact]
    public void AStillPanelsOneEdit_ArrivesWithoutASecondFrame()
    {
        TestFfmpeg.SkipWithout();
        // Encode waits for each frame's outcome before submitting the next: a held frame would fail here.
        const int w = 640, h = 384;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (20, 20, 20)), Solid(w, h, (200, 20, 20))]);
        Assert.Equal(2, outcomes.Count);
        Assert.False(outcomes[1].Keyframe);
    }

    [Fact]
    public void TheLargestVideoSize_StartsASession()
    {
        TestFfmpeg.SkipWithout();
        var outcomes = Encode(Backend(), VideoStream.MaxSide, 2304, [Solid(VideoStream.MaxSide, 2304, (40, 90, 200))]);
        Assert.Equal(H264OutcomeKind.Encoded, Assert.Single(outcomes).Kind);
    }

    [Fact]
    public void TwoHundredRestarts_LeaveNoProcessHandleOrFileBehind()
    {
        TestFfmpeg.SkipWithout();
        var backend = Backend();
        Encode(backend, 64, 64, [Solid(64, 64, (1, 2, 3))]);
        var files = OpenFiles();
        var live = FfmpegSession.Live;
        for (var i = 0; i < 200; i++)
        {
            var size = 64 + (i % 3) * 2 + (i % 2);
            Encode(backend, size, size, [Solid(size, size, (1, 2, 3))]);
        }

        var watch = Stopwatch.StartNew();
        while (FfmpegSession.Live > live && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(20);
        Assert.Equal(live, FfmpegSession.Live);
        Assert.All(backend.ProcessIds, pid => Assert.False(IsRunning(pid), $"ffmpeg {pid} still runs"));
        Assert.InRange(OpenFiles() - files, int.MinValue, 32);
    }

    private static int OpenFiles() => Directory.GetFileSystemEntries("/dev/fd").Length;

    // ---- restarts ----

    [Fact]
    public async Task KeyframeRequests_OnAStillPanel_AreEachAnswered_FourInARow()
    {
        TestFfmpeg.SkipWithout();
        const int w = 96, h = 64;
        var backend = Backend();
        var backends = new H264Backends([backend], NullLogger.Instance);
        var refreshes = 0;
        using var stream = new VideoStream(backends, 30, () => { }, () => Interlocked.Increment(ref refreshes), new SkipCounter(), NullLogger.Instance,
            TimeProvider.System);
        var pixels = Solid(w, h, (30, 60, 90));
        stream.Offer(pixels, Header(1, w, h));
        Assert.True((await Next(stream)).Header.Keyframe);

        for (var tick = 2; tick <= 5; tick++)
        {
            var before = Volatile.Read(ref refreshes);
            stream.RequestKeyframe();
            Assert.True(Volatile.Read(ref refreshes) > before, "a fresh frame is asked for");
            // The still panel's fresh frame: the same pixels.
            stream.Offer(pixels, Header(tick, w, h));
            var frame = await Next(stream);
            Assert.Equal(tick, frame.Header.Tick);
            Assert.True(frame.Header.Keyframe, $"request {tick - 1} is answered with a keyframe");
            Assert.Equal("software", frame.Header.Encoder);
        }

        Assert.Equal(5, backend.Starts);
        Assert.False(backends.IsGivenUp(backend));
        Assert.True(await Until(() => backend.ProcessIds.Take(4).All(pid => !IsRunning(pid))), "each replaced ffmpeg has ended");
    }

    [Fact]
    public async Task AFrameRateChange_StartsAnotherFfmpegAtThatRate()
    {
        TestFfmpeg.SkipWithout();
        const int w = 96, h = 64;
        var backend = Backend();
        using var stream = new VideoStream(new H264Backends([backend], NullLogger.Instance), 30, () => { }, () => { }, new SkipCounter(),
            NullLogger.Instance, TimeProvider.System);
        stream.Offer(Solid(w, h, (1, 2, 3)), Header(1, w, h));
        Assert.True((await Next(stream)).Header.Keyframe);
        stream.SetFrameRate(12);
        Assert.True(await Until(() => !IsRunning(backend.ProcessIds[0])), "the first ffmpeg ends");
        stream.Offer(Solid(w, h, (1, 2, 3)), Header(2, w, h));
        Assert.True((await Next(stream)).Header.Keyframe);
        Assert.Equal([30, 12], backend.Rates);
    }

    [Fact]
    public async Task AnFfmpegThatDies_IsReplaced_AndAKeyframeFollows()
    {
        TestFfmpeg.SkipWithout();
        const int w = 96, h = 64;
        var backend = Backend();
        using var stream = new VideoStream(new H264Backends([backend], NullLogger.Instance), 30, () => { }, () => { }, new SkipCounter(),
            NullLogger.Instance, TimeProvider.System);
        stream.Offer(Solid(w, h, (1, 2, 3)), Header(1, w, h));
        Assert.True((await Next(stream)).Header.Keyframe);
        using (var first = Process.GetProcessById(backend.ProcessIds[0]))
        {
            first.Kill();
            await first.WaitForExitAsync(Ct);
        }

        // The frame after the death fails the session; the one after that is the new ffmpeg's keyframe.
        EncodedFrame? frame = null;
        for (var tick = 2; tick < 40 && frame == null; tick++)
        {
            stream.Offer(Solid(w, h, (1, 2, 3)), Header(tick, w, h));
            await Until(() => stream.TryTake(out frame), 0.25);
        }

        Assert.NotNull(frame);
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(2, backend.Starts);
    }

    [Fact]
    public async Task ARefusedVideoToolboxSetting_FallsBackToFfmpeg()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "VideoToolbox is macOS only.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("INFOPANEL_NO_HARDWARE_ENCODER") == "1", "INFOPANEL_NO_HARDWARE_ENCODER=1: no hardware encoder.");
        TestFfmpeg.SkipWithout();
        var hardware = new VideoToolboxH264(NullLogger.Instance) { Faults = new VideoToolboxFaults { Refuse = ["AllowFrameReordering"] } };
        using var stream = new VideoStream(new H264Backends([hardware, Backend()], NullLogger.Instance), 30, () => { }, () => { }, new SkipCounter(),
            NullLogger.Instance, TimeProvider.System);
        stream.Offer(Solid(64, 64, (1, 2, 3)), Header(1, 64, 64));
        var frame = await Next(stream);
        Assert.True(frame.Header.Keyframe);
        Assert.Equal("software", frame.Header.Encoder);
    }

    // ---- teardown (R2-E1, R2-E2) ----

    [Fact]
    public async Task ARestart_WhileAWriteToItsInputBlocks_EndsWithinTheBound()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");
        // A stand-in that never reads its input: the 4 MiB frame fills the pipe and the write blocks.
        using var folder = new StandIns();
        var backend = new FfmpegH264(FfmpegDiscovery.At(folder.Write("ffmpeg", "exec sleep 30")), NullLogger.Instance);
        var outcomes = new List<H264Outcome>();
        var session = (FfmpegSession)backend.Start(new H264Config(1024, 1024, 30, VideoStream.SoftwareBitrate), o =>
        {
            lock (outcomes)
                outcomes.Add(o);
        });
        var pixels = Solid(1024, 1024, (9, 9, 9));
        var watch = Stopwatch.StartNew();
        Assert.Equal(H264Submit.Accepted, session.Submit(pixels, 1024, 1024, 0, true, out _));
        Assert.Equal(H264Submit.Accepted, session.Submit(pixels, 1024, 1024, 1, false, out _));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "a submission never waits for the write");
        await Task.Delay(300, Ct);
        Assert.Equal(0, session.FramesWritten);
        Assert.Equal(2, session.InFlight);

        watch.Restart();
        var teardown = session.Abort();
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), "the abort doesn't wait for the blocked write");
        Assert.True(await Task.WhenAny(teardown, Task.Delay(Quarantine.Bound, Ct)) == teardown, "the teardown ends within the bound");
        lock (outcomes)
            Assert.Equal([(0L, H264OutcomeKind.Aborted), (1L, H264OutcomeKind.Aborted)], outcomes.Select(o => (o.FrameId, o.Kind)));
        Assert.Equal(0, session.InFlight);
        Assert.False(IsRunning(session.ProcessId));
    }

    [Fact]
    public async Task TheReaders_AreCancelled_WhenTheOutputStaysOpenAfterTheKill()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");
        // A stand-in that leaves a process of its own behind, outside its tree, holding its output and error open: only
        // cancelling the reads ends the readers.
        using var folder = new StandIns();
        var pidFile = Path.Combine(folder.Path, "orphan.pid");
        var backend = new FfmpegH264(FfmpegDiscovery.At(folder.Write("ffmpeg", $"(sleep 30 & echo $! > '{pidFile}')\nexec sleep 30")), NullLogger.Instance);
        var session = (FfmpegSession)backend.Start(new H264Config(64, 64, 30, VideoStream.SoftwareBitrate), _ => { });
        Assert.True(await Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0), "the stand-in started its orphan");
        var orphan = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Assert.True(IsRunning(orphan));
            var teardown = session.Abort();
            Assert.True(await Task.WhenAny(teardown, Task.Delay(Quarantine.Bound, Ct)) == teardown, "the readers end within the bound");
            Assert.False(IsRunning(session.ProcessId));
            Assert.True(IsRunning(orphan), "the output stayed open throughout");
        }
        finally
        {
            using var process = Process.GetProcessById(orphan);
            process.Kill();
        }
    }

    [Fact]
    public async Task ARestart_ReleasesABlockedWrite_EvenWhileTheInputStaysOpenAfterTheKill()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");
        // The orphan holds the input's other end too, never reading it: killing ffmpeg doesn't break the pipe, so only
        // cancelling the write releases it (R2-E2). (A background command's input is /dev/null unless redirected.)
        using var folder = new StandIns();
        var pidFile = Path.Combine(folder.Path, "orphan.pid");
        var backend = new FfmpegH264(FfmpegDiscovery.At(folder.Write("ffmpeg", $"exec 3<&0\n(sleep 30 <&3 & echo $! > '{pidFile}')\nexec sleep 30")),
            NullLogger.Instance);
        var session = (FfmpegSession)backend.Start(new H264Config(1024, 1024, 30, VideoStream.SoftwareBitrate), _ => { });
        Assert.True(await Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0), "the stand-in started its orphan");
        var orphan = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Assert.Equal(H264Submit.Accepted, session.Submit(Solid(1024, 1024, (9, 9, 9)), 1024, 1024, 0, true, out _));
            await Task.Delay(300, Ct);
            Assert.Equal(0, session.FramesWritten);
            var teardown = session.Abort();
            Assert.True(await Task.WhenAny(teardown, Task.Delay(Quarantine.Bound, Ct)) == teardown, "the blocked write is released within the bound");
            Assert.True(IsRunning(orphan), "the input stayed open throughout");
        }
        finally
        {
            using var process = Process.GetProcessById(orphan);
            process.Kill();
        }
    }

    [Fact]
    public async Task AReaderThatWontEnd_GivesFfmpegUpForTheStream_InsteadOfStartingAnotherBesideIt()
    {
        TestFfmpeg.SkipWithout();
        // Open while the probe's session tears down (a probe whose teardown hangs fails), then shut.
        using var hold = new ManualResetEventSlim(true);
        try
        {
            const int w = 96, h = 64;
            var backend = Backend(new FfmpegFaults { HoldReader = hold });
            var backends = new H264Backends([backend], NullLogger.Instance);
            using var stream = new VideoStream(backends, 30, () => { }, () => { }, new SkipCounter(), NullLogger.Instance, TimeProvider.System);
            stream.Offer(Solid(w, h, (1, 2, 3)), Header(1, w, h));
            Assert.True((await Next(stream)).Header.Keyframe);
            hold.Reset();

            // A keyframe asked for: the session is replaced. Its reader never ends, so after the bound the stream
            // gives ffmpeg up instead of starting a second one beside it, and its frames are PNG.
            stream.RequestKeyframe();
            stream.Offer(Solid(w, h, (1, 2, 3)), Header(2, w, h));
            Assert.True(await Until(() => backends.Quarantines(backend) == 1), "the teardown is given up on at the bound");
            Assert.True(await Until(() => stream.Route(w, h) == VideoStream.Failed), "the stream's frames are PNG");
            await Task.Delay(200, Ct);
            Assert.Equal(1, backend.Starts);
            Assert.False(IsRunning(backend.ProcessIds[0]), "the process was killed even so");
            Assert.False(backends.IsGivenUp(backend), "given up for the stream only");
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task AProbeWhoseTeardownHangs_IsUnavailable_CountsAsQuarantined_AndNoSessionStartsBesideIt()
    {
        TestFfmpeg.SkipWithout();
        using var hold = new ManualResetEventSlim(false);
        try
        {
            // The probe's encode gives its keyframe, but its reader never ends: its teardown passes the bound.
            var backend = Backend(new FfmpegFaults { HoldReader = hold });
            var backends = new H264Backends([backend], NullLogger.Instance);
            var leaked = Quarantine.Leaked;
            Assert.Equal(H264Probe.Unavailable, await backends.ProbeAsync(backend).WaitAsync(TimeSpan.FromSeconds(15), Ct));
            Assert.Equal(1, backends.Quarantines(backend));
            Assert.Equal(leaked + 1, Quarantine.Leaked);
            Assert.True(backends.NoneLeft);

            const int w = 64, h = 64;
            using var stream = new VideoStream(backends, 30, () => { }, () => { }, new SkipCounter(), NullLogger.Instance, TimeProvider.System);
            stream.Offer(Solid(w, h, (1, 2, 3)), Header(1, w, h));
            Assert.True(await Until(() => stream.Route(w, h) == VideoStream.Unavailable), "the stream's frames are PNG");
            Assert.Equal(0, backend.Starts);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task AnAbandonedFfmpeg_AndOneNeverRetired_AreKilledWithoutBeingStopped_AndCollectedByTheEngineStop()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");
        // Each stand-in starts a watcher of its own (a child, as a tree kill finds) that writes the stand-in's state until
        // it is gone and collected, then "gone". A kill that stops the process first (SIGSTOP, as a tree kill does on
        // Unix) shows as T; a tree kill also kills the watcher before it writes "gone".
        using var folder = new StandIns();
        var pids = Path.Combine(folder.Path, "pids");
        var states = Path.Combine(folder.Path, "states.");
        var standIn = folder.Write("ffmpeg",
            $"p=$$\necho $p >> '{pids}'\n"
            + $"(while kill -0 $p 2>/dev/null; do ps -o stat= -p $p >> '{states}'$p; done; echo gone >> '{states}'$p) </dev/null >/dev/null 2>&1 &\n"
            + "exec sleep 60");
        using var hold = new ManualResetEventSlim(false);
        var backend = new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance) { Faults = new FfmpegFaults { HoldReader = hold } };
        var backends = new H264Backends([backend], NullLogger.Instance);
        var config = new H264Config(64, 64, 30, VideoStream.SoftwareBitrate);
        try
        {
            // One retired whose teardown passes the bound (its reader never ends): abandoned, counted as quarantined.
            var abandoned = (FfmpegSession)backend.Start(config, _ => { });
            Assert.True(await Until(() => File.Exists(states + abandoned.ProcessId)), "the stand-in's watcher runs");
            Assert.False(await Quarantine.RetireAsync(abandoned, backend, backends, TimeProvider.System, NullLogger.Instance, Ct),
                "its teardown is abandoned at the bound");
            // One that nothing retires before the engine stops.
            var left = (FfmpegSession)backend.Start(config, _ => { });
            Assert.True(await Until(() => File.Exists(states + left.ProcessId)), "the stand-in's watcher runs");
            Assert.True(IsRunning(left.ProcessId), "the session nothing retired runs");

            await backends.StopAsync(Ct);
            foreach (var pid in new[] { abandoned.ProcessId, left.ProcessId })
            {
                Assert.False(IsRunning(pid), $"ffmpeg {pid} is killed and collected by the engine's stop");
                Assert.True(await Until(() => File.Exists(states + pid) && File.ReadAllText(states + pid).Contains("gone", StringComparison.Ordinal)),
                    $"ffmpeg {pid}'s own child outlived the kill (it was signalled alone, never stopped first); it saw: "
                    + string.Join(",", File.Exists(states + pid) ? File.ReadAllLines(states + pid).Select(s => s.Trim()).Distinct() : ["nothing"]));
                Assert.DoesNotContain(File.ReadAllLines(states + pid), s => s.TrimStart().StartsWith('T'));
            }
        }
        finally
        {
            hold.Set();
            foreach (var pid in File.Exists(pids) ? File.ReadAllLines(pids) : [])
            {
                if (IsRunning(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture)))
                {
                    using var process = Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
                    process.Kill();
                }
            }
        }
    }

    // ---- bounded output (V3-1) ----

    /// <summary>What a stand-in floods an output with, in one line: 128 MiB.</summary>
    internal const int Flood = 128 * 1024 * 1024;

    /// <summary>What reading a flood may allocate in all, far below the flood's own size (a string of it is twice that).</summary>
    internal const long FloodAllocationBound = 64L * 1024 * 1024;

    /// <summary>
    /// A shell command writing <see cref="Flood"/> bytes of <c>x</c> with no newline to its output, from a 1 MiB file it
    /// makes in <paramref name="folder"/> (<c>tr</c> alone takes seconds).
    /// </summary>
    internal static string FloodCommand(string folder) =>
        $"(head -c 1048576 /dev/zero | tr '\\000' x > '{folder}/chunk'; i=0; while [ $i -lt {Flood / 1048576} ]; do cat '{folder}/chunk'; i=$((i+1)); done)";

    [Fact]
    public async Task ErrorsFloodedWithoutANewline_AreReadInBoundedMemory_AndTheSessionGoesOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");
        // 128 MiB on its error output with no newline, then quiet with its output open, as a broken ffmpeg might.
        using var folder = new StandIns();
        var flooded = Path.Combine(folder.Path, "flooded");
        var logger = new RecordingLogger();
        var backend = new FfmpegH264(FfmpegDiscovery.At(folder.Write("ffmpeg", $"{FloodCommand(folder.Path)} >&2\ntouch '{flooded}'\nexec sleep 30")), logger);
        var outcomes = new List<H264Outcome>();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var session = (FfmpegSession)backend.Start(new H264Config(64, 64, 30, VideoStream.SoftwareBitrate), o =>
        {
            lock (outcomes)
                outcomes.Add(o);
        });
        // The stand-in's writes block on a full pipe, so once it has written all of it the engine has read nearly all.
        Assert.True(await Until(() => File.Exists(flooded), 60), "the stand-in wrote its flood");
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < FloodAllocationBound, $"reading a {Flood}-byte line allocated {allocated} bytes");

        // The session goes on: a frame is taken, nothing failed.
        Assert.Equal(H264Submit.Accepted, session.Submit(Solid(64, 64, (1, 2, 3)), 64, 64, 0, true, out _));
        await Task.Delay(200, Ct);
        lock (outcomes)
            Assert.Empty(outcomes);
        var teardown = session.Abort();
        Assert.True(await Task.WhenAny(teardown, Task.Delay(Quarantine.Bound, Ct)) == teardown, "the teardown ends within the bound");
        lock (outcomes)
            Assert.Equal([(0L, H264OutcomeKind.Aborted)], outcomes.Select(o => (o.FrameId, o.Kind)));
        Assert.False(IsRunning(session.ProcessId));

        // Its diagnostics: the line logged once, cut at 1,024 characters.
        var logged = Assert.Single(logger.Messages, m => m.Contains("xxxx", StringComparison.Ordinal));
        Assert.Contains(new string('x', 1024), logged, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 1025), logged, StringComparison.Ordinal);
    }
}

/// <summary>Stand-in executables (shell scripts) in a folder of their own, removed afterwards.</summary>
internal sealed class StandIns : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("infopanel-ffmpeg-").FullName;

    /// <summary>Writes an executable shell script named <paramref name="name"/> with <paramref name="body"/>; returns its path.</summary>
    public string Write(string name, string body, string? folder = null)
    {
        var directory = folder == null ? Path : System.IO.Path.Combine(Path, folder);
        Directory.CreateDirectory(directory);
        var file = System.IO.Path.Combine(directory, name);
        File.WriteAllText(file, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return file;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            Directory.Delete(directory);
        Directory.Delete(Path);
    }
}

internal sealed class RecordingLogger : ILogger
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

/// <summary>ffmpeg's tests run alone: they count the process's open files and its ffmpeg processes, and time ffmpeg.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FfmpegCollection
{
    public const string Name = "Ffmpeg";
}
