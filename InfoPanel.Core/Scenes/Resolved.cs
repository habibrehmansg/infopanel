using System;
using System.Buffers;
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
    /// <summary>The outline's bounds (its curves' own extent, not their control points'), without any stroke.</summary>
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

    /// <summary>
    /// The bounds of the outline stroked by <paramref name="stroke"/>, as tight as the outline allows (the content box,
    /// spec/docs/geometry.md): half the width past a smooth outline, a miter's tip only at a corner where it is drawn, a
    /// square cap only at an open end. This default is the conservative reach all round.
    /// </summary>
    public virtual SKRect StrokedBounds(RStroke stroke) => Conservative(stroke);

    /// <summary>The outline's bounds grown by the stroke's conservative reach (<see cref="RStroke.Reach(RGeom)"/>) on every side.</summary>
    public SKRect Conservative(RStroke stroke)
    {
        var r = Bounds;
        var reach = stroke.Reach(this);
        r.Inflate(reach, reach);
        return r;
    }

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

    // Closed, so no caps: half the width on every side whatever the join (a dash's square cap may reach further).
    public override SKRect StrokedBounds(RStroke stroke) => stroke.CapsAnywhere ? Conservative(stroke) : StrokeBox.Grown(Rect, stroke);

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
    public override SKRect StrokedBounds(RStroke stroke) => stroke.CapsAnywhere ? Conservative(stroke) : StrokeBox.Grown(Oval, stroke);

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

    public override SKRect StrokedBounds(RStroke stroke)
    {
        if (stroke.CapsAnywhere || !float.IsFinite(Start) || !float.IsFinite(Sweep)) return Conservative(stroke);
        var box = new StrokeBox(stroke);
        var end = Start + Sweep;
        var (a0, a1) = (Math.Min(Start, end), Math.Max(Start, end));
        var full = Math.Abs(Sweep) >= 360;
        // A full sweep not started on a quarter is drawn as an arc that ends where it starts, not closed (SKPath.AddArc),
        // so a square cap is drawn there: the conservative reach. A round cap stays inside the outer circle's band.
        if (full && stroke.Cap == SKStrokeCap.Square) return Conservative(stroke);
        var c = new SKPoint(Cx, Cy);
        // The point at radius r and angle a, the unit radius there, and the unit tangent along the sweep.
        SKPoint At(float r, float a) => new((float)(Cx + r * SceneMath.CosDeg(a)), (float)(Cy + r * SceneMath.SinDeg(a)));
        SKPoint Out(float a) => new((float)SceneMath.CosDeg(a), (float)SceneMath.SinDeg(a));
        var s = Sweep < 0 ? -1f : 1f;
        SKPoint Along(float a) => new((float)(-s * SceneMath.SinDeg(a)), (float)(s * SceneMath.CosDeg(a)));
        SKPoint Neg(SKPoint p) => new(-p.X, -p.Y);

        box.Band(c, R, a0, a1, full);
        if (Open)
        {
            if (!full)
            {
                box.Cap(At(R, Start), Neg(Along(Start)));
                box.Cap(At(R, end), Along(end));
            }
            return box.Box;
        }
        if (full)
        {
            // Closed and smooth: the ring's (or the disc's) outer circle, its inner one inside it.
            if (Inner > 0) box.Band(c, Inner, a0, a1, full: true);
            return box.Box;
        }
        if (Sweep == 0) return Conservative(stroke);
        // A ring segment (outer arc, end, inner arc back, start) or a pie (centre, outer arc, back): closed, joined at each corner.
        if (Inner > 0)
        {
            box.Band(c, Inner, a0, a1, full: false);
            SKPoint o0 = At(R, Start), o1 = At(R, end), i0 = At(Inner, Start), i1 = At(Inner, end);
            box.Segment(o1, i1);
            box.Segment(i0, o0);
            box.Join(o1, Along(end), Neg(Out(end)));
            box.Join(i1, Neg(Out(end)), Neg(Along(end)));
            box.Join(i0, Neg(Along(Start)), Out(Start));
            box.Join(o0, Out(Start), Along(Start));
        }
        else
        {
            SKPoint o0 = At(R, Start), o1 = At(R, end);
            box.Segment(c, o0);
            box.Segment(o1, c);
            box.Join(o0, Out(Start), Along(Start));
            box.Join(o1, Along(end), Neg(Out(end)));
            box.Join(c, Neg(Out(end)), Out(Start));
        }
        return box.Box;
    }

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

    public override SKRect StrokedBounds(RStroke stroke)
    {
        // A line whose ends coincide draws as its caps: the conservative reach.
        if (stroke.CapsAnywhere || StrokeBox.Short(P1, P2)) return Conservative(stroke);
        var box = new StrokeBox(stroke);
        box.Segment(P1, P2);
        box.Cap(P1, StrokeBox.Unit(P1 - P2));
        box.Cap(P2, StrokeBox.Unit(P2 - P1));
        return box.Box;
    }

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
        _ = path.Bounds; // computed now, so drawing never mutates the shared path
        // The curves' own extent: an arc's control points lie up to 1.41 radii from its centre, its curve doesn't. Computed
        // from the curves, not the TightBounds property, which can drop a curve that doubles back to where it starts.
        Bounds = path.ComputeTightBounds();
    }

    public override SKRect Bounds { get; }

    /// <summary>
    /// A stroke measured: the stroke, the matrix it was mapped through without its translation (the identity for one that
    /// only scales and moves, mapped afterwards) and its box (null: nothing stroked).
    /// </summary>
    private sealed record Stroked(float Width, SKStrokeCap Cap, SKStrokeJoin Join, float Miter, SKMatrix Linear, bool Exact, SKRect? Box);

    /// <summary>The strokes measured, newest first, at most <see cref="OutlinesKeptAtMost"/>.</summary>
    private Stroked[] _stroked = []; // replaced whole, so a racing read sees one list or another

    internal const int OutlinesKeptAtMost = 4;

    /// <summary>How many strokes were measured, not found among those kept (a test's count).</summary>
    internal int OutlinesBuilt;

    internal int OutlinesKept => _stroked.Length;

    /// <summary>The stroke's own bounds, from the path's curves (<see cref="StrokeBox.Swept"/>).</summary>
    public override SKRect StrokedBounds(RStroke stroke) => StrokedBounds(stroke, SKMatrix.Identity) ?? Conservative(stroke);

    /// <summary>
    /// The bounds of the path stroked by <paramref name="stroke"/>, mapped through <paramref name="to"/>; null when it
    /// strokes nothing. Measured from the path's curves (<see cref="StrokeBox.Swept"/>), not from the outline Skia strokes,
    /// which can lose reach for a stroke wider than a curve's bend, where <paramref name="to"/> scales within the bound
    /// that measure is verified at (<see cref="StrokeBox.SweptHolds"/>); past it, from the path's control points grown by
    /// the stroke's whole reach and a margin (<see cref="StrokeBox.Hull"/>), loose but holding what Skia paints. Kept for
    /// the last few strokes and matrices, so a static path is measured once, not every tick. A matrix that only scales and
    /// moves maps the box; for one that turns or skews the box is measured through it, its translation added after.
    /// </summary>
    public SKRect? StrokedBounds(RStroke stroke, in SKMatrix to)
    {
        if (stroke.CapsAnywhere) return SceneBounds.Map(to, Conservative(stroke));
        if (to.Persp0 != 0 || to.Persp1 != 0) return StrokeBox.Outline(_path, stroke, to);
        var aligned = to.SkewX == 0 && to.SkewY == 0;
        var linear = aligned ? SKMatrix.Identity : new SKMatrix(to.ScaleX, to.SkewX, 0, to.SkewY, to.ScaleY, 0, 0, 0, 1);
        var exact = StrokeBox.SweptHolds(to);
        SKRect? box = null;
        var found = false;
        var kept = _stroked;
        foreach (var k in kept)
        {
            if (k.Width != stroke.Width || k.Cap != stroke.Cap || k.Join != stroke.Join || k.Miter != stroke.Miter || k.Linear != linear || k.Exact != exact)
                continue;
            (box, found) = (k.Box, true);
            break;
        }
        if (!found)
        {
            System.Threading.Interlocked.Increment(ref OutlinesBuilt);
            box = exact ? StrokeBox.Swept(_path, stroke, linear) : StrokeBox.Hull(_path, stroke, linear);
            var next = new Stroked[Math.Min(kept.Length + 1, OutlinesKeptAtMost)];
            next[0] = new Stroked(stroke.Width, stroke.Cap, stroke.Join, stroke.Miter, linear, exact, box);
            Array.Copy(kept, 0, next, 1, next.Length - 1);
            _stroked = next;
        }
        if (box is not { } b) return null;
        var mapped = aligned ? SceneBounds.Map(to, b) : new SKRect(b.Left + to.TransX, b.Top + to.TransY, b.Right + to.TransX, b.Bottom + to.TransY);
        if (!exact) mapped.Inflate(StrokeBox.HullPixels, StrokeBox.HullPixels);
        return mapped;
    }

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

    /// <summary>Each segment's band, a join at each corner and a cap at each open end.</summary>
    public override SKRect StrokedBounds(RStroke stroke)
    {
        if (stroke.CapsAnywhere || Points.Length < 2) return Conservative(stroke);
        // The distinct points in order (a point on the one before draws no segment; closed, the last isn't the first).
        var rented = ArrayPool<SKPoint>.Shared.Rent(Points.Length);
        try
        {
            var n = 0;
            foreach (var p in Points)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) return Conservative(stroke);
                if (n == 0 || p != rented[n - 1]) rented[n++] = p;
            }
            if (Closed && n > 1 && rented[n - 1] == rented[0]) n--;
            if (n < 2) return Conservative(stroke); // a zero-length outline draws as its caps and joins
            var pts = rented.AsSpan(0, n);
            var box = new StrokeBox(stroke);
            var segments = Closed ? n : n - 1;
            for (var i = 0; i < segments; i++) box.Segment(pts[i], pts[(i + 1) % n]);
            for (var i = Closed ? 0 : 1; i < (Closed ? n : n - 1); i++)
            {
                var prev = pts[(i - 1 + n) % n];
                var next = pts[(i + 1) % n];
                box.Corner(prev, pts[i], next);
            }
            if (!Closed)
            {
                box.End(pts[0], pts[1]);
                box.End(pts[n - 1], pts[n - 2]);
            }
            return box.Box;
        }
        finally
        {
            ArrayPool<SKPoint>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// The bounds of a stroked outline built from its pieces, in the outline's coordinates: each straight segment's band
/// (its ends grown by half the width across it), each arc's band, a join at each corner (a round join's disc, a miter's
/// tip where Skia draws one, nothing more for a bevel) and a cap at each open end (a square's corners, a round's disc).
/// </summary>
internal struct StrokeBox(RStroke stroke)
{
    private readonly float _half = stroke.Width / 2;
    private readonly RStroke _stroke = stroke;
    private bool _any;

    public SKRect Box;

    /// <summary>A segment shorter than this is one Skia's stroker may treat as no segment, so its corners take the conservative reach.</summary>
    private const float Tiny = 1f / 1024;

    public static bool Short(SKPoint a, SKPoint b) => Math.Abs(a.X - b.X) < Tiny && Math.Abs(a.Y - b.Y) < Tiny;

    public static SKPoint Unit(SKPoint v)
    {
        var len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return len > 0 ? new SKPoint(v.X / len, v.Y / len) : SKPoint.Empty;
    }

    /// <summary><paramref name="r"/> grown by half the stroke's width: a closed outline without corners past right angles.</summary>
    public static SKRect Grown(SKRect r, RStroke stroke)
    {
        var half = stroke.Width / 2;
        r.Inflate(half, half);
        return r;
    }

    /// <summary>How far <paramref name="m"/> stretches a unit length at most (its larger column).</summary>
    public static float Scale(in SKMatrix m) =>
        Math.Max(MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY), MathF.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY));

    /// <summary>
    /// The tight bounds of <paramref name="path"/> stroked by <paramref name="stroke"/> (its dashes aside), mapped through
    /// <paramref name="m"/>: the outline Skia strokes, at a resolution of 4 times the mapped scale (at least 4); null when
    /// it strokes nothing. Skia approximates a curve's offsets within a quarter of a unit over the resolution, so the box
    /// is grown by that tolerance, more for a wider stroke: a small fraction of a pixel where it is drawn.
    /// </summary>
    public static SKRect? Outline(SKPath path, RStroke stroke, SKMatrix m)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = stroke.Width, StrokeCap = stroke.Cap, StrokeJoin = stroke.Join, StrokeMiter = stroke.Miter,
        };
        using var outline = new SKPath();
        var scale = Scale(m);
        var resolution = 4 * Math.Max(1, float.IsFinite(scale) ? scale : 1);
        if (!paint.GetFillPath(path, outline, resolution) || outline.IsEmpty) return null;
        if (!m.IsIdentity) outline.Transform(m);
        var box = outline.ComputeTightBounds();
        if (!(float.IsFinite(box.Left) && float.IsFinite(box.Top) && float.IsFinite(box.Right) && float.IsFinite(box.Bottom))) return null;
        var margin = (1 + stroke.Width / 16) / (4 * resolution); // in the path's units
        box.Inflate(margin * (Math.Abs(m.ScaleX) + Math.Abs(m.SkewX)), margin * (Math.Abs(m.SkewY) + Math.Abs(m.ScaleY)));
        return box;
    }

    /// <summary>The largest scale, along any direction, at which <see cref="Swept"/> is trusted; also the most one direction may be stretched over another.</summary>
    internal const double SweptScaleAtMost = 4;

    /// <summary>
    /// Whether <see cref="Swept"/> holds what Skia paints through <paramref name="m"/> (its linear part): it stretches no
    /// direction more than <see cref="SweptScaleAtMost"/> times, nor one more than that many times another. Past it Skia's
    /// stroker departs from the ideal stroke by more than the pixel oracle's slack once the departure is scaled up: it
    /// strokes a curve as its chord, loses a round cap's reach and strays from a curve's offset by many times its
    /// tolerance (seen at 1000 times along one axis; the oracle and the stretched sets pass exact up to 10).
    /// </summary>
    public static bool SweptHolds(in SKMatrix m)
    {
        // The linear part's singular values: its most and least stretch.
        double a = m.ScaleX, b = m.SkewX, c = m.SkewY, d = m.ScaleY;
        var sum = a * a + b * b + c * c + d * d;
        var det = a * d - b * c;
        var root = Math.Sqrt(Math.Max(0, sum * sum - 4 * det * det));
        var most = Math.Sqrt((sum + root) / 2);
        var least = Math.Abs(det) / Math.Max(most, double.Epsilon);
        return most <= SweptScaleAtMost && most <= SweptScaleAtMost * least;
    }

    /// <summary>The device pixels <see cref="Hull"/>'s box is grown by, after it is mapped: a pixel the drawing's antialiasing touches past its outline.</summary>
    internal const float HullPixels = 1;

    /// <summary>
    /// The bounds of <paramref name="path"/> stroked by <paramref name="stroke"/> (its dashes aside), mapped through the
    /// linear <paramref name="m"/>, conservatively: the box of its contours' points, control points too, grown in the path's
    /// units by the stroke's whole reach and 1/1024 of the box's size, then mapped; null when it strokes nothing (or a point
    /// isn't finite). A curve lies within its control points' hull, and every point Skia's stroker puts on its outline lies
    /// within the stroke's reach of the curve, whatever directions it strokes it in (a chord's too) and wherever its joins
    /// and caps fall; the margin is many times the furthest Skia's offsets were seen to stray (0.006 units on a path 156
    /// across, stretched 1000 times). For the scales <see cref="SweptHolds"/> rules out, mapped and grown by
    /// <see cref="HullPixels"/> afterwards; loose there, by the curves' bulge short of their control points and the reach.
    /// </summary>
    public static SKRect? Hull(SKPath path, RStroke stroke, in SKMatrix m)
    {
        if (!(stroke.Width > 0) || !float.IsFinite(stroke.Width)) return null;
        double l = double.PositiveInfinity, t = double.PositiveInfinity, r = double.NegativeInfinity, b = double.NegativeInfinity;
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        var start = SKPoint.Empty;
        SKPathVerb verb;
        void Add(SKPoint p)
        {
            (l, t, r, b) = (Math.Min(l, p.X), Math.Min(t, p.Y), Math.Max(r, p.X), Math.Max(b, p.Y));
        }
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            var n = verb switch { SKPathVerb.Line => 2, SKPathVerb.Quad or SKPathVerb.Conic => 3, SKPathVerb.Cubic => 4, _ => 0 };
            if (verb == SKPathVerb.Move) start = pts[0];
            else if (verb == SKPathVerb.Close) Add(start);
            for (var i = 0; i < n; i++)
            {
                if (!float.IsFinite(pts[i].X) || !float.IsFinite(pts[i].Y)) return null;
                Add(pts[i]);
            }
        }
        if (!(l <= r) || !(t <= b)) return null;
        double reach = stroke.Reach(corners: true);
        var grow = reach + (Math.Max(r - l, b - t) + 2 * reach) / 1024;
        (l, t, r, b) = (l - grow, t - grow, r + grow, b + grow);
        // The grown box's corners, mapped: its box holds the mapped region.
        double xl = Math.Min(m.ScaleX * l, m.ScaleX * r) + Math.Min(m.SkewX * t, m.SkewX * b);
        double xr = Math.Max(m.ScaleX * l, m.ScaleX * r) + Math.Max(m.SkewX * t, m.SkewX * b);
        double yt = Math.Min(m.SkewY * l, m.SkewY * r) + Math.Min(m.ScaleY * t, m.ScaleY * b);
        double yb = Math.Max(m.SkewY * l, m.SkewY * r) + Math.Max(m.ScaleY * t, m.ScaleY * b);
        var box = new SKRect((float)xl, (float)yt, (float)xr, (float)yb);
        return float.IsFinite(box.Left) && float.IsFinite(box.Top) && float.IsFinite(box.Right) && float.IsFinite(box.Bottom) ? box : null;
    }

    /// <summary>
    /// The bounds of <paramref name="path"/> stroked by <paramref name="stroke"/> (its dashes aside), mapped through the
    /// linear <paramref name="m"/> (no translation, no perspective), from the path's curves; null when it strokes nothing
    /// (or a point isn't finite). Not from the outline Skia strokes, which can lose reach where a stroke is wider than a
    /// curve's bend (<see cref="StrokeSweep"/> says why this box holds every stroke).
    /// </summary>
    public static SKRect? Swept(SKPath path, RStroke stroke, in SKMatrix m)
    {
        if (!(stroke.Width > 0) || !float.IsFinite(stroke.Width)) return null;
        var sweep = new StrokeSweep(stroke, m);
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: sweep.Move(pts[0]); break;
                case SKPathVerb.Line: sweep.Segment(pts.AsSpan(0, 2), 0); break;
                case SKPathVerb.Quad: sweep.Segment(pts.AsSpan(0, 3), 0); break;
                case SKPathVerb.Conic: sweep.Segment(pts.AsSpan(0, 3), it.ConicWeight()); break;
                case SKPathVerb.Cubic: sweep.Segment(pts.AsSpan(0, 4), 0); break;
                case SKPathVerb.Close: sweep.Close(); break;
            }
        }
        return sweep.Finish();
    }

    public void Add(SKPoint p)
    {
        if (!_any)
        {
            Box = new SKRect(p.X, p.Y, p.X, p.Y);
            _any = true;
            return;
        }
        Box = new SKRect(Math.Min(Box.Left, p.X), Math.Min(Box.Top, p.Y), Math.Max(Box.Right, p.X), Math.Max(Box.Bottom, p.Y));
    }

    private static SKPoint OnCircle(SKPoint c, float radius, double a) =>
        new((float)(c.X + radius * SceneMath.CosDeg(a)), (float)(c.Y + radius * SceneMath.SinDeg(a)));

    private void Disc(SKPoint p, float r)
    {
        Add(new SKPoint(p.X - r, p.Y - r));
        Add(new SKPoint(p.X + r, p.Y + r));
    }

    /// <summary>A straight segment's band: its ends grown by half the width across it.</summary>
    public void Segment(SKPoint a, SKPoint b)
    {
        var d = Unit(b - a);
        var n = new SKPoint(-d.Y * _half, d.X * _half);
        Add(a + n); Add(a - n); Add(b + n); Add(b - n);
    }

    /// <summary>
    /// The band along the arc of radius <paramref name="r"/> about <paramref name="c"/> from angle <paramref name="a0"/>
    /// to <paramref name="a1"/> (degrees, a0 ≤ a1; the whole circle when <paramref name="full"/>), without its ends: its
    /// outer edge at each axis direction it passes and both edges at its ends.
    /// </summary>
    public void Band(SKPoint c, float r, float a0, float a1, bool full)
    {
        var outer = r + _half;
        var inner = r - _half;
        if (full)
        {
            Disc(c, outer);
            return;
        }
        Add(OnCircle(c, outer, a0)); Add(OnCircle(c, outer, a1)); Add(OnCircle(c, inner, a0)); Add(OnCircle(c, inner, a1));
        var k = Math.Ceiling(a0 / 90) * 90;
        for (var i = 0; i < 5 && k <= a1; i++, k += 90) Add(OnCircle(c, outer, k));
        if (inner < 0) Disc(c, -inner); // a band wider than its radius crosses the centre
    }

    /// <summary>The corner at <paramref name="p"/> between the segments from <paramref name="prev"/> and to <paramref name="next"/>.</summary>
    public void Corner(SKPoint prev, SKPoint p, SKPoint next)
    {
        if (Short(prev, p) || Short(p, next))
        {
            // Skia may skip a segment this short and join its neighbours instead: the join's furthest reach.
            Disc(p, _stroke.Reach(corners: true));
            return;
        }
        Join(p, Unit(p - prev), Unit(next - p));
    }

    /// <summary>The open end <paramref name="p"/> of the segment from <paramref name="other"/>.</summary>
    public void End(SKPoint p, SKPoint other)
    {
        if (Short(p, other)) Disc(p, _stroke.Reach(corners: true));
        else Cap(p, Unit(p - other));
    }

    /// <summary>The join at <paramref name="p"/> from the unit direction <paramref name="d1"/> into it to <paramref name="d2"/> out of it.</summary>
    public void Join(SKPoint p, SKPoint d1, SKPoint d2)
    {
        switch (_stroke.Join)
        {
            case SKStrokeJoin.Round:
                Disc(p, _half);
                return;
            case SKStrokeJoin.Miter:
            {
                // The tip lies along the outer bisector, half the width over the sine of half the angle between the
                // segments; past the miter limit Skia bevels it (taken a hair early, so a join on the limit counts).
                var dot = Math.Clamp(d1.X * d2.X + d1.Y * d2.Y, -1f, 1f);
                if (dot > 0.9999f) return; // straight on: the bands meet
                var sinHalf = MathF.Sqrt((1 + dot) / 2);
                var bisector = Unit(d1 - d2);
                if (bisector == SKPoint.Empty || sinHalf * _stroke.Miter < 0.9999f) return;
                var reach = _half / sinHalf;
                Add(new SKPoint(p.X + bisector.X * reach, p.Y + bisector.Y * reach));
                return;
            }
            default:
                return; // a bevel reaches only the segments' own corners
        }
    }

    /// <summary>The cap at the open end <paramref name="p"/>, <paramref name="d"/> the unit direction out of the outline there.</summary>
    public void Cap(SKPoint p, SKPoint d)
    {
        switch (_stroke.Cap)
        {
            case SKStrokeCap.Square:
            {
                var n = new SKPoint(-d.Y * _half, d.X * _half);
                var e = new SKPoint(d.X * _half, d.Y * _half);
                Add(p + e + n);
                Add(p + e - n);
                return;
            }
            case SKStrokeCap.Round:
                Disc(p, _half);
                return;
            default:
                return; // a butt end is the segment's own band
        }
    }
}

