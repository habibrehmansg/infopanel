# The desktop shell: the menu-bar item and the tray icon

The engine has no window of its own. On macOS its face is an item in the menu bar: the logo's hexagon, which macOS tints
for light and dark menu bars. On Windows it is an icon in the notification area, by the clock (below, "Windows"). Linux
(a status icon where the desktop shows one) comes later; there the engine runs as before, and the shell is off.

## The menu

Click the hexagon. The menu is built afresh from the engine's state each time it opens:

1. "InfoPanel 2.0.0 is running" (grey, not clickable), and under it, grey, who is signed in: "Signed in as ‹name›" while
   the engine holds a credential for the configured service and knows its holder; "Not signed in" while it holds none,
   or the service refused the one it holds; nothing while it holds one whose holder isn't known yet. The engine asks
   the configured service itself, with the credential (below, "Who is signed in").
2. **Open Editor**: opens the editor in the browser, already signed in (below, "Opening the editor"). **Edit from
   Another Computer…**, under it: the same, at the editor's engine settings, where another computer is set up to edit
   this one (the link's `then`, below).
3. **Show on Desktop ▸**, only while desktop windows are on (not `--no-windows`): each profile that can be opened, in
   the library's order, with a check mark when its desktop window is on; "No Profiles Yet" (grey) when there are none;
   past 20, the first 20 and **More in the Editor…**, which opens the editor. Choosing a profile turns its window on or
   off as the editor's Show on desktop does: one `profile` commit of `window.enabled` (the rest of `window` as the
   document has it) at the revision it read, read again on a conflict, one undo step; saved at once at that revision
   when the profile had no unsaved changes, otherwise left unsaved with them. Each line asks for the state opposite the
   check mark it showed, so a choice made after another client changed the window changes nothing.
