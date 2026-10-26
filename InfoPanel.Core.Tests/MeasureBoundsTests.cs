using System.Data;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests;

public class MeasureBoundsTests
{
    private static readonly Profile Profile = new("Bounds", 400, 300) { BackgroundColor = "#FF000000" };

    private static IReadOnlyList<ItemBounds> Measure(IReadOnlyList<DisplayItem> items, FakeSensorReader? sensors = null)
    {
        var renderer = new ProfileRenderer(sensors ?? new FakeSensorReader(), new FakeSensorHistory(), new FakeImageSource(),
            new FakeAssetLocator(Path.GetTempPath()), new FixedTimeProvider(GoldenScene.UtcNow));
        return renderer.MeasureBounds(Profile, items, new ProfileRenderState());
    }

    private static ShapeDisplayItem Shape(int x, int y, int width, int height, int rotation = 0) =>
        new("Shape", Profile) { X = x, Y = y, Width = width, Height = height, Rotation = rotation };

    [Fact]
    public void Shape_IsItsRect_WithRotationAboutTheCentre()
    {
        var shape = Shape(10, 20, 100, 50, rotation: 30);

        var bounds = Assert.Single(Measure([shape]));

        Assert.Equal(new ItemBounds(shape.Guid, 10, 20, 100, 50, 30, false), bounds);
    }