/// <summary>
/// The box of a path's stroke measured from its curves, in a linear map's coordinates (<see cref="StrokeBox.Swept"/>).
/// Why it holds the stroke: a segment's stroke is the line half the width long either side of each of its points, square
/// to it, with joins and caps. Along an axis that region reaches furthest at the segment's ends, where the segment runs
/// square to the axis (there the stroke reaches half the width past it), or where its side folds back on itself, which
/// needs the curve to bend tighter than half the width. A curve that may bend that tight (by the bound in
/// <see cref="NoTightBend"/>), one Skia may stroke as straight lines whose ends turn from the curve's
/// (<see cref="NearLine"/>), and a conic, takes half the width all round its ends instead: every point of its stroke
/// lies within half the width of the curve, so within the box of its ends and square points grown by half the width.
/// Joins and caps add their own reach (a round one's half width all round, a square cap's corners, a miter's tip where
/// Skia draws one); an end whose direction isn't certain, or a segment Skia may skip, the conservative reach all round.
/// What Skia draws strays from this geometry by at most the quarter pixel it approximates a curve's offset within, at
/// the scales <see cref="StrokeBox.SweptHolds"/> allows; past them its departures grow with the scale.
/// </summary>
internal sealed class StrokeSweep
{
    private readonly RStroke _stroke;
    private readonly double _half, _reach, _a, _b, _c, _d, _rowX, _rowY;
    private double _left = double.PositiveInfinity, _top = double.PositiveInfinity;
    private double _right = double.NegativeInfinity, _bottom = double.NegativeInfinity;
    private bool _bad;

