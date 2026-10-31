using System;
using System.Buffers;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>
/// Draws resolved nodes with Skia (spec/docs/scene.md, "Drawing model"): painter's order, source-over, antialiased,
/// everything through the canvas matrix, so one evaluation draws the same at every scale; above scale 1, raster media
/// as the canvas's own pixels enlarged (<see cref="DrawEnlarged"/>). Never writes to what it draws.
/// </summary>
internal static class SceneDrawer
{
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>
    /// A test hook: called on the drawing thread for every stroke drawn with a path effect, with the outline (a text
    /// run's glyph outlines), the paint and the canvas matrix, so a test can count what Skia draws.
    /// </summary>
    [ThreadStatic] internal static Action<RStroke, SKPath, SKPaint, SKMatrix>? StrokeProbe;

    /// <summary>
    /// Set on the drawing thread while <see cref="TickSnapshot.Draw(SKCanvas, float, bool)"/> draws a frame without its
    /// text nodes (what the preview checks sample under the text); never for an output.
    /// </summary>
    [ThreadStatic] internal static bool WithoutText;

    /// <summary>
    /// Set on the drawing thread while <see cref="TickSnapshot.Draw(SKCanvas, float, bool)"/> draws a frame: the frame's
    /// scale when it is above 1, else 0.
    /// </summary>
    [ThreadStatic] internal static float Zoom;

    /// <summary>
    /// Set with <see cref="Zoom"/>: the canvas matrix the frame was drawn under before its scale (identity for every
    /// engine output), so the drawer can find the canvas's own pixel grid.
    /// </summary>
    [ThreadStatic] internal static SKMatrix ZoomRoot;

    /// <summary>Set with <see cref="Zoom"/>: the panel's side of the frame (<see cref="PanelFrame"/>).</summary>
    [ThreadStatic] internal static PanelFrame? Panel;

    /// <summary>
    /// A test hook: called on the drawing thread with the size of every picture of canvas pixels raster media is drawn
    /// into above scale 1, before it is allocated.
    /// </summary>
    [ThreadStatic] internal static Action<SKImageInfo>? PictureProbe;

    /// <summary>
    /// A test hook: called on the drawing thread with the size of every enlargement of canvas pixels made in memory (at a
    /// scale between the quarter steps, or for media past the canvas), before it is allocated.
    /// </summary>
    [ThreadStatic] internal static Action<SKImageInfo>? EnlargementProbe;

    /// <summary>
    /// The most bytes a picture of canvas pixels, or its enlargement, may take: a frame's (16 Mpx of RGBA). An enlargement
    /// may take a row and a column of the frame more (<see cref="Fits"/>), so a frame within <see cref="TickSnapshot.MaxFramePixels"/>
    /// whose sides were rounded to whole pixels fits. Raster media whose picture or enlargement would need more is drawn
    /// at the frame's scale instead.
    /// </summary>
    internal const long MaxPictureBytes = 64L << 20;

    /// <summary>A test hook: a smaller <see cref="MaxPictureBytes"/> on this thread.</summary>
    [ThreadStatic] internal static long? PictureBound;

    public static void Draw(SKCanvas canvas, RNode[] nodes)
    {
        foreach (var n in nodes) Draw(canvas, n);
    }

    private static void Draw(SKCanvas canvas, RNode node)
    {
        if (node.Opacity <= 0) return;
        switch (node)
        {
            case RGroup g: DrawGroup(canvas, g); break;
            case RShape s: DrawShape(canvas, s); break;
            case RText t: if (!WithoutText) DrawText(canvas, t); break;
            case RMedia m: DrawMedia(canvas, m); break;
        }
    }

