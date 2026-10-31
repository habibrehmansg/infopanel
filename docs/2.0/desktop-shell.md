# The desktop shell: the menu-bar item

The engine has no window of its own. On macOS its face is an item in the menu bar: the logo's hexagon, which macOS tints
for light and dark menu bars. Windows (a tray icon by the clock) and Linux (a status icon where the desktop shows one)
come later; there the engine runs as before, and the shell is off.

## The menu

Click the hexagon. The menu is built afresh each time it opens:

1. "InfoPanel 2.0.0 is running" (grey, not clickable).
2. **Open Editor**: opens the editor in the browser, already signed in (the sign-in link, handed to the OS's opener; the
   link never reaches the log file).
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

## Flags

- `--no-tray`: no menu-bar item. Every test that starts the engine as a child process passes it; an engine an AI app
  starts gets it from the folder's launch record when the last start had it.
- `--no-windows`: no desktop windows. The menu-bar item stays.

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
- `INFOPANEL_SHELL_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacShellTests`: the built engine as a
  child process adds a real menu-bar item with `--no-windows`, and removes it and exits 0 on `SIGTERM` (skipped
  otherwise).
- The menu and the alert themselves are checked by hand.
