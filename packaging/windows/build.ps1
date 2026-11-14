# Builds InfoPanel 2's Windows installer, InfoPanel-<version>-win-x64-setup.exe, on Windows (a Windows publish puts the
# manifest and the icon into InfoPanel.exe only when built on Windows). Needs the .NET SDK (global.json's) and Inno
# Setup 6. See docs/2.0/packaging.md.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\build.ps1 `
#     -Version 2.0.0-beta.1 -AllowOrigin https://client.example -FFmpegDir <bundle> -Out C:\builds\2.0.0-beta.1
#
# -Out must be new or empty, a plain path (no junction or link in it) writable only by the user building: the publish and
# the installer go there, nothing is deleted. The two files it bundles are downloaded into -Out (or taken from
# -PawnIoSetup and -DotnetRuntime) and refused unless their hashes are the pinned ones: PawnIO's setup against
# PawnIoInstaller.BundledSha256, the .NET runtime installer against $Runtime.Sha512 below. The USB panels' library,
# libusb-1.0.dll, comes from libusb's release (its 7z downloaded, or -LibusbArchive, and unpacked with Windows' own tar,
# or with 7-Zip where that tar can't read it), the archive and the DLL both checked against native\libusb\pins.txt, and
# goes into the publish beside the engine, with libusb's licence text, libusb-COPYING.txt: the COPYING of the release's
# source tarball (downloaded, or -LibusbSource), the tarball and the file both checked against pins.txt too.
# -FFmpegDir: the bundle\ folder native/ffmpeg/build.sh --target windows-x64 makes (it builds on Linux, in
# native/ffmpeg/windows-cross.Dockerfile's container), holding exactly FFmpeg's four decode-only DLLs and the two licence
# texts, ffmpeg-COPYING.txt and dav1d-COPYING.txt. Each DLL is checked (PE32+ x86-64, a DLL, its version resource's file
# major the one the engine loads, its product version the FFmpeg release native\ffmpeg\pins.txt pins, its original file
# name its own) and each text against pins.txt; all six go into the publish beside the engine. Built DLLs have no pinned
# hash: each is held from its check until the installer is packed, to the SHA-256 taken from the bytes checked.
# -CrashReportsDsn (optional): where the engine sends crash reports, a Sentry DSN (https://<key>@<host>/<project>), built in
# as InfoPanelCrashReportsDsn; without it the build sends none (docs/2.0/crash-reports.md).
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$AllowOrigin,
  [Parameter(Mandatory = $true)][string]$Out,
  [Parameter(Mandatory = $true)][string]$FFmpegDir,
  [string]$PawnIoSetup,
  [string]$DotnetRuntime,
  [string]$LibusbArchive,
  [string]$LibusbSource,
  [string]$CrashReportsDsn,
  [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# The sensor helper's runtime, bundled for PCs with no .NET 8 or later: Microsoft's installer, pinned (its SHA-512 from
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
$Runtime = @{
  Version = '10.0.14'
  Url     = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.14/dotnet-runtime-10.0.14-win-x64.exe'
  Sha512  = '6277b0feac2ec312276ec62ccaaec7fb49a22baa0c4157d0418eb4bfc7c443fa90aa06318725c113da1ea19343b5a315749825299a71e34010f4554496cf36e8'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') { throw "-Version '$Version' isn't a version like 2.0.0 or 2.0.0-beta.1." }
$fileVersion = '{0}.{1}.{2}.0' -f $Matches[1], $Matches[2], $Matches[3]
if ($AllowOrigin -notmatch '^https://[^/\s]+$') { throw "-AllowOrigin '$AllowOrigin' isn't an https origin (scheme://host[:port], no path)." }
if ($CrashReportsDsn -and $CrashReportsDsn -notmatch '^https://[0-9A-Za-z]+@[A-Za-z0-9.-]+(:[0-9]{1,5})?/[0-9]+$') { throw '-CrashReportsDsn must be a Sentry DSN, https://<key>@<host>/<project>.' }
$crashProperty = @(if ($CrashReportsDsn) { "-p:InfoPanelCrashReportsDsn=$CrashReportsDsn" })
if (-not (Test-Path -LiteralPath $Iscc)) { throw "Inno Setup 6's compiler isn't at $Iscc (install Inno Setup 6, or pass -Iscc)." }
if (-not (Test-Path -LiteralPath $FFmpegDir -PathType Container)) { throw "-FFmpegDir $FFmpegDir isn't a folder: pass native/ffmpeg/build.sh --target windows-x64's bundle\." }
if ((Test-Path -LiteralPath $Out) -and (Get-ChildItem -LiteralPath $Out -Force | Select-Object -First 1)) { throw "-Out $Out isn't empty: choose a new folder." }

# Refused if $Path, or any folder above it that exists, is a reparse point (a junction or a symbolic link): the files the
# build holds and hashes must be where the compiler reads them, and a link above them could be pointed elsewhere between
# the two. -Out must also be writable only by the user building (docs/2.0/packaging.md).
function Assert-NoReparsePoint([string]$Path) {
  for ($p = [IO.Path]::GetFullPath($Path).TrimEnd('\'); $p; $p = Split-Path -Parent $p) {
    if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
      throw "$p is a junction or a link: -Out ($Path) must be a plain folder path, writable only by the user building. Refused."
    }
  }
}
Assert-NoReparsePoint $Out
$Out = (New-Item -ItemType Directory -Force -Path $Out).FullName
$downloads = (New-Item -ItemType Directory -Path (Join-Path $Out 'downloads')).FullName
Assert-NoReparsePoint $downloads
$publish = Join-Path $Out 'publish'

# Refused unless the file at $Path has the pinned hash $Expected.
function Assert-Pinned([string]$Path, [string]$Algorithm, [string]$Expected) {
  $actual = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash
  if ($actual -ne $Expected) { throw "$Path's $Algorithm is $actual, not the pinned ${Expected}: refused." }
  Write-Host "$Path : $Algorithm $actual (pinned)"
}

# The file at $Url (or the local $Given) as $Name in this build's own downloads folder, refused unless its hash is
# $Expected.
function Get-Pinned([string]$Name, [string]$Url, [string]$Given, [string]$Algorithm, [string]$Expected) {
  $path = Join-Path $downloads $Name
  if ($Given) { Copy-Item -LiteralPath $Given -Destination $path } else { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $path }
  Assert-Pinned $path $Algorithm $Expected
  $path
}

# PawnIO's setup: the version and SHA-256 the engine pins.
$pawnIoSource = Get-Content -Raw -LiteralPath (Join-Path $repo 'InfoPanel.Core\Sensors\Libre\PawnIoInstaller.cs')
if ($pawnIoSource -notmatch 'BundledVersion = "([^"]+)"') { throw 'PawnIoInstaller.BundledVersion not found.' }
$pawnIoVersion = $Matches[1]
if ($pawnIoSource -notmatch 'BundledSha256 = "([0-9a-fA-F]{64})"') { throw 'PawnIoInstaller.BundledSha256 not found.' }
$pawnIoSha256 = $Matches[1]
# libusb-1.0.dll (USB panels): the release's x64 DLL, the version and both SHA-256 values pinned in native\libusb\pins.txt.
$libusbPins = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $repo 'native\libusb\pins.txt')) {
  if ($line -match '^([a-z0-9_]+)=(.+)$') { $libusbPins[$Matches[1]] = $Matches[2] }
}
foreach ($key in 'version', 'source_sha256', 'source_copying_sha256', 'windows_archive_sha256', 'windows_dll_path', 'windows_dll_sha256') {
  if (-not $libusbPins[$key]) { throw "native\libusb\pins.txt has no $key." }
}
# Windows' own tar.exe reads the 7z only when it is built with liblzma, and the .tar.bz2 only with bz2lib: Windows 11's
# is (its --version lists both), Windows Server 2022's (bsdtar 3.5.2, zlib only) isn't. Without the codec, 7-Zip's
# 7z.exe unpacks the file instead (GitHub's Windows images carry it); with neither, the build stops.
$tarVersion = (& tar.exe --version) -join ' '
# $Member (a path with '/') of $Archive unpacked into the new folder $Name in downloads; its full path is returned.
# $Codec is the library tar.exe needs for the file: liblzma for a 7z, bz2lib for a .tar.bz2.
function Expand-Member([string]$Archive, [string]$Name, [string]$Member, [string]$Codec) {
  $into = (New-Item -ItemType Directory -Path (Join-Path $downloads $Name)).FullName
  if ($tarVersion -match "\b$Codec/") {
    & tar.exe -xf $Archive -C $into $Member | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "tar.exe couldn't unpack $Member from $Archive (exit $LASTEXITCODE)." }
  } else {
    $sevenZip = (Get-Command 7z.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe' }
    if (-not (Test-Path -LiteralPath $sevenZip)) { throw "tar.exe ($tarVersion) has no $Codec to read $Archive, and 7-Zip's 7z.exe isn't on the PATH or in $env:ProgramFiles\7-Zip: install 7-Zip." }
    Write-Host "tar.exe has no ${Codec}: $Archive unpacked with $sevenZip"
    if ($Archive -like '*.tar.bz2') {
      # 7-Zip takes off the bzip2 layer; tar.exe reads the plain tar inside.
      & $sevenZip x $Archive "-o$into" -y -bsp0 | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "7z.exe couldn't unpack $Archive (exit $LASTEXITCODE)." }
      $tar = Join-Path $into ([IO.Path]::GetFileNameWithoutExtension($Archive))
      & tar.exe -xf $tar -C $into $Member | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "tar.exe couldn't unpack $Member from $tar (exit $LASTEXITCODE)." }
    } else {
      & $sevenZip x $Archive "-o$into" -y -bsp0 ($Member -replace '/', '\') | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "7z.exe couldn't unpack $Member from $Archive (exit $LASTEXITCODE)." }
    }
  }
  $path = Join-Path $into ($Member -replace '/', '\')
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Member isn't in $Archive." }
  $path
}
$libusbArchiveName = "libusb-$($libusbPins.version).7z"
$libusbArchivePath = Get-Pinned $libusbArchiveName "https://github.com/libusb/libusb/releases/download/v$($libusbPins.version)/$libusbArchiveName" $LibusbArchive 'SHA256' $libusbPins.windows_archive_sha256
$libusbDll = Expand-Member $libusbArchivePath 'libusb' $libusbPins.windows_dll_path 'liblzma'
Assert-Pinned $libusbDll 'SHA256' $libusbPins.windows_dll_sha256
# libusb's licence text: the 7z holds none, so it is the COPYING of the release's source tarball (downloaded, or
# -LibusbSource), the tarball and the file both checked against pins.txt; installed beside the engine as libusb-COPYING.txt.
$libusbSourceName = "libusb-$($libusbPins.version).tar.bz2"
$libusbSourcePath = Get-Pinned $libusbSourceName "https://github.com/libusb/libusb/releases/download/v$($libusbPins.version)/$libusbSourceName" $LibusbSource 'SHA256' $libusbPins.source_sha256
$libusbCopying = Expand-Member $libusbSourcePath 'libusb-source' "libusb-$($libusbPins.version)/COPYING" 'bz2lib'
Assert-Pinned $libusbCopying 'SHA256' $libusbPins.source_copying_sha256

