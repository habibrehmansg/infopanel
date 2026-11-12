# Turing Smart Screen / TURZX panels

The `turing` family, package `InfoPanel.Panels.Turing` (which also holds the [`lianli`](lianli.md) family): InfoPanel 1.4's Turing panels, serial and USB, ported from the 1.4.x tip. **Unverified on hardware**: every protocol is checked byte for byte against the code 1.4.x ran (its own, and the source of the library it shipped as DLLs) in 1.4.x's call order, but no panel of the family has run on 2.0 yet. A tester's run (below) is what changes that.

## Models and how a panel is told

| Model id | Name | USB id | Transport | Size | 1.x model |
|---|---|---|---|---|---|
| `turing35` | Turing Smart Screen 3.5" | `1a86:5722` | serial, revision A | 320 × 480 | `TURING_3_5` |
| `xuanfang35` | XuanFang 3.5" | `1a86:5722` | serial (not driven) | 320 × 480 | `XUANFANG_3_5` |
| `rev2inch` | Turing Smart Screen 2.1" | `1d6b:0121` | serial, revision E | 480 × 480 | `REV_2INCH` |
| `rev5inch` | Turing Smart Screen 5" | `1d6b:0106` | serial, revision C | 800 × 480 | `REV_5INCH` |
| `rev8inch` | Turing Smart Screen 8.8" Rev 1.0 | `0525:a4a7` | serial, revision E | 480 × 1920 | `REV_8INCH` |
| `rev88inchusb` | Turing Smart Screen 8.8" Rev 1.1 | `1cbe:0088` | USB | 480 × 1920 | `REV_88INCH_USB` |
| `rev8inchusb` | Turing Smart Screen 8" | `1cbe:0080` | USB | 800 × 1280 | `REV_8INCH_USB` |
| `rev92inchusb` | Turing Smart Screen 9.2" | `1cbe:0092` | USB | 464 × 1920 | `REV_92INCH_USB` |
| `rev46inchusb` | Turing Smart Screen 4.6" | `1cbe:0046` | USB | 320 × 960 | `REV_46INCH_USB` |
| `rev5inchusb` | Turing Smart Screen 5" | `1cbe:0050` | USB | 720 × 1280 | `REV_5INCH_USB` |
| `rev16inchusb` | Turing Smart Screen 1.6" | `1cbe:0016` | USB | 400 × 400 | `REV_16INCH_USB` |
| `rev21inchusb` | Turing Smart Screen 2.1" | `1cbe:0021` | USB | 480 × 480 | `REV_21INCH_USB` |

