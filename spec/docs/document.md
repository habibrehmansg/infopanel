# Document

Schema: `urn:infopanel:document:1` (`schemas/document.schema.json`). Example: `examples/document/valid/desk.json`.

A document is one profile: a canvas size, settings, and a tree of items. The engine owns it: it stores the saved version, applies every change (commits, undo, redo, revert) and assigns revisions. A client reads it from the engine (`GET /api/profiles/{id}`, [API](api.md#documents)) and changes it only by committing ops ([commits](commit.md)).

```jsonc
{
  "format": "infopanel.document/1",
  "requires": [],                       // document capabilities beyond 1.0 (versioning.md)
  "id": "3dd19c0e-675a-4591-b096-83417853861f",
  "rev": 42,                            // set by the engine
  "profile": { "name": "Desk", "width": 800, "height": 480, ... },
  "items": [ ... ]                      // drawing order: first at the back
}
```

## Profile

| Member | Meaning |
|---|---|
| `rev` | Set by the engine: the document revision at which a profile field last changed. A `profile` op's `baseRev` is the document revision the client read, not this value: the op conflicts when a profile field it sets changed after that revision ([commits](commit.md)). |
| `name` | 1–256 characters. |
| `width`, `height` | The canvas in pixels, 1–16384. Every output shows this canvas (scaled where it must be); nothing is drawn outside it. |
| `background` | `#RRGGBB` or `#RRGGBBAA`; every frame starts as this colour (default opaque white). A transparent background lets a desktop window show what is behind it. |
| `statsOverlay` | Draw the engine's frame-rate overlay on every output (default false). |
| `locale` | The regional format numbers and dates are drawn in, a BCP 47 tag such as `de-CH` ([expressions](expressions.md#regional-format)). Absent, the PC's regional format. Set it so a shared profile looks the same on every PC. |
| `window` | The profile's desktop window ([below](#the-desktop-window)). |
| `triggerApps` | Process names whose being in front should bring the profile up. Stored and kept; acted on by engines that support it. |
| `guides` | Editor-only guide lines (below). |
| `editor` | An opaque object for client data kept with the document, such as defaults for new items. The engine stores and returns it as the same JSON value and never interprets it (at most 64 KiB). |

### The desktop window

While `window.enabled` is true the profile is shown in a borderless desktop window of its own, at one frame pixel per pixel of the display the window is on: the window is `width` × `height` of that display's pixels. A canvas of more than 16,777,216 pixels is over the frame cap ([limits](limits.md#run-time-limits)): it is drawn at the largest scale within the cap and stretched over the window. Its members:

- `x`, `y`: the window's top-left, in pixels of the matched display, from that display's top-left (default 0, 0).
- `display`: the display it belongs on. `name`, and `width` × `height`, the display's size in its own pixels, identify it. `display.x` and `display.y` are the display's top-left in the desktop's own coordinates, the system's layout of every display (on macOS these are points, which differ from pixels on a scaled display); they only tell apart displays with the same name and size. `GET /api/displays` lists the computer's displays, each in this shape ([API](api.md#displays)).
- Matching: a display with `display`'s name and size matches exactly; of several, the one whose top-left is at `display.x`, `display.y`, else the first of them. With no exact match the window is hidden when `strict` is true (default false); otherwise it goes on the first display with that name, else the first with that size, else the main display. Without `display` the window is on the main display. Displays are matched again whenever they change.
- `topmost` (default false) keeps the window above others; `draggable` and `resizable` (default true) let a user move and resize it.

A user's drag of the window is an ordinary undoable change of `window`: its `x`, `y` and the `display` it ended on (name, size and top-left as above). A resize changes `width` and `height`, and `x`, `y` where the top-left moved.

### Guides

`guides` are lines an editor shows and snaps to: `{ "axis": "x", "at": 400 }` is a vertical line at x = 400 across the canvas, `axis: "y"` a horizontal one. They are document content: saved with the document, changed by a `profile` op and undone like any other change. **They are never drawn on any output**, the engine's previews and thumbnails included; a client draws them over the engine's frames itself. (A guide is not an item, so it has no placement, scene or bounds.)

## Items

An item is a `scene` item, which draws a [scene](scene.md), or a `group` item, which holds other items. Both have:

| Member | Meaning |
|---|---|
| `kind` | `scene` or `group`. |
| `id` | 1–64 characters of `A–Z a–z 0–9 _ -`, unique in the document. Chosen by whoever creates the item (a client can create ids before the engine answers, so an insert can be retried safely) and never changed. A copy gets a new id. |
| `rev` | Set by the engine (ignored when a client inserts): the document revision at which the item last changed. |
| `name` | For people (layer lists); never drawn. |
| `visible` | `false` hides the item (and everything in a group). Default true. |
| `locked` | A hint to editors not to move or change the item. The engine doesn't enforce it. |
| `placement` | Where the engine puts the item ([geometry](geometry.md)). |
| `origin` | Opaque (below). |

A scene item also has:

| Member | Meaning |
|---|---|
| `scene` | What it draws. Kept as the JSON value received (stored in the canonical form, [below](#size-and-nesting)); checked on its own ([unsupported content](unsupported.md)). |
| `layout` | Its layout box `{ "w", "h" }`; an absent dimension is automatic ([geometry](geometry.md)). |
| `bindings` | The sensor each scene input reads ([bindings](bindings.md)). |
| `source` | How the client produced the scene (below). |

A group item has `children`, in drawing order. Items nest at most 8 levels (`itemDepth`): a top-level item is at level 1 and a group's children one level below the group, so an item may be inside at most seven groups.

### View state is not document content

Nothing about how a client shows the document is part of it: which items are selected, which groups are expanded in a layer list, the zoom, the scroll position. Changing them is never a commit and never an undo step. The one view-adjacent exception is `guides`, which belong to the design.

## Source and origin

The engine never interprets an item's scene source: a client keeps there whatever it needs to produce the scene again (for example after the user changes a setting). Only the envelope is specified:

```jsonc
"source": { "format": "example.widget/1", "data": { ... }, "deps": ["sha256:…"] }
"source": { "ref": "c1c2b7f0-opaque-reference", "deps": [] }
```

- `format` (1–128 characters) names the client's own description in `data` (any JSON). Clients should namespace it so that two clients' formats can't be confused.
- `ref` instead is **an opaque reference the engine never interprets**: the description is kept elsewhere and the document holds only the reference. An engine treats such an item like any other: it draws the scene and lets clients place, rotate, rebind, hide and remove it. A client that can't resolve the reference can still do all of that, but has no description to produce a new scene from.
- `deps` lists stored blobs (by `sha256:` id) that the description needs, each once; they have no count of their own, since `sourceBytes` bounds them ([limits](limits.md#document-limits)). The engine keeps every blob that a retained version of the document (the current one, the saved one, any in the undo history) refers to, through `deps` or through the scene's media, and may delete the rest.
- `data` together with the envelope is at most 256 KiB.

An item without a `source` has a scene that stands alone: a client that edits it edits the scene itself.

`origin` is an opaque object (at most 4 KiB) in which a client records where the item came from: created locally, imported from another format, copied from somewhere. The engine stores it, keeps it when the item is copied and never interprets it.

## Revisions

The document has one revision counter, `rev`. It goes up with every change: each commit that changes the document, and each undo, redo and revert that takes a step ([commits](commit.md#what-a-change-stamps)). An item's `rev` (and the profile's) is the document revision at which it last changed, so after a commit every item it changed has the same new `rev`. The engine also tracks, per item, the revision at which each field group last changed (placement, design, bindings, name, visible, locked, position in the tree, and for a group its children: an item added to it or taken out of it); a commit's `baseRev` is checked against those ([commits](commit.md)).

Revisions belong to one lifetime of the document in the engine, named by its tag (`instance`, [API](api.md#a-documents-tag)), which is new every time the engine creates or opens the document: after a restart, or once the profile is deleted and created again under the same id, revisions read before mean nothing, and a commit that names an earlier tag is refused. Unsaved changes do not survive the engine stopping; the saved document does. After a power loss each stored file is its previous version or its new one, never a mix of the two.

## Content identity

Every stored blob (media assets and other files) is named by the SHA-256 of its bytes, `sha256:` followed by 64 lower-case hex digits.

## Size and nesting

A document is at most 16 MiB with at most 2,000 items nested at most 8 levels (a top-level item at level 1) and at most 32 bindings per item; see [limits](limits.md#document-limits) for these and the aggregate limits across its scenes. Its size, and that of its scenes, sources, origins and editor data, is measured on its canonical form ([sizes](limits.md#sizes)), however it was sent; its own size leaves out its `rev` members.

The engine stores a document in that form, without its `rev` members, so its stored size is its measured size, and exports a document it can open in the same form. A stored document in another form (written by hand, say) is read the same way, and its next save writes the canonical form.

A document stored before sizes were measured this way is measured the same way, so it can measure larger than it was stored and no longer open: an origin holding 200 numbers written `1e20` is 1,007 bytes as stored and 4,407 in the canonical form, over `originBytes`, and its document is `unreadable`. The formats are a draft until version 1 first ships ([versioning](versioning.md)), so this narrowing is allowed; after that, no limit is measured differently within a major version.
