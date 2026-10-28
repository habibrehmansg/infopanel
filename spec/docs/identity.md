# Identity and state

## Instance ids

Every node may have an `id`, unique within its scene (`^[A-Za-z_][A-Za-z0-9_.-]{0,63}$`): `measure` names text nodes by it, and a client can use node ids to relate what it sees back to how it produced the scene. Every state cell's name is its instance id. A client that generates scenes should generate these ids deterministically, from the structure that produced them, so that producing a scene again gives the same ids for the same parts.

## Runtime state

The engine keeps a little state between ticks: the value of every **state cell** (the only state in version 1; media playback is a function of the tick clock). State belongs to the renderer, not the document: it is never saved, never part of a revision and never sent to clients.

A state cell's state is keyed by **(item id, cell name, cell definition, cell type)**, where the definition is the cell's `init` and `next` in the formats' canonical form ([sizes](limits.md#sizes): RFC 8785, each number as the double it reads as, [validation](validation.md#numbers)): `init: 0`, `init: -0` and `init: 0.0` define one cell. The type is the cell's resolved type ([expressions](expressions.md#state-cells)), which depends on what `next` reads as well as on the definition: a cell whose `next` takes a named value is a colour cell while the value gives a colour and a string cell once it gives text, with the same `init` and `next`. A kept value always has the cell's type. So:

| Change | The cell's state |
|---|---|
| The item is moved, rotated, rebound, hidden, renamed, locked, reordered or moved into another group | Kept |
| The item gets a new scene (`setDesign`) in which the cell has the same name, definition and resolved type | Kept |
| The cell is new, renamed or its `init` or `next` changed | Starts again: `init` on its next tick |
| The cell's resolved type changed, though its `init` and `next` didn't (a value it reads gives text instead of a colour) | Starts again: `init` on its next tick |
| The item is removed | Dropped. Undoing the removal brings the item back; its cells start again. |
| Undo or redo | The document goes back or forward to a previous version; every cell follows the rules above against that version, so a cell whose definition the undo restores starts again from `init` (its earlier values are not brought back). |
| No output renders the document | The engine may drop the document's state after a while; when rendering resumes, every cell starts again. |

## Time

Behaviour that changes over time is defined in elapsed time, never in ticks or frames:

- `{"op": "time", "field": "elapsed"}` is the seconds since the engine started rendering the document; it starts at 0 when rendering starts (again).
- `{"op": "dt"}` is the seconds between a cell's previous update and this one.
- Marquees, animated media and animated paints are functions of `elapsed`; smoothing, rate limits and peak holds are state cells over `dt`.

So a scene whose behaviour over time is written this way behaves the same whether the engine renders it at 5 or 60 frames a second, and two engines that render it at different rates show the same thing at the same moment (up to the time between their ticks). For a state cell that holds when its `next` gives the same result over an interval, with what it reads held, however the interval is cut into ticks: an update proportional to `dt` (`previous + rate·dt`, a hold counting `age + dt`), a rate limit (`previous` moved towards a target by at most `rate·dt`) or exponential smoothing (`previous + (target − previous)·(1 − exp(−dt / τ))`). An engine samples a cell once per tick and doesn't integrate it, so other updates depend on the rate: `previous + dt·dt` reaches 1 after one second at 1 tick a second and 0.5 at 2, and `previous + 1` counts ticks.

## The tick snapshot

The engine renders a document in ticks. In each tick it reads, once:

- the time (`elapsed`, the wall clock and the time zone),
- every input (readings, statistics, status, history, tables),
- the media samples (the frame of each animated or provider media),

then evaluates the values and state cells and lays out the text, once, in the two phases of [expressions](expressions.md#order-of-evaluation). Everything drawn or measured for that tick (every output at every scale, the bounds, a thumbnail taken during the tick) uses that one snapshot, so they agree pixel for pixel and with each other. State advances only when the tick does: drawing a tick again (another scale, a thumbnail) never moves it.

A one-off render of a document no output is rendering (a thumbnail) takes a fresh snapshot without advancing anything: every state cell at `init`, `elapsed` 0, the current inputs and time.

## Determinism

Given the same document, the same inputs and the same tick times, and the same fonts, locale and time zone, every engine computes the same values, state and text (all arithmetic is 64-bit IEEE 754 in the order the operations define). The pixels may differ slightly between engines (antialiasing, font rasterisation); the values don't.

The three conditions are what an engine reads from the computer it runs on:

- **Fonts**: the installed fonts and their fallback decide every text layout, so what `measure` reads, where text wraps, fits or is cut, and the values that follow from them.
- **Locale**: the engine's locale gives the month and weekday names and the AM/PM marker of a `formatTime` without `locale`; the locale data gives them for every locale named.
- **Time zone**: the engine's local zone gives the clock and calendar fields of a `time` or `formatTime` without `zone` (`elapsed` and `epoch` don't depend on it); the time zone data gives them for every zone named.

A scene that names a `locale` and a `zone` wherever it reads the time depends on the computer only through its fonts and its locale and time zone data.
