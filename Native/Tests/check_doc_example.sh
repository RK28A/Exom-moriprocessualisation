#!/usr/bin/env bash
# Extracts the complete example mod from docs/MODDING.md, compiles it (Debug and Release, -Werror)
# and loads it into the fake game, so the documented example can never silently rot.
# Run after run_host_test.sh (reuses its build directory).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BUILD="${BUILD_DIR:-$ROOT/Native/build-host}"
OUT="$BUILD/out"
WORK="$BUILD/doc-example"
CXX="${CXX:-c++}"
rm -rf "$WORK" && mkdir -p "$WORK"

# The first ```cpp block after the "## Complete example mod" heading.
awk '/^## Complete example mod/{s=1} s&&/^```cpp/{c=1;next} c&&/^```/{exit} c{print}' "$ROOT/docs/MODDING.md" >"$WORK/example.cpp"
test -s "$WORK/example.cpp" || { echo "FAIL  example not found in docs/MODDING.md"; exit 1; }

for cfg in "-g" "-O2 -DNDEBUG"; do
  # shellcheck disable=SC2086
  "$CXX" -std=c++17 -fPIC -shared -fvisibility=hidden -fno-exceptions -Wall -Wextra -Wno-unused-parameter -Werror $cfg \
    -I"$ROOT" "$WORK/example.cpp" -o "$WORK/libncmkmod_docexample.so"
done
echo "PASS  docs example compiles (Debug + Release, -Werror)"

cp "$WORK/libncmkmod_docexample.so" "$OUT/"
LOG="$WORK/run.log"
env NCMK_HOST_PACKAGE=com.mandegames.NightClubSimulater NCMK_MODS_ROOT="$WORK/NCMKMods" \
  HOST_FRAME_TARGETS=ExecuteTasks HOST_GAME_SECONDS=2 LD_LIBRARY_PATH="$OUT" timeout 30 "$OUT/host_game" >"$LOG" 2>&1 || true
rm -f "$OUT/libncmkmod_docexample.so"
FAILED=0
for want in "Mod loaded: ClickCounter 1.0.0" "ClickCounter running on the main thread" "[host_game] exiting"; do
  if grep -qF -- "$want" "$LOG"; then echo "PASS  $want"; else echo "FAIL  $want"; FAILED=1; fi
done
test -f "$WORK/NCMKMods/Config/example.clickcounter.cfg" && echo "PASS  example config written" || { echo "FAIL  example config"; FAILED=1; }
[ $FAILED = 0 ] || cat "$LOG"
exit $FAILED