    private static void DrawGroup(SKCanvas canvas, RGroup g)
    {
        var count = canvas.Save();
        var panelCount = Panel?.Save() ?? 0;
        if (!g.Matrix.IsIdentity)
        {
            canvas.Concat(in g.Matrix);
            Panel?.Concat(in g.Matrix);
        }
        if (g.NeedsLayer)
        {
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, Alpha(g.Opacity)), BlendMode = g.Blend };
            using var filter = Filter(g.Effects);
            paint.ImageFilter = filter;
            // Size the layer to where the children can draw, grown by the effects' reach; the canvas clips it further.
            SKRect? hint = null;
            if (SceneBounds.Measure(g.Children) is { } extent)
            {
                var h = extent.Hint;
                if (g.Clip is not null) h.Intersect(g.Clip.Bounds);
                h.Inflate(2, 2);
                // Raster media above scale 1 is drawn in whole canvas pixels, up to one past its edge however small
                // the group's units are.
                if (Zoom > 0 && HoldsRaster(g.Children)) h = GrowByCanvasPixel(h);
                hint = h;
            }
            if (hint is { } bounds) canvas.SaveLayer(bounds, paint);
            else canvas.SaveLayer(paint);
        }
        if (g.Clip is { } clip)
        {
            using var path = clip.ToPath();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);
        }
        foreach (var child in g.Children) Draw(canvas, child);
        Panel?.RestoreToCount(panelCount);
        canvas.RestoreToCount(count);
    }

    /// <summary>The frame's matrix from canvas pixels to device pixels at scale <paramref name="s"/>.</summary>
    private static SKMatrix CanvasToDevice(float s) => SKMatrix.Concat(ZoomRoot, SKMatrix.CreateScale(s, s));

    private static bool HoldsRaster(RNode[] nodes)
    {
        foreach (var n in nodes)
            if (n is RMedia { Sample.Image: not null } || n is RGroup g && HoldsRaster(g.Children)) return true;
        return false;
    }

    /// <summary><paramref name="r"/> (in the canvas's current coordinates) grown by a canvas pixel on every side.</summary>
    private static SKRect GrowByCanvasPixel(SKRect r)
    {
        if (Panel is null) return r;
        var local = Panel.Matrix;
        if (!local.TryInvert(out var fromCanvas)) return r;
        var grown = local.MapRect(r);
        grown.Inflate(1, 1);
        return SKRect.Union(r, fromCanvas.MapRect(grown));
    }

    private static SKImageFilter? Filter(REffect[] effects)
    {
        SKImageFilter? f = null;
        foreach (var fx in effects)
        {
            var previous = f;
            f = fx.Kind switch
            {
                EffectKind.Blur => SKImageFilter.CreateBlur(fx.Sigma, fx.Sigma, SKShaderTileMode.Decal, previous),
                EffectKind.Shadow => SKImageFilter.CreateDropShadow(fx.Dx, fx.Dy, fx.Sigma, fx.Sigma, SceneColor.ToSk(fx.Color), previous),
                _ => SKImageFilter.CreateDropShadow(0, 0, fx.Sigma, fx.Sigma, SceneColor.ToSk(fx.Color), previous),
            };
            previous?.Dispose();
        }
        return f;
    }

    private static byte Alpha(float opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255, MidpointRounding.AwayFromZero);

    /// <summary>A paint for <paramref name="p"/> at <paramref name="opacity"/>; the caller disposes the paint and its shader.</summary>
    private static SKPaint MakePaint(RPaint p, float opacity, SKPaintStyle style)
    {
        var paint = new SKPaint { IsAntialias = true, Style = style };
        if (p.SolidColor is { } c) paint.Color = SceneColor.ToSk(c, opacity);
        else
        {
            paint.Color = new SKColor(255, 255, 255, Alpha(opacity));
            paint.Shader = p.CreateShader();
        }
        return paint;
    }

    /// <summary>
    /// Sets <paramref name="s"/> on <paramref name="paint"/> for drawing under <paramref name="total"/>: the dash
    /// pattern floored at drawing resolution (<see cref="RStroke.DashAt"/>), none for a zero-length outline, which
    /// draws as an undashed stroke would (its caps).
    /// </summary>
    private static void ApplyStroke(SKPaint paint, RStroke s, in SKMatrix total, bool zeroLength)
    {
        paint.StrokeWidth = s.Width;
        paint.StrokeCap = s.Cap;
        paint.StrokeJoin = s.Join;
        paint.StrokeMiter = s.Miter;
        SKPathEffect? effect = null;
        if (s.Trimmed) effect = SKPathEffect.CreateTrim(s.TrimStart, s.TrimEnd, SKTrimPathEffectMode.Normal);
        if (!zeroLength && s.DashAt(SceneMath.MaxStretch(total)) is { } dash)
        {
            var d = SKPathEffect.CreateDash(dash, s.DashOffset);
            if (effect is null) effect = d;
            else
            {
                var composed = SKPathEffect.CreateCompose(d, effect); // trim first, then dash
                d.Dispose();
                effect.Dispose();
                effect = composed;
            }
        }
        paint.PathEffect = effect;
    }

    private static void Dispose(SKPaint paint)
    {
        paint.Shader?.Dispose();
        paint.PathEffect?.Dispose();
        paint.Dispose();
    }

    private static void DrawShape(SKCanvas canvas, RShape s)
    {
        if (s.Fill is null && s.Stroke is null) return;
        var opacity = s.Opacity;
        var layered = opacity < 1 && s.Fill is not null && s.Stroke is not null;
        if (layered)
        {
            var hint = SceneBounds.Measure(s)!.Value.Hint;
            using var lp = new SKPaint { Color = new SKColor(255, 255, 255, Alpha(opacity)) };
            canvas.SaveLayer(hint, lp);
            opacity = 1;
        }
        // The path a dashed outline was measured on is drawn as it is (never disposed here); otherwise one is built.
        SKPath? built = null;
        SKPath Path() => s.Geom.Drawn ?? (built ??= s.Geom.ToPath());
        try
        {
            if (s.Fill is { } fill && s.Geom is not LineGeom)
            {
                var paint = MakePaint(fill, opacity, SKPaintStyle.Fill);
                try
                {
                    if (!s.Geom.TryDrawFast(canvas, paint)) canvas.DrawPath(Path(), paint);
                }
                finally
                {
                    Dispose(paint);
                }
            }
            if (s.Stroke is { } stroke)
            {
                var paint = MakePaint(stroke.Paint, opacity, SKPaintStyle.Stroke);
                try
                {
                    // Zero length as measured on the path drawn (the budget's), not as the geometry asks for it.
                    var zeroLength = stroke.Dash is not null && s.Geom.Drawn is not null && s.Geom.Measured.Length == 0;
                    ApplyStroke(paint, stroke, canvas.TotalMatrix, zeroLength);
                    if (StrokeProbe is { } probe && paint.PathEffect is not null) probe(stroke, Path(), paint, canvas.TotalMatrix);
                    if (paint.PathEffect is not null || !s.Geom.TryDrawFast(canvas, paint)) canvas.DrawPath(Path(), paint);
                }
                finally
                {
                    Dispose(paint);
                }
            }
        }
        finally
        {
            built?.Dispose();
            if (layered) canvas.Restore();
        }
    }

    private static void DrawText(SKCanvas canvas, RText t)
    {
        var layout = t.Layout;
        if (layout.Lines.Length == 0) return;
        var count = canvas.Save();
        var opacity = t.Opacity;
        if (opacity < 1)
        {
            using var lp = new SKPaint { Color = new SKColor(255, 255, 255, Alpha(opacity)) };
            var hint = t.InkHint;
            hint.Inflate(2, 2);
            canvas.SaveLayer(hint, lp);
            opacity = 1;
        }
        if (t.ClipRect is { } clip) canvas.ClipRect(clip, SKClipOperation.Intersect, true);
        if (t.Marquee)
        {
            DrawLines(canvas, t, t.Left - t.MarqueeOffset, opacity);
            DrawLines(canvas, t, t.Left - t.MarqueeOffset + t.MarqueePeriod, opacity);
        }
        else
        {
            DrawLines(canvas, t, t.Left, opacity);
        }
        canvas.RestoreToCount(count);
    }

    private static void DrawLines(SKCanvas canvas, RText t, float left, float opacity)
    {
        var fills = new SKPaint?[t.SpanFills.Length];
        SKPaint? stroke = null;
        // Lines that can't reach the clip are skipped before any run goes to Skia, so a long text mostly out of view
        // costs only what shows. A line reaches its glyph bounds and decorations, grown by the stroke's reach (miters
        // on glyph corners); inside a group's effect layer the clip already includes what an effect can bring into view.
        var clip = canvas.LocalClipBounds;
        var reach = t.Stroke is { } st ? Math.Max(st.Width, st.Reach(corners: true)) : 0;
        try
        {
            foreach (var line in t.Layout.Lines)
            {
                var x = left + (t.AlignWidth - line.Width) * t.AlignFraction;
                var y = t.Top + line.Baseline;
                var ink = line.Ink;
                ink.Offset(x, y);
                ink.Inflate(reach + 1, reach + 1);
                if (!ink.IntersectsWith(clip)) continue;
                foreach (var run in line.Runs)
                {
                    var span = Math.Clamp(run.Span, 0, t.SpanFills.Length - 1);
                    if (t.SpanFills[span] is { } fill)
                    {
                        var paint = fills[span] ??= MakePaint(fill, opacity, SKPaintStyle.Fill);
                        canvas.DrawText(run.Blob, x, y, paint);
                        Decorate(canvas, t, run, span, x, y, paint);
                    }
                    if (t.Stroke is { } s)
                    {
                        if (stroke is null)
                        {
                            stroke = MakePaint(s.Paint, opacity, SKPaintStyle.Stroke);
                            ApplyStroke(stroke, s, canvas.TotalMatrix, zeroLength: false);
                        }
                        if (StrokeProbe is { } probe && stroke.PathEffect is not null && run.Typeface is not null)
                        {
                            using var font = TextLayoutEngine.MakeFont(run.Typeface, run.FontSize);
                            using var glyphs = new SKPath();
                            foreach (var g in run.Glyphs)
                                using (var outline = font.GetGlyphPath(g))
                                    if (outline is not null) glyphs.AddPath(outline);
                            probe(s, glyphs, stroke, canvas.TotalMatrix);
                        }
                        canvas.DrawText(run.Blob, x, y, stroke);
                    }
                }
            }
        }
        finally
        {
            foreach (var p in fills)
                if (p is not null) Dispose(p);
            if (stroke is not null) Dispose(stroke);
        }
    }

    private static void Decorate(SKCanvas canvas, RText t, GlyphRun run, int span, float x, float y, SKPaint paint)
    {
        var underline = span < t.Underline.Length && t.Underline[span];
        var strike = span < t.Strike.Length && t.Strike[span];
        if (!underline && !strike) return;
        var left = x + run.X0;
        var right = x + run.X1;
        if (underline) canvas.DrawRect(new SKRect(left, y + run.UnderlinePosition, right, y + run.UnderlinePosition + run.UnderlineThickness), paint);
        if (strike) canvas.DrawRect(new SKRect(left, y + run.StrikePosition - run.StrikeThickness, right, y + run.StrikePosition), paint);
    }

    private static void DrawMedia(SKCanvas canvas, RMedia m)
    {
        if (m.Missing)
        {
            Placeholders.DrawMissingMedia(canvas, m.Box, m.Opacity);
            return;
        }
        if (Zoom > 0 && m.Sample!.Image is not null && DrawEnlarged(canvas, m, Zoom)) return;
        var count = canvas.Save();
        if (m.ClipToBox) canvas.ClipRect(m.Box, SKClipOperation.Intersect, true);
        DrawSamples(canvas, m);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// How far the enlarged picture of canvas pixels is moved, in device pixels: a frame pixel whose centre is on the edge
    /// between two canvas pixels then shows the right or lower one. At the scales outputs ask for (multiples of 0.25),
    /// any other centre is at least ¼ of a device pixel from an edge, so nothing else moves.
    /// </summary>
    private const float TieNudge = -1f / 64;

    /// <summary>
    /// How far past the canvas's edge, in canvas pixels, raster media may reach and still be drawn as within the canvas:
    /// a float rounding (an edge mapped to 120.000008), never a visible part of a pixel.
    /// </summary>
    private const float CanvasSlack = 1f / 256;

    /// <summary>
    /// Raster media in a frame above scale 1 (spec/docs/api.md, "Frame size"): the media as the frame at scale 1 draws
    /// it (its transforms, box clip, opacity and crossfade, on the canvas's pixel grid) into a picture of the canvas
    /// pixels it covers, bounded by what the canvas can show, then those pixels enlarged by the frame's scale with
    /// nearest sampling, in the media's place in drawing order. The canvas's clips (groups' and the canvas's own) still
    /// apply, at the frame's scale. At a multiple of 0.25 within the canvas, Skia enlarges the picture, moved by
    /// <see cref="TieNudge"/>; at any other scale, and for media past the canvas, <see cref="Enlarge"/> computes the
    /// rule itself. False when the canvas's grid can't be found (a root matrix that can't be inverted) or the picture
    /// or its enlargement would be over the bound (<see cref="MaxPictureBytes"/>, <see cref="Fits"/>): the caller then
    /// draws the media at the frame's scale.
    /// </summary>
    private static bool DrawEnlarged(SKCanvas canvas, RMedia m, float s)
    {
        var frame = CanvasToDevice(s);
        if (Panel is not { } panel || !ZoomRoot.TryInvert(out var fromDevice) || !frame.TryInvert(out _)) return false;
        var local = panel.Matrix; // the media's coordinates → canvas pixels, as the frame at scale 1 has it
        var area = m.ClipToBox ? SKRect.Intersect(m.Dest, m.Box) : m.Dest;
        if (area.IsEmpty) return true;
        // The frame pixels the canvas's clips let through, and the canvas pixels they show by the rule, on the canvas's
        // grid in double precision: an ordinary clip's edge is the canvas's own edge, where the clip mapped back to
        // canvas pixels in float lands a rounding past it.
        var visible = fromDevice.MapRect(canvas.DeviceClipBounds);
        var (vi0, vj0) = ((int)Math.Floor(visible.Left), (int)Math.Floor(visible.Top));
        var (vi1, vj1) = ((int)Math.Ceiling(visible.Right), (int)Math.Ceiling(visible.Bottom));
        if (vi1 <= vi0 || vj1 <= vj0) return true;
        var shown = new SKRect(Shown(vi0, s), Shown(vj0, s), Shown(vi1 - 1, s) + 1, Shown(vj1 - 1, s) + 1);
        var covered = SKRect.Intersect(local.MapRect(area), shown);
        if (covered.IsEmpty) return true;
        var x0 = (int)MathF.Floor(covered.Left);
        var y0 = (int)MathF.Floor(covered.Top);
        var x1 = (int)MathF.Ceiling(covered.Right);
        var y1 = (int)MathF.Ceiling(covered.Bottom);
        var within = covered.Left > -CanvasSlack && covered.Top > -CanvasSlack
            && covered.Right < panel.Width + CanvasSlack && covered.Bottom < panel.Height + CanvasSlack;
        if (within) (x0, y0, x1, y1) = (Math.Max(x0, 0), Math.Max(y0, 0), Math.Min(x1, panel.Width), Math.Min(y1, panel.Height));
        if (x1 <= x0 || y1 <= y0) return true;
        // The frame pixels showing those canvas pixels.
        var (i0, j0) = (Math.Max(First(x0, s), vi0), Math.Max(First(y0, s), vj0));
        var (i1, j1) = (Math.Min(First(x1, s), vi1), Math.Min(First(y1, s), vj1));
        if (i1 <= i0 || j1 <= j0) return true;
        var bound = PictureBound ?? MaxPictureBytes;
        var exact = !within || s * 4 != MathF.Round(s * 4);

        SKBitmap? own = null, ownEnlarged = null;
        SKBitmap pixels;
        SKBitmap? enlarged = null;
        int ox = 0, oy = 0;
        if (within)
        {
            // Within the canvas (all raster media but some inside an effect layer that reaches past it): the frame's one
            // picture of the canvas, its origin the canvas's, so the media is drawn under the very matrix the panel's
            // frame draws it under and its pixels are the panel's to the last bit (a picture moved by whole pixels
            // rounds some samples the other way). Only the covered part is cleared, drawn and enlarged.
            if (panel.Pixels(bound) is not { } shared) return false;
            if (exact && (enlarged = panel.Enlargement(s, bound)) is null) return false;
            pixels = shared;
        }
        else
        {
            // Past the canvas, inside an effect layer reaching there (a shadow can bring media from a million pixels
            // away): a picture of the covered pixels alone, its origin at their corner, under the panel's matrix moved
            // by whole pixels. As the panel's frame draws such media into the layer at the layer's own origin, neither
            // origin gives its pixels to the last bit. Its enlargement is computed, as a nudge a fraction of a device
            // pixel can vanish in float that far out.
            var info = new SKImageInfo(x1 - x0, y1 - y0, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (info.BytesSize64 > bound || !Fits(i1 - i0, j1 - j0, panel.Sides(s), bound)) return false;
            PictureProbe?.Invoke(info);
            pixels = own = new SKBitmap(info);
            var size = new SKImageInfo(i1 - i0, j1 - j0, SKColorType.Rgba8888, SKAlphaType.Premul);
            EnlargementProbe?.Invoke(size);
            enlarged = ownEnlarged = new SKBitmap(size);
            (ox, oy) = (x0, y0);
        }
        using (own)
        using (ownEnlarged)
        {
            var part = new SKRectI(x0 - ox, y0 - oy, x1 - ox, y1 - oy);
            using (var picture = new SKCanvas(pixels))
            {
                picture.ClipRect(part);
                picture.Clear(SKColors.Transparent);
                if (own is null) picture.SetMatrix(local);
                else
                {
                    picture.Translate(-ox, -oy);
                    picture.Concat(in local);
                }
                if (m.ClipToBox) picture.ClipRect(m.Box, SKClipOperation.Intersect, true);
                DrawSamples(picture, m);
            }
            if (enlarged is not null)
            {
                Enlarge(canvas, pixels, part, x0, y0, s, new SKRectI(i0, j0, i1, j1), enlarged);
                return true;
            }
            using var all = pixels.PeekPixels();
            using var view = all.ExtractSubset(part);
            using var image = SKImage.FromPixels(view);
            var count = canvas.Save();
            canvas.SetMatrix(frame);
            var nudge = TieNudge / s;
            using (var paint = new SKPaint { IsAntialias = false })
                canvas.DrawImage(image, new SKRect(x0 + nudge, y0 + nudge, x1 + nudge, y1 + nudge), Nearest, paint);
            canvas.RestoreToCount(count);
        }
        return true;
    }

    /// <summary>
    /// Enlarges the <paramref name="part"/> of <paramref name="pixels"/> showing canvas pixels from
    /// (<paramref name="x0"/>, <paramref name="y0"/>) into frame pixels <paramref name="shows"/> by the rule itself: frame
    /// pixel i shows canvas pixel ⌊(i + ½) ÷ s⌋, computed in double precision, where a centre on an edge comes out
    /// exactly on it (s is a float, so (i + ½) ÷ s is a whole number only when the centre is on an edge, and is otherwise
    /// at least 2⁻²⁵ from one, far more than the division's rounding). The frame pixels are made in the top left of
    /// <paramref name="into"/>, then drawn one to one. Used at a scale that isn't a multiple of 0.25 (a thumbnail's, an
    /// AI app's preview), and for media past the canvas; at a multiple of 0.25 every centre off an edge is at least ¼ of a
    /// frame pixel from one, so Skia's nearest sampling moved by <see cref="TieNudge"/> gives the same pixels at no extra
    /// cost.
    /// </summary>
    private static void Enlarge(SKCanvas canvas, SKBitmap pixels, SKRectI part, int x0, int y0, float s, SKRectI shows, SKBitmap into)
    {
        var (w, h) = (shows.Width, shows.Height);
        var columns = ArrayPool<int>.Shared.Rent(w);
        try
        {
            for (var i = 0; i < w; i++) columns[i] = part.Left + Shown(shows.Left + i, s) - x0;
            var from = MemoryMarshal.Cast<byte, uint>(pixels.GetPixelSpan());
            var to = MemoryMarshal.Cast<byte, uint>(into.GetPixelSpan());
            var fromStride = pixels.RowBytes / 4;
            var toStride = into.RowBytes / 4;
            var made = -1;
            for (var j = 0; j < h; j++)
            {
                var row = to.Slice(j * toStride, w);
                var source = part.Top + Shown(shows.Top + j, s) - y0;
                if (source == made)
                {
                    to.Slice((j - 1) * toStride, w).CopyTo(row);
                    continue;
                }
                var line = from.Slice(source * fromStride, pixels.Width);
                for (var i = 0; i < w; i++) row[i] = line[columns[i]];
                made = source;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(columns);
        }
        using var all = into.PeekPixels();
        using var view = all.ExtractSubset(new SKRectI(0, 0, w, h));
        using var image = SKImage.FromPixels(view);
        var count = canvas.Save();
        canvas.SetMatrix(ZoomRoot);
        using (var paint = new SKPaint { IsAntialias = false })
            canvas.DrawImage(image, shows.Left, shows.Top, Nearest, paint);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// Whether an enlargement of <paramref name="w"/> × <paramref name="h"/> frame pixels is within <paramref name="bound"/>
    /// bytes, with a row and a column of the <paramref name="frame"/> more (<see cref="PanelFrame.Sides"/>): a frame within
    /// <see cref="TickSnapshot.MaxFramePixels"/> has sides rounded to whole pixels, which can take it past by up to half a
    /// row and half a column. The allowance is the frame's, never the enlargement's own, so the ceiling is the same for
    /// every enlargement in a frame: one a pixel wide gains nothing by being tall.
    /// </summary>
    internal static bool Fits(int w, int h, (int Width, int Height) frame, long bound) =>
        (long)w * h * 4 <= bound + 4L * ((long)frame.Width + frame.Height);

    /// <summary>The canvas pixel frame pixel <paramref name="i"/> shows at scale <paramref name="s"/>: ⌊(i + ½) ÷ s⌋.</summary>
    private static int Shown(int i, float s) => (int)Math.Floor((i + 0.5) / s);

    /// <summary>The first frame pixel showing canvas pixel <paramref name="c"/> or one after it: ⌈c × s − ½⌉ (exact in double).</summary>
    internal static int First(int c, float s) => (int)Math.Ceiling(c * (double)s - 0.5);

    /// <summary>The media's sample, and its crossfade's next one, into its dest at its opacity, under the canvas's clip.</summary>
    private static void DrawSamples(SKCanvas canvas, RMedia m)
    {
        var count = canvas.Save();
        var sampling = m.Nearest ? Nearest : Linear;
        var layered = m.Opacity < 1 && m.Over is not null;
        var opacity = m.Opacity;
        if (layered)
        {
            using var lp = new SKPaint { Color = new SKColor(255, 255, 255, Alpha(opacity)) };
            canvas.SaveLayer(m.Dest, lp);
            opacity = 1;
        }
        using (var paint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, Alpha(opacity)) })
            m.Sample!.Draw(canvas, m.Dest, sampling, paint);
        if (m.Over is { } over && m.FadeOpacity > 0)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, Alpha(opacity * m.FadeOpacity)) };
            over.Draw(canvas, m.Dest, sampling, paint);
        }
        canvas.RestoreToCount(count);
    }
}

