using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;
using static InfoPanel.Core.Tests.Golden.GoldenScene;

namespace InfoPanel.Core.Tests.Golden;

/// <summary>
/// Golden tests share process-wide rendering state (the font registry, typeface caches), so they run in one
/// collection, never in parallel with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoldenCollection
{
    public const string Name = "Golden";
}

/// <summary>
/// Renders each case with <see cref="GoldenScene"/>'s fixed inputs and compares it with <c>Golden/{os}/{case}.png</c>
/// (see <see cref="GoldenImage"/>). Regenerate with <c>UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public class GoldenTests
{
    private delegate (Profile Profile, List<DisplayItem> Items) Case();

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["text-left"] = () => Scene(320, 60, p => [Text(p, "Left aligned Inter 20", 10, 12)]),
        ["text-center-box"] = () => Scene(320, 60, p => [
            Box(p, 10, 8, 300, 44),
            Text(p, "Centred in 300 px", 10, 14, t => { t.Width = 300; t.CenterAlign = true; }),
        ]),
        ["text-right-auto"] = () => Scene(320, 60, p => [
            Box(p, 309, 4, 2, 52),
            Text(p, "Right aligned, auto size", 310, 14, t => t.RightAlign = true),
        ]),
        ["text-glow"] = () => Scene(320, 70, p => [
            Text(p, "Glowing text", 24, 16, t =>
            {
                t.FontSize = 26;
                t.Bold = true;
                t.Color = "#FFFFFFFF";
                t.GlowEnabled = true;
                t.GlowRadius = 8;
                t.GlowColor = "#FF00E5FF";
            }),
        ]),
        ["text-rotated-30"] = () => Scene(320, 180, p => [Text(p, "Rotated 30 degrees", 50, 75, t => t.Rotation = 30)]),
        ["text-marquee-t0"] = () => Scene(320, 60, p => [
            Box(p, 20, 10, 200, 40),
            Text(p, "A marquee that is much wider than its 200 px box", 20, 16, t =>
            {
                t.Width = 200;
                t.Height = 30;
                t.Marquee = true;
            }),
        ]),
        ["sensor-thresholds-precision-unit"] = () => Scene(360, 200, p => [
            // 42.5 % load: below both thresholds, default precision for "%" (0 decimals), name shown.
            Sensor(p, Keys.CpuLoad, 10, 8, s => { s.Name = "CPU"; s.ShowName = true; s.Threshold1 = 60; s.Threshold2 = 80; }),
            // 71.25 °C: at or above threshold 1 (orange), precision 2.
            Sensor(p, Keys.CpuTemp, 10, 44, s => { s.Threshold1 = 60; s.Threshold1Color = "#FFFFA000"; s.Threshold2 = 80; s.Threshold2Color = "#FFFF1744"; s.OverridePrecision = true; s.Precision = 2; }),
            // 1834.567 MHz: above threshold 2 (red), thousands separator, overridden unit.
            Sensor(p, Keys.GpuClock, 10, 80, s => { s.Threshold1 = 1000; s.Threshold1Color = "#FFFFA000"; s.Threshold2 = 1500; s.Threshold2Color = "#FFFF1744"; s.ShowThousandsSeparator = true; s.OverrideUnit = true; s.Unit = " MHz (gpu)"; }),
            // 1.2345 V: default precision for "V" (3 decimals).
            Sensor(p, Keys.Voltage, 10, 116),
            // Max value, unit hidden, precision 1, ×10 modifier.
            Sensor(p, Keys.CpuLoad, 10, 152, s => { s.ValueType = SensorValueType.MAX; s.ShowUnit = false; s.OverridePrecision = true; s.Precision = 1; s.MultiplicationModifier = 10; }),
            // Missing sensor: "-".
            Sensor(p, SensorKey.Plugin("/golden/missing"), 250, 8),
        ]),
        ["clock"] = () => Scene(400, 60, p => [new ClockDisplayItem("Clock", p) { X = 10, Y = 12, Font = "Inter", FontSize = 22, Color = "#FFFFFFFF", Format = "yyyy-MM-dd HH:mm:ss tt" }]),
        ["calendar"] = () => Scene(400, 60, p => [new CalendarDisplayItem("Calendar", p) { X = 10, Y = 12, Font = "Inter", FontSize = 22, Color = "#FFFFFFFF", Format = "dddd d MMMM yyyy", Uppercase = true }]),
        ["table"] = () => Scene(260, 130, p => [
            new TableSensorDisplayItem("Processes", p, Keys.Processes.Id) { X = 10, Y = 8, Font = "Inter", FontSize = 12, Color = "#FFE0E0E0", MaxRows = 4, TableFormat = "0:110|1:60|2:50", RightAlign = true },
        ]),
        ["image-png"] = () => Scene(200, 100, p => [Image(p, "logo.png", 10, 10), Image(p, "logo.png", 116, 10, i => { i.Width = 48; i.Height = 80; })]),
        ["image-svg"] = () => Scene(200, 100, p => [Image(p, "icon.svg", 10, 10), Image(p, "icon.svg", 100, 10, i => { i.Width = 80; i.Height = 80; i.Rotation = 20; })]),
        ["image-gif-frame0"] = () => Scene(120, 84, p => [Image(p, "blink.gif", 10, 10, i => i.Scale = 200)]),
        ["image-missing"] = () => Scene(260, 130, p => [Image(p, "does-not-exist.png", 10, 10), Image(p, "does-not-exist.png", 130, 10, i => { i.Width = 120; i.Height = 60; })]),
        ["sensor-image-shown"] = () => Scene(120, 100, p => [SensorImage(p, threshold1: 1000, threshold2: 2000)]),
        ["sensor-image-hidden"] = () => Scene(120, 100, p => [SensorImage(p, threshold1: 0, threshold2: 500)]),
        ["http-image-placeholder"] = () => Scene(140, 120, p => [
            new HttpImageDisplayItem("Camera", p) { X = 10, Y = 10, SensorType = SensorType.Plugin, PluginSensorId = Keys.Camera.Id },
        ]),
        ["gauge-middle-frame"] = () => Scene(120, 120, p => [Gauge(p)]),
        ["bar-horizontal-gradient"] = () => Scene(320, 60, p => [
            Bar(p, 10, 15, 300, 30, b => { b.Gradient = true; b.Color = "#FF00C853"; b.GradientColor = "#FFFFD600"; b.BackgroundColor = "#FF2A2F3A"; b.FrameColor = "#FFFFFFFF"; }),
        ]),
        ["bar-vertical-rounded"] = () => Scene(100, 220, p => [
            Bar(p, 30, 10, 40, 200, b => { b.Gradient = false; b.CornerRadius = 12; b.Color = "#FF2979FF"; b.BackgroundColor = "#FF2A2F3A"; b.FrameColor = "#FFB0BEC5"; }),
        ]),
        ["graph-line-fill"] = () => Scene(320, 120, p => [Graph(p, GraphDisplayItem.GraphType.LINE)]),
        ["graph-histogram"] = () => Scene(320, 120, p => [Graph(p, GraphDisplayItem.GraphType.HISTOGRAM)]),
        ["donut-span270-rotation90"] = () => Scene(160, 160, p => [
            new DonutDisplayItem("Donut", p) { X = 20, Y = 20, Radius = 60, Thickness = 16, Span = 270, Rotation = 90, SensorType = SensorType.Plugin, PluginSensorId = Keys.CpuTemp.Id, MinValue = 0, MaxValue = 100, Color = "#FF00E676", BackgroundColor = "#FF2A2F3A", Background = true, Frame = true, FrameColor = "#FFFFFFFF" },
        ]),
        ["shapes-all"] = () => Scene(440, 330, p => [.. Enum.GetValues<ShapeDisplayItem.ShapeType>().Select((type, i) => Shape(p, type, i))]),
        ["gradients-all"] = () => Scene(440, 220, p => [.. Enum.GetValues<GradientType>().Select((type, i) => Gradient(p, type, i))]),
        ["group-nested-hidden-child"] = () => Scene(340, 120, p => [NestedGroup(p)]),
        ["composite-scale1"] = () => Composite(),
        ["composite-scale2"] = () => Composite(),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Renders(string name)
    {
        using var scene = new GoldenScene();
        var (profile, items) = Cases[name]();
        var scale = name.EndsWith("scale2", StringComparison.Ordinal) ? 2f : 1f;

        using var bitmap = scene.Render(profile, items, scale);
        GoldenImage.Verify(name, bitmap);
    }

    private static (Profile, List<DisplayItem>) Scene(int width, int height, Func<Profile, List<DisplayItem>> items)
    {
        var profile = CreateProfile(width, height);
        return (profile, items(profile));
    }

    private static TextDisplayItem Text(Profile p, string text, int x, int y, Action<TextDisplayItem>? configure = null)
    {
        var item = new TextDisplayItem(text, p) { X = x, Y = y, Font = "Inter", FontSize = 20, Color = "#FFF5F5F5" };
        configure?.Invoke(item);
        return item;
    }

    /// <summary>An outline that marks a text item's box or anchor, so alignment is visible in the golden.</summary>
    private static ShapeDisplayItem Box(Profile p, int x, int y, int width, int height) => new("Box", p)
    {
        X = x,
        Y = y,
        Width = width,
        Height = height,
        ShowFill = false,
        ShowGradient = false,
        ShowFrame = true,
        FrameThickness = 1,
        FrameColor = "#FF5C6370",
    };

    private static SensorDisplayItem Sensor(Profile p, SensorKey key, int x, int y, Action<SensorDisplayItem>? configure = null)
    {
        var item = new SensorDisplayItem(key.Id, p) { X = x, Y = y, Font = "Inter", FontSize = 18, Color = "#FFE0E0E0", SensorType = SensorType.Plugin, PluginSensorId = key.Id };
        configure?.Invoke(item);
        return item;
    }

    private static ImageDisplayItem Image(Profile p, string file, int x, int y, Action<ImageDisplayItem>? configure = null)
    {
        var item = new ImageDisplayItem(file, p, file, relativePath: true) { X = x, Y = y };
        configure?.Invoke(item);
        return item;
    }

    private static SensorImageDisplayItem SensorImage(Profile p, double threshold1, double threshold2) => new("Fan", p)
    {
        X = 10,
        Y = 10,
        FilePath = "icon.svg",
        RelativePath = true,
        SensorType = SensorType.Plugin,
        PluginSensorId = Keys.Fan.Id,
        Threshold1 = threshold1,
        Threshold2 = threshold2,
    };

    private static GaugeDisplayItem Gauge(Profile p)
    {
        var gauge = new GaugeDisplayItem("Gauge", p) { X = 20, Y = 20, SensorType = SensorType.Plugin, PluginSensorId = Keys.Gauge.Id, MinValue = 0, MaxValue = 100, AnimationSpeed = 0 };
        for (var i = 0; i < FixtureAssets.GaugeFrames; i++)
        {
            gauge.Images.Add(new ImageDisplayItem($"gauge-{i}.png", p, $"gauge-{i}.png", relativePath: true));
        }

        return gauge;
    }

    private static BarDisplayItem Bar(Profile p, int x, int y, int width, int height, Action<BarDisplayItem> configure)
    {
        var bar = new BarDisplayItem("Bar", p) { X = x, Y = y, Width = width, Height = height, SensorType = SensorType.Plugin, PluginSensorId = Keys.CpuLoad.Id, MinValue = 0, MaxValue = 100, Background = true, Frame = true };
        configure(bar);
        return bar;
    }

    private static GraphDisplayItem Graph(Profile p, GraphDisplayItem.GraphType type) => new("Graph", p, type)
    {
        X = 10,
        Y = 10,
        Width = 300,
        Height = 100,
        SensorType = SensorType.Plugin,
        PluginSensorId = Keys.CpuLoad.Id,
        MinValue = 0,
        MaxValue = 100,
        Step = 4,
        Thickness = 2,
        Color = "#FF40C4FF",
        Fill = true,
        FillColor = "#5540C4FF",
        Background = true,
        BackgroundColor = "#FF1F2430",
        Frame = true,
        FrameColor = "#FF5C6370",
    };

    private static ShapeDisplayItem Shape(Profile p, ShapeDisplayItem.ShapeType type, int index) => new(type.ToString(), p)
    {
        Type = type,
        X = 10 + index % 4 * 108,
        Y = 10 + index / 4 * 108,
        Width = 96,
        Height = 96,
        CornerRadius = 20,
        ShowFill = true,
        FillColor = "#FF3D5AFE",
        ShowGradient = false,
        ShowFrame = true,
        FrameThickness = 3,
        FrameColor = "#FFFFFFFF",
    };

    private static ShapeDisplayItem Gradient(Profile p, GradientType type, int index) => new(type.ToString(), p)
    {
        Type = ShapeDisplayItem.ShapeType.Rectangle,
        X = 10 + index % 3 * 144,
        Y = 10 + index / 3 * 104,
        Width = 132,
        Height = 96,
        ShowFill = true,
        FillColor = "#FFFF1744",
        ShowGradient = true,
        GradientColor = "#FF00E676",
        GradientColor2 = "#FF2979FF",
        GradientType = type,
        GradientAngle = 30,
        GradientAnimationSpeed = 300,
        ShowFrame = false,
    };

    /// <summary>
    /// Outer group: a shape and an inner group. Inner group: a visible text and a hidden ellipse (not drawn).
    /// A hidden group with a visible shape is also not drawn.
    /// </summary>
    private static GroupDisplayItem NestedGroup(Profile p)
    {
        var inner = new GroupDisplayItem { Name = "Inner" };
        inner.DisplayItems.Add(Text(p, "Inner group text", 130, 20));
        inner.DisplayItems.Add(new ShapeDisplayItem("Hidden ellipse", p) { Type = ShapeDisplayItem.ShapeType.Ellipse, X = 130, Y = 60, Width = 160, Height = 50, ShowGradient = false, FillColor = "#FFFF1744", Hidden = true });

        var hiddenGroup = new GroupDisplayItem { Name = "Hidden group", Hidden = true };
        hiddenGroup.DisplayItems.Add(new ShapeDisplayItem("In hidden group", p) { X = 200, Y = 70, Width = 100, Height = 40, ShowGradient = false, FillColor = "#FFFFEA00" });

        var outer = new GroupDisplayItem { Name = "Outer" };
        outer.DisplayItems.Add(new ShapeDisplayItem("Square", p) { X = 10, Y = 10, Width = 100, Height = 100, ShowGradient = false, FillColor = "#FF00BFA5", ShowFrame = false });
        outer.DisplayItems.Add(inner);
        outer.DisplayItems.Add(hiddenGroup);
        return outer;
    }

    /// <summary>One of most kinds on one profile, so scale 2 is checked across them all.</summary>
    private static (Profile, List<DisplayItem>) Composite() => Scene(480, 240, p => [
        new ShapeDisplayItem("Panel", p) { X = 6, Y = 6, Width = 468, Height = 228, Type = ShapeDisplayItem.ShapeType.Capsule, CornerRadius = 14, ShowFill = true, FillColor = "#FF1C222B", ShowGradient = false, ShowFrame = true, FrameThickness = 2, FrameColor = "#FF3A4250" },
        Text(p, "CPU", 20, 16, t => { t.FontSize = 16; t.Bold = true; t.Color = "#FF90A4AE"; }),
        Sensor(p, Keys.CpuLoad, 20, 40, s => s.FontSize = 30),
        Sensor(p, Keys.CpuTemp, 290, 40, s => { s.FontSize = 30; s.Threshold1 = 60; s.Threshold1Color = "#FFFFA000"; s.Width = 110; s.RightAlign = true; }),
        Bar(p, 20, 96, 440, 14, b => { b.Gradient = true; b.Color = "#FF00C853"; b.GradientColor = "#FFFFD600"; b.BackgroundColor = "#FF2A2F3A"; b.Frame = false; b.CornerRadius = 7; }),
        new GraphDisplayItem("Load", p, GraphDisplayItem.GraphType.LINE) { X = 20, Y = 124, Width = 300, Height = 96, SensorType = SensorType.Plugin, PluginSensorId = Keys.CpuLoad.Id, MinValue = 0, MaxValue = 100, Color = "#FF40C4FF", Fill = true, FillColor = "#5540C4FF", Background = false, Frame = false },
        new DonutDisplayItem("Temp", p) { X = 360, Y = 128, Radius = 44, Thickness = 10, Span = 360, SensorType = SensorType.Plugin, PluginSensorId = Keys.CpuTemp.Id, MinValue = 0, MaxValue = 100, Color = "#FFFF9100", BackgroundColor = "#FF2A2F3A", Background = true, Frame = false },
        Image(p, "icon.svg", 410, 12, i => { i.Width = 48; i.Height = 48; }),
        new ClockDisplayItem("Clock", p) { X = 340, Y = 16, Font = "Inter", FontSize = 12, Color = "#FF90A4AE", Format = "HH:mm:ss", Italic = true },
    ]);
}

public class GoldenHarnessTests
{
    /// <summary>macOS is the reference OS, so losing its goldens fails instead of silently skipping them all.</summary>
    [Fact]
    public void MissingGoldenFolder_FailsOnMacOS_AndSkipsElsewhere()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-goldens-" + Guid.NewGuid().ToString("N"));

        var exception = Record.Exception(() => GoldenImage.RequireGoldenDirectory(missing));

        Assert.NotNull(exception);
        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<Xunit.Sdk.FailException>(exception);
        }
        else
        {
            Assert.IsNotType<Xunit.Sdk.FailException>(exception);
        }
    }
}
