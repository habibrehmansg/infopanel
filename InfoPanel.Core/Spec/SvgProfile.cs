using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace InfoPanel.Spec;

/// <summary>
/// The SVG profile of spec/docs/svg.md. A file is read as a stream (UTF-8 only; no DTD, no entities beyond XML's own,
/// no processing instructions, no CDATA) against an allowlist of elements, attributes and <c>style</c> properties, and
/// every value is parsed by a strict grammar for its property. Its references are resolved into a graph that must have
/// no cycle, nest at most <c>svgDrawnDepth</c> deep and expand to at most <c>svgElements</c>. A file that passes
/// is <b>rebuilt</b>: a fresh, canonical SVG document with only the allowlisted elements and attributes, every value
/// regenerated from its parsed form and <c>style</c> properties as attributes. Engines decode only the rebuilt document,
/// never the original text, so nothing the validator and the renderer read differently survives.
/// </summary>
internal static class SvgProfile
{
    public const string SvgNamespace = "http://www.w3.org/2000/svg";
    public const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Numbers of <c>d</c> or <c>points</c> that count as one more element.</summary>
    public const int NumbersPerElement = 32;

    /// <summary>The elements of the profile, in the SVG namespace, compared with case.</summary>
    public static IReadOnlySet<string> Elements { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "symbol", "use", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "linearGradient", "radialGradient", "stop", "clipPath", "mask", "title", "desc",
    };

    /// <summary>Presentation attributes: allowed as attributes and as <c>style</c> properties, emitted in this order.</summary>
    private static readonly string[] PresentationOrder =
    [
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "opacity", "color", "display", "visibility", "overflow", "clip-path", "clip-rule", "mask",
        "stop-color", "stop-opacity",
    ];

    /// <summary>Geometry and structure attributes: allowed as attributes only, never in <c>style</c>; emitted in this order.</summary>
    private static readonly string[] GeometryOrder =
    [
        "x", "y", "width", "height", "rx", "ry", "cx", "cy", "r", "fx", "fy", "fr", "x1", "y1", "x2", "y2", "d", "points",
        "pathLength", "offset", "transform", "gradientTransform", "gradientUnits", "spreadMethod", "clipPathUnits",
        "maskUnits", "maskContentUnits", "viewBox", "preserveAspectRatio", "version", "baseProfile",
    ];

    public static IReadOnlySet<string> Presentation { get; } = new HashSet<string>(PresentationOrder, StringComparer.Ordinal);
    public static IReadOnlySet<string> Geometry { get; } = new HashSet<string>(GeometryOrder, StringComparer.Ordinal);

    /// <summary>The elements that may have <c>href</c> (or <c>xlink:href</c>).</summary>
    private static readonly HashSet<string> HrefElements = new(StringComparer.Ordinal) { "use", "linearGradient", "radialGradient" };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private enum Ref { Use, Clip, Mask, Paint, Gradient }

    private sealed class Outside(string message) : Exception(message);