`panel.json` is the one table: each model carries its USB id (`usb`), its transport, and a serial model its revision. The USB id names the model, with one exception: the 3.5" and the XuanFang 3.5" share a CH340 serial chip's id, and 1.4 drove only the 3.5" (revision A). So the probe takes that port as the 3.5"; a panel saved as a XuanFang (from a 1.x import) answers `notSupported`, and its `model` setting ("Try it as…") can try it as the 3.5", and as no other model. A 1.x panel saved under an older name comes over through 1.4's own 1.3.1 rename (`REV_8INCH_USB` on `1cbe:0088` is the 8.8" Rev 1.1).

- **USB panels**: the probe asks nothing; the device's cached descriptors must show interface 0.
- **Revisions C and E** sit on USB gadget ids other devices use too (`1d6b`, `0525`), so the probe asks the port Hello, as the library's open did, and takes it only when the answer starts `chs_5inch` (C) or `chs_` (E); then lets the port go.
- **The 3.5"** sleeps until its port is opened: 1.4 opened and closed every `1a86:5722` port five times, a second apart, each time it looked for panels (its loop counted every such port, so it never ended early). Revision A has no query, so the probe takes the port at once, asking nothing: nothing tells a 3.5" from another CH340 on that id, as in 1.4. Its start wakes it first, the same five opens and closes a second apart (about 4 s; each open given a second, so at most 9 s, within a start's deadline), every start, remembering nothing of an earlier one; a panel that goes away meanwhile fails the start as gone.

No model gives a serial number, so a panel is told from another of its model by its USB port.

`detect` lists `1cbe:00??`, Turing's USB range, so a new Turing USB panel shows as detected, not supported.

## The serial protocol (revisions A, C and E)

Ported from TuringSmartScreenLib (its notice is in `LICENSES.md`), the library 1.4 drove these panels with, at the commit 1.4's DLL was built from. The port is opened at 115200 baud, DTR and RTS on, its input dropped first.

- **Revision A** (the 3.5"): six-byte commands, the command in byte 5: screen on 109, off 108, brightness 110 with the level in bytes 0–1 (the level `255 − percent × 2.55`, 0 the brightest). A bitmap is command 197 with its corners packed into bytes 0–4, then its pixels as RGB565 little-endian, each channel's low bits dropped (`Rgb565LeTruncated`). The library gave these writes no timeout, so each gets the device seam's longest, 60 s. A whole frame (300 KB at 115200 baud, about 27 s) goes as bands of 16 rows (10 KB, under a second each), each its own bitmap command at its place, as the library drew any part of the screen.
- **Revisions C and E** write in 250-byte blocks: at most 249 bytes of a command or of pixels, then padding. Hello is `01 ef 69 00 00 00 01 00 00 00 c5 d3`, answered with text ending in a 0; brightness is `7b ef 69 00 00 00 01 00 00 00` and the level 0–100 (on and off are 100 and 0). Every frame, a whole one too, goes in blocks of at most 80 × 80: UpdateBitmap with the block's size and a render count (counted from the screen's start), each row's position (24 bits) and width, its pixels (C: blue, green, red; E: blue, green, red, 255), `ef 69`, then QueryStatus, whose answer C takes as success only when it starts `needReSend:0` and E unless it starts `needReSend:1`. Each write and read waits at most 1000 ms; a read that brings nothing in that time ends the answer, so each block takes about a second. The library sent a frame of the screen's own size as one command instead (`2c` padded with `2c`, the display command, the whole payload, then PreUpdate and QueryStatus, each answer read): the 8.8" Rev 1.0's 3.7 MB about 5.7 s at 1 MB/s, the 5"'s about 3.5 s, longer than a write may take (below). The driver doesn't send it: the 2.1", on the same 480 × 1920 screen, never got it from the library, so revision E draws without it; that the 5" (C) does too is not yet seen on a panel. A read that finds the panel gone ends the frame there: nothing more is sent.
- **The 2.1"**: 1.4 made its revision E screen without a size, so the library's default, 480 × 1920; a 480 × 480 frame is then never "whole" and always goes as 80 × 80 blocks at the right rows. Kept as 1.4 did it.

**Frames**: the engine sends each frame whole, in the revision's format (A: `Rgb565LeTruncated`; C and E: `Bgr888`, which the driver gives E's fourth byte). The driver keeps the last frame it sent and, as 1.4 did, sends the first frame whole, then only the sectors that changed (A: 20 × 20, merged up to 40 × 40; C: 20 × 20 up to 120 × 80; E: 32 × 32 up to 128 × 96), each at its place, and the whole frame again when more sectors changed than the revision's cap (A 76, C 30, E 38) or when the panel answered the last sector with a request to send it again. 1.4 compared the rendered pictures; the driver compares what it sent the panel with the newest frame, which change where the pictures do.

A write starts bands, sectors or blocks for 1.2 s, then returns: the package runs in a panel host, whose writes the engine ends at 5 s, and a stop asked while a write is under way waits for it, while the engine gives the screen-off 3 s from the ask. A whole frame takes far longer (revision A's about 27 s; C's and E's blocks about a second each: the 5"'s 60, the 8.8" Rev 1.0's 144, the 2.1"'s 36). A write stops between pieces, never inside one, so it ends within about 2.2 s (1.2 s and its last piece: a band of A, about 0.9 s at 115200 baud; a block of C or E, about a second at a USB serial port's 1 MB/s), and the screen-off, one short command, follows within the stop's 3 s. It keeps what it sent, and the next write (a new frame, or the engine's keep-alive a second later) sends the rest in the newest frame's pixels, then the sectors a newer frame changed in what went earlier: a whole frame arrives over many writes (the 8.8" Rev 1.0's over about 70, at least two and a half minutes). A keep-alive with nothing left to send sends nothing: the panel keeps its picture. **The stop** turns the screen off (A: 108; C and E: brightness 0) and closes the port. At most 30 frames a second, the top of 1.4's Target FPS slider; a frame takes as long as its bytes do at 115200 baud, and the engine paces the panel by that.

