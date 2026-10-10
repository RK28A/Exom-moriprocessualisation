# Writing a NightClubModKit mod

A mod is an **ARM64 shared library** named `libncsmod_<name>.so` that talks to the game only
through the loader's host API (`SDK/Core/ncs_mod_api.h`, `NCS_LOADER_API = 1`).

## Minimal mod

```cpp
#include "SDK/Core/ncs_sdk.hpp"
#include "SDK/Il2Cpp/ncs_il2cpp.hpp"

static const NCSModInfo kInfo = {
    sizeof(NCSModInfo), "ncs.mymod", "My Mod", "me", "1.0.0", NCS_LOADER_API,
};

extern "C" NCS_EXPORT const NCSModInfo* NCSMod_GetInfo(void) { return &kInfo; }

extern "C" NCS_EXPORT int NCSMod_Load(const NCSHostApi* api) {
    if (!NCS::Init(api, kInfo.id)) return 1;          // refuses older loaders
    NCS_LOGI("Hello! Unity %s", api->unityVersion);

    // Name-based lookups: never hardcode offsets.
    auto* klass = NCS::IL2CPP::FindClass("Assembly-CSharp", "", "PlayerController");
    NCS_LOGI("PlayerController %s", klass ? "found" : "not found");
    return 0;                                          // non-zero = load failed, mod is unloaded
}

extern "C" NCS_EXPORT void NCSMod_Update(float dt) { /* ~10 Hz, loader thread */ }
extern "C" NCS_EXPORT void NCSMod_Unload(void) {}
```

Add it to `Native/CMakeLists.txt` with `ncs_add_mod(<target> ncsmod_<name> <sources>)` and
build with `Native/build.ps1`. The output lands in `Native/out/arm64-v8a/`.

## Shipping a mod

* **Embedded (recommended):** every `libncsmod_*.so` next to the loader is embedded into
  `lib/arm64-v8a/` of the patched APK (*Embed mods* option, on by default). This always works,
  even when Android forbids loading code from shared storage.
* **External:** copy the `.so` to `/sdcard/NightClubMods/Mods/` (or the fallback
  `/sdcard/Android/data/com.mandegames.NightClubSimulater/files/NightClubMods/Mods/`). The loader
  copies it into the app's private directory before `dlopen`, because shared storage is `noexec`.

Add a `mod.json` next to your sources for documentation / future tooling:

```json
{ "id": "ncs.mymod", "name": "My Mod", "author": "me", "version": "1.0.0", "requiredApi": 1 }
```

The authoritative metadata is `NCSModInfo` returned by `NCSMod_GetInfo`.

## Device information

```cpp
NCS::Game::GetDeviceName();      // "Meta Quest 3S" (loader >= 0.2.0, "unknown" before)
NCS::Game::GetDeviceCodename();  // hollywood / seacliff / eureka / panther
```

These fields were appended to `NCSHostApi` without changing the API number; raw C users must
check `NCS_HOST_HAS(api, deviceName)` first.

## Rules (API 1)

* `NCSMod_Load` and `NCSMod_Update` run on the loader worker thread, attached to the IL2CPP
  domain, **not** on Unity's main thread. Only call thread-safe engine APIs (e.g.
  `Application.unityVersion`, `Time.frameCount`). Scene graph access arrives with API 2.
* Managed exceptions raised through `invoke` are caught and reported, never propagated.
* A mod with `requiredApi` greater than the loader's API is skipped.
* Duplicate mod ids are skipped; `config.json` → `"mods": { "ncs.mymod": false }` disables a mod.
* Safe Mode (`NightClubMods/disable_mods` or `"safeMode": true`) loads no mods at all.

## Debugging

```
NightClubPatcher.exe --logcat          # or GUI: OPEN LOGCAT, tick "NCS only"
adb shell cat /sdcard/NightClubMods/Logs/latest.log
```

Every loader line is tagged `NCSLoader`; mod lines are prefixed with the mod id.
