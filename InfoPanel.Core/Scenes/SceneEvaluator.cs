using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InfoPanel.Images;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>A state cell's value between ticks and when it last updated (elapsed seconds).</summary>
internal sealed class CellState(Val value, double updated)
{
    public Val Value = value;
    public double Updated = updated;
}

/// <summary>
/// What the runtime keeps for one item between ticks: its state cells, keyed by (cell name, canonical definition) so a
/// changed definition starts again (spec/docs/identity.md), and its text layouts, so unchanged text keeps its layout.
/// </summary>
internal sealed class ItemRuntime(string id)
{
    public string Id { get; } = id;
    public readonly Dictionary<(string Name, string Definition), CellState> Cells = new();
    public CompiledScene? Scene;
    public readonly Dictionary<int, (TextLayoutKey Key, TextLayout Layout)> Texts = new();
    public readonly Dictionary<(int Table, int Row, int Column), (TextLayoutKey Key, TextLayout Layout)> TableCells = new();
    public bool Suspended;
    public long SuspendedAt;

    // The watchdog: ticks whose evaluation or slowest draw took longer than the item budget, counted once per tick
    // however many outputs and scales drew it. Draws mark from their own threads.
    private readonly Lock _watch = new();
    private long _slowTick = -1;
    private int _slowRun;

    /// <summary>Marks tick <paramref name="tick"/> slow; a tick already counted, or older than the last counted, changes nothing.</summary>
    public void MarkSlow(long tick)
    {
        lock (_watch)
        {
            if (tick <= _slowTick) return;
            _slowRun = tick == _slowTick + 1 ? _slowRun + 1 : 1;
            _slowTick = tick;
        }
    }

    /// <summary>
    /// After evaluating tick <paramref name="tick"/>: true when the ticks up to it (or up to the one before, whose draws
    /// are done) were slow three times running; the count then starts again after this tick.
    /// </summary>
    public bool TakeSuspension(long tick)
    {
        lock (_watch)
        {
            if (_slowRun < 3 || _slowTick < tick - 1) return false;
            _slowRun = 0;
            _slowTick = Math.Max(_slowTick, tick);
            return true;
        }
    }

    public void Use(CompiledScene scene)
    {
        if (ReferenceEquals(Scene, scene)) return;
        Scene = scene;
        Texts.Clear();
        TableCells.Clear();
    }
}

/// <summary>Per-tick services and budgets shared by a document's items.</summary>
internal sealed class TickServices
{
    public MediaSampleCache? Media;
    public IReadOnlyDictionary<string, IMediaProvider> Providers = new Dictionary<string, IMediaProvider>();
    public readonly List<MediaSample> Retained = [];

    /// <summary>Text layouts left for this tick across the document (cache misses only).</summary>
    public int LayoutsLeft = int.MaxValue;

    public int TextLength = 4096;

    /// <summary>This tick's media lookups, so every node that shows the same source at the same time shows the same frame.</summary>
    public readonly Dictionary<(string Hash, double Seconds, MediaPlayback Playback), MediaLookup> Assets = new();

    public readonly Dictionary<(string Provider, string Reference, double Seconds), (MediaSample? Sample, string? Error)> Provided = new();
}