4. **Show Data Folder in Finder** and **Open Logs Folder**: the engine's own folders, as the editor's buttons open
   them (`POST /api/folders/{folder}/open`). **Copy Engine Details**: what a bug report needs when the editor can't
   connect, with the labels of the editor's own details, in a fenced block: the engine's version, protocol and
   system, its build, when it started, the OS version, the architecture, its address and its data folder (the home
   folder as `~`, its path matched as the home folder's volume compares names: on macOS as the volume says, asked once,
   in any case when it can't be asked; in any case on Windows; with case elsewhere). Never the token, a
   link, a profile, a reading or who is signed in.
5. **Quit InfoPanel** (⌘Q while the menu is open).

Every line has a stable id (`ShellMenu`'s constants; a profile's line `desktop:show:{id}` or `desktop:hide:{id}`), so
another platform maps its own words to the same lines. Updates come later. Starting the engine never opens the editor:
it opens only when asked (below, "Opening the editor").

The item's tooltip is the engine's address ("InfoPanel — http://127.0.0.1:5296"), so two engines on one Mac can be told
apart.

## Who is signed in

The engine learns the credential's holder from the configured service, with the credential itself: `GET
{service address}/svc/v1/holder`, sent as every request to the service is (only to the address in effect, the credential
as a bearer token, no redirect followed, with `--offline` only to a service on this computer), answered
`{"name": string | null, "email": string}`. The line shows the name, else the email, without control characters, and
past 40 characters its first 39 and "…".

- **When it asks:** while the menu-bar item shows, at start, whenever the credential changes (a client provides one,
  even the same again, or removes it; the service address changes), and again every 6 hours while it holds one, never
  more often nor later, whatever a `Retry-After` asks. One request at a time, each bounded at 15 s, its answer read to
  at most 4 KiB. Should the asking itself fail, the next change asks again. Opening the menu never waits for the
  credential's store: it reads what the engine last learned.
- **What it keeps:** the name, in memory only, for the credential it was asked with. An answer that comes after that
  credential was replaced or removed is never shown for another. A failure (the service unreachable, a 5xx, a malformed
  answer) keeps the name known for that same credential, else shows no line; a 401 refuses the credential (as for every
  request), and the line says "Not signed in" until a client provides another.
- **Never** stored, logged (the log file, the recent entries, `/api/logs`) or copied by Copy Engine Details.

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

## Windows

The app's coloured logo in the notification area, as Windows apps show theirs, the same on a light taskbar and a dark
one, at the small-icon size for the taskbar's display scale (redrawn when the scale changes). Its tooltip is "InfoPanel 2.0.0". Each engine has one icon, removed when it
stops; Explorer restarting (or a taskbar that didn't exist yet at a sign-in start) adds it again (`TaskbarCreated`).

- **Click** it (or press Enter on it): the editor opens, as the menu's Open editor. The second click of a double-click
  doesn't open a second tab.
- **Right-click** it (or the menu key): the menu, built afresh each time, in Windows' sentence case (Windows menus stay
  light in dark mode):
  1. "InfoPanel 2.0.0 is running" and who is signed in ("Signed in as ‹name›" or "Not signed in"), both grey;
  2. **Open editor** (bold: what a click does) · **Edit from another computer…**;
  3. — **Show on desktop ▸**, a submenu, while desktop windows are on: each profile, checked when its window is on;
     "No profiles yet" (grey) when there are none; past 20, **More in the editor…**;
  4. — **Open data folder** · **Open logs folder** · **Copy engine details** (on the clipboard as plain text);
  5. — **Start when I sign in** ✓;
  6. — **Exit**.

  The lines and what each does are the model's menu (`ShellMenu.Build`, the same as macOS's) in Windows' words
  (`WinShellText`, by each line's id): Open Editor is "Open editor", Edit from Another Computer… "Edit from another
  computer…", Show on Desktop "Show on desktop", Show Data
  Folder in Finder "Open data folder", Quit InfoPanel "Exit", and so on; an item it doesn't know keeps the model's
  words.
- **Start when I sign in** is the value `InfoPanel` under the user's Run key
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), the engine's own program quoted (under the `dotnet` host:
  the host and the engine's assembly). It is read each time the menu opens, as Task Manager reads it (its Startup apps
  switch, under `…\Explorer\StartupApproved\Run`, can turn it off), so the menu shows what Task Manager shows. Ticking it
  writes the value and clears that switch; unticking removes both. The engine never runs as administrator, so the Run
  value, not a scheduled task, starts it.
- **Exit** is Quit: with unsaved profiles a dialog (a task dialog) asks first: "Exit InfoPanel?", the same text, and
  **Open editor** (Enter), **Exit without saving** and **Cancel** (Escape, or closing it). The engine's stop closes it as
  Cancel.
- **Signing out or shutting down** stops the engine the same way without asking, as macOS's system quit: Windows' end
  of the session (`WM_ENDSESSION`) is held until the engine has stopped, at most 4 s from the first (Windows offers to
  end an app that takes 5): the engine is asked to stop once, and another end, even one that arrives while the first is
  held, or a queue of messages that never empties, doesn't extend the wait. Console shutdown events don't reach a
  process that has loaded user32, so the icon's window hears it.
- Not shown when the process's window station isn't interactive (a service, a shell started over SSH).

## Opening the editor

The engine stays headless: it never opens a browser by itself, however it was started. It opens the editor only when
asked:

- the menu's **Open Editor** (and the Quit alert's), and its **Edit from Another Computer…**, at a page;
- a **second launch**: starting InfoPanel again on a data folder an engine holds. The new process asks the running engine,
  over the folder's local channel (as a `launch` companion, which may ask nothing else), to open the editor, prints
  "InfoPanel is already running; opened the editor", and exits 3. With `--no-browser` it asks nothing. When nothing
  answers on the channel, the engine holding the folder is stopping (it closes its channel before it lets go of the
  folder, which it keeps through its clean-up) or starting: the new process waits for the folder, up to 5 s, and starts
  once it is free; still held then, it exits 3. So a quick relaunch during a shutdown starts the engine again.

**What opens.** With no web client served here (no `--web-root`, no `wwwroot` beside the engine) and exactly one
cross origin allowed, the engine opens that origin's code link (`{origin}/#engine=http://127.0.0.1:{port}&code={code}`):
a web client on another origin, which exchanges the code for the run's token and a lasting pairing
([api.md, "Pairing"](../../spec/docs/api.md#pairing)). The code is new for each open, usable once within 2 minutes, and
the session token is never in the link (`EngineRuntime.EditorLinkFor`, `EditorOpener`). Otherwise it opens the sign-in
link (`/auth/{token}`) to the web client it serves.

**At a page.** Edit from Another Computer… opens the same link with the client's page to open at, its engine settings
(`#/settings/engine`, `EditorRoutes`), URL-encoded as `then`: the code link's last member
(`{origin}/#engine=…&code=…&then=%23%2Fsettings%2Fengine`), or the sign-in link's query (`/auth/{token}?then=…`), whose
redirect hands it on after the token (`/#token=…&then=…`; [api.md, "Signing in"](../../spec/docs/api.md#signing-in)).
The page is a constant the engine knows, never a caller's: the redirect carries only that one, as the engine spells it,
and leaves any other out. The client opens at it once the code is exchanged (after the reload that follows) or the token
taken, and keeps neither the link nor `then` in its address; a client that doesn't know the member ignores it.

The console prints only the links that lead somewhere: the sign-in
link only when a web client is served here, and each allowed origin's pairing link, which carries the token
(`{origin}/#engine=http://127.0.0.1:{port}&token={token}`).

**Which browser.** A code link goes to Chrome when it is installed, else to the default browser; a sign-in link goes
to the default browser. A browser started directly that exits with an error within 2 seconds didn't start (a Chrome that
can't run), and the next one is tried.

| OS | Chrome | The default browser |
|---|---|---|
| macOS | `open -b com.google.Chrome <file>` (fails when no app has that id) | `open -a <app> <file>`, the app Launch Services opens `http` links with |
| Windows | `chrome.exe` as App Paths registers it | the `http` association's command (the user's choice), the file for its `%1` (unquoted after Chrome's and Edge's `--single-argument`, which takes the rest of the line as it is) |
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
release build). A build from source carries none and behaves as before.

## Flags

- `--no-tray`: no menu-bar item or tray icon. Every test that starts the engine as a child process passes it, and so
  does every test engine in process (`EngineFactory`).
- `--no-windows`: no desktop windows. The menu-bar item or tray icon stays.
- `--no-browser`: a second launch doesn't ask the running engine to open the editor.
- `--allow-origin <origin>`: a web client on another origin; with no web root and one such origin, the editor that
  opens, through its code link. Replaces the origin a release build carries.

## How it is built

- One model for every OS (`InfoPanel/Shell/`): `ShellMenu.Build` makes the menu from a `ShellState` (the version, the
  credential and its holder's name, the profiles with their windows, whether desktop windows are on) each time the
  user opens it: lines with an id, a label, a kind (action, status, separator, submenu), a check mark and a submenu's
  lines; `IShellPlatform` is the OS's side (show and remove the icon, hand out menus, report clicks, ask a
  `ShellPrompt`: a title, a text, buttons, the default and the cancel one; put text on the clipboard, a default that
  does nothing); `ShellService` (a hosted service) shows the icon once the server listens, so Open Editor always has a
  port, removes it when the engine stops, and runs each choice off the UI thread. `NullShellPlatform` is used
  everywhere the shell is off.
- macOS (`Shell/MacOS/MacShellPlatform.cs`): an `NSStatusItem` through libobjc, as desktop windows are (no binding
  package): the glyph drawn by `ShellGlyph` at 2× as a template image, an `NSMenu` whose delegate rebuilds it
  (`menuNeedsUpdate:`) with its submenus and check marks (`setState:`), an `NSAlert` run modally for prompts (the app
  brought forward first, since it has no Dock icon), the general `NSPasteboard` for Copy Engine Details, and the
  application's delegate for the system's quit. Prompts run one at a time from a run-loop timer, never inside
  a queued main-queue callback, which would hold the queue until the alert is answered: the engine's stop still runs
  while one is open, and closes it as Cancel (`MacShellTests` stops an engine with it open; the host setting
  `Engine__ShellTestPrompt=true` asks it at start, in the `Test` host environment only: `ASPNETCORE_ENVIRONMENT=Test`).
- Windows (`Shell/Windows/`): `WinShellPlatform` on the engine's UI thread (`WinUiThread`, the one desktop windows
  use), whose hidden top-level window owns the icon and hears Explorer's broadcasts through a message hook (a
  message-only window wouldn't). `WinTray` is the logic with no Win32 in it (clicks, the menu and its commands, Start
  when I sign in, `TaskbarCreated`, the scale, prompts one at a time); `WinTrayNative` the Win32 side:
  `Shell_NotifyIconW` with version 4 callbacks, the coloured logo (`WinTrayLogo`: `Images/logo.png`, embedded, scaled
  down through mipmaps) as an icon at the taskbar's DPI, an `HMENU` shown with `TrackPopupMenuEx`, and `TaskDialogIndirect`, which needs
  Common Controls 6: the engine's application manifest (`InfoPanel/app.manifest`) asks for it, and the build copies it
  into `InfoPanel.exe`; under the `dotnet` host (`dotnet.exe` has its own manifest) `WinCommonControls` activates a
  context made from the same manifest, which the compiler embeds in `InfoPanel.dll`, around the dialog.
  The menu's lines are `WinTrayMenu.Flatten` (ids, the default item, the platform's own Start when I sign in above
  Exit); the Run value is `WinStartup` over a registry seam.
