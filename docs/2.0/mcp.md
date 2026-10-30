# AI apps (MCP): how to try it and tune it

The engine is an MCP server for AI apps on the same computer (Claude Desktop, Claude Code and other apps that run local
MCP servers over stdio). An app starts `InfoPanel --mcp`; that small front speaks MCP on stdin and stdout and hands each
tool call to the engine that holds the data folder, starting the engine (without a browser) on the first call when it
isn't running. Nothing goes over the network.

## The tools

| Tool | Scope | What it does |
|---|---|---|
| `list_profiles` | read | The profiles: id, name, size, status, unsaved changes |
| `get_profile` | read | A profile's name, size, revision and items (position, box, visibility, bindings, what made each; listed within 4 MiB, then "… and N more items"); with `item`, that item's scene and source |
| `list_sensors` | read | The sensors: key, name, group, unit, status, value; `query` filters, `limit` caps (200) |
| `list_fonts` | read | The font families a scene's text can name, the bundled ones (Inter) first and marked; `query` keeps those whose name holds it, in any case |
| `render_preview` | read | A profile drawn as a PNG, `width` pixels wide (the profile's, at most 1,200), and the checks on the same tick (below), of the profile as it is now (the live frame's tick when it shows the newest change; what the live output holds back over the process budget is held back here too) |
| `get_design_guide` | read | The guide's topics (built-in: `scenes`, the scene format in short), palettes and examples, from the text folder |
| `create_profile` | edit | An empty profile (`name`, `width`, `height`, `background`), stored and saved at once |
| `add_item` | edit | An item drawing a `scene`, at `x`, `y`, with an optional box (`width`, `height`), `name`, `bindings`, `group`, `index` |
| `update_item` | edit | Any of an item's `x`, `y`, `rotation`, `width`, `height`, `scene`, `bindings`, `name`, `visible`; the rest stays |
| `remove_item` | edit | An item, a group with everything in it |
| `add_asset` | edit | An SVG stored as a blob: its asset id and size, for a scene's `media` node; nothing stored when it isn't an SVG the engine shows |
| `show_on_desktop` | edit | The profile's desktop window on (`show`, the default) or off, its other window settings kept; the answer says when no window opens on this engine (`--no-windows`, or a platform without desktop windows), the setting kept |

