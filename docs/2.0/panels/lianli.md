# Lian Li panels

The `lianli` family, in package `InfoPanel.Panels.Turing` beside the [`turing`](turing.md) family, whose USB protocol it shares: InfoPanel 1.4's Lian Li LCD panels, ported from the 1.4.x tip (`LianLiPanel/LianLiUsbScreenDevice.cs`, `Services/LianLiPanelDeviceTask.cs`). **Unverified on hardware**: the protocol is checked byte for byte against 1.4.x's own code in its call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

## Models and how a panel is told

| Model id | Name | USB id | Size | 1.x model |
|---|---|---|---|---|
| `universalscreen88inch` | Lian Li Universal Screen 8.8" | `1cbe:a088` | 480 × 1920 | `UniversalScreen88Inch` |
| `universalscreen92inch` | Lian Li Universal Screen 9.2" | `1cbe:a092` | 464 × 1920 | `UniversalScreen92Inch` |
| `hydroshiftiioledcurve` | Lian Li HydroShift II OLED Curve | `1cbe:a068` | 2288 × 1080 | `HydroShiftIIOledCurve` |
| `hydroshiftiilcd` | Lian Li HydroShift II LCD | `1cbe:a034` | 480 × 480 | `HydroShiftIILcd` |

`panel.json` is the one table: each model carries its USB id (`usb`), which alone decides the model, so the family has no unknown model and no `model` setting. The probe asks nothing; the device's cached descriptors must show interface 0. No model gives a serial number, so a panel is told from another of its model by its USB port. `detect` lists `1cbe:a0??`, Lian Li's range, so a new Lian Li panel shows as detected, not supported.

## The protocol

The encrypted USB protocol Turing's USB panels speak too, in its Lian Li variant. A command is 500 bytes: its id in byte 0, `26 109` in bytes 2–3, a timestamp little-endian in bytes 4–7 (milliseconds since the day before the program started, as 1.4 counted), its arguments from byte 8. It is encrypted with DES-CBC, key and IV `slv3tuzx`, with PKCS7 padding (Turing's variant pads with zeros and adds none, so the two differ in their packets' last cipher block), into a 512-byte packet ending `A1 1A`. Interface 0, endpoints 1 out and 1 in.

- **Each command**: what the device left on the in endpoint is read away first (LibUsbDotNet's `ReadFlush`: reads until one brings nothing, at most 128; a read that fails, or packets still coming at the 128th, fail the command unwritten, so no stale packet is taken for its answer), then the packet and any image's bytes go as one write (1.4 found that these panels may stop answering when the two are sent apart), within 2000 ms, and one 512-byte answer is read within 2000 ms; an image's answer within 200 ms, and its absence is success, as 1.4 took it.
- **Flow control**: an image's answer carries in byte 8 how many frames the panel holds; above 3, the panel is asked (QueryBlock, 122) every 2 ms until it holds 2 or fewer, or an answer doesn't come in its time, at most 1000 times. A QueryBlock that couldn't be sent, or whose read failed otherwise, fails the frame.
- **The start**: Sync (10), 200 ms, Sync, 200 ms, StopMedia (123), 200 ms; then the layers are prepared: the clock (51: the year big-endian, month, day, hour, minute, second, then 2), 50 ms, StopClock (52), 50 ms, a transparent PNG of the panel's size on the overlay layer (102), 50 ms, a black JPEG on the background layer (101), 50 ms, the frame rate (15, 30 frames a second); then 200 ms, the brightness (14, 0–100 in byte 8), Sync, 200 ms. A Sync unanswered fails the start; the other steps' answers aren't looked at, but a step that couldn't be sent (its flush or its write failed) fails it. 1.4 encoded the two clearing images with its image library; the package writes them itself (a transparent RGBA PNG and a one-channel baseline JPEG): other bytes, the same pictures.
- **A frame** is one JPEG: command 101 with its length big-endian in bytes 8–11, then the JPEG. Quality 90, 1.4's default for these panels; at most 30 frames a second. A JPEG over 512,000 bytes is never sent: the engine encodes again at a lower quality until it fits (`MaxFrameBytes`).
- **Brightness**: 14, then Sync (whose failure ends the session), then 200 ms. A brightness that couldn't be sent fails the change there, so the engine keeps the old one.
- **The stop**: StopMedia, then brightness 0, then the interface let go. A start that failed after its first Sync was answered sends them too, as 1.4's did; a device that never answered a Sync is only let go (1.4 sent it the stop as well, though it may not be a panel at all).

1.4 set the panel's frame rate from the panel's own Target FPS; the engine doesn't pass a panel's rate to its driver, so the start sends 30, 1.4's default.

## Operating systems

- **Windows**: the panels need the WinUSB driver bound to them (Zadig, or L-Connect's own), as 1.4 did.
- **macOS**: no driver needed.
- **Linux**: `70-infopanel-turing.rules` (in the package) gives the user at the seat access to the family's devices (its name sorts before systemd's `73-seat-late.rules`, which grants that access): copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`TuringHardwareTests` runs only with `INFOPANEL_PANEL_HW=lianli` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: it starts the panel, shows colour bars, a gradient and a moving band, changes the brightness, and stops it. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector. It builds the engine's device seam as the panel host does, over libusb; an opted-in run that can't (no libusb, no panel named) fails.
