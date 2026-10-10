#!/usr/bin/env bash
# Builds libncsloader.so + sample mods for arm64-v8a. Output: Native/out/arm64-v8a/
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
NDK="${1:-${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-${ANDROID_NDK_LATEST_HOME:-}}}}"
[ -f "$NDK/build/cmake/android.toolchain.cmake" ] || { echo "Android NDK not found (set ANDROID_NDK_HOME or pass the path)"; exit 1; }
GEN=()
command -v ninja >/dev/null && GEN=(-G Ninja)
cmake -S "$HERE" -B "$HERE/build-android" "${GEN[@]}" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release
cmake --build "$HERE/build-android" -j
ls -la "$HERE/out/arm64-v8a"
