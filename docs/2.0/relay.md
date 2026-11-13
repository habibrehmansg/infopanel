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
| `{"type":"hello","protocol":1,"engineVersion":"2.0.1","features":["design"]}` | First, once open. `features` lists what the engine takes beyond the protocol: `design`, a call's design ([below](#designs)). A relay sends a design only to an engine that lists it, and reads the list tolerantly (an entry it doesn't know is ignored) |
| `{"type":"time","nonce":"‹128 random bits, base64url›"}` | A reading of the relay's clock ([below](#never-late)) |
| `{"type":"result","id":…,"result":{"content":[…],"isError":false}}` | A call's answer, as MCP's `tools/call` result. One that would be over 9 MiB is an error result saying so |
| `{"type":"item","id":…,"instance":…,"rev":12,"profile":"‹id›","item":{…},"detail":"‹text›"}` | A `read`'s answer ([below](#designs)) |
| `{"type":"stale","id":…}` | The design's base no longer holds inside the same document: nothing changed ([below](#designs)) |
| `{"type":"late","id":…}` | The call (or read) was sent too long ago; it didn't run |
| `{"type":"cancelled","id":…}` | The call's change was stopped before it was made: nothing changed (for a `read`: it was stopped before it answered) |
| `{"type":"error","code":…,"id":…}` | A frame the engine didn't take: `unsupported` (a type it doesn't dispatch, or a binary frame), `malformed` (not JSON, too deep, no `type`, a known type missing what it needs, or text anywhere in the frame that doesn't decode, such as an escaped lone surrogate, in a member no type reads too, checked before the frame is dispatched), `busy` (8 calls already run), `duplicate` (a call whose `id` is running). `id` is the frame's, when it had one of 1 to 64 base64url characters |
| `{"type":"bye"}` | The setting was turned off |

The relay sends:

| Frame | What the engine does |
|---|---|
| `{"type":"welcome"}` | `connected`; reads the relay's clock |
| `{"type":"time","nonce":…,"now":1762214400000}` | The answer to its reading with that nonce: the relay's clock, in ms since 1970 UTC. Another nonce is ignored |
| `{"type":"call","id":…,"tool":"add_item","arguments":{…},"sentAt":…,"deadline":…,"maxChars":140000}` | Runs the tool ([below](#calls)). `sentAt` is the relay's clock when it sent the call, in ms since 1970 UTC (one past 9999-12-31, what a time holds, is `malformed`; one ahead of the relay's clock still gets at most 60 s, [below](#never-late)); `deadline`, optional, the latest its change may be made, on the same clock (a time as `sentAt` is, else `malformed`; the engine takes the earlier of it and `sentAt` + 60 s, [below](#never-late)); `maxChars`, optional, the most text the AI app takes (brought within 1,000 and 9 MiB). For `add_item` and `update_item` also `design` and `files`, for `add_items` and `update_items` `designs` and `files`; for `update_item` also `basedOn`, an object, and for `update_items` a list ([below](#designs)) |
| `{"type":"read","id":…,"profile":…,"item":…,"detail":true,"sentAt":…,"deadline":…,"maxChars":140000}` | Reads one item ([below](#designs)): answered `item`, or a `result` with `isError`. It arrives as a call does: counted among the 8, late by `sentAt` + 10 s, cancellable |
| `{"type":"cancel","id":…}` | Trips the call's cutoff: its change stops unmade (a `read`'s: it answers `cancelled`) |

The engine dispatches only these five types; any other gets `error` `unsupported` and nothing closes, so the relay can
add types without a new protocol. A protocol the relay can't serve, it closes with 4426.

## Calls

- **Only the tools.** A call reaches `McpToolHost` (through `IRelayToolHost`) and nothing else: never the API, session
  powers, the settings (this switch included), tokens, the log, folders or plugins. A name that isn't a tool is an
  `unknownTool` error result. The tools are the local ones, with their scopes; no profile delete, undo or save.
- **At most 8 at once;** a ninth is answered `busy`.
- **Pictures in one call:** `add_asset`'s `data` holds a picture file's bytes in base64, at most 8,388,608 characters,
  so a 6 MiB picture as base64 fits one call frame.
- **Web images: refused for now.** A change from elsewhere may not insert or set a scene holding a media source with a
  `provider` (a web image or a camera), nor change the bindings of an item whose scene holds one, since a binding can
  re-address it: the answer is an error, `webImage`, "An AI app can't add or change a picture from a web address …
  add_asset stores a picture to show instead". Moving, hiding, renaming or removing such an item works.
  (`media.localNetwork` is on by default, so without the rule a call could make the engine fetch from the local network.)
  The `now-playing` provider's cover of what plays and the `plugin` provider's pictures are exempt: neither reaches an
  address a scene names (a reference only picks one of the covers the now-playing source read, or of the pictures the
  plugins source holds; a cover a player gives as a web address is fetched under the `http` provider's rules), so a call
  may add them or change their items' bindings.
- **Text fits the AI app:** with `maxChars`, `get_profile` lists items within it ("… and N more items: the list stops
  at N characters, the most this AI app takes"), an item's scene and source are cut at a quarter of it, and the whole
  answer is held within it, a picture counted by its data as sent: a picture is kept whole when it leaves 300
  characters for the text, which is cut at a line, saying how much was left out; a picture that doesn't makes the
  answer an error saying the limit and to ask `render_preview` for a smaller width.
- **The cutoff.** Each call has its own, tripped by `cancel`, by the connection's close, or by its commit deadline
  ([below](#never-late)). Every place an edit tool writes asks it under the write's own lock, just before it writes:
  each commit of `add_item`, `update_item`, `update_items`, `remove_item` and `show_on_desktop` (every retry included, under the
  document's lock), `create_profile`'s create (the workspace's) and `add_asset`'s blob (the blob store's). A cutoff
  that trips while the write waits for its lock stops it; one that trips once the write holds its lock comes after the
  change. Once tripped, the change stops unmade and the call answers `cancelled`. The web-image rule is asked there
  too, on the item as the commit finds it, so a design changed meanwhile (design and bindings have revisions of their
  own, so no conflict) can't let a binding change through. A test lists every tool
  that declares `edit` and fails for one without a cutoff test.
- **Logs:** as local calls (tool, profile as named, outcome, time; never the other arguments), marked "from
  elsewhere"; a late call is logged as late.

## Designs

A call to `add_item` or `update_item` from elsewhere may bring a design the service produced instead of a `scene` (and
one to `add_items` or `update_items` a design per item, [below](#designs)): the item's box, scene,
how it was produced and where it came from, as the document holds them ([document](../../spec/docs/document.md#source-and-origin)),
with the texts its `source` needs stored. The engine checks it as any item the API is given and never interprets it.

```jsonc
{"type":"call","id":…,"tool":"add_item","arguments":{"profile":"Overview","x":10,"y":20,"bindings":{…}},"sentAt":…,"deadline":…,
 "files":["‹text›"],
 "design":{"layout":{"w":120,"h":40},"source":{"format":…,"data":{…},"deps":["sha256:‹the text's SHA-256›"]},"scene":{…},"origin":{…}}}
```

- **The frame.** `design` is an object with `source` and `scene`, each an object, and optionally `layout` and `origin`,
  each an object; `files` a list of text. With a design, the arguments hold no `scene`, `width` or `height` (the design
  is the item's scene and box). Anything else is `malformed`.
- **Only from elsewhere, only to `add_item`, `update_item`, `add_items` and `update_items`.** A call made on this
  computer carries only arguments, and a `design`, `designs`, `basedOn` or `files` among them isn't one of the tool's. A
  design or files on a call to another tool, `designs` on a call to `add_item` or `update_item`, a `design` on one to
  `add_items` or `update_items`, a `basedOn` object on a call to any tool but `update_item` or a list on any but
  `update_items`, or non-empty
  `files` without a design, is an error result, `design`, with nothing stored or changed (`files: []` without a design
  is taken as none).
- **Checked before anything is written:** at most 16 files, each at most 1 MiB as UTF-8; each file's SHA-256
  (`sha256:` and 64 hexadecimal digits, as `POST /api/blobs` names a blob) must be one of the design's `source.deps`,
  and every dep must be one of the files or already stored (as a verified read finds it: a file under the id whose
  bytes don't hash to it isn't); `source.deps` must list blob ids; and the design's scene may not hold a provider's
  media source (the web-image rule). The first miss is an error result naming it (`files`, `design`, `missingDep`:
  "add_asset gives a picture's id", or `webImage`), and nothing is stored or changed. Then the item it would insert is
  built and checked against the profile as it is, by the commit's own checks, applying nothing (its layout, group,
  index and bindings, and the commit's every other refusal), and every change the call makes is held to the web-image
  rule on the profile as read (an `add_items` item given as a scene, or `update_item`'s bindings for an item showing
  a web image, included): a conflict (the profile changed since it was read) reads it again and rebuilds once, as a
  commit's does; any other refusal is the tool's error, and nothing is stored or changed. The commit asks the rule
  again on the profile as it finds it.
- **Then the files are stored** as `POST /api/blobs` stores bytes (already-stored bytes are kept as they are), each
  behind the call's cutoff, under the blob store's lock; then the item is inserted as `add_item` inserts one: a new id,
  the call's `x`, `y`, `name`, `bindings`, `group` and `index`, and the design's `layout`, `scene`, `source` and
  `origin` as given, in one commit behind the cutoff, with the save rule as for any change. The profile is found once,
  as the call starts, and that profile is the one checked and committed to, never one found again by its name. If it
  is deleted or replaced, or, when the call named it by its name, renamed (away, or away and back) at any point before
  the commit, the call is an error result, `notFound`, and no profile is changed: every check before the files are
  stored and every commit attempt (a retry's too) asks it under the profile's lock.
- **Cut off part way:** the files stored before the cutoff stay, unreferenced, and are swept as any such blob is
  (24 hours after they were stored, [API](../../spec/docs/api.md)); the item isn't added or changed.

**Adding items by designs.** `add_items` adds up to 50 items in one commit and one undo step, all or nothing, each as
`add_item` adds one, or as a group of them (an entry with `children`, each child counted among the 50); from elsewhere,
`designs` gives a design per item of its `items`, in order, a group's children right after the group, `null` for an
item given as a `scene` and for a group:

```jsonc
{"type":"call","id":…,"tool":"add_items","arguments":{"profile":"Overview","items":[{"x":10,"y":20},{"x":10,"y":70,"scene":{…}}]},"sentAt":…,"deadline":…,
 "files":["‹text›"],
 "designs":[{"layout":{…},"source":{…},"scene":{…},"origin":{…}},null]}
```

- `designs` is a list as long as `items` and their `children` together, each a design (as above) or `null`, never
  beside `design`; an item or a child with a design holds no `scene`, `width` or `height`. Anything else is
  `malformed`. A design for a group is the tool's error result, `design`.
- The files and every design are checked together before anything is written, as one design's are (at most 16 files
  in all; each file a dep of some design), then the insertion of every item is checked against the profile, then the
  files are stored, then the items are inserted in one commit behind the cutoff, with the profile held as for
  `add_item`. One item refused (its arguments, its group, its design, or the engine's checks, which name it as
  `items/‹its place›/…`, a child `items/‹its group's place›/children/‹its place›/…`) refuses the call whole: nothing
  is stored or changed.

**Reading an item.** A `read` names a profile and an item, each by id or exact name, as `get_profile` does. The answer,
`item`, holds the ids it resolved (`profile`, and the item's own `id`), the document's tag (`instance`) and revision
(`rev`), and the item whole as the document holds it, all from one snapshot; with `detail`, also `get_profile`'s text for
the item from that same snapshot, a sourced item's without its scene and source (the item carries them). With
`maxChars`, the detail is held within it by its structure, so it still parses as JSON: a longer one gains a `cut`
member saying so, and loses in turn the list of items, the item's scene and source, the length of every text but
the ids (names, what made it, each binding), then its bindings; the ids, size and revision stay whole. A profile or
item it can't resolve, or a name more than one item has, is an error result (`notFound`, `ambiguous`), held to
`maxChars` as a call's answer is. A read's cutoff is asked before it reads and again before it answers: tripped
(`cancel`, the connection's close, its deadline), it answers `cancelled`. Later frames name what the read resolved by
these ids.

**Changing an item by a design.** `update_item`'s design also names what it was built on:

```jsonc
"design":{"layout":{…},"source":{…},"scene":{…},"basedOn":{"instance":"‹the read's instance›","rev":12,"item":"‹the read's item id›"}}
```

- `basedOn` is required on `update_item` (without it, an error result `design`) and is an object with `instance` and
  `item`, each text, and `rev`, a whole number from 0 (else `malformed`).
- **Another document or item ends the call:** a profile whose tag isn't `basedOn.instance`, or an item the call names
  that isn't `basedOn.item`, is the error result "The profile was replaced or the item changed since this change was
  prepared; nothing was changed." (`basedOn`), checked as the call starts, before anything is stored. It is never
  `stale`. After that the profile is held as `add_item`'s is: deleted, replaced or renamed before the commit, the call
  is `notFound` with nothing changed.
- **One commit, one undo step:** the design is set (`setDesign`: its `layout`, `scene` and `source`; the item's `origin`
  kept) based on `basedOn.rev`, beside the call's other changes (placement, bindings, name, visibility) based on the
  revision the engine reads, as for any `update_item`, checked against the profile before its files are stored, with
  the save rule as for any change.
- **`stale`, never a rebuild:** a conflict on the design's own change (its layout, scene or source changed since
  `basedOn.rev`), found by the check before the files are stored or by the commit, answers `stale` at once, with
  nothing changed; the engine can't rebuild a design it didn't produce, so the service may read the item again and
  build it once more. A move, rename or other change meanwhile doesn't conflict with the design; any other conflict
  keeps the one retry every change has.

**A change based on a read.** An `update_item` without a design may name what the read it was built on found (a resize
or a scene worked out from the item as read), as a member of the call frame:

```jsonc
{"type":"call","id":…,"tool":"update_item","arguments":{"profile":…,"item":…,"width":160,"height":40},"sentAt":…,"deadline":…,
 "basedOn":{"instance":"‹the read's instance›","rev":12,"item":"‹the read's item id›"}}
```

- `basedOn` is an object as a design's is, never beside `design` (which carries its own); anything else is `malformed`.
- The same three rules as a design's: another tag or item is the error `basedOn`, checked as the call starts, never
  `stale`; from then on the profile is held (deleted, replaced or renamed before the commit: `notFound`); the change
  that sets the item's design (`width`, `height` or `scene`) is based on `basedOn.rev`, the call's other changes on the
  revision the engine reads, and a conflict on it answers `stale` at once, with nothing changed. A move, rename or
  other change meanwhile doesn't conflict with it.
- Without `basedOn` a change is as any `update_item`'s: on the revision the engine reads, a conflict retried once.

**Changing items.** `update_items` changes 1 to 50 items in one commit and one undo step, all or nothing, each entry
as `update_item` changes one (without `profile`), each item once. From elsewhere, `designs` gives a design per entry in
order (`null` for one without), each with its own `basedOn`, and `basedOn` a list of what a read found per entry
(`null` for one not based on a read, and for one with a design):

```jsonc
{"type":"call","id":…,"tool":"update_items","arguments":{"profile":"Overview","items":[{"item":"Gauge"},{"item":"Clock","width":160},{"item":"Label","x":200}]},"sentAt":…,"deadline":…,
 "files":["‹text›"],
 "designs":[{"layout":{…},"source":{…},"scene":{…},"basedOn":{…}},null,null],
 "basedOn":[null,{"instance":…,"rev":12,"item":…},null]}
```

- `designs` is a list as `add_items`' is (as long as `items`, each a design or `null`; an entry with a design holds no
  `scene`, `width` or `height`); `basedOn` a list as long as `items`, each `null` or an object as above, never one
  where `designs` has a design. Anything else is `malformed`. A design without its own `basedOn` is the error result
  `design`.
- Each entry based on a read or a design has its tag and item checked as the call starts; then the files and every
  design are checked together, as `add_items`' are; then the entries' changes, in order, are checked against the
  profile; then the files are stored and the change is one commit behind the cutoff, with the profile held as for
  `update_item`.
- One entry refused refuses the call whole, with nothing stored or changed, named by its place: its arguments, an entry
  with nothing to change, an item that isn't there or that another entry names too (`items/4 names the same item as
  items/1`), a change that doesn't fit it, `basedOn`, or the engine's checks (`items/‹its place›/…`). A conflict on any
  entry's design change (a design's, or one based on a read) answers `stale` at once; any other conflict keeps the one
  retry, rebuilt on the profile and items found first.
- The answer names the items changed, then those already as asked; with nothing changed, "Nothing changed: every item
  already was as asked."

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
- **A reading the call contradicts is read again:** a call arrives after it was sent, so the relay's clock is at least
  its `sentAt`; a reading that puts the relay's clock more than 1 s (half a reading's round trip, the most a good one
  can be off) behind `sentAt` missed a step of the relay's clock, and is asked for once more before the call is judged.
  A forward step of the relay's clock hidden by an equal delay in delivering the call isn't seen this way: the cutoff
  can then fall up to that step late. The engine accepts this rather than ask the relay's clock before every call.
- **One reading of the clocks decides:** both of the engine's clocks are read once as the call's reading is checked,
  and whether it may be used, how long the call waited, how late it is and its deadline (the instant it runs from) are
  all worked out from that; a step of the wall clock after it reaches none of them.
- **The commit deadline** is set when the call is admitted as the time left until its absolute cutoff, the earlier of
  the call's `deadline` and `sentAt` plus 60 s, on the relay's clock as read (so the time a call took to arrive counts
  against it), from 0 to 60 s (a relay can shorten a call's time, never extend it; a `sentAt` ahead of the relay's
  clock never gives a call more than 60 s; a `deadline` already past leaves a change no time at all), and never more
  than the call's own `deadline` − `sentAt` (60 s at most) from its arrival on the engine's clocks, so a call whose
  `sentAt` is ahead of the relay's clock even as read again gets no more than that; then runs on both clocks and trips
  when either has run it out: neither a sleep nor a step of the wall clock extends it. A step can at worst refuse a
  call.

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
write's guard to its lock (a commit, a create, a blob) with the lock held and the write waiting for it. Designs
(`RelayDesignTests`): inserted as given, files under their hashes, each refusal with nothing stored, the profile renamed (away, or away and back) at each point up to a retry's commit, and the cutoff
before each file and the commit for each tool that takes one (`EveryDesignTool_HasACutoffTestAtEachWrite` fails for a
tool that takes a design without one). Reads (`RelayReadTests`): whole with the ids, the tag and the revision, the
detail from the same revision and held to `maxChars` by its structure (still JSON, at the longest names, `source.ref`
and bindings), `notFound` and `ambiguous` as results held to `maxChars`, a read cancelled before it reads or before it
answers `cancelled` (the tool host's step hook, `reading` and `read`), late reads refused unread.
Changes by a design (`RelayUpdateDesignTests`): one commit and one undo step with the origin kept, another tag or item
the error and never `stale`, a design changed meanwhile `stale` with no retry, a move meanwhile not, the profile renamed
while the design is checked refused, a design beside bindings for an item showing a web image refused with nothing
stored. Batches (`RelayAddItemsTests`): 50 items in one commit and one undo step, the 51st refused, one bad item
refusing them all with nothing stored, a web image given as a scene beside a design refused with nothing stored, a
design per item with `null` for a scene item.
**Frame fixtures:** `InfoPanel.Core.Tests/Fixtures/relay/frames.json` holds one frame of each kind either side
sends; `RelayFrameFixtureTests` holds the engine to them (its own written exactly so, the relay's read as what they are),
so a relay can test its own parsing against the same file.
