#include "plugin_manager.h"

#include <dlfcn.h>

#include <algorithm>

#include "../Logging/ncmk_log.h"

namespace ncmk {

namespace {
// Current export, or the NightClubModKit-era NCSMod_* name for legacy mods.
template <typename T>
T Sym(void* h, const char* name, bool legacy) {
    std::string n = std::string(legacy ? "NCSMod_" : "NCMKMod_") + name;
    return reinterpret_cast<T>(dlsym(h, n.c_str()));
}
}  // namespace

bool PluginManager::IsDisabled(const std::string& id) const {
    return std::find(disabled_.begin(), disabled_.end(), id) != disabled_.end();
}

void PluginManager::LoadAll(const std::vector<ModCandidate>& candidates, const NCMKHostApi* host) {
    for (const auto& c : candidates) LoadOne(c, host);
    NCMK_LOGI("[NCMK] %zu mod(s) loaded", mods_.size());
}

bool PluginManager::LoadOne(const ModCandidate& c, const NCMKHostApi* host) {
    NCMK_LOGI("[NCMK] Loading mod %s (%s)", c.dlopenName.c_str(), c.origin.c_str());
    void* h = dlopen(c.dlopenName.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!h) {
        NCMK_LOGE("[NCMK] dlopen failed for %s: %s", c.dlopenName.c_str(), dlerror());
        return false;
    }

    bool legacy = false;
    auto getInfo = Sym<NCMKMod_GetInfo_t>(h, "GetInfo", false);
    auto load = Sym<NCMKMod_Load_t>(h, "Load", false);
    if (!getInfo || !load) {
        // NightClubModKit API 1 mod: same struct layouts, NCSMod_* export names.
        getInfo = Sym<NCMKMod_GetInfo_t>(h, "GetInfo", true);
        load = Sym<NCMKMod_Load_t>(h, "Load", true);
        legacy = getInfo && load;
    }
    if (!getInfo || !load) {
        NCMK_LOGE("[NCMK] %s does not export NCMKMod_GetInfo/NCMKMod_Load - skipped", c.dlopenName.c_str());
        dlclose(h);
        return false;
    }

    const NCMKModInfo* info = getInfo();
    if (!info || info->structSize < sizeof(NCMKModInfo) || !info->id || !info->name) {
        NCMK_LOGE("[NCMK] %s returned invalid NCMKModInfo - skipped", c.dlopenName.c_str());
        dlclose(h);
        return false;
    }
    if (info->requiredApi > NCMK_LOADER_API) {
        NCMK_LOGE("[NCMK] %s requires loader API %u, this loader provides %d - skipped", info->name, info->requiredApi,
                  NCMK_LOADER_API);
        dlclose(h);
        return false;
    }
    for (const auto& m : mods_) {
        if (m.id == info->id) {
            NCMK_LOGW("[NCMK] Mod id %s already loaded from %s - skipping %s", info->id, m.file.c_str(), c.dlopenName.c_str());
            dlclose(h);
            return false;
        }
    }
    if (IsDisabled(info->id)) {
        NCMK_LOGI("[NCMK] Mod %s disabled in config.json - skipped", info->id);
        dlclose(h);
        return false;
    }

    int rc = load(host);
    if (rc != 0) {
        NCMK_LOGE("[NCMK] %s Load returned %d - unloaded", info->name, rc);
        dlclose(h);
        return false;
    }

    LoadedMod m;
    m.handle = h;
    m.file = c.dlopenName;
    m.id = info->id;
    m.name = info->name;
    m.version = info->version ? info->version : "0.0.0";
    m.displayName = m.name + " " + m.version;
    m.requiredApi = info->requiredApi;
    m.legacy = legacy;
    m.unload = Sym<NCMKMod_Unload_t>(h, "Unload", legacy);
    m.onUpdate = Sym<NCMKMod_OnUpdate_t>(h, "OnUpdate", legacy);
    m.onLateUpdate = Sym<NCMKMod_OnLateUpdate_t>(h, "OnLateUpdate", legacy);
    m.onFixedUpdate = Sym<NCMKMod_OnFixedUpdate_t>(h, "OnFixedUpdate", legacy);
    m.onSceneLoaded = Sym<NCMKMod_OnSceneLoaded_t>(h, "OnSceneLoaded", legacy);
    m.onBackgroundTick = Sym<NCMKMod_OnBackgroundTick_t>(h, "OnBackgroundTick", legacy);
    // API 1: "Update" was the ~10 Hz loader-thread callback; it keeps that meaning.
    if (!m.onBackgroundTick && m.requiredApi < 2) m.onBackgroundTick = Sym<NCMKMod_OnBackgroundTick_t>(h, "Update", legacy);
    mods_.push_back(m);
    NCMK_LOGI("[NCMK] Mod loaded: %s %s (%s by %s, API %u%s)", m.name.c_str(), m.version.c_str(), m.id.c_str(),
              info->author ? info->author : "?", m.requiredApi, legacy ? ", legacy NCSMod exports" : "");
    return true;
}

bool PluginManager::AnyBackgroundTick() const {
    return std::any_of(mods_.begin(), mods_.end(), [](const LoadedMod& m) { return m.onBackgroundTick != nullptr; });
}

bool PluginManager::AnyMainThreadCallback() const {
    return std::any_of(mods_.begin(), mods_.end(), [](const LoadedMod& m) {
        return m.onUpdate || m.onLateUpdate || m.onFixedUpdate || m.onSceneLoaded;
    });
}

void PluginManager::BackgroundTickAll(float dt) {
    for (auto& m : mods_)
        if (m.onBackgroundTick) m.onBackgroundTick(dt);
}

void PluginManager::FrameAll(float dt, int fixedSteps, const char* sceneLoaded) {
    if (sceneLoaded) {
        NCMK_LOGI("[NCMK] Scene loaded: %s", sceneLoaded);
        for (auto& m : mods_)
            if (m.onSceneLoaded) m.onSceneLoaded(sceneLoaded);
    }
    for (int i = 0; i < fixedSteps; ++i)
        for (auto& m : mods_)
            if (m.onFixedUpdate) m.onFixedUpdate();
    for (auto& m : mods_)
        if (m.onUpdate) m.onUpdate(dt);
    for (auto& m : mods_)
        if (m.onLateUpdate) m.onLateUpdate(dt);
}

void PluginManager::UnloadAll() {
    for (auto it = mods_.rbegin(); it != mods_.rend(); ++it) {
        if (it->unload) it->unload();
        dlclose(it->handle);
    }
    mods_.clear();
}

}  // namespace ncmk