- Threads: AppKit runs on the process main thread. `DesktopHost` starts its loop when desktop windows **or** the
  menu-bar item are on, and runs the host on a worker. Off the main thread (every test host) neither is created. On
  Windows both run on the engine's UI thread, whatever thread starts the engine; test engines pass `--no-tray`.

## Tests

- `InfoPanel.Tests/Shell/`: the menu model (each state: signed in, not, unknown; no profiles, one, 20,
  21; checks; desktop windows off; the lines' ids), Copy Engine Details' text (`EngineDetailsTests`) and the glyph;
  `ShellService` on the engine host with a fake platform (`FakeShellPlatform`, which keeps what is copied), a fake
  desktop, the engine's recording folder opener and a recording browser launcher: when the icon comes and goes, Open
  Editor, Quit with and without unsaved profiles, the prompt's names, a system quit (`ShellServiceTests`); Show on
  Desktop's commit and save, the folders, More in the Editor and the details (`ShellActionTests`); the signed-in line
  from the service's answer on the engine host (`ShellSignedInTests`); when the engine asks, what it shows, the late
  answer, the 401, the failures and the bounds (`ServiceHolderTests`); the choice of what runs
  (`DesktopHost.Choose`); the flag.
- `InfoPanel.Tests/Hosting/`: the link the engine opens (`WebClientLinkTests`: the built-in origin, the link's choice,
  no browser at start, the logs), the launcher and the redirect files (`BrowserLauncherTests`: the file's mode,
  contents and removal, no token in any argument, Chrome and the fallback per OS), a second launch as a child process
  (`SecondLaunchTests`), and the build property (`WebClientOriginTargetTests`).
- `InfoPanel.Tests/Shell/Windows/` (any OS): the menu in Windows' words with its commands and default item, Show on
  desktop's submenu (its checks, none, 20 and more, a choice in it running the model's id), Start when
  I sign in (the Run value and Task Manager's switch, through a fake registry), a click, a double-click, a right click,
  `TaskbarCreated`, the theme, Exit's dialog in Windows' words, prompts one at a time, the icon's removal
  (`WinTrayTests`, through `FakeTrayNative`); the session's end, its hold over a scripted message queue (a second end
  dispatched inside it, one sent to it with a message queued behind, a hold that starts late, a queue that never
  empties) and a scale change (`WinTraySystemTests`); the glyph per
  theme and the Win32 structures' sizes and offsets (`WinShellPlatformTests`, `WinCommonControlsTests`). On Windows the
  registry itself, under a key of the test's own (`WinStartupTests`), and `TaskDialogIndirect` inside the engine's
  Common Controls 6 in the test host, whose manifest isn't the engine's (`WinCommonControlsTests`), and Copy engine
  details' clipboard outside a signed-in desktop (over SSH; skipped in one, whose clipboard is the user's).
- `INFOPANEL_SHELL_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacShellTests`: the built engine as a
  child process adds a real menu-bar item with `--no-windows`, and removes it and exits 0 on `SIGTERM` (skipped
  otherwise). On Windows, in a signed-in session, `…~WinShellPlatformTests` adds a real tray icon, adds it again on
  `TaskbarCreated` and removes it.
- The menus and the alert or dialog themselves are checked by hand.
