#pragma once

#include <string>
#include <vector>

namespace ncmk {

constexpr const char* kTargetPackage = "com.mandegames.NightClubSimulater";
constexpr const char* kLoaderVersion = "0.3.0";

struct LoaderConfig {
    bool logging = true;
    bool safeMode = false;
    std::vector<std::string> disabledMods;  // "mods": { "<id>": false }

    // Diagnostics (Native/Diagnostics). Defaults come from assets/NCMK/loader.json in the APK
    // (written by the patcher), then "loader" in NCMKMods/Config/config.json when it is readable.
    bool sceneLoaderDiagnostics = true;   // log bl_SceneLoaderManager metadata and state; behaviour unchanged
    bool sceneLoaderRecovery = false;     // after a confirmed failure, one SceneManager.LoadSceneAsync fallback
    int sceneLoaderRecoveryScene = -1;    // build index to load; -1 = active scene index + 1
    int sceneLoaderRecoveryDelay = 10;    // seconds stuck in the same scene before the fallback
    bool metaPlatformDiagnostics = true;  // log Oculus.Platform app id / init state (read-only)
    bool storageDiagnostics = true;       // log OBB / external storage access from the game process
    bool remoteCommands = true;           // @ncmk socket: lets the PC request an IL2CPP dump (read-only)
    std::string source = "defaults";      // where the values came from (logged)
};

struct DeviceInfo {
    std::string model;        // ro.product.model    e.g. "Quest 3S"
    std::string codename;     // ro.product.device   e.g. "panther"
    std::string displayName;  // "Meta Quest 3S"
    std::string androidRelease;
    std::string sdk;
    bool known = false;       // a recognised Quest generation
    bool supported = true;
};

/// Identifies the headset (Quest 2 / Pro / 3 / 3S; Quest 1 is unsupported).
DeviceInfo DetectDevice();

std::string CurrentPackageName();     // from /proc/self/cmdline
std::string SelfLibraryPath();        // path of libncmkloader.so (may contain "!/" when loaded from the APK)
std::string AppDataDir();             // /data/user/0/<pkg> (or /data/data/<pkg>)

/// Picks the writable NCMKMods root: /sdcard/Android/data/<pkg>/files/NCMKMods (the game has no
/// storage permission, so its own external files folder is used). Creates Mods/Config/Logs/Cache/Overrides.
std::string ResolveModsRoot(const std::string& package);

/// Applies the "loader" keys of a JSON text (config.json or the APK's assets/NCMK/loader.json).
void ApplyLoaderJson(LoaderConfig& cfg, const std::string& json);

/// Reads assets/NCMK/loader.json from the game's APK (stored entry) into cfg; false when absent.
bool LoadEmbeddedConfig(LoaderConfig& cfg, const std::string& apkPath);

/// base.apk next to an extracted lib dir (".../lib/arm64/libncmkloader.so" -> ".../base.apk"), or the
/// APK part of "app.apk!/lib/arm64-v8a/libncmkloader.so".
std::string ApkPathFromLibrary(const std::string& libPath);

/// Reads config.json (created with defaults when missing) on top of cfg.
LoaderConfig LoadConfig(const std::string& modsRoot, LoaderConfig cfg = {});
bool SafeModeFlagPresent(const std::string& modsRoot);

bool MakeDirs(const std::string& path);
bool FileExists(const std::string& path);
bool CopyFile(const std::string& from, const std::string& to);

/// Lists "lib/arm64-v8a/libncmkmod_*.so" (and legacy libncsmod_*.so) entries of an APK (reads the ZIP central directory).
std::vector<std::string> ListEmbeddedMods(const std::string& apkPath);

/// Lists libncmkmod_*.so (and legacy libncsmod_*.so) files in a directory.
std::vector<std::string> ListModFilesInDir(const std::string& dir);

}  // namespace ncmk
