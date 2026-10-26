# CLAUDE.md

Guidance for Claude Code (claude.ai/code) in this repository, for branch **`2.0.x`**: InfoPanel 2.0, one cross-platform .NET engine (Windows and macOS) that renders sensor dashboards with SkiaSharp and serves a REST API, a live preview socket and a browser editor's static files on localhost. No web client is built from this repository: the engine serves the built client that `--web-root` points at, or `wwwroot` next to the engine, else the API only. 1.x (the Windows-only WPF app) ships from branch `1.4.x`; its code is not on this branch (read it with `git show 1.4.x:<path>`; `PORTING.md` lists what still has to come over).

Every decision made while building 2.0 is in `docs/2.0/decisions.md`, newest last. It overrides older plans and docs; add an entry (date, decision, reason) for any new one.

## Branch rules

- Commit directly to `2.0.x`; no PRs until 2.0 is release-ready. No worktrees.
- No CI on this branch: `.github/workflows/dotnet-desktop.yml` is 1.x's and doesn't trigger on `2.0.x`. Don't touch `.github/`. Run the checks below yourself.

## Projects (`InfoPanel.slnx`, Debug/Release, Any CPU)

| Project | TFM | Role |
|---|---|---|
| `InfoPanel.Core` | net10.0 | Models (the 14 1.x item kinds, `Profile`), renderer (`Drawing/`), sensors (`Sensors/`: `SensorHub`, status, mock source, `PolledSensorSource`, history; `Sensors/MacOS/`: the macOS source through IOKit, CoreFoundation, libSystem and IOReport), persistence (`Persistence/`: 1.x XML, `.infopanel` archives, 1.x folder import), editing (`Editing/`: `Workspace`, `ProfileDocument`, undo, kind manifest), output (`Output/`: `RenderService` render loops, `IFrameSink`), the formats (`Spec/`: the embedded `spec/schemas`, a validator for their JSON Schema subset, the data-only model, the rule and limit validators, `MediaProbe`; nothing renders it yet) |
| `InfoPanel` | net10.0, `Microsoft.NET.Sdk.Web` | The engine executable: hosting and command line (`Hosting/`), REST API (`Api/`), `/ws` preview (`Preview/`), security (`Security/`), desktop windows (`Desktop/`: `DisplayWindowManager`, the platform seam, AppKit through libobjc in `Desktop/MacOS/`), serves the web root (`Hosting/StaticSite.cs`) |
| `InfoPanel.Core.Tests`, `InfoPanel.Tests` | net10.0 | xUnit v3 (VSTest). Core: models, persistence, goldens, render loop, the kind manifest snapshot (`Snapshots/kinds.json`), the e2e archive (`Fixtures/e2e/`), the formats (`Spec/`: every `spec/examples` file, the model against the schemas). Host: API, security, `/ws`, static files, preview parity |
| `spec/` | JSON, Node 22+ | The document, scene and engine API formats: JSON Schemas (`schemas/`), generated TypeScript types (`types/`), docs (`docs/`), examples (`examples/`, with `expected.json`), dependency-free tools (`tools/`). `package.json` is `@infopanel/spec`, private; clients use it by path. Licensed by `spec/LICENSE`; see `spec/README.md` |
| `InfoPanel.Plugins*`, `InfoPanel.Extras` | net8.0 | 1.x plugin SDK, host, loader and simulator, kept on net8.0 so existing plugins keep working. The engine doesn't load plugins yet. `InfoPanel.Extras` is net8.0-windows (builds on macOS with `EnableWindowsTargeting`) |

Package versions are central (`Directory.Packages.props`); `global.json` pins SDK 10.0.403 (`latestFeature`). Ask before adding any NuGet dependency.

## Commands

From the repo root:

```bash
dotnet build InfoPanel.slnx
dotnet test InfoPanel.slnx
dotnet test InfoPanel.slnx --filter Category=Golden            # renderer goldens only
dotnet test InfoPanel.slnx --filter "FullyQualifiedName~Preview"

# Engine for development: port 5199, data in ./.data, a web client's dev-server origin http://localhost:5180 allowed, mock sensors
dotnet run --project InfoPanel --launch-profile dev
# Engine as users run it: default data folder, opens the browser; a source build has no wwwroot, so without --web-root it serves the API only
dotnet run --project InfoPanel -c Release
dotnet run --project InfoPanel -c Release -- --web-root <dir>  # serve a built web client (the folder with its index.html)
dotnet run --project InfoPanel -- --help                       # all flags
```

