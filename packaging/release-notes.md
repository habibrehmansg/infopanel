**This is a pre-release** of InfoPanel 2.0. Try it, and tell us what breaks.

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
3. **InfoPanel 2.0 replaces InfoPanel 1.x.** If 1.x is installed, the setup says so before it starts, then closes 1.x,
   uninstalls it and installs 2.0 in `C:\Program Files\InfoPanel`. Save any work in 1.x first. Your 1.x profiles and
   settings stay on your PC: 2.0 only reads them. The first time you open the editor, it opens Import with your 1.x
   profiles and brings them over as copies. Plugins you added to 1.x aren't brought over.
4. InfoPanel runs in the notification area. Its menu's **Open editor** opens the editor, and **Start when I sign in**
   starts it with Windows. 1.x ran as administrator; 2.0 doesn't. For the sensors that need it, turn on **Read all
   sensors** in Settings (one Windows prompt).
5. **Going back to 1.x:** uninstall InfoPanel in Settings › Apps first, then install 1.x from its release. 1.x finds
   its profiles as you left them; what you made or changed in 2.0 stays with 2.0. Installing 1.x without uninstalling
   2.0 first leaves two InfoPanels sharing one Start menu entry, and 1.x switches off 2.0's start when you sign in.

## Source of the bundled libraries

Both downloads carry FFmpeg's decode-only libraries (with dav1d) and libusb. Their source, the release tarballs at the
pinned versions, is attached below: `ffmpeg-<version>.tar.xz`, `dav1d-<version>.tar.xz` and `libusb-<version>.tar.bz2`.
`LICENSES.md`, beside the program, lists their terms.

## Check a download

`SHA256SUMS` lists each file's SHA-256, the source tarballs' too. Compare it with the file you downloaded:

- macOS: `shasum -a 256 InfoPanel-<version>-mac-arm64.dmg`
- Windows (PowerShell): `Get-FileHash InfoPanel-<version>-win-x64-setup.exe -Algorithm SHA256`