/// <summary>
/// The panel's side of a frame drawn above scale 1 (<see cref="SceneDrawer.Panel"/>, made and disposed by
/// <see cref="TickSnapshot.Draw(SKCanvas, float, bool)"/>): the matrix the frame at scale 1 draws each node under, and
/// the one picture of the canvas's pixels its raster media is drawn into in turn, and the one enlargement of them.
/// </summary>
internal sealed class PanelFrame(int width, int height) : IDisposable
{
    /// <summary>
    /// A canvas that draws nothing, onto which every matrix concatenated onto the frame's canvas after its scale (an
    /// item's placement, a group's transform) is concatenated too, from identity. Its matrix is then the one the frame at
    /// scale 1 draws the node under, computed as that frame's canvas computes it, to the last bit: one recovered from the
    /// scaled matrix by cancelling the scale is a rounding apart, enough to move a nearest-sampled pixel.
    /// </summary>
    private readonly SKNoDrawCanvas _matrices = new(Math.Max(1, width), Math.Max(1, height));

    private SKBitmap? _pixels, _enlarged;

    public int Width { get; } = width;
    public int Height { get; } = height;

    public SKMatrix Matrix => _matrices.TotalMatrix;

    public int Save() => _matrices.Save();

    public void Concat(in SKMatrix m) => _matrices.Concat(in m);

