# InfoPanel formats

The formats an InfoPanel engine reads and writes, specified so that any client can produce them: the **document** (a profile and its items), the **scene** each item draws, and the engine API that carries them: its requests and bodies (the session, sensors, the profile list, documents, blobs, commits, conflicts, problems, the preview socket's messages, frames and their bounds). A client writes or generates scenes against this specification; the engine validates, stores and renders them. How a client produces a scene is up to the client.

**Status: draft.** `infopanel.document/1` and `infopanel.scene/1` may still change until the engine release that first ships them. From then on they change only as [versioning](docs/versioning.md) allows.

## Contents

| Path | What |
|---|---|
| `schemas/` | JSON Schemas (draft 2020-12): `common`, `expression`, `scene` (`infopanel.scene/1`), `document` (`infopanel.document/1`), `api` (session and format support, sensors and readings, profile list, profile creation and duplication, document, blob, commit request, undo, redo and revert request and result, problems, integration tokens, the preview socket's messages, frame header with item bounds, change event). Ids are `urn:infopanel:<name>:1`. |
| `openapi.json` | The engine API's requests ([below](#the-request-index)): every operation, its parameters and each status it answers, with its bodies' definitions in `schemas/`. |
| `types/index.d.ts` | TypeScript types, generated from the schemas (`npm run gen`), and `ApiOperations`, generated from `openapi.json`: each request, keyed `'METHOD /path'`, with its `params`, `query`, `request` and `responses` by status. |
| `docs/` | The specification in prose (below). |
| `examples/` | Valid and invalid documents, scenes, commits, API bodies (`api/{definition}/`, refused ones in its `invalid/`) and media; `examples/expected.json` gives each invalid example's error code and path. |
| `tools/` | `gen-types.mjs` (schemas and `openapi.json` to TypeScript, no dependencies), `check.mjs` (the types are current; `openapi.json` keeps to its subset, its `$ref`s resolve, its operation ids are unique, every `api` definition is used by a request or the socket, and `docs/api.md`'s request table lists exactly its operations; with `TSC` set, every valid example type-checks, except those nested deeper than 32 levels, which the compiler can't compare with the recursive JSON types, and so do `ApiOperations` and the response examples assigned to it). |
| `package.json` | `@infopanel/spec`, private for now: use it by path. |

## The specification

1. [Document](docs/document.md): profile, items, ids and revisions, source and origin, guides.
2. [Geometry](docs/geometry.md): placement, layout box, pivot, measured bounds, groups, clipping, drawing order.
3. [Bindings](docs/bindings.md): scene inputs, sensor bindings, statistics, status, history.
4. [Scene](docs/scene.md): nodes, paints, strokes, effects, transforms, clips, the drawing model.
5. [Expressions](docs/expressions.md): types, operations, values, state cells, time patterns, evaluation order.
6. [Smart nodes](docs/smart-nodes.md): `text`, `series`, `table`, `media`.
7. [Identity and state](docs/identity.md): instance ids, what keeps and what resets state, the tick snapshot.
8. [Commits](docs/commit.md): per-item per-field ops, what a change stamps, `baseRev`, `opId` and the retry window, coalescing, 409 and 422; undo, redo, revert and save.
9. [Engine API](docs/api.md): the requests, errors and tokens, the session, fonts and sensors, the profile list and its statuses, creating, duplicating, deleting and exporting profiles, documents, blobs, the preview socket's messages, frames and change events.
10. [Unsupported content](docs/unsupported.md): placeholders, never rewriting what an engine can't read.
11. [Versioning](docs/versioning.md): formats, minor and major versions, capabilities and `requires`.
12. [Validation](docs/validation.md): the order of checks and every error code.
13. [Limits and budgets](docs/limits.md): the numbers, and the performance budgets they serve.
14. [SVG profile](docs/svg.md): the restricted SVG that media assets may use.

## The request index

`openapi.json` is a hand-written OpenAPI 3.1.1 document that indexes the engine API: one operation per request in [the API's table](docs/api.md), with an `operationId`, its path and query parameters, its request body and, per status, its media type and body. It holds no body schemas of its own: every JSON body is a `$ref` to a definition in `schemas/` (or a `oneOf` of them, or an array of one), named by the schema's `$id` (`urn:infopanel:api:1#/$defs/session`), so a tool must resolve `$ref`s by `$id`, not by location. Errors shared by many operations are in `components.responses`; the token is a bearer `securityScheme`, and `security: []` marks what needs none. `GET /ws` is the preview socket: its `x-infopanel-socket` names the subprotocol and the definitions of the client's and the engine's messages and of a frame's header.

It uses a subset of OpenAPI: the objects and fields above, and in schemas the same JSON Schema keywords as `schemas/`. OpenAPI-only keywords (`discriminator`, `xml`, `example`, `nullable`, …) and features the engine API doesn't use (callbacks, links, webhooks, examples, headers' styles) are refused by `npm run check`.

## Checks

From `spec/`:

```bash
npm run gen                                  # regenerate types/index.d.ts after a schema change
npm run check                                # the types are current; the generator's own tests
TSC=path/to/tsc npm run check                # also type-check the types and the valid examples
```

From the repository root, the engine tests check every example against the schemas and the engine's rules, the C# model against the schemas, and (when Node.js is on the PATH) that the types are current:

```bash
dotnet test InfoPanel.slnx --filter "FullyQualifiedName~InfoPanel.Core.Tests.Spec"
```

## Licence

The contents of this folder are licensed under the terms in [`LICENSE`](LICENSE) in this folder.
