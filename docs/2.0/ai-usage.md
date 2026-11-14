# AI usage

AI usage (`ai-usage`, package `InfoPanel.AiUsage`) is a plugin that comes with the engine: how close Claude Code and Codex are to their five-hour and weekly limits, when those reset and how old the numbers are, as readings a panel can show. It is an ordinary plugin ([plugins](plugins.md)), built against the SDK like any other and bundled in `{engine folder}/plugins/InfoPanel.AiUsage/` (`InfoPanel/InfoPanel.csproj`'s `BundledPlugin` list builds it into the engine's build and publish).

## On until turned off

Its two readers are on by default, for a plugin with no saved setting, and the user can turn either off in its settings (Plugins › AI usage; a setting the user saved, on or off, wins). While both are off it opens no file: each set's `status` reads `off: turn on Read … in AI usage's settings` and every number "–". What it does look at when it loads is which Claude folders exist (folders only, below), so each can have readings of its own.

| Setting | Key | What |
|---|---|---|
| Read Claude Code | `readClaudeCode` | On by default. Reads Claude Code's limits from the usage file its status line writes once connected |
| Read Codex | `readCodex` | On by default. Reads Codex's limits and tokens from its session logs |
| Other Claude folders | `otherClaudeFolders` | Claude folders besides the ones it finds, separated by `;` (`~/` is the home folder); they count in the combined Claude Code set |
| Codex folder | `codexFolder` | Empty for the default: `CODEX_HOME` when it is set, else `~/.codex` |

## What it reads

Usage numbers only, never message text; it logs folder names, counts and field paths, never a path below a folder, a value or any content.

**Codex.** Its session logs, `{codex}/sessions/{yyyy}/{MM}/{dd}/rollout-*.jsonl`, in the day folders (local dates) of the last 8 days. Each file is read once, then only what is appended to it, to its last complete line; only lines holding `"token_count"` are parsed, and of those only the event's time, its limits (`payload.rate_limits`) and its file's running token total (`payload.info.total_token_usage.total_tokens`) are kept. A line over 16 MiB is passed over unread. A line still being written is kept (up to 32 MiB over every file) and nothing is read again until the file grows; a line past 16 MiB still being written is passed over to its end without being read again. A file that changes in a way an append can't (another file at its path, no longer than at its last read but at another time (however far its first scan got), its first 1 KiB changed) is read again from its start. The limits are the newest event's that has a window (an event whose `primary` and `secondary` are both null leaves the one before it); a window is the five-hour one when its `window_minutes` is 300 and the weekly one when it is 10080, whichever of `primary` and `secondary` it is in. Tokens today are, per file, its total now less its last total before local midnight; tokens over 7 days, less its last total before now less 7 × 24 hours; summed over the files; per file a total is kept at most once a minute (so the 7 days start to within a minute) and a repeated one not again. `credits` is `none`, `unlimited` or the balance as Codex writes it; `plan` its `plan_type`.

**Claude Code.** Claude Code gives its plan's limits only to its status line, after a session's first answer. Once the user connects the status line (Connect Claude Code limits, below), it also writes what Claude Code gives it to `infopanel-usage.json` in the Claude folder it runs for; the plugin reads only that file's `rate_limits` (`five_hour` and `seven_day`, each `used_percentage` and `resets_at`, Unix seconds or an ISO 8601 time), and the file's modification time is when they were seen. A file is read again only when its size or time changes, and only up to 1 MiB, the limit held on the read itself (a file that grows past it after it was looked at is refused, unread past the limit).

**Budgets.** A round of Codex's logs lists at most 1,000 logs a day folder, looking at no more than 2,000 of its entries (files and folders alike, one at a time), reads the newest first, and reads at most 64 MiB and for about 2 seconds, its listing included; past either it stops at a line's end or keeps the line it is in, and carries on there in the next round. Over every file it keeps at most 262,144 token totals. The plugin's host stopping it ends a round at once; what was read is kept. A round whose time runs out while it lists takes nothing as gone and lists again in the next. While a round hasn't caught up, or a day folder holds more logs than it lists or more entries than it looks at, or a total wasn't kept, the token readings are "–" and `status` says why (`reading Codex's logs (3 of 8 files so far)`; `Codex's day folders hold more than 1000 logs; tokens are left out`; `a Codex day folder holds more than 2000 entries; tokens are left out`; `Codex's logs hold more token counts than AI usage keeps (262144); tokens are left out`); the limits read so far still show.

