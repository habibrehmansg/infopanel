# AI apps elsewhere: the engine's side

AI apps reach InfoPanel through the configured service: the paired client turns on **AI apps elsewhere** (the
`service.aiApps` setting), and the engine keeps one connection open to the service's relay ([relay.md](relay.md)). The
service speaks MCP to the AI apps and supplies the tools' descriptions, the instructions and the design guide; the
engine runs the tools below on that connection's calls, under its own rules ([below](#how-it-works)). The engine's list
of the tools (names and input schemas, no wording) is committed as `InfoPanel.Tests/Snapshots/mcp-tools.json`.

## The tools

| Tool | Scope | What it does |
|---|---|---|
| `list_profiles` | read | The profiles: id, name, size, status, unsaved changes |
| `get_profile` | read | A profile's name, size, revision and items (position, box, visibility, bindings, what made each; listed within 4 MiB, then "… and N more items"); with `item`, that item's scene and source |
| `list_sensors` | read | The sensors: key, name, group, unit, status, value; `query` filters, `limit` caps (200) |
| `list_fonts` | read | The font families a scene's text can name, the bundled ones (Inter, DSEG7 Classic) first and marked; `query` keeps those whose name holds it, in any case |
| `render_preview` | read | A profile drawn as a PNG, `width` pixels wide (the profile's, at most 1,200), and the checks on the same tick (below), of the profile as it is now (the live frame's tick when it shows the newest change; what the live output holds back over the process budget is held back here too) |
| `create_profile` | edit | An empty profile (`name`, `width`, `height`, `background`), stored and saved at once |
| `add_item` | edit | An item drawing a `scene`, at `x`, `y`, with an optional box (`width`, `height`), `name`, `bindings`, `group`, `index`; from elsewhere, a design the service produced may stand for the scene and box ([relay.md](relay.md#designs)) |
| `add_items` | edit | Up to 50 items (`items`, each as `add_item` takes one, without `profile`, or a group: `name`, `x`, `y`, `group`, `index` and `children`, items as these but without `group`, `index` or `children`, their `x`, `y` from the group's; each child counted) in one commit and one undo step, all or nothing: one item refused refuses them all, the answer naming it by its place (`items/3/…`, `items/3/children/1/…`); from elsewhere, a design per item and per child may stand for its scene and box ([relay.md](relay.md#designs)) |
| `update_item` | edit | Any of an item's `x`, `y`, `rotation`, `width`, `height`, `scene`, `bindings`, `name`, `visible`, and its place: `group` (a group to move it into, `""` the top level) and `index` (0 at the back; alone, a new place in its group or the top level); a move keeps its `x`, `y` as numbers, set first when given; a group into itself or a group in it is refused (`op.cycle`); the rest stays; from elsewhere, a design the service produced may stand for the scene and box, built on the item as a read found it ([relay.md](relay.md#designs)) |
| `update_items` | edit | 1 to 50 items (`items`, each as `update_item` takes one, without `profile`, each item once) changed in one commit and one undo step, all or nothing: one entry refused refuses them all, the answer naming it by its place (`items/3: …`, `items/3/scene/…`); from elsewhere, a design per entry, or what a read found, as for `update_item` ([relay.md](relay.md#designs)) |
| `remove_item` | edit | An item, a group with everything in it |
| `add_asset` | edit | A picture stored as a blob: `svg` (an SVG's text) or `data` (a picture file's bytes in base64, at most 8,388,608 characters); its asset id, format and size, for a scene's `media` node; nothing stored when it isn't a picture the engine shows (a video is refused) |
| `show_on_desktop` | edit | The profile's desktop window on (`show`, the default) or off, its other window settings kept; the answer says when no window opens on this engine (`--no-windows`, or a platform without desktop windows), the setting kept |

A profile is named by its id or its exact name, an item by its id or its exact name. **Changes:** each call that changes
a profile is one commit, one step the editor can undo, made at the revision the tool read (with the document's tag), so
a change someone else made to the same part in between is a conflict: the tool reads again and retries once, then says
so. The retry acts on the profile and the items the tool found first, never on others given their names meanwhile; one
deleted or replaced meanwhile is an error that names it. The editor sees each change as another client's. **Saving:** the change is saved when the profile had no unsaved
changes just before it applied (decided as the commit applies); otherwise it is left unsaved and the answer says "Not
saved: ‹name› has unsaved changes from the editor; save there." A save refused because another change landed at the
same moment, or one the data folder refuses (no space, no write access), leaves the change applied: "Changed, but not
saved", never an error that would invite a second copy. `create_profile` saves the new profile at once; an
interrupted build leaves it saved and unfinished.

**The preview's checks** (heuristics, and they say so), with the thresholds of the built-in `checks.json`:
1. **Text that doesn't fit its box**, as the text layout knows it: cut with "…", lines left out, clipped, drawn past its
   box, or shrunk to its `fit` minimum and still too big.
2. **Past the canvas:** an item's drawing more than `canvasTolerance` (1 px) outside the canvas.
3. **Overlap:** two visible items whose boxes overlap by more than `overlapMin` (10 %) of the smaller, unless the one
   that holds the other is drawn below it (a card behind its content). Boxes, not shapes.
4. **Contrast:** solid-colour text, its colour with its own and its groups' opacity, against the pixels under its
   lines, each where it is drawn (not the box's unused width), in the same tick drawn without text: the colour covering 70 % of them, else the worst covering 10 %; below
   `contrastMin` (4.5:1, WCAG's ratio) is a problem.

Each problem names the item and a next step. What they couldn't look at is listed under "Not checked" per item:
gradient-filled text, text in a group drawn with a blend mode (the checks don't model compositing), text over a background with no main colour, and whatever is past the budgets (4,000,000 pixels
sampled, 10,000 item pairs compared). With neither: "The checks found nothing (they cover fit, the canvas edge, box
overlap and contrast)", never a bare "no problems". An item whose picture was still loading when the preview was drawn
(a placeholder in it) gets a last line, "‹item›: its picture is still loading; preview again"; the preview never waits. Every problem comes back as a tool result marked as an error, with its
cause and a next step (for example "There's no profile Desk (notFound). list_profiles names them."), so the AI can
explain it.

## When something is off

The engine's log (`<data folder>/logs/`) has one line per tool call (the tool, the profile as named, the outcome and how
long it took; never the other arguments), marked "from elsewhere". `GET /api/service`'s `aiApps` says how the connection
stands ([relay.md](relay.md)).

## How it works

- **Scopes.** The tools run inside the engine on the same code paths as the API, each with `read` or `edit`, never the
  session's powers.
- **AI apps elsewhere.** With the `service.aiApps` setting on, the tools answer AI apps elsewhere, through one
  connection the engine opens to the configured service, under the engine's own rules: only the tools, a cutoff before
  every write (bounded by the call's own deadline, never past 60 s after it was sent), late calls refused, web images
  refused for now, text held to what the app takes, and log lines marked "from elsewhere" ([relay.md](relay.md)). A
  call to `add_item` or `update_item` from elsewhere may bring a design the service produced, and one to `add_items` or
  `update_items` a design per item, with the files their sources need: every file and dependency, and every change of the call (the web-image
  rule included, on each item of a batch), is checked before anything is stored, so a refused call leaves
  nothing behind; an `update_item`'s design names the document and item it was built on, and answers `stale` with
  nothing changed when the item's design changed meanwhile. The relay may also read one item whole, with
  `get_profile`'s text for it held to what the app takes as JSON that still parses, cancellable until it answers
  ([relay.md](relay.md#designs)).
