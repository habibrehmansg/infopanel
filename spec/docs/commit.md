# Commits

Schema: `urn:infopanel:api:1#/$defs/commitRequest` and the result and problem definitions next to it. Examples: `examples/commit/`, `examples/api/`.

A client changes a document only by committing ops to the engine, which owns revisions, undo, redo, dirty state, save and revert. A commit is a list of ops applied in order, all or none, as one undo step: each op sees the document as the ops before it left it, so an op may target an item inserted earlier in the same commit.

```http
POST /api/profiles/{id}/commit
```
```jsonc
{ "opId": "0b8a6c1e-2f43-4a7e-9d3f-5f1c2a7b9e10", "instance": "e1f6", "coalesceKey": "gesture:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:7",
  "ops": [ { "op": "setPlacement", "id": "cpu-bar", "baseRev": 42, "placement": { "x": 52, "y": 108 } } ] }
```

## Ops

Each op writes one part of one item, or the profile, so that changes to different parts never collide: a drag (`setPlacement`) and a new design (`setDesign`) of the same item can both succeed.

| `op` | Writes | Field groups (for conflicts) |
|---|---|---|
| `setPlacement` | `id`, `placement` (replaced whole) | placement |
| `setDesign` | `id`, `layout`, `source`, `scene` (replaced together; absent `layout` or `source` means none), and `origin` when given | design |
| `setBindings` | `id`, `bindings` (replaced whole) | bindings |
| `setAttributes` | `id`, `set`: any of `name`, `visible`, `locked` | each attribute it sets |
| `insert` | `item` (with its children), `parentId` (a group, or null/absent for the top level), `index` (absent, or past the end of the list: the end, at the front) | the parent's children |
| `remove` | `id`: the item and everything in it | every field of the item and of everything in it |
| `move` | `id`, `parentId`, `index` (in the target list once the item has been taken out; past its end: the end), optional `placement` ([geometry](geometry.md), reparenting) | position (and placement, when given) |
| `profile` | `set`: any profile fields, each replaced whole (`guides`, `window`, `editor`, ...) | each field it sets |

`setDesign` and `setBindings` apply to scene items, not groups. An inserted item's ids (its own and its children's) must not exist in the document. A group can't move into itself or anything in it.

