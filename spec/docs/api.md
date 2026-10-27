# Engine API

Schema: `urn:infopanel:api:1` (`schemas/api.schema.json`). Examples: `examples/api/`, one folder per definition; each `invalid/` folder holds bodies its definition refuses, or that break a check beyond the schema ([below](#checks-beyond-the-schema)).

The engine serves a REST API under `/api` and a preview socket at `/ws`. This page specifies the bodies that carry the formats, the tokens that give access to them, and the engine behaviour behind them; commits, undo, redo, revert and save have their own page ([commits](commit.md)). Members are camelCase, and errors are `application/problem+json`. A client ignores members it doesn't know in what the engine sends ([versioning](versioning.md#the-engine-api)).

Every request below needs a token ([authentication](#authentication)): without a valid one the engine answers 401, and to a token without the scope the request needs, 403 `scopeProblem`.

| Request | Body | Answer |
|---|---|---|
| `GET /api/session` | | `session` |
| `GET /api/profiles` | | An array of `profileEntry`, in display order |
| `POST /api/profiles` | `createProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` ([below](#creating-and-duplicating)) |
| `POST /api/profiles/{id}/duplicate` | `duplicateProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` |
| `DELETE /api/profiles/{id}` | | 204 ([below](#deleting)) |
| `GET /api/profiles/{id}` | | `documentResponse` ([below](#documents)) |
| `GET /api/profiles/{id}/document` | | The document, to keep or import elsewhere ([below](#exporting)) |
| `GET /api/profiles/{id}/thumbnail` | | A PNG ([below](#thumbnails)) |
| `POST /api/profiles/{id}/commit` | `commitRequest` | `commitResult`; 409 `conflictProblem` or `expiredProblem`; 422 `invalidProblem` ([commits](commit.md)) |
| `POST /api/profiles/{id}/undo`, `…/redo`, `…/revert` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` ([commits](commit.md#undo-redo-revert-and-save)) |
| `POST /api/profiles/{id}/save` | | `commitResult` |
| `POST /api/blobs` | The bytes | `blobInfo`: 201 when the blob is new, 200 when it was already stored; 413 over `mediaBytes` |
| `HEAD /api/blobs/{id}`, `GET /api/blobs/{id}` | | The blob's bytes; 404 when it isn't stored |
| `POST /api/tokens` | `createTokenRequest` | 201 `createdToken`; 422 `invalidProblem` ([below](#integration-tokens)) |
| `GET /api/tokens` | | An array of `tokenInfo`, oldest first |
| `DELETE /api/tokens/{id}` | | 204; 404 when there is no such token |
| `/ws`, the `doc` message | | `docEvent` |
| `/ws`, a frame message | | A `frameHeader`, then the frame |

A request on a profile is checked in this order, and the first failure answers: the profile exists (else 404); a request that reads, changes or draws its document needs a profile that can be opened (else 409 `profileStatusProblem`, [below](#profiles)); the body matches its definition and the formats (else 422 `invalidProblem`, at most 100 errors and then one `more`, [validation](validation.md)); then what the request itself checks.

## Authentication

Every request under `/api` carries a token, as `Authorization: Bearer {token}`. The preview socket takes it as the subprotocol `auth.{token}` offered with `infopanel.v1` or, when no `auth.*` subprotocol is offered, as its first message, `{"type": "auth", "token": "…"}`, within 5 seconds. There are two kinds of token:

- The **session** token is made by the engine each time it starts, or given on its command line, and has full access. The engine shows it only in the sign-in link it prints (`/auth/{token}`) and in the pairing link for a client served from another origin, and never stores it.
- **Integration** tokens are for scripts and other programs. They are made with the session token ([below](#integration-tokens)), kept by the engine across restarts until they are revoked, and limited to the scopes they were made with.

| Scope | Allows |
|---|---|
| `read` | Every `GET` and `HEAD` under `/api` (the session, fonts, sensors, the profile list, documents, exports, thumbnails, blobs) and the preview socket, `doc` messages and frames included |
| `edit` | Every request that changes a profile, a document or a blob: creating, duplicating and deleting profiles, commits, undo, redo, revert, save and `POST /api/blobs`. `edit` implies `read`: a token made with `edit` has both |

Only the session token signs in (`GET /auth/{token}` refuses any other token as an invalid link, so an integration token can never be exchanged for the session token) and uses the token endpoints. Scopes fail closed: a request that neither scope covers needs the session token.

A request without a token, or with one the engine doesn't know or has revoked, is answered 401 with `WWW-Authenticate: Bearer`. A valid token without the scope the request needs is answered 403 `scopeProblem`, whose `scope` is what it lacks: `read`, `edit`, or `session` for what only the session token may do. Both are `application/problem+json`.

### Integration tokens

`POST /api/tokens` (`createTokenRequest`) makes a token: `name` is for people (1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end; names needn't be unique), and `scopes` is `read`, `edit` or both. The answer, 201 `createdToken`, has the token's `id` and its secret, `token`: `ipi_` and 43 base64url characters (256 random bits). That answer is the only place the secret appears: the engine keeps only its SHA-256 and never writes the secret to its logs, so a lost secret can't be recovered; the token is revoked and another one made. A body that breaks `createTokenRequest` is answered 422 `invalidProblem`, and no token is made.

`GET /api/tokens` lists the integration tokens, oldest first, as `tokenInfo`: `id`, `name`, `kind` (`integration`), `scopes` (`read`, and `edit` when it has it) and `created`, with `lastUsed` once the token has authenticated a request (kept to within a minute). It has no secrets and never lists the session token.

`DELETE /api/tokens/{id}` revokes a token and answers 204, or 404 when there is no token with that id. Revocation takes effect at once: the next request with the token is answered 401, and every preview socket authenticated with it is closed with status 1008 and the reason `token revoked`. Every other connection stays open.

## Session

`GET /api/session` describes the engine and this run of it:

- `protocol` is the engine API's version: 2 for the API these schemas describe. It goes up only when the API changes in a way a client can't ignore; members and endpoints are added without changing it.
- `instance` is new every time the engine starts. Revisions (a document's `rev`, an item's, a commit's `baseRev`) are only comparable within one instance: those read from an earlier instance mean nothing to the current one, and a commit, undo, redo or revert that names an old instance is refused ([commits](commit.md#baserev-conflicts)).
- `formatSupport` is what the engine reads: formats, the newest minor version of each, optional capabilities and the limits ([versioning](versioning.md)).
- `engineVersion`, `platform` (`macos`, `windows`, `linux` or `other`) and `dataDir` (the folder the engine keeps its data in) describe the engine.

## Profiles

`GET /api/profiles` lists every profile in display order. Each entry's `status` says whether the engine can open its document, and which members come with it (one schema definition per status, `profileEntryOk`, `profileEntryUnsupported` and `profileEntryUnreadable`):

| `status` | Meaning | Members besides `id` and `status` |
|---|---|---|
| `ok` | The engine opens the document. A document with invalid or unsupported scenes is `ok`: those items are placeholders ([unsupported content](unsupported.md)). | `name`, `width`, `height`, `rev`, `dirty` |
| `unsupported` | The document needs a format major version (`unsupported.format`, at `/format`) or a document capability (`unsupported.capability`, at `/requires/i`) this engine lacks. | `problem`; `name` when the document's `profile.name` is a string of 1–256 characters |
| `unreadable` | The stored file can't be read (`io`), isn't JSON (`json`), or breaks the document schema or a document limit outside its scenes (the document check's code and path, [validation](validation.md)). | `problem` |

`problem` is the first problem found, `{ path, code, message }`, with a JSON pointer into the stored document. A profile that isn't `ok` is never opened, changed or rewritten: it is listed, and it can be deleted and exported as stored ([unsupported content](unsupported.md#document-level)). Every other request on its document answers 409 `profileStatusProblem`, whose `code` is the status and whose `problem` is the entry's.

### Creating and duplicating

`POST /api/profiles` creates a profile; `POST /api/profiles/{id}/duplicate` copies one. Both take the new profile's `id`, chosen by the client: a document id, a UUID in lower case ([document](document.md)). An `id` that already exists answers 409 `existsProblem` and creates nothing, so a client that lost the answer to a create or duplicate sends it again: 201 means this attempt created it, 409 `exists` that an earlier one did (the client reads `GET /api/profiles` to see it).

A create's body (`createProfileRequest`) is one of:

- `{ "id", "name", "width", "height" }`: an empty profile, a document with that name and canvas size and no items.
- `{ "id", "document" }`: a profile from a document (`infopanel.document/1`), in the shape a create takes (`importDocument`): the document format with its `rev` members optional and every scene taken as stored (`storedScene`), so a document exported by `GET /api/profiles/{id}/document`, scenes drawn as placeholders included, can be created again. The request's other members are checked against its schema, and `document` as a stored document is, in the same order ([validation](validation.md#order)): a scene that is invalid, unsupported or over an aggregate limit doesn't refuse it, it is kept exactly as sent and drawn as a placeholder, as is a scene the engine cannot compile ([unsupported content](unsupported.md)). A document the engine wouldn't open is refused: 422, with the document check's errors at their paths under `/document` (`unsupported.format` at `/document/format`, `unsupported.capability` at `/document/requires/i`, or what makes it invalid outside its scenes). The document's own `id` is replaced by the request's `id`, and its `rev` members are ignored.

A duplicate's body (`duplicateProfileRequest`) is `{ "id" }`. The copy is the profile's current document (its latest revision, saved or not), with the same name, content and item ids. Duplicating a profile that isn't `ok` answers 409 `profileStatusProblem`.

The new profile is stored and saved at once, is added at the end of the list, and starts with its own revisions and an empty history (`dirty`, `canUndo` and `canRedo` false). Both answer **201** with its `profileEntry`.

### Deleting

`DELETE /api/profiles/{id}` deletes the profile, whatever its status, with its saved and unsaved document and its history, and answers **204**. It takes no body and isn't guarded: it removes the profile for every client. A profile that doesn't exist answers 404, so a client that lost the answer and deletes again gets 404 for the profile it deleted.

### Exporting

`GET /api/profiles/{id}/document` answers the profile's document, for a client to keep or to create a profile from elsewhere:

- for an `ok` profile, the current document (its latest revision, saved or not) as `application/json`: `infopanel.document/1` without its `rev` members, every scene and every other opaque member (`source` data, `origin`, `editor`) exactly as stored;
- for an `unsupported` or `unreadable` profile, the stored file byte for byte, as `application/octet-stream`. A profile whose file can't be read at all (`io`) answers 409 `profileStatusProblem`.

### Thumbnails

`GET /api/profiles/{id}/thumbnail?w={width}` answers a PNG of the current document, drawn as an output draws it: from the tick a running output of the profile last drew, without advancing it, or else from a one-off snapshot ([identity](identity.md#the-tick-snapshot)). `w` is the width in pixels, 320 when absent, clamped to 16–4,096; the height is the canvas height in proportion, rounded to the nearest pixel and at least 1. When that height would be over 4,096, the height is 4,096 and the width the canvas width in proportion, rounded and at least 1. A profile that isn't `ok` answers 409 `profileStatusProblem`.

## Documents

`GET /api/profiles/{id}` answers `documentResponse`: the current document (the latest revision, saved or not), the document's state (`rev`, the same as `document.rev`; `dirty`, `canUndo`, `canRedo`) and the `instance` its revisions belong to. The document is in the shape the engine returns (`responseDocument`): `infopanel.document/1` with every revision member set (the `rev` of the document, of the profile and of every item), and every scene exactly as stored (`storedScene`). A scene the engine draws normally matches the scene schema; one it draws as a placeholder (it needs a newer format or a capability the engine lacks, breaks a rule or limit, or can't be compiled) comes back unchanged and may not, so a client checks a scene before it reads it as one. The `rev` read here is what a commit's `baseRev` is based on ([commits](commit.md)).

## Blobs

Media assets and any other files a document needs are blobs, named by their content: `sha256:` and the SHA-256 of their bytes in 64 lower-case hex digits ([document](document.md#content-identity)).

`POST /api/blobs`, with the bytes as the body (of any content type), stores them and answers `blobInfo`. A body over `mediaBytes` (32 MiB, [limits](limits.md#media-limits-checked-from-the-header-before-decoding)) is refused with 413 and nothing is stored. Any other body is stored, and the answer has the `id`, the size in `bytes` and what the engine found when it inspected the bytes as a media asset, from the header and without decoding ([validation](validation.md)), as one of two definitions:

- `blobInfoMedia`, with `media`, when they are a media asset within the media limits: `format` (`png`, `jpeg`, `gif`, `webp` or `svg`), `width` and `height` in pixels (an SVG's from its `width` and `height` or its `viewBox`, [SVG](svg.md)), `frames` (1 for a still image) and `duration` in seconds (0 for a still image);
- `blobInfoMediaError`, with `mediaError`, otherwise: the media check's first error, with path `""` (`media.format` for bytes that aren't media at all, another `media.*` code, or a `limit.*` code: a media limit other than `mediaBytes`, such as `limit.mediaSide`).

So blobs that aren't media (a client's description of how it produced a scene, listed in a source's `deps`) are stored like any other, and so is media over a limit other than its size in bytes. Storing bytes that are already stored answers the same `blobInfo` with 200. Media that names a blob with a `mediaError`, or one that isn't stored, draws the missing-media placeholder.

`HEAD /api/blobs/{id}` answers 200 with the blob's size, and `GET /api/blobs/{id}` with its bytes exactly as they were stored, as `application/octet-stream`; both answer 404 for a blob that isn't stored.

The engine keeps every blob that a retained version of a document refers to (the current one, the saved one, any in the undo history; through a source's `deps` or a scene's media, [document](document.md#source-and-origin)). It may delete any other blob, but not within 24 hours of the last successful `POST` of its bytes: every `POST /api/blobs` that answers 201 or 200 starts the 24 hours again, so a blob can be stored, or stored again, before the commit that refers to it. `HEAD` and `GET` don't.

## The preview socket

A client subscribes to a document on `/ws` at a scale and frame rate. The engine sends text messages, JSON objects with a `type`, and binary frame messages.

### Change events

The `doc` message (`docEvent`) is the subscribed document's state: `rev`, `dirty`, `canUndo`, `canRedo`. The engine sends it when the subscription starts and after every change to the document or to its state: every commit, undo, redo, revert and save that changes either, whichever client made it. It keeps only the latest state to send, so a client that falls behind gets the newest one and may skip revisions, and the last `doc` message it gets is always the current state. It carries no content: the document at that revision is read with `GET /api/profiles/{id}`.

### Frames

A frame message is binary: the four bytes `IPF1`, the header's length N as an unsigned 32-bit little-endian integer, N bytes of UTF-8 JSON (`frameHeader`), then the frame in the header's `encoding` (`png`: the whole frame as a PNG).

```jsonc
{ "profileId": "3dd19c0e-675a-4591-b096-83417853861f", "rev": 43, "tick": 1820, "frameId": 5113,
  "encoding": "png", "scale": 2, "pixelWidth": 1600, "pixelHeight": 960, "profileWidth": 800, "profileHeight": 480,
  "itemBounds": [ { "id": "cpu-bar", "frame": { "x": 20, "y": 60, "w": 240, "h": 20 }, "ink": { … }, "rotation": 0 } ] }
```

- `rev` is the document revision drawn and `tick` the tick it was drawn from. The pixels and `itemBounds` come from that one tick snapshot ([identity](identity.md#the-tick-snapshot)), so the bounds always describe the pixels they arrive with, at `rev`. Within one subscription, frames with the same `tick` were drawn from the same snapshot, and tick numbers never go down.
- `frameId` is unique within one run of the engine and greater for a frame rendered later.
- `scale` is the frame's pixels per profile pixel, `pixelWidth` and `pixelHeight` the frame's size, and `profileWidth` and `profileHeight` the canvas size at `rev` ([frame size](#frame-size)).
- `itemBounds` holds the bounds of every visible item that has bounds ([geometry](geometry.md#measured-bounds-reported-by-the-engine)), each id once, in document order: a group before its children, the first item at the back. A hidden item, everything in a hidden group and a group with no visible children are absent. They are in profile pixels, like the document.

The engine sends a frame only when its pixels, its bounds or its revision changed.

#### Frame size

The frame is the canvas drawn at `scale`: the point (x, y) of the canvas, in profile pixels, is at (x × `scale`, y × `scale`) in the frame, whose top-left corner is the canvas's (0, 0). Item bounds, and anything else in profile pixels, map to frame pixels the same way: multiply by `scale`.

`scale` comes from the subscription's scale, s:

1. When s isn't a positive finite number, it is 1.
2. It is rounded to a step of 0.25 (s ÷ 0.25 rounded to the nearest integer, halves away from zero, times 0.25), then clamped to 0.1–4. So below 0.125 the scale is 0.1, and from 3.875 it is 4.
3. While the frame at that scale would have more than 16,777,216 pixels (16 Mpx, [limits](limits.md#run-time-limits)), counted after rounding each side as below, a scale that is a multiple of 0.25 is lowered by 0.25. Every canvas within the document's limits (each side at most 16,384) fits at 0.25 and at 0.1, so `scale` is always 0.1 or a multiple of 0.25 from 0.25 to 4.

Each side of the frame is the canvas side times `scale`, rounded to the nearest integer, and at least 1:

- at a multiple of 0.25, k × 0.25, the side is side × k ÷ 4 rounded to the nearest integer, halves to the even integer (so 10 at 0.25 is 2, and 6 at 0.25 is 2);
- at 0.1, the side is side ÷ 10 rounded to the nearest integer, halves up (so 25 is 3), because the engine multiplies by 0.1 in single precision, a little over 0.1.

`pixelWidth` × `pixelHeight` is at most 16,777,216 after this rounding. Since each side is rounded, `pixelWidth` ÷ `profileWidth` can differ slightly from `scale`, and the frame's last column or row can cover a little more or less than the canvas: a client maps between the two with `scale`, never with the ratio of the sizes.

## Checks beyond the schema

Some agreements between members of an engine body can't be written in JSON Schema. The engine keeps them, and the examples check them, with the code in `examples/expected.json` ([validation](validation.md#codes)):

- `rev.mismatch`: a `documentResponse`'s `rev` is its `document.rev`, and no `rev` in the document (the profile's, an item's) is greater; a `commitResult`'s `items` values and `profileRev` all equal its `rev` (they changed at that revision).
- `id.duplicate`: a `frameHeader`'s `itemBounds` lists each id once.
