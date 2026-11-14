using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InfoPanel.Images;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Scenes;

internal abstract class SnapItem
{
    public required string Id;

    /// <summary>Local coordinates to the canvas.</summary>
    public SKMatrix World;

    /// <summary>Local coordinates to the parent's.</summary>
    public SKMatrix Local;

    /// <summary>The sum of the rotations from the canvas down.</summary>
    public double Rotation;
}

internal sealed class SnapGroup : SnapItem
{
    public required SnapItem[] Children;
}

internal sealed class SnapScene : SnapItem
{
    public RNode[]? Nodes;
    public string? Problem;

    /// <summary>The first problem the item's media had in the tick.</summary>
    public ItemMedia? Media;
    public double? LayoutW, LayoutH;
    public SKRect PlaceholderBox;
    public ItemRuntime? Runtime;
}

/// <summary>Whether a text node's text fits its box, as its layout knows it (<see cref="TickSnapshot.Texts"/>).</summary>
public enum TextFit
{
    /// <summary>It fits, it has no box, or it scrolls (a marquee).</summary>
    Fits,

    /// <summary>Its text was cut and ended with "…" (<c>overflow: ellipsis</c>).</summary>
    Ellipsis,

    /// <summary>Lines were left out (<c>maxLines</c>, or explicit lines past it).</summary>
    LinesLeftOut,

    /// <summary>It is larger than its box and clipped to it (<c>overflow: clip</c>).</summary>
    Clipped,

    /// <summary>It is larger than its box and drawn past it (<c>overflow: visible</c>).</summary>
    PastBox,

    /// <summary><c>fit</c> shrank it to its minimum size, and it still didn't fit.</summary>
    FitMinimum,
}

/// <summary>A rectangle on the canvas, in profile pixels.</summary>
public readonly record struct CanvasBox(double X, double Y, double W, double H);

/// <summary>
/// One drawn text node of a tick: its item, its text (its spans joined), where its lines are drawn on the canvas
/// (<see cref="Lines"/>, each line's axis-aligned box from its own start to its end, clipped to the node's box when it is
/// clipped; <see cref="Box"/>, the box around them all), and whether it fits its box. For contrast:
/// <see cref="Colors"/>, its spans' colours (0xRRGGBBAA, straight alpha) when every span is filled with a single colour,
/// else null (a gradient or a composed paint), <see cref="Opacity"/>, its own opacity times its groups', and
/// <see cref="Blended"/>, whether a group it is in composites with a blend mode other than normal (its colour on what is
/// below is then no longer its colour and opacity); <see cref="Blurred"/>, whether a group it is in blurs it (a glow
/// or a shadow may be drawn as a blurred copy under the text).
/// </summary>
public sealed record TextReport(string ItemId, string Text, CanvasBox Box, TextFit Fit, IReadOnlyList<uint>? Colors = null, double Opacity = 1,
    bool Blended = false, IReadOnlyList<CanvasBox>? Lines = null, bool Blurred = false);

/// <summary>
/// One tick of one document generation (spec/docs/identity.md, "The tick snapshot"): the clock, every input, the text
/// layouts and the media frames the tick read, evaluated once. Everything drawn or measured for the tick uses it: every
/// output at every scale, thumbnails and bounds, so they agree pixel for pixel. Immutable and safe to draw from several
/// threads at once. Reference counted: it owns its media samples and the paths its dashed outlines are drawn from, and
/// releases them when the last holder disposes it.
/// </summary>
public sealed class TickSnapshot : IDisposable
{
    /// <summary>The frame cap (spec/docs/limits.md, run-time limits): a render that would exceed it is drawn at the largest scale that fits.</summary>
    public const long MaxFramePixels = 16_777_216;

    private readonly SnapItem[] _items;
    private readonly MediaSample[] _samples;
    private readonly IDisposable[] _owned;
    private readonly Lazy<IReadOnlyList<ItemBounds>> _bounds, _boundsWithoutEffects;
    private readonly Lazy<IReadOnlyList<TextReport>> _texts;
    private readonly double _drawBudgetMs;
    private readonly TimeProvider _drawTime;
    private int _references = 1;

