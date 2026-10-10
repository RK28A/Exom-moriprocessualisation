#pragma once

#include <string>
#include <vector>

#include "../../SDK/Core/ncs_mod_api.h"

namespace ncs {

struct LoadedMod {
    void* handle = nullptr;
    std::string file;
    std::string id;
    std::string name;
    std::string version;
    std::string displayName;  // "Name version"
    NCSMod_Update_t update = nullptr;
    NCSMod_Unload_t unload = nullptr;
    NCSMod_OnSceneLoaded_t onSceneLoaded = nullptr;
};

struct ModCandidate {
    std::string dlopenName;  // what to pass to dlopen (soname or absolute path)
    std::string origin;      // "apk" / "external" / "dir"
};

class PluginManager {
public:
    void SetDisabledIds(std::vector<std::string> ids) { disabled_ = std::move(ids); }

    /// Loads candidates in order; failures are logged and skipped, never fatal.
    void LoadAll(const std::vector<ModCandidate>& candidates, const NCSHostApi* host);
    void UpdateAll(float dt);
    void UnloadAll();

    const std::vector<LoadedMod>& Mods() const { return mods_; }

private:
    bool LoadOne(const ModCandidate& c, const NCSHostApi* host);
    bool IsDisabled(const std::string& id) const;

    std::vector<LoadedMod> mods_;
    std::vector<std::string> disabled_;
};

}  // namespace ncs
