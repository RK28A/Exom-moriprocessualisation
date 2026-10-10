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

LoaderConfig LoadConfig(const std::string& modsRoot);
bool SafeModeFlagPresent(const std::string& modsRoot);

bool MakeDirs(const std::string& path);
bool FileExists(const std::string& path);
bool CopyFile(const std::string& from, const std::string& to);

/// Lists "lib/arm64-v8a/libncmkmod_*.so" (and legacy libncsmod_*.so) entries of an APK (reads the ZIP central directory).
std::vector<std::string> ListEmbeddedMods(const std::string& apkPath);

/// Lists libncmkmod_*.so (and legacy libncsmod_*.so) files in a directory.
std::vector<std::string> ListModFilesInDir(const std::string& dir);

}  // namespace ncmk