    internal TickSnapshot(long generation, long rev, long tick, SceneClock clock, int width, int height, uint background,
        SnapItem[] items, MediaSample[] samples, IDisposable[] owned, IReadOnlyList<SceneDiagnostic> diagnostics, double drawBudgetMs, TimeProvider drawTime)
    {
        _drawTime = drawTime;
        Generation = generation;
        Rev = rev;
        Tick = tick;
        Clock = clock;
        Width = width;
        Height = height;
        Background = background;
        _items = items;
        _samples = samples;
        _owned = owned;
        Diagnostics = diagnostics;
        _drawBudgetMs = drawBudgetMs;
        _bounds = new Lazy<IReadOnlyList<ItemBounds>>(() => BoundsOf(_items, effects: true), LazyThreadSafetyMode.ExecutionAndPublication);
        _boundsWithoutEffects = new Lazy<IReadOnlyList<ItemBounds>>(() => BoundsOf(_items, effects: false), LazyThreadSafetyMode.ExecutionAndPublication);
        _texts = new Lazy<IReadOnlyList<TextReport>>(() => TextsOf(_items), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public long Generation { get; }

    /// <summary>The document revision the snapshot shows (<see cref="InfoPanel.Documents.Generation.Rev"/>; a loaded JSON document's <c>rev</c>, else 0).</summary>
    public long Rev { get; }
    public long Tick { get; }
    public SceneClock Clock { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The profile background, 0xRRGGBBAA.</summary>
    public uint Background { get; }

    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }

    /// <summary>The media frames this tick holds.</summary>
    public int SampleCount => _samples.Length;

    public bool IsReleased => Volatile.Read(ref _references) == 0;

    /// <summary>Every visible item's measured frame and ink (spec/docs/geometry.md), in document order, from this tick.</summary>
    public IReadOnlyList<ItemBounds> Bounds => _bounds.Value;

    /// <summary>As <see cref="Bounds"/>, each ink without its effects (blur, shadow, glow): its outlines and strokes (not part of the API).</summary>
    public IReadOnlyList<ItemBounds> BoundsWithoutEffects => _boundsWithoutEffects.Value;

    /// <summary>Every drawn text node of every visible item, in drawing order, from this tick's layouts (not part of the API).</summary>
    public IReadOnlyList<TextReport> Texts => _texts.Value;

    private static IReadOnlyList<TextReport> TextsOf(SnapItem[] roots)
    {
        var result = new List<TextReport>();
        void Item(SnapItem item)
        {
            switch (item)
            {
                case SnapGroup g:
                    foreach (var child in g.Children) Item(child);
                    break;
                case SnapScene { Nodes: { } nodes } s:
                    foreach (var node in nodes) Node(s.Id, node, s.World, 1, false, false);
                    break;
            }
        }
        void Node(string id, RNode node, SKMatrix matrix, double opacity, bool blended, bool blurred)
        {
            if (node.Opacity <= 0) return; // not drawn
            opacity *= node.Opacity;
            switch (node)
            {
                case RGroup g:
                    var inner = g.Matrix.IsIdentity ? matrix : matrix.PreConcat(g.Matrix);
                    blended |= g.Blend != SKBlendMode.SrcOver;
                    blurred |= g.Effects.Any(e => e.Kind == EffectKind.Blur && e.Sigma > 0);
                    foreach (var child in g.Children) Node(id, child, inner, opacity, blended, blurred);
                    break;
                case RText t:
                    var lines = new List<CanvasBox>(t.Lines.Length);
                    var box = SKRect.Empty;
                    foreach (var line in t.Lines)
                    {
                        var mapped = matrix.MapRect(line);
                        lines.Add(new CanvasBox(mapped.Left, mapped.Top, mapped.Width, mapped.Height));
                        box = box.IsEmpty ? mapped : SKRect.Union(box, mapped);
                    }
                    var text = t.Runs.Length == 1 ? t.Runs[0].Text : string.Concat(t.Runs.Select(r => r.Text));
                    List<uint>? colors = [];
                    foreach (var fill in t.SpanFills)
                    {
                        if (fill is null) continue;
                        if (fill.SolidColor is not { } solid)
                        {
                            colors = null;
                            break;
                        }
                        if (!colors.Contains(solid)) colors.Add(solid);
                    }
                    result.Add(new TextReport(id, text, new CanvasBox(box.Left, box.Top, box.Width, box.Height), t.Fit, colors, opacity, blended, lines, blurred));
                    break;
            }
        }
        foreach (var item in roots) Item(item);
        return result;
    }

    /// <summary>Another reference; the holder disposes it.</summary>
    public TickSnapshot Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current == 0) throw new ObjectDisposedException(nameof(TickSnapshot));
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current) return this;
        }
    }

    public void Dispose()
    {
        var left = Interlocked.Decrement(ref _references);
        if (left == 0)
        {
            foreach (var s in _samples) s.Release();
            foreach (var o in _owned) o.Dispose();
        }
        else if (left < 0) Interlocked.Exchange(ref _references, 0);
    }

    /// <summary>
    /// More than any scale a frame of <paramref name="width"/> × <paramref name="height"/> is drawn at: its largest
    /// within <see cref="MaxFramePixels"/>, plus 1 for sides rounded to whole pixels. For the dash budget.
    /// </summary>
    internal static double LargestScale(int width, int height) => Math.Sqrt(MaxFramePixels / ((double)Math.Max(1, width) * Math.Max(1, height))) + 1;

    /// <summary>The scale actually drawn for <paramref name="scale"/>: reduced so the frame stays within <see cref="MaxFramePixels"/>.</summary>
    public float EffectiveScale(float scale) => EffectiveScale(Width, Height, scale);

    /// <summary>The scale a frame of <paramref name="width"/> × <paramref name="height"/> is drawn at for <paramref name="scale"/> (<see cref="EffectiveScale(float)"/>).</summary>
    internal static float EffectiveScale(int width, int height, float scale)
    {
        if (!(scale > 0) || !float.IsFinite(scale)) scale = 1;
        var pixels = (double)width * height * scale * scale;
        return pixels > MaxFramePixels ? (float)Math.Sqrt(MaxFramePixels / ((double)width * height)) : scale;
    }

    /// <summary>
    /// Draws the frame: the background over the canvas (0, 0)–(width, height) × <paramref name="scale"/>, then every
    /// visible item, clipped to the canvas. Only the canvas matrix changes with the scale. The canvas's pixels are
    /// Rgba8888, as every output's are (<see cref="Draw(SKCanvas, float, bool, SKColorType)"/> for others).
    /// </summary>
    public void Draw(SKCanvas canvas, float scale) => Draw(canvas, scale, withoutText: false);

    /// <summary>
    /// <see cref="Draw(SKCanvas, float)"/>, or with <paramref name="withoutText"/> the same frame with its text nodes left
    /// out: what lies under the text, for the preview checks. Not an output.
    /// </summary>
    public void Draw(SKCanvas canvas, float scale, bool withoutText) => Draw(canvas, scale, withoutText, SKColorType.Rgba8888);

    /// <summary>
    /// <see cref="Draw(SKCanvas, float, bool)"/> into a canvas whose pixels are <paramref name="colorType"/>, Rgba8888 (every
    /// output's) or Bgra8888. Above scale 1 raster media is drawn as at scale 1 into a picture of that colour type, because
    /// Skia samples the same draw a little differently into RGBA and BGRA pixels (a sample on a pixel's edge can go either
    /// way): a frame then shows the pixels the frame at scale 1 in the same colour type has.
    /// </summary>
    public void Draw(SKCanvas canvas, float scale, bool withoutText, SKColorType colorType) => Draw(canvas, scale, withoutText, colorType, null);

    /// <summary>
    /// <see cref="Draw(SKCanvas, float, bool, SKColorType)"/>, above scale 1 with <paramref name="scratch"/> as the frame's
    /// panel side when it is the canvas's size and colour type (a render loop's, kept from frame to frame by one drawing
    /// thread, so raster media allocates no canvas-size picture per frame); else with one of its own.
    /// </summary>
    internal void Draw(SKCanvas canvas, float scale, bool withoutText, SKColorType colorType, PanelFrame? scratch)
    {
        if (IsReleased) throw new ObjectDisposedException(nameof(TickSnapshot));
        if (colorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            throw new ArgumentOutOfRangeException(nameof(colorType), colorType, "Rgba8888 or Bgra8888");
        var count = canvas.Save();
        var was = SceneDrawer.WithoutText;
        var wasZoom = SceneDrawer.Zoom;
        var wasRoot = SceneDrawer.ZoomRoot;
        var wasPanel = SceneDrawer.Panel;
        var reused = scale > 1 && scratch is not null && scratch.Width == Width && scratch.Height == Height && scratch.ColorType == colorType;
        var panel = reused ? scratch!.Reset() : scale > 1 ? new PanelFrame(Width, Height, colorType) : null;
        SceneDrawer.WithoutText = withoutText;
        SceneDrawer.Zoom = scale > 1 ? scale : 0;
        SceneDrawer.ZoomRoot = canvas.TotalMatrix;
        SceneDrawer.Panel = panel;
        try
        {
            canvas.Scale(scale);
            canvas.ClipRect(new SKRect(0, 0, Width, Height));
            canvas.DrawColor(SceneColor.ToSk(Background), SKBlendMode.Src);
            DrawItems(canvas, _items, scale);
        }
        finally
        {
            SceneDrawer.WithoutText = was;
            SceneDrawer.Zoom = wasZoom;
            SceneDrawer.ZoomRoot = wasRoot;
            SceneDrawer.Panel = wasPanel;
            if (!reused) panel?.Dispose();
            canvas.RestoreToCount(count);
        }
    }

    /// <summary>
    /// Whether drawing this snapshot gives the pixels drawing <paramref name="other"/> gives, at any scale: the same canvas
    /// and background, and item by item the same placement and nodes, every field a draw reads compared by value, media
    /// samples and text layouts by reference. Conservative: what it can't compare (a composed paint, a compiled path that
    /// isn't the same object) counts as different.
    /// </summary>
    internal bool DrawsAs(TickSnapshot other) =>
        ReferenceEquals(this, other)
        || Width == other.Width && Height == other.Height && Background == other.Background && SameItems(_items, other._items);

    private static bool SameItems(SnapItem[] a, SnapItem[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].World.Equals(b[i].World)) return false;
            var same = (a[i], b[i]) switch
            {
                (SnapGroup g, SnapGroup h) => SameItems(g.Children, h.Children),
                (SnapScene s, SnapScene t) => s.Nodes is { } m && t.Nodes is { } n ? SameNodes(m, n)
                    : s.Nodes is null && t.Nodes is null && s.PlaceholderBox == t.PlaceholderBox,
                _ => false,
            };
            if (!same) return false;
        }
        return true;
    }

    private static bool SameNodes(RNode[] a, RNode[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!SameNode(a[i], b[i])) return false;
        return true;
    }

    private static bool SameNode(RNode a, RNode b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Opacity != b.Opacity) return false;
        return (a, b) switch
        {
            (RGroup g, RGroup h) => g.Matrix.Equals(h.Matrix) && g.Blend == h.Blend && g.Effects.AsSpan().SequenceEqual(h.Effects)
                && SameGeom(g.Clip, h.Clip) && Nullable.Equals(g.BoundsOverride, h.BoundsOverride) && SameNodes(g.Children, h.Children),
            (RShape s, RShape t) => SameGeom(s.Geom, t.Geom) && SamePaint(s.Fill, t.Fill) && SameStroke(s.Stroke, t.Stroke),
            (RText s, RText t) => ReferenceEquals(s.Layout, t.Layout) && s.Left == t.Left && s.Top == t.Top && s.AlignWidth == t.AlignWidth
                && s.AlignFraction == t.AlignFraction && SamePaints(s.SpanFills, t.SpanFills) && SameStroke(s.Stroke, t.Stroke)
                && Nullable.Equals(s.ClipRect, t.ClipRect) && s.Marquee == t.Marquee && s.MarqueeOffset == t.MarqueeOffset
                && s.MarqueePeriod == t.MarqueePeriod && s.Underline.AsSpan().SequenceEqual(t.Underline)
                && s.Strike.AsSpan().SequenceEqual(t.Strike) && s.Outline == t.Outline,
            (RMedia m, RMedia n) => ReferenceEquals(m.Sample, n.Sample) && ReferenceEquals(m.Over, n.Over) && m.FadeOpacity == n.FadeOpacity
                && m.Dest == n.Dest && m.Box == n.Box && m.ClipToBox == n.ClipToBox && m.Nearest == n.Nearest,
            _ => false,
        };
    }

    private static bool SameGeom(RGeom? a, RGeom? b) => ReferenceEquals(a, b) || (a, b) switch
    {
        (RectGeom r, RectGeom q) => r.Rect == q.Rect && (ReferenceEquals(r.Radii, q.Radii) || r.Radii is { } x && q.Radii is { } y && x.AsSpan().SequenceEqual(y)),
        (EllipseGeom e, EllipseGeom f) => e.Oval == f.Oval,
        (ArcGeom r, ArcGeom q) => r.Cx == q.Cx && r.Cy == q.Cy && r.R == q.R && r.Inner == q.Inner && r.Start == q.Start && r.Sweep == q.Sweep && r.Open == q.Open,
        (LineGeom l, LineGeom m) => l.P1 == m.P1 && l.P2 == m.P2,
        (PolyGeom p, PolyGeom q) => p.Closed == q.Closed && p.Fill == q.Fill && p.Points.AsSpan().SequenceEqual(q.Points),
        _ => false, // a compiled path is compared by reference only
    };

    private static bool SamePaint(RPaint? a, RPaint? b) => ReferenceEquals(a, b) || (a, b) switch
    {
        (RSolid s, RSolid t) => s.Color == t.Color,
        (RGradient g, RGradient h) => g.Kind == h.Kind && g.Tile == h.Tile && g.Geometry.AsSpan().SequenceEqual(h.Geometry)
            && g.Colors.AsSpan().SequenceEqual(h.Colors) && g.Positions.AsSpan().SequenceEqual(h.Positions),
        _ => false, // a composed paint is compared by reference only
    };

    private static bool SamePaints(RPaint?[] a, RPaint?[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!SamePaint(a[i], b[i])) return false;
        return true;
    }

    private static bool SameStroke(RStroke? a, RStroke? b) => ReferenceEquals(a, b)
        || a is not null && b is not null && SamePaint(a.Paint, b.Paint) && a.Width == b.Width && a.Cap == b.Cap && a.Join == b.Join
        && a.Miter == b.Miter && a.DashOffset == b.DashOffset && a.TrimStart == b.TrimStart && a.TrimEnd == b.TrimEnd
        && (ReferenceEquals(a.Dash, b.Dash) || a.Dash is { } x && b.Dash is { } y && x.AsSpan().SequenceEqual(y));

    /// <summary>Renders the frame to a new bitmap (premultiplied, the platform's byte order) at <paramref name="scale"/>.</summary>
    public SKBitmap Render(float scale = 1)
    {
        var s = EffectiveScale(scale);
        var w = Math.Max(1, (int)Math.Round(Width * s));
        var h = Math.Max(1, (int)Math.Round(Height * s));
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        Draw(canvas, s, withoutText: false, bitmap.ColorType);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>A PNG of <see cref="Render"/>.</summary>
    public byte[] RenderPng(float scale = 1)
    {
        using var bitmap = Render(scale);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void DrawItems(SKCanvas canvas, SnapItem[] items, float scale)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case SnapGroup g:
                    DrawItems(canvas, g.Children, scale);
                    break;
                case SnapScene s:
                {
                    var count = canvas.Save();
                    var panelCount = SceneDrawer.Panel?.Save() ?? 0;
                    canvas.Concat(in s.World);
                    SceneDrawer.Panel?.Concat(in s.World);
                    var watch = s.Runtime is not null ? WatchSpan.Start(_drawTime) : default;
                    var start = _drawTime.GetTimestamp();
                    if (s.Nodes is { } nodes) SceneDrawer.Draw(canvas, nodes);
                    else Placeholders.DrawItem(canvas, s.PlaceholderBox);
                    SceneDrawer.Panel?.RestoreToCount(panelCount);
                    canvas.RestoreToCount(count);
                    if (s.Runtime is { } rt && s.Nodes is not null)
                    {
                        // The draw watchdog: a slow draw marks this tick slow (once, however many outputs draw it); the
                        // next tick suspends an item slow three ticks running.
                        // Counted per canvas pixel: a frame drawn at a scale above 1 has scale² times the pixels, so its
                        // time is divided by that; at 1 or smaller it counts whole (spec/docs/limits.md).
                        var area = (double)scale * scale;
                        var wall = _drawTime.GetElapsedTime(start).TotalMilliseconds;
                        var ms = wall / (area > 1 ? area : 1);
                        if (ms > _drawBudgetMs)
                        {
                            // The figures first: naming the drawer walks the stack, which no figure may include.
                            var mark = watch.Slow(Tick, "draw", wall, scale);
                            rt.MarkSlow(mark with { Drawer = Drawer(_drawTime) });
                        }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Who draws, for a slow draw's figures only: <c>loop</c> (a render loop's frame), <c>thumbnail</c>, or <c>other</c>
    /// (a test, the preview checks), read off the calling stack.
    /// </summary>
    private static string Drawer(TimeProvider time)
    {
        if ((time as IThreadTime)?.Drawer() is { } given) return given;
        foreach (var frame in new StackTrace(1, false).GetFrames())
            for (var type = frame.GetMethod()?.DeclaringType; type is not null; type = type.DeclaringType)
            {
                if (type == typeof(Output.Thumbnail)) return "thumbnail";
                if (type == typeof(Output.RenderLoop)) return "loop";
            }
        return "other";
    }

    // ---- bounds ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ItemBounds> BoundsOf(SnapItem[] roots, bool effects)
    {
        var result = new List<ItemBounds>();
        foreach (var item in roots) Measure(item, result, effects);
        return result;
    }

    /// <summary>The item's frame and ink (with its effects, or without) in its local coordinates (null: no bounds), adding its bounds and its descendants' to <paramref name="result"/>.</summary>
    private static (SKRect Frame, SKRect Ink)? Measure(SnapItem item, List<ItemBounds> result, bool effects)
    {
        switch (item)
        {
            case SnapScene s:
            {
                SKRect frame, ink;
                if (s.Nodes is null)
                {
                    frame = ink = s.PlaceholderBox;
                }
                else if (SceneBounds.Measure(s.Nodes) is { } extent)
                {
                    var box = extent.Box;
                    frame = new SKRect(
                        s.LayoutW is null ? box.Left : 0, s.LayoutH is null ? box.Top : 0,
                        s.LayoutW is { } w ? (float)w : box.Right, s.LayoutH is { } h ? (float)h : box.Bottom);
                    ink = effects ? extent.Ink : extent.Box;
                }
                else
                {
                    frame = ink = new SKRect(0, 0, (float)(s.LayoutW ?? 0), (float)(s.LayoutH ?? 0));
                }
                result.Add(Report(s, frame, ink, s.Problem, s.Media));
                return (frame, ink);
            }
            case SnapGroup g:
            {
                var index = result.Count;
                SKRect? frame = null, ink = null;
                foreach (var child in g.Children)
                {
                    if (Measure(child, result, effects) is not { } c) continue;
                    var f = SceneBounds.Map(child.Local, c.Frame);
                    var k = SceneBounds.Map(child.Local, c.Ink);
                    frame = frame is { } fa ? SKRect.Union(fa, f) : f;
                    ink = ink is { } ka ? SKRect.Union(ka, k) : k;
                }
                if (frame is not { } fr || ink is not { } ik) return null;
                result.Insert(index, Report(g, fr, ik, null, null));
                return (fr, ik);
            }
            default:
                return null;
        }
    }

    /// <summary>A local rectangle as reported: its centre mapped by the world transform, its size, the world rotation.</summary>
    private static ItemBounds Report(SnapItem item, SKRect frame, SKRect ink, string? problem, ItemMedia? media)
    {
        static BoundsRect Rect(SKMatrix world, SKRect r)
        {
            var c = world.MapPoint(new SKPoint(r.MidX, r.MidY));
            return new BoundsRect { X = c.X - r.Width / 2.0, Y = c.Y - r.Height / 2.0, W = Math.Max(0, r.Width), H = Math.Max(0, r.Height) };
        }
        return new ItemBounds { Id = item.Id, Frame = Rect(item.World, frame), Ink = Rect(item.World, ink), Rotation = item.Rotation, Problem = problem, Media = media };
    }
}
