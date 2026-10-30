using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InfoPanel.Drawing;
using SkiaSharp;
using Topten.RichTextKit;

namespace InfoPanel.Scenes;

internal enum TextOverflow : byte { Visible, Clip, Ellipsis, Marquee }

/// <summary>One span's text and font, at the node's size before fitting.</summary>
internal readonly record struct TextRun(string Text, string Family, float Size, int Weight, bool Italic);

/// <summary>Everything a text layout depends on. Equal keys give the same layout, so an unchanged text keeps its layout.</summary>
internal sealed class TextLayoutKey : IEquatable<TextLayoutKey>
{
    public required TextRun[] Runs;
    public float? W, H;
    public bool Wrap;
    public int? MaxLines;
    public TextOverflow Overflow;
    public float? LineHeight;
    public float LetterSpacing;
    public float? FitMin;

    /// <summary>The node's font size, which <see cref="FitMin"/> shrinks from.</summary>
    public float BaseSize;

    public bool Equals(TextLayoutKey? o)
    {
        if (o is null) return false;
        if (W != o.W || H != o.H || Wrap != o.Wrap || MaxLines != o.MaxLines || Overflow != o.Overflow || LineHeight != o.LineHeight
            || LetterSpacing != o.LetterSpacing || FitMin != o.FitMin || BaseSize != o.BaseSize || Runs.Length != o.Runs.Length) return false;
        for (var i = 0; i < Runs.Length; i++)
            if (!Runs[i].Equals(o.Runs[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as TextLayoutKey);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var r in Runs) h.Add(r);
        h.Add(W); h.Add(H); h.Add(Wrap); h.Add(MaxLines); h.Add(Overflow); h.Add(LineHeight); h.Add(LetterSpacing); h.Add(FitMin);
        return h.ToHashCode();
    }

    public TextLayoutKey With(int? maxLines, float scale, TextOverflow overflow) => new()
    {
        Runs = scale == 1 ? Runs : Runs.Select(r => r with { Size = r.Size * scale }).ToArray(),
        W = W, H = H, Wrap = Wrap, MaxLines = maxLines, Overflow = overflow, LineHeight = LineHeight,
        LetterSpacing = LetterSpacing, FitMin = null, BaseSize = BaseSize * scale,
    };
}

/// <summary>Shaped glyphs of one span on one line, positioned from the line's start at its baseline.</summary>
internal sealed class GlyphRun
{
    public required int Span;
    public required SKTextBlob Blob;
    public float X0, X1;

    /// <summary>The glyphs and their font, for the outline length of a dashed stroke.</summary>
    public SKTypeface? Typeface;
    public float FontSize;
    public ushort[] Glyphs = [];
    public float UnderlinePosition, UnderlineThickness, StrikePosition, StrikeThickness;
}

internal sealed class LaidLine
{
    public float Top, Baseline, Width, Height;
    public required GlyphRun[] Runs;

    private SKRect[]? _ink; // an array, so a racing first read publishes it atomically

    /// <summary>
    /// Where the line's fill can draw, from its start at its baseline: the runs' conservative glyph bounds and their
    /// underline and strikethrough, whether drawn or not. A stroke reaches further.
    /// </summary>
    public SKRect Ink
    {
        get
        {
            if (Volatile.Read(ref _ink) is { } known) return known[0];
            SKRect? ink = null;
            foreach (var run in Runs)
            {
                var r = SKRect.Union(run.Blob.Bounds, SKRect.Union(
                    new SKRect(run.X0, run.UnderlinePosition, run.X1, run.UnderlinePosition + run.UnderlineThickness),
                    new SKRect(run.X0, run.StrikePosition - run.StrikeThickness, run.X1, run.StrikePosition)));
                ink = ink is { } i ? SKRect.Union(i, r) : r;
            }
            var result = ink ?? SKRect.Empty;
            Volatile.Write(ref _ink, [result]);
            return result;
        }
    }
}

/// <summary>
/// Text laid out for a tick (spec/docs/smart-nodes.md, text): shaped with kerning and ligatures (HarfBuzz, through
/// RichTextKit), broken into lines, measured. Immutable and shared by every scale and output; positions are in the
/// node's local pixels, so drawing at another scale changes nothing but the canvas matrix.
/// </summary>
internal sealed class TextLayout
{
    public static readonly TextLayout Empty = new() { Lines = [], Width = 0, Height = 0 };

