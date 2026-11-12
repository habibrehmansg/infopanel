# Thermaltake / ASRock panels

The `thermaltake` family, package `InfoPanel.Panels.Thermaltake`: InfoPanel 1.4's Thermaltake / ASRock LCD panels over HID, ported from the 1.4.x tip. **Unverified on hardware**: the protocol is checked byte for byte against 1.4.x's own code and its call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

## Models and how a panel is told

| Model id | Name | USB id | Size | 1.x model |
|---|---|---|---|---|
| `toughliquid6inch` | Thermaltake 6" LCD | `264a:2347` | 1480 × 720 | `ToughLiquid6Inch` |
| `asrockphantomgaming360lcd` | ASRock Phantom Gaming 360 LCD | `26ce:0a10` | 480 × 480 | `AsrockPhantomGaming360LCD` |

`panel.json` is the one table: each model carries its USB id (`usb`), which alone decides the model, so the family has no unknown model and no `model` setting. The probe sends nothing: it takes a matched device that has a HID collection whose output report holds 1025 bytes, as 1.4's scan did. HID gives no serial number, so a panel is told from another of its model by its USB port. 1.x's `Unknown` model names no model; a 1.x panel saved with it is matched by its USB id.

`detect` lists `264a:23??`, Thermaltake's range around the 6" LCD, so a new Thermaltake panel shows as detected, not supported. ASRock's vendor id (`26ce`) also covers its boards' own devices, so it has no pattern.

## The protocol

Both models speak one protocol (it came to 1.4 with the 6" LCD, then the ASRock panel was found to speak it too). Every transfer is a 1024-byte packet sent as a 1025-byte output report, report id 0 first (the device declares no report ids).

- **Commands** are text, framed `5A 00 [length] [text] [checksum] 5A 00`, zeros after: the length is the text's plus 5, the checksum the low byte of the length plus the text's bytes. The text is a request line, then `SeqNumber=` (100 for a session's first command, one more each), `Date=` (Unix milliseconds), for a command with a body `ContentType=json` and `ContentLength=`, a blank line, and the body; lines end `\r\n`.
- **The start**: `POST conn 1` (no body), then `POST realtimeDisplay 1` with `{"enable":true}`. After each, 50 ms, then one input report is read; the command was taken when the report's printable text (after its report id) holds `200`. A command not answered so ends the start.
- **A frame** is one JPEG, sent in chunks of 1000 bytes, each after a 24-byte header: `5C 03 FD 00`, the chunk count and the chunk's index (each 16 bits, big-endian), `01`, then zeros. The last chunk is padded with zeros. Nothing is read back.
- **The stop**: `POST realtimeDisplay 1` with `{"enable":false}` and its reply read, which hands the screen back to the panel's own display. 1.4 sent it whenever it let the device go, after a failed start too; so does this package.
- **Brightness**: the panel has `POST brightness 1` with `{"value":0–100}`, which 1.4 never used: it dimmed the frame instead. So does the engine (`DimInSoftware`).

JPEG quality is 85, 1.4's default for these panels; the most frames a second is 30, the top of 1.4's Target FPS slider (15 was its default).

1.4's first version of this driver sent three fixed packets captured from the vendor's software; the tip builds every command. Two of those packets agree with the framing rule. The third, `realtimeDisplay` off, carried checksum `03` where the rule gives `55`; the tip, and this package, send `55`. A tester's trace would show whether the panel minds either.

## Operating systems

- **Windows**: HID needs no driver.
- **macOS**: no driver needed.
- **Linux**: `70-infopanel-thermaltake.rules` (in the package) gives the user at the seat access to the family's devices and their `hidraw` nodes: copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`ThermaltakeHardwareTests` runs only with `INFOPANEL_PANEL_HW=thermaltake` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: it starts the panel, shows colour bars, then a gradient, and stops it. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector. It needs the engine's device seam over the OS's HID.
