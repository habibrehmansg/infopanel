# Crash reports

A release build of the engine sends crash reports to [Sentry](https://sentry.io), so failures on users' computers can be
fixed. Reporting is on by default, the setting `telemetry.crashReports` turns it off (spec/docs/api.md, "Crash
reports"; a client shows it as a switch), and nothing that names the user, their computer, their files, their network or
their profiles is sent. A build from source sends nothing.

## Which builds send

Only a build given a crash-report address: `-p:InfoPanelCrashReportsDsn=<a Sentry DSN>` records it as the assembly
metadata `InfoPanel.CrashReportsDsn` (`InfoPanel/CrashReportsDsn.targets`). The packaging scripts take it as
`packaging/macos/build.sh --crash-reports-dsn` and `packaging\windows\build.ps1 -CrashReportsDsn`
([packaging](packaging.md#releases)); without it a build carries none. A build with no address, or an engine
started with `--offline`, never starts the reporting library, whatever the setting says: so does every build from source, every test engine and every
development engine.

## What is sent

| When | What |
|---|---|
| An exception nothing caught, on any thread, or a failed task nothing observed | The exception (level fatal or error): its type and its stack (function, module, line and column, the file's path after the build root), never its message, inner exceptions the same, the release (`infopanel-engine@{version}+{build}`), the environment (`production`, `beta`, `ci` or `development`, from the version), the operating system's name and version, the .NET runtime's, the CPU architecture, the tags `process: engine` and `os`, and the last 50 breadcrumbs. Sent within 2 s before the process ends |
| An error the engine logs with an exception | As above, at level error, with the log entry's message template as its message (`"Profile {Name} failed"`), never the text with its values filled in: at most one report per exception type and top InfoPanel frame an hour, and 20 a run. Never one logged by the plugin hosts' code, nor a plugin host's own error: their text is a plugin's |
| A plugin host's process ends without the engine asking | One warning, "A plugin host exited unexpectedly", with its exit code, how long it ran (in buckets) and how many times it was restarted. Never the plugin's name, path or id |
| The engine started and found the previous run's `runtime.json` (it ended without shutting down: a native crash, a forced quit or a power loss) | One event, "The previous run ended unexpectedly", and that run's session counted as crashed. No log lines and no system crash report |

**Breadcrumbs** are the engine's log entries at Information and above, as their message template only
(`"Loaded {Count} profiles from {Path}"`), never the text with its values filled in, with the entry's source, level and
exception type. Debug entries, console output and requests are never breadcrumbs.

**Sessions** (Sentry's Release Health) count crash-free users by a random id made once per install, the first time
reports start, and kept in the data folder's `crash-reports.json` (`{"installId": "…"}`). It is never derived from the
computer, the user or the hardware. Off, no id is made. A clean stop ends the session as exited (else the next start
would count it as ended abnormally); a stop on an error the engine can't go on from (a port it can't listen on, say)
ends it as crashed; a failed task nothing observed marks it, without ending it.

## Scrubbing

No free text leaves the engine. Every event is rebuilt from an allowlist before it is sent
(`InfoPanel/CrashReports/CrashScrubber.cs`):

- an exception is its type and its stack frames (function, module, line and column; the file's path after the build
  root), never its message, and its inner exceptions the same;
- the event's message, and each breadcrumb's, is kept only when it is the engine's own text: one of its fixed messages
  (the two above) or a log entry's message template, never the text with its values filled in;
- the level, release, environment, platform, time, id and SDK; the tags above, each only with its fixed values; the
  contexts `os` and `runtime` (name, version), `app` (version) and `device` (architecture); the breadcrumbs' source,
  level, type and exception type; the user as the install's id.

Everything else is dropped: the request, extra data, the server name, loaded modules, threads and any other context or
tag. The user's and computer's names are never collected (`SendDefaultPii` stays off).

What text remains (templates, type and function names, file names) then goes through a second net, in this order:

1. a URL's user information (`rtsp://user:password@`) and the value after `Bearer`, `Basic` (in any case), `token=`,
   `password:` and the like, whatever its characters;
2. whole absolute paths (Windows, UNC, POSIX and `~`-relative), kept as `<path>` with the file's extension; a path's
   later words still belong to it, folder names with spaces included, up to one that holds a separator or is a file name
   with an extension, so `C:\Users\Alice\Old Family Files\Secret Budget.json` goes whole;
3. the values the engine knows, each replaced by a label (in a template and a file name; never in a type or function
   name, which are the engine's code), read as the report is made, every one read during the run kept, and a read that
   takes too long kept when it ends: the user's and computer's names, the home, data and 1.x folders, profile names,
   plugin packages' folders and names and their plugins' ids and names, saved panels' serials and 1.x device ids, and
   sensor names and their groups of 3 characters or more; a value shorter than 3 characters (a short user name) only as
   a whole word;
4. e-mail addresses; host names under `ts.net`, `local` and `lan`; MAC addresses; IPv4 and IPv6 addresses (loopback kept
   as `loopback`); runs of 20 or more letters, digits, `-` and `_` (tokens, serial numbers), unless they hold fewer than 5
   different characters or read as words (no digit, CamelCase, or lower-case parts of at most 15 letters:
   `InvalidOperationException` stays, a lower-case word of over 15 letters goes) and aren't hexadecimal
   (`CafeBabeDeadBeefCafeBabe` goes);
5. quoted values (`'…'`, `"…"`, `“…”`).

What one step replaces, a later one never changes (a profile called `token` doesn't undo a marked value). It is then cut
at 300 characters. The Sentry project's own scrubbing and its "don't store IP addresses" setting are a further net.

## Turning it off

Nothing queued is ever sent unless reporting was on when it was queued and has been on since. This rests on a
**consent epoch**: a random id (32 hex digits) that the settings write turning reporting on makes, in that same write,
and the write turning it off removes (`telemetry.crashReportsEpoch` in `settings.json`; never in the API, and no change
can set it). Reporting on since the install, with none yet, gets one when the library first starts. The library keeps
its queue of reports not yet sent and its session in `crash-reports/{epoch}/`, and a start reads and writes only its own
epoch's folder. So turned off and on again, it starts on a new, empty folder: nothing queued before is ever read again,
whether or not the old folder could be deleted or even read, and whatever the stopped library still writes into it.

`PATCH /api/settings` with `{"telemetry": {"crashReports": false}}` stops the reporting library at once, without
sending what it holds: its sending is retired first (a report being sent is abandoned, and none is started after), then
it stops. Turned on again, it starts again, on the new epoch's folder. With the setting off at start, the library is
never started. Every other epoch's folder is deleted at start and at each change of the settings, best effort: one that
can't be deleted stays, harmless, and is tried again later (at most 100 entries a time). A build without a crash-report
address (a build from source, or `--offline`) never starts the library, but deletes old folders all the same; its switch
turned off removes the epoch, so a later release build starts on a new one. If the settings can't keep an epoch (a
settings file that can't be read or written), a run's reports go to a folder of its own that no later start reads. A link
or junction as `crash-reports/` or as the epoch's folder refuses reports for the run (one warning), never followed.

Stopping the library, turned off or with the engine, takes at most 3 s (sending what is queued, at a stop, at most 2):
past that, its sending is retired, so nothing it still holds is ever sent, and the engine goes on. Errors before the
engine has read its settings (bad arguments, a data folder in use) are never sent. A failure of the reports themselves
(a folder they can't write, the library's own) never stops the engine: they stay off, with one warning in the log, and
the setting stays as stored.

## The data folder

`crash-reports.json` holds the install's id. `crash-reports/{epoch}/` holds, for the consent in effect, the reporting
library's queue of reports not yet sent (sent at a later start while reporting stays on), its session, and its own copy
of the id; any other folder in `crash-reports/` is an earlier consent's, never read, and deleted when it can be.
`settings.json` keeps the epoch as `telemetry.crashReportsEpoch`.

## Trying it

`INFOPANEL_CRASH_TEST`, read only by a build with a crash-report address while reporting is on: `unhandled` throws on a
background thread 10 s after start (a fatal report; the engine ends); `fail-fast` ends the process 10 s after start
(reported at the next start as a run that ended unexpectedly). Use a data folder of its own (`--data-dir`) and a free
port.
