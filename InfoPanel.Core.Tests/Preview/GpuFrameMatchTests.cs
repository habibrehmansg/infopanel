using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Images;
using InfoPanel.Output;
using InfoPanel.Output.Gpu;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// The engine's fixtures drawn on the CPU and on this computer's GPU agree within a bound, and a thumbnail at the
/// profile's width equals the live frame on the GPU. Needs a real GPU: Linux skips (<c>Linux: no GPU backend</c>);
/// elsewhere a computer without one skips with <c>GPU: …</c>, unless <c>INFOPANEL_REQUIRE_GPU=1</c>, which makes that a
/// failure (so a run on a computer that has one can't pass by skipping). Each comparison's numbers are written to the
/// test output.
/// </summary>
/// <remarks>
/// The bound: alpha differs by at most <see cref="AlphaDelta"/>, and at most each input's share of the pixels differs
/// by more than 4 in any channel. The GPU and CPU rasterisers differ at anti-aliased edges, in gradients and in blur.
/// Set from the runs on an Apple M4 Max (Metal), every input live: all-kinds 0.92 % at scale 1 and 0.30 % at scale 2,
/// wide-bar 0.27 %, clips-effects (clips with round corners, blur and shadow on a small canvas) 2.98 %; alpha never
/// differed. The same GPU draws the same pixels each run, so the bound is exact here; another GPU re-measures it.
/// </remarks>
[Collection(GoldenCollection.Name)]
public sealed class GpuFrameMatchTests(ITestOutputHelper output)
{
    private const int AlphaDelta = 1;

    /// <summary>Each input, its scale and the most of its pixels that may differ by more than 4.</summary>
    public static TheoryData<string, float, double> Inputs => new()
    {
        { "all-kinds", 1, 0.01 },
        { "wide-bar", 1, 0.01 },
        { "clips-effects", 1, 0.04 },
        { "all-kinds", 2, 0.01 },
    };

    /// <summary>
    /// The diagnostics each fixture is expected to report (by code), and the only ones it may: a new one, or a placeholder
    /// (an item's <c>problem</c>), fails rather than being drawn the same on both.
    /// </summary>
    private static readonly Dictionary<string, (string Item, string Code)[]> ExpectedDiagnostics = new()
    {
        // Consolas where this computer lacks it, the reference's missing asset, the http and rtsp providers not configured.
        ["all-kinds"] =
        [
            ("v1-010", "font.missing"), ("v1-013", "font.missing"), ("v1-015", "media.missing"), ("v1-017", "media.provider"),
            ("v1-018", "media.provider"), ("v1-019", "media.provider"), ("v1-023", "media.missing"),
        ],
        ["wide-bar"] = [],
        ["clips-effects"] = [],
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void TheGpusFrame_AgreesWithTheCpus_WithinTheBound(string name, float scale, double bound)
    {
        using var gpu = Gpu();
        using var media = new MediaSampleCache(Assets(), new MediaSampleCacheOptions { Synchronous = true });
        // Every input live (numbers, histories, the process table), as the parity goldens draw all-kinds.
        var inputs = new ReadInputs(new EveryKeyInputs());
        using var runtime = new SceneRuntime(SceneKit.Options(inputs, media));
        runtime.Load(Document(name));
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        foreach (var d in snapshot.Diagnostics)
            output.WriteLine($"{name}: diagnostic {d.ItemId} {d.Code}: {d.Message}");
        Assert.All(inputs.NotLive, read => Assert.Fail($"{name}: input {read} isn't live"));
        Assert.All(snapshot.Bounds, b => Assert.True(b.Problem == null, $"{name}: {b.Id} is a placeholder: {b.Problem}"));
        Assert.All(snapshot.Diagnostics, d => Assert.True(ExpectedDiagnostics[name].Contains((d.ItemId, d.Code)), $"{name}: unexpected diagnostic {d.ItemId} {d.Code}: {d.Message}"));
        var (width, height) = FrameScale.PixelSize(scale, snapshot.Width, snapshot.Height);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        void Draw(SKCanvas canvas)
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, scale);
        }

        using var cpu = new SKBitmap(info);
        GpuRendering.DrawOnCpu(cpu, Draw);
        using var onGpu = new SKBitmap(info);
        var drewWith = gpu.Draw(onGpu, Draw);