    public void RestoreToCount(int count) => _matrices.RestoreToCount(count);

    /// <summary>
    /// The canvas-size picture, made the first time raster media needs it and kept for the rest of the frame (each media
    /// clears what it covers), so a frame allocates one however many rasters it has; null when it would be over
    /// <paramref name="bound"/>.
    /// </summary>
    public SKBitmap? Pixels(long bound)
    {
        if (_pixels is not null) return _pixels;
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (info.BytesSize64 > bound) return null;
        SceneDrawer.PictureProbe?.Invoke(info);
        return _pixels = new SKBitmap(info);
    }

    /// <summary>
    /// The sides of the frame at scale <paramref name="s"/> capped as an output caps it
    /// (<see cref="TickSnapshot.EffectiveScale(float)"/>): the frame pixels showing the canvas, ⌈W × s − ½⌉ × ⌈H × s − ½⌉.
    /// What an enlargement's rounding allowance is taken from (<see cref="SceneDrawer.Fits"/>).
    /// </summary>
    public (int Width, int Height) Sides(float s)
    {
        var capped = TickSnapshot.EffectiveScale(Width, Height, s);
        return (SceneDrawer.First(Width, capped), SceneDrawer.First(Height, capped));
    }

    /// <summary>
    /// The frame-size bitmap raster media within the canvas is enlarged into at scale <paramref name="s"/> when that isn't
    /// a multiple of 0.25 (the frame pixels showing the canvas, ⌈W × s − ½⌉ × ⌈H × s − ½⌉), made the first time it is
    /// needed and kept for the rest of the frame, so a frame allocates one however many rasters it has; null when it
    /// wouldn't fit <paramref name="bound"/> (<see cref="SceneDrawer.Fits"/>).
    /// </summary>
    public SKBitmap? Enlargement(float s, long bound)
    {
        if (_enlarged is not null) return _enlarged;
        var (w, h) = (SceneDrawer.First(Width, s), SceneDrawer.First(Height, s));
        if (!SceneDrawer.Fits(w, h, Sides(s), bound)) return null;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        SceneDrawer.EnlargementProbe?.Invoke(info);
        return _enlarged = new SKBitmap(info);
    }

