# Engine API

Schema: `urn:infopanel:api:1` (`schemas/api.schema.json`). Requests: `openapi.json` (OpenAPI 3.1.1), every request below with its parameters and every status it answers, by these definitions. Examples: `examples/api/`, one folder per definition; each `invalid/` folder holds bodies its definition refuses, or that break a check beyond the schema ([below](#checks-beyond-the-schema)).

The engine serves a REST API under `/api` and a preview socket at `/ws`. This page specifies the bodies that carry the formats, the tokens that give access to them, and the engine behaviour behind them; commits, undo, redo, revert and save have their own page ([commits](commit.md)). Members are camelCase, and errors are `application/problem+json` ([errors](#errors)). Request bodies and the socket's client messages are closed, and what the engine sends is read tolerantly ([versioning](versioning.md#unknown-members-and-values)).

Every request under `/api` needs a token ([authentication](#authentication)): without a valid one the engine answers 401, and to a token without the scope the request needs, 403 `scopeProblem`. The preview socket takes its token its own way ([below](#connecting)); `GET /healthz` and `GET /auth/{token}` need none.

| Request | Body | Answer |
|---|---|---|
| `GET /api/session` | | `session` |
| `GET /api/fonts` | | An array of font family names ([below](#fonts-and-sensors)) |
| `GET /api/sensors` | | An array of `sensorEntry` ([below](#fonts-and-sensors)) |
| `GET /api/sensors/values` | | An array of `sensorValue`; 400 for a `history` that isn't a whole number from −2,147,483,648 to 2,147,483,647 |
| `GET /api/profiles` | | An array of `profileEntry`, in display order |
| `POST /api/profiles` | `createProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` ([below](#creating-and-duplicating)) |
| `POST /api/profiles/{id}/duplicate` | `duplicateProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` |
| `DELETE /api/profiles/{id}` | | 204 ([below](#deleting)) |
| `GET /api/profiles/{id}` | | `documentResponse` ([below](#documents)) |
| `GET /api/profiles/{id}/document` | | The document, to keep or import elsewhere ([below](#exporting)) |
| `GET /api/profiles/{id}/thumbnail` | | A PNG; 400 for a `w` that isn't a whole number from −2,147,483,648 to 2,147,483,647 ([below](#thumbnails)) |
| `POST /api/profiles/{id}/commit` | `commitRequest` | `commitResult`; 409 `conflictProblem` or `expiredProblem`; 422 `invalidProblem` ([commits](commit.md)) |
| `POST /api/profiles/{id}/undo` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` ([commits](commit.md#undo-redo-revert-and-save)) |
| `POST /api/profiles/{id}/redo` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` |
| `POST /api/profiles/{id}/revert` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` |
| `POST /api/profiles/{id}/save` | | `commitResult` |
| `POST /api/blobs` | The bytes | `blobInfo`: 201 when the blob is new, 200 when it was already stored; 413 over `mediaBytes` |
| `HEAD /api/blobs/{id}` | | 200 with the blob's size; 404 when it isn't stored |
| `GET /api/blobs/{id}` | | The blob's bytes; 404 when it isn't stored |
| `POST /api/tokens` | `createTokenRequest` | 201 `createdToken`; 422 `invalidProblem` ([below](#integration-tokens)) |
| `GET /api/tokens` | | An array of `tokenInfo`, oldest first |
| `DELETE /api/tokens/{id}` | | 204; 404 when there is no such token |
| `GET /ws` | | The preview socket: 101, then `serverMessage`s and frames ([below](#the-preview-socket)) |
| `GET /healthz` | | 200 `ok` as `text/plain`, without a token |
| `GET /auth/{token}` | | 302 to `/#token={token}`; 401 for any token but the session token ([below](#signing-in)) |

A request on a profile is checked in this order, and the first failure answers: the profile exists (else 404); a request that reads, changes or draws its document needs a profile that can be opened (else 409 `profileStatusProblem`, [below](#profiles)); the body matches its definition and the formats (else 422 `invalidProblem`, at most 100 errors and then one `more`, [validation](validation.md)); then what the request itself checks.

## Errors

Every error under `/api` is `application/problem+json`: the definition the request names in `openapi.json` for its status, or else `problem`, the HTTP layer's error (`title`, `status`, and `type`, `detail` and `traceId` when given). Its codes and paths are part of the API, and its `title` and `detail` text for people ([versioning](versioning.md#error-codes-and-paths)). Besides what the table lists, a request answers:

- 401 `problem` without a valid token, on every request under `/api` and on `/ws`;
- 403 `scopeProblem` to a token without the scope a request that changes something needs (`edit`), or that only the session token may make (`session`); every token has `read`;
- 404 `problem` on a request that names a profile, blob or token that doesn't exist;
- 400 `problem` for a query parameter that isn't a whole number from −2,147,483,648 to 2,147,483,647 (`w`, `history`);
- 500 `problem` when the engine fails; the error is in its log.

A path under `/api` that isn't a request above, or a method a path doesn't take, is answered 404 (or 405) `problem`. Under `/ws/` and `/auth`, a path that isn't `/ws` or `/auth/{token}` is answered 404 with no body.

Before any of these, the engine checks where a request comes from ([below](#where-requests-come-from)): a `Host` that isn't a loopback name is answered 400 with no body, and an `Origin` the engine doesn't accept 403 `problem`.

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

### Signing in

`GET /auth/{token}` with the session token answers 302 to `/#token={token}`, so a browser opened from the sign-in link lands on the client with the token in the fragment, which is never sent to a server. Any other token, an integration token included, is answered 401 with a short `text/plain` explanation. Neither answer is cached (`Cache-Control: no-store`), and no cookie is set.

`GET /healthz` answers 200 with the text `ok` (`text/plain`) to anyone: a client can poll it to see that the engine runs before it has a token.

### Where requests come from

The engine listens on the loopback interface only, and answers a request whose `Host` isn't `localhost`, `127.0.0.1` or `[::1]` (any port) with 400 and no body. A request with an `Origin` (a browser's, WebSocket upgrades included) must come from the engine's own origin or one the engine was started to accept; any other is answered 403 `problem`, without CORS headers. An origin the engine was started to accept as a separate client gets CORS on `/api` and `/healthz`: its preflights (`OPTIONS` with `Access-Control-Request-Method`) are answered 204 with no body before the token check, and every other answer carries `Access-Control-Allow-Origin`.

### Integration tokens

`POST /api/tokens` (`createTokenRequest`) makes a token: `name` is for people (1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end; names needn't be unique), and `scopes` is `read`, `edit` or both. The answer, 201 `createdToken`, has the token's `id` and its secret, `token`: `ipi_` and 43 base64url characters (256 random bits). That answer is the only place the secret appears: the engine keeps only its SHA-256 and never writes the secret to its logs, so a lost secret can't be recovered; the token is revoked and another one made. A body that breaks `createTokenRequest` is answered 422 `invalidProblem`, and no token is made.

`GET /api/tokens` lists the integration tokens, oldest first, as `tokenInfo`: `id`, `name`, `kind` (`integration`), `scopes` (`read`, and `edit` when it has it) and `created`, with `lastUsed` once the token has authenticated a request (kept to within a minute). It has no secrets and never lists the session token.

`DELETE /api/tokens/{id}` revokes a token and answers 204, or 404 when there is no token with that id. Revocation takes effect at once: the next request with the token is answered 401, and every preview socket authenticated with it is closed with status 1008 and the reason `token revoked`. Every other connection stays open.

## Session

`GET /api/session` describes the engine and this run of it:

- `protocol` is the engine API's version: 2 for the API these schemas describe. It goes up only when the API changes in a way a client can't ignore; members and endpoints are added without changing it ([versioning](versioning.md#the-engine-api)).
- `instance` is new every time the engine starts. Revisions (a document's `rev`, an item's, a commit's `baseRev`) are only comparable within one instance: those read from an earlier instance mean nothing to the current one, and a commit, undo, redo or revert that names an old instance is refused ([commits](commit.md#baserev-conflicts)).
- `formatSupport` is what the engine reads: formats, the newest minor version of each, optional capabilities and the limits ([versioning](versioning.md)).
- `engineVersion`, `platform` (`macos`, `windows`, `linux` or `other`) and `dataDir` (the folder the engine keeps its data in) describe the engine.

## Fonts and sensors

`GET /api/fonts` answers the font families a scene's text can name, as an array of strings: the engine's own families (Inter, and the fonts in its data folder's `fonts/`) first, then the system's, each sorted, each family once whatever its case.

`GET /api/sensors` answers every sensor the engine's sources offer, as an array of `sensorEntry`: its `key`, which a binding names ([bindings](bindings.md#bindings)); its `source`, `group`, `name` and `unit`, for people; and its `status` now: `live`, `stale` (the last value kept), `unavailable`, `permission-denied` (the operating system refused the engine access to it), `simulated` or `missing` (the key isn't well formed). A client treats a status it doesn't know as no reading.

`GET /api/sensors/values?history={n}` answers a `sensorValue` for every listed sensor that has a reading now, in list order: its `key`, `now`, `min`, `max` and `avg` (a value that isn't a finite number is 0), `unit`, `text` for a text or table sensor, and `status`, `live` or `stale`. `history` is a whole number, clamped to 0–4,096 (0 when absent): with 1 or more, each number sensor also has its last `history` samples at most, oldest first, one a second ([bindings](bindings.md#history)). A `history` that isn't a whole number from −2,147,483,648 to 2,147,483,647 is answered 400 `problem`.

## Profiles

`GET /api/profiles` lists every profile in display order. Each entry's `status` says whether the engine can open its document, and which members come with it (one schema definition per status, `profileEntryOk`, `profileEntryUnsupported` and `profileEntryUnreadable`):

| `status` | Meaning | Members besides `id` and `status` |
|---|---|---|
| `ok` | The engine opens the document. A document with invalid or unsupported scenes is `ok`: those items are placeholders ([unsupported content](unsupported.md)). | `name`, `width`, `height`, `rev`, `dirty` |
| `unsupported` | The document needs a format major version (`unsupported.format`, at `/format`) or a document capability (`unsupported.capability`, at `/requires/i`) this engine lacks. | `problem`; `name` when the document's `profile.name` is a string of 1–256 characters |
| `unreadable` | The stored file can't be read (`io`), isn't JSON (`json`), or breaks the document schema or a document limit outside its scenes (the document check's code and path, [validation](validation.md)). | `problem` |

`problem` is the first problem found, `{ path, code, message }`, with a JSON pointer into the stored document. A profile that isn't `ok` is never opened, changed or rewritten: it is listed, and it can be deleted and exported as stored ([unsupported content](unsupported.md#document-level)). Every other request on its document answers 409 `profileStatusProblem`, whose `code` is the status and whose `problem` is the entry's. A client treats a status it doesn't know as a profile it can't open.

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

`GET /api/profiles/{id}/thumbnail?w={width}` answers a PNG of the current document, drawn as an output draws it: from the tick a running output of the profile last drew, without advancing it, or else from a one-off snapshot ([identity](identity.md#the-tick-snapshot)). `w` is the width in pixels, 320 when absent, clamped to 16–4,096; the height is the canvas height in proportion, rounded to the nearest pixel and at least 1. When that height would be over 4,096, the height is 4,096 and the width the canvas width in proportion, rounded and at least 1. A `w` that isn't a whole number from −2,147,483,648 to 2,147,483,647 is answered 400 `problem`. A profile that isn't `ok` answers 409 `profileStatusProblem`.

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

A client subscribes to a document on `/ws` at a scale and frame rate. The engine sends text messages, JSON objects with a `type` (`serverMessage`), and binary frame messages; the client sends text messages (`clientMessage`).

### Connecting

`GET /ws` is a WebSocket upgrade with the subprotocol `infopanel.v1`, and the token either as a second subprotocol, `auth.{token}`, or, when no `auth.*` subprotocol is offered, as the first message (`authMessage`, `{"type": "auth", "token": "…"}`) within 5 seconds. The token is the session token or an integration token. The engine accepts the socket with `infopanel.v1` alone, so the token is never echoed. An upgrade without `infopanel.v1`, or with an `auth.*` subprotocol whose token isn't valid, is answered 401 `problem`; a request to `/ws` that isn't an upgrade, 400 `problem`.

The engine closes a socket only with these statuses and reasons, a fixed list ([versioning](versioning.md#error-codes-and-paths)):

| Status | Reason | When |
|---|---|---|
| 1008 | `auth timeout` | No `auth` message within 5 seconds of the upgrade (the engine sends nothing before it) |
| 1008 | `auth rejected` | The `auth` message's token isn't valid |
| 1008 | `auth expected` | The first message isn't an `auth` message, with no members but `type` and `token` (or isn't text, is over 4,096 bytes, or isn't JSON as the formats read it, [below](#messages)) |
| 1008 | `token revoked` | The integration token the socket was opened with was revoked; every other socket stays open |
| 1009 | `Messages are at most 4096 bytes.` | A client message over 4,096 bytes in UTF-8 |
| 1001 | `The engine is stopping.` | The engine stops |
| 1000 | | The client closed the socket |

After an `auth` failure, the engine waits at most 2 seconds for the client's close before dropping the connection.

### Messages

A client sends JSON text messages of at most 4,096 bytes in UTF-8 (`clientMessage`):

- `subscribe` (`subscribeMessage`): `profileId`, and optionally `scale` (frame pixels per profile pixel, 1 when absent, [frame size](#frame-size)) and `maxFps` (30 when absent; the engine draws at most 60). A subscription the engine starts ends any earlier one, and is answered with a `doc` message, then frames; one it can't start is answered with an `error` and leaves the earlier one as it was.
- `view` (`viewMessage`): a new `scale` or `maxFps` for the current subscription; a member left out keeps its value.
- `unsubscribe` (`unsubscribeMessage`): no more `doc` messages or frames until the next `subscribe`.
- `auth` (`authMessage`): only as the first message, above.

The engine reads each message as the formats read JSON ([validation](validation.md#json)) before reading any member of it, members it doesn't define included: a message nested deeper than `jsonDepth`, with a number that rounds to ±∞, an object that repeats a member name (never resolved by keeping one) or a string or member name that isn't Unicode text is one it can't act on. Client messages are closed ([versioning](versioning.md#unknown-members-and-values)): a message with a member its definition doesn't have is one the engine can't act on, and it acts on no part of it. It answers `error` to a message it can't act on and keeps the socket open: one that isn't JSON as the formats read it or isn't a JSON object with a string `type`, a binary message, an unknown `type`, a member the message's definition doesn't have, a `subscribe` without a string `profileId`, a `scale` that isn't a positive number or a `maxFps` that isn't a whole number from 1 to 2,147,483,647, or a `view` without a subscription.

The engine sends (`serverMessage`):

- `hello` (`helloMessage`): first, once the socket is authenticated, with `protocol` ([session](#session)).
- `doc` (`docEvent`): the subscribed document's state ([below](#change-events)).
- `profiles` (`profilesMessage`): the profile list changed (a profile was created, duplicated or deleted, by any client); the client reads `GET /api/profiles`. `version` is greater for a later change; the engine keeps only the newest to send.
- `error` (`errorMessage`): a message the engine couldn't act on, or a subscription to a profile that doesn't exist, can't be opened or previewed, or was deleted (which also ends the subscription), with its `code` and `path` ([below](#errors-on-the-socket)) and a `message` for people.

#### Errors on the socket

An `error` names what went wrong with a `code`, and with a `path`: the JSON pointer to what it is about in the client message it answers, `""` for the whole message and for an error that answers none. Codes and paths are part of the API, and a client handles a code it doesn't know by its family, else as a message the engine didn't act on ([versioning](versioning.md#error-codes-and-paths)).

| Code | Path | When |
|---|---|---|
| `json` | The value, or `""` | The message isn't JSON as the formats read it ([validation](validation.md#json)): it isn't UTF-8 JSON text, or is binary (at `""`); a number rounds to ±∞ (at the number); a string isn't Unicode text (at the string; a member name's at its object) |
| `limit.jsonDepth` | `""` | The message is nested deeper than `jsonDepth` |
| `schema` | The value | The message isn't an object with a `type` (at `""`); its `type` isn't a string, or is a type the engine doesn't take (at `/type`); it has a member its definition doesn't (at the member) or repeats a member name (at its object); a `subscribe` has no `profileId` (at `""`) or one that isn't a string (at `/profileId`); a `scale` that isn't a positive number (at `/scale`); a `maxFps` that isn't a whole number from 1 to 2,147,483,647 (at `/maxFps`) |
| `subscription.missing` | `""` | A `view` without a subscription |
| `profile.missing` | `/profileId` | A `subscribe` to a profile that doesn't exist |
| `profile.unsupported`, `profile.unreadable` | `/profileId` | A `subscribe` to a profile that can't be opened: `profile.` and its status ([profiles](#profiles)) |
| `profile.deleted` | `/profileId`, or `""` | The profile was deleted: at `/profileId` as the `subscribe` started, else at `""`. The subscription has ended |
| `profile.unrenderable` | `/profileId`, or `""` | The engine can't draw the profile: it can't be drawn within the frame limit at any scale ([frame size](#frame-size)), or the engine is stopping (the socket then closes with 1001). At `/profileId` as the `subscribe` started, else at `""`. The subscription has ended |

A `profile.*` code means there is no subscription to that profile. One at `""`, which ends a subscription, is always about the latest one: it is never sent once a later `subscribe` has started another.

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
