# The desktop shell: the menu-bar item

The engine has no window of its own. On macOS its face is an item in the menu bar: the logo's hexagon, which macOS tints
for light and dark menu bars. Windows (a tray icon by the clock) and Linux (a status icon where the desktop shows one)
come later; there the engine runs as before, and the shell is off.

## The menu

Click the hexagon. The menu is built afresh each time it opens:

1. "InfoPanel 2.0.0 is running" (grey, not clickable).
2. **Open Editor**: opens the editor in the browser, already signed in (below, "Opening the editor").
3. **Quit InfoPanel** (⌘Q while the menu is open).

The item's tooltip is the engine's address ("InfoPanel — http://127.0.0.1:5296"), so two engines on one Mac can be told
apart.

## Quit

- No profile has unsaved changes: the engine stops at once (desktop windows close, sensors stop, `runtime.json` goes,
  exit 0).
- Some do: an alert asks first. Its title is "Quit InfoPanel?", its text names up to three of them, then how many more
  ("“A”, “B”, “C” and 2 more have unsaved changes. InfoPanel keeps them for its next start, unsaved."): unsaved changes
  are kept across a restart (data-folder.md, "Unsaved changes"). Its buttons: **Open Editor** (Return; the engine keeps
  running and the editor opens), **Cancel** (Escape) and **Quit Without Saving**. The alert never holds up the engine:
  stopped while it is open (a system quit, `SIGTERM`), the engine closes it as Cancel and stops.
- The system quits the app (logout, shutdown, Activity Monitor's Quit): the engine stops the same way without asking,
  since a logout can't wait on a browser. AppKit is told to wait (`NSTerminateLater`) until the engine has stopped, then
  the quit goes on.
- `SIGTERM` and Ctrl-C in a terminal stop it as before.

## Opening the editor

The engine stays headless: it never opens a browser by itself, however it was started. It opens the editor only when
asked:

- the menu's **Open Editor** (and the Quit alert's);
- a **second launch**: starting InfoPanel again on a data folder an engine holds. The new process asks the running engine,
  over the folder's local channel (as a `launch` companion, which may ask nothing else), to open the editor, prints
  "InfoPanel is already running; opened the editor", and exits 3. With `--no-browser` it asks nothing.

**What opens.** With no web client served here (no `--web-root`, no `wwwroot` beside the engine) and exactly one
cross origin allowed, the engine opens that origin's pairing link
(`{origin}/#engine=http://127.0.0.1:{port}&token={token}`): a web client on another origin, which the link pairs with
this engine. Otherwise it opens the sign-in link (`/auth/{token}`) to the web client it serves. The console prints only
the links that lead somewhere: the sign-in link only when a web client is served here.

**Which browser.** A pairing link goes to Chrome when it is installed, else to the default browser; a sign-in link goes
to the default browser. A browser started directly that exits with an error within 2 seconds didn't start (a Chrome that
can't run), and the next one is tried.

| OS | Chrome | The default browser |
|---|---|---|
| macOS | `open -b com.google.Chrome <file>` (fails when no app has that id) | `open -a <app> <file>`, the app Launch Services opens `http` links with |
| Windows | `chrome.exe` as App Paths registers it | the `http` association's command (the user's choice), the file for its `%1` |
| Linux | `google-chrome-stable` or `google-chrome` on the `PATH` | the `Exec` line of the desktop entry `xdg-settings get default-web-browser` names, the file for its `%u` |

The default browser is named in the command because the file is HTML: opened by its association it would go to
whatever opens `.html` files, which may be a text editor. Only when the default browser can't be told does the file go
by its association (`open <file>`, the shell, `xdg-open <file>`).

**No token in any process's arguments.** Other users can read a process's arguments on some systems, so the link
never goes there. The engine writes it into a private redirect file, a small HTML page that forwards the browser to it
(an immediate refresh, no referrer), and passes only the file's path:

- the file is `{run}/{hash16}.open-{random}.html`, 0600, in the run folder (`{LocalApplicationData}/InfoPanel/run`,
  0700, this user's; the local channel's folder), named after the data folder's channel. On Windows, where a new file
  takes its folder's access list, the run folder and each file are created with a protected list that grants only this
  user, and an existing run folder that lets anyone else in (other than SYSTEM and the Administrators group) or whose
  list is inherited (so a later change to a folder above would reach it) gets that list when it is this user's, and is
  read again before it is used;
