# Builds libncsloader.so and the sample mods for the Quest (arm64-v8a) with the Android NDK.
# Output: Native/out/arm64-v8a/  (picked up automatically by the patcher)
#
#   .\Native\build.ps1                      # uses $env:ANDROID_NDK_HOME / ANDROID_NDK_ROOT
#   .\Native\build.ps1 -Ndk C:\Android\ndk\27.2.12479018
param(
    [string]$Ndk = $(if ($env:ANDROID_NDK_HOME) { $env:ANDROID_NDK_HOME } elseif ($env:ANDROID_NDK_ROOT) { $env:ANDROID_NDK_ROOT } else { "" }),
    [string]$Platform = "android-29",
    [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
    $ndkDir = Join-Path $sdk "ndk"
    if (Test-Path $ndkDir) { $Ndk = (Get-ChildItem $ndkDir | Sort-Object Name -Descending | Select-Object -First 1).FullName }
}
if (-not $Ndk -or -not (Test-Path (Join-Path $Ndk "build\cmake\android.toolchain.cmake"))) {
    throw "Android NDK not found. Install it (Android Studio > SDK Manager > NDK) or pass -Ndk <path>."
}

$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    $sdkCmake = Get-ChildItem (Join-Path (Split-Path $Ndk -Parent | Split-Path -Parent) "cmake") -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($sdkCmake) { $env:PATH = (Join-Path $sdkCmake.FullName "bin") + ";" + $env:PATH } else { throw "cmake not found." }
}

$build = Join-Path $here "build-android"
cmake -S $here -B $build -G Ninja `
    "-DCMAKE_TOOLCHAIN_FILE=$Ndk\build\cmake\android.toolchain.cmake" `
    -DANDROID_ABI=arm64-v8a "-DANDROID_PLATFORM=$Platform" -DANDROID_STL=c++_static "-DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE) { throw "cmake configure failed" }
cmake --build $build
if ($LASTEXITCODE) { throw "cmake build failed" }

Write-Host ""
Write-Host "Built:" -ForegroundColor Green
Get-ChildItem (Join-Path $here "out\arm64-v8a") -Filter *.so | ForEach-Object { Write-Host "  $($_.FullName)" }
