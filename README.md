# NCMK — NightClub Simulator Mod Kit

Windows patcher and **general-purpose native modding API** for **NightClub Simulator** on Meta Quest.
Any mod can be built on it: a stable C ABI (`NCMK_LOADER_API = 2`) with main-thread callbacks, and a
header-only C++ SDK for Unity objects, Quest controllers, in-headset UI and per-mod config files
([docs/MODDING.md](docs/MODDING.md)).

> **Not yet tested on a headset.** The patcher, loader, main-thread dispatcher and SDK are covered by
> unit tests and a host end-to-end test against a fake IL2CPP; every on-device item is marked
> *needs on-device verification* in [docs/ROADMAP.md](docs/ROADMAP.md).

| | |
|---|---|
| Package | `com.mandegames.NightClubSimulater` (the only package the toolkit will touch) |
| Supported build | 1.1.0 (versionCode 212) |
| Engine | Unity 6000.2.2f1, IL2CPP, arm64-v8a |
| Headsets | Meta Quest 2, Quest Pro, **Quest 3**, **Quest 3S** (Horizon OS, Oculus XR / Meta XR) |

> **Main design rule:** the original game backup must always stay recoverable. Every operation
> leaves you with `original.apk`, `patched-current.apk`, `patched-previous.apk`, logs, and a
> working **RESTORE ORIGINAL** / **EMERGENCY RESTORE** path.

---

## What it does

```
Quest ──adb pull──► Backups/…/original.apk   (immutable, read-only, SHA-256 + backup.json)
                         │  copy (never edited)
                         ▼
        %TEMP%\NCMK\<ts>\{input,unpacked,native,patched,output}
                         │  FAST PATCH
                         │   • libmain.so  +DT_NEEDED libncmkloader.so   (ELF patch, no code change)
                         │   • + lib/arm64-v8a/libncmkloader.so          (stored, page-aligned)
                         │   • + lib/arm64-v8a/libncmkmod_*.so           (optional embedded mods)
                         │   • + assets/NCMK/patch.json      (marker)
                         │   • old signature stripped
                         ▼
                 zipalign ─► apksigner (permanent NCMK key)
                         ▼
     Builds/1.1.0-212/patched-current.apk  (previous one rotated to patched-previous.apk)
                         ▼
                adb install (signature-safe) ─► launch ─► logcat:
                         [NCMK] Hello from NCMK
```

On the device the loader runs **before Unity**: it logs, starts a worker thread, waits for
`libunity.so` then `libil2cpp.so`, resolves the IL2CPP API with `dlsym`, waits for the runtime,
enumerates assemblies and loads mods. **No inline hooks, no game code changes.** To call mods on
Unity's main thread, the loader swaps the two function-pointer slots of one per-frame managed
method's `MethodInfo` (resolved by name; restored on unload; never in Safe Mode) — see
[Native/Hooks/README.md](Native/Hooks/README.md).

---

## Supported headsets & device choice

| Headset | Codename | Status |
|---|---|---|
| Meta Quest 2 | `hollywood` | supported |
| Meta Quest Pro | `seacliff` | supported |
| Meta Quest 3 | `eureka` | supported |
| Meta Quest 3S (incl. Xbox Edition) | `panther` | supported |
| Meta Quest (1st gen) | `monterey` | refused (not supported by Unity 6 / current Meta XR) |
| unknown future Quest | — | allowed with a warning (same arm64 build) |

All supported headsets use the **same patched APK** (arm64-v8a). The headset matters for:

* **Which device is used.** Before **PATCH GAME** / **PATCH + INSTALL** the patcher lists every
  adb device (model, serial, USB/Wi-Fi, authorization state, compatibility) and asks which one to
  use; the original APK is backed up from it and the patched game installed on it. The status panel
  also has a headset selector. CLI: `--list-devices`, `--device <serial>`.
  *PATCH GAME* can also run with no headset, building from an existing backup.
* **Compatibility check.** The headset is matched against the game's
  `com.oculus.supportedDevices` manifest entry. If the developer does not list your headset you get
  a warning and must confirm; sideloading usually still works.
