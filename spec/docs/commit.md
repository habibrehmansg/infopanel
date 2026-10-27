# Commits

Schema: `urn:infopanel:api:1#/$defs/commitRequest` and the result and problem definitions next to it. Examples: `examples/commit/`, `examples/api/`.

A client changes a document only by committing ops to the engine, which owns revisions, undo, redo, dirty state, save and revert. A commit is a list of ops applied in order, all or none, as one undo step: each op sees the document as the ops before it left it, so an op may target an item inserted earlier in the same commit.

```http
POST /api/profiles/{id}/commit
```
```jsonc
{ "opId": "0b8a6c1e-2f43-4a7e-9d3f-5f1c2a7b9e10", "instance": "e1f6", "baseRev": 42,
  "coalesceKey": "gesture:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:7",
  "ops": [ { "op": "setPlacement", "id": "cpu-bar", "baseRev": 42, "placement": { "x": 52, "y": 108 } } ] }
```

Every commit names the engine `instance` its revisions came from and its own `baseRev`, the document `rev` the client last read before it built the commit ([retries](#opid-retries)); each op may carry a `baseRev` of its own, which decides its conflicts ([below](#baserev-conflicts)).

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

## What a change stamps

A commit that changes the document, and an undo, redo or revert that takes a step, makes one new revision and gives it, as their last-change revision, to exactly the field groups whose value differs between the document before it and after it:

- A field group's value is its members as stored, compared as JSON in canonical form (RFC 8785, as content hashes are, [document](document.md#content-identity)): member order and the spelling of numbers don't count, but an absent member and one set to its default value (`visible: true`, `name: ""`) differ. `design` is `layout`, `source`, `scene` and `origin` together; each other group is its one member.
- An item's `position` is its parent (a group, or the top level) and its index among the siblings it has in both versions (the items in that parent's list both before and after the change, itself included). It differs when the parent differs or when that index differs. So a `move` within a list also changes the position of every sibling whose index among the others changed, while an item inserted, removed, or moved into or out of the list beside an item doesn't change that item's position.
- A group's `children` is the set of its direct children. It differs when an item is in its list in one version and not in the other (inserted into it, removed from it, or moved into or out of it); a new order within the group changes the children's `position`, not the group's `children`.
- An item present only after the change (inserted, or brought back by undo, redo or revert) has every field group at the new revision; one present only before is removed.

An item's `rev` is the latest last-change revision of its field groups, and the profile's `rev` that of its fields. The result lists in `items` every item with a field group at the new revision (inserted items and their children included), in `removed` every removed item (children included), and sets `profileRev` when a profile field is at the new revision.

**A commit stamps only what it changed.** A field group that a commit's ops write with a value equal to the one it had keeps its last-change revision, so it never makes a later op conflict; within a commit that changes something else it is stored as the commit wrote it. **A commit whose result doesn't differ from the document makes no revision**: `rev` stays, `items` and `removed` are empty, nothing is recorded in the history, and the document stays exactly as it was. Its `opId` is remembered like any applied commit's.

**Undo, redo and revert make a new revision whenever they take a step, even when the content comes out equal** (a recorded step whose net change was nothing, such as a drag that ended where it started): `rev` goes up, and the field groups that differ, if any, get it.

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

`instance` is the engine instance id from `GET /api/session` that the client's revisions came from; when it isn't the current one (the engine restarted), the commit conflicts with one conflict `{ "reason": "instance" }` (no `op`: it is the whole commit's) and the client starts over from a fresh read.

## `opId`: retries

`opId` (1–128 of `A–Z a–z 0–9 _ . : -`) is unique per commit. **A commit whose `opId` was already applied returns the stored result, with `replayed: true`, before any revision check**, so a client that lost the response can retry safely. The stored result is the commit's result as it was when it was applied, even if later commits, undo or redo have changed the document since; a replay changes nothing and records nothing.

**The engine remembers the `opId`s of at least the last 1,000 commits applied to each document** (commits that changed nothing included), for as long as it runs, and may remember more. It forgets them oldest first, in the order their commits were applied, so the `opId`s it remembers are always those of the most recent commits applied to the document, with none missing between them. The window is a count, not a time: an engine that forgets an `opId` only once 1,000 later commits have been applied to the same document conforms.

**The retry window.** For each document the engine keeps the revision at which the oldest `opId` it still remembers was applied (the `rev` of that commit's result): the start of its retry window. Once it has forgotten any `opId` of the document, **a commit whose `opId` it doesn't remember and whose `baseRev` is older than the start of the window is refused with 409 `expiredProblem`**, and nothing is applied:

```jsonc
{ "status": 409, "code": "expired", "rev": 1203 }
```

Such a commit may be a retry of one the engine applied before the window started and has since forgotten (a commit that changed the document was applied at a revision newer than its `baseRev`, and no newer than the window's start), and applying it again could apply its edit twice. `rev` is the document's current revision. The client reads the document, decides whether its change is still needed (it may already be there), and if so commits it again with a new `opId` and a current `baseRev`. Before the engine has forgotten any `opId` of the document there is no such refusal, and a commit based on the document's current revision is never refused this way.

So a retry of a commit that changed the document is never applied again: while the engine remembers its `opId` it gets the stored result; once the `opId` is forgotten it is refused (`expired`); after a restart its `instance` is refused first.

Reusing an `opId` with a different body is refused (422, `opId.reused`). Bodies are compared byte for byte, so a retry sends exactly the bytes the first attempt sent. A refused commit (409 or 422) is not remembered: retrying it is checked again.

## Coalescing and history

Each applied commit is one undo step, unless `coalesceKey` merges it into the previous step: when the previous recorded commit had the identical key and either the key starts with `gesture:` (one drag, however long) or it came less than 1 second earlier (a burst of typing or nudging). Undo and redo end any merge. A commit that changes nothing records nothing.

**A `coalesceKey` names one gesture of one client.** The engine compares keys only for equality and doesn't know which client sent them, so two clients (or two tabs of one) that both sent `gesture:7` would merge each other's steps. A client therefore puts a random part, chosen once per client instance, in every key, and a counter or the gesture's own id after it: `gesture:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:7` for its seventh drag, `name:5b0e8f0c-3c1d-4b8e-a7f2-1d9c6e4a2b7f:3` for typing a name. Keys are at most 128 characters.

Undo history is kept in memory, bounded by a count and by bytes: the last 100 steps, within 64 MiB per document ([limits](limits.md)). **The byte cap wins**: steps are evicted oldest first, so a document with large steps (whole scenes) may keep fewer than 100. It is not saved. Undo, redo and revert are separate endpoints and are document changes like commits: each that takes a step is a new revision ([below](#undo-redo-revert-and-save)).

## Results

**200** (`commitResult`):

```jsonc
{ "rev": 43, "items": { "cpu-bar": 43 }, "removed": [], "profileRev": 43,
  "dirty": true, "canUndo": true, "canRedo": false, "warnings": [] }
```

`items` has the new `rev` of every item the commit changed or added; `removed` the ids it removed (children included); `profileRev` is set when the profile changed ([what a change stamps](#what-a-change-stamps)). A commit that changed nothing answers with the unchanged `rev` and empty `items`. Undo, redo, revert and save answer with the same body ([below](#undo-redo-revert-and-save)).

`warnings` lists content the engine accepted but can't draw as written: code `unsupported` for a scene that needs a newer format or a capability the engine lacks. The engine keeps such a scene exactly as sent and draws a placeholder ([unsupported content](unsupported.md)).

**422** (`invalidProblem`): the commit breaks the format, a rule or a limit; nothing was applied. Each error has a JSON pointer into the request body, a code from [validation](validation.md) and a message:

```jsonc
{ "status": 422, "code": "invalid", "errors": [ { "path": "/ops/0/scene/nodes/0/w", "code": "ref.input", "message": "…" } ] }
```

A client must not write invalid content; an engine tolerates it only in documents it reads ([unsupported content](unsupported.md)).

## Undo, redo, revert and save

```http
POST /api/profiles/{id}/undo
POST /api/profiles/{id}/redo
POST /api/profiles/{id}/revert
POST /api/profiles/{id}/save
```

Undo, redo and revert take a body (`historyRequest`) with the state the client's request is based on, both members required:

```jsonc
{ "instance": "7c2f9a41d0e34b8f9e6a5b3c1d2e4f60", "baseRev": 43 }
```

The engine checks, in order, and stops at the first that fails; nothing is done when one fails:

1. The body: one that breaks the schema is 422 (`invalidProblem`).
2. `instance`: when it isn't the current instance, 409 `conflictProblem` with one conflict, `{ "reason": "instance" }`.
3. `baseRev`: when the document's `rev` isn't `baseRev`, 409 `conflictProblem` with a `changed` conflict, without `op`, for each item (its `id`, current `rev` and the `fields` that changed) and for the profile (no `id`) with a field group whose last change is after `baseRev`, as a commit's conflicts list them (`examples/api/conflictProblem/undo-changed.json`). Items removed since aren't listed, and an undo, redo or revert step may change no field group, so the list can be empty.
4. Otherwise the request acts, and answers **200** with a `commitResult`, as a commit does.

They have no `opId`: a client that gets a 409, or loses the answer, reads the state (`GET /api/profiles/{id}`, or the next `doc` event, [API](api.md#change-events)) and decides again. A retry of one that was done finds the document at a newer revision and is refused, so a retry never undoes, redoes or reverts twice.

Save takes no body and isn't guarded. It changes neither content nor revision, only which version is the saved one, and the version it saves is the current one, which every client shares: there is no stale state it could act on, and saving again saves the same document. A client that loses the answer to a save saves again.

- **Undo** restores the document as it was before the last recorded step, whichever client made it: a document has one history, shared by every client. **Redo** applies the last undone step again; a commit that changes the document clears what could be redone. **Revert** restores the saved document, as one new step that can itself be undone.
- Each of the three, when it takes a step, is a new revision, even when the content comes out equal: `rev` goes up; `items` has every item with a field group that differs from the version it replaced, or that it brought back, with the new `rev`; `removed` the items it took out (children included); `profileRev` is set when a profile field differs ([what a change stamps](#what-a-change-stamps)). Those field groups get the new `rev` as their last-change revision, so a later op whose `baseRev` is older conflicts with them ([above](#baserev-conflicts)).
- **Save** makes the current document the saved one. It changes neither content nor revision: `rev` is unchanged, `items` empty, `dirty` false.
- An undo with nothing to undo, a redo with nothing to redo, and a revert of a document that isn't dirty change nothing: they answer with the unchanged `rev` and empty `items`, and record nothing.
- `replayed` and `warnings` are never set.

## What the engine checks when it applies a commit

1. **The body, without the document**: its size, its collection counts (`limit.ops`, and `limit.items` for an inserted item with its children), its schema, and each new scene and inserted item on its own ([validation](validation.md)), with errors at their paths in the request. A scene that passes validation but that the engine cannot compile is refused like an invalid one, with an error at that scene's path in the request (`/ops/{i}/scene`, or the scene's path under `/ops/{i}/item`). Any failure is 422 at once, with nothing else checked. This runs for every request, a retry included, before the `opId` is looked up.
2. `opId`: a remembered one with the same body, byte for byte, returns its stored result (`replayed: true`); with another body it is 422 `opId.reused`.
3. `instance`: a different one is 409 (one conflict, `reason: "instance"`), with nothing else checked.
4. The retry window: once the engine has forgotten any `opId` of the document, a `baseRev` older than the window's start is 409 `expiredProblem`, with nothing else checked ([retries](#opid-retries)).
5. The ops, **in order, against a tentative copy of the document**, each applied to it before the next is checked:
   - the target (and a `move`'s or `insert`'s parent) exists at that point, else a conflict (`missing`);
   - its `baseRev` (the document revision the client read) against the field groups' last-change revisions as of the start of the commit (for `remove`, the item's and every descendant's): one greater than `baseRev` is a conflict (`changed`);
   - inserted ids are new (`id.duplicate`), `setDesign` and `setBindings` target scene items (`op.target`), a parent is a group (`op.parent`) and not the moved item or inside it (`op.cycle`);
   - the copy with the op applied stays within `groupDepth` (`limit.groupDepth`: an `insert` or `move` that nests items deeper than it allows) and the aggregate limits ([limits](limits.md#document-limits): `limit.documentNodes`, `limit.documentWork`, `limit.documentTextNodes`, `limit.documentEffectCost`; an op is refused when after it some item is over one of them, counted in document order as when the document is drawn, that was not over it before the op, so an item already over one in a stored document doesn't stop other edits). These errors are at `/ops/{i}`, the op's path.

   An op that fails is not applied to the copy, and the engine goes on with the next op, so that every conflict is found. Because each op is checked as it applies, a commit that swaps an item for a larger one removes the old one before it inserts the new one.
6. **The result**: the document's item count, groups' children included (`limit.items`), and its size (`limit.documentBytes`), with errors at `""`.

If anything failed in step 5 or 6 the whole commit is rolled back (the tentative copy is dropped) and nothing is recorded: **409** when any op conflicted, listing every conflict, otherwise **422** with every error. A 422 lists at most 100 errors, then, when there were more, one last error with code `more` and path `""` that counts the rest ([validation](validation.md)). Otherwise the copy becomes the document, as one revision and one undo step, or, when it doesn't differ from the document, as nothing ([what a change stamps](#what-a-change-stamps)).
