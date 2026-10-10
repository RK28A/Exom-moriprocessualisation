#!/usr/bin/env bash
# End-to-end host test (Linux x86-64):
#   1. builds libncmkloader.so, sample mods, a fake libil2cpp/libunity and an unpatched libmain
#   2. patches libmain with the C# ELF patcher (NightClubPatcher.Cli --elf-add-needed)
#   3. runs a fake "game" that loads libmain -> libunity -> libil2cpp, like Unity on Android
#   4. checks the loader output for every startup stage
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BUILD="${BUILD_DIR:-$ROOT/Native/build-host}"
OUT="$BUILD/out"

cmake -S "$ROOT/Native" -B "$BUILD" -DNCMK_HOST_TEST=ON -DCMAKE_BUILD_TYPE="${BUILD_TYPE:-Debug}" >/dev/null
cmake --build "$BUILD" -j >/dev/null

dotnet run --project "$ROOT/Patcher/NightClubPatcher.Cli" -c Release -- \
  --elf-add-needed "$OUT/libmain_unpatched.so" libncmkloader.so "$OUT/libmain.so"
dotnet run --project "$ROOT/Patcher/NightClubPatcher.Cli" -c Release -- --elf-needed "$OUT/libmain.so"

MODS_ROOT="$BUILD/NCMKMods"
rm -rf "$MODS_ROOT"
FAILED=0

# run <log> [VAR=value...]: one fake game session (Meta Quest 3S by default).
run() {
  local log="$1"; shift
  env NCMK_HOST_PACKAGE=com.mandegames.NightClubSimulater NCMK_MODS_ROOT="$MODS_ROOT" \
    NCMK_HOST_MODEL="Quest 3S" NCMK_HOST_DEVICE=panther NCMK_HOST_ANDROID=14 NCMK_HOST_SDK=34 \
    LD_LIBRARY_PATH="$OUT" "$@" timeout 60 "$OUT/host_game" >"$log" 2>&1 || true
}
expect() {  # expect <log> <text>
  if grep -qF -- "$2" "$1"; then echo "PASS  $2"; else echo "FAIL  $2   (in $(basename "$1"))"; FAILED=1; fi
}
reject() {  # reject <log> <text>
  if grep -qF -- "$2" "$1"; then echo "FAIL  unexpected: $2   (in $(basename "$1"))"; FAILED=1; else echo "PASS  absent: $2"; fi
}

# 1) Default player loop: ExecuteTasks never runs, EventSystem.Update runs twice per frame (two
#    instances), OVRManager.Update once. The dispatcher must give up on ExecuteTasks after 5 s,
#    hook EventSystem.Update, dispatch once per frame, then restore every slot on NCMK_Shutdown.
LOG="$BUILD/host_game.log"
run "$LOG"
cat "$LOG"
echo "==== checks"
expect "$LOG" "[NCMK] Hello from NCMK"
expect "$LOG" "STAGE 01 Loader constructor"
expect "$LOG" "STAGE 02 Worker thread"
expect "$LOG" "Package verified"
expect "$LOG" "STAGE 03 libunity detected"
expect "$LOG" "[NCMK] libil2cpp.so found"
expect "$LOG" "[NCMK] IL2CPP API ready"
expect "$LOG" "STAGE 06 Runtime initialized"
expect "$LOG" "Assembly-CSharp"
expect "$LOG" "UnityEngine.CoreModule"
expect "$LOG" "Unity 6000.2.2f1, game version 1.1.0"
expect "$LOG" "[NCMK] Class found"
expect "$LOG" "STAGE 07 Plugin manager initialized"
expect "$LOG" "[NCMK] Mod loaded: HelloWorld 1.0.0"
expect "$LOG" "[NCMK] Mod loaded: DebugOverlay 0.2.0"
expect "$LOG" "[NCMK] Mod loaded: LegacyTest 0.9.0 (ncs.legacytest by old, API 1, legacy NCSMod exports)"
expect "$LOG" "legacy NCSMod_Update ticking on the background thread"
expect "$LOG" "STAGE 08 Mods loaded"
expect "$LOG" "NCMK started successfully"
expect "$LOG" "[fake libmain] constructor"
expect "$LOG" "Device: Meta Quest 3S (Quest 3S / panther), Android 14 (SDK 34)"
expect "$LOG" "[frametest] API 2 host checks: PASS"
expect "$LOG" "no tick from UnityEngine.CoreModule UnityEngine.UnitySynchronizationContext.ExecuteTasks (static) after 5000 ms"
expect "$LOG" "STAGE 09 Main-thread dispatcher: hooked UnityEngine.UI UnityEngine.EventSystems.EventSystem.Update (instance)"
expect "$LOG" "Scene loaded: MainMenu"
expect "$LOG" "Scene loaded: Club"
expect "$LOG" "---- NCMK Debug Overlay ----"
expect "$LOG" "Device   : Meta Quest 3S"
expect "$LOG" "[frametest] RESULT PASS"
expect "$LOG" "[host_game] before shutdown: original slots ExecuteTasks=1 EventSystem=0 OVRManager=1, OVRManager slot1=1"
expect "$LOG" "Dispatcher stopped: MethodInfo pointers restored"
expect "$LOG" "[host_game] after shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1"
expect "$LOG" "[host_game] post-unload frame ok: yes"
test -f "$MODS_ROOT/Logs/latest.log" && echo "PASS  latest.log written" || { echo "FAIL  latest.log"; FAILED=1; }
test -f "$MODS_ROOT/Config/config.json" && echo "PASS  default config.json created" || { echo "FAIL  config.json"; FAILED=1; }
test -f "$MODS_ROOT/Config/ncmk.debugoverlay.cfg" && echo "PASS  NCMK::Config wrote ncmk.debugoverlay.cfg" || { echo "FAIL  mod config"; FAILED=1; }