# FFmpeg (video files and RTSP cameras): the four DLLs the engine loads from beside itself
# (InfoPanel.Core/Images/Video/FFmpeg.cs, which accepts only these majors) and the two licence texts, pinned in
# native\ffmpeg\pins.txt.
$ffmpegPins = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $repo 'native\ffmpeg\pins.txt')) {
  if ($line -match '^([a-z0-9_]+)=(.+)$') { $ffmpegPins[$Matches[1]] = $Matches[2] }
}
foreach ($key in 'ffmpeg_version', 'ffmpeg_copying_sha256', 'dav1d_copying_sha256') {
  if (-not $ffmpegPins[$key]) { throw "native\ffmpeg\pins.txt has no $key." }
}
$ffmpegDlls = [ordered]@{ 'avutil-61.dll' = 61; 'avcodec-63.dll' = 63; 'avformat-63.dll' = 63; 'swscale-10.dll' = 10 }
$ffmpegTexts = [ordered]@{ 'ffmpeg-COPYING.txt' = $ffmpegPins.ffmpeg_copying_sha256; 'dav1d-COPYING.txt' = $ffmpegPins.dav1d_copying_sha256 }
$ffmpegNames = @($ffmpegDlls.Keys) + @($ffmpegTexts.Keys)
# -FFmpegDir holds exactly the six files and nothing else, each a regular file (no folder, junction or link). Names are
# compared case-sensitively: the engine loads these exact names, and an NTFS folder can be case-sensitive, where
# AVUTIL-61.dll and avutil-61.dll are two files.
$ffmpegEntries = @(Get-ChildItem -LiteralPath $FFmpegDir -Force)
$ffmpegProblems = @()
if ($ffmpegEntries.Count -ne $ffmpegNames.Count) { $ffmpegProblems += "it holds $($ffmpegEntries.Count) entries, not $($ffmpegNames.Count)" }
foreach ($entry in $ffmpegEntries) {
  if ($ffmpegNames -cnotcontains $entry.Name) { $ffmpegProblems += "it holds $($entry.Name), which doesn't ship" }
  elseif ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $ffmpegProblems += "$($entry.Name) isn't a regular file" }
}
foreach ($name in $ffmpegNames) {
  if (@($ffmpegEntries | Where-Object { $_.Name -ceq $name }).Count -eq 0) { $ffmpegProblems += "it has no $name" }
}
if ($ffmpegProblems) {
  throw "-FFmpegDir $FFmpegDir must hold exactly $($ffmpegNames -join ', ') (native/ffmpeg/build.sh --target windows-x64's bundle\): $($ffmpegProblems -join '; '). Refused."
}

