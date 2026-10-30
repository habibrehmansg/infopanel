using System.Security.Cryptography;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Media with a <c>{ "video" }</c> source in a scene (spec/docs/smart-nodes.md, "Video files"): the frame drawn at the
/// tick clock times <c>speed</c>, the video's own size as its natural size, the placeholder while it is missing, and an
/// item that is unsupported without <c>media.video</c>.
/// </summary>
public sealed class SceneVideoTests
{
    private static readonly string Clip = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(VideoFixtures.Read("clip-h264.mp4")));

    private sealed class Videos : IMediaAssetStore
    {
        public byte[]? Read(string hash) => hash == Clip ? VideoFixtures.Read("clip-h264.mp4") : null;
    }

    private static string Scene(string extra = "", string? source = null) => $$$"""
        {'format': 'infopanel.scene/1', 'requires': ['media.video'], 'nodes': [
          {'kind': 'media', 'x': 10, 'y': 10, {{{extra}}} 'source': {'video': '{{{source ?? Clip}}}'}}]}
        """;

    private static (SKBitmap Bitmap, IReadOnlyList<SceneDiagnostic> Diagnostics, DocumentCheck Check) Tick(string scene, double elapsed,
        EngineSupport? support = null)
    {
        using var media = SceneKit.SyncMedia(new Videos(), FFmpegForTests.Library);
        var options = SceneKit.Options(media: media);
        options = new SceneRuntimeOptions
        {
            Inputs = options.Inputs, Media = media, LocalZone = options.LocalZone, Locale = options.Locale,
            Support = support ?? EngineSupport.All,
        };
        using var runtime = new SceneRuntime(options);
        var check = runtime.Load(SceneKit.Doc(SceneKit.Json(scene), 120, 80, background: "#ffffffff"));
        runtime.Tick(0, SceneKit.Now).Dispose();
        using var snapshot = runtime.Tick(elapsed, SceneKit.Now);
        return (snapshot.Render(1), snapshot.Diagnostics, check);
    }

    private static int FrameAt(SKBitmap bitmap, int x, int y) => VideoFixtures.FrameOf(bitmap.GetPixel(x, y));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.35, 3)]
    [InlineData(0.95, 9)]
    [InlineData(1.25, 2)]
    public void TheFrameDrawn_FollowsTheTickClock(double elapsed, int frame)
    {
        var (bitmap, _, _) = Tick(Scene(), elapsed);
        using (bitmap) Assert.Equal(frame, FrameAt(bitmap, 40, 30));
    }

    [Fact]
    public void Speed_ScalesTheClock()
    {
        var (bitmap, _, _) = Tick(Scene("'speed': 2,"), 0.2);
        using (bitmap) Assert.Equal(4, FrameAt(bitmap, 40, 30));
    }

    [Fact]
    public void Once_HoldsTheLastFrame()
    {
        var (bitmap, _, _) = Tick(Scene("'playback': 'once',"), 3);
        using (bitmap) Assert.Equal(9, FrameAt(bitmap, 40, 30));
    }

    [Fact]
    public void TheNaturalSize_IsTheVideosOwn()
    {
        var (bitmap, _, _) = Tick(Scene(), 0.35);
        using (bitmap)
        {
            // Drawn at 64 x 48 from (10, 10): inside it the frame's colour, past it the white background.
            Assert.Equal(3, FrameAt(bitmap, 72, 56));
            Assert.Equal(SKColors.White, bitmap.GetPixel(76, 30));
            Assert.Equal(SKColors.White, bitmap.GetPixel(40, 60));
        }
    }

    [Fact]
    public void AVideoDrawnSmall_IsDecodedSmall_AndStillFillsItsBox()
    {
        var (bitmap, _, _) = Tick(Scene("'w': 32, 'h': 24,"), 0.35);
        using (bitmap)
        {
            Assert.Equal(3, FrameAt(bitmap, 25, 22));
            Assert.Equal(SKColors.White, bitmap.GetPixel(44, 22));
        }
    }

    [Fact]
    public void AVideoNotStored_DrawsTheMissingMediaPlaceholder()
    {
        var (bitmap, diagnostics, _) = Tick(Scene(source: "sha256:" + new string('a', 64)), 0.35);
        bitmap.Dispose();
        Assert.Contains(diagnostics, d => d.Code == "media.missing");
    }

    [Fact]
    public void WithoutTheCapability_TheItemIsUnsupported()
    {
        var (bitmap, _, check) = Tick(Scene(), 0.35, EngineSupport.Base);
        bitmap.Dispose();
        Assert.Equal("unsupported", Assert.Single(check.Items).Problem);
    }
}
