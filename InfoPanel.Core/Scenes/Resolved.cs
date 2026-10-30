using System;
using System.Collections.Generic;
using InfoPanel.Images;
using SkiaSharp;

namespace InfoPanel.Scenes;

// A scene evaluated for one tick: every expression replaced by its value. Immutable, so one evaluation is drawn at every
// scale, on every output and measured for bounds, from any thread. Skia objects are created while drawing and disposed
// right after, except the static paths of path nodes and clips, which belong to the compiled scene, and the path a
// dashed outline is drawn from, built while evaluating and released with the tick; both are only read once built.

/// <summary>An outline in a node's coordinates.</summary>
internal abstract class RGeom
{
    /// <summary>The outline's bounds (control points for curves of static paths), without any stroke.</summary>
    public abstract SKRect Bounds { get; }

    private SKPath? _drawn;
    private (double Length, int Contours, bool Curved) _measured;

    /// <summary>
    /// The path a dashed stroke of this outline is drawn from: built and measured once by <see cref="Measure"/>, then
    /// only read, at every scale and on every output; null before.
    /// </summary>
    public virtual SKPath? Drawn => _drawn;

    /// <summary>
    /// Builds the path a dashed stroke of the outline is drawn from (<see cref="ToPath"/>, once; <paramref name="owner"/>
    /// disposes it with the tick) and measures it for the dash budget (<see cref="SceneMath.Outline"/>): its control
    /// polygon's length, at least what Skia strokes at any resolution, and never negative, so lengths summed over
    /// several outlines never cancel; its contours, on each of which a dash pattern starts again; and whether it has
    /// curves, which Skia trims by parameter, so only an outline without them keeps the trim fraction of its length.
    /// A length of 0 is a zero-length outline, which draws undashed.
    /// </summary>
    public virtual (double Length, int Contours, bool Curved) Measure(List<IDisposable> owner)
    {
        if (_drawn is null)
        {
            var path = ToPath();
            _ = path.Bounds; // computed now, so drawing from several threads never mutates the path
            owner.Add(path);
            _measured = SceneMath.Outline(path);
            _drawn = path;
        }
        return _measured;
    }

    /// <summary>What <see cref="Measure"/> measured; only read once <see cref="Drawn"/> is set.</summary>
    public virtual (double Length, int Contours, bool Curved) Measured => _measured;

    /// <summary>Whether the outline has corners where a miter join can reach past the stroke's half width.</summary>
    public virtual bool HasCorners => true;

    /// <summary>A new path for the outline; the caller disposes it.</summary>
    public abstract SKPath ToPath();

    /// <summary>Draws the outline with a paint that has no path effect, when a faster call than a path exists.</summary>
    public virtual bool TryDrawFast(SKCanvas canvas, SKPaint paint) => false;
}

internal sealed class RectGeom(float x, float y, float w, float h, SKPoint[]? radii) : RGeom
{
    public SKRect Rect { get; } = new(x, y, x + w, y + h);

    /// <summary>Top-left, top-right, bottom-right, bottom-left; null for square corners.</summary>
    public SKPoint[]? Radii { get; } = radii;

    public override SKRect Bounds => Rect;
    public override bool HasCorners => false; // right angles: a miter reaches exactly the rectangle grown by half the width

    public override SKPath ToPath()
    {
        var p = new SKPath();
        if (Radii is null) p.AddRect(Rect);
        else
        {
            using var rr = new SKRoundRect();
            rr.SetRectRadii(Rect, Radii);
            p.AddRoundRect(rr);
        }
        return p;
    }

    public override bool TryDrawFast(SKCanvas canvas, SKPaint paint)
    {
        if (Radii is null)
        {
            canvas.DrawRect(Rect, paint);
            return true;
        }
        using var rr = new SKRoundRect();
        rr.SetRectRadii(Rect, Radii);
        canvas.DrawRoundRect(rr, paint);
        return true;
    }
}

internal sealed class EllipseGeom(float cx, float cy, float rx, float ry) : RGeom
{
    public SKRect Oval { get; } = new(cx - rx, cy - ry, cx + rx, cy + ry);
    public override SKRect Bounds => Oval;
    public override bool HasCorners => false;

    public override SKPath ToPath()
    {
        var p = new SKPath();
        p.AddOval(Oval);
        return p;
    }

    public override bool TryDrawFast(SKCanvas canvas, SKPaint paint)
    {
        canvas.DrawOval(Oval, paint);
        return true;
    }
}