    public required LaidLine[] Lines;

    /// <summary>The widest line and the lines' height: what <c>measure</c> reads.</summary>
    public float Width, Height;

    /// <summary>The fit's font scale (1 without fit).</summary>
    public float Scale = 1;

    /// <summary>Families this layout asked for that the engine doesn't have (drawn in the default family).</summary>
    public IReadOnlyList<string> MissingFamilies = [];

    /// <summary>Lines made to produce this one (fit probes and ellipsis passes included), for the text budget.</summary>
    public int Cost = 1;

    /// <summary>A fit probe stopped before its last line (it can't fit).</summary>
    public bool Stopped;

    /// <summary>
    /// Text was left out: lines past <c>maxLines</c> (or past the box's height, for an ellipsis), or a line cut with
    /// "…". For the preview checks (<see cref="TickSnapshot.Texts"/>), not for drawing.
    /// </summary>
    public bool Truncated;

    /// <summary><c>fit</c> found no size down to its minimum at which the text fits: it is laid out at the minimum.</summary>
    public bool FitFailed;

    public float FirstBaseline => Lines.Length > 0 ? Lines[0].Baseline : 0;

    private double[]? _outline; // an array, so a racing first read publishes length and contours together

    /// <summary>
    /// The glyph outlines' total length and contour count (every contour of every glyph drawn), for the dash budget of
    /// a stroked text: a bound at any resolution (<see cref="SceneMath.Outline"/>). Measured once per layout, each
    /// distinct glyph of a run once.
    /// </summary>
    public (double Length, double Contours) GlyphOutline
    {
        get
        {
            if (Volatile.Read(ref _outline) is { } known) return (known[0], known[1]);
            double total = 0, contours = 0;
            foreach (var line in Lines)
                foreach (var run in line.Runs)
                {
                    if (run.Typeface is null || run.Glyphs.Length == 0) continue;
                    using var font = TextLayoutEngine.MakeFont(run.Typeface, run.FontSize);
                    var measured = new Dictionary<ushort, (double Length, int Contours)>();
                    foreach (var glyph in run.Glyphs)
                    {
                        if (!measured.TryGetValue(glyph, out var outline))
                        {
                            outline = (0, 0);
                            using var path = font.GetGlyphPath(glyph);
                            if (path is not null && !path.IsEmpty)
                            {
                                var (length, n, _) = SceneMath.Outline(path);
                                outline = (length, n);
                            }
                            measured[glyph] = outline;
                        }
                        total += outline.Length;
                        contours += outline.Contours;
                    }
                }
            Volatile.Write(ref _outline, [total, contours]);
            return (total, contours);
        }
    }
}

/// <summary>
/// Lines left for one <see cref="TextLayoutEngine.Layout"/> call: each is charged as wrapping produces it, before it is
/// made; one more than the limit is over the text budget.
/// </summary>
internal sealed class LayoutMeter(int limit)
{
    public int Used;

    public int Left => limit - Used;

    public void Charge()
    {
        if (++Used > limit)
            throw new BudgetExceeded("budget.text", "Too many texts changed this tick; the item is drawn again on a later tick.");
    }
}

/// <summary>Resolves font families: the registry (bundled Inter, user fonts), then the system; otherwise the default family.</summary>
internal sealed class SceneFontMapper : FontMapper
{
    public const string DefaultFamily = "Inter";

    public static readonly SceneFontMapper Instance = new();

    private static readonly ConcurrentDictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the engine has <paramref name="family"/> (registered or installed).</summary>
    public static bool Has(string family) => Known.GetOrAdd(family, static f =>
    {
        if (FontRegistry.TryGet(f, 400, SKFontStyleSlant.Upright, out _)) return true;
        using var typeface = SKFontManager.Default.MatchFamily(f);
        return typeface is not null && string.Equals(typeface.FamilyName, f, StringComparison.OrdinalIgnoreCase);
    });

