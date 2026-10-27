using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public double? LayoutW, LayoutH;
    public SKRect PlaceholderBox;
    public ItemRuntime? Runtime;
}

/// <summary>
/// One tick of one document generation (spec/docs/identity.md, "The tick snapshot"): the clock, every input, the text
/// layouts and the media frames the tick read, evaluated once. Everything drawn or measured for the tick uses it: every
/// output at every scale, thumbnails and bounds, so they agree pixel for pixel. Immutable and safe to draw from several
/// threads at once. Reference counted: it owns its media samples and releases them when the last holder disposes it.
/// </summary>
public sealed class TickSnapshot : IDisposable
{
    /// <summary>The frame cap (spec/docs/limits.md, run-time limits): a render that would exceed it is drawn at the largest scale that fits.</summary>
    public const long MaxFramePixels = 16_777_216;

    private readonly SnapItem[] _items;
    private readonly MediaSample[] _samples;
    private readonly Lazy<IReadOnlyList<ItemBounds>> _bounds;
    private readonly double _drawBudgetMs;
    private int _references = 1;

    internal TickSnapshot(long generation, long rev, long tick, SceneClock clock, int width, int height, uint background,
        SnapItem[] items, MediaSample[] samples, IReadOnlyList<SceneDiagnostic> diagnostics, double drawBudgetMs)
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
        Diagnostics = diagnostics;
        _drawBudgetMs = drawBudgetMs;
        _bounds = new Lazy<IReadOnlyList<ItemBounds>>(() => BoundsOf(_items), LazyThreadSafetyMode.ExecutionAndPublication);
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
            foreach (var s in _samples) s.Release();
        else if (left < 0) Interlocked.Exchange(ref _references, 0);
    }

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
                result.Add(Report(s, frame, ink, s.Problem));
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
                result.Insert(index, Report(g, fr, ik, null));
                return (fr, ik);
            }
            default:
                return null;
        }
    }

    /// <summary>A local rectangle as reported: its centre mapped by the world transform, its size, the world rotation.</summary>
    private static ItemBounds Report(SnapItem item, SKRect frame, SKRect ink, string? problem)
    {
        static BoundsRect Rect(SKMatrix world, SKRect r)
        {
            var c = world.MapPoint(new SKPoint(r.MidX, r.MidY));
            return new BoundsRect { X = c.X - r.Width / 2.0, Y = c.Y - r.Height / 2.0, W = Math.Max(0, r.Width), H = Math.Max(0, r.Height) };
        }
        return new ItemBounds { Id = item.Id, Frame = Rect(item.World, frame), Ink = Rect(item.World, ink), Rotation = item.Rotation, Problem = problem };
    }
}