/// <summary>An arc: a sector (ring segment or pie) or, when <see cref="Open"/>, the curve at <see cref="R"/>.</summary>
internal sealed class ArcGeom(float cx, float cy, float r, float inner, float start, float sweep, bool open) : RGeom
{
    public float Cx { get; } = cx;
    public float Cy { get; } = cy;
    public float R { get; } = r;
    public float Inner { get; } = inner;
    public float Start { get; } = start;
    public float Sweep { get; } = sweep;
    public bool Open { get; } = open;

    public override bool HasCorners => !Open && Math.Abs(Sweep) < 360;

    public override SKRect Bounds
    {
        get
        {
            var b = SKRect.Empty;
            var first = true;
            void Add(double x, double y)
            {
                var px = (float)x;
                var py = (float)y;
                if (first)
                {
                    b = new SKRect(px, py, px, py);
                    first = false;
                }
                else b = SKRect.Union(b, new SKRect(px, py, px, py));
            }
            var a0 = Math.Min(Start, Start + Sweep);
            var a1 = Math.Max(Start, Start + Sweep);
            foreach (var radius in Open ? new[] { R } : new[] { R, Inner })
            {
                Add(Cx + radius * SceneMath.CosDeg(a0), Cy + radius * SceneMath.SinDeg(a0));
                Add(Cx + radius * SceneMath.CosDeg(a1), Cy + radius * SceneMath.SinDeg(a1));
                for (var k = Math.Ceiling(a0 / 90) * 90; k <= a1; k += 90)
                    Add(Cx + radius * SceneMath.CosDeg(k), Cy + radius * SceneMath.SinDeg(k));
            }
            if (!Open && Inner <= 0) Add(Cx, Cy);
            return b;
        }
    }

    public override SKPath ToPath()
    {
        var p = new SKPath();
        var outer = new SKRect(Cx - R, Cy - R, Cx + R, Cy + R);
        var full = Math.Abs(Sweep) >= 360;
        if (Open)
        {
            p.AddArc(outer, Start, Sweep);
            return p;
        }
        if (Inner > 0)
        {
            var inner = new SKRect(Cx - Inner, Cy - Inner, Cx + Inner, Cy + Inner);
            if (full)
            {
                p.AddArc(outer, Start, Sweep);
                p.AddArc(inner, Start + Sweep, -Sweep);
                return p;
            }
            p.ArcTo(outer, Start, Sweep, true);
            p.ArcTo(inner, Start + Sweep, -Sweep, false);
            p.Close();
            return p;
        }
        if (full)
        {
            p.AddArc(outer, Start, Sweep);
            return p;
        }
        p.MoveTo(Cx, Cy);
        p.ArcTo(outer, Start, Sweep, false);
        p.Close();
        return p;
    }
}

internal sealed class LineGeom(float x1, float y1, float x2, float y2) : RGeom
{
    public SKPoint P1 { get; } = new(x1, y1);
    public SKPoint P2 { get; } = new(x2, y2);
    public override SKRect Bounds => new(Math.Min(P1.X, P2.X), Math.Min(P1.Y, P2.Y), Math.Max(P1.X, P2.X), Math.Max(P1.Y, P2.Y));
    public override bool HasCorners => false;

    public override SKPath ToPath()
    {
        var p = new SKPath();
        p.MoveTo(P1);
        p.LineTo(P2);
        return p;
    }

    public override bool TryDrawFast(SKCanvas canvas, SKPaint paint)
    {
        if (paint.Style == SKPaintStyle.Fill) return true; // a line has no area
        canvas.DrawLine(P1, P2, paint);
        return true;
    }
}

/// <summary>
/// A static path (a path node's or clip's <c>d</c>), parsed and measured once per compiled scene and only read
/// afterwards: it is the path drawn, with or without a path effect.
/// </summary>
internal sealed class StaticPathGeom : RGeom
{
    private readonly SKPath _path;
    private readonly (double Length, int Contours, bool Curved) _outline;

    private StaticPathGeom(SKPath path)
    {
        _path = path;
        _outline = SceneMath.Outline(path);
        Bounds = path.Bounds; // computed now, so drawing never mutates the shared path
    }

    public override SKRect Bounds { get; }
    public override SKPath? Drawn => _path;
    public override (double Length, int Contours, bool Curved) Measure(List<IDisposable> owner) => _outline;
    public override (double Length, int Contours, bool Curved) Measured => _outline;

    public override SKPath ToPath() => new(_path);

    public override bool TryDrawFast(SKCanvas canvas, SKPaint paint)
    {
        canvas.DrawPath(_path, paint);
        return true;
    }