    public override SKTypeface TypefaceFromStyle(IStyle style, bool ignoreFontVariants)
    {
        var slant = style.FontItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        if (FontRegistry.TryGet(style.FontFamily, style.FontWeight, slant, out var typeface)) return typeface;
        if (Has(style.FontFamily)) return base.TypefaceFromStyle(style, ignoreFontVariants);
        return FontRegistry.TryGet(DefaultFamily, style.FontWeight, slant, out var fallback) ? fallback : SKTypeface.Default;
    }
}

internal static class TextLayoutEngine
{
    /// <summary>RichTextKit keeps process-wide shaping state; layouts are made one at a time.</summary>
    private static readonly Lock Gate = new();

    private const string Ellipsis = "…";

    /// <summary>
    /// Test seam: called on the laying-out thread with the number of lines RichTextKit produced each time it lays out a
    /// paragraph (every read of a <see cref="TextBlock"/>'s lines that lays it out), whether or not they are kept.
    /// </summary>
    [ThreadStatic] internal static Action<int>? LinesProduced;

    /// <summary>
    /// Lays out <paramref name="key"/> making at most <paramref name="budget"/> lines (every line of every paragraph,
    /// a blank one too, fit probes and ellipsis passes included); throws <see cref="BudgetExceeded"/> before making one
    /// more, so a paragraph wrapped to many lines stops there. The result's <see cref="TextLayout.Cost"/> is the lines
    /// it made.
    /// </summary>
    public static TextLayout Layout(TextLayoutKey key, int budget = int.MaxValue)
    {
        lock (Gate)
        {
            var meter = new LayoutMeter(budget);
            var missing = key.Runs.Select(r => r.Family).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !SceneFontMapper.Has(f)).ToArray();
            TextLayout result;
            if (key.FitMin is { } min && key.W is not null)
                result = Fit(key, min, meter);
            else
                result = Lay(key.With(key.MaxLines, 1, key.Overflow), meter);
            result.MissingFamilies = missing;
            result.Cost = meter.Used;
            return result;
        }
    }

    /// <summary>The largest size from the node's size down to <paramref name="min"/>, in 0.5 px steps, at which the text fits.</summary>
    private static TextLayout Fit(TextLayoutKey key, float min, LayoutMeter meter)
    {
        var size = key.BaseSize;
        min = Math.Clamp(min, 1, Math.Max(1, size));
        var steps = (int)Math.Floor((size - min) / 0.5f + 1e-4f);
        bool Fits(int k)
        {
            var scale = size > 0 ? (size - 0.5f * k) / size : 1;
            // Probe without truncation, so an ellipsis never counts as fitting. A probe stops as soon as it can't fit (one
            // line past maxLines, or a line ending below the box), and asks RichTextKit for lines a few at a time, so it
            // produces little more than its answer needs (every line produced is charged); that answer is the same as
            // the whole layout's.
            var probe = Lay(key.With(key.MaxLines + 1, scale, key.Overflow == TextOverflow.Ellipsis ? TextOverflow.Visible : key.Overflow), meter,
                key.H is { } box ? box + 0.01f : float.PositiveInfinity);
            var lines = probe.Lines.Length;
            return !probe.Stopped && probe.Width <= key.W!.Value + 0.01f && (key.H is not { } h || probe.Height <= h + 0.01f) && (key.MaxLines is not { } m || lines <= m);
        }
        var found = steps;
        var fitted = Fits(0);
        if (fitted) found = 0;
        else
        {
            int lo = 1, hi = steps; // smallest k that fits, assuming fitting is monotonic in size
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (Fits(mid))
                {
                    found = mid;
                    fitted = true;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }
        }
        var s = size > 0 ? (size - 0.5f * found) / size : 1;
        var result = Lay(key.With(key.MaxLines, s, key.Overflow), meter);
        result.Scale = s;
        result.FitFailed = !fitted;
        return result;
    }

