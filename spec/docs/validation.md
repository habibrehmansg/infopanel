# Validation

Every reader checks content in the same order, and reports problems as `{ path, code, message }` with a JSON pointer into what it checked.

## Order

**Scene** (on its own, or inside a document or commit):

1. It is an object whose `format` is a scene format; a major version other than 1 is `unsupported.format`.
2. Every capability in `requires` is supported, or `unsupported.capability`.
3. Its size (`limit.sceneBytes`).
4. Its collection counts, on the raw JSON: `limit.inputs`, `limit.values`, `limit.stateCells`, `limit.nodes` (nodes at every depth).
5. Its nesting (`limit.jsonDepth`), its numbers (`json`, [below](#numbers)), then the schema (`schema`).
6. The rules and limits below.

**Document**: it is UTF-8 JSON (`json`); the same first two steps for the document format; its size; its item count, groups' children included (`limit.items`); its nesting (`limit.jsonDepth`); its numbers (`json`); the schema with each scene checked only for being an object with a string `format`; ids, nesting and sizes; then each scene on its own (a bad scene makes a placeholder, not a bad document); then the aggregate budgets, in document order.

**Commit**: its size; the op count (`limit.ops`) and each inserted item's count with its children (`limit.items`); its nesting (`limit.jsonDepth`); its numbers (`json`); the schema (scenes as for documents); each new scene and inserted item on its own (unsupported: a warning; invalid: an error); then, when it is applied, the checks in [commits](commit.md).

Content nested deeper than `jsonDepth` ([limits](limits.md#nesting-limit)) gets the one error `limit.jsonDepth` at `""` (the root of what was checked), and no schema errors. Content with a number that rounds to ±∞ gets a `json` error at each such number, and no schema errors.

**Media asset**: its size (an SVG's `limit.svgBytes` too, before it is read); its header and container structure, without decoding (an SVG is read as a stream, held to the [SVG profile](svg.md), and reading stops past `limit.svgElements`; then its expanded size, references included, is held to `limit.svgElements`, and an accepted SVG is rebuilt into the canonical document that is the only form decoded); its dimensions, frames and duration ([limits](limits.md)).

A check reports at most 100 errors; when there are more than 100 (101 included), a last error with code `more` (path `""`) says how many were not listed. There is never more than one `more` error.

## Numbers

A JSON number means the IEEE 754 64-bit double nearest its text (ties to even), as in I-JSON ([RFC 7493](https://www.rfc-editor.org/rfc/rfc7493)). This holds for every number in a scene, document, commit or other engine API body, opaque members included (an `origin`, editor data, a source's `data`, a scene of a newer format), and for the preview socket's `scale` and `maxFps`:

- Spellings of one double are one number: `1`, `1.0`, `10e-1` and `1.00000000000000001` are all 1, `9007199254740993` is 9007199254740992 (2<sup>53</sup>), and `1e-400` is 0.
- `-0` reads as `0`.
- Text that rounds to ±∞ (`1e400`, `-2e308`) is not a number: content holding one is refused, `json` at that number (the socket answers `error`).
- A member whose type is integer takes any number that reads as a whole double, however it is written (`1.0`, `1e2`, `2.00000000000000001`).
- Bounds, equality (what a change stamps and the dirty state, [commits](commit.md#what-a-change-stamps)), state keys ([identity](identity.md#runtime-state)) and every other comparison compare these doubles. A value that needs more precision than a double holds is carried as a string.

Where the engine keeps a value as it received it (the opaque members), a number keeps its spelling; elsewhere it may come back spelled otherwise. Its value as a double never changes.

## Codes

| Code | Meaning |
|---|---|
| `json` | Not JSON: a stored or imported document (or other body) that isn't well-formed UTF-8 JSON, or content with a number whose text rounds to ±∞ ([numbers](#numbers)), at that number. Content nested too deeply is `limit.jsonDepth`, not `json`. |
| `schema` | The JSON doesn't match the schema (unknown or missing member, wrong type, value out of the schema's range or pattern). The message names the schema keyword; `complexity` when the content is too complex to check within the validator's work budget ([limits](limits.md#checking-hostile-content)). |
| `unsupported.format` | A format major version this engine doesn't read. |
| `unsupported.capability` | A capability in `requires` this engine lacks. |
| `requires.missing` | The scene uses a capability it doesn't list in `requires`. |
| `id.duplicate` | Two nodes of a scene, or two items of a document (or of an inserted item), have the same id; or a frame header lists an item's bounds twice ([API](api.md#checks-beyond-the-schema)). |
| `ref.input` | An expression or node reads an input the scene doesn't declare. |
| `type.input` | An input is read as a type it isn't (a `series` of a text input, a `unit` of a table). |
| `ref.value`, `ref.state` | A value or state cell the scene doesn't define. |
| `ref.node` | `measure` names no text node of the scene. |
| `cycle.value` | Named values that depend on each other. |
| `cycle.measure` | A measured text node whose layout depends, directly or through values, on a measurement ([expressions](expressions.md#order-of-evaluation)). |
| `type.mismatch` | An expression's type doesn't fit where it is used (a read of a state cell included, against the cell's resolved type), or `if` branches, `steps` outputs, `eq` operands, or a state cell's `init` and `next` have no common type. |
| `steps.arity` | `steps` has other than one more output than thresholds. |
| `context.dt` | `dt` outside a state cell's `next`. |
| `context.state` | A state cell's `init` reads state, or its `next` reads a value that does. |
| `context.measure` | A measurement that could affect state. |
| `time.pattern` | A `formatTime` pattern with a reserved letter or an unclosed quote. |
| `path.syntax` | Path data that isn't valid SVG path data. |
| `text.box` | A marquee, word wrap or fit on a text node without `w`. |
| `limit.<name>` | Over the named limit ([limits](limits.md)): `limit.nodes`, `limit.exprWork`, `limit.documentNodes`, `limit.jsonDepth`, `limit.mediaSide`, ... |
| `media.format` | Not a supported media format. |
| `media.truncated`, `media.malformed` | The header or container structure is incomplete or broken. |
| `media.svg-external` | An SVG outside the [SVG profile](svg.md), an allowlist: an element, attribute, `style` property or value it doesn't list, a reference to anything outside the file (`data:` URIs included) or that doesn't resolve, a reference cycle, a CDATA section or processing instruction, ... |
| `media.svg-size` | An SVG whose `width` or `height` is in a unit other than `px`, a percentage, or not a non-negative number: unsupported. |
| `media.svg-parse` | An SVG that isn't well-formed UTF-8 XML (an XML declaration naming another encoding included), has a DTD, or is too long to read. |
| `more` | Not a problem itself: the count of errors not listed after the first 100. |
| `io` | Not a problem with content: a stored file the engine couldn't read (reported in the profile list, [API](api.md#profiles)). |
| `opId.reused` | An `opId` already applied with a different body ([commits](commit.md)). |
| `baseRev.future` | Not a check on content: a commit's or op's `baseRev`, or an undo's, redo's or revert's, greater than the document's `rev`, a revision it hasn't reached ([commits](commit.md#baserev-conflicts)). |
| `rev.mismatch` | Not a check on content: an engine API body whose revisions disagree, such as a document response whose `rev` isn't its document's ([API](api.md#checks-beyond-the-schema)). |
| `op.target` | `setDesign` or `setBindings` on a group. |
| `op.parent` | An `insert` or `move` into an item that isn't a group. |
| `op.cycle` | A group moved into itself or into something it contains. |

Conflicts and other refusals that depend on the document's state (409: `conflictProblem`, `expiredProblem`, `existsProblem`, `profileStatusProblem`) are not validation errors: see [commits](commit.md) and the [API](api.md).

## The examples

`examples/` holds valid content using every node, paint, effect, transform, operation and op, and invalid content for every rule and limit that can be checked without a document to apply to (the `op.*`, `opId.reused` and `baseRev.future` checks need one); `examples/expected.json` gives each invalid example's code and path, and the limits an example tightens to stay small. The engine's tests check every example against the schemas and the rules; any validator for these formats can run the same set.
