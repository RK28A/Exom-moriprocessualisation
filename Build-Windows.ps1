<#
.SYNOPSIS
  Builds NCMK and assembles a ready-to-use Release\ folder for Windows.

.DESCRIPTION
  Release\
  ├── NightClubPatcher.exe        WPF GUI  (main application, self-contained single file)
  ├── NightClubPatcher.Cli.exe    command line (advanced, self-contained single file)
  ├── Native\                     loader sources + build.ps1; out\arm64-v8a\libncmkloader.so (prebuilt or built in-app)
  ├── SDK\  Mods\                 headers and sample mods used by the native build
  ├── Versions\                   game version profiles
  ├── DeviceMods\                 Mods\ + Config\ pushed to the headset's NCMKMods folder
  ├── Tools\                      adb, build-tools, jdk, ndk, cmake, ninja + tools.json (installed by the app)
  ├── Logs\
  └── NCMK.root        marks this folder as the kit root

  The Release folder is NEVER wiped: if you already used the patcher from it, it contains
  Backups\ (your original APK) and Keys\ (your permanent signing key). Only the executables are
  replaced and missing files are added.

.EXAMPLE
  .\Build-Windows.ps1
  .\Build-Windows.ps1 -BuildNative -SmokeTest
  .\Build-Windows.ps1 -Output D:\NCMK
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "Release",
    [switch]$BuildNative,      # also run Native\build.ps1 (needs the Android NDK)
    [switch]$SkipTests,
    [switch]$SmokeTest,        # launch NightClubPatcher.exe --smoke-test and the CLI after publishing
    [switch]$NoSingleFile      # publish as a folder of DLLs instead of single-file executables
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root
if (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $Root $Output }

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# PE header: Subsystem 2 = Windows GUI, 3 = console.
function Get-PeSubsystem([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    return [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
}

Step "Checking the .NET SDK"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0" }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^(8|9|10)\.' })) { throw "A .NET 8 (or newer) SDK is required. Installed: $sdks" }
Write-Host ($sdks -join "`n")

if ($BuildNative) {
    Step "Building the native loader (Android NDK)"
    & (Join-Path $Root "Native\build.ps1")
}

Step "Building the solution (Core, CLI, GUI, Tests)"
Invoke-Checked "dotnet build" { dotnet build (Join-Path $Root "NCMK.sln") -c $Configuration }

if (-not $SkipTests) {
    Step "Running unit tests"
    Invoke-Checked "dotnet test" { dotnet test (Join-Path $Root "Patcher\NightClubPatcher.Tests") -c $Configuration --no-build }
}

$staging = Join-Path $Root "artifacts\publish"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$single = if ($NoSingleFile) { "false" } else { "true" }
$publishArgs = @("-c", $Configuration, "-r", $Runtime, "--self-contained", "true",
                 "-p:PublishSingleFile=$single", "-p:IncludeNativeLibrariesForSelfExtract=$single")

Step "Publishing the GUI (NightClubPatcher.exe, $Runtime, self-contained)"
Invoke-Checked "publish GUI" { dotnet publish (Join-Path $Root "Patcher\NightClubPatcher.Gui") @publishArgs -o (Join-Path $staging "gui") }

Step "Publishing the CLI (NightClubPatcher.Cli.exe, $Runtime, self-contained)"
Invoke-Checked "publish CLI" { dotnet publish (Join-Path $Root "Patcher\NightClubPatcher.Cli") @publishArgs -o (Join-Path $staging "cli") }

Step "Assembling $Output"
New-Item -ItemType Directory -Force -Path $Output | Out-Null
foreach ($part in "gui", "cli") {
    $src = Join-Path $staging $part
    # Versions\ and Payload\ are handled below (never overwrite user-pinned profiles).
    Get-ChildItem $src -Force | Where-Object { $_.Name -notin @("Versions", "Payload") } | ForEach-Object {
        Copy-Item $_.FullName -Destination $Output -Recurse -Force
    }
}

# Version profiles: add missing ones, keep files the user already pinned.
$versionsOut = Join-Path $Output "Versions"
New-Item -ItemType Directory -Force -Path $versionsOut | Out-Null
Get-ChildItem (Join-Path $Root "Versions") -Filter *.json | ForEach-Object {
    $dest = Join-Path $versionsOut $_.Name
    if (Test-Path $dest) { Write-Host "  kept existing Versions\$($_.Name)" }
    else { Copy-Item $_.FullName $dest; Write-Host "  added Versions\$($_.Name)" }
}

# Tools: README files + any tools already placed in the repository's Tools\ (never overwrite).
$toolsOut = Join-Path $Output "Tools"
New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
Get-ChildItem (Join-Path $Root "Tools") -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring((Join-Path $Root "Tools").Length).TrimStart('\', '/')
    $dest = Join-Path $toolsOut $rel
    if (-not (Test-Path $dest)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item $_.FullName $dest
    }
}

# DeviceMods\ (mods + config pushed to the headset): add missing files, never overwrite the user's.
$devOut = Join-Path $Output "DeviceMods"
Get-ChildItem (Join-Path $Root "DeviceMods") -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring((Join-Path $Root "DeviceMods").Length).TrimStart('\', '/')
    $dest = Join-Path $devOut $rel
    if (-not (Test-Path $dest)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item $_.FullName $dest
    }
}

# Native sources, so "Build Native Loader" works from the Release folder (SDK\ and Mods\ are
# referenced by Native\CMakeLists.txt). Build outputs and caches are not copied.
foreach ($dir in "Native", "SDK", "Mods") {
    $src = Join-Path $Root $dir
    $dst = Join-Path $Output $dir
    Get-ChildItem $src -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($src.Length).TrimStart('\', '/')
        -not ($rel -match '^(build[^\\/]*|out)[\\/]')
    } | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length).TrimStart('\', '/')
        $target = Join-Path $dst $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item $_.FullName $target -Force
    }
}
# Every source file must be in the Release folder, or "Build Native Loader" fails there.
foreach ($dir in "Native", "SDK", "Mods") {
    $src = Join-Path $Root $dir
    Get-ChildItem $src -Recurse -File -Include *.cpp, *.c, *.h, *.hpp, CMakeLists.txt | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length).TrimStart('\', '/')
        if ($rel -match '^(build[^\\/]*|out)[\\/]') { return }
        if (-not (Test-Path (Join-Path (Join-Path $Output $dir) $rel))) { throw "Release is missing $dir\$rel" }
    }
}
Write-Host "  copied Native\, SDK\ and Mods\ sources (for Build Native Loader)"