    // The contour being read: its start, the end so far, the first segment's direction out of its start and the last
    // one's into its end (null: not certain), whether it has a segment and whether it has anything Skia may draw a cap for.
    private SKPoint _start, _last;
    private SKPoint? _first, _prev;
    private bool _segments, _drawn;

    public StrokeSweep(RStroke stroke, in SKMatrix m)
    {
        _stroke = stroke;
        _half = stroke.Width / 2.0;
        _reach = stroke.Reach(corners: true);
        (_a, _b, _c, _d) = (m.ScaleX, m.SkewX, m.SkewY, m.ScaleY);
        _rowX = Math.Sqrt(_a * _a + _b * _b);
        _rowY = Math.Sqrt(_c * _c + _d * _d);
    }

    public void Move(SKPoint p)
    {
        EndContour(closed: false);
        _start = _last = p;
    }

    public void Close()
    {
        _drawn = true; // a closed contour of no length draws its caps as a dot
        if (_last != _start) Segment([_last, _start], 0);
        EndContour(closed: true);
        _last = _start;
    }

    public SKRect? Finish()
    {
        EndContour(closed: false);
        if (_bad || !(_left <= _right) || !(_top <= _bottom)) return null;
        var box = new SKRect((float)_left, (float)_top, (float)_right, (float)_bottom);
        return float.IsFinite(box.Left) && float.IsFinite(box.Top) && float.IsFinite(box.Right) && float.IsFinite(box.Bottom) ? box : null;
    }

