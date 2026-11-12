# Third-party components

What InfoPanel 2.0's builds carry from other projects, grouped by where it goes in a build. Each NuGet package is listed by id and version as the restore graph resolves it (`obj/project.assets.json` of `InfoPanel` and `InfoPanel.SensorHelper`), with the licence its package declares (an SPDX expression, a file in the package, or an address), the copyright line it states, and its project's address. Packages only the tests use are not listed. `InfoPanel.Tests/LicensesListTests.cs` checks each build section against its graph.

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

### HarfBuzzSharp 14.4.0
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.Linux 14.4.0 (native libraries for Linux)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.macOS 14.4.0 (native libraries for macOS)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HarfBuzzSharp.NativeAssets.Win32 14.4.0 (native libraries for Windows)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### HidSharp 2.6.4
- **Use**: the panel driver seam's HID reports (`InfoPanel.Core/Panels/Devices/HidSharpSystem.cs`)
- **Licence**: Apache-2.0, the package's own `LICENSE.txt`, copied below whole: its notice, then the licence's terms

  ```text
  HIDSharp
  Copyright 2010-2025 James F. Bellinger <http://software.seekye.com/hidsharp>

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

     http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.


                                   Apache License
                             Version 2.0, January 2004
                          http://www.apache.org/licenses/

     TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

     1. Definitions.

        "License" shall mean the terms and conditions for use, reproduction,
        and distribution as defined by Sections 1 through 9 of this document.

        "Licensor" shall mean the copyright owner or entity authorized by
        the copyright owner that is granting the License.

        "Legal Entity" shall mean the union of the acting entity and all
        other entities that control, are controlled by, or are under common
        control with that entity. For the purposes of this definition,
        "control" means (i) the power, direct or indirect, to cause the
        direction or management of such entity, whether by contract or
        otherwise, or (ii) ownership of fifty percent (50%) or more of the
        outstanding shares, or (iii) beneficial ownership of such entity.

        "You" (or "Your") shall mean an individual or Legal Entity
        exercising permissions granted by this License.

        "Source" form shall mean the preferred form for making modifications,
        including but not limited to software source code, documentation
        source, and configuration files.

        "Object" form shall mean any form resulting from mechanical
        transformation or translation of a Source form, including but
        not limited to compiled object code, generated documentation,
        and conversions to other media types.

        "Work" shall mean the work of authorship, whether in Source or
        Object form, made available under the License, as indicated by a
        copyright notice that is included in or attached to the work
        (an example is provided in the Appendix below).

        "Derivative Works" shall mean any work, whether in Source or Object
        form, that is based on (or derived from) the Work and for which the
        editorial revisions, annotations, elaborations, or other modifications
        represent, as a whole, an original work of authorship. For the purposes
        of this License, Derivative Works shall not include works that remain
        separable from, or merely link (or bind by name) to the interfaces of,
        the Work and Derivative Works thereof.

        "Contribution" shall mean any work of authorship, including
        the original version of the Work and any modifications or additions
        to that Work or Derivative Works thereof, that is intentionally
        submitted to Licensor for inclusion in the Work by the copyright owner
        or by an individual or Legal Entity authorized to submit on behalf of
        the copyright owner. For the purposes of this definition, "submitted"
        means any form of electronic, verbal, or written communication sent
        to the Licensor or its representatives, including but not limited to
        communication on electronic mailing lists, source code control systems,
        and issue tracking systems that are managed by, or on behalf of, the
        Licensor for the purpose of discussing and improving the Work, but
        excluding communication that is conspicuously marked or otherwise
        designated in writing by the copyright owner as "Not a Contribution."

        "Contributor" shall mean Licensor and any individual or Legal Entity
        on behalf of whom a Contribution has been received by Licensor and
        subsequently incorporated within the Work.

     2. Grant of Copyright License. Subject to the terms and conditions of
        this License, each Contributor hereby grants to You a perpetual,
        worldwide, non-exclusive, no-charge, royalty-free, irrevocable
        copyright license to reproduce, prepare Derivative Works of,
        publicly display, publicly perform, sublicense, and distribute the
        Work and such Derivative Works in Source or Object form.

     3. Grant of Patent License. Subject to the terms and conditions of
        this License, each Contributor hereby grants to You a perpetual,
        worldwide, non-exclusive, no-charge, royalty-free, irrevocable
        (except as stated in this section) patent license to make, have made,
        use, offer to sell, sell, import, and otherwise transfer the Work,
        where such license applies only to those patent claims licensable
        by such Contributor that are necessarily infringed by their
        Contribution(s) alone or by combination of their Contribution(s)
        with the Work to which such Contribution(s) was submitted. If You
        institute patent litigation against any entity (including a
        cross-claim or counterclaim in a lawsuit) alleging that the Work
        or a Contribution incorporated within the Work constitutes direct
        or contributory patent infringement, then any patent licenses
        granted to You under this License for that Work shall terminate
        as of the date such litigation is filed.

     4. Redistribution. You may reproduce and distribute copies of the
        Work or Derivative Works thereof in any medium, with or without
        modifications, and in Source or Object form, provided that You
        meet the following conditions:

        (a) You must give any other recipients of the Work or
            Derivative Works a copy of this License; and

        (b) You must cause any modified files to carry prominent notices
            stating that You changed the files; and

        (c) You must retain, in the Source form of any Derivative Works
            that You distribute, all copyright, patent, trademark, and
            attribution notices from the Source form of the Work,
            excluding those notices that do not pertain to any part of
            the Derivative Works; and

        (d) If the Work includes a "NOTICE" text file as part of its
            distribution, then any Derivative Works that You distribute must
            include a readable copy of the attribution notices contained
            within such NOTICE file, excluding those notices that do not
            pertain to any part of the Derivative Works, in at least one
            of the following places: within a NOTICE text file distributed
            as part of the Derivative Works; within the Source form or
            documentation, if provided along with the Derivative Works; or,
            within a display generated by the Derivative Works, if and
            wherever such third-party notices normally appear. The contents
            of the NOTICE file are for informational purposes only and
            do not modify the License. You may add Your own attribution
            notices within Derivative Works that You distribute, alongside
            or as an addendum to the NOTICE text from the Work, provided
            that such additional attribution notices cannot be construed
            as modifying the License.

        You may add Your own copyright statement to Your modifications and
        may provide additional or different license terms and conditions
        for use, reproduction, or distribution of Your modifications, or
        for any such Derivative Works as a whole, provided Your use,
        reproduction, and distribution of the Work otherwise complies with
        the conditions stated in this License.

     5. Submission of Contributions. Unless You explicitly state otherwise,
        any Contribution intentionally submitted for inclusion in the Work
        by You to the Licensor shall be under the terms and conditions of
        this License, without any additional terms or conditions.
        Notwithstanding the above, nothing herein shall supersede or modify
        the terms of any separate license agreement you may have executed
        with Licensor regarding such Contributions.

     6. Trademarks. This License does not grant permission to use the trade
        names, trademarks, service marks, or product names of the Licensor,
        except as required for reasonable and customary use in describing the
        origin of the Work and reproducing the content of the NOTICE file.

     7. Disclaimer of Warranty. Unless required by applicable law or
        agreed to in writing, Licensor provides the Work (and each
        Contributor provides its Contributions) on an "AS IS" BASIS,
        WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
        implied, including, without limitation, any warranties or conditions
        of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
        PARTICULAR PURPOSE. You are solely responsible for determining the
        appropriateness of using or redistributing the Work and assume any
        risks associated with Your exercise of permissions under this License.

     8. Limitation of Liability. In no event and under no legal theory,
        whether in tort (including negligence), contract, or otherwise,
        unless required by applicable law (such as deliberate and grossly
        negligent acts) or agreed to in writing, shall any Contributor be
        liable to You for damages, including any direct, indirect, special,
        incidental, or consequential damages of any character arising as a
        result of this License or out of the use or inability to use the
        Work (including but not limited to damages for loss of goodwill,
        work stoppage, computer failure or malfunction, or any and all
        other commercial damages or losses), even if such Contributor
        has been advised of the possibility of such damages.

     9. Accepting Warranty or Additional Liability. While redistributing
        the Work or Derivative Works thereof, You may choose to offer,
        and charge a fee for, acceptance of support, warranty, indemnity,
        or other liability obligations and/or rights consistent with this
        License. However, in accepting such obligations, You may act only
        on Your own behalf and on Your sole responsibility, not on behalf
        of any other Contributor, and only if You agree to indemnify,
        defend, and hold each Contributor harmless for any liability
        incurred by, or claims asserted against, such Contributor by reason
        of your accepting any such warranty or additional liability.

     END OF TERMS AND CONDITIONS
  ```
