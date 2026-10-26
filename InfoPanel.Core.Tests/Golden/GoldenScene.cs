using System.Data;
using System.Globalization;
using InfoPanel.Drawing;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using SkiaSharp;

namespace InfoPanel.Core.Tests.Golden;

/// <summary>A clock stopped at <see cref="UtcNow"/>, in a UTC local zone, whose timestamps never advance (Elapsed = 0).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long GetTimestamp() => 0;
}

/// <summary>Every profile's assets folder is <c>Fixtures/assets</c> in the test output.</summary>
internal sealed class FixtureAssetLocator : IAssetLocator
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets");

    public string GetProfileAssetsDirectory(Guid profileId) => Directory;
}

/// <summary>
/// The fixed inputs every golden renders with: fake sensors and history with fixed values, the fixture
/// images through a synchronous <see cref="ImageCache"/>, 2026-01-02 03:04:05 (UTC local zone), Elapsed 0,
/// no smoothing, tick 1, invariant culture.
/// </summary>
internal sealed class GoldenScene : IDisposable
{
    public static readonly DateTimeOffset UtcNow = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public const string Background = "#FF15191F";

    public static class Keys
    {
        public static readonly SensorKey CpuLoad = SensorKey.Plugin("/golden/cpu/load");
        public static readonly SensorKey CpuTemp = SensorKey.Plugin("/golden/cpu/temp");
        public static readonly SensorKey GpuClock = SensorKey.Plugin("/golden/gpu/clock");
        public static readonly SensorKey Voltage = SensorKey.Plugin("/golden/cpu/vcore");
        public static readonly SensorKey Fan = SensorKey.Plugin("/golden/fan/rpm");
        public static readonly SensorKey Gauge = SensorKey.Plugin("/golden/gauge");
        public static readonly SensorKey Processes = SensorKey.Plugin("/golden/processes");
        public static readonly SensorKey Camera = SensorKey.Plugin("/golden/camera");
    }

    public FakeSensorReader Sensors { get; } = new();
    public FakeSensorHistory History { get; } = new();
    public ImageCache Images { get; } = new(new ImageCacheOptions { SynchronousLoad = true });
    public ProfileRenderer Renderer { get; }

    public GoldenScene()
    {
        Sensors.Readings[Keys.CpuLoad] = new SensorReading(3.0, 97.0, 38.0, 42.5, "%");
        Sensors.Readings[Keys.CpuTemp] = new SensorReading(35.0, 88.0, 61.0, 71.25, "°C");
        Sensors.Readings[Keys.GpuClock] = new SensorReading(210.0, 2100.0, 1500.0, 1834.567, "MHz");
        Sensors.Readings[Keys.Voltage] = new SensorReading(0.8, 1.4, 1.1, 1.2345, "V");
        Sensors.Readings[Keys.Fan] = new SensorReading(800.0, 1900.0, 1100.0, 1200.0, "RPM");
        Sensors.Readings[Keys.Gauge] = new SensorReading(0.0, 100.0, 50.0, 50.0, "%");
        Sensors.Readings[Keys.Processes] = new SensorReading(ProcessTable(), "0:110|1:60|2:50", "");
        Sensors.Readings[Keys.Camera] = new SensorReading("https://example.com/camera.png");

        History.Samples[Keys.CpuLoad] = [.. Enumerable.Range(0, 160).Select(i => 50 + 30 * Math.Sin(i / 9.0) + 12 * Math.Sin(i / 2.5))];
        History.Samples[Keys.CpuTemp] = [.. Enumerable.Range(0, 160).Select(i => 60 + 20 * Math.Sin(i / 14.0))];

        Renderer = new ProfileRenderer(Sensors, History, Images, new FixtureAssetLocator(), new FixedTimeProvider(UtcNow));
    }

    private static DataTable ProcessTable()
    {
        var table = new DataTable();
        table.Columns.Add("Process", typeof(IPluginData));
        table.Columns.Add("CPU", typeof(IPluginData));
        table.Columns.Add("RAM", typeof(IPluginData));

        (string Name, string Cpu, string Ram)[] rows =
        [
            ("dotnet", "12.5%", "412 MB"),
            ("WindowServer", "8.1%", "260 MB"),
            ("Safari", "3.4%", "1.2 GB"),
            ("kernel_task", "2.0%", "88 MB"),
        ];

        foreach (var (name, cpu, ram) in rows)
        {
            table.Rows.Add(new PluginText("name", name), new PluginText("cpu", cpu), new PluginText("ram", ram));
        }

        return table;
    }

    public static Profile CreateProfile(int width, int height) => new("Golden", width, height)
    {
        BackgroundColor = Background,
        Font = "Inter",
    };

    /// <summary>Renders at <paramref name="scale"/> onto a bitmap of the scaled profile size.</summary>
    public SKBitmap Render(Profile profile, IReadOnlyList<DisplayItem> items, float scale = 1)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        try
        {
            var info = new SKImageInfo((int)Math.Ceiling(profile.Width * scale), (int)Math.Ceiling(profile.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            var state = new ProfileRenderState { Tick = 1 };
            Renderer.Draw(canvas, profile, items, state, new RenderOptions(Smoothing: false), scale);
            canvas.Flush();

            return bitmap;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    public void Dispose() => Images.Dispose();
}
