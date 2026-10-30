# Versioning

## Formats

| Format | String | Schema id |
|---|---|---|
| Document | `infopanel.document/1` | `urn:infopanel:document:1` |
| Scene | `infopanel.scene/1` | `urn:infopanel:scene:1` |
| Profile archive (its manifest, [archive](archive.md)) | `infopanel.archive/1` | `urn:infopanel:archive:1` |
| Expressions, shared definitions | (part of the scene and document formats) | `urn:infopanel:expression:1`, `urn:infopanel:common:1` |
| Engine API: its bodies, requests, the preview socket's messages and the frame header; the 1.x items of a 1.x import's answer | (versioned with the engine API, [below](#the-engine-api)) | `urn:infopanel:api:1`, `urn:infopanel:v1item:1` |

The format string names the **major** version. Within a major version there are **minor** versions (1.0, 1.1, ...), identified by the capabilities they add. The schema files always describe the newest minor version; each addition says in its description which version and capability introduced it.

## What freezes

Until the first engine release, every format here is a draft and may change in any way. That release freezes them all at once: every format an engine stores, or that passes between versions of the engine and of the clients that talk to it, which is every format in the table above and every one this specification adds before that release. After it, each changes only as this page allows.

A major version's limits ([limits](limits.md)) are frozen with it, and so are its error codes and the paths they are reported at ([below](#error-codes-and-paths)). The engine's budgets are not part of the formats ([limits](limits.md)), so they are not frozen.

## Minor versions

A minor version may only **add**, and every addition belongs to a named capability:

- node kinds, expression operations, paint, effect and transform types;
- optional members, whose absence means exactly what it meant before;
- values of any enumeration, required discriminators included (a new `kind`, `type` or `op`, a new `fit` or `blend` mode); content that uses a new value lists its capability in `requires`, so an engine without it treats the content as unsupported before it reads the value. Existing values never change meaning.

A minor version never removes or renames anything, never changes a default or what existing content means or how it is drawn (beyond fixing a bug against this specification), never makes an optional member required and never changes a limit. Limits are fixed for a major version: every engine that reads version 1 accepts everything within version 1's limits.

### Shipping a minor version

Each addition ships with:

1. its capability, named as below and listed in this page's table with the minor version that defines it;
2. the schema change, whose description names that version and capability;
3. the docs that specify it;
4. examples: a valid one, and an invalid one for each new rule or limit, with its code and path in `examples/expected.json`;
5. the regenerated types (`types/index.d.ts`);
6. the engine's `formatSupport` ([API](api.md#session)): the new minor version in the format's entry, and the capability there when the engine has it.

## Capabilities and `requires`

A capability is a lower-case dotted name (`media.provider`). A scene lists in `requires` (at most 32, each once) every capability it uses beyond version 1.0, a document every document capability, and a profile archive's manifest every archive capability ([archive](archive.md#the-manifest)); an engine refuses a scene that uses one without listing it (`requires.missing`), so `requires` is always complete.

An engine that lacks a capability in `requires` doesn't try to read the rest: the item (or document) is **unsupported** ([unsupported content](unsupported.md)). That is why unknown members can always be refused as invalid: any member a newer minor version adds comes with a capability, which an older engine sees first.

Capabilities defined by version 1.0:

| Capability | Format | What |
|---|---|---|
| `media.provider` | scene | Media from providers configured on the engine (`{ "provider", "ref" }` sources). Optional: an engine may lack it. |

Everything else in version 1.0 is the base every engine supports. Version 1.0 defines no document or archive capability.

An engine reports what it supports in `GET /api/session` (`urn:infopanel:api:1#/$defs/formatSupport`), by format string: for each format it reads, the newest minor version, the format's optional capabilities it has and the format's limits. So an engine that read `infopanel.scene/1` at 1.2 and `infopanel.scene/2` at 2.0 would list each with its own. A client checks the format's entry before writing content that needs a capability.

## Major versions

Anything else (removing or renaming, changing meaning, changing limits) needs a new major version, with a new format string (`infopanel.scene/2`). A new major version never strands what a user has saved:

- an engine keeps content in a major version it can't read exactly as stored ([unsupported content](unsupported.md)), and never converts content from one major version to another on its own, because that would rewrite it silently;
- the engine that brings a new major version offers the user an upgrade that converts the content losslessly and keeps the original;
- it keeps reading the earlier major version only where that conversion can't be exact.

Until a second major version exists, nothing in an engine prepares for one.

## Unknown members and values

Closed where the engine interprets, tolerant where a client reads what an engine of another version sent:

- **Closed:** content (scenes and documents), request bodies and the preview socket's client messages. The engine refuses a member it doesn't know, so an addition sent to an engine that predates it fails out loud instead of being ignored. Additions to content come under a capability, which a reader checks before anything else ([above](#capabilities-and-requires)).
- **Tolerant:** what the engine sends, that is response bodies, the preview socket's server messages and its frame header. A client ignores members it doesn't know and server messages whose `type` it doesn't know, and treats an HTTP status it doesn't expect by its class (4xx: refused, 5xx: the engine failed). Every enumeration in what the engine sends is open: a later engine may add a value, and the definition's description says what a client does with a value it doesn't know. The schemas list today's members and values, closed, for the engine's own conformance tests; a client reads them openly.

## Error codes and paths

What the engine refuses or can't do, it reports with a `code` for programs, a `path` into what it checked, and text for people: a 422's errors and every other `{ path, code, message }` ([validation](validation.md)), such as a profile's `problem` and a blob's `mediaError`; the `code` of a commit's warning and of every problem body ([API](api.md#errors)); and the preview socket's `error` messages ([API](api.md#errors-on-the-socket)). These rules hold for all of them, and the other pages point here:

- **Codes freeze.** A code keeps its meaning for as long as the version that defines it: a format's codes with its major version, the engine API's with the API's version (`protocol`). A minor version, or the engine API within its version, may add codes, never remove, rename or reuse one. Every engine of that version reports the same content's errors with the same codes at the same paths.
- **`path` is exact.** It is the JSON pointer ([RFC 6901](https://www.rfc-editor.org/rfc/rfc6901)) to what the error is about, in what was checked (a request body, a stored document, a client message); `""` when the error is about the whole (a whole-content limit, a value that isn't JSON) or when there is no content to point into (a media asset, a socket error that answers no message). A missing member is at the object it is missing from. The examples pin every code they show to its path (`examples/expected.json`), and are compared exactly, never by prefix.
- **Text is for people.** A `message`, a problem's `title` and `detail`, may change in any release. A client never reads meaning from it.
- **A code a client doesn't know** is handled by its family, the part before its first dot: an unknown `limit.*` as content over a limit, an unknown `unsupported.*` as content that needs a newer engine, an unknown `media.*` as bytes that can't be used as media, an unknown `profile.*` on the socket as a subscription that didn't start or has ended. A code whose family it doesn't know either is handled by the answer's HTTP status class (4xx: refused, nothing done; 5xx: the engine failed), or, on the socket, as a message the engine didn't act on.
- **Close reasons are a fixed list.** The preview socket closes only with the statuses and reasons [listed](api.md#connecting), each reason a fixed text. A later version may add one; a client handles a reason it doesn't know by its close status.

## The engine API

The engine API's bodies (session, sensors, profile list, profile creation and duplication, document, blob, commit, undo, redo and revert, result, problems, token, the engine settings and a change of them, the engine's recent log entries, the displays, the 1.x import's answers and its migration record, the preview socket's messages, frame header and change event) and its requests (`openapi.json`: each operation, its parameters and the statuses it answers) are versioned with the engine API, which reports its version in `GET /api/session` (`protocol`, [API](api.md#session)). Within a version it only adds, compatibly: operations, statuses an operation may answer, optional members, values of the enumerations in what it sends, socket message types, and error codes ([above](#error-codes-and-paths)). A member added later to a request body or a client message is refused by an engine that predates it ([above](#unknown-members-and-values)). The 1.x items in a 1.x import's answer (`urn:infopanel:v1item:1`) are generated from the engine's 1.x kind manifest; the 1.x models are frozen, so they don't grow, and a client skips an item of a kind it doesn't know ([1.x import](import.md#items)).

Three identifiers name the API's layers, each changing on its own. `protocol` (2, in `GET /api/session` and the socket's `hello`) is the API's version: what its requests, bodies and messages mean; it goes up only for a change a client can't ignore. `infopanel.v1`, the preview socket's subprotocol, names how the socket carries messages: JSON text messages both ways, binary frame messages from the engine. `IPF1`, the first four bytes of every frame message, names a frame message's layout: the magic, the header's length, the JSON header, then the payload. A change to the socket's framing or to the frame layout would take a new subprotocol or a new magic; neither has rules for growing within itself yet.

## Four markers, four questions

Four markers say what a reader may do, each answering its own question; folding two into one would hide different failures.

| Marker | The question it answers |
|---|---|
| A scene's or document's `requires` | Can this engine read this saved content? |
| The data folder's marker, with its own `requires` | Is it safe for this engine to touch this data folder? |
| A document's tag (`instance`) in a request, with `baseRev` | Is this request about the document I read? |
| `protocol`, and named API features | Can this engine do this operation? |

- **Content `requires`** (a scene's, a document's, a profile archive's manifest's). A scene that draws media from a provider configured on the engine lists `media.provider`; an engine without it draws the item as a placeholder and keeps the scene exactly as stored. An engine lists the capabilities it has, per format, in `formatSupport`.
- **The data folder's marker.** The data folder isn't part of these formats: it is the engine's own, documented with the engine (its `docs/2.0/data-folder.md`). Its marker, `store.json` at the folder's root (`{"format": "infopanel.store/1", "requires": []}`), lists what an engine must understand before it repairs, sweeps or writes anything there: a later engine that kept blob references outside the profiles' folders would list a capability, so an earlier engine stops at start-up, changing nothing, instead of deleting blobs it doesn't see referenced. A file a later engine keeps in a profile's own folder needs none: an engine counts every file there as referring to the blobs it names. Version 1.0 of the folder defines no capabilities.
- **A document's tag.** Every document has a lifetime tag, `instance`, new every time the engine creates or opens it ([API](api.md#a-documents-tag)). A commit, undo, redo, revert or delete names the tag of the document it read; one that names another is refused (409, reason `instance`) instead of acting on a document it never saw. A profile deleted and imported again under the same id has a new tag, so a delayed commit or delete carrying the old one is refused instead of acting on the new profile. The session's `instance` answers another question: did the engine restart?
- **API features.** `protocol` goes up only for a change a client can't ignore. An operation added after version 1 comes under a feature name, listed in `GET /api/session` (`features`, [API](api.md#session)), and a client offers it only when the engine lists it: an operation that restored a profile's earlier save, say, would come as `profiles.restore`, and an engine that predates it neither lists the name nor serves the request. Version 1 defines no features, so the list is empty.

Choosing one: a new saved member, node kind or value is a content capability; a new file or folder holding blob references outside a profile's folder, a data-folder capability; a new operation, an API feature; a new guard on an existing request, the document's tag or `baseRev`, never a new marker.
