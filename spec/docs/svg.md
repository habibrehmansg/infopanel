# SVG profile

An SVG asset ([smart nodes](smart-nodes.md), `media`) is a static, self-contained picture. Engines read only this restricted profile of SVG 1.1 / SVG 2, defined as an **allowlist**: the elements, attributes, `style` properties and references listed here, each value in the grammar given for it, and nothing else. Anything outside it refuses the file when the asset is added, before anything renders it. A file inside it is **rebuilt** into a canonical document ([below](#the-rebuilt-document)), and engines render only that document, never the original text. Examples: `examples/media/valid/*.svg`, `examples/media/invalid/*.svg`.

Unless a row below says otherwise, a file outside the profile is refused with `media.svg-external`.

## Reading

- The file is XML in UTF-8 (a UTF-8 byte order mark is allowed), at most `svgBytes` (1 MiB), checked before any of it is read ([limits](limits.md)). An XML declaration that names another encoding (`encoding="ISO-8859-1"`, `"UTF-16"`, ...) and bytes that aren't valid UTF-8 are `media.svg-parse`.
- The root element is `svg`, unprefixed, in the SVG namespace (`<svg xmlns="http://www.w3.org/2000/svg">`); any other root, a prefixed one (`<s:svg xmlns:s="http://www.w3.org/2000/svg">`) included, is `media.format`.
- A reader stops at element 10,001 (`svgElements`) and refuses the file (`limit.svgElements`) without reading the rest. Elements nest at most 32 deep (`svgDepth`), the root counting 1; reading stops at the first element deeper than that.
- No DTD (`<!DOCTYPE`), and so no entities other than XML's own (`&amp;`, `&lt;`, `&gt;`, `&quot;`, `&apos;`) and character references: `media.svg-parse`, as for XML that isn't well formed.
- No processing instructions other than the XML declaration (`<?xml-stylesheet ...?>` included), and no CDATA sections. Comments are ignored.
- Text only inside `<title>` and `<desc>`, which hold text only (no elements). White space anywhere is ignored.

## Namespaces

Every element is in the SVG namespace (`http://www.w3.org/2000/svg`): the root declares it as the default namespace; elements inside it may also use a prefix bound to it. Namespace declarations may bind only the SVG and XLink (`http://www.w3.org/1999/xlink`) namespaces. Attributes have no namespace, except `xlink:href` (see [references](#references)) and `xml:space`; every other namespaced attribute is refused, `xml:base` and `xml:lang` included.

## Elements

`svg`, `g`, `defs`, `symbol`, `use`, `path`, `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`, `linearGradient`, `radialGradient`, `stop`, `clipPath`, `mask`, `title`, `desc`.

Names are compared with case (`<Rect>` is not `<rect>`). Everything else is refused: `<style>`, `<script>`, `<image>`, `<a>`, `<foreignObject>`, `<filter>` and its primitives, `<pattern>`, `<marker>`, `<switch>`, animation elements and, in this version, `<text>` and `<tspan>`: an SVG renderer resolves fonts outside the engine's font registry, so text would draw differently on each machine. Put text in the scene instead ([smart nodes](smart-nodes.md), `text`).

## Attributes

On any element of the profile:

| Group | Attributes |
|---|---|
| Identity | `id` (an XML name, unique in the file), `class` (allowed and ignored: there is no stylesheet for it to match) |
| Geometry and structure (attribute only) | `x`, `y`, `width`, `height`, `rx`, `ry`, `cx`, `cy`, `r`, `fx`, `fy`, `fr`, `x1`, `y1`, `x2`, `y2`, `d`, `points`, `pathLength`, `offset`, `transform`, `gradientTransform`, `gradientUnits`, `spreadMethod`, `clipPathUnits`, `maskUnits`, `maskContentUnits`, `viewBox`, `preserveAspectRatio`, `version`, `baseProfile` |
| Presentation (attribute or `style` property) | `fill`, `fill-opacity`, `fill-rule`, `stroke`, `stroke-width`, `stroke-opacity`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit`, `opacity`, `color`, `display`, `visibility`, `overflow`, `clip-path`, `clip-rule`, `mask`, `stop-color`, `stop-opacity` |
| Style | `style` (below) |
| References | `href` or `xlink:href` (not both), on `use`, `linearGradient` and `radialGradient` only (below) |
| Namespaces | `xmlns`, `xmlns:*` (SVG and XLink only), `xml:space` |

Names are compared with case. Every other attribute is refused, event attributes (`onload`, `onclick`, ...) included. Dashes are not in the profile: `stroke-dasharray` and `stroke-dashoffset` are refused (as attributes and in `style`), since a dash pattern's cost grows with the stroke's length in user units, which a transform can make arbitrarily large for a small drawing.

### Values

Every value is parsed by the grammar of its attribute or property, white space allowed around it; a value that doesn't parse refuses the file. There is no other syntax: no URIs or functions besides those below, no `inherit`, `var()`, `calc()`, `!important`, escapes or comments.

| Values | Attributes and properties | Grammar |
|---|---|---|
| Number | `pathLength` (≥ 0), `stroke-miterlimit` (≥ 1) | `[+-]? (digits [. digits?] \| . digits) ([eE] [+-]? digits)?`, finite; a length, coordinate, number list, transform argument or path number has a magnitude of at most 10⁹ (`svgNumber`; beyond it the value doesn't parse, it isn't clamped), while an opacity and a colour's components are clamped as their rows below say |
| Length | `x`, `y`, `cx`, `cy`, `fx`, `fy`, `x1`, `y1`, `x2`, `y2`; not negative: `width`, `height`, `r`, `rx`, `ry`, `fr`, `stroke-width` | a number, unitless, in `px` (any case) or a percentage (`50%`); no other unit. The root's `width` and `height` are read as in [size](#size) |
| Opacity | `opacity`, `fill-opacity`, `stroke-opacity`, `stop-opacity`, `offset` | a number or a percentage, clamped to 0–1 |
| Number lists | `points` (an even count), `viewBox` (four, the width and height not negative as written: `-1e-29` is refused, though it would be rebuilt as 0; a value is read as a double-precision number, as decoders read it, so one that reads as zero or negative zero, such as `-1e-999`, is zero) | numbers separated by white space and at most one comma |
| Path data | `d` | SVG path data: commands `M L H V C S Q T A Z` in either case, implicit repeats, the first command a moveto; or empty |
| Transform list | `transform`, `gradientTransform` | `matrix(6)`, `translate(1 or 2)`, `scale(1 or 2)`, `rotate(1 or 3)`, `skewX(1)`, `skewY(1)`, names compared with case, arguments and functions separated by white space or commas |
| Colour | `color`; `stop-color` also takes `currentColor` | `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`/`rgba()` (three numbers or three percentages, optional alpha; comma or space syntax with `/ alpha`), `hsl()`/`hsla()` (hue in degrees, saturation and lightness as percentages, optional alpha), a CSS named colour or `transparent`; keywords and function names in any case |
| Paint | `fill`, `stroke` | `none`, `currentColor`, a colour, or `url(#id)` optionally followed by a colour or `currentColor` as the fallback |
| Reference | `clip-path`, `mask` | `none` or `url(#id)` |
| Keywords (any case) | `fill-rule`, `clip-rule`: `nonzero`, `evenodd`; `stroke-linecap`: `butt`, `round`, `square`; `stroke-linejoin`: `miter`, `round`, `bevel`; `display`: `inline`, `block`, `none`; `visibility`: `visible`, `hidden`, `collapse`; `overflow`: `visible`, `hidden`, `scroll`, `auto` | one keyword |
| Keywords (with case) | `gradientUnits`, `clipPathUnits`, `maskUnits`, `maskContentUnits`: `userSpaceOnUse`, `objectBoundingBox`; `spreadMethod`: `pad`, `reflect`, `repeat`; `preserveAspectRatio`: `none` or `xMinYMin` ... `xMaxYMax`, optionally followed by `meet` or `slice` | as listed |
| Other | `version` (a number such as `1.1`), `baseProfile` (an XML name) | as listed |

`url(#id)` is `url` in any case, then `(`, optional white space, the target optionally quoted (`url("#id")`, `url( '#id' )`), `#` and an XML name, optional white space and `)`.

### `style`

`style` holds `property: value` declarations separated by `;`. Each property (any case) is one of the presentation attributes above, and its value follows its grammar above. Geometry is never a `style` property (`width`, `height`, `x`, `y`, `transform`, ... are refused there), there are no braces, selectors, at-rules (`@`), escapes (`\`) or comments, and a declaration without a value is refused. A `style` property wins over the attribute of the same name, as in CSS; of two declarations of one property, the last wins.

## References

An SVG refers only to its own elements, by fragment:

- `href` / `xlink:href` is exactly `#id` (no white space, no other scheme, `data:` included, and not empty). On `use` it names the element drawn; on a gradient, the gradient it inherits from.
- `url(#id)` in `fill` and `stroke` names a gradient, in `clip-path` a `clipPath`, in `mask` a `mask`.

Every reference resolves to an element of the file of the right kind (a gradient, `clipPath` or `mask` as above; anything for `use`). The references and the element tree together form a graph that has no cycle: an element that draws, through `use`, `clip-path` or `mask` (directly or through anything it contains), itself or an element that contains it is refused, as is a gradient that inherits from itself. A gradient inherits through at most 4 `href` references (`svgGradientChain`) (`#g5` inheriting from `#g4` ... from `#g1` from `#g0` is one too many).

## Expanded size and depth

The element limit applies to the drawn size, before anything decodes the file. An element's expanded size is:

- 1, plus 1 for every 32 numbers (and arc flags) of its `d` or `points` (a path of 100 numbers counts 4);
- plus, for each `fill` and `stroke` that names a gradient, 1 and the number of `href` references the gradient inherits through;
- plus the expanded size of each element in it;
- plus, for each `use`, `clip-path` and `mask` reference it has, the expanded size of the referenced element (each use of a `mask` or `clipPath` draws its content again).

The file's expanded size is its root's, and it is held to `svgElements` (10,000): over it, `limit.svgElements`. So 20 definitions that each draw the previous one twice (2^20 drawn elements from about 60 written) are refused, as is a 100,000-segment path drawn by 50 uses, while a symbol of 20 elements drawn by 50 uses (about 1,100) is not.

The drawn depth is the longest chain of elements drawn inside one another, counting each `use`, `clip-path` and `mask` reference as a level (the root is level 1, a `use` in it level 2, the element it draws level 3). It is at most 64 (`svgDrawnDepth`).

## Size

The asset's size is read from the root `<svg>`'s `width` and `height` attributes only; CSS never sizes it (`width` and `height` are not `style` properties, and there is no stylesheet):

- `width` and `height` are a non-negative number, unitless or in `px` (any case, white space allowed around it): `120`, `80px`, `12.5 PX`, `1e2`. Any other unit (`mm`, `cm`, `in`, `pt`, `pc`, `em`, `ex`, ...), a percentage, a negative or a non-numeric value is unsupported: the file is refused with `media.svg-size`. (Physical units would otherwise let a small number stand for a large picture.)
- Where `width` or `height` is absent, that dimension is the `viewBox`'s width or height; where both it and the `viewBox` are absent, it is 0 (the natural size is unknown, [smart nodes](smart-nodes.md)).
- A root `viewBox` whose width or height is above 0 but rounds to 0 in the rebuilt document (below about 1e-28, [the rebuilt document](#the-rebuilt-document)) is refused with `media.svg-size`: rebuilt, its aspect ratio would be lost.
- The media limits (`mediaSide`, `mediaArea`, and `mediaFrameArea` as for one frame) apply to that resolved size from its numbers as written, before the rebuilt document rounds them: each number is read as a double-precision number, as decoders read it (so `8192.0000000000001` is 8192, and a value that reads as zero or negative zero is zero), and each side is rounded up to whole pixels (`viewBox="0 0 8192.0001 1"` is rebuilt, and its size reported, as 8192 × 1, and is held to 8193 × 1, so it is refused, `limit.mediaSide`), and, where only one of `width` and `height` is given and the `viewBox` has a width and height above 0, also to the size the `viewBox`'s aspect ratio (from its numbers as written, read as double-precision numbers, before rounding) gives the other, the size other renderers draw it at: `width="8000" viewBox="0 0 1 1000"` is 8000 × 1000 and is held to 8000 × 8,000,000 too, so it is refused (`limit.mediaSide`).

## The rebuilt document

An accepted SVG is not decoded as written. The engine rebuilds it from what it parsed, and renders **only** the rebuilt document, so nothing a renderer might read differently from the validator (a value it parses more loosely, an encoding, a namespace prefix) reaches it. The rebuilt document is UTF-8 without a byte order mark or XML declaration, and:

- has an unprefixed `<svg>` root in the SVG namespace, which also declares the XLink namespace; every element is written unprefixed, in the order and nesting of the original;
- has only the allowlisted elements and attributes, in a fixed order: `id`, then geometry and structure, then presentation, then the reference as `xlink:href="#id"`; `class`, `version`, `baseProfile` and `xml:space` are left out (they don't change the drawing), as are empty `transform`s;
- has every value regenerated from its parsed form: numbers with at most 7 significant digits and no exponent (`.5` → `0.5`, `1e1` → `10`), `px` dropped, percentages kept, opacities and offsets as numbers clamped to 0–1; colours as `#rrggbb` when opaque and `rgba(r,g,b,a)` otherwise (named colours, `hsl()` and short forms converted); paint as `none`, `currentColor`, a colour or `url(#id)` with an optional fallback; CSS keywords in lower case; path data re-serialised from its commands (each segment with its command letter: `m0 0 10 10` → `m0 0l10 10`); points and `viewBox` as numbers separated by spaces; transforms as their functions (`translate(4 4) scale(0.5)`);
- has `style` properties written as attributes (the `style` value where both were given);
- keeps the text of `title` and `desc`.

Media validation returns the rebuilt document with its result (`MediaCheck.Sanitized` in the reference engine), and that is what an engine decodes. The probed format (`MediaCheck.Format`) chooses the decoder: a file probed as GIF is decoded as a GIF even if it also looks like XML.
