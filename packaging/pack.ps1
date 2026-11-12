# Builds the MSIX package for SimpleLetters Fun (Windows).
#
#   .\packaging\pack.ps1                     unsigned .msix for Store upload (the Store signs it)
#   .\packaging\pack.ps1 -Version 1.0.1.0    bump the package version (the last part must stay 0 for the Store)
#   .\packaging\pack.ps1 -Install            also install it on this PC for testing (needs Developer Mode)
#
# -Install copies the package layout to %USERPROFILE%\apps\SimpleFunLetters-dev and
# registers it from there, so it shows in the Start menu and opens .funletter files
# like the Store version, and rebuilding the project doesn't disturb it. Remove
# it from Settings > Apps, or with: Get-AppxPackage AkwardSnail.64090DED9158F | Remove-AppxPackage
#
# Output goes to packaging\out\. Needs the .NET SDK and the Windows 10/11 SDK
# (makeappx, makepri).

param(
    [string]$Version = '1.0.0.0',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'
$priRoot = Join-Path $out 'pri'
$msix = Join-Path $out "SimpleFunLetters_${Version}_x64.msix"

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Version must look like 1.2.3.0 (the Store needs the last part to be 0).' }

$sdkBin = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\makeappx.exe' |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty DirectoryName
if (-not $sdkBin) { throw 'Windows SDK not found; install it from the Visual Studio Installer.' }

function Invoke-Tool([string]$exe) {
    $output = & (Join-Path $sdkBin $exe) @args 2>&1
    if ($LASTEXITCODE -ne 0) { $output | Out-Host; throw "$exe failed with exit code $LASTEXITCODE" }
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, $priRoot | Out-Null

# Self-contained, because no .NET 10 desktop runtime is available as a Store dependency.
$assemblyVersion = $Version -replace '\.\d+$', ''   # 1.0.0.0 -> 1.0.0
dotnet publish (Join-Path $root 'desktop\SimpleFunLetters') -c Release -r win-x64 --self-contained true `
    "-p:Version=$assemblyVersion" -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem $stage -Filter *.pdb | Remove-Item

$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw) -replace '(<Identity[^>]*?Version=")[^"]+', "`${1}$Version"
Set-Content (Join-Path $stage 'AppxManifest.xml') $manifest -Encoding utf8
Copy-Item (Join-Path $PSScriptRoot 'Assets') $stage -Recurse

# resources.pri maps Assets\Square44x44Logo.png to its scale/targetsize variants.
# Index only the assets, not the whole published app.
Copy-Item (Join-Path $PSScriptRoot 'Assets') $priRoot -Recurse
Copy-Item (Join-Path $stage 'AppxManifest.xml') $priRoot
$priConfig = Join-Path $out 'priconfig.xml'
Invoke-Tool makepri.exe createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o
# Keep every scale in one resources.pri; split resource packs only work in an .msixbundle.
(Get-Content $priConfig -Raw) -replace '(?s)\s*<packaging>.*?</packaging>', '' | Set-Content $priConfig -Encoding utf8
Invoke-Tool makepri.exe new /pr $priRoot /cf $priConfig /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $stage 'resources.pri') /o

Invoke-Tool makeappx.exe pack /d $stage /p $msix /o
"Package: $msix"

if ($Install) {
    $name = ([xml]$manifest).Package.Identity.Name
    $dev = Join-Path $env:USERPROFILE 'apps\SimpleFunLetters-dev'
    # Replace any earlier test install (and the folder it runs from).
    Get-AppxPackage -Name $name | Remove-AppxPackage
    Remove-Item $dev -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dev | Out-Null
    Copy-Item (Join-Path $stage '*') $dev -Recurse
    Add-AppxPackage -Register (Join-Path $dev 'AppxManifest.xml')
    $pkg = Get-AppxPackage -Name $name
    "Installed $($pkg.Name) $($pkg.Version) from $($pkg.InstallLocation)"
}