# Refused unless $Path, read through $Stream (a handle on it that the caller holds), is a PE32+ x86-64 DLL whose version
# resource gives the file major $Major, FFmpeg's pinned release as its product version and its own name as its original
# file name (so no other library or major under its name).
function Assert-FFmpegDll([IO.FileStream]$Stream, [string]$Path, [string]$Name, [int]$Major) {
  # Not disposed: disposing the reader would close the held handle.
  $reader = [IO.BinaryReader]::new($Stream)
  $Stream.Position = 0
  if ($Stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "$Path isn't a PE file (no MZ header): refused." }
  $Stream.Position = 0x3C
  $pe = $reader.ReadInt32()
  if ($pe -lt 64 -or $pe + 26 -gt $Stream.Length) { throw "$Path isn't a PE file (its PE header is out of the file): refused." }
  $Stream.Position = $pe
  if ($reader.ReadUInt32() -ne 0x00004550) { throw "$Path isn't a PE file (no PE signature): refused." }
  $machine = $reader.ReadUInt16()
  $Stream.Position = $pe + 22
  $characteristics = $reader.ReadUInt16()
  $magic = $reader.ReadUInt16()
  if ($machine -ne 0x8664 -or $magic -ne 0x20B) { throw ("$Path isn't PE32+ x86-64 (machine 0x{0:X4}, optional header 0x{1:X3}): refused." -f $machine, $magic) }
  if (-not ($characteristics -band 0x2000)) { throw "$Path isn't a DLL: refused." }
  # FileVersionInfo reads only a path, not a handle: it reads $Path, which $Stream holds open for reading only and lets no
  # one else write, replace or rename, so these are the same bytes the checks above read and the caller hashes.
  $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
  if ($info.FileMajorPart -ne $Major) { throw "$Path's version resource gives the major $($info.FileMajorPart), not $Major, which the engine loads: refused." }
  if ($info.ProductVersion -ne $ffmpegPins.ffmpeg_version) { throw "$Path is from FFmpeg '$($info.ProductVersion)', not the pinned $($ffmpegPins.ffmpeg_version): refused." }
  if ($info.OriginalFilename -ne $Name) { throw "$Path was built as '$($info.OriginalFilename)', not ${Name}: refused." }
  Write-Host "$Path : PE32+ x86-64 DLL, FFmpeg $($info.ProductVersion), $Name version $($info.FileVersion)"
}
# Held (opened for reading; others may read a file but not change, replace or rename it, nor its folder) from the start
# of a check until the compiler has packed it, here and below; every handle is closed at the end.
$held = @()
try {
  # Copied into this build's downloads folder; each DLL there is held before it is checked, its checks read the held
  # bytes, and its SHA-256, taken through the same handle, is what the publish's copy is held to below (a built DLL has
  # no pinned hash).
  $ffmpegCopy = (New-Item -ItemType Directory -Path (Join-Path $downloads 'ffmpeg')).FullName
  $ffmpegHashes = @{}
  foreach ($name in $ffmpegNames) { Copy-Item -LiteralPath (Join-Path $FFmpegDir $name) -Destination (Join-Path $ffmpegCopy $name) }
  foreach ($name in $ffmpegDlls.Keys) {
    $path = Join-Path $ffmpegCopy $name
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $held += $stream
    Assert-FFmpegDll $stream $path $name $ffmpegDlls[$name]
    $stream.Position = 0
    $ffmpegHashes[$name] = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash
  }
  foreach ($name in $ffmpegTexts.Keys) { Assert-Pinned (Join-Path $ffmpegCopy $name) 'SHA256' $ffmpegTexts[$name] }

  $pawnIo = Get-Pinned 'PawnIO_setup.exe' "https://github.com/namazso/PawnIO.Setup/releases/download/$pawnIoVersion/PawnIO_setup.exe" $PawnIoSetup 'SHA256' $pawnIoSha256
  $runtimeName = Split-Path -Leaf $Runtime.Url
  $runtimeInstaller = Get-Pinned $runtimeName $Runtime.Url $DotnetRuntime 'SHA512' $Runtime.Sha512

  # The engine, self-contained for win-x64, with the sensor helper, the web client's origin built in.
  & dotnet publish (Join-Path $repo 'InfoPanel\InfoPanel.csproj') -c Release -r win-x64 --self-contained true `
    "-p:Version=$Version" "-p:InfoPanelAllowOrigin=$AllowOrigin" @crashProperty -o $publish --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited $LASTEXITCODE." }
  # Every panel driver project, InfoPanel.Panels.{Family}\ with its {folder}.csproj and a panel.json, but the virtual
  # panel (a test fixture, never shipped): published for win-x64 into plugins\{folder} beside the engine, where it finds
  # its bundled panel packages (docs/2.0/plugins.md, Panel packages); the installer takes the publish's folders whole.
  $panelDrivers = Get-ChildItem -LiteralPath $repo -Directory -Filter 'InfoPanel.Panels.*' |
    Where-Object { $_.Name -ne 'InfoPanel.Panels.Virtual' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'panel.json')) -and (Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj")) }
  foreach ($driver in $panelDrivers) {
    $driverOut = Join-Path $publish "plugins\$($driver.Name)"
    & dotnet publish (Join-Path $driver.FullName "$($driver.Name).csproj") -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o $driverOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the panel driver $($driver.Name) exited $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $driverOut "$($driver.Name).dll")) -or -not (Test-Path -LiteralPath (Join-Path $driverOut 'panel.json'))) {
      throw "The panel driver $($driver.Name) published no $($driver.Name).dll and panel.json."
    }
    Write-Host "Panel driver: $($driver.Name) -> plugins\$($driver.Name)"
  }
  $libusbFinal = Join-Path $publish 'libusb-1.0.dll'
  Copy-Item -LiteralPath $libusbDll -Destination $libusbFinal
  $libusbCopyingFinal = Join-Path $publish 'libusb-COPYING.txt'
  Copy-Item -LiteralPath $libusbCopying -Destination $libusbCopyingFinal
  # FFmpeg's DLLs and texts into the publish's top level, beside the engine, where it loads them (the installer takes
  # the whole top level); one the publish already has stops the build.
  $ffmpegPinned = @()
  foreach ($name in $ffmpegNames) {
    $final = Join-Path $publish $name
    if (Test-Path -LiteralPath $final) { throw "The publish already holds ${name}: refused." }
    Copy-Item -LiteralPath (Join-Path $ffmpegCopy $name) -Destination $final
    if ($ffmpegDlls.Contains($name)) { $ffmpegPinned += , @($final, 'SHA256', $ffmpegHashes[$name], 'hash at check') }
    else { $ffmpegPinned += , @($final, 'SHA256', $ffmpegTexts[$name], 'pinned') }
  }
  $pawnIoDir = (New-Item -ItemType Directory -Path (Join-Path $publish 'pawnio')).FullName
  $pawnIoFinal = Join-Path $pawnIoDir 'PawnIO_setup.exe'
  Copy-Item -LiteralPath $pawnIo -Destination $pawnIoFinal

  # Every bundled file checked again and held until the compiler has packed it, hashed through its handle. One replaced
  # since its first check (during the publish) is refused; none can be replaced while the compiler runs. FFmpeg's DLLs
  # are held to the hash taken through their held copy in downloads, their texts to pins.txt.
  foreach ($pin in @(@($pawnIoFinal, 'SHA256', $pawnIoSha256), @($libusbFinal, 'SHA256', $libusbPins.windows_dll_sha256), @($libusbCopyingFinal, 'SHA256', $libusbPins.source_copying_sha256), @($runtimeInstaller, 'SHA512', $Runtime.Sha512)) + $ffmpegPinned) {
    $stream = [IO.File]::Open($pin[0], [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $held += $stream
    $actual = (Get-FileHash -InputStream $stream -Algorithm $pin[1]).Hash
    $what = if ($pin.Count -gt 3) { $pin[3] } else { 'pinned' }
    if ($actual -ne $pin[2]) { throw "$($pin[0])'s $($pin[1]) is $actual, not the $what $($pin[2]): refused." }
    Write-Host "$($pin[0]) : $($pin[1]) $actual ($what, held until packed)"
  }

  & $Iscc /Qp "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DPublishDir=$publish" "/DRuntimeInstaller=$runtimeInstaller" `
    "/DRuntimeVersion=$($Runtime.Version)" "/DOutputDir=$Out" (Join-Path $PSScriptRoot 'InfoPanel.iss')
  if ($LASTEXITCODE -ne 0) { throw "Inno Setup's compiler exited $LASTEXITCODE." }
} finally {
  foreach ($stream in $held) { $stream.Dispose() }
}

$setup = Join-Path $Out "InfoPanel-$Version-win-x64-setup.exe"
$item = Get-Item -LiteralPath $setup
Write-Output ("{0}: {1} bytes, SHA-256 {2}" -f $item.FullName, $item.Length, (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)