    /// <summary>A line (2 points), quad or conic (3, the conic's weight <paramref name="w"/>) or cubic (4).</summary>
    public void Segment(ReadOnlySpan<SKPoint> p, float w)
    {
        foreach (var q in p)
        {
            if (float.IsFinite(q.X) && float.IsFinite(q.Y)) continue;
            _bad = true;
            return;
        }
        _drawn = true;
        SKPoint p0 = p[0], pn = p[^1];
        var degenerate = true;
        for (var i = 1; i < p.Length && degenerate; i++) degenerate = StrokeBox.Short(p[i], p0);
        if (degenerate)
        {
            // Skia may skip a segment this short and join its neighbours here, or draw it as its caps.
            Disc(p0, _reach);
            Disc(pn, _reach);
            _prev = null;
            _last = pn;
            return;
        }
        var t0 = Direction(p, fromStart: true);
        var t1 = Direction(p, fromStart: false);
        if (_segments) Join(p0, _prev, t0);
        else _first = t0;
        _segments = true;
        if (t0 is { } d0 && t1 is { } d1 && (p.Length == 2 || (w == 0 && NoTightBend(p) && !NearLine(p))))
        {
            Across(p0, d0);
            Across(pn, d1);
        }
        else
        {
            Disc(p0, _half);
            Disc(pn, _half);
        }
        if (p.Length > 2)
        {
            SquarePoints(p, w, _a, _b, x: true);
            SquarePoints(p, w, _c, _d, x: false);
        }
        _prev = t1;
        _last = pn;
    }

