using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests;

public class CoreSmokeTests
{
    private static readonly SKColor Background = SKColors.White;

    /// <summary>
    /// Draws a text, a shape and a bar through the real renderer. Proves the Skia and HarfBuzz
    /// native libraries load on this platform and that each item kind puts pixels down.
    /// </summary>
    [Fact]
    public void RendersTextShapeBar()
    {
        var profile = new Profile("Smoke", 400, 200) { BackgroundColor = "#FFFFFFFF" };

        var text = new TextDisplayItem("Hello InfoPanel", profile) { X = 10, Y = 10, FontSize = 24, Color = "#FF000000" };
        var shape = new ShapeDisplayItem("Box", profile) { X = 250, Y = 10, Width = 80, Height = 80, ShowGradient = false, FillColor = "#FFFF0000" };
        var bar = new BarDisplayItem("Bar", profile)
        {
            X = 10,
            Y = 120,
            Width = 300,
            Height = 40,
            SensorType = SensorType.Plugin,
            PluginSensorId = "/test/cpu/load",
            Color = "#FF0000FF",
            Gradient = false,
            Background = false,
            Frame = false,
        };

        var sensors = new FakeSensorReader();
        sensors.Readings[SensorKey.Plugin("/test/cpu/load")] = new SensorReading(0, 100, 50, 50, "%");

        var ctx = TestContexts.Create(sensors);

        using var bitmap = new SKBitmap(400, 200);
        using (var g = SkiaGraphics.FromBitmap(bitmap, profile.FontScale))
        {
            PanelDraw.Run(profile, [text, shape, bar], g, ctx);
        }

        Assert.True(CountNonBackground(bitmap, new SKRectI(10, 10, 240, 60)) > 50, "text drew no pixels");
        Assert.True(CountNonBackground(bitmap, new SKRectI(250, 10, 330, 90)) > 1000, "shape drew no pixels");

        // 50% of a 300 px bar: the left half is filled, the right half is not.
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(80, 140));
        Assert.Equal(Background, bitmap.GetPixel(240, 140));
    }

    [Fact]
    public void MissingSensorDoesNotThrow()
    {
        var profile = new Profile("Missing", 400, 200);
        var sensor = new SensorDisplayItem("CPU", profile) { SensorType = SensorType.Plugin, PluginSensorId = "/nope" };
        var bar = new BarDisplayItem("Bar", profile) { SensorType = SensorType.HwInfo };
        var graph = new GraphDisplayItem("Graph", profile, GraphDisplayItem.GraphType.LINE);

        var ctx = TestContexts.Create();

        using var bitmap = new SKBitmap(400, 200);
        using (var g = SkiaGraphics.FromBitmap(bitmap, profile.FontScale))
        {
            PanelDraw.Run(profile, [sensor, bar, graph], g, ctx);
        }

        Assert.Equal("-", sensor.EvaluateText(ctx));
    }

    private static int CountNonBackground(SKBitmap bitmap, SKRectI rect)
    {
        var count = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                if (bitmap.GetPixel(x, y) != Background)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
