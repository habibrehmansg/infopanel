# Packaging

How a release's downloads are built from this repository. Each platform has one script under `packaging/` that turns a
self-contained publish of the engine into the download. The scripts name no web client: the version and the web
client's origin are arguments, and the origin is built in as `--allow-origin`'s default (`InfoPanelAllowOrigin`, see
the security model in `CLAUDE.md`), so the engine opens that web client from Open Editor without any flag.

The downloads carry the engine, libusb-1.0 (USB panels, [panels.md](panels.md)) and FFmpeg's four decode-only
libraries (video files and RTSP cameras, [video.md](video.md)); no web client (`wwwroot`). libusb is pinned by
version and SHA-256 in `native/libusb/pins.txt` and comes from libusb's own release: the Mac app's `libusb-1.0.dylib`
is built from its source by `native/libusb/build.sh`, the Windows installer carries the release's x64
`libusb-1.0.dll`. FFmpeg 9.0.2 and dav1d 1.5.4 are pinned the same way in `native/ffmpeg/pins.txt` and built from their
release sources by `native/ffmpeg/build.sh` without `--enable-gpl`, `--enable-nonfree` and `--enable-version3`
(their terms: `LICENSES.md`), with dav1d linked into libavcodec: natively for the Mac app, cross-compiled in a pinned
Linux container for the Windows installer. The Linux
downloads carry neither: the `.deb` depends on the system's libusb and recommends its FFmpeg (below).

## macOS: the app and its disk image

```bash
packaging/macos/build.sh --version 2.0.0-beta.1 --allow-origin https://client.example --out <a new or empty folder>
```

`--version` is a SemVer (`2.0.0`, `2.0.0-beta.1`, `2.0.0+build.1`); `--allow-origin` is what the engine accepts as an origin:
https, a host and an optional port, with no user info, path, query or fragment (`--check-args` only checks both).

On a Mac with the .NET SDK `global.json` pins, Xcode's command line tools, and meson, ninja and pkg-config for FFmpeg
(`brew install meson ninja pkgconf`, or meson and ninja from PyPI in a virtual environment on `PATH`);
`codesign`, `hdiutil`, `plutil` and `ditto` are macOS's own. The script first builds libusb from its pinned source with
`native/libusb/build.sh`, into `<out>/libusb` (`--libusb-source <libusb-<version>.tar.bz2>` takes the release tarball
fetched elsewhere instead of downloading it; it is refused unless its SHA-256 is the pinned one), then FFmpeg with
`native/ffmpeg/build.sh --target macos-arm64`, into `<out>/ffmpeg` (`--ffmpeg-source <ffmpeg-<version>.tar.xz>` and
`--dav1d-source <dav1d-<version>.tar.xz>` the same way). `INFOPANEL_BUILD_JOBS` caps FFmpeg's parallel compile jobs.
It writes:

- `<out>/libusb/`: libusb's build, `libusb-1.0.dylib` and `libusb-COPYING.txt`;
- `<out>/ffmpeg/`: FFmpeg's build: `bundle/` (the four libraries and `ffmpeg-COPYING.txt` and
  `dav1d-COPYING.txt`), `source/` (the two tarballs it was built from) and `config.mak` (FFmpeg's configuration, kept
  for reference: the checks read the libraries themselves);
- `<out>/publish/`: the self-contained `osx-arm64` Release publish (`-p:Version`, `-p:InfoPanelAllowOrigin`, no debug
  symbols, with the bundled AI usage plugin in `plugins/InfoPanel.AiUsage/`, [plugins](plugins.md)) with `libusb-1.0.dylib` and FFmpeg's four libraries copied into its top level, and in `plugins/{folder}` every panel driver project
  (`InfoPanel.Panels.{Family}/` with a `panel.json`, but the virtual panel, a test fixture), each published framework
  dependent for `osx-arm64`: the engine's bundled panel packages (`build.sh --list-panel-drivers` lists them, and those
  it leaves out, without building anything). The Windows script publishes the same projects for `win-x64` the same way;
- `<out>/dmg/InfoPanel.app`: the app;
- `<out>/InfoPanel-<version>-mac-arm64.dmg`: the disk image (`hdiutil create -format UDZO`, an HFS+ volume named
  "InfoPanel <version>") holding the app, a link to `/Applications`, `LICENSE`, `LICENSES.md`, `libusb-COPYING.txt`,
  `ffmpeg-COPYING.txt` and `dav1d-COPYING.txt`; its SHA-256 is printed last.

**The app.** `Info.plist` is `packaging/macos/Info.plist` with the version set: `CFBundleShortVersionString` its
major.minor.patch (`2.0.0`), `CFBundleVersion` the whole of it. The bundle id is `net.infopanel.InfoPanel`; `LSUIElement` makes it a
menu-bar app with no Dock icon; `LSMinimumSystemVersion` is 15.0, the oldest macOS .NET 10 supports (Microsoft's
[.NET 10 supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md): macOS 15, 26
and 27). The icon is `packaging/macos/AppIcon.icns`, made from `Images/logo.png` by `packaging/macos/make-icon.sh`
(`sips` and `iconutil`; run it when the logo changes and commit its output). `CFBundleURLTypes` declares the `infopanel:` link
(`CFBundleURLSchemes` `infopanel`, named `net.infopanel.InfoPanel`), which opens the editor; nothing is read from it
(desktop-shell.md, "Opening the editor").

