<#
.SYNOPSIS
  Builds libncmkloader.so and the sample mods for the Quest (arm64-v8a).
  Output: Native\out\arm64-v8a\  (exactly where the patcher looks for the loader)

.DESCRIPTION
  Normally you never run this by hand: NightClubPatcher.exe -> "Build Native Loader" calls it with
  the portable tools it installed in Tools\. It also works standalone (Windows PowerShell 5.1+).

  Tool detection order (the kit folder is the parent of this Native\ folder):
    NDK   : -Ndk, <kit>\Tools\ndk, ANDROID_NDK_HOME, ANDROID_NDK_ROOT, <Android SDK>\ndk\<latest>, ndk-bundle
    CMake : -CMake, <kit>\Tools\cmake\bin\cmake.exe, PATH, <Android SDK>\cmake\<latest>\bin
    Ninja : -Ninja, <kit>\Tools\ninja\ninja.exe, PATH, <Android SDK>\cmake\<latest>\bin

  The chosen tools are prepended to PATH for this process only; the user's PATH is never modified.

.EXAMPLE
  .\Native\build.ps1
  .\Native\build.ps1 -Ndk C:\Android\ndk\27.2.12479018
#>
[CmdletBinding()]
param(
    [string]$Ndk = "",
    [string]$CMake = "",
    [string]$Ninja = "",
    [string]$Platform = "android-29",
    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$kit = Split-Path -Parent $here
$tools = Join-Path $kit "Tools"
$isWin = ($env:OS -eq "Windows_NT")
$exe = if ($isWin) { ".exe" } else { "" }
$hint = "Open NightClubPatcher.exe and click 'Install / Repair Tools' (or run NightClubPatcher.Cli.exe --install-tools)."

function Get-SdkRoots {
    $roots = @()
    foreach ($v in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) { if ($v) { $roots += $v } }
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA "Android\Sdk") }
    return $roots | Where-Object { Test-Path $_ }
}

function Get-SdkCMakeBins {
    $bins = @()
    foreach ($root in Get-SdkRoots) {
        $cm = Join-Path $root "cmake"
        if (Test-Path $cm) {
            Get-ChildItem $cm -Directory | Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
                ForEach-Object { $bins += (Join-Path $_.FullName "bin") }
        }
    }
    return $bins
}

function Test-Ndk([string]$dir) {
    return $dir -and (Test-Path (Join-Path $dir "build\cmake\android.toolchain.cmake"))
}

function Find-FromPath([string]$name) {
    $c = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { return $c.Source }
    return $null
}

# ---- NDK -----------------------------------------------------------------------------------
$ndkCandidates = @()
if ($Ndk) { $ndkCandidates += $Ndk }
$ndkCandidates += (Join-Path $tools "ndk")
foreach ($v in @($env:ANDROID_NDK_HOME, $env:ANDROID_NDK_ROOT)) { if ($v) { $ndkCandidates += $v } }
foreach ($root in Get-SdkRoots) {
    $ndkDir = Join-Path $root "ndk"
    if (Test-Path $ndkDir) {
        Get-ChildItem $ndkDir -Directory | Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
            ForEach-Object { $ndkCandidates += $_.FullName }
    }
    $ndkCandidates += (Join-Path $root "ndk-bundle")
}
$NdkFound = $ndkCandidates | Where-Object { Test-Ndk $_ } | Select-Object -First 1
if (-not $NdkFound) {
    if ($Ndk) { throw "-Ndk '$Ndk' is not an Android NDK (build\cmake\android.toolchain.cmake missing)." }
    throw "Android NDK not found. $hint"
}

# ---- CMake ---------------------------------------------------------------------------------
$cmakeCandidates = @()
if ($CMake) { $cmakeCandidates += $CMake }
$cmakeCandidates += (Join-Path $tools "cmake\bin\cmake$exe")
$p = Find-FromPath "cmake"; if ($p) { $cmakeCandidates += $p }
foreach ($b in Get-SdkCMakeBins) { $cmakeCandidates += (Join-Path $b "cmake$exe") }
$CMakeFound = $cmakeCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $CMakeFound) { throw "CMake not found. $hint" }

# ---- Ninja ---------------------------------------------------------------------------------
$ninjaCandidates = @()
if ($Ninja) { $ninjaCandidates += $Ninja }
$ninjaCandidates += (Join-Path $tools "ninja\ninja$exe")
$p = Find-FromPath "ninja"; if ($p) { $ninjaCandidates += $p }
foreach ($b in Get-SdkCMakeBins) { $ninjaCandidates += (Join-Path $b "ninja$exe") }
$NinjaFound = $ninjaCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $NinjaFound) { throw "Ninja not found. $hint" }

Write-Host "Android NDK : $NdkFound"
Write-Host "CMake       : $CMakeFound"
Write-Host "Ninja       : $NinjaFound"

# Process-local PATH only.
$sep = [System.IO.Path]::PathSeparator
$env:PATH = (Split-Path -Parent $CMakeFound) + $sep + (Split-Path -Parent $NinjaFound) + $sep + $NdkFound + $sep + $env:PATH
$env:ANDROID_NDK_HOME = $NdkFound

# Fresh build directory: avoids stale CMake caches from another NDK / generator.
$build = Join-Path $here "build-android"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }

# Native tools write warnings to stderr; with "Stop" Windows PowerShell 5.1 could turn them into
# terminating errors. Failures are detected through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"
$toolchain = Join-Path $NdkFound "build\cmake\android.toolchain.cmake"
& $CMakeFound -S $here -B $build -G Ninja `
    "-DCMAKE_MAKE_PROGRAM=$NinjaFound" `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
    -DANDROID_ABI=arm64-v8a "-DANDROID_PLATFORM=$Platform" -DANDROID_STL=c++_static "-DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE) { throw "cmake configure failed (exit code $LASTEXITCODE)" }
& $CMakeFound --build $build
if ($LASTEXITCODE) { throw "cmake build failed (exit code $LASTEXITCODE)" }

$ErrorActionPreference = "Stop"
$out = Join-Path $here "out\arm64-v8a"
$loader = Join-Path $out "libncmkloader.so"
if (-not (Test-Path $loader)) { throw "Build finished but $loader is missing." }

Write-Host ""
Write-Host "Built:" -ForegroundColor Green
Get-ChildItem $out -Filter *.so | ForEach-Object { Write-Host "  $($_.FullName)" }
