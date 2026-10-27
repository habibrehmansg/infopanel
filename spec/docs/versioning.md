# Versioning

## Formats

| Format | String | Schema id |
|---|---|---|
| Document | `infopanel.document/1` | `urn:infopanel:document:1` |
| Scene | `infopanel.scene/1` | `urn:infopanel:scene:1` |
| Expressions, shared definitions | (part of the scene and document formats) | `urn:infopanel:expression:1`, `urn:infopanel:common:1` |
| Engine API bodies | (versioned with the engine API) | `urn:infopanel:api:1` |

The format string names the **major** version. Within a major version there are **minor** versions (1.0, 1.1, ...), identified by the capabilities they add. The schema files always describe the newest minor version; each addition says in its description which version and capability introduced it.

Until the engine release that first ships version 1, the formats are a draft and may change in any way. After it, these rules hold.

## Minor versions

A minor version may only **add**, and every addition belongs to a named capability:

- node kinds, expression operations, paint, effect and transform types;
- optional members, whose absence means exactly what it meant before;
- values of any enumeration, required discriminators included (a new `kind`, `type` or `op`, a new `fit` or `blend` mode); content that uses a new value lists its capability in `requires`, so an engine without it treats the content as unsupported before it reads the value. Existing values never change meaning.

A minor version never removes or renames anything, never changes a default or what existing content means or how it is drawn (beyond fixing a bug against this specification), never makes an optional member required and never changes a limit. Limits are fixed for a major version: every engine that reads version 1 accepts everything within version 1's limits.

## Capabilities and `requires`

A capability is a lower-case dotted name (`media.provider`). A scene lists in `requires` (at most 32, each once) every capability it uses beyond version 1.0, and a document every document capability; an engine refuses a scene that uses one without listing it (`requires.missing`), so `requires` is always complete.

An engine that lacks a capability in `requires` doesn't try to read the rest: the item (or document) is **unsupported** ([unsupported content](unsupported.md)). That is why unknown members can always be refused as invalid: any member a newer minor version adds comes with a capability, which an older engine sees first.

Capabilities defined by version 1.0:

| Capability | Format | What |
|---|---|---|
| `media.provider` | scene | Media from providers configured on the engine (`{ "provider", "ref" }` sources). Optional: an engine may lack it. |

Everything else in version 1.0 is the base every engine supports.

An engine reports what it supports in `GET /api/session` (`urn:infopanel:api:1#/$defs/formatSupport`): the format strings it reads, the newest minor version of each, its optional capabilities and its limits. A client checks it before writing content that needs a capability.

## Major versions

Anything else (removing or renaming, changing meaning, changing limits) needs a new major version, with a new format string (`infopanel.scene/2`). An engine may read several major versions. It never converts content from one to another on its own, because that would rewrite content silently; a client may offer the user to.

## The engine API

The engine API bodies (session, sensors, profile list, profile creation and duplication, document, blob, commit, undo, redo and revert, result, problems, token, the preview socket's messages, frame header and change event) and its requests (`openapi.json`: each operation, its parameters and the statuses it answers) are versioned with the engine API, which reports its version in `GET /api/session` (`protocol`, [API](api.md#session)). Within a version it only adds, compatibly: operations, statuses an operation may answer, optional members, and socket message types. A client ignores members it doesn't know in what the engine sends, and socket messages whose `type` it doesn't know, and treats a status it doesn't expect by its class (4xx: refused, 5xx: the engine failed). The engine refuses members it doesn't know in a request body; in a preview socket message it ignores them.
