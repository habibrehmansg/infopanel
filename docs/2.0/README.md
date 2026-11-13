# InfoPanel 2.0 documentation

The pages for the `2.0.x` branch. Start with the [README](../../README.md): what works today and how to run the engine from source.

## Using the engine

| Page | What |
|---|---|
| [The data folder](data-folder.md) | Where the engine keeps profiles, media, settings, tokens and logs, and the rules that keep one engine from damaging another's files |
| [The desktop shell](desktop-shell.md) | The menu-bar item on macOS, Quit, and how the editor is opened (Open Editor, a second launch) |
| [Plugins](plugins.md) | How the engine finds and runs 1.x plugins, what it reads from them, and their limits |
| [Video](video.md) | The live preview as H.264 video (hardware encoders, ffmpeg), and FFmpeg's libraries for video files and cameras |
| [Weather](weather.md) | The weather source: its readings, and how it asks the configured service |
| [AI apps elsewhere](mcp.md) | The tools AI apps may use on this engine, and the rules they run under |
| [Crash reports](crash-reports.md) | What a release build sends to Sentry when the engine fails, how it is scrubbed, and the switch that turns it off |
| [The relay](relay.md) | The engine's connection to the configured service's relay, which AI apps elsewhere use, and its frames |

## Writing a client

[`spec/`](../../spec/README.md) is the contract a client works to: the document and scene formats, their JSON Schemas and examples, and the engine API ([`spec/docs/api.md`](../../spec/docs/api.md), with its request index `spec/openapi.json`).

## Writing a plugin

| Page | What |
|---|---|
| [The plugin guide](../../PLUGINS.md) | Writing a plugin with the `InfoPanel.Plugins` SDK, which 1.x and 2.0 share |
| [Plugin architecture](../../PLUGIN-ARCHITECTURE.md) | The plugin host, its process and how it talks to the engine |
| [The plugin registry](../../PLUGIN-REGISTRY.md) | Community plugins, and how to list yours |

## Working on the engine

| Page | What |
|---|---|
| [Contributing](../../CONTRIBUTING.md) | Issues, pull requests, and the checks to run |
| [`CLAUDE.md`](../../CLAUDE.md) | The guide to the code: projects, commands, architecture, invariants and conventions |
| [Porting](../../PORTING.md) | What still has to come over from 1.x |
| [Logging guidelines](../../LoggingGuidelines.md) | How the code logs |
| [macOS sensor spike](macos-sensor-spike.md) | A research note: which Apple Silicon sensors a normal user can read |
| [Third-party components](../../LICENSES.md) | The third-party components InfoPanel uses |
