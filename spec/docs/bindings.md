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
| `table` | A table sensor: rows and named columns of text cells, and a summary text | `table`; `input` field `text` |

Every input also has a `status` and `available` ([expressions](expressions.md)). A scene declares at most 32 inputs, each named like other scene names (`^[a-z][A-Za-z0-9_]{0,31}$`), with an optional `label` (up to 64 characters) for a client to show next to its binding control.

## Bindings

`bindings` maps input names to `{ "key", "stat" }`:

- `key` is a sensor key as the engine lists them (`GET /api/sensors`, [API](api.md#fonts-and-sensors)), up to 512 characters. Keys are opaque to the scene format; an engine documents its own (this engine's look like `infopanel:/mock/cpu/cpu-temp`; its four forms are in [API](api.md#fonts-and-sensors)). This engine refuses a key that names one of its own sources in the plugin form, `plugin:/infopanel.{source}/…` with `infopanel.` in any case (its own sources' keys are `infopanel:/{source}/…`, and no plugin may take an id starting `infopanel.`): a document's (created, imported in an archive, or stored) or a commit's binding with one is invalid, `key.pluginForm` at the binding's `key` ([validation](validation.md#codes)).
- `stat` (`now`, `min`, `max`, `avg`; default `now`) is the statistic an input's `value` reads. `now`, `min`, `max` and `avg` can always be read explicitly too.

A binding whose name the scene doesn't declare is kept and ignored, so an item can switch between scenes that share some inputs without losing the others. A declared input with no binding is **unbound**.

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

## History

A `number` input's history is the sensor's samples, oldest first, taken every `graphRate` milliseconds, an engine setting (one a second by default, [API](api.md#settings)), at most 4,096 of them. A change of `graphRate` keeps the samples taken before it. The engine keeps history for the keys that are read; the first read of a key may start with fewer samples. `history` aggregates the last `count` samples and `series` draws them. History is part of the tick snapshot, like every other input ([identity](identity.md)).

## Time is not a binding

The clock (`time`, `formatTime`) and the item's layout box (`box`) are always available and need no binding.
