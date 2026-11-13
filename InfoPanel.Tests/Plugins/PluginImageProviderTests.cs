using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.Plugins.PluginPictures;

namespace InfoPanel.Tests.Plugins;

// The plugin media provider (spec/docs/providers.md, "plugin"): an Image draws a plugin's picture through a text input
// bound to its image sensor, by the reference alone, reaching no address; any reference that isn't a plugin's picture
// draws nothing. And each image sensor's entry names its provider (sensorEntry.provider).
public sealed class PluginImageProviderTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Folder = "InfoPanel.Pictures";
    private static readonly SensorKey Cover = SensorKey.Plugin("/pictures/c/cover");
    private static readonly SensorKey NotAPicture = SensorKey.Plugin("/test.texts/reference");

    /// <summary>
    /// A test engine with the now-playing source over a reader that plays nothing, a text sensor whose text looks like a
    /// reference, and its plugins source holding the test plugin's picture (<see cref="Pixel"/>) as a host would send it.
    /// </summary>
    private WebApplicationFactory<Program> Engine()
    {
        var nowPlaying = NowPlayingSource.Create(new SilentReader());
        var texts = new FixedSensorSource("test.texts", (NotAPicture, "Reference", new SensorState(SensorStatus.Live, SensorKind.Text, new SensorReading("0123456789abcdef"))));
        var engine = FixedSensorSource.Engine(factory, nowPlaying, texts);
        var plugins = engine.Services.GetRequiredService<PluginSensorSource>();
        Assert.Empty(plugins.Load(Folder, [new PluginMetadataDto { Id = "pictures", Name = "Pictures" }],
            new Dictionary<string, List<ContainerDto>> { ["pictures"] = [new ContainerDto { Id = "c", Name = "C", Entries = [new EntryDto { Id = "cover", Name = "Cover", Type = "image" }] }] }));
        plugins.SetState(Folder, PluginHostState.Running);
        plugins.Update(Folder, [new SensorUpdateBatchDto { PluginId = "pictures", Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = "cover", Type = "image", ImageValue = Convert.ToBase64String(Pixel) }] }]);
        return engine;
    }

    [Fact]
    public async Task Each_image_sensors_entry_names_its_provider_now_playing_for_the_cover_plugin_for_a_plugins_picture()
    {
        using var engine = Engine();
        using var client = FixedSensorSource.Client(engine);

        var session = JsonDocument.Parse(await client.GetStringAsync("/api/session", Ct)).RootElement;
        Assert.Contains("plugin", session.GetProperty("media").GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        var listed = JsonDocument.Parse(await client.GetStringAsync("/api/sensors", Ct)).RootElement.EnumerateArray()
            .ToDictionary(s => s.GetProperty("key").GetString()!);
        foreach (var key in new[] { Cover.ToString(), NowPlayingSource.KeyOf("art").ToString(), NowPlayingSource.KeyOf("title").ToString() })
            Assert.True(ReadEndpointTests.Valid(listed[key], "sensorEntry"), key);

        Assert.Equal(("image", "plugin"), (listed[Cover.ToString()].GetProperty("type").GetString(), listed[Cover.ToString()].GetProperty("provider").GetString()));
        var art = listed[NowPlayingSource.KeyOf("art").ToString()];
        Assert.Equal(("image", "now-playing"), (art.GetProperty("type").GetString(), art.GetProperty("provider").GetString()));
        // Any other type names none.
        Assert.False(listed[NowPlayingSource.KeyOf("title").ToString()].TryGetProperty("provider", out _));
        Assert.False(listed[NotAPicture.ToString()].TryGetProperty("provider", out _));
    }

    // The provider-media example's scene with the plugin provider: a 96 × 96 media node whose reference is the input art's
    // text. The test plugin's picture is one half-transparent blue pixel, drawn over white.
    [Fact]
    public async Task An_Image_draws_a_plugins_picture_through_a_text_input_bound_to_its_image_sensor_and_nothing_for_another_reference()
    {
        using var engine = Engine();
        var options = engine.Services.GetRequiredService<SceneRuntimeOptions>();

        var drawn = await Drawn(options, Cover);
        Assert.Null(drawn.Media);
        Assert.InRange(drawn.Pixel.Red, 120, 136);
        Assert.InRange(drawn.Pixel.Green, 120, 136);
        Assert.InRange(drawn.Pixel.Blue, 250, 255);

        // A text that isn't a plugin's picture's reference: no picture, nothing drawn (not the placeholder).
        var other = await Drawn(options, NotAPicture);
        Assert.Equal(("media.provider", "No picture."), (other.Media?.Code, other.Media?.Message));
        Assert.Equal(SKColors.White, other.Pixel);
    }

    // A picture the plugins source lets go of leaves the provider's store at its next sweep, within a second.
    [Fact]
    public void A_picture_the_plugins_source_lets_go_of_leaves_the_providers_store_within_a_second()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var source = new PluginSensorSource(time: time);
        Assert.Empty(source.Load(Folder, [new PluginMetadataDto { Id = "pictures", Name = "Pictures" }],
            new Dictionary<string, List<ContainerDto>> { ["pictures"] = [new ContainerDto { Id = "c", Name = "C", Entries = [new EntryDto { Id = "cover", Name = "Cover", Type = "image" }] }] }));
        source.SetState(Folder, PluginHostState.Running);
        var store = new FetchedMediaStore();
        using var cache = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true });
        using var provider = new PluginImageProvider(source, cache, store, time);
        var (a, b, c) = (Solid(SKColors.Red), Solid(SKColors.Green), Solid(SKColors.Blue));
        Send(source, a);
        var request = new MediaRequest(ReferenceOf(a), 0, MediaPlayback.Loop);

        var answer = provider.Get(request);
        answer.Sample?.Release();
        Assert.Null(answer.Status);
        var key = "fetched:plugin:sha256:" + ReferenceOf(a);
        Assert.True(store.Contains(key));

        // Kept as the previous after b, let go of after c.
        Send(source, b);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(store.Contains(key));
        Send(source, c);
        Assert.False(source.Keeps(ReferenceOf(a), out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(store.Contains(key));
        Assert.Equal("No picture.", provider.Get(request).Status?.Message);
    }

    /// <summary>A 4 × 4 PNG of one colour, which decodes.</summary>
    private static byte[] Solid(SKColor color)
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Send(PluginSensorSource source, byte[] picture) =>
        source.Update(Folder, [new SensorUpdateBatchDto { PluginId = "pictures", Updates = [new EntryUpdateDto { ContainerId = "c", EntryId = "cover", Type = "image", ImageValue = Convert.ToBase64String(picture) }] }]);

    /// <summary>
    /// The scene drawn once a tick (a one-off render) with its input <c>art</c> bound to <paramref name="key"/>, until its
    /// picture is decoded or it says it has none: the item's centre and its media problem.
    /// </summary>
    private static async Task<(SKColor Pixel, ItemMedia? Media)> Drawn(SceneRuntimeOptions options, SensorKey key)
    {
        var document = Document(key);
        (SKColor, ItemMedia?) result = default;
        await Eventually.Until(() =>
        {
            using var snapshot = SceneRuntime.RenderOnce(document, DateTimeOffset.UtcNow, options);
            using var frame = snapshot.Render();
            result = (frame.GetPixel(58, 58), snapshot.Bounds.Single().Media);
            return result.Item2?.Code != "media.loading";
        }, "the picture is decoded, or there is none");
        return result;
    }

    /// <summary>A 116 × 116 white document with the plugin provider's scene at (10, 10), its input <c>art</c> bound to <paramref name="key"/>.</summary>
    private static JsonElement Document(SensorKey key)
    {
        var scene = new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["requires"] = new JsonArray("media.provider"),
            ["inputs"] = new JsonObject { ["art"] = new JsonObject { ["type"] = "text", ["label"] = "Picture" } },
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["kind"] = "media",
                ["w"] = 96,
                ["h"] = 96,
                ["fit"] = "cover",
                ["source"] = new JsonObject { ["provider"] = PluginImageProvider.Name, ["ref"] = new JsonObject { ["op"] = "input", ["name"] = "art", ["field"] = "text" } },
            }),
        };
        var document = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = "00000000-0000-4000-8000-000000000001",
            ["profile"] = new JsonObject { ["name"] = "Picture", ["width"] = 116, ["height"] = 116, ["background"] = "#ffffffff" },
            ["items"] = new JsonArray(new JsonObject
            {
                ["kind"] = "scene",
                ["id"] = "item",
                ["placement"] = new JsonObject { ["x"] = 10, ["y"] = 10, ["rotation"] = 0 },
                ["scene"] = scene,
                ["bindings"] = new JsonObject { ["art"] = new JsonObject { ["key"] = key.ToString() } },
            }),
        };
        return JsonSerializer.SerializeToElement(document);
    }

    /// <summary>A now-playing reader that plays nothing.</summary>
    private sealed class SilentReader : INowPlayingReader
    {
        public SourceState? State => null;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public bool TryRead(out NowPlayingSnapshot? snapshot)
        {
            snapshot = null;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
