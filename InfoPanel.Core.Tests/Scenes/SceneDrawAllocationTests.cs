using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>What one draw of a snapshot makes in native objects: paints and clip paths.</summary>
public class SceneDrawAllocationTests
{
    [Fact]
    public void A_400_bar_series_draws_with_one_fill_and_one_stroke_paint_and_no_clip_path()
    {
        var inputs = new FakeSceneInputs();
        inputs.Number("test:v", 50);
        inputs.Histories["test:v"] = Enumerable.Range(0, 400).Select(i => (double)(i * 37 % 100)).ToArray();
        var scene = SceneKit.Json("""
            {'format': 'infopanel.scene/1', 'inputs': {'v': {'type': 'number'}},
             'nodes': [{'kind': 'series', 'input': 'v', 'mode': 'bars', 'x': 10, 'y': 10, 'w': 400, 'h': 80, 'pitch': 1, 'barWidth': 0.8,
                        'count': 400, 'min': 0, 'max': 100, 'fill': '#22c55e', 'stroke': {'paint': '#ffffff', 'width': 0.25}}]}
            """);
        using var snapshot = SceneRuntime.RenderOnce(SceneKit.Doc(scene, 420, 100), SceneKit.Now, SceneKit.Options(inputs));
        using var bitmap = new SKBitmap(new SKImageInfo(420, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        const int draws = 50;
        SceneDrawer.PaintsMade = 0;
        SceneDrawer.ClipPathsMade = 0;
        for (var i = 0; i < draws; i++)
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, 1);
        }
        var paints = SceneDrawer.PaintsMade;
        var clips = SceneDrawer.ClipPathsMade;

        // The bars are drawn: a bar's fill shows in the series' box.
        Assert.NotEqual(SKColors.Transparent, bitmap.GetPixel(409, 89));
        Assert.True(paints <= 2 * draws && clips == 0, $"{paints} paints ({paints / draws} a draw) and {clips} clip paths in {draws} draws");
    }
}
