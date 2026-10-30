# Media providers

A scene's `media` node can show media from a provider configured on the engine: the source `{ "provider", "ref", "refresh" }` ([smart nodes](smart-nodes.md#media)), under the capability `media.provider`. The provider alone decides what it fetches and from where; the scene only names it and passes it `ref`. The session lists the providers an engine has (`media.providers`, [API](api.md#session)): a source naming one it doesn't list draws the missing-media placeholder with `media.provider`.

What a provider supplies is drawn as an asset is: the same formats and media limits ([limits](limits.md#media-limits-checked-from-the-header-before-decoding)), the same decoding, animation timing on the tick clock and SVG profile. Nothing a provider fetches is stored in the engine's data folder or travels with a profile.

## Asking, and what each item shows

A provider is asked for its media every tick that shows it, without waiting: it answers the newest media it has for `ref`, or nothing while it has none. It fetches only while ticks ask: a profile no output shows fetches nothing, and a provider drops what it holds for a reference 30 seconds after the last tick asked for it, stopping a fetch of it under way or waiting. A one-off render (a thumbnail of a profile no output shows) is answered with what the provider has and starts no fetch.

- **Before the first media** arrives, or when the first fetch fails, the item draws the missing-media placeholder in its box.
- **After it**, a failed fetch keeps the last good media: the item goes on showing it, and its status says why the newest couldn't be had. A newer answer replaces it only once it is decoded; one that can't be decoded is a failed fetch (`media.malformed`, …).
- **Status.** Each tick, an item whose media had a problem reports the first one in the frame header (`itemBounds[].media`, [API](api.md#frames)): `media.loading` while it loads, `media.provider` when the provider couldn't fetch it (with why: "The server answered 404.", "The server took longer than 15 s."), `media.address` for an address the engine may not connect to ([below](#addresses)), or the code a fetched answer was refused with ([validation](validation.md): `media.format`, `limit.mediaBytes`, …). Items showing the same reference share one fetch, and so one status.

## Refresh

`refresh` is in whole seconds, 0–86,400, default 0.

- **0:** the reference is fetched once while it is shown, and again once the provider has dropped it (30 seconds after the last tick asked). A failed fetch is tried again 5, 10, 20, 40 … seconds later, at most every 300 seconds, while it is shown.
- **N:** the next fetch starts N seconds after the last one was answered, while it is shown, conditionally when the answer allowed it (an unchanged answer keeps the media). A failed fetch is tried again at the next refresh.
- Items showing the same reference use the shortest refresh among them (0 counts as none).

## `http`: web images

`ref` is an absolute `http://` or `https://` address of an image, trimmed of spaces at either end: at most 2,048 characters, with a host that has no `%`. Anything else (empty text, a relative address, another scheme) is refused with `media.provider`, "Not a web address."

- **The request** is a `GET` with `Accept: image/*` and a `User-Agent` naming the engine and its version. It sends no cookies and keeps none, uses no proxy, and asks for no compression. The address's own user name and password (`http://user:password@camera.local/snapshot.jpg`) are sent as the server asks (Basic or Digest), only to that address and only for that reference: the same address written without them is asked without credentials.
- **The answer** must be a 200, or a 304 to a conditional fetch (the media kept). Its body is counted as it is read and refused past `mediaBytes` (`limit.mediaBytes`), then inspected from its header and refused as an asset would be. Up to 5 redirects are followed, each to an `http` or `https` address, each a connection checked as below; a sixth is refused.
- **Time:** a connection must be made within 5 seconds and the whole answer read within 15.
- **At once:** one fetch per address at a time, and at most 4 fetches at once in the engine.
- **TLS** uses the operating system's trusted certificates: a server whose certificate it doesn't trust is refused.

## Addresses

Every connection a provider makes, the first and each redirect and refresh, resolves the host itself (`localhost` and the names under it are this computer, `127.0.0.1` and `::1`, never looked up), checks every address it may connect to, and connects only to an address it checked, so neither a redirect nor a name whose answer changes between the check and the connection reaches an address refused here. An IPv6 address that carries an IPv4 one (IPv4-mapped `::ffff:a.b.c.d`, IPv4-compatible `::a.b.c.d`, NAT64 `64:ff9b::a.b.c.d`, 6to4 `2002:aabb:ccdd::/48`) is checked as that IPv4 address. One in the local-use translation prefix `64:ff9b:1::/48` (RFC 8215), whose layout only its translator knows, is a cloud metadata service when it carries one under any of the layouts RFC 6052 gives a prefix inside it (/48, /56, /64 and /96); otherwise it is checked as the IPv4 address in its last 32 bits unless that is a public address, and then it counts as the local network. Each fetch makes new connections (none is kept open between fetches), so a change of the setting below applies to the next one.

| Addresses | |
|---|---|
| Unspecified (`0.0.0.0/8`, `::`), link-local (`169.254.0.0/16`, `fe80::/10`), cloud metadata services (`169.254.169.254`, `169.254.170.2`, `100.100.100.200`, `192.0.0.192`, `fd00:ec2::254`, whatever range they are in), multicast (`224.0.0.0/4`, `ff00::/8`), broadcast and reserved (`240.0.0.0/4`) | Always refused |
| This computer (`127.0.0.0/8`, `::1`) and the local network (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `fc00::/7`, `fec0::/10`, `64:ff9b:1::/48` as above) | Allowed while the engine setting `media.localNetwork` is on, which it is by default ([API](api.md#settings)); refused when it is off |
| Every other address | Allowed |

A refused address is reported as `media.address`, naming it: "192.168.1.20 is on your local network.", "127.0.0.1 is on this computer.", "169.254.169.254 is a link-local address.", "100.100.100.200 is a cloud metadata service." A name is connected to at the first of its addresses that may be, and a name none of whose addresses may be is refused the same way, naming the first. When the setting changes, the engine drops what its providers fetched, so each reference is fetched again under the new rule.

**What a shared profile can do with this.** A profile can make the engine send `GET` requests to the addresses it names, as often as its refresh says, and show the answers as pictures on this computer's outputs. An answer never leaves the engine except as those pixels. An address built from a sensor's text carries that text to the server it names.