    /// <summary>One element as read: its canonical attributes (style merged in), its href target, its text.</summary>
    private sealed class Node(string name, int parent, int depth)
    {
        public string Name { get; } = name;
        public int Parent { get; } = parent;
        public int Depth { get; } = depth;
        public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);
        public string? Id { get; set; }
        public string? Href { get; set; }
        public StringBuilder? Text { get; set; }
        /// <summary>Numbers in its <c>d</c> and <c>points</c>.</summary>
        public int Numbers { get; set; }
    }

    /// <summary>
    /// Reads an SVG within <paramref name="limits"/> (<c>svgElements</c>, <c>svgDepth</c>, <c>svgDrawnDepth</c>,
    /// <c>svgGradientChain</c>): null and a problem, or the info with <see cref="MediaInfo.Elements"/> the expanded
    /// element count (<c>svgElements</c> + 1 when over it; reading stops at element <c>svgElements</c> + 1), and
    /// <paramref name="rebuilt"/> the canonical document (UTF-8, no BOM) when the file is in the profile and within
    /// <c>svgElements</c>, else null.
    /// </summary>
    public static MediaInfo? Read(ReadOnlySpan<byte> bytes, out MediaProblem? problem, SpecLimits limits, out byte[]? rebuilt)
    {
        var maxElements = limits.SvgElements;
        problem = null;
        rebuilt = null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MediaProbe.MaxSvgCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = false,
        };

        var nodes = new List<Node>();
        var open = new List<int>(); // the element open at each depth
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string? widthAttr = null, heightAttr = null;
        double? vbW = null, vbH = null; // the root viewBox's size as rebuilt
        double writtenVbW = 0, writtenVbH = 0; // and as written, before rounding
        int textDepth = -1; // the depth of the <title> or <desc> being read, or -1
        try
        {
            // UTF-8 only: decoded strictly here, so an encoding named in the XML declaration can't change how it reads.
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
            var text = StrictUtf8.GetString(bytes);
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        break;
                    case XmlNodeType.EndElement:
                        if (reader.Depth == textDepth) textDepth = -1;
                        continue;
                    case XmlNodeType.XmlDeclaration:
                        var encoding = reader.GetAttribute("encoding");
                        if (encoding is not null && !encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
                            return Fail(out problem, MediaProbe.SvgParseCode, $"SVG must be UTF-8 (the XML declaration names {Clip(encoding)}).");
                        continue;
                    case XmlNodeType.Text when textDepth >= 0:
                        (nodes[^1].Text ??= new StringBuilder()).Append(reader.Value);
                        continue;
                    case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.Comment:
                        continue;
                    case XmlNodeType.Text:
                        throw new Outside("has text outside <title> and <desc>.");
                    case XmlNodeType.CDATA:
                        throw new Outside("has a CDATA section.");
                    case XmlNodeType.ProcessingInstruction:
                        throw new Outside($"has a processing instruction ({reader.Name}).");
                    default:
                        throw new Outside($"has a {reader.NodeType} node.");
                }

                var index = nodes.Count;
                if (index + 1 > maxElements)
                    return new MediaInfo(MediaFormat.Svg, 0, 0, 1, 0, index + 1);
                string name = reader.LocalName;
                if (index == 0 && (name != "svg" || reader.Prefix.Length != 0))
                    return Fail(out problem, MediaProbe.FormatCode, "The XML root element is not an unprefixed <svg>.");
                if (reader.NamespaceURI != SvgNamespace)
                    throw new Outside($"has an element <{reader.Name}> outside the SVG namespace.");
                if (!Elements.Contains(name))
                    throw new Outside($"has an element outside the profile (<{name}>).");
                if (textDepth >= 0)
                    throw new Outside("has an element inside <title> or <desc>, which hold text only.");

                var depth = reader.Depth;
                if (depth >= limits.SvgDepth)
                    throw new Outside($"nests elements more than {limits.SvgDepth} deep.");
                var node = new Node(name, depth == 0 ? -1 : open[depth - 1], depth);
                if (open.Count == depth) open.Add(index);
                else open[depth] = index;
                nodes.Add(node);
                if (name is "title" or "desc" && !reader.IsEmptyElement) textDepth = depth;

                Dictionary<string, string>? style = null;
                if (reader.MoveToFirstAttribute())
                {
                    do
                    {
                        string ns = reader.NamespaceURI, attr = reader.LocalName, value = reader.Value;
                        if (ns == XmlnsNamespace)
                        {
                            if (value is not (SvgNamespace or XLinkNamespace))
                                throw new Outside($"declares a namespace other than SVG and XLink ({reader.Name}).");
                            continue;
                        }
                        if (ns == XmlNamespace)
                        {
                            if (attr == "space") continue; // allowed and not emitted: no text is drawn
                            throw new Outside($"has an xml:{attr} attribute.");
                        }
                        if (ns == XLinkNamespace && attr == "href" || ns.Length == 0 && attr == "href")
                        {
                            if (node.Href is not null) throw new Outside("has both href and xlink:href.");
                            node.Href = Href(name, reader.Name, value);
                            continue;
                        }
                        if (ns.Length != 0)
                            throw new Outside($"has an attribute {reader.Name} outside the SVG and XLink attributes.");

                        switch (attr)
                        {
                            case "id":
                                if (!IsName(value)) throw new Outside($"has an id that isn't an XML name ({Clip(value)}).");
                                if (!ids.TryAdd(value, index)) throw new Outside($"has the id {Clip(value)} twice.");
                                node.Id = value;
                                break;
                            case "class":
                                break; // allowed and not emitted: the profile has no stylesheet for it to match
                            case "style":
                                style = Style(value);
                                break;
                            case "width" or "height" when index == 0:
                                // The root's size: unitless or px, checked after reading (media.svg-size).
                                if (attr == "width") widthAttr = value; else heightAttr = value;
                                break;
                            default:
                                if (Presentation.Contains(attr))
                                    node.Attrs[attr] = PresentationValue(attr, value) ?? throw BadValue(attr, value);
                                else if (Geometry.Contains(attr))
                                {
                                    var canonical = GeometryValue(attr, value, out var numbers, out var vb) ?? throw BadValue(attr, value);
                                    node.Numbers += numbers;
                                    if (canonical.Length > 0 || attr is "d" or "points") node.Attrs[attr] = canonical;
                                    if (index == 0 && vb is { } box) (vbW, vbH, writtenVbW, writtenVbH) = box;
                                }
                                else if (attr.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                                    throw new Outside($"has an event attribute ({attr}).");
                                else throw new Outside($"has a {attr} attribute.");
                                break;
                        }
                    }
                    while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }
                // style wins over the presentation attributes, as CSS declarations do.
                if (style is not null)
                    foreach (var (property, value) in style) node.Attrs[property] = value;
            }
        }
        catch (Outside ex)
        {
            return Fail(out problem, MediaProbe.SvgExternalCode, "SVG is outside the profile: it " + ex.Message);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            return Fail(out problem, MediaProbe.SvgParseCode, "SVG could not be parsed: " + ex.Message);
        }

        if (nodes.Count == 0)
            return Fail(out problem, MediaProbe.FormatCode, "No <svg> root element.");

        long expanded;
        try
        {
            expanded = Expand(nodes, ids, (long)maxElements + 1, limits);
        }
        catch (Outside ex)
        {
            return Fail(out problem, MediaProbe.SvgExternalCode, "SVG is outside the profile: it " + ex.Message);
        }
        var elements = (int)Math.Min(expanded, int.MaxValue);
        if (expanded > maxElements)
            return new MediaInfo(MediaFormat.Svg, 0, 0, 1, 0, elements);

        // width and height: unitless or px; any other unit or a percentage is unsupported. Absent: the viewBox size.
        if (!MediaProbe.TryLength(widthAttr, out var w) || !MediaProbe.TryLength(heightAttr, out var h))
            return Fail(out problem, MediaProbe.SvgSizeCode, "SVG width and height must be unitless or in px.");
        // A viewBox side too small for the rebuilt document's numbers would be 0 there, its aspect ratio lost.
        if ((writtenVbW > 0 && vbW == 0) || (writtenVbH > 0 && vbH == 0))
            return Fail(out problem, MediaProbe.SvgSizeCode, "SVG viewBox width or height is above 0 but too small to keep.");
        if (w is { } width) nodes[0].Attrs["width"] = SvgValues.Num(Math.Min(width, SvgValues.MaxMagnitude))!;
        if (h is { } height) nodes[0].Attrs["height"] = SvgValues.Num(Math.Min(height, SvgValues.MaxMagnitude))!;

        rebuilt = Emit(nodes);
        // The size reported is the rebuilt document's; the limits hold the size as written (the stored file is what other
        // renderers read), each side rounded up to whole pixels, so rounding to 7 significant digits can't take a side
        // just over a limit back under it (a viewBox 8192.0001 wide is rebuilt as 8192, and held to 8193).
        int sizeW = MediaProbe.ToPixels(w ?? vbW), sizeH = MediaProbe.ToPixels(h ?? vbH);
        int extentW = MediaProbe.ToPixels(w ?? writtenVbW), extentH = MediaProbe.ToPixels(h ?? writtenVbH);
        // Other renderers take the absent side from the viewBox's aspect ratio rather than its size: the limits hold that
        // too, worked out from the viewBox as written.
        if (writtenVbW > 0 && writtenVbH > 0)
        {
            if (w is { } onlyW && h is null) extentH = Math.Max(extentH, MediaProbe.ToPixels(onlyW * writtenVbH / writtenVbW));
            else if (h is { } onlyH && w is null) extentW = Math.Max(extentW, MediaProbe.ToPixels(onlyH * writtenVbW / writtenVbH));
        }
        return new MediaInfo(MediaFormat.Svg, sizeW, sizeH, 1, 0, elements, extentW > sizeW ? extentW : 0, extentH > sizeH ? extentH : 0);
    }

    private static MediaInfo? Fail(out MediaProblem? problem, string code, string message)
    {
        problem = new MediaProblem(code, message);
        return null;
    }

    private static string Clip(string value) => value.Length <= 40 ? value : value[..40] + "...";

    private static Outside BadValue(string attr, string value) =>
        new($"has a {attr} value outside the profile's grammar ({Clip(value.Trim())}).");

    /// <summary><c>href</c> or <c>xlink:href</c>: only on <c>use</c> and gradients, and only <c>#id</c>; returns the id.</summary>
    private static string Href(string element, string attr, string value)
    {
        if (!HrefElements.Contains(element))
            throw new Outside($"has {attr} on <{element}> (only <use> and gradients may have one).");
        if (value.Length < 2 || value[0] != '#' || !IsName(value[1..]))
            throw new Outside("references something outside itself (only #id references are allowed).");
        return value[1..];
    }

    /// <summary>A <c>style</c> attribute: <c>property: value</c> pairs whose property is a presentation attribute, parsed.</summary>
    private static Dictionary<string, string> Style(string style)
    {
        if (style.Contains('\\')) throw new Outside("has a CSS escape.");
        if (style.Contains('@')) throw new Outside("has an @ (CSS at-rules such as @import are not allowed).");
        if (style.Contains("/*", StringComparison.Ordinal)) throw new Outside("has a CSS comment.");
        if (style.AsSpan().IndexOfAny('{', '}') >= 0) throw new Outside("has a style attribute with braces.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in style.Split(';'))
        {
            if (declaration.AsSpan().Trim().IsEmpty) continue;
            var colon = declaration.IndexOf(':');
            if (colon < 0) throw new Outside($"has a style declaration without a value ({Clip(declaration.Trim())}).");
            var property = declaration[..colon].Trim().ToLowerInvariant();
            if (!Presentation.Contains(property))
                throw new Outside($"has a style property {Clip(property)} (only presentation properties such as fill and stroke).");
            var value = declaration[(colon + 1)..];
            result[property] = PresentationValue(property, value) ?? throw BadValue(property, value);
        }
        return result;
    }

    /// <summary>A presentation value in canonical form, or null when it doesn't parse under its property's grammar.</summary>
    private static string? PresentationValue(string property, string value) => property switch
    {
        "fill" or "stroke" => SvgValues.Paint(value, out _),
        "fill-opacity" or "stroke-opacity" or "opacity" or "stop-opacity" => SvgValues.Opacity(value),
        "fill-rule" or "clip-rule" => SvgValues.Keyword(value, "nonzero", "evenodd"),
        "stroke-width" => SvgValues.Length(value, nonNegative: true),
        "stroke-linecap" => SvgValues.Keyword(value, "butt", "round", "square"),
        "stroke-linejoin" => SvgValues.Keyword(value, "miter", "round", "bevel"),
        "stroke-miterlimit" => SvgValues.Number(value, min: 1),
        "color" => SvgValues.Color(value),
        "display" => SvgValues.Keyword(value, "inline", "block", "none"),
        "visibility" => SvgValues.Keyword(value, "visible", "hidden", "collapse"),
        "overflow" => SvgValues.Keyword(value, "visible", "hidden", "scroll", "auto"),
        "clip-path" or "mask" => SvgValues.Reference(value, out _),
        "stop-color" => SvgValues.ColorOrCurrent(value),
        _ => null,
    };

    /// <summary>
    /// A geometry or structure value in canonical form ("" for one that isn't emitted), or null when it doesn't parse;
    /// <paramref name="numbers"/> counts the numbers of <c>d</c> and <c>points</c>, <paramref name="viewBox"/> is the size of a <c>viewBox</c>,
    /// as rebuilt and as written.
    /// </summary>
    private static string? GeometryValue(string attr, string value, out int numbers, out (double, double, double, double)? viewBox)
    {
        numbers = 0;
        viewBox = null;
        switch (attr)
        {
            case "x" or "y" or "cx" or "cy" or "fx" or "fy" or "x1" or "y1" or "x2" or "y2":
                return SvgValues.Length(value, nonNegative: false);
            case "width" or "height" or "r" or "rx" or "ry" or "fr":
                return SvgValues.Length(value, nonNegative: true);
            case "d":
                return value.AsSpan().Trim().IsEmpty ? "" : PathData.Rebuild(value, out numbers);
            case "points":
                return SvgValues.Points(value, out numbers);
            case "pathLength":
                return SvgValues.Number(value, min: 0);
            case "offset":
                return SvgValues.Offset(value);
            case "transform" or "gradientTransform":
                return SvgValues.Transform(value);
            case "gradientUnits" or "clipPathUnits" or "maskUnits" or "maskContentUnits":
                return SvgValues.Exact(value, "userSpaceOnUse", "objectBoundingBox");
            case "spreadMethod":
                return SvgValues.Exact(value, "pad", "reflect", "repeat");
            case "viewBox":
                var box = SvgValues.ViewBox(value, out var vw, out var vh, out var writtenW, out var writtenH);
                if (box is not null) viewBox = (vw, vh, writtenW, writtenH);
                return box;
            case "preserveAspectRatio":
                return SvgValues.AspectRatio(value);
            case "version":
                // Allowed and not emitted: it doesn't change the drawing.
                var i = 0;
                var t = value.Trim();
                return t.Length > 0 && char.IsAsciiDigit(t[0]) && SvgValues.ReadNumber(t, ref i, out _) && i == t.Length && !t.Contains('e', StringComparison.OrdinalIgnoreCase) ? "" : null;
            case "baseProfile":
                return IsName(value.Trim()) ? "" : null;
            default:
                return null;
        }
    }

    internal static bool IsName(string s)
    {
        if (s.Length == 0 || !XmlConvert.IsStartNCNameChar(s[0])) return false;
        foreach (var c in s) if (!XmlConvert.IsNCNameChar(c)) return false;
        return true;
    }

    /// <summary>The id of a canonical <c>url(#id)</c> value (with or without a fallback after it), or null.</summary>
    private static string? UrlId(string? canonical)
    {
        if (canonical is null || !canonical.StartsWith("url(#", StringComparison.Ordinal)) return null;
        return canonical[5..canonical.IndexOf(')')];
    }

    /// <summary>
    /// Resolves the references (each to an element of the right kind) and returns the root's expanded element count,
    /// saturating at <paramref name="cap"/>. An element counts 1, plus 1 per <see cref="NumbersPerElement"/> numbers of
    /// its <c>d</c> and <c>points</c>, plus for each <c>fill</c> and <c>stroke</c> gradient 1 and the number of
    /// <c>href</c> references the gradient inherits through (at most <c>svgGradientChain</c>), plus its children,
    /// plus for each <c>use</c>, <c>clip-path</c> and <c>mask</c> reference the expanded count of the target. The tree
    /// and references must have no cycle and nest at most <c>svgDrawnDepth</c> deep. Iterative: no recursion.
    /// </summary>
    private static long Expand(List<Node> nodes, Dictionary<string, int> ids, long cap, SpecLimits limits)
    {
        var n = nodes.Count;
        var edges = new List<(int To, bool Counts)>?[n];
        var weight = new long[n];
        var gradientHref = new int[n];
        Array.Fill(gradientHref, -1);
        for (int i = 1; i < n; i++) (edges[nodes[i].Parent] ??= []).Add((i, true));

        int Resolve(string id, Ref kind, int from)
        {
            if (!ids.TryGetValue(id, out var to)) throw new Outside($"references #{Clip(id)}, which isn't in the file.");
            var target = nodes[to].Name;
            var ok = kind switch
            {
                Ref.Clip => target == "clipPath",
                Ref.Mask => target == "mask",
                Ref.Paint or Ref.Gradient => target is "linearGradient" or "radialGradient",
                _ => true,
            };
            if (!ok) throw new Outside($"references #{Clip(id)}, a <{target}>, where {(kind is Ref.Clip ? "a <clipPath>" : kind is Ref.Mask ? "a <mask>" : "a gradient")} is needed.");
            (edges[from] ??= []).Add((to, kind is Ref.Use or Ref.Clip or Ref.Mask));
            return to;
        }

        var paints = new List<(int From, int To)>();
        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            if (node.Href is { } href)
            {
                var to = Resolve(href, node.Name == "use" ? Ref.Use : Ref.Gradient, i);
                if (node.Name != "use") gradientHref[i] = to;
            }
            foreach (var paint in (string[])["fill", "stroke"])
                if (UrlId(node.Attrs.GetValueOrDefault(paint)) is { } id) paints.Add((i, Resolve(id, Ref.Paint, i)));
            if (UrlId(node.Attrs.GetValueOrDefault("clip-path")) is { } clip) Resolve(clip, Ref.Clip, i);
            if (UrlId(node.Attrs.GetValueOrDefault("mask")) is { } mask) Resolve(mask, Ref.Mask, i);
            weight[i] = 1 + node.Numbers / NumbersPerElement;
        }

        // Gradient inheritance: at most svgGradientChain href steps, and no cycle.
        var chain = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (gradientHref[i] < 0) continue;
            int steps = 0;
            for (int g = gradientHref[i]; g >= 0; g = gradientHref[g])
            {
                if (g == i) throw new Outside($"has a reference cycle through <{nodes[g].Name}>.");
                if (++steps > limits.SvgGradientChain)
                    throw new Outside($"has a gradient that inherits through more than {limits.SvgGradientChain} href references.");
            }
            chain[i] = steps;
        }
        foreach (var (from, to) in paints) weight[from] += 1 + chain[to];

        var state = new byte[n]; // 0 unvisited, 1 on the path, 2 done
        var size = new long[n];
        var height = new int[n];
        var stack = new Stack<(int Node, int Next)>();
        stack.Push((0, 0));
        state[0] = 1;
        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();
            var list = edges[node];
            if (list is not null && next < list.Count)
            {
                stack.Push((node, next + 1));
                var to = list[next].To;
                if (state[to] == 1) throw new Outside($"has a reference cycle through <{nodes[to].Name}>.");
                if (state[to] == 0)
                {
                    state[to] = 1;
                    stack.Push((to, 0));
                }
                continue;
            }
            long total = Math.Min(cap, weight[node]);
            int deepest = 0;
            if (list is not null)
                foreach (var (to, counts) in list)
                    if (counts)
                    {
                        total = Math.Min(cap, total + size[to]);
                        deepest = Math.Max(deepest, height[to]);
                    }
            size[node] = total;
            height[node] = deepest + 1;
            if (height[node] > limits.SvgDrawnDepth)
                throw new Outside($"draws more than {limits.SvgDrawnDepth} levels deep (nested elements and the use, clip-path and mask references they draw through).");
            state[node] = 2;
        }
        return size[0];
    }

    /// <summary>The canonical document: unprefixed SVG elements, attributes in a fixed order, references as <c>xlink:href</c>.</summary>
    private static byte[] Emit(List<Node> nodes)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.Entitize,
            CheckCharacters = true,
        };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            var depth = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                for (; depth >= node.Depth; depth--) w.WriteEndElement();
                w.WriteStartElement(node.Name, SvgNamespace);
                if (i == 0) w.WriteAttributeString("xmlns", "xlink", null, XLinkNamespace);
                if (node.Id is not null) w.WriteAttributeString("id", node.Id);
                foreach (var attr in GeometryOrder)
                    if (node.Attrs.TryGetValue(attr, out var v)) w.WriteAttributeString(attr, v);
                foreach (var attr in PresentationOrder)
                    if (node.Attrs.TryGetValue(attr, out var v)) w.WriteAttributeString(attr, v);
                if (node.Href is not null) w.WriteAttributeString("xlink", "href", XLinkNamespace, "#" + node.Href);
                if (node.Text is not null) w.WriteString(node.Text.ToString());
                depth = node.Depth;
            }
            for (; depth >= 0; depth--) w.WriteEndElement();
        }
        return stream.ToArray();
    }
}
