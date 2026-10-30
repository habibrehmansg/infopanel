using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Images.Video;
using InfoPanel.Output;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// Video in the engine (spec/docs/smart-nodes.md, "Video files"): the session lists <c>media.video</c> only with a
/// decoder; without one a video item is unsupported and kept exactly; with one, preview = panel holds for a playing
/// video, the <c>/ws</c> frame byte for byte a direct draw of the loop's tick snapshot.
/// </summary>
public sealed class VideoPreviewTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>FFmpeg's libraries for a test engine: <c>INFOPANEL_FFMPEG_DIR</c>'s, else Homebrew's on a Mac.</summary>
    private static readonly Lazy<string?> FFmpegFolder = new(() =>
        FFmpeg.Load([Environment.GetEnvironmentVariable(FFmpeg.FolderVariable), "/opt/homebrew/lib", "/usr/local/lib"], out _)?.Folder);

    private readonly EngineFactory _base = new();
    private readonly EngineFactory _videoBase = new(); // its own data folder: one engine per folder
    private WebApplicationFactory<Program>? _video;
    private readonly ManualClock _clock = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_video is not null) await _video.DisposeAsync();
        await _base.DisposeAsync();
        await _videoBase.DisposeAsync();
    }

    /// <summary>An engine with a decoder, its clock and render loops driven by hand; the test skips without FFmpeg.</summary>
    private WebApplicationFactory<Program> Video()
    {
        var folder = FFmpegFolder.Value;
        if (folder is null)
            Assert.Skip($"FFmpeg's libraries weren't found: set {FFmpeg.FolderVariable}.");
        return _video ??= _videoBase.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:FFmpegDir", folder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton(new RenderServiceOptions(AutoTick: false));
            });
        });
    }

    private static HttpClient Api(WebApplicationFactory<Program> factory)
    {
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);
        return api;
    }

    private static async Task<string[]> SceneCapabilities(HttpClient api) =>
        [.. (await api.GetFromJsonAsync<JsonElement>("/api/session", Ct)).GetProperty("formatSupport").GetProperty("infopanel.scene/1")
            .GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];

    private static JsonObject VideoScene(string blob) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["requires"] = new JsonArray("media.video"),
        ["nodes"] = new JsonArray(new JsonObject
        {
            ["kind"] = "media", ["w"] = 64, ["h"] = 48, ["source"] = new JsonObject { ["video"] = blob },
        }),
    };

    [Fact]
    public async Task WithADecoder_TheSessionListsMediaVideo_AndWithoutOne_ItDoesnt()
    {
        using var plain = Api(_base);
        Assert.DoesNotContain("media.video", await SceneCapabilities(plain));
        using var video = Api(Video());
        Assert.Contains("media.video", await SceneCapabilities(video));
    }

    [Fact]
    public async Task WithoutADecoder_AVideoItem_IsKeptExactly()
    {
        using var api = Api(_base);
        var scene = VideoScene("sha256:" + new string('b', 64));
        var id = await CreateAsync(api, Doc("Video", 200, 100, Scene("v", 10, 10, scene)), Ct);
        var stored = await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);
        var kept = stored.GetProperty("document").GetProperty("items")[0].GetProperty("scene");
        Assert.True(JsonNode.DeepEquals(scene, JsonNode.Parse(kept.GetRawText())), kept.GetRawText());
        using var thumbnail = await api.GetAsync($"/api/profiles/{id}/thumbnail?w=200", Ct);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
    }

    [Fact]
    public async Task APlayingVideo_ThePreviewFrame_IsByteIdenticalToADirectDraw()
    {
        var factory = Video();
        using var api = Api(factory);
        using var upload = await api.PostAsync("/api/blobs", new ByteArrayContent(VideoFixture.Read("clip-h264.mp4")), Ct);
        var blob = (await upload.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var id = await CreateAsync(api, Doc("Video", 200, 100, Scene("v", 10, 10, VideoScene(blob))), Ct);

        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        var render = factory.Services.GetRequiredService<RenderService>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!render.IsRendering(id) && DateTime.UtcNow < deadline)
            await Task.Delay(5, Ct);

        // 0.35 s in: tick until the decoder, on its own thread, has frame 3 for the loop's snapshot.
        render.Tick(id);
        _clock.Advance(TimeSpan.FromSeconds(0.35));
        long tick = 0;
        while (DateTime.UtcNow < deadline)
        {
            render.Tick(id);
            using var latest = render.LatestSnapshot(id)!;
            using var drawn = latest.Render(1);
            if (VideoColour(drawn.GetPixel(42, 34)) == 3)
            {
                tick = latest.Tick;
                break;
            }
            await Task.Delay(10, Ct);
        }
        Assert.True(tick > 0, "the video's frame never arrived");

        // The socket's frame for that tick against a direct draw of the same snapshot.
        var frame = await client.ReceiveFrameAsync(Ct, h => h.Tick >= tick);
        using var snapshot = render.LatestSnapshot(id)!;
        Assert.Equal(tick, snapshot.Tick);
        using var panel = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(panel))
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, 1);
        }
        var info = new SKImageInfo(200, 100, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var expected = new SKBitmap(info);
        using (var source = panel.PeekPixels())
        using (var target = expected.PeekPixels())
            Assert.True(source.ReadPixels(target));
        using var decoded = SKBitmap.Decode(frame.Png.ToArray(), info);
        Assert.True(expected.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()), "the preview frame differs from the direct draw");
        Assert.Equal(3, VideoColour(decoded.GetPixel(42, 34)));
    }

    /// <summary>The fixtures' frame i is (25 i, 250 - 25 i, 40 or 210): the nearest says which frame was drawn.</summary>
    private static int VideoColour(SKColor c) => Enumerable.Range(0, 10)
        .OrderBy(i => Math.Pow(25 * i - c.Red, 2) + Math.Pow(250 - 25 * i - c.Green, 2) + Math.Pow((i % 2 == 0 ? 40 : 210) - c.Blue, 2))
        .First();

    /// <summary>A clock frozen except when the test advances it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 9, 41, 7, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
