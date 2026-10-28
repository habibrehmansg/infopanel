using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InfoPanel.Spec;
using SkiaSharp;
using SpecScene = InfoPanel.Spec.Scene;

namespace InfoPanel.Scenes;

/// <summary>A scene that passed validation but can't be compiled (the item is drawn as an invalid placeholder).</summary>
internal sealed class SceneCompileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// A scene compiled once for the runtime: names resolved to indexes, types inferred, values ordered for the tick's phases,
// static paths parsed. Shared by every item that draws the same scene and by every tick; never changed after compiling.

internal abstract class CNode
{
    public CExpr? Visible;
    public CExpr? Opacity;
}

internal sealed class CGroup : CNode
{
    public required CTransform[] Transform;
    public CClip? Clip;
    public SKBlendMode Blend = SKBlendMode.SrcOver;
    public required CEffect[] Effects;
    public required CNode[] Children;
}

internal abstract class CTransform;
internal sealed class CTranslate(CExpr x, CExpr y) : CTransform { public CExpr X = x, Y = y; }
internal sealed class CRotate(CExpr angle, CExpr cx, CExpr cy) : CTransform { public CExpr Angle = angle, Cx = cx, Cy = cy; }
internal sealed class CScale(CExpr x, CExpr y, CExpr cx, CExpr cy) : CTransform { public CExpr X = x, Y = y, Cx = cx, Cy = cy; }
internal sealed class CSkew(CExpr x, CExpr y) : CTransform { public CExpr X = x, Y = y; }
internal sealed class CMatrix(SKMatrix m) : CTransform { public SKMatrix M = m; }

internal abstract class CClip;
internal sealed class CRectClip : CClip { public required CExpr X, Y, W, H; public CExpr[]? Radii; }
internal sealed class CEllipseClip : CClip { public required CExpr Cx, Cy, Rx, Ry; }
internal sealed class CArcClip : CClip { public required CExpr Cx, Cy, R, Inner, Start, Sweep; }
internal sealed class CPathClip(StaticPathGeom path) : CClip { public StaticPathGeom Path = path; }

internal sealed class CEffect
{
    public EffectKind Kind;
    public required CExpr Sigma;
    public CExpr? Dx, Dy, Color;
}

internal abstract class CPaint;
internal sealed class CSolid(CExpr color) : CPaint { public CExpr Color = color; }
internal sealed class CStop(CExpr at, CExpr color) { public CExpr At = at, Color = color; }

internal sealed class CGradient : CPaint
{
    public GradientKind Kind;
    public required CExpr[] Geometry;
    public required CStop[] Stops;
    public SKShaderTileMode Tile;
    /// <summary>For a sweep: whether <c>end</c> is absent (start + 360).</summary>
    public bool DefaultEnd;
}

internal sealed class CCompose(SKBlendMode blend, CPaint dst, CPaint src) : CPaint { public SKBlendMode Blend = blend; public CPaint Dst = dst, Src = src; }

internal sealed class CStroke
{
    public required CPaint Paint;
    public required CExpr Width;
    public SKStrokeCap Cap;
    public SKStrokeJoin Join;
    public float Miter = 4;
    public CExpr[]? Dash;
    public CExpr? DashOffset, TrimStart, TrimEnd;
}

internal abstract class CShape : CNode
{
    public CPaint? Fill;
    public CStroke? Stroke;
}

internal sealed class CRect : CShape { public required CExpr X, Y, W, H; public CExpr[]? Radii; }
internal sealed class CEllipse : CShape { public required CExpr Cx, Cy, Rx, Ry; }
internal sealed class CArc : CShape { public required CExpr Cx, Cy, R, Inner, Start, Sweep; public bool Open; }
internal sealed class CLine : CShape { public required CExpr X1, Y1, X2, Y2; }
internal sealed class CPath(StaticPathGeom path) : CShape { public StaticPathGeom Path = path; }
internal sealed class CPolyline : CShape { public required CExpr[] Points; public bool Closed; public SKPathFillType FillType; }

internal sealed class CFont
{
    public required string? Family;
    public CExpr? Size;
    public int? Weight;
    public bool? Italic;
}

internal sealed class CSpan
{
    public required CExpr Text;
    public CFont? Font;
    public CPaint? Fill;
    public bool? Underline, Strike;
}

