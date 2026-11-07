# USB panels

The engine drives small USB screens (panels) itself: each shows one profile, live, as the preview does. BeadaPanel is the first family; others follow.

## What is driven

- **BeadaPanel**, every model InfoPanel 1.4 knows (21; their ids, `2` to `Z`, are in `InfoPanel.Core/Panels/BeadaPanel/BeadaPanelModel.cs`), over USB through libusb-1.0, as InfoPanel 1.4 speaks to them byte for byte: the info query, the start sequence, frames in RGB565, brightness, and the screen turned off when a panel stops. Panels known to decode JPEG get JPEG frames instead (below), about twice the frame rate on a 1920 × 480 panel.
- **libusb-1.0** is looked for, by full path: beside the engine, then in the folder `INFOPANEL_LIBUSB_DIR` names (a full path), then on macOS in `/opt/homebrew/lib` and `/usr/local/lib`, and on Linux the system's `libusb-1.0.so.0`. Without it panels are unsupported, with one line in the log, and nothing else changes. On macOS, `brew install libusb` puts it in place.
- **Another program holding a panel** keeps it: the engine never resets a device or takes it from a driver, sets its configuration only when it has none, and a panel it can't claim is tried again every second with one line in the log.
- **`--no-panels`** turns all of it off: no USB device is listed, opened or written.

## How it runs

- Every second the engine lists the USB devices. A BeadaPanel it hasn't seen is identified by its info query and matched to the panels it saved, by serial, else (a panel without one) by its USB port. Panels that report the same serial are told apart by the port each was last seen at; one that still can't be told apart (at a port none of them had) is listed as a new panel, with a line in the log. A new one is saved **off, without a profile**: the log says `BeadaPanel {model} found, serial …, at {port}: saved, off`.
- A saved panel runs while it is plugged in, turned on, and its profile exists and can be drawn. It shows the profile at its own size: turned by its rotation, scaled to fit and centred, the rest black. Frames come at the engine's frame rate (Settings › Engine), slowed to what the panel takes: a panel that needs 48 ms a frame is asked for at most 23 a second. A frame that didn't change isn't sent, and the last one is sent again every second.
- The screen goes off (backlight off, a black frame, the stream's end) whenever a panel stops: turned off, unplugged, its profile deleted, or the engine quitting (at most 3 s for every panel together, counted from the quit). A deleted profile leaves the panel off, its profile kept, until a profile with that id comes back. Changing a running panel's profile moves it to the new one without turning the screen off.
- **Frames as JPEG.** A panel's `encoding` is `auto` (the default), `jpeg` or `raw`. `auto` streams JPEG to the panels it has been seen to work on: a BeadaPanel on the T113 platform with PanelLink 2 or later (the BeadaPanel 8 and its 1920 × 480 kin); every other panel gets raw RGB565, as 1.4 sends it. Each frame is one JPEG at quality 90, encoded from the turned and fitted frame, and the stop's black frame is a JPEG too. On a BeadaPanel 8 that is about 35 frames a second against raw's 16, for some 3–4 ms of encoding a frame. A panel that doesn't take the JPEG stream's start has its stream reset, as at the start, and is sent raw instead, with one line in the log, and stays raw until the engine restarts. `jpeg` tries JPEG on any panel, for one that isn't known yet; `raw` never does. After a JPEG stream ends, a BeadaPanel 8 takes no new stream for 20 to 30 s, so a panel turned off and on again, or set to another encoding, starts again 30 s after its JPEG stream ended, its state `waiting` until then (another engine started on the panel sooner retries until the panel answers).
- Unplugged, a panel stays saved; plugged in again it is found and runs again: in any port when it has a serial of its own, at the port it was saved at when it has none (or shares its serial with another saved panel).
- The log shows each panel starting, its frame rate and time a frame 10 s after it starts and then every 5 minutes, and why one failed (it is tried again every second).

## `devices.json`

The saved panels are in [`devices.json`](data-folder.md#the-panels) in the data folder:

```json
{"format": "infopanel.devices/1", "devices": [
  {"id": "0f8e4a52-6c1b-4f38-9a57-2d1c3b4e5f60", "family": "beadapanel", "model": "8", "serial": "934072005c00481401044f2420751fd6",
   "port": "0-1", "enabled": false, "profileId": null, "rotation": 0, "brightness": 100, "encoding": "auto"}
]}
```

## Turning a panel on

Until clients can change it, a panel is turned on in the file:

1. Plug the panel in with the engine running, and wait for its `found … saved, off` line in the log.
2. Quit the engine.
3. In `devices.json`, set the panel's `enabled` to `true` and its `profileId` to the id of the profile to show (the profile's folder name under `profiles/`). Optionally set `rotation` (0, 90, 180, 270), `brightness` (0–100) and `encoding` (`auto`, `jpeg`, `raw`).
4. Start the engine: the panel lights up within a second or two.

A file the engine can't read (not JSON, a value out of range) is left as it is, and panels stay off for that run, with the reason in the log. An `encoding` it doesn't know reads as `auto`.
