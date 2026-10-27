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
| `window` | The profile's desktop window: `enabled`, `x`/`y` (pixels from the top-left of the matched display), `display` (name and size in pixels; `x`/`y` only tell equal displays apart), `strict` (hide the window when no display matches), `topmost`, `draggable`, `resizable`. A user's drag or resize of the window is an ordinary undoable change of these fields. |
| `triggerApps` | Process names whose being in front should bring the profile up. Stored and kept; acted on by engines that support it. |
| `guides` | Editor-only guide lines (below). |
| `editor` | An opaque object for client data kept with the document, such as defaults for new items. The engine stores and returns it unchanged and never interprets it (at most 64 KiB). |

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
| `scene` | What it draws. Kept as received; checked on its own ([unsupported content](unsupported.md)). |
| `layout` | Its layout box `{ "w", "h" }`; an absent dimension is automatic ([geometry](geometry.md)). |
| `bindings` | The sensor each scene input reads ([bindings](bindings.md)). |
| `source` | How the client produced the scene (below). |

A group item has `children`, in drawing order. Groups nest (at most 8 levels in all).

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
- `deps` lists stored blobs (by `sha256:` id) that the description needs. The engine keeps every blob that a retained version of the document (the current one, the saved one, any in the undo history) refers to, through `deps` or through the scene's media, and may delete the rest.
- `data` together with the envelope is at most 256 KiB.

An item without a `source` has a scene that stands alone: a client that edits it edits the scene itself.

`origin` is an opaque object (at most 4 KiB) in which a client records where the item came from: created locally, imported from another format, copied from somewhere. The engine stores it, keeps it when the item is copied and never interprets it.

## Revisions

The document has one revision counter, `rev`. It goes up with every change: each commit that changes the document, and each undo, redo and revert that takes a step ([commits](commit.md#what-a-change-stamps)). An item's `rev` (and the profile's) is the document revision at which it last changed, so after a commit every item it changed has the same new `rev`. The engine also tracks, per item, the revision at which each field group last changed (placement, design, bindings, name, visible, locked, position in the tree, and for a group its children: an item added to it or taken out of it); a commit's `baseRev` is checked against those ([commits](commit.md)).

Revisions are meaningful within one run of the engine: `GET /api/session` reports an `instance` id that changes every time the engine starts, and a commit that names an old `instance` is refused. Unsaved changes do not survive the engine stopping; the saved document does.

## Content identity

Every stored blob (media assets and other files) is named by the SHA-256 of its bytes, `sha256:` followed by 64 lower-case hex digits. Documents and items have a content hash too: the SHA-256 of their JSON in RFC 8785 canonical form (JCS) with the revision metadata removed. That is exactly the `rev` member of the document, of `profile` and of every item (groups' children included), and nothing else: a member named `rev` inside an opaque member (`scene`, `source`, `origin`, `editor`) is content and is hashed. Two items with the same hash are the same item content, whoever wrote them; an engine API that reports hashes uses this definition.

## Size and nesting

A document is at most 16 MiB of JSON with at most 2,000 items, groups nested at most 8 levels and at most 32 bindings per item; see [limits](limits.md) for these and the aggregate budgets across its scenes.
