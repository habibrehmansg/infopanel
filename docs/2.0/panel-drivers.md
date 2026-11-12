# Add a panel family

A panel family is one driver and the `panel.json` beside it, in a panel package. This guide takes a family from an empty folder to a pull request, tested without a device until the last step. What the engine does with a driver is in [panels](panels.md); how it finds, runs and bounds a package is in [plugins](plugins.md#panel-packages).

- [Two ways in](#two-ways-in)
- [The package](#the-package)
- [`panel.json`](#paneljson)
- [A rebadge is data](#a-rebadge-is-data)
- [The driver](#the-driver)
- [What the engine does for you](#what-the-engine-does-for-you)
- [Tests without a device](#tests-without-a-device)
- [On your panel](#on-your-panel)
- [What a pull request includes](#what-a-pull-request-includes)

## Two ways in

- **A package in this repository**, `InfoPanel.Panels.{Family}`: every release bundles it in `{engine folder}/plugins/`, so the family works out of the box. It comes as a pull request, which needs the contributor agreement signed once ([CONTRIBUTING.md](../../CONTRIBUTING.md#contributor-agreement)); 2.0 takes pull requests from its first release. This is the way for a family many people own.
- **A package of your own**, the same folder built and published by you: a user copies it into the data folder's `plugins/` and starts the engine again ([plugins](plugins.md#panel-packages)). No pull request is needed.

Both are the same code and the same tests; only where the package lives differs.

## The package

```
InfoPanel.Panels.Acme/
  InfoPanel.Panels.Acme.csproj
  panel.json
  AcmeDriver.cs
  70-infopanel-acme.rules
```

The project targets **.NET 10** (the engine's runtime; sensor plugins stay on .NET 8, so one project can't be both) and references the panel SDK without copying it, since the host gives the package its own:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\InfoPanel.Panels.Sdk\InfoPanel.Panels.Sdk.csproj" Private="false" />
  </ItemGroup>
  <ItemGroup>
    <None Update="panel.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="70-infopanel-acme.rules" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

It references nothing else: no USB, HID or serial library, no image library, no native DLL. The device is reached through the SDK's `IPanelDevices` alone (USB bulk transfers, HID reports, a serial port), the engine encodes every frame, and anything a protocol needs beyond that comes from .NET itself (DES, say, is `System.Security.Cryptography.DES`).

In this repository the package adds one line to `InfoPanel.slnx`, its tests go in `InfoPanel.Core.Tests/Panels/{Family}/`, and the family's notes in `docs/2.0/panels/{family}.md` (the protocol, the models, what each OS needs, and "unverified on hardware" until a log from a real panel says otherwise). The release scripts publish every `InfoPanel.Panels.*` project that has a `panel.json` (the virtual panel, a test fixture, excepted).

The tests run the driver from the package's own build output, where its `panel.json` is. A plain project reference would copy the package's DLL and `panel.json` beside the test assembly, where every package's manifest has the same name and the test assembly's own reading of `panel.json` would find one. So the test project references the package without copying it, marked as a panel package, one line in `InfoPanel.Core.Tests.csproj`:

```xml
<ProjectReference Include="..\InfoPanel.Panels.Acme\InfoPanel.Panels.Acme.csproj" Private="false" PanelPackage="true" />
```

The test project imports the kit's `PanelPackages.targets` (`InfoPanel.Core.Tests.csproj` already does), which writes the path MSBuild built each such package to into the test assembly, whatever the configuration, runtime or output layout; and one module initializer, `InfoPanel.Core.Tests/Panels/Testing/PanelPackagesUnderTest.cs`, has the test kit's `PanelPackageOutput` load each package from there:

```xml
<Import Project="..\InfoPanel.Panels.Testing\PanelPackages.targets" />
```

```csharp
[ModuleInitializer]
internal static void Use() => PanelPackageOutput.Use(typeof(PanelPackagesUnderTest).Assembly);
```

The tests then use the driver's types as any others (`new AcmeDriver(devices)`), and `PanelManifest.Read(typeof(AcmeDriver).Assembly)` reads the package's own `panel.json`. A package that isn't built fails the tests that reach its driver, saying so, and no other. A package of your own does the same in its own test project.

The udev rule lets a Linux user's session open the family's devices without root, one line a USB id:

```
SUBSYSTEM=="usb", ATTR{idVendor}=="1209", ATTR{idProduct}=="000a", TAG+="uaccess"
```

Name the rule file with a number below 73 (`70-infopanel-acme.rules`): systemd applies the `uaccess` tag to the user at the seat in `73-seat-late.rules`, and a rule that sorts after it (`99-…`) sets the tag too late, so an ordinary user still can't open the device.

## `panel.json`

`panel.json` is the family's one source of truth: the engine reads it when it starts, before any driver runs, so a family is listed, matched and imported from it even while its package is off, and the driver builds its matches and its model table from the same file (`PanelManifest`, below), never from a second table in code. Its bounds are in [plugins](plugins.md#panel-packages).

```json
{
  "format": "infopanel.panel-package/1",
  "sdk": "1.0",
  "families": [
    {
      "id": "acme",
      "name": "Acme panels",
      "match": [
        { "usb": "1209:000a" },
        { "usb": "1209:000b", "transport": "hid", "interface": 1 }
      ],
      "detect": [{ "usb": "1209:00??" }],
      "models": [
        { "id": "a35", "name": "Acme 3.5\"", "width": 480, "height": 320, "v1Model": "Acme35", "code": 3 },
        { "id": "a88", "name": "Acme 8.8\"", "width": 1920, "height": 480, "v1Model": "Acme88", "v1Aliases": ["Acme88v2"], "code": 8 }
      ],
      "settings": [
        { "id": "mask", "name": "Display mask", "type": "enum", "default": "none", "v1Field": "DisplayMask",
          "values": [{ "id": "none", "name": "None" }, { "id": "rounded", "name": "Rounded corners" }] },
        { "id": "flickerFix", "name": "Flicker fix", "type": "boolean", "default": false, "v1Field": "FlickerFix" },
        { "id": "model", "name": "Model", "type": "model" }
      ]
    }
  ]
}
```

- **`id`** is stored with every panel of the family and never renamed; **`name`** is what a client shows for a family it doesn't know.
- **`match`**: every USB `vendor:product` the driver drives, with its `transport` (`usb`, the default, `serial` for a USB CDC port, `hid`) and the `interface` it uses. The engine offers a device only to the drivers whose matches name it.
- **`models`**: every model the family knows, each with an `id` (stored with the panel), a `name`, its `width` and `height` in its own orientation, and `v1Model`, the model's name in InfoPanel 1.x, which the 1.x import maps a saved panel by; `v1Aliases` lists other names 1.x saved the same model under. Any other member is the driver's own (a detection code, a protocol variant, `code` above): the engine keeps it and the driver reads it.
- **`detect`**: the vendor's ids beyond `match`, as patterns, at most 64 (the vendor exactly; in the product id `?` one hexadecimal digit, or `*` alone for any product id). A device a pattern names and no family's match does is listed as "detected, not supported", so a user can report it. Detection reads only the descriptors the USB stack has already cached; it never opens, claims or sends anything to a detected device. (Listing USB devices at all goes through libusb's enumeration, which on some systems reads a new device's descriptors once, as it does for every panel family.) Hubs, hubs, boot keyboards and mice, and the vendors `1a86`, `1d6b` and `0525` never are ([panels](panels.md#detected-not-supported)).
- **`settings`**: the family's own settings, which each panel of it carries: `boolean`, `enum` (each value with an `id` and a `name`), `integer` (with `min` and `max`), `string` (at most 64 characters) and `model` (its values the family's models), each with an `id`, a `name`, a `default` and, when 1.x had it, `v1Field`, the 1.x setting it is imported from. The engine checks a change against them, saves it, and opens the session again with them in `PanelOpenOptions.FamilySettings`: capabilities are fixed for a session's life, so a change always reopens.

A family whose handshake tells the model (a code in its reply) may meet a code its table doesn't have. The probe then answers an identity whose model isn't in `models`, named by its code (`code-47`); the panel is saved with it and stays `notSupported` until its `model` setting names a known model, which the driver then uses for its size and protocol variant. A family whose USB id alone decides the model has no unknown model to meet and declares no `model` setting.

## A rebadge is data

Most new panels are an old protocol in a new box. Neither case below needs code:

- **A known protocol on a new USB id** is one line in `match` (and, while it isn't matched, it is often already listed as detected through `detect`).
- **A new model of a known family** (a new code in the handshake's reply, a new size) is one entry in `models`, with the detection key the driver already reads.

The manifest test (below) is its test: it reads `panel.json` as the engine does and checks the `v1Model` set against 1.x. A rebadge's pull request is the line, the test still green, and a log from the panel (the short form of the [template](#what-a-pull-request-includes)).

## The driver

A driver is two interfaces from `InfoPanel.Panels.Sdk`:

- `IPanelDriver`: its `Family` id, the devices it `Matches`, a `Probe` that tells one of its own panels (it may open the device and ask it, but never draws, and lets it go before it answers) and answers its identity (model, size, serial, firmware), and `Open`, which runs the start sequence and answers an `IPanelSession`.
- `IPanelSession`: its `Capabilities`, fixed for the session's life (size; pixel formats, the driver's preference first; the most frames a second; a JPEG cap, quality, floor and step; a brightness range or `DimInSoftware`; overlays and a crop; how often the last frame is written again), `WriteFrame` (one frame, answering `DeviceTime`, the time the transfer alone took: the engine paces the panel by it), `SetBrightness` (0–100, mapped to the device's range), and `Close`, which turns the screen off within the budget it is given and lets the device go. `SetPower`, `KeepAlive` and `Apply` are optional.

Two exceptions keep a 1.x driver's behaviour (JL's): a session may run at the size its device reports at the start rather than its model's, and a panel may declare both a brightness range and `DimInSoftware`, its light set and the frame dimmed as well.

Its public constructor takes one `IPanelDevices`, the device seam: the engine and the panel host pass theirs, a test passes the kit's fakes. (A constructor that takes nothing is allowed for a driver that reaches no device, as the virtual panel.) Through the seam a driver gets a device's cached descriptors, claims a USB interface for bulk transfers (a zero-length packet after a full write only when it asks for one), opens a HID collection by interface, report length and usage page, or opens the device's own serial port. There is no reset, detach, alternate setting or control transfer. Every call that can wait takes a timeout of 1 to 60,000 ms and the caller's cancellation token, answers a `PanelDeviceStatus` and never throws for the device's sake.

A driver keeps a few rules, which the conformance suite checks:

- It never retries: a call that doesn't answer `Ok` ends the session, and the engine opens it again a second later.
- A probe only asks; a session's calls come from one thread and block.
- A start honours its token (passed to each call that waits, or checked after each), one already cancelled when it begins too: cancelled, it throws `OperationCanceledException` with the screen off again and the device let go, and never answers a session.
- `DeviceTime` is the transfer's time, measured around the write, nothing else.
- An unplug answers `Gone`, and once the device is gone nothing more is tried.
- `Close(screenOff: true)` tries each step of its screen-off, none once the device is gone, each write's timeout what is left of the budget, and none started once it is spent; `Close(screenOff: false)` writes nothing. Nothing reaches the device after `Close`: no timer or thread of the driver's writes on.

### A whole driver

A 320 × 240 panel that takes raw RGB565 frames and a two-byte backlight command. It is a test in this repository (`InfoPanel.Core.Tests/Panels/Testing/ExampleDriver.cs`), run against the conformance suite over the kit's fakes:

```csharp
using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Example;

/// <summary>A 320 × 240 panel on USB 1209:000a: raw RGB565 frames and a two-byte backlight command, both on endpoint 0x01.</summary>
public sealed class ExampleDriver(IPanelDevices devices) : IPanelDriver
{
    public string Family => "example";

    public IReadOnlyList<PanelMatch> Matches { get; } = [new(0x1209, 0x000a)];

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop) =>
        new(true, new PanelIdentity("320x240", 320, 240, Serial: null, Firmware: null), PanelDeviceStatus.Ok);

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        var status = devices.OpenUsb(device, 0, 1000, out var usb, stop);
        if (usb == null)
            return new(null, status, "the panel couldn't be opened");
        var session = new Session(usb);
        try
        {
            if ((status = session.Backlight(options.Brightness, stop)) == PanelDeviceStatus.Ok)
                return new(session, status, "");
        }
        catch (OperationCanceledException) // a cancelled start: the screen off again, the device let go
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(1));
            throw;
        }
        session.Dispose();
        return new(null, status, "the panel didn't take its backlight");
    }

    private sealed class Session(IPanelUsb usb) : IPanelSession
    {
        public PanelCapabilities Capabilities { get; } =
            new(320, 240, [PanelFrameFormat.Rgb565Le], MaxFps: 30, Brightness: new PanelRange(0, 255));

        public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
        {
            var started = Stopwatch.GetTimestamp();
            var status = usb.BulkWrite(0x01, frame.Bytes, 1000, cancellationToken: stop);
            return new(status, Stopwatch.GetElapsedTime(started));
        }

        public PanelDeviceStatus SetBrightness(int percent) => Backlight(percent, CancellationToken.None);

        public PanelDeviceStatus Backlight(int percent, CancellationToken stop) =>
            usb.BulkWrite(0x01, [0xB1, (byte)(percent * 255 / 100)], 1000, cancellationToken: stop);

        public void Close(bool screenOff, TimeSpan budget)
        {
            if (screenOff && budget.TotalMilliseconds >= 1) // the backlight off, within what is left of the budget
                usb.BulkWrite(0x01, [0xB1, 0], (int)Math.Min(budget.TotalMilliseconds, 1000));
            usb.Dispose();
        }

        public void Dispose() => usb.Dispose();
    }
}
```

The sample names its USB id and size in code to stay short. A family's driver reads them from its `panel.json`, so the file the engine lists and imports by and the table the driver runs on are one:

```csharp
private static readonly PanelManifestFamily Manifest = PanelManifest.Read(typeof(AcmeDriver).Assembly).Family("acme");

public IReadOnlyList<PanelMatch> Matches => Manifest.Matches;

// A model by the code its handshake answers, read from the model's own member:
private static PanelManifestModel? ModelOf(int code) =>
    Manifest.Models.FirstOrDefault(m => m.Json.TryGetProperty("code", out var c) && c.GetInt32() == code);
```

`PanelManifest.Read` holds the file to the rules the engine reads it by, so a manifest the engine would refuse fails the driver's own tests first.

## What the engine does for you

A driver only talks to its device. The engine:

- **Lists the devices** every second from the OS's cache, with no I/O to any of them, offers a device only to the drivers whose matches name it, and keeps it for the first whose probe says "mine" until it is unplugged. A probe is cut short at its deadline, when the engine stops and before the computer sleeps.
- **Renders** the panel's profile at the panel's size, turns it by the panel's rotation (unless the driver says `RotatesItself`), scales it to fit, and **converts** it to the first format in `Capabilities.Formats` it produces: `Jpeg`, `Rgb565Le`, `Rgb565LeTruncated`, `Rgb565Be`, `Rgb888`, `Bgr888` or `Rgba8888`. `Rgb565Le` is Skia's own conversion, which rounds; the others drop the low bits (truncation, as the vendors' own software does), so a family whose software truncated little-endian RGB565 declares `Rgb565LeTruncated`. In that order it **dims** the frame when the panel has `DimInSoftware` (each channel × brightness ⁄ 100), draws the **overlays** (`Overlays`, a PNG for each rotation it has one for, each the panel's size, its alpha kept: a mask over a camera hole, say; a rotation without one gets none), **crops** it (`Crop`: render at 480 rows, send the top 462) and **encodes** it: JPEG at `JpegQuality`, and a JPEG over `MaxFrameBytes` encoded again `JpegQualityStep` lower, down to `MinJpegQuality`; one still over the cap at the floor isn't sent (the panel keeps the last frame).
- **Paces** each panel by its `DeviceTime`, at the lowest of the panel's own frame rate (a setting of each panel), its `MaxFps` and what it has learned the panel takes; a frame that didn't change isn't sent, and the last one is written again after `KeepAliveEvery` (1 s when null), or `KeepAlive` is called when the session has one.
- **Sends the brightness** between frames when it changes, and the panel's family settings at each open.
- **Stops** a session with the screen off whenever the panel stops (turned off, unplugged, its profile gone, the engine quitting, the computer going to sleep), within a 3 s bound for every panel together, and lets the device go even when a close fails.
- **Keeps** the panels' identities and settings in `devices.json`, answers the API, imports 1.x's panels through `v1Model`, `v1Aliases` and `v1Field`, and lists devices `detect` names.
- **Runs** a package's drivers in a panel host, a process of its own, so a driver that crashes takes down only its own panels; the host is started again, at most three times a minute.

## Tests without a device

Every test runs without hardware, against the test kit, `InfoPanel.Panels.Testing` (it references only the SDK and no test framework):

- `FakePanelDevices`, the device seam faked: add a device at a `PanelCandidate`, set its descriptors, queue its replies (`Reply`) or compute them from what it last received (`Respond`), fail the nth call with a status (`Fail`, a write delivering a part of its bytes first), stall it until its timeout (`Stall`), slow it (`Delay`), hold it until the test lets it go (`Hold`), unplug the device at a call (`UnplugAt`) or now (`Unplug`). Every call is recorded, in order, in the device's `Trace`, and timed from its entry to its return in its `Timings`.
- `PanelTrace`, the text form of a trace, one line a call on its handle, which a test writes by hand and compares, and parses back:

  ```
  h1 open usb if0
  h1 > ep01 b17f
  h1 > ep01 b100
  h1 close usb if0
  ```

- `RecordingPanelDevices`, which wraps the engine's real seam on your panel and writes the same trace to a file.
- `Conformance`, the suite every driver passes, and `ConformanceFrames`, frames of any format and size (a raw pattern, or a JPEG any decoder reads).
- `PanelPackageOutput`, which loads a package's driver from its own build output ([the package](#the-package)).

### Byte vectors from the vendor protocol

For each pure function the driver carries (a packet or header builder, a checksum, the encryption, the framing, a pixel conversion, a parser of the handshake's reply), feed the same inputs to it and to the protocol's own description, and compare the bytes. When the family came from InfoPanel 1.x, the description is 1.x's own function: copy it verbatim into a test-only `V1Oracle/` folder, with its 1.x path and line in a comment (no device code, nothing from Windows' UI), and compare against it. When it comes from the vendor's documentation or a capture of the vendor's software, the bytes in it are the vector: cite where each comes from.

Then the sequences: the start, a frame, a brightness change, the screen-off and the close, written as text traces from the protocol's order (each step with its source in a comment), run against `FakePanelDevices` with the replies scripted (the handshake's reply for each model, an error, a short read). One scripted reply for each detection rule shows the probe answers the right model, and an unknown code the unknown model.

### The conformance suite

```csharp
[Fact]
public void The_driver_passes_the_conformance_suite() =>
    Assert.Empty(Conformance.Check(() =>
    {
        var devices = new FakePanelDevices();
        var panel = new PanelCandidate(0x1209, 0x000a, Bus: 1, Address: 5, PortPath: "1-3");
        var device = devices.Add(panel);
        device.Usb.Respond(last => /* the handshake's reply, from what the driver last wrote */ null);
        return new ConformanceSetup(new AcmeDriver(devices), panel, device);
    }));
```

The function makes a new driver over a new fake device each time it is called: each check starts from it and opens a session of its own. `Conformance.Check` answers the rules the driver broke, each with the check that found it and what it saw, so it works with any test framework, and a driver that throws is a failure, not an exception. `ConformanceOptions` changes the brightness it opens at, its timings, and the frames it writes (for a driver that reads its frames' contents).

A family whose probe writes (a handshake query) names its queries in the setup, so the suite holds the probe to them: `new ConformanceSetup(…) { ProbeWrite = Conformance.ProbeWrites(AcmeProtocol.InfoQuery) }`, or a function of its own that tells a query from anything else. Without it the suite only sees that no write of the probe is a frame's size or more, which catches a probe that draws a frame in one write but not one that draws it in parts or sends some other command; a family whose writes could pass for parts of a frame names its queries.

| Check | What the driver does |
|---|---|
| `probe` | takes the family's panel, lets the device go before it answers (every handle closed) and only asks: each write one of the setup's `ProbeWrite` queries, or without them, no write a frame's size or more (width × height × 2 bytes) |
| `start` | cancelled while the start's second call (its first, for a start of one call) is held on the device until after the cancel, or cancelled before it begins, it throws `OperationCanceledException`, answers no session and lets the device go |
| `formats` | declares whole capabilities (a size, a format at least and none twice, 1 to 60 frames a second, the JPEG settings in range), and a frame of every declared format writes and reaches the device |
| `brightness` | a range runs low to high and `SetBrightness` takes every percentage from 0 to 100 between frames; without a range (a fixed light, or `DimInSoftware`) it is never called. With a range or without, the start starts at 0, the configured brightness and 100 (sampled: each start is a whole session) |
| `close-budget` | `Close(screenOff: true)` returns within its budget while the device doesn't answer: as the device saw them, no call starts once the budget is spent (to the millisecond) and none has a timeout past what was left of it; with no budget left it makes no call |
| `unplug` | a device unplugged during a frame (at its first call and at its last) answers `Gone`, the write stops there, and the screen-off stops at its first `Gone` |
| `after-close` | every handle is let go when `Close` returns; nothing reaches the device after it, before the session's dispose or after; no call is made on a handle let go, and `Close(screenOff: false)` writes nothing |
| `device-time` | `DeviceTime` covers the frame's transfer (a transfer made slow shows in it) and is no longer than the frame's calls on the device took, from the first's entry to the last's return: the driver's own work before or after the write is outside it |
| `overlays` | each overlay is a whole PNG of the panel's size (signature; IHDR first, with a bit depth its colour type takes, compression and filter method 0, interlace 0 or 1; the image data in IDATs one after the other; a palette before them for an indexed image and none in a greyscale one; IEND last; every chunk's CRC; ancillary and animation chunks allowed), for 0, 90, 180 or 270 degrees |
| `crop` | the crop lies inside the frame |

The timings allow `ConformanceOptions.Slack` (50 ms) for the threads' scheduling only: for how late a call's end or a timeout is seen, never for a call started after the close budget is spent.

### The manifest test

Reads `panel.json` (`PanelManifest.Read`, the engine's rules), and checks its `v1Model` set against the model names 1.x has for the family, written into the test from 1.x's source; a family new in 2.0 checks its models against the vendor's table instead. A model can never be missed silently by the 1.x import.

### The rest

- The runner test: the driver through the engine's `PanelRunner` over the fakes, with the engine's own transform producing the family's format, so the whole path from a rendered frame to the device's bytes is checked once.
- The screen-off on stop, as the protocol does it, and nothing written after `Close`.
- The 1.x import, for a family 1.x had: a 1.x settings file with one of its devices maps to the family's model and settings.
- New tests are seen to fail first, against a stub, then pass.

## On your panel

The hardware test, `HardwareTests` in the family's test folder, is skipped unless `INFOPANEL_PANEL_HW` names the family. It runs the driver on a plugged-in panel through the engine's own seam wrapped in `RecordingPanelDevices`, which writes every transfer to the file `INFOPANEL_PANEL_TRACE` names:

```bash
INFOPANEL_PANEL_HW=acme INFOPANEL_PANEL_TRACE=acme-first-light.trace \
  dotnet test InfoPanel.slnx --filter "FullyQualifiedName~Panels.Acme.HardwareTests"
```

First light, on the panel: it starts, shows colour bars and a gradient, brightness 100 then 50, then stops with the screen off. Then in the engine: plug the panel in, see it listed, show a profile, change its brightness and rotation, unplug it (it shows as not connected), plug it in again, quit (its screen goes off). The trace reads back as the calls the driver made, ready to become a vector in its tests. Look through a trace before you share it: it holds the bytes the panel was sent and the serial number it reported.

On Windows a libusb family's device needs the WinUSB driver bound to its interface, as with InfoPanel 1.x (Zadig binds it); a HID or serial family needs nothing. On Linux the udev rule above gives the user's session the device; without it the panel shows `access`.

## What a pull request includes

Pull requests use the panel family template (`?template=panel-family.md` on the new pull request's address); [`.github/PULL_REQUEST_TEMPLATE/panel-family.md`](../../.github/PULL_REQUEST_TEMPLATE/panel-family.md) is its checklist:

- the package, its `panel.json` and its udev rule;
- the tests: byte vectors, traces, detection, the manifest test, the conformance suite, the runner test, the screen-off, the 1.x import where the family had one, and the hardware test, skipped without `INFOPANEL_PANEL_HW`;
- where the model table comes from (1.x's source, the vendor's software, a capture);
- the family's notes in `docs/2.0/panels/{family}.md`;
- a first-light log and its trace from your own panel, or the family marked "unverified on hardware" until someone with one sends theirs;
- the contributor agreement, signed once.

A rebadge (a USB id or a model added to a family already here) uses the template's short form: the line, the panel's log, and the tests still green.
