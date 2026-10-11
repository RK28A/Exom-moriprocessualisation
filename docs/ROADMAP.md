# Roadmap / phase status

Legend: ✅ implemented and covered by automated tests · 🟡 implemented, **needs on-device verification** ·
⏳ planned

> Nothing in NCMK has run on a headset yet. "✅ host" means proven against the fake IL2CPP / fake
> game on Linux only; every such item **also needs on-device verification** before it can be
> called done.

| # | Phase | Status | Where |
|---|---|---|---|
| 1 | WPF application | 🟡 | `Patcher/NightClubPatcher.Gui` (built by CI on Windows) |
| 2 | Detect ADB | ✅ | `Signing/ToolLocator`, `Adb/AdbClient` |
| 3 | Detect Quest (`device` / `unauthorized` / `offline`) | 🟡 | `AdbClient.DetectDevice` |
| 3b | Device choice + Quest 2 / Pro / 3 / 3S identification | ✅ parsing & checks / 🟡 device | `Adb/QuestDevices`, `ModKitService.SelectDevice`, loader `DetectDevice` |
| 4 | Detect NightClub package | 🟡 | `pm list packages` |
| 5 | Find installed APK path (+ splits) | 🟡 | `pm path` |
| 6 | Pull original APK | 🟡 | `BackupManager.CreateFromDevice` |
| 7 | Validate original APK | ✅ | `ApkInspector`, `BackupManager.Verify` |
| 8 | Immutable backup | ✅ | read-only files, `original.sha256`, `backup.json`, never overwritten |
| 9 | Detect Unity / ARM64 / IL2CPP / XR | ✅ | `UnityDetector` (UnityFS header, libunity strings), `Il2CppDetector` (metadata magic), ELF machine |
| 10 | Extract libmain.so | ✅ | `PatchEngine.BuildUnsignedPatchedApk` |
| 11 | Build libncmkloader.so | 🟡 | `Native/` (NDK build in CI; host build tested) |
| 12 | Patch libmain.so DT_NEEDED | ✅ | `Elf/ElfNeededPatcher` (tested on lld AArch64 fixture + glibc runtime) |
| 13 | Inject libncmkloader.so | ✅ | stored + page aligned |
| 14 | Rebuild APK | ✅ | `Apk/ApkRewriter` (raw copy, no resource decoding) |
| 15 | zipalign | 🟡 | `ApkSigningService.Zipalign` |
| 16 | Sign APK | 🟡 | `apksigner` v1+v2+v3, permanent keystore (keystore generation tested) |
| 17 | Install patched APK | 🟡 | `InstallService` (signature-safe) |
| 18 | Launch game | 🟡 | `cmd package resolve-activity` + `am start` |
| 19 | Verify `[NCMK] Hello…` | ✅ host / 🟡 Quest | `Native/Tests/run_host_test.sh` |
| 20 | RESTORE ORIGINAL | 🟡 | `Recovery/RestoreService` |
| 21 | EMERGENCY RESTORE | 🟡 | works from `original.apk` alone (lookup tested) |
| 22 | UNPATCH | 🟡 | `PatchEngine.Unpatch` |
| 23 | Detect libil2cpp at runtime | ✅ host | `dlopen(RTLD_NOLOAD)` + `dl_iterate_phdr` |
| 24 | Resolve IL2CPP API | ✅ host | `Native/Il2Cpp/il2cpp_api.cpp` (dlsym only) |
| 25 | List assemblies | ✅ host | |
| 26 | NCMK SDK (C ABI + header-only C++) | ✅ host / 🟡 | `SDK/` |
| 27 | Plugin manager | ✅ host / 🟡 | `Native/Plugins/` (API version check, config disable, embedded + external, API 1 + legacy `NCSMod_*` mods) |
| 28 | HelloWorld mod | ✅ host / 🟡 | `Mods/HelloWorld` |
| 29 | Debug Overlay (in-headset NCMK::UI panel, log fallback) | ✅ host (log fallback only) / 🟡 panel | `Mods/DebugOverlay` |
| 30 | Diagnostics | 🟡 | `DiagnosticService`, OPEN LOGCAT |
| 31 | APK Explorer | 🟡 | Advanced → APK Explorer (extract / replace-as-new-signed-copy / export / hash / info) |
| 32 | IL2CPP Inspector | ⏳ | Cpp2IL offline step → `Cache/<ver>/{assemblies,classes,methods,fields,strings}.json` |
| 33 | SDK generation | ⏳ | `GeneratedSDK/{Assembly-CSharp.hpp, UnityEngine.hpp, Il2CppTypes.hpp, metadata.json}` from the cache |
| 34 | Asset override system | ⏳ | `NCMKMods/Overrides/…` (folders already created by the loader) |
| 35 | Update compatibility system | ✅ | `ProfileStore.Evaluate` + update warnings in Detect (+ metadata version 31) |

