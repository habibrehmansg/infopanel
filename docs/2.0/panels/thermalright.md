# Thermalright panels

The `thermalright` family, package `InfoPanel.Panels.Thermalright`: InfoPanel 1.4's Thermalright panels over USB bulk and HID, ported from the 1.4.x tip. **Unverified on hardware**: every protocol is checked byte for byte against 1.4.x's own code and its call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

## Models and how a panel is told

`panel.json` is the family's only table: its five USB ids, and all 69 1.4.x models in 1.4.x's order, each with the keys that identify it (the init reply's identifier string, PM and SUB bytes, ChiZhu rules, TrofeoBulk's byte 20 and reported height, ALi's device type), its pixels and its render size (`nativeWidth`/`nativeHeight` where the glass is larger, as the 6.67" panels). Each model's `v1Model` is its 1.4 name, so a 1.x panel comes over as the same model.

| USB id | Protocol | How the model is told |
|---|---|---|
| `87ad:70db` | ChiZhu (bulk, `12 34 56 78` headers) | The probe sends the 64-byte init and reads the PM and SUB (bytes 24 and 28), else the identifier (`SSCRM-V1`…); the start does it again in three passes (identifier and SUB, PM and SUB, identifier), retrying 5 times 3 s apart while the panel boots |
| `0416:5302` | Trofeo over HID (`DA DB DC DD`, 512-byte packets as 513-byte reports) | Each collection with a 513-byte output report in turn, until one answers the init (one busy or silent is let go first; a report of its id alone is no answer; in the probe the collections share 4.5 s, each the part of what is left that falls to it, so one silent collection can't keep the next from being asked before the engine's 5 s); the init's PM (byte 5) and SUB (byte 4), else the size the PM names |
| `0416:5408` | TrofeoBulk LY | Byte 20 of the init reply: 5 is the 11.3" (1920 × 400), 2 or 3 the 9.16" v2, else the 9.16" |
| `0416:5409` | TrofeoBulk LY1 | The id alone; the start takes the size the panel reports |
| `0416:5406` | ALi (bulk, RGB565), or a SCSI panel | Told apart by the device's interfaces without I/O: mass storage is SCSI. ALi's device type 54 is 320 × 240, any other 320 × 320; with no reply, frames carry 1.4's 204,800 bytes (320 × 320), a smaller frame padded with zeros |

A reply that names no model of its protocol (a new PM, say, or an identifier that is another protocol's) gives an unknown model named by its code (`pm-200-sub-9`): the panel is listed, and runs once the family setting `model` names a model of its protocol to drive it as. A HID panel is never taken for the first model with its ids.

## Frames

- **Formats**: JPEG for most; RGB565 for the small panels, little-endian (`Rgb565LeTruncated`), but big-endian (`Rgb565Be`) for the ChiZhu 320 × 320 panels and the HID panel with PM `0x20`. The engine converts RGB565 by truncation, never rounded or dithered, as 1.4 (and TRCC) did.
- **JPEG**: quality 95 (1.4's default); a frame over the panel's buffer (400,000 bytes over HID, 230,000 over TrofeoBulk) is encoded again 5 lower, down to 50, and one still over it isn't sent.
- **Brightness**: these panels have no light of their own; the engine dims the frame.
- **Frame rate**: at most 30 fps.
- **Pacing**: ChiZhu waits 15 ms after each frame; HID 1 ms (20 ms after an RGB565 frame, with 1 ms after every 32nd packet); TrofeoBulk reads an acknowledgement after each frame and ends the session after five missing in a row; ALi reads one after each frame and ends it on the first missing.
- **Zero-length packets**: ChiZhu sends one after a frame that fills its last 512-byte packet.
- **Stop**: nothing is sent, as in 1.4 (there is no screen-off command): the panel keeps its last frame.

## Family settings

| Setting | Values | Models |
|---|---|---|
| `displayMask` | `None` (default), `RoundedLeft`, `RoundedAll`: 1.4's own names, so a 1.x panel's `DisplayMask` is the value as it stands | The 6.67" Wonder, Rainbow and Levita Vision 360: a PNG the engine draws over the camera's punch-hole at each rotation (the Levita's turned 180° more), as 1.4 drew it. 1.4's 90° and 270° masks are portrait PNGs (720 × 1600) that 1.4 drew at the top left of the landscape frame, clipped to it, so only their top 720 rows show, at the frame's left (the rounded-left mask at 270° shows nothing at all): likely a 1.4 bug, kept as it is. The package holds what 1.4 drew as panel-size PNGs (`mask_rounded_*_{90,270}_clipped.png`, made from 1.4's by the tests' generator and proven pixel for pixel against 1.4's drawing); a rotation change while the panel runs switches the mask with the next frame |
| `flickerFix` | `false` (default), `true` | The 9.16" TrofeoBulk panels: rendered at 480 rows, the top 462 sent |
| `model` | a model id of the family | Any: the panel is driven as that model, at its size (a panel whose ids name another size; an unknown model) |

They are declared in `panel.json`'s `settings`, `displayMask` and `flickerFix` with their 1.x elements as `v1Field` (`DisplayMask`, `FlickerFix`), so a 1.x panel's settings come over with it; a 1.x panel saved under an older model name (`TrofeoVision686`) comes over through the model's `v1Aliases`. A changed setting opens the session again.

## Operating systems

- **Windows**: the bulk panels (ChiZhu, TrofeoBulk, ALi) need the WinUSB driver bound, as in 1.4. HID panels need nothing.
- **macOS**: no driver needed.
- **Linux**: `70-infopanel-thermalright.rules` (in the package) gives the user at the seat access to the family's ids: copy it to `/etc/udev/rules.d/` and reload udev.
- **SCSI panels**: `0416:5406` as mass storage is listed and not driven: macOS and Linux can't reach it ("Windows only"); on Windows it comes later, with the other two SCSI ids (`87cd:70db`, `0402:3922`), which aren't matched yet.

## Testing on a panel

`ThermalrightHardwareTests` runs only with `INFOPANEL_PANEL_HW=thermalright` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`; with `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector. It drives the panel through the engine's own device seam, built as the panel host builds it (libusb, HID, serial).

Without a panel, the tests run the driver over the test kit's fakes: byte vectors against 1.4.x's own functions, the start, frame and stop traces, the conformance suite for every protocol and format, the runner test (the engine's transform, dimming, the mask's overlays, the flicker fix's crop and the JPEG cap's encodes again, to the device's bytes), the 1.x import, and the package read as the engine reads it.
