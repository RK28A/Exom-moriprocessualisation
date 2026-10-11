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
expect "$LOG" "[NCMK] Mod loaded: DebugOverlay 0.3.0"
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
expect "$LOG" "Input: no OVRManager instance - NCMK calls OVRInput.Update() once per frame"
expect "$LOG" "NCMK::Input source: UnityEngine.XR.InputDevices (OVRInput stayed silent for 90 frames)"
expect "$LOG" "[frametest] input: source=2 rightDown=1 pose=(0.25, 1.10, 0.40) valid=1"
expect "$LOG" "[frametest] RESULT PASS"
expect "$LOG" "[host_game] before shutdown: original slots ExecuteTasks=1 EventSystem=0 OVRManager=1, OVRManager slot1=1"
expect "$LOG" "Dispatcher stopped: MethodInfo pointers restored"
expect "$LOG" "[host_game] after shutdown: original slots ExecuteTasks=1 EventSystem=1 OVRManager=1"
expect "$LOG" "[host_game] post-unload frame ok: yes"
# Scene loader / platform / storage diagnostics (default ON, behaviour unchanged).
expect "$LOG" "[NCMK][SceneLoader] bl_SceneLoaderManager found"
expect "$LOG" "[NCMK][SceneLoader] field m_Instance"
expect "$LOG" "[NCMK][SceneLoader] LoadSceneByID resolved"
expect "$LOG" "[NCMK][SceneLoader] IsGlobalLoadingScreen resolved"
expect "$LOG" "[NCMK][SceneLoader] bl_SceneLoader.Awake probe installed"
expect "$LOG" "[NCMK][SceneLoader] bl_SceneLoader.Awake begins"
expect "$LOG" "[NCMK][SceneLoader] NULL FIELD: static m_Instance (bl_SceneLoaderManager) = NULL"
expect "$LOG" "[NCMK][SceneLoader] IsGlobalLoadingScreen() threw System.NullReferenceException: Object reference not set"
expect "$LOG" "[NCMK][SceneLoader] recovery off"
expect "$LOG" "[NCMK][Storage] /sdcard/Android/obb/com.mandegames.NightClubSimulater"
expect "$LOG" "[NCMK][MetaPlatform] Oculus.Platform.PlatformSettings not found"
expect "$LOG" "slots restored: yes"
reject "$LOG" "attempting SceneManager fallback"
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

# 7b) Diagnostics with every mod disabled (device "Test A/B"): the dispatcher is still installed for
#     the diagnostics only, and recovery (on) loads the configured scene once after a confirmed failure.
cat > "$MODS_ROOT/Config/config.json" <<'JSON'
{
  "loader": { "logging": true, "safeMode": false, "sceneLoaderDiagnostics": true, "sceneLoaderRecovery": true,
              "sceneLoaderRecoveryScene": 0, "sceneLoaderRecoveryDelaySeconds": 1 },
  "mods": { "ncmk.helloworld": false, "ncmk.debugoverlay": false, "ncs.legacytest": false, "ncmk.test.frametest": false }
}
JSON
LOG8="$BUILD/host_game_recovery.log"
run "$LOG8" HOST_FRAME_TARGETS="ExecuteTasks" HOST_GAME_SECONDS=4
expect "$LOG8" "0 mod(s) loaded"
expect "$LOG8" "STAGE 09 Main-thread dispatcher: hooked UnityEngine.CoreModule"
expect "$LOG8" "sceneLoaderRecovery=1 recoveryScene=0 recoveryDelay=1s"
expect "$LOG8" "[NCMK][SceneLoader] recovery ON"
expect "$LOG8" "[NCMK][SceneLoader] attempting SceneManager fallback"
expect "$LOG8" "[fake il2cpp] SceneManager.LoadSceneAsync(0)"
expect "$LOG8" "[NCMK][SceneLoader] LoadSceneAsync(0) 'Assets/Scenes/MainMenu.unity' started"
reject "$LOG8" "Input: no OVRManager instance"
# Default target (active index + 1) is out of range in the fake build: refused, nothing loaded.
sed -i 's/"sceneLoaderRecoveryScene": 0/"sceneLoaderRecoveryScene": -1/' "$MODS_ROOT/Config/config.json"
LOG9="$BUILD/host_game_recovery_refused.log"
run "$LOG9" HOST_FRAME_TARGETS="ExecuteTasks" HOST_GAME_SECONDS=4
expect "$LOG9" "[NCMK][SceneLoader] Recovery refused: invalid sceneID 2"
reject "$LOG9" "[fake il2cpp] SceneManager.LoadSceneAsync"
rm -f "$MODS_ROOT/Config/config.json"

