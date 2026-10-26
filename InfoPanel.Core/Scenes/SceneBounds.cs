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
/// strokes (a conservative reach for joins and caps), effects not included; ink grows it by each effect's reach. A
/// group's extents are its children's, clipped to its clip and mapped through its transform. A series or table stands
/// for its box. The hint is where pixels may land (glyph overhangs included), used to size effect layers.
/// </summary>
internal static class SceneBounds
{
    public static NodeExtent? Measure(RNode[] nodes)
    {
        NodeExtent? total = null;
        foreach (var n in nodes)
        {
            if (Measure(n) is not { } e) continue;
            total = total is { } t ? new NodeExtent(SKRect.Union(t.Box, e.Box), SKRect.Union(t.Ink, e.Ink), SKRect.Union(t.Hint, e.Hint)) : e;
        }
        return total;
    }

    public static NodeExtent? Measure(RNode node)
    {
        switch (node)
        {
            case RShape s:
            {
                var r = s.Geom.Bounds;
                if (s.Stroke is { } st)
                {
                    var reach = st.Reach(s.Geom);
                    r.Inflate(reach, reach);
                }
                var hint = r;
                hint.Inflate(1, 1);
                return new NodeExtent(r, r, hint);
            }
            case RText t:
            {
                var hint = t.InkHint;
                hint.Inflate(1, 1);
                return new NodeExtent(t.Outline, t.Outline, hint);
            }
            case RMedia m:
            {
                var hint = m.ClipToBox ? m.Box : SKRect.Union(m.Box, m.Dest);
                return new NodeExtent(m.Box, m.Box, hint);
            }
            case RGroup g:
            {
                NodeExtent inner;
                if (g.BoundsOverride is { } over)
                {
                    var h = over;
                    if (Measure(g.Children) is { } c) h = SKRect.Union(h, c.Hint);
                    if (g.Clip is not null) h.Intersect(g.Clip.Bounds);
                    inner = new NodeExtent(over, over, h);
                }
                else
                {
                    if (Measure(g.Children) is not { } c) return null;
                    inner = c;
                    if (g.Clip is { } clip)
                    {
                        var cb = clip.Bounds;
                        inner = new NodeExtent(Intersect(inner.Box, cb), Intersect(inner.Ink, cb), Intersect(inner.Hint, cb));
                    }
                }
                var ink = inner.Ink;
                var hintRect = inner.Hint;
                foreach (var fx in g.Effects)
                {
                    // Each effect acts on the result of the one before.
                    ink.Inflate(fx.Reach, fx.Reach);
                    hintRect.Inflate(fx.Reach, fx.Reach);
                }
                return new NodeExtent(Map(g.Matrix, inner.Box), Map(g.Matrix, ink), Map(g.Matrix, hintRect));
            }
            default:
                return null;
        }
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