`index` is 0–2,000 (the document's item limit; schema `itemIndex`): a larger value is invalid (`schema`), never clamped. Within that range, an index past the end of the list means the end.

The field groups an engine tracks per item are `placement`, `design`, `bindings`, `name`, `visible`, `locked`, `position` (its parent and index) and, for a group, `children` (its membership: an item inserted into it, moved into or out of it, or removed from it); per profile, each profile field.

## `baseRev`: conflicts

`baseRev` is **the document revision the client's change is based on**: the document `rev` it last read (from a fetch of the document, a commit result or a change event), for every kind of op. It is not the item's own `rev`: an item's `rev` stays the revision of its last change, and a field group's last-change revision can be newer than the `rev` of a group that contains it. **An op conflicts when a field group it writes has a last-change revision greater than `baseRev`**, compared with the field revisions as they were when the commit started: an earlier op of the same commit never makes a later one conflict, and an item inserted earlier in the same commit has no earlier revisions, so a `baseRev` on it never conflicts. Changes to other field groups don't matter: a `setPlacement` with `baseRev` 42 succeeds after another client's `setDesign` moved the item to `rev` 43, because the placement didn't change. A client usually sends the same `baseRev` on every op of a commit. An op without `baseRev` is not checked (last writer wins); a client that produces a scene from an item's state should always send `baseRev` with the `setDesign`, so that a result produced from stale state is refused instead of overwriting newer work.

**A `remove` with `baseRev` conflicts when the item or anything in it (children, their children, ...) has any field group, membership (`children`) included, whose last change is greater than `baseRev`**: removing a group someone else has just added to, or whose child someone has just redesigned, is refused. The 409 lists one conflict for each such item, with its own `id`, current `rev` and changed `fields`, all under the `remove`'s `op` index (`examples/api/conflictProblem/remove.json`).

For example, a group `gauges` last changed at revision 5 and its child `gpu-gauge` was redesigned at revision 10, so the document is at `rev` 10 and the group's own `rev` is still 5. A client that has read revision 10 removes the group with `baseRev: 10` (`examples/commit/valid/remove-group.json`): nothing in it changed after 10, so the remove applies. Had the client sent the group's `rev` (5) instead, the child's design change at 10 would make it conflict although the client had seen it. If another client then redesigns the child at revision 11 before the remove arrives, the same `baseRev: 10` conflicts, listing `gpu-gauge` with `rev` 11 and `fields: ["design"]` (`examples/api/conflictProblem/remove-child.json`).

An op on an item that doesn't exist at that point of the commit, or into a parent that doesn't, conflicts too (`reason: "missing"`).

On any conflict nothing is applied and the engine answers **409** (`application/problem+json`, `conflictProblem`):

```jsonc
{ "status": 409, "code": "conflict", "rev": 47,
  "conflicts": [ { "op": 0, "id": "cpu-bar", "reason": "changed", "rev": 46, "fields": ["design"] } ] }
```

Every conflicting op is listed, with the item's current `rev` and the field groups that changed. The client refetches what it needs, rebuilds its intent on the current state (producing the scene again if it must) and commits again with a **new** `opId`.

`instance` (optional) is the engine instance id from `GET /api/session` that the client's revisions came from; when it isn't the current one (the engine restarted), the commit conflicts with `reason: "instance"` and the client starts over from a fresh read.

## `opId`: retries

`opId` (1–128 of `A–Z a–z 0–9 _ . : -`) is unique per commit. The engine remembers recent applied commits (at least the last 1,000 per document, or 10 minutes). **A commit whose `opId` was already applied returns the stored result, with `replayed: true`, before any revision check**, so a client that lost the response can retry safely. Reusing an `opId` with a different body is refused (422, `opId.reused`). A refused commit (409 or 422) is not remembered: retrying it is checked again.

## Coalescing and history

Each applied commit is one undo step, unless `coalesceKey` merges it into the previous step: when the previous recorded commit had the identical key and either the key starts with `gesture:` (one drag, however long) or it came less than 1 second earlier (a burst of typing or nudging). Undo and redo end any merge. A commit that changes nothing records nothing.

**A `coalesceKey` names one gesture of one client.** The engine compares keys only for equality and doesn't know which client sent them, so two clients (or two tabs of one) that both sent `gesture:7` would merge each other's steps. A client therefore puts a random part, chosen once per client instance, in every key, and a counter or the gesture's own id after it: `gesture:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:7` for its seventh drag, `name:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:3` for typing a name. Keys are at most 128 characters.

Undo history is kept in memory, bounded by a count and by bytes: the last 100 steps, within 64 MiB per document ([limits](limits.md)). **The byte cap wins**: steps are evicted oldest first, so a document with large steps (whole scenes) may keep fewer than 100. It is not saved. Undo, redo and revert are separate endpoints and are document changes like commits: each is a new revision.

## Results

**200** (`commitResult`):

```jsonc
{ "rev": 43, "items": { "cpu-bar": 43 }, "removed": [], "profileRev": 43,
  "dirty": true, "canUndo": true, "canRedo": false, "warnings": [] }
```

`items` has the new `rev` of every item the commit changed or added; `removed` the ids it removed (children included); `profileRev` is set when the profile changed. A commit that changed nothing answers with the unchanged `rev` and empty `items`.

`warnings` lists content the engine accepted but can't draw as written: code `unsupported` for a scene that needs a newer format or a capability the engine lacks. The engine keeps such a scene exactly as sent and draws a placeholder ([unsupported content](unsupported.md)).

**422** (`invalidProblem`): the commit breaks the format, a rule or a limit; nothing was applied. Each error has a JSON pointer into the request body, a code from [validation](validation.md) and a message:

```jsonc
{ "status": 422, "code": "invalid", "errors": [ { "path": "/ops/0/scene/nodes/0/w", "code": "ref.input", "message": "…" } ] }
```

A client must not write invalid content; an engine tolerates it only in documents it reads ([unsupported content](unsupported.md)).

## What the engine checks when it applies a commit

1. Without the document: the body's size, its collection counts (`limit.ops`, and `limit.items` for an inserted item with its children), its schema, and each new scene and inserted item on its own ([validation](validation.md)). Failures: 422.
2. `opId`: an applied one returns its stored result (`replayed: true`); a reused one with another body is 422 `opId.reused`.
3. `instance`: a different one is 409 (`reason: "instance"`), with nothing else checked.
4. The ops, **in order, against a tentative copy of the document**, each applied to it before the next is checked:
   - the target (and a `move`'s or `insert`'s parent) exists at that point, else a conflict (`missing`);
   - its `baseRev` (the document revision the client read) against the field groups' last-change revisions as of the start of the commit (for `remove`, the item's and every descendant's): one greater than `baseRev` is a conflict (`changed`);
   - inserted ids are new (`id.duplicate`), `setDesign` and `setBindings` target scene items (`op.target`), a parent is a group (`op.parent`) and not the moved item or inside it (`op.cycle`).

   An op that fails is not applied to the copy, and the engine goes on with the next op, so that every conflict is found.
5. The result stays within the document's limits and budgets (`limit.*`).

If anything failed the whole commit is rolled back (the tentative copy is dropped) and nothing is recorded: **409** when any op conflicted, listing every conflict, otherwise **422** with every error. Otherwise the copy becomes the document, as one revision and one undo step.