# 8) Drop-in mod: a mod is ONE folder in Mods/ (mod.json + .cpp). Built in a temporary copy of the
#    kit, so no tracked file changes. Also: .disabled folders, folders without mod.json, stale
#    output removal and duplicate output names.
TRACKED_BEFORE="$(cd "$ROOT" && git status --porcelain 2>/dev/null || true)"
KIT="$BUILD/dropin-kit"
rm -rf "$KIT" && mkdir -p "$KIT"
cp -r "$ROOT/SDK" "$ROOT/Mods" "$KIT/"
mkdir -p "$KIT/Native" && (cd "$ROOT/Native" && tar --exclude='./build*' --exclude='./out' -cf - .) | (cd "$KIT/Native" && tar -xf -)
mkdir -p "$KIT/Mods/Pulse/src" "$KIT/Mods/Off" "$KIT/Mods/NoJson"
cat >"$KIT/Mods/Pulse/mod.json" <<'JSON'
{ "id": "test.pulse", "name": "Pulse", "version": "1.0.0", "requiredApi": 2, "library": "libncmkmod_pulse.so" }
JSON
cat >"$KIT/Mods/Pulse/src/pulse.cpp" <<'CPP'
#include "SDK/Core/ncmk_sdk.hpp"
static const NCMKModInfo kInfo = {sizeof(NCMKModInfo), "test.pulse", "Pulse", "test", "1.0.0", NCMK_LOADER_API};
extern "C" NCMK_EXPORT const NCMKModInfo* NCMKMod_GetInfo(void) { return &kInfo; }
extern "C" NCMK_EXPORT int NCMKMod_Load(const NCMKHostApi* api) { return NCMK::Init(api, kInfo.id) ? 0 : 1; }
extern "C" NCMK_EXPORT void NCMKMod_OnUpdate(float) {
  static bool once = false;
  if (!once) { once = true; NCMK_LOGI("[pulse] OnUpdate from a dropped-in folder"); }
}
CPP
echo '{ "id": "test.off" }' >"$KIT/Mods/Off/mod.json"
cp "$KIT/Mods/Pulse/src/pulse.cpp" "$KIT/Mods/Off/off.cpp"
touch "$KIT/Mods/Off/.disabled"
cp "$KIT/Mods/Pulse/src/pulse.cpp" "$KIT/Mods/NoJson/x.cpp"

CONF="$BUILD/dropin-configure.log"
cmake -S "$KIT/Native" -B "$KIT/build" -DNCMK_HOST_TEST=ON -DCMAKE_BUILD_TYPE="${BUILD_TYPE:-Debug}" >"$CONF" 2>&1
cmake --build "$KIT/build" -j >/dev/null
expect "$CONF" "NCMK mod  : Mods/Pulse -> libncmkmod_pulse.so (1 source file(s))"
expect "$CONF" "NCMK mod  : Mods/HelloWorld -> libncmkmod_helloworld.so"
expect "$CONF" "NCMK mod  : Mods/Off DISABLED (.disabled file) - not built"
expect "$CONF" "NCMK mod  : Mods/NoJson ignored (no mod.json)"
test -f "$KIT/build/out/libncmkmod_pulse.so" && echo "PASS  drop-in mod built" || { echo "FAIL  drop-in mod not built"; FAILED=1; }
test ! -e "$KIT/build/out/libncmkmod_off.so" && echo "PASS  disabled mod not built" || { echo "FAIL  disabled mod built"; FAILED=1; }
cp "$OUT/libmain.so" "$KIT/build/out/"
LOG8="$BUILD/host_game_dropin.log"
env NCMK_HOST_PACKAGE=com.mandegames.NightClubSimulater NCMK_MODS_ROOT="$KIT/NCMKMods" HOST_FRAME_TARGETS=ExecuteTasks \
  HOST_GAME_SECONDS=2 LD_LIBRARY_PATH="$KIT/build/out" timeout 60 "$KIT/build/out/host_game" >"$LOG8" 2>&1 || true
expect "$LOG8" "[NCMK] Mod loaded: Pulse 1.0.0"
expect "$LOG8" "[pulse] OnUpdate from a dropped-in folder"

# Removing the folder removes its output on the next configure (never embedded by accident).
rm -rf "$KIT/Mods/Pulse"
cmake "$KIT/build" >"$CONF" 2>&1
expect "$CONF" "removed stale libncmkmod_pulse.so"
test ! -e "$KIT/build/out/libncmkmod_pulse.so" && echo "PASS  stale drop-in output deleted" || { echo "FAIL  stale output kept"; FAILED=1; }

# Two folders with the same output name: configure fails with a clear message.
mkdir -p "$KIT/Mods/DupA" "$KIT/Mods/DupB"
for d in DupA DupB; do
  echo '{ "library": "libncmkmod_dup.so" }' >"$KIT/Mods/$d/mod.json"
  echo 'int f() { return 0; }' >"$KIT/Mods/$d/m.cpp"
done
if cmake "$KIT/build" >"$CONF" 2>&1; then echo "FAIL  duplicate output names accepted"; FAILED=1; fi
expect "$CONF" "Mods/DupA and Mods/DupB both build libncmkmod_dup.so"

TRACKED_AFTER="$(cd "$ROOT" && git status --porcelain 2>/dev/null || true)"
if [ "$TRACKED_BEFORE" = "$TRACKED_AFTER" ]; then echo "PASS  no repository file changed by the drop-in test"; else echo "FAIL  repository changed"; FAILED=1; fi

exit $FAILED