internal sealed class BudgetExceeded(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Evaluates one item's compiled scene for a tick (spec/docs/scene.md, "Order of evaluation"): values that read neither
/// state nor measurements, the state cells, the values that read state, the text layout of measured text nodes, the
/// values that read measurements, then every node. Produces the item's resolved nodes.
/// </summary>
internal sealed class SceneEvaluator
{
    /// <summary>Most dash segments one stroke may produce; more is over budget.</summary>
    public const int MaxDashSegments = 100_000;

    /// <summary>Most text layouts one item may make in a tick (cache misses).</summary>
    public const int MaxItemLayouts = 2_000;

    private readonly CompiledScene _scene;
    private readonly ItemRuntime _rt;
    private readonly TickServices _services;
    private readonly Ev _e;
    private readonly double _elapsed;
    private readonly ItemDiagnostics _diag;
    private readonly RText?[] _laidOut;
    private readonly (TextLayout Layout, float? W, float? H)?[] _textLayouts;
    private int _layouts;

    public SceneEvaluator(CompiledScene scene, ItemRuntime rt, TickServices services, InputSnap[] inputs, SceneClock clock,
        double boxW, double boxH, ItemDiagnostics diag, int workLimit)
    {
        _scene = scene;
        _rt = rt;
        _services = services;
        _elapsed = clock.Elapsed;
        _diag = diag;
        _laidOut = new RText?[scene.TextNodes.Length];
        _textLayouts = new (TextLayout, float?, float?)?[scene.TextNodes.Length];
        _e = new Ev
        {
            Values = new Val[scene.ValueCount],
            State = new Val[scene.Cells.Length],
            Inputs = inputs,
            Clock = clock,
            MeasureW = new double[scene.TextNodes.Length],
            MeasureH = new double[scene.TextNodes.Length],
            BoxW = boxW,
            BoxH = boxH,
            WorkLimit = workLimit,
            TextLength = services.TextLength,
            Diagnostics = diag,
        };
    }

    /// <summary>Evaluates the tick; <paramref name="advance"/> false leaves the item's state untouched (a one-off render).</summary>
    public RNode[] Run(bool advance)
    {
        var e = _e;
        foreach (var i in _scene.PhaseBeforeState) e.Values[i] = _scene.Values[i].Eval(e);
        RunCells(advance);
        foreach (var i in _scene.PhaseAfterState) e.Values[i] = _scene.Values[i].Eval(e);
        CheckWork();

        foreach (var t in _scene.TextNodes)
        {
            if (!t.Measured) continue;
            // Only the layout: the node's position, paints and stroke may read the measurements and resolve below.
            var layout = TextLayoutOf(t).Layout;
            e.MeasureW[t.TextIndex] = layout.Width;
            e.MeasureH[t.TextIndex] = layout.Height;
        }
        foreach (var i in _scene.PhaseAfterLayout) e.Values[i] = _scene.Values[i].Eval(e);
        CheckWork();

        var nodes = Resolve(_scene.Nodes);
        CheckWork();
        return nodes;
    }

    private void CheckWork()
    {
        if (_e.Work > _e.WorkLimit)
            throw new BudgetExceeded("budget.work", $"Expression work {_e.Work} is over this item's budget of {_e.WorkLimit}.");
    }

    private void RunCells(bool advance)
    {
        var cells = _scene.Cells;
        if (cells.Length == 0) return;
        var e = _e;
        var existing = new CellState?[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            if (advance && _rt.Cells.TryGetValue((cells[i].Name, cells[i].DefinitionKey), out var s))
            {
                existing[i] = s;
                e.State[i] = s.Value;
            }
            else
            {
                e.State[i] = cells[i].Init.Eval(e);
            }
        }
        if (!advance) return;
        var next = new Val[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            if (existing[i] is not { } s) continue;
            e.Dt = Math.Max(0, _elapsed - s.Updated);
            next[i] = cells[i].Next.Eval(e);
        }
        e.Dt = 0;
        for (var i = 0; i < cells.Length; i++)
        {
            var key = (cells[i].Name, cells[i].DefinitionKey);
            if (existing[i] is { } s)
            {
                e.State[i] = next[i];
                s.Value = next[i];
                s.Updated = _elapsed;
            }
            else
            {
                _rt.Cells[key] = new CellState(e.State[i], _elapsed);
            }
        }
    }

    // ---- nodes -------------------------------------------------------------------------------------------------

    private RNode[] Resolve(CNode[] nodes)
    {
        var result = new List<RNode>(nodes.Length);
        foreach (var node in nodes)
        {
            if (node.Visible is { } v && !v.Bool(_e)) continue;
            var opacity = node.Opacity is { } o ? (float)Math.Clamp(o.Num(_e), 0, 1) : 1f;
            RNode? r = node switch
            {
                CGroup g => ResolveGroup(g),
                CShape s => ResolveShape(s),
                CText t => LayText(t),
                CSeries s => ResolveSeries(s),
                CTable t => ResolveTable(t),
                CMedia m => ResolveMedia(m),
                _ => null,
            };
            if (r is null) continue;
            r.Opacity *= opacity;
            result.Add(r);
        }
        return result.ToArray();
    }

    private RGroup ResolveGroup(CGroup g)
    {
        var m = SKMatrix.Identity;
        foreach (var step in g.Transform) m = m.PreConcat(Step(step));
        var effects = new List<REffect>(g.Effects.Length);
        foreach (var fx in g.Effects)
        {
            var sigma = (float)Math.Clamp(fx.Sigma.Num(_e), 0, 64);
            var color = fx.Color?.Col(_e) ?? 0;
            var dx = fx.Dx is { } x ? SceneMath.F(x.Num(_e)) : 0;
            var dy = fx.Dy is { } y ? SceneMath.F(y.Num(_e)) : 0;
            if (fx.Kind == EffectKind.Blur && sigma <= 0) continue;
            effects.Add(new REffect(fx.Kind, dx, dy, sigma, color));
        }
        return new RGroup
        {
            Matrix = m,
            Clip = g.Clip is null ? null : ResolveClip(g.Clip),
            Blend = g.Blend,
            Effects = effects.ToArray(),
            Children = Resolve(g.Children),
        };
    }

    private SKMatrix Step(CTransform step)
    {
        switch (step)
        {
            case CTranslate t: return SKMatrix.CreateTranslation(SceneMath.F(t.X.Num(_e)), SceneMath.F(t.Y.Num(_e)));
            case CRotate r: return SKMatrix.CreateRotationDegrees(SceneMath.F(r.Angle.Num(_e)), SceneMath.F(r.Cx.Num(_e)), SceneMath.F(r.Cy.Num(_e)));
            case CScale s: return SKMatrix.CreateScale(SceneMath.F(s.X.Num(_e)), SceneMath.F(s.Y.Num(_e)), SceneMath.F(s.Cx.Num(_e)), SceneMath.F(s.Cy.Num(_e)));
            case CSkew k:
            {
                var ax = Math.Clamp(k.X.Num(_e), -89, 89);
                var ay = Math.Clamp(k.Y.Num(_e), -89, 89);
                return SKMatrix.CreateSkew((float)Math.Tan(ax * Math.PI / 180), (float)Math.Tan(ay * Math.PI / 180));
            }
            case CMatrix c: return c.M;
            default: return SKMatrix.Identity;
        }
    }

    private RGeom? ResolveClip(CClip clip) => clip switch
    {
        CRectClip r => (RGeom?)RectGeom(r.X, r.Y, r.W, r.H, r.Radii) ?? new RectGeom(0, 0, 0, 0, null),
        CEllipseClip e => (RGeom?)Ellipse(e.Cx, e.Cy, e.Rx, e.Ry) ?? new RectGeom(0, 0, 0, 0, null),
        CArcClip a => (RGeom?)Arc(a.Cx, a.Cy, a.R, a.Inner, a.Start, a.Sweep, false) ?? new RectGeom(0, 0, 0, 0, null),
        CPathClip p => p.Path,
        _ => null,
    };

    private RectGeom? RectGeom(CExpr cx, CExpr cy, CExpr cw, CExpr ch, CExpr[]? radii)
    {
        var x = SceneMath.F(cx.Num(_e));
        var y = SceneMath.F(cy.Num(_e));
        var w = SceneMath.F(cw.Num(_e));
        var h = SceneMath.F(ch.Num(_e));
        SKPoint[]? r = null;
        if (radii is { Length: > 0 })
        {
            var max = Math.Max(0, Math.Min(w, h) / 2);
            var values = radii.Select(c => Math.Clamp(SceneMath.F(c.Num(_e)), 0, max)).ToArray();
            if (values.Length == 1) values = [values[0], values[0], values[0], values[0]];
            while (values.Length < 4) values = [.. values, 0];
            if (values.Any(v => v > 0)) r = values.Take(4).Select(v => new SKPoint(v, v)).ToArray();
        }
        if (w < 0 || h < 0) return null;
        return new RectGeom(x, y, w, h, r);
    }

    private EllipseGeom? Ellipse(CExpr cx, CExpr cy, CExpr crx, CExpr cry)
    {
        var x = SceneMath.F(cx.Num(_e));
        var y = SceneMath.F(cy.Num(_e));
        var rx = SceneMath.F(crx.Num(_e));
        var ry = SceneMath.F(cry.Num(_e));
        return rx <= 0 || ry <= 0 ? null : new EllipseGeom(x, y, rx, ry);
    }

    private ArcGeom? Arc(CExpr cx, CExpr cy, CExpr cr, CExpr cinner, CExpr cstart, CExpr csweep, bool open)
    {
        var x = SceneMath.F(cx.Num(_e));
        var y = SceneMath.F(cy.Num(_e));
        var r = SceneMath.F(cr.Num(_e));
        var inner = Math.Max(0, SceneMath.F(cinner.Num(_e)));
        var start = SceneMath.F(cstart.Num(_e));
        var sweep = (float)Math.Clamp(csweep.Num(_e), -360, 360);
        if (r <= 0 || (!open && inner >= r) || sweep == 0) return null;
        return new ArcGeom(x, y, r, open ? 0 : inner, start, sweep, open);
    }

    private RShape? ResolveShape(CShape s)
    {
        RGeom? geom = s switch
        {
            CRect r => RectGeom(r.X, r.Y, r.W, r.H, r.Radii),
            CEllipse e => Ellipse(e.Cx, e.Cy, e.Rx, e.Ry),
            CArc a => Arc(a.Cx, a.Cy, a.R, a.Inner, a.Start, a.Sweep, a.Open),
            CLine l => new LineGeom(SceneMath.F(l.X1.Num(_e)), SceneMath.F(l.Y1.Num(_e)), SceneMath.F(l.X2.Num(_e)), SceneMath.F(l.Y2.Num(_e))),
            CPath p => p.Path,
            CPolyline p => Poly(p),
            _ => null,
        };
        if (geom is null) return null;
        var fill = s is CLine ? null : Paint(s.Fill);
        var stroke = Stroke(s.Stroke, geom);
        return new RShape { Geom = geom, Fill = fill, Stroke = stroke };
    }

    private PolyGeom Poly(CPolyline p)
    {
        var points = new SKPoint[p.Points.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new SKPoint(SceneMath.F(p.Points[2 * i].Num(_e)), SceneMath.F(p.Points[2 * i + 1].Num(_e)));
        return new PolyGeom(points, p.Closed, p.FillType);
    }

    private RPaint? Paint(CPaint? paint, int depth = 0)
    {
        switch (paint)
        {
            case null: return null;
            case CSolid s: return new RSolid(s.Color.Col(_e));
            case CGradient g:
            {
                var geometry = new float[g.Kind == GradientKind.Sweep ? 4 : g.Geometry.Length];
                for (var i = 0; i < g.Geometry.Length; i++) geometry[i] = SceneMath.F(g.Geometry[i].Num(_e));
                if (g.Kind == GradientKind.Sweep && g.DefaultEnd) geometry[3] = geometry[2] + 360;
                var colors = new uint[g.Stops.Length];
                var positions = new float[g.Stops.Length];
                var previous = 0f;
                for (var i = 0; i < g.Stops.Length; i++)
                {
                    var at = (float)Math.Clamp(g.Stops[i].At.Num(_e), 0, 1);
                    positions[i] = Math.Max(at, previous);
                    previous = positions[i];
                    colors[i] = g.Stops[i].Color.Col(_e);
                }
                if (colors.Length == 0) return new RSolid(0);
                if (colors.Length == 1) return new RSolid(colors[0]);
                return new RGradient(g.Kind, geometry, colors, positions, g.Tile);
            }
            case CCompose c when depth < 16:
                return new RCompose(c.Blend, Paint(c.Dst, depth + 1) ?? new RSolid(0), Paint(c.Src, depth + 1) ?? new RSolid(0));
            default: return null;
        }
    }

    private RStroke? Stroke(CStroke? s, RGeom? geom)
    {
        if (s is null) return null;
        var width = SceneMath.F(s.Width.Num(_e));
        var paint = Paint(s.Paint) ?? new RSolid(SceneColor.Black);
        float[]? dash = null;
        if (s.Dash is { Length: > 0 } d)
        {
            var lengths = d.Select(x => SceneMath.F(x.Num(_e))).ToArray();
            var sum = lengths.Sum();
            if (lengths.All(x => x >= 0) && sum > 0)
            {
                dash = lengths.Length % 2 == 1 ? [.. lengths, .. lengths] : lengths;
            }
        }
        var offset = s.DashOffset is { } o ? SceneMath.F(o.Num(_e)) : 0;
        var trimStart = s.TrimStart is { } ts ? (float)Math.Clamp(ts.Num(_e), 0, 1) : 0;
        var trimEnd = s.TrimEnd is { } te ? (float)Math.Clamp(te.Num(_e), 0, 1) : 1;
        if (!(width > 0) || trimEnd <= trimStart) return null;
        var stroke = new RStroke(paint, width, s.Cap, s.Join, s.Miter, dash, offset, trimStart, trimEnd);
        if (geom is not null) CheckDash(stroke, geom.Length, geom.Contours);
        return stroke;
    }

    /// <summary>
    /// The dash budget: a dashed stroke along <paramref name="contours"/> contours of <paramref name="length"/> in all
    /// that would draw too many dashes is over budget.
    /// </summary>
    private static void CheckDash(RStroke? stroke, double length, double contours)
    {
        if (stroke?.Dash is null) return;
        var segments = stroke.DashSegments(length, contours);
        if (!(segments <= MaxDashSegments))
            throw new BudgetExceeded("budget.dash", $"A dashed stroke would draw {segments:0} dashes; at most {MaxDashSegments} are drawn.");
    }

    // ---- text --------------------------------------------------------------------------------------------------

    /// <summary>The node's text layout (and box) for this tick, made once: phase one makes it for measured text.</summary>
    private (TextLayout Layout, float? W, float? H) TextLayoutOf(CText t)
    {
        if (_textLayouts[t.TextIndex] is { } made) return made;
        var e = _e;
        var baseFamily = t.Font.Family ?? SceneFontMapper.DefaultFamily;
        var baseSize = (float)Math.Clamp(t.Font.Size?.Num(e) ?? 16, 1, 2048);
        var baseWeight = Math.Clamp(t.Font.Weight ?? 400, 1, 1000);
        var baseItalic = t.Font.Italic ?? false;

        TextRun[] runs;
        if (t.Spans is { } spans)
        {
            runs = new TextRun[spans.Length];
            var left = e.TextLength;
            for (var i = 0; i < spans.Length; i++)
            {
                var text = SceneText.Truncate(spans[i].Text.Str(e), Math.Max(0, left));
                left -= CountScalars(text);
                var f = spans[i].Font;
                runs[i] = new TextRun(text, f?.Family ?? baseFamily, f?.Size is { } sz ? (float)Math.Clamp(sz.Num(e), 1, 2048) : baseSize,
                    Math.Clamp(f?.Weight ?? baseWeight, 1, 1000), f?.Italic ?? baseItalic);
            }
        }
        else
        {
            runs = [new TextRun(e.Cut(t.Plain?.Str(e) ?? ""), baseFamily, baseSize, baseWeight, baseItalic)];
        }

        float? w = t.W is { } cw ? Math.Max(0, SceneMath.F(cw.Num(e))) : null;
        float? h = t.H is { } ch ? Math.Max(0, SceneMath.F(ch.Num(e))) : null;
        var key = new TextLayoutKey
        {
            Runs = runs,
            W = w,
            H = h,
            Wrap = t.Wrap && w is not null,
            MaxLines = t.MaxLines,
            Overflow = t.Overflow,
            LineHeight = t.LineHeight is { } lh ? Math.Max(0, SceneMath.F(lh.Num(e))) : null,
            LetterSpacing = t.LetterSpacing is { } ls ? SceneMath.F(ls.Num(e)) : 0,
            FitMin = t.FitMin is { } fm && w is not null ? (float)Math.Clamp(fm.Num(e), 1, 2048) : null,
            BaseSize = baseSize,
        };
        var layout = Layout(t.TextIndex, key);
        foreach (var family in layout.MissingFamilies) _diag.Add("font.missing", $"Font family '{family}' is not available; {SceneFontMapper.DefaultFamily} is used.");
        var result = (layout, w, h);
        _textLayouts[t.TextIndex] = result;
        return result;
    }

    private RText LayText(CText t)
    {
        if (_laidOut[t.TextIndex] is { } done) return done;
        var e = _e;
        var (layout, w, h) = TextLayoutOf(t);
        var spanCount = t.Spans?.Length ?? 1;

        var x = SceneMath.F(t.X.Num(e));
        var y = SceneMath.F(t.Y.Num(e));
        var left0 = w is null ? x - layout.Width * t.AlignFraction : x;
        var alignWidth = w ?? layout.Width;
        var g = t.Valign switch { "middle" => 0.5f, "bottom" => 1f, _ => 0f };
        float top;
        if (h is { } bh) top = t.Valign == "baseline" ? y : y + (bh - layout.Height) * g;
        else top = t.Valign == "baseline" ? y - layout.FirstBaseline : y - layout.Height * g;

        var fills = new RPaint?[spanCount];
        var nodeFill = Paint(t.Fill) ?? new RSolid(SceneColor.Black);
        for (var i = 0; i < spanCount; i++) fills[i] = t.Spans?[i].Fill is { } sf ? Paint(sf) : nodeFill;

        var r = new RText
        {
            Layout = layout,
            Left = left0,
            Top = top,
            AlignWidth = alignWidth,
            AlignFraction = t.AlignFraction,
            SpanFills = fills,
            Stroke = Stroke(t.Stroke, null),
            Underline = t.Spans?.Select(s => s.Underline ?? t.Underline).ToArray() ?? [t.Underline],
            Strike = t.Spans?.Select(s => s.Strike ?? t.Strike).ToArray() ?? [t.Strike],
        };

        // The lines' extent, and the box on each axis that has one.
        var linesLeft = left0;
        var linesRight = left0;
        foreach (var line in layout.Lines)
        {
            var lx = left0 + (alignWidth - line.Width) * t.AlignFraction;
            linesLeft = Math.Min(linesLeft, lx);
            linesRight = Math.Max(linesRight, lx + line.Width);
        }
        if (layout.Lines.Length == 0) linesRight = left0;
        var linesRect = new SKRect(linesLeft, top, linesRight, top + layout.Height);
        var boxRect = new SKRect(w is null ? linesRect.Left : x, h is null ? linesRect.Top : y,
            w is null ? linesRect.Right : x + w.Value, h is null ? linesRect.Bottom : y + h.Value);

        var marquee = t.Overflow == TextOverflow.Marquee && w is not null && layout.Width > w.Value + 0.01f;
        if (marquee)
        {
            var speed = Math.Max(1, t.MarqueeSpeed?.Num(e) ?? 30);
            var gap = Math.Max(0, t.MarqueeGap?.Num(e) ?? 40);
            var period = layout.Width + gap;
            var o = period > 0 ? (_elapsed * speed) % period : 0;
            r.Marquee = true;
            r.MarqueeOffset = (float)o;
            r.MarqueePeriod = (float)period;
            r.AlignFraction = 0;
            r.Left = x;
            r.ClipRect = boxRect;
        }
        else if (t.Overflow == TextOverflow.Clip)
        {
            r.ClipRect = boxRect;
        }

        // Text strokes have no geometry of their own: the dash budget runs on the glyph outlines (twice for a marquee).
        if (r.Stroke?.Dash is not null)
        {
            var (length, contours) = layout.GlyphOutline;
            var copies = marquee ? 2 : 1;
            CheckDash(r.Stroke, length * copies, contours * copies);
        }

        var clipped = r.ClipRect is not null;
        var outline = new SKRect(
            w is null ? linesRect.Left : clipped ? boxRect.Left : Math.Min(boxRect.Left, linesRect.Left),
            h is null ? linesRect.Top : clipped ? boxRect.Top : Math.Min(boxRect.Top, linesRect.Top),
            w is null ? linesRect.Right : clipped ? boxRect.Right : Math.Max(boxRect.Right, linesRect.Right),
            h is null ? linesRect.Bottom : clipped ? boxRect.Bottom : Math.Max(boxRect.Bottom, linesRect.Bottom));
        if (r.Stroke is { } st && !clipped) outline.Inflate(st.Width / 2, st.Width / 2);
        r.Outline = outline;
        _laidOut[t.TextIndex] = r;
        return r;
    }

    private static int CountScalars(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    private TextLayout Layout(int textIndex, TextLayoutKey key)
    {
        if (_rt.Texts.TryGetValue(textIndex, out var cached) && cached.Key.Equals(key)) return cached.Layout;
        var layout = NewLayout(key);
        _rt.Texts[textIndex] = (key, layout);
        return layout;
    }

    private TextLayout NewLayout(TextLayoutKey key)
    {
        // What is left of the document's and the item's budgets bounds the layout as it is made (every paragraph and
        // fit probe), not only before it starts.
        var left = Math.Min(_services.LayoutsLeft, MaxItemLayouts - _layouts);
        if (left <= 0)
            throw new BudgetExceeded("budget.text", "Too many texts changed this tick; the item is drawn again on a later tick.");
        TextLayout layout;
        try
        {
            layout = TextLayoutEngine.Layout(key, left);
        }
        catch (BudgetExceeded)
        {
            _layouts += left;
            _services.LayoutsLeft -= left;
            throw;
        }
        _layouts += layout.Cost;
        _services.LayoutsLeft -= layout.Cost;
        return layout;
    }

    // ---- series ------------------------------------------------------------------------------------------------

    private RGroup? ResolveSeries(CSeries s)
    {
        var e = _e;
        var x = SceneMath.F(s.X.Num(e));
        var y = SceneMath.F(s.Y.Num(e));
        var w = SceneMath.F(s.W.Num(e));
        var h = SceneMath.F(s.H.Num(e));
        var min = s.Min.Num(e);
        var max = s.Max.Num(e);
        var pitch = Math.Max(0.25, s.Pitch?.Num(e) ?? 1);
        var barWidth = SceneMath.F(s.BarWidth?.Num(e) ?? pitch);
        var fill = Paint(s.Fill);
        if (w <= 0 || h <= 0) return null;
        var box = new SKRect(x, y, x + w, y + h);

        var samples = s.Input < e.Inputs.Length ? e.Inputs[s.Input].History : [];
        var fitting = (int)Math.Min(4096, Math.Floor(w / pitch) + 1);
        var count = Math.Min(Math.Min(s.Count ?? fitting, 4096), samples.Count);
        var bottom = y + h;
        var points = new SKPoint[count];
        for (var j = 0; j < count; j++)
        {
            // j = 0 is the oldest drawn sample; the newest is at the right edge.
            var v = SceneMath.Finite(samples[samples.Count - count + j]);
            var age = count - 1 - j;
            var px = x + w - age * pitch;
            var f = max <= min ? 0 : Math.Clamp((v - min) / (max - min), 0, 1);
            points[j] = new SKPoint((float)px, (float)(bottom - f * h));
        }

        var children = new List<RNode>();
        var line = new PolyGeom(points, false, SKPathFillType.Winding);
        var stroke = Stroke(s.Stroke, line);
        switch (s.Mode)
        {
            case SeriesMode.Line:
                if (count >= 2) children.Add(new RShape { Geom = line, Stroke = stroke });
                break;
            case SeriesMode.Area:
                if (count >= 2)
                {
                    var area = new SKPoint[count + 2];
                    Array.Copy(points, area, count);
                    area[count] = new SKPoint(points[^1].X, bottom);
                    area[count + 1] = new SKPoint(points[0].X, bottom);
                    children.Add(new RShape { Geom = new PolyGeom(area, true, SKPathFillType.Winding), Fill = fill });
                    children.Add(new RShape { Geom = line, Stroke = stroke });
                }
                break;
            case SeriesMode.Bars:
                foreach (var p in points)
                {
                    var bar = new RectGeom(p.X - barWidth, p.Y, barWidth, bottom - p.Y, null);
                    children.Add(new RShape { Geom = bar, Fill = fill, Stroke = Stroke(s.Stroke, bar) });
                }
                break;
        }
        return new RGroup { Clip = new RectGeom(x, y, w, h, null), Children = children.ToArray(), BoundsOverride = box };
    }

    // ---- table -------------------------------------------------------------------------------------------------

    private RGroup? ResolveTable(CTable t)
    {
        var e = _e;
        var snap = t.Input < e.Inputs.Length ? e.Inputs[t.Input] : InputSnap.Unbound;
        var x = SceneMath.F(t.X.Num(e));
        var y = SceneMath.F(t.Y.Num(e));
        var gap = SceneMath.F(t.Gap.Num(e));
        var widths = t.Columns.Select(c => Math.Max(0, SceneMath.F(c.W.Num(e)))).ToArray();
        var titles = t.Columns.Select(c => c.Title?.Str(e)).ToArray();
        if (!snap.Available) return null;
        var table = snap.Table;

        var family = t.Font.Family ?? SceneFontMapper.DefaultFamily;
        var size = (float)Math.Clamp(t.Font.Size?.Num(e) ?? 16, 1, 2048);
        var weight = Math.Clamp(t.Font.Weight ?? 400, 1, 1000);
        var italic = t.Font.Italic ?? false;
        var hf = t.HeaderFont;
        var hFamily = hf?.Family ?? family;
        var hSize = hf?.Size is { } hs ? (float)Math.Clamp(hs.Num(e), 1, 2048) : size;
        var hWeight = Math.Clamp(hf?.Weight ?? weight, 1, 1000);
        var hItalic = hf?.Italic ?? italic;

        var fill = Paint(t.Fill) ?? new RSolid(SceneColor.Black);
        var headerFill = Paint(t.HeaderFill) ?? fill;
        var stroke = Stroke(t.Stroke, null);

        var columnIndex = t.Columns.Select(c =>
        {
            if (c.Index is { } i) return i >= 0 && i < table.Columns.Count ? i : -1;
            for (var k = 0; k < table.Columns.Count; k++)
                if (string.Equals(table.Columns[k], c.Name, StringComparison.Ordinal)) return k;
            return -1;
        }).ToArray();

        float rowHeight;
        if (t.RowHeight is { } rh) rowHeight = Math.Max(0, SceneMath.F(rh.Num(e)));
        else
        {
            var probe = Cell(t, -1, 0, new TextRun("", family, size, weight, italic), null, TextOverflow.Visible);
            rowHeight = probe.Lines.Length > 0 ? probe.Lines[0].Height : size * 1.2f;
        }

        var cells = new List<RNode>();
        var row = 0;
        if (t.Header)
        {
            for (var c = 0; c < t.Columns.Length; c++)
            {
                var title = titles[c] ?? (columnIndex[c] >= 0 ? table.Columns[columnIndex[c]] : t.Columns[c].Name ?? "");
                if (columnIndex[c] < 0 && titles[c] is null) continue;
                cells.Add(CellNode(t, row, c, new TextRun(e.Cut(title), hFamily, hSize, hWeight, hItalic), headerFill, stroke, x, y, widths, gap, rowHeight));
            }
            row++;
        }
        var rows = Math.Min(t.Rows, table.Rows.Count);
        for (var r = 0; r < rows; r++, row++)
        {
            var data = table.Rows[r];
            for (var c = 0; c < t.Columns.Length; c++)
            {
                var k = columnIndex[c];
                if (k < 0 || k >= data.Count) continue;
                cells.Add(CellNode(t, row, c, new TextRun(e.Cut(data[k] ?? ""), family, size, weight, italic), fill, stroke, x, y, widths, gap, rowHeight));
            }
        }
        if (stroke?.Dash is not null)
        {
            double length = 0, contours = 0;
            foreach (var cell in cells)
            {
                var outline = ((RText)cell).Layout.GlyphOutline;
                length += outline.Length;
                contours += outline.Contours;
            }
            CheckDash(stroke, length, contours);
        }
        var totalWidth = widths.Sum() + gap * Math.Max(0, widths.Length - 1);
        var bounds = new SKRect(x, y, x + Math.Max(0, totalWidth), y + row * rowHeight);
        return new RGroup { Children = cells.ToArray(), BoundsOverride = bounds };
    }

    private RText CellNode(CTable t, int row, int column, TextRun run, RPaint fill, RStroke? stroke, float x, float y, float[] widths, float gap, float rowHeight)
    {
        var w = widths[column];
        var left = x;
        for (var k = 0; k < column; k++) left += widths[k] + gap;
        var layout = Cell(t, row, column, run, w, t.Overflow);
        var top = y + row * rowHeight + (rowHeight - layout.Height) / 2;
        var cellRect = new SKRect(left, y + row * rowHeight, left + w, y + (row + 1) * rowHeight);
        return new RText
        {
            Layout = layout,
            Left = left,
            Top = top,
            AlignWidth = w,
            AlignFraction = t.Columns[column].AlignFraction,
            SpanFills = [fill],
            Stroke = stroke,
            Underline = [false],
            Strike = [false],
            ClipRect = t.Overflow == TextOverflow.Clip ? cellRect : null,
            Outline = cellRect,
        };
    }

    private TextLayout Cell(CTable t, int row, int column, TextRun run, float? w, TextOverflow overflow)
    {
        var key = new TextLayoutKey { Runs = [run], W = w, MaxLines = 1, Overflow = overflow, BaseSize = run.Size };
        var cacheKey = (t.TableIndex, row, column);
        if (_rt.TableCells.TryGetValue(cacheKey, out var cached) && cached.Key.Equals(key)) return cached.Layout;
        var layout = NewLayout(key);
        _rt.TableCells[cacheKey] = (key, layout);
        return layout;
    }

    // ---- media -------------------------------------------------------------------------------------------------

    private RMedia ResolveMedia(CMedia m)
    {
        var e = _e;
        var x = SceneMath.F(m.X.Num(e));
        var y = SceneMath.F(m.Y.Num(e));
        float? w = m.W is { } cw ? Math.Max(0, SceneMath.F(cw.Num(e))) : null;
        float? h = m.H is { } ch ? Math.Max(0, SceneMath.F(ch.Num(e))) : null;
        var speed = m.Speed is { } sp ? Math.Clamp(sp.Num(e), 0, 16) : 1;
        var seconds = _elapsed * speed;

        MediaSample? sample = null, over = null;
        float fade = 0;
        var src = m.Source;
        if (src.Sequence is { Length: > 0 } seq)
        {
            var index = Math.Clamp(src.Index?.Num(e) ?? 0, 0, seq.Length - 1);
            if (src.Crossfade)
            {
                var lo = (int)Math.Floor(index);
                var hi = (int)Math.Ceiling(index);
                sample = Fetch(seq[lo], seconds, m.Playback);
                fade = (float)(index - lo);
                if (hi != lo && fade > 0) over = Fetch(seq[hi], seconds, m.Playback);
            }
            else
            {
                sample = Fetch(seq[(int)Math.Round(index, MidpointRounding.AwayFromZero)], seconds, m.Playback);
            }
        }
        else
        {
            sample = Fetch(src, seconds, m.Playback);
        }
        if (sample is null && over is not null)
        {
            sample = over;
            over = null;
            fade = 0;
        }

        float nw = sample?.Width ?? 0, nh = sample?.Height ?? 0;
        float bw, bh;
        if (sample is null || nw <= 0 || nh <= 0)
        {
            bw = w ?? 100;
            bh = h ?? 100;
        }
        else
        {
            bw = w ?? (h is { } hh ? hh * nw / nh : nw);
            bh = h ?? (w is { } ww ? ww * nh / nw : nh);
        }
        var box = new SKRect(x, y, x + bw, y + bh);
        var dest = box;
        var clip = false;
        if (sample is not null && nw > 0 && nh > 0)
        {
            switch (m.Fit)
            {
                case "contain":
                case "cover":
                {
                    var s = m.Fit == "contain" ? Math.Min(bw / nw, bh / nh) : Math.Max(bw / nw, bh / nh);
                    dest = Aligned(box, nw * s, nh * s, m.AlignX, m.AlignY);
                    clip = m.Fit == "cover";
                    break;
                }
                case "none":
                    dest = Aligned(box, nw, nh, m.AlignX, m.AlignY);
                    clip = true;
                    break;
            }
        }
        return new RMedia { Sample = sample, Over = over, FadeOpacity = fade, Dest = dest, Box = box, ClipToBox = clip, Nearest = m.Nearest };
    }

    private static SKRect Aligned(SKRect box, float w, float h, float ax, float ay)
    {
        var left = box.Left + (box.Width - w) * ax;
        var top = box.Top + (box.Height - h) * ay;
        return new SKRect(left, top, left + w, top + h);
    }

    private MediaSample? Fetch(CMediaSource src, double seconds, MediaPlayback playback)
    {
        if (src.Asset is { } hash)
        {
            if (_services.Media is not { } media)
            {
                _diag.Add("media.missing", $"No media store: {hash} is drawn as missing.");
                return null;
            }
            if (!_services.Assets.TryGetValue((hash, seconds, playback), out var lookup))
            {
                lookup = media.Get(hash, seconds, playback);
                _services.Assets[(hash, seconds, playback)] = lookup;
                if (lookup.Sample is { } retained) _services.Retained.Add(retained);
            }
            if (lookup.Sample is { } sample) return sample;
            if (lookup.State == MediaState.Failed) _diag.Add("media.missing", $"{hash}: {lookup.Problem}");
            else _diag.Add("media.loading", $"{hash} is loading.");
            return null;
        }
        if (src.Provider is { } name)
        {
            var reference = src.Ref?.Str(_e) ?? "";
            if (!_services.Providers.TryGetValue(name, out var provider))
            {
                _diag.Add("media.provider", $"No media provider '{name}' is configured.");
                return null;
            }
            if (!_services.Provided.TryGetValue((name, reference, seconds), out var provided))
            {
                try
                {
                    provided = (provider.TryGet(reference, seconds), null);
                }
                catch (Exception ex)
                {
                    provided = (null, ex.Message);
                }
                _services.Provided[(name, reference, seconds)] = provided;
                if (provided.Sample is { } retained) _services.Retained.Add(retained);
            }
            var sample = provided.Sample;
            if (provided.Error is { } error) _diag.Add("media.provider", $"Provider '{name}' failed: {error}");
            if (sample is null)
            {
                _diag.Add("media.loading", $"Provider '{name}' has nothing for this reference yet.");
                return null;
            }
            return sample;
        }
        return null;
    }
}