**Its layout.** `codesign` takes every file in `Contents/MacOS` for code: a managed `.dll`, a `.json` or a folder there
(the satellite assemblies) makes it refuse to sign the bundle. So the publish goes to
`Contents/Resources/engine`, its Mach-O files (the executable, the .NET runtime's and SkiaSharp's libraries) move to
`Contents/MacOS`, and each side links to the other's entries with relative symlinks. The executable finds its libraries
and managed files beside it; .NET takes the app's folder from `InfoPanel.dll`'s real path, `Contents/Resources/engine`,
and finds the libraries there through the links. The one Mach-O file below the publish's top level, the media helper's
library (Now Playing's `mediaremote-adapter/MediaRemoteAdapter.framework/MediaRemoteAdapter`, checked with
`native/mediaremote-adapter/build.sh --verify`), moves to `Contents/Frameworks/MediaRemoteAdapter.dylib`, and a relative
symlink takes its place, where the engine and the helper's script find it; a publish without it stops the script. Any
other Mach-O file below the publish's top level stops it too: this layout would seal it as a resource rather than sign
it as code. The bundled plugins under `plugins/` (AI usage in `plugins/InfoPanel.AiUsage/`, and the panel drivers) are
managed files only, so they stay in `Contents/Resources/engine/plugins/`, where the engine finds them beside it.

**libusb.** `native/libusb/build.sh` downloads libusb's release tarball (the version in `native/libusb/pins.txt`), refuses
it unless its SHA-256 is the pinned one, builds it for arm64 and leaves one regular file, `libusb-1.0.dylib`, whose
install name is `@rpath/libusb-1.0.dylib`, linked against macOS's own libraries only (`--source` takes a tarball fetched
elsewhere; it is checked the same way). It builds for the app's minimum macOS, not the building Mac's:
`MACOSX_DEPLOYMENT_TARGET` is `Info.plist`'s `LSMinimumSystemVersion` for the compile and the link, and a library whose
minimum (its `LC_BUILD_VERSION`'s `minos`, `vtool -show-build`) is newer is refused, after the build and again by
`build.sh` before packaging (`native/libusb/build.sh --verify <dylib>` runs those checks alone). It also leaves
`libusb-COPYING.txt`, the source's `COPYING` (libusb's licence text, its SHA-256 pinned in `pins.txt` too), which the
app's `Contents/Resources` and the disk image carry beside `LICENSES.md`. `build.sh` copies the library into the publish
before the layout step, so the rule above moves it to `Contents/MacOS` (linked from `Contents/Resources/engine`) and
signs it with the rest; the engine finds it beside itself (`InfoPanel.Core/Panels/Usb/LibUsb.cs`). There is no option
to package a library built elsewhere. To move to a new libusb release, change `pins.txt`.

