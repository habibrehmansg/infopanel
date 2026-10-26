# Expressions

Schema: `urn:infopanel:expression:1`. Example using every operation: `examples/scene/valid/expressions.json`.

Wherever a scene member holds a number, string, bool or colour, it may hold an expression instead of a literal. An expression is a tree of JSON objects whose `op` names the operation; literals are plain JSON values. Expressions have no loops, no recursion and no side effects; they are evaluated once per tick ([identity](identity.md)).

```jsonc
"w": { "op": "mul", "args": [ { "op": "box", "field": "w" }, { "op": "value", "name": "pct" } ] }
```

## Types

There are four types: **number** (64-bit float), **string**, **bool** and **color**. A JSON number is a number, `true`/`false` a bool, and a JSON string a string, or a colour where a colour is expected (then it must be `#RRGGBB` or `#RRGGBBAA`). The schemas give every member its type; for named values, state cells and the operands of `eq`, `ne`, `if` and `steps` the engine infers types and refuses a scene where they don't fit (`type.mismatch`). A string literal that is a colour literal has both types until where it is used decides.

## Operations

Positional operands are in `args`:

| `op` | Operands → result | Meaning |
|---|---|---|
| `add`, `mul`, `min`, `max` | 2–16 numbers → number | |
| `sub`, `div`, `mod`, `pow` | [a, b] → number | a − b; a ÷ b (0 when b = 0); floored modulo a − b·floor(a/b) (0 when b = 0); a^b |
| `atan2` | [y, x] → number | The angle of (x, y) in degrees |
| `neg`, `abs`, `floor`, `ceil`, `round`, `sqrt`, `exp`, `log`, `sin`, `cos` | [x] → number | `round` is half away from zero; `log` natural; `sin`, `cos` take degrees |
| `clamp` | [x, lo, hi] → number | |
| `norm` | [x, lo, hi] → number | (x − lo) ÷ (hi − lo), 0 when hi = lo; not clamped |
| `lerp` | [a, b, t] numbers → number | a + (b − a)·t, not clamped |
| `lerp` | [a, b, t] colours and a number → color | Per channel, straight alpha, in sRGB; t clamped to 0–1 |
| `lt`, `le`, `gt`, `ge` | [a, b] numbers → bool | |
| `eq`, `ne` | [a, b] of one type → bool | Numbers exactly, strings by code point, colours by value |
| `and`, `or` | 2–16 bools → bool | Every operand is evaluated (there are no side effects) |
| `not` | [x] → bool | |
| `if` | [condition, then, else] → the branches' type | |
| `concat` | 1–32 strings → string | |
| `upper`, `lower` | [s] → string | Locale-independent case mapping |
| `rgba` | [r, g, b, a] → color | r, g, b 0–255, a 0–1, each clamped |
| `alpha` | [color, a] → color | The colour with its alpha replaced (0–1) |

Named operands:

