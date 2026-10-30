# The scene format in short

A profile is a canvas (`width` × `height` pixels, a `background` colour) holding items. An item sits at
`placement` `{x, y}` (its top-left corner on the canvas, optionally `rotation` in degrees), may have a layout box
`layout` `{w, h}`, and draws a **scene**: data, not code. A scene is

```json
{
  "format": "infopanel.scene/1",
  "inputs": { "v": { "type": "number", "label": "Value" } },
  "values": { "pct": { "op": "clamp", "args": [{ "op": "input", "name": "v" }, 0, 100] } },
  "nodes": [
    { "kind": "rect", "w": 300, "h": 120, "r": 12, "fill": "#141414ff" },
    { "kind": "text", "x": 16, "y": 16, "text": "CPU", "font": { "family": "Inter", "size": 20, "weight": 700 }, "fill": "#8a8a8aff" },
    { "kind": "text", "x": 16, "y": 48,
      "text": { "op": "concat", "args": [{ "op": "format", "value": { "op": "value", "name": "pct" } }, "%"] },
      "font": { "family": "Inter", "size": 40, "weight": 700 }, "fill": "#ffffffff" }
  ]
}
```

- **Coordinates** are the item's own, in pixels, from its top-left corner; y grows downwards.
- **Nodes** draw in order, the first at the back: `rect` (`x`, `y`, `w`, `h`, `r` corner radius), `ellipse` (`cx`, `cy`,
  `rx`, `ry`), `arc` (`cx`, `cy`, `r`, `inner`, `start`, `sweep` in degrees, clockwise), `line` (`x1`, `y1`, `x2`, `y2`,
  needs `stroke`), `path` (`d`, SVG path data), `polyline` (`points`), `text`, `series` (a graph of an input's
  history), `table`, `media` (a picture), and `group` (`children`, `transform`, `clip`). Every node may have `id`,
  `visible` and `opacity`.
- **Paint**: `fill` is a colour (`#rrggbb` or `#rrggbbaa`) or a gradient (`linear`, `radial`, `sweep`); `stroke` is
  `{ "paint", "width" }`.
- **Text**: `text`, `font` (`family`, `size`, `weight`, `italic`), `fill`, `x`, `y`, and a box `w`, `h` with `align`,
  `valign`, `wrap`, `maxLines`, `overflow` (`visible`, `clip`, `ellipsis`, `marquee`) and `fit` (`{ "min" }`, shrink to
  fit the box). Without `w` the text is as wide as it measures.
- **Pictures**: a `media` node shows a stored asset, `{ "kind": "media", "x", "y", "w", "h", "source": { "asset":
  "sha256:…" } }`, with `fit` (`fill`, the default, stretches to the box; `contain`, `cover`, `none`). `add_asset` stores an SVG and answers its id. An SVG
  may hold shapes, groups, `use`, gradients, clip paths and masks, sizes in pixels; no text, style sheets, images,
  filters, scripts or references outside the file.
- **Sensors**: a scene declares `inputs` (`number`, `text` or `table`); the item's `bindings` map each input to a sensor
  key from `list_sensors`, e.g. `"bindings": { "v": { "key": "plugin:/…/cpu/temperature" } }`. Read an input with
  `{ "op": "input", "name": "v" }` (fields `value`, `now`, `min`, `max`, `avg`, `unit`, `text`, `status`), and its recent
  samples with `{ "op": "history", "input": "v", "fn": "last", "count": 1 }`.
- **Expressions** are objects with an `op`: arithmetic (`add`, `sub`, `mul`, `div`, `clamp`, `norm`, `lerp`, `round`, …),
  comparisons and `if`, `concat`, `format` (a number as text, with `decimals`), `formatTime` (the clock as text, e.g.
  `"pattern": "HH:mm"`), `time`, `box` (the item's layout box: `{ "op": "box", "field": "w" }`), `steps` (thresholds to
  colours or texts), `value` (a named value) and `state` (a cell that keeps its value between ticks, for smoothing).
- **The box**: a scene that reads `box` follows its item's `layout` when the item is resized; one that doesn't is drawn
  at its own size.
- **The preview's checks**: `render_preview` lists text that doesn't fit its box (give text a `w` and, for a value
  that grows, `fit` or `overflow: ellipsis`), items whose drawing passes the canvas edge, items whose boxes overlap
  (a card drawn before its content is fine), and solid-colour text with low contrast against what is under it. Text
  filled with a gradient isn't checked. They are heuristics: look at the picture too.

The full format is in the engine's `spec/docs` (`scene.md`, `smart-nodes.md`, `expressions.md`, `bindings.md`).