- **Copyright**: Copyright 2010-2025 James Bellinger
- **Project**: https://software.seekye.com/hidsharp

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

### runtime.android-arm.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Android, arm)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.android-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Android, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.android-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Android, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.android-x86.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Android, x86)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-arm.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux, arm)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-bionic-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux with Bionic, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-bionic-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux with Bionic, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-musl-arm.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux with musl, arm)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-musl-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux with musl, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-musl-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux with musl, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.linux-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Linux, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.maccatalyst-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Mac Catalyst, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.maccatalyst-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for Mac Catalyst, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.osx-arm64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for macOS, arm64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### runtime.osx-x64.runtime.native.System.IO.Ports 10.0.6 (System.IO.Ports' native library for macOS, x64)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

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

### ShimSkiaSharp 5.2.3
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### SkiaSharp 4.155.0
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.HarfBuzz 4.155.0
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.Linux 4.155.0 (native libraries for Linux)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.macOS 4.155.0 (native libraries for macOS)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### SkiaSharp.NativeAssets.Win32 4.155.0 (native libraries for Windows)
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/mono/SkiaSharp

### StreamJsonRpc 2.24.84
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://microsoft.github.io/vs-streamjsonrpc/

### Svg.Animation 5.2.3
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Custom 5.2.3
- **Licence**: MS-PL
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Model 5.2.3
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.SceneGraph 5.2.3
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### Svg.Skia 5.2.3
- **Licence**: MIT
- **Copyright**: Copyright © Wiesław Šoltés 2026
- **Project**: https://github.com/wieslawsoltes/Svg.Skia

