#pragma once

#include <string>
#include <vector>

#include "../../SDK/Core/ncmk_mod_api.h"

namespace ncmk {

struct LoadedMod {
    void* handle = nullptr;
    std::string file;
    std::string id;
    std::string name;
    std::string version;
    std::string displayName;  // "Name version"
    unsigned requiredApi = 1;
    bool legacy = false;       // NightClubModKit-era mod exporting NCSMod_*
    NCMKMod_Unload_t unload = nullptr;
    NCMKMod_OnUpdate_t onUpdate = nullptr;
    NCMKMod_OnLateUpdate_t onLateUpdate = nullptr;
    NCMKMod_OnFixedUpdate_t onFixedUpdate = nullptr;
    NCMKMod_OnSceneLoaded_t onSceneLoaded = nullptr;
    NCMKMod_OnBackgroundTick_t onBackgroundTick = nullptr;
};

struct ModCandidate {
    std::string dlopenName;  // what to pass to dlopen (soname or absolute path)
    std::string origin;      // "apk" / "external" / "dir"
};

class PluginManager {
public:
    void SetDisabledIds(std::vector<std::string> ids) { disabled_ = std::move(ids); }

    /// Loads candidates in order; failures are logged and skipped, never fatal. Worker thread.
    void LoadAll(const std::vector<ModCandidate>& candidates, const NCMKHostApi* host);

    /// Loader worker thread, ~10 Hz.
    void BackgroundTickAll(float dt);

    /// Unity main thread (dispatcher), once per frame.
    void FrameAll(float dt, int fixedSteps, const char* sceneLoaded);

    void UnloadAll();

    const std::vector<LoadedMod>& Mods() const { return mods_; }
    bool AnyBackgroundTick() const;
    bool AnyMainThreadCallback() const;

private:
    bool LoadOne(const ModCandidate& c, const NCMKHostApi* host);
    bool IsDisabled(const std::string& id) const;

    std::vector<LoadedMod> mods_;
    std::vector<std::string> disabled_;
};

}  // namespace ncmk
