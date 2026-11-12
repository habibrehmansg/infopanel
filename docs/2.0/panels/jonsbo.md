# Jonsbo panels

The `jonsbo` family, package `InfoPanel.Panels.Ms9132Jonsbo`: InfoPanel 1.4's Jonsbo displays, ported from the 1.4.x tip, the DS916 on a USB CDC serial port and the DS339 on a MacroSilicon MS9132 USB display bridge. **Unverified on hardware**: the protocols are checked byte for byte against 1.4.x's own code and its call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

The same package holds the [VMAX](vmax.md) family, because the VMAX 4.6" is on the same MS9132 (`345f:9132`): one package owns the shared USB id, and its families tell their panels apart by the EDID the bridge reads from its panel.

## Models and how a panel is told

| Model id | Name | USB id | Size | 1.x model |
|---|---|---|---|---|
| `ds916` | Jonsbo DS916 | `33c3:f101` (serial) | 462 × 1920 | `DS916` |
| `ds339` | Jonsbo DS339 | `345f:9132` (MS9132) | 376 × 960 | `DS339` |

`panel.json` is the one table. Each model carries its USB id (`usb`) and its transport (`serial` or `ms9132`); the DS339 its mode index (`vic`, 171); the DS916 its JPEG cap (`maxJpegBytes`). Both carry 1.4's default frame rate (`defaultFrameRate`) and its default rotation, 90° (`defaultRotation`, so a landscape profile fills the portrait panel), which the engine doesn't apply yet: a new panel starts at 0° until the user turns it, and a 1.x panel keeps the rotation 1.x saved.

- **The DS916** answers its handshake with its model code, its native size and its serial (`VMAXB160462*1920S261301227`). The model is the USB id's; the size and serial are the reply's, as 1.4 trusted the size the panel reports over its table's.
- **An MS9132** is asked for its EDID. A size of 376 × 960 is the DS339 (its EDID names it `VMAXB01`). A size of 320 × 960 is the VMAX 4.6": this family passes it on, and the `vmax` family, asked next, takes it. Any other size is the unknown model, named by it (`edid-480x480`), and an EDID that doesn't read is `edid-unread`: the panel is listed and starts once its `model` setting ("Try it as…") names the DS339, which 1.4's Jonsbo section did for any MS9132 the user added to it. Tried as the DS339, a panel runs at its EDID's size when 1.4's mode table has a mode index for it (480 × 480, 240 × 320, 320 × 240, 240 × 240, 480 × 272, 360 × 960, 376 × 960, 320 × 960, 600 × 1600), else at the DS339's 376 × 960.

The MS9132's HID interface reports no serial number to the engine, so a DS339 is told from another by its USB port.

`detect` lists `33c3:f1??` (Jonsbo's serial range around the DS916) and `345f:91??` (MacroSilicon's MS91xx bridges), so a new one shows as detected, not supported.

## The DS916's protocol

A USB CDC serial port, opened at a nominal 9600 baud (the firmware ignores the rate) with DTR and RTS raised, its input dropped.