# 2) ExecuteTasks ticks: first target wins immediately.
LOG5="$BUILD/host_game_executetasks.log"
run "$LOG5" HOST_FRAME_TARGETS="ExecuteTasks,EventSystem" HOST_GAME_SECONDS=3
expect "$LOG5" "STAGE 09 Main-thread dispatcher: hooked UnityEngine.CoreModule UnityEngine.UnitySynchronizationContext.ExecuteTasks (static)"
expect "$LOG5" "[frametest] RESULT PASS"
expect "$LOG5" "[host_game] after shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1"

# 3) Only OVRManager.Update runs: third target; its slot 1 differs from slot 0 and must stay untouched.
LOG6="$BUILD/host_game_ovr.log"
run "$LOG6" HOST_FRAME_TARGETS="OVRManager" HOST_GAME_SECONDS=3 NCMK_HOOK_TIMEOUT_MS=300
expect "$LOG6" "STAGE 09 Main-thread dispatcher: hooked Oculus.VR OVRManager.Update (instance)"
expect "$LOG6" "Dispatcher: trying Oculus.VR OVRManager.Update (instance) (slot 0 swapped)"
expect "$LOG6" "[host_game] before shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=0, OVRManager slot1=1"
expect "$LOG6" "[host_game] after shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1"

# 4) Nothing ticks: one STAGE 09 line saying so, every slot restored, main-thread requests refused.
LOG7="$BUILD/host_game_none.log"
run "$LOG7" HOST_FRAME_TARGETS="none" HOST_GAME_SECONDS=3 NCMK_HOOK_TIMEOUT_MS=300
expect "$LOG7" "STAGE 09 Main-thread dispatcher: no per-frame target worked"
expect "$LOG7" "[host_game] before shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1, OVRManager slot1=1"
expect "$LOG7" "queued main-thread call(s) dropped"

# 5) Safe mode: flag file present -> loader starts, no mods, dispatcher never installed.
touch "$MODS_ROOT/disable_mods"
LOG2="$BUILD/host_game_safe.log"
run "$LOG2" HOST_GAME_SECONDS=2
expect "$LOG2" "SAFE MODE"
expect "$LOG2" "STAGE 09 Main-thread dispatcher: not installed (Safe Mode)"
expect "$LOG2" "[host_game] before shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1, OVRManager slot1=1"
reject "$LOG2" "Mod loaded"
reject "$LOG2" "Dispatcher: trying"
rm -f "$MODS_ROOT/disable_mods"

# 6) Quest 3 (eureka) must not be mistaken for a Quest 3S.
LOG4="$BUILD/host_game_quest3.log"
run "$LOG4" NCMK_HOST_MODEL="Quest 3" NCMK_HOST_DEVICE=eureka HOST_GAME_SECONDS=1
expect "$LOG4" "Device: Meta Quest 3 (Quest 3 / eureka)"

# 7) Wrong package: loader must stay idle.
LOG3="$BUILD/host_game_wrongpkg.log"
run "$LOG3" NCMK_HOST_PACKAGE=com.example.other HOST_GAME_SECONDS=1
expect "$LOG3" "loader stays idle"
reject "$LOG3" "STAGE 03"

exit $FAILED
