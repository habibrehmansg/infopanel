# Bindings

A scene says which **inputs** it reads; an item says which sensor feeds each one (its **bindings**). Changing a binding never touches the scene, so any client can point an item at another sensor.

```jsonc
// in the scene
"inputs": { "v": { "type": "number", "label": "Temperature" } }
// in the item
"bindings": { "v": { "key": "infopanel:/mock/cpu/cpu-temp", "stat": "now" } }
```

## Inputs

| `type` | Reads | Used by |
|---|---|---|
| `number` | A numeric sensor: its value (by the binding's statistic) and its now, min, max and average, its unit, its history | `input` fields `value`, `now`, `min`, `max`, `avg`, `unit`; `history`; `series` |
| `text` | A text sensor's text, or an image sensor's reference (the picture it names, [API](api.md#sensor-pictures)) | `input` field `text` |
| `table` | A table sensor: named columns and rows of cells, each with its text and its number ([below](#tables)), and a summary text | `table`; `cell`; `input` fields `rows`, `text`; `history` and `series` with a `column` ([samples](#samples)) |

Every input also has a `status` and `available` ([expressions](expressions.md)). A scene declares at most 64 inputs, each named like other scene names (`^[a-z][A-Za-z0-9_]{0,31}$`), with an optional `label` (up to 64 characters) for a client to show next to its binding control.

## Bindings

`bindings` maps input names to `{ "key", "stat" }`:

- `key` is a sensor key as the engine lists them (`GET /api/sensors`, [API](api.md#fonts-and-sensors)), up to 512 characters. Keys are opaque to the scene format; an engine documents its own (this engine's look like `infopanel:/mock/cpu/cpu-temp`; its four forms are in [API](api.md#fonts-and-sensors)). This engine refuses a key that names one of its own sources in the plugin form, `plugin:/infopanel.{source}/…` with `infopanel.` in any case (its own sources' keys are `infopanel:/{source}/…`, and no plugin may take an id starting `infopanel.`): a document's (created, imported in an archive, or stored) or a commit's binding with one is invalid, `key.pluginForm` at the binding's `key` ([validation](validation.md#codes)).
- `stat` (`now`, `min`, `max`, `avg`; default `now`) is the statistic an input's `value` reads. `now`, `min`, `max` and `avg` can always be read explicitly too.

`min`, `max` and `avg` are the sensor's statistics as its source keeps them. A source that keeps its own gives its own, over its own period. Otherwise they are the engine's: the least, greatest and mean of the finite readings it has taken since the statistics last started. They start with the sensor's first reading in a run, and start again only when the source says the sensor has no reading. A read that fails, a read the operating system refuses, and a reading that has only grown old do not start them again: when readings resume, the statistics go on. A scene that needs a stated period uses `history` (a number of samples) or a state cell (from the cell's first tick, [expressions](expressions.md#state-cells)).

A binding whose name the scene doesn't declare is kept and ignored, so an item can switch between scenes that share some inputs without losing the others; an item keeps at most 1,024 bindings ([limits](limits.md#document-limits)), and its scene reads only those its inputs name. A declared input with no binding is **unbound**.

## Status

| `status` | Meaning | `available` |
|---|---|---|
| `unbound` | No binding for the input | false |
| `live` | A current reading | true |
| `stale` | A reading, but older than the sensor's source normally updates | true |
| `unavailable` | No reading: unknown key, the source isn't running, or the sensor is of another type than the input | false |
| `denied` | The operating system refused the engine access to the sensor | false |

An input bound to a sensor reads the sensor's status in the sensor list ([API](api.md#fonts-and-sensors)) as follows: `live` and `simulated` as `live`, `stale` as `stale`, `denied` as `denied`, and `unavailable` and `missing` as `unavailable`. A sensor of another type than the input is `unavailable`, whatever its status; an `image` sensor is a `text` input's type (it reads the picture's reference with the sensor's status), and a `number` or `table` input's other type. A `stale` input keeps the sensor's last reading.

An input without a reading reads 0 (numbers), "" (text) and an empty table; a scene shows something else by testing `available` or `status`.

## Tables

A table input's reading for a tick is its column names and its first `tableRows` rows (4,096, [limits](limits.md#run-time-limits)), in the source's order, each cell with its text and, when the source gives the cell as a number, that number. `table`, `cell`, the `rows` field, and `series` and `history` with a `column` ([samples](#samples)) all read this one reading, so in one tick they never disagree, however the source's table changes between ticks ([identity](identity.md#the-tick-snapshot)). A source with more rows is read to its first `tableRows`; `rows` is at most that. The `table` node still draws at most 100 rows of it ([smart nodes](smart-nodes.md)).

**A cell's number** is the source's own number for that cell, its unit left out: a cell given as a number with a unit (a plugin's sensor cell, whose text reads `28.6%`, [API](api.md#fonts-and-sensors)) has the number 28.6 (the value the source holds, which may carry more digits than its text shows). A number that isn't finite is 0, as every reading is; a cell that isn't a number has the number 0. A cell's number is never read from its text: a text cell that reads `42` has the number 0 (`parse` reads a number from text, [expressions](expressions.md#reading-text-and-tables)).

## History

A `number` input's history is the sensor's samples, oldest first, taken every `graphRate` milliseconds, an engine setting (one a second by default, [API](api.md#settings)), at most 4,096 of them. A change of `graphRate` keeps the samples taken before it. The engine keeps history for the keys that are read; the first read of a key may start with fewer samples. `history` aggregates the last `count` samples and `series` draws them. A `count` is a number of samples, not a length of time: the time it spans follows `graphRate`; a table column's samples ([below](#samples)) have no rate at all. History is part of the tick snapshot, like every other input ([identity](identity.md)).

## Samples

`series` and `history` read a list of samples, oldest first and the newest last: a `number` input's history ([above](#history)), or, given a `column`, a `table` input's column. A column's samples are the numbers of that column's cells ([a cell's number](#tables)), one per row of the tick's reading, in row order, so the last row is the newest:

- Nothing is filtered: sample i is row i's cell (`cell` at row i with `field` `number`), a text cell's sample is 0, and `history` with `fn` `count` gives the smaller of `count` and the reading's rows.
- A column the reading lacks (an index past its last column, a name no column has), and any column of an unbound or unavailable input, has no samples: `series` draws nothing and `history` gives 0.
- The samples are the reading's rows, at most `tableRows`, read where the source's table holds them: they have no rate, and `graphRate` doesn't apply. A source that publishes samples as a table keeps them within `tableRows`, the newest last.
- Every number is finite (a cell's number that isn't is 0), so the samples sort and compare without exception.
- `column` names the column by index (0 first) or by name, as `cell` does. It goes with a table input, which then needs one; a number input takes none (`type.input` at the `input`).

`series` and `history` over one column in one tick read the one reading, so they see the same samples.

## Time is not a binding

The clock (`time`, `formatTime`) and the item's layout box (`box`) are always available and need no binding.
