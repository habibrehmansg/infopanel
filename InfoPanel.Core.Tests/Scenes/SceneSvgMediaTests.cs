using System.Text;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The media path decodes by the probed format, and an SVG only from the document the validator rebuilt from it
/// (spec/docs/svg.md): the stored bytes never reach the SVG decoder.
/// </summary>
public sealed class SceneSvgMediaTests
{
    private static readonly string Original = new string('a', 64);
    private static readonly string Rebuilt = new string('b', 64);
    private static readonly string Absent = new string('c', 64);

    // Comments, a `style` attribute, a `class`, a `title`, odd whitespace and colours in other spellings: all dropped or
    // normalised by the rebuild.
    private const string Messy = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- a comment the rebuild drops -->
        <svg xmlns="http://www.w3.org/2000/svg"   width="40"
             height="40"   viewBox="0 0 40 40">
          <title>  an icon  </title>
          <!-- another comment -->
          <rect x="0" y="0" width="20.000" height="40" class="left" style="fill: rgb( 255 , 0 , 0 ) ; fill-opacity : 0.5"/>
          <rect x="20" y="0" width="20" height="40" fill="hsl(120, 100%, 25%)"/>
          <circle cx="20" cy="20" r="10" fill="#0Af" stroke="Navy" stroke-width="2.50"/>
          <path d="M 0,0   L 40 , 40" stroke="rgba(255,255,0,0.5)" stroke-width="1.0" fill="none"/>
        </svg>
        """;

    private static System.Text.Json.JsonElement Scene(string hash) => SceneKit.Doc(
        SceneKit.Json($"{{'format':'infopanel.scene/1','nodes':[{{'kind':'media','w':40,'h':40,'source':{{'asset':'sha256:{hash}'}}}}]}}"), 40, 40);

    private static (MediaSampleCache Media, Dictionary<string, byte[]> Decoded) Cache(MemoryAssets store)
    {
        var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true });
        var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        media.DecodingSvg = (hash, bytes) => decoded[hash] = bytes.ToArray();
        return (media, decoded);
    }

    [Fact]
    public void An_svg_renders_as_its_rebuilt_form_and_only_the_rebuilt_bytes_reach_the_decoder()
    {
        var original = Encoding.UTF8.GetBytes(Messy);
        var check = MediaValidator.Check(original);
        Assert.Empty(check.Errors);
        Assert.Equal(MediaFormat.Svg, check.Format);
        var rebuilt = Assert.IsType<byte[]>(check.Sanitized);
        var text = Encoding.UTF8.GetString(rebuilt);
        Assert.DoesNotContain("<!--", text);
        Assert.DoesNotContain("style=", text);
        Assert.DoesNotContain("hsl(", text);
        Assert.DoesNotContain("Navy", text);

        var store = new MemoryAssets();
        store.Assets["sha256:" + Original] = original;
        store.Assets["sha256:" + Rebuilt] = rebuilt;
        var (media, decoded) = Cache(store);
        using (media)
        {
            using var fromOriginal = SceneKit.Render(Scene(Original), SceneKit.Options(media: media));
            using var fromRebuilt = SceneKit.Render(Scene(Rebuilt), SceneKit.Options(media: media));
            Assert.Equal(0, SceneKit.DifferingPixels(fromOriginal, fromRebuilt, tolerance: 0));
            // Something was drawn: the left half is the half-transparent red over the background.
            var left = fromOriginal.GetPixel(4, 30);
            Assert.True(left.Red > 100 && left.Green < 60, left.ToString());

            // The decoder got the rebuilt document for the stored original, never the original itself.
            Assert.Equal(rebuilt, decoded["sha256:" + Original]);
            Assert.NotEqual(original, decoded["sha256:" + Original]);
            // Rebuilding is idempotent, so the asset stored in its rebuilt form decodes the same bytes.
            Assert.Equal(rebuilt, decoded["sha256:" + Rebuilt]);
            // The parse counted the bytes it decoded and gave them back; what stays is the recorded pictures.
            Assert.True(media.PeakBytes >= rebuilt.Length, $"{media.PeakBytes} bytes at the peak");
            Assert.True(media.DecoderCount == 0, $"{media.DecoderCount} decoders open");
        }
    }

    [Fact]
    public void A_gif_with_svg_text_in_its_header_is_decoded_as_a_gif()
    {
        var gif = TestGif.Build(4, 4, new TestGif.Frame(0, 0, 4, 4, 1));
        // Palette entries 5 and 6 are unused (13-byte header, 3 bytes per entry): put "<svg/>" there.
        "<svg/>"u8.CopyTo(gif.AsSpan(13 + 5 * 3));
        Assert.True(gif.AsSpan(0, 40).IndexOf("<svg"u8) >= 0);
        Assert.Equal(MediaFormat.Gif, MediaValidator.Check(gif).Format);

        var store = new MemoryAssets();
        store.Assets["gif"] = gif;
        var (media, decoded) = Cache(store);
        using (media)
        {
            var lookup = media.Get("gif", 0, MediaPlayback.Loop);
            Assert.Equal(MediaState.Ready, lookup.State);
            var sample = Assert.IsType<MediaSample>(lookup.Sample);
            try
            {
                Assert.NotNull(sample.Image);
                using var bmp = SKBitmap.FromImage(sample.Image!);
                Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(1, 1));
            }
            finally
            {
                sample.Release();
            }
            Assert.Empty(decoded);
        }
    }

    [Fact]
    public void Refused_svg_media_draws_the_missing_media_placeholder()
    {
        // Inside the old screening rules, outside the profile: a bare URI as a mask, and a dash pattern.
        var refused = Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <rect width="40" height="40" fill="#ff0000" mask="http://example.invalid/m.svg"/>
              <line x1="0" y1="0" x2="40" y2="40" stroke="#00ff00" stroke-dasharray="1 1"/>
            </svg>
            """);
        var check = MediaValidator.Check(refused);
        Assert.NotEmpty(check.Errors);
        Assert.Null(check.Sanitized);

        var store = new MemoryAssets();
        store.Assets["sha256:" + Original] = refused;
        var (media, decoded) = Cache(store);
        using (media)
        {
            using var snapshot = SceneRuntime.RenderOnce(Scene(Original), SceneKit.Now, SceneKit.Options(media: media));
            Assert.Contains(snapshot.Diagnostics, d => d.Code == "media.missing");
            Assert.Equal(0, snapshot.SampleCount);
            using var drawn = snapshot.Render();
            using var missing = SceneKit.Render(Scene(Absent), SceneKit.Options(media: media));
            Assert.Equal(0, SceneKit.DifferingPixels(drawn, missing, tolerance: 0));
            Assert.Empty(decoded);
            Assert.Equal(0, media.Bytes);
        }
    }
}
