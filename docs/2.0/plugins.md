# Plugins

The engine runs 1.x plugins (the `InfoPanel.Plugins` SDK, net8.0) and reads their sensors, texts and tables into the sensor hub as the `plugins` source, under 1.x's keys, so a 1.x profile bound to a plugin reads it live. This page says how the engine finds plugins, runs them, and what it takes from them. Writing a plugin is still `PLUGINS.md` and `PLUGIN-ARCHITECTURE.md` (1.x; the SDK is the same).

## Packages and where they are

A plugin package is a folder `InfoPanel.{Name}` (letters, digits, `-` and `_`) holding `InfoPanel.{Name}.dll` and, optionally, `PluginInfo.ini` (its name, description, author, version and website). The engine looks in two folders when it starts:

| Folder | What |
|---|---|
| `{engine folder}/plugins/` | The bundled plugins (`InfoPanel.Extras` today), read-only; `--bundled-plugins <dir>` names another folder |
| `{data}/plugins/` | Plugins the user added (per user, in the [data folder](data-folder.md)) |

Folder names are compared ignoring case, and a bundled package wins over an installed one of the same name. A folder whose name isn't `InfoPanel.{Name}`, or that has no `InfoPanel.{Name}.dll`, is skipped with a warning; a folder starting with `.` is skipped silently. In the data folder, a package folder that is a link isn't followed, and the folder's listing is bounded (10,000 entries). A package that can't be read, or a plugins folder that can't be listed, is skipped with a warning, and the other packages start. `--no-plugins` starts no plugin at all.

## Hosts

Each package runs in a process of its own: the engine's own executable, started as `InfoPanel --plugin-host --pipe <name> --plugin <dll> --log-dir <dir> --settings-dir <dir> --log-level <level>`, which runs the plugin host (`InfoPanel.Plugins.Host`) and nothing of the engine. The plugins inside a package's DLL share its host. The engine and the host talk JSON-RPC (StreamJsonRpc) over a named pipe the engine serves, for the current user only, named `ipph-{16 hex}` so its Unix socket path stays short.

- **Start.** Hosts start once the engine listens, after the built-in sensor sources, never inside their start-up wait. A host has 30 s to connect, 30 s to initialise its plugins and 10 s more to list their entries (without that list the engine goes on with none).
- **Restarts.** A host that exits or disconnects unexpectedly, or fails to start, is started again after 2 s, at most 3 times in 60 s (counted from the first failure); after that its package is failed, with the reason: the host's exit code and the end of what it wrote to stderr, or the last error. A stop the engine makes never restarts.
- **Stop.** One package's host is asked to shut down and given 5 s to exit, then killed (with its children) and waited for 5 s. When the engine stops, every host gets 2 s in all, in parallel, and the rest are killed before the engine exits; a stop that comes before the hosts have started means none starts.
- **The engine going away.** A host whose pipe ends without a shutdown (the engine crashed or was killed) gives its plugins 2 s to close and exits; one still alive 5 s after the pipe ended ends itself through the operating system, whatever threads or process-exit handlers a plugin left running (no managed code runs in that end).
- **Other operating systems.** The loader creates every plugin type in a DLL except one marked `[SupportedOSPlatform]` only for other operating systems, which it skips and logs, so a plugin built on a Windows-only library doesn't fail the others in its DLL. A DLL built for Windows as a whole (`net8.0-windows`) is still tried: most use portable APIs. In the bundled Extras, Volume, System Info and HWiNFO Registry are Windows only; Clock, Drive Info, Network Info and Weather run everywhere.
- **The runtime.** Plugins built for .NET 8 run on the engine's runtime.

## What a host writes

Only in the data folder, in the folders the engine gives it:

| File | What |
|---|---|
| `logs/plugins/{package}-{yyyyMMdd}.log` | The host's log, at the engine's `logLevel`, daily, the newest 3 kept, 10 MiB a file; never the console |
| `plugin-settings/{package}/{plugin id}.json` | A configurable plugin's settings, a flat map of its keys and values, written to a temp file and renamed over it |

A settings change is logged by its key, never its value (settings can be secrets, such as an API key). Nothing goes to 1.x's folder (`{LocalApplicationData}/InfoPanel`), which 1.x's host wrote its logs and settings to. Before each start of a host, the engine checks the folders it gives it: a package's settings folder or the log folder that is a link, or one of the package's log files that is a link, or a folder that with links resolved is in the 1.x folder (as start-up checks the data folder's own entries), fails the package at once, with the reason, and no host starts. Plugins run as the user, who can write to the 1.x folder anyway, so this check guards against a misconfigured folder, not against a hostile plugin.

## Sensors

Every entry a package's plugins list is a sensor of the `plugins` source:

- **Keys** are 1.x's: `plugin:/{plugin id}/{container id}/{entry id}`, or `plugin:/{plugin id}/{entry id}` for an entry in an ephemeral container.
- **Group** is `{plugin name} › {container name}`; **name** the entry's; **unit** the sensor's (empty for texts and tables).
- **Readings:** a sensor's value, minimum, maximum and average as the plugin reports them (a number that isn't finite reads as 0); a text's text; a table's rows, read by a scene's table input as `docs/bindings.md` says, its text the first row's cells.
- **Status** follows the package's host: `live` while it runs (plugins send changes only, so an unchanged value is still live), `stale` while it restarts (the last values kept), `unavailable` once it is stopped or failed. A key under a plugin id a package provided in this run but that names no entry is `unavailable`; a key under any other plugin id is left to the other sources (unavailable, or simulated in mock mode).
- **Plugin ids** that are empty, contain `/`, are longer than 64 characters or start with `infopanel.` (the engine's own sources) are refused, and so is an id another package already provides, so keys stay unique. A plugin whose containers or entries have an id longer than 256 characters, or that would make a key longer than 512 (as long as a document's sensor key may be), is refused too, before any of its keys is made. A refusal is logged as the package's error.

## Bounds

Plugins are code the user chose to run, but a buggy one must not take the engine's memory. A message over 16 MiB from a host closes its connection (the host restarts by the policy), and so does a host that has more than 1,024 messages, or 32 MiB of them, waiting for the engine to handle them (it handles a host's messages one at a time, in order). A host's calls to the engine are notifications: one that asks for a reply closes its connection too, so the engine never holds replies a host doesn't read. One the engine doesn't take (another method, or arguments it can't read) is dropped as it is read, with one warning per connection, and never counts against those limits. A host's plugins list at most 16,384 entries; the rest are left out, with one warning. Names, groups and units are cut at 256 characters, texts at 4,096, tables at 1,000 rows, 64 columns and 1,024 characters a cell, each with one warning per entry. A table's column names are made unique, ignoring case (an empty or repeated name gets `#` and a number no other column has), and a table the engine can't read is left without a value, with one warning, while the rest of the update is applied. A host's stdout and stderr are read as they come, in chunks of at most 4 KiB rather than as lines: stdout is dropped (the host logs to its file), and only the last 4 KiB of stderr are kept, for the reason a host failed.

## Errors

A plugin's errors, a refused plugin, a failed start and a restart are logged as warnings, `plugin {package}/{plugin id}: …` (the recent log entries carry them), and a package keeps its newest 20.
