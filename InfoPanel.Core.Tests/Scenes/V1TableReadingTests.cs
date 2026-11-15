using System.Data;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// A 1.x Table item imports as a <c>table</c> node over a table input; reading tables as data (each row read when asked,
/// a cell's number beside its text, up to <c>tableRows</c> rows) changes nothing it draws.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class V1TableReadingTests
{
    /// <summary>
    /// <see cref="EveryKeyInputs"/>, but its table read the way the engine reads a source's table: through
    /// <see cref="SensorSceneInputs"/> from a plugin's table of the same cells.
    /// </summary>
    private sealed class SourceTables : ISceneInputSource
    {
        private static readonly SensorKey Key = SensorKey.Plugin("/test/processes");
        private readonly EveryKeyInputs _inner = new();
        private readonly SensorSceneInputs _tables;

        public SourceTables()
        {
            var table = new DataTable();
            table.Columns.Add("Process", typeof(IPluginData));
            table.Columns.Add("CPU", typeof(IPluginData));
            table.Columns.Add("RAM", typeof(IPluginData));
            table.Rows.Add(new PluginText("name", "dotnet"), new PluginText("cpu", "12.5%"), new PluginText("ram", "412 MB"));
            table.Rows.Add(new PluginText("name", "WindowServer"), new PluginText("cpu", "8.1%"), new PluginText("ram", "260 MB"));
            table.Rows.Add(new PluginText("name", "Safari"), new PluginText("cpu", "3.4%"), new PluginText("ram", "1.2 GB"));
            var sensors = new FakeSensorReader();
            sensors.Readings[Key] = new SensorReading(table, "", "");
            _tables = new SensorSceneInputs(sensors, new FakeSensorHistory());
        }

        public SceneReading Read(string key, string inputType) =>
            inputType == "table" ? _tables.Read(Key.ToString(), "table") : _inner.Read(key, inputType);

        public IReadOnlyList<double> History(string key) => _inner.History(key);
    }

    private static SKBitmapResult Render(ISceneInputSource inputs)
    {
        using var media = V1ParityTests.FixtureMedia();
        var document = SceneKit.Load(Path.Combine(V1ParityTests.ReferenceDir, "all-kinds.document.json"));
        var options = new SceneRuntimeOptions { Inputs = inputs, Media = media, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant };
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, options);
        Assert.All(snapshot.Bounds, b => Assert.Null(b.Problem));
        var texts = snapshot.Texts.Where(t => t.ItemId == "v1-010").Select(t => t.Text).ToList();
        return new SKBitmapResult(snapshot.Render(), texts);
    }

    /// <summary>A frame and the texts its process table (item <c>v1-010</c>) draws.</summary>
    private sealed record SKBitmapResult(SkiaSharp.SKBitmap Bitmap, IReadOnlyList<string> Cells) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    [Fact]
    public void The_all_kinds_table_draws_the_same_read_from_a_source_as_its_golden()
    {
        using var before = Render(new EveryKeyInputs());
        using var after = Render(new SourceTables());

        // The process table draws its two columns of three rows, read from the source's table, and the frame is the same
        // pixel for pixel.
        Assert.Equal(6, after.Cells.Count);
        Assert.Equal(before.Cells, after.Cells);
        Assert.Equal(0, SceneKit.DifferingPixels(before.Bitmap, after.Bitmap, tolerance: 0));
        GoldenImage.Verify("v1-all-kinds", after.Bitmap, "scene");
    }
}