* **Storage.** The game's manifest has no storage permission, so mods, configs and logs live in its
  own external files folder,
  `/sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods`, on every headset. The
  patcher pushes mods and config there (**Push mods + config**, `--push-mods`).
* The loader logs the headset (`[NCMK] Device: Meta Quest 3S (Quest 3S / panther), Android 14 (SDK 34)`)
  and exposes it to mods (`NCMK::Game::GetDeviceName()`), and `backup.json` / `build.json` record the
  source and target headset.

---

## Repository layout

```
NCMK.sln
Patcher/
  NightClubPatcher.Core/    all logic (shared by GUI + CLI)
    Adb/  Apk/  Android/  Elf/  Signing/  Backup/  Recovery/  Unity/  Il2Cpp/
    Diagnostics/  Versions/  Patching/  Builds/  Infrastructure/
  NightClubPatcher.Gui/     WPF app - MAIN APPLICATION -> NightClubPatcher.exe
  NightClubPatcher.Cli/     command line (advanced)    -> NightClubPatcher.Cli.exe
  NightClubPatcher.Tests/   xUnit tests (+ real AArch64 ELF fixtures)
Native/                     libncmkloader.so (C++17, Android NDK)
  Loader/ Il2Cpp/ Unity/ Logging/ Plugins/ Hooks/ Tests/
SDK/                        headers for mod authors
  Core/ncmk_mod_api.h        stable C ABI (NCMK_LOADER_API = 2)
  Core/ncmk_sdk.hpp  Il2Cpp/ncmk_il2cpp.hpp  Unity/ncmk_unity.hpp
  Input/ncmk_input.hpp  UI/ncmk_ui.hpp  Config/ncmk_config.hpp
Mods/<Name>/                one folder per mod (mod.json + .cpp) - HelloWorld, DebugOverlay
DeviceMods/                 Mods/ + Config/ pushed to the headset's NCMKMods folder
Tools/  Versions/  Backups/  Builds/  Cache/  Logs/  Keys/
```

The patcher sub-areas listed in the spec (`Apk/`, `Elf/`, `Adb/`, …) live as folders/namespaces
inside `NightClubPatcher.Core` so the GUI and CLI share one implementation.

---

## Quick start on Windows (no build, nothing to install)

Works on a clean Windows 10/11 PC: no Android Studio, Visual Studio, CMake, NDK, Java or PATH
changes needed.

