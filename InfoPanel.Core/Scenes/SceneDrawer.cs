using System;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>
/// Draws resolved nodes with Skia (spec/docs/scene.md, "Drawing model"): painter's order, source-over, antialiased,
/// everything through the canvas matrix, so one evaluation draws the same at every scale. Never writes to what it draws.
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
        if (!g.Matrix.IsIdentity) canvas.Concat(in g.Matrix);
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
        canvas.RestoreToCount(count);
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
        var count = canvas.Save();
        if (m.ClipToBox) canvas.ClipRect(m.Box, SKClipOperation.Intersect, true);
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
