# 1.x import

An engine reads the data of InfoPanel 1.x, the Windows application that came before it, without installing anything: a 1.x data folder (its profiles, its settings) and a 1.x `.infopanel` file (one exported profile). It answers what it read, and a client creates profiles from it through the API like any other ([API](api.md#creating-and-duplicating)). The engine never turns 1.x items into scenes and never writes 1.x data.

Requests ([API](api.md)), all under `/api/import/v1`:

| Request | What |
|---|---|
| `GET /api/import/v1` | The migration's state, and the 1.x folder's profiles, settings and diagnostics (`v1Migration`, [below](#migrating-a-1x-folder)) |
| `PUT /api/import/v1` | Stores what a migration did as the run-once record (`v1MigrationRequest`, [below](#the-record)) |
| `POST /api/import/v1/profiles/{v1Id}` | One profile of the folder, read into its envelope (`v1Profile`, [below](#the-envelope)) |
| `POST /api/import/v1/archive` | A 1.x `.infopanel` file, sent as the body, read into its envelope |

These bodies are engine API bodies, versioned with the engine API ([versioning](versioning.md#the-engine-api)): what the engine answers is read tolerantly, so a client ignores members it doesn't know and handles an enumeration value it doesn't know as each definition's description says ([versioning](versioning.md#unknown-members-and-values)). Their limits are in [limits](limits.md#1x-import-limits) and [archive limits](limits.md#archive-limits). Only 1.x data from 1.2 and later is imported; 1.1.x data is recognised and reported ([below](#1x-versions)).

## The 1.x folder

The engine reads one 1.x folder: 1.x's own data folder, `{LocalApplicationData}/InfoPanel` (`%LOCALAPPDATA%\InfoPanel` on Windows), unless the engine is started with another. It only reads it: files are opened read-only and shared, nothing is created, locked, moved or written there, and links (symbolic links, junctions) are never followed. The folder has 1.x data when it holds `profiles.xml` or `settings.xml`.

- `profiles.xml` lists the profiles, in 1.x's order; `profiles/{guid}.xml` holds a profile's items, and `assets/{guid}/` its image files.
- `settings.xml` holds the settings; when it can't be read, `settings.xml.bak` is read instead, as 1.x falls back (`import.settings.backup`).
- Names inside the folder match whatever their letter case (the folder may have been copied to a case-sensitive file system).
- Paths inside 1.x data are Windows paths: `\` and `/` both separate folders.

### 1.x versions

1.x data from 1.2 and later is imported. 1.1.x data is recognised by its settings, as 1.x itself recognised it: a readable `settings.xml` (or its backup) whose `Version` is 114 or lower, or has none. Such a folder answers its profile list with an `import.version` error and no settings, and reading one of its profiles is refused (422, `import.version` at `""`): opening it once in 1.4 upgrades it. A folder whose settings can't be read, or whose `Version` doesn't parse, is imported. A `.infopanel` file carries no version and is never refused as 1.1.x.

## Migrating a 1.x folder

`GET /api/import/v1` answers `v1Migration`, the migration's `state`:

- `none`: the folder has no 1.x data;
- `pending`: it has 1.x data and the engine has no record: it hasn't been migrated;
- `done`: the engine has a record ([below](#the-record)), or one it can't read (`import.record`): a migration never runs on its own twice.

With it come `folder`, the 1.x folder the engine reads, the `record` when the engine can read it, and, when the folder has 1.x data, its `profiles` (each with its 1.x Guid as `id`, its `name`, `width`, `height` and `active`, in 1.x's order), its `settings` ([below](#settings)) and the `diagnostics` found reading the folder ([below](#diagnostics)). Every request on the folder works in every state, so a client can offer to import from 1.x again at any time.

A client migrates a folder by:

1. reading the state;
2. reading each profile with `POST /api/import/v1/profiles/{v1Id}` (404 when the folder has no such profile, or no 1.x data);
3. creating a profile from each envelope with `POST /api/profiles`, in 1.x's order so the profiles keep 1.x's display order, with the 1.x Guid in lower case as the profile's id: a migration resumed after an interruption then gets 409 `exists` for a profile it created before, never a second copy, and 1.x's device list and hotkeys, which name profiles by that Guid, still match;
4. recording what it did with `PUT /api/import/v1`.

A profile read from a `.infopanel` file is created under a new id.

### The record

`PUT /api/import/v1` takes `v1MigrationRequest`: one outcome per 1.x profile (`v1Id`, the profile `id` created from it or `null`, `outcome` `imported`, `skipped` or `failed`, and an optional `reason` for people) and whether the client applied the settings (`settings`: `imported` or `skipped`). The engine stores it as the run-once record, with `completed`, the time it stored it (UTC, to the second), and `folder`, replacing any record, and answers the new state, `done`. The body is checked as every body is ([validation](validation.md#order)): its size as received within `recordBytes` (else `limit.recordBytes` at `""`), its outcomes within `recordOutcomes` before the schema (else `limit.recordOutcomes` at `/profiles`), then the schema (`schema` at the value); and the record as stored within `recordBytes` (`limit.recordBytes` at `""`). A refused body stores nothing (422 `invalidProblem`).

The record is the engine's own file, read tolerantly: members a later engine added are ignored when it is read and kept when it is replaced. A record the engine can't read whole (larger than `recordBytes`, nested deeper than `jsonDepth`, or not a file it can open) counts as done and is never replaced: a `PUT` then answers 500 and stores nothing.

## The envelope

`POST /api/import/v1/profiles/{v1Id}` and `POST /api/import/v1/archive` answer `v1Profile`, one 1.x profile read without installing anything:

- `source`: `folder` or `archive`;
- `id`: the 1.x profile's Guid as stored, in lower case. It is never a profile id of the engine's: a client that creates a profile from the envelope chooses that ([above](#migrating-a-1x-folder));
- `profile`: every member of the 1.x profile model, by its camelCase name, as stored (`v1ProfileProps`); and `targetWindow`, the display 1.x placed the window on, when stored;
- `items`: the items ([below](#items));
- `guides`: 1.x's guides ([below](#guides));
- `assets`: the image files the items name, stored as blobs ([below](#assets));
- `diagnostics`: what was found reading it ([below](#diagnostics)).

Problems inside a readable profile (items that can't be read, images that can't be stored, fonts the engine doesn't have) are diagnostics in a 200 answer, never a refusal. Reading a profile again stores no blob twice.

### Items

`items` holds the profile's top-level items as the 1.x item JSON, in 1.x's order, the first at the back: for each item its `id` (the 1.x item's Guid in lower case), its `kind` (one of the 14 1.x kinds), its `props` (every property of the kind's 1.x model, by its camelCase name, as stored), `children` for a group (its items, nested the same way) and `images` for a gauge (its frames, image items), and `meta`. Their grammar is `schemas/v1item.schema.json` (`urn:infopanel:v1item:1`): one definition per kind and one for its props, generated from the engine's 1.x kind manifest and frozen with the engine API; a string 1.x left null, and a number that isn't finite, is `null`. `meta.sensor` is the item's sensor binding as this engine reads it: its `key` ([bindings](bindings.md#bindings)) and its `status` now.

What the engine leaves out, each with a diagnostic: an item of a type outside the 14 kinds (a newer 1.x build's), wherever it sits (`import.itemType`), and an empty (`xsi:nil`) item (`import.nil`). An element or attribute 1.x's reader doesn't know is ignored and its item still read (`import.element`). A client reads `items` tolerantly too: it skips an item whose `kind` it doesn't know, reporting it as `import.itemType`.

### Guides

1.x's guides (lines 1.x showed only while a profile was being designed, never on a panel) are data, not items: each has `x`, `y`, `length`, `color`, `rotation`, `name` (1.x's default `DisplayItem` when absent), `hidden` and `locked` as stored, and `container`, the JSON pointer of the group's `children` it sat in, absent at the top level.

### Assets

`assets` has one entry per distinct pair of an image item's `filePath` and `relativePath` (gauge frames included), keyed by exactly those props, so a client looks each up. A relative path is resolved inside the profile's assets (the folder's `assets/{guid}/`, an archive's `assets/` entries), names matched whatever their letter case. A file that passes the media check, a picture or a video file ([video files](smart-nodes.md#video-files): 1.x showed videos as images), is read up to `videoBytes` when its first bytes are a video container's, else up to `mediaBytes`, and stored as `POST /api/blobs` stores it ([API](api.md#blobs)), and its entry has the `blob`, as that request answers it; the engine keeps it for 24 hours, time for the client to create the profile that refers to it. Otherwise the entry has a `code`, nothing is stored, and the item draws the missing-image placeholder once created:

| `code` | Why |
|---|---|
| `import.asset.absolute` | A path that isn't relative to the profile's assets (`relativePath` false, a drive, a rooted or network path, or one holding `:`): never read |
| `import.asset.path` | A relative path that leaves the assets folder, or isn't a file path (`scheme://`) |
| `import.asset.missing` | No such file, or one that can't be read |
| `import.asset.link` | A link: never followed |
| `import.asset.media` | Not stored: `reason` is the media check's code ([validation](validation.md#codes)) or limit (`limit.mediaBytes` for a picture), `limit.videoBytes` for a video file larger than any blob, or `archive.expanded` when the import has read `expandedBytes` |

An archive's asset files no item uses aren't stored (one `import.asset.unused` counts them).

### Settings

`settings` is the user data of the folder's settings, each value as 1.x would use it (an absent one is 1.x's default; one that doesn't parse is 1.x's default too, with `import.setting`): `version`; `targetFrameRate` and `targetGraphUpdateRate`, within the engine's frame and graph rate ranges; `libre` (whether 1.x read sensors with LibreHardwareMonitor, and whether and how often it kept their readings); `programSpecific` (whether 1.x switched profiles by the program in front, and whether it hid the others then); `devices`, every panel device of 1.x's eight families with the profile it showed and each of its elements as stored; `multiDevice`, per family; and `hotkeys`. Nothing is applied by reading them. Theme, scale, window and other display settings of 1.x's own window are left out.

## Reading a `.infopanel` file

`POST /api/import/v1/archive` takes the file's bytes as the body, of any content type, up to `archiveBytes` (413 over it, nothing read or stored). The file is a zip archive read within the [archive guards](#archive-guards); then, as 1.4 reads its own exports:

- `Profile.xml` and `DisplayItems.xml` are its profile and items, named in any letter case; a file without both is refused (`import.archive`), and one whose `Profile.xml` can't be read too (`import.xml`, at its entry);
- its asset entries are named `assets/` or `assets\` and the file's name; of two whose names differ only in letter case or separator (1.x's aliases of one file), the later wins (`import.asset.duplicate`);
- any other entry is ignored (`import.entry`).

An exported `Profile.xml` holds no window placement, so `profile` has the model's defaults there. A 2.0 profile archive sent here is refused with the hint `archive.v2`.

### Archive guards

An archive is refused (422 `invalidProblem`) before any entry is read when:

| Code | When |
|---|---|
| `archive.zip` | It isn't a zip archive, its directory can't be read, or an entry's data doesn't decompress or, read to its end, isn't the size its headers claim |
| `archive.entries` | It has more than `archiveEntries` entries, as its directory declares them or as they are read, or an entry's directory record is over `entryRecordBytes` |
| `archive.expanded` | Reading it decompresses more than `expandedBytes`, counted as read, whatever the entries' headers claim |
| `archive.name` | An entry's name is over `entryNameBytes`, holds NUL or `:`, is rooted (`/`, `\`), or has an empty, `.` or `..` segment |
| `archive.duplicate` | Two entries have exactly the same name |
| `archive.link` | An entry is a symbolic link |
| `archive.encrypted` | An entry is encrypted |
| `archive.method` | An entry's compression method or header can't be read |

An error about one entry is at `/entries/{name}` (the entry's name as one JSON pointer token, `~` written `~0` and `/` written `~1`); one about the archive as a whole at `""`.

## Diagnostics

Each diagnostic (`importDiagnostic`) has a `severity` (`error`: something wasn't read; `warning`: read, but not as 1.x had it; `info`), a `code` from the catalogue below, a `path` (a JSON pointer into the answer, `""` for the whole), a `message` for people, and the 1.x `file` and `line` it was found at when known. One answer lists at most `diagnostics` of them, then one more, `more`, counting the rest. Codes and severities are part of the engine API ([versioning](versioning.md#error-codes-and-paths)); a client handles a code it doesn't know by its severity.

| Code | Severity | When |
|---|---|---|
| `import.profiles` | error, warning | `profiles.xml` can't be read (no profiles listed); it lists more than `v1Profiles` (the rest left out) |
| `import.profile.duplicate` | warning | Two profiles share a Guid: 1.x kept one items file and one assets folder for both, so reading that id reads the first |
| `import.nil` | info | An empty (`xsi:nil`) profile or item, left out |
| `import.settings` | error | Neither `settings.xml` nor `settings.xml.bak` can be read: no settings |
| `import.settings.backup` | info | `settings.xml.bak` was read, as 1.x falls back |
| `import.setting` | warning | A setting that doesn't parse (1.x's default is used), or a device, field, value or hotkey over its limit (left out) |
| `import.version` | error | 1.1.x data ([above](#1x-versions)) |
| `import.record` | warning | The record can't be read: the state is `done` without it |
| `import.autosave` | warning | 1.x holds autosaved changes to the profile that were never saved; the saved version is read |
| `import.items` | info, error | The profile has no items file (no items, as 1.x loads it); it can't be read, isn't a 1.x item list, or is over `v1ItemsXmlBytes` (no items) |
| `import.xml` | error | An XML file refused: a DTD, deeper than `v1XmlDepth`, more than `v1XmlElements` elements, or malformed |
| `import.itemType` | warning | An item of a type outside the 14 kinds, left out |
| `import.element` | warning | An element or attribute 1.x's reader doesn't know, ignored |
| `import.size` | warning | A canvas side over the largest a document allows (a client clamps it) |
| `import.font` | warning | A font family (an item's or the profile's) the engine doesn't have: text in it draws in the default family, and its widths change |
| `import.sensor` | info | A binding whose sensor the engine has no reading of now (`unavailable`, `missing`); it stays bound |
| `import.asset.absolute`, `import.asset.path`, `import.asset.missing`, `import.asset.link`, `import.asset.media` | warning | An image file not stored ([above](#assets)) |
| `import.asset.duplicate`, `import.asset.unused`, `import.entry` | info | An archive's alias of an asset, its unused assets, an entry it ignores ([above](#reading-a-infopanel-file)) |
| `more` | info | The count of diagnostics not listed |

XML is read without DTDs, entities or external resources, its depth and element count checked as it streams, before the engine builds anything from it.