1. Download the **NightClubPatcher-Release-win-x64** artifact from the latest successful *build*
   run in GitHub Actions (it is the `Release\` folder) and unzip it.
2. Double-click **`NightClubPatcher.exe`** (self-contained, no .NET install needed).
3. The **SETUP** card checks every tool; on first start the app offers to install what is
   missing (one confirmation): **Install / Repair Tools**.
4. Click **Build Native Loader** (skip it if the card already shows *Native Loader: Ready*, e.g.
   when the Release folder contains a prebuilt loader).
5. Connect the Quest by USB (developer mode on), accept *Allow USB debugging* in the headset,
   then press **Patch + Install** (enabled once the loader exists).

## Portable tools (Install / Repair Tools)

Tools are downloaded once per PC into `%LOCALAPPDATA%\NCMK\Tools`, never system-wide, and the user's
`PATH` is never modified (tools are prepended to the PATH of the build process only). Before
downloading anything NCMK checks the kit's own `Tools\`, that shared folder, the `Tools\` folders of
other NCMK copies next to the kit and the Android SDK, so unzipping a new kit does not reinstall
anything (`--bundled-only` keeps everything in the kit's `Tools\`). Layout of a tools folder:

```
Release\
├── Tools\
│   ├── adb\          Android SDK platform-tools          dl.google.com, SHA-1 from Google's SDK repository manifest
│   ├── zipalign\     } latest stable Android             dl.google.com, SHA-1 from Google's SDK repository manifest
│   ├── apksigner\    } SDK build-tools
│   ├── jdk\          Microsoft Build of OpenJDK 17       aka.ms, SHA-256 published by Microsoft
│   ├── ndk\          Android NDK r27 (LTS)               dl.google.com, SHA-1 from Google's SDK repository manifest
│   ├── cmake\        CMake 3.31.7 (Kitware)              GitHub release, SHA-256 from the release's SHA-256.txt
│   ├── ninja\        Ninja 1.12.1                        GitHub release, pinned SHA-256 of the official asset
│   └── tools.json    installed versions + URL + SHA-256 of every archive
├── Native\           loader sources + build.ps1; out\arm64-v8a\libncmkloader.so
└── ...
```

* Google publishes SHA-1 (not SHA-256) for SDK/NDK packages; the app verifies that official value
  and records the archive's SHA-256 in `tools.json`.
* Each archive is downloaded to `Cache\downloads`, verified, extracted to `Tools\.staging` and only
  then swapped into place: **a failed download never breaks an installed tool**. Failed tools are
  listed and the button becomes **Retry Install / Repair**.
* A tool that is present and runs is **never downloaded again**; a broken one is reinstalled.
* Detection order (also used by `Native\build.ps1`):
  * NDK: `-Ndk` argument → `Tools\ndk` → `ANDROID_NDK_HOME` → `ANDROID_NDK_ROOT` → Android SDK `ndk\<latest>`
  * CMake: `Tools\cmake\bin\cmake.exe` → `PATH` → Android SDK `cmake\<latest>\bin`
  * Ninja: `Tools\ninja\ninja.exe` → `PATH` → Android SDK `cmake\<latest>\bin`
* Sizes: NDK ≈ 700 MB download / 2.5 GB on disk, JDK ≈ 190 MB, build-tools ≈ 60 MB, CMake ≈ 45 MB.

CLI equivalents:

```powershell
.\NightClubPatcher.Cli.exe --check-tools                       # ADB / Build Tools / JDK 17 / NDK / CMake / Ninja / Native Loader
.\NightClubPatcher.Cli.exe --install-tools [--accept-licenses]  # install / repair (add --no-native to skip NDK/CMake/Ninja)
.\NightClubPatcher.Cli.exe --build-native                      # -> Native\out\arm64-v8a\libncmkloader.so
.\NightClubPatcher.Cli.exe --bundled-only ...                  # ignore Android SDK / JAVA_HOME / PATH tools
```

CI runs this exact flow on a Windows runner with every preinstalled SDK ignored
(`NCMK_BUNDLED_TOOLS_ONLY=1`): install all tools, verify a second run downloads nothing, build the
loader, check that everything is *Ready*, then smoke-test the GUI.

## Requirements to build from source (Windows)

1. **Visual Studio 2022 / 2026** with the *.NET desktop development* workload, or the
   **.NET 8 SDK** alone (`dotnet --list-sdks` must show 8.0.x or newer).
2. Nothing else: adb, build-tools, JDK, NDK, CMake and Ninja are installed by the app
   (see *Portable tools*). An existing Android SDK / JDK / CMake on `PATH` is used if present.
3. Quest in **developer mode**, USB debugging authorized.

## Build & run on Windows

### Visual Studio (F5)

1. Open **`NCMK.sln`**. Solution Explorer shows the *Patcher* folder with
   `NightClubPatcher.Gui` (startup project, listed first), `NightClubPatcher.Core`,
   `NightClubPatcher.Cli` and `NightClubPatcher.Tests`.
2. Press **F5**: the WPF window opens. (If Visual Studio remembered an older startup project from a
   previous `.vs` folder: right-click `NightClubPatcher.Gui` → *Set as Startup Project*, or delete
   the hidden `.vs` folder.)
3. When running from Visual Studio the kit folder is the repository root (`Backups\`, `Builds\`,
   `Logs\`, `Keys\`, `Tools\`, `Versions\` next to the `.sln`).

### Command line

```powershell
# optional: native loader + sample mods -> Native\out\arm64-v8a\*.so
# (uses Tools\ndk, Tools\cmake, Tools\ninja when installed, else ANDROID_NDK_HOME / PATH / Android SDK)
.\Native\build.ps1