    /// <summary>
    /// Lays out <paramref name="key"/>; with <paramref name="stopBelow"/> (a fit probe), stops after the first line that
    /// ends below it and marks the layout <see cref="TextLayout.Stopped"/> when lines were left out.
    /// </summary>
    private static TextLayout Lay(TextLayoutKey key, LayoutMeter meter, float stopBelow = float.PositiveInfinity)
    {
        var paragraphs = Paragraphs(key.Runs);
        var lines = new List<LaidLine>();
        var remaining = key.MaxLines ?? int.MaxValue;
        var ellipsis = key.Overflow == TextOverflow.Ellipsis;
        var height = 0f;
        var stopped = false;
        var truncated = false;
        var p = 0;
        for (; p < paragraphs.Count && remaining > 0 && !stopped; p++)
        {
            var dropsLater = p + 1 < paragraphs.Count && remaining == 1;
            var block = LayParagraph(key, paragraphs[p], remaining, dropsLater && ellipsis, meter, stopBelow - height, out stopped, out var cut);
            truncated |= cut || (dropsLater && ellipsis);
            lines.AddRange(block);
            remaining -= block.Count;
            foreach (var line in block) height += line.Height;
            if (height > stopBelow && p + 1 < paragraphs.Count) stopped = true;
        }
        if (p < paragraphs.Count && !stopped) truncated = true; // paragraphs past maxLines

        Stack(lines);
        var layout = new TextLayout
        {
            Lines = lines.ToArray(),
            Width = lines.Count == 0 ? 0 : lines.Max(l => l.Width),
            Height = lines.Count == 0 ? 0 : lines[^1].Top + lines[^1].Height,
            Stopped = stopped,
            Truncated = truncated,
        };

        // Ellipsis with a box height: keep the lines that fit (at least one) and end the last with "…".
        if (ellipsis && key.H is { } h && layout.Height > h + 0.01f && layout.Lines.Length > 1)
        {
            var fit = 0;
            while (fit < layout.Lines.Length && layout.Lines[fit].Top + layout.Lines[fit].Height <= h + 0.01f) fit++;
            fit = Math.Max(1, fit);
            if (fit < layout.Lines.Length && fit < (key.MaxLines ?? int.MaxValue))
            {
                var cut = Lay(key.With(fit, 1, key.Overflow), meter);
                cut.Truncated = true;
                return cut;
            }
        }
        return layout;
    }

    private sealed class SpanStyle : Style
    {
        public int Span;
    }

