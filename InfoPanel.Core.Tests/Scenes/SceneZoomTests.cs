using System.Security.Cryptography;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Output;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Frames above scale 1 (spec/docs/api.md, "Frame size"): media drawn from a raster picture shows the canvas's own
/// pixels, drawn as at scale 1 and enlarged, so frame pixel (i, j) shows canvas pixel (⌊(i + ½) ÷ s⌋, ⌊(j + ½) ÷ s⌋)
/// (a centre on an edge shows the pixel right of or below it); everything else is drawn at the scale. The reference
/// is the panel's frame, the same snapshot at scale 1. Opaque pixels must equal it exactly; edges and translucent
/// pixels may be one 8-bit rounding away (2 levels, as the goldens).
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneZoomTests
{
    private const int W = 240, H = 140;

    public static TheoryData<float> Scales() => new(1.25f, 1.5f, 2f, 2.5f, 3f, 4f);

    // ---- assets -------------------------------------------------------------------------------------------------

    /// <summary>The spec's example media, and PNGs a test makes, by content hash.</summary>
    private sealed class Assets
    {
        private readonly Dictionary<string, byte[]> _made = new(StringComparer.Ordinal);
        public readonly FolderAssets Store = FolderAssets.Examples();

        public Assets() => Store.Override = hash => _made.TryGetValue(hash, out var bytes) ? bytes : null;

        public string Add(SKBitmap bitmap)
        {
            var png = GoldenImage.EncodePng(bitmap);
            var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(png));
            _made[hash] = png;
            return hash;
        }

        public string Example(string file) => Store.Hash(file);
    }

    private static SKBitmap Image(int w, int h, Func<int, int, SKColor> pixel)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bitmap.SetPixel(x, y, pixel(x, y));
        return bitmap;
    }

    /// <summary>An opaque picture with hard pixels: each neighbour differs, so a pixel shown from the wrong place shows.</summary>
    private static SKBitmap Photo(int w, int h) => Image(w, h, (x, y) =>
        new SKColor((byte)((x * 37 + y * 11) % 256), (byte)((x * 13 + y * 29 + 64) % 256), (byte)((x * x + y * 7 + 128) % 256)));

    /// <summary>A picture with every alpha from transparent to opaque.</summary>
    private static SKBitmap Translucent(int w, int h) => Image(w, h, (x, y) =>
        new SKColor((byte)(x * 8 % 256), (byte)(255 - y * 12 % 256), (byte)((x + y) * 5 % 256), (byte)((x * 9 + y * 5) % 256)));

    /// <summary>One-pixel black and white squares.</summary>
    private static SKBitmap Checker(int w, int h) => Image(w, h, (x, y) => (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);

    // ---- drawing ------------------------------------------------------------------------------------------------

    private static JsonElement Doc(string nodes, string background = "#000000ff", int w = W, int h = H) =>
        SceneKit.Doc(SceneKit.Json("{'format': 'infopanel.scene/1', 'nodes': [" + nodes + "]}"), w, h, background: background);

    private static SceneRuntimeOptions Options(Assets assets, out IDisposable media)
    {
        var cache = SceneKit.SyncMedia(assets.Store);
        media = cache;
        return SceneKit.Options(new FakeSceneInputs(), cache);
    }

    /// <summary>The frame at <paramref name="scale"/>, as an output gets it.</summary>
    private static SKBitmap Frame(JsonElement doc, Assets assets, float scale)
    {
        var options = Options(assets, out var media);
        using (media)
            return SceneKit.Render(doc, options, scale);
    }

    /// <summary>
    /// The frame as the engine drew every scale before raster media was enlarged: the snapshot drawn at scale 1 under a
    /// canvas already scaled, so every node is drawn through the canvas matrix at <paramref name="scale"/>.
    /// </summary>
    private static SKBitmap Today(JsonElement doc, Assets assets, float scale)
    {
        var options = Options(assets, out var media);
        using (media)
        using (var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, options))
        {
            var bitmap = new SKBitmap(new SKImageInfo((int)Math.Round(snapshot.Width * scale), (int)Math.Round(snapshot.Height * scale),
                SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            snapshot.Draw(canvas, 1);
            canvas.Flush();
            return bitmap;
        }
    }

    /// <summary>The canvas pixel frame pixel <paramref name="i"/> shows at <paramref name="scale"/>.</summary>
    private static int Source(int i, float scale) => (int)Math.Floor((i + 0.5) / scale);

    private static int Distance(SKColor p, SKColor q) => Math.Max(Math.Max(Math.Abs(p.Red - q.Red), Math.Abs(p.Green - q.Green)),
        Math.Max(Math.Abs(p.Blue - q.Blue), Math.Abs(p.Alpha - q.Alpha)));

    /// <summary>
    /// Every frame pixel in <paramref name="area"/> (canvas pixels; the whole canvas by default) against the panel's
    /// pixel it shows: exact where <paramref name="exact"/> says so, else within 2 levels.
    /// </summary>
    private static void AgainstThePanel(SKBitmap frame, SKBitmap panel, float scale, Func<int, int, bool> exact, SKRectI? area = null)
    {
        var a = area ?? new SKRectI(0, 0, panel.Width, panel.Height);
        int inexact = 0, far = 0, worst = 0, checkedPixels = 0;
        string? first = null;
        for (var j = 0; j < frame.Height; j++)
        for (var i = 0; i < frame.Width; i++)
        {
            var (x, y) = (Source(i, scale), Source(j, scale));
            if (x < a.Left || x >= a.Right || y < a.Top || y >= a.Bottom) continue;
            checkedPixels++;
            var d = Distance(frame.GetPixel(i, j), panel.GetPixel(x, y));
            worst = Math.Max(worst, d);
            var bad = exact(x, y) ? d > 0 : d > GoldenImage.ChannelTolerance;
            if (!bad) continue;
            if (exact(x, y)) inexact++;
            else far++;
            first ??= $"frame ({i}, {j}) is {frame.GetPixel(i, j)}, canvas ({x}, {y}) is {panel.GetPixel(x, y)}";
        }
        Assert.True(checkedPixels > 0);
        if (inexact + far > 0)
            Assert.Fail($"at {scale}: {inexact} covered pixels not exact, {far} others more than 2 levels off, worst {worst} levels; first: {first}");
    }

    // ---- the rule -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Scales))]
    public void Raster_media_above_scale_1_shows_the_canvas_pixels_enlarged(float scale)
    {
        var assets = new Assets();
        using var photo = Photo(40, 30);
        using var translucent = Translucent(30, 20);
        var p = assets.Add(photo);
        var t = assets.Add(translucent);
        var nodes = string.Join(",",
            // At a sub-pixel place, 1:1.
            $"{{'kind': 'media', 'x': 10.3, 'y': 8.6, 'source': {{'asset': '{p}'}}}}",
            // Turned and skewed by groups.
            $"{{'kind': 'group', 'transform': [{{'type': 'rotate', 'angle': 20, 'cx': 90, 'cy': 25}}], 'children': [{{'kind': 'media', 'x': 70.4, 'y': 12.2, 'source': {{'asset': '{p}'}}}}]}}",
            $"{{'kind': 'group', 'transform': [{{'type': 'skew', 'x': 15, 'y': 0}}], 'children': [{{'kind': 'media', 'x': 128.7, 'y': 10.1, 'source': {{'asset': '{p}'}}}}]}}",
            // Scaled up, down, and up with nearest sampling.
            $"{{'kind': 'media', 'x': 10.5, 'y': 60.25, 'w': 68, 'h': 51, 'source': {{'asset': '{p}'}}}}",
            $"{{'kind': 'media', 'x': 90.1, 'y': 70.7, 'w': 24, 'h': 18, 'source': {{'asset': '{p}'}}}}",
            $"{{'kind': 'media', 'x': 122.35, 'y': 64.8, 'w': 52, 'h': 39, 'sampling': 'nearest', 'source': {{'asset': '{p}'}}}}",
            // Translucent pixels at an opacity, and a crossfade.
            $"{{'kind': 'media', 'x': 182.2, 'y': 62.3, 'opacity': 0.6, 'source': {{'asset': '{t}'}}}}",
            $"{{'kind': 'media', 'x': 182.6, 'y': 95.4, 'source': {{'sequence': [{{'asset': '{p}'}}, {{'asset': '{t}'}}], 'index': 0.4, 'crossfade': true}}}}");

        // Covered pixels: the same over black and white, so the raster alone decides them.
        using var panel = Frame(Doc(nodes), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff"), assets, 1);
        bool Covered(int x, int y) => panel.GetPixel(x, y) == white.GetPixel(x, y);
        var covered = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (Covered(x, y) && panel.GetPixel(x, y) != SKColors.Black) covered++;
        Assert.True(covered > 6000, $"{covered} covered pixels");

        using var frame = Frame(Doc(nodes), assets, scale);
        Assert.Equal((Math.Round(W * scale), Math.Round(H * scale)), ((double)frame.Width, (double)frame.Height));
        AgainstThePanel(frame, panel, scale, Covered);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void A_whole_raster_at_a_whole_place_is_exact_at_every_scale(float scale)
    {
        var assets = new Assets();
        using var checker = Checker(40, 40);
        var c = assets.Add(checker);
        using var frame = Frame(Doc($"{{'kind': 'media', 'source': {{'asset': '{c}'}}}}", w: 40, h: 40), assets, scale);
        var differ = 0;
        for (var j = 0; j < frame.Height; j++)
            for (var i = 0; i < frame.Width; i++)
                if (frame.GetPixel(i, j) != checker.GetPixel(Source(i, scale), Source(j, scale))) differ++;
        Assert.Equal(0, differ);
    }

    [Theory]
    [InlineData(2.5f)]
    [InlineData(1.25f)]
    public void Ties_go_to_the_right_and_lower_pixel(float scale)
    {
        // Each column and each row its own grey, so a frame pixel names the canvas pixel it shows.
        var assets = new Assets();
        using var stripes = Image(40, 40, (x, y) => new SKColor((byte)(x * 6), (byte)(y * 6), 0));
        var s = assets.Add(stripes);
        using var frame = Frame(Doc($"{{'kind': 'media', 'source': {{'asset': '{s}'}}}}", w: 40, h: 40), assets, scale);
        var ties = 0;
        for (var j = 0; j < frame.Height; j++)
            for (var i = 0; i < frame.Width; i++)
            {
                var p = frame.GetPixel(i, j);
                if (p.Red != Source(i, scale) * 6 || p.Green != Source(j, scale) * 6)
                    Assert.Fail($"at {scale}: frame ({i}, {j}) shows canvas ({p.Red / 6}, {p.Green / 6}), not ({Source(i, scale)}, {Source(j, scale)})");
                if ((i + 0.5) / scale % 1 == 0) ties++;
            }
        Assert.True(ties > 0, "no frame pixel centre is on a canvas pixel's edge");
    }

    [Fact]
    public void Vectors_and_svg_are_drawn_at_the_scale()
    {
        // At 2 an enlarged canvas pixel is a 2 × 2 block on even coordinates; an edge drawn at the scale splits blocks.
        var assets = new Assets();
        var nodes = $"{{'kind': 'media', 'x': 10.3, 'y': 10.6, 'w': 40, 'h': 40, 'source': {{'asset': '{assets.Example("icon.svg")}'}}}},"
            + "{'kind': 'rect', 'x': 60.3, 'y': 10.6, 'w': 30, 'h': 20, 'fill': '#ffffff'}";
        using var frame = Frame(Doc(nodes), assets, 2);
        int Split(int left, int top, int right, int bottom)
        {
            var n = 0;
            for (var y = top * 2; y < bottom * 2; y += 2)
                for (var x = left * 2; x < right * 2; x += 2)
                {
                    var p = frame.GetPixel(x, y);
                    if (frame.GetPixel(x + 1, y) != p || frame.GetPixel(x, y + 1) != p || frame.GetPixel(x + 1, y + 1) != p) n++;
                }
            return n;
        }
        Assert.True(Split(9, 9, 52, 52) > 40, "the SVG is enlarged");
        Assert.True(Split(59, 9, 92, 32) > 40, "the rectangle is enlarged");
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(4f)]
    public void Vector_only_frames_are_drawn_as_before(float scale)
    {
        var assets = new Assets();
        var nodes = string.Join(",",
            "{'kind': 'rect', 'w': 240, 'h': 140, 'fill': {'type': 'linear', 'x1': 0, 'y1': 0, 'x2': 240, 'y2': 140, 'stops': [{'at': 0, 'color': '#1e3a8a'}, {'at': 1, 'color': '#facc15'}]}}",
            $"{{'kind': 'media', 'x': 10.3, 'y': 10.6, 'w': 50, 'h': 50, 'source': {{'asset': '{assets.Example("icon.svg")}'}}}}",
            $"{{'kind': 'media', 'x': 70.2, 'y': 8.4, 'w': 60, 'h': 30, 'source': {{'asset': '{assets.Example("badge.svg")}'}}}}",
            "{'kind': 'media', 'x': 140.5, 'y': 10.5, 'w': 40, 'h': 30, 'source': {'asset': 'sha256:0000000000000000000000000000000000000000000000000000000000000000'}}",
            "{'kind': 'group', 'transform': [{'type': 'rotate', 'angle': 25, 'cx': 60, 'cy': 100}], 'effects': [{'type': 'shadow', 'dx': 3, 'dy': 3, 'sigma': 2, 'color': '#000000aa'}], 'children': [{'kind': 'rect', 'x': 30.3, 'y': 85.6, 'w': 60, 'h': 30, 'r': 6, 'fill': '#22c55e', 'stroke': {'paint': '#ffffff', 'width': 1.5, 'dash': [4, 2]}}]}",
            "{'kind': 'group', 'clip': {'kind': 'ellipse', 'cx': 160, 'cy': 100, 'rx': 30, 'ry': 20}, 'effects': [{'type': 'blur', 'sigma': 1}], 'children': [{'kind': 'rect', 'x': 120, 'y': 70, 'w': 80, 'h': 60, 'fill': '#ef4444'}]}",
            "{'kind': 'text', 'x': 190.4, 'y': 50.3, 'font': {'family': 'Inter', 'size': 13}, 'fill': '#ffffff', 'text': 'Zoom'}");
        using var frame = Frame(Doc(nodes), assets, scale);
        using var before = Today(Doc(nodes), assets, scale);
        Assert.True(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), $"the vector-only frame at {scale} changed");
    }

    [Fact]
    public void A_vector_over_a_raster_keeps_its_pixels()
    {
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        const string rect = "{'kind': 'rect', 'x': 20.3, 'y': 15.7, 'w': 60, 'h': 40, 'r': 8, 'fill': '#3b82f6'}";
        using var over = Frame(Doc($"{{'kind': 'media', 'w': 240, 'h': 140, 'source': {{'asset': '{p}'}}}}," + rect), assets, 2.5f);
        using var alone = Frame(Doc(rect), assets, 2.5f);
        var fill = new SKColor(0x3b, 0x82, 0xf6);
        var inside = 0;
        for (var y = 0; y < alone.Height; y++)
            for (var x = 0; x < alone.Width; x++)
            {
                if (alone.GetPixel(x, y) != fill) continue;
                inside++;
                Assert.Equal(fill, over.GetPixel(x, y));
            }
        Assert.True(inside > 10_000, $"{inside} pixels inside");
        // Its edge is drawn at the scale too: a pixel the outline half covers is between the fill and the picture.
        Assert.NotEqual(alone.GetPixel(50, (int)(15.7 * 2.5)), fill);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void At_or_below_scale_1_media_is_drawn_at_its_scale(float scale)
    {
        // A one-pixel checkerboard drawn smaller is grey (mipmapped), not every other canvas pixel.
        var assets = new Assets();
        using var checker = Checker(40, 40);
        var c = assets.Add(checker);
        using var frame = Frame(Doc($"{{'kind': 'media', 'source': {{'asset': '{c}'}}}}", w: 40, h: 40), assets, scale);
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                Assert.InRange(frame.GetPixel(x, y).Red, (byte)96, (byte)160);
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void The_box_clips_the_canvas_pixels(float scale)
    {
        // Covering a box at a sub-pixel place: its edge pixels are the panel's, the box's coverage blended in them.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = $"{{'kind': 'media', 'x': 20.3, 'y': 10.6, 'w': 50.5, 'h': 30.2, 'fit': 'cover', 'source': {{'asset': '{p}'}}}}";
        using var panel = Frame(Doc(nodes), assets, 1);
        using var frame = Frame(Doc(nodes), assets, scale);
        AgainstThePanel(frame, panel, scale, (_, _) => false, new SKRectI(17, 8, 74, 44));
    }

    [Fact]
    public void A_raster_far_larger_than_the_canvas_costs_at_most_the_canvas()
    {
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = $"{{'kind': 'media', 'x': -400000, 'y': 20.5, 'w': 1000000, 'h': 30, 'source': {{'asset': '{p}'}}}}";
        long largest = 0;
        var pictures = 0;
        SceneDrawer.PictureProbe = info =>
        {
            pictures++;
            largest = Math.Max(largest, info.BytesSize64);
        };
        try
        {
            using var frame = Frame(Doc(nodes), assets, 4);
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
        }
        Assert.Equal(1, pictures);
        Assert.InRange(largest, 1, (long)W * H * 4);
    }

    [Fact]
    public void The_canvas_grid_is_found_under_any_root_matrix()
    {
        // A frame drawn into a canvas already moved: the same pixels, moved.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var doc = Doc($"{{'kind': 'media', 'x': 10.3, 'y': 8.6, 'source': {{'asset': '{p}'}}}}");
        using var frame = Frame(doc, assets, 2);
        var options = Options(assets, out var media);
        using (media)
        using (var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, options))
        using (var moved = new SKBitmap(new SKImageInfo(2 * W + 12, 2 * H + 8, SKImageInfo.PlatformColorType, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(moved))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(12, 8);
                snapshot.Draw(canvas, 2, withoutText: false, moved.ColorType);
            }
            var differ = 0;
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                    if (frame.GetPixel(x, y) != moved.GetPixel(x + 12, y + 8)) differ++;
            Assert.Equal(0, differ);
        }
    }

    [Fact]
    public void A_shrunk_group_layer_keeps_the_raster_edge_pixels()
    {
        // A group with opacity (a layer) shrinks a large picture to 40 × 30 canvas pixels at a sub-pixel place: its
        // first canvas row and column are partly covered, and their enlarged blocks start before the layer's own
        // bounds grown by 2 local units (0.1 canvas pixel) would.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = "{'kind': 'group', 'opacity': 0.5, 'transform': [{'type': 'translate', 'x': 50, 'y': 40}, {'type': 'scale', 'x': 0.05, 'y': 0.05}], "
            + $"'children': [{{'kind': 'media', 'x': 6, 'y': 12, 'w': 800, 'h': 600, 'source': {{'asset': '{p}'}}}}]}}";
        using var panel = Frame(Doc(nodes), assets, 1);
        using var frame = Frame(Doc(nodes), assets, 4);
        Assert.NotEqual(SKColors.Black, panel.GetPixel(50, 40));
        AgainstThePanel(frame, panel, 4, (_, _) => false, new SKRectI(46, 36, 96, 76));
    }

    // ---- the picture's size, its matrix, any scale, crossfades, effect layers -------------------------------------

    [Fact]
    public void A_displaced_effect_source_costs_only_the_pixels_it_covers()
    {
        // A raster a million pixels away, brought onto the canvas by its group's shadow: the effect layer reaches it, so
        // it is drawn, but its picture is the 10 × 10 canvas pixels it covers, not everything from the canvas's origin
        // to it (a million pixels square, about 4 TB). The probe refuses anything larger than the canvas before it is
        // allocated.
        const int side = 100;
        var assets = new Assets();
        using var photo = Photo(10, 10);
        var p = assets.Add(photo);
        var nodes = "{'kind': 'group', 'effects': [{'type': 'shadow', 'dx': -1000000, 'dy': -1000000, 'sigma': 0, 'color': '#000000ff'}], "
            + $"'children': [{{'kind': 'media', 'x': 1000000, 'y': 1000000, 'source': {{'asset': '{p}'}}}}]}}";
        var doc = Doc(nodes, "#ffffffff", side, side);
        using var panel = Frame(doc, assets, 1);
        Assert.Equal(SKColors.Black, panel.GetPixel(5, 5));
        long largest = 0;
        SceneDrawer.PictureProbe = info =>
        {
            if (info.BytesSize64 > (long)side * side * 4)
                throw new InvalidOperationException($"a picture of {info.Width} × {info.Height} canvas pixels ({info.BytesSize64:N0} bytes) for a raster covering 10 × 10");
            largest = Math.Max(largest, info.BytesSize64);
        };
        SKBitmap frame;
        try
        {
            frame = Frame(doc, assets, 2);
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
        }
        using (frame)
        {
            Assert.InRange(largest, 1, 16 * 16 * 4);
            // Its shadow is the panel's: black over the 10 × 10 canvas pixels, white past them.
            AgainstThePanel(frame, panel, 2, (_, _) => true, new SKRectI(0, 0, 14, 14));
        }
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(1.3278f)]
    public void Raster_media_whose_picture_is_over_the_bound_is_drawn_at_the_scale(float scale)
    {
        // The bounded fallback: with the bound below a canvas of pixels, the media is drawn as before the enlargement.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var doc = Doc($"{{'kind': 'media', 'x': 10.3, 'y': 8.6, 'w': 60, 'h': 45, 'source': {{'asset': '{p}'}}}}");
        var pictures = 0;
        SceneDrawer.PictureBound = (long)W * H * 4 - 1;
        SceneDrawer.PictureProbe = _ => pictures++;
        SKBitmap frame;
        try
        {
            frame = Frame(doc, assets, scale);
        }
        finally
        {
            SceneDrawer.PictureBound = null;
            SceneDrawer.PictureProbe = null;
        }
        using (frame)
        using (var before = Today(doc, assets, scale))
        {
            Assert.Equal(0, pictures);
            Assert.True(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), $"the media over the bound at {scale} isn't drawn at the scale");
        }
    }

    public static TheoryData<float, int> QuarterStepsAndTurns()
    {
        var data = new TheoryData<float, int>();
        for (var q = 5; q <= 16; q++)
            foreach (var angle in new[] { 90, 180, 270, 33 })
                data.Add(q / 4f, angle);
        return data;
    }

    [Theory]
    [MemberData(nameof(QuarterStepsAndTurns))]
    public void Turned_nearest_media_shows_the_panels_pixels_at_every_quarter_step(float scale, int angle)
    {
        // A one-pixel checkerboard enlarged 1.7 times with nearest sampling, turned and moved by a group: some of the
        // panel's pixel centres fall on a checker square's edge, so the picture must be drawn under the panel's own
        // matrix to the last bit, or a covered canvas pixel flips between black and white.
        var assets = new Assets();
        using var checker = Checker(40, 30);
        var c = assets.Add(checker);
        var (tx, ty) = angle switch { 90 => (50, 40), 180 => (70, 55), 270 => (5, 72), _ => (30, 5) };
        var nodes = $"{{'kind': 'group', 'transform': [{{'type': 'translate', 'x': {tx}, 'y': {ty}}}, {{'type': 'rotate', 'angle': {angle}}}], "
            + $"'children': [{{'kind': 'media', 'w': 68, 'h': 51, 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}]}}";
        using var panel = Frame(Doc(nodes, w: 120, h: 120), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff", 120, 120), assets, 1);
        using var frame = Frame(Doc(nodes, w: 120, h: 120), assets, scale);
        AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
    }

    [Theory]
    [InlineData(241, 320)]
    [InlineData(300, 320)]
    [InlineData(173, 512)]
    [InlineData(150, 400)]
    [InlineData(97, 320)]
    [InlineData(61, 200)]
    public void A_thumbnail_shows_the_canvas_pixel_whose_square_holds_each_centre(int width, int thumbnail)
    {
        // A thumbnail (and an AI app's preview) is drawn at its width over the profile's: any scale, not a quarter step.
        const int height = 23;
        var assets = new Assets();
        using var checker = Checker(width, height);
        var c = assets.Add(checker);
        var doc = Doc($"{{'kind': 'media', 'source': {{'asset': '{c}'}}}}", w: width, h: height);
        var scale = Thumbnail.Size(width, height, thumbnail).Scale;
        Assert.True(scale > 1 && scale * 4 % 1 != 0, $"{scale} is a quarter step");
        var options = Options(assets, out var media);
        using (media)
        using (var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, options))
        using (var frame = SKBitmap.Decode(Thumbnail.RenderPng(snapshot, thumbnail)))
        {
            var differ = 0;
            string? first = null;
            for (var j = 0; j < frame.Height; j++)
                for (var i = 0; i < frame.Width; i++)
                {
                    var (x, y) = (Source(i, scale), Source(j, scale));
                    if (x >= width || y >= height) continue;
                    if (frame.GetPixel(i, j) == checker.GetPixel(x, y)) continue;
                    differ++;
                    first ??= $"frame ({i}, {j}) doesn't show canvas ({x}, {y})";
                }
            Assert.True(differ == 0, $"at {scale}: {differ} frame pixels show another canvas pixel; first: {first}");
        }
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void A_crossfade_from_a_raster_to_an_svg_shows_the_canvas_pixels(float scale)
    {
        // The lower sample decides: fading from a raster, the SVG it fades into is in the same picture of canvas pixels.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = $"{{'kind': 'media', 'x': 20.3, 'y': 10.6, 'w': 60, 'h': 45, "
            + $"'source': {{'sequence': [{{'asset': '{p}'}}, {{'asset': '{assets.Example("icon.svg")}'}}], 'index': 0.5, 'crossfade': true}}}}";
        using var panel = Frame(Doc(nodes), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff"), assets, 1);
        using var frame = Frame(Doc(nodes), assets, scale);
        AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
        using var before = Today(Doc(nodes), assets, scale);
        Assert.False(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), "drawn at the scale");
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(2.5f)]
    public void A_crossfade_from_an_svg_to_a_raster_is_drawn_at_the_scale(float scale)
    {
        // The lower sample decides: fading from an SVG, the raster it fades into is drawn at the scale with it.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = $"{{'kind': 'media', 'x': 20.3, 'y': 10.6, 'w': 60, 'h': 45, "
            + $"'source': {{'sequence': [{{'asset': '{assets.Example("icon.svg")}'}}, {{'asset': '{p}'}}], 'index': 0.5, 'crossfade': true}}}}";
        using var frame = Frame(Doc(nodes), assets, scale);
        using var before = Today(Doc(nodes), assets, scale);
        Assert.True(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), $"the crossfade from an SVG at {scale} changed");
    }

    /// <summary>The quarter steps of <see cref="Scales"/>, and two thumbnails' scales (241 and 173 pixels wide at 320 and 512).</summary>
    public static TheoryData<float> ScalesAndThumbnails() => new(1.25f, 1.5f, 2f, 2.5f, 3f, 4f, 320f / 241, 512f / 173);

    [Theory]
    [MemberData(nameof(ScalesAndThumbnails))]
    public void Raster_media_in_effect_layers_keeps_the_panels_pixels(float scale)
    {
        // Rasters in groups drawn through a layer: shadows (sigma 0, offsets a multiple of 4 canvas pixels, so whole
        // frame pixels at every quarter step), one reaching above and left of the canvas's origin, one inside a shrunk
        // group, and a blend. Opaque pixels exact, the rest within 2 levels, the shadows' included. At a thumbnail's scale
        // an offset is a fraction of a frame pixel, an effect drawn at the scale like a blur, so there the shadows have none.
        var k = scale * 4 % 1 == 0 ? 1 : 0;
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var nodes = string.Join(",",
            // Partly off the canvas, its shadow down and right: the layer starts above and left of the canvas.
            $"{{'kind': 'group', 'effects': [{{'type': 'shadow', 'dx': {8 * k}, 'dy': {4 * k}, 'sigma': 0, 'color': '#ff0000ff'}}], "
                + $"'children': [{{'kind': 'media', 'x': -5.3, 'y': -4.6, 'source': {{'asset': '{p}'}}}}]}}",
            // Turned inside, its shadow up and left: the layer reaches right of and below the raster.
            $"{{'kind': 'group', 'effects': [{{'type': 'shadow', 'dx': {-12 * k}, 'dy': {-8 * k}, 'sigma': 0, 'color': '#00ff00ff'}}], "
                + "'children': [{'kind': 'group', 'transform': [{'type': 'rotate', 'angle': 20, 'cx': 150, 'cy': 40}], "
                + $"'children': [{{'kind': 'media', 'x': 130.4, 'y': 22.2, 'source': {{'asset': '{p}'}}}}]}}]}}",
            // A shrunk group with a shadow (8 group units: 4 canvas pixels), the raster scaled up within it.
            $"{{'kind': 'group', 'transform': [{{'type': 'scale', 'x': 0.5, 'y': 0.5}}], 'effects': [{{'type': 'shadow', 'dx': {8 * k}, 'dy': {8 * k}, 'sigma': 0, 'color': '#0000ffff'}}], "
                + $"'children': [{{'kind': 'media', 'x': 300.5, 'y': 160.3, 'w': 80, 'h': 60, 'source': {{'asset': '{p}'}}}}]}}",
            // A blend (a layer without an effect), skewed.
            "{'kind': 'group', 'blend': 'screen', 'transform': [{'type': 'skew', 'x': 15, 'y': 0}], "
                + $"'children': [{{'kind': 'media', 'x': 40.7, 'y': 80.3, 'w': 52, 'h': 39, 'source': {{'asset': '{p}'}}}}]}}");
        using var panel = Frame(Doc(nodes), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff"), assets, 1);
        bool Covered(int x, int y) => panel.GetPixel(x, y) == white.GetPixel(x, y);
        if (k == 1)
        {
            Assert.Equal(SKColors.Red, panel.GetPixel(36, 27));
            Assert.Equal(SKColors.Blue, panel.GetPixel(192, 112));
        }
        using var frame = Frame(Doc(nodes), assets, scale);
        AgainstThePanel(frame, panel, scale, Covered);
    }

    // ---- clipped and displaced rasters, the enlargement's bound, scales beside a quarter step ---------------------

    public static TheoryData<float> QuarterSteps()
    {
        var data = new TheoryData<float>();
        for (var q = 5; q <= 16; q++) data.Add(q / 4f);
        return data;
    }

    [Theory]
    [MemberData(nameof(QuarterSteps))]
    public void A_raster_the_canvas_clips_keeps_the_canvas_origin(float scale)
    {
        // A one-pixel checkerboard with nearest sampling, turned a quarter and moved so the canvas's right edge cuts it.
        // The canvas's clip mapped back to canvas pixels in float lands a rounding past the edge (120.000008 at 1.75);
        // the raster is still within the canvas, so it is drawn into the canvas's picture under the panel's own matrix,
        // and every covered pixel is the panel's.
        var assets = new Assets();
        using var checker = Checker(40, 30);
        var c = assets.Add(checker);
        var nodes = "{'kind': 'group', 'transform': [{'type': 'translate', 'x': 150, 'y': 40}, {'type': 'rotate', 'angle': 90}], "
            + $"'children': [{{'kind': 'media', 'w': 68, 'h': 51, 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}]}}";
        using var panel = Frame(Doc(nodes, w: 120, h: 120), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff", 120, 120), assets, 1);
        List<SKImageInfo> pictures = [];
        SceneDrawer.PictureProbe = pictures.Add;
        SKBitmap frame;
        try
        {
            frame = Frame(Doc(nodes, w: 120, h: 120), assets, scale);
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
        }
        using (frame)
        {
            Assert.NotEmpty(pictures);
            Assert.All(pictures, info => Assert.Equal((120, 120), (info.Width, info.Height)));
            AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
        }
    }

    [Theory]
    [MemberData(nameof(QuarterSteps))]
    public void A_raster_ending_at_the_canvas_edge_in_a_layer_past_it_keeps_the_canvas_origin(float scale)
    {
        // Shrunk by 0.3, the raster's right edge maps to 120 and a float rounding (120.0000076), in a shadow's layer that
        // reaches past the canvas: still within the canvas, drawn into the canvas's picture under the panel's matrix.
        var assets = new Assets();
        using var checker = Checker(40, 30);
        var c = assets.Add(checker);
        var nodes = "{'kind': 'group', 'effects': [{'type': 'shadow', 'dx': -8, 'dy': 0, 'sigma': 0, 'color': '#ff0000ff'}], 'children': ["
            + "{'kind': 'group', 'transform': [{'type': 'translate', 'x': 0, 'y': 20}, {'type': 'scale', 'x': 0.3, 'y': 0.3}], "
            + $"'children': [{{'kind': 'media', 'x': 200, 'w': 200, 'h': 150, 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}]}}]}}";
        using var panel = Frame(Doc(nodes, w: 120, h: 120), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff", 120, 120), assets, 1);
        List<SKImageInfo> pictures = [];
        SceneDrawer.PictureProbe = pictures.Add;
        SKBitmap frame;
        try
        {
            frame = Frame(Doc(nodes, w: 120, h: 120), assets, scale);
        }
        finally
        {
            SceneDrawer.PictureProbe = null;
        }
        using (frame)
        {
            Assert.NotEmpty(pictures);
            Assert.All(pictures, info => Assert.Equal((120, 120), (info.Width, info.Height)));
            AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
        }
    }

    /// <summary>The frame at <paramref name="scale"/> drawn into pixels of <paramref name="type"/>.</summary>
    private static SKBitmap FrameIn(JsonElement doc, Assets assets, float scale, SKColorType type)
    {
        var options = Options(assets, out var media);
        using (media)
        using (var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, options))
        {
            var s = snapshot.EffectiveScale(scale);
            var bitmap = new SKBitmap(new SKImageInfo((int)Math.Round(snapshot.Width * s), (int)Math.Round(snapshot.Height * s), type, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, s, withoutText: false, type);
            canvas.Flush();
            return bitmap;
        }
    }

    [Theory]
    [InlineData(SKColorType.Rgba8888, 2f)]
    [InlineData(SKColorType.Rgba8888, 2.25f)]
    [InlineData(SKColorType.Rgba8888, 3.3f)]
    [InlineData(SKColorType.Bgra8888, 2f)]
    [InlineData(SKColorType.Bgra8888, 2.25f)]
    [InlineData(SKColorType.Bgra8888, 3.3f)]
    public void A_frame_in_either_byte_order_shows_the_panels_pixels_in_that_order(SKColorType type, float scale)
    {
        // Skia samples the same draw a little differently into RGBA and into BGRA pixels (one is the platform's own
        // order, which has paths of its own): this checker, shrunk by 0.3 with nearest sampling, has a sample on a
        // pixel's edge in every third row and column, and 360 of its pixels come out the other colour in the other
        // order. The panel's frame at scale 1 in either order is what a frame above 1 in that order shows.
        var assets = new Assets();
        using var checker = Checker(40, 30);
        var c = assets.Add(checker);
        var nodes = "{'kind': 'group', 'transform': [{'type': 'translate', 'x': 0, 'y': 20}, {'type': 'scale', 'x': 0.3, 'y': 0.3}], "
            + $"'children': [{{'kind': 'media', 'x': 200, 'w': 200, 'h': 150, 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}]}}";
        using var panel = FrameIn(Doc(nodes, w: 120, h: 120), assets, 1, type);
        using var white = FrameIn(Doc(nodes, "#ffffffff", 120, 120), assets, 1, type);
        using var frame = FrameIn(Doc(nodes, w: 120, h: 120), assets, scale, type);
        AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
    }

    [Fact]
    public void An_enlargement_over_the_bound_is_drawn_at_the_scale()
    {
        // The picture of canvas pixels is within the bound, its enlargement at a scale between the quarter steps isn't:
        // the media is drawn at the scale, as before the enlargement, not by Skia's sampling with the tie nudge.
        var assets = new Assets();
        using var photo = Photo(40, 30);
        var p = assets.Add(photo);
        var doc = Doc($"{{'kind': 'media', 'w': {W}, 'h': {H}, 'source': {{'asset': '{p}'}}}}");
        const float scale = 1.3278f;
        var pictures = 0;
        SceneDrawer.PictureBound = (long)W * H * 4;
        SceneDrawer.PictureProbe = _ => pictures++;
        SKBitmap frame;
        try
        {
            frame = Frame(doc, assets, scale);
        }
        finally
        {
            SceneDrawer.PictureBound = null;
            SceneDrawer.PictureProbe = null;
        }
        using (frame)
        using (var before = Today(doc, assets, scale))
            Assert.True(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), $"the enlargement over the bound at {scale} isn't drawn at the scale");
    }

    [Fact]
    public void A_frame_at_the_frame_cap_enlarges_by_the_rule()
    {
        // 1501 × 1001 asked at 4 is drawn at the largest scale within the frame cap (3.3415854, not a quarter step), its
        // sides rounded up to 5016 × 3345: a few thousand pixels over 16 Mpx, which the enlargement's bound allows, so
        // every frame pixel shows the canvas pixel the rule names.
        const int w = 1501, h = 1001;
        var assets = new Assets();
        using var checker = Checker(w, h);
        var c = assets.Add(checker);
        var doc = Doc($"{{'kind': 'media', 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}", w: w, h: h);
        var options = Options(assets, out var media);
        using (media)
        using (var snapshot = SceneRuntime.RenderOnce(doc, SceneKit.Now, options))
        {
            var scale = snapshot.EffectiveScale(4);
            Assert.True(scale < 4 && scale * 4 % 1 != 0, $"{scale}");
            using var frame = snapshot.Render(4);
            Assert.True((long)frame.Width * frame.Height > TickSnapshot.MaxFramePixels, $"{frame.Width} × {frame.Height}");
            // Black and white are the same in either byte order.
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.GetPixelSpan());
            var stride = frame.RowBytes / 4;
            var differ = 0;
            string? first = null;
            for (var j = 0; j < frame.Height; j++)
            {
                var y = Source(j, scale);
                if (y >= h) continue;
                for (var i = 0; i < frame.Width; i++)
                {
                    var x = Source(i, scale);
                    if (x >= w) continue;
                    var want = (x + y) % 2 == 0 ? 0xFF000000u : 0xFFFFFFFFu;
                    if (pixels[j * stride + i] == want) continue;
                    differ++;
                    first ??= $"frame ({i}, {j}) doesn't show canvas ({x}, {y})";
                }
            }
            Assert.True(differ == 0, $"at {scale}: {differ} frame pixels show another canvas pixel; first: {first}");
        }
    }

    [Fact]
    public void A_thin_enlargement_past_the_canvas_is_bounded_by_the_frame()
    {
        // A tall raster past the canvas, under a clip a quarter of a canvas pixel wide (one frame pixel at 4), brought
        // onto the canvas by a long shadow: the clip's bounds let through 3 frame pixels, so its picture is 2 canvas
        // pixels wide and within the bound, but its enlargement, 3 frame pixels wide and 4 times as tall, is over it.
        // The rounding allowance is a row and a column of the frame (400 × 400), not of the enlargement, which would
        // allow a thin enlargement a column for every row (at the real bound, 1.5 times it; one frame pixel wide, any
        // size): it is refused before anything is allocated, and the media is drawn at the scale. The bound here is the
        // canvas's bytes.
        const int side = 100, tall = 1100;
        const float scale = 4;
        var assets = new Assets();
        using var photo = Photo(10, 10);
        var p = assets.Add(photo);
        var nodes = $"{{'kind': 'group', 'clip': {{'kind': 'rect', 'x': 50, 'y': 0, 'w': 0.25, 'h': {side}}}, 'children': ["
            + $"{{'kind': 'group', 'effects': [{{'type': 'shadow', 'dx': 0, 'dy': {-tall}, 'sigma': 0, 'color': '#000000ff'}}], "
            + $"'children': [{{'kind': 'media', 'x': 45, 'y': {side}, 'w': 10, 'h': {tall}, 'source': {{'asset': '{p}'}}}}]}}]}}";
        var doc = Doc(nodes, "#ffffffff", side, side);
        long bound = (long)side * side * 4, largest = 0;
        var pictures = 0;
        SceneDrawer.PictureBound = bound;
        SceneDrawer.PictureProbe = _ => pictures++;
        SceneDrawer.EnlargementProbe = info => largest = Math.Max(largest, info.BytesSize64);
        SKBitmap frame;
        try
        {
            frame = Frame(doc, assets, scale);
        }
        finally
        {
            SceneDrawer.PictureBound = null;
            SceneDrawer.PictureProbe = null;
            SceneDrawer.EnlargementProbe = null;
        }
        using (frame)
        using (var before = Today(doc, assets, scale))
        {
            Assert.Equal(SKColors.Black, before.GetPixel(200, 10));
            Assert.True(largest == 0, $"an enlargement of {largest:N0} bytes, over the bound of {bound:N0} and a row and a column of the 400 × 400 frame");
            Assert.Equal(0, pictures);
            Assert.True(frame.GetPixelSpan().SequenceEqual(before.GetPixelSpan()), "the thin media over the bound isn't drawn at the scale");
        }
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(1.25f)]
    [InlineData(2.5f)]
    [InlineData(4f)]
    public void A_displaced_effect_source_keeps_its_alpha_detail(float scale)
    {
        // A one-pixel checkerboard of transparent and opaque pixels a million pixels away, brought onto the canvas by
        // its group's shadow: the shadow shows the alpha of each canvas pixel by the rule, ties included, though a
        // fraction of a device pixel is lost in float at a million.
        const int side = 100;
        var assets = new Assets();
        using var holes = Image(10, 10, (x, y) => (x + y) % 2 == 0 ? SKColors.Transparent : SKColors.Black);
        var p = assets.Add(holes);
        var nodes = "{'kind': 'group', 'effects': [{'type': 'shadow', 'dx': -1000000, 'dy': -1000000, 'sigma': 0, 'color': '#000000ff'}], "
            + $"'children': [{{'kind': 'media', 'x': 1000000, 'y': 1000000, 'source': {{'asset': '{p}'}}}}]}}";
        var doc = Doc(nodes, "#ffffffff", side, side);
        using var panel = Frame(doc, assets, 1);
        Assert.Equal(SKColors.Black, panel.GetPixel(1, 0));
        Assert.Equal(SKColors.White, panel.GetPixel(0, 0));
        using var frame = Frame(doc, assets, scale);
        AgainstThePanel(frame, panel, scale, (_, _) => true, new SKRectI(0, 0, 14, 14));
    }

    public static TheoryData<float> BesideQuarterSteps()
    {
        var data = new TheoryData<float>();
        foreach (var q in new[] { 1.25f, 2.5f, 3.75f, 4f })
        {
            data.Add(MathF.BitDecrement(q));
            data.Add(MathF.BitIncrement(q));
        }
        data.Add(2.49f);
        data.Add(2.51f);
        return data;
    }

    [Theory]
    [MemberData(nameof(BesideQuarterSteps))]
    public void Scales_beside_a_quarter_step_follow_the_rule(float scale)
    {
        // Just below and above a quarter step the enlargement is computed by the rule, not Skia's sampling: a whole
        // checkerboard shows every canvas pixel the rule names, and turned nearest media shows the panel's pixels.
        Assert.True(scale * 4 % 1 != 0);
        var assets = new Assets();
        using var checker = Checker(40, 30);
        var c = assets.Add(checker);
        using (var whole = Frame(Doc($"{{'kind': 'media', 'source': {{'asset': '{c}'}}}}", w: 40, h: 30), assets, scale))
        {
            var differ = 0;
            for (var j = 0; j < whole.Height; j++)
                for (var i = 0; i < whole.Width; i++)
                {
                    var (x, y) = (Source(i, scale), Source(j, scale));
                    if (x < 40 && y < 30 && whole.GetPixel(i, j) != checker.GetPixel(x, y)) differ++;
                }
            Assert.True(differ == 0, $"at {scale}: {differ} frame pixels show another canvas pixel");
        }
        var nodes = "{'kind': 'group', 'transform': [{'type': 'translate', 'x': 30, 'y': 5}, {'type': 'rotate', 'angle': 33}], "
            + $"'children': [{{'kind': 'media', 'w': 68, 'h': 51, 'sampling': 'nearest', 'source': {{'asset': '{c}'}}}}]}}";
        using var panel = Frame(Doc(nodes, w: 120, h: 120), assets, 1);
        using var white = Frame(Doc(nodes, "#ffffffff", 120, 120), assets, 1);
        using var frame = Frame(Doc(nodes, w: 120, h: 120), assets, scale);
        AgainstThePanel(frame, panel, scale, (x, y) => panel.GetPixel(x, y) == white.GetPixel(x, y));
    }
}