    public static StaticPathGeom Parse(string d, SKPathFillType fill)
    {
        var path = PathParser.Parse(d);
        path.FillType = fill;
        return new StaticPathGeom(path);
    }
}

internal sealed class PolyGeom(SKPoint[] points, bool closed, SKPathFillType fill) : RGeom
{
    public SKPoint[] Points { get; } = points;
    public bool Closed { get; } = closed;
    public SKPathFillType Fill { get; } = fill;

    public override SKRect Bounds
    {
        get
        {
            if (Points.Length == 0) return SKRect.Empty;
            float l = Points[0].X, t = Points[0].Y, r = l, b = t;
            foreach (var p in Points)
            {
                l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
            }
            return new SKRect(l, t, r, b);
        }
    }

    public override SKPath ToPath()
    {
        var p = new SKPath { FillType = Fill };
        if (Points.Length < 2) return p;
        p.AddPoly(Points, Closed);
        return p;
    }
}

/// <summary>A paint for one tick.</summary>
internal abstract class RPaint
{
    /// <summary>A new shader (the caller disposes it), or null for a solid colour (<see cref="SolidColor"/>).</summary>
    public abstract SKShader? CreateShader();

    public virtual uint? SolidColor => null;
}

internal sealed class RSolid(uint color) : RPaint
{
    public uint Color { get; } = color;
    public override SKShader? CreateShader() => null;
    public override uint? SolidColor => Color;
}

internal enum GradientKind : byte { Linear, Radial, Sweep }

/// <summary>
/// A gradient. Stops are in the order given with positions clamped to 0–1 and made non-decreasing. The format
/// interpolates premultiplied; Skia's gradients interpolate straight colours, so between stops whose alpha differs the
/// stops are subdivided with premultiplied interpolation (within a channel step of the exact result).
/// </summary>
internal sealed class RGradient(GradientKind kind, float[] geometry, uint[] colors, float[] positions, SKShaderTileMode tile) : RPaint
{
    private const int Subdivisions = 8;

    public GradientKind Kind { get; } = kind;

    /// <summary>Linear: x1, y1, x2, y2. Radial: cx, cy, r. Sweep: cx, cy, start, end.</summary>
    public float[] Geometry { get; } = geometry;

    public uint[] Colors { get; } = colors;
    public float[] Positions { get; } = positions;
    public SKShaderTileMode Tile { get; } = tile;

    public override SKShader? CreateShader()
    {
        var (colors, positions) = Expand();
        var g = Geometry;
        switch (Kind)
        {
            case GradientKind.Linear:
            {
                var p1 = new SKPoint(g[0], g[1]);
                var p2 = new SKPoint(g[2], g[3]);
                if (SKPoint.Distance(p1, p2) < 1e-6f) return SKShader.CreateColor(colors[^1]);
                return SKShader.CreateLinearGradient(p1, p2, colors, positions, Tile);
            }
            case GradientKind.Radial:
                if (!(g[2] > 1e-6f)) return SKShader.CreateColor(colors[^1]);
                return SKShader.CreateRadialGradient(new SKPoint(g[0], g[1]), g[2], colors, positions, Tile);
            default:
            {
                // Angles as for arcs, measured from start: rotate the shader to start and sweep 0…|end − start|,
                // mirrored when end < start, so a range across 0° (135…405) is continuous.
                var span = g[3] - g[2];
                if (Math.Abs(span) < 1e-6f) return SKShader.CreateColor(colors[^1]);
                var c = new SKPoint(g[0], g[1]);
                var m = SKMatrix.CreateRotationDegrees(g[2], c.X, c.Y);
                if (span < 0) m = m.PreConcat(SKMatrix.CreateScale(1, -1, c.X, c.Y));
                return SKShader.CreateSweepGradient(c, colors, positions, Tile, 0, Math.Abs(span), m);
            }
        }
    }

    private (SKColor[] Colors, float[] Positions) Expand()
    {
        var colors = new List<SKColor>(Colors.Length * 2);
        var positions = new List<float>(Colors.Length * 2);
        for (var i = 0; i < Colors.Length; i++)
        {
            if (i > 0 && SceneColor.A(Colors[i - 1]) != SceneColor.A(Colors[i]) && Positions[i] > Positions[i - 1])
            {
                for (var k = 1; k < Subdivisions; k++)
                {
                    var t = k / (float)Subdivisions;
                    colors.Add(PremulLerp(Colors[i - 1], Colors[i], t));
                    positions.Add(Positions[i - 1] + (Positions[i] - Positions[i - 1]) * t);
                }
            }
            colors.Add(SceneColor.ToSk(Colors[i]));
            positions.Add(Positions[i]);
        }
        return (colors.ToArray(), positions.ToArray());
    }