A profile is named by its id or its exact name, an item by its id or its exact name. **Changes:** each call that changes
a profile is one commit, one step the editor can undo, made at the revision the tool read (with the document's tag), so
a change someone else made to the same part in between is a conflict: the tool reads again and retries once, then says
so. The retry acts on the profile and the items the tool found first, never on others given their names meanwhile; one
deleted or replaced meanwhile is an error that names it. The editor sees each change as another client's. **Saving:** the change is saved when the profile had no unsaved
changes just before it applied (decided as the commit applies); otherwise it is left unsaved and the answer says "Not
saved: ‹name› has unsaved changes from the editor; save there." A save refused because another change landed at the
same moment, or one the data folder refuses (no space, no write access), leaves the change applied: "Changed, but not
saved", never an error that would invite a second copy. `create_profile` saves the new profile at once; an
interrupted build leaves it saved and unfinished.

**The preview's checks** (heuristics, and they say so), with the thresholds of `checks.json`:
1. **Text that doesn't fit its box**, as the text layout knows it: cut with "…", lines left out, clipped, drawn past its
   box, or shrunk to its `fit` minimum and still too big.
2. **Past the canvas:** an item's drawing more than `canvasTolerance` (1 px) outside the canvas.
3. **Overlap:** two visible items whose boxes overlap by more than `overlapMin` (10 %) of the smaller, unless the one
   that holds the other is drawn below it (a card behind its content). Boxes, not shapes.
4. **Contrast:** solid-colour text, its colour with its own and its groups' opacity, against the pixels under its
   lines, each where it is drawn (not the box's unused width), in the same tick drawn without text: the colour covering 70 % of them, else the worst covering 10 %; below
   `contrastMin` (4.5:1, WCAG's ratio) is a problem.

Each problem names the item and a next step. What they couldn't look at is listed under "Not checked" per item:
gradient-filled text, text in a group drawn with a blend mode (the checks don't model compositing), text over a background with no main colour, and whatever is past the budgets (4,000,000 pixels
sampled, 10,000 item pairs compared). With neither: "The checks found nothing (they cover fit, the canvas edge, box
overlap and contrast)", never a bare "no problems". An item whose picture was still loading when the preview was drawn
(a placeholder in it) gets a last line, "‹item›: its picture is still loading; preview again"; the preview never waits. Every problem comes back as a tool result marked as an error, with its
cause and a next step (for example "There's no profile Desk (notFound). list_profiles names them."), so the AI can
explain it. The tools are always listed, also while the engine isn't running, then with the wording of the text folder the
launch record names (`--mcp-text`, read as the engine reads it), so an app that connects first gets the same text. A
record the channel wouldn't trust (its run folder or the record not this user's alone, or a link) gives the built-in text.

## 1. Get a build

From the repository root:

```bash
dotnet build InfoPanel/InfoPanel.csproj -c Release
```

The engine is then `<repo>/InfoPanel/bin/Release/net10.0/InfoPanel` (below, `<engine>`). Start it once by hand the way
you want it to run, for example:

```bash
<engine> --data-dir <data folder> --mcp-text <text folder> --web-root <web client> --port 5300
```

That start is remembered (the launch record, below), so an engine an AI app starts later on the same data folder gets
the same flags.

## 2. Claude Desktop

Quit Claude Desktop (⌘Q), then add InfoPanel to `~/Library/Application Support/Claude/claude_desktop_config.json`
(create the file when it isn't there; keep any other servers in `mcpServers`):

```json
{
  "mcpServers": {
    "infopanel": {
      "command": "<engine>",
      "args": ["--mcp", "--data-dir", "<data folder>"]
    }
  }
}
```

Use absolute paths. Leave out `"--data-dir"` and its value for the default data folder
(`~/Library/Application Support/InfoPanel/v2`). Open Claude Desktop: Settings › Developer shows `infopanel` running, and
the tools menu lists its tools.

## 3. Claude Code

```bash
claude mcp add --scope user infopanel -- <engine> --mcp --data-dir <data folder>
```

In a session, `/mcp` shows `infopanel` connected with its tools.

## 4. Use it

Ask, for example: "Which InfoPanel profiles do I have?", "Show me a preview of Wide bar", "What's my CPU temperature?",
"Make a new 800×480 profile called Desk with a big clock, my CPU temperature and a GPU load bar, then show it on my
desktop". The editor, open on that profile, changes as the AI works.
Quit the engine and ask again: it is started by itself, without a browser, and the answer says so. Quitting the AI app
leaves the engine running.

## 5. Tune the text

The tools' wording, the server's instructions, the guide, palettes, examples and the preview checks' thresholds are
text the engine reads on every call: its built-in text, with the folder given to `--mcp-text` over it.

```text
<text folder>/
  tools.json         { "instructions": "…", "tools": { "get_profile": { "description": "…" } } }
  guide/{id}.md      a topic for get_design_guide (id: lower-case letters, digits, hyphens; e.g. design.md)
  palettes.json      colour palettes and type pairings (a JSON object)
  examples/{id}.json starting points (JSON objects; an optional "description" is listed)
  checks.json        the preview checks' thresholds: canvasTolerance (0–50 px), overlapMin (0–1), contrastMin (1–21)
```

| What | When it takes effect |
|---|---|
| A guide topic, palettes, examples, thresholds | The next call that reads them |
| Tool descriptions and instructions (`tools.json`) | The next tool list: the front tells the app the list changed after a call; otherwise Claude Code's `/mcp` › Reconnect, or quit and reopen Claude Desktop |
| Tool behaviour and checks | A new engine build, then quit and restart the engine |

A file there replaces the built-in file of the same name; `tools.json` merges tool by tool, so it may hold only the
descriptions it changes. A file that can't be used is skipped and the built-in one used instead, with one warning in the
engine's log naming the file and why: one that isn't JSON or isn't shaped as above, a value out of its range, an
instructions text over 16 KiB or a description over 4 KiB, a file over 1 MiB, something that isn't a regular file, or a
link that leads out of the folder. Topic and example ids are only ever looked up in what the folder lists; they are
never used as paths.

## When something is off

- **The app's side:** Claude Desktop keeps the front's messages in `~/Library/Logs/Claude/mcp-server-infopanel.log`.
  The front writes only protocol messages to stdout; its own lines (starting `infopanel-mcp:`) go to stderr.
- **The engine's side:** its log (`<data folder>/logs/`) has one line per tool call (the tool, the profile as named, the
  outcome and how long it took; never the other arguments) and every text file it skipped.
- **"An older version that can't talk to AI apps":** an engine holds the data folder but has no channel. Quit it and
  start this build.
- **"InfoPanel isn't running and couldn't be started":** the answer names why (an exit code, or no answer within 30 s).
  Start the engine by hand with the same data folder to see its message.

## How it works

- **The local channel.** The engine that holds a data folder's lock listens on a Unix socket
  `~/Library/Application Support/InfoPanel/run/{hash}.sock` (`{LocalApplicationData}/InfoPanel/run` elsewhere; on
  Windows the named pipe `InfoPanel-{hash}`), where `{hash}` is the first 16 hex digits of the SHA-256 of the data
  folder's real path (on macOS as the filesystem spells it, so `/Users/me/x` and `/users/ME/x` on a volume that folds
  case are one channel). The folder is owner-only (0700) and the socket 0600: only the user's own processes reach it.
  The front uses the channel only when the run folder belongs to the user and no one else can write in it, and the
  process at the other end is the user's; an engine never takes over a run folder another user owns. It is internal to
  the engine (JSON lines with a versioned hello), not part of the API.
- **Starting.** `initialize` and the tool list never start anything. The first tool call attaches to the channel, or
  starts the engine in a session of its own (so it outlives the app) and waits up to 30 s for its channel. The data
  folder's lock decides between engines: two starts at once leave one engine, and a `runtime.json` a crash left behind
  never stops a start.
- **The launch record.** Every engine that takes a data folder's lock writes `{run folder}/{hash}.launch.json` (0600)
  with its allowlisted flags: `--port`, `--web-root`, `--allow-origin`, `--dev-origin`, `--mcp-text`, `--mock`,
  `--mock-seed`, `--no-windows`, `--v1-dir`. Never `--token` (a restarted engine has a new sign-in link), `--fresh` or
  `--data-dir`. The front starts an engine with those flags, `--no-browser` and `--fresh=false`, checking each against
  the list again. The AI app's own environment never configures it: `Engine__*` variables there are neither read by the
  front (beyond the run folder and the start timeout) nor passed on.
- **Scopes.** The tools run inside the engine on the same code paths as the API, each with `read` or `edit`, never the
  session's powers.
