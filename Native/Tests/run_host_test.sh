#!/usr/bin/env bash
# End-to-end host test (Linux x86-64):
#   1. builds libncsloader.so, sample mods, a fake libil2cpp/libunity and an unpatched libmain
#   2. patches libmain with the C# ELF patcher (NightClubPatcher --elf-add-needed)
#   3. runs a fake "game" that loads libmain -> libunity -> libil2cpp, like Unity on Android
#   4. checks the loader output for every startup stage
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BUILD="${BUILD_DIR:-$ROOT/Native/build-host}"
OUT="$BUILD/out"

cmake -S "$ROOT/Native" -B "$BUILD" -DNCS_HOST_TEST=ON -DCMAKE_BUILD_TYPE=Debug >/dev/null
cmake --build "$BUILD" -j >/dev/null

dotnet run --project "$ROOT/Patcher/NightClubPatcher.Cli" -c Release -- \
  --elf-add-needed "$OUT/libmain_unpatched.so" libncsloader.so "$OUT/libmain.so"
dotnet run --project "$ROOT/Patcher/NightClubPatcher.Cli" -c Release -- --elf-needed "$OUT/libmain.so"

MODS_ROOT="$BUILD/NightClubMods"
rm -rf "$MODS_ROOT"
LOG="$BUILD/host_game.log"
NCS_HOST_PACKAGE=com.mandegames.NightClubSimulater NCS_MODS_ROOT="$MODS_ROOT" \
  LD_LIBRARY_PATH="$OUT" "$OUT/host_game" 2>&1 | tee "$LOG"

expect() {
  if grep -qF -- "$1" "$LOG"; then echo "PASS  $1"; else echo "FAIL  $1"; FAILED=1; fi
}
FAILED=0
expect "[NCSLoader] Hello from NightClubModKit"
expect "STAGE 01 Loader constructor"
expect "STAGE 02 Worker thread"
expect "Package verified"
expect "STAGE 03 libunity detected"
expect "[NCSLoader] libil2cpp.so found"
expect "[NCSLoader] IL2CPP API ready"
expect "STAGE 06 Runtime initialized"
expect "Assembly-CSharp"
expect "UnityEngine.CoreModule"
expect "Unity 6000.2.2f1, game version 1.1.0"
expect "[NCSLoader] Class found"
expect "STAGE 07 Plugin manager initialized"
expect "[NCS] Mod loaded: HelloWorld 1.0.0"
expect "[NCS] Mod loaded: DebugOverlay 0.1.0"
expect "STAGE 08 Mods loaded"
expect "NCSLoader started successfully"
expect "---- NCS Debug Overlay ----"
expect "[fake libmain] constructor"
test -f "$MODS_ROOT/Logs/latest.log" && echo "PASS  latest.log written" || { echo "FAIL  latest.log"; FAILED=1; }
test -f "$MODS_ROOT/Config/config.json" && echo "PASS  default config.json created" || { echo "FAIL  config.json"; FAILED=1; }

# Safe mode: flag file present -> loader starts, no mods.
touch "$MODS_ROOT/disable_mods"
LOG2="$BUILD/host_game_safe.log"
NCS_HOST_PACKAGE=com.mandegames.NightClubSimulater NCS_MODS_ROOT="$MODS_ROOT" \
  LD_LIBRARY_PATH="$OUT" timeout 20 "$OUT/host_game" >"$LOG2" 2>&1 || true
if grep -q "SAFE MODE" "$LOG2" && ! grep -q "Mod loaded" "$LOG2"; then echo "PASS  safe mode"; else echo "FAIL  safe mode"; FAILED=1; fi

# Wrong package: loader must stay idle.
LOG3="$BUILD/host_game_wrongpkg.log"
NCS_HOST_PACKAGE=com.example.other NCS_MODS_ROOT="$MODS_ROOT" \
  LD_LIBRARY_PATH="$OUT" timeout 20 "$OUT/host_game" >"$LOG3" 2>&1 || true
if grep -q "loader stays idle" "$LOG3" && ! grep -q "STAGE 03" "$LOG3"; then echo "PASS  foreign package refused"; else echo "FAIL  foreign package"; FAILED=1; fi

exit $FAILED