# build everything, run tests, publish, assemble Release\
.\Build-Windows.ps1                 # add -BuildNative to also run Native\build.ps1,
                                    #     -SmokeTest to launch the GUI/CLI once after publishing

# run
.\Release\NightClubPatcher.exe      # graphical patcher (main application)
.\Release\NightClubPatcher.Cli.exe --help
```

`Build-Windows.ps1` builds Core, CLI and GUI, runs the tests, publishes the GUI and the CLI as
**self-contained single-file** `win-x64` executables, and assembles:

```
Release\
├── NightClubPatcher.exe        WPF GUI - main application (PE subsystem: Windows GUI, no console)
├── NightClubPatcher.Cli.exe    command line, advanced usage
├── libncmkloader.so             native loader (when built)
├── libncmkmod_*.so              sample mods (when built; embedded into the APK by the patcher)
├── Versions\                   game version profiles
├── Tools\                      adb / zipalign / apksigner / jdk (auto-installed on first start)
├── Logs\
└── NCMK.root        marks this folder as the kit root
```

The script **never deletes** an existing `Release\` folder: once used, it holds `Backups\` (your
original APK) and `Keys\` (your signing key). It only replaces the executables, adds missing
profiles/tools and keeps version profiles you already pinned.

Plain `dotnet` equivalents:

```powershell
dotnet build NCMK.sln -c Release
dotnet test  Patcher\NightClubPatcher.Tests -c Release
dotnet run   --project Patcher\NightClubPatcher.Gui          # GUI
dotnet run   --project Patcher\NightClubPatcher.Cli -- --help
dotnet publish Patcher\NightClubPatcher.Gui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Release
```

### Architecture

```
NightClubPatcher.Gui  (WPF, NightClubPatcher.exe)      ─┐
NightClubPatcher.Cli  (console, NightClubPatcher.Cli.exe) ─┼─►  NightClubPatcher.Core
NightClubPatcher.Tests                                  ─┘     (ADB, APK, ELF, backup, signing,
                                                                 install, restore, diagnostics)