    public void Dispose()
    {
        _matrices.Dispose();
        _pixels?.Dispose();
        _enlarged?.Dispose();
    }
}

/// <summary>The engine's placeholders (spec/docs/unsupported.md): a hatched box with a mark for an item, a crossed box for missing media.</summary>
internal static class Placeholders
{
    public static void DrawItem(SKCanvas canvas, SKRect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var count = canvas.Save();
        canvas.ClipRect(r, SKClipOperation.Intersect, true);
        using (var fill = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80, 0x33), IsAntialias = true })
            canvas.DrawRect(r, fill);
        using (var hatch = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80, 0x66), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        {
            var span = r.Width + r.Height;
            for (float d = 0; d < span; d += 8) canvas.DrawLine(r.Left + d, r.Top, r.Left + d - r.Height, r.Bottom, hatch);
        }
        using (var border = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80, 0xCC), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            canvas.DrawRect(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), border);
        // The mark: an exclamation mark in a disc at the centre.
        var size = Math.Min(24, Math.Min(r.Width, r.Height) * 0.6f);
        if (size >= 6)
        {
            var c = new SKPoint(r.MidX, r.MidY);
            using var disc = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60, 0xDD), IsAntialias = true };
            canvas.DrawCircle(c, size / 2, disc);
            using var mark = new SKPaint { Color = SKColors.White, IsAntialias = true, StrokeWidth = size / 8, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(c.X, c.Y - size * 0.25f, c.X, c.Y + size * 0.05f, mark);
            canvas.DrawPoint(c.X, c.Y + size * 0.25f, mark);
        }
        canvas.RestoreToCount(count);
    }

    public static void DrawMissingMedia(SKCanvas canvas, SKRect r, float opacity)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var a = Math.Clamp(opacity, 0, 1);
        using var fill = new SKPaint { Color = new SKColor(0x9C, 0xA3, 0xAF, (byte)(0x40 * a)), IsAntialias = true };
        canvas.DrawRect(r, fill);
        using var line = new SKPaint { Color = new SKColor(0x9C, 0xA3, 0xAF, (byte)(0xB0 * a)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        var inner = new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f);
        canvas.DrawRect(inner, line);
        canvas.DrawLine(inner.Left, inner.Top, inner.Right, inner.Bottom, line);
        canvas.DrawLine(inner.Right, inner.Top, inner.Left, inner.Bottom, line);
    }
}
