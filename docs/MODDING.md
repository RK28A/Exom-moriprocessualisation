# Writing an NCMK mod

NCMK is a general-purpose modding API for NightClub Simulator (Meta Quest, Unity 6000.2.2f1,
IL2CPP, arm64-v8a). A mod is an **ARM64 shared library** named `libncmkmod_<name>.so`. It talks to
the game only through the loader's C ABI (`SDK/Core/ncmk_mod_api.h`, `NCMK_LOADER_API = 2`), usually
via the header-only C++ SDK in `SDK/`.

> **Status:** nothing in API 2 has run on a headset yet. Everything below is covered by the host
> test (fake IL2CPP) only and is marked *needs on-device verification* in
> [ROADMAP.md](ROADMAP.md). Expect names of game-side objects (camera rig, UI classes) to need
> adjustment once tested on the Quest.

## Contents

1. [Lifecycle and threads](#lifecycle-and-threads)
2. [Exports](#exports)
3. [Host API](#host-api)
4. [C++ SDK](#c-sdk)
5. [Complete example mod](#complete-example-mod)
6. [Building and shipping](#building-and-shipping)
7. [Compatibility](#compatibility)
8. [Rules](#rules)
9. [Debugging](#debugging)

## Lifecycle and threads

```
game start
  libmain.so ──DT_NEEDED──> libncmkloader.so        STAGE 01 constructor (starts the worker thread)
  worker: wait libunity / libil2cpp, resolve il2cpp_* by name, wait for il2cpp_init   STAGE 02-06
  worker: load mods  ── NCMKMod_Load(api)                                              STAGE 07-08
  worker: main-thread dispatcher (one per-frame managed method)                        STAGE 09
  Unity main thread, every frame:
      original per-frame method
      queued runOnMainThread() functions
      NCMKMod_OnSceneLoaded(name)        when the active scene changed (polled every 10 frames)
      NCMKMod_OnFixedUpdate() x N        N = physics steps Unity ran since the previous frame
      NCMKMod_OnUpdate(Time.deltaTime)   every mod
      NCMKMod_OnLateUpdate(deltaTime)    every mod, after all OnUpdate calls
  worker, ~10 Hz:  NCMKMod_OnBackgroundTick(dt)
unload (NCMK_Shutdown / library destructor): dispatcher pointers restored, NCMKMod_Unload
```

| Callback | Thread | Notes |
|---|---|---|
| `NCMKMod_Load` | loader worker | IL2CPP ready, attached to the domain. **No scene-graph calls.** |
| `NCMKMod_OnUpdate` / `OnLateUpdate` / `OnFixedUpdate` / `OnSceneLoaded` | **Unity main thread** | Only once a per-frame target ticks; never in Safe Mode. |
| `NCMKMod_OnBackgroundTick` | loader worker, ~10 Hz | Starts after STAGE 09. Not the main thread. |
| `NCMKMod_Unload` | caller of `NCMK_Shutdown` | Usually not called at all on Android (the process is killed). |

### How the main-thread dispatcher works

No inline hook and no code patching. The loader resolves a per-frame managed method **by name**
and swaps the first two function pointers of its IL2CPP `MethodInfo` (`methodPointer`, and
`virtualMethodPointer` when it equals the original) for an NCMK entry point that calls the original
first and then dispatches. Targets, tried in order, each given 5 s to tick:

1. `UnityEngine.CoreModule` `UnityEngine.UnitySynchronizationContext.ExecuteTasks` (static)
2. `UnityEngine.UI` `UnityEngine.EventSystems.EventSystem.Update` (instance)
3. `Oculus.VR` `OVRManager.Update` (instance)

Frames are deduplicated with `Time.frameCount`. That two-slot `MethodInfo` layout (Unity 2021.2+)
is the **only** IL2CPP memory-layout assumption in NCMK; slot 0 is checked to be non-null before
anything is written. The loader logs a single line:

```
[NCMK] STAGE 09 Main-thread dispatcher: hooked UnityEngine.UI UnityEngine.EventSystems.EventSystem.Update (instance)
[NCMK] STAGE 09 Main-thread dispatcher: no per-frame target worked (...) - main-thread callbacks disabled
```

If no target works, mods still load; only main-thread callbacks and `runOnMainThread` are
unavailable (`runOnMainThread` returns 0).

## Exports

```c
const NCMKModInfo* NCMKMod_GetInfo(void);                 // required
int   NCMKMod_Load(const NCMKHostApi* api);               // required, 0 = success
void  NCMKMod_Unload(void);                               // optional
void  NCMKMod_OnUpdate(float deltaTime);                  // optional, main thread  (API 2)
void  NCMKMod_OnLateUpdate(float deltaTime);              // optional, main thread  (API 2)
void  NCMKMod_OnFixedUpdate(void);                        // optional, main thread  (API 2)
void  NCMKMod_OnSceneLoaded(const char* sceneName);       // optional, main thread  (API 2)
void  NCMKMod_OnBackgroundTick(float deltaSeconds);       // optional, worker, ~10 Hz
```

`NCMKModInfo` = `{ structSize, id, name, author, version, requiredApi }`. Set `requiredApi` to
`NCMK_LOADER_API` (2). The `id` also names your config file.

## Host API

`NCMKHostApi` is append-only. Fields added after API 1 must be checked with
`NCMK_HOST_HAS(api, field)` (the C++ SDK does it for you: `NCMK::HasApi2()`).

**API 1**

| Member | Purpose |
|---|---|
| `apiVersion`, `loaderVersion`, `packageName`, `gameVersion`, `unityVersion`, `modsRoot`, `safeMode` | info |
| `deviceName`, `deviceCodename`, `deviceModel` | headset (Quest 2 / Pro / 3 / 3S) |
| `log(level, modId, msg)` | logcat tag `NCMK` + `NCMKMods/Logs/latest.log` |
| `isIl2CppReady`, `getLoadedModCount`, `getLoadedModName` | state |
| `il2cppResolve(name)` | raw `dlsym` on libil2cpp.so (all 241 `il2cpp_*` exports exist in this game) |
| `getAssemblyCount/Name`, `findImage`, `findClass`, `findMethod(klass, name, argc)`, `findField` | name-based lookup |
| `invoke(method, instance, args, &exception)` | `il2cpp_runtime_invoke`; exceptions are returned, never thrown |
| `newString`, `stringToUtf8`, `getField`/`setField`, `getStaticField`/`setStaticField`, `unbox`, `attachCurrentThread` | values |

**API 2**

| Member | Purpose |
|---|---|
| `isMainThread()` | 1 on Unity's main thread (known after the first dispatched frame) |
| `runOnMainThread(fn, user)` | queue `fn(user)` for the next frame, from any thread; 0 if no dispatcher |
| `findMethodBySignature(klass, name, paramTypeSuffixes, count)` | overload-safe lookup: `{"OVRInput.Button", "OVRInput.Controller"}`. Suffixes match on a `.` boundary, nested types accept `.`, `+` or `/`, parent classes are searched |
| `findNestedClass(outer, name)` | `OVRInput` → `Button` |
| `typeOf(klass)` | `System.Type` object (for `GetComponent(Type)`, `AddComponent(Type)`) |
| `newObject(klass)` | `il2cpp_object_new`; **the constructor is not run** — invoke `.ctor` yourself |
| `pin(obj)` / `unpin(handle)` | `il2cpp_gchandle_new(obj, pinned=true)`: keep an object alive across frames |
| `getEnumValue(enumClass, name, fallback)` | enum member by name, sign-extended from the enum's base type |

Argument passing for `invoke`: reference types are passed as the object pointer itself, value types
(int, float, bool, structs) as a pointer to the value. Return values of value types come back boxed.

## C++ SDK

All headers are under `SDK/`, header-only, and depend only on the host API. Every Unity / UI /
Input helper **must run on the main thread**; in debug builds (no `NDEBUG`) each helper checks it
and logs once per helper when it is not. Every failure (class or method missing in this game
build, managed exception) is logged **once** per call site and the helper returns an empty
handle / default value instead of crashing.

| Header | Namespace | Contents |
|---|---|---|
| `Core/ncmk_sdk.hpp` | `NCMK` | `Init`, `Log`/`NCMK_LOGI`, `FailOnce`, `IsMainThread`, `RunOnMainThread`, `HasApi2`, `Game::*` (version, device, loaded mods, mods root) |
| `Il2Cpp/ncmk_il2cpp.hpp` | `NCMK::IL2CPP` | `FindClass`, `FindMethod(k, name, argc)`, `FindMethod(k, name, {types...})`, `FindNestedClass`, `EnumValue`, `TypeOf`, `NewObject`, `Invoke`, `Call` (logs once), `Get/SetField`, `Unbox<T>`, `ToString`, `NewString`, `Export<Fn>`, `Pinned`, `ClassRef`, `StructCodec`, `NewByteArray` |
| `Unity/ncmk_unity.hpp` | `NCMK::Unity` | `Vec2`, `Vec3`, `Vec4`, `Quat` (Euler, LookRotation, `*`), `Color`, `Rect`; `Object` (IsAlive, Name, Destroy, DontDestroyOnLoad), `GameObject` (Create, Find, Destroy, SetActive, AddComponent, GetComponent, transform), `Component`, `Transform` (position, rotation, localScale/scale, localPosition/localRotation, parent/SetParent, TransformPoint, forward), `Camera::Main`, `Time::Delta/Unscaled/Now/FrameCount`, `Scene::CurrentName`, `DontDestroyOnLoad` |
| `Input/ncmk_input.hpp` | `NCMK::Input` | `IsDown`, `WasPressed`, `WasReleased` (`Button::Primary/Secondary/Thumbstick/Trigger/Grip/Menu`), `Trigger`, `Grip`, `Thumbstick`, `LocalPose`, `WorldPose` — on `OVRInput`, resolved by signature |
| `UI/ncmk_ui.hpp` | `NCMK::UI` | `Canvas::CreateWorldSpace`, `RectTransform` (SetSize, SetAnchors, SetPivot, SetAnchoredPosition, Stretch, PlaceTopLeft), `CreateElement`, `CreatePanel`, `CreateText`, `Image`, `Text`, `AddRectMask2D`, `AddCanvasGroup`, `CreateTexture` (RGBA32), `CreateSolidTexture`, `CreateSprite`, `BuiltinFont` |
| `Config/ncmk_config.hpp` | `NCMK` | `Config`: `<modsRoot>/Config/<modId>.cfg`, `key = value`, `GetBool/Int/Float/String` (record defaults), `Set`, `Save` (atomic) |

### Value types without layout assumptions

`Vec3`, `Quat`, `Color`… are plain C++ structs. To pass one to a managed method the SDK writes its
fields **by name** (`x`, `y`, `z`… / `m_XMin`… for `Rect`) into a pinned boxed instance with
`il2cpp_field_set_value`, and reads returned boxes with `il2cpp_field_get_value`. Field offsets
always come from IL2CPP itself. `Texture2D` data is uploaded with `LoadRawTextureData(IntPtr, int)`
(or `Marshal.Copy` into a `byte[]`), so no managed array layout is assumed either.

### Keeping objects across frames

Handles (`GameObject`, `Transform`, `Text`…) are raw pointers to managed objects. Pin what you keep:

```cpp
NCMK::IL2CPP::Pinned keep(go.Ptr());   // GC handle, released by keep.Reset() or its destructor
```

Release long-lived pins in `NCMKMod_Unload` rather than in static destructors.

### Calling game code

```cpp
using namespace NCMK;
static IL2CPP::ClassRef player{"Assembly-CSharp", "", "PlayerController"};   // resolved once
NCMKMethod* jump = NCMK_METHOD_SIG(player.Get(), "Jump", "System.Single");    // cached per call site
float force = 2.f;
void* args[] = {&force};
IL2CPP::Call("PlayerController.Jump", jump, playerInstance, args);           // logs once on failure
```

Find class / method names in the game's metadata (global-metadata.dat v31, not encrypted) with an
IL2CPP dumper on your own copy of the game. Never hardcode offsets or RVAs.

## Complete example mod

`libncmkmod_clickcounter.so`: a small panel floats above the right controller and counts presses
of the A button; it resets on scene change and reads two options from its config file. It uses
every SDK module (Config, Input, UI, Unity, main-thread queue). CI extracts this exact block,
compiles it (Debug and Release, `-Werror`) and loads it into the fake game
(`Native/Tests/check_doc_example.sh`). It has not run on a headset yet.

```cpp
// libncmkmod_clickcounter.so - counts presses of the right controller's A button on a small
// panel that floats above the right controller.
#include <cstdio>

#include "SDK/Config/ncmk_config.hpp"
#include "SDK/Core/ncmk_sdk.hpp"
#include "SDK/Input/ncmk_input.hpp"
#include "SDK/UI/ncmk_ui.hpp"
#include "SDK/Unity/ncmk_unity.hpp"

using namespace NCMK;

static const NCMKModInfo kInfo = {
    sizeof(NCMKModInfo), "example.clickcounter", "ClickCounter", "you", "1.0.0", NCMK_LOADER_API,
};

namespace {
UI::Canvas g_canvas;            // handles are plain pointers...
UI::Text g_label;
IL2CPP::Pinned g_keepCanvas;    // ...so pin what you keep across frames
IL2CPP::Pinned g_keepLabel;
bool g_uiFailed = false;
int g_clicks = 0;
float g_offset = 0.12f;         // meters above the controller (from the config file)
Unity::Color g_color{1, 1, 1, 1};

void LogSceneOnMainThread(void*) {
    // Queued from NCMKMod_Load (worker thread), runs on Unity's main thread on the next frame.
    NCMK_LOGI("ClickCounter running on the main thread, scene: %s", Unity::Scene::CurrentName().c_str());
}

void Refresh() {
    char text[64];
    snprintf(text, sizeof(text), "A pressed: %d", g_clicks);
    g_label.SetText(text);
}

bool BuildUi() {
    g_canvas = UI::Canvas::CreateWorldSpace("ClickCounter", {300, 90});   // 300 x 90 mm
    if (!g_canvas) return false;
    UI::CreatePanel(g_canvas.rect, "Background", {0, 0, 0, 0.6f});
    g_label = UI::CreateText(g_canvas.rect, "Label", "", 40, g_color, 10.f, UI::Anchor::MiddleCenter);
    if (!g_label) return false;
    g_keepCanvas = IL2CPP::Pinned(g_canvas.gameObject.Ptr());
    g_keepLabel = IL2CPP::Pinned(g_label.Ptr());
    Refresh();
    return true;
}
}  // namespace

extern "C" NCMK_EXPORT const NCMKModInfo* NCMKMod_GetInfo(void) { return &kInfo; }

extern "C" NCMK_EXPORT int NCMKMod_Load(const NCMKHostApi* api) {
    if (!Init(api, kInfo.id) || !HasApi2()) return 1;  // needs loader API 2

    Config cfg;                                         // NCMKMods/Config/example.clickcounter.cfg
    cfg.Load();
    g_offset = cfg.GetFloat("offsetMeters", g_offset);
    g_color.g = g_color.b = cfg.GetBool("redText", false) ? 0.2f : 1.f;
    if (cfg.Dirty()) cfg.Save();                        // first run: writes the defaults

    RunOnMainThread(LogSceneOnMainThread, nullptr);
    return 0;
}

extern "C" NCMK_EXPORT void NCMKMod_OnUpdate(float) {
    if (g_uiFailed) return;
    if (!g_canvas && !(g_uiFailed = !BuildUi())) NCMK_LOGI("ClickCounter panel ready");
    if (g_uiFailed) return;

    if (Input::WasPressed(Input::Hand::Right, Input::Button::Primary)) {
        ++g_clicks;
        Refresh();
    }
}

extern "C" NCMK_EXPORT void NCMKMod_OnLateUpdate(float) {
    if (!g_canvas) return;
    Input::Pose hand = Input::WorldPose(Input::Hand::Right);
    if (!hand.valid) return;
    Unity::Camera cam = Unity::Camera::Main();
    Unity::Vec3 pos = hand.position + Unity::Vec3{0, g_offset, 0};
    g_canvas.rect.SetPosition(pos);
    if (cam) g_canvas.rect.SetRotation(Unity::Quat::LookRotation(pos - cam.transform().position()));
}

extern "C" NCMK_EXPORT void NCMKMod_OnSceneLoaded(const char* scene) {
    NCMK_LOGI("Scene %s loaded, counter reset", scene);
    g_clicks = 0;
    if (g_label) Refresh();
}

extern "C" NCMK_EXPORT void NCMKMod_Unload(void) {
    g_keepLabel.Reset();
    g_keepCanvas.Reset();
}
```

Build it like the samples: put the file in `Mods/ClickCounter/` and run **Build Native Loader**
(or `Native/build.ps1`). Every `Mods/<Name>/` folder with `.cpp`/`.c` files is built automatically
as `libncmkmod_<name>.so`; a missing or empty `Mods/` folder only skips the mods, the loader is
always built.

## Building and shipping

* Mods are built with the same toolchain as the loader: NDK r27, `arm64-v8a`, `android-29`,
  `c++_static`, `-Wl,-z,max-page-size=16384`. NightClubPatcher.exe → **Build Native Loader**
  builds `Native/` (loader + sample mods) into `Native/out/arm64-v8a/`.
* **Embedded (recommended):** every `libncmkmod_*.so` next to the loader is embedded into
  `lib/arm64-v8a/` of the patched APK (*Embed mods* option, on by default).
* **External:** put the `.so` in the kit's `DeviceMods/Mods/` and click **Push mods + config**
  (Advanced) or run `NightClubPatcher.Cli.exe --push-mods`. Files go to the only folder both adb and
  the game can use — the game has **no storage permission**:

  ```
  /sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods/
    Mods/       external mods (copied to the app's private dir before dlopen: shared storage is noexec)
    Config/     config.json (loader) and <modId>.cfg (mods)
    Logs/       latest.log
    disable_mods   Safe Mode flag
  ```

  Uninstalling the game deletes this folder (back it up with **Backup Game Data**).

`mod.json` next to your sources documents the mod; the authoritative metadata is `NCMKModInfo`.

## Compatibility

* `NCMK_LOADER_API` is **2**. A loader refuses mods whose `requiredApi` is higher than its own.
* Mods built for **API 1** load unchanged. Their `NCMKMod_Update` export is called as the
  ~10 Hz background tick (that was its meaning in API 1).
* Mods from the **NightClubModKit** era (`libncsmod_*.so`, exports `NCSMod_GetInfo/Load/Update/...`)
  still load: the struct layouts are identical. `NCSMod_Update` is the background tick.
* Raw C users: check `NCMK_HOST_HAS(api, field)` before using anything appended after API 1.

## Rules

* Name-based resolution only: assembly / namespace / class / method / field / enum member names
  and parameter type names. No offsets, no RVAs, no signatures scanning.
* Do not block the main thread: heavy work goes to `OnBackgroundTick` or your own thread, results
  come back with `runOnMainThread`.
* Managed exceptions raised through `invoke` are caught and reported, never propagated.
* Duplicate mod ids are skipped; `config.json` → `"mods": { "my.mod.id": false }` disables a mod.
* Safe Mode (`NCMKMods/disable_mods` or `"safeMode": true`) loads no mods and never installs the
  dispatcher.
* The game uses a Meta entitlement check (`MetaAccount.OnUseEntitlement`) and a server-side ban
  system. **NCMK adds no bypass, spoofing or evasion for either, and mods built on it must not
  either.** If the re-signed APK fails the entitlement check, the patcher's diagnostics report it.
  The game's multiplayer (Photon Fusion / Voice) is off-limits for cheating features.

## Debugging

```
NightClubPatcher.Cli.exe --logcat          # or GUI: OPEN LOGCAT
adb shell cat /sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods/Logs/latest.log
```

Every loader line is tagged `NCMK` and prefixed `[NCMK]`; mod lines are prefixed with the mod id.
Look for the `STAGE 09` line first when main-thread callbacks do not fire.
