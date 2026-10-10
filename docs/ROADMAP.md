# Roadmap / phase status

Legend: ✅ implemented and covered by automated tests · 🟡 implemented, needs on-device verification ·
⏳ planned

| # | Phase | Status | Where |
|---|---|---|---|
| 1 | WPF application | 🟡 | `Patcher/NightClubPatcher.Gui` (built by CI on Windows) |
| 2 | Detect ADB | ✅ | `Signing/ToolLocator`, `Adb/AdbClient` |
| 3 | Detect Quest (`device` / `unauthorized` / `offline`) | 🟡 | `AdbClient.DetectDevice` |
| 4 | Detect NightClub package | 🟡 | `pm list packages` |
| 5 | Find installed APK path (+ splits) | 🟡 | `pm path` |
| 6 | Pull original APK | 🟡 | `BackupManager.CreateFromDevice` |
| 7 | Validate original APK | ✅ | `ApkInspector`, `BackupManager.Verify` |
| 8 | Immutable backup | ✅ | read-only files, `original.sha256`, `backup.json`, never overwritten |
| 9 | Detect Unity / ARM64 / IL2CPP / XR | ✅ | `UnityDetector` (UnityFS header, libunity strings), `Il2CppDetector` (metadata magic), ELF machine |
| 10 | Extract libmain.so | ✅ | `PatchEngine.BuildUnsignedPatchedApk` |
| 11 | Build libncsloader.so | 🟡 | `Native/` (NDK build in CI; host build tested) |
| 12 | Patch libmain.so DT_NEEDED | ✅ | `Elf/ElfNeededPatcher` (tested on lld AArch64 fixture + glibc runtime) |
| 13 | Inject libncsloader.so | ✅ | stored + page aligned |
| 14 | Rebuild APK | ✅ | `Apk/ApkRewriter` (raw copy, no resource decoding) |
| 15 | zipalign | 🟡 | `ApkSigningService.Zipalign` |
| 16 | Sign APK | 🟡 | `apksigner` v1+v2+v3, permanent keystore (keystore generation tested) |
| 17 | Install patched APK | 🟡 | `InstallService` (signature-safe) |
| 18 | Launch game | 🟡 | `cmd package resolve-activity` + `am start` |
| 19 | Verify `[NCSLoader] Hello…` | ✅ host / 🟡 Quest | `Native/Tests/run_host_test.sh` |
| 20 | RESTORE ORIGINAL | 🟡 | `Recovery/RestoreService` |
| 21 | EMERGENCY RESTORE | 🟡 | works from `original.apk` alone (lookup tested) |
| 22 | UNPATCH | 🟡 | `PatchEngine.Unpatch` |
| 23 | Detect libil2cpp at runtime | ✅ host | `dlopen(RTLD_NOLOAD)` + `dl_iterate_phdr` |
| 24 | Resolve IL2CPP API | ✅ host | `Native/Il2Cpp/il2cpp_api.cpp` (dlsym only) |
| 25 | List assemblies | ✅ host | |
| 26 | NCS SDK | ✅ | `SDK/` |
| 27 | Plugin manager | ✅ host | `Native/Plugins/` (API version check, config disable, embedded + external) |
| 28 | HelloWorld mod | ✅ host | `Mods/HelloWorld` |
| 29 | Debug Overlay | ✅ host (log based) | `Mods/DebugOverlay` – in-headset rendering needs API 2 hooks |
| 30 | Diagnostics | 🟡 | `DiagnosticService`, OPEN LOGCAT |
| 31 | APK Explorer | 🟡 | Advanced → APK Explorer (extract / replace-as-new-signed-copy / export / hash / info) |
| 32 | IL2CPP Inspector | ⏳ | Cpp2IL offline step → `Cache/<ver>/{assemblies,classes,methods,fields,strings}.json` |
| 33 | SDK generation | ⏳ | `GeneratedSDK/{Assembly-CSharp.hpp, UnityEngine.hpp, Il2CppTypes.hpp, metadata.json}` from the cache |
| 34 | Asset override system | ⏳ | `NightClubMods/Overrides/…` (folders already created by the loader) |
| 35 | Update compatibility system | ✅ | `ProfileStore.Evaluate` + update warnings in Detect |

## Next steps, in order

1. **On-device first test.** Build with the NDK, `--patch-install --no-mods --launch`, confirm the
   game runs and logcat shows `[NCSLoader] Hello from NightClubModKit`. Then pin fingerprints and set
   `"verified": true` in `Versions/1.1.0-212.json`.
2. Tests 2–6 from the spec (libil2cpp found → API ready → assemblies → class found → HelloWorld).
   All of them are already logged by the loader; only confirmation on the headset is missing.
3. **API 2 – Hooks module:** main-thread dispatcher via a name-resolved Unity player-loop entry,
   unlocking `NCSMod_OnSceneLoaded/OnFixedUpdate/OnGUI` and scene-graph helpers
   (`FindGameObject`, `GetMainCamera`, `GetComponent`, …). Then an in-headset Debug Overlay.
4. Cpp2IL integration + IL2CPP cache + SDK generator (offsets only as a cache, never the only lookup).
5. Unity Object Explorer (read-only first) and the development console.
6. FULL PATCH (apktool, only when manifest/resources must change), asset overrides.

## Known limitations

- `/data/data/<pkg>` is not readable without root; it can be lost when Android requires uninstalling
  because of a signature change. External data (`/sdcard/Android/data`, `obb`) is preserved.
- Meta platform entitlement checks are tied to the account, not to the APK signature, but this has
  to be confirmed for this title on the first on-device test.
- ZIP64 APKs (> 4 GB) are not supported by the rewriter.
- API 1 `NCSMod_Update` runs on the loader thread (~10 Hz), not Unity's main thread.