Engine flags: `--port`, `--data-dir`, `--web-root`, `--token`, `--dev-origin` (repeatable), `--allow-origin <origin>` (repeatable; an exact `scheme://host[:port]` origin of a web client served elsewhere, `https` unless the host is loopback, see the security model), `--import-v1 <1.x folder>`, `--fresh`, `--mock`, `--mock-seed` (implies `--mock`), `--no-browser`, `--no-windows` (no desktop windows). `--fresh` is a dev and test flag that wipes the whole data folder before start-up: it needs `--data-dir`; it refuses a filesystem root, the home folder or a folder above it (checked on the path as given and on its real path, links resolved) and a folder with any top-level entry the engine doesn't write (`profiles.xml`, `profiles/`, `assets/`, `fonts/`, `logs/`, `runtime.json`, `engine.lock`, their write temp files); otherwise it deletes everything in the folder except `engine.lock`, including whatever is inside those engine folders (user-added fonts too), removing links without following them. The web root is `--web-root <dir>` as given, else `{BaseDirectory}/wwwroot` when it has an `index.html`; with neither the engine serves the API and `/ws`, and a plain-text "no web client installed" page on unmatched non-file paths outside `/api`, `/ws` and `/auth` (file-like paths such as `/index.html` or `/missing.js` return 404). `--mock` is mock mode: the mock sensors instead of the platform's real ones, and simulated values for well-formed keys no source handles; without it unknown keys are `unavailable` (the `dev` launch profile and every test host pass it). Exit codes: 0 clean, 1 fatal, 2 bad arguments, 3 data folder in use. The sign-in link (`/auth/<token>`) and each `--allow-origin`'s pairing link go to the console only (the file log gets them with `<token>`).

From `spec/` (Node.js 22+, no dependencies):

```bash
npm run gen                                    # regenerate types/index.d.ts from schemas/
npm run check                                  # types current + the generator's tests
TSC=path/to/tsc npm run check                  # also type-check every valid example against the types
```

`dotnet test InfoPanel.slnx --filter "FullyQualifiedName~InfoPanel.Core.Tests.Spec"` runs the format tests: every example against the schemas and the engine's rules (expectations in `spec/examples/expected.json`), the `InfoPanel.Spec` model against the schemas, and `spec/tools/check.mjs` when `node` is on the PATH (skipped otherwise).

The check that everything is green, from the repo root: `dotnet build InfoPanel.slnx && dotnet test InfoPanel.slnx`, and `npm run check` in `spec/`.

### Regenerating committed outputs (only on purpose; review the diff)

| What | Command | When |
|---|---|---|
| Renderer goldens `InfoPanel.Core.Tests/Golden/{osx,win,linux}/*.png` | `UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden` | Rendering changed on purpose; look at every rewritten PNG. Only out-of-tolerance or missing goldens are rewritten. macOS is the reference OS |
| Kind manifest snapshot `InfoPanel.Core.Tests/Snapshots/kinds.json` | `UPDATE_SNAPSHOTS=1 dotnet test InfoPanel.slnx --filter KindManifest` | Models or `PropertyMap` changed. It is the contract a client's schemas are checked against: a changed snapshot is an API change, and clients copy the new file |
| E2E archive `InfoPanel.Core.Tests/Fixtures/e2e/all-kinds.infopanel` | `UPDATE_FIXTURES=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~E2eArchiveFixture` | The Core fixtures or their assets changed |
| TypeScript types `spec/types/index.d.ts` | `npm run gen` in `spec/` | Any schema change; the types are never edited by hand |
| Fixture images `InfoPanel.Core.Tests/Fixtures/assets/` | `GENERATE_FIXTURE_ASSETS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~FixtureAssets` | Rarely; regenerate goldens and the e2e archive after |
| Frozen 1.x XML `InfoPanel.Core.Tests/Fixtures/v1/` | `GENERATE_FIXTURES=1 …--filter FixtureGenerator` | Don't: they are 1.x's format, generated before 2.0 changed anything. Regenerating after a model change defeats them |

## Architecture

