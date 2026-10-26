# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

Branch `2.0.x` is InfoPanel 2.0: a cross-platform .NET engine (Windows + macOS) with a web editor. 1.x ships from `1.4.x`. Decisions for 2.0 are recorded in `docs/2.0/decisions.md`.

- **SDK:** .NET 10, pinned by `global.json` (`10.0.403`, `rollForward: latestFeature`).
- **Solution:** `InfoPanel.slnx`, Debug/Release, Any CPU. No platform or runtime identifier is forced; builds run on macOS (arm64) and Windows.
- **Package versions** live in `Directory.Packages.props` (central package management). A `PackageReference` never carries a `Version`. There are no `packages.lock.json` files.
- **Plugin SDK projects** (`InfoPanel.Plugins*`) target net8.0 so existing plugins keep loading. `InfoPanel.Extras` targets net8.0-windows and builds anywhere via `EnableWindowsTargeting`. Core and tests target net10.0.
- The 1.x WPF app is gone from this branch; `PORTING.md` lists what it did and where each part goes in 2.0.

```bash
dotnet build InfoPanel.slnx            # -c Release for Release
dotnet test InfoPanel.slnx             # xUnit v3, run through VSTest
dotnet test InfoPanel.slnx --filter FullyQualifiedName~SensorKey

# Run the engine as the product does (launch profile "InfoPanel": default data folder, opens the browser)
dotnet run --project InfoPanel
# Dev loop (launch profile "dev": --port 5199 --dev-origin http://localhost:5180 --no-browser --data-dir ../.data)
dotnet run --project InfoPanel --launch-profile dev
# Run it on its own data folder, port and token
dotnet run --project InfoPanel -- --data-dir /tmp/ip --port 5299 --token t3st --no-browser
dotnet run --project InfoPanel -- --help

# Run plugin simulator for testing plugins
dotnet run --project InfoPanel.Plugins.Simulator/InfoPanel.Plugins.Simulator.csproj
```

