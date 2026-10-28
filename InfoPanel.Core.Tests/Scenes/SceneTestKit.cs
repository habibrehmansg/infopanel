using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Spec;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>Scene inputs from dictionaries; every read is counted, so tests can check that a tick reads each key once.</summary>
internal sealed class FakeSceneInputs : ISceneInputSource
{
    public Dictionary<string, SceneReading> Readings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double[]> Histories { get; } = new(StringComparer.Ordinal);
    public int Reads;
    public int HistoryReads;

    public void Number(string key, double now, string unit = "", double? min = null, double? max = null, double? avg = null) =>
        Readings[key] = new SceneReading { Status = SceneInputStatus.Live, Now = now, Min = min ?? now, Max = max ?? now, Avg = avg ?? now, Unit = unit };

    public void Text(string key, string text, SceneInputStatus status = SceneInputStatus.Live) =>
        Readings[key] = new SceneReading { Status = status, Text = text };

    public SceneReading Read(string key, string inputType)
    {
        Interlocked.Increment(ref Reads);
        return Readings.TryGetValue(key, out var r) ? r : SceneReading.Unavailable;
    }

    public IReadOnlyList<double> History(string key)
    {
        Interlocked.Increment(ref HistoryReads);
        return Histories.TryGetValue(key, out var h) ? h : [];
    }
}

/// <summary>Assets by content hash from folders of files (the spec's example media by default).</summary>
internal sealed class FolderAssets : IMediaAssetStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    public Func<string, byte[]?>? Override;

    public FolderAssets(params string[] folders)
    {
        foreach (var folder in folders)
            foreach (var file in Directory.EnumerateFiles(folder))
                _files.TryAdd("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), file);
    }

    public static FolderAssets Examples() => new(Path.Combine(SceneKit.ExamplesDir, "media", "valid"));

    public string Hash(string fileName) => _files.First(p => Path.GetFileName(p.Value) == fileName).Key;

    public byte[]? Read(string hash)
    {
        if (Override?.Invoke(hash) is { } bytes) return bytes;
        return _files.TryGetValue(hash, out var file) ? File.ReadAllBytes(file) : null;
    }
}

/// <summary>A provider that serves one image for every reference.</summary>
internal sealed class FixedProvider(SKImage image) : IMediaProvider
{
    private readonly MediaSample _sample = MediaSample.FromImage(image);
    public int Calls;

    public MediaSample? TryGet(string reference, double seconds)
    {
        Interlocked.Increment(ref Calls);
        return _sample.Retain();
    }
}

/// <summary>Fixed inputs for scene tests: 2026-01-02 03:04:05 UTC in a UTC zone, invariant culture, the example media.</summary>
internal static class SceneKit
{
    public static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static string ExamplesDir { get; } = Path.Combine(SpecExampleTests.SpecDir, "examples");

    public static string FixturesDir { get; } = Path.Combine(Golden.TestPaths.ProjectDirectory, "Scenes", "Fixtures");

    public static JsonElement Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    public static JsonElement Example(string rel) => Load(Path.Combine(ExamplesDir, rel));

    public static JsonElement Fixture(string name) => Load(Path.Combine(FixturesDir, name));

    /// <summary>Runtime options with the zone (UTC) and the PC's regional format (<paramref name="locale"/>, else the invariant one) pinned.</summary>
    public static SceneRuntimeOptions Options(ISceneInputSource? inputs = null, MediaSampleCache? media = null,
        IReadOnlyDictionary<string, IMediaProvider>? providers = null, SpecLimits? limits = null, RegionalFormat? locale = null,
        Serilog.ILogger? logger = null) => new()
    {
        Inputs = inputs ?? new FakeSceneInputs(),
        Media = media,
        Providers = providers ?? new Dictionary<string, IMediaProvider>(),
        LocalZone = TimeZoneInfo.Utc,
        Locale = locale ?? RegionalFormat.Invariant,
        Logger = logger,
        Limits = limits ?? SpecLimits.Default,
    };

    public static MediaSampleCache SyncMedia(IMediaAssetStore? store = null) =>
        new(store ?? FolderAssets.Examples(), new MediaSampleCacheOptions { Synchronous = true });

    /// <summary>A document from a JSON literal (single quotes allowed for readability).</summary>
    public static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json.Replace('\'', '"'), SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    /// <summary>A document with one scene item from a scene JSON, bound to keys named as the inputs.</summary>
    public static JsonElement Doc(JsonElement scene, int width, int height, double x = 0, double y = 0, double? w = null, double? h = null,
        string background = "#15191fff", IReadOnlyDictionary<string, string>? bindings = null, double rotation = 0)
    {
        if (bindings is null && scene.TryGetProperty("inputs", out var inputs))
            bindings = inputs.EnumerateObject().ToDictionary(p => p.Name, p => "test:" + p.Name);
        return SceneRender.WrapScene(scene, width, height, x, y, w, h, bindings, background, rotation);
    }

    public static SKBitmap Render(JsonElement document, SceneRuntimeOptions options, float scale = 1)
    {
        using var snapshot = SceneRuntime.RenderOnce(document, Now, options);
        return snapshot.Render(scale);
    }

    public static JsonObject Obj(string json) => JsonNode.Parse(json.Replace('\'', '"'))!.AsObject();

    public static int DifferingPixels(SKBitmap a, SKBitmap b, int tolerance = 2)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var n = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance
                    || Math.Abs(p.Alpha - q.Alpha) > tolerance) n++;
            }
        return n;
    }
}

/// <summary>
/// Counts the lines RichTextKit produces on this thread while an action runs (the layout's test seam: every line of every
/// paragraph laid out, fit probes included, kept or not). Past the trap it throws inside the layout, so a layout that
/// isn't bounded fails fast (the item is then drawn as invalid) instead of running on.
/// </summary>
internal static class LinesLaidOut
{
    public static int While(Action run, int trap = 20_000)
    {
        var lines = 0;
        var previous = TextLayoutEngine.LinesProduced;
        TextLayoutEngine.LinesProduced = n =>
        {
            if ((lines += n) > trap) throw new InvalidOperationException($"more than {trap} lines laid out");
        };
        try
        {
            run();
        }
        finally
        {
            TextLayoutEngine.LinesProduced = previous;
        }
        return lines;
    }
}
