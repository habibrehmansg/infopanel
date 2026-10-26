# InfoPanel formats

The formats an InfoPanel engine reads and writes, specified so that any client can produce them: the **document** (a profile and its items), the **scene** each item draws, and the engine API bodies that carry them (commits, conflicts, bounds). A client writes or generates scenes against this specification; the engine validates, stores and renders them. How a client produces a scene is up to the client.

**Status: draft.** `infopanel.document/1` and `infopanel.scene/1` may still change until the engine release that first ships them. From then on they change only as [versioning](docs/versioning.md) allows.

## Contents

| Path | What |
|---|---|
| `schemas/` | JSON Schemas (draft 2020-12): `common`, `expression`, `scene` (`infopanel.scene/1`), `document` (`infopanel.document/1`), `api` (commit request and result, problems, item bounds, format support). Ids are `urn:infopanel:<name>:1`. |
| `types/index.d.ts` | TypeScript types, generated from the schemas (`npm run gen`). |
| `docs/` | The specification in prose (below). |
| `examples/` | Valid and invalid documents, scenes, commits, API bodies and media; `examples/expected.json` gives each invalid example's error code and path. |
| `tools/` | `gen-types.mjs` (schemas to TypeScript, no dependencies), `check.mjs` (the types are current; with `TSC` set, every valid example type-checks). |
| `package.json` | `@infopanel/spec`, private for now: use it by path. |

## The specification

1. [Document](docs/document.md): profile, items, ids and revisions, source and origin, guides.
2. [Geometry](docs/geometry.md): placement, layout box, pivot, measured bounds, groups, clipping, drawing order.
3. [Bindings](docs/bindings.md): scene inputs, sensor bindings, statistics, status, history.
4. [Scene](docs/scene.md): nodes, paints, strokes, effects, transforms, clips, the drawing model.
5. [Expressions](docs/expressions.md): types, operations, values, state cells, time patterns, evaluation order.
6. [Smart nodes](docs/smart-nodes.md): `text`, `series`, `table`, `media`.
7. [Identity and state](docs/identity.md): instance ids, what keeps and what resets state, the tick snapshot.
8. [Commits](docs/commit.md): per-item per-field ops, `baseRev`, `opId`, coalescing, 409 and 422.
9. [Unsupported content](docs/unsupported.md): placeholders, never rewriting what an engine can't read.
10. [Versioning](docs/versioning.md): formats, minor and major versions, capabilities and `requires`.
11. [Validation](docs/validation.md): the order of checks and every error code.
12. [Limits and budgets](docs/limits.md): the numbers, and the performance budgets they serve.
13. [SVG profile](docs/svg.md): the restricted SVG that media assets may use.

## Checks

From `spec/`:

```bash
npm run gen                                  # regenerate types/index.d.ts after a schema change
npm run check                                # the types are current; the generator's own tests
TSC=path/to/tsc npm run check                # also type-check the types and every valid example
```

From the repository root, the engine tests check every example against the schemas and the engine's rules, the C# model against the schemas, and (when Node.js is on the PATH) that the types are current:

```bash
dotnet test InfoPanel.slnx --filter "FullyQualifiedName~InfoPanel.Core.Tests.Spec"
```

## Licence

The contents of this folder are licensed under the terms in [`LICENSE`](LICENSE) in this folder.
