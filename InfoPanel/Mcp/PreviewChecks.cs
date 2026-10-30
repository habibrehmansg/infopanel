using System.Globalization;
using System.Text;
using InfoPanel.Mcp.Text;
using InfoPanel.Mcp.Tools;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using TextFit = InfoPanel.Scenes.TextFit;

namespace InfoPanel.Mcp;

/// <summary>
/// What the preview checks found: each problem with its next step, what they couldn't look at, and the items whose
/// pictures were still loading when the preview was drawn (a placeholder in the picture, not the design).
/// </summary>
public sealed record PreviewCheckReport(IReadOnlyList<string> Problems, IReadOnlyList<string> NotChecked, IReadOnlyList<string>? Loading = null)
{
    public const int MaxLinesShown = 40;

    /// <summary>The report as the tool's answer says it; never an unconditional "no problems".</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder("Checks (heuristics: they read boxes and sample pixels, so look at the picture too):\n");
            if (Problems.Count == 0)
            {
                text.Append(NotChecked.Count == 0
                    ? "The checks found nothing (they cover fit, the canvas edge, box overlap and contrast)."
                    : "The checks found nothing in what they could look at (fit, the canvas edge, box overlap and contrast).");
            }
            else
            {
                text.Append("Problems:");
                Lines(text, Problems);
            }
            if (NotChecked.Count > 0)
            {
                text.Append("\nNot checked:");
                Lines(text, NotChecked);
            }
            foreach (var item in (Loading ?? []).Take(MaxLinesShown))
                text.Append($"\n{item}: its picture is still loading; preview again.");
            return text.ToString();
        }
    }

    private static void Lines(StringBuilder text, IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(MaxLinesShown))
            text.Append("\n- ").Append(line);
        if (lines.Count > MaxLinesShown)
            text.Append($"\n- … and {lines.Count - MaxLinesShown} more");
    }
}

/// <summary>
/// <c>render_preview</c>'s four checks (heuristics, and said to be), on one tick snapshot, the picture's:
/// <list type="number">
/// <item><b>Text that doesn't fit its box</b>, as the layout knows it (<see cref="TickSnapshot.Texts"/>): cut with "…",
/// lines left out, clipped, drawn past its box, shrunk to its fit minimum.</item>
/// <item><b>Past the canvas:</b> an item's ink more than <c>canvasTolerance</c> pixels outside the canvas.</item>
/// <item><b>Overlap:</b> two visible items whose ink boxes overlap by more than <c>overlapMin</c> of the smaller,
/// unless one contains the other and is drawn below it (a card behind its content). Boxes, not shapes.</item>
/// <item><b>Contrast:</b> each solid-colour text node, its colour composited with its own and its groups' opacity,
/// against the pixels under its lines (each where it is drawn) in the same tick drawn without text: the colour covering at least
/// <see cref="DominantShare"/> of them, else the worst covering at least <see cref="MinorShare"/>; below
/// <c>contrastMin</c> (WCAG's ratio) is a problem.</item>
/// </list>
/// What they skip is said per item: gradient-filled text, text in a group drawn with a blend mode, text over no main
/// colour, and whatever is past the budgets
/// (<see cref="MaxSampledPixels"/> sampled, <see cref="MaxPairs"/> pairs compared). The thresholds are the text layers'
/// (<c>checks.json</c>), read on every call.
/// </summary>
public static class PreviewChecks
{
    public const long MaxSampledPixels = 4_000_000;
    public const int MaxPairs = 10_000;
    public const double DominantShare = 0.7;
    public const double MinorShare = 0.1;