- **The handshake**: `F0 A5 5A 0F`; the reply's first byte within 2 s, the rest for 300 ms after it, at most 64 bytes, read as ASCII: a 7-character model code, the width, `*`, the height, the serial. Silent, it is asked again after 1 s, as the vendor's app does.
- **A frame** is one JPEG, nothing around it, at quality 80 (1.4's default), 15 lower each try down to 30 to fit 256 KiB. With nothing new for 1 s the last frame is written again, so the panel doesn't fall back to its own animation.
- **Brightness**: no command is known; the engine dims the frame (`DimInSoftware`), as 1.4 did.
- **The stop** sends nothing (no command is known): the panel returns to its own animation.

The probe asks the handshake with a 1 s wait for its reply (1.4's start waited 2 s, which twice with its 1 s gap wouldn't fit the engine's probe time), again after 1 s, and takes the device as not a DS916 only after both went unanswered in their whole waits; a wait cut short by its time answers "couldn't ask now", and so does a reply whose 300 ms of gathering its time cut short (what came may be a part of it, its serial cut).

## The DS339's protocol (MS9132)

The MS9132's control plane is its HID interface: 8-byte feature reports, each set, then got back (report id 0, then the opcode, the address and data), as the vendor's app does. Frames go to the bulk OUT endpoint of the vendor interface 3 (`0x04`, or the interface's own), in 64 KiB writes.

- **The start** (1.4's `a2c8ed0`, the vendor app's capture): the HID collections in turn until one takes the startup block (registers, the power-up, the video off, the display control register, the panel's status, then the EDID, 32 reads of 4 bytes), each report followed by the capture's pause; the video interface claimed; the panel's status asked; the mode set for the EDID's size and its mode index (1.4's `0f81b8f`), ending at "accept frames"; then 400 ms before the first frame.
- **A frame**: an 8-byte header (`FF 00`, x / 16, y, the width / 16 rounded down, the height with bit 15 set), the BGR888 pixels, the trailer `FF C0` and six zeros; then the panel's status, and after the second frame the output turned on (1.4 found that turning it on before frames flow stalls the bulk pipe on some units). A frame's writes and reports keep to 4 s in all, inside the panel host's time for a call.
- **Replies**: each must be the whole report. As in 1.4, which got every reply back and read none but the reads', a reply isn't checked further in the start or in frames: a write answered with nine zero bytes is taken. Only the probe's EDID and status reads, which tell the bridge, must echo the opcode and address they answer (below). 1.4 let a failed status pass; here a status or output enable that times out or fails ends the session, the panel left powered, and the engine opens it again: the engine closes a HID collection's stream when a report on it times out, so nothing more would reach the panel through it.
- **Brightness**: no command; the engine dims the frame, as 1.4 dimmed it in its conversion.
- **The stop** powers the panel off (`A6 07 00`), as 1.4's dispose did, its 105 ms pause kept whole (its reply's wait is what the stop's time leaves after it); after a failed transfer, mode set or control report it leaves it powered, as 1.4's retry did, so a failing panel isn't seen flickering off and on. The engine opens it again on its next pass. A device gone while its start opens the video interface is sent nothing more.
- **The EDID**: the probe's answer carries the EDID's size, clock, name and the whole block in hexadecimal (1.4's `3939e56`), so an unfamiliar panel can be identified from a log; the engine logs it only when no family takes the device. **Not yet**: 1.4 logged the block for every panel it started, and the engine doesn't yet log the probe's answer for a panel a family takes, so a DS339's or a VMAX's EDID isn't in the log.

The probe reads the EDID with its 32 reads alone. A collection that refuses a read, doesn't answer it in its whole wait, or answers with what isn't the bridge's reply (short, or not echoing the read) is sent nothing more; when none answers, the device isn't one of the families'. One that answered all 32 as the bridge, without an EDID that reads, gets 1.4's whole startup block (whose reads come after the power-up) and then 1.4's power-off, with the power-off's time kept back from the block: its set's whole wait and its whole 105 ms pause, its reply's wait taking what is left after the pause. In the block the status and EDID reads must echo as the first 32 did; its writes' replies need only be whole. A failure after the bridge answered (in the block, out of time, or of the power-off, one whose pause no longer fits included) answers "couldn't ask now", asked again on the next pass, and a failed block is followed by the power-off too unless the device went; after a report that timed out, the engine has closed the collection's stream, so that power-off doesn't reach the panel. The probe keeps to 4 s.

## Operating systems

- **Windows**: the DS916 is a CDC port and needs no driver. The MS9132's HID interface needs none; its video interface 3 needs WinUSB bound (as with 1.4, which used the vendor's libusb driver or WinUSB; Zadig binds it), else the start fails with `access`.
- **macOS**: no driver needed.
- **Linux**: `70-infopanel-ms9132jonsbo.rules` (in the package) gives the user at the seat access to the devices, their `hidraw` and `tty` nodes: copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`Ms9132HardwareTests` runs only with `INFOPANEL_PANEL_HW=ds916` (a DS916) or `INFOPANEL_PANEL_HW=ms9132` (a DS339 or a VMAX), and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: it builds the engine's device seam as the panel host does, starts the panel, shows colour bars, a gradient and a band moving down (JPEGs on the DS916), and stops it; a run whose setup can't complete fails. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector.
