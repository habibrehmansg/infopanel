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
| `GET /api/settings` | | `engineSettings` ([below](#settings)) |
| `PATCH /api/settings` | `settingsChange` | `engineSettings`; 409 `settingsStatusProblem`; 422 `invalidProblem` ([below](#changing-settings)) |
| `GET /api/logs` | | `logPage`; 400 for an `after` that isn't a whole number from 0 to 9,007,199,254,740,991 ([below](#logs)) |
| `POST /api/folders/{folder}/open` | | 204; 404 for a `folder` other than `data` and `logs`; 500 when the engine can't open it ([below](#folders)) |
| `GET /api/displays` | | `displayList`; 500 when the engine can't list the displays ([below](#displays)) |
| `GET /api/profiles` | | An array of `profileEntry`, in display order |
| `POST /api/profiles` | `createProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` ([below](#creating-and-duplicating)) |
| `POST /api/profiles/{id}/duplicate` | `duplicateProfileRequest` | 201 `profileEntry`; 409 `existsProblem`; 422 `invalidProblem` |
| `DELETE /api/profiles/{id}` | | 204; 400 `problem` without `instance`, the document's tag; 409 `deleteConflictProblem` for another tag ([below](#deleting)) |
| `GET /api/profiles/{id}` | | `documentResponse` ([below](#documents)) |
| `GET /api/profiles/{id}/document` | | The document, to keep or import elsewhere ([below](#exporting)) |
| `GET /api/profiles/{id}/archive` | | The profile archive (`.infopanel2`): its manifest, document and blobs; 409 `profileStatusProblem` or `exportProblem` ([archive](archive.md#exporting)) |
| `GET /api/profiles/{id}/thumbnail` | | A PNG; 400 for a `w` that isn't a whole number from −2,147,483,648 to 2,147,483,647 ([below](#thumbnails)) |
| `POST /api/profiles/{id}/commit` | `commitRequest` | `commitResult`; 409 `conflictProblem` or `expiredProblem`; 422 `invalidProblem` ([commits](commit.md)) |
| `POST /api/profiles/{id}/undo` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` ([commits](commit.md#undo-redo-revert-and-save)) |
| `POST /api/profiles/{id}/redo` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` |
| `POST /api/profiles/{id}/revert` | `historyRequest` | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` |
| `POST /api/profiles/{id}/save` | `historyRequest`, optional | `commitResult`; 409 `conflictProblem`; 422 `invalidProblem` ([commits](commit.md#undo-redo-revert-and-save)) |
| `POST /api/blobs` | The bytes | `blobInfo`: 201 when the blob is new, 200 when it was already stored; 413 over `videoBytes` |
| `HEAD /api/blobs/{id}` | | 200 with the blob's size; 404 when it isn't stored |
| `GET /api/blobs/{id}` | | The blob's bytes; 404 when it isn't stored |
| `GET /api/import/v1` | | `v1Migration`: the 1.x migration's state and the 1.x folder ([1.x import](import.md#migrating-a-1x-folder)) |
| `PUT /api/import/v1` | `v1MigrationRequest` | `v1Migration`; 422 `invalidProblem` ([1.x import](import.md#the-record)) |
| `POST /api/import/v1/profiles/{v1Id}` | | `v1Profile`; 404 when the 1.x folder has no such profile; 422 `invalidProblem` for 1.1.x data ([1.x import](import.md#the-envelope)) |
| `POST /api/import/archive` | A profile archive's bytes | `archiveContents`; 413 over `archiveBytes`; 422 `invalidProblem` ([archive](archive.md#importing)) |
| `POST /api/import/v1/archive` | A 1.x `.infopanel` file's bytes | `v1Profile`; 413 over `archiveBytes`; 422 `invalidProblem` ([1.x import](import.md#reading-a-infopanel-file)) |
| `POST /api/tokens` | `createTokenRequest` | 201 `createdToken`; 422 `invalidProblem` ([below](#integration-tokens)) |
| `GET /api/tokens` | | An array of `tokenInfo`, oldest first |
| `DELETE /api/tokens/{id}` | | 204; 404 when there is no such token |
| `GET /ws` | | The preview socket: 101, then `serverMessage`s and frames ([below](#the-preview-socket)) |
| `GET /healthz` | | 200 `ok` as `text/plain`, without a token |
| `GET /auth/{token}` | | 302 to `/#token={token}`; 401 for any token but the session token ([below](#signing-in)) |

A request on a profile is checked in this order, and the first failure answers: its query parameters (else 400 `problem`); the profile exists (else 404); a request that reads, changes or draws its document needs a profile that can be opened (else 409 `profileStatusProblem`, [below](#profiles)); the body matches its definition and the formats (else 422 `invalidProblem`, at most 100 errors and then one `more`, [validation](validation.md)); then what the request itself checks.

A request body is held to its size limit as received, whitespace around the JSON value included, before it is parsed ([limits](limits.md#request-limits)): an undo's, redo's, revert's, save's or duplicate's to `requestBytes` (64 KiB), a token create's to `tokenRequestBytes` (4 KiB), a settings change's to `settingsRequestBytes` (512 KiB), a 1.x migration record's to `recordBytes` (1 MiB) and a commit's to `commitBytes`. A body over its limit is answered 422 `invalidProblem` with one error, `limit.<name>` at `""`. A create's body is read up to `documentBytes` plus 64 KiB, then its document is held to `documentBytes` ([sizes](limits.md#sizes)); a blob's bytes are held to `videoBytes` and an archive's to `archiveBytes` (413).

## Errors

Every error under `/api` is `application/problem+json`: the definition the request names in `openapi.json` for its status, or else `problem`, the HTTP layer's error (`title`, `status`, and `type`, `detail` and `traceId` when given). Its codes and paths are part of the API, and its `title` and `detail` text for people ([versioning](versioning.md#error-codes-and-paths)). Besides what the table lists, a request answers:

- 401 `problem` without a valid token, on every request under `/api` and on `/ws`;
- 403 `scopeProblem` to a token without the scope a request that changes something needs (`edit`), or that only the session token may make (`session`); every token has `read`;
- 404 `problem` on a request that names a profile, blob, token or folder that doesn't exist;
- 400 `problem` for a query parameter that isn't a whole number from −2,147,483,648 to 2,147,483,647 (`w`, `history`), for an `after` that isn't one from 0 to 9,007,199,254,740,991 ([below](#logs)), and for a `DELETE` of a profile without one `instance` of 1–64 characters ([below](#deleting));
- 500 `problem` when the engine fails; the error is in its log.

A path under `/api` that isn't a request above, or a method a path doesn't take, is answered 404 (or 405) `problem`. Under `/ws/` and `/auth`, a path that isn't `/ws` or `/auth/{token}` is answered 404 with no body.

Before any of these, the engine checks where a request comes from ([below](#where-requests-come-from)): a `Host` that isn't a loopback name is answered 400 with no body, and an `Origin` the engine doesn't accept 403 `problem`.

## Authentication

Every request under `/api` carries a token, as `Authorization: Bearer {token}`. The preview socket takes it as the subprotocol `auth.{token}` offered with `infopanel.v1` or, when no `auth.*` subprotocol is offered, as its first message, `{"type": "auth", "token": "…"}`, within 5 seconds. There are two kinds of token:

- The **session** token is made by the engine each time it starts, or given on its command line, and has full access. The engine shows it only in the sign-in link it prints (`/auth/{token}`) and in the pairing link for a client served from another origin, and never stores it.
- **Integration** tokens are for scripts and other programs. They are made with the session token ([below](#integration-tokens)), kept by the engine across restarts until they are revoked, and limited to the scopes they were made with.

| Scope | Allows |
|---|---|
| `read` | Every `GET` and `HEAD` under `/api` but the session token's own (below): the session, fonts, sensors, the displays, the profile list, documents, exports and archives, thumbnails, blobs, the 1.x migration's state; and the preview socket, `doc` messages and frames included |
| `edit` | Every request that changes a profile, a document or a blob, or the engine's data: creating, duplicating and deleting profiles, commits, undo, redo, revert, save, `POST /api/blobs`, opening a profile archive, and the 1.x import's reads (which store blobs) and record. `edit` implies `read`: a token made with `edit` has both |

Only the session token signs in (`GET /auth/{token}` refuses any other token as an invalid link, so an integration token can never be exchanged for the session token), uses the token endpoints, reads and changes the engine settings ([below](#settings)), reads the engine's recent log entries ([below](#logs)) and opens the engine's folders ([below](#folders)). Scopes fail closed: a request that neither scope covers needs the session token.

Narrower access, such as a token that sees only some profiles, would come as a token kind of its own in a later version: `read` and `edit` keep allowing everything this table says.

A request without a token, or with one the engine doesn't know or has revoked, is answered 401 with `WWW-Authenticate: Bearer`. A valid token without the scope the request needs is answered 403 `scopeProblem`, whose `scope` is what it lacks: `read`, `edit`, or `session` for what only the session token may do. Both are `application/problem+json`.

### Signing in

`GET /auth/{token}` with the session token answers 302 to `/#token={token}`, so a browser opened from the sign-in link lands on the client with the token in the fragment, which is never sent to a server. Any other token, an integration token included, is answered 401 with a short `text/plain` explanation. Neither answer is cached (`Cache-Control: no-store`), and no cookie is set.

`GET /healthz` answers 200 with the text `ok` (`text/plain`) to anyone: a client can poll it to see that the engine runs before it has a token.

### Where requests come from

The engine listens on the loopback interface only, and answers a request whose `Host` isn't `localhost`, `127.0.0.1` or `[::1]` (any port) with 400 and no body. A request with an `Origin` (a browser's, WebSocket upgrades included) must come from the engine's own origin or one the engine was started to accept; any other is answered 403 `problem`, without CORS headers. An origin the engine was started to accept as a separate client gets CORS on `/api` and `/healthz`: its preflights (`OPTIONS` with `Access-Control-Request-Method`) are answered 204 with no body before the token check, and every other answer carries `Access-Control-Allow-Origin`.

### Integration tokens

`POST /api/tokens` (`createTokenRequest`) makes a token: `name` is for people (1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end; names needn't be unique), and `scopes` is `read`, `edit` or both. The answer, 201 `createdToken`, has the token's `id` and its secret, `token`: `ipi_` and 43 base64url characters (256 random bits). That answer is the only place the secret appears: the engine keeps only its SHA-256 and never writes the secret to its logs, so a lost secret can't be recovered; the token is revoked and another one made. A body that breaks `createTokenRequest` is answered 422 `invalidProblem`, and no token is made; so is a create that would take the engine's stored tokens over `tokensBytes` ([limits](limits.md#stored-list-limits)), with one error, `limit.tokensBytes` at `""`, until tokens are revoked.

`GET /api/tokens` lists the integration tokens, oldest first, as `tokenInfo`: `id`, `name`, `kind` (`integration`), `scopes` (`read`, and `edit` when it has it) and `created`, with `lastUsed` once the token has authenticated a request (kept to within a minute). It has no secrets and never lists the session token.

`DELETE /api/tokens/{id}` revokes a token and answers 204, or 404 when there is no token with that id. Revocation takes effect at once: the next request with the token is answered 401, and every preview socket authenticated with it is closed with status 1008 and the reason `token revoked`. Every other connection stays open.

## Session

`GET /api/session` describes the engine and this run of it:

- `protocol` is the engine API's version: 2 for the API these schemas describe. It goes up only when the API changes in a way a client can't ignore; members and endpoints are added without changing it ([versioning](versioning.md#the-engine-api)).
- `instance` is new every time the engine starts: a client that sees another (here or in the preview socket's `hello`) knows the engine restarted, and that the document tags and revisions it read before mean nothing now. It is the run's, not a document's: a commit names the tag of the document it changes ([below](#a-documents-tag)).
- `formatSupport` is what the engine reads, by format string (the profile archive's manifest names one too, [archive](archive.md)): for each format, `version`, the newest minor version of it the engine reads (and every earlier one of that major version); `capabilities`, the format's optional capabilities the engine has (`media.video` only when it has a video decoder); and `limits`, the format's limits by name ([limits](limits.md), which says which format each belongs to). An engine of this specification lists `infopanel.document/1`, `infopanel.scene/1` and `infopanel.archive/1`, each at 1.0 or later. Before it sends content, a client checks that its format string is listed, that every capability in its `requires` is in that format's `capabilities` ([versioning](versioning.md#capabilities-and-requires)), and that it is within that format's limits. A client ignores a format string it doesn't know.
- `features` lists the optional API features the engine has, by name, each once, named as capabilities are. An operation added after this version of the API comes under one, and a client uses the operation only when the session lists its name ([versioning](versioning.md#four-markers-four-questions)). This version defines none, so the list is empty. A client ignores a name it doesn't know.
- `locale` is the PC's regional format as the engine read it when it started: `tag` (its language and region, `und` when the engine knows no language for it), `decimalSeparator` and `groupSeparator` (each 1–4 characters). A profile without `locale` draws numbers and dates with it ([expressions](expressions.md#regional-format)); a client that formats readings of its own uses it so they match the frames. It changes only when the engine restarts.
- `started` is when this run of the engine started, in UTC to the second (`2026-10-29T09:30:00Z`). Like `instance`, it changes only when the engine restarts.
- `media.providers` lists the media providers configured on the engine, by name, each once ([providers](providers.md)): `http` for web images. A client offers a provider source only for a provider listed here, and ignores a name it doesn't know; `media.provider` in the scene format's `capabilities` says only that providers exist. It changes only when the engine restarts.
- `mock` is true when the engine was started in mock mode: its sensors are simulated (a mock source's, and `simulated` ones for keys no source has, [below](#fonts-and-sensors)) rather than the computer's, so a client can say its readings aren't real. It changes only when the engine restarts.
- `engineVersion`, `engineBuild`, `platform`, `osVersion`, `arch` and `dataDir` describe the engine, for people and for bug reports:
  - `engineBuild` is the source revision the engine was built from: the commit's abbreviated id as git writes it (`004cf52`), followed by `-dirty` when the build had changes not yet committed, or `unknown` when the build couldn't tell (built without git or outside a repository, as from a source archive);
  - `platform` is the operating system (`macos`, `windows`, `linux` or `other`), and `osVersion` its version as it reports it: macOS's product version (`26.1`), Windows's major.minor.build (`10.0.26100`), Linux's kernel release (`6.8.0-45-generic`), or `unknown` when the engine can't read it;
  - `arch` is the processor architecture the engine runs as, `x64`, `arm64`, `x86`, `arm` or `other`: the engine's own, so an engine emulated on another architecture reports the one it was built for;
  - `dataDir` is the folder the engine keeps its data in.

  A client shows `engineBuild` and `osVersion` as given, and treats a `platform` or `arch` it doesn't know as `other`.

## Fonts and sensors

`GET /api/fonts` answers the font families a scene's text can name, as an array of strings: the engine's own families (Inter, and the fonts in its data folder's `fonts/`) first, then the system's, each sorted, each family once whatever its case.

`GET /api/sensors` answers every sensor the engine's sources offer, as an array of `sensorEntry`: its `key`, which a binding names ([bindings](bindings.md#bindings)); its `source`, `group`, `name` and `unit`, for people; and its `status` now: `live`, `stale` (the last value kept), `unavailable`, `denied` (the operating system refused the engine access to it), `simulated` or `missing` (the key isn't well formed). These are a scene input's spellings where the meaning is the same; [bindings](bindings.md#status) maps each to the status an input bound to the sensor reads. A client treats a status it doesn't know as no reading.

`GET /api/sensors/values?history={n}` answers a `sensorValue` for every listed sensor that has a reading now, in list order: its `key`, `now`, `min`, `max` and `avg` (a value that isn't a finite number is 0), `unit`, `text` for a text or table sensor, and `status`, `live` or `stale`. `history` is a whole number, clamped to 0–4,096 (0 when absent): with 1 or more, each number sensor also has its last `history` samples at most, oldest first, one every `graphRate` milliseconds ([settings](#settings), one a second by default; [bindings](bindings.md#history)). A `history` that isn't a whole number from −2,147,483,648 to 2,147,483,647 is answered 400 `problem`.

## Settings

`GET /api/settings` answers the engine settings in effect, `engineSettings`: each setting's value, or its default when none is set. Only the session token reads and changes them ([authentication](#authentication)).

| Setting | Values | Default | What it does |
|---|---|---|---|
| `frameRate` | 1–60 | 15 | Frames a second for every output without a rate of its own (a profile's desktop window). The preview socket keeps its own rate, a subscription's `maxFps` |
| `graphRate` | 100–3000 | 1000 | Milliseconds between the samples of the sensor history ([bindings](bindings.md#history), a sensor value's `history`) |
| `logLevel` | `debug`, `information`, `warning`, `error` | `information` | The least severe entries the engine writes to its log |
| `sources.libre.enabled` | `true`, `false` | `true` | Whether the LibreHardwareMonitor sensor source (on Windows) reads sensors |
| `sources.libre.storage` | `true`, `false` | `true` | Whether it reads storage drives |
| `sources.libre.storageInterval` | 1–300 | 30 | Seconds between its reads of storage drives |
| `media.localNetwork` | `true`, `false` | `true` | Whether media providers may connect to this computer and the local network ([providers](providers.md#addresses)) |
| `clients.{name}` | Any JSON value but `null` | none | A client's own preferences ([below](#client-preferences)) |

A change takes effect at once: outputs re-time to the new frame rate, the history takes its next sample at the new rate and keeps the samples it has, the log follows the new level, and media providers fetch every reference again under the new `media.localNetwork`. An engine keeps the source settings whether or not it has that source.

`version` counts the changes this run of the engine made to the stored settings: 0 when it starts, one more after each change that changed them ([below](#changing-settings)). It belongs to the run the session's `instance` names: after a restart it starts again at 0, so a client that sees another `instance` (here or in `hello`) reads the settings again.

`status` says whether the engine could read its stored settings when it started: `ok`; `unreadable` when it couldn't (`problem` says why, with a JSON pointer into them: `io`, `json`, `schema`, `limit.settingsBytes`, `limit.settingsDepth`); or `unsupported` when they are another major version's (`unsupported.format` at `/format`). Unless it is `ok`, the defaults are in effect, the engine leaves the stored settings as they are, and every change is refused with 409 `settingsStatusProblem` (its `code` is the status and its `problem` the settings') until they are fixed or removed and the engine restarts. A client treats a status it doesn't know as settings it can't change, and shows the `problem` when one comes.

### Changing settings

`PATCH /api/settings` changes the settings its body (`settingsChange`) names and answers the settings in effect, as `GET` does. A value sets a setting, `null` sets it back to its default, and a setting left out keeps its value; `sources`, `sources.libre`, `media` and `clients` are merged member by member. The change is stored before the answer, all of it or none of it. No revision guards it: two changes to different settings never conflict, and of two changes to the same setting the later one stays. A change is one to the stored settings, not only to the values in effect: a value equal to its setting's default is stored, and from then on the setting keeps it rather than following the default (an engine with another default keeps it too); `null` removes a stored value, so the default applies again. Either changes the stored settings, and counts in `version`, though the value in effect stays the same. A change that leaves the stored settings as they were (a setting given the value it already has, the same number spelled otherwise included, `null` for a setting that isn't set, or `{}`) keeps `version` as it was and sends no message; it rewrites the stored file only to finish an earlier write whose folder flush failed.

The engine checks, in order, and the first failure answers, nothing changed:

1. The settings can be changed (`status` is `ok`), else 409 `settingsStatusProblem`.
2. The body, as every body: within `settingsRequestBytes` as received, then JSON as the formats read it and `settingsChange` ([validation](validation.md#order)); closed, so a member the engine doesn't know is `schema` at that member. Else 422 `invalidProblem`.
3. The body's nesting within `settingsDepth` (`limit.settingsDepth` at `""`), and each client's preferences within `settingsClientBytes` (`limit.settingsClientBytes` at `/clients/{name}`), else 422.
4. The settings it makes: at most `settingsClients` clients (`limit.settingsClients` at `/clients`), and within `settingsBytes` as the engine stores them (`limit.settingsBytes` at `""`), else 422 ([limits](limits.md#settings-limits)).

After each change that changed the stored settings, every preview socket authenticated with the session token gets a `settings` message ([below](#messages)).

### Client preferences

`clients` keeps each client's own preferences with the engine, by a name the client picks (1–64 letters, digits, dots, underscores and hyphens), so that they last as long as the engine's data, whatever the client's own storage keeps. Each is any JSON value but `null`, and the engine stores it as the same value and never reads it. A change sets a client's preferences whole, or removes them with `null`, and keeps the other clients'. A client changes only its own name's.

## Logs

`GET /api/logs?after={seq}` answers the engine's recent log entries, `logPage`: `entries`, oldest first, and `latest`, the `seq` of the newest entry this run of the engine has logged, kept or not (0 before the first). Only the session token reads them ([authentication](#authentication)). The engine keeps this run's newest 2,000 entries, and at most 2 MiB of their text, dropping the oldest first. They are exactly what it writes to its log file, at the `logLevel` setting ([settings](#settings)): never what it shows only on its console, such as the sign-in and pairing links, which carry the session token.

Each entry (`logEntry`) has `seq`, its number (1 for the run's first entry, one more for each after it); `time`, when it was logged, in RFC 3339 with milliseconds and the engine's offset from UTC (`2026-10-30T09:30:00.125+08:00`); `level`, `verbose`, `debug`, `information`, `warning`, `error` or `fatal`; `source`, the part of the engine that logged it, or empty; `message`; and `exception`, the error it records, when there is one. `source`, `message` and `exception` are text for people, never a contract: a client shows them as given and never parses them. A source longer than 256 characters, a message longer than 4,096 and an exception longer than 8,192 are cut, ending in "…". A client treats a level it doesn't know as `information`.

With `after`, only the entries whose `seq` is greater are answered; without it, every kept entry. A client polls with `after` set to the `latest` it last read and adds what comes to what it has; when the first entry answered isn't `after` + 1, the engine dropped the entries between. `seq` and `latest` belong to the run the session's `instance` names: after a restart they start again, so a client that sees another `instance` (here or in `hello`) reads the entries from the start. An `after` beyond `latest` answers no entries. An `after` that isn't a whole number from 0 to 9,007,199,254,740,991 written in decimal digits, or that is given more than once, is answered 400 `problem`.

## Folders

`POST /api/folders/{folder}/open` opens one of the engine's own folders in the file manager of the computer the engine runs on, and answers 204: `data`, the folder it keeps its data in (the session's `dataDir`), or `logs`, the folder of its log files. Only the session token opens them ([authentication](#authentication)). Any other `folder` is answered 404 `problem`: the engine opens only its own folders, never a path a client gives. The 204 says the engine asked the system to open the folder; what the system shows is its own. A request for a folder the engine asked to open less than a second before, or one made while four file managers it started haven't exited, is answered 204 without asking again: repeated requests open one window, and file managers that don't exit don't pile up. When the engine can't (the folder isn't there, or the file manager can't be started) it logs why and answers 500 `problem`.

## Displays

`GET /api/displays` answers the displays of the computer the engine runs on, `displayList`, read from the system when the request comes, so a display added or removed since shows in the next answer. `supported` says whether this engine shows desktop windows (a profile's `window`, [document](document.md#the-desktop-window)); where it doesn't, it lists no displays and `supported` is false, so a client tells an engine that can't list them from a computer without displays.

Each display (`displayInfo`) has `name`, `width`, `height`, `x` and `y`: exactly the `displayMatch` a profile's `window.display` takes to put its window on that display (its name, its size in its own pixels and its top-left in the desktop's own coordinates). `label` is its name for people: the same as `name` where the system has one name for a display, else the name the system shows people while `name` is the one the engine matches by. `scale` is its pixels per unit of the desktop's coordinates (2 on a Retina display on macOS; 1 on Windows, whose desktop coordinates are pixels). `primary` is true for the primary display, the one a window without `display` goes on, which is listed first; the others follow in the order the system gives them.

To fill a display with a profile, a client commits one `profile` op ([commits](commit.md)) setting `width` and `height` to the display's `width` and `height`, and `window` with `x` and `y` 0 and `display` the display's `name`, `width`, `height`, `x` and `y` (its other members as they were): while `window.enabled` is true, the window then covers the display exactly, one frame pixel per pixel of it. A display of more than 16,777,216 pixels is over the frame cap ([limits](limits.md#run-time-limits)): its window still covers it, but the frame is drawn at the largest scale within the cap ([frame size](#frame-size)) and stretched over the window. It is one change, undone like any other.

The engine reads the displays on the thread that runs its desktop windows; when that thread doesn't answer within 5 seconds, or the system fails, the engine logs why and answers 500 `problem`.

## Profiles

`GET /api/profiles` lists every profile in display order. Each entry's `status` says whether the engine can open its document, and which members come with it (one schema definition per status, `profileEntryOk`, `profileEntryUnsupported` and `profileEntryUnreadable`):

| `status` | Meaning | Members besides `id`, `status` and `instance` |
|---|---|---|
| `ok` | The engine opens the document. A document with invalid or unsupported scenes is `ok`: those items are placeholders ([unsupported content](unsupported.md)). | `name`, `width`, `height`, `rev`, `dirty`; `recovered` when the document's unsaved changes were restored at open ([below](#documents)) |
| `unsupported` | The document needs a format major version (`unsupported.format`, at `/format`) or a document capability (`unsupported.capability`, at `/requires/i`) this engine lacks. | `problem`; `name` when the document's `profile.name` is a string of 1–256 characters |
| `unreadable` | The stored file can't be read (`io`), isn't JSON (`json`), or breaks the document schema or a document limit outside its scenes (the document check's code and path, [validation](validation.md)). | `problem` |

`problem` is the first problem found, `{ path, code, message }`, with a JSON pointer into the stored document. A profile that isn't `ok` is never opened, changed, exported or rewritten: it is listed, and it can be deleted ([unsupported content](unsupported.md#document-level)). Every other request on its document answers 409 `profileStatusProblem`, whose `code` is the status and whose `problem` is the entry's. A client treats a status it doesn't know as a profile it can't open.

### A document's tag

Every profile has a lifetime tag, `instance`: an opaque id (`instanceId`, compared for equality only) that the engine gives it when it creates the profile (a create or a duplicate) or first reads it in a run (a profile it can't open too), and keeps until the profile is deleted or the engine stops. A profile deleted and created again under the same id has a new tag, and after a restart every profile has a new one. Its entry in `GET /api/profiles`, the 201 answer that created it, its `documentResponse` and its `doc` events carry it, and the document's revisions belong to it.

A commit, undo, redo or revert names the tag of the document it was built on, a save may name it ([commits](commit.md#undo-redo-revert-and-save)), and a `DELETE` names the tag of the profile it deletes ([below](#deleting)). A request that names another tag is refused with 409 `conflictProblem` (a `DELETE`'s: `deleteConflictProblem`), one conflict `{ "reason": "instance" }` (the whole request's, without `op`), and nothing is done ([commits](commit.md#baserev-conflicts)): a lost commit retried, or a save or delete that arrives late, never acts on a profile created again under the same id. The client reads the profile again (its entry or its document) and starts over. The session's `instance` is the run's, not a document's ([session](#session)).

### Creating and duplicating

`POST /api/profiles` creates a profile; `POST /api/profiles/{id}/duplicate` copies one. Both take the new profile's `id`, chosen by the client: a document id, a UUID in lower case ([document](document.md)). An `id` that already exists answers 409 `existsProblem` and creates nothing, so a client that lost the answer to a create or duplicate sends it again: 201 means this attempt created it, 409 `exists` that an earlier one did (the client reads `GET /api/profiles` to see it). A create or duplicate that would take the engine's list of profiles over `profileListBytes` ([limits](limits.md#stored-list-limits)) answers 422 `invalidProblem` with one error, `limit.profileListBytes` at `""`, and creates nothing; an `id` that already exists answers 409 first.

A create's body (`createProfileRequest`) is one of:

- `{ "id", "name", "width", "height" }`: an empty profile, a document with that name and canvas size and no items.
- `{ "id", "document" }`: a profile from a document (`infopanel.document/1`), in the shape a create takes (`importDocument`): the document format with its `id` and `rev` members optional and every scene taken as stored (`storedScene`), so a document exported by `GET /api/profiles/{id}/document`, scenes drawn as placeholders included, can be created again. The request's other members are checked against its schema, and `document` as a stored document is, in the same order ([validation](validation.md#order)): a scene that is invalid, unsupported or over an aggregate limit doesn't refuse it, it is kept exactly as sent and drawn as a placeholder, as is a scene the engine cannot compile ([unsupported content](unsupported.md)). A document the engine wouldn't open is refused: 422, with the document check's errors at their paths under `/document` (`unsupported.format` at `/document/format`, `unsupported.capability` at `/document/requires/i`, or what makes it invalid outside its scenes). The document's own `id` is replaced by the request's `id`: it may be left out, and one that is given must be a document id (`schema` at `/document/id` otherwise), as each `rev` member must be a revision although it is ignored.

A duplicate's body (`duplicateProfileRequest`) is `{ "id" }`. The copy is the profile's current document (its latest revision, saved or not), with the same name, content and item ids. Duplicating a profile that isn't `ok` answers 409 `profileStatusProblem`.

The new profile is stored and saved at once, is added at the end of the list, and starts with a new tag ([above](#a-documents-tag)), its own revisions and an empty history (`dirty`, `canUndo` and `canRedo` false). Both answer **201** with its `profileEntry`.

### Deleting

`DELETE /api/profiles/{id}?instance={tag}` deletes the profile, whatever its status, with its saved and unsaved document and its history, and answers **204**. It takes no body. `instance` is the profile's tag ([above](#a-documents-tag)), from its entry or its document: the delete removes the profile for every client, but only the profile the client read. The engine checks, in order, and nothing is deleted when a check fails:

1. `instance`: given once, of 1–64 characters, else 400 `problem`, before anything else, as every query parameter.
2. The profile exists, else 404.
3. `instance` is the profile's tag, else 409 `deleteConflictProblem` with one conflict, `{ "reason": "instance" }`. Its `rev` is the document's current revision, absent for a profile that can't be opened, which has none (`examples/api/deleteConflictProblem/unopened.json`).

A client that lost the answer and deletes again gets 404 for the profile it deleted, or 409 when the id was created again meanwhile.

### Exporting

`GET /api/profiles/{id}/document` answers the profile's current document (its latest revision, saved or not), for a client to keep or to create a profile from elsewhere, as `application/json`: `infopanel.document/1` without its `rev` members, every scene and every other opaque member (`source` data, `origin`, `editor`) exactly as stored. An engine exports only a document it opened, so a create accepts what it exports. A profile that isn't `ok` (`unsupported` or `unreadable`) is not exported: 409 `profileStatusProblem`, as for every other request on its document.

`GET /api/profiles/{id}/archive` answers the same document in a profile archive (`.infopanel2`, `application/zip`) with every blob it refers to, and `POST /api/import/archive` opens one, verifies it and stores its blobs, for a client to create the profile from; the format, its checks and its limits are on their own page ([archive](archive.md)).

### Thumbnails

`GET /api/profiles/{id}/thumbnail?w={width}` answers a PNG of the current document, drawn as an output draws it: from the tick a running output of the profile last drew, without advancing it, or else from a one-off snapshot ([identity](identity.md#the-tick-snapshot)). `w` is the width in pixels, 320 when absent, clamped to 16–4,096; the height is the canvas height in proportion, rounded to the nearest pixel and at least 1. When that height would be over 4,096, the height is 4,096 and the width the canvas width in proportion, rounded and at least 1. A `w` that isn't a whole number from −2,147,483,648 to 2,147,483,647 is answered 400 `problem`. A profile that isn't `ok` answers 409 `profileStatusProblem`.

## Documents

`GET /api/profiles/{id}` answers `documentResponse`: the current document (the latest revision, saved or not), the document's state (`rev`, the same as `document.rev`; `dirty`, `canUndo`, `canRedo`; `recovered`) and its tag, `instance`, which its revisions belong to ([above](#a-documents-tag)). The document is in the shape the engine returns (`responseDocument`): `infopanel.document/1` with every revision member set (the `rev` of the document, of the profile and of every item), and every scene exactly as stored (`storedScene`). A scene the engine draws normally matches the scene schema; one it draws as a placeholder (it needs a newer format or a capability the engine lacks, breaks a rule or limit, or can't be compiled) comes back unchanged and may not, so a client checks a scene before it reads it as one. The `rev` read here is what a commit's `baseRev` is based on ([commits](commit.md)).

`recovered` (a timestamp, UTC to the second) is set when the engine restored the document's unsaved changes as it opened it after a restart ([document](document.md#revisions)): when those changes were kept. It stays from the restore until the document is saved or reverted; an undo back to the saved document and a redo leave it. A client shows it only while `dirty` ("Unsaved changes restored, kept at 14:32"); a document without it has nothing restored.

## Blobs

Media assets and any other files a document needs are blobs, named by their content: `sha256:` and the SHA-256 of their bytes in 64 lower-case hex digits ([document](document.md#content-identity)).

`POST /api/blobs`, with the bytes as the body (of any content type), stores them and answers `blobInfo`. A body over `videoBytes` (256 MiB, [limits](limits.md#media-limits-checked-from-the-header-before-decoding)) is refused with 413 and nothing is stored: at once when its `Content-Length` says so, else as soon as more than that has arrived. The engine writes the body to disk as it arrives, hashing it as it goes, and never holds it in memory whole; the blob is stored only once all of it has arrived and is on disk. Any other body is stored, and the answer has the `id`, the size in `bytes` and what the engine found when it inspected the bytes as a media asset, from the header and without decoding ([validation](validation.md)), as one of two definitions:

- `blobInfoMedia`, with `media`, when they are a media asset within the media limits: `format` (`png`, `jpeg`, `gif`, `webp` or `svg`; a video's container, `mp4`, `matroska` or `avi`), `width` and `height` in pixels (an SVG's from its `width` and `height` or its `viewBox`, [SVG](svg.md); a video's its first video track's coded size), `frames` (1 for a still image; a video's as its container states it, 1 when it states none), `duration` in seconds (0 for a still image; a video's as its container states it, 0 when it states none) and, for a video, its `codec` ([video files](smart-nodes.md#video-files));
- `blobInfoMediaError`, with `mediaError`, otherwise: the media check's first error, with path `""` (`media.format` for bytes that aren't media at all, another `media.*` code such as `media.codec` for a video in a codec outside the list, or a `limit.*` code: a media limit such as `limit.mediaSide`, or `limit.mediaBytes` for a picture over 32 MiB).

So blobs that aren't media (a client's description of how it produced a scene, listed in a source's `deps`) are stored like any other, and so is media over a limit other than `videoBytes`; a client that checks a picture's size against `mediaBytes` before sending it sends what it always did. Storing bytes that are already stored answers the same `blobInfo` with 200. Media that names a blob with a `mediaError`, or one that isn't stored, draws the missing-media placeholder.

`HEAD /api/blobs/{id}` answers 200 with the blob's size, and `GET /api/blobs/{id}` with its bytes exactly as they were stored, as `application/octet-stream`; both answer 404 for a blob that isn't stored. A stored file whose bytes don't hash to its id counts as not stored.

The engine keeps every blob that a retained version of a document refers to (the current one, the saved one, any in the undo history; through a source's `deps` or a scene's media, [document](document.md#source-and-origin)). It may delete any other blob, but not within 24 hours of the last successful `POST` of its bytes: every `POST /api/blobs` that answers 201 or 200 starts the 24 hours again, so a blob can be stored, or stored again, before the commit that refers to it. `HEAD` and `GET` don't.

## 1.x import

An engine reads InfoPanel 1.x's data without installing anything: a 1.x data folder, its migration state and run-once record, and a 1.x `.infopanel` file, each profile answered as an envelope a client creates a profile from with `POST /api/profiles`. The requests, the envelope, its limits and its diagnostics are on their own page ([1.x import](import.md)).

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
| 1009 | `Messages are at most 4096 bytes.` | A client message over 4,096 bytes in UTF-8, on an authenticated socket (a first message over the cap on a socket still awaiting `auth` is 1008 `auth expected`) |
| 1001 | `The engine is stopping.` | The engine stops |
| 1000 | | The client closed the socket |

After an `auth` failure, the engine waits at most 2 seconds for the client's close before dropping the connection.

### Messages

A client sends JSON text messages of at most 4,096 bytes in UTF-8 (`socketMessageBytes`, [limits](limits.md#socket-limits); `clientMessage`):

- `subscribe` (`subscribeMessage`): `profileId`, and optionally `scale` (frame pixels per profile pixel, 1 when absent, [frame size](#frame-size)) `maxFps` (30 when absent; the engine draws at most 60) and `encoding` (`png` when absent, or `h264`, [video frames](#video-frames)). A subscription the engine starts ends any earlier one, and is answered with a `doc` message, then frames; one it can't start is answered with an `error` and leaves the earlier one as it was.
- `view` (`viewMessage`): a new `scale` or `maxFps` for the current subscription; a member left out keeps its value.
- `keyframe` (`keyframeMessage`): the client lost its video decoder's state; the next `h264` frame of the subscription is a keyframe, drawn at once if nothing changes ([video frames](#video-frames)). It does nothing for a `png` subscription.
- `unsubscribe` (`unsubscribeMessage`): no more `doc` messages or frames until the next `subscribe`.
- `auth` (`authMessage`): only as the first message, above.

The engine reads each message as the formats read JSON ([validation](validation.md#json)) before reading any member of it, members it doesn't define included: a message nested deeper than `jsonDepth`, with a number that rounds to ±∞, an object that repeats a member name (never resolved by keeping one) or a string or member name that isn't Unicode text is one it can't act on. Client messages are closed ([versioning](versioning.md#unknown-members-and-values)): a message with a member its definition doesn't have is one the engine can't act on, and it acts on no part of it. It answers `error` to a message it can't act on and keeps the socket open: one that isn't JSON as the formats read it or isn't a JSON object with a string `type`, a binary message, an unknown `type`, a member the message's definition doesn't have, a `subscribe` without a string `profileId`, a `scale` that isn't a positive number, a `maxFps` that isn't a whole number from 1 to 2,147,483,647 or an `encoding` the engine doesn't take, or a `view` or `keyframe` without a subscription.

The engine sends (`serverMessage`):

- `hello` (`helloMessage`): first, once the socket is authenticated, with `protocol` and the run's `instance` ([session](#session)).
- `doc` (`docEvent`): the subscribed document's state ([below](#change-events)).
- `profiles` (`profilesMessage`): the profile list changed (a profile was created, duplicated or deleted, by any client); the client reads `GET /api/profiles`. `version` is greater for a later change; the engine keeps only the newest to send.
- `settings` (`settingsMessage`): the engine settings changed, by any client; the client reads `GET /api/settings`. Only a socket authenticated with the session token gets it. `version` is the settings' `version` after the change; the engine keeps only the newest to send. The order of `settings` and `profiles` messages relative to each other is unspecified: a client doesn't rely on one coming before the other.
- `error` (`errorMessage`): a message the engine couldn't act on, or a subscription to a profile that doesn't exist, can't be opened or previewed, or was deleted (which also ends the subscription), with its `code` and `path` ([below](#errors-on-the-socket)) and a `message` for people.

#### Errors on the socket

An `error` names what went wrong with a `code`, and with a `path`: the JSON pointer to what it is about in the client message it answers, `""` for the whole message and for an error that answers none. Codes and paths are part of the API, and a client handles a code it doesn't know by its family, else as a message the engine didn't act on ([versioning](versioning.md#error-codes-and-paths)).

| Code | Path | When |
|---|---|---|
| `json` | The value, or `""` | The message isn't JSON as the formats read it ([validation](validation.md#json)): it isn't UTF-8 JSON text, or is binary (at `""`); a number rounds to ±∞ (at the number); a string isn't Unicode text (at the string; a member name's at its object) |
| `limit.jsonDepth` | `""` | The message is nested deeper than `jsonDepth` |
| `schema` | The value | The message isn't an object with a `type` (at `""`); its `type` isn't a string, or is a type the engine doesn't take (at `/type`); it has a member its definition doesn't (at the member) or repeats a member name (at its object); a `subscribe` has no `profileId` (at `""`) or one that isn't a string (at `/profileId`); a `scale` that isn't a positive number (at `/scale`); a `maxFps` that isn't a whole number from 1 to 2,147,483,647 (at `/maxFps`); an `encoding` that isn't one the engine takes (at `/encoding`: a client that gets it subscribes again with one it knows) |
| `subscription.missing` | `""` | A `view` or `keyframe` without a subscription |
| `profile.missing` | `/profileId` | A `subscribe` to a profile that doesn't exist |
| `profile.unsupported`, `profile.unreadable` | `/profileId` | A `subscribe` to a profile that can't be opened: `profile.` and its status ([profiles](#profiles)) |
| `profile.deleted` | `/profileId`, or `""` | The profile was deleted: at `/profileId` as the `subscribe` started, else at `""`. The subscription has ended |
| `profile.unrenderable` | `/profileId`, or `""` | The engine can't draw the profile: it can't be drawn within the frame limit at any scale ([frame size](#frame-size)), or the engine is stopping (the socket then closes with 1001). At `/profileId` as the `subscribe` started, else at `""`. The subscription has ended |

A `profile.*` code means there is no subscription to that profile. One at `""`, which ends a subscription, is always about the latest one: it is never sent once a later `subscribe` has started another.

### Change events

The `doc` message (`docEvent`) is the subscribed document's state: its tag (`instance`, [above](#a-documents-tag)), `rev`, `dirty`, `canUndo`, `canRedo`, and `recovered` while the document has it ([above](#documents)). The engine sends it when the subscription starts and after every change to the document or to its state: every commit, undo, redo, revert and save that changes either, whichever client made it. It keeps only the latest state to send, so a client that falls behind gets the newest one and may skip revisions, and the last `doc` message it gets is always the current state. It carries no content: the document at that revision is read with `GET /api/profiles/{id}`.

### Frames

A frame message is binary: the four bytes `IPF1`, the header's length N as an unsigned 32-bit little-endian integer, N bytes of UTF-8 JSON (`frameHeader`), then the frame in the header's `encoding` (`png`: the whole frame as a PNG; `h264`: [video frames](#video-frames)).

```jsonc
{ "profileId": "3dd19c0e-675a-4591-b096-83417853861f", "rev": 43, "tick": 1820, "frameId": 5113,
  "encoding": "png", "scale": 2, "pixelWidth": 1600, "pixelHeight": 960, "profileWidth": 800, "profileHeight": 480,
  "encodeMs": 9.5, "skipped": 0, "itemBounds": [ { "id": "cpu-bar", "frame": { "x": 20, "y": 60, "w": 240, "h": 20 }, "ink": { … }, "rotation": 0 } ] }
```

- `rev` is the document revision drawn and `tick` the tick it was drawn from. The pixels and `itemBounds` come from that one tick snapshot ([identity](identity.md#the-tick-snapshot)), so the bounds always describe the pixels they arrive with, at `rev`. Within one subscription, frames with the same `tick` were drawn from the same snapshot, and tick numbers never go down.
- `frameId` is unique within one run of the engine and greater for a frame rendered later.
- `scale` is the frame's pixels per profile pixel, `pixelWidth` and `pixelHeight` the frame's size, and `profileWidth` and `profileHeight` the canvas size at `rev` ([frame size](#frame-size)).
- `encodeMs` is the milliseconds from handing the frame's pixels to its encoder to having the encoded frame: for an `h264` frame, copying and encoding it, waiting for the encoder included; for a `png` frame, that PNG's encoding, which may serve several subscriptions. `skipped` counts the frames the engine drew for this subscription and didn't send since the last one it sent: dropped before encoding, or replaced by a newer one before it could be sent.
- `itemBounds` holds the bounds of every visible item that has bounds ([geometry](geometry.md#measured-bounds-reported-by-the-engine)), each id once, in document order: a group before its children, the first item at the back. A hidden item, everything in a hidden group and a group with no visible children are absent. They are in profile pixels, like the document.
- An item's `media`, when present, is the first problem the media it shows had in that tick, `{ code, message }`: `media.loading`, `media.missing` (an asset that isn't stored, or was refused), `media.provider` and `media.address` ([providers](providers.md#asking-and-what-each-item-shows)), `media.budget` (a video past the engine's decode sessions, [video files](smart-nodes.md#video-files)), or the code a fetched answer was refused with. `message` says why, for people, in at most 1,024 characters (a longer one is cut, ending in "…": it can carry text from the answer). A client treats a code it doesn't know as media that can't be shown. The item isn't a placeholder: its media draws the missing-media placeholder, or a provider's last good media while it says why the newest couldn't be had.

The engine sends a frame only when its pixels, its bounds (an item's `media` included) or its revision changed.

#### Video frames

A subscription with `encoding` `h264` gets an H.264 video stream. Its frames are `h264` while the engine has a working H.264 encoder and the frame is within the video size: each side at most 4,096 pixels and at most 9,437,184 pixels in all (4096 × 2304). Every other frame of it is a `png` frame with `fallback`, which says why:

- `encoder.unavailable`: the engine has no working H.264 encoder (none found, turned off, or every one given up in this run);
- `encoder.failed`: its encoders failed, or are busy, during this subscription; PNG frames until it ends, unless a busy encoder frees up. A new subscription tries again;
- `encoder.size`: this frame is larger than the video size, or no working encoder takes its size; video resumes, with a keyframe, when frames fit again.

The list may grow: a client reads a `fallback` it doesn't know as "video unavailable for this frame". The engine never refuses an `h264` subscription for want of an encoder, so a client that can show `png` frames can always show the subscription.

The stream:

- **One access unit per frame**, AVCC: each NAL unit preceded by its length as a 4-byte big-endian integer; no parameter sets in the stream itself. Each keyframe's header has `codec` and `description`, the decoder configuration record (avcC) for the frames from it on, and `encoder` (`hardware` or `software`, open: a client shows a value it doesn't know as it is); every `h264` frame has `keyframe`.
- **In order, and never dropped once encoded**: frames are dropped only before encoding, so every `h264` frame sent must be decoded, in the order sent. There are no B-frames and no reordering, which the avcC's SPS declares (`max_num_reorder_frames` 0 in its VUI's `bitstream_restriction`): a decoder can output each frame as soon as it has decoded it.
- **A keyframe** begins the subscription's video, and follows a size change, PNG frames, a change or restart of the engine's encoder, and a `keyframe` message.
- **Padded to even sides**: the coded picture may be a pixel wider or taller than `pixelWidth` × `pixelHeight`, the frame's own size, which a client crops to.
- **Opaque**: transparent pixels show as if over black.
- **BT.709** primaries, transfer and matrix, limited range, also written in the SPS's VUI.

The bit rate and which encoder the engine uses are the engine's choice. `view`'s `maxFps` applies to the running stream, and a `view` that changes the frame's size is followed by a keyframe. To change the encoding, a client subscribes again.

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
- `frame.video`: in a `frameHeader`, an `h264` frame has `keyframe`; a keyframe has `codec`, `description` and `encoder`; `keyframe`, `codec`, `description` and `encoder` are only on `h264` frames, and `fallback` only on `png` frames.