    [Fact]
    public void OddSize_RotatesAboutTheIntegerCentreDrawingUses()
    {
        // Drawing rotates a 101 × 51 rect about (10 + 50, 20 + 25), not its true centre (60.5, 45.5).
        var shape = Shape(10, 20, 101, 51, rotation: 90);

        var bounds = Assert.Single(Measure([shape]));

        var drawn = Corners(SKRect.Create(10, 20, 101, 51), SKMatrix.CreateRotationDegrees(90, 60, 45));
        var reported = SKRect.Create(bounds.X, bounds.Y, bounds.W, bounds.H);
        var measured = Corners(reported, SKMatrix.CreateRotationDegrees(bounds.Rotation, reported.MidX, reported.MidY));

        Assert.Equal((101f, 51f, 90f), (bounds.W, bounds.H, bounds.Rotation));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(drawn[i].X, measured[i].X, 3);
            Assert.Equal(drawn[i].Y, measured[i].Y, 3);
        }
    }

    [Fact]
    public void Group_IsUnionOfRotatedChildren_BeforeThem()
    {
        var rotated = Shape(0, 0, 100, 20, rotation: 90);   // occupies x 40..60, y -40..60
        var square = Shape(200, 200, 50, 50);
        var group = new GroupDisplayItem { Name = "Group" };
        group.DisplayItems.Add(rotated);
        group.DisplayItems.Add(square);

        var bounds = Measure([group]);

        Assert.Equal([group.Guid, rotated.Guid, square.Guid], bounds.Select(b => b.Id));
        var g = bounds[0];
        Assert.True(g.IsGroup);
        Assert.Equal(0, g.Rotation);
        Assert.Equal(40, g.X, 3);
        Assert.Equal(-40, g.Y, 3);
        Assert.Equal(250 - 40, g.W, 3);
        Assert.Equal(250 + 40, g.H, 3);
    }

    [Fact]
    public void HiddenItems_AndEverythingInHiddenGroups_AreLeftOut()
    {
        var visible = Shape(0, 0, 10, 10);
        var hidden = Shape(0, 0, 10, 10);
        hidden.Hidden = true;

        var hiddenGroup = new GroupDisplayItem { Name = "Hidden", Hidden = true };
        hiddenGroup.DisplayItems.Add(Shape(0, 0, 10, 10));

        var onlyHiddenChildren = new GroupDisplayItem { Name = "Empty" };
        var hiddenChild = Shape(300, 300, 10, 10);
        hiddenChild.Hidden = true;
        onlyHiddenChildren.DisplayItems.Add(hiddenChild);

        var partlyHidden = new GroupDisplayItem { Name = "Partly" };
        var shown = Shape(100, 100, 20, 20);
        partlyHidden.DisplayItems.Add(shown);
        partlyHidden.DisplayItems.Add(hiddenChild);

        var bounds = Measure([visible, hidden, hiddenGroup, onlyHiddenChildren, partlyHidden]);

        Assert.Equal([visible.Guid, partlyHidden.Guid, shown.Guid], bounds.Select(b => b.Id));
        Assert.Equal((100f, 100f, 20f, 20f), (bounds[1].X, bounds[1].Y, bounds[1].W, bounds[1].H));
    }

    [Fact]
    public void ImagesWithoutAnImage_UseThePlaceholderSize()
    {
        var unsized = new ImageDisplayItem("Missing", Profile, "missing.png", relativePath: true) { X = 5, Y = 6 };
        var halfSized = new ImageDisplayItem("Missing", Profile, "missing.png", relativePath: true) { X = 5, Y = 6, Width = 50, Rotation = 45 };
        var http = new HttpImageDisplayItem("Camera", Profile) { X = 1, Y = 2 };
        var gauge = new GaugeDisplayItem("Gauge", Profile) { X = 7, Y = 8 };

        var bounds = Measure([unsized, halfSized, http, gauge]);

        Assert.Equal(new ItemBounds(unsized.Guid, 5, 6, ImagePlaceholder.DefaultSize, ImagePlaceholder.DefaultSize, 0, false), bounds[0]);
        Assert.Equal(new ItemBounds(halfSized.Guid, 5, 6, 50, ImagePlaceholder.DefaultSize, 45, false), bounds[1]);
        Assert.Equal((100f, 100f), (bounds[2].W, bounds[2].H));
        Assert.Equal((7f, 8f, 100f, 100f), (bounds[3].X, bounds[3].Y, bounds[3].W, bounds[3].H));
    }

    [Fact]
    public void SensorImageOutsideItsThresholds_IsStillMeasured()
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[SensorKey.Plugin("/test/fan")] = new SensorReading(0, 0, 0, 5000, "RPM");
        var item = new SensorImageDisplayItem("Fan", Profile) { X = 3, Y = 4, SensorType = SensorType.Plugin, PluginSensorId = "/test/fan", Threshold1 = 0, Threshold2 = 100 };

        Assert.False(item.ShouldShow(sensors));
        Assert.Equal(new ItemBounds(item.Guid, 3, 4, 100, 100, 0, false), Assert.Single(Measure([item], sensors)));
    }

    [Fact]
    public void Donut_IsItsSquare_RotatedByItsRotation()
    {
        var donut = new DonutDisplayItem("Donut", Profile) { X = 20, Y = 30, Radius = 50 };
        Assert.Equal(90, donut.Rotation);   // the default turns the whole bitmap

        Assert.Equal(new ItemBounds(donut.Guid, 20, 30, 100, 100, 90, false), Assert.Single(Measure([donut])));
    }

    [Fact]
    public void Charts_AreTheirWidthAndHeight()
    {
        var bar = new BarDisplayItem("Bar", Profile) { X = 1, Y = 2, Width = 300, Height = 40, Rotation = -15 };

        Assert.Equal(new ItemBounds(bar.Guid, 1, 2, 300, 40, -15, false), Assert.Single(Measure([bar])));
    }

    [Fact]
    public void RightAlignedAutoSizedText_ExtendsLeftOfX()
    {
        var left = new TextDisplayItem("Some text", Profile) { X = 200, Y = 10 };
        var right = new TextDisplayItem("Some text", Profile) { X = 200, Y = 10, RightAlign = true };
        var boxed = new TextDisplayItem("Some text", Profile) { X = 200, Y = 10, Width = 150, RightAlign = true };

        var bounds = Measure([left, right, boxed]);

        Assert.True(bounds[0].W > 50);
        Assert.Equal(200, bounds[0].X);
        Assert.Equal(200 - bounds[0].W, bounds[1].X, 3);
        Assert.Equal(bounds[0].W, bounds[1].W, 3);
        Assert.Equal((200f, 150f), (bounds[2].X, bounds[2].W));
    }

    [Fact]
    public void GlowingText_GrowsByThreeTimesTheGlowRadius()
    {
        // The radius is the blur's sigma; Skia's blur reaches 3 sigma.
        var plain = new TextDisplayItem("Glow", Profile) { X = 50, Y = 50 };
        var glowing = new TextDisplayItem("Glow", Profile) { X = 50, Y = 50, GlowEnabled = true, GlowRadius = 6 };

        var bounds = Measure([plain, glowing]);

        Assert.Equal((32f, 32f), (bounds[1].X, bounds[1].Y));
        Assert.Equal(bounds[0].W + 36, bounds[1].W, 3);
        Assert.Equal(bounds[0].H + 36, bounds[1].H, 3);
    }

    /// <summary>The <c>text-glow</c> golden scene: every pixel the glow changes lies inside the bounds.</summary>
    [Fact]
    public void GlowingText_EveryChangedPixelLiesInsideItsBounds()
    {
        using var scene = new GoldenScene();
        var profile = GoldenScene.CreateProfile(320, 70);
        var text = new TextDisplayItem("Glowing text", profile)
        {
            X = 24, Y = 16, Font = "Inter", FontSize = 26, Bold = true, Color = "#FFFFFFFF",
            GlowEnabled = true, GlowRadius = 8, GlowColor = "#FF00E5FF",
        };

        var (bitmap, bounds) = RenderAndMeasure(scene, profile, text);
        using (bitmap)
        {
            var background = SKColor.Parse(GoldenScene.Background);
            var drawn = AssertDrawnPixelsInside(bitmap, bounds, c => Differs(c, background));
            Assert.True(drawn > 100);
        }
    }

    [Fact]
    public void Table_CoversMultilineCellsAndGlow()
    {
        using var scene = new GoldenScene();
        var profile = Transparent(300, 200);
        var key = SensorKey.Plugin("/bounds/table");

        var dataTable = new DataTable();
        dataTable.Columns.Add("Name", typeof(IPluginData));
        dataTable.Columns.Add("Value", typeof(IPluginData));
        dataTable.Rows.Add(new PluginText("name", "first\nsecond\nthird\nfourth"), new PluginText("value", "1"));
        scene.Sensors.Readings[key] = new SensorReading(dataTable, "0:80|1:40", "");

        var table = new TableSensorDisplayItem("Table", profile, key.Id)
        {
            X = 20, Y = 10, Font = "Inter", FontSize = 14, Color = "#FFFFFFFF", MaxRows = 1, TableFormat = "0:80|1:40",
            GlowEnabled = true, GlowRadius = 3, GlowColor = "#FFFFFFFF",
        };

        var (bitmap, bounds) = RenderAndMeasure(scene, profile, table);
        using (bitmap)
        {
            Assert.Equal(0, bounds.Rotation);
            var drawn = AssertDrawnPixelsInside(bitmap, bounds, c => c.Alpha > 2);
            Assert.True(drawn > 100);
        }
    }

    /// <summary>
    /// Flipped charts and gauges mirror after rotating, so they are drawn at -Rotation (and an odd-width raster
    /// 1 px left of X): every drawn pixel lies inside the reported bounds.
    /// </summary>
    [Theory]
    [InlineData("bar", 200, 45)]
    [InlineData("bar", 201, 0)]
    [InlineData("bar", 201, -30)]
    [InlineData("donut", 0, 60)]
    [InlineData("gauge-placeholder", 0, 30)]
    public void FlippedItems_DrawnPixelsLieInsideTheirBounds(string kind, int width, int rotation)
    {
        using var scene = new GoldenScene();
        var profile = Transparent(400, 300);

        DisplayItem item = kind switch
        {
            "bar" => new BarDisplayItem("Bar", profile)
            {
                X = 100, Y = 100, Width = width, Height = 40, Rotation = rotation, FlipX = true,
                SensorType = SensorType.Plugin, PluginSensorId = GoldenScene.Keys.CpuLoad.Id, MinValue = 0, MaxValue = 100,
                Background = true, BackgroundColor = "#FF2A2F3A", Frame = true, FrameColor = "#FFFFFFFF",
            },
            "donut" => new DonutDisplayItem("Donut", profile)
            {
                X = 100, Y = 100, Radius = 60, Thickness = 16, Span = 180, Rotation = rotation, FlipX = true,
                SensorType = SensorType.Plugin, PluginSensorId = GoldenScene.Keys.CpuLoad.Id, MinValue = 0, MaxValue = 100,
                Background = true, BackgroundColor = "#FF2A2F3A",
            },
            _ => new GaugeDisplayItem("Gauge", profile) { X = 100, Y = 100, Width = 151, Height = 60, Rotation = rotation, FlipX = true },
        };

        var (bitmap, bounds) = RenderAndMeasure(scene, profile, item);
        using (bitmap)
        {
            Assert.Equal(-rotation, bounds.Rotation);
            var drawn = AssertDrawnPixelsInside(bitmap, bounds, c => c.Alpha > 64);
            Assert.True(drawn > 100);
        }

        if (kind == "bar" && rotation == 0)
        {
            Assert.Equal(99, bounds.X);
        }
    }

    /// <summary>
    /// An SVG rotates about (x + Width / 2, y + Width / 2), a 1.x quirk kept for parity; the bounds are moved so
    /// rotating them about their centre puts them where the SVG is drawn. Gauges draw SVG frames the same way.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RotatedNonSquareSvg_DrawnPixelsLieInsideItsBounds(bool gauge, bool flipX)
    {
        using var scene = new GoldenScene();
        var profile = Transparent(400, 400);

        DisplayItem item;
        if (gauge)
        {
            var g = new GaugeDisplayItem("Gauge", profile)
            {
                X = 100, Y = 100, Width = 200, Height = 100, Rotation = 90, FlipX = flipX, AnimationSpeed = 0,
                SensorType = SensorType.Plugin, PluginSensorId = GoldenScene.Keys.Gauge.Id, MinValue = 0, MaxValue = 100,
            };
            g.Images.Add(new ImageDisplayItem("icon.svg", profile, "icon.svg", relativePath: true));
            item = g;
        }
        else
        {
            item = new ImageDisplayItem("icon.svg", profile, "icon.svg", relativePath: true) { X = 100, Y = 100, Width = 200, Height = 100, Rotation = 90 };
        }

        var (bitmap, bounds) = RenderAndMeasure(scene, profile, item);
        using (bitmap)
        {
            Assert.Equal((200f, 100f), (bounds.W, bounds.H));
            var drawn = AssertDrawnPixelsInside(bitmap, bounds, c => c.Alpha > 64);
            Assert.True(drawn > 10000);
        }
    }

    /// <summary>Every drawn pixel of a text item lies inside its measured (rotated) bounds.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 30)]
    [InlineData(true, -40)]
    public void Text_DrawnPixelsLieInsideItsBounds(bool rightAlign, int rotation)
    {
        var text = new TextDisplayItem("Bounds gyp Ag", Profile) { X = 200, Y = 120, FontSize = 24, Color = "#FFFFFFFF", RightAlign = rightAlign, Rotation = rotation };

        var bounds = Assert.Single(Measure([text]));

        using var bitmap = new SKBitmap(new SKImageInfo(Profile.Width, Profile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            var renderer = new ProfileRenderer(new FakeSensorReader(), new FakeSensorHistory(), new FakeImageSource(),
                new FakeAssetLocator(Path.GetTempPath()), new FixedTimeProvider(GoldenScene.UtcNow));
            renderer.Draw(canvas, Profile, [text], new ProfileRenderState(), new RenderOptions(Smoothing: false), 1);
        }

        Assert.True(AssertDrawnPixelsInside(bitmap, bounds, c => c.Red > 64) > 100);
    }

    private static Profile Transparent(int width, int height) => new("Bounds", width, height) { BackgroundColor = "" };

    private static bool Differs(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) > 2 || Math.Abs(a.Green - b.Green) > 2 || Math.Abs(a.Blue - b.Blue) > 2 || Math.Abs(a.Alpha - b.Alpha) > 2;

    /// <summary>Renders <paramref name="item"/> as the goldens do and measures it with the same renderer and tick.</summary>
    private static (SKBitmap Bitmap, ItemBounds Bounds) RenderAndMeasure(GoldenScene scene, Profile profile, DisplayItem item)
    {
        var bitmap = scene.Render(profile, [item]);
        var bounds = Assert.Single(scene.Renderer.MeasureBounds(profile, [item], new ProfileRenderState { Tick = 1 }));
        return (bitmap, bounds);
    }

    /// <summary>
    /// Asserts every pixel <paramref name="isDrawn"/> picks lies inside <paramref name="bounds"/> rotated about its centre
    /// (grown by 1 px for anti-aliasing), and returns how many there were.
    /// </summary>
    private static int AssertDrawnPixelsInside(SKBitmap bitmap, ItemBounds bounds, Func<SKColor, bool> isDrawn)
    {
        var rect = SKRect.Create(bounds.X, bounds.Y, bounds.W, bounds.H);
        var toLocal = SKMatrix.CreateRotationDegrees(-bounds.Rotation, rect.MidX, rect.MidY);
        rect.Inflate(1, 1);

        var drawn = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (isDrawn(bitmap.GetPixel(x, y)))
                {
                    drawn++;
                    var local = toLocal.MapPoint(x + 0.5f, y + 0.5f);
                    Assert.True(rect.Contains(local), $"pixel ({x},{y}) is outside {bounds}");
                }
            }
        }

        return drawn;
    }

    private static SKPoint[] Corners(SKRect rect, SKMatrix matrix) => matrix.MapPoints(
    [
        new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
        new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom),
    ]);
}
