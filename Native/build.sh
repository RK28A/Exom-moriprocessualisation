#!/usr/bin/env bash
# Builds libncmkloader.so + sample mods for arm64-v8a. Output: Native/out/arm64-v8a/
# Tool order: NDK = $1, <kit>/Tools/ndk, ANDROID_NDK_HOME, ANDROID_NDK_ROOT, ANDROID_NDK_LATEST_HOME
#             cmake/ninja = $CMAKE/$NINJA, <kit>/Tools/cmake/bin, <kit>/Tools/ninja, PATH
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
KIT="$(dirname "$HERE")"
TOOLS="$KIT/Tools"
for required in SDK Mods; do
  [ -d "$KIT/$required" ] || { echo "$(basename "$KIT")/$required is missing (Native/ needs SDK/ and Mods/ next to it)"; exit 1; }
done

NDK=""
for c in "${1:-}" "$TOOLS/ndk" "${ANDROID_NDK_HOME:-}" "${ANDROID_NDK_ROOT:-}" "${ANDROID_NDK_LATEST_HOME:-}"; do
  if [ -n "$c" ] && [ -f "$c/build/cmake/android.toolchain.cmake" ]; then NDK="$c"; break; fi
done
[ -n "$NDK" ] || { echo "Android NDK not found (run NightClubPatcher.Cli --install-tools or set ANDROID_NDK_HOME)"; exit 1; }

CMAKE_BIN="${CMAKE:-}"
[ -n "$CMAKE_BIN" ] || { [ -x "$TOOLS/cmake/bin/cmake" ] && CMAKE_BIN="$TOOLS/cmake/bin/cmake"; } || true
[ -n "$CMAKE_BIN" ] || CMAKE_BIN="$(command -v cmake || true)"
[ -n "$CMAKE_BIN" ] || { echo "cmake not found"; exit 1; }

NINJA_BIN="${NINJA:-}"
[ -n "$NINJA_BIN" ] || { [ -x "$TOOLS/ninja/ninja" ] && NINJA_BIN="$TOOLS/ninja/ninja"; } || true
[ -n "$NINJA_BIN" ] || NINJA_BIN="$(command -v ninja || true)"

echo "Android NDK : $NDK"
echo "CMake       : $CMAKE_BIN"
echo "Ninja       : ${NINJA_BIN:-<none, using make>}"

GEN=()
[ -n "$NINJA_BIN" ] && GEN=(-G Ninja "-DCMAKE_MAKE_PROGRAM=$NINJA_BIN")
rm -rf "$HERE/build-android"
"$CMAKE_BIN" -S "$HERE" -B "$HERE/build-android" "${GEN[@]}" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release
"$CMAKE_BIN" --build "$HERE/build-android" -j
test -f "$HERE/out/arm64-v8a/libncmkloader.so"
ls -la "$HERE/out/arm64-v8a"