    private const int MaxQuoted = 40;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);

        public bool Contains(Box other) =>
            other.Left >= Left - 0.5 && other.Top >= Top - 0.5 && other.Right <= Right + 0.5 && other.Bottom <= Bottom + 0.5;

        public double Overlap(Box other) =>
            Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left)) * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
    }

    public static PreviewCheckReport Run(TickSnapshot snapshot, Document document, CheckThresholds thresholds)
    {
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in ItemNames.Flatten(document.Items))
            items.TryAdd(item.Id, item);
        string Label(string id) => items.TryGetValue(id, out var item) ? ItemNames.Label(item) : id;

        var problems = new List<string>();
        var notChecked = new List<string>();
        Fit(snapshot, Label, problems);

        // The visible scene items' ink boxes, in drawing order (a group's box is its items').
        var scenes = snapshot.Bounds
            .Where(b => items.TryGetValue(b.Id, out var item) && item is SceneItem)
            .Select(b => (b.Id, Box: InkBox(b)))
            .ToList();
        Canvas(scenes, snapshot.Width, snapshot.Height, thresholds.CanvasTolerance, Label, problems);
        Overlap(scenes, thresholds.OverlapMin, Label, problems, notChecked);
        Contrast(snapshot, thresholds.ContrastMin, Label, problems, notChecked);
        // Media still loading draws as a placeholder: said, so the AI previews again rather than judge it. No waiting.
        var loading = snapshot.Diagnostics.Where(d => d.Code == "media.loading").Select(d => d.ItemId).Distinct().Select(Label).ToList();
        return new PreviewCheckReport(problems, notChecked, loading);
    }

    private static void Fit(TickSnapshot snapshot, Func<string, string> label, List<string> problems)
    {
        foreach (var text in snapshot.Texts)
        {
            var (what, next) = text.Fit switch
            {
                TextFit.Ellipsis => ("ends with \"…\": it doesn't fit its box", "Make the box wider or the text smaller."),
                TextFit.LinesLeftOut => ("has lines left out (maxLines)", "Allow more lines, make the box larger or the text smaller."),
                TextFit.Clipped => ("is clipped by its box", "Make the box larger or the text smaller."),
                TextFit.PastBox => ("is drawn past its box", "Make the box larger or the text smaller."),
                TextFit.FitMinimum => ("is shrunk to its fit minimum and still doesn't fit", "Make the box larger, the text shorter or the minimum smaller."),
                _ => ((string?)null, (string?)null),
            };
            if (what != null)
                problems.Add($"{label(text.ItemId)}: its text {Quote(text.Text)} {what}. {next}");
        }
    }

    private static void Canvas(List<(string Id, Box Box)> scenes, int width, int height, double tolerance, Func<string, string> label, List<string> problems)
    {
        foreach (var (id, box) in scenes)
        {
            var past = new[] { ("left", -box.Left), ("top", -box.Top), ("right", box.Right - width), ("bottom", box.Bottom - height) }.MaxBy(e => e.Item2);
            if (past.Item2 > tolerance)
                problems.Add($"{label(id)}: its drawing reaches {Pixels(past.Item2)} px past the canvas's {past.Item1} edge. Move it inside or make it smaller.");
        }
    }

    private static void Overlap(List<(string Id, Box Box)> scenes, double overlapMin, Func<string, string> label, List<string> problems, List<string> notChecked)
    {
        var compared = 0;
        for (var i = 0; i < scenes.Count; i++)
        {
            for (var j = i + 1; j < scenes.Count; j++)
            {
                if (++compared > MaxPairs)
                {
                    foreach (var (id, _) in scenes.Skip(i))
                        notChecked.Add($"{label(id)} (its overlaps: past the {MaxPairs.ToString("N0", Invariant)} item pairs the overlap check compares)");
                    return;
                }

                var (below, above) = (scenes[i].Box, scenes[j].Box); // i is drawn first
                var smaller = Math.Min(below.Area, above.Area);
                if (smaller <= 0)
                    continue;
                var share = below.Overlap(above) / smaller;
                if (share <= overlapMin || below.Contains(above)) // a card under its content isn't an overlap
                    continue;
                var how = above.Contains(below) ? $", and {label(scenes[j].Id)} is drawn over all of {label(scenes[i].Id)}" : "";
                problems.Add($"{label(scenes[i].Id)} and {label(scenes[j].Id)} overlap (their boxes; the drawn shapes may not): {Percent(share)} of the smaller{how}. " +
                    "Move one, or put the one that holds the other first so it is drawn below.");
            }
        }
    }

    private static void Contrast(TickSnapshot snapshot, double contrastMin, Func<string, string> label, List<string> problems, List<string> notChecked)
    {
        var texts = snapshot.Texts;
        if (texts.Count == 0)
            return;

        // What is under the text: the same tick drawn without its text nodes, at most MaxSampledPixels in all.
        var scale = (float)Math.Min(1, Math.Sqrt(MaxSampledPixels / ((double)Math.Max(1, snapshot.Width) * Math.Max(1, snapshot.Height))));
        scale = snapshot.EffectiveScale(scale);
        var width = Math.Max(1, (int)Math.Round(snapshot.Width * scale));
        var height = Math.Max(1, (int)Math.Round(snapshot.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            snapshot.Draw(canvas, scale, withoutText: true);
            canvas.Flush();
        }
        var pixels = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;

        long sampled = 0;
        foreach (var text in texts)
        {
            var name = $"{label(text.ItemId)}: text {Quote(text.Text)}";
            if (text.Colors is not { } colors)
            {
                notChecked.Add($"{name} (filled with a gradient or a composed paint: its contrast isn't checked)");
                continue;
            }
            if (text.Blended)
            {
                // The checks have no model of compositing: its colour on what is below isn't its colour (Codex p2 #4).
                notChecked.Add($"{name} (in a group drawn with a blend mode: its contrast isn't checked)");
                continue;
            }
            if (colors.Count == 0)
                continue;

            // The pixels under each line where it is drawn (Codex p2 #5: not the box's unused width), in bitmap pixels.
            var areas = new List<(int X0, int Y0, int X1, int Y1)>();
            long count = 0;
            foreach (var line in text.Lines ?? new[] { text.Box })
            {
                var x0 = Math.Clamp((int)Math.Floor(line.X * scale), 0, width);
                var y0 = Math.Clamp((int)Math.Floor(line.Y * scale), 0, height);
                var x1 = Math.Clamp((int)Math.Ceiling((line.X + line.W) * scale), 0, width);
                var y1 = Math.Clamp((int)Math.Ceiling((line.Y + line.H) * scale), 0, height);
                if (x1 > x0 && y1 > y0)
                {
                    areas.Add((x0, y0, x1, y1));
                    count += (long)(x1 - x0) * (y1 - y0);
                }
            }
            if (count <= 0)
                continue; // off the canvas: the canvas check says so
            if (sampled + count > MaxSampledPixels)
            {
                notChecked.Add($"{name} (its contrast: past the {MaxSampledPixels.ToString("N0", Invariant)} pixels the contrast check samples)");
                continue;
            }
            sampled += count;

            var histogram = new Dictionary<uint, int>();
            foreach (var (x0, y0, x1, y1) in areas)
            {
                for (var y = y0; y < y1; y++)
                {
                    var row = pixels.Slice(y * stride, width * 4);
                    for (var x = x0; x < x1; x++)
                    {
                        var rgb = (uint)(row[x * 4] << 16 | row[x * 4 + 1] << 8 | row[x * 4 + 2]);
                        histogram[rgb] = histogram.GetValueOrDefault(rgb) + 1;
                    }
                }
            }
            var ranked = histogram.OrderByDescending(e => e.Value).ToList();
            List<uint> backgrounds = ranked[0].Value >= DominantShare * count
                ? [ranked[0].Key]
                : ranked.Where(e => e.Value >= MinorShare * count).Select(e => e.Key).ToList();
            if (backgrounds.Count == 0)
            {
                notChecked.Add($"{name} (the background under it has no main colour: its contrast isn't checked)");
                continue;
            }

            (double Ratio, uint Text, uint Background)? worst = null;
            foreach (var color in colors)
            {
                foreach (var background in backgrounds)
                {
                    var ratio = Ratio(Composite(color, text.Opacity, background), background);
                    if (worst is not { } w || ratio < w.Ratio)
                        worst = (ratio, color, background);
                }
            }
            if (worst is { } found && found.Ratio < contrastMin)
            {
                var alpha = (found.Text & 0xFF) / 255.0 * Math.Clamp(text.Opacity, 0, 1);
                var faded = alpha < 1 ? $" at {Percent(alpha)} opacity" : "";
                problems.Add($"{name} has contrast {Number(found.Ratio)}:1 against what is under it ({Hex(found.Text >> 8)}{faded} on {Hex(found.Background)}); " +
                    $"{Number(contrastMin)}:1 or more reads well. Change the text's colour or the background's.");
            }
        }
    }

    /// <summary>A straight-alpha 0xRRGGBBAA colour at <paramref name="opacity"/> over an opaque 0xRRGGBB one, in sRGB as renderers blend.</summary>
    private static uint Composite(uint color, double opacity, uint background)
    {
        var a = (color & 0xFF) / 255.0 * Math.Clamp(opacity, 0, 1);
        uint Channel(int shift) => (uint)Math.Round(a * (color >> (shift + 8) & 0xFF) + (1 - a) * (background >> shift & 0xFF));
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    /// <summary>WCAG's contrast ratio of two 0xRRGGBB colours.</summary>
    private static double Ratio(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(uint rgb)
    {
        static double Linear(uint c8)
        {
            var c = c8 / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(rgb >> 16 & 0xFF) + 0.7152 * Linear(rgb >> 8 & 0xFF) + 0.0722 * Linear(rgb & 0xFF);
    }

    /// <summary>A bounds rectangle (its centre, size and rotation) as the axis-aligned box around it.</summary>
    private static Box InkBox(ItemBounds bounds)
    {
        var ink = bounds.Ink;
        var cx = ink.X + ink.W / 2;
        var cy = ink.Y + ink.H / 2;
        var radians = bounds.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians)));
        var hw = (ink.W * cos + ink.H * sin) / 2;
        var hh = (ink.W * sin + ink.H * cos) / 2;
        return new Box(cx - hw, cy - hh, cx + hw, cy + hh);
    }

    private static string Quote(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return $"\"{(line.Length > MaxQuoted ? line[..MaxQuoted] + "…" : line)}\"";
    }

    private static string Hex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("x6", Invariant);

    private static string Number(double value) => value.ToString("0.#", Invariant);

    private static string Pixels(double value) => value.ToString("0.#", Invariant);

    private static string Percent(double share) => (share * 100).ToString("0", Invariant) + " %";
}