- **One engine, browser UI.** The engine runs headless on `127.0.0.1:{port}` and serves the API, `/ws` and the web root's static files (`index.html` for every non-file path outside `/api`, `/ws` and `/auth`). A client only edits through the API and only shows engine frames; it has no model or renderer of its own.
- **REST** (`/api/...`): session, kinds manifest, fonts, sensors and values, profiles (CRUD, duplicate, save, revert, undo, redo, thumbnail, export, import), items (add, batch patch, delete, move, duplicate). Item properties travel under `props` with the C# names in camelCase, enums as strings. Errors are `application/problem+json`.
- **`/ws`**: the client subscribes to a profile at a scale and fps; the server sends `hello`, `doc` events (revision, dirty, canUndo, canRedo after every change, from any tab), `profiles` events, and binary frames (`IPF1`, a JSON header with revision and item bounds, then a PNG). A frame is sent only when pixels, bounds or revision changed.
- **Rendering**: `ProfileRenderer` (Core) draws a profile at `scale` through the canvas matrix. `RenderService` runs one loop per subscribed profile, renders each requested scale once per tick and hands the same `RenderedFrame` to every `IFrameSink` (the `/ws` preview and desktop windows now; panels later). A thumbnail (`RenderService.RenderOnce`, `GET .../thumbnail`) of a profile with a live loop redraws the loop's current tick with the loop's options (smoothing included) without advancing it, so at the profile's width it equals the live frame and leaves the loop's animation alone; with no loop it advances the tick and draws values at their target (`Smoothing = false`).
- **Desktop windows** (`InfoPanel/Desktop/`, 1.x's `DisplayWindow`): `DisplayWindowManager` (hosted service) keeps one borderless window per profile with `Active = true`, each an `IFrameSink` at scale 1 (one frame pixel per backing pixel), and applies `Topmost`, `Drag`, `Resize`, size and placement (`WindowX`/`WindowY` in pixels from the display matched by `TargetWindow`, 1.x's rules) as they change, undo and redo included. A drag or resize of the window is an ordinary undoable profile edit (`ProfileDocument.PlaceWindow`), saved by Save. On macOS AppKit runs on the process main thread: `DesktopHost` runs the host on a worker and `-[NSApplication run]` on the main thread until the host stops; with `--no-windows`, off the main thread (test hosts) or on Windows/Linux (not ported yet) the platform is `NullDesktopPlatform` and nothing opens. Manager state lives on the UI thread (`IDesktopPlatform.Post`). `INFOPANEL_WINDOW_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacDesktopWindowTests` opens real windows (skipped otherwise).
- **Editing**: `Workspace` holds the profile list and open `ProfileDocument`s. Every edit runs under the document's `SyncRoot` as one transaction, bumps the revision only when the XML snapshot changed, and records undo (per-document XML snapshots; coalescing keys sent with the edit: `gesture:` always merges, other keys within 1 s). Dirty compares with the saved snapshot.
- **Sensors**: `SensorHub` over `ISensorSource`s: in mock mode `MockSensorSource` only, with a deterministic simulated walk for unknown well-formed keys; otherwise the platform's real source (macOS: `MacSensorSource`, keys `plugin:/infopanel.macos/{group}/{sensor}`; none on Windows and Linux yet) and unknown keys are `unavailable`. Statuses (`SensorStatus`): `live`, `stale` (last value kept), `unavailable`, `permission-denied`, `simulated` (mock mode), `missing` (unbound or malformed); `/api/sensors` and `/api/sensors/values` carry `status`, items `meta.sensor.status`. Real sources are `PolledSensorSource`s over an `ISensorSampler`: discovery on start (the host's `SensorService` waits up to 5 s for every source's discovery before the server starts, so the first `/api/sensors` is complete; a slower source is logged and finishes in the background), a poll a second on the thread pool, stale after 5 s without a good sample (or right after a failed read), unavailable after 30 s. `INFOPANEL_SENSOR_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacSensorSourceTests` reads the real macOS sensors (skipped otherwise). Values and history are frozen per render tick.

## Invariants (break one only with a decision entry)

- **Preview = panel.** A client never renders; frames come from `ProfileRenderer`, and `PreviewParityTests` (host) holds the WS frame to the panel path pixel for pixel.
- **Model freeze**, until 2.0's own document format replaces the 1.x XML as the engine's storage (superseded as a goal: 1.x profiles and `.infopanel` files become import-only; decision 2026-10-27). Until then: never add, remove or rename a public read/write property or public field on the models (`InfoPanel.Core/Models`); `DisplayItem.Guid` (`<Guid>`) was the one exception. The XML must stay readable by 1.x; `HttpImage.HttpUrl` serializing as null is the only whitelisted difference.
- **Rendering never writes to the model**, and render time, sensors and history come only from `RenderContext` (per-tick memo), so goldens and parity stay deterministic. Per-item animation state lives in `ItemStateStore`.
- **1.x files load or fail loudly**: unknown item types (newer 1.4.x builds) and `xsi:nil` items are dropped with a warning; unreadable XML throws and is never overwritten.
- **Undo, redo, dirty and save belong to the engine**; don't reintroduce client-side history.
- **The kind manifest snapshot is the client contract**: `GET /api/kinds` equals `InfoPanel.Core.Tests/Snapshots/kinds.json` (`KindManifestTests`, `EditEndpointTests.Kinds_MatchSnapshot`); change it only with the models or `PropertyMap`, and treat the diff as an API change.
- **The formats are a contract.** `spec/` (schemas, docs, examples) is versioned: a change to it is a contract change, made only as `spec/docs/versioning.md` allows (in draft until first shipped), with a decision entry, a regenerated `spec/types/index.d.ts`, examples for what changed (valid ones and an invalid one per new rule or limit, with `expected.json`) and the `InfoPanel.Spec` model and validators changed with it (`SpecSchemaAgreementTests`). The engine validates with the schema files themselves (embedded from `spec/schemas`), so there is one description of the formats. Public `spec/` text stays client-neutral: it specifies content and engine behaviour, never how a client produces it.
- **Limits are fixed per major format version** (`InfoPanel.Spec.SpecLimits`, `spec/docs/limits.md`): an engine accepts everything within them.
- Plugin SDK projects stay on net8.0.

