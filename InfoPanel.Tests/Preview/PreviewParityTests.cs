using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// Preview = panel, on the example document (a 2.0 document), with a clock that moves only when the test moves it,
/// constant sensors and render loops driven by hand. What these tests prove, and no more:
/// <list type="bullet">
/// <item><see cref="TheScale1Frame_IsByteIdenticalToADirectDraw"/>: the scale-1 frame decoded from <c>/ws</c> has
/// exactly the pixels a separate <see cref="InfoPanel.Scenes.TickSnapshot.Draw"/> of the loop's tick snapshot into a
/// fresh <see cref="SKBitmap"/> produces, and its bounds are the snapshot's. So the loop and the PNG round trip add
/// nothing to the runtime's drawing, compared unpremultiplied (the form PNG stores).</item>
/// <item><see cref="APanelSinkAndAPreviewSocket_OnOneLoop_GetIdenticalPixelsForTheSameFrame"/>: a panel-style
/// <see cref="IFrameSink"/> at scale 1 and a <c>/ws</c> subscriber at scale 1 on the same loop receive the same
/// <see cref="RenderedFrame"/> (same <c>FrameId</c>), and the pixels the panel sink copies during <c>OnFrame</c> equal
/// the decoded <c>/ws</c> pixels byte for byte. A real panel driver's own conversion (colour format, rotation) comes
/// later and is not covered.</item>
/// </list>
/// </summary>
public sealed class PreviewParityTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 26, 9, 41, 7, TimeSpan.Zero));
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<ISensorReader>(new ConstantSensors());
            services.AddSingleton<ISensorHistory>(new ConstantHistory());
            services.AddSingleton(new RenderServiceOptions(AutoTick: false));
        }));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _base.DisposeAsync();
    }

    private RenderService Render => _factory.Services.GetRequiredService<RenderService>();

    private async Task<string> ExampleProfileId()
    {
        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        return profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetString()!;
    }

    private async Task<PreviewClient> Subscribe(string id, float scale)
    {
        var client = await PreviewClient.ConnectAsync(_factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale, maxFps = 30 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!Render.IsRendering(id))
            await Task.Delay(5, timeout.Token);
        return client;
    }

    [Fact]
    public async Task TheScale1Frame_IsByteIdenticalToADirectDraw()
    {
        var id = await ExampleProfileId();
        await using var client = await Subscribe(id, 1);

        // Two ticks a second apart, so the clock shows another time and animated scenes have moved on.
        Assert.Equal(1, Render.Tick(id));
        await client.ReceiveFrameAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Render.Tick(id));
        var (header, png) = await client.ReceiveFrameAsync(Ct);
        Assert.Equal((1.0, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));

        // A direct draw of the loop's tick snapshot (no tick runs in between: ticks are manual).
        using var snapshot = Render.LatestSnapshot(id)!;
        Assert.Equal((2L, header.Tick, header.Rev), (snapshot.Tick, snapshot.Tick, snapshot.Rev));
        using var panel = new SKBitmap(new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(panel))
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, 1);
        }

        // PNG stores unpremultiplied pixels, so compare both sides unpremultiplied (identical to premultiplied for opaque pixels).
        var expected = Unpremultiplied(panel);
        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var actual = decoded.GetPixelSpan().ToArray();

        Assert.Equal(expected.Length, actual.Length);
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 1920}, {firstDifference / 4 / 1920})");

        // The frame's bounds are the snapshot's.
        Assert.Equal(JsonSerializer.Serialize(snapshot.Bounds, SpecJson.Options), JsonSerializer.Serialize(header.ItemBounds, SpecJson.Options));
    }

    [Fact]
    public async Task TheScale2Frame_WithARaster_IsByteIdenticalToADirectDraw()
    {
        // A one-pixel checkerboard at a sub-pixel place: above scale 1 raster media shows the canvas's pixels enlarged,
        // on the socket's frame as on a direct draw.
        using var checker = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                checker.SetPixel(x, y, (x + y) % 2 == 0 ? new SKColor(255, 64, 0) : new SKColor(0, 64, 255));
        using var image = SKImage.FromBitmap(checker);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        using var upload = await api.PostAsync("/api/blobs", new ByteArrayContent(encoded.ToArray()), Ct);
        var blob = (await upload.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var scene = new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "media", ["x"] = 10.3, ["y"] = 6.6, ["source"] = new JsonObject { ["asset"] = blob } }),
        };
        var id = await TestDocs.CreateAsync(api, TestDocs.Doc("Raster", 200, 100, TestDocs.Scene("r", 20, 10, scene)), Ct);
        await using var client = await Subscribe(id, 2);

        // Tick until the media cache has decoded the picture for the loop's snapshot (canvas pixel (40, 20) is in it),
        // moving the clock on so each tick looks the picture up again.
        long tick = 0;
        var seen = SKColors.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            Render.Tick(id);
            using var latest = Render.LatestSnapshot(id)!;
            using var drawn = latest.Render(1);
            seen = drawn.GetPixel(40, 20);
            if (seen.Green == 64)
            {
                tick = latest.Tick;
                break;
            }
            await Task.Delay(10, Ct);
        }
        Assert.True(tick > 0, $"the picture never arrived: canvas pixel (40, 20) is {seen}");
        var (header, png) = await client.ReceiveFrameAsync(Ct, h => h.Tick >= tick);
        Assert.Equal((2.0, 400, 200), (header.Scale, header.PixelWidth, header.PixelHeight));

        using var snapshot = Render.LatestSnapshot(id)!;
        Assert.Equal(header.Tick, snapshot.Tick);
        using var panel = new SKBitmap(new SKImageInfo(400, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(panel))
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, 2);
        }
        var expected = Unpremultiplied(panel);
        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(400, 200, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var actual = decoded.GetPixelSpan().ToArray();
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 400}, {firstDifference / 4 / 400})");

        // Canvas pixels (40, 20) and (41, 20), inside the picture, are each a 2 × 2 block of one colour on the frame.
        foreach (var (x, y) in new[] { (40, 20), (41, 20) })
        {
            var block = decoded.GetPixel(2 * x, 2 * y);
            Assert.True(block.Green == 64 && block == decoded.GetPixel(2 * x + 1, 2 * y) && block == decoded.GetPixel(2 * x, 2 * y + 1)
                && block == decoded.GetPixel(2 * x + 1, 2 * y + 1), $"canvas pixel ({x}, {y}) isn't a 2 × 2 block of one colour");
        }
        Assert.NotEqual(decoded.GetPixel(80, 40), decoded.GetPixel(82, 40));
    }

    [Fact]
    public async Task APanelSinkAndAPreviewSocket_OnOneLoop_GetIdenticalPixelsForTheSameFrame()
    {
        var id = await ExampleProfileId();
        await using var client = await Subscribe(id, 1);
        var panel = new PanelSink(id);
        Assert.True(Render.Attach(panel));

        // Two ticks a second apart, so the second frame differs from the first; both sinks get both frames.
        Assert.Equal(2, Render.Tick(id));
        await client.ReceiveFrameAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Render.Tick(id));
        var (header, png) = await client.ReceiveFrameAsync(Ct);
        Render.Detach(panel);

        var (frameId, expected) = panel.Last!;
        Assert.Equal(frameId, header.FrameId);
        Assert.Equal((1.0, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));
        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var actual = decoded.GetPixelSpan().ToArray();

        Assert.Equal(expected.Length, actual.Length);
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 1920}, {firstDifference / 4 / 1920})");
    }

    [Fact]
    public async Task AThumbnail_WhilePreviewing_IsTheLoopsTick_WithoutAdvancingIt()
    {
        var id = await ExampleProfileId();
        await using var client = await Subscribe(id, 1);
        Render.Tick(id);
        var (_, png) = await client.ReceiveFrameAsync(Ct);
        long Tick()
        {
            using var latest = Render.LatestSnapshot(id)!;
            return latest.Tick;
        }
        var tick = Tick();

        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        using var thumbnail = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=1920", Ct);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal(tick, Tick());

        // At the profile's width, the thumbnail is the frame.
        var info = new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var frame = SKBitmap.Decode(png.ToArray(), info);
        using var drawn = SKBitmap.Decode(await thumbnail.Content.ReadAsByteArrayAsync(Ct), info);
        Assert.True(frame.GetPixelSpan().SequenceEqual(drawn.GetPixelSpan()));

        await client.SendAsync(new { type = "unsubscribe" }, Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (Render.IsRendering(id))
            await Task.Delay(5, timeout.Token);

        // Nothing previews it: a one-off snapshot.
        using var again = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=320", Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(Render.LatestSnapshot(id));
    }

    private static byte[] Unpremultiplied(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var converted = new SKBitmap(info);
        using var source = bitmap.PeekPixels();
        using var target = converted.PeekPixels();
        Assert.True(source.ReadPixels(target));
        return converted.GetPixelSpan().ToArray();
    }

    /// <summary>A panel-style sink at scale 1: copies each frame's pixels during <c>OnFrame</c>, unpremultiplied (as PNG stores them).</summary>
    private sealed class PanelSink(string profileId) : IFrameSink
    {
        private Copy? _last;

        public string ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(1, 30, IncludeBounds: false);

        public Copy? Last => Volatile.Read(ref _last);

        public void OnFrame(RenderedFrame frame)
        {
            using var source = frame.Image.PeekPixels();
            using var converted = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using var target = converted.PeekPixels();
            Assert.True(source.ReadPixels(target));
            Volatile.Write(ref _last, new Copy(frame.FrameId, converted.GetPixelSpan().ToArray()));
        }

        public sealed record Copy(long FrameId, byte[] Pixels);
    }

    /// <summary>A clock frozen except when the test advances it (timers still run on real time, from the base class).</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref _ticks));

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>Every key reads 62.5 of 0–100 (text and tables have no reading).</summary>
    private sealed class ConstantSensors : ISensorReader
    {
        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = new SensorReading(0, 100, 62.5, 62.5, "%");
            return true;
        }
    }

    /// <summary>A fixed ramp for every key.</summary>
    private sealed class ConstantHistory : ISensorHistory
    {
        private static readonly double[] Samples = [.. Enumerable.Range(0, 60).Select(i => 20 + i % 15 * 4.0)];

        public IReadOnlyList<double> GetSamples(SensorKey key) => Samples;
    }
}
