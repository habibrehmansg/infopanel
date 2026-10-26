using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InfoPanel.Spec;

// The scene format, infopanel.scene/1 (spec/schemas/scene.schema.json). Data only: these records mirror the schema
// one to one (SpecSchemaAgreementTests holds them to it); absent optional members are null. Read them with SpecJson
// after the scene has passed SceneValidator; rendering is not part of this namespace.

/// <summary>A scene: what one item draws.</summary>
[SpecDef("urn:infopanel:scene:1")]
public sealed record Scene
{
    public required string Format { get; init; }
    public IReadOnlyList<string>? Requires { get; init; }
    public IReadOnlyDictionary<string, InputDecl>? Inputs { get; init; }
    public IReadOnlyDictionary<string, Expr>? Values { get; init; }
    public IReadOnlyDictionary<string, StateCell>? State { get; init; }
    public required IReadOnlyList<SceneNode> Nodes { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/inputDecl")]
public sealed record InputDecl
{
    /// <summary><c>number</c>, <c>text</c> or <c>table</c>.</summary>
    public required string Type { get; init; }
    public string? Label { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/stateCell")]
public sealed record StateCell
{
    public required Expr Init { get; init; }
    public required Expr Next { get; init; }
}

/// <summary>A node; <see cref="Id"/> is its instance id (docs/identity.md).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GroupNode), "group")]
[JsonDerivedType(typeof(RectNode), "rect")]
[JsonDerivedType(typeof(EllipseNode), "ellipse")]
[JsonDerivedType(typeof(ArcNode), "arc")]
[JsonDerivedType(typeof(LineNode), "line")]
[JsonDerivedType(typeof(PathNode), "path")]
[JsonDerivedType(typeof(PolylineNode), "polyline")]
[JsonDerivedType(typeof(TextNode), "text")]
[JsonDerivedType(typeof(SeriesNode), "series")]
[JsonDerivedType(typeof(TableNode), "table")]
[JsonDerivedType(typeof(MediaNode), "media")]
public abstract record SceneNode
{
    public string? Id { get; init; }
    public Expr? Visible { get; init; }
    public Expr? Opacity { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/groupNode")]
public sealed record GroupNode : SceneNode
{
    public IReadOnlyList<TransformStep>? Transform { get; init; }
    public ClipShape? Clip { get; init; }
    public string? Blend { get; init; }
    public IReadOnlyList<Effect>? Effects { get; init; }
    public required IReadOnlyList<SceneNode> Children { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/rectNode")]
public sealed record RectNode : SceneNode
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public required Expr W { get; init; }
    public required Expr H { get; init; }
    public CornerRadii? R { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

/// <summary>One radius for every corner (<see cref="All"/>), or four (<see cref="Corners"/>: top-left, top-right, bottom-right, bottom-left).</summary>
public sealed record CornerRadii(Expr? All, IReadOnlyList<Expr>? Corners);

[SpecDef("urn:infopanel:scene:1#/$defs/ellipseNode")]
public sealed record EllipseNode : SceneNode
{
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
    public required Expr Rx { get; init; }
    public required Expr Ry { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/arcNode")]
public sealed record ArcNode : SceneNode
{
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
    public required Expr R { get; init; }
    public Expr? Inner { get; init; }
    public Expr? Start { get; init; }
    public required Expr Sweep { get; init; }
    /// <summary><c>sector</c> (default) or <c>open</c>.</summary>
    public string? Close { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/lineNode")]
public sealed record LineNode : SceneNode
{
    public required Expr X1 { get; init; }
    public required Expr Y1 { get; init; }
    public required Expr X2 { get; init; }
    public required Expr Y2 { get; init; }
    public required Stroke Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/pathNode")]
public sealed record PathNode : SceneNode
{
    public required string D { get; init; }
    public string? FillRule { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/polylineNode")]
public sealed record PolylineNode : SceneNode
{
    /// <summary>Each point is [x, y].</summary>
    public required IReadOnlyList<IReadOnlyList<Expr>> Points { get; init; }
    public bool? Closed { get; init; }
    public string? FillRule { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/textNode")]
public sealed record TextNode : SceneNode
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public Expr? W { get; init; }
    public Expr? H { get; init; }
    public required TextContent Text { get; init; }
    public required Font Font { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
    public string? Align { get; init; }
    public string? Valign { get; init; }
    public string? Wrap { get; init; }
    public int? MaxLines { get; init; }
    public string? Overflow { get; init; }
    public Marquee? Marquee { get; init; }
    public TextFit? Fit { get; init; }
    public Expr? LineHeight { get; init; }
    public Expr? LetterSpacing { get; init; }
    public bool? Underline { get; init; }
    public bool? Strike { get; init; }
}

/// <summary>A text node's text: one string expression (<see cref="Plain"/>) or runs (<see cref="Spans"/>).</summary>
public sealed record TextContent(Expr? Plain, IReadOnlyList<TextSpan>? Spans);

[SpecDef("urn:infopanel:scene:1#/$defs/textSpan")]
public sealed record TextSpan
{
    public required Expr Text { get; init; }
    public FontOverride? Font { get; init; }
    public Paint? Fill { get; init; }
    public bool? Underline { get; init; }
    public bool? Strike { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/font")]
public sealed record Font
{
    public required string Family { get; init; }
    public required Expr Size { get; init; }
    public int? Weight { get; init; }
    public bool? Italic { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/fontOverride")]
public sealed record FontOverride
{
    public string? Family { get; init; }
    public Expr? Size { get; init; }
    public int? Weight { get; init; }
    public bool? Italic { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/textNode/properties/marquee")]
public sealed record Marquee
{
    public Expr? Speed { get; init; }
    public Expr? Gap { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/textNode/properties/fit")]
public sealed record TextFit
{
    public required Expr Min { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/seriesNode")]
public sealed record SeriesNode : SceneNode
{
    public required string Input { get; init; }
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public required Expr W { get; init; }
    public required Expr H { get; init; }
    public required Expr Min { get; init; }
    public required Expr Max { get; init; }
    public string? Mode { get; init; }
    public Expr? Pitch { get; init; }
    public int? Count { get; init; }
    public Expr? BarWidth { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/tableNode")]
public sealed record TableNode : SceneNode
{
    public required string Input { get; init; }
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public required IReadOnlyList<TableColumn> Columns { get; init; }
    public int? Rows { get; init; }
    public bool? Header { get; init; }
    public Expr? RowHeight { get; init; }
    public Expr? Gap { get; init; }
    public required Font Font { get; init; }
    public Paint? Fill { get; init; }
    public Stroke? Stroke { get; init; }
    public FontOverride? HeaderFont { get; init; }
    public Paint? HeaderFill { get; init; }
    public string? Overflow { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/tableColumn")]
public sealed record TableColumn
{
    public required ColumnRef Column { get; init; }
    public required Expr W { get; init; }
    public string? Align { get; init; }
    public Expr? Title { get; init; }
}

/// <summary>A table column by index or by name.</summary>
public sealed record ColumnRef(int? Index, string? Name);

[SpecDef("urn:infopanel:scene:1#/$defs/mediaNode")]
public sealed record MediaNode : SceneNode
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public Expr? W { get; init; }
    public Expr? H { get; init; }
    public required MediaSource Source { get; init; }
    public string? Fit { get; init; }
    public IReadOnlyList<double>? Align { get; init; }
    public string? Playback { get; init; }
    public Expr? Speed { get; init; }
    public string? Sampling { get; init; }
}

/// <summary>
/// A media node's source: an asset (<see cref="Asset"/>), a configured provider (<see cref="Provider"/> and
/// <see cref="Ref"/>), or a sequence of assets picked by <see cref="Index"/>. The schema allows exactly one form.
/// </summary>
[SpecDef("urn:infopanel:scene:1#/$defs/assetSource", "urn:infopanel:scene:1#/$defs/providerSource", "urn:infopanel:scene:1#/$defs/sequenceSource")]
public sealed record MediaSource
{
    public string? Asset { get; init; }
    public string? Provider { get; init; }
    public Expr? Ref { get; init; }
    public IReadOnlyList<MediaSource>? Sequence { get; init; }
    public Expr? Index { get; init; }
    public bool? Crossfade { get; init; }
}

/// <summary>A paint: a colour (<see cref="SolidPaint"/>) or a shader (gradient or composition).</summary>
public abstract record Paint;

/// <summary>A colour literal or colour expression used as a paint.</summary>
public sealed record SolidPaint(Expr Color) : Paint;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LinearPaint), "linear")]
[JsonDerivedType(typeof(RadialPaint), "radial")]
[JsonDerivedType(typeof(SweepPaint), "sweep")]
[JsonDerivedType(typeof(ComposePaint), "compose")]
public abstract record ShaderPaint : Paint;

[SpecDef("urn:infopanel:scene:1#/$defs/linearPaint")]
public sealed record LinearPaint : ShaderPaint
{
    public required Expr X1 { get; init; }
    public required Expr Y1 { get; init; }
    public required Expr X2 { get; init; }
    public required Expr Y2 { get; init; }
    public required IReadOnlyList<GradientStop> Stops { get; init; }
    public string? Spread { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/radialPaint")]
public sealed record RadialPaint : ShaderPaint
{
    public required Expr Cx { get; init; }
    public required Expr Cy { get; init; }
    public required Expr R { get; init; }
    public required IReadOnlyList<GradientStop> Stops { get; init; }
    public string? Spread { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/sweepPaint")]
public sealed record SweepPaint : ShaderPaint
{
    public required Expr Cx { get; init; }
    public required Expr Cy { get; init; }
    public Expr? Start { get; init; }
    public Expr? End { get; init; }
    public required IReadOnlyList<GradientStop> Stops { get; init; }
    public string? Spread { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/composePaint")]
public sealed record ComposePaint : ShaderPaint
{
    public required string Blend { get; init; }
    public required Paint Dst { get; init; }
    public required Paint Src { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/stop")]
public sealed record GradientStop
{
    public required Expr At { get; init; }
    public required Expr Color { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/stroke")]
public sealed record Stroke
{
    public required Paint Paint { get; init; }
    public required Expr Width { get; init; }
    public string? Cap { get; init; }
    public string? Join { get; init; }
    public double? MiterLimit { get; init; }
    public IReadOnlyList<Expr>? Dash { get; init; }
    public Expr? DashOffset { get; init; }
    public Expr? TrimStart { get; init; }
    public Expr? TrimEnd { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TranslateStep), "translate")]
[JsonDerivedType(typeof(RotateStep), "rotate")]
[JsonDerivedType(typeof(ScaleStep), "scale")]
[JsonDerivedType(typeof(SkewStep), "skew")]
[JsonDerivedType(typeof(MatrixStep), "matrix")]
public abstract record TransformStep;

[SpecDef("urn:infopanel:scene:1#/$defs/translateStep")]
public sealed record TranslateStep : TransformStep
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/rotateStep")]
public sealed record RotateStep : TransformStep
{
    public required Expr Angle { get; init; }
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/scaleStep")]
public sealed record ScaleStep : TransformStep
{
    public required Expr X { get; init; }
    public required Expr Y { get; init; }
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/skewStep")]
public sealed record SkewStep : TransformStep
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/matrixStep")]
public sealed record MatrixStep : TransformStep
{
    public required IReadOnlyList<double> M { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectClip), "rect")]
[JsonDerivedType(typeof(EllipseClip), "ellipse")]
[JsonDerivedType(typeof(ArcClip), "arc")]
[JsonDerivedType(typeof(PathClip), "path")]
public abstract record ClipShape;

[SpecDef("urn:infopanel:scene:1#/$defs/rectClip")]
public sealed record RectClip : ClipShape
{
    public Expr? X { get; init; }
    public Expr? Y { get; init; }
    public required Expr W { get; init; }
    public required Expr H { get; init; }
    public CornerRadii? R { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/ellipseClip")]
public sealed record EllipseClip : ClipShape
{
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
    public required Expr Rx { get; init; }
    public required Expr Ry { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/arcClip")]
public sealed record ArcClip : ClipShape
{
    public Expr? Cx { get; init; }
    public Expr? Cy { get; init; }
    public required Expr R { get; init; }
    public Expr? Inner { get; init; }
    public Expr? Start { get; init; }
    public required Expr Sweep { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/pathClip")]
public sealed record PathClip : ClipShape
{
    public required string D { get; init; }
    public string? FillRule { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BlurEffect), "blur")]
[JsonDerivedType(typeof(ShadowEffect), "shadow")]
[JsonDerivedType(typeof(GlowEffect), "glow")]
public abstract record Effect;

[SpecDef("urn:infopanel:scene:1#/$defs/blurEffect")]
public sealed record BlurEffect : Effect
{
    public required Expr Sigma { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/shadowEffect")]
public sealed record ShadowEffect : Effect
{
    public Expr? Dx { get; init; }
    public Expr? Dy { get; init; }
    public required Expr Sigma { get; init; }
    public required Expr Color { get; init; }
}

[SpecDef("urn:infopanel:scene:1#/$defs/glowEffect")]
public sealed record GlowEffect : Effect
{
    public required Expr Sigma { get; init; }
    public required Expr Color { get; init; }
}