internal sealed class CText : CNode
{
    /// <summary>Index among the scene's text nodes (layout caches and <c>measure</c>).</summary>
    public int TextIndex;
    public bool Measured;
    public required CExpr X, Y;
    public CExpr? W, H;
    public CExpr? Plain;
    public CSpan[]? Spans;
    public required CFont Font;
    public CPaint? Fill;
    public CStroke? Stroke;
    public float AlignFraction;
    public string Valign = "top";
    public bool Wrap;
    public int? MaxLines;
    public TextOverflow Overflow;
    public CExpr? MarqueeSpeed, MarqueeGap, FitMin, LineHeight, LetterSpacing;
    public bool Underline, Strike;
}

internal enum SeriesMode : byte { Line, Area, Bars }

internal sealed class CSeries : CNode
{
    public int Input;
    public required CExpr X, Y, W, H, Min, Max;
    public SeriesMode Mode;
    public CExpr? Pitch, BarWidth;
    public int? Count;
    public CPaint? Fill;
    public CStroke? Stroke;
}

internal sealed class CColumn
{
    public int? Index;
    public string? Name;
    public required CExpr W;
    public float AlignFraction;
    public CExpr? Title;
}

internal sealed class CTable : CNode
{
    public int TableIndex;
    public int Input;
    public required CExpr X, Y;
    public required CColumn[] Columns;
    public int Rows = 10;
    public bool Header = true;
    public CExpr? RowHeight;
    public required CExpr Gap;
    public required CFont Font;
    public CFont? HeaderFont;
    public CPaint? Fill, HeaderFill;
    public CStroke? Stroke;
    public TextOverflow Overflow = TextOverflow.Ellipsis;
}

internal sealed class CMediaSource
{
    public string? Asset;
    public string? Provider;
    public CExpr? Ref;
    public CMediaSource[]? Sequence;
    public CExpr? Index;
    public bool Crossfade;
}

internal sealed class CMedia : CNode
{
    public CExpr X = ExprCompiler.Zero, Y = ExprCompiler.Zero;
    public CExpr? W, H;
    public required CMediaSource Source;
    public string Fit = "fill";
    public float AlignX = 0.5f, AlignY = 0.5f;
    public Images.MediaPlayback Playback = Images.MediaPlayback.Loop;
    public CExpr? Speed;
    public bool Nearest;
}

internal sealed record CCell(string Name, string DefinitionKey, CExpr Init, CExpr Next);

/// <summary>A compiled scene.</summary>
internal sealed class CompiledScene
{
    public required SpecScene Source;
    public required string[] InputNames;
    public required string[] InputTypes;
    public required int ValueCount;
    public required CExpr[] Values;

    /// <summary>Values in evaluation order for each phase: not reading state or measurements; reading state; reading measurements.</summary>
    public required int[] PhaseBeforeState, PhaseAfterState, PhaseAfterLayout;

    public required CCell[] Cells;
    public required CNode[] Nodes;
    public required CText[] TextNodes;
    public required int TableCount;
    public required int NodeCount;
    public required SceneCost Cost;
    public bool HasMeasure => TextNodes.Any(t => t.Measured);

    public static CompiledScene Compile(SpecScene scene, SceneCost cost, SpecLimits limits) => new SceneCompiler(scene, limits).Run(cost);
}

internal sealed class SceneCompiler(SpecScene scene, SpecLimits limits)
{
    private readonly List<CText> _texts = [];
    private int _tables;
    private int _nodes;
    private ExprCompiler _x = null!;
    private ExprScope _scope = null!;

