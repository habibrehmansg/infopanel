# Third-party components

What InfoPanel 2.0's builds carry from other projects, grouped by where it goes in a build. Each NuGet package is listed by id and version as the restore graph resolves it (`obj/project.assets.json` of `InfoPanel`, `InfoPanel.Extras` and `InfoPanel.SensorHelper`), with the licence its package declares (an SPDX expression, a file in the package, or an address), the copyright line it states, and its project's address. Packages only the tests use are not listed. `InfoPanel.Tests/LicensesListTests.cs` checks each build section against its graph.

InfoPanel's own licence is in [`LICENSE`](LICENSE); the formats in `spec/` have theirs in [`spec/LICENSE`](spec/LICENSE).

## The engine (every build)

The engine executable and what it loads beside it: `InfoPanel.Core`, the plugin SDK and the plugin host (the `InfoPanel` graph). A native-libraries package's files are for its own operating system; a publish for one runtime carries only that runtime's.

### AsyncKeyedLock 8.0.2
- **Licence**: MIT
- **Copyright**: © 2026 Mark Cilia Vincenti
- **Project**: https://github.com/MarkCiliaVincenti/AsyncKeyedLock

### CommunityToolkit.Mvvm 8.4.2
- **Licence**: MIT
- **Copyright**: (c) .NET Foundation and Contributors. All rights reserved.
- **Project**: https://github.com/CommunityToolkit/dotnet

### ExCSS 4.3.1
- **Licence**: MIT
- **Copyright**: none stated in the package (authors: Tyler Brinks)
- **Project**: https://github.com/TylerBrinks/ExCSS

### HarfBuzzSharp 7.3.0.3
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.Linux 7.3.0.3 (native libraries for Linux)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.macOS 7.3.0.3 (native libraries for macOS)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.Win32 7.3.0.3 (native libraries for Windows)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### ini-parser-netstandard 2.5.3
- **Licence**: MIT
- **Copyright**: none stated in the package (authors: Ricardo Amores Hernández)
- **Project**: https://github.com/rickyah/ini-parser

### MessagePack 2.5.198
- **Licence**: MIT
- **Copyright**: © Yoshifumi Kawai and contributors. All rights reserved.
- **Project**: https://github.com/MessagePack-CSharp/MessagePack-CSharp

### MessagePack.Annotations 2.5.198
- **Licence**: MIT
- **Copyright**: © Yoshifumi Kawai and contributors. All rights reserved.
- **Project**: https://github.com/MessagePack-CSharp/MessagePack-CSharp

### Microsoft.Extensions.DependencyModel 10.0.0
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### Microsoft.NET.StringTools 18.0.2
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/dotnet/msbuild

### Microsoft.VisualStudio.Threading.Only 17.14.15
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://microsoft.github.io/vs-threading/

### Microsoft.VisualStudio.Validation 17.13.22
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/Microsoft/vs-validation

### Nerdbank.MessagePack 1.0.2
- **Licence**: MIT
- **Copyright**: © Andrew Arnott. All rights reserved.
- **Project**: https://aarnott.github.io/Nerdbank.MessagePack/

### Nerdbank.Streams 2.13.16
- **Licence**: MIT
- **Copyright**: © Andrew Arnott. All rights reserved.
- **Project**: https://github.com/AArnott/Nerdbank.Streams

### Newtonsoft.Json 13.0.3
- **Licence**: MIT
- **Copyright**: Copyright © James Newton-King 2008
- **Project**: https://www.newtonsoft.com/json

### PolyType 1.0.0
- **Licence**: MIT
- **Copyright**: 2024 (authors: Eirik Tsarpalis)
- **Project**: https://eiriktsarpalis.github.io/PolyType/

### Serilog 4.3.1
- **Licence**: Apache-2.0
- **Copyright**: Copyright © Serilog Contributors
- **Project**: https://serilog.net/

### Serilog.AspNetCore 10.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Microsoft, Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-aspnetcore

### Serilog.Extensions.Hosting 10.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Microsoft, Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-extensions-hosting

### Serilog.Extensions.Logging 10.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Microsoft, Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-extensions-logging

### Serilog.Formatting.Compact 3.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-formatting-compact

### Serilog.Settings.Configuration 10.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-settings-configuration

### Serilog.Sinks.Console 6.1.1
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-sinks-console

### Serilog.Sinks.Debug 3.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-sinks-debug

### Serilog.Sinks.File 7.0.0
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Serilog Contributors)
- **Project**: https://github.com/serilog/serilog-sinks-file

### ShimSkiaSharp 4.3.0
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### SkiaSharp 3.119.2
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.HarfBuzz 2.88.7
- **Licence**: by address, https://go.microsoft.com/fwlink/?linkid=868514
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.Linux 2.88.7 (native libraries for Linux)
- **Licence**: by address, https://go.microsoft.com/fwlink/?linkid=868514
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.macOS 3.119.2 (native libraries for macOS)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.Win32 3.119.2 (native libraries for Windows)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### StreamJsonRpc 2.24.84
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://microsoft.github.io/vs-streamjsonrpc/

### Svg.Animation 4.3.0
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Custom 4.3.0
- **Licence**: MS-PL
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Model 4.3.0
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.SceneGraph 4.3.0
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Skia 4.3.0
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### System.CommandLine 2.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/dotnet/command-line-api