    private static List<List<(int Span, string Text)>> Paragraphs(TextRun[] runs)
    {
        var result = new List<List<(int, string)>> { new() };
        for (var i = 0; i < runs.Length; i++)
        {
            var parts = runs[i].Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var k = 0; k < parts.Length; k++)
            {
                if (k > 0) result.Add([]);
                if (parts[k].Length > 0) result[^1].Add((i, parts[k]));
            }
        }
        return result;
    }

    private static SpanStyle StyleOf(TextLayoutKey key, int span) => new()
    {
        Span = span,
        FontFamily = SceneFontMapper.Has(key.Runs[span].Family) ? key.Runs[span].Family : SceneFontMapper.DefaultFamily,
        FontSize = Math.Clamp(key.Runs[span].Size, 0.5f, 4096),
        FontWeight = Math.Clamp(key.Runs[span].Weight, 1, 1000),
        FontItalic = key.Runs[span].Italic,
        LetterSpacing = key.LetterSpacing,
    };

    /// <param name="truncated">Lines of the paragraph were left out, or its line cut with "…" (a whole layout only, not a fit probe).</param>
    private static List<LaidLine> LayParagraph(TextLayoutKey key, List<(int Span, string Text)> fragments, int maxLines, bool forceEllipsis, LayoutMeter meter, float stopBelow, out bool stopped, out bool truncated)
    {
        stopped = false;
        truncated = false;
        var ellipsis = key.Overflow == TextOverflow.Ellipsis;
        var tb = new TextBlock { FontMapper = SceneFontMapper.Instance, Alignment = TextAlignment.Left };
        if (key.Wrap && key.W is { } ww) tb.MaxWidth = Math.Max(0, ww);
        else if (ellipsis && key.W is { } we)
        {
            tb.MaxWidth = Math.Max(0, we);
            maxLines = 1; // one line, cut with "…" where it is wider than the box
        }
        tb.EllipsisEnabled = ellipsis;
        // RichTextKit stops breaking lines one past what the budget has left (that line is produced, charged and refused),
        // so a paragraph that would wrap to more lines than the budget allows is never broken further. Below that bound
        // the lines are the same: the cut only applies when the budget is about to run out.
        int Cap() => Math.Min(maxLines, meter.Left < int.MaxValue ? meter.Left + 1 : int.MaxValue);

        // An empty paragraph (a blank line) still has a line's height, in the font in force where it is.
        var emptySpan = fragments.Count == 0 ? 0 : fragments[0].Span;
        if (fragments.Count == 0)
        {
            meter.Charge();
            return [EmptyLine(StyleOf(key, Math.Min(emptySpan, key.Runs.Length - 1)), key)];
        }
        foreach (var (span, text) in fragments) tb.AddText(text, StyleOf(key, span));
        if (forceEllipsis)
        {
            // Lines after this one are dropped: end it with "…" (RichTextKit truncates it further if that doesn't fit).
            tb.AddText(Ellipsis, StyleOf(key, fragments[^1].Span));
        }

        // Every line RichTextKit produces is charged as soon as it is produced, kept or not. A fit probe (a finite
        // stopBelow) only needs the lines up to the first that ends below the box, so it asks for a few lines first and
        // twice as many each time those all fit, up to the cap. RichTextKit may break the last line it is allowed early,
        // so only the lines before it are taken as the uncapped layout's: when one of them ends below the box, the next
        // exists and the probe stops there, as it would with every line; otherwise it asks again.
        var cap = Cap();
        var limit = float.IsPositiveInfinity(stopBelow) ? cap : Math.Min(cap, FirstProbeLines);
        IReadOnlyList<TextLine> produced;
        while (true)
        {
            tb.MaxLines = limit < int.MaxValue ? limit : null;
            produced = tb.Lines;
            LinesProduced?.Invoke(produced.Count);
            for (var i = 0; i < produced.Count; i++) meter.Charge();
            if (produced.Count < limit || limit >= cap || EndsBelow(key, produced, produced.Count - 1, emptySpan, stopBelow)) break;
            cap = Cap();
            limit = (int)Math.Min(2L * limit, cap);
        }
        // A whole layout's cap is maxLines (one more line than the budget allows has thrown above). RichTextKit's
        // Truncated is also set when the lines merely reach the cap, so text left out is what it laid out falling
        // short of the paragraph: lines past maxLines, or a line cut with "…".
        truncated = float.IsPositiveInfinity(stopBelow) && tb.MeasuredLength < tb.Length;

        var lines = new List<LaidLine>();
        var made = 0f; // the lines' height so far
        foreach (var line in produced)
        {
            if (made > stopBelow)
            {
                stopped = true;
                break;
            }
            var runs = new List<GlyphRun>();
            var lineTop = line.YCoord + line.BaseLine;
            foreach (var run in line.Runs)
            {
                var style = run.Style as SpanStyle;
                var spanIndex = style?.Span ?? fragments[^1].Span;
                var fontSize = run.Style.FontSize;
                using var font = MakeFont(run.Typeface, fontSize);
                var m = font.Metrics;
                var glyphs = run.Glyphs.AsSpan();
                if (glyphs.Length == 0 || run.RunKind == FontRunKind.TrailingWhitespace) continue;
                var positions = run.GlyphPositions.ToArray();
                for (var g = 0; g < positions.Length; g++) positions[g] = new SKPoint(positions[g].X, positions[g].Y - lineTop);
                using var builder = new SKTextBlobBuilder();
                builder.AddPositionedRun(glyphs, font, positions);
                var blob = builder.Build();
                if (blob is null) continue;
                runs.Add(new GlyphRun
                {
                    Span = spanIndex,
                    Blob = blob,
                    X0 = run.XCoord,
                    X1 = run.XCoord + run.Width,
                    Typeface = run.Typeface,
                    FontSize = fontSize,
                    Glyphs = glyphs.ToArray(),
                    UnderlinePosition = m.UnderlinePosition ?? fontSize * 0.1f,
                    UnderlineThickness = m.UnderlineThickness is > 0 ? m.UnderlineThickness.Value : Math.Max(1, fontSize / 14),
                    StrikePosition = m.StrikeoutPosition ?? -fontSize * 0.3f,
                    StrikeThickness = m.StrikeoutThickness is > 0 ? m.StrikeoutThickness.Value : Math.Max(1, fontSize / 14),
                });
            }
            var (height, baseline) = LineMetrics(key, line, emptySpan);
            lines.Add(new LaidLine { Runs = runs.ToArray(), Width = line.Width, Height = height, Baseline = baseline });
            made += height;
        }
        if (lines.Count == 0)
        {
            meter.Charge();
            lines.Add(EmptyLine(StyleOf(key, emptySpan), key));
        }
        return lines;
    }

    /// <summary>Lines a fit probe asks RichTextKit for first.</summary>
    private const int FirstProbeLines = 8;

    /// <summary>Whether the first <paramref name="count"/> lines end below <paramref name="stopBelow"/>.</summary>
    private static bool EndsBelow(TextLayoutKey key, IReadOnlyList<TextLine> lines, int count, int emptySpan, float stopBelow)
    {
        var made = 0f;
        for (var i = 0; i < count; i++)
        {
            made += LineMetrics(key, lines[i], emptySpan).Height;
            if (made > stopBelow) return true;
        }
        return false;
    }

    /// <summary>A produced line's height and baseline (from its top): its runs' fonts, or the empty line's when it has no text.</summary>
    private static (float Height, float Baseline) LineMetrics(TextLayoutKey key, TextLine line, int emptySpan)
    {
        float asc = 0, desc = 0, lead = 0, size = 0;
        foreach (var run in line.Runs)
        {
            var fontSize = run.Style.FontSize;
            using var font = MakeFont(run.Typeface, fontSize);
            var m = font.Metrics;
            asc = Math.Max(asc, -m.Ascent);
            desc = Math.Max(desc, m.Descent);
            lead = Math.Max(lead, m.Leading);
            size = Math.Max(size, fontSize);
        }
        if (size == 0)
        {
            var empty = EmptyLine(StyleOf(key, emptySpan), key);
            return (empty.Height, empty.Baseline);
        }
        return LineBox(asc, desc, lead, size, key.LineHeight);
    }

    private static LaidLine EmptyLine(SpanStyle style, TextLayoutKey key)
    {
        var typeface = SceneFontMapper.Instance.TypefaceFromStyle(style, false);
        using var font = MakeFont(typeface, style.FontSize);
        var m = font.Metrics;
        var (height, baseline) = LineBox(-m.Ascent, m.Descent, m.Leading, style.FontSize, key.LineHeight);
        return new LaidLine { Runs = [], Width = 0, Height = height, Baseline = baseline };
    }

    /// <summary>A line's height and baseline (from its top): the font's spacing, or <c>lineHeight</c> × size with the leading split above and below.</summary>
    private static (float Height, float Baseline) LineBox(float asc, float desc, float lead, float size, float? lineHeight)
    {
        if (lineHeight is { } l)
        {
            var h = Math.Max(0, l * size);
            return (h, (h - (asc + desc)) / 2 + asc);
        }
        return (asc + desc + lead, lead / 2 + asc);
    }

    /// <summary>Stacks the lines (their baselines are relative until now) from the top of the block.</summary>
    private static void Stack(List<LaidLine> lines)
    {
        float y = 0;
        foreach (var line in lines)
        {
            line.Top = y;
            line.Baseline += y;
            y += line.Height;
        }
    }

    /// <summary>The glyph font: unhinted with subpixel positions and linear metrics, so a layout looks the same at every scale.</summary>
    public static SKFont MakeFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
    };
}
