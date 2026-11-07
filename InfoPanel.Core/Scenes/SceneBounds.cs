using System;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>A node's extents in its parent's coordinates: the content box (outlines and strokes), ink (plus effects) and a drawing hint.</summary>
internal readonly record struct NodeExtent(SKRect Box, SKRect Ink, SKRect Hint)
{
    public static NodeExtent Of(SKRect r) => new(r, r, r);
}

/// <summary>
/// Measured bounds (spec/docs/geometry.md): the content box is the union of the visible nodes' outlines with their
/// strokes, effects not included; ink grows it by each effect's reach. A stroke reaches half its width past a smooth
/// outline, and further only where it is drawn further: a miter's tip at a corner, a square cap at an open end
/// (<see cref="RGeom.StrokedBounds"/>). A group's extents are its children's, clipped to its clip and mapped through its
/// transform; a group that turns or skews measures its children's outlines through its matrix, so a turned shape's box is
/// the box of what it draws, not its own box turned. A series or table stands for its box. The hint is where pixels may
/// land (glyph overhangs included; a stroke's conservative reach all round, each group's box turned), used to size
/// effect layers: it is measured as it always was, so no drawing changes.
/// </summary>
internal static class SceneBounds
{
    public static NodeExtent? Measure(RNode[] nodes) => Measure(nodes, SKMatrix.Identity);

    public static NodeExtent? Measure(RNode node) => Measure(node, SKMatrix.Identity);

    /// <summary>
    /// The box and ink of <paramref name="nodes"/> mapped through <paramref name="to"/> (their coordinates to the
    /// measuring one's); the hint in their parent's coordinates, as <see cref="Measure(RNode)"/>.
    /// </summary>
    private static NodeExtent? Measure(RNode[] nodes, in SKMatrix to)
    {
        NodeExtent? total = null;
        foreach (var n in nodes)
        {
            if (Measure(n, to) is not { } e) continue;
            total = total is { } t ? new NodeExtent(SKRect.Union(t.Box, e.Box), SKRect.Union(t.Ink, e.Ink), SKRect.Union(t.Hint, e.Hint)) : e;
        }
        return total;
    }

    private static NodeExtent? Measure(RNode node, in SKMatrix to)
    {
        switch (node)
        {
            case RShape s:
            {
                var hint = s.Stroke is { } st ? s.Geom.Conservative(st) : s.Geom.Bounds;
                hint.Inflate(1, 1);
                var box = Shape(s, to);
                return new NodeExtent(box, box, hint);
            }
            case RText t:
            {
                var hint = t.InkHint;
                hint.Inflate(1, 1);
                var box = Map(to, t.Outline);
                return new NodeExtent(box, box, hint);
            }
            case RMedia m:
            {
                var hint = m.ClipToBox ? m.Box : SKRect.Union(m.Box, m.Dest);
                var box = Map(to, m.Box);
                return new NodeExtent(box, box, hint);
            }
            case RGroup g:
            {
                var inner = to.PreConcat(g.Matrix); // the children's coordinates to the measuring one's
                SKRect box, ink, hintRect;
                if (g.BoundsOverride is { } over)
                {
                    var h = over;
                    if (Measure(g.Children, inner) is { } c) h = SKRect.Union(h, c.Hint);
                    if (g.Clip is not null) h.Intersect(g.Clip.Bounds);
                    box = ink = Map(inner, over);
                    hintRect = h;
                }
                else
                {
                    if (Measure(g.Children, inner) is not { } c) return null;
                    (box, ink, hintRect) = (c.Box, c.Ink, c.Hint);
                    if (g.Clip is { } clip)
                    {
                        // Both the children's box and the clip's hold what is drawn, so their intersection does too.
                        var cb = clip.Bounds;
                        var mapped = Map(inner, cb);
                        (box, ink, hintRect) = (Intersect(box, mapped), Intersect(ink, mapped), Intersect(hintRect, cb));
                    }
                }
                // Each effect acts on the result of the one before, in the children's coordinates: its reach, a square
                // there, is mapped as one.
                var reachX = Math.Abs(inner.ScaleX) + Math.Abs(inner.SkewX);
                var reachY = Math.Abs(inner.SkewY) + Math.Abs(inner.ScaleY);
                foreach (var fx in g.Effects)
                {
                    ink.Inflate(fx.Reach * reachX, fx.Reach * reachY);
                    hintRect.Inflate(fx.Reach, fx.Reach);
                }
                return new NodeExtent(box, ink, Map(g.Matrix, hintRect));
            }
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="m"/> keeps a rectangle a rectangle (scale and translate): mapping a box is then exact.</summary>
    private static bool AxisAligned(in SKMatrix m) => m.SkewX == 0 && m.SkewY == 0 && m.Persp0 == 0 && m.Persp1 == 0;

    /// <summary>A shape's content box mapped through <paramref name="to"/>: its outline (with its stroke) mapped, then boxed.</summary>
    private static SKRect Shape(RShape s, in SKMatrix to)
    {
        // A static path's stroked outline, made at the scale it is drawn at and kept across ticks.
        if (s.Geom is StaticPathGeom p && s.Stroke is { } ps) return p.StrokedBounds(ps, to) ?? Map(to, s.Geom.Conservative(ps));
        if (AxisAligned(to)) return Map(to, s.Stroke is { } st ? s.Geom.StrokedBounds(st) : s.Geom.Bounds);
        if (s.Stroke is null && s.Geom is RectGeom { Radii: null } r) return Map(to, r.Rect);
        // Turned or skewed: the outline Skia draws, mapped, and its box. Caps a dash may draw anywhere keep the
        // conservative reach, boxed and turned.
        if (s.Stroke is { CapsAnywhere: true } loose) return Map(to, s.Geom.Conservative(loose));
        using var path = s.Geom.ToPath();
        if (s.Stroke is { } stroke)
            return StrokeBox.Outline(path, stroke, to) ?? Map(to, s.Geom.Conservative(stroke));
        path.Transform(to);
        var box = path.ComputeTightBounds();
        return float.IsFinite(box.Left) && float.IsFinite(box.Top) && float.IsFinite(box.Right) && float.IsFinite(box.Bottom) ? box : Map(to, s.Geom.Bounds);
    }

    private static SKRect Intersect(SKRect a, SKRect b)
    {
        var l = Math.Max(a.Left, b.Left);
        var t = Math.Max(a.Top, b.Top);
        return new SKRect(l, t, Math.Max(l, Math.Min(a.Right, b.Right)), Math.Max(t, Math.Min(a.Bottom, b.Bottom)));
    }

    /// <summary>The axis-aligned bounds of a rectangle mapped through <paramref name="m"/>.</summary>
    public static SKRect Map(SKMatrix m, SKRect r)
    {
        if (m.IsIdentity) return r;
        Span<SKPoint> pts = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        float l = float.PositiveInfinity, t = float.PositiveInfinity, rr = float.NegativeInfinity, b = float.NegativeInfinity;
        foreach (var p in pts)
        {
            var q = m.MapPoint(p);
            l = Math.Min(l, q.X); t = Math.Min(t, q.Y); rr = Math.Max(rr, q.X); b = Math.Max(b, q.Y);
        }
        if (!float.IsFinite(l) || !float.IsFinite(t) || !float.IsFinite(rr) || !float.IsFinite(b)) return SKRect.Empty;
        return new SKRect(l, t, rr, b);
    }
}
