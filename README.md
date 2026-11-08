<p align="center">
  <a href="https://www.infopanel.net"><img src="Images/logo.png" width="60" alt="InfoPanel"></a>
</p>

<h1 align="center">InfoPanel</h1>

<p align="center">InfoPanel turns your computer's sensors into panels you design, on the desktop, a second display and (later in 2.0) USB LCD panels.</p>

> [!NOTE]
> **This is the `2.0.x` branch: InfoPanel 2.0, in development and not released.** InfoPanel 1.x, the Windows app, is the current release: get it from [Releases][release] or the [Microsoft Store][msstore]; its code and README are on branch [`1.4.x`][1.4.x]. If you use 1.x, keep using it for now. 2.0 reads your 1.x profiles and settings and never changes them, and USB panels aren't in 2.0 yet.

2.0 is one engine for Windows and macOS. It reads the sensors, draws every panel with Skia, and listens on this computer only (127.0.0.1) for the editor in your browser; what you edit is the engine's own picture of the panel, so the preview is what the panel shows.

## What works today (as of 2026-11-05)

| | macOS | Windows |
|---|---|---|
| Sensors | The Mac's own (CPU, GPU, memory, temperatures, fans, power, battery, disks, network), without administrator rights | [HWiNFO][hwinfo] (with Shared Memory Support on), HWiNFO's gadget values, LibreHardwareMonitor (with the PawnIO driver; every sensor once [set up to read them all](#windows-sensors-from-source)) |
| Clock, drives, network (the public address too) | Yes | Yes |
| Weather | Yes, from the configured service¹ | Yes, from the configured service¹ |
| 1.x plugins (sensors, texts, tables) | Yes² | Yes |
| Panels on the desktop | Yes | Yes |
| Menu-bar item | Yes | Later (a tray icon) |
| USB LCD panels | Later | Later |
| 1.x profiles and settings | Read, never changed | Read, never changed |

¹ A build from source has no configured service by default, so its weather stays off. ² A plugin built on a Windows-only library runs on Windows only (the bundled System Info among them). Linux isn't supported yet: the code has Linux paths (the clock, drives and network; the preview's video through ffmpeg), but no Linux build is tested, and it reads no hardware sensors and opens no desktop windows.

**[Documentation](docs/2.0/README.md) · [Run from source](#run-from-source) · [Website][website] · [Discord][discord] · [Reddit][reddit]**

## Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then from the repository's root:

```bash
dotnet run --project InfoPanel -c Release                          # the API and the live preview
dotnet run --project InfoPanel -c Release -- --web-root <folder>   # also serves a built web client from <folder>
dotnet run --project InfoPanel -- --help                           # every flag
```

This repository builds the engine, not a web client. Without `--web-root` (or a `wwwroot` folder with an `index.html` next to the engine) it serves the API and the live preview only, for a client of your own: [`spec/`](spec/README.md) has the formats and the API. With a web client, the engine prints a sign-in link to the console; it never opens a browser by itself. The menu-bar item's **Open Editor** (macOS) opens the editor, and so does starting InfoPanel again while it runs ([the desktop shell](docs/2.0/desktop-shell.md)).

The engine keeps its profiles, media, settings and logs in `~/Library/Application Support/InfoPanel/v2` on macOS and `%LOCALAPPDATA%\InfoPanel\v2` on Windows, or the folder `--data-dir` names ([the data folder](docs/2.0/data-folder.md)). It reads 1.x's data folder (`%LOCALAPPDATA%\InfoPanel`, or `--v1-dir <folder>`) but never writes to it ([1.x import](spec/docs/import.md)).

Video files and RTSP cameras in a panel need FFmpeg's libraries, which a build from source doesn't include (HTTP MJPEG cameras work without them); the preview as video uses the computer's hardware encoder, or ffmpeg on macOS ([video](docs/2.0/video.md)).

### Windows sensors from source

- **HWiNFO:** turn on Shared Memory Support in HWiNFO's settings (the free version's shared memory stops after 12 hours). Readings ticked "Report value in Gadget" in HWiNFO are read too.
- **LibreHardwareMonitor** runs in the engine's sensor helper, which only a Windows publish includes: `dotnet publish InfoPanel -c Release -r win-x64`, then run the engine from the publish folder (`dotnet run` and a plain build have no `sensor-helper` folder).
- **PawnIO** is the driver LibreHardwareMonitor needs for the CPU's temperatures, clocks and power and the board's fans and voltages. A build from source doesn't include its installer, so the engine doesn't offer to install it: install it from [pawnio.eu](https://pawnio.eu).
- **Every sensor:** some need administrator rights even with PawnIO. The engine never elevates itself; it can set up a task that runs its sensor helper as SYSTEM, which needs the engine installed under Program Files ([reading all sensors](spec/docs/api.md#reading-all-sensors)).

## Documentation

[The documentation index](docs/2.0/README.md) lists every page: the data folder, the desktop shell, plugins, video, weather, AI apps; then the formats and the engine API in [`spec/`](spec/README.md), for client authors; then the pages for contributors.

## Plugins

2.0 runs 1.x plugins: the SDK is the same, and the engine reads their sensors, texts and tables, so a 1.x profile bound to a plugin reads it live. How the engine finds and runs them: [plugins](docs/2.0/plugins.md). Writing one: [the plugin guide](PLUGINS.md) and [its architecture](PLUGIN-ARCHITECTURE.md). Community plugins: [the plugin registry](PLUGIN-REGISTRY.md).

## InfoPanel 1.x

InfoPanel 1.x is the Windows app released today, with USB LCD panels (BeadaPanel, Turing Smart Screen/Turzx and others). Its features, supported hardware and [display panels guide][panels] are on branch [`1.4.x`][1.4.x]. Get it from [Releases][release] or the [Microsoft Store][msstore]; it has a thread on the [HWiNFO forum][forum].

## Contributing and security

Issues are welcome; 2.0 takes no pull requests until its first release ([contributing](CONTRIBUTING.md)). To report a vulnerability, see [security](SECURITY.md).

## Licence

InfoPanel is licensed under GPL 3.0 - see the [license file][license] for details.

The formats and the API contract in `spec/` (its schemas, documentation and generated types) are under the MIT licence, [`spec/LICENSE`](spec/LICENSE), so anyone can build a client on them.

Third-party components: [LICENSES.md](LICENSES.md).

Contributions need the contributor agreement, [`CLA.md`](CLA.md): you keep the copyright in your contribution and give Habib Rehman a broad licence to use and license it, including under other terms.

---

InfoPanel is not affiliated with HWiNFO. HWiNFO is a registered trademark of its respective owners.

<!--
References
-->

[reddit]: https://www.reddit.com/r/InfoPanel/
[website]: https://www.infopanel.net
[forum]: https://www.hwinfo.com/forum/threads/infopanel-desktop-visualisation-software.8673/
[discord]: https://discord.gg/aNGeJxjE7Q
[msstore]: https://apps.microsoft.com/store/detail/XPFP7C8H5446ZD
[release]: https://github.com/habibrehmansg/infopanel/releases
[1.4.x]: https://github.com/habibrehmansg/infopanel/tree/1.4.x
[panels]: https://github.com/habibrehmansg/infopanel/blob/1.4.x/PANELS.md
[hwinfo]: https://www.hwinfo.com
[license]: LICENSE
