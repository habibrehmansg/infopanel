# Limits and budgets

**Limits** are part of the format: every engine that reads version 1 accepts all content and requests within them and refuses what is beyond them, with `limit.<name>` or the code a table gives ([validation](validation.md)). They are fixed for a major version ([versioning](versioning.md)) and an engine reports each by its name in `GET /api/session`, in the `limits` of the format it belongs to (`formatSupport`, [API](api.md#session)): the scene, media and run-time limits are the scene format's (`infopanel.scene/1`), and every other limit on this page is the document format's (`infopanel.document/1`). They bound the worst case: what hostile or careless content can cost. Every limit that decides whether an engine accepts content or a request is on this page, once, grouped by what it bounds: scenes, documents, commits, request bodies, the profile list, the preview socket's messages, media, archives, and the 1.x import with its record. Bounds on a single member (a list's length, a string's, a number's range) are in the schemas; the main ones are repeated below their format's table.

**Budgets** are what an engine guarantees beyond the limits, for content within them: each states a floor that every engine meets and may exceed (undo steps kept, `opId`s remembered, the work a tick may do). Content is never refused for a budget, and budgets are not part of the format: an engine's values may change from release to release, never below the floor. **Performance targets** are what this engine is measured against for ordinary content; they promise nothing. Costs marked as estimated are measured when the scene runtime exists, and these numbers are revised if they are wrong.

## The reference

The numbers below are justified from measurements of the current renderer on its reference document, the "Wide bar" example profile (1920 × 480, about 30 items: four donuts, a graph, bars, histograms, text, a clock), on an Apple M-series Mac:

| Measurement (October 2026) | Scale 1 (1920 × 480) | Scale 2 (3840 × 960) |
|---|---|---|
| Draw the frame | 1.3–3.4 ms | 3.4–3.9 ms |
| Measure every item's bounds | 0.4–1.1 ms | 0.35 ms |
| Hash the frame | 0.4 ms | 1.0 ms |
| Encode a lossless PNG (zlib level 1, Up filter) | 5.4 ms, 106 KB | 19.1–19.6 ms, 301 KB |
| Engine process (debug build), one preview at 30 frames a second | 22 % of one core, 113 KB a frame | 74 % of one core, 308 KB a frame |
| Engine memory (resident, debug build) | idle 147 MB; 200–250 MB while previewing and after 20 thumbnails at 3840 px | |

## Nesting limit

| Limit | Value | Why |
|---|---|---|
| `jsonDepth` | 256 levels | Bounds the recursion of every reader and writer of the formats. Within their own limits scenes nest far less (each node level adds two levels, each expression level two). |

`jsonDepth` is the deepest nesting of arrays and objects in any document or request body an engine reads, and in any document a commit would produce: a number, string, boolean or null is at depth 0, and an array or object is one level deeper than its deepest member (`[]` and `{}` are at depth 1, `{"a": [1]}` at depth 2). Content nested deeper gets one error, `limit.jsonDepth` at `""`, in place of the schema's ([validation](validation.md#order)), and is checked no further. Opaque members (editor data, a source's data, an origin, a scene of a newer format) are held to it where they sit: a value a commit carries sits deeper in the document than in the commit's body, under the groups it lands in, so a commit is also checked on the document it would produce ([commits](commit.md#what-the-engine-checks-when-it-applies-a-commit)).

## Sizes

The five content sizes, `sceneBytes`, `documentBytes`, `sourceBytes`, `originBytes` and `editorBytes`, are measured on the content's **canonical form**: [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) (the JSON Canonicalization Scheme) in UTF-8, every number written as the double it reads as ([validation](validation.md#numbers)). That is: no whitespace; members sorted by their names' UTF-16 code units; strings that escape only `"` and `\` (as `\"` and `\\`) and the control characters U+0000 to U+001F (`\b`, `\t`, `\n`, `\f`, `\r`, the others as `\u00xx` in lower case), every other character in UTF-8 as it is; numbers in ECMAScript's shortest form (`1.0` is `1`, `1e2` is `100`, `-0` is `0`, `0.0000001` is `1e-7`). How the content was sent doesn't count: whitespace, escapes (`"\u00e9"` is the two bytes of `é`) and a number's spelling (`1e20` is 21 digits) are measured as the canonical form has them.

`documentBytes` measures the document without its revision metadata: the `rev` member of the document, of `profile` and of every item (groups' children included), and nothing else; a `rev` inside an opaque member (`scene`, `source`, `origin`, `editor`) is content and is measured. The engine stores documents in the canonical form without those members, so a stored document's size is its measured size, and a document read with its revisions (a copy of one the API returned) is measured as it will be stored.

A document's bytes as received or stored are held only to a read cap, which bounds reading and decides nothing else: a create's body, an archive's document and a stored document are read when they are at most `documentBytes` plus 64 KiB (otherwise `limit.documentBytes`, unread); then the document's canonical size decides. Only `commitBytes` and the size limits on other request bodies ([request limits](#request-limits)) measure the bytes received.

## Scene limits

| Limit | Value | Why this number |
|---|---|---|
| `sceneBytes` | 1 MiB in the canonical form ([sizes](#sizes)) | Two orders above a typical scene (a few KB); bounds parsing and storage. |
| `nodes` | 2,000 | Each of the 1.x item kinds needs fewer than 30 nodes (a 101-image gauge is one media node); a 60-segment gauge with ticks about 150. Shapes draw in roughly a microsecond each, so 2,000 stays within a frame. |
| `depth` | 24 | Node nesting; real scenes need under 8. Bounds recursion. |
| `inputs` | 32 | Widgets bind one to a few sensors; 32 covers a multi-sensor card. |
| `values` | 256 | Shared expressions; a typical widget has under 20. |
| `stateCells` | 64 | Smoothing, rate limits and holds take one or two cells each. |
| `exprDepth` | 32 | Formatting a 1.x sensor value nests under 10 deep. Bounds recursion. |
| `exprWork` | 20,000 | Operations per tick (values once; string-producing operations 16 each; `history` 1 more per 64 samples). At an estimated tens of nanoseconds per operation, under 1 ms per scene. |
| `pathCommands` | 8,192 | Per path or polyline; an icon is a few hundred. |
| `geometry` | 32,768 | Path segments and polyline points per scene. |
| `textNodes` | 256 | Text layout is the dominant per-tick cost (estimated at tens of microseconds per changed string; unchanged text keeps its layout). |
| `seriesNodes` | 32 | Each draws up to 4,096 samples. |
| `tableNodes` | 8 | Each up to 100 rows × 16 columns of text (schema). |
| `mediaNodes` | 64 | Bounds decoders and cached frames per scene. |
| `sequence` | 256 frames | 1.x gauges use at most 101 images. |
| `effectCost` | 64 units | Each blur, shadow or glow costs 1 + ceil(sigma ÷ 8) (sigma 0–64; 9 for a computed sigma). Blur cost grows with sigma and area; with layers clipped to the canvas this bounds a scene to a few full-canvas blurs. 1.x glow (radius up to 20) costs 4. |
| `composeDepth` | 16 | Compose paints nested in one another, through `dst` or `src` (the outermost is at 1). Two gradients multiplied need 1; bounds the paint tree's recursion. |

Schema-level bounds also apply, where the schema states them: up to 16 gradient stops, 16 dash lengths, 8 transform steps and 4 effects per group, 32 spans per text, 16 columns and 100 rows per table, 4,096 samples per series, `history` counts up to 4,096, `maxLines` up to 1,000, 16 operands per numeric or boolean operation and 32 per `concat`, 32 thresholds per `steps`, path data of 65,536 characters, string literals of 4,096 and 32 capabilities in `requires`.

## Document limits

| Limit | Value | Why |
|---|---|---|
| `documentBytes` | 16 MiB in the canonical form ([sizes](#sizes)), as stored | Fits 2,000 items of ordinary size with room; bounds memory per document (the parsed model is a small multiple of it). |
| `items` | 2,000 | 1.x profiles have tens to a few hundred items. |
| `itemDepth` | 8 levels | How deep items nest: a top-level item is at level 1 and a group's children one level below the group, so an item may be inside at most seven groups (and a group at level 8 is empty). |
| `bindings` | 32 per item | As `inputs`. |
| `sourceBytes` | 256 KiB per item, in the canonical form | A client's description of how it produced a scene; usually a few KB. |
| `originBytes` | 4 KiB, in the canonical form | Where an item came from. |
| `editorBytes` | 64 KiB, in the canonical form | Client data on the profile. |
| `documentNodes` | 20,000 | Aggregate over the document's scenes: about 10× the reference. |
| `documentWork` | 200,000 | Aggregate expression work: a few milliseconds per tick at most. |
| `documentTextNodes` | 1,000 | Aggregate text nodes: even if every string changed every tick, layout stays around 25 ms (estimated at about 25 µs per changed string; to be measured). |
| `documentEffectCost` | 512 units | Aggregate effect cost. |

Aggregates are admitted in document order: a scene that would take the document over one is drawn as a placeholder (`budget`), and a commit that would is refused.

Schema-level bounds also apply: up to 32 `triggerApps` of 260 characters, 256 `guides` and 32 capabilities in `requires`; names of 256 characters, sensor keys and source references of 512; canvas sides from 1 to 16,384 pixels and placements within ±1,000,000. A source's `deps` have no count of their own: `sourceBytes` bounds them.

## Commit limits

| Limit | Value |
|---|---|
| `commitBytes` | 16 MiB of the request body as received, whitespace around the JSON value included (a commit may carry a whole document's worth of items) |
| `ops` | 1,000 per commit |

## Request limits

Every request body is held to a size as received, whitespace around the JSON value included, before it is parsed. A body over its limit is answered 422 `invalidProblem` with one error, `limit.<name>` at `""` (a blob's 413), and nothing is done.

| Limit | Value | The body of |
|---|---|---|
| `requestBytes` | 64 KiB | An undo, redo, revert, save or duplicate (a few members; [API](api.md)) |
| `tokenRequestBytes` | 4 KiB | A token create (a name and its scopes) |

A commit's body is held to `commitBytes` ([commit limits](#commit-limits)), a create's is read up to `documentBytes` plus 64 KiB and its document then held to `documentBytes` ([sizes](#sizes)), a 1.x migration record's to `recordBytes` ([1.x import limits](#1x-import-limits)), a blob's bytes to `mediaBytes` and an archive's to `archiveBytes` (413, nothing read further or stored). Every body is nested at most `jsonDepth` deep. A body is read only as far as its limit plus one byte, whether or not it states its length.

## Stored list limits

| Limit | Value | What it bounds |
|---|---|---|
| `profileListBytes` | 16 MiB | The list of an engine's profiles as the engine stores it: every profile's id, in display order. This engine stores it as JSON, 39 bytes a profile, so about 430,000 profiles |
| `tokensBytes` | 16 MiB | The integration tokens as the engine stores them: each token's id, name, scopes, times and secret's hash. This engine stores them as JSON, about 210 bytes a token, so about 80,000 tokens |

An engine never stores a list over its limit, and reads every list within it: a create or duplicate that would take the profile list over `profileListBytes` is answered 422 `invalidProblem` with one error, `limit.profileListBytes` at `""`, and creates nothing ([API](api.md#creating-and-duplicating)); a token create that would take the stored tokens over `tokensBytes` is answered 422 `invalidProblem` with one error, `limit.tokensBytes` at `""`, and creates nothing ([API](api.md#integration-tokens)). Deleting a profile or revoking a token makes room again.

## Socket limits

| Limit | Value | What it bounds |
|---|---|---|
| `socketMessageBytes` | 4,096 bytes | A client message on the preview socket, in UTF-8: a longer one closes the socket with 1009 once the socket is authenticated; on a socket still awaiting `auth`, a first message over the cap closes it with 1008 `auth expected` ([API](api.md#the-preview-socket)) |

## Media limits (checked from the header, before decoding)

| Limit | Value | Why |
|---|---|---|
| `mediaBytes` | 32 MiB per asset, and per stored blob of any kind: a larger blob is refused (413) and not stored | Generous for images; bounds storage and upload. |
| `svgBytes` | 1 MiB | SVG is parsed in full; checked before any of it is read. |
| `svgElements` | 10,000 | Bounds SVG rendering cost: reading stops at element 10,001, and the expanded size (each `use`, `clip-path` and `mask` reference counting what it draws, every 32 path numbers and each gradient reference counting too, [SVG](svg.md)) is held to it. |
| `svgDepth` | 32 levels | Element nesting in an SVG file, the root at 1; reading stops at the first element deeper ([SVG](svg.md#reading)). Bounds the reader. |
| `svgDrawnDepth` | 64 levels | Elements drawn inside one another, each `use`, `clip-path` and `mask` reference a level, the root at 1 ([SVG](svg.md#expanded-size-and-depth)). Bounds drawing's recursion. |
| `svgGradientChain` | 4 `href` references | How far a gradient inherits ([SVG](svg.md#references)). |
| `svgNumber` | 1,000,000,000 in magnitude | Every length, coordinate, number list entry, transform argument and path number in an SVG: a file with one beyond it is refused, not clamped ([SVG](svg.md#values)); opacities and colour components are clamped as documented there. Well beyond any drawable coordinate. |
| `mediaSide` | 8,192 px | Twice a 4K panel's width. |
| `mediaArea` | 16,777,216 px (16 Mpx) | The engine's frame cap: 64 MB of RGBA per decoded frame. |
| `mediaFrames` | 1,000 | Animated GIF, PNG or WebP frames. |
| `mediaFrameArea` | 268,435,456 px (frames × area) | 1 GiB of RGBA if every frame were decoded; the engine decodes on demand into a bounded cache. |
| `mediaDuration` | 600 s | An animation's length. |

Bytes over `mediaBytes` are refused when they are added, and not stored. An asset within `mediaBytes` that is over another limit (or fails another media check) is stored as a blob but refused as media: the engine reports why it can't be used as media (`mediaError`, [API](api.md#blobs)). Media in a document that names such a blob, or one over a limit, draws the missing-media placeholder. An SVG's size is its `width` and `height` in pixels (unitless or `px`; other units are unsupported, [SVG](svg.md)) or, where one is absent, its `viewBox` size, and the limits apply to that size.

## Archive limits

An import reads an archive, a profile archive or a 1.x `.infopanel` export, within these, and refuses one beyond them; an export that would be beyond them isn't made. The codes are those given.

| Limit | Value | What it bounds |
|---|---|---|
| `archiveBytes` | 512 MiB | The archive's file, as received or as an export would write it: an import's body over it is answered 413 before any of it is read, and no export over it is made (`archive.expanded`) |
| `archiveEntries` | 10,000 | Its entries, as its directory declares them and as they are read (`archive.entries`) |
| `expandedBytes` | 512 MiB | The bytes one import decompresses or reads, counted as they are read, whatever the entries' headers claim; a 1.x folder's assets count too (`archive.expanded`) |
| `entryNameBytes` | 1,024 bytes | An entry's name, in UTF-8 (`archive.name`) |
| `entryRecordBytes` | 4 KiB | An entry's directory record: its name, extra field and comment, and 46 fixed bytes (`archive.entries`) |

## 1.x import limits

What an engine reads of 1.x files, and what one import answer and the 1.x migration's record hold ([1.x import](import.md)).

| Limit | Value | What it bounds |
|---|---|---|
| `v1ProfileXmlBytes` | 4 MiB | A `Profile.xml`, `profiles.xml` or `settings.xml` |
| `v1ItemsXmlBytes` | 16 MiB | An items file (`DisplayItems.xml`, `profiles/{guid}.xml`) |
| `v1XmlDepth` | 64 levels | Element nesting in any 1.x XML file (the root element at 1) |
| `v1XmlElements` | 500,000 | Elements in any 1.x XML file |
| `v1Profiles` | 1,000 | Profiles read from `profiles.xml`; the rest are left out with a warning |
| `v1Devices` | 64 | Devices read per device family of `settings.xml`; the rest are left out with a warning |
| `v1DeviceFields` | 64 | Fields read per device; the rest are left out with a warning |
| `v1SettingLength` | 1,024 characters | A device field's value or a hotkey's: a longer field is left out, and a hotkey with one, each with a warning. The other settings are read by their own rules, not held to it |
| `v1Hotkeys` | 256 | Hotkeys read; the rest are left out with a warning |
| `diagnostics` | 1,000 | Diagnostics one import answer lists; then one more, `more`, counts the rest |
| `recordOutcomes` | 1,000 | Profile outcomes in the migration record, counted before the schema (`limit.recordOutcomes`) |
| `recordBytes` | 1 MiB | The migration record, as received and as stored (`limit.recordBytes`) |
| `recordReasonLength` | 256 characters | An outcome's reason in the record (the schema's `maxLength`: `schema`) |

A 1.x file over one of the first four isn't read: an archive's `Profile.xml` refuses the import (`import.xml`), and any other file is reported in the answer's diagnostics.

## Run-time limits

These hold while a scene draws; content is never refused for them.

| Limit | Value | What it bounds |
|---|---|---|
| `textLength` | 4,096 Unicode scalar values | Every string an expression produces, cut at the operation that produces it ([expressions](expressions.md#numbers-and-strings)); and what a text node draws, its spans together, and each table cell ([smart nodes](smart-nodes.md#text)). No string an engine holds for a scene is longer. |

- Every number a scene draws with (a coordinate, size, radius, transform value, path or polyline number, stroke width, dash length or effect offset) is clamped to ±10⁹ on its own before drawing, and bounds are measured from the clamped values ([geometry](geometry.md#coordinate-range)). This is drawing, not acceptance, and covers a scene's own numbers only: an SVG's are held to `svgNumber`, and a file with one beyond it is refused.
- Effect layers are clipped to the canvas plus the effect's reach.
- Frames are at most 16 Mpx, counted after each side is rounded to whole pixels (a preview at a scale that would exceed it is drawn at the largest step of 0.25 that fits, [API](api.md#frame-size)).

## Run-time budgets

Content within the limits can still be expensive on a given tick. An engine draws an item that would exceed one of these as a `budget` placeholder for that tick, with a diagnostic, and tries it again on the next. Each is a floor: every engine allows at least this much (this engine allows exactly this much), and an item within all of them is never drawn as over budget.

| Budget | At least |
|---|---|
| Text layouts per document tick | 4,000 lines (of layouts made, not ones kept from earlier ticks; every line laid out counts, each line a paragraph wraps to and a blank one too, `fit` probes and ellipsis passes included). An engine stops a layout at the line that would exceed it, so a paragraph wrapped to many lines costs no more than the budget |
| Text layouts per item tick | 2,000 lines, counted the same way, of every layout the item draws: kept ones count too (reuse saves time, not budget), so what an item keeps between ticks stays within it. An item drawn as over budget keeps none |
| Dashes per item tick | 100,000 for all the item's dashed strokes together, in every group (a table's or series' stroke along every cell or bar it outlines). Each stroke counts along the part of its outline it draws after trimming: a shape's, or the glyph outlines of a text or table; a trimmed outline with curves (arcs, ellipses, rounded corners, a path's curves, glyphs) counts whole, since where a trim ends on a curve depends on drawing resolution. The pattern starts again on every contour (each glyph has one or more), so each contour counts at least one period however short it is. Counted over the outline as drawn, its curves by their control points, with the pattern floored at drawing resolution ([strokes](scene.md#strokes)) at the largest scale the item can be drawn at (the largest stretch of its whole transform, its placement's and its groups' together, times the largest frame scale the cap allows), so a pattern finer than a pixel counts by the outline's length in device pixels |
| Expression work per item tick | twice `exprWork`, so a valid scene always stays under it |
| Watchdog | 50 ms: an item whose evaluation or drawing takes over it on three ticks running is drawn as over budget for 5 s, then tried again. A tick counts once, by its evaluation and its slowest draw, however many outputs and scales draw it |

## Checking hostile content

Validation itself is bounded, since it runs on untrusted imports and commits:

- Collection counts (`ops`, `items` with inserted items' children, `nodes`, `inputs`, `values`, `stateCells`) are checked on the raw JSON before the schema, so no later check runs over more than the limits allow.
- Named values are ordered by their dependencies without recursion through references, so a chain of values costs no stack.
- Schema validation remembers the result of each union (`oneOf`, `anyOf`) on each object or array it has already been tried on, and has a work budget (64 steps per JSON value of the instance, and 64 more per 64 characters of each property name, plus 100,000; reading a property name costs one step per 64 characters of it); content over it gets one `schema` error (keyword `complexity`). Error paths cost nothing until an error is reported: a long property name above many values doesn't multiply into long pointers.
- `uniqueItems` buckets items by a hash seeded per process (equal items share one), and every comparison of two items in a bucket costs the work budget one step per value compared, so items made to share a hash can't make it quadratic for free. Capability lists (`requires`) have at most 32 items.
- Schema `pattern`s and the engine's own regular expressions are matched without backtracking (linear in the string's length); a schema whose pattern needs backtracking (a backreference, a lookaround) doesn't load.
- An SVG is read as a stream with its depth, element count and references bounded before anything decodes it, and the engine decodes only the document rebuilt from what it parsed ([SVG](svg.md#the-rebuilt-document)).
- At most 100 errors are reported per check, then, whenever there are more than 100, one error with code `more` that counts the rest.

## Process budgets

Shared by everything one engine runs. Each is a floor, as above: this engine's value, which every engine meets or exceeds.

| Budget | At least |
|---|---|
| `opId`s remembered | the last 1,000 commits applied to each document, for as long as the engine runs ([commits](commit.md#opid-retries)) |
| Undo history | per document the last 100 steps within 64 MiB, and all documents together 256 MiB; the byte caps win, so a document with large steps keeps fewer than 100; oldest steps are evicted first ([commits](commit.md#undo-redo-revert-and-save)) |
| Decoded media cache | 512 MiB; least recently used frames are dropped first |
| Scene nodes being rendered | 100,000 across all documents; a document that would exceed it is not rendered until others stop (its outputs show it as over budget) |
| Output buffers | a bounded pool per output, each buffer up to the 16 Mpx frame (64 MiB) |

## Performance targets

What this engine is measured against, for a document like the reference (and any within the limits, at proportionally more cost). They are targets, not guarantees:

| Budget | Target (p95) | From the reference |
|---|---|---|
| Tick: read inputs, evaluate values and state, lay out text, once per document per tick | ≤ 2 ms | Today's renderer does this work inside its 1.3–3.4 ms draw. |
| Frame: draw one output at scale 1 | ≤ 4 ms | Measured 1.3–3.4 ms: the new runtime may cost a little more than the current renderer, not more. |
| Frame: draw at scale 2 | ≤ 8 ms | Measured 3.4–3.9 ms. |
| Preview encoding at scale 2 | ≤ 10 ms | A full PNG takes 19–20 ms and 301 KB, so the preview needs dirty-rectangle updates (lossless tiles of what changed, with full keyframes to recover) to meet the drag budget below. |
| Engine CPU, one document, one preview at scale 2 and 30 frames a second | ≤ 75 % of one core | Measured 74 % today. |
| Engine CPU, outputs at scale 1 only (panels, desktop windows), 30 frames a second | ≤ 25 % of one core | Measured 22 % with PNG encoding. |
| Engine CPU when nothing is rendered | ≤ 1 % | Measured 0 %. |
| Memory per document | ≤ 8 MB beyond its output buffers (model, scenes, tick snapshot, state) | The reference document is well under 1 MB of JSON. |
| Engine memory | idle ≤ 150 MB; rendering 3 documents with one preview at scale 2 ≤ 400 MB | Measured 147 MB and 200–250 MB. |
| **Drag latency**: pointer move in a client to the engine's new pixels on screen, at scale 2 | **≤ 50 ms** | Budgeted as: client to commit sent 4 ms, commit applied 2 ms, wait for the tick 8 ms, tick and draw 10 ms, encode 10 ms, transfer and decode in the client 8 ms, presentation 8 ms. Full-frame PNG alone would take 20 ms to encode and several more to transfer and decode, so this needs dirty rectangles; a client may also move the item's last pixels itself while it waits. |
| Selection and handles | Immediate | Drawn by the client from the bounds of the last frame, never waiting for the engine. |
