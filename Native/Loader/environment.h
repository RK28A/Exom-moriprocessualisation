#pragma once

#include <string>
#include <vector>

namespace ncs {

constexpr const char* kTargetPackage = "com.mandegames.NightClubSimulater";
constexpr const char* kLoaderVersion = "0.1.0";

struct LoaderConfig {
    bool logging = true;
    bool safeMode = false;
    std::vector<std::string> disabledMods;  // "mods": { "<id>": false }
};

std::string CurrentPackageName();     // from /proc/self/cmdline
std::string SelfLibraryPath();        // path of libncsloader.so (may contain "!/" when loaded from the APK)
std::string AppDataDir();             // /data/user/0/<pkg> (or /data/data/<pkg>)

/// Picks a writable NightClubMods root: /sdcard/NightClubMods, falling back to
/// /sdcard/Android/data/<pkg>/files/NightClubMods. Creates Mods/Config/Logs/Cache/Overrides.
std::string ResolveModsRoot(const std::string& package);

LoaderConfig LoadConfig(const std::string& modsRoot);
bool SafeModeFlagPresent(const std::string& modsRoot);

bool MakeDirs(const std::string& path);
bool FileExists(const std::string& path);
bool CopyFile(const std::string& from, const std::string& to);

/// Lists "lib/arm64-v8a/libncsmod_*.so" entries of an APK (reads the ZIP central directory).
std::vector<std::string> ListEmbeddedMods(const std::string& apkPath);

/// Lists libncsmod_*.so files in a directory.
std::vector<std::string> ListModFilesInDir(const std::string& dir);

}  // namespace ncs