    private static SKColor PremulLerp(uint a, uint b, float t)
    {
        double aa = SceneColor.A(a) / 255.0, ba = SceneColor.A(b) / 255.0;
        var alpha = aa + (ba - aa) * t;
        if (alpha <= 0) return new SKColor(0, 0, 0, 0);
        byte Ch(byte x, byte y) => (byte)Math.Clamp(Math.Round((x * aa + (y * ba - x * aa) * t) / alpha), 0, 255);
        return new SKColor(Ch(SceneColor.R(a), SceneColor.R(b)), Ch(SceneColor.G(a), SceneColor.G(b)), Ch(SceneColor.B(a), SceneColor.B(b)),
            (byte)Math.Round(alpha * 255));
    }
}

internal sealed class RCompose(SKBlendMode blend, RPaint dst, RPaint src) : RPaint
{
    public override SKShader? CreateShader()
    {
        using var d = dst.CreateShader() ?? SKShader.CreateColor(SceneColor.ToSk(dst.SolidColor ?? 0));
        using var s = src.CreateShader() ?? SKShader.CreateColor(SceneColor.ToSk(src.SolidColor ?? 0));
        return SKShader.CreateCompose(d, s, blend);
    }
}

internal sealed record RStroke(RPaint Paint, float Width, SKStrokeCap Cap, SKStrokeJoin Join, float Miter, float[]? Dash, float DashOffset, float TrimStart, float TrimEnd)
{
    public bool Trimmed => TrimStart > 0 || TrimEnd < 1;

    /// <summary>How far the stroke reaches past the outline (conservative): half the width, more for square caps and miters.</summary>
    public float Reach(RGeom geom) => Reach(geom.HasCorners);

    /// <summary>As <see cref="Reach(RGeom)"/>, for an outline that has corners (glyphs) or not.</summary>
    public float Reach(bool corners)
    {
        var half = Width / 2;
        var reach = half;
        if (Cap == SKStrokeCap.Square) reach = half * 1.4143f;
        if (Join == SKStrokeJoin.Miter && corners) reach = Math.Max(reach, half * Math.Max(1, Miter));
        return reach;
    }

    /// <summary>
    /// The floor of a dash or gap, in device pixels (spec/docs/scene.md, strokes): each length of a pattern is at least
    /// this after the stroke's whole transform, so the dashes Skia draws are bounded by the outline's device length.
    /// </summary>
    public const double DashFloor = 0.125;

    /// <summary>
    /// The shortest dash or gap, in the stroke's coordinates, where they are drawn at most <paramref name="scale"/>
    /// device pixels a unit: <see cref="DashFloor"/> device pixels, at most 10⁹ (a collapsed transform); 0 when the
    /// scale isn't known (not a number), so nothing is floored.
    /// </summary>
    public static double DashFloorAt(double scale) => double.IsNaN(scale) ? 0 : Math.Min(DashFloor / Math.Max(0, scale), 1e9);

    /// <summary>The pattern drawn at <paramref name="scale"/> device pixels a unit: every length floored (<see cref="DashFloorAt"/>).</summary>
    public float[]? DashAt(double scale)
    {
        if (Dash is not { } dash) return null;
        var floor = DashFloorAt(scale);
        var drawn = new float[dash.Length];
        for (var i = 0; i < dash.Length; i++) drawn[i] = (float)Math.Max(dash[i], floor);
        return drawn;
    }

    /// <summary>
    /// The dash segments this stroke draws along <paramref name="contours"/> contours of <paramref name="length"/> in
    /// all, drawn at no more than <paramref name="scale"/> device pixels a unit (along the transform's largest stretch);
    /// 0 without a dash. The pattern is the floored one, so the count is at most the outline's length times the scale
    /// over the floor however fine the pattern; a larger scale never gives fewer. Dashes follow the trimmed outline
    /// (trimming comes first): an outline without curves keeps the trim fraction of its length exactly, a
    /// <paramref name="curved"/> one is counted whole, since Skia trims curves by parameter at its default resolution,
    /// which can keep more than the fraction. The pattern starts again on each contour, so each counts at least one
    /// (partial) period.
    /// </summary>
    public double DashSegments(double length, double contours, double scale, bool curved)
    {
        if (Dash is not { Length: > 0 } dash) return 0;
        var floor = DashFloorAt(scale);
        double sum = 0;
        foreach (var d in dash) sum += Math.Max(d, floor);
        var kept = curved ? 1 : TrimEnd - TrimStart;
        return (length * kept / sum + Math.Max(1, contours)) * dash.Length;
    }
}

