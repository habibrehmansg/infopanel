# Weather

The weather source, `infopanel.weather` ("Weather"), reads the current weather from the configured service ([the configured service](../../spec/docs/api.md#the-configured-service)), on every operating system, outside mock mode and a replay. It never calls a weather provider itself: it asks the configured service, with the credential a client provides, and shows what the service answers. Code: `InfoPanel.Core/Sensors/Weather/` (`WeatherSource`, `WeatherFetcher`, `WeatherSchedule`, `WeatherAnswer`), registered by `AddBuiltInSources`.

## Readings

Every reading is listed from the start, under `plugin:/infopanel.weather/{id}`, group "Weather", and is `unavailable` until the first answer:

| Id | Name | Kind | Metric | Imperial | From the answer |
|---|---|---|---|---|---|
| `place` | Place | text | | | `place.name`; for a place given as coordinates that the service names none for, the place as set |
| `condition` | Condition | text | | | `current.condition` ("Light rain") |
| `symbol` | Symbol | text | | | `current.symbol` (`lightrain`) |
| `temperature` | Temperature | number | °C | °F | `current.temperature` |
| `feels_like` | Feels like | number | °C | °F | `current.feelsLike` |
| `high` | High (next 24 h) | number | °C | °F | `next24h.high` |
| `low` | Low (next 24 h) | number | °C | °F | `next24h.low` |
| `dew_point` | Dew point | number | °C | °F | `current.dewPoint` |
| `humidity` | Humidity | number | % | % | `current.humidity` |
| `cloud_cover` | Cloud cover | number | % | % | `current.cloudCover` |
| `pressure` | Pressure (sea level) | number | hPa | inHg | `current.pressure` |
| `wind_speed` | Wind speed | number | m/s | mph | `current.windSpeed` |
| `wind_gust` | Wind gust | number | m/s | mph | `current.windGust` |
| `wind_direction` | Wind direction (from) | number | ° | ° | `current.windDirection` |
| `precipitation` | Precipitation (next hour) | number | mm | in | `current.precipitation` |
| `uv_index` | UV index (clear sky) | number | | | `current.uvIndex` |
| `sunrise` | Sunrise | text | | | `sun.sunrise`, as this PC shows a time of day: in its time zone, in its regional format's short time ("06:58") |
| `sunset` | Sunset | text | | | `sun.sunset`, the same |
| `attribution` | Credit | text | | | `attribution.text`: the credit the service asks to show with its data |

A value the answer gives as `null` (or not as a number or a string) reads `unavailable`; the rest of the answer still reads. Units follow the setting `sources.weather.units` ([settings](../../spec/docs/api.md#settings)): `auto` (the default) is metric unless the PC's regional format is a region's that uses imperial units. Conversions: °F = °C × 9/5 + 32; mph = m/s × 2.236936; inHg = hPa × 0.0295300; in = mm ÷ 25.4. A change of units reads the answer held in the new units at once (no request), stale values too, still stale; the readings whose unit changes start afresh, so their minimum, maximum and average are never mixed across units.

**Freshness.** The readings are polled every second from the answer the source holds (a poll never waits on the network): `live` while the answer is at most 90 minutes old and the service didn't call it `stale`; then `stale`, the last values kept, until the answer is 6 hours old; then `unavailable`. The age is the values': an answer the service calls `stale` (it couldn't refresh it) never renews it. With values shown, a stale answer keeps them, and they read `stale` at once, still aged from the answer they came in; with none yet (for the place set, since the source was turned on), its values are shown, `stale`, aged from their arrival.

## Requests

`GET {service.address}/svc/v1/weather`, with `?place=` and the place, escaped, when `sources.weather.place` sets one (none: the service finds the place from the request's network address). Through the rules every request to the configured service follows: only to the address's origin, the credential as `Authorization: Bearer`, no redirect followed (a `3xx` is a failure, and its `Location` never gets the credential), no cookies, no proxy; no credential, no request; under `--offline`, only a service on this computer. Each request is bounded at 15 seconds; an answer over 64 KiB, or nested deeper than 16 levels, is refused unread past it.

**When.** At once when the source starts, and when the place, the credential or the service address changes, or the source is turned on; after an answer, when it `expires`, plus a random 0–120 seconds so engines don't all ask at once, never sooner than 10 minutes nor later than 60 after it; after a failure, after 1, 2, 4, 8 and 16 minutes, then every 30 minutes until one answers (a `Retry-After` longer than the step is waited instead); after a 429, its `Retry-After`, at least 10 minutes; a `Retry-After` is the service's least wait, honoured up to 24 hours (a longer one is taken as 24 hours), and the 10 to 60 minutes bound only the wait after an answer; after a 401, never, until a client provides another credential. Turned off, the source cancels a request under way, forgets its answer and asks nothing. A new place forgets the answer for the place before.

The source's state and its reasons are in [the weather source's reasons](../../spec/docs/api.md#the-weather-sources-reasons). The credential and the place are never logged.

## The answer the engine reads

A `200` with a JSON body (`application/json`), metric always:

```json
{
  "format": "infopanel.weather/1",
  "place": { "name": "Oslo", "country": "NO", "from": "network" },
  "time": "2026-11-01T13:00:00Z",
  "updated": "2026-11-01T12:40:00Z",
  "expires": "2026-11-01T13:35:00Z",
  "stale": false,
  "current": {
    "temperature": 6.1, "feelsLike": 3.2, "humidity": 81, "dewPoint": 3.0, "pressure": 1012.4,
    "windSpeed": 3.4, "windGust": 7.9, "windDirection": 210, "cloudCover": 88, "uvIndex": 0.4,
    "precipitation": 0.3, "symbol": "lightrain", "condition": "Light rain"
  },
  "next24h": { "high": 8.2, "low": 2.9 },
  "sun": { "sunrise": "2026-11-01T06:58:00Z", "sunset": "2026-11-01T15:31:00Z" },
  "attribution": { "text": "Weather data from …", "url": "https://…", "licence": "…", "licenceUrl": "https://…", "changes": "…" }
}
```

The engine needs `format` exactly `infopanel.weather/1`, `expires` (an ISO 8601 time with its offset), `stale` (a boolean), and `place` and `current` (objects); an answer without one of them, or that isn't JSON, is a failure (`serviceUnavailable`). It reads the members the table above names and ignores the rest (`time`, `updated`, `place.country`, `place.from` but for the place as set, and `attribution` but for `text`). Any measure may be `null`, and so may `sun.sunrise` and `sun.sunset` (polar day or night). Units: °C, %, hPa at sea level, m/s at 10 m, degrees the wind comes from, mm in the next hour.

Other answers (`application/problem+json`, with `code`): `401` (the credential refused: the engine marks it rejected and sends nothing until a client provides another), `429` (`Retry-After`), `400` (the place can't be read: `placeNotFound`), `422` with `placeNotFound` or `locationUnknown`, `503` (`Retry-After`) and any other status: `serviceUnavailable`.

## 1.x

The Weather plugin (`weather-plugin`, OpenWeatherMap with a key and a city each user typed) is no longer bundled with the engine, and its id is refused from any package ([plugins](plugins.md#sensors)). The engine never answers its keys; a 1.x import binds 15 of its 19 entries to this source's, once ([import](../../spec/docs/import.md#plugins-the-engine-reads-itself)): its snow, ground-level pressure, icon and icon address have no equivalent and stay as they were, unavailable.
