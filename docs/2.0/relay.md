# AI apps elsewhere: the engine's side of the relay

With the `service.aiApps` setting on (spec/docs/api.md, "AI apps elsewhere"), AI apps a user connects to the configured
service may use this engine's tools (the ones in [mcp.md](mcp.md)) from elsewhere. The engine opens one WebSocket to the
service's relay itself (the service never connects to the engine), and answers the tool calls that arrive over it
under its own rules. This page is the connection and its frames: the engine's part, not the API's (`spec/` has the
setting and its state, not the frames). Code: `InfoPanel.Core/Service/` (`RelayClient`, `RelayFrames`, `RelayClock`),
`InfoPanel/Mcp/` (`RelayHostedService`, the call context `Tools/McpCall.cs`, `McpToolHost`).

## The connection

- **When it's open:** while `service.aiApps` is on, the engine has a service address, and its credential is `present`.
  `GET /api/service` says how it stands (`aiApps`: `off`, `connecting`, `connected`, `replaced`, `outdated`,
  `waiting`).
- **How:** `GET {address}/svc/v1/relay` upgraded to a WebSocket, under the configured service's rules (`ServiceClient`):
  the address's origin only, `wss` (or `ws` on a loopback host), with `--offline` a loopback host only; no proxy; no
  redirect followed (a 3xx is a failure); the credential only in the upgrade request's `Authorization: Bearer`, never in
  a frame.
- **Upgrade answers:** 101 opens it. 401 marks the credential `rejected` (only the credential that request carried), and
  the engine connects no more until a client provides another. 429 or 503 with `Retry-After` (seconds or a date, from 1
  s to 24 h) is `waiting`, and the engine asks again after it. Anything else backs off.
- **Backoff:** 1 s, doubling to 60 s, each wait from 80 % to 120 % of it (never over 60 s); a connection that lasted 60
  s after `welcome` starts it again from 1 s. A change of the computer's network ends a backoff wait at once and starts
  the backoff again; it doesn't shorten a wait the service asked for.