## Security model

- Kestrel binds 127.0.0.1 only and ignores configured URLs and endpoints. Host filtering allows `localhost`, `127.0.0.1` and `[::1]`; a request that carries an `Origin` (WebSocket upgrades included) must come from the engine's own origin, a `--dev-origin` or an `--allow-origin`; any other gets 403 and no CORS headers.
- `/api` needs `Authorization: Bearer {token}`; `/ws` needs the subprotocol `infopanel.v1` and the token, either as the subprotocol `auth.{token}` (browsers can't set WebSocket headers) or, when no `auth.*` subprotocol is offered, as the first message `{"type":"auth","token":"…"}` within 5 s (a hard deadline for the whole phase, however the client streams fragments; the close handshake after it is bounded at 2 s): the server sends nothing before it and closes with 1008 and the reason `auth timeout`, `auth rejected` or `auth expected` otherwise (`PreviewSocketHandler`). Everything else is public: the web root's static files, `/healthz` and `/auth/{token}`. No cookies (a `localhost` cookie reaches every local server) and no CORS unless `--allow-origin`.
- `--allow-origin <origin>` (off by default) lets a web client served from that exact origin call the engine. It must be `https`, or `http` on a loopback host (`localhost`, 127.0.0.0/8, `[::1]`), since the pairing link hands it the token; `%` (escapes, IPv6 zone ids) is refused, and IPv6 hosts are stored as browsers serialise them (`[::ffff:127.0.0.1]` becomes `[::ffff:7f00:1]`). The origin gets CORS on `/api` and `/healthz` only (`Security/CrossOrigin.cs`: preflights answered 204 before the token check, the exact origin echoed, `Vary: Origin`, `Access-Control-Allow-Private-Network: true` when asked, never `Allow-Credentials`; 401s carry the CORS headers too), and its `/ws` upgrades pass the Origin check. The pairing link `{origin}/#engine=http://127.0.0.1:{port}&token={token}` hands that origin the engine URL and token in the fragment. The engine's own origin and the dev origins get no CORS headers.
- `GET /auth/{token}` redirects to `/#token={token}`; the web client reads the fragment (never sent to a server), keeps the token in `localStorage` (per origin, so per port), removes it from the address bar and sends it as a Bearer token. The token is random per start unless `--token` is given.
- CSP, `X-Frame-Options: DENY`, `nosniff` and `Referrer-Policy: no-referrer` on every response; imports guard against zip-slip, entry-count and size bombs, and oversized profiles.

## Data folder

`{LocalApplicationData}/InfoPanel/v2`: `~/Library/Application Support/InfoPanel/v2` on macOS, `%LOCALAPPDATA%\InfoPanel\v2` on Windows (`--data-dir` overrides). Same layout as 1.x plus the engine's files: `profiles.xml`, `profiles/{guid}.xml`, `assets/{guid}/`, `fonts/` (user fonts; Inter is bundled), `logs/infopanel-*.log` (7 days), `runtime.json` (pid, port and URL while running), `engine.lock`. 1.x's own data (`%LOCALAPPDATA%\InfoPanel`) is only read, by `--import-v1`.

## Conventions

- Logging: Serilog (see `LoggingGuidelines.md`).
- Tests pin culture and time; goldens and other process-wide font state run in the non-parallel `Golden` collection.
- Docs for 2.0 live in `docs/2.0/`; `PANELS.md`, `PLUGINS.md` and `PLUGIN-ARCHITECTURE.md` still describe 1.x.