    public CompiledScene Run(SceneCost cost)
    {
        var inputs = scene.Inputs ?? new Dictionary<string, InputDecl>();
        var inputNames = inputs.Keys.ToArray();
        var valueDefs = scene.Values ?? new Dictionary<string, Expr>();
        var valueNames = valueDefs.Keys.ToArray();
        var cellDefs = scene.State ?? new Dictionary<string, StateCell>();
        var cellNames = cellDefs.Keys.ToArray();

        var textIds = new Dictionary<string, int>(StringComparer.Ordinal);
        CollectTextIds(scene.Nodes, textIds, 0);

        _scope = new ExprScope
        {
            Inputs = inputNames.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i, StringComparer.Ordinal),
            InputTypes = inputNames.Select(n => inputs[n].Type).ToArray(),
            Values = valueNames.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i, StringComparer.Ordinal),
            ValueTypes = Enumerable.Repeat(Ty.Any, valueNames.Length).ToArray(),
            Cells = cellNames.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i, StringComparer.Ordinal),
            CellTypes = Enumerable.Repeat(Ty.Any, cellNames.Length).ToArray(),
            TextNodes = textIds,
            TextLength = limits.TextLength,
        };
        _x = new ExprCompiler(_scope);

        // Value dependencies, without recursion: direct references, then a topological order (Kahn). Values in a
        // cycle (refused by validation) never evaluate and read as 0.
        var n = valueNames.Length;
        var deps = new List<int>[n];
        var readsState = new bool[n];
        var readsMeasure = new bool[n];
        for (var i = 0; i < n; i++)
        {
            deps[i] = [];
            foreach (var r in ExprWalk.Refs(valueDefs[valueNames[i]]))
            {
                switch (r)
                {
                    case ValueRefExpr v when _scope.Values.TryGetValue(v.Name, out var j): deps[i].Add(j); break;
                    case StateRefExpr: readsState[i] = true; break;
                    case MeasureExpr: readsMeasure[i] = true; break;
                }
            }
        }
        var order = TopologicalOrder(deps);
        foreach (var i in order)
            foreach (var j in deps[i])
            {
                readsState[i] |= readsState[j];
                readsMeasure[i] |= readsMeasure[j];
            }

        var values = Enumerable.Repeat(ExprCompiler.Zero, n).ToArray();
        foreach (var i in order.Where(i => !readsState[i]))
        {
            values[i] = _x.Compile(valueDefs[valueNames[i]]);
            _scope.ValueTypes[i] = values[i].Type;
        }

        // State cells: a cell's type is resolved from init and next together (iterated to a fixed point, as next may
        // read other cells).
        var cells = new CCell[cellNames.Length];
        var inits = cellNames.Select(c => _x.Compile(cellDefs[c].Init)).ToArray();
        for (var i = 0; i < inits.Length; i++) _scope.CellTypes[i] = inits[i].Type;
        for (var round = 0; round <= cellNames.Length + 1; round++)
        {
            var changed = false;
            for (var i = 0; i < cellNames.Length; i++)
            {
                var next = _x.Compile(cellDefs[cellNames[i]].Next);
                var t = _scope.CellTypes[i] & next.Type;
                if (t != 0 && t != _scope.CellTypes[i])
                {
                    _scope.CellTypes[i] = t;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        for (var i = 0; i < cellNames.Length; i++)
        {
            var def = cellDefs[cellNames[i]];
            // Every number literal is finite: the model reads none that rounds to ±∞ (SpecJson refuses it).
            var key = CanonicalJson.Write(SpecJson.Write(def));
            cells[i] = new CCell(cellNames[i], key, _x.Compile(def.Init), _x.Compile(def.Next));
        }

        foreach (var i in order.Where(i => readsState[i]))
        {
            values[i] = _x.Compile(valueDefs[valueNames[i]]);
            _scope.ValueTypes[i] = values[i].Type;
        }

        var nodes = CompileNodes(scene.Nodes, 0);
        foreach (var t in _texts) t.Measured = _scope.Measured.Contains(t.TextIndex);

        return new CompiledScene
        {
            Source = scene,
            InputNames = inputNames,
            InputTypes = _scope.InputTypes.ToArray(),
            ValueCount = n,
            Values = values,
            PhaseBeforeState = order.Where(i => !readsState[i] && !readsMeasure[i]).ToArray(),
            PhaseAfterState = order.Where(i => readsState[i] && !readsMeasure[i]).ToArray(),
            PhaseAfterLayout = order.Where(i => readsMeasure[i]).ToArray(),
            Cells = cells,
            Nodes = nodes,
            TextNodes = _texts.ToArray(),
            TableCount = _tables,
            NodeCount = _nodes,
            Cost = cost,
        };
    }

    private static int[] TopologicalOrder(List<int>[] deps)
    {
        var n = deps.Length;
        var pending = new int[n];
        var dependents = new List<int>[n];
        for (var i = 0; i < n; i++) dependents[i] = [];
        for (var i = 0; i < n; i++)
            foreach (var j in deps[i].Distinct())
            {
                pending[i]++;
                dependents[j].Add(i);
            }
        var ready = new Queue<int>(Enumerable.Range(0, n).Where(i => pending[i] == 0));
        var order = new List<int>(n);
        while (ready.Count > 0)
        {
            var i = ready.Dequeue();
            order.Add(i);
            foreach (var d in dependents[i])
                if (--pending[d] == 0) ready.Enqueue(d);
        }
        return order.ToArray();
    }

    private void CollectTextIds(IReadOnlyList<SceneNode> nodes, Dictionary<string, int> ids, int depth)
    {
        // Pre-order, as CompileNodes numbers text nodes.
        var stack = new Stack<(IReadOnlyList<SceneNode> List, int Index)>();
        stack.Push((nodes, 0));
        var count = 0;
        while (stack.Count > 0)
        {
            var (list, index) = stack.Pop();
            if (index >= list.Count) continue;
            stack.Push((list, index + 1));
            var node = list[index];
            if (node is TextNode t)
            {
                if (t.Id is { } id) ids.TryAdd(id, count);
                count++;
            }
            else if (node is GroupNode g && stack.Count < 64) stack.Push((g.Children, 0));
        }
    }

    private CNode[] CompileNodes(IReadOnlyList<SceneNode> nodes, int depth)
    {
        var result = new List<CNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (depth >= 64) break; // validation allows 24
            _nodes++;
            var c = CompileNode(node, depth);
            c.Visible = node.Visible is null ? null : _x.Compile(node.Visible);
            c.Opacity = node.Opacity is null ? null : _x.Compile(node.Opacity);
            result.Add(c);
        }
        return result.ToArray();
    }

    private CExpr N(Expr? e, double fallback = 0) => _x.Number(e, fallback);

    private CNode CompileNode(SceneNode node, int depth)
    {
        switch (node)
        {
            case GroupNode g:
                return new CGroup
                {
                    Transform = (g.Transform ?? []).Select(CompileTransform).ToArray(),
                    Clip = g.Clip is null ? null : CompileClip(g.Clip),
                    Blend = Blend(g.Blend),
                    Effects = (g.Effects ?? []).Select(CompileEffect).ToArray(),
                    Children = CompileNodes(g.Children, depth + 1),
                };
            case RectNode r:
                return new CRect { X = N(r.X), Y = N(r.Y), W = N(r.W), H = N(r.H), Radii = Radii(r.R), Fill = Paint(r.Fill), Stroke = Stroke(r.Stroke) };
            case EllipseNode e:
                return new CEllipse { Cx = N(e.Cx), Cy = N(e.Cy), Rx = N(e.Rx), Ry = N(e.Ry), Fill = Paint(e.Fill), Stroke = Stroke(e.Stroke) };
            case ArcNode a:
                return new CArc
                {
                    Cx = N(a.Cx), Cy = N(a.Cy), R = N(a.R), Inner = N(a.Inner), Start = N(a.Start), Sweep = N(a.Sweep),
                    Open = a.Close == "open", Fill = Paint(a.Fill), Stroke = Stroke(a.Stroke),
                };
            case LineNode l:
                return new CLine { X1 = N(l.X1), Y1 = N(l.Y1), X2 = N(l.X2), Y2 = N(l.Y2), Stroke = Stroke(l.Stroke) };
            case PathNode p:
                return new CPath(StaticPathGeom.Parse(p.D, FillType(p.FillRule))) { Fill = Paint(p.Fill), Stroke = Stroke(p.Stroke) };
            case PolylineNode p:
            {
                var points = new CExpr[p.Points.Count * 2];
                for (var i = 0; i < p.Points.Count; i++)
                {
                    points[2 * i] = p.Points[i].Count > 0 ? _x.Compile(p.Points[i][0]) : ExprCompiler.Zero;
                    points[2 * i + 1] = p.Points[i].Count > 1 ? _x.Compile(p.Points[i][1]) : ExprCompiler.Zero;
                }
                return new CPolyline { Points = points, Closed = p.Closed == true, FillType = FillType(p.FillRule), Fill = Paint(p.Fill), Stroke = Stroke(p.Stroke) };
            }
            case TextNode t:
            {
                var c = new CText
                {
                    TextIndex = _texts.Count,
                    X = N(t.X), Y = N(t.Y),
                    W = t.W is null ? null : _x.Compile(t.W),
                    H = t.H is null ? null : _x.Compile(t.H),
                    Plain = t.Text.Plain is null ? null : _x.Compile(t.Text.Plain),
                    Spans = t.Text.Spans?.Select(s => new CSpan
                    {
                        Text = _x.Compile(s.Text),
                        Font = s.Font is null ? null : new CFont { Family = s.Font.Family, Size = s.Font.Size is null ? null : _x.Compile(s.Font.Size), Weight = s.Font.Weight, Italic = s.Font.Italic },
                        Fill = Paint(s.Fill),
                        Underline = s.Underline,
                        Strike = s.Strike,
                    }).ToArray(),
                    Font = Font(t.Font),
                    Fill = Paint(t.Fill),
                    Stroke = Stroke(t.Stroke),
                    AlignFraction = Align(t.Align),
                    Valign = t.Valign ?? "top",
                    Wrap = t.Wrap == "word",
                    MaxLines = t.MaxLines,
                    Overflow = Overflow(t.Overflow, TextOverflow.Visible),
                    MarqueeSpeed = t.Marquee?.Speed is null ? null : _x.Compile(t.Marquee.Speed),
                    MarqueeGap = t.Marquee?.Gap is null ? null : _x.Compile(t.Marquee.Gap),
                    FitMin = t.Fit is null ? null : _x.Compile(t.Fit.Min),
                    LineHeight = t.LineHeight is null ? null : _x.Compile(t.LineHeight),
                    LetterSpacing = t.LetterSpacing is null ? null : _x.Compile(t.LetterSpacing),
                    Underline = t.Underline == true,
                    Strike = t.Strike == true,
                };
                _texts.Add(c);
                return c;
            }
            case SeriesNode s:
                return new CSeries
                {
                    Input = _scope.Inputs.TryGetValue(s.Input, out var si) ? si : int.MaxValue,
                    X = N(s.X), Y = N(s.Y), W = N(s.W), H = N(s.H), Min = N(s.Min), Max = N(s.Max),
                    Mode = s.Mode switch { "area" => SeriesMode.Area, "bars" => SeriesMode.Bars, _ => SeriesMode.Line },
                    Pitch = s.Pitch is null ? null : _x.Compile(s.Pitch),
                    BarWidth = s.BarWidth is null ? null : _x.Compile(s.BarWidth),
                    Count = s.Count,
                    Fill = Paint(s.Fill),
                    Stroke = Stroke(s.Stroke),
                };
            case TableNode tb:
                return new CTable
                {
                    TableIndex = _tables++,
                    Input = _scope.Inputs.TryGetValue(tb.Input, out var ti) ? ti : int.MaxValue,
                    X = N(tb.X), Y = N(tb.Y),
                    Columns = tb.Columns.Select(c => new CColumn
                    {
                        Index = c.Column.Index, Name = c.Column.Name, W = N(c.W), AlignFraction = Align(c.Align),
                        Title = c.Title is null ? null : _x.Compile(c.Title),
                    }).ToArray(),
                    Rows = Math.Clamp(tb.Rows ?? 10, 1, 100),
                    Header = tb.Header ?? true,
                    RowHeight = tb.RowHeight is null ? null : _x.Compile(tb.RowHeight),
                    Gap = N(tb.Gap),
                    Font = Font(tb.Font),
                    HeaderFont = tb.HeaderFont is null ? null : new CFont { Family = tb.HeaderFont.Family, Size = tb.HeaderFont.Size is null ? null : _x.Compile(tb.HeaderFont.Size), Weight = tb.HeaderFont.Weight, Italic = tb.HeaderFont.Italic },
                    Fill = Paint(tb.Fill),
                    HeaderFill = Paint(tb.HeaderFill),
                    Stroke = Stroke(tb.Stroke),
                    Overflow = Overflow(tb.Overflow, TextOverflow.Ellipsis),
                };
            case MediaNode m:
                return new CMedia
                {
                    X = N(m.X), Y = N(m.Y),
                    W = m.W is null ? null : _x.Compile(m.W),
                    H = m.H is null ? null : _x.Compile(m.H),
                    Source = Source(m.Source, 0),
                    Fit = m.Fit ?? "fill",
                    AlignX = (float)Math.Clamp(m.Align is { Count: > 0 } ax ? ax[0] : 0.5, 0, 1),
                    AlignY = (float)Math.Clamp(m.Align is { Count: > 1 } ay ? ay[1] : 0.5, 0, 1),
                    Playback = m.Playback switch { "once" => Images.MediaPlayback.Once, "still" => Images.MediaPlayback.Still, _ => Images.MediaPlayback.Loop },
                    Speed = m.Speed is null ? null : _x.Compile(m.Speed),
                    Nearest = m.Sampling == "nearest",
                };
            default:
                return new CGroup { Transform = [], Effects = [], Children = [] };
        }
    }

    private CMediaSource Source(MediaSource s, int depth) => new()
    {
        Asset = s.Asset,
        Provider = s.Provider,
        Ref = s.Ref is null ? null : _x.Compile(s.Ref),
        Sequence = depth > 0 ? null : s.Sequence?.Take(limits.Sequence).Select(x => Source(x, depth + 1)).ToArray(),
        Index = s.Index is null ? null : _x.Compile(s.Index),
        Crossfade = s.Crossfade == true,
    };

    private CFont Font(Font f) => new() { Family = f.Family, Size = _x.Compile(f.Size), Weight = f.Weight, Italic = f.Italic };

    private static float Align(string? a) => a switch { "center" => 0.5f, "end" => 1, _ => 0 };

    private static TextOverflow Overflow(string? o, TextOverflow fallback) => o switch
    {
        "visible" => TextOverflow.Visible,
        "clip" => TextOverflow.Clip,
        "ellipsis" => TextOverflow.Ellipsis,
        "marquee" => TextOverflow.Marquee,
        _ => fallback,
    };

    private static SKPathFillType FillType(string? rule) => rule == "evenodd" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

    private CExpr[]? Radii(CornerRadii? r)
    {
        if (r is null) return null;
        if (r.All is { } all) return [_x.Compile(all)];
        return r.Corners?.Take(4).Select(c => _x.Compile(c)).ToArray();
    }

    private CTransform CompileTransform(TransformStep step) => step switch
    {
        TranslateStep t => new CTranslate(N(t.X), N(t.Y)),
        RotateStep r => new CRotate(N(r.Angle), N(r.Cx), N(r.Cy)),
        ScaleStep s => new CScale(N(s.X, 1), N(s.Y, 1), N(s.Cx), N(s.Cy)),
        SkewStep k => new CSkew(N(k.X), N(k.Y)),
        MatrixStep m when m.M.Count == 6 => new CMatrix(new SKMatrix(
            SceneMath.F(m.M[0]), SceneMath.F(m.M[2]), SceneMath.F(m.M[4]),
            SceneMath.F(m.M[1]), SceneMath.F(m.M[3]), SceneMath.F(m.M[5]), 0, 0, 1)),
        _ => new CMatrix(SKMatrix.Identity),
    };

    private CClip CompileClip(ClipShape clip) => clip switch
    {
        RectClip r => new CRectClip { X = N(r.X), Y = N(r.Y), W = N(r.W), H = N(r.H), Radii = Radii(r.R) },
        EllipseClip e => new CEllipseClip { Cx = N(e.Cx), Cy = N(e.Cy), Rx = N(e.Rx), Ry = N(e.Ry) },
        ArcClip a => new CArcClip { Cx = N(a.Cx), Cy = N(a.Cy), R = N(a.R), Inner = N(a.Inner), Start = N(a.Start), Sweep = N(a.Sweep) },
        PathClip p => new CPathClip(StaticPathGeom.Parse(p.D, FillType(p.FillRule))),
        _ => new CRectClip { X = ExprCompiler.Zero, Y = ExprCompiler.Zero, W = ExprCompiler.Zero, H = ExprCompiler.Zero },
    };

    private CEffect CompileEffect(Effect effect) => effect switch
    {
        BlurEffect b => new CEffect { Kind = EffectKind.Blur, Sigma = N(b.Sigma) },
        ShadowEffect s => new CEffect { Kind = EffectKind.Shadow, Sigma = N(s.Sigma), Dx = N(s.Dx), Dy = N(s.Dy), Color = _x.Compile(s.Color) },
        GlowEffect g => new CEffect { Kind = EffectKind.Glow, Sigma = N(g.Sigma), Color = _x.Compile(g.Color) },
        _ => new CEffect { Kind = EffectKind.Blur, Sigma = ExprCompiler.Zero },
    };

    private CPaint? Paint(Paint? paint, int depth = 0)
    {
        switch (paint)
        {
            case null: return null;
            case SolidPaint s: return new CSolid(_x.Compile(s.Color));
            case LinearPaint l:
                return new CGradient { Kind = GradientKind.Linear, Geometry = [N(l.X1), N(l.Y1), N(l.X2), N(l.Y2)], Stops = Stops(l.Stops), Tile = Tile(l.Spread) };
            case RadialPaint r:
                return new CGradient { Kind = GradientKind.Radial, Geometry = [N(r.Cx), N(r.Cy), N(r.R)], Stops = Stops(r.Stops), Tile = Tile(r.Spread) };
            case SweepPaint s:
                return new CGradient
                {
                    Kind = GradientKind.Sweep, Geometry = [N(s.Cx), N(s.Cy), N(s.Start), s.End is null ? ExprCompiler.Zero : _x.Compile(s.End)],
                    DefaultEnd = s.End is null, Stops = Stops(s.Stops), Tile = Tile(s.Spread),
                };
            case ComposePaint c when depth < 16:
                return new CCompose(Blend(c.Blend), Paint(c.Dst, depth + 1) ?? new CSolid(ExprCompiler.Const(Val.Col(0), Ty.Color)),
                    Paint(c.Src, depth + 1) ?? new CSolid(ExprCompiler.Const(Val.Col(0), Ty.Color)));
            default: return null;
        }
    }

    private CStop[] Stops(IReadOnlyList<GradientStop> stops) => stops.Take(16).Select(s => new CStop(N(s.At), _x.Compile(s.Color))).ToArray();

    private static SKShaderTileMode Tile(string? spread) => spread switch
    {
        "repeat" => SKShaderTileMode.Repeat,
        "reflect" => SKShaderTileMode.Mirror,
        _ => SKShaderTileMode.Clamp,
    };

    private CStroke? Stroke(Stroke? s)
    {
        if (s is null) return null;
        return new CStroke
        {
            Paint = Paint(s.Paint) ?? new CSolid(ExprCompiler.Const(Val.Col(SceneColor.Black), Ty.Color)),
            Width = N(s.Width),
            Cap = s.Cap switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
            Join = s.Join switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
            Miter = (float)Math.Clamp(s.MiterLimit ?? 4, 1, 1000),
            Dash = s.Dash?.Take(16).Select(d => _x.Compile(d)).ToArray(),
            DashOffset = s.DashOffset is null ? null : _x.Compile(s.DashOffset),
            TrimStart = s.TrimStart is null ? null : _x.Compile(s.TrimStart),
            TrimEnd = s.TrimEnd is null ? null : _x.Compile(s.TrimEnd),
        };
    }

    public static SKBlendMode Blend(string? blend) => blend switch
    {
        "multiply" => SKBlendMode.Multiply,
        "screen" => SKBlendMode.Screen,
        "overlay" => SKBlendMode.Overlay,
        "darken" => SKBlendMode.Darken,
        "lighten" => SKBlendMode.Lighten,
        "color-dodge" => SKBlendMode.ColorDodge,
        "color-burn" => SKBlendMode.ColorBurn,
        "hard-light" => SKBlendMode.HardLight,
        "soft-light" => SKBlendMode.SoftLight,
        "difference" => SKBlendMode.Difference,
        "exclusion" => SKBlendMode.Exclusion,
        "hue" => SKBlendMode.Hue,
        "saturation" => SKBlendMode.Saturation,
        "color" => SKBlendMode.Color,
        "luminosity" => SKBlendMode.Luminosity,
        "plus" => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };
}

/// <summary>Walks expression trees without recursion.</summary>
internal static class ExprWalk
{
    /// <summary>Every operation in <paramref name="root"/> (the root included).</summary>
    public static IEnumerable<Expr> Refs(Expr root)
    {
        var stack = new Stack<Expr>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 1_000_000)
        {
            var e = stack.Pop();
            yield return e;
            switch (e)
            {
                case CallExpr c: foreach (var a in c.Args) stack.Push(a); break;
                case FormatExpr f:
                    stack.Push(f.Value);
                    if (f.Decimals is not null) stack.Push(f.Decimals);
                    if (f.Group is not null) stack.Push(f.Group);
                    break;
                case StepsExpr s:
                    stack.Push(s.Value);
                    foreach (var a in s.At) stack.Push(a);
                    foreach (var o in s.Out) stack.Push(o);
                    break;
            }
        }
    }
}

