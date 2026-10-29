# Profile archive

A profile archive (`.infopanel2`) carries one profile from one engine to another: its document and every blob the document refers to, in one file. Its format is `infopanel.archive/1` (schema `urn:infopanel:archive:1`, `schemas/archive.schema.json`), frozen with the other formats ([versioning](versioning.md#what-freezes)). An engine writes one (`GET /api/profiles/{id}/archive`, [below](#exporting)) and opens one (`POST /api/import/archive`, [below](#importing)); a client creates the profile from what it opened with `POST /api/profiles` ([API](api.md#creating-and-duplicating)), so nothing is created before everything in the archive is checked.

## The format

A zip archive of exactly these entries, each stored or deflated, and nothing else (no folders):

| Entry | What |
|---|---|
| `manifest.json` | The manifest ([below](#the-manifest)), read before anything else in the archive |
| `document.json` | The profile's document (`infopanel.document/1`), in the shape a create takes (`importDocument`, [API](api.md#creating-and-duplicating)) |
| `blobs/{hex}` | One per blob the document refers to: its bytes, named by their SHA-256 in 64 lower-case hex digits (the blob id without `sha256:`) |

Names are exact, in letter case too: `Document.json`, a blob name in upper-case hex, or a `\` in a name is another entry, which the archive may not hold. Two entries with exactly one name are refused; unlike a 1.x `.infopanel` file ([1.x import](import.md#reading-a-infopanel-file)), a profile archive has no aliases of one name.

### The manifest

```json
{ "format": "infopanel.archive/1", "requires": [], "profiles": ["document.json"] }
```

- `format` is the archive format and its major version. An engine that doesn't read the major version refuses the archive as unsupported, `unsupported.format` at `/manifest/format`.
- `requires` lists every archive capability beyond version 1.0 the archive uses ([versioning](versioning.md#capabilities-and-requires)). An engine that lacks one refuses the archive as unsupported, `unsupported.capability` at `/manifest/requires/{i}`, before it reads anything else. Version 1.0 defines no archive capability; an engine lists the ones it has in `GET /api/session` (`formatSupport`, under `infopanel.archive/1`, [API](api.md#session)). Several profiles in one archive, or fonts carried with it, would each come as one.
- `profiles` names the entries that hold the profile documents: in version 1.0 exactly one, `document.json`.

The manifest is closed: a member it doesn't define is refused (`schema`), as in every format an engine interprets ([versioning](versioning.md#unknown-members-and-values)).

## Exporting

`GET /api/profiles/{id}/archive` answers the profile's archive as `application/zip`, with `Content-Disposition: attachment` naming it: the profile's name with the characters a Windows file name can't hold (`"`, `<`, `>`, `|`, `:`, `*`, `?`, `\`, `/` and the control characters) and spaces replaced by `_`, a `-`, the time in Unix seconds and `.infopanel2` (with `filename*`, its UTF-8 form). The archive holds:

- `manifest.json`, first: version 1.0's, no capability required;
- `document.json`: the document `GET /api/profiles/{id}/document` answers ([API](api.md#exporting)): the current revision, saved or not;
- every blob the document refers to (a source's `deps`, media, any `sha256:` id in its opaque members) that is stored and whose bytes hash to its id, each once, in id order. The no-image id (`sha256:` and 64 zeros) is never a blob. A blob that isn't stored, or whose stored bytes don't hash to its id, is left out: the archive's import then reports it as missing.

The engine builds the whole archive before it sends any of it, so an export that fails is answered 500, never a cut-off file. It exports only what its own import accepts:

- a profile that isn't `ok` is not exported: 409 `profileStatusProblem`, as for every request on its document ([API](api.md#profiles));
- an archive the import would refuse for its size is not made: 409 `exportProblem`, whose `errors` carry the code the import would give at `""` (`archive.entries` for more than `archiveEntries` entries, `archive.expanded` for more than `expandedBytes` read or a file over `archiveBytes`).

## Importing

`POST /api/import/archive` takes the archive's bytes as the body, of any content type, up to `archiveBytes` (413 over it, nothing read or stored), and checks it in this order; the first failure is answered 422 `invalidProblem`:

1. The [archive guards](import.md#archive-guards) (`archive.zip`, `archive.entries`, `archive.expanded`, `archive.name`, `archive.duplicate`, `archive.link`, `archive.encrypted`, `archive.method`); an entry neither stored nor deflated is `archive.method`.
2. A 1.x `.infopanel` file (a `Profile.xml` or `DisplayItems.xml` and no `document.json`) is refused with the hint `archive.v1`: it is read through the 1.x import ([1.x import](import.md)). A profile archive sent there gets `archive.v2`.
3. The manifest: none is `archive.manifest` at `""`; one over `manifestBytes` is `limit.manifestBytes` at `/entries/manifest.json`, unread; one that isn't JSON is `json` at `/manifest`; then it is checked as every reader checks content ([validation](validation.md#order)): its JSON-level pass, its format and requirements ([above](#the-manifest)) and its schema, each error under `/manifest`.
4. Every other entry is `document.json` or `blobs/` and 64 lower-case hex digits (`archive.name` otherwise), and there is a `document.json` (`archive.document` otherwise).
5. The document, checked as a create checks its `document` ([API](api.md#creating-and-duplicating)): read when it is at most `documentBytes` plus 64 KiB, then checked by its canonical size, JSON-level pass, format, schema and rules, its errors under `/document` (an `unsupported.*` code for a document this engine can't open).
6. Every blob entry is one the document refers to (`archive.unreferenced` at its entry otherwise), before any is read.
7. Each blob, in the archive's order: at most `mediaBytes` (`limit.mediaBytes` at its entry), its bytes hashing to its name (`archive.hash` at its entry), then stored as `POST /api/blobs` stores it ([API](api.md#blobs)), which also replaces a stored copy whose bytes don't hash to its id.

The answer, `archiveContents`, has the `document` as the archive holds it, the `blobs` it carried as `POST /api/blobs` answers each, and `missing`: the blob ids the document refers to that the archive didn't carry and this engine doesn't store (the no-image id never). Nothing is created: the client creates the profile from `document` with `POST /api/profiles`, under an id it chooses. The engine keeps each stored blob for 24 hours, time for that create; blobs stored before a refusal at step 7, or by an import that was cut short, are kept only that long unless a document refers to them.

An error about one entry is at `/entries/{name}` (the entry's name as one JSON pointer token); one about the manifest under `/manifest`, the document under `/document`, and the archive as a whole at `""`.

| Code | When |
|---|---|
| `archive.manifest` | No `manifest.json` |
| `archive.document` | No `document.json` |
| `archive.unreferenced` | A blob the document doesn't refer to |
| `archive.hash` | A blob whose bytes don't hash to its name |
| `archive.v1` | A 1.x `.infopanel` file sent as a profile archive |
| `archive.v2` | A profile archive sent as a 1.x `.infopanel` file ([1.x import](import.md)) |

The archive guards' codes are in [1.x import](import.md#archive-guards); `limit.*` codes in [limits](limits.md#archive-limits); the document's in [validation](validation.md#codes).
