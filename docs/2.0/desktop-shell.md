# The desktop shell: the menu-bar item and the tray icon

The engine has no window of its own. On macOS its face is an item in the menu bar: the logo's hexagon, which macOS tints
for light and dark menu bars. On Windows it is an icon in the notification area, by the clock (below, "Windows"). Linux
(a status icon where the desktop shows one) comes later; there the engine runs as before, and the shell is off.

## The menu

Click the hexagon. The menu is built afresh from the engine's state each time it opens:

1. The header, "InfoPanel" (bold, not greyed, not clickable), and under it "Running · 2.0.0" in the secondary colour.
   Under that, the update line (below, "Updates"): **Update Available: InfoPanel 2.0.1…** while a newer version is
   known, which opens its release page in the browser; otherwise **Check for Updates…**. A development build has
   neither.
2. — Who is signed in, one row: while the engine holds a credential for the configured service and knows its holder, a
   round initial (the first letter or digit of the name, else of the email, white on a grey disc; no picture is
   downloaded), the holder's name, and beneath it the email in the secondary colour; the email alone when the holder
   has no name. "Not signed in" in the secondary colour with "Sign In…" beneath in the label colour (what the row does,
   so it doesn't read as disabled) while it holds none, or the service refused the one it holds.
   No row (nor its separator) while it holds one whose holder isn't known yet. Clicking the row opens the editor at its
   sign-in settings, where this computer is connected (the link's `then`, below). The engine asks the configured
   service itself, with the credential (below, "Who is signed in"). The name and the email are attributed titles (the
   second line, and the item's image the initial), so the row stays a plain menu item, with no arrow: it opens a page,
   not a submenu.
3. — **Open Editor**: opens the editor in the browser, already signed in (below, "Opening the editor"). **Edit from
   Another Computer…**, under it: the same, at the editor's sign-in settings (the page the row of who is signed in
   opens), where another computer is set up to edit this one (the link's `then`, below).
4. — **Show on Desktop ▸**, only while desktop windows are on (not `--no-windows`): each profile that can be opened, in
   the library's order, with a check mark when its desktop window is on; "No Profiles Yet" (grey) when there are none;
   past 20, the first 20 and **More in the Editor…**, which opens the editor. Choosing a profile turns its window on or
   off as the editor's Show on desktop does: one `profile` commit of `window.enabled` (the rest of `window` as the
   document has it) at the revision it read, read again on a conflict, one undo step; saved at once at that revision
   when the profile had no unsaved changes, otherwise left unsaved with them. Each line asks for the state opposite the
   check mark it showed, so a choice made after another client changed the window changes nothing. The profile lines
   are the ones the engine last read, off the menu's thread, at start and after each change of the profile list or of
   a listed profile, so opening the menu never waits for a save's disk writes; a change made a moment before can show
   at the next opening.
   **Click-Through ▸**, right under it, while desktop windows are on: each profile Show on Desktop lists whose window
   is on, in the same order and by the same name, with a check mark while clicks pass through its window to whatever
   is beneath it (`window.clickThrough`); "No Windows Shown" (grey) when none is on. Choosing a line lets clicks
   through or has the window take them again, as the editor's Click-through does: one `profile` commit of
   `window.clickThrough`, otherwise as Show on Desktop (the revision read, a conflict read again, one undo step, saved
   at once only when the profile had no unsaved changes, each line asking for the state opposite its check). A window
   that lets clicks through can't be clicked, dragged or resized, so this menu, the editor's drawer and Show on Desktop
   are the ways back; a window whose profile is past the first 20 is reached from the editor.
5. — **Show Data Folder in Finder** and **Open Logs Folder**: the engine's own folders, as the editor's buttons open
   them (`POST /api/folders/{folder}/open`). **Copy Engine Details**: what a bug report needs when the editor can't
   connect, with the labels of the editor's own details, in a fenced block: the engine's version, protocol and
   system, its build, when it started, the OS version, the architecture, its address and its data folder (the home
   folder as `~`, its path matched as the home folder's volume compares names: on macOS as the volume says, asked once,
   in any case when it can't be asked; in any case on Windows; with case elsewhere). Never the token, a
   link, a profile, a reading or who is signed in.
6. — **Quit InfoPanel** (⌘Q while the menu is open).

**Start at Login**, in a group of its own above Quit, only when the engine runs from the app bundle (`InfoPanel.app`;
never from `dotnet run` or a plain publish): the app itself as a login item through ServiceManagement
(`SMAppService.mainAppService`, `Shell/MacOS/MacLoginItem.cs`, the framework loaded by path), so System Settings ›
General › Login Items lists it and can turn it off. Read each time the menu opens: ticked while on; choosing it turns it
on or off; "Start at Login (approve in System Settings…)" while the user has to allow it there, which opens that pane;
greyed when it can't be read; greyed as "Move InfoPanel to Applications to start at login" while macOS runs the app
from App Translocation's read-only copy (`/private/var/folders/…/AppTranslocation/…`). The engine handles the line
itself (it isn't the model's); `MacLoginItemTests` cover its states with a fake status source, and no test registers
anything. The bundle needs macOS 15 (`LSMinimumSystemVersion`), so `SMAppService` (macOS 13) is always there.

Every line has a stable id (`ShellMenu`'s constants; a profile's line `desktop:show:{id}` or `desktop:hide:{id}`), so
another platform maps its own words to the same lines. Updates come later. Starting the engine never opens the editor:
it opens only when asked (below, "Opening the editor").

The item's tooltip is the engine's address ("InfoPanel — http://127.0.0.1:5296"), so two engines on one Mac can be told
apart.

## Who is signed in

The engine learns the credential's holder from the configured service, with the credential itself: `GET
{service address}/svc/v1/holder`, sent as every request to the service is (only to the address in effect, the credential
as a bearer token, no redirect followed, with `--offline` only to a service on this computer), answered
`{"name": string | null, "email": string}`. The row shows the name with the email beneath, or the email alone when the
name is null or empty, each without control characters; the name past 40 characters its first 39 and "…", the email
(beneath the name or in its place) past 32 its first 15, "…" and its last 16, so its domain stays.

- **When it asks:** while the menu-bar item shows, at start, whenever the credential changes (a client provides one,
  even the same again, or removes it; the service address changes), and again every 6 hours after an answer while it
  holds one. After a failed ask (the service unreachable, a 5xx, a malformed answer) it asks again sooner, as the
  weather source does: after 1 minute, then 2, 4, 8… doubling with each failure in a row, or after the failure's
  `Retry-After` when that is longer, never later than 6 hours; and at once when the computer's network changes (an
  address comes or goes: a network joined, a sleep's end), so an engine started before its network or its service
  shows the row within a minute or so of both being up. A network change during an ask is kept for its end: asked
  again at once if it fails. Once per failed ask: further changes while that ask is due or under way, or after it
  fails, ask nothing more until the next ask (a burst of changes, or a network that keeps changing, asks once). An
  answer, a change of the credential or the address, or such a network change starts the retries again at 1
  minute; what an ask sent before a change of the credential or the address comes to changes no retry. One request at
  a time, each bounded at 15 s, its answer read
  to at most 4 KiB. Should the asking itself fail, the next change asks again. Opening the menu never waits for the
  credential's store: it reads what the engine last learned.
- **What it keeps:** the name and the email, in memory only, for the credential they were asked with. An answer that
  comes after that credential was replaced or removed is never shown for another. A failure keeps what is known for that
  same credential, else shows no row; a 401 refuses the credential (as for every request), and the row says "Not signed
  in" until a client provides another; the name and the email go together.
- **Never** stored, logged (the log file, the recent entries, `/api/logs`) or copied by Copy Engine Details.

## Updates

The engine checks the engine repository's public releases on GitHub for a newer version (`UpdateCheck`, with the list
read by `ReleaseFeed`, both in `InfoPanel.Core/Updates/`):

- **What is sent, and to whom:** one `GET https://api.github.com/repos/habibrehmansg/infopanel/releases?per_page=30`,
  with `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28` and `User-Agent: InfoPanel/{version}`,
  and nothing else: no token, cookie, machine name, install id or trace header. GitHub sees the computer's address and
  the engine's version, once a day. `If-None-Match` carries the last answer's `ETag` (kept in memory only); its 304
  keeps that answer.
- **How:** `https` only; no redirect followed (a moved list fails, and is logged); 15 s per request; the list read to
  at most 1 MiB; the system's proxy as .NET reads it.
- **When:** 30 s after the item shows, then once a day has passed since the last answer by the wall clock, looked at
  every hour, so a computer asleep overnight checks within the hour it wakes; an hour after a failure; when GitHub
  limits the address (403 or 429), at its `Retry-After` or `X-RateLimit-Reset`, between an hour and a day. One check at
  a time; none while the item doesn't show (`--no-tray`, every test engine).
- **Never** for a development build (a version whose pre-release starts with `dev`: `2.0.0-dev`, a source build;
  `2.0.0-dev.ci.12`, a dry run's), nor for a version that isn't SemVer 2. With `--offline`, only a feed on this
  computer. `INFOPANEL_UPDATE_FEED` replaces the address for development (an `https` URL, or `http` on this computer),
  logged at start.
- **What is offered:** the highest version above the engine's that its channel takes. The channel follows the
  engine's own version, not GitHub's pre-release flag: a pre-release build (`2.0.0-beta.1`) takes pre-releases and
  releases; a release build (`2.0.0`) only versions without a `-`. Drafts, tags that aren't SemVer 2 versions and 1.x's
  versions are never offered. The release page is built from the tag
  (`https://github.com/habibrehmansg/infopanel/releases/tag/{version}`), never taken from the list.
- **The menu:** "Update Available: InfoPanel {version}…" while one is known (a version past 32 characters is shortened
  in its middle on the line, never in the prompt); otherwise "Check for Updates…", which checks at once (at most once
  every 10 s) and answers in an alert:
  - "InfoPanel 2.0.1 is available", "You have InfoPanel 2.0.0. Download opens its release page.": **Download** (Return)
    opens the release page, **Later** (Escape);
  - "You're up to date", "InfoPanel 2.0.0 is the newest version.": **OK**;
  - "Couldn't check for updates", "Check your connection, then try again.": **OK**.
- **The log:** "InfoPanel {version} is available: {page}" once per version; a failure once a run as Information, then
  at Debug; "Not checking for updates: {reason}" once when the item shows on an engine that doesn't check.

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
  the quit goes on. Before the item shows (the engine still starting), the quit goes on at once, as it would without
  the app delegate.
- `SIGTERM` and Ctrl-C in a terminal stop it as before.

## Windows

The app's coloured logo in the notification area, as Windows apps show theirs, the same on a light taskbar and a dark
one, at the small-icon size for the taskbar's display scale (redrawn when the scale changes). Its tooltip is "InfoPanel 2.0.0". Each engine has one icon, removed when it
stops; Explorer restarting (or a taskbar that didn't exist yet at a sign-in start) adds it again (`TaskbarCreated`).

- **Click** it (or press Enter on it): the editor opens, as the menu's Open editor. The second click of a double-click
  doesn't open a second tab.
- **Right-click** it (or the menu key): the menu, built afresh each time, in Windows' sentence case (Windows menus stay
  light in dark mode):
  1. "InfoPanel · Running · 2.0.0", grey: the header and the status in one line (a Windows menu has no header);
     under it **Update available: InfoPanel 2.0.1…** or **Check for updates…** (above, "Updates");
  2. — who is signed in, one line (a Windows menu line has no second line, nor an image here): "‹name› (‹email›)", the
     email alone when the holder has no name, or "Not signed in · Sign in…"; it opens the editor at its sign-in
     settings, as on macOS; none while the holder isn't known;
  3. — **Open editor** (bold: what a click does) · **Edit from another computer…**;
  4. — **Show on desktop ▸**, a submenu, while desktop windows are on: each profile, checked when its window is on;
     "No profiles yet" (grey) when there are none; past 20, **More in the editor…**; **Click-through ▸** under it:
     each shown window, checked while clicks pass through it, or "No windows shown" (grey);
  5. — **Open data folder** · **Open logs folder** · **Copy engine details** (on the clipboard as plain text);
  6. — **Start when I sign in** ✓;
  7. — **Exit**.

  The lines and what each does are the model's menu (`ShellMenu.Build`, the same as macOS's) in Windows' words
  (`WinShellText`, by each line's id): Open Editor is "Open editor", Edit from Another Computer… "Edit from another
  computer…", Show on Desktop "Show on desktop", Click-Through "Click-through", Show Data
  Folder in Finder "Open data folder", Quit InfoPanel "Exit", and so on; an item it doesn't know keeps the model's
  words.
- **Start when I sign in** is the value `InfoPanel 2` under the user's Run key
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
- **opening the app again** on macOS (double-clicking InfoPanel in Finder or the Dock while it runs): no second process
  starts; the running app is told (the app delegate's `applicationShouldHandleReopen:hasVisibleWindows:`) and runs Open
  Editor. Only with the menu-bar item on (not `--no-tray`), since the delegate is the item's target.
- an **`infopanel:` link** (`infopanel://open`; a web page's, or `open infopanel://open` in Terminal, `start
  infopanel://open` on Windows): the same as starting InfoPanel again. It starts InfoPanel if it isn't running, and opens
  the editor at the engine's own configured web client with a new one-time code, exactly as Open Editor does. **Nothing
  is read from the link**: not parsed, logged or passed on, so it can't choose a page, profile, port, folder, origin or
  flag, and nothing goes back to the page that opened it. The browser asks the user before it opens the link.
  - macOS: the app bundle declares the scheme (`CFBundleURLTypes` in `packaging/macos/Info.plist`); the app delegate's
    `application:openURLs:` never reads its URLs and raises the same request as opening the app again. The delegate is
    set on the main thread before the AppKit loop runs, so a link that starts the app (delivered while AppKit finishes
    launching, before the engine's services start) is kept and opens the editor once the engine listens. Until the item
    shows, a system quit goes on at once, as without a delegate.
  - Windows: the installer registers the scheme per machine, its command `"{app}\InfoPanel.exe" "%1"`. The engine
    accepts an argument starting with `infopanel:` (any case) only as its **only** argument, and drops it unread: a link
    with anything else (a flag a crafted link added by breaking out of its quotes, a second link) is refused whole as an
    unknown argument, exit 2, before anything is touched. On a held data folder it is a second launch (exit 3); a link
    launch that starts the engine opens the editor once, when the server listens ("InfoPanel was opened from a link:
    opening the editor").

**What opens.** With no web client served here (no `--web-root`, no `wwwroot` beside the engine) and exactly one
cross origin allowed, the engine opens that origin's code link (`{origin}/#engine=http://127.0.0.1:{port}&code={code}`):
a web client on another origin, which exchanges the code for the run's token and a lasting pairing
([api.md, "Pairing"](../../spec/docs/api.md#pairing)). The code is new for each open, usable once within 2 minutes, and
the session token is never in the link (`EngineRuntime.EditorLinkFor`, `EditorOpener`). Otherwise it opens the sign-in
link (`/auth/{token}`) to the web client it serves.

**At a page.** Edit from Another Computer… and the row of who is signed in open the same link with the client's page
to open at, its sign-in settings, where this computer is connected and another computer is set up to edit this one
(`#/settings/account`, `EditorRoutes.AccountSettings`), URL-encoded as `then`: the code link's last member
(`{origin}/#engine=…&code=…&then=%23%2Fsettings%2Faccount`), or the sign-in link's query (`/auth/{token}?then=…`), whose
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
  The menu's lines are `WinTrayMenu.Lines` (ids, the default item, the platform's own Start when I sign in above
  Exit), and `WinTrayMenu.Find` the line a chosen command names; the Run value is `WinStartup` over a registry seam.
- Threads: AppKit runs on the process main thread. `DesktopHost` starts its loop when desktop windows **or** the
  menu-bar item are on, and runs the host on a worker. Off the main thread (every test host) neither is created. On
  Windows both run on the engine's UI thread, whatever thread starts the engine; test engines pass `--no-tray`.

## Tests

- `InfoPanel.Tests/Shell/`: the menu model (each state: signed in, not, unknown; no profiles, one, 20,
  21; checks; desktop windows off; the update line, its long version; the lines' ids), Copy Engine Details' text (`EngineDetailsTests`) and the glyph;
  `ShellService` on the engine host with a fake platform (`FakeShellPlatform`, which keeps what is copied), a fake
  desktop, the engine's recording folder opener and a recording browser launcher: when the icon comes and goes, Open
  Editor, Quit with and without unsaved profiles, the prompt's names, a system quit (`ShellServiceTests`); Show on
  Desktop's commit and save, the menu opening at once with the profiles last read while a write holds the workspace,
  the folders, More in the Editor and the details (`ShellActionTests`); the signed-in line
  from the service's answer on the engine host (`ShellSignedInTests`); when the engine asks, what it shows, the late
  answer, the 401, the failures and the bounds (`ServiceHolderTests`); the choice of what runs
  (`DesktopHost.Choose`); the flag. The update line on the engine host over a feed answered in memory: Check for
  Updates…'s three answers, Download, Later, Update Available opening the release page, a development build with no
  line (`ShellUpdateTests`).
- `InfoPanel.Core.Tests/Updates/`: the list and the channel (`ReleaseFeedTests`: the channel table, drafts, 1.x, tags
  that aren't SemVer 2, SemVer 2's order, the page from the tag, the 1 MiB bound) and the check (`UpdateCheckTests`,
  over a fake handler or a loopback server, with a fake clock: the headers in memory and on the wire, no redirect, a
  development build, `--offline`, 304 and the `ETag`, GitHub's limits, the hour after a failure, a day of wall-clock
  time, a check asked for).
- `InfoPanel.Tests/Hosting/`: the link the engine opens (`WebClientLinkTests`: the built-in origin, the link's choice,
  no browser at start, the logs), the launcher and the redirect files (`BrowserLauncherTests`: the file's mode,
  contents and removal, no token in any argument, Chrome and the fallback per OS), a second launch as a child process
  (`SecondLaunchTests`: an `infopanel:` link alone on a held folder, exit 3; a link with `--fresh`, exit 2 and nothing
  wiped), and the build property (`WebClientOriginTargetTests`). The command line's link rule is in `EngineOptionsTests`;
  the early open (an open asked before the server listens, once; a link launch that starts the engine) in
  `ShellEarlyOpenTests`; the app delegate's `application:openURLs:` and its quit before the item shows in
  `MacAppDelegateTests` (macOS); the plist template's scheme and the installer's key in `LinkRegistrationTests`.
- `InfoPanel.Tests/Shell/Windows/` (any OS): the menu in Windows' words with its commands and default item, Show on
  desktop's submenu (its checks, none, 20 and more, a choice in it running the model's id), Start when
  I sign in (the Run value and Task Manager's switch, through a fake registry), a click, a double-click, a right click,
  `TaskbarCreated`, Exit's dialog and the update lines and prompts in Windows' words, prompts one at a time, the
  icon's removal, the update notification's balloon (its cuts, its click, none without the icon) (`WinTrayTests`,
  through `FakeTrayNative`); the session's end, its hold over a scripted message queue (a second end
  dispatched inside it, one sent to it with a message queued behind, a hold that starts late, a queue that never
  empties) and a scale change (`WinTraySystemTests`); the icon, the coloured logo at each size whatever the theme,
  and the Win32 structures' sizes and offsets (`WinShellPlatformTests`, `WinCommonControlsTests`). On Windows the
  registry itself, under a key of the test's own (`WinStartupTests`), and `TaskDialogIndirect` inside the engine's
  Common Controls 6 in the test host, whose manifest isn't the engine's (`WinCommonControlsTests`), and Copy engine
  details' clipboard outside a signed-in desktop (over SSH; skipped in one, whose clipboard is the user's).
- `INFOPANEL_SHELL_TESTS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~MacShellTests`: the built engine as a
  child process adds a real menu-bar item with `--no-windows`, and removes it and exits 0 on `SIGTERM` (skipped
  otherwise). On Windows, in a signed-in session, `…~WinShellPlatformTests` adds a real tray icon, adds it again on
  `TaskbarCreated` and removes it.
- The menus and the alert or dialog themselves are checked by hand.

## Notifications

`IShellPlatform.Notify(ShellNotification, clicked)` shows a `ShellNotification` (a title and a text) through the
system's own notifications; a click on it runs `clicked`. A platform without them keeps the default, which does
nothing (`NullShellPlatform`, Linux). Each platform keeps only the newest notification's `clicked`: the next `Notify`
replaces it. The update check calls it, at most once per version (below, "The update notification").

- Windows: a balloon on the tray icon (`Shell_NotifyIconW` with `NIM_MODIFY` and `NIF_INFO`), which Windows 10 and 11
  show as a toast with no package or AppUserModelID: `NIIF_USER` (the icon's own image) and `NIIF_RESPECT_QUIET_TIME`
  (held in the user's first hour on a new install, and by Focus Assist), the title cut to 63 characters and the text to
  255, with an ellipsis and never inside a surrogate pair. A click on it comes back as `NIN_BALLOONUSERCLICK` on the
  icon's callback message and runs `clicked`. Nothing is shown while the icon isn't there. `WinTray.Notify` is the
  logic (`WinTrayTests`, through `FakeTrayNative`); `WinTrayNative.ShowBalloon` the call.
- macOS: `UNUserNotificationCenter` (`Shell/MacOS/MacNotifications.cs`, the framework loaded by path, through the same
  libobjc binding). Only from the app bundle: without a bundle identifier (`dotnet run`, the tests) the centre raises
  an Objective-C exception that ends the process, so it is never asked. Permission is asked the first time there is
  something to show, never at launch: the system asks the user once, as a notification of its own, and later asks
  answer at once (Allow and Don't Allow are then the user's switch in System Settings, Notifications). One
  notification at a time, under one identifier, so a newer one replaces an older. Its delegate's
  `didReceiveNotificationResponse` runs `clicked` off the centre's thread, and `willPresentNotification` shows it
  while the app is active (an open prompt). The centre's calls take blocks: `ObjC.GlobalBlock` makes a block that
  captures nothing (the Blocks ABI's global block, whose copy and release do nothing), each made once and kept for the
  process; a block the system hands in is called through `ObjC.BlockInvoke`. The system refuses an app bundle that
  runs from a temporary folder (`/private/tmp`: "Failed to find or validate client"), so a hand check runs the app
  from anywhere else. A centre that can't be set up (the framework, a class or the delegate missing) is logged once as
  a warning and never thrown out of `Notify`; it isn't tried again in that run, so nothing is shown (`CentreSetup`).
  Once the engine stops (`Remove`, which calls `MacNotifications.Stop`), the notification waiting for permission is
  dropped and none is asked for: a permission the system answers after the stop submits nothing.
  Tests (`MacNotificationTests`): without a bundle the centre is never asked (with the check
  removed, the test host ends with `bundleProxyForCurrentProcess is nil`), a centre whose setup fails is logged once
  and not tried again, a permission answered after the stop submits nothing (the setup's seams standing in for the
  centre), and Foundation calls a global block with its arguments. A real notification needs the bundle and the user's permission, so it is a hand check, not a test.

### The update notification

`ShellService` follows the update check while the icon shows (`UpdateCheck.Changed`, subscribed just before the check
starts and dropped in `StopAsync`). When the version offered changes and is above every version notified before, by
SemVer 2 precedence, it first records the version in `updates.json` ([data folder](data-folder.md#the-update-notified),
`UpdateNotices`), then calls `Notify` with "InfoPanel {version} is available", "Click to see what's new and
download it." only when that was written. So each version notifies at most once, ever, across restarts, and a lower
version after it (a newer release withdrawn) never does. A failed write shows nothing for that version in that run, and
a crash between the write and `Notify` loses that notice: the menu's update line still says it. The record means
attempted, not seen: notifications turned off, or a toast Windows drops, still count. A development build, whose check
is off, and an engine that is up to date notify nothing. Check for Updates… answers in its own prompt only: the version
it finds is recorded, without a notification, so the daily check doesn't notify it later (the check's trigger,
`UpdateTrigger.Menu`, comes with `Changed`). A click on the notification opens the release page of the version it
named (the address captured when it was shown); as the platform keeps only the newest notification's click, that is
the newest version notified. It runs off the platform's thread, through the same path as the menu's line, and does
nothing else. Tests (`ShellUpdateTests`, through `FakeShellPlatform`, whose `Notify` records each notification and
keeps only the newest click, as the platforms do): one notification per new version across a restart on the same data
folder, none for a development build, none when up to date, none when `updates.json` can't be written, Check for
Updates… answering in its prompt only while recording the version, and the click opening the newest notified
version's page and nothing else.
