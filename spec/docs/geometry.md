# Geometry

Units are pixels of the profile canvas, x to the right, y down. Angles are degrees, clockwise. Every item has its own **local coordinates**, in which its scene draws; the engine maps them onto the canvas with the item's placement and those of the groups it is in.

## Placement (applied by the engine)

```jsonc
"placement": { "x": 640, "y": 80, "rotation": 15, "pivot": [120, 60] }
```

- `x`, `y`: the item's translation, in the parent's coordinates (the canvas for a top-level item, the group's local coordinates inside a group): where the item's **local origin (0, 0) sits before rotation**. With `rotation` 0 the origin is at (`x`, `y`); with a rotation the item turns about its pivot, which stays at (`x` + pivot x, `y` + pivot y), so the origin moves unless it is the pivot. This is 1.x's X/Y with rotation about the centre. The origin is the item's anchor: content that grows (automatic text, glow) grows from it as the scene lays it out, so a right-aligned automatic-width text whose scene ends at x = 0 stays right-aligned on `x` whatever its text.
- `rotation` (default 0): degrees clockwise about `pivot`.
- `pivot`: a point in local coordinates. Absent, it is the centre of the layout box on each axis where the box has a size, and 0 where the dimension is automatic; for a group it is (0, 0). A pivot doesn't move when content changes, so a rotated item never wobbles as its text changes.

The item's transform is `T(x, y) · R(rotation, pivot)` with `R(θ, p) = T(p) · R(θ) · T(−p)`, that is, for a local point q:

```
parent(q) = (x, y) + p + R(θ) · (q − p)        R(θ) · (u, v) = (u·cos θ − v·sin θ, u·sin θ + v·cos θ)
```

(y points down, so a positive θ turns clockwise on screen). Its world transform is its parents' world transforms times its own. Placement has no scale: a resize changes the layout box.

Worked example: `{ "x": 100, "y": 50, "rotation": 90, "pivot": [20, 10] }`, so cos θ = 0, sin θ = 1 and R(θ) · (u, v) = (−v, u):

| Local point q | q − p | R(θ) · (q − p) | Parent point |
|---|---|---|---|
| (20, 10), the pivot | (0, 0) | (0, 0) | (120, 60) = (x, y) + p |
| (0, 0), the origin | (−20, −10) | (10, −20) | (130, 40) |
| (40, 10) | (20, 0) | (0, 20) | (120, 80): right of the pivot turns to below it |

With `rotation` 0 the same item has its origin at (100, 50) and the pivot at (120, 60).

A client without any knowledge of how a scene was produced can move, rotate, rebind, hide, reorder and remove every item, because all of those are placement, bindings, attributes or tree position, never the scene.

## Layout box (an input to the scene)

```jsonc
"layout": { "w": 240, "h": 240 }
```

The layout box is (0, 0)–(w, h) in local coordinates. The engine doesn't draw it or clip to it: the scene reads it with `{"op": "box", "field": "w"}` and lays itself out in it. An absent dimension is **automatic**: the content decides it (a text that is as wide as it measures), and `box` reads 0 for it.

The scene also reads the item's **rotation on the canvas** with `{"op": "box", "field": "rotation"}`: in degrees, its own placement `rotation` plus those of the groups it is in, not normalised (an item at 350 in a group at 15 reads 365), the same number its bounds report as `rotation` (below). It leaves out the scene's own group transforms, which the scene knows, and the output scale, which doesn't turn anything. Like `w` and `h` it is read once per tick from the document the tick draws, so a turn shows at the next tick and keeps the item's state ([identity](identity.md)). A scene uses it to keep part of its drawing level or upright while the item turns, such as a gradient that stays level with the canvas ([paints](scene.md#paints)).

Resizing an item changes `layout` (with `setDesign`, [commits](commit.md)). A scene that lays itself out from `box` follows the new size at once; a client that bakes sizes into the scene sends a new scene with the new layout in the same op.

## Measured bounds (reported by the engine)

The engine measures every item each frame, from the same tick it draws ([identity](identity.md)), and reports it with the frame (`urn:infopanel:api:1#/$defs/itemBounds`, in the frame's header, [API](api.md#frames)):

```jsonc
{ "id": "cpu-bar", "frame": { "x": 40, "y": 100, "w": 240, "h": 20 },
  "ink": { "x": 39.5, "y": 99.5, "w": 241, "h": 21 }, "rotation": 15 }
```

- **Content box**: the union, in local coordinates, of the outlines of every visible node, strokes included, effects not.
- **Frame**: the layout box, with each automatic dimension replaced by the content box's extent on that axis (so an automatic frame can start left of or above the origin). Editors put selection and resize handles on the frame.
- **Ink**: the content box grown by each effect's reach (3 sigma for blur and glow, offset plus 3 sigma for shadow), so everything the item changes on the canvas. Editors hit-test against frame ∪ ink.
- Both are rectangles in local coordinates mapped by the item's world transform, which is a rotation and a translation. They are reported as the rectangle before rotation, in canvas pixels, and the world `rotation`; each rotates about **its own centre**. (For a local rectangle r: its centre c goes to W(c); report x = W(c).x − w/2, y = W(c).y − h/2, the same w and h, and the sum of the rotations on the way down.)
- A group's frame and ink are the axis-aligned unions, in the group's local coordinates, of its visible children's (rotated) frames and inks, reported with the group's world rotation. A group with no visible children has no bounds.
- Bounds are not clipped to the canvas, so an item that is partly off the canvas can still be selected and moved back.
- An item drawn as a placeholder has `problem` set ([unsupported content](unsupported.md)); its frame is its layout box, 100 × 100 where a dimension is automatic.

## Groups and reparenting

A group's placement moves and rotates all its children as one. Its children's placements are in the group's local coordinates.

`move` (into, out of or between groups) keeps the item's placement numbers, so its local position is kept and it may jump on the canvas. To keep it where it is on the canvas, send the new placement in the same op (`move`'s `placement`), computed from the two world transforms: with W_from and W_to the world transforms of the old and new parent and p the item's pivot,

- `rotation' = rotation + rotation(W_from) − rotation(W_to)`
- `(x', y') = W_to⁻¹ · W_from · (x + p.x, y + p.y) − p`

which keeps the pivot's world position and the item's world rotation. Grouping a selection is one commit: insert a group with the placement the client chooses (for example the selection's top-left), then move each item into it with its converted placement (`examples/commit/valid/group-selection.json`).

## Coordinate range

An engine draws in 32-bit precision ([scene](scene.md#drawing-model)), so every number a scene draws with (a coordinate, size, radius, transform value, path or polyline number, stroke width, dash length or effect offset) is clamped to ±10⁹ on its own before drawing, and bounds are measured from the clamped values. A rectangle at x = −1.5 × 10⁹ that is 3 × 10⁹ wide is drawn from −10⁹ and 10⁹ wide, so it ends at 0. Content meant to be seen stays far inside the range: canvases are at most 16,384 pixels a side.

## Clipping

- Nothing an item draws is clipped to its layout box unless the scene clips it (a group's `clip`, a `series` box, a text node's overflow).
- Everything is clipped to the canvas.
- Groups (items) never clip their children.

## Drawing order

Items are drawn in document order, depth first: `items[0]` first (at the back), a group's children in their order at the group's place. A group item doesn't composite its children as a layer: they draw straight onto the canvas, as if they were at the top level. Hidden items, and everything in a hidden group, are not drawn and have no bounds.