## The USB protocol

Shared with Lian Li's panels ([lianli.md](lianli.md)), in its Turing variant, as 1.4 drove Turing's USB panels: through the library's `ScreenDevice`, which 1.4 shipped as a DLL (`LcdDriver.TuringSmartScreen`). A command is 500 bytes: its id in byte 0, `26 109` in bytes 2–3, a timestamp (the local time of day in milliseconds) little-endian in bytes 4–7, its arguments from byte 8. It is zero-padded to 504 bytes and encrypted with DES-CBC, key and IV `slv3tuzx`, no padding added, into a 512-byte packet ending `A1 1A`. Interface 0, endpoints 1 out and 1 in.

- **Each command**: what the device left on the in endpoint is read away first (LibUsbDotNet's `ReadFlush`, 10 ms reads until one brings nothing, at most 128; a read that fails, or packets still coming at the 128th, fail the command unwritten, so no stale packet is taken for its answer), the packet is written, an image's bytes follow as a write of their own, and one 512-byte answer is read; each transfer within 1500 ms. An answer that doesn't come, or comes short, is a failure.
- **The start**: Sync (10), a pause of 200 ms, Sync, 200 ms, StopMedia (111), 200 ms, the brightness (14, 0–100 in byte 8), Sync, 200 ms. A Sync unanswered fails the start; the other commands' answers aren't looked at, as 1.4 didn't, but one that couldn't be sent (its flush or its write failed) fails it.
- **A frame** is one JPEG: command 101 with its length big-endian in bytes 8–11, then the JPEG. Quality 90, 1.4's default for these panels; at most 30 frames a second.
- **Brightness**: 14, then Sync (whose failure ends the session), then 200 ms. A brightness that couldn't be sent fails the change there, so the engine keeps the old one.
- **The stop**: brightness 0, then the interface let go. A start that failed after its first Sync was answered sends it too, as 1.4's did; a device that never answered a Sync is only let go (1.4 sent it the stop as well, though it may not be a panel at all).

1.4.x also carries a Turing USB class of its own (`TuringPanel/TuringDevice.cs`), which it never called; its command bytes for 10, 14 and 101 are the library's.

## Operating systems

- **Windows**: the USB panels need the WinUSB driver bound to them (Zadig, or the panel vendor's tool), as 1.4 did. The serial panels need their port's driver: the CH340's usually comes through Windows Update; the 2.1", 5" and 8.8" Rev 1.0 are standard USB serial (CDC) devices.
- **macOS**: no driver needed (the CH340 has one built in on current macOS).
- **Linux**: `70-infopanel-turing.rules` (in the package) gives the user at the seat access to the family's USB devices and serial ports (its name sorts before systemd's `73-seat-late.rules`, which grants that access to what is tagged by then): copy it to `/etc/udev/rules.d/` and reload udev.

## Testing on a panel

`TuringHardwareTests` runs only with `INFOPANEL_PANEL_HW=turing` and `INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port`: it probes the panel, starts it (a 3.5" woken first), shows colour bars, a gradient and a moving band (a serial panel's sector updates, each frame's rest sent by keep-alives a second apart), changes the brightness, and stops it. With `INFOPANEL_PANEL_TRACE=<file>` it records every transfer, which can become a test vector. It builds the engine's device seam as the panel host does, over libusb and the OS's serial ports; an opted-in run that can't (no libusb, no panel named) fails.
