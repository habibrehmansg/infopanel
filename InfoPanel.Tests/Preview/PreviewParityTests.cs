using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Output;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// Preview = panel, with a clock that moves only when the test moves it, constant sensors and render loops driven by
/// hand. What these tests prove, and no more:
/// <list type="bullet">
/// <item><see cref="TheScale1Frame_IsByteIdenticalToADirectDraw"/>: the scale-1 frame decoded from <c>/ws</c> has
/// exactly the pixels a separate <see cref="ProfileRenderer.Draw"/> into a fresh <see cref="SKBitmap"/> produces for
/// the same document, render state, tick and <see cref="RenderOptions"/>. So the loop and the PNG round trip add
/// nothing to <c>Draw</c>, compared unpremultiplied (the form PNG stores).</item>
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

    private async Task<Guid> ExampleProfileId()
    {
        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        return profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetGuid();
    }

    private async Task<PreviewClient> Subscribe(Guid id, float scale)
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

        // Two ticks a frame apart, so smoothed bars and donuts are mid-animation, not at their first value.
        Assert.Equal(1, Render.Tick(id));
        await client.ReceiveFrameAsync(Ct);
        _clock.Advance(TimeSpan.FromMilliseconds(34));
        Assert.Equal(1, Render.Tick(id));
        var (header, png) = await client.ReceiveFrameAsync(Ct);
        Assert.Equal((1f, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));

        // A direct Draw, at the same tick with the same state and options (no tick runs in between: ticks are manual).
        var workspace = _factory.Services.GetRequiredService<Workspace>();
        var renderer = _factory.Services.GetRequiredService<ProfileRenderer>();
        var doc = workspace.Open(id)!;
        var state = Render.GetState(id);
        Assert.Equal(2, state.Tick);
        using var panel = new SKBitmap(new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        lock (doc.SyncRoot)
        {
            using var canvas = new SKCanvas(panel);
            canvas.Clear(SKColors.Transparent);
            renderer.Draw(canvas, doc.Profile, doc.Items, state, Render.Options, 1);
        }

        // PNG stores unpremultiplied pixels, so compare both sides unpremultiplied (identical to premultiplied for opaque pixels).
        var expected = Unpremultiplied(panel);
        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var actual = decoded.GetPixelSpan().ToArray();

        Assert.Equal(expected.Length, actual.Length);
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 1920}, {firstDifference / 4 / 1920})");

        // The frame's bounds are the renderer's.
        lock (doc.SyncRoot)
            Assert.Equal(renderer.MeasureBounds(doc.Profile, doc.Items, state), header.Bounds);
    }

    [Fact]
    public async Task APanelSinkAndAPreviewSocket_OnOneLoop_GetIdenticalPixelsForTheSameFrame()
    {
        var id = await ExampleProfileId();
        await using var client = await Subscribe(id, 1);
        var panel = new PanelSink(id);
        Assert.True(Render.Attach(panel));

        // Two ticks a frame apart, so smoothed bars and donuts are mid-animation; both sinks get both frames.
        Assert.Equal(2, Render.Tick(id));
        await client.ReceiveFrameAsync(Ct);
        _clock.Advance(TimeSpan.FromMilliseconds(34));
        Assert.Equal(2, Render.Tick(id));
        var (header, png) = await client.ReceiveFrameAsync(Ct);
        Render.Detach(panel);

        var (frameId, expected) = panel.Last!;
        Assert.Equal(frameId, header.FrameId);
        Assert.Equal((1f, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight));
        using var decoded = SKBitmap.Decode(png.ToArray(), new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var actual = decoded.GetPixelSpan().ToArray();

        Assert.Equal(expected.Length, actual.Length);
        var firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == expected.Length,
            $"pixels differ from byte {firstDifference} (pixel {firstDifference / 4 % 1920}, {firstDifference / 4 / 1920})");
    }

    [Fact]
    public async Task AThumbnail_WhilePreviewing_RendersAtTheLoopsTick()
    {
        var id = await ExampleProfileId();
        await using var client = await Subscribe(id, 1);
        Render.Tick(id);
        await client.ReceiveFrameAsync(Ct);
        var state = Render.GetState(id);
        var tick = state.Tick;

        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        using var thumbnail = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=320", Ct);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal(tick, state.Tick);

        await client.SendAsync(new { type = "unsubscribe" }, Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (Render.IsRendering(id))
            await Task.Delay(5, timeout.Token);

        using var again = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=320", Ct);
        Assert.Equal(tick + 1, state.Tick); // nothing previews it: the thumbnail advances the tick
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
    private sealed class PanelSink(Guid profileId) : IFrameSink
    {
        private Copy? _last;

        public Guid ProfileId { get; } = profileId;

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
