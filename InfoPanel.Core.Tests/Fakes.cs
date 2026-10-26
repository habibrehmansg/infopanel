using InfoPanel.Drawing;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Sensors;

namespace InfoPanel.Core.Tests;

internal sealed class FakeSensorReader : ISensorReader
{
    public Dictionary<SensorKey, SensorReading> Readings { get; } = [];

    public bool TryRead(SensorKey key, out SensorReading reading) => Readings.TryGetValue(key, out reading);
}

internal sealed class FakeSensorHistory : ISensorHistory
{
    public Dictionary<SensorKey, double[]> Samples { get; } = [];

    public IReadOnlyList<double> GetSamples(SensorKey key) => Samples.TryGetValue(key, out var samples) ? samples : [];
}

/// <summary>Has no images, so every image item draws the placeholder.</summary>
internal sealed class FakeImageSource : IImageSource
{
    public IRenderImage? Get(string source, bool persistent) => null;
}

internal sealed class FakeAssetLocator(string root) : IAssetLocator
{
    public string GetProfileAssetsDirectory(Guid profileId) => Path.Combine(root, "assets", profileId.ToString());
}

internal static class TestContexts
{
    public static RenderContext Create(ISensorReader? sensors = null, ISensorHistory? history = null) => new()
    {
        Sensors = sensors ?? new FakeSensorReader(),
        History = history ?? new FakeSensorHistory(),
        Images = new FakeImageSource(),
        Assets = new FakeAssetLocator(Path.GetTempPath()),
        State = new ItemStateStore(),
        Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Elapsed = TimeSpan.Zero,
        Options = new RenderOptions(Smoothing: false),
    };
}