**Files.** Every file is read below the folder it belongs to (a Claude folder, Codex's folder; that folder itself may be a link, as `~/.claude` often is), and only when neither it nor a folder between is a link and it is a regular file. On macOS and Linux the file is opened folder by folder with `O_NOFOLLOW`, non-blocking (a FIFO or a device is refused without waiting), and its type, size, time and identity are asked of that open descriptor, so nothing swapped in between a look and the open is read. On Windows the folders on the way are checked for links and the file is opened as itself (not a link's target) and checked on its handle; a folder swapped for a link between that check and the open isn't caught there.

**Claude folders.** Found when the plugin loads: the `CLAUDE_CONFIG_DIR` folder when it is set, every `~/.claude` and `~/.claude-*` holding `projects/`, and `~/.config/claude` holding `projects/`, at most 16, by name. The home folder is listed one entry at a time, no more than 10,000 entries, and the search takes at most about 2 seconds; past either, the folders found so far are given and a warning is logged (a folder left out can be named in Other Claude folders). Each has a set of its own; a folder made later gets one at the next reload of the plugin, and counts in the combined set at once if Other Claude folders names it. An engine started from a menu bar or when the user signs in doesn't see a terminal's `CLAUDE_CONFIG_DIR`, which is why it looks for the folders itself.

## Readings

Under `plugin:/ai-usage/{set}/{entry}`; the group is `AI usage › {set's name}`.

| Set | Name |
|---|---|
| `codex` | Codex |
| `claude` | Claude Code: the limits of the folder whose usage file was written last (the one in use), with `profile` naming it |
| `profile-{folder}` | Claude Code · `{folder}` (`profile-.claude-work` for `~/.claude-work`; a folder outside the home by its own name; ids are unique over the folders found and those Other Claude folders names, a second of the same name ending `-2`, and at most 128 characters, a longer name cut and ended with 8 characters of its hash) |

| Entry | Kind | What |
|---|---|---|
| `five-hour-used`, `weekly-used` | number, `%` | How much of the window is used |
| `five-hour-reset-in`, `weekly-reset-in` | number, `min` | Minutes until the window resets |
| `caption` | text | `resets 14:30 · wk Thu 09:00` while the numbers are under 30 minutes old; `as of Mon 02:11` past that; else the status in a few words |
| `as-of-age` | number, `min` | Minutes since the numbers were seen |
| `status` | text | `ok`, or what's wrong in plain words |
| `profile` | text | `claude` only: which folder its limits are from (`.claude-work`) |
| `credits`, `plan` | text | `codex` only |
| `tokens-today`, `tokens-7d` | number, `tokens` | `codex` only |

