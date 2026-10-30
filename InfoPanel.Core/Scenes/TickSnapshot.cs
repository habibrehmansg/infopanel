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
/// One drawn text node of a tick: its item, its text (its spans joined), where its lines are drawn on the canvas (the
/// axis-aligned box of the lines, clipped to its box when it is clipped), and whether it fits its box.
/// </summary>
public sealed record TextReport(string ItemId, string Text, CanvasBox Box, TextFit Fit);

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
    private readonly Lazy<IReadOnlyList<ItemBounds>> _bounds;
    private readonly Lazy<IReadOnlyList<TextReport>> _texts;
    private readonly double _drawBudgetMs;
    private int _references = 1;

    internal TickSnapshot(long generation, long rev, long tick, SceneClock clock, int width, int height, uint background,
        SnapItem[] items, MediaSample[] samples, IDisposable[] owned, IReadOnlyList<SceneDiagnostic> diagnostics, double drawBudgetMs)
    {
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
        _bounds = new Lazy<IReadOnlyList<ItemBounds>>(() => BoundsOf(_items), LazyThreadSafetyMode.ExecutionAndPublication);
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
                    foreach (var node in nodes) Node(s.Id, node, s.World);
                    break;
            }
        }
        void Node(string id, RNode node, SKMatrix matrix)
        {
            if (node.Opacity <= 0) return; // not drawn
            switch (node)
            {
                case RGroup g:
                    var inner = g.Matrix.IsIdentity ? matrix : matrix.PreConcat(g.Matrix);
                    foreach (var child in g.Children) Node(id, child, inner);
                    break;
                case RText t:
                    var box = matrix.MapRect(t.Lines);
                    var text = t.Runs.Length == 1 ? t.Runs[0].Text : string.Concat(t.Runs.Select(r => r.Text));
                    result.Add(new TextReport(id, text, new CanvasBox(box.Left, box.Top, box.Width, box.Height), t.Fit));
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
    public float EffectiveScale(float scale)
    {
        if (!(scale > 0) || !float.IsFinite(scale)) scale = 1;
        var pixels = (double)Width * Height * scale * scale;
        return pixels > MaxFramePixels ? (float)Math.Sqrt(MaxFramePixels / ((double)Width * Height)) : scale;
    }

    /// <summary>
    /// Draws the frame: the background over the canvas (0, 0)–(width, height) × <paramref name="scale"/>, then every
    /// visible item, clipped to the canvas. Only the canvas matrix changes with the scale.
    /// </summary>
    public void Draw(SKCanvas canvas, float scale)
    {
        if (IsReleased) throw new ObjectDisposedException(nameof(TickSnapshot));
        var count = canvas.Save();
        canvas.Scale(scale);
        canvas.ClipRect(new SKRect(0, 0, Width, Height));
        canvas.DrawColor(SceneColor.ToSk(Background), SKBlendMode.Src);
        DrawItems(canvas, _items);
        canvas.RestoreToCount(count);
    }

    /// <summary>Renders the frame to a new bitmap (premultiplied, the platform's byte order) at <paramref name="scale"/>.</summary>
    public SKBitmap Render(float scale = 1)
    {
        var s = EffectiveScale(scale);
        var w = Math.Max(1, (int)Math.Round(Width * s));
        var h = Math.Max(1, (int)Math.Round(Height * s));
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        Draw(canvas, s);
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

    private void DrawItems(SKCanvas canvas, SnapItem[] items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case SnapGroup g:
                    DrawItems(canvas, g.Children);
                    break;
                case SnapScene s:
                {
                    var count = canvas.Save();
                    canvas.Concat(in s.World);
                    var start = Stopwatch.GetTimestamp();
                    if (s.Nodes is { } nodes) SceneDrawer.Draw(canvas, nodes);
                    else Placeholders.DrawItem(canvas, s.PlaceholderBox);
                    canvas.RestoreToCount(count);
                    if (s.Runtime is { } rt && s.Nodes is not null)
                    {
                        // The draw watchdog: a slow draw marks this tick slow (once, however many outputs draw it); the
                        // next tick suspends an item slow three ticks running.
                        if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > _drawBudgetMs) rt.MarkSlow(Tick);
                    }
                    break;
                }
            }
        }
    }

    // ---- bounds ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ItemBounds> BoundsOf(SnapItem[] roots)
    {
        var result = new List<ItemBounds>();
        foreach (var item in roots) Measure(item, result);
        return result;
    }

    /// <summary>The item's frame and ink in its local coordinates (null: no bounds), adding its bounds and its descendants' to <paramref name="result"/>.</summary>
    private static (SKRect Frame, SKRect Ink)? Measure(SnapItem item, List<ItemBounds> result)
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
                    ink = extent.Ink;
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
                    if (Measure(child, result) is not { } c) continue;
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
