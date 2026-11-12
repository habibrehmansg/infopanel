# Jungle Leopard / Hongtai panels

The `jl` family, package `InfoPanel.Panels.Jl`: InfoPanel 1.4's Jungle Leopard / Hongtai cooler displays (the JL Chill Arc 360 and the other displays on the Hongtai reference firmware) over their USB serial port, ported from the 1.4.x tip. **Unverified on hardware**: the protocol is checked byte for byte against 1.4.x's own code and its call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

## Models and how a panel is told

| Model id | Name | Size | 1.x model |
|---|---|---|---|
| `chillarc360` | JL Chill Arc 360 | 480 × 960 | `ChillArc360` |
| `stripdisplay` | JL Strip Display | 1920 × 462 | `StripDisplay` |

Both are on one USB id, `33c3:7788`, matched as a serial port. `panel.json` is the one table: each model carries its USB id (`usb`), its JPEG cap (`maxJpegBytes`, 80 KB) and 1.4's default frame rate for a new panel (`defaultFrameRate`, which the engine doesn't read yet). The probe opens the port as a start does (below) and asks the device info alone (200 ms, the input dropped, the query, its answer waited for 1 s); only when that goes unanswered does it send the start's pipeline reset, wait 200 ms and ask again (1 s). A device that answers neither is not the family's: the engine leaves it alone until it is plugged in again, and the driver remembers nothing. The whole probe, the open included, keeps within 4 s on one clock, inside the engine's 5 s; a probe that couldn't ask (the port not opened, a call failed, the time run out before an answer could come) answers a status, and the device is offered again on a later pass. The device info's `width` and `height` name the model. A resolution the table hasn't (480 × 1920, say) is the unknown model, named by its size (`480x1920`): it is listed and saved, and starts once its `model` setting names one of the family's models ("Try it as…"). A reply without a size is the Chill Arc 360, the model 1.4's table gave the USB id. The device info's `uid` is the panel's serial and its `version` the firmware. A session runs at the size the device info gives at its start, as 1.4 trusted it over its table, else at its model's.

`detect` lists `33c3:7791` to `33c3:7810` (as nine patterns, which take in `7790` too): OEM displays that share the protocol at other sizes, which 1.4 didn't match, so they show as detected, not supported. Among them is `0x7793`, the reference firmware's SPI variant (raw RGB565 through a CH340 serial bridge), a different transport that 1.4 left out on purpose; so does this package.

## The protocol

A USB CDC serial port, no flow control, opened with DTR and RTS on. 1.4 asked for 2,000,000 baud, which a CDC port ignores; where a platform refuses the rate, the package asks for the nominal 115,200 instead.

- **Commands** are framed `55 AA [length] [command] [payload] [checksum]`: the length is the whole frame's (the payload's plus 7) and the checksum the low 16 bits of the sum of every byte before it, both little-endian. Replies have the same shape; their checksum isn't checked. The commands: restart `0x01`, the light `0x03` (one byte, 0–100), the device info `0x06` (answered with JSON: `status`, `uid`, `model`, `version`, `width`, `height`, `angle`, `region`), the keep-alive `0x11`, a preset by name `0x20`, close `0x21`. The package sends the light, the device info, the keep-alive and close, as 1.4 did.
- **The start**: the port opened, 200 ms, the pipeline reset `FF D9 FF D9 00 00 00 00`, 200 ms, the input dropped, the device info (a reply within 2 s, `status` 200), then the light at the panel's brightness. A start that fails sends close before it lets the port go, as 1.4's did.
- **A frame** is the keep-alive, then the JPEG in its envelope (`[length, 32 bits LE] [JPEG] [sum, 16 bits LE]`, the sum over the length's bytes and the JPEG's), then `FF D9 FF D9`. Nothing is read back.
- **The keep-alive**: the firmware falls back to its own screen without a `0x11` within about 1.5 s. Each frame carries one, and after 1 s without a new frame the engine's keep-alive sends `0x11` alone, as 1.4's send loop did.
- **Brightness**: both of 1.4's paths: the panel's light (`0x03`, sent at the start and whenever the brightness changes), and the frame dimmed by the engine below 100 (`DimInSoftware`), which 1.4 did "to preserve the look of dim profiles below the backlight floor".
- **The stop**: close (`0x21`, firmware 3.1 and later), then the port let go; the engine's port gives what is still queued 100 ms to go out before it throws it away.

JPEG quality is 80, 1.4's default for these panels. A frame over the model's 80 KB is encoded again 15 lower, down to 30, as 1.4 did; one still over it at 30 isn't sent and the panel keeps its last frame (1.4 sent it anyway). The most frames a second is 60, the top of 1.4's Target FPS slider (15 was its default).

1.4's protocol notes (`JL-PROTOCOL.md`) give the keep-alive as `55 AA 07 00 11 11 01`; the framing rule, and 1.4's code, give `55 AA 07 00 11 17 01`, which is what 1.4 sent and what this package sends.

## Operating systems

- **Windows**: a CDC port needs no driver. The port is the device's own, found by its USB location.
- **macOS**: no driver needed. Whether the platform takes 2,000,000 baud for a CDC port isn't known yet; the package falls back to 115,200 if it doesn't.
- **Linux**: `99-infopanel-jl.rules` (in the package) gives the user at the seat access to the device and its `ttyACM` port: copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`JlHardwareTests` runs only with `INFOPANEL_PANEL_HW=jl` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: through the engine's own device seam it starts the panel, shows colour bars, then a gradient at brightness 50, holds it with keep-alives for three seconds, and stops it. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector; the trace holds the panel's `uid`.