### System.IO.Hashing 10.0.14
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### Topten.RichTextKit 0.4.167
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Topten Software)
- **Project**: https://github.com/toptensoftware/richtextkit

## The bundled plugin (`plugins/InfoPanel.Extras/`)

Beside the engine in every build (`InfoPanel/InfoPanel.csproj` copies it there on build and on publish); its plugins list sensors on Windows only ([plugins](docs/2.0/plugins.md)). The `InfoPanel.Extras` graph, Windows runtimes.

### NAudio.Core 2.3.0
- **Licence**: MIT
- **Copyright**: © Mark Heath 2026
- **Project**: https://github.com/naudio/NAudio

### NAudio.Wasapi 2.3.0
- **Licence**: MIT
- **Copyright**: © Mark Heath 2026
- **Project**: https://github.com/naudio/NAudio

### Serilog 4.3.1
- **Licence**: Apache-2.0
- **Copyright**: Copyright © Serilog Contributors
- **Project**: https://serilog.net/

### System.Configuration.ConfigurationManager 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Diagnostics.EventLog 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Diagnostics.PerformanceCounter 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Security.Cryptography.ProtectedData 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Threading.AccessControl 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

## The sensor helper (`sensor-helper/`, Windows)

Only in a publish for Windows (`dotnet publish InfoPanel -r win-x64`), in its own folder beside the engine: LibreHardwareMonitorLib and its dependencies, unmodified, as published on nuget.org; their source is at the projects below. The `InfoPanel.SensorHelper` graph, Windows runtimes.

### BlackSharp.Core 1.0.7
- **Licence**: MPL-2.0
- **Copyright**: Florian K.
- **Project**: https://github.com/Blacktempel/BlackSharp

### DiskInfoToolkit 1.1.2
- **Licence**: MPL-2.0
- **Copyright**: Florian K.
- **Project**: https://github.com/Blacktempel/DiskInfoToolkit

### HidSharp 2.6.4
- **Licence**: a file in the package, `LICENSE.txt`
- **Copyright**: Copyright 2010-2025 James Bellinger
- **Project**: https://software.seekye.com/hidsharp

### LibreHardwareMonitorLib 0.9.6
- **Licence**: MPL-2.0
- **Copyright**: LibreHardwareMonitor
- **Project**: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

### Mono.Posix.NETStandard 1.0.0
- **Licence**: by address, https://go.microsoft.com/fwlink/?linkid=869050
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/mono

### RAMSPDToolkit-NDD 1.4.2
- **Licence**: MPL-2.0
- **Copyright**: Florian K.
- **Project**: https://github.com/Blacktempel/RAMSPDToolkit

### System.CodeDom 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.IO.Ports 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Management 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Threading.AccessControl 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

## Not from the restore graphs

### .NET runtime and ASP.NET Core
- **Use**: the engine runs on them. A framework-dependent build uses the ones installed on the computer; a self-contained build carries them. `Microsoft.Extensions.Caching.Memory`, which `InfoPanel.Core` references, comes from ASP.NET Core's shared framework, not from its package.
- **Licence**: MIT, as their runtime packages (`Microsoft.NETCore.App.Runtime.*`, `Microsoft.AspNetCore.App.Runtime.*`) declare
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore

### FFmpeg 9.0.2 (video decoding)
- **Use**: libavutil, libavcodec, libavformat and libswscale as shared libraries, built decode-only by `native/ffmpeg/build.sh` from FFmpeg 9.0.2's release source, configured without `--enable-gpl` and `--enable-nonfree`. Not in this repository: the engine looks for them beside itself, then in `INFOPANEL_FFMPEG_DIR` (`InfoPanel.Core/Images/Video/FFmpeg.cs`).
- **Licence**: LGPL-2.1-or-later (built without `--enable-gpl` and `--enable-nonfree`)
- **Copyright**: © the FFmpeg developers
- **Project**: https://ffmpeg.org/

### dav1d (AV1 decoding inside the FFmpeg build)
- **Use**: built and installed before FFmpeg, which `build.sh` configures with `--enable-libdav1d`; `build.sh` doesn't pin its version
- **Licence**: BSD-2-Clause
- **Copyright**: © VideoLAN and dav1d authors
- **Project**: https://code.videolan.org/videolan/dav1d

### PawnIO installer 2.2.0 (Windows)
- **Use**: a Windows package carries PawnIO 2.2.0's `PawnIO_setup.exe`, unmodified, in `pawnio/` beside the engine, its SHA-256 pinned (`InfoPanel.Core/Sensors/Libre/PawnIoInstaller.cs`); the engine runs it only when a client asks it to install the driver. Not in this repository.
- **Licence**: as the installer and PawnIO's repository state
- **Copyright**: © namazso
- **Project**: https://github.com/namazso/PawnIO (https://pawnio.eu)

### Inter 4.001 (the bundled font)
- **Use**: Regular, Bold, Italic and Bold Italic, embedded in `InfoPanel.Core` (`InfoPanel.Core/Fonts/`)
- **Licence**: OFL-1.1, its text in [`InfoPanel.Core/Fonts/OFL.txt`](InfoPanel.Core/Fonts/OFL.txt)
- **Copyright**: Copyright 2016 The Inter Project Authors
- **Project**: https://github.com/rsms/inter

---

*From the restore graph at `ec716ce0`.*
