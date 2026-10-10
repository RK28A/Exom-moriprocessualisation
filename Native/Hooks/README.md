# Hooks - main-thread dispatcher

`main_thread.cpp` runs NCMK code on Unity's main thread once per frame. It powers the API 2
callbacks (`NCMKMod_OnUpdate`, `OnLateUpdate`, `OnFixedUpdate`, `OnSceneLoaded`) and
`NCMKHostApi::runOnMainThread`.

**No inline hook, no code patching.** A per-frame managed method is resolved by name through the
exported IL2CPP API, and the two function-pointer slots at the start of its `MethodInfo` are
swapped for an NCMK entry point:

| slot | field                  | swapped when                         |
|------|------------------------|--------------------------------------|
| 0    | `methodPointer`        | always (must be non-null first)      |
| 1    | `virtualMethodPointer` | only when it equals the original slot 0 |

This is the only IL2CPP memory-layout assumption in NCMK (valid for Unity 2021.2+, including the
game's Unity 6000.2.2f1 / metadata v31).

The entry calls the original method first, then dispatches. Frames are deduplicated with
`Time.frameCount` (several `EventSystem` instances do not cause several dispatches).

Targets, tried in order (each gets 5 s to produce a tick before the next one is also installed;
the first target that ticks wins and every other slot is restored):

1. `UnityEngine.CoreModule` `UnityEngine.UnitySynchronizationContext.ExecuteTasks` (static)
2. `UnityEngine.UI` `UnityEngine.EventSystems.EventSystem.Update` (instance)
3. `Oculus.VR` `OVRManager.Update` (instance)

The loader logs one line, `STAGE 09 Main-thread dispatcher: hooked <target>` or
`... no per-frame target worked`. It is never installed in Safe Mode, and the pointers are restored
on unload (`NCMK_Shutdown()` / library destructor).

Status: **needs on-device verification**. Only the host test (fake IL2CPP) has exercised it.
