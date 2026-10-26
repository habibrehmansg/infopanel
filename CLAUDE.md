# CLAUDE.md

Guidance for Claude Code (claude.ai/code) in this repository, for branch **`2.0.x`**: InfoPanel 2.0, one cross-platform .NET engine (Windows and macOS) that renders sensor dashboards with SkiaSharp and serves its editor to the browser on localhost. 1.x (the Windows-only WPF app) ships from branch `1.4.x`; its code is not on this branch (read it with `git show 1.4.x:<path>`; `PORTING.md` lists what still has to come over).

Every decision made while building 2.0 is in `docs/2.0/decisions.md`, newest last. It overrides older plans and docs; add an entry (date, decision, reason) for any new one.

## Branch rules

- Commit directly to `2.0.x`; no PRs until 2.0 is release-ready. No worktrees.
- No CI on this branch: `.github/workflows/dotnet-desktop.yml` is 1.x's and doesn't trigger on `2.0.x`. Don't touch `.github/`. Run the checks below yourself.

## Projects (`InfoPanel.slnx`, Debug/Release, Any CPU)

| Project | TFM | Role |
|---|---|---|
| `InfoPanel.Core` | net10.0 | Models (the 14 1.x item kinds, `Profile`), renderer (`Drawing/`), sensors (`Sensors/`: `SensorHub`, mock source, history), persistence (`Persistence/`: 1.x XML, `.infopanel` archives, 1.x folder import), editing (`Editing/`: `Workspace`, `ProfileDocument`, undo, kind manifest), output (`Output/`: `RenderService` render loops, `IFrameSink`) |
| `InfoPanel` | net10.0, `Microsoft.NET.Sdk.Web` | The engine executable: hosting and command line (`Hosting/`), REST API (`Api/`), `/ws` preview (`Preview/`), security (`Security/`), serves the editor |
| `InfoPanel.Core.Tests`, `InfoPanel.Tests` | net10.0 | xUnit v3 (VSTest). Core: models, persistence, goldens, render loop. Host: API, security, `/ws`, preview parity |
| `InfoPanel.Plugins*`, `InfoPanel.Extras` | net8.0 | 1.x plugin SDK, host, loader and simulator, kept on net8.0 so existing plugins keep working. The engine doesn't load plugins yet. `InfoPanel.Extras` is net8.0-windows (builds on macOS with `EnableWindowsTargeting`) |

Package versions are central (`Directory.Packages.props`); `global.json` pins SDK 10.0.403 (`latestFeature`). Ask before adding any NuGet dependency.

## Commands

From the repo root:

```bash
dotnet build InfoPanel.slnx
dotnet test InfoPanel.slnx
dotnet test InfoPanel.slnx --filter Category=Golden            # renderer goldens only
dotnet test InfoPanel.slnx --filter "FullyQualifiedName~Preview"

# Engine for development: port 5199, data in ./.data, the editor's dev origin http://localhost:5180 allowed
dotnet run --project InfoPanel --launch-profile dev
# Engine as users run it: default data folder, opens the browser (serves the API; with `--web-root <built client>`, that client too)
dotnet run --project InfoPanel -c Release
dotnet run --project InfoPanel -- --help                       # all flags
```

Engine flags: `--port`, `--data-dir`, `--web-root`, `--token`, `--dev-origin` (repeatable), `--import-v1 <1.x folder>`, `--fresh`, `--mock-seed`, `--no-browser`. `--fresh` is a dev and test flag that wipes the whole data folder before start-up: it needs `--data-dir`; it refuses a filesystem root, the home folder or a folder above it (checked on the path as given and on its real path, links resolved) and a folder with any top-level entry the engine doesn't write (`profiles.xml`, `profiles/`, `assets/`, `fonts/`, `logs/`, `runtime.json`, `engine.lock`, their write temp files); otherwise it deletes everything in the folder except `engine.lock`, including whatever is inside those engine folders (user-added fonts too), removing links without following them. Exit codes: 0 clean, 1 fatal, 2 bad arguments, 3 data folder in use. The sign-in link (`/auth/<token>`) goes to the console only.

The step-1 check that everything is green, from the repo root: `dotnet build InfoPanel.slnx && dotnet test InfoPanel.slnx`.

### Regenerating committed outputs (only on purpose; review the diff)

| What | Command | When |
|---|---|---|
| Renderer goldens `InfoPanel.Core.Tests/Golden/{osx,win,linux}/*.png` | `UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden` | Rendering changed on purpose; look at every rewritten PNG. Only out-of-tolerance or missing goldens are rewritten. macOS is the reference OS |
| Kind manifest snapshot | `UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter KindManifest` | Models or `PropertyMap` changed |
| E2E archive `all-kinds.infopanel` | `UPDATE_FIXTURES=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~E2eArchiveFixture` | The Core fixtures or their assets changed |
| Fixture images `InfoPanel.Core.Tests/Fixtures/assets/` | `GENERATE_FIXTURE_ASSETS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~FixtureAssets` | Rarely; regenerate goldens and the e2e archive after |
| Frozen 1.x XML `InfoPanel.Core.Tests/Fixtures/v1/` | `GENERATE_FIXTURES=1 …--filter FixtureGenerator` | Don't: they are 1.x's format, generated before 2.0 changed anything. Regenerating after a model change defeats them |

