using System.Data;
using System.Reflection;
using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests;

public class RenderBehaviourTests
{
    [Fact]
    public void Clock_FormatsDateTimeOnlyFormats_LikeDateTimeNow()
    {
        var clock = new ClockDisplayItem("Clock", new Profile()) { Format = "U" };
        Assert.Equal("U", clock.Format);

        // A local time formats exactly as 1.x formatted DateTime.Now.
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var ctx = TestContexts.Create();
        ctx.Now = new DateTimeOffset(local);
        Assert.Equal(local.ToString("U"), clock.EvaluateText(ctx));

        // A non-local offset doesn't throw either (DateTimeOffset rejects "U").
        ctx.Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(13.75));
        Assert.False(string.IsNullOrEmpty(clock.EvaluateText(ctx)));

        var calendar = new CalendarDisplayItem("Calendar", new Profile()) { Format = "U" };
        ctx.Now = new DateTimeOffset(local);
        Assert.Equal(local.Date.ToString("U"), calendar.EvaluateText(ctx));
    }

    [Fact]
    public void ClockAndCalendar_FallBackToDefaultFormat_WhenFormatThrows()
    {
        var clock = new ClockDisplayItem("Clock", new Profile());
        var calendar = new CalendarDisplayItem("Calendar", new Profile());
        SetFormatField(clock, "%");
        SetFormatField(calendar, "%");

        var ctx = TestContexts.Create();
        var now = ClockDisplayItem.ToLocalDateTime(ctx.Now);

        Assert.Equal(now.ToString(ClockDisplayItem.DefaultFormat), clock.EvaluateText(ctx));
        Assert.Equal(now.Date.ToString(CalendarDisplayItem.DefaultFormat), calendar.EvaluateText(ctx));
    }

    private static void SetFormatField(object item, string format)
    {
        item.GetType().GetField("_format", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, format);
    }

    [Fact]
    public void UnsmoothedRender_DoesNotChangeLiveSmoothing()
    {
        static double[] Run(bool unsmoothedInBetween)
        {
            var bar = new BarDisplayItem("Bar", new Profile()) { Width = 300, Height = 40, SensorType = SensorType.Plugin, PluginSensorId = "/test/load" };
            var sensors = new FakeSensorReader();
            sensors.Readings[SensorKey.Plugin("/test/load")] = new SensorReading(0, 100, 50, 50, "%");

            var ctx = TestContexts.Create(sensors);
            var smoothed = new RenderOptions(Smoothing: true);
            var unsmoothed = new RenderOptions(Smoothing: false);
            var state = ctx.State.Get<ChartState>(bar.Guid);
            var values = new List<double>();

            using var bitmap = new SKBitmap(300, 40);
            using var g = SkiaGraphics.FromBitmap(bitmap, 1);

            ctx.Tick = 1;
            ctx.Options = smoothed;
            GraphDraw.Run(bar, g, ctx);
            values.Add(state.SmoothedValue);

            if (unsmoothedInBetween)
            {
                ctx.Options = unsmoothed;
                GraphDraw.Run(bar, g, ctx);
                values.Add(state.SmoothedValue);
            }

            ctx.Tick = 2;
            ctx.Options = smoothed;
            GraphDraw.Run(bar, g, ctx);
            values.Add(state.SmoothedValue);

            return [.. values];
        }

        var control = Run(unsmoothedInBetween: false);
        var withUnsmoothed = Run(unsmoothedInBetween: true);

        Assert.InRange(control[0], 0.001, 149.999); // tick 1 is mid-animation towards 150
        Assert.Equal(control[0], withUnsmoothed[0]);
        Assert.Equal(control[0], withUnsmoothed[1]); // the unsmoothed render left the state alone
        Assert.Equal(control[1], withUnsmoothed[2]); // tick 2 progresses as if it never happened
    }

    [Fact]
    public void MissingImages_DrawPlaceholder_AtDefaultSizeWhenSizeIsNative()
    {
        var profile = new Profile("Placeholder", 400, 200) { BackgroundColor = "#FF000000" };
        var image = new ImageDisplayItem("Logo", profile, "missing.png", relativePath: true) { X = 20, Y = 20 };
        var gauge = new GaugeDisplayItem("Gauge", profile) { X = 200, Y = 20 };

        Assert.Equal(0, image.Width);
        Assert.Equal(0, gauge.Width);

        using var bitmap = new SKBitmap(400, 200);
        using (var g = SkiaGraphics.FromBitmap(bitmap, profile.FontScale))
        {
            PanelDraw.Run(profile, [image, gauge], g, TestContexts.Create());
        }

        foreach (var x in new[] { 20, 200 })
        {
            Assert.Equal(new SKColor(250, 250, 250), bitmap.GetPixel(x + 5, 25));
            Assert.NotEqual(SKColors.Black, bitmap.GetPixel(x + 98, 20 + 98));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(x + 101, 25));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(x + 5, 20 + 101));
        }

        Assert.Equal(new SKSize(100, 100), ImagePlaceholder.Resolve(SKSize.Empty));
        Assert.Equal(new SKSize(50, 100), ImagePlaceholder.Resolve(new SKSize(50, 0)));
    }

    /// <summary>A text item whose evaluation always throws, as a model bug or bad data would.</summary>
    private sealed class BrokenText : TextDisplayItem
    {
        public BrokenText(Profile profile) : base("Broken", profile) { }

        public override string EvaluateText(RenderContext ctx) => throw new InvalidOperationException("broken");

        public override (string, string) EvaluateTextAndColor(RenderContext ctx) => throw new InvalidOperationException("broken");
    }

    [Fact]
    public void BrokenItem_IsSkipped_AndTheRestOfTheFrameDraws()
    {
        var profile = new Profile("Broken", 300, 100) { BackgroundColor = "#FF000000" };
        ShapeDisplayItem Square(int x, string color) => new("Square", profile)
        {
            X = x, Y = 10, Width = 50, Height = 50, CornerRadius = 0, ShowFrame = false, ShowGradient = false, FillColor = color,
        };
        var red = Square(10, "#FFFF0000");
        var green = Square(100, "#FF00FF00");
        var blue = Square(200, "#FF0000FF");
        var broken = new BrokenText(profile) { Rotation = 30 };
        var group = new GroupDisplayItem { Name = "Group" };
        group.DisplayItems.Add(new BrokenText(profile));
        group.DisplayItems.Add(blue);
        DisplayItem[] items = [red, broken, green, group];

        var ctx = TestContexts.Create();
        using var bitmap = new SKBitmap(300, 100);
        using (var g = SkiaGraphics.FromBitmap(bitmap, profile.FontScale))
        {
            PanelDraw.Run(profile, items, g, ctx);
            PanelDraw.Run(profile, items, g, ctx); // a second frame: still drawn, logged only once
            Assert.Equal(1, g.Canvas.SaveCount);
        }

        Assert.Equal(SKColors.Red, bitmap.GetPixel(35, 35));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(125, 35));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(225, 35));
        Assert.False(ctx.State.ShouldLogFailure(broken.Guid, "draw", "System.InvalidOperationException: broken"));

        // Measuring skips it the same way.
        var renderer = new ProfileRenderer(new FakeSensorReader(), new FakeSensorHistory(), new FakeImageSource(),
            new FakeAssetLocator(Path.GetTempPath()), TimeProvider.System);
        var bounds = renderer.MeasureBounds(profile, items, new ProfileRenderState());
        Assert.Equal([red.Guid, green.Guid, group.Guid, blue.Guid], bounds.Select(b => b.Id));
    }

    [Fact]
    public void Histogram_WithHugeThicknessAndStep_DoesNotOverflow()
    {
        var graph = new GraphDisplayItem("Graph", new Profile(), GraphDisplayItem.GraphType.HISTOGRAM)
        {
            Width = 100, Height = 40, Thickness = int.MaxValue, Step = int.MaxValue, SensorType = SensorType.Plugin, PluginSensorId = "/test/load",
        };
        var history = new FakeSensorHistory();
        history.Samples[SensorKey.Plugin("/test/load")] = [10, 50, 90];
        var ctx = TestContexts.Create(history: history);

        using var bitmap = new SKBitmap(100, 40);
        using var g = SkiaGraphics.FromBitmap(bitmap, 1);
        GraphDraw.Run(graph, g, ctx); // was a DivideByZeroException: Thickness + Step + 2 wrapped to 0
    }

    [Fact]
    public void TableLayout_SkipsColumnsOutOfRange()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(IPluginData));
        table.Rows.Add(new PluginText("name", "first"));
        var item = new TableSensorDisplayItem("Table", new Profile()) { MaxRows = 1, ShowHeader = true, TableFormat = "-1:100|0:50|1:20" };

        var g = SkiaGraphics.FromEmpty(1);
        var cells = PanelDraw.LayoutTable(g, item, 0, 0, 12, table).ToList();

        Assert.Equal(["Name", "first"], cells.Select(c => c.Text));
        Assert.All(cells, c => Assert.Equal((0, 50), (c.X, c.Width)));
    }
}
