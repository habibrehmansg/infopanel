# Builds InfoPanel 2's Windows installer, InfoPanel-<version>-win-x64-setup.exe, on Windows (a Windows publish puts the
# manifest and the icon into InfoPanel.exe only when built on Windows). Needs the .NET SDK (global.json's) and Inno
# Setup 6. See docs/2.0/packaging.md.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\build.ps1 `
#     -Version 2.0.0-beta.1 -AllowOrigin https://client.example -Out C:\builds\2.0.0-beta.1
#
# -Out must be new or empty, a plain path (no junction or link in it) writable only by the user building: the publish and
# the installer go there, nothing is deleted. The two files it bundles are downloaded into -Out (or taken from
# -PawnIoSetup and -DotnetRuntime) and refused unless their hashes are the pinned ones: PawnIO's setup against
# PawnIoInstaller.BundledSha256, the .NET runtime installer against $Runtime.Sha512 below. The USB panels' library,
# libusb-1.0.dll, comes from libusb's release (its 7z downloaded, or -LibusbArchive, and unpacked with Windows' own tar),
# the archive and the DLL both checked against native\libusb\pins.txt, and goes into the publish beside the engine, with
# libusb's licence text, libusb-COPYING.txt: the COPYING of the release's source tarball (downloaded, or -LibusbSource),
# the tarball and the file both checked against pins.txt too.
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$AllowOrigin,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$PawnIoSetup,
  [string]$DotnetRuntime,
  [string]$LibusbArchive,
  [string]$LibusbSource,
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
if (-not (Test-Path -LiteralPath $Iscc)) { throw "Inno Setup 6's compiler isn't at $Iscc (install Inno Setup 6, or pass -Iscc)." }
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
$libusbArchiveName = "libusb-$($libusbPins.version).7z"
$libusbArchivePath = Get-Pinned $libusbArchiveName "https://github.com/libusb/libusb/releases/download/v$($libusbPins.version)/$libusbArchiveName" $LibusbArchive 'SHA256' $libusbPins.windows_archive_sha256
$libusbUnpacked = (New-Item -ItemType Directory -Path (Join-Path $downloads 'libusb')).FullName
& tar.exe -xf $libusbArchivePath -C $libusbUnpacked $libusbPins.windows_dll_path
if ($LASTEXITCODE -ne 0) { throw "tar.exe couldn't unpack $libusbArchivePath (exit $LASTEXITCODE)." }
$libusbDll = Join-Path $libusbUnpacked ($libusbPins.windows_dll_path -replace '/', '\')
Assert-Pinned $libusbDll 'SHA256' $libusbPins.windows_dll_sha256
# libusb's licence text: the 7z holds none, so it is the COPYING of the release's source tarball (downloaded, or
# -LibusbSource), the tarball and the file both checked against pins.txt; installed beside the engine as libusb-COPYING.txt.
$libusbSourceName = "libusb-$($libusbPins.version).tar.bz2"
$libusbSourcePath = Get-Pinned $libusbSourceName "https://github.com/libusb/libusb/releases/download/v$($libusbPins.version)/$libusbSourceName" $LibusbSource 'SHA256' $libusbPins.source_sha256
$libusbSourceUnpacked = (New-Item -ItemType Directory -Path (Join-Path $downloads 'libusb-source')).FullName
& tar.exe -xf $libusbSourcePath -C $libusbSourceUnpacked "libusb-$($libusbPins.version)/COPYING"
if ($LASTEXITCODE -ne 0) { throw "tar.exe couldn't unpack $libusbSourcePath (exit $LASTEXITCODE)." }
$libusbCopying = Join-Path $libusbSourceUnpacked "libusb-$($libusbPins.version)\COPYING"
Assert-Pinned $libusbCopying 'SHA256' $libusbPins.source_copying_sha256

$pawnIo = Get-Pinned 'PawnIO_setup.exe' "https://github.com/namazso/PawnIO.Setup/releases/download/$pawnIoVersion/PawnIO_setup.exe" $PawnIoSetup 'SHA256' $pawnIoSha256
$runtimeName = Split-Path -Leaf $Runtime.Url
$runtimeInstaller = Get-Pinned $runtimeName $Runtime.Url $DotnetRuntime 'SHA512' $Runtime.Sha512

# The engine, self-contained for win-x64, with the sensor helper, the web client's origin built in.
& dotnet publish (Join-Path $repo 'InfoPanel\InfoPanel.csproj') -c Release -r win-x64 --self-contained true `
  "-p:Version=$Version" "-p:InfoPanelAllowOrigin=$AllowOrigin" -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited $LASTEXITCODE." }
$libusbFinal = Join-Path $publish 'libusb-1.0.dll'
Copy-Item -LiteralPath $libusbDll -Destination $libusbFinal
$libusbCopyingFinal = Join-Path $publish 'libusb-COPYING.txt'
Copy-Item -LiteralPath $libusbCopying -Destination $libusbCopyingFinal
$pawnIoDir = (New-Item -ItemType Directory -Path (Join-Path $publish 'pawnio')).FullName
$pawnIoFinal = Join-Path $pawnIoDir 'PawnIO_setup.exe'
Copy-Item -LiteralPath $pawnIo -Destination $pawnIoFinal

# Every bundled file checked again and held until the compiler has packed it: opened for reading, others may read it but
# not change, replace or rename it (nor its folder), and hashed through that handle. One replaced since its first check
# (during the publish) is refused; none can be replaced while the compiler runs.
$held = @()
try {
  foreach ($pin in @(@($pawnIoFinal, 'SHA256', $pawnIoSha256), @($libusbFinal, 'SHA256', $libusbPins.windows_dll_sha256), @($libusbCopyingFinal, 'SHA256', $libusbPins.source_copying_sha256), @($runtimeInstaller, 'SHA512', $Runtime.Sha512))) {
    $stream = [IO.File]::Open($pin[0], [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $held += $stream
    $actual = (Get-FileHash -InputStream $stream -Algorithm $pin[1]).Hash
    if ($actual -ne $pin[2]) { throw "$($pin[0])'s $($pin[1]) is $actual, not the pinned $($pin[2]): refused." }
    Write-Host "$($pin[0]) : $($pin[1]) $actual (pinned, held until packed)"
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
