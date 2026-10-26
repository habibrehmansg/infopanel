# Scene

Schema: `urn:infopanel:scene:1` (`schemas/scene.schema.json`). Examples: `examples/scene/valid/`.

A scene is what one item draws: a tree of nodes in the item's local coordinates. It is data, not code: every number, colour, text and condition in it is a literal or a typed [expression](expressions.md) over the item's inputs, the clock, its layout box, named values and state cells. The engine evaluates a scene once per tick and draws it at every scale and on every output from that one evaluation ([identity](identity.md)).

```jsonc
{
  "format": "infopanel.scene/1",
  "requires": [],                          // capabilities beyond 1.0 (versioning.md)
  "inputs": { "v": { "type": "number" } }, // what the item binds (bindings.md)
  "values": { "pct": { ... } },            // named expressions
  "state": { "shown": { "init": ..., "next": ... } },
  "nodes": [ ... ]                         // drawn in order, first at the back
}
```

## Drawing model

- Nodes draw in order (painter's algorithm), each onto what is below with source-over unless a group says otherwise.
- Colours are sRGB with straight (not premultiplied) alpha in the format; blending and gradients interpolate in sRGB (not linear light), premultiplied, as common 2D renderers do.
- Geometry is antialiased. Coordinates are not snapped: a 1 px line at an integer x covers two half pixels, at x + 0.5 one pixel.
- Numbers are 64-bit floating point while evaluated; an engine may draw with 32-bit precision. A value that isn't finite reads as 0 ([expressions](expressions.md)).
- Out-of-range values are clamped where a member says so (opacity 0–1, sigma 0–64, sweep −360…360, …) and otherwise draw what they say: a rectangle with a negative width or height, an ellipse with a radius at or below 0 and an arc with `inner` ≥ `r` draw nothing.
- Every node has an optional `id` (its instance id, unique in the scene), `visible` (bool, default true) and `opacity` (0–1, default 1).

## Nodes

| `kind` | Geometry members | Paint members |
|---|---|---|
| `group` | `children`; optional `transform`, `clip`, `effects`, `blend` | — |
| `rect` | `x`, `y` (default 0), `w`, `h`, `r` (one radius, or [top-left, top-right, bottom-right, bottom-left]; each clamped to half the shorter side) | `fill`, `stroke` |
| `ellipse` | `cx`, `cy` (default 0), `rx`, `ry` | `fill`, `stroke` |
| `arc` | `cx`, `cy`, `r`, `inner` (default 0), `start` (default 0; 0° along +x), `sweep` (clockwise when positive), `close`: `sector` (the region between `inner` and `r`: a ring segment, or a pie when `inner` is 0) or `open` (the curve at `r`, for stroking) | `fill`, `stroke` |
| `line` | `x1`, `y1`, `x2`, `y2` | `stroke` (required) |
| `path` | `d`: static SVG path data (M L H V C S Q T A Z, absolute and relative); `fillRule` `nonzero` (default) or `evenodd` | `fill`, `stroke` |
| `polyline` | `points`: [x, y] pairs that may be expressions; `closed` (default false); `fillRule` | `fill`, `stroke` |
| `text`, `series`, `table`, `media` | See [smart nodes](smart-nodes.md) | |

A shape with neither `fill` nor `stroke` draws nothing (but still has a place in the content box). An open polyline or arc can still be filled; it is closed for filling.

A `path` is static: move, rotate or scale it with a group's `transform`. For outlines that follow values, use `polyline`, `arc` and `rect` with expressions, or a stroke's `trimStart`/`trimEnd` along a static path.

## Paints

A paint is a colour (a literal `#RRGGBB`/`#RRGGBBAA` or a colour expression) or a shader object. Shader geometry is in the node's local coordinates (an expression over `box` makes it follow the layout box):

| `type` | Members |
|---|---|
| `linear` | `x1`, `y1`, `x2`, `y2`, `stops`, `spread` |
| `radial` | `cx`, `cy`, `r`, `stops`, `spread` |
| `sweep` | `cx`, `cy`, `start` (default 0), `end` (default start + 360), `stops`, `spread`; angles as for arcs |
| `compose` | `blend`, `dst`, `src`: two paints combined per pixel, `src` blended onto `dst`, then drawn as one paint (two perpendicular linear gradients multiplied make a diamond) |

`stops` are 2–16 `{ "at", "color" }`, used in the order given, `at` clamped to 0–1; both may be expressions, so a gradient can move with time or a value. `spread` says what lies outside 0–1: `pad` (default: the end colours), `repeat`, `reflect`. Animated gradients are expressions over `{"op": "time", "field": "elapsed"}`: a rotating sweep, a pulsing radial, a moving linear (`examples/scene/valid/shapes.json`).

## Strokes

`{ "paint", "width", "cap", "join", "miterLimit", "dash", "dashOffset", "trimStart", "trimEnd" }`: a stroke centred on the outline, `width` pixels wide (0 or less draws nothing), with caps `butt` (default), `round` or `square`, joins `miter` (default, limit 4), `round` or `bevel`, optional dashes (2–16 lengths, alternating dash and gap) starting `dashOffset` into the pattern, and trimmed to the part of the outline between the fractions `trimStart` (default 0) and `trimEnd` (default 1) of its length, before dashing. A trimmed open arc is a progress ring; a dashed one a segmented gauge.

## Groups

A group draws its children in its own coordinates, applying in this order:

1. `transform`: up to 8 steps, the first outermost (as in an SVG `transform` list): `translate` (x, y), `rotate` (angle about cx, cy), `scale` (x, y about cx, cy; a negative factor mirrors), `skew` (x and y angles, each clamped to −89…89) and `matrix` (static [a, b, c, d, e, f]: x' = a·x + c·y + e, y' = b·x + d·y + f).
2. `clip`: children are drawn only inside a `rect` (with radii), `ellipse`, `arc` sector or `path`, antialiased, in the group's coordinates after the transform.
3. The children.
4. `effects`, up to 4, in order, on the composited children: `blur` (Gaussian, `sigma`), `shadow` (the children's coverage filled with `color`, offset by `dx`, `dy`, blurred by `sigma`, drawn under them) and `glow` (a shadow without offset). Sigma is clamped to 0–64 pixels; a blur reaches 3 sigma. Effect parameters are in the group's own coordinates, after its transform, so a shadow's offset (and every sigma) turns and scales with the group. Effects act on the clipped children but are not clipped by the group's own `clip`: a glow or shadow extends past it.
5. `opacity` and `blend` when compositing the result onto what is below. `blend` is one of the W3C Compositing and Blending modes (`normal` default, `multiply`, `screen`, `overlay`, `darken`, `lighten`, `color-dodge`, `color-burn`, `hard-light`, `soft-light`, `difference`, `exclusion`, `hue`, `saturation`, `color`, `luminosity`) or `plus` (adds the colours).

A group with effects, opacity below 1 or a blend other than `normal` is drawn as a layer (an engine may skip the layer when the result is the same). Effects cost more than shapes; their cost counts against a scene's budget ([limits](limits.md)).

## Order of evaluation

Once per tick, in two phases ([expressions](expressions.md#order-of-evaluation)): the inputs are read (and frozen for the tick); phase 1 evaluates the values that don't depend on `measure` (around the state cells) and lays out every text node whose layout doesn't depend on a measurement; phase 2 evaluates the values that depend on measurements, lays out the text nodes that read them, and evaluates everything else. Nodes are drawn after that. [Expressions](expressions.md) gives the rules that make this order well defined (a measured text node's layout can't depend on a measurement, `cycle.measure`); [identity](identity.md) what carries over from tick to tick.

## Limits

A scene is at most 1 MiB of JSON with at most 2,000 nodes nested at most 24 deep, and more limits on expressions, paths, text, media and effects; see [limits](limits.md).