**FFmpeg.** `native/ffmpeg/build.sh --target macos-arm64` downloads FFmpeg's and dav1d's release tarballs (the versions
in `native/ffmpeg/pins.txt`), refuses each unless its SHA-256 is the pinned one, before unpacking, and each licence text
unless its own is; builds dav1d static (meson) and FFmpeg's four libraries with only the containers, codecs and RTSP
client the engine uses, dav1d inside libavcodec. Each is one regular file, `lib{name}.{major}.dylib`
(`libavutil.61`, `libavcodec.63`, `libavformat.63`, `libswscale.10`), with the install name `@rpath/lib{name}.{major}.dylib`
and the rpath `@loader_path`, so the four find each other in the folder they're in; built for the app's minimum macOS as
libusb is. Its checks, on each library itself (after the build, and again in `build.sh` through `--verify <folder>`):
arm64, built for macOS (`LC_BUILD_VERSION`'s platform), the install names and rpath, its `{name}_version` exported,
nothing linked but macOS's own libraries and each other, the minimum macOS, no weak imports, a valid signature; and
the build record FFmpeg compiles into each library (what `avutil_license()` and `avutil_configuration()` and the
others answer): the licence `pins.txt` pins (`ffmpeg_license_sha256`), and one configure line in all four, with
`--enable-libdav1d` and without `--enable-gpl`, `--enable-nonfree` and `--enable-version3`. The Windows DLLs
(`--target windows-x64`, in `native/ffmpeg/windows-cross.Dockerfile`'s container: x86-64 Ubuntu, its packages from one
snapshot of Ubuntu's archive; the image built from it and checked on 13 Nov 2026 was
`sha256:46bcfee013110abda4cc9359387e5dd7a5c1057aeee3db24719324653e8f0f6d`, a digest a rebuild needn't repeat, since
its layers carry the build's own times) get the same build record checks, and each its export directory's own name (the library
and its major, which renaming the file doesn't change), its `{name}_version` export and its imports;
`packaging/windows/build.ps1` takes them as `-FFmpegDir` (Windows, below). `build.sh` copies the four into the publish beside libusb, so they take the same way into `Contents/MacOS`, and the two
licence texts beside `libusb-COPYING.txt`; the engine loads them by full path from beside itself
(`InfoPanel.Core/Images/Video/FFmpeg.cs`). To replace them (a right their licence gives users, `LICENSES.md`), put another build of the same majors in
`InfoPanel.app/Contents/MacOS` under the same names and sign the app again ad hoc:
`codesign --force --deep --sign - InfoPanel.app` (on Windows, put them in `C:\Program Files\InfoPanel 2` under the
same names). To move to new releases, change `pins.txt`.

**Signing.** Ad hoc only: every Mach-O file is signed (`codesign --force --sign -`), the deepest first, then the bundle,
with no hardened runtime; then `codesign --verify --deep --strict` checks it. Nothing is notarised.
An ad hoc signature is still needed: on Apple silicon every Mach-O must carry one, and a downloaded app without a valid
one is reported as damaged with no way to open it. A downloaded ad hoc app is refused once (`spctl` says rejected), and
the user allows it in System Settings › Privacy & Security › **Open Anyway** (since macOS 15, right-click › Open no
longer offers it).

**Checks** before a release, on the built app and on a copy taken out of the mounted image with a quarantine attribute
set by hand (`xattr -w com.apple.quarantine "0081;$(printf %x $(date +%s));Safari;" <copy>`), which stands in for a
download:

```bash
plutil -lint InfoPanel.app/Contents/Info.plist
codesign -dv InfoPanel.app                                   # Signature=adhoc; and on each Mach-O in it
codesign --verify --deep --strict --verbose=2 InfoPanel.app  # valid on disk; on the quarantined copy too
spctl -a -vv -t exec InfoPanel.app                           # rejected: expected for an app that isn't notarised
hdiutil imageinfo InfoPanel-<version>-mac-arm64.dmg
```

These checks prove the bundle's integrity (a valid ad hoc signature on every Mach-O file, a sound plist and image) and
that it isn't notarised. They don't prove which warning macOS shows on a user's Mac, nor that Open Anyway offers it:
only trying it as a user does (below) settles that.

The engine inside runs from Terminal as any build does. Give it a data folder and a port of its own, so it doesn't hand
off to an engine already running on the default data folder, and keep it headless:

```bash
InfoPanel.app/Contents/MacOS/InfoPanel --data-dir <temp> --port <free port> --no-tray --no-browser --no-windows
```

`GET /api/session` reports the version passed to `build.sh`; the console's pairing link starts with the origin passed
to it; `SIGTERM` stops it with exit code 0.

**Trying it as a user does** (by hand, after each release build): as a second macOS user, for whom no other InfoPanel
engine runs (another engine on the default data folder would take the launch and open its own editor):

1. Open the disk image and drag InfoPanel to Applications.
2. Open InfoPanel from Applications. macOS refuses it, saying it can't verify the app: choose Done.
3. System Settings › Privacy & Security: under Security, **Open Anyway** for InfoPanel; confirm with the user's
   password. InfoPanel starts.
4. The menu-bar icon appears; its menu says "Running · <version>". No window and no browser open by themselves.
5. Double-click InfoPanel in Applications again while it runs: the editor opens in the browser (the app is told it was
   opened again; no second process starts).
6. Quit InfoPanel from its menu: the icon goes.

## Windows

The Windows download is `InfoPanel-<version>-win-x64-setup.exe`, an Inno Setup 6 installer built on Windows by
`packaging/windows/build.ps1` from `packaging/windows/InfoPanel.iss`. It installs **InfoPanel 2** beside InfoPanel 1.x
and never replaces, moves or changes anything of 1.x's.

### Building it

On a Windows PC with the .NET SDK `global.json` names and [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\build.ps1 `
  -Version 2.0.0-beta.1 -AllowOrigin https://client.example -FFmpegDir C:\builds\ffmpeg\bundle -Out C:\builds\2.0.0-beta.1
```

- `-Version` is the release's version (`2.0.0`, `2.0.0-beta.1`): the engine's version (`GET /api/session`'s
  `engineVersion`), the installer's and the file name's. The file version is its first three numbers and `.0`.
- `-AllowOrigin` is the origin of the web client served elsewhere, built in as the default for `--allow-origin` (the
  engine's pairing link opens it). An `https` origin with no path.
- `-Out` must be a new or empty folder: the downloads, the publish (`publish\`) and the installer go there; nothing is
  deleted. It must be writable only by the user building (a folder of the building user's own, never one other users can
  change), and `build.ps1` refuses an `-Out` that is, or lies below, a junction or a
  symbolic link. The checks on the bundled files hold only against a folder nobody else can change.
- `-FFmpegDir` is FFmpeg's Windows build: the `bundle\` folder `native/ffmpeg/build.sh --target windows-x64 --out <dir>`
  makes in `native/ffmpeg/windows-cross.Dockerfile`'s container (on Linux, or Docker on a Mac or PC), with exactly
  `avutil-61.dll`, `avcodec-63.dll`, `avformat-63.dll`, `swscale-10.dll`, `ffmpeg-COPYING.txt` and
  `dav1d-COPYING.txt`. A missing file, any other entry, or one that is a folder or a link is refused before anything is
  published.
- It runs on Windows because only a build on Windows writes the manifest and the icon into `InfoPanel.exe`.

`build.ps1` publishes the engine self-contained for `win-x64` (with the sensor helper in `sensor-helper\` and the bundled AI usage plugin in `plugins\InfoPanel.AiUsage\`, then the panel drivers), adds four files it downloads (or takes from a download) and FFmpeg's six from `-FFmpegDir`, checks them, and compiles the installer:

| File | Where from | Checked against | Installed |
|---|---|---|---|
| `PawnIO_setup.exe` | PawnIO's GitHub release, at `PawnIoInstaller.BundledVersion` | `PawnIoInstaller.BundledSha256` (SHA-256), read from the engine's source | `pawnio\` beside the engine, never run by the installer (the engine's Install PawnIO runs it when asked) |
| `libusb-1.0.dll` (x64) | libusb's GitHub release, the 7z at the version in `native\libusb\pins.txt`, unpacked with Windows' own `tar` (7-Zip's `7z.exe` where that `tar` has no liblzma) | the SHA-256 of the 7z and of the DLL (the release's `MinGW64\dll\libusb-1.0.dll`, which needs no Visual C++ runtime), both in `pins.txt` | beside the engine in `{app}`, where the engine finds it (the installer takes the publish's whole top level) |
| `libusb-COPYING.txt` | libusb's licence text: the `COPYING` of the release's source tarball (`libusb-<version>.tar.bz2`, at the same version; the 7z holds none), unpacked with Windows' own `tar` (7-Zip's `7z.exe`, then `tar`, where that `tar` has no bz2lib) | the SHA-256 of the tarball and of the file, both in `pins.txt` | beside the engine in `{app}`, with `LICENSES.md` (as the DLL, from the publish's top level) |
| `avutil-61.dll`, `avcodec-63.dll`, `avformat-63.dll`, `swscale-10.dll` | `-FFmpegDir`: FFmpeg 9.0.2, decode-only, with dav1d inside `avcodec-63.dll`, built by `native/ffmpeg/build.sh --target windows-x64` from the sources `native\ffmpeg\pins.txt` pins (macOS, above, "FFmpeg"; `--verify` checks the build itself) | each DLL's PE header (PE32+, x86-64, a DLL) and version resource: its file major the one the engine loads (61, 63, 63, 10), its product version `pins.txt`'s `ffmpeg_version`, its original file name its own; a build isn't byte for byte reproducible, so no hash is pinned: each one's copy in `-Out\downloads\ffmpeg` is opened read only before its check and held until the compiler has finished, and it is held to the SHA-256 of the bytes checked, taken through that hold | beside the engine in `{app}`, where the engine loads them (`InfoPanel.Core/Images/Video/FFmpeg.cs`) |
| `ffmpeg-COPYING.txt`, `dav1d-COPYING.txt` | `-FFmpegDir`: the licence texts of FFmpeg's and dav1d's release sources | the SHA-256 of each, `ffmpeg_copying_sha256` and `dav1d_copying_sha256` in `pins.txt` | beside the engine in `{app}`, with `LICENSES.md` |
| `dotnet-runtime-<v>-win-x64.exe` | Microsoft, the version pinned in `build.ps1` | the SHA-512 pinned beside it (from Microsoft's release metadata) | not installed as a file: run `/install /quiet /norestart` only when no stable .NET 8 or later (x64) is installed |

Windows 11's `tar` reads both archives and needs nothing more; where it doesn't (Windows Server 2022's), `build.ps1` falls back to 7-Zip's `7z.exe`, found on the PATH or in `%ProgramFiles%\7-Zip` (on GitHub's Windows images already, not bundled), and stops if there is none.

A file whose hash isn't the pinned one, or an FFmpeg DLL that fails its checks, stops the build before anything is
published. Right before the compiler packs them, all of them (PawnIO's, libusb's and FFmpeg's in their final places in
the publish) are opened read only, so that nothing can change, replace or rename them, hashed again through that handle
and held until the compiler has finished: one replaced during the publish stops the build, and none can be replaced
while the compiler runs. `-PawnIoSetup`, `-LibusbArchive` (the 7z), `-LibusbSource` (the tarball) and
`-DotnetRuntime` take local copies instead of downloading them; they are checked the same way. To move to a new .NET
runtime, change `$Runtime` in `build.ps1` (its version, URL and SHA-512 from
`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`).

The engine itself is self-contained and needs no .NET. The sensor helper is framework-dependent and rolls forward to
any stable .NET 8 or later, so the installer adds Microsoft's runtime only on a PC with none (where 1.x, which shipped
self-contained, left none). It counts a folder under `shared\Microsoft.NETCore.App` only when its name is a stable
version (`8.0.11`, never a prerelease such as `11.0.0-preview.1`, which the helper's roll-forward doesn't pick) and it
holds `coreclr.dll` (`packaging/windows/dotnet-check.iss`). Without it, Read all sensors (LibreHardwareMonitor) reads nothing; HWiNFO still works.

`packaging/windows/InfoPanel.ico` is the program's icon (16 to 256 pixels, PNG entries), `<ApplicationIcon>` in
`InfoPanel.csproj`; `packaging/windows/make-icon.ps1` makes it from `Images/logo.png` (on Windows; the `.ico` is
committed, so it runs only when the logo changes).

### What it installs, and beside 1.x

| | InfoPanel 2 | InfoPanel 1.x |
|---|---|---|
| AppId (Programs and Features) | `{B63E31BF-4503-4A62-BB7A-D021C854BEEA}`, kept for every 2.0 release | `{761BE813-4A06-4646-B930-3E647446A307}` |
| Program folder | `C:\Program Files\InfoPanel 2`, always | `C:\Program Files\InfoPanel` |
| Start menu | `InfoPanel 2` (and Uninstall InfoPanel 2) | `InfoPanel` |
| Start when I sign in | the Run value `InfoPanel 2` | its task `\InfoPanel` (1.4.x deletes the Run value `InfoPanel` at every start) |
| Read all sensors | the tasks `\InfoPanel 2\Sensor helper <SID>` | none (it ran elevated) |
| Data | `%LOCALAPPDATA%\InfoPanel\v2` | `%LOCALAPPDATA%\InfoPanel` (2.0's 1.x import only reads it) |
| Setup file | `InfoPanel-<version>-win-x64-setup.exe` | `InfoPanelSetup.exe` |

The Task Scheduler can't hold a task and a folder of one name at its root: with 1.4.x's Run on startup on (its root task
`\InfoPanel`), a folder `\InfoPanel` can't be created, and a task made beside the folder hides it. So 2.0's folder is
`\InfoPanel 2`.

- **One folder**, `C:\Program Files\InfoPanel 2` (`{autopf}\InfoPanel 2`): no folder page, and an earlier install's
  folder isn't reused. A `/DIR` that resolves anywhere else (1.x's `C:\Program Files\InfoPanel`, a folder a user can
  write) is refused before anything is stopped or written (exit code 7).
- **Per machine**, administrator rights asked once (UAC). Read all sensors registers its SYSTEM task only for a helper
  under Program Files, whose files a user can't write. x64 Windows 10 or later only.
- The licence page shows `LICENSE`; `LICENSE`, `LICENSES.md`, `libusb-COPYING.txt`, `ffmpeg-COPYING.txt` and
  `dav1d-COPYING.txt` are installed beside the engine.
- The finish page's "Launch InfoPanel 2" starts the engine as the user who started Setup, not with Setup's administrator
  rights, and is offered only when that user's token is unelevated (integrity Medium). Setup started elevated (Run as
  administrator, an elevated prompt, UAC off) offers no launch, since it would start the engine elevated. A silent
  install (`/VERYSILENT`) launches nothing.
- The finish page's "Start InfoPanel 2 when I sign in", ticked, turns on the tray's Start when I sign in for the user
  who started Setup: `reg.exe`, run as that user, writes the Run value `InfoPanel 2`,
  `"C:\Program Files\InfoPanel 2\InfoPanel.exe"`, into that user's `HKCU` and clears its Task Manager switch, as the
  tray does (not through Setup's own `HKCU`, the administrator's when another user than the one who started it
  elevated it). Unticked, nothing changes. A silent install writes it too, unless `/STARTATSIGNIN=no`. Offered, as the
  launch, only when that user's token is unelevated: Setup started elevated (Run as administrator, maybe with another
  administrator's credentials) can't run anything as the user who started it, so it offers and writes nothing; then
  turn it on from the tray's Start when I sign in.
- **Over a running InfoPanel 2** (an update or a repair): Setup first stops this install's running sensor helper
  tasks (the helper runs as SYSTEM in session 0), then stops the engine as it stops at a sign-out (below). The Restart
  Manager then closes, without forcing, anything else holding a file (`CloseApplications=yes`), and Setup restarts
  nothing. An earlier 2.0's `sensor-helper\` and `pawnio\` are replaced whole, and the retired bundled plugin folder of earlier builds (`plugins\InfoPanel.Extras\`) is removed.
- **Stopping the engine** (Setup and the uninstaller): each of this install's engines on Setup's desktop gets
  `WM_ENDSESSION` at its hidden window (`InfoPanelMessageWindow`), which it takes as the session ending: its own orderly
  stop, which writes every unsaved change to `recovery.json`. Setup waits up to 20 s for it, then up to 5 s for its
  plugin hosts and helpers, and ends only those. An engine is never forced: one still running (in another user's
  session, or started without its notification area icon) stops Setup or the uninstall with a message to quit it and
  Retry; a silent run answers Cancel (Setup exits 7, the uninstaller 1) and changes nothing.
- Desktop shortcut: an optional task, on by default (as 1.x's).
- **The `infopanel:` link**, per machine (`[Registry]`, `HKA`: HKLM in this administrative install):
  `HKLM\Software\Classes\infopanel` with its default value `URL:InfoPanel` and an empty `URL Protocol`, `DefaultIcon`
  `"C:\Program Files\InfoPanel 2\InfoPanel.exe",0` and `shell\open\command`
  `"C:\Program Files\InfoPanel 2\InfoPanel.exe" "%1"`: the link is the engine's only argument, which it accepts only
  alone and never reads (desktop-shell.md, "Opening the editor"). 1.x registers no such scheme.

### Uninstalling

The uninstaller (Settings › Apps, or Uninstall InfoPanel 2 in the Start menu):

- stops this install's engines as above (only those under its program folder: 1.x's program is `InfoPanel.exe` too),
  or stops, changing nothing, while one is left;
- stops and removes every user's Read all sensors task that is this install's: in `\InfoPanel 2`, named
  `Sensor helper <SID>`, with one action, exactly this install's helper (its path resolved: `..` doesn't pass) with
  `--serve --user <SID>`; any other is left alone. Then the `\InfoPanel 2` folder once empty. Through the Task
  Scheduler: the helper isn't run, since a .NET program run elevated would take the user's environment;
- removes the Run value `InfoPanel 2` (and its Task Manager switch) from the uninstaller's own `HKCU` and from every
  user hive loaded under `HKEY_USERS` (each signed-in user's, so the user who started the uninstall too when another
  administrator's credentials elevated it: the uninstaller can run nothing as that user), each read and checked by the
  uninstaller itself: removed only when its program (the command line's first part, its path resolved; the arguments
  don't count) is this install's `InfoPanel.exe`. The value of a user who isn't signed in stays, starting a program
  that is gone;
- removes the program folder and the shortcuts, and the `infopanel:` link's key, `HKLM\Software\Classes\infopanel`
  (`uninsdeletekey`);
- **keeps** `%LOCALAPPDATA%\InfoPanel\v2` (profiles, settings, logs), and touches nothing of 1.x's.

### Unsigned

The beta's installer isn't signed: SmartScreen says "Windows protected your PC" (More info › Run anyway) and UAC shows
"Publisher: Unknown". Signing comes before the public release.

## Linux: the tarball and the .deb

```bash
packaging/linux/build.sh --version 2.0.0-beta.1 --allow-origin https://client.example --arch amd64 --out <a new or empty folder>
```

`--version` and `--allow-origin` are the macOS script's (`--check-args` only checks them and `--arch`); `--arch` is
Debian's name for the architecture, `amd64` or `arm64` (the runtimes `linux-x64` and `linux-arm64`): any Linux machine
builds either, since the publish takes the runtime's files from NuGet. On Linux with the .NET SDK `global.json` pins,
`dpkg-deb` and coreutils, as `packaging/linux/test.Dockerfile`'s image has them. On a Mac, run it in that image on a copy
of the repository without the projects' `bin/` and `obj/` (docs/agents/linux.md):

```bash
docker run --rm --user 1000:1000 -v <copy>:/src -v <nuget cache>:/nuget -v <out parent>:/out infopanel-linux-test \
  packaging/linux/build.sh --version 2.0.0-beta.1 --allow-origin https://client.example --arch arm64 --out /out/arm64
```

It writes:

- `<out>/publish/`: the self-contained Release publish for the runtime (`-p:Version`, `-p:InfoPanelAllowOrigin`, no
  debug symbols, with the bundled AI usage plugin in `plugins/InfoPanel.AiUsage/`), with every panel driver in `plugins/{folder}` as the macOS script publishes them;
- `<out>/files.sha256`: every file of the tarball's folder, hashed once after the checks below;
- `<out>/InfoPanel-<version>-linux-<arch>.tar.gz`: one folder, `InfoPanel-<version>-linux-<arch>/`, holding the publish,
  `LICENSE`, `LICENSES.md` and `linux/` (`70-infopanel.rules`, `70-infopanel-opt-in.rules`, `infopanel.desktop`,
  `infopanel-autostart.desktop` and the icon, `infopanel.png`, for an install by hand); run `InfoPanel` in it;
- `<out>/InfoPanel-<version>-linux-<arch>.deb`: the package `infopanel`, made from that tarball; both SHA-256s are
  printed last.

**The checks.** Before anything is packed, the publish must hold the executable `InfoPanel`, `InfoPanel.dll` and the
runtime's and Skia's libraries; every ELF file in it must be 64-bit and built for `--arch`; no Mach-O file, link or
special file, and none of another platform's folders (`sensor-helper/`, `mediaremote-adapter/`, `wwwroot/`). The two
rules files must hold exactly the lines the bundled panel drivers need, split as below: `packaging/linux/panel-matches.cs`
(run with `dotnet run`) reads each published driver's `panel.json` with the engine's own reader (`PanelPackages`: only
each `families[].match[]` entry's own members, `usb` when it names no transport; a package the engine would skip or not
run, a family id taken twice included, stops the build with the engine's reason, and so does a file the driver's own
`PanelManifest` refuses or reads other matches from), and BeadaPanel's built-in id is added; one line missing or one more in either file stops
the build (`build.sh --print-rules` prints the lines, `--check-rules` checks the files against the drivers in the source
without building). The tarball is unpacked again and checked file by file against `files.sha256`, and the `.deb` is
staged from that unpacked copy, each file at its place (`linux/`'s each to its own, a file there without one stopping
the build; every other under `/usr/lib/infopanel`); once `dpkg-deb` has built it, it is unpacked into a new folder, its
listing (types, modes, link targets) must equal what was staged, and every file in it, the relocated ones and the
copyright too, must have its hash in `files.sha256`, with no file more.

**The panels' access.** `70-infopanel.rules` gives the user signed in at the computer only devices that are a panel's
own: every id a bundled driver matches but the generic ones some panels share with other devices, the Linux
Foundation's vendor id `1d6b` (Linux's USB gadgets), `0525:a4a7` (Linux's serial gadget) and `1a86:5722` (a WCH serial
chip's id). A rule on these would give the user every device with them, such as a single-board computer's or a
microcontroller's serial console, and a driver's `panel.json` names a device only by its id, transport and interface,
nothing that tells a panel from those. So these panels need the owner's opt-in: the Turing Smart Screen 3.5" and the
XuanFang 3.5" (`1a86:5722`), and the serial Turing Smart Screen 2.1", 5" and 8.8" Rev 1.0 (`1d6b:0121`, `1d6b:0106`,
`0525:a4a7`); the Turing USB models, Lian Li, Thermalright, Thermaltake, JL, Jonsbo, VMAX and BeadaPanel need none.
The package installs their lines, inactive, as `/usr/share/doc/infopanel/70-infopanel-opt-in.rules` (the tarball has it
in `linux/`); to opt in, copy it into `/etc/udev/rules.d/` and replug the panel, or apply it at once:

```bash
sudo cp /usr/share/doc/infopanel/70-infopanel-opt-in.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger --action=change --subsystem-match=tty
```

Or, instead, add yourself to the `dialout` group (`sudo usermod -aG dialout $USER`, then sign out and in), which may use
every serial port. A rules file in `/etc/udev/rules.d/` is the computer owner's: removing or purging the package leaves
it.

**What the .deb installs.**

| Path | What |
|---|---|
| `/usr/lib/infopanel/` | the engine (the tarball's folder but `linux/`) |
| `/usr/bin/infopanel` | a link to `/usr/lib/infopanel/InfoPanel` |
| `/usr/lib/udev/rules.d/70-infopanel.rules` | the panels' access: `TAG+="uaccess"`, so the user signed in at the computer may use them (never a world-writable mode); USB device nodes for the `usb` transport, `hidraw` for `hid`, `tty` for `serial`; a panel's own ids only (above) |
| `/usr/share/applications/infopanel.desktop` | the menu entry, which also opens `infopanel:` links |
| `/etc/xdg/autostart/infopanel.desktop` | start at sign-in, **off** for every user (`Hidden=true`); a user turns it on with a copy in `~/.config/autostart/infopanel.desktop` without the `Hidden` line. Its one conffile |
| `/usr/share/pixmaps/infopanel.png` | the icon (`Images/logo.png`) |
| `/usr/share/doc/infopanel/copyright` | `LICENSE` |
| `/usr/share/doc/infopanel/70-infopanel-opt-in.rules` | the generic ids' rules, inactive there: a user copies them into `/etc/udev/rules.d/` (above) |

Its version is the release's with the prerelease after a `~` and the revision 1 (`2.0.0-beta.1` is `2.0.0~beta.1-1`), so
a release sorts after its betas. It depends on what the self-contained engine loads from the system (glibc, libstdc++,
ICU, OpenSSL, the CA certificates, libuuid, FreeType), `libfontconfig1` (Skia's fonts), `libusb-1.0-0` (panels) and
`tzdata` (the time zones .NET reads, for the clock and every zone a document names; ICU doesn't carry them), and
recommends `ffmpeg` (the preview's software H.264 encoder). After the install, `postinst` reloads udev's rules and
replays the USB, `hidraw` and `tty` devices, so a panel already plugged in is the user's; where udev doesn't run (a
container) it does nothing. Removing it removes every file but the conffile; purging removes that too. The user's data
folder (`~/.local/share/InfoPanel/v2`) is kept, as on Windows.

**The smoke test.** `packaging/linux/smoke.sh`, as root in a clean Ubuntu or Debian container, installs a `.deb` with
apt (without its recommends), starts the engine from the `PATH` as uid 1000 (made when the image has none, as Debian's),
headless on a data folder and a port of its own with `TZ=Europe/Berlin`, checks `GET /api/session`'s `engineVersion`
(and `engineBuild`, when given), that the clock's 24-hour reading is 1 or 2 hours past UTC's (the zone found in the
time zone data; without it .NET takes UTC) and a stop with exit code 0, then
removes the package (only the conffile left), purges it and removes what it pulled in, and compares the file system
and the packages with before: nothing that was there is gone, and nothing of InfoPanel's is left outside the user's
home (its data folder and fontconfig's cache stay there; apt's autoremove keeps what an installed package recommends,
such as `ca-certificates`, which apt itself recommends):

```bash
docker run --rm -v <out>:/pkg:ro -v <repository>/packaging/linux:/smoke:ro ubuntu:24.04 \
  bash /smoke/smoke.sh /pkg/InfoPanel-2.0.0-beta.1-linux-arm64.deb 2.0.0-beta.1
```

`debian:12` runs the same in place of `ubuntu:24.04`. amd64 runs the same with `--platform linux/amd64` on an Apple
silicon Mac (emulated). Docker proves the package, the
install and a headless start; only a Linux desktop proves the menu entry, the sign-in entry and the panels' access.

## Releases

GitHub Actions builds the downloads with the two scripts above: `.github/workflows/release.yml`, on GitHub's runners,
from this repository alone. Nothing is signed beyond the Mac's ad hoc signature, and nothing is published by itself.

**Versions.** A version is a SemVer 2 version with major 2. A build from source is `2.0.0-dev` (the `<Version>` in
`InfoPanel/InfoPanel.csproj`); a release passes its version to the scripts, which stamp it everywhere: the engine
(`GET /api/session`'s `engineVersion`), the app's `Info.plist`, the installer and the file names. Each release is a
tag of the bare version, with no `v` and no `+build`: `2.0.0-beta.1`, `2.0.0`. A `-dev` version is never a release's.
The engine also reports the commit it was built from (`engineBuild`), with `-dirty` when the checkout had changes.

**The web client's origin** (built in as `--allow-origin`'s default) is the repository variable
`INFOPANEL_ALLOW_ORIGIN` (Settings › Secrets and variables › Actions › Variables), never written in this repository.
Every run needs it: without it the first job fails with "Set the repository variable INFOPANEL_ALLOW_ORIGIN to the web
client's origin".

**Crash reports** ([crash reports](crash-reports.md)): `build.sh --crash-reports-dsn` and `build.ps1 -CrashReportsDsn`
take the address a build sends crash reports to (a Sentry DSN), built in as `InfoPanelCrashReportsDsn`. Unlike the
origin it is optional: a build without it sends none.

**The jobs.**

1. `prepare` (Linux) decides the mode and the version (`.github/scripts/release-version.sh`) and checks the arguments
   with `build.sh --check-args`. A pushed tag is a **release**: refused unless it is a bare 2.x version, not `-dev`,
   on a commit of `2.0.x`. Anything else is a **dry run**, stamped with the version given or
   `2.0.0-dev.ci.<run number>`.
2. `mac` (`macos-15`, Apple silicon) installs FFmpeg's build tools (`brew install meson ninja pkgconf`) and runs
   `packaging/macos/build.sh` into the runner's temporary folder, outside the checkout, so the build isn't `-dirty`; it
   keeps the release tarballs the app's libraries were built from (FFmpeg's, dav1d's and libusb's) as the artifact
   `sources`. `ffmpeg-windows` (`ubuntu-24.04`) builds `native/ffmpeg/windows-cross.Dockerfile`'s container and runs
   `native/ffmpeg/build.sh --target windows-x64` in it, then `--verify`, and keeps the `bundle` folder as the artifact
   `ffmpeg-windows` for a day. `windows` (`windows-2022`, with Inno Setup 6, installed when the image lacks it) waits
   for it and runs `packaging\windows\build.ps1` the same way, with that folder as `-FFmpegDir`.
3. Each smoke-runs what it built: the engine on a temporary data folder with `--offline --no-tray --no-windows
   --no-browser --no-plugins --no-panels`, then `GET /api/session` must report the version as `engineVersion` and the
   checked-out commit, without `-dirty`, as `engineBuild`, and list `media.video` among the scene format's
   capabilities and `rtsp` among the media providers (the bundled FFmpeg loaded). Each keeps its file as the run's
   artifact (`mac`, `windows`) for 14 days.
4. `release` runs for a release only, and is the only job that may write to the repository. It checks each source
   tarball against its pin (`native/ffmpeg/pins.txt`, `native/libusb/pins.txt`) and writes `SHA256SUMS` for the two
   downloads and the three tarballs. It never reuses a release: if one exists for the tag, a draft or a published one,
   it fails and writes nothing. It also fails unless the tag on GitHub still points at the commit the run built.
   Otherwise it makes a **draft** pre-release from the existing tag, with the disk image, the setup, the tarballs
   (the source of the bundled libraries, offered beside them as their licences ask) and `SHA256SUMS` attached in the
   same call (`gh release create --draft --verify-tag`: it never makes a tag) and `packaging/release-notes.md` as its
   notes.

Every 2.x release is marked pre-release on GitHub for now, `2.0.0` included: 1.x's update check offers every release
that isn't one, so a 2.x release marked otherwise would be offered to 1.x users.

**A dry run** starts by itself on each push to `2.0.x` that changes the workflow, `packaging/` or `native/`, or by hand
with `gh workflow run`, which works once the workflow has run on `2.0.x`. The Actions page's Run workflow button may
not show while the repository's default branch is `1.4.x`, which has no such workflow:

```bash
gh workflow run release.yml --ref 2.0.x                          # stamped 2.0.0-dev.ci.<run number>
gh workflow run release.yml --ref 2.0.x -f version=2.0.0-beta.1  # a release-like build to try; not a release
gh run download <run id> --dir <a new folder>                    # its artifacts: the disk image and the setup
```

**A release:**

1. Tag the `2.0.x` commit to release with its version and push the tag: `git tag -s 2.0.0-beta.1 -m "InfoPanel
   2.0.0-beta.1"`, then `git push origin 2.0.0-beta.1`.
2. The run for the tag builds both downloads and makes the draft release "InfoPanel 2.0.0-beta.1".
3. On the Releases page, edit the draft's notes (replace `<version>` in them), try its files, then publish it.

A tag the workflow refuses makes no release: delete the tag, fix what the run's first job says and tag again. To rebuild
a draft, delete the draft on the Releases page, then re-run the tag's workflow; while any release for the tag exists, a
run changes nothing. A published release is never rebuilt: release a new version instead.