### System.CommandLine 2.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://github.com/dotnet/command-line-api

### System.Configuration.ConfigurationManager 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Diagnostics.PerformanceCounter 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.IO.Hashing 10.0.14
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.IO.Ports 10.0.6
- **Use**: the panel driver seam's serial ports (`InfoPanel.Core/Panels/Devices/SerialPorts.cs`)
- **Licence**: MIT (the package's own licence expression; it carries no licence file)
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### System.Security.Cryptography.ProtectedData 10.0.6
- **Licence**: MIT
- **Copyright**: © Microsoft Corporation. All rights reserved.
- **Project**: https://dot.net/

### Tmds.DBus.Protocol 0.95.2
- **Licence**: MIT
- **Copyright**: Tom Deseyn
- **Project**: https://tmds.github.io/Tmds.DBus

### Topten.RichTextKit 0.6.170
- **Licence**: Apache-2.0
- **Copyright**: none stated in the package (authors: Topten Software)
- **Project**: https://github.com/toptensoftware/richtextkit

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
- **Use**: libavutil, libavcodec, libavformat and libswscale as shared libraries, built decode-only by `native/ffmpeg/build.sh` from FFmpeg 9.0.2's release source, configured without `--enable-gpl` and `--enable-nonfree`. Not in this repository: the engine looks for them beside itself, then in `INFOPANEL_FFMPEG_DIR` (`InfoPanel.Core/Images/Video/FFmpeg.cs`). Not redistributed: the Mac app and the Windows installer carry neither FFmpeg nor dav1d ([packaging](docs/2.0/packaging.md)); the engine loads the FFmpeg libraries installed on the computer.
- **Licence**: LGPL-2.1-or-later (built without `--enable-gpl` and `--enable-nonfree`)
- **Copyright**: © the FFmpeg developers
- **Project**: https://ffmpeg.org/

### dav1d (AV1 decoding inside the FFmpeg build)
- **Use**: built and installed before FFmpeg, which `build.sh` configures with `--enable-libdav1d`; `build.sh` doesn't pin its version
- **Licence**: BSD-2-Clause
- **Copyright**: © VideoLAN and dav1d authors
- **Project**: https://code.videolan.org/videolan/dav1d

### libusb 1.0.30 (USB panels)
- **Use**: libusb-1.0 as a shared library, dynamically linked: the engine loads it by full path from beside itself and calls it through its own function pointers (`InfoPanel.Core/Panels/Usb/LibUsb.cs`), so it can be replaced by another build of the same library. The macOS app carries `libusb-1.0.dylib`, built unmodified by `native/libusb/build.sh` from libusb 1.0.30's release source; the Windows installer carries the release's x64 `libusb-1.0.dll`, unmodified. Both are pinned by SHA-256 in `native/libusb/pins.txt`. Not in this repository: the source is the release at https://github.com/libusb/libusb/releases/tag/v1.0.30 (`libusb-1.0.30.tar.bz2`). Linux uses the system's libusb.
- **Licence**: LGPL-2.1-or-later. Its full text, the `COPYING` of that release's source (its SHA-256 pinned in `native/libusb/pins.txt` too), comes with both packages as `libusb-COPYING.txt` beside this file: in the Mac app's `Contents/Resources` and on its disk image, and in the Windows install folder.
- **Copyright**: © the libusb contributors
- **Project**: https://libusb.info/ (https://github.com/libusb/libusb)

### mediaremote-adapter 0e82bf1c (Now Playing on macOS)
- **Use**: the Now Playing source's media helper on macOS: its Objective-C framework, built unmodified by `native/mediaremote-adapter/build.sh` from the sources vendored in `native/mediaremote-adapter/` (upstream commit `0e82bf1c44ec39554fbfc402ec88a48c6ae2a0fd`, each file's SHA-256 pinned in `native/mediaremote-adapter/pins.txt`; its test client and test command left out), and its Perl script, unmodified. A build on a Mac puts both, with the licence, in `mediaremote-adapter/` beside the engine, where the engine runs the script with macOS's own `/usr/bin/perl` as a child process (`InfoPanel.Core/Sensors/NowPlaying/MacNowPlayingReader.cs`); the engine doesn't link it.
- **Licence**: BSD-3-Clause. Its full text, the upstream `LICENSE`, is in `native/mediaremote-adapter/LICENSE` and comes with the helper as `mediaremote-adapter/LICENSE`.
- **Copyright**: © 2025 Jonas van den Berg and contributors
- **Project**: https://github.com/ungive/mediaremote-adapter

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

