# Smart nodes

Four nodes do work no combination of shapes can: shaping text, drawing history, laying out tables and showing media. Each is defined so that any engine draws the same thing from the same inputs.

## `text`

Example: `examples/scene/valid/text-layout.json`.

| Member | Meaning |
|---|---|
| `text` | A string expression, or 1–32 **spans** `{ "text", "font", "fill", "underline", "strike" }`, each overriding the node's font fields and paint, laid out as one run (a value and its smaller unit). |
| `font` | `family` (static), `size` (pixels, the em size, clamped to 1–2048), `weight` (1–1000, default 400; the nearest available face), `italic`. A family the engine doesn't have falls back to its default family (this engine: Inter) and is reported; characters a face lacks fall back per character. |
| `fill`, `stroke` | Paints, as for shapes (default fill black). |
| `x`, `y` | The anchor. |
| `w`, `h` | The box. Without `w` the text is one line (or its explicit `\n` lines) as wide as it measures; without `h` as tall as its lines. |
| `align` | `start` (default), `center`, `end`. With `w`, within the box; without it, which point of the text `x` names (its start, centre or end). |
| `valign` | `top` (default), `middle`, `bottom`, `baseline`. With `h`, within the box; without it, which part of the text block `y` names (`baseline`: the first line's baseline). |
| `wrap` | `none` (default) or `word` (needs `w`): break lines at word boundaries, a word wider than the box at characters. |
| `maxLines` | At most this many lines (1–1000). |
| `overflow` | When the text is larger than the box: `visible` (default; drawn past it), `clip` (to the box; to the lines' height where `h` is absent), `ellipsis` (the last line that fits ends with "…"), `marquee` (below). |
| `marquee` | `speed` (pixels a second, at least 1, default 30) and `gap` (pixels, default 40). With `overflow: marquee` and a `w`: when the single-line text is wider than `w`, it scrolls left, drawn at `x − o` and `x − o + width + gap`, where o = (elapsed × speed) mod (width + gap) and elapsed is the tick's `elapsed`, clipped to the box; when it fits, it is drawn as `align` says. |
| `fit` | `{ "min" }` (needs `w`): the largest font size from `font.size` down to `min`, in steps of 0.5 px, at which the text fits the box (`w`, and `h` when set); every span shrinks in proportion. |
| `lineHeight` | A multiple of the font size; absent, the font's own line spacing. |
| `letterSpacing` | Extra pixels after each character (default 0). |
| `underline`, `strike` | Decorations, in the text's paint. |

Text is laid out once per tick: the layout is part of the tick snapshot, so every scale, output and bounds measurement of the tick uses the same lines, and `measure` reads its size. A text node whose layout reads a measurement is laid out after the nodes it measures (phase 2), and can't itself be measured ([expressions](expressions.md#order-of-evaluation)). Drawn text is at most 4,096 scalar values per string, as every string result is ([expressions](expressions.md#numbers-and-strings)). Text is shaped with the font's kerning and ligatures, left to right (bidirectional text is laid out as the shaping engine does; it is not specified further in version 1).

## `series`

Examples: `graph.json`, `histogram.json`.

A number input's history (newest last) drawn in the box (`x`, `y`, `w`, `h`), clipped to it. The newest sample is at the right edge, each older one `pitch` pixels to the left (at least 0.25, default 1); at most `count` samples (default: as many as fit, floor(w ÷ pitch) + 1). A sample v is at y = bottom − clamp((v − min) ÷ (max − min), 0, 1) × h; when `max` ≤ `min`, at the bottom.

| `mode` | Draws |
|---|---|
| `line` (default) | A polyline through the samples, with `stroke`. |
| `area` | The polyline closed down to the bottom of the box, with `fill`, then the polyline's `stroke` (not the closing edges). |
| `bars` | One bar per sample from the bottom to the sample, `barWidth` wide (default `pitch`), its right edge on the sample's x, with `fill` and `stroke`. |

`min` and `max` are expressions: a fixed range, or an automatic one from `history` and state cells (`graph.json` expands at once and contracts smoothly).

## `table`

Example: `table.json`.

A table input as text. Columns sit side by side from `x`, each `w` wide and `gap` apart; `column` picks the table's column by index (0 first) or by name, and a missing column keeps its width and draws nothing. With `header` (default true) the first row is each column's `title`, or its name, in `headerFont` (overriding `font`) and `headerFill`; then up to `rows` rows (1–100, default 10), each `rowHeight` below the last (default: the font's line height). Each cell is its text, aligned in its column by `align` and cut by `overflow` (`ellipsis` default, `clip`, `visible`) at the column's width. Cells are single lines. A table with no reading draws nothing.

## `media`

Examples: `image.json`, `gauge-sequence.json`, `provider-media.json`.

| Member | Meaning |
|---|---|
| `source` | `{ "asset": "sha256:…" }`: a stored asset. `{ "sequence": [assets], "index", "crossfade" }`: one of several assets, picked by `index` (0 first, clamped to the range): without `crossfade`, frame round(index); with it, frame floor(index) and over it frame ceil(index) at opacity index − floor(index). `{ "provider", "ref" }`: media from a provider configured on the engine (capability `media.provider`); `ref`, a string expression, is passed to it. |
| `x`, `y`, `w`, `h` | The box. An absent `w` or `h` is the media's natural size on that axis, or kept in proportion when the other is given. |
| `fit` | `fill` (default; stretch to the box), `contain`, `cover` (scale keeping proportions), `none` (natural size). |
| `align` | Where contained, covered or natural-size media sits in the box: fractions of the free space, default [0.5, 0.5]. |
| `playback` | For animated media: `loop` (default), `once` (stops on the last frame), `still` (the first frame). The frame shown is the one at elapsed × `speed` (0–16, default 1) seconds into the animation, from the tick's `elapsed`: playback is a function of the tick clock, the same on every output. |
| `sampling` | `linear` (default) or `nearest` (pixel art). |

Formats: PNG (and animated PNG), JPEG, GIF, WebP (still and animated) and SVG (static, self-contained, in the allowlisted profile of [SVG](svg.md): shapes, groups, symbols, `use`, gradients, clip paths and masks; no text, style sheets, images, filters, scripts or references outside the file; sizes in pixels). Every asset is inspected from its header before anything decodes it, and refused if it is over the media limits ([limits](limits.md)). A scene refers to media only by asset hash or through a configured provider, never by file path or URL, so a document is self-contained and an imported one can't reach outside its assets. Media that isn't available (a missing asset, an unconfigured provider, one still loading) draws the engine's missing-media placeholder in the box (100 × 100 where the size is natural) and is reported; the item is not "unsupported".
