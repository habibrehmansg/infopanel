using InfoPanel.Drawing;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Sensors;

namespace InfoPanel.Core.Tests;

internal sealed class FakeSensorReader : ISensorReader
{
    public Dictionary<SensorKey, SensorReading> Readings { get; } = [];

    /// <summary>Called after every read (tests use it to change a value between two reads).</summary>
    public Action<SensorKey>? AfterRead { get; set; }

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var found = Readings.TryGetValue(key, out reading);
        AfterRead?.Invoke(key);
        return found;
    }
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

/// <summary>A clock in a UTC local zone that moves only when <see cref="Advance"/> is called.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        _utcNow += by;
        _timestamp += by.Ticks;
    }
}
