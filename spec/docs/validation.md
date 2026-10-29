# Validation

Every reader checks content in the same order, and reports problems as `{ path, code, message }` with a JSON pointer into what it checked. Codes and paths are part of the formats: frozen with their major version, each path exact, and a code a client doesn't know handled by its family ([versioning](versioning.md#error-codes-and-paths)); `message` is for people.

## Order

Every reader checks what it receives in this order: first the read caps on the bytes received or stored (a commit's `limit.commitBytes`; a create's body, an archive's document or a stored document over `documentBytes` plus 64 KiB is `limit.documentBytes`, unread; [sizes](limits.md#sizes)); then the [JSON-level pass](#json), once, on the content exactly as received, before any member is read for its meaning, taken out or replaced; then the format and its major version; then the size (on the canonical form), counts, schema and rules below. Content the JSON-level pass refuses gets its errors and nothing else.

**Scene** (on its own, or inside a document or commit, whose JSON-level pass covers it):

1. The [JSON-level pass](#json): its nesting (`limit.jsonDepth`), its numbers, member names and strings (`json`, `schema`).
2. It is an object whose `format` is a scene format; a major version other than 1 is `unsupported.format`.
3. Every capability in `requires` is supported, or `unsupported.capability`.
4. Its size (`limit.sceneBytes`).
5. Its collection counts, on the raw JSON: `limit.inputs`, `limit.values`, `limit.stateCells`, `limit.nodes` (nodes at every depth).
6. The schema (`schema`).
7. The rules and limits below.

**Document**: it is UTF-8 JSON (`json`); the JSON-level pass; the same format steps for the document format; its size; its item count, groups' children included (`limit.items`); the schema with each scene checked only for being an object with a string `format`; ids, nesting and sizes; then each scene on its own (a bad scene makes a placeholder, not a bad document); then the aggregate budgets, in document order. A stored document is read before its folder's id replaces its own, and a create's or import's document before the request's id does.

**Commit**: its size (`limit.commitBytes`); the JSON-level pass; the op count (`limit.ops`) and each inserted item's count with its children (`limit.items`); the schema (scenes as for documents); each new scene and inserted item on its own (unsupported: a warning; invalid: an error); then, when it is applied, the checks in [commits](commit.md).

A request body is read the same way: the JSON-level pass on the whole body, then its schema. A create's `{id, document}` body is read as a whole, every repeated member included, with each member's nesting counted from that member (a `document` nested too deeply is `limit.jsonDepth` at `/document`).

**Media asset**: its size (an SVG's `limit.svgBytes` too, before it is read); its header and container structure, without decoding (an SVG is read as a stream, held to the [SVG profile](svg.md), and reading stops past `limit.svgElements`; then its expanded size, references included, is held to `limit.svgElements`, and an accepted SVG is rebuilt into the canonical document that is the only form decoded); its dimensions, frames and duration ([limits](limits.md)).

A check reports at most 100 errors; when there are more than 100 (101 included), a last error with code `more` (path `""`) says how many were not listed. There is never more than one `more` error.

## JSON

The JSON-level pass reads content as these formats read JSON ([RFC 8259](https://www.rfc-editor.org/rfc/rfc8259) in UTF-8, as restricted by I-JSON, [RFC 7493](https://www.rfc-editor.org/rfc/rfc7493)), in this order:

1. **Nesting**: content nested deeper than `jsonDepth` ([limits](limits.md#nesting-limit)) gets the one error `limit.jsonDepth` at `""` (the root of what was checked), and is checked no further.
2. **Numbers**: a number whose text rounds to ±∞ is `json` at that number ([numbers](#numbers)).
3. **Member names**: an object that repeats a member name is `schema` at that object, once per repeated name. This holds at every depth, opaque members included (an `origin`, editor data, a source's `data`, a scene of a newer format), and every occurrence counts: a repeated member is never resolved by keeping one of them.
4. **Unicode**: every string and member name is Unicode text, a sequence of scalar values. A lone surrogate escape (`"\ud800"`, a low surrogate escape not after a high one) or bytes that aren't UTF-8 are `json` at that string; in a member name, at its object (whose value is then not read). Strings are compared and stored as they are, never normalised.

Errors of steps 2 to 4 are reported together, in the order of their paths; when there is any, nothing after the pass is checked (no format, size, count, schema or rule errors).

A schema `pattern` is a regular expression as ECMA-262 reads it, as JSON Schema says: `$` matches only at the end of the string, so `"#ff0000\n"` is not a colour and `"…\n"` is not an id.

## Numbers

A JSON number means the IEEE 754 64-bit double nearest its text (ties to even), as in I-JSON ([RFC 7493](https://www.rfc-editor.org/rfc/rfc7493)). This holds for every number in a scene, document, commit or other engine API body, opaque members included (an `origin`, editor data, a source's `data`, a scene of a newer format), and for the preview socket's `scale` and `maxFps`:

- Spellings of one double are one number: `1`, `1.0`, `10e-1` and `1.00000000000000001` are all 1, `9007199254740993` is 9007199254740992 (2<sup>53</sup>), and `1e-400` is 0.
- `-0` reads as `0`.
- Text that rounds to ±∞ (`1e400`, `-2e308`) is not a number: content holding one is refused, `json` at that number (the socket answers `error`).
- A member whose type is integer takes any number that reads as a whole double, however it is written (`1.0`, `1e2`, `2.00000000000000001`).
- Bounds, equality (what a change stamps and the dirty state, [commits](commit.md#what-a-change-stamps)), state keys ([identity](identity.md#runtime-state)) and every other comparison compare these doubles. A value that needs more precision than a double holds is carried as a string.

The engine keeps an opaque member as the same JSON value, not the same text: it stores and exports documents in the canonical form ([sizes](limits.md#sizes)), so a number may come back spelled otherwise (`1.50` as `1.5`, `1e2` as `100`), in opaque members too. Its value as a double never changes.

## Codes

Each code is reported at the value it is about ([versioning](versioning.md#error-codes-and-paths)), as the examples pin it.

| Code | Meaning |
|---|---|
| `json` | Not JSON as these formats read it ([JSON](#json)): a stored or imported document (or other body) that isn't well-formed UTF-8 JSON; a number whose text rounds to ±∞ ([numbers](#numbers)), at that number; a string that isn't Unicode text, at that string (a member name's at its object). Content nested too deeply is `limit.jsonDepth`, not `json`. |
| `schema` | The JSON doesn't match the schema (unknown or missing member, wrong type, value out of the schema's range or pattern), or an object repeats a member name ([JSON](#json)): at the value that breaks it (an unknown member or a member name the schema refuses at that member), and at the object a member is missing from or repeated in. The message names the schema keyword; `complexity` when the content is too complex to check within the validator's work budget ([limits](limits.md#checking-hostile-content)). |
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

The 1.x import's codes (`import.*`, and the `archive.*` codes of the archive guards) are on its own page ([1.x import](import.md#diagnostics)): `import.version`, `import.archive`, `import.xml` and `archive.*` refuse a request, every other `import.*` code is a diagnostic in an answer that succeeded. A profile archive's own codes (`archive.manifest`, `archive.document`, `archive.unreferenced`, `archive.hash`, `archive.v1`) are on its page ([archive](archive.md#importing)); its manifest is checked in this page's order, its errors under `/manifest`.

Conflicts and other refusals that depend on the document's or the engine's state (409: `conflictProblem`, `expiredProblem`, `existsProblem`, `profileStatusProblem`, `settingsStatusProblem`) are not validation errors: see [commits](commit.md) and the [API](api.md).

## The examples

`examples/` holds valid content using every node, paint, effect, transform, operation and op, and invalid content for every rule and limit that can be checked without a document to apply to (the `op.*`, `opId.reused` and `baseRev.future` checks need one); `examples/expected.json` gives each invalid example's code and exact path (`""` for a media asset), each placeholder's path, problem, and its first error's code and path (`errorPath`), and the limits an example tightens to stay small. A create's body is read as the shape its members choose (`{ "id", "document" }` when it has a `document`, else an empty profile's), so its errors are at their paths in that shape, where a schema validator that tries both shapes can only refuse the body as a whole. The engine's tests check every example against the schemas and the rules, comparing paths exactly ([versioning](versioning.md#error-codes-and-paths)); any validator for these formats can run the same set.
