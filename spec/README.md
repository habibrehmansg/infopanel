# InfoPanel formats

The formats an InfoPanel engine reads and writes, specified so that any client can produce them: the **document** (a profile and its items), the **scene** each item draws, the **profile archive** that carries a profile and its blobs between engines, and the engine API that carries them: its requests and bodies (the session, sensors, the engine settings, the engine's recent log entries, the displays, the profile list, documents, blobs, commits, conflicts, problems, the preview socket's messages, frames and their bounds). A client writes or generates scenes against this specification; the engine validates, stores and renders them. How a client produces a scene is up to the client.

**Status: draft** until the first engine release, which freezes every format here at once; from then on they change only as [versioning](docs/versioning.md#what-freezes) allows.

## Contents

| Path | What |
|---|---|
| `schemas/` | JSON Schemas (draft 2020-12): `common`, `expression`, `scene` (`infopanel.scene/1`), `document` (`infopanel.document/1`), `api` (session and format support, sensors and readings, profile list, profile creation and duplication, document, blob, commit request, undo, redo and revert request and result, problems, integration tokens, the engine settings and a change of them, the engine's recent log entries, the displays, the 1.x import's answers and migration record, the preview socket's messages, frame header with item bounds, change event), `v1item` (the 1.x items of a 1.x import's answer, generated from the engine's 1.x kind manifest by `npm run gen`), `archive` (`infopanel.archive/1`: a profile archive's manifest). Ids are `urn:infopanel:<name>:1`. |
| `openapi.json` | The engine API's requests ([below](#the-request-index)): every operation, its parameters and each status it answers, with its bodies' definitions in `schemas/`. |
| `types/index.d.ts` | TypeScript types, generated from the schemas (`npm run gen`), and `ApiOperations`, generated from `openapi.json`: each request, keyed `'METHOD /path'`, with its `params`, `query`, `request` and `responses` by status. |
| `docs/` | The specification in prose (below). |
| `examples/` | Valid and invalid documents, scenes, commits, API bodies (`api/{definition}/`, refused ones in its `invalid/`) and media; `examples/expected.json` gives each invalid example's error code and path. `examples/expressions/vectors.json` gives expressions with their exact results. |
| `tools/` | `gen-types.mjs` (schemas and `openapi.json` to TypeScript, no dependencies), `gen-v1-items.mjs` (the 1.x kind manifest, `../InfoPanel.Core.Tests/Snapshots/kinds.json`, to `schemas/v1item.schema.json`), `check.mjs` (the 1.x item schema and the types are current; `openapi.json` keeps to its subset, its `$ref`s resolve, its operation ids are unique, every `api` definition is used by a request or the socket, every enumeration in what the engine sends says what a client does with a value it doesn't know, and `docs/api.md`'s request table lists exactly its operations; with `TSC` set, every valid example type-checks, except those nested deeper than 32 levels, which the compiler can't compare with the recursive JSON types, and so do `ApiOperations` and the response examples assigned to it). |
| `package.json` | `@infopanel/spec`, private for now: use it by path. |

## The specification

1. [Document](docs/document.md): profile, items, ids and revisions, source and origin, guides.
2. [Geometry](docs/geometry.md): placement, layout box, pivot, measured bounds, groups, clipping, drawing order.
3. [Bindings](docs/bindings.md): scene inputs, sensor bindings, statistics, status, history.
4. [Scene](docs/scene.md): nodes, paints, strokes, effects, transforms, clips, the drawing model.
5. [Expressions](docs/expressions.md): types, operations, values, state cells, time patterns, evaluation order.
6. [Smart nodes](docs/smart-nodes.md): `text`, `series`, `table`, `media`; [media providers](docs/providers.md): the `http` provider, refresh, status, and the addresses a provider may connect to.
7. [Identity and state](docs/identity.md): instance ids, what keeps and what resets state, the tick snapshot.
8. [Commits](docs/commit.md): per-item per-field ops, what a change stamps, `baseRev`, `opId` and the retry window, coalescing, 409 and 422; undo, redo, revert and save.
9. [Engine API](docs/api.md): the requests, errors and tokens, the session, fonts and sensors, the engine settings, the recent log entries, opening the engine's folders, the displays, the profile list and its statuses, creating, duplicating, deleting and exporting profiles, documents, blobs, the preview socket's messages, frames and change events.
10. [Profile archive](docs/archive.md): the `.infopanel2` format, its manifest, exporting a profile and importing an archive.
11. [1.x import](docs/import.md): reading a 1.x data folder and a 1.x `.infopanel` file, the migration's state and run-once record, the envelope (items, guides, assets, settings), the archive guards and the diagnostics.
12. [Unsupported content](docs/unsupported.md): placeholders, never rewriting what an engine can't read.
13. [Versioning](docs/versioning.md): formats, what freezes, minor versions and what each ships with, capabilities and `requires`, major versions, unknown members and values, the engine API and its identifiers, the four markers.
14. [Validation](docs/validation.md): the order of checks and every error code.
15. [Limits and budgets](docs/limits.md): every acceptance limit, the budgets an engine guarantees, and the performance targets.
16. [SVG profile](docs/svg.md): the restricted SVG that media assets may use.

## The request index

`openapi.json` is a hand-written OpenAPI 3.1.1 document that indexes the engine API: one operation per request in [the API's table](docs/api.md), with an `operationId`, its path and query parameters, its request body and, per status, its media type and body. It holds no body schemas of its own: every JSON body is a `$ref` to a definition in `schemas/` (or a `oneOf` of them, or an array of one), named by the schema's `$id` (`urn:infopanel:api:1#/$defs/session`), so a tool must resolve `$ref`s by `$id`, not by location. Errors shared by many operations are in `components.responses`; the token is a bearer `securityScheme`, and `security: []` marks what needs none. `GET /ws` is the preview socket: its `x-infopanel-socket` names the subprotocol and the definitions of the client's and the engine's messages and of a frame's header.

It uses a subset of OpenAPI: the objects and fields above, and in schemas the same JSON Schema keywords as `schemas/`. OpenAPI-only keywords (`discriminator`, `xml`, `example`, `nullable`, …) and features the engine API doesn't use (callbacks, links, webhooks, examples, headers' styles) are refused by `npm run check`.

## Checks

From `spec/`:

```bash
npm run gen                                  # regenerate schemas/v1item.schema.json and types/index.d.ts after a schema or kind manifest change
npm run check                                # the types are current; the generator's own tests
TSC=path/to/tsc npm run check                # also type-check the types and the valid examples
```

From the repository root, the engine tests check every example against the schemas and the engine's rules, the C# model against the schemas, and (when Node.js is on the PATH) that the types are current:

```bash
dotnet test InfoPanel.slnx --filter "FullyQualifiedName~InfoPanel.Core.Tests.Spec"
```

## Licence

The contents of this folder are licensed under the terms in [`LICENSE`](LICENSE) in this folder.
