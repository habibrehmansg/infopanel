# Jungle Leopard / Hongtai Cooler Display Protocol

This document describes the wire protocol used by Jungle Leopard branded
cooler displays and the ~80 white-label SKUs that share the same Hongtai
reference firmware (MSI EZ Display, Thermaltake LCD, ZOTAC, Jonsbo, and
many more).

The protocol was reverse-engineered from `Jungle Leopard Display.exe`
v1.0.52 (Electron app, unpacked from `resources/app.asar`).

## Transport

| Property | Value |
| --- | --- |
| Class | USB CDC ACM (virtual COM port) |
| VID | `0x33C3` |
| PID | `0x7788` (reference) — `0x7791..0x7810` for OEM SKUs |
| Baud rate | **2 000 000** (`0x1E8480`) |
| Flow control | none |
| Resolution | 480×960 (Chill Arc 360) — also 1920×462 strip variant |

There is also an unrelated CH340-based SPI variant (PID `0x7793` behind
VID `0x1A86` / PID `0x8040`) that uses raw RGB565 at 921 600 baud. That
one is **not** supported by this driver — it is a different transport.

## Command frame format

Every host-to-device command is wrapped in this frame:

```
+--------+--------+----------------+--------+-------------------+----------------+
| 0x55   | 0xAA   | length (LE u16)| cmd u8 | payload (N bytes) | checksum LE u16|
+--------+--------+----------------+--------+-------------------+----------------+
   2 bytes magic     2 bytes         1 byte      N bytes               2 bytes
```

* `length` = total frame size in bytes = `N + 7`
  (magic 2 + length 2 + cmd 1 + payload + checksum 2).
* `checksum` = `sum(all_preceding_bytes) & 0xFFFF`, written little-endian.
  It is a plain u16 sum, **not** a CRC.
* No escaping — the fixed length field tells the parser when a frame ends.

### Response frame
The device replies with the same outer shape:

```
[0x55 0xAA] [len_lo len_hi] [cmd] [payload] [chk_lo chk_hi]
```

For successful informational replies the payload is UTF-8 JSON. Status /
error replies are a single byte:

| Byte | Meaning |
| ---- | ------- |
| `0x01` | operation failed |
| `0x02` | out of memory |
| `0x03` | internal storage full |
| `0x04` | SD card full |
| `0x05` | file not found |
| `0x06` | file open failed |
| `0x07` | file write failed |

## Command opcode table

| Opcode | Action | Payload | Notes |
| -----: | --- | --- | --- |
| `0x01` | restart | none | Soft reboot of the panel MCU |
| `0x03` | setLight | `[brightness 0..100]` | Backlight; 1 byte |
| `0x06` | getDeviceInfo | none | Reply is JSON: `{uid, model, version, width, height, angle, region, ...}` |
| `0x0C` | OTA begin | `[0xF2,0xFF, size_le_u32, 0,0,0,0]` (10 B) | Followed immediately by raw `.bin` chunked write |
| `0x11` | live frame ack / refresh | none | Sent before each live image and every 1500 ms while streaming |
| `0x14` | setMotionBeforeOffScreen | `[mode, secs]` | Firmware ≥ 4.1 |
| `0x15` | setMotionTimeout | `[mode, secs]` | Firmware ≥ 2.8 — auto-off |
| `0x20` | setRegion | UTF-8 string (region/profile name) | Selects a firmware-side preset |
| `0x21` | close | none | Graceful shutdown, firmware ≥ 3.1 |
| `0x23` | setSerialNum | UTF-8 string | Followed by an `0x01` restart |
| `0x25` | setMotor | `[0|1]` | Pump motor toggle (only for region `sulcs`) |
| `0x26` | setRealTimePlayTimeout | `[secs]` | Firmware ≥ 4.1 |

## Image upload (live streaming)

Live mode is JPEG over the same serial port. After connecting, the host:

1. Sends `0x11` (refresh) to wake the live pipeline.
2. Captures and resizes a frame to `width × height` (480×960 for Chill Arc 360).
3. Encodes as JPEG, dropping quality until it fits in the firmware's
   `maxSize` budget (~80 KB for the Chill Arc).
4. Wraps the JPEG in a *data* envelope (firmware ≥ 2.8):

   ```
   +----------------+-------------------+----------------+
   | size LE u32    | JPEG bytes        | sum LE u16     |
   +----------------+-------------------+----------------+
      4 bytes          variable             2 bytes
   ```

   `sum = sumOf(size_field || jpeg_bytes) & 0xFFFF`, little-endian.

5. Writes the envelope to the serial port (the JL Display app uses
   20 KB chunks for non-live writes; for live, the whole envelope is
   written in one go).
6. Pings `0x11` again every 1500 ms; otherwise re-sends pictures
   continuously at the configured frame rate.

A frame is **terminated** by writing the literal bytes `FF D9 FF D9`
(two JPEG end-of-image markers back-to-back). Connecting also flushes
the parser with `FF D9 FF D9 00 00 00 00`.

## Connect / handshake sequence

```
open serial @ 2 Mbaud
sleep 200 ms
write FF D9 FF D9 00 00 00 00       # pipeline reset
sleep 200 ms
send cmd 0x06 (getDeviceInfo)        # expect JSON reply with uid, w, h, ver
   if status != 200:  abort and retry
   else: store deviceInfo
optionally send 0x03 (setLight)
enter live loop (0x11 keep-alive + JPEG envelope per frame)
```

A successful `getDeviceInfo` reply looks like:

```json
{
  "status": 200,
  "uid": "<serial>",
  "model": "TXW818-ST7701S-4.0inch",
  "version": "Ver1.0",
  "width": 480,
  "height": 960,
  "angle": 0,
  "region": "sulcs"
}
```

## Important constants

* **Frame magic:** `0x55 0xAA` (host → device and device → host)
* **Image envelope ≥ v2.8:** `[u32 size_le][bytes][u16 sum_le]`
* **End-of-image marker:** `FF D9 FF D9`
* **Pipeline reset:** `FF D9 FF D9 00 00 00 00`
* **Keep-alive:** `0x55 0xAA 07 00 11 11 01` (cmd `0x11`, no payload, sum = `0x111`)
* **Default live rate:** 60 fps for v ≥ 2.8 displays, 30 fps below
* **Max JPEG size (Chill Arc 360):** 80 KB

## Implementation notes for InfoPanel

The driver lives under `InfoPanel/JlPanel/` (model definitions and
serial communication) and `InfoPanel/Services/JlPanelDeviceTask.cs`
(per-device render+send loop). The settings collection is
`Settings.JlPanelDevices` and the multi-device toggle is
`Settings.JlPanelMultiDeviceMode`.

Key behaviors implemented:

* The render thread produces JPEGs at the user's target frame rate;
  the send thread drains them to the serial port.
* When no frame is available for ≥1 s, the send thread issues a bare
  `0x11` keep-alive so the firmware doesn't drop us back to the boot
  animation.
* Frame size is capped at ~80 KB; quality is automatically reduced
  (down to 30) until the JPEG fits.
* Brightness changes (0–100) are pushed via cmd `0x03` whenever the
  user moves the slider, plus once on connect.
* On disconnect/dispose, the driver sends cmd `0x21` (close) so the
  firmware knows to release the live pipeline cleanly.