## Architecture

- **One engine, browser UI.** The engine runs headless and serves the editor on `127.0.0.1:{port}`. The editor only edits through the API and only shows engine frames; it has no model or renderer of its own.
- **REST** (`/api/...`): session, kinds manifest, fonts, sensors and values, profiles (CRUD, duplicate, save, revert, undo, redo, thumbnail, export, import), items (add, batch patch, delete, move, duplicate). Item properties travel under `props` with the C# names in camelCase, enums as strings. Errors are `application/problem+json`.
- **`/ws`**: the client subscribes to a profile at a scale and fps; the server sends `hello`, `doc` events (revision, dirty, canUndo, canRedo after every change, from any tab), `profiles` events, and binary frames (`IPF1`, a JSON header with revision and item bounds, then a PNG). A frame is sent only when pixels, bounds or revision changed.
- **Rendering**: `ProfileRenderer` (Core) draws a profile at `scale` through the canvas matrix. `RenderService` runs one loop per subscribed profile, renders each requested scale once per tick and hands the same `RenderedFrame` to every `IFrameSink` (the web preview now; panels and overlays later). A thumbnail (`RenderService.RenderOnce`, `GET .../thumbnail`) of a profile with a live loop redraws the loop's current tick with the loop's options (smoothing included) without advancing it, so at the profile's width it equals the live frame and leaves the loop's animation alone; with no loop it advances the tick and draws values at their target (`Smoothing = false`).
- **Editing**: `Workspace` holds the profile list and open `ProfileDocument`s. Every edit runs under the document's `SyncRoot` as one transaction, bumps the revision only when the XML snapshot changed, and records undo (per-document XML snapshots, coalescing keys: `gesture:` always merges, other keys within 1 s). Dirty compares with the saved snapshot.
- **Sensors**: `SensorHub` over `ISensorSource`s (only `MockSensorSource` in step 1); unknown well-formed keys get a deterministic simulated walk so imported Windows profiles still move. Values and history are frozen per render tick.

## Invariants (break one only with a decision entry)

- **Preview = panel.** The editor never renders; frames come from `ProfileRenderer`, and `PreviewParityTests` (host) holds the WS frame to the panel path pixel for pixel.
- **Model freeze.** Never add, remove or rename a public read/write property or public field on the models (`InfoPanel.Core/Models`); `DisplayItem.Guid` (`<Guid>`) was the one exception. The XML must stay readable by 1.x; `HttpImage.HttpUrl` serializing as null is the only whitelisted difference.
- **Rendering never writes to the model**, and render time, sensors and history come only from `RenderContext` (per-tick memo), so goldens and parity stay deterministic. Per-item animation state lives in `ItemStateStore`.
- **1.x files load or fail loudly**: unknown item types (newer 1.4.x builds) and `xsi:nil` items are dropped with a warning; unreadable XML throws and is never overwritten.
- **Undo, redo, dirty and save belong to the engine**; don't reintroduce client-side history.
- **The kind manifest snapshot moves with the models** (`KindManifestTests`).
- Plugin SDK projects stay on net8.0.

## Security model

- Kestrel binds 127.0.0.1 only and ignores configured URLs and endpoints. Host filtering allows `localhost`, `127.0.0.1` and `[::1]`; a request that carries an `Origin` (WebSocket upgrades included) must come from the engine's own origin or a `--dev-origin`.
- `/api` needs `Authorization: Bearer {token}`; `/ws` needs the subprotocols `infopanel.v1` and `auth.{token}` (browsers can't set WebSocket headers). Everything else is public: the editor's static files, `/healthz` and `/auth/{token}`. No cookies (a `localhost` cookie reaches every local server) and no CORS.
- `GET /auth/{token}` redirects to `/#token={token}`; a browser client keeps the token in `localStorage` (per origin, so per port) and out of the address bar. The token is random per start unless `--token` is given.
- CSP, `X-Frame-Options: DENY`, `nosniff` and `Referrer-Policy: no-referrer` on every response; imports guard against zip-slip, entry-count and size bombs, and oversized profiles.

## Data folder

`{LocalApplicationData}/InfoPanel/v2`: `~/Library/Application Support/InfoPanel/v2` on macOS, `%LOCALAPPDATA%\InfoPanel\v2` on Windows (`--data-dir` overrides). Same layout as 1.x plus the engine's files: `profiles.xml`, `profiles/{guid}.xml`, `assets/{guid}/`, `fonts/` (user fonts; Inter is bundled), `logs/infopanel-*.log` (7 days), `runtime.json` (pid, port and URL while running), `engine.lock`. 1.x's own data (`%LOCALAPPDATA%\InfoPanel`) is only read, by `--import-v1`.

## Conventions

- Logging: Serilog (see `LoggingGuidelines.md`).
- Tests pin culture and time; goldens and other process-wide font state run in the non-parallel `Golden` collection.
- Docs for 2.0 live in `docs/2.0/`; `PANELS.md`, `PLUGINS.md` and `PLUGIN-ARCHITECTURE.md` still describe 1.x.
