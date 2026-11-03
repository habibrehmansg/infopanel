using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static InfoPanel.Tests.Preview.Video.MediaFoundationRestartChecks;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// Windows' hardware H.264 encoder through Media Foundation, the real port and catalog: on Windows a working encoder must
/// exist, whichever vendor's (skipped only with <c>INFOPANEL_NO_HARDWARE_ENCODER=1</c>, a PC without one); no test names
/// a vendor. Streams are decoded by ffmpeg, a test tool only (<see cref="TestFfmpeg.DecoderPath"/>: the first
/// <c>ffmpeg.exe</c> on <c>PATH</c>); the engine never runs it here. Timings and the encoder's identity are written to
/// the test output for the record.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(MediaFoundationCollection.Name)]
public class MediaFoundationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string OnlyOnWindows = "Media Foundation exists only on Windows.";
    private const string NoHardwareEncoder = "INFOPANEL_NO_HARDWARE_ENCODER";

    /// <summary>This PC's encoder lacks an identity attribute: <c>device</c>, <c>driver</c> or both, comma-separated (unset: neither).</summary>
    private const string IdentityAbsent = "INFOPANEL_MF_IDENTITY_ABSENT";

    private static void SkipWithoutMediaFoundation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        Assert.SkipWhen(Environment.GetEnvironmentVariable(NoHardwareEncoder) == "1", $"{NoHardwareEncoder}=1: this PC has no hardware H.264 encoder.");
    }

    private static void Note(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static readonly List<MediaFoundationH264> Made = [];

    /// <summary>
    /// A backend over the real catalog whose probe has passed (the run's first probe of each test). The backends made
    /// before it are stopped first, as the engine's stop does (the activation each keeps shut down): one backend at a
    /// time, as in a run. <paramref name="refuseReuse"/>: every re-use's renegotiation is refused (<see cref="RefusingCatalog"/>).
    /// </summary>
    private static MediaFoundationH264 Backend(ILogger? logger = null, bool refuseReuse = false)
    {
        lock (Made)
        {
            foreach (var made in Made)
                made.ReapAsync(TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            Made.Clear();
        }

        IMfCatalog catalog = refuseReuse ? new RefusingCatalog(new MfComCatalog()) : new MfComCatalog();
        var backend = new MediaFoundationH264(catalog, logger ?? NullLogger.Instance);
        lock (Made)
            Made.Add(backend);
        Assert.True(backend.Probe() == H264Probe.Available, $"no hardware H.264 encoder passed the probe; on a PC without one, set {NoHardwareEncoder}=1.");
        return backend;
    }

    /// <summary>Moving noise: the hardest content to encode.</summary>
    private static byte[] Noise(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        return pixels;
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

    private static async Task<(string Stdout, string Stderr)> Ffmpeg(string[] args, MemoryStream? output = null)
    {
        var info = new ProcessStartInfo(TestFfmpeg.DecoderPath!, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ffmpeg = Process.Start(info)!;
        var copy = output != null ? ffmpeg.StandardOutput.BaseStream.CopyToAsync(output, Ct) : Task.CompletedTask;
        var stdout = output == null ? ffmpeg.StandardOutput.ReadToEndAsync(Ct) : Task.FromResult("");
        var stderr = ffmpeg.StandardError.ReadToEndAsync(Ct);
        await ffmpeg.WaitForExitAsync(Ct);
        await copy;
        Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg failed: {await stderr}");
        return (await stdout, await stderr);
    }

    /// <summary>ffmpeg's decode as RGB (BT.709, limited range in): each frame's pixels at <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static async Task<List<byte[]>> Decode(byte[] annexB, int width, int height)
    {
        var folder = Directory.CreateTempSubdirectory("infopanel-mf-");
        var file = Path.Combine(folder.FullName, "stream.h264");
        await File.WriteAllBytesAsync(file, annexB, Ct);
        using var output = new MemoryStream();
        await Ffmpeg(["-hide_banner", "-loglevel", "error", "-xerror", "-f", "h264", "-i", file, "-fps_mode", "passthrough",
            "-vf", "scale=in_color_matrix=bt709:in_range=limited:out_range=full,format=rgb24", "-f", "rawvideo", "-"], output);
        File.Delete(file);
        Directory.Delete(folder.FullName);
        var frameBytes = width * height * 3;
        var bytes = output.ToArray();
        Assert.True(bytes.Length % frameBytes == 0, $"{bytes.Length} decoded bytes aren't whole {width}x{height} frames");
        return [.. Enumerable.Range(0, bytes.Length / frameBytes).Select(i => bytes.AsSpan(i * frameBytes, frameBytes).ToArray())];
    }

    /// <summary>
    /// The SPS's fields by name, as ffmpeg's trace_headers parses them (the first of a name), through the SPS tests' own
    /// helper with the decoder's ffmpeg: its framecrc output and its field-name parser.
    /// </summary>
    private static async Task<Dictionary<string, long>> TraceSps(byte[] avcc)
    {
        var fields = await H264SpsTests.TraceSps(avcc, Ct, TestFfmpeg.DecoderPath);
        return fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value);
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
        var rewritten = H264Sps.RewriteForStream(key.Avcc!, out var problem);
        Assert.True(problem == null, problem);
        return rewritten!;
    }

    private static FrameHeader Header(long tick, int width, int height) => new()
    {
        ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1, PixelWidth = width,
        PixelHeight = height, ProfileWidth = width, ProfileHeight = height, ItemBounds = [], EncodeMs = 0, Skipped = 0,
    };

    // ---- 1: the encoder, its identity, and what the probe tried ----

    [Fact]
    public async Task TheEncoder_IsFound_AndItsIdentityIsWindowsOwn()
    {
        SkipWithoutMediaFoundation();
        var logger = new RecordingLogger();
        var watch = Stopwatch.StartNew();
        var backend = Backend(logger);
        Note($"probe: {watch.ElapsedMilliseconds} ms; forced keyframes {(backend.ChangesLive ? "work" : "don't work")}");
        foreach (var line in logger.Messages)
            Note("log: " + line);
        var identity = backend.Trusted;
        Assert.NotNull(identity);
        Note($"identity: {identity}");
        Assert.Contains(logger.Messages, m => m.Contains("passed", StringComparison.Ordinal));

        // Every entry Windows lists, every attribute of it and of its activated transform, for the record.
        foreach (var dump in DumpEntries())
            Note(dump);

        // The identity is Windows' own report of one graphics adapter: the device key its PNP device id and the driver
        // version its DriverVersion, both of the same adapter (compared, not assumed). Evidence that is missing fails,
        // unless this PC's setting says that attribute is absent here (printed).
        var adapters = await VideoControllers();
        foreach (var (pnp, driver) in adapters)
            Note($"Win32_VideoController: {pnp} driver {driver}");
        var absent = (Environment.GetEnvironmentVariable(IdentityAbsent) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Note($"{IdentityAbsent}={string.Join(",", absent)}");
        var key = identity.DeviceKey is { } k && !k.StartsWith("LUID ", StringComparison.Ordinal) ? k : null;
        var version = identity.DriverVersion;
        Assert.True(key != null || absent.Contains("device"),
            $"no device-instance path on the entry ({identity.DeviceKey ?? "none"}); if this PC's encoder has none, set {IdentityAbsent}=device");
        Assert.True(version != null || absent.Contains("driver"),
            $"no driver version on the transform; if this PC's encoder has none, set {IdentityAbsent}=driver");
        if (key != null)
        {
            var adapter = Assert.Single(adapters, a => string.Equals(a.Pnp, key, StringComparison.OrdinalIgnoreCase));
            if (version != null)
                Assert.True(adapter.Driver == version, $"the driver version {version} isn't the adapter {adapter.Pnp}'s ({adapter.Driver})");
            else
                Note("identity: the device compared; the driver version absent by this PC's setting");
        }
        else if (version != null)
        {
            Assert.Contains(adapters, a => a.Driver == version);
            Note("identity: the driver version compared against every adapter; the device key absent by this PC's setting");
        }
        else
        {
            Note("identity: neither compared; both absent by this PC's setting");
        }
        Note($"MFT_ENUM_ADAPTER_LUID on an entry: {(DumpEntries().Any(d => d.Contains(Mf.AdapterLuid.ToString(), StringComparison.OrdinalIgnoreCase)) ? "yes" : "no")}");

        // A second run (a new backend over a new catalog) probes again and finds the same identity.
        var again = Backend();
        Assert.Equal(identity, again.Trusted);
    }

    private static async Task<List<(string Pnp, string Driver)>> VideoControllers()
    {
        var info = new ProcessStartInfo("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Get-CimInstance Win32_VideoController | ForEach-Object { $_.PNPDeviceID + '|' + $_.DriverVersion }"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var shell = Process.Start(info)!;
        var text = await shell.StandardOutput.ReadToEndAsync(Ct);
        await shell.WaitForExitAsync(Ct);
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(p => p.Length == 2).Select(p => (p[0], p[1]))];
    }

    /// <summary>Every hardware entry and its activated transform, every attribute by GUID and type.</summary>
    private static unsafe List<string> DumpEntries()
    {
        var lines = new List<string>();
        var catalog = new MfComCatalog();
        Assert.True(catalog.EnterThread("test") >= 0);
        try
        {
            Assert.True(catalog.Startup() >= 0);
            var output = new MftRegisterTypeInfo { Major = MfCom.MediaTypeVideo, Sub = Mf.H264 };
            Assert.True(MfNative.MFTEnumEx(MfCom.CategoryVideoEncoder, MediaFoundationH264.EnumFlags, null, &output, out var array, out var count) >= 0);
            try
            {
                for (var i = 0; i < (int)count; i++)
                {
                    var activate = MfWrappers.Wrap<IMFActivate>(((nint*)array)[i]);
                    try
                    {
                        lines.Add($"entry {i}:\n{Dump(activate)}");
                        if (activate.ActivateObject(typeof(IMFTransform).GUID, out var p) >= 0 && p != 0)
                        {
                            var mft = MfWrappers.Wrap<IMFTransform>(p);
                            if (mft.GetAttributes(out var a) >= 0 && a != 0)
                            {
                                var attributes = MfWrappers.Wrap<IMFAttributes>(a);
                                lines.Add($"entry {i}'s transform:\n{Dump(attributes)}");
                                MfWrappers.Free(attributes);
                            }

                            activate.ShutdownObject();
                            MfWrappers.Free(mft);
                        }
                    }
                    finally
                    {
                        MfWrappers.Free(activate);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(array);
            }
        }
        finally
        {
            catalog.LeaveThread();
        }

        return lines;
    }

    private static unsafe string Dump(IMFAttributes a)
    {
        if (a.GetCount(out var count) < 0)
            return "    (no count)";
        var lines = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            if (a.GetItemByIndex(i, out var key, null) < 0)
                continue;
            a.GetItemType(key, out var type);
            var value = type switch
            {
                19 => MfAttributes.UInt32(a, key)?.ToString() ?? "?",
                21 => MfAttributes.UInt64(a, key) is { } v ? $"{v} (0x{v:X16})" : "?",
                72 => a.GetGUID(key, out var g) >= 0 ? g.ToString() : "?",
                31 => MfAttributes.String(a, key, MfPlatObjects.Instance) ?? "?",
                _ => $"(type {type})",
            };
            lines.Add($"    {key} = {value}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The SPS helper itself, on any OS with an ffmpeg to decode with: over the committed parameter sets of the AMD
    /// encoder (1600×960, Baseline, level 4.0), as captured and as the stream rewrites them, it reads the fields by name.
    /// </summary>
    [Fact]
    public async Task TheSpsHelper_ReadsTheFieldsByName_OverTheCommittedParameterSets()
    {
        TestFfmpeg.SkipWithoutDecoder();
        var captured = Convert.FromHexString(AnnexBTests.AmdAvcc);
        var sps = await TraceSps(captured);
        foreach (var (name, value) in sps)
            Note($"captured: {name} = {value}");
        Assert.Equal(66, sps["profile_idc"]);
        Assert.Equal(40, sps["level_idc"]);
        Assert.Equal(1600 / 16 - 1, sps["pic_width_in_mbs_minus1"]);
        Assert.Equal(960 / 16 - 1, sps["pic_height_in_map_units_minus1"]);

        var rewritten = H264Sps.RewriteForStream(captured, out var problem);
        Assert.Null(problem);
        var after = await TraceSps(rewritten!);
        foreach (var (name, value) in after)
            Note($"rewritten: {name} = {value}");
        Assert.Equal(66, after["profile_idc"]);
        Assert.Equal(1, after["bitstream_restriction_flag"]);
        Assert.Equal(0, after["max_num_reorder_frames"]);
        Assert.Equal(0, after["video_full_range_flag"]);
    }

    // ---- 2: the stream ----

    [Fact]
    public async Task TheAvccSaysNoReordering_AndEachUnitDecodesToOneFrame()
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        const int w = 320, h = 192;
        var frames = Enumerable.Range(0, 12).Select(i => Solid(w, h, ((byte)(i * 20), 80, 160))).ToList();
        var outcomes = Encode(Backend(), w, h, frames);
        Assert.All(outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        var avcc = Keyframe(outcomes);
        var sps = await TraceSps(avcc);
        Assert.Equal(66, sps["profile_idc"]);
        Assert.Equal(1, sps["bitstream_restriction_flag"]);
        Assert.Equal(0, sps["max_num_reorder_frames"]);
        Assert.Equal(0, sps["video_full_range_flag"]);
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
        Assert.Equal(frames.Count, decoded.Count);
    }

    // ---- 3: sizes ----

    [Fact]
    public async Task AnOddSize_IsPaddedToEvenSides_InBlack()
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        const int w = 321, h = 241;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (255, 255, 255))]);
        var frame = Assert.Single(await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w + 1, h + 1));
        Assert.True(frame[((h / 2) * (w + 1) + w / 2) * 3] > 230, "the picture is white");
        var padding = frame.AsSpan(((h / 2) * (w + 1) + w) * 3, 3).ToArray();
        Assert.All(padding, c => Assert.True(c < 40, $"the padding column is black ({string.Join(",", padding)})"));
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(802, 602)]
    public async Task UnalignedSizes_DecodeAtTheirOwnSize(int w, int h)
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (40, 90, 200)), Solid(w, h, (200, 90, 40))]);
        var decoded = await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w, h);
        Assert.Equal(2, decoded.Count);
        var (r, g, b) = Centre(decoded[1], w, h);
        Assert.True(Math.Abs(r - 200) <= 4 && Math.Abs(g - 90) <= 4 && Math.Abs(b - 40) <= 4, $"decoded as #{r:x2}{g:x2}{b:x2}");
    }

    [Fact]
    public void TheLargestSize_StartsOrIsUnsupported_NeverAFault_AndTheSmallestIsRecorded()
    {
        SkipWithoutMediaFoundation();
        var backend = Backend();
        try
        {
            var outcomes = Encode(backend, VideoStream.MaxSide, 2304, [Solid(VideoStream.MaxSide, 2304, (40, 90, 200))]);
            Assert.Equal(H264OutcomeKind.Encoded, Assert.Single(outcomes).Kind);
            Note("4096x2304: starts");
        }
        catch (H264StartException ex)
        {
            Assert.True(ex.Problem == H264StartProblem.Unsupported, $"4096x2304: {ex.Problem}, {ex.Message}");
            Note($"4096x2304: unsupported ({ex.Message})");
        }

        foreach (var side in new[] { 16, 32, 48, 64, 96, 128 })
        {
            try
            {
                Encode(backend, side, side, [Solid(side, side, (1, 2, 3))]);
                Note($"the smallest size taken: {side}x{side}");
                return;
            }
            catch (H264StartException ex)
            {
                Note($"{side}x{side}: {ex.Problem}, {ex.Message}");
            }
        }

        Assert.Fail("no size up to 128x128 started");
    }

    [Fact]
    public void AtTheLargestSize_ThreeHundredFramesAtThirty_KeepUp()
    {
        SkipWithoutMediaFoundation();
        var backend = Backend();
        var (w, h) = LargestAccepted(backend);
        Note($"the largest size it starts at: {w}x{h}; measured there");

        var frames = Enumerable.Range(0, 8).Select(i => Noise(w, h, i)).ToArray();
        var submits = new List<double>();
        var encodes = new List<double>();
        var submitted = new Dictionary<long, long>();
        var outcomes = new List<H264Outcome>();
        var session = backend.Start(new H264Config(w, h, 30, VideoStream.HardwareBitrate), outcome =>
        {
            lock (outcomes)
            {
                outcomes.Add(outcome);
                if (submitted.TryGetValue(outcome.FrameId, out var at))
                    encodes.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            }
        });
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var due = TimeSpan.FromSeconds(i / 30.0);
                if (due > wall.Elapsed)
                    Thread.Sleep(due - wall.Elapsed);
                var start = Stopwatch.GetTimestamp();
                lock (outcomes)
                    submitted[i] = start;
                var result = session.Submit(frames[i % frames.Length], w, h, i, i == 0, out _);
                submits.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Assert.Equal(H264Submit.Accepted, result);
            }

            var settle = Stopwatch.StartNew();
            while (outcomes.Count < 300 && settle.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(10);
        }
        finally
        {
            Assert.True(session.Abort().Wait(Quarantine.Bound, Ct), "the session tears down");
        }

        var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        double P(List<double> v, double p) => v.Order().ElementAt(Math.Clamp((int)Math.Ceiling(p / 100 * v.Count) - 1, 0, v.Count - 1));
        lock (outcomes)
        {
            Note($"{w}x{h}: conversion (Submit) p50 {P(submits, 50):F2} ms p95 {P(submits, 95):F2} ms; encodeMs p95 {P(encodes, 95):F2} ms; " +
                 $"{outcomes.Count(o => o.Kind == H264OutcomeKind.Encoded)} of 300 encoded; CPU {used.TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100:F0}% of a core");
            Assert.Equal(300, outcomes.Count(o => o.Kind == H264OutcomeKind.Encoded));
            if (!EngineIsJitOptimised)
            {
                Note("the engine isn't JIT-optimised (a Debug build): the 33 ms bound applies to optimised builds only; every frame encoded is asserted, the timing printed");
                return;
            }

            Note("the engine is JIT-optimised: the 33 ms bound is asserted");
            Assert.True(P(encodes, 95) < 33, $"encodeMs p95 {P(encodes, 95):F1} ms is over one frame at 30 a second");
        }
    }

    /// <summary>Whether the engine's code runs JIT-optimised, as it ships: its assembly's <see cref="DebuggableAttribute"/> doesn't disable the optimiser.</summary>
    internal static bool EngineIsJitOptimised =>
        typeof(MediaFoundationH264).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }, ..];

    /// <summary>
    /// The size of the largest pixel area this encoder starts at, up to 4096 × 2304 (<see cref="VideoStream.MaxSide"/> a
    /// side): that size if it starts; else the best of 4096 × 2160, the 16:9 sizes (bisection over the width, a multiple
    /// of 16), the widest at 2160 high and the tallest at 4096 wide (bisections; an encoder that takes a size takes the
    /// smaller ones along the same axis). Every start refused is awaited to its teardown before the next size, so a
    /// capacity-limited encoder isn't busy with it. A refusal must be Unsupported; any other problem fails the test. The
    /// result and the sizes it beat are printed.
    /// </summary>
    private static (int Width, int Height) LargestAccepted(IH264Backend backend)
    {
        bool Starts(int w, int h)
        {
            try
            {
                Assert.Equal(H264OutcomeKind.Encoded, Assert.Single(Encode(backend, w, h, [Solid(w, h, (1, 2, 3))])).Kind);
                Note($"{w}x{h}: starts");
                return true;
            }
            catch (H264StartException ex) when (ex.Problem == H264StartProblem.Unsupported)
            {
                Note($"{w}x{h}: unsupported ({ex.Message})");
                if (ex.Teardown is { } teardown)
                    Assert.True(teardown.Wait(Quarantine.Bound, Ct), $"{w}x{h}'s refused start tears down");
                return false;
            }
        }

        // In units: lo starts, hi doesn't; the largest unit count that starts.
        static int Bisect(int lo, int hi, Func<int, bool> starts)
        {
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (starts(mid))
                    lo = mid;
                else
                    hi = mid;
            }

            return lo;
        }

        static int HeightOf(int width) => (width * 9 / 16 + 1) & ~1;
        const int MaxHeight = 2304;
        var best = (Width: 0, Height: 0);
        void Accept(int w, int h)
        {
            if ((long)w * h > (long)best.Width * best.Height)
                best = (w, h);
        }

        if (Starts(VideoStream.MaxSide, MaxHeight))
            return (VideoStream.MaxSide, MaxHeight);
        Assert.True(Starts(1280, 720), "1280x720 starts");
        Accept(1280, 720);

        // 16:9: the macroblock columns from 1280 up to below 4096.
        var columns = Bisect(1280 / 16, VideoStream.MaxSide / 16, c => Starts(c * 16, HeightOf(c * 16)));
        Accept(columns * 16, HeightOf(columns * 16));

        // The tall edge: 4096 wide, a multiple of 8 high, up to below 2304.
        var fullHeight = Starts(VideoStream.MaxSide, 2160);
        if (fullHeight)
            Accept(VideoStream.MaxSide, 2160);
        if (fullHeight || Starts(VideoStream.MaxSide, 720))
        {
            var rows = Bisect(fullHeight ? 2160 / 8 : 720 / 8, (fullHeight ? MaxHeight : 2160) / 8, r => Starts(VideoStream.MaxSide, r * 8));
            Accept(VideoStream.MaxSide, rows * 8);
        }

        // The wide edge: 2160 high, a multiple of 16 wide, below 4096.
        if (!fullHeight && Starts(1280, 2160))
        {
            var wide = Bisect(1280 / 16, VideoStream.MaxSide / 16, c => Starts(c * 16, 2160));
            Accept(wide * 16, 2160);
        }

        Note($"the largest pixel area that starts: {best.Width}x{best.Height} ({(long)best.Width * best.Height} pixels)");
        return best;
    }

    // ---- 4, 5: colour and pairing ----

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0x2a, 0x6f, 0xd0)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 255, 255)]
    [InlineData(16, 16, 16)]
    public async Task FlatColours_DecodeWithinFourLevels(byte r, byte g, byte b)
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        const int w = 256, h = 160;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (r, g, b))]);
        var (dr, dg, db) = Centre((await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w, h)).Single(), w, h);
        Assert.True(Math.Abs(dr - r) <= 4 && Math.Abs(dg - g) <= 4 && Math.Abs(db - b) <= 4, $"#{r:x2}{g:x2}{b:x2} decoded as #{dr:x2}{dg:x2}{db:x2}");
    }

    [Fact]
    public async Task EachDecodedPicture_IsTheFrameItsHeaderNames()
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        // Not square, so a swapped width and height shows; both sides at least the 128 every hardware encoder takes.
        const int w = 192, h = 128;
        (byte, byte, byte)[] colours = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 255, 255), (0, 0, 0)];
        var backends = new H264Backends([Backend()], NullLogger.Instance);
        var skipped = new SkipCounter();
        using var stream = new VideoStream(backends, 30, () => { }, () => { }, skipped, NullLogger.Instance, TimeProvider.System);
        var sent = new List<EncodedFrame>();
        for (var tick = 1; tick <= colours.Length; tick++)
        {
            Assert.Null(stream.Route(w, h, out _));
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
        var avcc = Convert.FromBase64String(sent[0].Header.Description!);
        Assert.Equal("hardware", sent[0].Header.Encoder);
        var decoded = await Decode(AnnexB(avcc, sent.Select(f => f.Payload.ToArray())), w, h);
        Assert.Equal(colours.Length, decoded.Count);
        for (var i = 0; i < colours.Length; i++)
        {
            var (r, g, b) = Centre(decoded[i], w, h);
            var (er, eg, eb) = colours[sent[i].Header.Tick - 1];
            Assert.True(Math.Abs(r - er) <= 8 && Math.Abs(g - eg) <= 8 && Math.Abs(b - eb) <= 8, $"frame {sent[i].Header.Tick} decoded as #{r:x2}{g:x2}{b:x2}");
        }
    }

    // ---- 6, 7: no hold, forced keyframes ----

    [Fact]
    public void AStillPanelsOneEdit_ArrivesWithoutASecondFrame()
    {
        SkipWithoutMediaFoundation();
        const int w = 640, h = 384;
        var outcomes = Encode(Backend(), w, h, [Solid(w, h, (20, 20, 20)), Solid(w, h, (200, 20, 20))]);
        // Both frames encoded, each the frame it names, before what their delivery says: Encode waited for the edit's
        // outcome with no frame after it, so an encoded edit arrived without a second frame.
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        Assert.Equal([0L, 1L], outcomes.Select(o => o.FrameId));
        Assert.True(outcomes[0].Keyframe);
        Assert.False(outcomes[1].Keyframe);
    }

    [Fact]
    public void AForcedKeyframe_OnALiveSession_IsAnIdr_OrTheProbeSaidSo()
    {
        SkipWithoutMediaFoundation();
        var logger = new RecordingLogger();
        var backend = Backend(logger);
        const int w = 320, h = 192;
        var frames = Enumerable.Range(0, 6).Select(i => Solid(w, h, ((byte)(i * 30), 60, 90))).ToList();
        if (!backend.ChangesLive)
        {
            // The probe found forcing doesn't work: keyframe requests restart the session instead (the stream's tests).
            Note("this encoder ignores forced keyframes; the probe said so");
            Assert.Contains(logger.Messages, m => m.Contains("keyframe", StringComparison.OrdinalIgnoreCase));
            return;
        }

        var outcomes = Encode(backend, w, h, frames, forced: i => i is 0 or 3);
        Assert.All(outcomes, o => Assert.Equal(H264OutcomeKind.Encoded, o.Kind));
        Assert.True(outcomes[3].Keyframe, "the forced frame came out an IDR");
        Assert.False(outcomes[2].Keyframe);
    }

    // ---- 8: irregular times and the bit rate ----

    [Fact]
    public async Task IrregularTimes_PairAndDecode_AndTheBitRateHoldsAtThirtyAndTen()
    {
        SkipWithoutMediaFoundation();
        TestFfmpeg.SkipWithoutDecoder();
        const int w = 320, h = 192;
        int[] gaps = [0, 5, 2000, 33, 500, 5, 100, 1000, 16];
        var outcomes = Encode(Backend(), w, h, [.. gaps.Select((_, i) => Solid(w, h, ((byte)(i * 25), 100, 50)))], i => TimeSpan.FromMilliseconds(gaps[i]));
        Assert.Equal(gaps.Length, outcomes.Count(o => o.Kind == H264OutcomeKind.Encoded));
        Assert.Equal(gaps.Length, (await Decode(AnnexB(Keyframe(outcomes), outcomes.Select(o => o.Data!)), w, h)).Count);

        // The bit rate on what the preview sends: a dashboard changing every frame, ten seconds at 30 and at 10 frames a
        // second (send-on-change: fewer frames than the session's 30). At 30 the rate control holds about the target. At
        // 10 an encoder may hold the rate over time or each frame's budget at the session's 30 (2 × 10 / 30 Mbit/s;
        // Media Foundation's hardware CBR does either): the rate stays at most the target's upper bound and at least
        // three quarters of the per-frame budget's rate, and no frame after the first IDR is over four frames' budget.
        const int bw = 1280, bh = 720;
        foreach (var fps in new[] { 30, 10 })
        {
            var frames = new Generated(fps * 10, i => Dashboard(bw, bh, i * 30 / fps));
            var start = Stopwatch.StartNew();
            var rate = Encode(Backend(), bw, bh, frames, i => TimeSpan.FromSeconds(i / (double)fps) - start.Elapsed);
            Assert.Equal(fps * 10, rate.Count(o => o.Kind == H264OutcomeKind.Encoded));
            var bits = rate.Sum(o => (long)(o.Data?.Length ?? 0)) * 8.0 / 10;
            var largest = rate.Skip(1).Max(o => o.Data?.Length ?? 0);
            Note($"{fps} fps: {bits / 1e6:F2} Mbit/s; the first frame {rate[0].Data?.Length} bytes, the largest after it {largest}");
            if (fps == 30)
            {
                Assert.InRange(bits, VideoStream.HardwareBitrate * 0.75, VideoStream.HardwareBitrate * 1.25);
            }
            else
            {
                Assert.InRange(bits, VideoStream.HardwareBitrate * 10 * 0.75 / 30, VideoStream.HardwareBitrate * 1.25);
                Assert.True(largest <= VideoStream.HardwareBitrate * 4 / 30.0 / 8, $"a frame after the first IDR is {largest} bytes, over four frames' budget");
            }
        }
    }

    /// <summary>Frames made when asked for, so ten seconds of 1280 × 720 aren't all held at once.</summary>
    private sealed class Generated(int count, Func<int, byte[]> make) : IReadOnlyList<byte[]>
    {
        public byte[] this[int index] => make(index);

        public int Count => count;

        public IEnumerator<byte[]> GetEnumerator() => Enumerable.Range(0, count).Select(make).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A preview's kind of picture at time <paramref name="t"/> (30 a second), laid out for 1280 × 720: a dark panel,
    /// eight colour swatches, fixed thin strokes like text, a bar that fills, a moving wave and digits that change every
    /// third frame.
    /// </summary>
    private static byte[] Dashboard(int w, int h, int t)
    {
        var px = new byte[w * h * 4];
        void Fill(int x0, int y0, int fw, int fh, byte r, byte g, byte b)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(h, y0 + fh); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(w, x0 + fw); x++)
            {
                var i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, 255);
            }
        }

        Fill(0, 0, w, h, 18, 20, 26);
        for (var s = 0; s < 8; s++)
            Fill(30 + s * 150, 30, 120, 120, (byte)(40 + s * 25), (byte)(200 - s * 20), (byte)(60 + s * 20));
        var strokes = new Random(7);
        for (var line = 0; line < 14; line++)
        {
            var y0 = 180 + line * 22;
            for (var x = 30; x < w / 2;)
            {
                var glyph = strokes.Next(6, 12);
                for (var k = 0; k < 6; k++)
                {
                    var gx = x + strokes.Next(glyph);
                    var gy = y0 + strokes.Next(14);
                    if (strokes.Next(2) == 0)
                        Fill(gx, gy, 1, 14 - (gy - y0), 220, 222, 228);
                    else
                        Fill(gx, gy, glyph, 1, 220, 222, 228);
                }

                x += glyph + 2;
            }
        }

        var fraction = t % 90 / 89.0;
        Fill(w / 2, 180, w / 2 - 30, 50, 40, 44, 54);
        Fill(w / 2, 180, (int)((w / 2 - 30) * fraction), 50, 60, 200, 120);
        for (var x = 0; x < w / 2 - 30; x++)
        {
            var y = 380 + (int)(100 * Math.Sin((x + t * 8) / 60.0) * Math.Cos((x - t * 3) / 210.0));
            Fill(w / 2 + x, y - 1, 1, 3, 120, 180, 255);
        }

        var digits = new Random(t / 3);
        for (var d = 0; d < 12; d++)
        for (var seg = 0; seg < 7; seg++)
            if (digits.Next(3) != 0)
                Fill(30 + d * 100 + seg * 8, 540 + seg % 3 * 40, 50, 10, 240, 240, 245);
        return px;
    }

    // ---- 9: sessions at once ----

    [Fact]
    public void TwoSessionsThenFour_EachEncodes_OrAnExtraOneIsBusy()
    {
        SkipWithoutMediaFoundation();
        var backend = Backend();
        foreach (var count in new[] { 2, 4 })
        {
            var sessions = new List<IH264Session>();
            var encoded = 0;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        sessions.Add(backend.Start(new H264Config(320, 192, 30, VideoStream.HardwareBitrate),
                            o => { if (o.Kind == H264OutcomeKind.Encoded) Interlocked.Increment(ref encoded); }));
                    }
                    catch (H264StartException ex)
                    {
                        // Busy only beside a session that started: the first must start.
                        Assert.True(i > 0 && ex.Problem == H264StartProblem.Busy, $"session {i + 1} of {count}: {ex.Problem}, {ex.Message}");
                        Note($"session {i + 1} of {count}: busy");
                        if (ex.Teardown is { } teardown)
                            Assert.True(teardown.Wait(Quarantine.Bound, Ct), $"session {i + 1} of {count}'s refused start tears down");
                    }
                }

                Assert.NotEmpty(sessions);
                for (var i = 0; i < sessions.Count; i++)
                    Assert.Equal(H264Submit.Accepted, sessions[i].Submit(Solid(320, 192, (9, 9, 9)), 320, 192, 0, true, out _));
                var watch = Stopwatch.StartNew();
                while (Volatile.Read(ref encoded) < sessions.Count && watch.Elapsed < TimeSpan.FromSeconds(5))
                    Thread.Sleep(10);
                Assert.Equal(sessions.Count, Volatile.Read(ref encoded));
                Note($"{count} at once: {sessions.Count} encoded");
            }
            finally
            {
                foreach (var session in sessions)
                    Assert.True(session.Abort().Wait(Quarantine.Bound, Ct));
            }
        }
    }

    // ---- 10: restarts and references ----

    /// <summary>200 restarts on one activation over the real encoder (<see cref="MediaFoundationRestartChecks.OnOneActivation"/>).</summary>
    [Fact]
    public void TwoHundredRestarts_OnOneActivation_KeepHandlesAndBytesFlat()
    {
        SkipWithoutMediaFoundation();
        MediaFoundationRestartChecks.OnOneActivation(Hardware);
    }

    /// <summary>
    /// The fallback's 200 restarts over the real encoder, refusing every renegotiation (<see cref="RefusingCatalog"/>;
    /// <see cref="MediaFoundationRestartChecks.Fallback"/>).
    /// </summary>
    [Fact]
    public void TwoHundredRestarts_GiveBackEveryWrapperThreadHandleAndByte()
    {
        SkipWithoutMediaFoundation();
        MediaFoundationRestartChecks.Fallback(Hardware);
    }

    /// <summary>The restart checks' bed on the real encoder: the process's handles, bytes, threads and wrappers.</summary>
    private static RestartBed Hardware => new()
    {
        Backend = (log, refuseReuse) => Backend(log, refuseReuse),
        Compacted = () =>
        {
            using var process = Process.GetCurrentProcess();
            return Compacted(process);
        },
        Resources = () =>
        {
            using var process = Process.GetCurrentProcess();
            return Resources(process);
        },
        Wrappers = () => MfWrappers.Live,
        Threads = () =>
        {
            using var process = Process.GetCurrentProcess();
            return process.Threads.Count;
        },
        Managed = () => GC.GetTotalMemory(forceFullCollection: true),
        Catalog = () => new MfComCatalog(),
    };

    /// <summary>
    /// <see cref="Resources"/> after an aggressive, compacting collection, which gives the GC's free memory back to the
    /// OS: private bytes then move with what is kept, not with how much the GC holds on to.
    /// </summary>
    private static (long Handles, long Bytes) Compacted(Process process)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        process.Refresh();
        return (process.HandleCount, process.PrivateMemorySize64);
    }

    private static (long Handles, long Bytes) Resources(Process process)
    {
        Settle();
        process.Refresh();
        return (process.HandleCount, process.PrivateMemorySize64);
    }

    // ---- 10b: renegotiation on one activation (opt-in proof, INFOPANEL_MF_RENEGOTIATION=1) ----

    private const string RenegotiationProof = "INFOPANEL_MF_RENEGOTIATION";

    /// <summary>
    /// The proof re-use rests on, from the port's raw calls (no session code): one activation encodes at one size, is
    /// flushed, its streaming ended, given the output then the input type of another size, streaming begun and the stream
    /// started again, and encodes at the new size; its first output there must be an IDR whose own SPS describes the new
    /// size, and the segment must decode. Then the same at the same size with no forced frame (a keyframe restart), and
    /// 60 renegotiations over three sizes with the process's handles counted. Every call's answer and the events between
    /// the phases are written to the output.
    /// </summary>
    [Fact]
    public async Task OneActivation_RenegotiatesItsTypes_AndItsFirstFrameAtTheNewSizeIsAnIdr()
    {
        SkipWithoutMediaFoundation();
        Assert.SkipUnless(Environment.GetEnvironmentVariable(RenegotiationProof) == "1", $"opt-in: {RenegotiationProof}=1");
        TestFfmpeg.SkipWithoutDecoder();
        var trusted = Backend().Trusted!;
        var catalog = new MfComCatalog();
        Assert.True(catalog.EnterThread("test") >= 0);
        try
        {
            Assert.True(catalog.Startup() >= 0);
            Assert.True(catalog.Enumerate(MediaFoundationH264.EnumFlags, out var entries) >= 0);
            var entry = entries.First(e => e.GetString(Mf.HardwareUrl) == trusted.HardwareUrl);
            using var raw = new RawEncoder(catalog, entry.CreatePort());
            foreach (var e in entries)
                e.Dispose();

            raw.Start(128);
            var first = raw.Pump(128, 3, forceFirst: true);
            Note($"128x128: {Describe(first)}");

            raw.Renegotiate(160);
            var bigger = raw.Pump(160, 3, forceFirst: false);
            Note($"160x160 after the renegotiation: {Describe(bigger)}");
            var unit = new AnnexB().Normalise(bigger[0].Data);
            Assert.True(unit.Problem == null, unit.Problem);
            Assert.True(unit.Keyframe && unit.NewSps != null, "the first output at the new size is an IDR with its SPS");
            Assert.Equal((160, 160), H264Sps.PictureSize(unit.NewSps!));
            var decoded = await Decode(AnnexB(H264Sps.RewriteForStream(unit.Avcc!, out _)!, Units(bigger)), 160, 160);
            Assert.Equal(3, decoded.Count);
            Note($"decoded {decoded.Count} frames at 160x160; centre of the last {Centre(decoded[^1], 160, 160)}");

            raw.Renegotiate(160);
            var same = raw.Pump(160, 3, forceFirst: false);
            Note($"160x160 again (a keyframe restart, nothing forced): {Describe(same)}");
            var sameUnit = new AnnexB().Normalise(same[0].Data);
            Note($"its first output: keyframe {sameUnit.Keyframe}, SPS {(sameUnit.NewSps != null ? "in band" : "absent")}");

            using var process = Process.GetCurrentProcess();
            Settle();
            process.Refresh();
            var handles = process.HandleCount;
            var bytes = process.PrivateMemorySize64;
            var idrs = 0;
            for (var i = 0; i < 60; i++)
            {
                var side = 128 + (i % 3) * 2;
                raw.Renegotiate(side);
                var outputs = raw.Pump(side, 1, forceFirst: true);
                if (new AnnexB().Normalise(outputs[0].Data).Keyframe)
                    idrs++;
            }

            Settle();
            process.Refresh();
            Note($"60 renegotiations over 128/130/132: {idrs} first frames IDR; handles {process.HandleCount - handles:+#;-#;0}, private bytes {process.PrivateMemorySize64 - bytes:+#;-#;0}");
            Note("calls: " + string.Join("; ", raw.Log.Take(60)));
            Assert.Equal(60, idrs);
        }
        finally
        {
            catalog.LeaveThread();
        }

        static string Describe(List<MfOutput> outputs) =>
            string.Join(", ", outputs.Select(o => $"{o.Data.Length} B at {o.SampleTime}{(new AnnexB().Normalise(o.Data).Keyframe ? " IDR" : "")}"));

        static IEnumerable<byte[]> Units(List<MfOutput> outputs)
        {
            var annexB = new AnnexB();
            foreach (var o in outputs)
                yield return annexB.Normalise(o.Data).Data!;
        }
    }

    /// <summary>One activation over the port's raw calls, its events read on a thread of their own, for the renegotiation proof.</summary>
    private sealed class RawEncoder : IDisposable
    {
        private readonly MfComCatalog _catalog;
        private readonly IMfEncoderPort _port;
        private readonly System.Collections.Concurrent.BlockingCollection<MfEvent> _events = new();
        private readonly Thread _reader;
        private MfStreamInfo _input;
        private MfStreamInfo _output;
        private long _time;
        private int _banked;

        public readonly List<string> Log = [];

        public RawEncoder(MfComCatalog catalog, IMfEncoderPort port)
        {
            _catalog = catalog;
            _port = port;
            Assert.Equal(0, _port.Activate());
            _reader = new Thread(() =>
            {
                _catalog.EnterThread("events");
                try
                {
                    while (_port.NextEvent(out var e) >= 0)
                        _events.Add(e);
                }
                finally
                {
                    _catalog.LeaveThread();
                    _events.CompleteAdding();
                }
            }) { IsBackground = true };
        }

        private int Call(string what, int hr)
        {
            Log.Add($"{what} {Mf.Hr(hr)}");
            return hr;
        }

        private void Types(int side)
        {
            Assert.True(Call($"output {side}", _port.SetOutputType(new MfVideoType(Mf.H264, side, side)
            {
                FrameRate = 30, Bitrate = 2_000_000, Profile = Mf.ProfileBaseline, InterlaceMode = Mf.InterlaceProgressive,
                Matrix = Mf.MatrixBt709, Primaries = Mf.PrimariesBt709, Transfer = Mf.TransferBt709, NominalRange = Mf.NominalRange16To235,
            })) >= 0, $"the output type at {side}: {Log[^1]}");
            Assert.True(Call($"input {side}", _port.SetInputType(new MfVideoType(Mf.Nv12, side, side)
            {
                FrameRate = 30, DefaultStride = side, InterlaceMode = Mf.InterlaceProgressive,
                Matrix = Mf.MatrixBt709, Primaries = Mf.PrimariesBt709, Transfer = Mf.TransferBt709, NominalRange = Mf.NominalRange16To235,
            })) >= 0, $"the input type at {side}: {Log[^1]}");
            Assert.True(_port.GetInputStreamInfo(out _input) >= 0);
            Assert.True(_port.GetOutputStreamInfo(out _output) >= 0);
            Assert.True(Call("begin streaming", _port.ProcessMessage(Mf.MessageNotifyBeginStreaming)) >= 0);
            Assert.True(Call("start of stream", _port.ProcessMessage(Mf.MessageNotifyStartOfStream)) >= 0);
        }

        public void Start(int side)
        {
            Assert.True(Call("unlock", _port.SetUInt32(Mf.TransformAsyncUnlock, 1)) >= 0);
            Call("low latency", _port.SetCodecValue(Mf.CodecLowLatencyMode, 1));
            Call("CBR", _port.SetCodecValue(Mf.CodecRateControlMode, Mf.RateControlCbr));
            Call("GOP", _port.SetCodecValue(Mf.CodecGopSize, 600));
            Call("B-frames", _port.SetCodecValue(Mf.CodecBFrames, 0));
            Types(side);
            _reader.Start();
        }

        /// <summary>Flush, end streaming, the output type then the input type at <paramref name="side"/>, begin streaming, start of stream; events in between logged.</summary>
        public void Renegotiate(int side)
        {
            Assert.True(Call("flush", _port.ProcessMessage(Mf.MessageCommandFlush)) >= 0, Log[^1]);
            Assert.True(Call("end streaming", _port.ProcessMessage(Mf.MessageNotifyEndStreaming)) >= 0, Log[^1]);
            var before = Drain();
            Types(side);
            Log.Add($"banked {_banked} before; events after the flush: {before}");
            _banked = 0;
        }

        private string Drain()
        {
            var seen = new List<string>();
            while (_events.TryTake(out var e, TimeSpan.FromMilliseconds(50)))
                seen.Add(e.Type.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return seen.Count == 0 ? "none" : string.Join(",", seen);
        }

        /// <summary>Frames in one at a time, each on a NeedInput (one banked before counts), each output awaited before the next input.</summary>
        public List<MfOutput> Pump(int side, int frames, bool forceFirst)
        {
            var nv12Layout = Nv12Layout.Packed(side, side);
            var outputs = new List<MfOutput>();
            var inputs = 0;
            var needInputs = 0;
            while (outputs.Count < frames)
            {
                if (inputs == outputs.Count && inputs < frames && _banked > 0)
                {
                    _banked--;
                    Input(side, nv12Layout, inputs++, forceFirst && inputs == 1);
                    continue;
                }

                Assert.True(_events.TryTake(out var e, TimeSpan.FromSeconds(5)), $"an event within 5 s at {side} ({inputs} in, {outputs.Count} out, {needInputs} NeedInput)");
                Assert.True(e.Status >= 0 && e.Type != Mf.EventError, $"event {e.Type} {Mf.Hr(e.Status)}");
                if (e.Type == Mf.EventNeedInput)
                {
                    needInputs++;
                    if (inputs == outputs.Count && inputs < frames)
                        Input(side, nv12Layout, inputs++, forceFirst && inputs == 1);
                    else
                        _banked++;
                }
                else if (e.Type == Mf.EventHaveOutput)
                {
                    var hr = _port.ProcessOutput(MfOutputAllocation.Of(_output), out var output);
                    Assert.True(hr >= 0 || hr == Mf.TransformNeedMoreInput, $"ProcessOutput {Mf.Hr(hr)}");
                    if (output != null)
                        outputs.Add(output);
                }
            }

            Log.Add($"pumped {frames} at {side}: {needInputs} NeedInput, banked {_banked}");
            return outputs;
        }

        private void Input(int side, Nv12Layout layout, int index, bool force)
        {
            if (force)
                Call("force", _port.SetCodecValue(Mf.CodecForceKeyFrame, 1));
            var nv12 = new byte[layout.Size];
            Nv12.Write(Solid(side, side, ((byte)(40 + 50 * index), 120, 200)), side, side, nv12, layout);
            Assert.True(_port.CreateInputBuffer(Math.Max(nv12.Length, _input.Size), Math.Max(_input.Alignment, 1), out var buffer) >= 0);
            using (buffer)
            {
                Assert.True(buffer!.Write(nv12) >= 0);
                var hr = _port.ProcessInput(buffer, _time, 333_333, discontinuity: false);
                Assert.True(hr >= 0, $"ProcessInput {Mf.Hr(hr)}");
            }

            _time += 333_333;
        }

        public void Dispose()
        {
            _port.Shutdown();
            if (_reader.IsAlive)
                Assert.True(_reader.Join(TimeSpan.FromSeconds(2)), "GetEvent ends with the shutdown");
            _port.Release();
        }
    }

    /// <summary>
    /// The real catalog, whose ports refuse both media types (<c>MF_E_INVALIDMEDIATYPE</c>) once their streaming has
    /// ended, which only a re-use's renegotiation does: every restart takes the fallback, a new activation. Every other
    /// call is the real port's.
    /// </summary>
    private sealed class RefusingCatalog(IMfCatalog real) : IMfCatalog
    {
        public int EnterThread(string role) => real.EnterThread(role);

        public void LeaveThread() => real.LeaveThread();

        public int Startup() => real.Startup();

        public int Enumerate(uint flags, out IReadOnlyList<IMfEntry> entries)
        {
            var hr = real.Enumerate(flags, out var found);
            entries = [.. (found ?? []).Select(e => (IMfEntry)new Entry(e))];
            return hr;
        }

        private sealed class Entry(IMfEntry real) : IMfEntry
        {
            public string? GetString(Guid key) => real.GetString(key);

            public Guid? GetGuid(Guid key) => real.GetGuid(key);

            public ulong? GetUInt64(Guid key) => real.GetUInt64(key);

            public IMfEncoderPort CreatePort() => new Port(real.CreatePort());

            public void Dispose() => real.Dispose();
        }

        private sealed class Port(IMfEncoderPort real) : IMfEncoderPort
        {
            private volatile bool _ended;

            public int Activate() => real.Activate();

            public int GetUInt32(Guid key, out uint value) => real.GetUInt32(key, out value);

            public int GetUInt64(Guid key, out ulong value) => real.GetUInt64(key, out value);

            public int GetString(Guid key, out string? value) => real.GetString(key, out value);

            public int SetUInt32(Guid key, uint value) => real.SetUInt32(key, value);

            public int SetCodecValue(Guid api, uint value) => real.SetCodecValue(api, value);

            public int GetCodecValue(Guid api, out uint value) => real.GetCodecValue(api, out value);

            public int SetOutputType(MfVideoType type) => _ended ? Mf.InvalidMediaType : real.SetOutputType(type);

            public int SetInputType(MfVideoType type) => _ended ? Mf.InvalidMediaType : real.SetInputType(type);

            public int GetOutputCurrentType(out MfVideoType? type) => real.GetOutputCurrentType(out type);

            public int GetOutputAvailableType(int index, out MfVideoType? type) => real.GetOutputAvailableType(index, out type);

            public int GetInputCurrentLayout(out MfInputLayout layout) => real.GetInputCurrentLayout(out layout);

            public int GetInputStreamInfo(out MfStreamInfo info) => real.GetInputStreamInfo(out info);

            public int GetOutputStreamInfo(out MfStreamInfo info) => real.GetOutputStreamInfo(out info);

            public int ProcessMessage(uint message)
            {
                if (message == Mf.MessageNotifyEndStreaming)
                    _ended = true;
                return real.ProcessMessage(message);
            }

            public int NextEvent(out MfEvent e) => real.NextEvent(out e);

            public int CreateInputBuffer(int size, int alignment, out IMfInputBuffer? buffer) => real.CreateInputBuffer(size, alignment, out buffer);

            public int ProcessInput(IMfInputBuffer buffer, long sampleTime, long duration, bool discontinuity) =>
                real.ProcessInput(buffer, sampleTime, duration, discontinuity);

            public int ProcessOutput(MfOutputAllocation allocation, out MfOutput? output) => real.ProcessOutput(allocation, out output);

            public int Shutdown() => real.Shutdown();

            public void Release() => real.Release();
        }
    }

    /// <summary>The real objects, each with one more reference the test holds, whose counts the test reads.</summary>
    private sealed unsafe class CountingObjects : IMfObjects
    {
        public readonly List<(string What, nint Pointer)> Made = [];

        private int Keep(string what, int hr, nint p)
        {
            if (hr >= 0 && p != 0)
            {
                Marshal.AddRef(p);
                Made.Add((what, p));
            }

            return hr;
        }

        public int CreateMediaType(out nint type) => Keep("media type", MfPlatObjects.Instance.CreateMediaType(out type), type);

        public int CreateSample(out nint sample) => Keep("sample", MfPlatObjects.Instance.CreateSample(out sample), sample);

        public int CreateBuffer(int size, int alignment, out nint buffer) => Keep("buffer", MfPlatObjects.Instance.CreateBuffer(size, alignment, out buffer), buffer);

        public void FreeCoTaskMem(nint p) => MfPlatObjects.Instance.FreeCoTaskMem(p);

        public int ClearVariant(Variant* value) => MfPlatObjects.Instance.ClearVariant(value);

        public static int Count(nint p)
        {
            Marshal.AddRef(p);
            return Marshal.Release(p);
        }

        /// <summary>
        /// The test's references in the order that leaves each object at its own count: samples first (a sample holds its
        /// buffers), then media types, then buffers.
        /// </summary>
        public IEnumerable<(string What, nint Pointer)> InReleaseOrder() =>
            Made.OrderBy(m => m.What switch { "sample" => 0, "media type" => 1, _ => 2 });
    }

    [Fact]
    public void NativeReferences_AreBackToTheTestsOwn_OnceTheTransformLetsGo()
    {
        SkipWithoutMediaFoundation();
        var trusted = Backend().Trusted!;
        var objects = new CountingObjects();
        var released = new HashSet<nint>();
        var catalog = new MfComCatalog(objects);
        Assert.True(catalog.EnterThread("test") >= 0);
        try
        {
            Assert.True(catalog.Startup() >= 0);
            Assert.True(catalog.Enumerate(MediaFoundationH264.EnumFlags, out var entries) >= 0);
            var entry = entries.First(e => e.GetString(Mf.HardwareUrl) == trusted.HardwareUrl);
            var port = entry.CreatePort();
            foreach (var e in entries)
                e.Dispose();
            Assert.Equal(0, port.Activate());
            Assert.True(port.SetUInt32(Mf.TransformAsyncUnlock, 1) >= 0);
            const int w = 640, h = 480;
            Assert.True(port.SetOutputType(new MfVideoType(Mf.H264, w, h)
            {
                FrameRate = 30, Bitrate = 2_000_000, Profile = Mf.ProfileBaseline, InterlaceMode = Mf.InterlaceProgressive,
            }) >= 0, "the output type");
            var refused = port.SetInputType(new MfVideoType(Mf.H264, w, h) { FrameRate = 30 });
            Assert.True(refused < 0, "an H.264 input type is refused");
            // Refused: let go at once.
            Assert.Equal(1, CountingObjects.Count(objects.Made[^1].Pointer));
            Assert.True(port.SetInputType(new MfVideoType(Mf.Nv12, w, h) { FrameRate = 30, DefaultStride = w, InterlaceMode = Mf.InterlaceProgressive }) >= 0, "the input type");
            Assert.True(port.GetInputStreamInfo(out var input) >= 0);
            Assert.True(port.GetOutputStreamInfo(out var outputInfo) >= 0);
            Assert.True(port.ProcessMessage(Mf.MessageNotifyBeginStreaming) >= 0);
            Assert.True(port.ProcessMessage(Mf.MessageNotifyStartOfStream) >= 0);

            // One frame in on the first NeedInput, its output out on HaveOutput: events read on a thread of their own.
            var events = new System.Collections.Concurrent.BlockingCollection<MfEvent>();
            var reader = new Thread(() =>
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
            MfEvent Next(uint type)
            {
                while (events.TryTake(out var e, TimeSpan.FromSeconds(5)))
                {
                    if (e.Type == type)
                        return e;
                }

                throw new Xunit.Sdk.XunitException($"no event {type} within 5 s");
            }

            Next(Mf.EventNeedInput);
            Assert.True(port.CreateInputBuffer(Math.Max(w * h * 3 / 2, input.Size), Math.Max(input.Alignment, 1), out var buffer) >= 0);
            Assert.True(buffer!.Write(new byte[w * h * 3 / 2]) >= 0);
            Assert.True(port.ProcessInput(buffer, 0, 333_333, discontinuity: false) >= 0);
            buffer.Dispose();
            Next(Mf.EventHaveOutput);
            Assert.True(port.ProcessOutput(MfOutputAllocation.Of(outputInfo), out var output) >= 0);
            Assert.NotNull(output);

            // The teardown: the transform lets go of what it held, then every count is the test's own one. The test holds
            // every sample, and a sample holds its buffers: each sample is checked and let go first (releasing its
            // buffers), then the media types, then the buffers, each at the test's one reference.
            port.Shutdown();
            Assert.True(reader.Join(TimeSpan.FromSeconds(2)), "GetEvent ends with the shutdown");
            port.Release();
            Assert.Contains(objects.Made, m => m.What == "sample");
            Assert.Contains(objects.Made, m => m.What == "buffer");
            foreach (var (what, p) in objects.InReleaseOrder())
            {
                var count = CountingObjects.Count(p);
                Assert.True(count == 1, $"a {what} has {count} references after the teardown");
                released.Add(p);
                Marshal.Release(p);
            }
        }
        finally
        {
            // Whatever an assertion left: let go in the same order.
            foreach (var (_, p) in objects.InReleaseOrder())
            {
                if (released.Add(p))
                    Marshal.Release(p);
            }

            catalog.LeaveThread();
        }
    }

    // ---- 11: a long run ----

    [Fact]
    public void NineHundredFramesAtThirty_NoStall_AndTheWrappersStayFlat()
    {
        SkipWithoutMediaFoundation();
        var backend = Backend();
        const int w = 640, h = 384;
        var frames = Enumerable.Range(0, 8).Select(i => Noise(w, h, i)).ToArray();
        var live = MfWrappers.Live;
        var peak = live;
        var start = Stopwatch.StartNew();
        var outcomes = Encode(backend, w, h, [.. Enumerable.Range(0, 900).Select(i => frames[i % frames.Length])], i =>
        {
            peak = Math.Max(peak, MfWrappers.Live);
            return TimeSpan.FromSeconds(i / 30.0) - start.Elapsed;
        });
        Assert.Equal(900, outcomes.Count(o => o.Kind == H264OutcomeKind.Encoded));
        // A session holds its activation, its transform and its attributes; output samples are released per frame.
        Assert.InRange(peak - live, 0, 8);
    }

    // ---- 12: aborts ----

    [Fact]
    public void AnAbort_EndsItsTeardownInTime_WithFramesInFlight_AndIdle()
    {
        SkipWithoutMediaFoundation();
        var backend = Backend();
        const int w = 640, h = 384;
        var backends = new H264Backends([backend], NullLogger.Instance);

        var busy = backend.Start(new H264Config(w, h, 30, VideoStream.HardwareBitrate), _ => { });
        Assert.Equal(H264Submit.Accepted, busy.Submit(Solid(w, h, (1, 1, 1)), w, h, 0, true, out _));
        Assert.Equal(H264Submit.Accepted, busy.Submit(Solid(w, h, (2, 2, 2)), w, h, 1, false, out _));
        var watch = Stopwatch.StartNew();
        Assert.True(busy.Abort().Wait(Quarantine.Bound, Ct), "two frames in flight: the teardown ends within 2 s");
        Note($"in flight: {watch.ElapsedMilliseconds} ms");

        var arrived = new SemaphoreSlim(0);
        var idle = backend.Start(new H264Config(w, h, 30, VideoStream.HardwareBitrate), _ => arrived.Release());
        Assert.Equal(H264Submit.Accepted, idle.Submit(Solid(w, h, (3, 3, 3)), w, h, 0, true, out _));
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), Ct));
        watch.Restart();
        Assert.True(idle.Abort().Wait(TimeSpan.FromMilliseconds(250), Ct), "an idle session's teardown ends within 250 ms");
        Note($"idle: {watch.ElapsedMilliseconds} ms");

        // Waiting for a credit: a session that has had no NeedInput yet.
        var waiting = backend.Start(new H264Config(w, h, 30, VideoStream.HardwareBitrate), _ => { });
        watch.Restart();
        Assert.True(waiting.Abort().Wait(TimeSpan.FromMilliseconds(250), Ct), "a session waiting for a credit tears down within 250 ms");
        Note($"waiting for a credit: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(0, backends.Quarantines(backend));
    }

    // ---- 13, 14: the chain, and no ffmpeg on Windows ----

    [Fact]
    public void ThisComputersChain_IsMediaFoundationAlone()
    {
        SkipWithoutMediaFoundation();
        var discovery = FfmpegDiscovery.ForThisComputer(null);
        Assert.Equal(["Media Foundation"], H264Backends.ForThisComputer("auto", NullLogger.Instance, discovery).Chain.Select(b => b.Name));
        Assert.Equal(["Media Foundation"], H264Backends.ForThisComputer("hardware", NullLogger.Instance, discovery).Chain.Select(b => b.Name));
        Assert.Empty(H264Backends.ForThisComputer("software", NullLogger.Instance, discovery).Chain);
        Assert.Empty(H264Backends.ForThisComputer("off", NullLogger.Instance, discovery).Chain);
    }

    /// <summary>
    /// The built engine, as users run it, never starts ffmpeg on Windows: a fake <c>ffmpeg.exe</c> first on its
    /// <c>PATH</c> leaves a marker if anything starts it, at any moment of the run (a start too short for a process list
    /// to see included). <c>--ffmpeg</c> naming it: exit 2. <c>--h264 software</c>: an h264 subscription over the
    /// engine's socket gets PNG with <c>encoder.unavailable</c>. Then the marker must not exist.
    /// </summary>
    [Fact]
    public async Task TheFfmpegFlag_IsRefused_AndSoftwareMode_IsImages_WithNoFfmpegStarted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        var root = Directory.CreateTempSubdirectory("infopanel-mf-noffmpeg-").FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var marker = Path.Combine(root, "ffmpeg-ran.txt");
        var fake = await FakeFfmpeg(bin, marker);

        // The fake works: a start leaves the marker, so its absence at the end means nothing started it.
        using (var check = Process.Start(new ProcessStartInfo(fake, "-version") { UseShellExecute = false })!)
            await check.WaitForExitAsync(Ct);
        Assert.True(File.Exists(marker), "the fake ffmpeg.exe leaves its marker when started");
        File.Delete(marker);
        var path = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        // --ffmpeg naming it: exit 2, saying why.
        using (var engine = Process.Start(EngineWithPath(root, "refused", path, "--ffmpeg", fake))!)
        {
            var said = engine.StandardOutput.ReadToEndAsync(Ct);
            var errors = engine.StandardError.ReadToEndAsync(Ct);
            await engine.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(2, engine.ExitCode);
            Assert.Contains("--ffmpeg isn't used on Windows", await said + await errors, StringComparison.Ordinal);
        }

        // --h264 software: the flag through the engine's own wiring; its chain is empty on Windows, so an h264
        // subscription's frames are PNG with encoder.unavailable.
        var port = InfoPanel.Hosting.EngineRuntime.FindFreePort();
        var software = Process.Start(EngineWithPath(root, "software", path, "--port", port.ToString(), "--token", EngineFactory.Token, "--h264", "software"))!;
        var output = software.StandardOutput.ReadToEndAsync(Ct);
        var errorOutput = software.StandardError.ReadToEndAsync(Ct);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
            var watch = Stopwatch.StartNew();
            JsonElement? profiles = null;
            while (profiles == null && !software.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                try
                {
                    profiles = await http.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !Ct.IsCancellationRequested)
                {
                    await Task.Delay(100, Ct);
                }
            }

            Assert.True(profiles != null, "the engine answers within 30 s");
            var id = profiles.Value.EnumerateArray().First().GetProperty("id").GetString()!;
            var client = await PreviewClient.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), EngineFactory.Token, Ct);
            try
            {
                await client.ReceiveTextAsync("hello", Ct);
                await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.5, encoding = "h264" }, Ct);
                var (frame, _) = await client.ReceiveFrameAsync(Ct, null, TimeSpan.FromSeconds(15));
                Assert.Equal(("png", VideoStream.Unavailable), (frame.Encoding, frame.Fallback));
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            if (!software.HasExited)
                software.Kill(entireProcessTree: true);
            await software.WaitForExitAsync(Ct);
            software.Dispose();
        }

        Assert.Contains("Windows has no software H.264 encoder", await output + await errorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), $"ffmpeg was started: {(File.Exists(marker) ? File.ReadAllText(marker) : "")}");
    }

    /// <summary>The built engine on a data folder of its own under <paramref name="root"/>, its <c>PATH</c> <paramref name="path"/>.</summary>
    private static ProcessStartInfo EngineWithPath(string root, string name, string path, params string[] more)
    {
        var data = Path.Combine(root, name);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", data, "--v1-dir", data + "-v1", "--no-browser", "--no-windows", "--no-tray", "--mock" }.Concat(more))
            start.ArgumentList.Add(arg);
        start.Environment["PATH"] = path;
        return start;
    }

    /// <summary>
    /// A fake <c>ffmpeg.exe</c> in <paramref name="folder"/> that appends its arguments to <paramref name="marker"/> and
    /// exits 1, built with the C# compiler of the .NET Framework 4 that Windows 10 and 11 ship (no package, no SDK).
    /// </summary>
    private static async Task<string> FakeFfmpeg(string folder, string marker)
    {
        var csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.True(File.Exists(csc), $"no C# compiler at {csc} to build the fake ffmpeg.exe with");
        var source = Path.Combine(folder, "ffmpeg.cs");
        await File.WriteAllTextAsync(source,
            "class Ffmpeg { static int Main(string[] args) { System.IO.File.AppendAllText(@\"" + marker.Replace("\"", "\"\"") +
            "\", string.Join(\" \", args) + \"\\r\\n\"); return 1; } }", Ct);
        var exe = Path.Combine(folder, "ffmpeg.exe");
        var info = new ProcessStartInfo(csc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "/nologo", "/target:exe", "/out:" + exe, source })
            info.ArgumentList.Add(arg);
        using var compiler = Process.Start(info)!;
        var said = compiler.StandardOutput.ReadToEndAsync(Ct);
        var errors = compiler.StandardError.ReadToEndAsync(Ct);
        await compiler.WaitForExitAsync(Ct);
        Assert.True(compiler.ExitCode == 0 && File.Exists(exe), $"the fake ffmpeg.exe didn't build: {await said}{await errors}");
        File.Delete(source);
        return exe;
    }
}

/// <summary>Media Foundation's tests run alone: they count the process's threads, handles, bytes and live wrappers.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaFoundationCollection
{
    public const string Name = "Media Foundation";
}