    private void EndContour(bool closed)
    {
        if (_segments)
        {
            if (closed) Join(_start, _prev, _first);
            else
            {
                Cap(_start, _first is { } f ? new SKPoint(-f.X, -f.Y) : null);
                Cap(_last, _prev);
            }
        }
        else if (_drawn) Disc(_start, _reach);
        (_segments, _drawn, _first, _prev) = (false, false, null, null);
    }

    /// <summary>The unit direction out of the segment's start or into its end; null when Skia's choice isn't certain.</summary>
    private static SKPoint? Direction(ReadOnlySpan<SKPoint> p, bool fromStart)
    {
        var end = fromStart ? p[0] : p[^1];
        for (var i = 1; i < p.Length; i++)
        {
            var q = fromStart ? p[i] : p[p.Length - 1 - i];
            if (q == end) continue; // a control point on its end: the next one gives the direction
            if (StrokeBox.Short(q, end)) return null;
            return StrokeBox.Unit(fromStart ? q - end : end - q);
        }
        return null;
    }

    /// <summary>
    /// Whether the curve bends nowhere tighter than half the width: its curvature |B′ × B″| / |B′|³ is at most |B″| / |B′|²,
    /// |B″| at most its largest value over the control points' differences and |B′| at least the distance from the origin
    /// to the hull of B′'s control points.
    /// </summary>
    private bool NoTightBend(ReadOnlySpan<SKPoint> p)
    {
        if (p.Length == 3)
        {
            var (ax, ay, bx, by) = ((double)p[1].X - p[0].X, (double)p[1].Y - p[0].Y, (double)p[2].X - p[1].X, (double)p[2].Y - p[1].Y);
            var least = 2 * ToSegment(ax, ay, bx, by);
            var most = 2 * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            return _half * most < least * least;
        }
        {
            var (ax, ay) = ((double)p[1].X - p[0].X, (double)p[1].Y - p[0].Y);
            var (bx, by) = ((double)p[2].X - p[1].X, (double)p[2].Y - p[1].Y);
            var (cx, cy) = ((double)p[3].X - p[2].X, (double)p[3].Y - p[2].Y);
            var least = 3 * ToTriangle(ax, ay, bx, by, cx, cy);
            var most = 6 * Math.Max(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), Math.Sqrt((cx - bx) * (cx - bx) + (cy - by) * (cy - by)));
            return _half * most < least * least;
        }
    }

    /// <summary>
    /// Whether Skia may stroke the curve as straight lines (its chord, or lines through its tightest point), whose ends
    /// turn from the curve's own: a control point within 1/100 of the curve's widest span from the line through that
    /// span's ends. Skia's own test is tighter (within about 1/450 of the span), so every curve it straightens is caught.
    /// </summary>
    private static bool NearLine(ReadOnlySpan<SKPoint> p)
    {
        var (i0, i1, span) = (0, 1, -1.0);
        for (var i = 0; i < p.Length; i++)
            for (var j = i + 1; j < p.Length; j++)
            {
                var (dx, dy) = ((double)p[j].X - p[i].X, (double)p[j].Y - p[i].Y);
                var d2 = dx * dx + dy * dy;
                if (d2 > span) (i0, i1, span) = (i, j, d2);
            }
        var (ux, uy) = ((double)p[i1].X - p[i0].X, (double)p[i1].Y - p[i0].Y);
        for (var k = 0; k < p.Length; k++)
        {
            if (k == i0 || k == i1) continue;
            var cross = ux * ((double)p[k].Y - p[i0].Y) - uy * ((double)p[k].X - p[i0].X);
            // The distance squared, cross² / span, against (span / 100)².
            if (cross * cross > span * span * 1e-4) return false;
        }
        return true;
    }

    /// <summary>The distance from the origin to the segment from a to b.</summary>
    private static double ToSegment(double ax, double ay, double bx, double by)
    {
        var (vx, vy) = (bx - ax, by - ay);
        var len2 = vx * vx + vy * vy;
        var t = len2 > 0 ? Math.Clamp(-(ax * vx + ay * vy) / len2, 0, 1) : 0;
        var (x, y) = (ax + t * vx, ay + t * vy);
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The distance from the origin to the triangle a, b, c (0 inside it, or when it is flat).</summary>
    private static double ToTriangle(double ax, double ay, double bx, double by, double cx, double cy)
    {
        var s1 = (bx - ax) * -ay - (by - ay) * -ax;
        var s2 = (cx - bx) * -by - (cy - by) * -bx;
        var s3 = (ax - cx) * -cy - (ay - cy) * -cx;
        if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) return 0;
        return Math.Min(ToSegment(ax, ay, bx, by), Math.Min(ToSegment(bx, by, cx, cy), ToSegment(cx, cy, ax, ay)));
    }

    /// <summary>
    /// Where the curve runs square to the axis whose row is (<paramref name="ux"/>, <paramref name="uy"/>): the stroke
    /// reaches half the width past it there, along that axis.
    /// </summary>
    private void SquarePoints(ReadOnlySpan<SKPoint> p, float w, double ux, double uy, bool x)
    {
        Span<double> v = stackalloc double[p.Length];
        for (var i = 0; i < p.Length; i++) v[i] = ux * p[i].X + uy * p[i].Y;
        double q2, q1, q0;
        if (p.Length == 4)
        {
            // B′ along the axis is 3 × ((1 − t)² A + 2t(1 − t) B + t² C).
            var (a, b, c) = (v[1] - v[0], v[2] - v[1], v[3] - v[2]);
            (q2, q1, q0) = (a - 2 * b + c, 2 * (b - a), a);
        }
        else if (w != 0)
        {
            // A conic's extremes along an axis (as Skia finds them): (w − 1) P20 t² + (P20 − 2w P10) t + w P10 = 0.
            var (p10, p20) = (v[1] - v[0], v[2] - v[0]);
            (q2, q1, q0) = ((w - 1) * p20, p20 - 2 * w * p10, w * p10);
        }
        else (q2, q1, q0) = (0, v[2] - 2 * v[1] + v[0], v[1] - v[0]); // a quad's B′ is 2 × ((1 − t) A + t B)
        var reach = _half * (x ? _rowX : _rowY);
        Span<double> roots = stackalloc double[2];
        var n = Roots(q2, q1, q0, roots);
        for (var i = 0; i < n; i++)
        {
            var t = roots[i];
            if (!(t > 0 && t < 1)) continue;
            var (px, py) = At(p, w, t);
            var along = ux * px + uy * py;
            if (x) { Extend(along - reach, true); Extend(along + reach, true); }
            else { Extend(along - reach, false); Extend(along + reach, false); }
        }
    }

    private static int Roots(double a, double b, double c, Span<double> roots)
    {
        var scale = Math.Abs(a) + Math.Abs(b) + Math.Abs(c);
        if (scale == 0) return 0;
        if (Math.Abs(a) <= 1e-12 * scale)
        {
            if (b == 0) return 0;
            roots[0] = -c / b;
            return 1;
        }
        var disc = b * b - 4 * a * c;
        if (disc < 0) return 0;
        var s = Math.Sqrt(disc);
        // The stable pair: one root from the sum that doesn't cancel, the other from the product.
        var q = -0.5 * (b + (b >= 0 ? s : -s));
        roots[0] = q / a;
        if (q == 0) return 1;
        roots[1] = c / q;
        return 2;
    }

    private static (double X, double Y) At(ReadOnlySpan<SKPoint> p, float w, double t)
    {
        var u = 1 - t;
        if (p.Length == 4)
        {
            double k0 = u * u * u, k1 = 3 * u * u * t, k2 = 3 * u * t * t, k3 = t * t * t;
            return (k0 * p[0].X + k1 * p[1].X + k2 * p[2].X + k3 * p[3].X, k0 * p[0].Y + k1 * p[1].Y + k2 * p[2].Y + k3 * p[3].Y);
        }
        double m0 = u * u, m1 = 2 * u * t * (w != 0 ? w : 1), m2 = t * t;
        var den = w != 0 ? m0 + m1 + m2 : 1;
        return ((m0 * p[0].X + m1 * p[1].X + m2 * p[2].X) / den, (m0 * p[0].Y + m1 * p[1].Y + m2 * p[2].Y) / den);
    }

    /// <summary>The ends of the line half the width either side of <paramref name="p"/>, square to the direction <paramref name="d"/>.</summary>
    private void Across(SKPoint p, SKPoint d)
    {
        var (nx, ny) = (-d.Y * _half, d.X * _half);
        Point(p.X + nx, p.Y + ny);
        Point(p.X - nx, p.Y - ny);
    }

    private void Join(SKPoint p, SKPoint? into, SKPoint? outOf)
    {
        if (into is not { } d1 || outOf is not { } d2)
        {
            Disc(p, _reach);
            return;
        }
        switch (_stroke.Join)
        {
            case SKStrokeJoin.Round:
                Disc(p, _half);
                return;
            case SKStrokeJoin.Miter:
            {
                // The tip is where the outer sides meet, half the width over the sine of half the angle between the
                // segments from the corner; past the miter limit Skia bevels it (taken a hair early, so a join on the
                // limit counts). (n1 + n2) / (1 + cos) stays exact as the segments straighten.
                var dot = (double)d1.X * d2.X + (double)d1.Y * d2.Y;
                if (dot <= -1) return;
                var sinHalf = Math.Sqrt((1 + dot) / 2);
                if (sinHalf * _stroke.Miter < 0.9999) return;
                var cross = (double)d1.X * d2.Y - (double)d1.Y * d2.X;
                var k = (cross > 0 ? -_half : _half) / (1 + dot);
                Point(p.X + k * (-d1.Y - d2.Y), p.Y + k * (d1.X + d2.X));
                return;
            }
            default:
                return; // a bevel reaches only the segments' own ends
        }
    }

    private void Cap(SKPoint p, SKPoint? outward)
    {
        if (outward is not { } d)
        {
            Disc(p, _stroke.Cap == SKStrokeCap.Square ? _half * 1.4143 : _half);
            return;
        }
        switch (_stroke.Cap)
        {
            case SKStrokeCap.Square:
            {
                var (ex, ey, nx, ny) = (d.X * _half, d.Y * _half, -d.Y * _half, d.X * _half);
                Point(p.X + ex + nx, p.Y + ey + ny);
                Point(p.X + ex - nx, p.Y + ey - ny);
                return;
            }
            case SKStrokeCap.Round:
                Disc(p, _half);
                return;
            default:
                return; // a butt end is the segment's own
        }
    }

    private void Point(double px, double py)
    {
        Extend(_a * px + _b * py, true);
        Extend(_c * px + _d * py, false);
    }

    /// <summary>The disc of radius <paramref name="r"/> about <paramref name="p"/>, mapped: an ellipse, boxed.</summary>
    private void Disc(SKPoint p, double r)
    {
        var (x, y) = (_a * p.X + _b * p.Y, _c * p.X + _d * p.Y);
        Extend(x - r * _rowX, true); Extend(x + r * _rowX, true);
        Extend(y - r * _rowY, false); Extend(y + r * _rowY, false);
    }

    private void Extend(double v, bool x)
    {
        if (x) { _left = Math.Min(_left, v); _right = Math.Max(_right, v); }
        else { _top = Math.Min(_top, v); _bottom = Math.Max(_bottom, v); }
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

    /// <summary>Whether caps other than butt may be drawn anywhere along the outline (at each dash's or the trim's ends).</summary>
    public bool CapsAnywhere => (Dash is not null || Trimmed) && Cap != SKStrokeCap.Butt;

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

    /// <summary>
    /// Where each line is drawn, in the node's coordinates: from its own start (aligned within the box) to its end, a
    /// marquee's across its box; within <see cref="ClipRect"/> when clipped, a line wholly outside it left out.
    /// </summary>
    public SKRect[] Lines = [];

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