### DSEG7 Classic 0.46 (the bundled font)
- **Use**: Regular and Bold, unmodified, embedded in `InfoPanel.Core` (`InfoPanel.Core/Fonts/`), so seven-segment digits draw the same on every computer
- **Licence**: OFL-1.1, with the Reserved Font Name "DSEG"; its text in [`InfoPanel.Core/Fonts/DSEG-OFL.txt`](InfoPanel.Core/Fonts/DSEG-OFL.txt)
- **Copyright**: Copyright (c) 2018, keshikan (http://www.keshikan.net), with Reserved Font Name "DSEG"
- **Project**: https://github.com/keshikan/DSEG

## Source ported into InfoPanel

Code from other projects that InfoPanel's own sources carry in a port, rather than a component a build ships beside them.

**TuringSmartScreenLib** (the Turing / TURZX panel package, `InfoPanel.Panels.Turing`)
- **Use**: the serial Turing panels' protocol (revisions A, C and E) and the Turing variant of the USB panels' protocol, ported into `InfoPanel.Panels.Turing/TuringSerialScreen.cs`, `TuringUsbProtocol.cs` and `TuringUsbSession.cs`, and copied into the tests' reference code under `InfoPanel.Core.Tests/Panels/Turing/V1Oracle/`, from the library's source at the commit InfoPanel 1.4 built its DLLs from (`df363d80`)
- **Licence**: MIT
- **Copyright**: Copyright (c) 2021 machi_pon
- **Project**: https://github.com/usausa/turing-smart-screen

> MIT License
>
> Copyright (c) 2021 machi_pon
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

*From the restore graph at `ec716ce0`.*