- a run folder that isn't this user's alone is never used: nothing opens, and the console says to open the printed link;
- each file is removed 2 minutes after it is written (time for the browser to read it, a cold start included), the
  rest when the engine stops (a removal that failed is tried again then: Windows can't remove a file a browser still
  holds open), and a crash's leftovers at the next start;
- a browser that can't be started is logged without the link: the error on the console, its type in the log file.

**A release build's web client.** A release build carries the origin of the web client it opens: built with
`-p:InfoPanelAllowOrigin=https://client.example`, the origin is recorded as the assembly metadata
`InfoPanel.AllowOrigin` (`InfoPanel/WebClientOrigin.targets`) and is the default for `--allow-origin` when no such flag
is given. It is checked as the flag is, so a bad value stops start-up with exit 2 (a release pipeline's smoke run
catches it). Any `--allow-origin` replaces it (development: `--allow-origin <origin>` with no `--web-root` behaves as a
release build). A build from source carries none and behaves as before. A launch record leaves it out, so an engine an
AI app starts takes its own build's.

## Flags

- `--no-tray`: no menu-bar item. Every test that starts the engine as a child process passes it; an engine an AI app
  starts gets it from the folder's launch record when the last start had it.
- `--no-windows`: no desktop windows. The menu-bar item stays.
- `--no-browser`: a second launch doesn't ask the running engine to open the editor.
- `--allow-origin <origin>`: a web client on another origin; with no web root and one such origin, the editor that
  opens. Replaces the origin a release build carries.

## How it is built

- One model for every OS (`InfoPanel/Shell/`): `ShellMenu.Build` makes the menu from the engine's state each time the
  user opens it; `IShellPlatform` is the OS's side (show and remove the icon, hand out menus, report clicks, ask a
  `ShellPrompt`: a title, a text, buttons, the default and the cancel one); `ShellService` (a hosted service) shows the
  icon once the server listens, so Open Editor always has a port, removes it when the engine stops, and runs each
  choice off the UI thread. `NullShellPlatform` is used everywhere the shell is off.
- macOS (`Shell/MacOS/MacShellPlatform.cs`): an `NSStatusItem` through libobjc, as desktop windows are (no binding
  package): the glyph drawn by `ShellGlyph` at 2× as a template image, an `NSMenu` whose delegate rebuilds it
  (`menuNeedsUpdate:`), an `NSAlert` run modally for prompts (the app brought forward first, since it has no Dock icon),
  and the application's delegate for the system's quit. Prompts run one at a time from a run-loop timer, never inside
  a queued main-queue callback, which would hold the queue until the alert is answered: the engine's stop still runs
  while one is open, and closes it as Cancel (`MacShellTests` stops an engine with it open; the host setting
  `Engine__ShellTestPrompt=true` asks it at start, in the `Test` host environment only: `ASPNETCORE_ENVIRONMENT=Test`).
- Threads: AppKit runs on the process main thread. `DesktopHost` starts its loop when desktop windows **or** the
  menu-bar item are on, and runs the host on a worker. Off the main thread (every test host) neither is created.

## Tests

- `InfoPanel.Tests/Shell/`: the menu model and the glyph; `ShellService` on the engine host with a fake platform
  (`FakeShellPlatform`) and a recording browser launcher: when the icon comes and goes, Open Editor, Quit with and
  without unsaved profiles, the prompt's names, a system quit; the choice of what runs (`DesktopHost.Choose`); the
  flag and the launch record.
- `InfoPanel.Tests/Hosting/`: the link the engine opens (`WebClientLinkTests`: the built-in origin, the link's choice,
  no browser at start, the logs), the launcher and the redirect files (`BrowserLauncherTests`: the file's mode,
  contents and removal, no token in any argument, Chrome and the fallback per OS), a second launch as a child process
  (`SecondLaunchTests`), and the build property (`WebClientOriginTargetTests`).
- `INFOPANEL_SHELL_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacShellTests`: the built engine as a
  child process adds a real menu-bar item with `--no-windows`, and removes it and exits 0 on `SIGTERM` (skipped
  otherwise).
- The menu and the alert themselves are checked by hand.