internal enum EffectKind : byte { Blur, Shadow, Glow }

internal sealed record REffect(EffectKind Kind, float Dx, float Dy, float Sigma, uint Color)
{
    /// <summary>How far the effect reaches past the content: 3 sigma, plus the offset for a shadow.</summary>
    public float Reach => 3 * Sigma + (Kind == EffectKind.Shadow ? Math.Max(Math.Abs(Dx), Math.Abs(Dy)) : 0);
}

/// <summary>A node evaluated for a tick, in its parent's coordinates.</summary>
internal abstract class RNode
{
    public float Opacity = 1;
}

internal sealed class RGroup : RNode
{
    public SKMatrix Matrix = SKMatrix.Identity;
    public RGeom? Clip;
    public SKBlendMode Blend = SKBlendMode.SrcOver;
    public REffect[] Effects = [];
    public RNode[] Children = [];

    /// <summary>Set for a series or table: its box stands for its content in bounds.</summary>
    public SKRect? BoundsOverride;

    public bool NeedsLayer => Effects.Length > 0 || Opacity < 1 || Blend != SKBlendMode.SrcOver;
}

internal sealed class RShape : RNode
{
    public required RGeom Geom;
    public RPaint? Fill;
    public RStroke? Stroke;
}

/// <summary>A text node: its layout (shared across ticks while its inputs are unchanged) placed at <see cref="Left"/>, <see cref="Top"/>.</summary>
internal sealed class RText : RNode
{
    public required TextLayout Layout;
    public float Left, Top;

    /// <summary>The width lines align within: the box when there is one, otherwise the text's own width.</summary>
    public float AlignWidth;

    public float AlignFraction;
    public required RPaint?[] SpanFills;
    public RStroke? Stroke;

    /// <summary>The box, for overflow clip and marquee; null when the text is drawn unclipped.</summary>
    public SKRect? ClipRect;

    /// <summary>For a scrolling marquee: the scroll offset this tick and the distance between the copies.</summary>
    public float MarqueeOffset;
    public float MarqueePeriod;
    public bool Marquee;

    /// <summary>Per span: underline and strike-through.</summary>
    public bool[] Underline = [], Strike = [];

    /// <summary>The node's outline for bounds: the box on each axis it has one, otherwise the lines.</summary>
    public SKRect Outline;

    /// <summary>Whether the text fits its box (a text node; a table's cells are <see cref="TextFit.Fits"/>), for <see cref="TickSnapshot.Texts"/>.</summary>
    public TextFit Fit;

    /// <summary>The runs laid out (shared with the layout's key), for the report's text.</summary>
    public TextRun[] Runs = [];

    /// <summary>Where the lines are drawn, in the node's coordinates (within <see cref="ClipRect"/> when clipped).</summary>
    public SKRect Lines;

    private SKRect[]? _glyphs; // an array, so a racing first read publishes it atomically

    /// <summary>Where glyphs may draw (the blobs' conservative bounds and the outline), for layer bounds.</summary>
    public SKRect InkHint
    {
        get
        {
            if (_glyphs is { } known) return known[0];
            var r = Outline;
            if (ClipRect is null)
            {
                foreach (var line in Layout.Lines)
                {
                    var lx = Left + (AlignWidth - line.Width) * AlignFraction;
                    var by = Top + line.Baseline;
                    foreach (var run in line.Runs)
                    {
                        var b = run.Blob.Bounds;
                        b.Offset(lx, by);
                        r = SKRect.Union(r, b);
                    }
                }
                if (Stroke is { } s)
                {
                    var reach = Math.Max(s.Width, s.Reach(corners: true));
                    r.Inflate(reach, reach);
                }
            }
            _glyphs = [r];
            return r;
        }
    }
}

internal sealed class RMedia : RNode
{
    public MediaSample? Sample;

    /// <summary>For a crossfading sequence: the next frame, drawn over <see cref="Sample"/> at <see cref="FadeOpacity"/>.</summary>
    public MediaSample? Over;
    public float FadeOpacity;

    public SKRect Dest;

    /// <summary>The box; media is clipped to it when it covers or is drawn at natural size.</summary>
    public SKRect Box;
    public bool ClipToBox;
    public bool Nearest;

    /// <summary>Nothing to show yet or at all: the missing-media placeholder fills <see cref="Box"/>.</summary>
    public bool Missing => Sample is null;
}
