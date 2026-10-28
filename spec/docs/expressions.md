# Expressions

Schema: `urn:infopanel:expression:1`. Example using every operation: `examples/scene/valid/expressions.json`. Exact results: [below](#exact-results) and `examples/expressions/vectors.json`.

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
| `atan2` | [y, x] → number | The angle of (x, y) in degrees, −180 to 180 ([degrees](#degrees)) |
| `neg`, `abs`, `floor`, `ceil`, `round`, `sqrt`, `exp`, `log`, `sin`, `cos` | [x] → number | `round` is half away from zero; `log` natural; `sin`, `cos` take degrees ([degrees](#degrees)) |
| `clamp` | [x, lo, hi] → number | |
| `norm` | [x, lo, hi] → number | (x − lo) ÷ (hi − lo), 0 when hi = lo; not clamped |
| `lerp` | [a, b, t] numbers → number | a + (b − a)·t, not clamped |
| `lerp` | [a, b, t] colours and a number → color | Per channel, straight alpha, in sRGB: a + (b − a)·t with t clamped to 0–1, rounded half away from zero ([colour channels](#colour-channels)) |
| `lt`, `le`, `gt`, `ge` | [a, b] numbers → bool | |
| `eq`, `ne` | [a, b] of one type → bool | As the operands' common type, a string when both can be strings ([equality](#equality)): numbers exactly, strings by code point, colours by value |
| `and`, `or` | 2–16 bools → bool | Every operand is evaluated (there are no side effects) |
| `not` | [x] → bool | |
| `if` | [condition, then, else] → the branches' type | |
| `concat` | 1–32 strings → string | |
| `upper`, `lower` | [s] → string | Locale-independent case mapping ([case mapping](#case-mapping)) |
| `rgba` | [r, g, b, a] → color | r, g, b 0–255, a 0–1, each clamped and rounded half away from zero ([colour channels](#colour-channels)) |
| `alpha` | [color, a] → color | The colour with its alpha replaced (a 0–1, clamped and rounded as `rgba`'s) |

Named operands:

| `op` | Members → result | Meaning |
|---|---|---|
| `input` | `name`, `field` → number, string or bool | An input's reading ([bindings](bindings.md)): `value` (default; the binding's statistic), `now`, `min`, `max`, `avg`, `unit` (number inputs), `text` (text and table inputs), `status`, `available` (any input) |
| `history` | `input`, `fn`, `count` → number | Over the last `count` (1–4096) samples: `min`, `max`, `avg`, `first`, `last`, `count`; 0 without samples |
| `time` | `field`, `zone` → number | From the tick's clock: `elapsed` (seconds since the engine started rendering the document, monotonic), `epoch` (seconds since 1970-01-01T00:00:00Z), `dayTime` (seconds since local midnight, with fraction), `year`, `month` (1–12), `day` (1–31), `weekday` (1 Monday … 7 Sunday), `hour` (0–23), `minute`, `second`, `millisecond` |
| `formatTime` | `pattern`, `locale`, `zone` → string | The tick's date and time as text (below) |
| `format` | `value`, `decimals`, `group`, `decimalSeparator`, `groupSeparator` → string | A number rounded half away from zero to `decimals` (0–6, default 0) places; with `group`, `groupSeparator` (default ",") between groups of three integer digits; `decimalSeparator` default "." ([number formatting](#number-formatting)) |
| `box` | `field` (`w`, `h`, `rotation`) → number | The item's layout box; an automatic dimension reads 0. `rotation`: the item's rotation on the canvas in degrees, its own and its groups' summed ([geometry](geometry.md#layout-box-an-input-to-the-scene)) |
| `measure` | `node`, `field` (`w`, `h`) → number | The measured size of a text node of this scene this tick: its laid-out text (the widest line's width, the lines' height), not its box |
| `value` | `name` → its type | A named value |
| `state` | `name` → its type | A state cell (its previous value inside a cell's `next`) |
| `dt` | → number | Seconds since the cell's previous update; only directly in a state cell's `next` |
| `steps` | `value`, `at` (thresholds), `out` → the outputs' type | `out[i]`, where i is how many thresholds `value` is at or above; `out` has one more entry than `at`, all of one type: numbers, strings, bools or colours (colour rules: `"at": [70, 85], "out": ["#e5e7eb", "#f59e0b", "#ef4444"]`) |

`zone` is an IANA zone name such as `Europe/Berlin`; absent, the engine's local zone. An engine that doesn't know a zone uses the local one and reports it.

## Numbers and strings

- Any operation whose result isn't finite (√−1, log 0, overflow) gives 0, so a scene never draws NaN.
- **No number is negative zero.** A literal `-0` reads as `0` ([validation](validation.md#numbers)), and so does every result and reading that is zero: `mul(-1, 0)`, `neg(0)`, `ceil(-0.5)`, `round(-0.4)` and an input reading of −0 are all `0`. So `atan2(mul(-1, 0), -1)` is 180, never −180, and nothing an expression computes depends on a zero's sign.
- Comparisons are exact; there is no epsilon: `eq(add(0.1, 0.2), 0.3)` is false.
- Strings are sequences of Unicode code points. A string literal is at most 4,096 code points (schema).
- **Every operation that produces a string** (`concat`, `upper`, `lower`, `format`, `formatTime`, and `input` with a text field: `unit`, `text`, `status`) **cuts its result to its first 4,096 Unicode scalar values** (`textLength`, [limits](limits.md)), at the operation, before the result is used anywhere: by another operation, a comparison (`eq`, `ne`), a value, a state cell or a text node. So `concat` of two 4,096-value strings is the first string, and `eq` compares the cut strings. No string an expression holds is ever longer, however the operations are chained.

## Equality

`eq` and `ne` compare their operands as the type they have in common, and when more than one type fits, in this order: number, bool, string, colour. Only a string literal that is a colour literal (and a value, cell, `if` or `steps` of such literals only) has two types, string and colour, so the order matters only there:

- Two such literals, or expressions of only such literals, compare **as text**, by code point: `eq("#ff0000", "#FF0000FF")` is false, and so is `eq("#ff0000", "#FF0000")`.
- When one operand is a colour expression (`rgba`, `alpha`, a colour `lerp`, or a value, cell, `if` or `steps` with one among its outputs), the other is read as a colour and they compare **as colours**: equal when their 8-digit values are, so `#ff0000` equals `#FF0000FF`. To compare two colour literals as colours, make one a colour expression: `eq(alpha("#ff0000", 1), "#FF0000FF")` is true.

## Colour channels

A colour holds four channels of 8 bits (`#RRGGBBAA`). Every operation that makes a colour from numbers rounds each channel **half away from zero** to an integer 0–255:

- `rgba(r, g, b, a)`: r, g and b are clamped to 0–255 and rounded; a is clamped to 0–1, multiplied by 255 and rounded. So `rgba(127.5, 0.5, 254.5, 0.5)` is `#8001ff80`.
- `alpha(c, a)`: c's red, green and blue, with a clamped to 0–1, multiplied by 255 and rounded.
- `lerp(a, b, t)` of colours: t clamped to 0–1, then each channel (alpha too, unpremultiplied) is a + (b − a)·t, rounded. So `lerp("#000000", "#ffffff", 0.5)` is `#808080ff` (127.5 rounds to 128).

## Degrees

`sin`, `cos` and `atan2` work in degrees and are exact where the result is 0, 1 or −1:

- `sin(x)`: first r = x − 360·floor(x ÷ 360), computed in doubles. When r is 0 or 180 the result is 0; 90 gives 1; 270 gives −1; otherwise it is the sine of r·(π ÷ 180) in radians. So `sin(180)`, `sin(-180)` and `sin(720)` are exactly 0, and `eq(sin(180), 0)` is true.
- `cos(x)` is `sin(x + 90)`, with x + 90 computed in doubles: `cos(90)` is 0 and `cos(180)` is −1.
- `atan2(y, x)` is the angle in radians, from −π to π, multiplied by 180 ÷ π (that quotient computed once, as a double). On the axes it is exact: 0 for y = 0 with x > 0 and for (0, 0), 90 for x = 0 with y > 0, −90 for x = 0 with y < 0, and 180 for y = 0 with x < 0, never −180, since no zero is negative.

Other values are the platform's sine and arctangent, which may differ in the last bits between platforms: `sin(30)` may be 0.49999999999999994, so compare such results with `lt` and `gt`, not `eq`.

## Number formatting

`format` computes its text in four steps:

1. `decimals` is rounded half away from zero to an integer and clamped to 0–6 (absent: 0); `value` that isn't finite is 0.
2. When |value| < 10¹⁶, it is multiplied by 10^decimals in doubles, that product is rounded half away from zero to an integer, and the integer is divided by 10^decimals in doubles; larger values are already integers and are kept. The product is the double nearest the true product, so the halfway cases are those of the double: `format(2.675, 2)` is "2.68" (2.675 × 100 is 267.5 in doubles), `format(8.345, 2)` is "8.35", while `format(1.005, 2)` is "1.00" (100.49999999999999) and `format(0.285, 2)` is "0.28".
3. The result is written in plain decimal notation, never with an exponent, with exactly `decimals` digits after the point: the double's exact decimal value, rounded to `decimals` places (half to even). Whenever the integer of step 2 is below 2⁵³ in size, that is its digits with the point placed `decimals` digits from the end. A minus sign comes first unless every digit is 0: `format(-0.4)` is "0" and `format(-0.004, 2)` is "0.00", never "-0". `format(1e21, 2)` is "1000000000000000000000.00".
4. With `group`, `groupSeparator` goes between groups of three integer digits, counted from the point, when there are more than three (`format(999.5)` with `group` is "1,000"); `decimalSeparator` goes between the integer and fraction digits.

## Case mapping

`upper` and `lower` map each code point on its own by Unicode's simple case mapping (the one-to-one uppercase and lowercase mappings of `UnicodeData.txt`), with no language, context or special-casing rules, so the length in code points never changes:

- A code point without a one-to-one mapping is kept: `upper("straße")` is "STRAßE" and `upper("ﬁ")` is "ﬁ" (no "SS" or "FI").
- Σ always lowercases to σ (no final-sigma rule); `upper("ſ")` is "S"; the Kelvin sign (U+212A) lowercases to "k"; titlecase ǅ uppercases to Ǆ and lowercases to ǆ; supplementary letters map too (𐐨 to 𐐀).
- Two exceptions: dotless ı (U+0131) doesn't uppercase and dotted İ (U+0130) doesn't lowercase. So `upper("ıi")` is "ıI" and `lower("İI")` is "İi".

The mappings are those of the Unicode version the engine's platform provides; a code point that version doesn't map is kept.

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

## Exact results

Each expression here gives exactly the result shown (numbers as doubles, colours as `#rrggbbaa`); `examples/expressions/vectors.json` has these and more, and an engine's tests evaluate every one.

| Expression | Result |
|---|---|
| `{"op": "eq", "args": ["#ff0000", "#FF0000FF"]}` | `false` |
| `{"op": "eq", "args": [{"op": "alpha", "args": ["#ff0000", 1]}, "#FF0000FF"]}` | `true` |
| `{"op": "eq", "args": [{"op": "rgba", "args": [255, 0, 0, 1]}, "#FF0000FF"]}` | `true` |
| `{"op": "eq", "args": [{"op": "add", "args": [0.1, 0.2]}, 0.3]}` | `false` |
| `{"op": "atan2", "args": [{"op": "mul", "args": [-1, 0]}, -1]}` | `180` |
| `{"op": "eq", "args": [{"op": "sin", "args": [180]}, 0]}` | `true` |
| `{"op": "cos", "args": [180]}` | `-1` |
| `{"op": "rgba", "args": [127.5, 0.5, 254.5, 0.5]}` | `"#8001ff80"` |
| `{"op": "lerp", "args": ["#000000", "#ffffff", 0.5]}` | `"#808080ff"` |
| `{"op": "format", "value": 2.675, "decimals": 2}` | `"2.68"` |
| `{"op": "format", "value": 1.005, "decimals": 2}` | `"1.00"` |
| `{"op": "format", "value": -0.4}` | `"0"` |
| `{"op": "format", "value": 1e21, "decimals": 2}` | `"1000000000000000000000.00"` |
| `{"op": "format", "value": 999.5, "group": true}` | `"1,000"` |
| `{"op": "upper", "args": ["straße ıi"]}` | `"STRAßE ıI"` |
| `{"op": "steps", "value": 1, "at": [1], "out": [false, true]}` | `true` |

## Cost

Each operation object counts 1 towards a scene's expression work, except the string-producing operations `concat`, `upper`, `lower`, `format` and `formatTime`, which count 16 each (each may copy up to 4,096 scalar values), and `history`, which counts 1 more per 64 samples (rounded up); values and state cells count once, however often they are read. A scene has at most 20,000 units of work and expressions nested at most 32 deep ([limits](limits.md)).
