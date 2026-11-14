**This is a pre-release** of InfoPanel 2. Try it, and tell us what breaks.

## Install on a Mac (Apple silicon, macOS 15 or later)

1. Download `InfoPanel-<version>-mac-arm64.dmg` and open it.
2. Drag InfoPanel to Applications.
3. Open InfoPanel from Applications. The first time, macOS says it can't verify the app: choose Done, then go to
   System Settings › Privacy & Security and choose **Open Anyway** for InfoPanel. This build isn't notarised yet, so
   macOS asks once.
4. InfoPanel runs in the menu bar (no Dock icon). Its menu's **Open Editor** opens the editor.

## Install on Windows (x64, Windows 10 or later)

1. Download `InfoPanel-<version>-win-x64-setup.exe` and run it.
2. The setup isn't signed yet: if Windows says it protected your PC, choose **More info**, then **Run anyway**.
3. It installs as **InfoPanel 2**, beside InfoPanel 1.x, which it leaves alone: 1.x's program, settings and profiles
   stay as they are. The first time you open the editor, it opens Import with your 1.x profiles.
4. InfoPanel 2 runs in the notification area. Its menu's **Open editor** opens the editor, and **Start when I sign in**
   starts it with Windows.

## Source of the bundled libraries

Both downloads carry FFmpeg's decode-only libraries (with dav1d) and libusb. Their source, the release tarballs at the
pinned versions, is attached below: `ffmpeg-<version>.tar.xz`, `dav1d-<version>.tar.xz` and `libusb-<version>.tar.bz2`.
`LICENSES.md`, beside the program, lists their terms.

## Check a download

`SHA256SUMS` lists each file's SHA-256, the source tarballs' too. Compare it with the file you downloaded:

- macOS: `shasum -a 256 InfoPanel-<version>-mac-arm64.dmg`
- Windows (PowerShell): `Get-FileHash InfoPanel-<version>-win-x64-setup.exe -Algorithm SHA256`
