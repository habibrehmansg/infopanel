using System.Globalization;
using System.Xml;
using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// Writes the frozen 1.x fixtures into the source tree. Runs only with <c>GENERATE_FIXTURES=1</c>:
/// <c>GENERATE_FIXTURES=1 dotnet test InfoPanel.slnx --filter FixtureGenerator</c>.
/// The fixtures were generated from the 1.x-shaped models (before <c>DisplayItem.Guid</c> was serialized)
/// and are committed; regenerating them after a model change defeats their purpose.
/// </summary>
public class FixtureGenerator
{
    internal static readonly Guid DeskProfileGuid = new("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7");
    internal static readonly Guid PanelProfileGuid = new("0a1b2c3d-4e5f-4061-8273-94a5b6c7d8e9");

    [Fact]
    public void Generate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GENERATE_FIXTURES") == "1",
            "Set GENERATE_FIXTURES=1 to regenerate the 1.x fixtures.");

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            var root = FindRepoRoot();
            var profiles = BuildProfiles();
            var items = BuildItems(profiles[0]);

            var itemsPath = Path.Combine(root, "InfoPanel.Core.Tests", V1Format.ItemsFixture);
            Directory.CreateDirectory(Path.GetDirectoryName(itemsPath)!);
            using (var writer = XmlWriter.Create(itemsPath, V1Format.WriterSettings()))
                V1Format.ItemsSerializer.Serialize(writer, items);

            var profilesPath = Path.Combine(root, "InfoPanel.Core.Tests", V1Format.ProfilesFixture);
            using (var writer = XmlWriter.Create(profilesPath, V1Format.WriterSettings()))
                V1Format.ProfilesSerializer.Serialize(writer, profiles);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("InfoPanel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static List<Profile> BuildProfiles()
    {
        var desk = new Profile("Desk Overlay", 800, 480)
        {
            Guid = DeskProfileGuid,
            IsSelected = true,
            ShowFps = true,
            Drag = false,
            BackgroundColor = "#FF102030",
            Active = true,
            OpenGL = true,
            FontScale = 1.5f,
            Topmost = true,
            Font = "Consolas",
            FontSize = 18,
            Color = "#FFEEEEEE",
            WindowX = 1940,
            WindowY = -40,
            TargetWindow = new TargetWindow(1920, 0, 2560, 1440, @"\\.\DISPLAY2"),
            Resize = false,
            StrictWindowMatching = true,
            TriggerProcessNames = "game.exe, another.exe",
        };

        var panel = new Profile("USB Panel", 480, 320)
        {
            Guid = PanelProfileGuid,
            Active = false,
            BackgroundColor = "#FF000000",
        };

        return [desk, panel];
    }

    private static List<DisplayItem> BuildItems(Profile profile)
    {
        const string LibreLoad = "/amdcpu/0/load/0";
        const string PluginTemp = "/infopanel.mock/gpu/gpu-temp";

        var innerGroup = new GroupDisplayItem { Name = "Inner Group", X = 12, Y = 24, IsExpanded = false };
        innerGroup.SetProfile(profile);
        innerGroup.DisplayItems.Add(new ClockDisplayItem("Clock", profile)
        {
            X = 20, Y = 300, Format = "HH:mm", FontSize = 48, Color = "#FFFFCC00", Bold = true, Rotation = -10,
        });
        innerGroup.DisplayItems.Add(new CalendarDisplayItem("Calendar", profile)
        {
            X = 20, Y = 360, Format = "dddd d MMMM yyyy", FontSize = 16, Italic = true, Uppercase = true, Color = "#FFAAAAAA",
        });

        var outerGroup = new GroupDisplayItem { Name = "Outer Group", X = 5, Y = 6, Rotation = 0 };
        outerGroup.SetProfile(profile);
        outerGroup.DisplayItems.Add(new TextDisplayItem("Grouped Label", profile) { X = 20, Y = 260, FontSize = 14, Color = "#FF00FF00" });
        outerGroup.DisplayItems.Add(innerGroup);

        var bar = new BarDisplayItem("CPU Bar", profile, 0xF0000300u, 1u, 0x1000005u)
        {
            X = 400, Y = 20, Width = 300, Height = 24, Rotation = 15,
            HwInfoRemoteIndex = 1, ValueType = SensorValueType.MAX,
            MinValue = 10, MaxValue = 90, AutoValue = true, FlipX = true,
            Frame = false, FrameColor = "#FF112233", Background = false, BackgroundColor = "#FF445566", Color = "#FF778899",
            CornerRadius = 6, Gradient = false, GradientColor = "#FFAABBCC",
        };

        var graph = new GraphDisplayItem("CPU Load Graph", profile, GraphDisplayItem.GraphType.HISTOGRAM, LibreLoad)
        {
            X = 400, Y = 60, Width = 320, Height = 80,
            ValueType = SensorValueType.AVERAGE, MinValue = 5, MaxValue = 95, AutoValue = true, FlipX = true,
            FrameColor = "#FF223344", BackgroundColor = "#FF000000", Color = "#FF00FFFF",
            Thickness = 3, Step = 6, Fill = false, FillColor = "#80FF00FF",
        };

        var evenDonut = new DonutDisplayItem("GPU Temp Donut", profile)
        {
            X = 400, Y = 160, Width = 120, Height = 120, Rotation = 45,
            SensorType = SensorType.Plugin, PluginSensorId = PluginTemp,
            ValueType = SensorValueType.MIN, MinValue = 30, MaxValue = 110,
            Thickness = 14, Span = 270, Color = "#FFFF4400", BackgroundColor = "#FF202020",
        };

        // Odd width: 1.x serializes Radius = Width / 2 after Width, so loading turns 101 into 100.
        var oddDonut = new DonutDisplayItem("Odd Donut", profile, LibreLoad)
        {
            X = 540, Y = 160, Width = 101, Height = 101, Thickness = 9, Span = 180, Rotation = 0,
        };

        var table = new TableSensorDisplayItem("Process Table", profile, "/infopanel.mock/processes/top")
        {
            X = 20, Y = 420, Font = "Consolas", FontSize = 12, Color = "#FFDDDDDD", Width = 360, Height = 120,
            ValueType = SensorValueType.MAX, MaxRows = 5, ShowHeader = false, TableFormat = "0:120|2:60|3:40",
        };

        var sensor = new SensorDisplayItem("CPU Temp", profile, 0xF0000300u, 0u, 0x2000001u)
        {
            X = 20, Y = 20, Rotation = 5, FontSize = 28, Bold = true, Color = "#FFFFFFFF", RightAlign = true,
            HwInfoRemoteIndex = 2, ValueType = SensorValueType.AVERAGE,
            Threshold1 = 60.5, Threshold1Color = "#FFFFA500", Threshold2 = 85.25, Threshold2Color = "#FFFF0000",
            ShowName = true, Unit = " °C", OverrideUnit = true, ShowUnit = true,
            OverridePrecision = true, Precision = 1, ShowThousandsSeparator = true,
            AdditionModifier = -1.5, AbsoluteAddition = false, MultiplicationModifier = 0.5, DivisionToggle = true,
        };

        var hiddenSensor = new SensorDisplayItem("GPU Temp (hidden)", profile)
        {
            X = 20, Y = 60, Hidden = true, SensorType = SensorType.Plugin, PluginSensorId = PluginTemp,
            ShowUnit = false, Color = "#FF00FF00",
        };

        var text = new TextDisplayItem("Now Playing: a long marquee title", profile)
        {
            X = 20, Y = 100, Rotation = 30, IsLocked = true,
            Font = "Consolas", FontStyle = "Bold Italic", FontSize = 32, Bold = true, Italic = true, Underline = true, Strikeout = true,
            Color = "#FF00AAFF", CenterAlign = true, Uppercase = true, Wrap = false, Ellipsis = false, Width = 300, Height = 60,
            Marquee = true, MarqueeSpeed = 80, MarqueeSpacing = 24,
            GlowEnabled = true, GlowRadius = 12, GlowColor = "#FFFF00FF", GlowBlendMode = "Screen",
        };

        var sensorImage = new SensorImageDisplayItem("Fan Icon", profile, "/amdcpu/0/fan/1")
        {
            X = 700, Y = 300, FilePath = "icon.svg", RelativePath = true, Scale = 75, Width = 64, Height = 64,
            ValueType = SensorValueType.MAX, Threshold1 = 20.5, Threshold2 = 80, Layer = true, LayerColor = "#80000000",
        };

        var gif = new ImageDisplayItem("Animated", profile, "anim.gif", relativePath: true)
        {
            X = 600, Y = 20, Width = 160, Height = 90, Scale = 50, Cache = false, ShowPanel = true, Volume = 30, Rotation = 90,
        };

        var logo = new ImageDisplayItem("Logo", profile, "logo.png", relativePath: true) { X = 760, Y = 400, Scale = 125 };

        var urlImage = new ImageDisplayItem("Remote Image", profile)
        {
            X = 600, Y = 120, Type = ImageDisplayItem.ImageType.URL, HttpUrl = "https://example.com/feed.png",
        };

        var rtspImage = new ImageDisplayItem("Camera", profile)
        {
            X = 600, Y = 220, Type = ImageDisplayItem.ImageType.RTSP, RtspUrl = "rtsp://camera.local/stream", Volume = 75,
        };

        var httpImage = new HttpImageDisplayItem("Album Art", profile)
        {
            X = 700, Y = 380, Width = 96, Height = 96, Scale = 90,
            SensorType = SensorType.Plugin, PluginSensorId = "/infopanel.mock/media/cover-url", ValueType = SensorValueType.NOW,
        };

        var gauge = new GaugeDisplayItem("Load Gauge", profile, 0xF0000400u, 0u, 0x7000000u)
        {
            X = 300, Y = 300, Rotation = 180, HwInfoRemoteIndex = 0, ValueType = SensorValueType.MAX,
            MinValue = 10.5, MaxValue = 95.25, Scale = 80, AnimationSpeed = 12.5, FlipX = true, Width = 128, Height = 128,
        };
        for (var i = 0; i < 3; i++)
            gauge.Images.Add(new ImageDisplayItem($"g{i}", profile, $"g{i}.png", relativePath: true) { Scale = 80 });

        var star = new ShapeDisplayItem("Star", profile)
        {
            X = 200, Y = 200, Rotation = 45, Type = ShapeDisplayItem.ShapeType.Star, Width = 90, Height = 80,
            CornerRadius = 4, ShowFrame = false, FrameThickness = 2, FrameColor = "#FF0000FF",
            ShowFill = true, FillColor = "#FFFFFF00", ShowGradient = true, GradientColor = "#FFFF00FF", GradientColor2 = "#FF00FFFF",
            GradientType = GradientType.Spiral, GradientAngle = -30, GradientAnimationSpeed = 120,
        };

        return
        [
            outerGroup, bar, graph, evenDonut, oddDonut, table, sensor, hiddenSensor, text,
            sensorImage, gif, logo, urlImage, rtspImage, httpImage, gauge, star,
        ];
    }
}
