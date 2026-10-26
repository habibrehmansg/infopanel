using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

public sealed class WorkspaceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ProfileStore NewStore() => new(new DataPaths(_temp.Path));

    [Fact]
    public void EmptyFolder_GetsTheExampleProfile()
    {
        var store = NewStore();
        Assert.True(Workspace.EnsureExampleProfile(store));
        Assert.False(Workspace.EnsureExampleProfile(store));

        var workspace = new Workspace(NewStore());
        var summary = Assert.Single(workspace.ListProfiles());
        Assert.Equal("Wide bar", summary.Name);
        Assert.Equal((1920, 480), (summary.Width, summary.Height));
        Assert.Equal(1, summary.Revision);
        Assert.False(summary.Dirty);
        Assert.Equal(["GPU", "Clock", "Game", "System", "Fans"], workspace.Open(summary.Id)!.Items.Select(i => i.Name));
    }

    [Fact]
    public void ExampleProfile_BindsMockSensorsInInter()
    {
        var (profile, items) = ExampleProfiles.WideBar();
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock]);

        var all = ItemTree.Walk(items).ToList();
        var bound = all.Select(SensorKeys.Of).OfType<SensorKey>().ToList();
        Assert.True(bound.Count >= 20);
        Assert.All(bound, key => Assert.Equal(SensorStatus.Live, hub.GetStatus(key)));
        Assert.All(bound, key => Assert.StartsWith(MockSensorCatalog.KeyPrefix, key.Id));
        Assert.Equal(FontDefaults.Family, profile.Font);
        Assert.All(all.OfType<TextDisplayItem>(), t => Assert.Equal(FontDefaults.Family, t.Font));
        Assert.All(all, i => Assert.Same(profile, i.Profile));
        Assert.Equal(20, profile.FontSize); // the seed's 26 px at font scale 1.33
        Assert.True(((GroupDisplayItem)items[4]).Hidden);
    }

    [Fact]
    public void Open_IsLazyAndCached()
    {
        var store = NewStore();
        var profile = new Profile("A", 200, 100);
        store.AddProfile(profile, [new TextDisplayItem("hello", profile)]);
        var workspace = new Workspace(store);

        var doc = workspace.Open(profile.Guid);
        Assert.NotNull(doc);
        Assert.Same(doc, workspace.Open(profile.Guid));
        Assert.Null(workspace.Open(Guid.NewGuid()));

        Assert.Equal(profile.Guid, doc.Id);
        Assert.Equal(1, doc.Revision);
        var item = Assert.Single(doc.Items);
        Assert.Same(doc.Profile, item.Profile);

        var json = doc.ToDoc();
        Assert.Equal(profile.Guid.ToString(), json.Id);
        Assert.Equal(1, json.Revision);
        Assert.False(json.Dirty || json.CanUndo || json.CanRedo);
        Assert.Equal("A", (string?)json.Profile["name"]);
        Assert.Equal("text", Assert.Single(json.Items).Kind);
    }

    [Theory]
    [InlineData(800, 480, 320, 320, 192)]
    [InlineData(800, 480, 1, 16, 10)]
    [InlineData(800, 480, 99999, 4096, 2458)]
    [InlineData(100, 10000, 320, 41, 4096)]
    [InlineData(0, 0, 320, 320, 320)]
    public void ThumbnailSize(int pw, int ph, int requested, int width, int height)
    {
        var (w, h, _) = ThumbnailRenderer.Size(pw, ph, requested);

        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Fact]
    public void Thumbnail_IsAPngAndAdvancesTheTick()
    {
        var (profile, items) = ExampleProfiles.WideBar();
        var doc = new ProfileDocument(profile, items);
        var states = new ProfileRenderStates();
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock]);
        var renderer = new ProfileRenderer(hub, new SensorHistory(hub, TimeProvider.System, hub.GetBackfill),
            new FakeImageSource(), new FakeAssetLocator(_temp.Path), TimeProvider.System);
        var thumbnails = new ThumbnailRenderer(renderer, states);

        var png = thumbnails.RenderPng(doc, 400);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(400, bitmap.Width);
        Assert.Equal(100, bitmap.Height);
        Assert.Equal(1, states.Get(doc.Id).Tick);

        thumbnails.RenderPng(doc, 100);
        Assert.Equal(2, states.Get(doc.Id).Tick);
        thumbnails.RenderPng(doc, 100, advanceTick: false);
        Assert.Equal(2, states.Get(doc.Id).Tick);
    }

    private ThumbnailRenderer NewThumbnails(ISensorReader sensors, ProfileRenderStates? states = null) =>
        new(new ProfileRenderer(sensors, new FakeSensorHistory(), new FakeImageSource(), new FakeAssetLocator(_temp.Path), TimeProvider.System),
            states ?? new ProfileRenderStates());

    private static ProfileDocument BarDocument(out BarDisplayItem bar)
    {
        var profile = new Profile("Bar", 300, 40) { BackgroundColor = "#000000" };
        bar = new BarDisplayItem("Bar", profile) {
            X = 0, Y = 0, Width = 300, Height = 40, Color = "#FFFF0000", BackgroundColor = "#FF00FF00", Gradient = false, Frame = false,
            SensorType = SensorType.Plugin, PluginSensorId = "/test/load",
        };
        return new ProfileDocument(profile, [bar]);
    }

    [Fact]
    public void Thumbnail_DrawsBarsAtTheirValue_WithoutSmoothing()
    {
        var full = new FakeSensorReader();
        full.Readings[SensorKey.Plugin("/test/load")] = new SensorReading(0, 100, 100, 100, "%");
        var empty = new FakeSensorReader();
        empty.Readings[SensorKey.Plugin("/test/load")] = new SensorReading(0, 100, 0, 0, "%");

        var thumbnails = NewThumbnails(full);
        var doc = BarDocument(out _);
        var first = thumbnails.RenderPng(doc, 300);
        var second = thumbnails.RenderPng(doc, 300);
        var atZero = NewThumbnails(empty).RenderPng(BarDocument(out _), 300);

        Assert.Equal(first, second); // no creeping towards the value from one request to the next
        using var fullBitmap = SKBitmap.Decode(first);
        using var zeroBitmap = SKBitmap.Decode(atZero);
        Assert.Equal(new SKColor(0, 255, 0), zeroBitmap.GetPixel(285, 20));
        Assert.Equal(new SKColor(255, 0, 0), fullBitmap.GetPixel(285, 20)); // filled to the end on the first thumbnail
    }

    [Fact]
    public void Rendering_DoesNotChangeTheItems()
    {
        var profile = new Profile("Gauge", 200, 200);
        var gauge = new GaugeDisplayItem("Gauge", profile) { Scale = 50, SensorType = SensorType.Plugin, PluginSensorId = "/test/load" };
        gauge.Images.Add(new ImageDisplayItem("Frame 0", profile) { Scale = 100 });
        gauge.Images.Add(new ImageDisplayItem("Frame 1", profile) { Scale = 100 });
        var (_, exampleItems) = ExampleProfiles.WideBar();
        var doc = new ProfileDocument(profile, [gauge, .. exampleItems]);
        var sensors = new FakeSensorReader();
        sensors.Readings[SensorKey.Plugin("/test/load")] = new SensorReading(0, 100, 70, 70, "%");
        var states = new ProfileRenderStates();
        var thumbnails = NewThumbnails(sensors, states);
        var renderer = new ProfileRenderer(sensors, new FakeSensorHistory(), new FakeImageSource(), new FakeAssetLocator(_temp.Path), TimeProvider.System);

        var before = ProfileSerializer.SnapshotItems(doc.Items);
        thumbnails.RenderPng(doc, 200);
        renderer.MeasureBounds(profile, doc.Items, states.Get(doc.Id));
        thumbnails.RenderPng(doc, 100);

        Assert.Equal(before, ProfileSerializer.SnapshotItems(doc.Items));
        Assert.All(gauge.Images, image => Assert.Equal(100, image.Scale));
    }

    private sealed class ThrowingReader : ISensorReader
    {
        public bool TryRead(SensorKey key, out SensorReading reading) => throw new InvalidOperationException("sensor failure");
    }

    [Fact]
    public void Thumbnail_WhoseItemsFailToDraw_StillRenders_AndReleasesTheDocument()
    {
        var (profile, items) = ExampleProfiles.WideBar();
        var doc = new ProfileDocument(profile, items);
        var thumbnails = NewThumbnails(new ThrowingReader());

        // Every sensor-bound item throws; each is skipped and the rest of the frame is drawn.
        Assert.NotEmpty(thumbnails.RenderPng(doc, 100));
        Assert.NotEmpty(thumbnails.RenderPng(doc, 100));
        Assert.True(Monitor.TryEnter(doc.SyncRoot));
        Monitor.Exit(doc.SyncRoot);
    }
}
