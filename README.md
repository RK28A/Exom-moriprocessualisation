# NightClubModKit

Dedicated Windows patcher and native modding framework for **NightClub Simulator** on Meta Quest.

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
        %TEMP%\NightClubModKit\<ts>\{input,unpacked,native,patched,output}
                         │  FAST PATCH
                         │   • libmain.so  +DT_NEEDED libncsloader.so   (ELF patch, no code change)
                         │   • + lib/arm64-v8a/libncsloader.so          (stored, page-aligned)
                         │   • + lib/arm64-v8a/libncsmod_*.so           (optional embedded mods)
                         │   • + assets/NightClubModKit/patch.json      (marker)
                         │   • old signature stripped
                         ▼
                 zipalign ─► apksigner (permanent NightClubModKit key)
                         ▼
     Builds/1.1.0-212/patched-current.apk  (previous one rotated to patched-previous.apk)
                         ▼
                adb install (signature-safe) ─► launch ─► logcat:
                         [NCSLoader] Hello from NightClubModKit
```

On the device the loader runs **before Unity**: it logs, starts a worker thread, waits for
`libunity.so` then `libil2cpp.so`, resolves the IL2CPP API with `dlsym`, waits for the runtime,
enumerates assemblies and loads mods. **No hooks, no game code changes.**

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
* **Storage on newer Horizon OS (Quest 3 / 3S).** Shared storage is scoped: when
  `/sdcard/NightClubMods` is not writable by the game, the loader automatically uses
  `/sdcard/Android/data/com.mandegames.NightClubSimulater/files/NightClubMods` (Safe Mode flags are
  written to both).
* The loader logs the headset (`[NCS] Device: Meta Quest 3S (Quest 3S / panther), Android 14 (SDK 34)`)
  and exposes it to mods (`NCS::Game::GetDeviceName()`), and `backup.json` / `build.json` record the
  source and target headset.

---

## Repository layout

```
NightClubModKit.sln
Patcher/
  NightClubPatcher.Core/    all logic (shared by GUI + CLI)
    Adb/  Apk/  Android/  Elf/  Signing/  Backup/  Recovery/  Unity/  Il2Cpp/
    Diagnostics/  Versions/  Patching/  Builds/  Infrastructure/
  NightClubPatcher.Gui/     WPF app - MAIN APPLICATION -> NightClubPatcher.exe
  NightClubPatcher.Cli/     command line (advanced)    -> NightClubPatcher.Cli.exe
  NightClubPatcher.Tests/   xUnit tests (+ real AArch64 ELF fixtures)
Native/                     libncsloader.so (C++17, Android NDK)
  Loader/ Il2Cpp/ Unity/ Logging/ Plugins/ Hooks/ Tests/
SDK/                        headers for mod authors
  Core/ncs_mod_api.h        stable C ABI (NCS_LOADER_API = 1)
  Core/ncs_sdk.hpp  Il2Cpp/ncs_il2cpp.hpp  Unity/ncs_unity.hpp
Mods/HelloWorld  Mods/DebugOverlay
Tools/  Versions/  Backups/  Builds/  Cache/  Logs/  Keys/
```

The patcher sub-areas listed in the spec (`Apk/`, `Elf/`, `Adb/`, …) live as folders/namespaces
inside `NightClubPatcher.Core` so the GUI and CLI share one implementation.

---

## Quick start on Windows (no build)

1. Download the **NightClubPatcher-Release-win-x64** artifact from the latest successful
   *build* run in GitHub Actions (it is the `Release\` folder described below) and unzip it.
2. Double-click **`NightClubPatcher.exe`**: the graphical patcher opens. No .NET install is
   needed (self-contained).
3. On first start, click **Install tools** when offered (adb, zipalign, apksigner, Java).
4. Connect the Quest by USB (developer mode on), accept *Allow USB debugging* in the headset,
   then press **Patch + Install**.

## Requirements to build (Windows)

1. **Visual Studio 2022 / 2026** with the *.NET desktop development* workload, or the
   **.NET 8 SDK** alone (`dotnet --list-sdks` must show 8.0.x or newer).
2. **adb, zipalign, apksigner, Java 17** — **installed automatically** by the app (Install tools):
   Android platform-tools and the latest stable build-tools from Google's official SDK repository
   (SHA-1 verified, Android SDK License), Java from the Microsoft Build of OpenJDK, all into
   `Tools\`. CLI: `NightClubPatcher.Cli.exe --install-tools`. An existing Android SDK / JDK /
   `PATH` install is used if present.
3. **Android NDK r26+** only to build the native loader (`libncsloader.so`); CI builds it too.
4. Quest in **developer mode**, USB debugging authorized.

## Build & run on Windows

### Visual Studio (F5)

1. Open **`NightClubModKit.sln`**. Solution Explorer shows the *Patcher* folder with
   `NightClubPatcher.Gui` (startup project, listed first), `NightClubPatcher.Core`,
   `NightClubPatcher.Cli` and `NightClubPatcher.Tests`.
2. Press **F5**: the WPF window opens. (If Visual Studio remembered an older startup project from a
   previous `.vs` folder: right-click `NightClubPatcher.Gui` → *Set as Startup Project*, or delete
   the hidden `.vs` folder.)
3. When running from Visual Studio the kit folder is the repository root (`Backups\`, `Builds\`,
   `Logs\`, `Keys\`, `Tools\`, `Versions\` next to the `.sln`).

### Command line

```powershell
# optional: native loader + sample mods -> Native\out\arm64-v8a\*.so  (Android NDK)
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
├── libncsloader.so             native loader (when built)
├── libncsmod_*.so              sample mods (when built; embedded into the APK by the patcher)
├── Versions\                   game version profiles
├── Tools\                      adb / zipalign / apksigner / jdk (auto-installed on first start)
├── Logs\
└── NightClubModKit.root        marks this folder as the kit root
```

The script **never deletes** an existing `Release\` folder: once used, it holds `Backups\` (your
original APK) and `Keys\` (your signing key). It only replaces the executables, adds missing
profiles/tools and keeps version profiles you already pinned.

Plain `dotnet` equivalents:

```powershell
dotnet build NightClubModKit.sln -c Release
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
  **Restart ADB** fixes most *offline* headsets; **Install tools** appears when something is missing.