Engine options (flags, or `Engine:*` configuration): `--port` (0 = a free port), `--data-dir` (default `{LocalApplicationData}/InfoPanel/v2`), `--web-root` (default `wwwroot` next to the binary, else the editor's `dist` folder in a source checkout), `--no-browser`, `--token` (default random), `--dev-origin <url>` (repeatable), `--import-v1 <dir>` (import a 1.x data folder at start-up), `--fresh` (wipe the data folder first; needs `--data-dir`; refuses, by the folder's real path, a root, the home folder or a folder above it, and any folder holding something the engine didn't create), `--mock-seed`. Kestrel/URL settings (`--Kestrel:*`, `--urls`, …) are refused, and Kestrel endpoints from configuration or the environment are ignored. Every option, the `--import-v1` overlap rule included, is validated before anything is deleted. Unknown or invalid arguments exit with code 2; a second engine on the same data folder exits with code 3. The engine prints its sign-in link (`http://127.0.0.1:{port}/auth/{token}`, plus one per dev origin) to the console only (the log file gets it with the token redacted), creates the data folder and `logs/` owner-only (0700) on Unix, and writes `{data}/runtime.json` (`{pid, port, url}`).

**Dev loop:** run the engine with the `dev` profile (`--launch-profile dev`: port 5199, data in `{repo}/.data`, gitignored) with a client's dev server on `http://localhost:5180` (the allowed dev origin), then open the logged `http://localhost:5180/auth/{token}` link. Without one, the engine serves a built client from its web root itself.

## Architecture (2.0.x)

2.0 is one headless .NET engine that loads profiles, reads sensors, renders frames with SkiaSharp and serves the editor to the user's browser on localhost. The preview the editor shows is rendered by the engine with the same code that drives panels; a client draws only its own overlays, from the item bounds. Today the engine serves read-only endpoints and thumbnails (M4); editing (M5), the live preview WebSocket (M6) and the editor on the engine (M7) follow.

| Project | Role |
|---|---|
| **InfoPanel** (net10.0, `Microsoft.NET.Sdk.Web`) | The engine host and product binary: ASP.NET Core on loopback, REST API, static editor files, hosting glue |
| **InfoPanel.Tests** | xUnit v3 tests for the host (`WebApplicationFactory<Program>`) |
| **InfoPanel.Core** (net10.0) | The 1.x model (14 display item kinds + `Profile`) and renderer, moved from the WPF app with history, behind cross-platform seams; mock sensors, sensor hub and history; the read side of editing |
| **InfoPanel.Core.Tests** | xUnit v3 tests for Core |
| **InfoPanel.Plugins**, **.Plugins.Graphics**, **.Plugins.Ipc**, **.Plugins.Host**, **.Plugins.Loader**, **.Plugins.Simulator** (net8.0) | Plugin SDK, out-of-process plugin host and simulator (see `PLUGINS.md`, `PLUGIN-ARCHITECTURE.md`) |
| **InfoPanel.Extras** | Built-in plugins (Windows-only at runtime) |

### InfoPanel.Core

Root namespace `InfoPanel`. Files moved from 1.x keep their 1.x namespaces (`InfoPanel.Models`, `InfoPanel.Drawing`, ...).

- `Models/` — `DisplayItem` and the 14 kinds (Text, Sensor, Clock, Calendar, Table, Image, SensorImage, HttpImage, Gauge, Graph, Bar, Donut, Shape, Group), `Profile`, `SensorReading`. These are the 1.x XML types.
- `Sensors/` — `SensorKey` (`hwinfo:{remote}:{id}:{instance}:{entry}`, `libre:{id}`, `plugin:{id}`), `ISensorReader`, `ISensorSource`, `ISensorHistory`, `SensorKeys` (maps an item's sensor binding to a key).
- `Images/` — `IImageSource` (non-blocking; null means "draw the placeholder"), `IRenderImage`, `IAssetLocator`, `IMediaPlayback`; `ImageCache` (local raster and SVG files; null for HTTP, RTSP, video and `plugin-image://` until providers exist) and `LockedImage`.
- `Drawing/` — `RenderContext` (sensors, history, images, assets, `ItemStateStore`, `Tick`, `Now`, `Elapsed`, `RenderOptions`), `PanelDraw.Run(profile, items, graphics, ctx)`, `GraphDraw`, `SkiaGraphics` (all three internal), `FontDefaults`.
- `Sensors/` also holds the engine's sensor runtime: `MockSensorSource` + `MockSensorCatalog` (mock sensors, keys `plugin:/infopanel.mock/{group}/{id}`, plus a process table and a text sensor), `SensorHub` (the engine's `ISensorReader`: sources in order, unknown well-formed keys simulated, status `live | simulated | missing`) and `SensorHistory` (samples requested keys every second, ≤4096, drops keys idle for 10 min).
- `Editing/` — `ItemKinds` (the 14 kind ids and the `/api/kinds` manifest), `PropertyMap` (which properties the API exposes, camelCase), `ItemJson` (`ItemNode`, profile props), `ProfileDocument` (one open profile, `SyncRoot`, `Revision`) and `Workspace` (profile list, lazily opened documents); M5 adds editing. `UndoHistory.cs` is the 1.x undo manager, unchanged; rewritten per document in M5.
- `Drawing/ThumbnailRenderer.cs` — PNG thumbnails through `ProfileRenderer`; `ProfileRenderStates` keeps one render state per profile.
- `Persistence/` — `DataPaths`, `ProfileStore` (1.x XML layout), `.infopanel` archives and the 1.x data import.

### InfoPanel (engine host)

`Program.cs` parses the command line (`Hosting/EngineCommandLine`, `EngineOptions`), takes `{data}/engine.lock` (`InstanceLock`), sets up Serilog (console + `{data}/logs/infopanel-*.log`, 7 days; `LogTargets` routes an event to one sink), runs `--import-v1`, creates a starter profile in an empty folder, registers Core services as singletons and builds the pipeline. `Hosting/` has `EngineRuntime` (port, token, allowed origins), `EngineLifetime` (confirms the port, writes/deletes `runtime.json`, logs the sign-in links, opens the browser), `SensorService` (starts sources, ticks the hub and history), `StaticSite` and `DataFolder`. `Api/ReadEndpoints.cs` maps `/api/session`, `/kinds`, `/fonts`, `/sensors`, `/sensors/values`, `/profiles`, `/profiles/{id}`, `/profiles/{id}/thumbnail` (camelCase JSON, enums as strings, errors as `application/problem+json`).

**Security model (local-only):** Kestrel listens on 127.0.0.1 only; host filtering accepts only `localhost`, `127.0.0.1` and `[::1]`; any request with an `Origin` header must come from `http://127.0.0.1:{port}`, `http://localhost:{port}` or a `--dev-origin` (403 otherwise, WebSocket upgrades included); `/api/*` needs `Authorization: Bearer {token}` and `/ws` needs the WebSocket subprotocols `infopanel.v1` + `auth.{token}` (401 otherwise, before any upgrade; M6's `/ws` answers with `infopanel.v1` only, never echoing the token). There is **no cookie**: browsers send loopback cookies to every port, so any other local server would get it. `GET /auth/{token}` redirects to `/#token={token}` (`Cache-Control: no-store`); a browser client reads the fragment, keeps the token in `localStorage` (per origin, so per port), removes it from the URL and sends it as a Bearer header, for images and thumbnails too. `/healthz`, `/auth/{token}` and the editor's files are public. No CORS. Every response carries the CSP, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.

Rendering reads nothing global: time, sensor values, history, images and per-item animation state (gauge frames, chart smoothing) all come from the `RenderContext`, so the same inputs give the same pixels. Animation state advances at most once per `Tick`. Selection, grid lines and other editor chrome are never drawn by Core.

## Invariants

- **The engine owns the model.** The editor sends patches only (property sets, structural operations) and never round-trips whole profiles or items through its own model. Undo/redo and dirty state live in the engine.
- **1.x XML compatibility.** Profiles are `profiles.xml` + `profiles/{guid}.xml`, written with `XmlSerializer`, and 1.x must keep reading what 2.0 writes:
  - Model freeze: never add, remove or rename a public read/write property or public field on `Profile` or any display item type, and never change the type of one that serializes. `[ObservableProperty]` fields count (it does not copy `[XmlIgnore]`, so those serialize). Members that do not serialize (get-only, `[XmlIgnore]`) may change.
  - `HttpImageDisplayItem.HttpUrl` is kept but always serializes as null: the one whitelisted difference from 1.x output.
  - `DisplayItem.Guid` is still `[XmlIgnore]`; M2 serializes it as a `<Guid>` element (1.x ignores unknown elements). That is the one planned model change.
- **Goldens and snapshots** (from M3 and M5): rendering changes are guarded by golden images and the kind manifest by a snapshot. Regenerate only for an intended change, and review the diff:
  - `UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden` (goldens are per OS in `InfoPanel.Core.Tests/Golden/{osx,win,linux}/`; only missing or out-of-tolerance files are rewritten; failures leave `*.actual.png`/`*.diff.png` in `InfoPanel.Core.Tests/TestResults/golden/`). Text in goldens uses the bundled Inter only.
  - The images they draw are generated: `GENERATE_FIXTURE_ASSETS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~FixtureAssets` rewrites `InfoPanel.Core.Tests/Fixtures/assets/`.
  - `UPDATE_SNAPSHOTS=1 dotnet test --filter KindManifest`
- **1.x code** is read from the `1.4.x` branch with `git show 1.4.x:<path>` (no worktrees). Port from there, using `PORTING.md` as the backlog.
- **No CI on 2.0.x** for now; `.github/workflows/dotnet-desktop.yml` is the 1.x workflow and is left untouched.

## Related Documentation

- `docs/2.0/decisions.md` — 2.0 decisions (date, decision, reason)
- `PORTING.md` — 1.x components still to port, with their 1.4.x sources
- `PLUGINS.md` — Plugin development guide (for plugin authors)
- `PLUGIN-ARCHITECTURE.md` — Plugin system internals (for contributors; describes the 1.x host side)
- `PANELS.md` — Hardware panel support and models (1.x)
- `LoggingGuidelines.md` — Serilog usage standards and log levels