```

The GUI and the CLI contain no patching logic; they only call Core services. Long operations
(adb pull, patching, signing, installing, restoring, logcat) run on background tasks, so the
window never freezes; errors open a dialog with the technical details (and are written to
`Logs\`), never a crash.

CI (`.github/workflows/build.yml`) builds the loader with the NDK, runs the unit tests and the host
end-to-end test, then runs `Build-Windows.ps1 -SmokeTest` on Windows (verifying that
`NightClubPatcher.exe` is a GUI executable that opens its WPF window and that the CLI works) and
uploads the `Release\` folder as an artifact.

## The app

* **Header** – live **ADB indicator**: green = adb running and a headset connected, amber = adb
  OK but no headset / headset unauthorized (accept *Allow USB debugging* in the headset) / several
  headsets, red = adb missing or adb server broken. Hover for adb path and version.
  **Restart ADB** fixes most *offline* headsets; **Install / Repair Tools** appears when something is missing.
* **Headset is watched continuously** (`adb devices` every 3 s): plugging/unplugging a Quest or
  accepting the USB-debugging prompt updates the status by itself.
* **SETUP card** – ADB, Build Tools, JDK 17, Android NDK, CMake, Ninja and Native Loader with their
  versions, plus **Install / Repair Tools** and **Build Native Loader** (Patch buttons unlock once the
  loader is built).
* **Cards** – Headset (model, serial, Android version, compatibility), NightClub Simulator
  (installed / version / original or modded / loader), Engine (Unity, ARM64, IL2CPP, XR, build
  verification), Backup & builds, Warnings.
* **Activity panel** – every operation (patch, install, restore…) shows its **steps** with
  ✓ / ✗ and a timer, and the full **colored log** live in the app (filter: all / steps / warnings),
  with Copy, Save and Clear. Everything is also written to `Logs/patcher-YYYYMMDD.log`.

CLI equivalents: `--check-adb` (binary, version, server, devices), `--restart-adb`.

## First run (the "absolute first test")

1. Install NightClub Simulator 1.1.0 from the Meta store, connect the Quest.
2. `NightClubPatcher.Gui.exe` → it detects the Quest and the game.
3. **PATCH + INSTALL**. On first use it pulls the installed APK and stores it as the immutable
   original backup (`Backups/com.mandegames.NightClubSimulater/1.1.0-212/`).
   - The shipped profile `Versions/1.1.0-212.json` has no file fingerprints yet, so the build is
     reported as *"fingerprints not pinned"*. After checking that your backup is the untouched
     store build, run **Advanced → Pin fingerprints** (`--pin-fingerprints`) once — or tick
     *Allow unverified build* for a one-off patch.
4. The first install of a modded build **requires uninstalling the store build** (different
   signature). The patcher asks first, backs up `/sdcard/Android/data/<pkg>` and
   `/sdcard/Android/obb/<pkg>`, reinstalls, and restores them.
5. **OPEN LOGCAT** and look for:

```
[NCMK] Hello from NCMK
[NCMK] STAGE 02 Worker thread
[NCMK] libil2cpp.so found
[NCMK] IL2CPP API ready
[NCMK]   Assembly-CSharp (… classes)
[NCMK] Class found: UnityEngine.Application
[NCMK] Mod loaded: HelloWorld 1.0.0
[NCMK] NCMK started successfully
```

To run *only* the first milestone (loader, no mods), patch with `--no-mods` or untick
*Embed libncmkmod_*.so* in Advanced.

## CLI (advanced)

Everything the GUI does is also available from `NightClubPatcher.Cli.exe`:

```
NightClubPatcher.Cli.exe --detect              detect Quest / game / version / state
NightClubPatcher.Cli.exe --backup              create the immutable original backup
NightClubPatcher.Cli.exe --patch               build patched-current.apk
NightClubPatcher.Cli.exe --install             install patched-current.apk
NightClubPatcher.Cli.exe --patch-install --launch
NightClubPatcher.Cli.exe --restore             RESTORE ORIGINAL
NightClubPatcher.Cli.exe --emergency-restore [original.apk]
NightClubPatcher.Cli.exe --unpatch             remove ModKit changes (keeps ModKit signature, no uninstall)
NightClubPatcher.Cli.exe --diagnose            all checks -> Logs/diagnostic.txt
NightClubPatcher.Cli.exe --logcat              filtered logcat
NightClubPatcher.Cli.exe --safe-mode on|off
NightClubPatcher.Cli.exe --install-previous    crash protection: go back one modded build
NightClubPatcher.Cli.exe --pin-fingerprints | --analyze <apk> | --import-backup <apk> | --tools
NightClubPatcher.Cli.exe --list-devices        headsets connected (model, serial, compatibility)
NightClubPatcher.Cli.exe --device <serial> --patch-install   use that headset, no prompt
```

Options: `--allow-unverified`, `--no-mods`, `--loader <path>`, `--mods keep|disable|delete`,
`--non-interactive` (never confirms anything destructive), `--verbose`.

---

## Safety & recovery model

| Rule | Implementation |
|---|---|
| Only NightClub Simulator | package checked on every APK (`GameConstants.IsTargetPackage`); the loader also verifies `/proc/self/cmdline` and stays idle elsewhere |
| Original backup never overwritten | `BackupManager` refuses to write if `original.apk` exists; files marked read-only; `original.sha256` + `backup.json` |
| Modified game never backed up as original | `libncmkloader.so`, `DT_NEEDED` in libmain, patch marker, embedded mods, ModKit signer, unsigned APK, debuggable manifest → *"Installed APK appears modified. Original backup will not be replaced."* |
| Never edit the backup | all work happens on a copy in `%TEMP%\NCMK\…` |
| Unknown update ⇒ no auto patch | version profile + SHA-256 of libmain/libunity/libil2cpp/global-metadata → `KNOWN BUILD` / `UNKNOWN GAME UPDATE` |
| One permanent signing key | `Keys/NCMK.keystore`, never regenerated automatically (back it up!) |
| Signature mismatch | detected up front (installed signer hash from `dumpsys package`) and on `INSTALL_FAILED_UPDATE_INCOMPATIBLE` → *Cancel* / *Uninstall and reinstall*. The game's **OBB files** (game content in `/sdcard/Android/obb/<pkg>`, deleted with the package) are saved to `Backups\<pkg>\obb\` first (size + MD5 verified; nothing is uninstalled if that fails) and put back after the install; save data is not backed up |
| Unpinned build | patching asks *Pin fingerprints and continue* (pins the verified original backup, then patches) |
| No silent uninstall | uninstall only after explicit confirmation |
| Mod files survive restore | `…/files/NCMKMods` → Keep / rename to `NCMKMods.disabled` / Delete (asked) |
| Older kits | APKs patched by NightClubModKit (`libncsloader.so`, `libncsmod_*.so`) are detected and fully unpatched; `Keys/NightClubModKit.keystore` is migrated to `Keys/NCMK.keystore` (same key) |
| Meta entitlement / bans | no bypass, spoofing or evasion of the game's entitlement check (`MetaAccount.OnUseEntitlement`) or its server-side ban system; **Diagnose** reports an entitlement failure |
| Crash protection | `patched-current.apk` + `patched-previous.apk`, Safe Mode, Restore Original |

**RESTORE ORIGINAL** verifies the backup (hash, package, version, no ModKit markers), stops the
game, installs the original (uninstalling the modded build only after confirmation), handles the mod folder, then verifies package, version, developer signature, absence
of the loader and the installed APK hash.

**EMERGENCY RESTORE** needs nothing but an `original.apk` (it works without `backup.json`,
version profiles, `build.json` or the keystore).

**UNPATCH** puts the original `libmain.so` back, removes the loader, embedded mods and marker,
and re-signs with the ModKit key so it installs over the modded game *without* uninstalling.
RESTORE ORIGINAL remains the safest option.

### About user data

NCMK does not back up or restore save data. The game keeps almost nothing (a few settings);
when a signature change forces an uninstall, it simply starts with fresh data.

The game's **OBB files** are different: they are the game itself (Unity loads its `Resources` and every
scene after `Loading` from `/sdcard/Android/obb/<pkg>/main.<versionCode>.<pkg>.obb`). Android deletes them
with the package, and without them the game stays on its Loading screen. NCMK saves them with
**Backup Original** and before every uninstall (`Backups\<pkg>\obb\`, verified by size + MD5, reused when
identical), puts them back after the install, and also puts them back when an install finds the headset
without its OBB. If they were already lost, reinstall the game from the store once, then patch again.

## Scene loader diagnostics (stuck in "Loading")

The loader can explain why the game stays in its `Loading` scene (`NullReferenceException` in
`bl_SceneLoaderManager.IsGlobalLoadingScreen` / `LoadSceneByID`). Everything is resolved by name
through IL2CPP; the only memory-layout assumption stays the two MethodInfo slots.

| Log prefix | What it shows |
| --- | --- |
| `[NCMK][SceneLoader]` | class namespace, parents, every field (type, static/instance), property and method (parameters, MethodInfo, method pointer); on the first frame and on each scene change: static field values, loaded instances and their fields, **`NULL FIELD:`** lines (including destroyed / missing Unity objects), the result or exception of `IsGlobalLoadingScreen()`; `bl_SceneLoader*.Awake begins / normal loader failed` |
| `[NCMK][MetaPlatform]` | PlatformSettings app id set or empty, `Core.IsInitialized()`, `MetaAchievements.InitPlatform` begins / failed (read-only, nothing bypassed) |
| `[NCMK][Storage]` | the OBB folder and the external files folder as the game process sees them (exists, files, readable), `Application.dataPath` |

`LoadSceneByID` and `IsGlobalLoadingScreen` are called directly by compiled game code, so NCMK cannot
run code right before them; it probes the engine-invoked `Awake()` of their callers instead.
The requested `sceneID` of the game's own call is therefore not visible.

Settings: **Advanced > Builds & Recovery** (*Scene loader diagnostics*, *Scene loader recovery*) or
`--scene-loader-diagnostics on|off` / `--scene-loader-recovery on|off`; applied at the next patch
(written to `assets/NCMK/loader.json` in the APK). `NCMKMods/Config/config.json` can override them:

```json
{ "loader": { "sceneLoaderDiagnostics": true, "sceneLoaderRecovery": false,
              "sceneLoaderRecoveryScene": -1, "sceneLoaderRecoveryDelaySeconds": 10 } }