# Prebuilt native payload (when available) goes exactly where the patcher looks for it.
$nativeOut = Join-Path $Root "Native\out\arm64-v8a"
$loader = Join-Path $nativeOut "libncmkloader.so"
$releaseNativeOut = Join-Path $Output "Native\out\arm64-v8a"
if (Test-Path $loader) {
    New-Item -ItemType Directory -Force -Path $releaseNativeOut | Out-Null
    Copy-Item (Join-Path $nativeOut "*.so") $releaseNativeOut -Force
    Write-Host "  copied prebuilt libncmkloader.so + mods to Native\out\arm64-v8a"
} else {
    Write-Host "  no prebuilt loader: build it from the app (Install / Repair Tools, then Build Native Loader)"
}

New-Item -ItemType Directory -Force -Path (Join-Path $Output "Logs") | Out-Null
Set-Content -Path (Join-Path $Output "NCMK.root") -Value "NCMK kit root - keep this file next to NightClubPatcher.exe."
Copy-Item (Join-Path $Root "README.md") (Join-Path $Output "README.md") -Force

Step "Verifying"
$gui = Join-Path $Output "NightClubPatcher.exe"
$cli = Join-Path $Output "NightClubPatcher.Cli.exe"
if (-not (Test-Path $gui)) { throw "NightClubPatcher.exe was not produced." }
if (-not (Test-Path $cli)) { throw "NightClubPatcher.Cli.exe was not produced." }
$guiSub = Get-PeSubsystem $gui
$cliSub = Get-PeSubsystem $cli
if ($guiSub -ne 2) { throw "NightClubPatcher.exe is not a Windows GUI executable (PE subsystem $guiSub)." }
if ($cliSub -ne 3) { throw "NightClubPatcher.Cli.exe is not a console executable (PE subsystem $cliSub)." }
Write-Host "  NightClubPatcher.exe      : Windows GUI (WPF)  $([math]::Round((Get-Item $gui).Length / 1MB, 1)) MB"
Write-Host "  NightClubPatcher.Cli.exe  : console            $([math]::Round((Get-Item $cli).Length / 1MB, 1)) MB"

if ($SmokeTest) {
    Step "Smoke test: CLI"
    Invoke-Checked "NightClubPatcher.Cli.exe --tools" { & $cli --tools }

    Step "Smoke test: GUI window (NightClubPatcher.exe --smoke-test)"
    $p = Start-Process -FilePath $gui -ArgumentList "--smoke-test" -PassThru
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "The GUI did not finish its smoke test within 120 s." }
    if ($p.ExitCode -ne 0) { throw "The GUI smoke test failed (exit code $($p.ExitCode)). See $Output\Logs\crash-*.txt" }
    Write-Host "  WPF main window opened and the first detection completed." -ForegroundColor Green
}

Remove-Item (Join-Path $Root "artifacts") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Release ready: $Output" -ForegroundColor Green
Write-Host "Start the patcher:  $gui" -ForegroundColor Green