* **Headset is watched continuously** (`adb devices` every 3 s): plugging/unplugging a Quest or
  accepting the USB-debugging prompt updates the status by itself.
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
[NCSLoader] Hello from NightClubModKit
[NCS] STAGE 02 Worker thread
[NCSLoader] libil2cpp.so found
[NCSLoader] IL2CPP API ready
[NCS]   Assembly-CSharp (… classes)
[NCSLoader] Class found: UnityEngine.Application
[NCS] Mod loaded: HelloWorld 1.0.0
[NCS] NCSLoader started successfully
```

To run *only* the first milestone (loader, no mods), patch with `--no-mods` or untick
*Embed libncsmod_*.so* in Advanced.

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
| Modified game never backed up as original | `libncsloader.so`, `DT_NEEDED` in libmain, patch marker, embedded mods, ModKit signer, unsigned APK, debuggable manifest → *"Installed APK appears modified. Original backup will not be replaced."* |
| Never edit the backup | all work happens on a copy in `%TEMP%\NightClubModKit\…` |
| Unknown update ⇒ no auto patch | version profile + SHA-256 of libmain/libunity/libil2cpp/global-metadata → `KNOWN BUILD` / `UNKNOWN GAME UPDATE` |
| One permanent signing key | `Keys/NightClubModKit.keystore`, never regenerated automatically (back it up!) |
| Signature mismatch | detected up front (installed signer hash from `dumpsys package`) and on `INSTALL_FAILED_UPDATE_INCOMPATIBLE` → *Cancel* / *Backup accessible data and reinstall* |
| No silent data loss | uninstall only after explicit confirmation; accessible data backed up to `Backups/<pkg>/userdata/<ts>/` and restored |
| Mod files survive restore | `/sdcard/NightClubMods` → Keep / rename to `NightClubMods.disabled` / Delete (asked) |
| Crash protection | `patched-current.apk` + `patched-previous.apk`, Safe Mode, Restore Original |

**RESTORE ORIGINAL** verifies the backup (hash, package, version, no ModKit markers), stops the
game, installs the original (uninstalling the modded build only after confirmation and data
backup), handles the mod folder, then verifies package, version, developer signature, absence
of the loader and the installed APK hash.

**EMERGENCY RESTORE** needs nothing but an `original.apk` (it works without `backup.json`,
version profiles, `build.json` or the keystore).

**UNPATCH** puts the original `libmain.so` back, removes the loader, embedded mods and marker,
and re-signs with the ModKit key so it installs over the modded game *without* uninstalling.
RESTORE ORIGINAL remains the safest option.

### About user data

Unity stores most saves in `persistentDataPath` = `/sdcard/Android/data/<pkg>/files`, which adb
can back up. `/data/data/<pkg>` (e.g. `PlayerPrefs`) is **not** readable without root on store
builds; it may be lost when Android forces an uninstall because of a signature change. Meta
cloud backup may restore it. The patcher states this before asking for confirmation.

---

## Writing mods

See [`docs/MODDING.md`](docs/MODDING.md). In short, a mod is `libncsmod_<name>.so` exporting
`NCSMod_GetInfo`, `NCSMod_Load`, and optionally `NCSMod_Update` / `NCSMod_Unload`. Mods are
either embedded into the APK by the patcher (always works) or dropped into
`/sdcard/NightClubMods/Mods/` (copied to app-private storage before `dlopen`).

On-device folders (`/sdcard/NightClubMods`, falling back to
`/sdcard/Android/data/<pkg>/files/NightClubMods` when shared storage is not writable):

```
NightClubMods/
├── Mods/          external libncsmod_*.so
├── Config/        config.json  ({"loader":{"logging":true,"safeMode":false},"mods":{"ncs.debugoverlay":true}})
├── Logs/          latest.log
├── Cache/
├── Overrides/     Textures/ Audio/ Bundles/  (reserved for the asset override system)
└── disable_mods   ← presence = SAFE MODE (loader + IL2CPP start, no mods)
```

## Testing without a headset

`Native/Tests/run_host_test.sh` (Linux) builds the loader for the host, patches a stand-in
`libmain.so` **with the C# ELF patcher**, and runs a fake game that loads
`libmain → libunity → libil2cpp` in Unity's order against a fake IL2CPP runtime. It checks every
startup stage, both sample mods, Safe Mode and the foreign-package refusal.

## Status

See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the phase-by-phase status (phases 1–30 implemented
in code; on-device verification pending), and what is still planned (Cpp2IL cache, SDK
generator, object explorer, asset overrides, FULL PATCH, main-thread hooks).