| `op` | Members → result | Meaning |
|---|---|---|
| `input` | `name`, `field` → number, string or bool | An input's reading ([bindings](bindings.md)): `value` (default; the binding's statistic), `now`, `min`, `max`, `avg`, `unit` (number inputs), `text` (text and table inputs), `status`, `available` (any input) |
| `history` | `input`, `fn`, `count` → number | Over the last `count` (1–4096) samples: `min`, `max`, `avg`, `first`, `last`, `count`; 0 without samples |
| `time` | `field`, `zone` → number | From the tick's clock: `elapsed` (seconds since the engine started rendering the document, monotonic), `epoch` (seconds since 1970-01-01T00:00:00Z), `dayTime` (seconds since local midnight, with fraction), `year`, `month` (1–12), `day` (1–31), `weekday` (1 Monday … 7 Sunday), `hour` (0–23), `minute`, `second`, `millisecond` |
| `formatTime` | `pattern`, `locale`, `zone` → string | The tick's date and time as text (below) |
| `format` | `value`, `decimals`, `group`, `decimalSeparator`, `groupSeparator` → string | A number rounded half away from zero to `decimals` (0–6, default 0) places; with `group`, `groupSeparator` (default ",") between groups of three integer digits; `decimalSeparator` default "." |
| `box` | `field` (`w`, `h`) → number | The item's layout box; an automatic dimension reads 0 |
| `measure` | `node`, `field` (`w`, `h`) → number | The measured size of a text node of this scene this tick |
| `value` | `name` → its type | A named value |
| `state` | `name` → its type | A state cell (its previous value inside a cell's `next`) |
| `dt` | → number | Seconds since the cell's previous update; only directly in a state cell's `next` |
| `steps` | `value`, `at` (thresholds), `out` → the outputs' type | `out[i]`, where i is how many thresholds `value` is at or above; `out` has one more entry than `at` (colour rules: `"at": [70, 85], "out": ["#e5e7eb", "#f59e0b", "#ef4444"]`) |

`zone` is an IANA zone name such as `Europe/Berlin`; absent, the engine's local zone. An engine that doesn't know a zone uses the local one and reports it.

## Numbers and strings

- Any operation whose result isn't finite (√−1, log 0, overflow) gives 0, so a scene never draws NaN.
- Comparisons are exact; there is no epsilon.
- Strings are sequences of Unicode code points. A string literal is at most 4,096 code points (schema).
- **Every operation that produces a string** (`concat`, `upper`, `lower`, `format`, `formatTime`, and `input` with a text field: `unit`, `text`, `status`) **cuts its result to its first 4,096 Unicode scalar values** (`textLength`, [limits](limits.md)), at the operation, before the result is used anywhere: by another operation, a comparison (`eq`, `ne`), a value, a state cell or a text node. So `concat` of two 4,096-value strings is the first string, and `eq` compares the cut strings. No string an expression holds is ever longer, however the operations are chained.
- A colour compares equal to another with the same 8-digit value; `#ff0000` equals `#FF0000FF`.

## Time patterns

`formatTime` patterns use letters for fields (a subset of the Unicode LDML date patterns); text in single quotes is literal (`''` is a quote) and every other non-letter stands for itself. Every other ASCII letter is reserved, and a pattern with one is refused (`time.pattern`).

| Letters | Field |
|---|---|
| `yyyy`, `yy`, `y` | Year: four digits, two digits, as is |
| `M`, `MM`, `MMM`, `MMMM` | Month: 1–12, 01–12, abbreviated name, full name |
| `d`, `dd` | Day of the month |
| `E` or `EEE`, `EEEE` | Weekday: abbreviated, full name |
| `H`, `HH` | Hour 0–23 |
| `h`, `hh` | Hour 1–12 |
| `a` | AM/PM marker |
| `m`, `mm` | Minute |
| `s`, `ss` | Second |
| `S`, `SS`, `SSS` | Tenths, hundredths, milliseconds |

Names and the AM/PM marker come from `locale` (a BCP 47 tag such as `en-GB`), or the engine's locale when it is absent. Two letters pad with a zero.

## Named values

`values` maps names to expressions. A value is evaluated at most once per tick, however often it is read, which keeps shared work (a percentage that several nodes use) cheap. Values may read other values, but not in a cycle (`cycle.value`, reported once per cycle, at the reference that closes it when the values are searched in the order they are written). A value's type is inferred from its expression.

## State cells

`state` maps names to `{ "init", "next" }`: values that carry over from one tick to the next, defined in elapsed time, never in tick counts, so they behave the same at any frame rate.

- On a cell's first tick it takes `init`; on each later tick it takes `next`, evaluated with the **previous** values of all cells (`{"op": "state"}`) and `{"op": "dt"}`, the seconds since that previous tick.
- `init` can't read state (`context.state`). `next` can read state cells directly, but not through a named value that reads state, whose meaning there would be ambiguous (`context.state`).
- A cell has one type, resolved once from `init` and `next` together: their common type (`type.mismatch` at `next` when they have none), where `next` reads the cells' types. So `init: "#000000"` with `next: "not-a-color"` makes a string cell, and a paint that reads it is refused (`type.mismatch` at the paint): every read of a cell is checked against its resolved type. (An engine resolves the types by starting each cell at its `init` type and narrowing every cell by its `next` until nothing changes.)
- A one-off render (a thumbnail with no live rendering) shows every cell at `init`, so a smoothed bar is drawn at its target.

Typical cells (all in `examples/scene/valid/`):

```jsonc
// exponential smoothing with a 0.25 s time constant (bar.json)
"next": prev + (target − prev) · (1 − exp(−dt / 0.25))
// rate limit: at most 8 frames a second (gauge-sequence.json)
"next": prev + clamp(target − prev, −8·dt, 8·dt)
// peak hold for 1.5 s, then fall at 0.4 per second (peak-hold.json: two cells, peak and age)
```

## Contexts

Where an expression is evaluated limits what it may read:

| Context | May not read |
|---|---|
| A state cell's `init` | state, `dt`, `measure` (directly or through values) |
| A state cell's `next` | named values that read state, `measure` (directly or through values) |
| A named value | `dt` |
| The layout of a text node that is measured (its `text`, `font.size`, `w`, `h`, `fit`, `lineHeight`, `letterSpacing`, spans' text and size) | `measure`, directly or through values (`cycle.measure`) |
| Anywhere else | `dt` |

## Order of evaluation

Each tick evaluates in this order, which the context rules make well defined, with no cycles:

1. The inputs (frozen for the tick).
2. **Phase 1**: the named values that don't depend on state or `measure` (transitively, through other values), then the state cells (each `next` with the previous tick's cells), then the values that depend on state but not on `measure`; then the layout of every text node whose layout members don't depend on `measure`.
3. **Phase 2**: the values that depend on `measure` (they read the phase-1 layouts), then the layout of the text nodes whose layout members depend on them, then everything else (the other node members, which may read anything but `dt`).

A text node laid out in phase 2 can't be measured: `measure` of a text node whose own content or style depends, directly or through values, on a measurement is a cycle (`cycle.measure`), even when the measurement is of another node. Values are evaluated in dependency order within each step.

## Cost

Each operation object counts 1 towards a scene's expression work, except the string-producing operations `concat`, `upper`, `lower`, `format` and `formatTime`, which count 16 each (each may copy up to 4,096 scalar values), and `history`, which counts 1 more per 64 samples (rounded up); values and state cells count once, however often they are read. A scene has at most 20,000 units of work and expressions nested at most 32 deep ([limits](limits.md)).
