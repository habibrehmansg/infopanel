# VMAX panels

The `vmax` family, package `InfoPanel.Panels.Ms9132Jonsbo`: InfoPanel 1.4's VMAX 4.6" on a MacroSilicon MS9132 USB display bridge, ported from the 1.4.x tip. **Unverified on hardware**: the protocol is checked byte for byte against 1.4.x's own code and its call order, but no VMAX panel has run on 2.0 yet. A tester's run (below) is what changes that.

The package also holds the [Jonsbo](jonsbo.md) family, whose DS339 is on the same MS9132 (`345f:9132`).

## Models and how a panel is told

| Model id | Name | USB id | Size | 1.x model |
|---|---|---|---|---|
| `vmax46inch` | VMAX 4.6" LCD | `345f:9132` (MS9132) | 320 × 960 | `Vmax46Inch` |

`panel.json` is the one table; the model carries its mode index (`vic`, 178) and 1.4's default frame rate (`defaultFrameRate`, 1). The engine asks the `jonsbo` family first; it reads the EDID the bridge holds of its panel and passes on a size of 320 × 960, which this family's probe reads again and takes. Any other size is the Jonsbo family's (see [Jonsbo](jonsbo.md)). The HID interface reports no serial number, so a panel is told from another by its USB port.

**Screen on** (`screenSwitch`, 1.x's `ScreenSwitch`, on by default) is the family's one setting: once frames flow the session sets the panel's screen switch to it, and sets it again with the next frame until the panel takes it. A change reopens the session with it.

## The protocol

The MS9132's control plane is its HID interface: 8-byte feature reports, each set, then got back, as the vendor's app does. Frames go to a bulk OUT endpoint of the vendor interface 3 (`0x04`, or the last OUT endpoint the interface lists), in 64 KiB writes.

- **The start**: the video interface claimed; then the HID collections in turn until one takes the streaming start: the vendor app's captured block (registers, the power-up, the video off, the EDID read, the mode set at 320 × 960 and mode index 178), each report followed by its pause, 42 pulses 2 ms apart, the display enable, 25 ms, the status, 25 ms.
- **A frame**: a fixed 8-byte header (`FF 00 00 00 00 14 03 C0`: 320 / 16 wide, 960 high), the BGR888 pixels (1.4's `3d24d42` put the channels in this order), the trailer `FF C0` and six zeros; then the status, at most every 500 ms; then the screen switch, until it has taken. A frame's writes and reports keep to 4 s in all, inside the panel host's time for a call. Each reply must be the whole report; as in 1.4, which got every reply back and read none of the session's, it isn't checked further (a write answered with nine zero bytes is taken). Only the probe's EDID and status reads, which tell the bridge, must echo the opcode and address they answer (see [Jonsbo](jonsbo.md)). 1.4 let a failed status or screen switch pass; here one that times out or fails ends the session, and the engine opens it again: the engine closes a HID collection's stream when a report on it times out, so the session could no longer switch the screen on.
- **Frame rate**: at most 30 a second (1.4's `0b43898`, its tested limit), the session's `MaxFps`; the panel's own frame rate setting (1.x's `TargetFrameRate`, 1 by default there) and the engine's rate apply under it. 1.4's pipelining of the render and the write is the engine's runner's own.
- **Brightness**: no command; the engine dims the frame (`DimInSoftware`), as 1.4 applied it in its conversion. 1.4's slider ran 0 to 99 and 99 dimmed by 1 %; in 2.0 brightness is a percentage, and 100 doesn't dim.
- **The stop** sends nothing, as 1.4's dispose sent nothing: the panel keeps its last picture until the next session.

## Operating systems

- **Windows**: the HID interface needs no driver; the video interface 3 needs WinUSB bound (1.4 warned of it), else the start fails with `access`.
- **macOS**: no driver needed.
- **Linux**: `70-infopanel-ms9132jonsbo.rules` (in the package) gives the user at the seat access to the device and its `hidraw` nodes: copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`Ms9132HardwareTests` runs only with `INFOPANEL_PANEL_HW=ms9132` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: it asks the package's families in the engine's order, starts the panel the one that takes it, shows colour bars, a gradient and a band moving down, and stops it; a run whose setup can't complete fails. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector.