- **Keepalive:** protocol pings every 20 s; one unanswered for 10 s drops the connection (then the backoff).
- **Close codes from the relay:** 4401 (this computer was removed) marks the credential `rejected`; 4409 (another engine
  with the credential took over) is `replaced`: no reconnect until the setting is turned off and on, or the engine
  restarts (a 4409 that ends a connection made before the setting was turned off and on is about that connection, and
  ignored, as is any outcome of such a connection except a refused credential, 401 or 4401, which marks `rejected` the
  credential that connection was made with, by its generation, and no other; a 4409 or 4426 is kept in the same step that
  finds it current, so a turn off and on after that clears the 4409); 4426 (the relay doesn't serve this protocol) is `outdated`: no reconnect until the engine is updated; 4429
  waits 10 minutes (`waiting`). Any other close backs off.
- **Closing:** turning the setting off sends `bye`, then a normal close with the reason `bye`: the relay forgets this
  computer. A change of address, or the credential replaced or removed, closes normally without `bye` (and connects
  again with the new one, if any). The engine's stop closes "going away" (1001), without `bye`. As the engine ends a
  connection, every call's cutoff trips at once, before the relay answers the close; the close, `bye` included, gets 5
  s, behind a send that can't finish too, and then the connection is cut off. Sends still waiting their turn, or
  stalled, stop as the connection ends, so no call is left holding one of the 8 places.

## Frames

Text frames of JSON, each an object with a `type`, at most 9 MiB as received (a longer frame closes the connection
with 1009) and 272 levels deep (the formats' 256 and an envelope, as on the local channel). The engine sends:

| Frame | When |
|---|---|
| `{"type":"hello","protocol":1,"engineVersion":"2.0.1"}` | First, once open |
| `{"type":"time","nonce":"‹128 random bits, base64url›"}` | A reading of the relay's clock ([below](#never-late)) |
| `{"type":"result","id":…,"result":{"content":[…],"isError":false}}` | A call's answer, as MCP's `tools/call` result. One that would be over 9 MiB is an error result saying so |
| `{"type":"late","id":…}` | The call was sent too long ago; it didn't run |
| `{"type":"cancelled","id":…}` | The call's change was stopped before it was made: nothing changed |
| `{"type":"error","code":…,"id":…}` | A frame the engine didn't take: `unsupported` (a type it doesn't dispatch, or a binary frame), `malformed` (not JSON, too deep, no `type`, a known type missing what it needs, or text anywhere in the frame that doesn't decode, such as an escaped lone surrogate, in a member no type reads too, checked before the frame is dispatched), `busy` (8 calls already run), `duplicate` (a call whose `id` is running). `id` is the frame's, when it had one of 1 to 64 base64url characters |
| `{"type":"bye"}` | The setting was turned off |

The relay sends:

| Frame | What the engine does |
|---|---|
| `{"type":"welcome"}` | `connected`; reads the relay's clock |
| `{"type":"time","nonce":…,"now":1762214400000}` | The answer to its reading with that nonce: the relay's clock, in ms since 1970 UTC. Another nonce is ignored |
| `{"type":"call","id":…,"tool":"add_item","arguments":{…},"sentAt":…,"maxChars":140000}` | Runs the tool ([below](#calls)). `sentAt` is the relay's clock when it sent the call, in ms since 1970 UTC (one past 9999-12-31, what a time holds, is `malformed`; one ahead of the relay's clock still gets at most 60 s, [below](#never-late)); `maxChars`, optional, the most text the AI app takes (brought within 1,000 and 9 MiB) |
| `{"type":"cancel","id":…}` | Trips the call's cutoff: its change stops unmade |

The engine dispatches only these four types; any other gets `error` `unsupported` and nothing closes, so the relay can
add types without a new protocol. A protocol the relay can't serve, it closes with 4426.

## Calls

- **Only the tools.** A call reaches `McpToolHost` (through `IRelayToolHost`) and nothing else: never the API, session
  powers, the settings (this switch included), tokens, the log, folders or plugins. A name that isn't a tool is an
  `unknownTool` error result. The tools are the local ones, with their scopes; no profile delete, undo or save.
- **At most 8 at once;** a ninth is answered `busy`.
- **Web images: refused for now.** A change from elsewhere may not insert or set a scene holding a media source with a
  `provider` (a web image or a camera), nor change the bindings of an item whose scene holds one, since a binding can
  re-address it: the answer is an error, `webImage`, "Adding pictures from web addresses isn't ready yet for AI apps
  elsewhere … add_asset takes an SVG". Moving, hiding, renaming or removing such an item works. (`media.localNetwork`
  is on by default, so without the rule a call could make the engine fetch from the local network.)
- **Text fits the AI app:** with `maxChars`, `get_profile` lists items within it ("… and N more items: the list stops
  at N characters, the most this AI app takes"), an item's scene and source are cut at a quarter of it, and the whole
  answer is held within it, a picture counted by its data as sent: a picture is kept whole when it leaves 300
  characters for the text, which is cut at a line, saying how much was left out; a picture that doesn't makes the
  answer an error saying the limit and to ask `render_preview` for a smaller width.
- **The cutoff.** Each call has its own, tripped by `cancel`, by the connection's close, or by its commit deadline
  ([below](#never-late)). Every place an edit tool writes asks it under the write's own lock, just before it writes:
  each commit of `add_item`, `update_item`, `remove_item` and `show_on_desktop` (every retry included, under the
  document's lock), `create_profile`'s create (the workspace's) and `add_asset`'s blob (the blob store's). A cutoff
  that trips while the write waits for its lock stops it; one that trips once the write holds its lock comes after the
  change. Once tripped, the change stops unmade and the call answers `cancelled`. The web-image rule is asked there
  too, on the item as the commit finds it, so a design changed meanwhile (design and bindings have revisions of their
  own, so no conflict) can't let a binding change through. A test lists every tool
  that declares `edit` and fails for one without a cutoff test.
- **Logs:** as local calls (tool, profile as named, outcome, time; never the other arguments), marked "from
  elsewhere"; a late call is logged as late.

## Never late

A frame TCP holds across a sleep can arrive minutes later. The engine refuses unrun, with `late`, a call whose `sentAt`
plus 10 s has passed on the relay's clock, which it reads by a challenge, never from `welcome` (a `welcome` held across
the same sleep would make an old call look fresh).

- **A reading** is a `time` frame with a fresh nonce, answered with the relay's `now`. It counts only when it carries
  that nonce and came back within 2 s on both of the engine's clocks: the wall clock, and Stopwatch's (which on macOS
  stops in sleep while the wall clock runs on, so an answer held across a sleep fails one of them). The offset is `now`
  less the wall clock's midpoint between question and answer.
- **When:** after `welcome`, and before a call whenever the last good reading is over 30 s old on either clock, so the
  first call after any pause waits one round trip. A failed reading is asked once more; a call still without one, or
  that waited over 10 s on either clock, is `late`.
- **A clock step voids a reading:** one is used only while the wall clock's time since it isn't negative and agrees with
  Stopwatch's within 1 s. A call sent at T=1 and delivered at T=26, after the wall clock went back 60 s, is refused
  `late`, not run.
- **A reading a call joins in flight** is checked again when it comes: one voided meanwhile (a step, or its age) is
  asked for once more.
- **One reading of the clocks decides:** both of the engine's clocks are read once as the call's reading is checked,
  and whether it may be used, how long the call waited, how late it is and its deadline (the instant it runs from) are
  all worked out from that; a step of the wall clock after it reaches none of them.
- **The commit deadline** is set when the call is admitted as the time left until `sentAt` plus 60 s on the relay's
  clock, from 0 to 60 s (a `sentAt` ahead of the relay's clock never gives a call more than 60 s), then runs on both clocks and trips when either has run it out: neither a sleep nor a step of the wall clock
  extends it. A step can at worst refuse a call.

## Tests

`InfoPanel.Tests/Relay/` runs the client against a fake relay on a loopback port of its own (`FakeRelay`), with both
clocks faked (`TwoClocks`: a sleep moves the wall clock alone, a step moves it without the true time, timers run on the
monotonic clock): the upgrade's answers, close codes, waits, keepalive, `bye`, redirects, admission, readings across a
faked sleep and a step (a reading joined in flight held by `TwoClocks.HoldDispose`; the client's step hook,
`RelayOptions.Step`, says when a call joined it (`joined`) and when its reading was checked (`checked`), where a test
steps the wall clock), a turn off and on from the log line a 4409 writes, the deadline, closing with calls in
flight (`RelayPeer.PauseReading` withholds the relay's reading and its close answer), and each edit tool paused just
before its write and again past where a check before its lock would have passed (the tool host's step hook, `write`
and `locking`) and then cut off by `cancel`, its deadline or the connection's close. `InfoPanel.Core.Tests` holds each
write's guard to its lock (a commit, a create, a blob) with the lock held and the write waiting for it.