```

Recovery (off by default) acts only after a confirmed failure (a probed `Awake` threw, or
`IsGlobalLoadingScreen()` throws) and `sceneLoaderRecoveryDelaySeconds` in the same scene: it validates the
target (`sceneLoaderRecoveryScene`, or active index + 1) against `sceneCountInBuildSettings` and
`SceneUtility.GetScenePathByBuildIndex`, calls `SceneManager.LoadSceneAsync` once on the main thread, and
reports `fallback succeeded` or that the scene did not change. If the scene's content is missing (see
`[NCMK][Storage]`), no fallback can load it.

Test flow on the headset (keep the logcat of each run):
1. **A** - diagnostics on, recovery off, no mods (`--no-mods`): find the NULL field / reference.
2. **B** - diagnostics on, recovery on, no mods: does the fallback get past `Loading`?
3. **C** - mods back on once scene loading is stable.

## Writing mods

See [`docs/MODDING.md`](docs/MODDING.md): lifecycle and threads, every export and host-API
function, the C++ SDK (`NCMK::Unity`, `NCMK::Input`, `NCMK::UI`, `NCMK::Config`) and a complete
example mod. In short, a mod is `libncmkmod_<name>.so` exporting `NCMKMod_GetInfo`,
`NCMKMod_Load` and any of `NCMKMod_OnUpdate` / `OnLateUpdate` / `OnFixedUpdate` /
`OnSceneLoaded` (Unity main thread), `OnBackgroundTick` (worker, ~10 Hz) and `Unload`. Mods built
for API 1 and NightClubModKit-era mods (`libncsmod_*.so`, `NCSMod_*`) still load.

**Adding a mod = create `Mods/<Name>/` with a `mod.json` and your `.cpp` files, then click Build
Native Loader.** No other file is edited: the build discovers every folder, and the patcher embeds
every built `libncmkmod_*.so` (it lists them before patching). Delete the folder to remove the mod;
put a `.disabled` file in it to skip it.

Mods are either embedded into the APK by the patcher (always works) or put in `DeviceMods\Mods\`
and pushed to the headset (copied to app-private storage before `dlopen`).

On-device folder — `/sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods`
(the game has no storage permission, so this is the only shared folder it can use; uninstalling the
game deletes it):

```
NCMKMods/
├── Mods/          external libncmkmod_*.so
├── Config/        config.json  ({"loader":{"logging":true,"safeMode":false},"mods":{"ncmk.debugoverlay":true}})
│                  <modId>.cfg  (NCMK::Config, one per mod)
├── Logs/          latest.log
├── Cache/
├── Overrides/     Textures/ Audio/ Bundles/  (reserved for the asset override system)
└── disable_mods   ← presence = SAFE MODE (loader + IL2CPP start, no mods, no dispatcher)
```

## Testing without a headset

`Native/Tests/run_host_test.sh` (Linux) builds the loader for the host, patches a stand-in
`libmain.so` **with the C# ELF patcher**, and runs a fake game that loads
`libmain → libunity → libil2cpp` in Unity's order against a fake IL2CPP runtime, then plays a
72 Hz player loop that calls per-frame methods through `MethodInfo` slot 0 like Unity. It checks
every startup stage, the main-thread dispatcher (fallback after 5 s, per-frame dedupe, original
called first, slots restored on unload, untouched in Safe Mode), every API 2 host function, both
sample mods, a NightClubModKit-era mod, Safe Mode and the foreign-package refusal.
`Native/Tests/check_doc_example.sh` compiles and loads the example mod from `docs/MODDING.md`.

## Status

See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the phase-by-phase status. Everything is implemented
in code and tested on the host only: **every item still needs on-device verification**. Planned:
Cpp2IL cache, SDK generator, object explorer, asset overrides, FULL PATCH.