- **As of.** A window keeps its last value with its age. Once its reset time has passed it reads 0 (no use seen since the reset) and its reset has no value until the window is seen again.
- **"–" and why** (`status`): the reader is off; Codex's folder wasn't found; no Codex use in the last 8 days; Codex reports no limits; Codex reports no five-hour (or weekly) window for the plan (that window alone is "–"); Claude Code not connected (`Not connected: Connect Claude Code limits in AI usage`); no plan limits (an API key, Bedrock or Vertex: `rate_limits` is null); no limits yet (a session before its first answer, with none seen before); a window Claude Code's status line left out.
- **Format changed.** An event or usage file with a field missing or of another type (in a Codex event `info`, `rate_limits` and its `primary` and `secondary` must be there; null is how Codex says it has none), or a Codex window of another length, reads "–" with `Codex's log format changed (rate_limits.primary.used_percent missing); InfoPanel may need an update` (or the usage file's), naming the field, never its value, and logs it once a run; a Codex token total that can't be read makes the token readings "–" too, until a file's next readable total (an event with `info` null between leaves them "–"). A usage file that doesn't parse keeps the last good reading (it was likely caught mid-write) and is read again; one that keeps failing for 2 minutes while it changes reads "format changed".

## Connect Claude Code limits

Claude Code gives its limits only to its status line, so the plugin's two actions (Plugins › AI usage › Actions) put a small wrapper in it and take it out again: **Connect Claude Code limits** (`ConnectClaudeCodeLimits`) and **Disconnect Claude Code limits** (`DisconnectClaudeCodeLimits`). Each says its state and asks first with the plugin's own words (`IPluginActionStates`, [plugins](plugins.md)); both are off while Read Claude Code is off, so they too open no file then.

**What the user sees.** The state line, the same under both: `Not connected · 5 Claude folders, 1 settings file`, `Connected · 5 Claude folders` (Connect is then off: there is nothing to do), `Partly connected · 1 of 2 settings files`, `Not connected: your status line changed after Connect`, `Broken: the wrapper is missing`, `Can't connect: ~/.claude/settings.json: it isn't plain JSON …`, or why neither can run: `Turn on Read Claude Code first`, `Your organisation's settings set the status line`, `Not available on Windows yet`, `No Claude folder found`. Connect's confirmation names each settings file it changes and the Claude folders that share it, the status line now and after, the backup, the files it won't change and why, and the projects whose own status line wins:

> **Connect Claude Code limits**
> Changes 1 settings file, shared by 5 Claude folders (.claude, .claude-a, .claude-b, .claude-c, .claude-d): ~/.claude/settings.json.
>
> Your status line now: bash ~/statusline.sh
> After: bash ~/.claude/infopanel-statusline.sh, which runs your command unchanged and copies the limits Claude Code gives it to infopanel-usage.json in each Claude folder.
>
> A backup is kept beside each file it changes (settings.json.infopanel-backup-{date}-{time}). Disconnect puts your status line back.
>
> Overridden: none (no project or organisation setting sets a status line).

Disconnect's says what goes back where (`Puts back bash ~/statusline.sh in ~/.claude/settings.json.`, or that the status line Connect added is removed), and that the usage files stay. An action that fails says why in the plugin's errors, file by file; the files it could change are changed.

**The wrapper** (`Claude/Wrapper.cs`), written beside the settings file as `infopanel-statusline.sh` (mode 0700; beside a settings file of another name, `infopanel-statusline.{name}.{8 hex}.sh`, the hex the start of the SHA-256 of the file's real path, so no two settings files in one folder share a wrapper or record). It runs bash, `mktemp` and `mv` (the new usage file and the rename that puts it in place), and `rm` only when those fail:

```bash
#!/bin/bash
# Written by InfoPanel's AI usage plugin (Connect Claude Code limits). Disconnect in InfoPanel puts your status line back.
umask 077
IFS= read -r -d '' input || true
__infopanel_f="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/infopanel-usage.json"
if __infopanel_t=$(mktemp "${__infopanel_f%/*}/.infopanel-usage.json.XXXXXXXXXX" 2>/dev/null) && { printf '%s' "$input" > "$__infopanel_t"; } 2>/dev/null; then
  exec 3< "$__infopanel_t"
  { [[ ! -d $__infopanel_f ]] && mv -f -- "$__infopanel_t" "$__infopanel_f"; } 2>/dev/null || rm -f -- "$__infopanel_t"
else
  [[ -z $__infopanel_t ]] || rm -f -- "$__infopanel_t" 2>/dev/null
  exec 3< <(printf '%s' "$input" 2>/dev/null)
fi
eval '<your command, each single quote as '\''>' <&3 3<&-
```

The status line runs it as `bash '/…/infopanel-statusline.sh'`. Your command runs last with the same input, byte for byte (read from the new usage file, or from bash itself when that couldn't be made), so its output, its stderr and its exit code are what Claude Code gets; the usage file is written to a new private file (`mktemp`: a regular file, 0600, made only where nothing is) and renamed over `infopanel-usage.json`, so a link there is replaced, never followed, a FIFO there is never waited on, and the file is 0600 whatever it was before; it is written in the Claude folder Claude Code runs for (`CLAUDE_CONFIG_DIR`, else `~/.claude`; `~/.config/claude` instead for a settings file that folder uses and `~/.claude` doesn't), so one settings file shared by several folders serves each; a folder that isn't there is no error. With no status line before, a minimal one stands in for your command: `5h 24% · wk 45%` (each whole percent; `–` for a window Claude Code didn't give), read with bash's own regular expressions. A command holding a NUL can't be wrapped and is refused. The wrapper keeps working when InfoPanel is removed: Disconnect first, or put the status line back by hand from the record or a backup.

**Writing the settings file.** For each Claude folder (those found at load and those Other Claude folders names), its `settings.json` with every link resolved, folder links too; folders whose settings files are one real file are grouped, so one change connects them all. A settings file that is a link is written at its target and the link stays as it is; one that is a link to nothing is refused, and none is made there. Then:

1. Read the real file whole: a regular file (opened through no link), at most 1 MiB (and at most 1 MiB once changed, else refused with nothing changed, so it can always be read back and disconnected), a plain JSON object (comments, trailing commas, `statusLine` twice or anything else: refused, "nothing changed"). A status line must be a command (`type` `command` or none) with a `command`; one that already runs an InfoPanel wrapper but isn't this file's own is refused, so a wrapper never runs itself. A missing file is made only by Connect, holding only the status line (0600).
2. Only `statusLine` changes: its `command` becomes the wrapper's, every other member kept in place (`type`, `padding`, `refreshInterval` …), or a new `statusLine` (`type` `command`) goes last. Every other member is kept in order and written again with 2-space indentation and a final newline, as Claude Code writes it; values as they were (numbers as written; a string may come back with its escapes written another way).
3. The wrapper, and the record `infopanel-statusline.json` (or named as the wrapper; 0600: the status line before Connect, or null, the wrapper's command, the settings file, the time), are written first, each to a new file beside it and renamed over it.
4. A backup of the bytes read, `settings.json.infopanel-backup-{yyyyMMdd-HHmmss}` (0600), beside it; the newest three are kept.
5. The new bytes go to a new file in the same folder with the file's mode, flushed; the file is read again, and only if it still holds the bytes read in step 1 is the new file renamed over it (Claude Code writes this file itself, often). If it changed, that backup and the new file go and Connect starts again from step 1, at most 3 times; then it gives up with nothing changed there, and the wrapper and record it wrote go too, unless the file's status line command (as JSON decodes it) names the wrapper by then (another press connected it meanwhile), or the file can't be read or parsed. The rename keeps the file it displaces aside (`File.Replace`, `{name}.infopanel-displaced-{32 hex}`) and compares it with the bytes read: written after that last read, it is renamed back and the change made again from it (one of the 3 tries); written once more while that happens, the newer version stays aside and the error names it.
6. Read back: the status line must run the wrapper.

**Idempotent, and later edits.** Connect on a file that already runs the wrapper writes nothing; if the wrapper was deleted it is written again from the record (`Broken: the wrapper is missing` until then), the settings file unchanged. Disconnect puts the record's command (and `type`) back into the status line as it is now, so a member changed since Connect (`padding`) keeps its change, or removes `statusLine` when there was none, through steps 4 to 6 (a record that names another settings file is refused, nothing changed), then removes the wrapper and the record; the usage files stay, their age showing. If the user changed the status line after Connect (by hand or with Claude Code's `/statusline`), the wrapper no longer runs and the state says so; Connect wraps the new command (a new record and backup), and Disconnect leaves the file as it is and removes only the wrapper and record, unless the new status line runs the wrapper itself.

**Settings that override it** (`Claude/Overrides.cs`). Claude Code's own order is the organisation's settings, then the command line's, then a project's local and shared settings, then the user's (the command line's can't be seen). The organisation's (managed) settings file, `/Library/Application Support/ClaudeCode/managed-settings.json` on macOS, `/etc/claude-code/managed-settings.json` on Linux, `C:\Program Files\ClaudeCode\` and `C:\ProgramData\ClaudeCode\` on Windows: if one sets `statusLine`, both actions are off; one that is there and can't be read or isn't plain JSON counts as one that may. A project's (`{project}/.claude/settings.json` or `settings.local.json`) setting `statusLine` doesn't stop Connect: the confirmation names it (`In these projects your project's status line is used, so limits are seen only from other projects: ~/work/my-app.`). The projects are the ones Claude Code keeps a folder for in each Claude folder's `projects/` (at most 500 each), each folder's name read back as the path it stands for (Claude Code names it by the folder it ran in, links resolved, with every character but an ASCII letter or digit as `-`) by listing folders from `/` down and matching their names written the same way (a folder too full to list within 10,000 entries is asked about the names with each `-` as itself; at most 64 listings and questions a project, about 2 seconds in all); no transcript is opened. A user's own settings file found that way (a project in the home folder) isn't a project's.

**Windows.** Connect and Disconnect read `Not available on Windows yet` until a check on a Windows PC passes (Claude Code runs status lines there through Git Bash; the wrapper's command would be `bash "C:/Users/…/.claude/infopanel-statusline.sh"`, and a path holding `"`, `$`, `` ` ``, `%` or `!` is refused). The readings work there as anywhere.

## Testing it

Its tests (`InfoPanel.Tests/Plugins/AiUsage/`) run on hand-written fixtures in the shapes above, in a home folder of their own; the host test runs it in a real plugin host whose `HOME` is such a folder. Connect's tests (`ConnectTests`, `WrapperTests`) write only in such a home, with the managed settings file's path given by the test, and run the wrapper with bash over a stub command, with `HOME` and `CLAUDE_CONFIG_DIR` set by the test; never a real Claude folder or the real `claude`. The simulator runs it too: `dotnet run --project InfoPanel.Plugins.Simulator -- <InfoPanel.AiUsage.dll> ai-usage --set readCodex=true --once`; `--set readClaudeCode=true --action ConnectClaudeCodeLimits --yes --once` connects (with `HOME` set to a fixture home, never your own while trying it).
