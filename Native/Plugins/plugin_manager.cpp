#include "plugin_manager.h"

#include <dlfcn.h>

#include <algorithm>

#include "../Logging/ncs_log.h"

namespace ncs {

bool PluginManager::IsDisabled(const std::string& id) const {
    return std::find(disabled_.begin(), disabled_.end(), id) != disabled_.end();
}

void PluginManager::LoadAll(const std::vector<ModCandidate>& candidates, const NCSHostApi* host) {
    for (const auto& c : candidates) LoadOne(c, host);
    NCS_LOGI("[NCS] %zu mod(s) loaded", mods_.size());
}

bool PluginManager::LoadOne(const ModCandidate& c, const NCSHostApi* host) {
    NCS_LOGI("[NCS] Loading mod %s (%s)", c.dlopenName.c_str(), c.origin.c_str());
    void* h = dlopen(c.dlopenName.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!h) {
        NCS_LOGE("[NCS] dlopen failed for %s: %s", c.dlopenName.c_str(), dlerror());
        return false;
    }

    auto getInfo = reinterpret_cast<NCSMod_GetInfo_t>(dlsym(h, "NCSMod_GetInfo"));
    auto load = reinterpret_cast<NCSMod_Load_t>(dlsym(h, "NCSMod_Load"));
    if (!getInfo || !load) {
        NCS_LOGE("[NCS] %s does not export NCSMod_GetInfo/NCSMod_Load - skipped", c.dlopenName.c_str());
        dlclose(h);
        return false;
    }

    const NCSModInfo* info = getInfo();
    if (!info || info->structSize < sizeof(NCSModInfo) || !info->id || !info->name) {
        NCS_LOGE("[NCS] %s returned invalid NCSModInfo - skipped", c.dlopenName.c_str());
        dlclose(h);
        return false;
    }
    if (info->requiredApi > NCS_LOADER_API) {
        NCS_LOGE("[NCS] %s requires loader API %u, this loader provides %d - skipped", info->name, info->requiredApi,
                 NCS_LOADER_API);
        dlclose(h);
        return false;
    }
    for (const auto& m : mods_) {
        if (m.id == info->id) {
            NCS_LOGW("[NCS] Mod id %s already loaded from %s - skipping %s", info->id, m.file.c_str(), c.dlopenName.c_str());
            dlclose(h);
            return false;
        }
    }
    if (IsDisabled(info->id)) {
        NCS_LOGI("[NCS] Mod %s disabled in config.json - skipped", info->id);
        dlclose(h);
        return false;
    }

    int rc = load(host);
    if (rc != 0) {
        NCS_LOGE("[NCS] %s NCSMod_Load returned %d - unloaded", info->name, rc);
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
    m.update = reinterpret_cast<NCSMod_Update_t>(dlsym(h, "NCSMod_Update"));
    m.unload = reinterpret_cast<NCSMod_Unload_t>(dlsym(h, "NCSMod_Unload"));
    m.onSceneLoaded = reinterpret_cast<NCSMod_OnSceneLoaded_t>(dlsym(h, "NCSMod_OnSceneLoaded"));
    mods_.push_back(m);
    NCS_LOGI("[NCS] Mod loaded: %s %s (%s by %s)", m.name.c_str(), m.version.c_str(), m.id.c_str(),
             info->author ? info->author : "?");
    return true;
}

void PluginManager::UpdateAll(float dt) {
    for (auto& m : mods_)
        if (m.update) m.update(dt);
}

void PluginManager::UnloadAll() {
    for (auto it = mods_.rbegin(); it != mods_.rend(); ++it) {
        if (it->unload) it->unload();
        dlclose(it->handle);
    }
    mods_.clear();
}

}  // namespace ncs
