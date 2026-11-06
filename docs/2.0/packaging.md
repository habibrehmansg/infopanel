# Packaging

How a release's downloads are built from this repository. Each platform has one script under `packaging/` that turns a
self-contained publish of the engine into the download. The scripts name no web client: the version and the web
client's origin are arguments, and the origin is built in as `--allow-origin`'s default (`InfoPanelAllowOrigin`, see
the security model in `CLAUDE.md`), so the engine opens that web client from Open Editor without any flag.

The downloads carry the engine only: no web client (`wwwroot`) and no FFmpeg (video files and cameras need FFmpeg's
libraries beside the engine, [video.md](video.md)).

## macOS: the app and its disk image

```bash
packaging/macos/build.sh --version 2.0.0-beta.1 --allow-origin https://client.example --out <a new or empty folder>
```

`--version` is a SemVer (`2.0.0`, `2.0.0-beta.1`, `2.0.0+build.1`); `--allow-origin` is what the engine accepts as an origin:
https, a host and an optional port, with no user info, path, query or fragment (`--check-args` only checks both).

On a Mac with the .NET SDK `global.json` pins; `codesign`, `hdiutil`, `plutil` and `ditto` are macOS's own. It writes:

- `<out>/publish/`: the self-contained `osx-arm64` Release publish (`-p:Version`, `-p:InfoPanelAllowOrigin`, no debug
  symbols);
- `<out>/dmg/InfoPanel.app`: the app;
- `<out>/InfoPanel-<version>-mac-arm64.dmg`: the disk image (`hdiutil create -format UDZO`, an HFS+ volume named
  "InfoPanel <version>") holding the app, a link to `/Applications`, `LICENSE` and `LICENSES.md`; its SHA-256 is printed
  last.

**The app.** `Info.plist` is `packaging/macos/Info.plist` with the version set: `CFBundleShortVersionString` its
major.minor.patch (`2.0.0`), `CFBundleVersion` the whole of it. The bundle id is `net.infopanel.InfoPanel`; `LSUIElement` makes it a
menu-bar app with no Dock icon; `LSMinimumSystemVersion` is 15.0, the oldest macOS .NET 10 supports (Microsoft's
[.NET 10 supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md): macOS 15, 26
and 27). The icon is `packaging/macos/AppIcon.icns`, made from `Images/logo.png` by `packaging/macos/make-icon.sh`
(`sips` and `iconutil`; run it when the logo changes and commit its output).

**Its layout.** `codesign` takes every file in `Contents/MacOS` for code: a managed `.dll`, a `.json` or a folder there
(the satellite assemblies, `plugins/InfoPanel.Extras`) makes it refuse to sign the bundle. So the publish goes to
`Contents/Resources/engine`, its Mach-O files (the executable, the .NET runtime's and SkiaSharp's libraries) move to
`Contents/MacOS`, and each side links to the other's entries with relative symlinks. The executable finds its libraries
and managed files beside it; .NET takes the app's folder from `InfoPanel.dll`'s real path, `Contents/Resources/engine`,
and finds the libraries there through the links, and the bundled plugins in `plugins/` beside it. A Mach-O file below
the publish's top level stops the script: this layout would seal it as a resource rather than sign it as code.

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
