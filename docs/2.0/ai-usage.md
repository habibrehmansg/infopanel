# AI usage

AI usage (`ai-usage`, package `InfoPanel.AiUsage`) is a plugin that comes with the engine: how close Claude Code and Codex are to their five-hour and weekly limits, when those reset and how old the numbers are, as readings a panel can show. It is an ordinary plugin ([plugins](plugins.md)), built against the SDK like any other and bundled in `{engine folder}/plugins/InfoPanel.AiUsage/` (`InfoPanel/InfoPanel.csproj`'s `BundledPlugin` list builds it into the engine's build and publish).

## Off until turned on

Its two readers are off until the user turns them on in its settings (Plugins › AI usage), and while both are off it opens no file: each set's `status` reads `off: turn on Read … in AI usage's settings` and every number "–". What it does look at when it loads is which Claude folders exist (folders only, below), so each can have readings of its own.

| Setting | Key | What |
|---|---|---|
| Read Claude Code | `readClaudeCode` | Off by default. Reads Claude Code's limits from the usage file its status line writes once connected |
| Read Codex | `readCodex` | Off by default. Reads Codex's limits and tokens from its session logs |
| Other Claude folders | `otherClaudeFolders` | Claude folders besides the ones it finds, separated by `;` (`~/` is the home folder); they count in the combined Claude Code set |
| Codex folder | `codexFolder` | Empty for the default: `CODEX_HOME` when it is set, else `~/.codex` |

## What it reads

Usage numbers only, never message text; it logs folder names, counts and field paths, never a path below a folder, a value or any content.

**Codex.** Its session logs, `{codex}/sessions/{yyyy}/{MM}/{dd}/rollout-*.jsonl`, in the day folders (local dates) of the last 8 days. Each file is read once, then only what is appended to it, to its last complete line; only lines holding `"token_count"` are parsed, and of those only the event's time, its limits (`payload.rate_limits`) and its file's running token total (`payload.info.total_token_usage.total_tokens`) are kept. A line over 16 MiB is passed over unread. A line still being written is kept (up to 32 MiB over every file) and nothing is read again until the file grows; a line past 16 MiB still being written is passed over to its end without being read again. A file that changes in a way an append can't (another file at its path, no longer than at its last read but at another time (however far its first scan got), its first 1 KiB changed) is read again from its start. The limits are the newest event's that has a window (an event whose `primary` and `secondary` are both null leaves the one before it); a window is the five-hour one when its `window_minutes` is 300 and the weekly one when it is 10080, whichever of `primary` and `secondary` it is in. Tokens today are, per file, its total now less its last total before local midnight; tokens over 7 days, less its last total before now less 7 × 24 hours; summed over the files; per file a total is kept at most once a minute (so the 7 days start to within a minute) and a repeated one not again. `credits` is `none`, `unlimited` or the balance as Codex writes it; `plan` its `plan_type`.

**Claude Code.** Claude Code gives its plan's limits only to its status line, after a session's first answer. Once the user connects the status line (Connect Claude Code limits, a later step), it also writes what Claude Code gives it to `infopanel-usage.json` in the Claude folder it runs for; the plugin reads only that file's `rate_limits` (`five_hour` and `seven_day`, each `used_percentage` and `resets_at`, Unix seconds or an ISO 8601 time), and the file's modification time is when they were seen. A file is read again only when its size or time changes, and only up to 1 MiB, the limit held on the read itself (a file that grows past it after it was looked at is refused, unread past the limit).

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

## Testing it

Its tests (`InfoPanel.Tests/Plugins/AiUsage/`) run on hand-written fixtures in the shapes above, in a home folder of their own; the host test runs it in a real plugin host whose `HOME` is such a folder. The simulator runs it too: `dotnet run --project InfoPanel.Plugins.Simulator -- <InfoPanel.AiUsage.dll> ai-usage --set readCodex=true --once`.
