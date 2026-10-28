# Unsupported content

Documents travel: between engines of different versions, through files, through clipboards. Wherever a document holds more than an engine supports (when it opens, imports, pastes, undoes or redoes), the engine shows what it can, keeps the rest exactly as it is, and never rewrites it silently.

## Item-level

An item is drawn as a **placeholder** when its scene is:

| Problem | Cause | Code in the check |
|---|---|---|
| `unsupported` | A scene format major version the engine doesn't read, or a capability in `requires` it lacks | `unsupported.format`, `unsupported.capability` |
| `invalid` | The scene breaks the schema, a rule or a scene limit of this engine, or the engine cannot compile it | `schema`, `limit.*`, `ref.*`, ... ([validation](validation.md)) |
| `budget` | The scene is valid but would take the document over an aggregate budget; items are admitted in document order, so this is a later item | `limit.document*` |

A placeholder item:

- is drawn as the engine's placeholder (a neutral hatched rectangle with a mark) filling its layout box, 100 × 100 where a dimension is automatic, at its placement;
- is reported in the frame's bounds with `problem` set, so a client can say why;
- can still be moved, rotated, renamed, hidden, locked, rebound, reordered, grouped, copied and removed, because those ops never touch the scene;
- gets new content only by an explicit `setDesign` that replaces its scene;
- **is kept exactly as it is**: the engine stores the scene as the same JSON value it received (member order and a number's spelling may differ, never its value as a double, [validation](validation.md#numbers)), saves it, exports it, copies it and restores it on undo unchanged. It never upgrades, downgrades, trims or repairs it.

Clients follow the same rule: a client that copies, pastes, exports or imports an item it can't interpret carries its scene, source and origin verbatim.

## Document-level

A document whose format major version or document `requires` the engine doesn't support is **not opened for editing**. It is listed with the reason (`status: "unsupported"` and a `problem` in the profile list, [API](api.md#profiles)); it can be deleted, and exported as stored, byte for byte. A document that is invalid as a whole (not JSON, or its structure breaks the schema outside its scenes) is refused when it is imported (422 to a create, [API](api.md#creating-and-duplicating)), and a stored one that has become unreadable is reported (`status: "unreadable"`) and left alone: the engine never overwrites a file it couldn't read.

## Writers versus readers

An engine is strict with what clients send and tolerant with what it reads:

- A commit with an **unsupported** scene (a paste from a newer engine) is accepted with a warning and stored verbatim.
- A commit with an **invalid** scene is refused (422): no client should create invalid content.
- A stored or imported document with invalid or over-budget scenes loads, with those items as placeholders.

## What is not unsupported

Things the engine can draw a fallback for are reported, not placeholders: a missing font family (drawn in the default family), missing media (the missing-media placeholder in the media's box), an unknown time zone (the local zone), a sensor with no reading (its input's status).