        // This frame came from the GPU (not a refused size or a fallback drawn on the CPU, which the status wouldn't say).
        Assert.Equal(gpu.Status.Using, drewWith);
        Assert.NotEqual(GpuRendering.CpuBackend, drewWith);
        var (max, over1, over4) = Compare(cpu, onGpu);
        output.WriteLine($"{name} {width}x{height} (scale {scale}) on {GpuRendering.Label(gpu.Status.Using)} · {gpu.Status.Device}: " +
            $"max delta R{max[0]} G{max[1]} B{max[2]} A{max[3]}; pixels over 1: {over1:P3}; over 4: {over4:P3}");
        Assert.True(max[3] <= AlphaDelta, $"{name}: alpha differs by {max[3]}, over {AlphaDelta}");
        Assert.True(over4 <= bound, $"{name}: {over4:P3} of the pixels differ by more than 4, over {bound:P0}");
    }

    [Fact]
    public async Task ThePreviewsFrameAndTheThumbnailAtTheProfilesWidth_AreEqual_OnTheGpu()
    {
        Gpu().Dispose();
        var scene = JsonNode.Parse(SceneKit.Fixture("clips-effects.json").GetRawText())!.AsObject();
        var document = RenderHarness.Doc(400, 160, DocKit.Scene("fx", 20, 10, scene));
        await using var h = new RenderHarness(document);
        var drew = new List<string>();
        h.Service.Gpu.Drew += drew.Add;
        var backend = h.Service.Gpu.Apply(true).Using;
        Assert.NotEqual(GpuRendering.CpuBackend, backend);
        var sink = h.Attach();

        h.Tick();
        using var thumbnail = SKBitmap.Decode(h.Service.RenderOnce(h.Document, 400), new SKImageInfo(400, 160, SKColorType.Rgba8888, SKAlphaType.Premul));

        // Both drawn on the GPU, each draw by the backend that ran it: the loop's frame, then the thumbnail.
        Assert.Equal([backend, backend], drew);
        Assert.Equal(sink.Frames[^1].Pixels, thumbnail.GetPixelSpan().ToArray());
    }

    /// <summary>This computer's GPU, on; skipped (or failed, with <c>INFOPANEL_REQUIRE_GPU=1</c>) when there is none.</summary>
    private static GpuRendering Gpu()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux: no GPU backend");
        var gpu = new GpuRendering();
        var status = gpu.Apply(true);
        if (status.Fallback is { } why)
        {
            gpu.Dispose();
            var reason = $"GPU: {why.Code}: {why.Message}";
            Assert.False(Environment.GetEnvironmentVariable("INFOPANEL_REQUIRE_GPU") == "1", reason + " (INFOPANEL_REQUIRE_GPU=1)");
            Assert.Skip(reason);
        }

        return gpu;
    }

    private static FolderAssets Assets()
    {
        var core = TestPaths.ProjectDirectory;
        var examples = Path.Combine(Path.GetDirectoryName(core)!, "InfoPanel.Core", "Documents", "Examples");
        return new FolderAssets(Path.Combine(examples, "blobs"), Path.Combine(core, "Fixtures", "assets"), Path.Combine(SceneKit.ExamplesDir, "media", "valid"));
    }

    private static JsonElement Document(string name)
    {
        var core = TestPaths.ProjectDirectory;
        var examples = Path.Combine(Path.GetDirectoryName(core)!, "InfoPanel.Core", "Documents", "Examples");
        return name switch
        {
            "all-kinds" => SceneKit.Load(Path.Combine(core, "Scenes", "Reference", "v1", "all-kinds.document.json")),
            "wide-bar" => SceneKit.Load(Path.Combine(examples, "wide-bar.document.json")),
            "clips-effects" => SceneKit.Doc(SceneKit.Fixture("clips-effects.json"), 360, 140),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    /// <summary>
    /// Inputs read through <paramref name="inner"/>, keeping each number or table read that wasn't live and each empty
    /// history (text inputs read as unavailable in <see cref="EveryKeyInputs"/>, as a numeric sensor's do).
    /// </summary>
    private sealed class ReadInputs(ISceneInputSource inner) : ISceneInputSource
    {
        private readonly List<string> _notLive = [];

        public IReadOnlyList<string> NotLive
        {
            get { lock (_notLive) return [.. _notLive]; }
        }

        public SceneReading Read(string key, string inputType)
        {
            var reading = inner.Read(key, inputType);
            if (inputType is "number" or "table" && reading.Status != SceneInputStatus.Live)
                lock (_notLive) _notLive.Add($"{inputType} {key}");
            return reading;
        }

        public IReadOnlyList<double> History(string key)
        {
            var history = inner.History(key);
            if (history.Count == 0)
                lock (_notLive) _notLive.Add($"history {key}");
            return history;
        }
    }

    /// <summary>The largest difference in each channel, and the shares of pixels differing by more than 1 and by more than 4 in any channel.</summary>
    private static (int[] Max, double Over1, double Over4) Compare(SKBitmap a, SKBitmap b)
    {
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        var max = new int[4];
        long over1 = 0, over4 = 0;
        var pixels = x.Length / 4;
        for (var i = 0; i < pixels; i++)
        {
            var most = 0;
            for (var c = 0; c < 4; c++)
            {
                var d = Math.Abs(x[i * 4 + c] - y[i * 4 + c]);
                max[c] = Math.Max(max[c], d);
                most = Math.Max(most, d);
            }

            if (most > 1) over1++;
            if (most > 4) over4++;
        }

        return (max, (double)over1 / pixels, (double)over4 / pixels);
    }
}