## NCMK API 2 (all items: needs on-device verification)

| # | Item | Status | Where |
|---|---|---|---|
| 36 | Rename to NCMK (libncmkloader.so, libncmkmod_*.so, tag NCMK, NCMKMods, NCMK.keystore, assets/NCMK marker) | ✅ / 🟡 | everywhere |
| 37 | Detect + cleanly unpatch APKs patched with the old names (libncsloader.so, libncsmod_*.so) | ✅ / 🟡 | `GameConstants.Legacy*`, `PatchEngine.BuildUnsignedUnpatchedApk` |
| 38 | Keystore migration NightClubModKit.keystore → NCMK.keystore (same key, never regenerated) | ✅ / 🟡 | `KeystoreManager.MigrateLegacyKeystore` |
| 39 | Main-thread dispatcher (MethodInfo slot 0/1 swap, 3 targets, 5 s fallback, frameCount dedupe, restore on unload, never in Safe Mode) | ✅ host / 🟡 | `Native/Hooks/main_thread.cpp` |
| 39a | — target 1 `UnitySynchronizationContext.ExecuteTasks` actually ticks on Unity 6000.2.2f1 | 🟡 | first thing to check on the Quest (STAGE 09 line) |
| 39b | — Unity calls per-frame methods through `MethodInfo.methodPointer` (not a cached copy) | 🟡 | if not, STAGE 09 reports "no per-frame target worked" |
| 40 | `NCMKMod_OnUpdate/OnLateUpdate/OnFixedUpdate/OnSceneLoaded`, `OnBackgroundTick` | ✅ host / 🟡 | `Native/Plugins/plugin_manager.cpp` |
| 41 | Host API 2: isMainThread, runOnMainThread, findMethodBySignature, findNestedClass, typeOf, newObject, pin/unpin, getEnumValue | ✅ host / 🟡 | `Native/Loader/loader.cpp`, `Native/Il2Cpp/il2cpp_api.cpp` |
| 42 | API 1 and NightClubModKit-era mods still load | ✅ host / 🟡 | `Native/Tests/legacy_mod.c` |
| 43 | `NCMK::Unity` (GameObject, Transform, Camera, Time, Scene, value types) | 🟡 (compiles; fake has no scene graph) | `SDK/Unity/ncmk_unity.hpp` |
| 44 | `NCMK::Input` on OVRInput (+ world pose via OVRCameraRig / Camera.main parent; HurricaneVR rig unknown) | 🟡 | `SDK/Input/ncmk_input.hpp` |
| 45 | `NCMK::UI` (world-space Canvas, RectTransform, Image, Text, RectMask2D, CanvasGroup, Texture2D/Sprite, built-in font) | 🟡 | `SDK/UI/ncmk_ui.hpp` |
| 46 | `NCMK::Config` (`<modsRoot>/Config/<modId>.cfg`) | ✅ host / 🟡 | `SDK/Config/ncmk_config.hpp` |
| 47 | Mods root `/sdcard/Android/data/<pkg>/files/NCMKMods` (no storage permission) + patcher push of mods/config | ✅ unit / 🟡 | loader `ResolveModsRoot`, `Mods/DeviceModPusher`, `--push-mods` |
| 48 | Entitlement failure reported by diagnostics (no bypass) | ✅ unit / 🟡 | `LogcatFilter.EntitlementFailures` |
| 49 | Re-signed APK passes the game's Meta entitlement check | 🟡 unknown | if it fails, diagnostics say so; NCMK will not work around it |
| 50 | Drop-in mods: one `Mods/<Name>/` folder (mod.json + .cpp), auto-discovered by CMake, `.disabled`, duplicate-name check, stale outputs removed | ✅ host / 🟡 NDK on Windows | `Native/CMakeLists.txt`, `Mods/ModCatalog.cs`, host test scenario 8 |
| 51 | Build Native Loader fails early and readably when `Mods\` or `SDK\` is missing from a Release folder | ✅ unit / 🟡 Windows | `NativeBuilder.MissingSourcesMessage`, `Native/build.ps1` |
| 52 | GUI lists the mods that will be embedded before patching | 🟡 (WPF only built on CI) | `MainWindow.ConfirmEmbeddedMods`, `PatchEngine.PlannedMods` |
| 53 | GC handles are pointer-sized (`uintptr_t`) end to end — fixes the crash on the first unpin with the game's libil2cpp | ✅ host (fake hands out handles > 4 GB) / 🟡 | `il2cpp_api`, `NCMKHostApi::pin/unpin`, `IL2CPP::Pinned` |
| 54 | OVRInput pumped by the loader when there is no OVRManager instance | ✅ host / 🟡 | `Native/Unity/input_pump.cpp` |
| 55 | `NCMK::Input` fallback through `UnityEngine.XR.InputDevices` when OVRInput is missing or silent | ✅ host / 🟡 (HurricaneVR rig) | `SDK/Input/ncmk_input.hpp` |
| 56 | UI: `Image::SetSliced`, 9-slice `CreateSprite`, `Text::SetBold/SetOverflow`, `SetRaycastTarget`, `CreateRoundedSprite`, `CreateGlowSprite` | 🟡 (compiles; fake has no uGUI) | `SDK/UI/ncmk_ui.hpp` |
| 57 | Tool check reuses tools from `%LOCALAPPDATA%\NCMK\Tools` and from other NCMK copies next to the kit; new tools go to the shared folder (no re-download after unzipping a new kit) | ✅ unit / 🟡 Windows (needs on-device verification) | `ToolLocator.ToolRoots`, `InstallRoot` |
| 58 | No game data backup/restore: a signature-mismatch reinstall asks once, uninstalls and installs; the game starts with fresh data | ✅ unit / 🟡 needs on-device verification | `InstallService` |
| 60 | "Pin fingerprints and continue" prompt when a patch is blocked by unpinned fingerprints | 🟡 needs on-device verification | `ModKitService.PatchSelected` |
| 61 | GUI: custom dark title bar with the NCMK logo (all windows and dialogs), app icon, dark ScrollBar / ComboBox / CheckBox / TabControl / TreeView / ToolTip styles | 🟡 (WPF only built on CI) | `App.xaml`, `Assets/ncmk.ico` |

## Next steps, in order

1. **On-device first test.** Build with the NDK, `--patch-install --no-mods --launch`, confirm the
   game runs and logcat shows `[NCMK] Hello from NCMK`. Check **Diagnose → Meta entitlement**. Then
   pin fingerprints and set `"verified": true` in `Versions/1.1.0-212.json`.
2. With mods: confirm the `STAGE 09` line (which per-frame target hooked) and that the
   DebugOverlay panel is visible and follows the head. If no target works, collect the log and
   the game's per-frame managed methods before adding a target.
3. Verify `NCMK::Input` against the game's HurricaneVR rig (tracking space for `WorldPose`).
4. Cpp2IL integration + IL2CPP cache + SDK generator (offsets only as a cache, never the only lookup).
5. Unity Object Explorer (read-only first) and the development console.
6. FULL PATCH (apktool, only when manifest/resources must change), asset overrides.

## Known limitations

- `/data/data/<pkg>` is not readable without root; game data is not backed up: an uninstall forced by a
  signature change resets the game's settings. NCMKMods lives in external data, so uninstalling deletes it.
- The game checks Meta entitlement (`MetaAccount.OnUseEntitlement`) and has a server-side ban
  system. NCMK contains no bypass, spoofing or evasion for either; whether a re-signed APK passes
  the entitlement check is unknown until tested, and diagnostics report a failure.
- ZIP64 APKs (> 4 GB) are not supported by the rewriter.
- `OnFixedUpdate` runs after the frame's Update with the number of physics steps Unity ran, not
  interleaved with physics. `OnSceneLoaded` is polled every 10 frames.
- `NCMKMod_Load` and `OnBackgroundTick` run on the loader worker thread, not Unity's main thread.
