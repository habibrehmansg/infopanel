using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Images;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The scene runtime's goldens (<c>Golden/scene/{os}/</c>, macOS the reference): every valid example scene of
/// spec/examples, and fixture scenes (<c>Scenes/Fixtures/</c>) that cover each node, paint, stroke option, transform,
/// clip, blend mode and effect on their own. Rendered as one-off snapshots with <see cref="SceneKit"/>'s fixed inputs.
/// Regenerate with <c>UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class SceneGoldenTests
{
    private sealed record Case(string Scene, int Width, int Height, double X, double Y, double? W, double? H, Action<FakeSceneInputs>? Inputs = null,
        float Scale = 1, double Elapsed = 0, bool Provider = false, double Rotation = 0);

    private static void Load(FakeSceneInputs i)
    {
        i.Number("test:v", 42.5, "%", 3, 97, 38);
        i.Number("test:load", 42.5, "%");
        i.Text("test:label", "idle");
        i.Text("test:song", "Now playing: a song title that is much too long for its box", SceneInputStatus.Stale);
        i.Text("test:art", "album-1");
        var wave = Enumerable.Range(0, 300).Select(k => 50 + 40 * Math.Sin(k / 6.0) + 8 * Math.Sin(k / 1.7)).ToArray();
        i.Histories["test:v"] = wave;
        i.Histories["test:load"] = wave;
        i.Readings["test:t"] = i.Readings["test:procs"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Text = "4 processes",
            Table = new SceneTable(["Name", "CPU", "Memory"],
            [
                ["WindowServer", "12.5", "512 MB"],
                ["a process with a very long name indeed", "8.25", "1.2 GB"],
                ["kernel_task", "3.0", "2 GB"],
                ["Finder", "0.4", "96 MB"],
            ]),
        };
    }

    private static readonly Dictionary<string, Case> Examples = new()
    {
        ["bar"] = new("scene/valid/bar.json", 260, 40, 10, 10, 240, 20, Load),
        ["clock"] = new("scene/valid/clock.json", 200, 230, 0, 0, null, null),
        ["donut"] = new("scene/valid/donut.json", 140, 140, 10, 10, null, null, i => i.Number("test:v", 62)),
        ["effects"] = new("scene/valid/effects.json", 220, 140, 0, 0, null, null),
        ["expressions"] = new("scene/valid/expressions.json", 560, 60, 4, 4, 300, 48, Load),
        ["gauge-sequence"] = new("scene/valid/gauge-sequence.json", 148, 148, 10, 10, null, null, i => i.Number("test:v", 60)),
        ["gradient-stops"] = new("scene/valid/gradient-stops.json", 220, 108, 10, 10, null, null),
        ["graph"] = new("scene/valid/graph.json", 260, 100, 10, 10, null, null, Load),
        ["histogram"] = new("scene/valid/histogram.json", 260, 100, 10, 10, 240, 80, Load),
        ["image"] = new("scene/valid/image.json", 220, 140, 10, 10, 200, 120),
        ["image-t1"] = new("scene/valid/image.json", 220, 140, 10, 10, 200, 120, Elapsed: 0.35),
        ["peak-hold"] = new("scene/valid/peak-hold.json", 44, 180, 10, 10, null, null, i => i.Number("test:v", 70)),
        ["provider-media"] = new("scene/valid/provider-media.json", 116, 116, 10, 10, null, null, Load, Provider: true),
        ["provider-media-missing"] = new("scene/valid/provider-media.json", 116, 116, 10, 10, null, null, Load),
        ["sensor-text"] = new("scene/valid/sensor-text.json", 200, 60, 10, 6, null, null, i => i.Number("test:v", 71.25, "°C")),
        ["sensor-text-unavailable"] = new("scene/valid/sensor-text.json", 200, 60, 10, 6, null, null),
        ["shapes"] = new("scene/valid/shapes.json", 220, 180, 5, 5, null, null),
        ["shapes-t1"] = new("scene/valid/shapes.json", 220, 180, 5, 5, null, null, Elapsed: 1.25),
        ["table"] = new("scene/valid/table.json", 340, 120, 0, 0, null, null, Load),
        ["text-layout"] = new("scene/valid/text-layout.json", 220, 180, 10, 10, null, null, Load),
        ["text-layout-t2"] = new("scene/valid/text-layout.json", 220, 180, 10, 10, null, null, Load, Elapsed: 2),
        ["shapes-scale2"] = new("scene/valid/shapes.json", 220, 180, 5, 5, null, null, Scale: 2),
        ["sensor-text-rotated"] = new("scene/valid/sensor-text.json", 200, 120, 40, 40, null, null, i => i.Number("test:v", 90, "°C"), Rotation: 20),
    };

    private static readonly Dictionary<string, Case> Fixtures = new()
    {
        ["fx-strokes"] = new("strokes.json", 320, 200, 0, 0, null, null),
        ["fx-paints"] = new("paints.json", 400, 220, 0, 0, null, null),
        ["fx-blends"] = new("blends.json", 360, 120, 0, 0, null, null),
        ["fx-transforms"] = new("transforms.json", 320, 120, 0, 0, null, null),
        ["fx-clips-effects"] = new("clips-effects.json", 360, 140, 0, 0, null, null),
        ["fx-text"] = new("text.json", 360, 260, 0, 0, null, null),
        ["fx-media"] = new("media.json", 420, 130, 0, 0, null, null),
        ["fx-series"] = new("series.json", 330, 100, 0, 0, null, null, Load),
        ["fx-shapes"] = new("shapes.json", 360, 120, 0, 0, null, null),
        ["fx-dash-floor"] = new("dash-floor.json", 320, 80, 0, 0, null, null),
    };

    public static TheoryData<string> ExampleNames() => new(Examples.Keys.Order(StringComparer.Ordinal));
    public static TheoryData<string> FixtureNames() => new(Fixtures.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void Example(string name) => Verify(name, Examples[name], SceneKit.Example(Examples[name].Scene));

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture(string name) => Verify(name, Fixtures[name], SceneKit.Fixture(Fixtures[name].Scene));

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixtures_are_valid_scenes(string name)
    {
        var check = InfoPanel.Spec.SceneValidator.Check(SceneKit.Fixture(Fixtures[name].Scene));
        Assert.True(check.Status == InfoPanel.Spec.CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
    }

    private static void Verify(string name, Case c, JsonElement scene)
    {
        var inputs = new FakeSceneInputs();
        c.Inputs?.Invoke(inputs);
        using var media = SceneKit.SyncMedia();
        using var logo = LoadImage("logo.png");
        var providers = c.Provider ? new Dictionary<string, IMediaProvider> { ["now-playing"] = new FixedProvider(logo!) } : null;
        var doc = SceneKit.Doc(scene, c.Width, c.Height, c.X, c.Y, c.W, c.H, rotation: c.Rotation);
        using var runtime = new SceneRuntime(SceneKit.Options(inputs, media, providers));
        runtime.Load(doc);
        TickSnapshot snapshot;
        if (c.Elapsed > 0)
        {
            // Two ticks: the first starts every cell, the second is at the case's elapsed time.
            runtime.Tick(0, SceneKit.Now).Dispose();
            snapshot = runtime.Tick(c.Elapsed, SceneKit.Now);
        }
        else
        {
            snapshot = runtime.Snapshot(SceneKit.Now);
        }
        using (snapshot)
        {
            using var bitmap = snapshot.Render(c.Scale);
            GoldenImage.Verify(name, bitmap, "scene");
        }
    }

    internal static SKImage? LoadImage(string file)
    {
        var path = Path.Combine(SceneKit.ExamplesDir, "media", "valid", file);
        using var data = SKData.Create(path);
        return SKImage.FromEncodedData(data);
    }
}
