using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

public class SensorHubTests
{
    /// <summary>Handles keys whose id starts with its prefix; reads the fixed value when it has one.</summary>
    private sealed class PrefixSource(string id, string prefix, double? value) : ISensorSource
    {
        public string Id => id;

        public bool Handles(SensorKey key) => key.Id.StartsWith(prefix, StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() => [new(SensorKey.Plugin(prefix + "a"), id, "G", "A", "u")];

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = value is { } v ? new SensorReading(v, v, v, v, id) : default;
            return value != null;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A source whose every key has the status the test sets, with a reading when that is live or stale.</summary>
    private sealed class StatusSource : ISensorSource, ISensorStatusSource
    {
        public SensorStatus Status { get; set; } = SensorStatus.Live;

        public string Id => "s";

        public bool Handles(SensorKey key) => key.Id.StartsWith("/s/", StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() => [];

        public SensorStatus GetStatus(SensorKey key) => Status;

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = new SensorReading(1, 1, 1, 1, "");
            return Status is SensorStatus.Live or SensorStatus.Stale;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly SensorHubOptions Simulating = new() { SimulateUnknown = true };

    private static readonly SensorKey HwInfoKey = SensorKey.HwInfo(-1, 0xF0000300, 0, 7);
    private static readonly SensorKey LibreKey = SensorKey.Libre("/amdcpu/0/load/0");

    [Fact]
    public void Sources_AreTriedInOrder_FirstHandlerAnswers()
    {
        var hub = new SensorHub([new PrefixSource("first", "/x/", 1), new PrefixSource("second", "/x/y/", 2), new PrefixSource("third", "/z/", 3)]);

        Assert.True(hub.TryRead(SensorKey.Plugin("/x/y/1"), out var a));
        Assert.Equal(1, a.ValueNow);
        Assert.True(hub.TryRead(SensorKey.Plugin("/z/1"), out var b));
        Assert.Equal(3, b.ValueNow);
        Assert.Equal(["first", "second", "third"], hub.GetSensors().Select(s => s.SourceId));
    }

    [Fact]
    public void HandledKeyWithoutReading_IsUnavailable_NotSimulated()
    {
        var hub = new SensorHub([new PrefixSource("empty", "/x/", null)], Simulating);
        var key = SensorKey.Plugin("/x/1");

        Assert.False(hub.TryRead(key, out _));
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(key));
    }

    [Fact]
    public void Status_LiveSimulatedUnavailable_InMockMode()
    {
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock], Simulating);

        Assert.Equal(SensorStatus.Live, hub.GetStatus(MockSensorCatalog.KeyOf("gpu.temp")));
        Assert.Equal(SensorStatus.Live, hub.GetStatus(MockSensorCatalog.TopProcessesKey));
        Assert.Equal(SensorStatus.Simulated, hub.GetStatus(HwInfoKey));
        Assert.Equal(SensorStatus.Simulated, hub.GetStatus(LibreKey));
        Assert.Equal(SensorStatus.Simulated, hub.GetStatus(SensorKey.Plugin("/someplugin/sensor")));
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.Plugin("/infopanel.mock/gpu/nope")));
    }

    [Fact]
    public void ByDefault_UnknownKeysAreUnavailable_NotSimulated()
    {
        Assert.False(new SensorHubOptions().SimulateUnknown);
        var hub = new SensorHub([]);

        foreach (var key in new[] { LibreKey, HwInfoKey, SensorKey.Plugin("/someplugin/sensor") })
        {
            Assert.False(hub.TryRead(key, out _));
            Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(key));
        }

        hub.Tick();
        Assert.False(hub.TryRead(LibreKey, out _));
    }

    [Fact]
    public void StatusSource_IsAskedForItsKeys()
    {
        var source = new StatusSource();
        var hub = new SensorHub([source]);

        source.Status = SensorStatus.Stale;
        Assert.Equal(SensorStatus.Stale, hub.GetStatus(SensorKey.Plugin("/s/a")));
        source.Status = SensorStatus.PermissionDenied;
        Assert.Equal(SensorStatus.PermissionDenied, hub.GetStatus(SensorKey.Plugin("/s/a")));
        // A source can't claim a hub-only status for its own keys.
        source.Status = SensorStatus.Simulated;
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.Plugin("/s/a")));
        source.Status = SensorStatus.Missing;
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.Plugin("/s/a")));
        // Malformed keys are missing before any source is asked.
        Assert.Equal(SensorStatus.Missing, hub.GetStatus(SensorKey.Plugin(" ")));
    }

    [Fact]
    public void Statuses_SerializeAsTheApiStrings()
    {
        Assert.Equal(
            "[\"live\",\"simulated\",\"missing\",\"stale\",\"unavailable\",\"permission-denied\"]",
            System.Text.Json.JsonSerializer.Serialize(Enum.GetValues<SensorStatus>()));
    }

    public static TheoryData<SensorKey> MalformedKeys => new()
    {
        default(SensorKey),
        new SensorKey(SensorType.HwInfo, "x"),
        new SensorKey(SensorType.HwInfo, "-1:1:2"),
        new SensorKey(SensorType.HwInfo, "-1:0:0:0"),
        new SensorKey(SensorType.Libre, ""),
        new SensorKey(SensorType.Plugin, " "),
        new SensorKey((SensorType)42, "/a"),
    };

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public void MalformedOrUnboundKeys_AreMissing(SensorKey key)
    {
        var hub = new SensorHub([]);

        Assert.False(hub.TryRead(key, out _));
        Assert.Equal(SensorStatus.Missing, hub.GetStatus(key));
    }

    [Fact]
    public void SimulatedKeys_AreDeterministicPerKey_AndIn0To100()
    {
        var a = new SensorHub([], Simulating);
        var b = new SensorHub([], Simulating);
        var keys = new[] { HwInfoKey, LibreKey, SensorKey.Plugin("/p/1"), SensorKey.HwInfo(0, 1, 2, 3) };

        var seriesA = keys.ToDictionary(k => k, _ => new List<double>());
        var seriesB = keys.ToDictionary(k => k, _ => new List<double>());
        for (var i = 0; i < 500; i++)
        {
            foreach (var key in keys)
            {
                Assert.True(a.TryRead(key, out var ra));
                Assert.True(b.TryRead(key, out var rb));
                seriesA[key].Add(ra.ValueNow);
                seriesB[key].Add(rb.ValueNow);
                Assert.InRange(ra.ValueNow, 0, 100);
                Assert.InRange(ra.ValueMin, 0, ra.ValueNow);
                Assert.InRange(ra.ValueMax, ra.ValueNow, 100);
                Assert.InRange(ra.ValueAvg, ra.ValueMin, ra.ValueMax);
            }

            a.Tick();
            b.Tick();
        }

        foreach (var key in keys)
        {
            Assert.Equal(seriesA[key], seriesB[key]);
            Assert.True(seriesA[key].Distinct().Count() > 100, "the walk moves");
        }

        Assert.NotEqual(seriesA[HwInfoKey], seriesA[LibreKey]);
    }

    [Fact]
    public void SimulatedKey_HoldsItsValueBetweenTicks()
    {
        var hub = new SensorHub([], Simulating);

        Assert.True(hub.TryRead(LibreKey, out var first));
        Assert.True(hub.TryRead(LibreKey, out var second));
        Assert.Equal(first.ValueNow, second.ValueNow);
    }

    [Fact]
    public void SimulatedKeys_AreCapped()
    {
        var hub = new SensorHub([], new SensorHubOptions { SimulateUnknown = true, MaxSimulatedKeys = 2 });

        Assert.True(hub.TryRead(SensorKey.Plugin("/a"), out _));
        Assert.True(hub.TryRead(SensorKey.Plugin("/b"), out _));
        Assert.False(hub.TryRead(SensorKey.Plugin("/c"), out _));
        Assert.Equal(SensorStatus.Unavailable, hub.GetStatus(SensorKey.Plugin("/c")));
        Assert.Equal(SensorStatus.Simulated, hub.GetStatus(SensorKey.Plugin("/a")));
    }

    [Fact]
    public void Backfill_ComesFromTheHandlingSource()
    {
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock]);

        Assert.Equal(MockSensorSource.BackfillSamples, hub.GetBackfill(MockSensorCatalog.KeyOf("cpu.load")).Count);
        Assert.Empty(hub.GetBackfill(LibreKey));
    }

    [Fact]
    public void StableHash_IsFnv1a()
    {
        // FNV-1a test vectors.
        Assert.Equal(0x811C9DC5u, StableHash.Fnv1a(""));
        Assert.Equal(0xE40C292Cu, StableHash.Fnv1a("a"));
        Assert.Equal(0xBF9CF968u, StableHash.Fnv1a("foobar"));
    }
}
