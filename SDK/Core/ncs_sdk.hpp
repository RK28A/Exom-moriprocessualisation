// NightClubSDK - C++ convenience layer for mods. Header-only; wraps NCSHostApi.
#pragma once

#include <cstdarg>
#include <cstdio>
#include <string>
#include <vector>

#include "ncs_mod_api.h"

namespace NCS {

namespace detail {
inline const NCSHostApi*& Host() {
    static const NCSHostApi* host = nullptr;
    return host;
}
inline const char*& ModId() {
    static const char* id = "mod";
    return id;
}
}  // namespace detail

/// Call first thing in NCSMod_Load.
inline bool Init(const NCSHostApi* api, const char* modId) {
    detail::Host() = api;
    detail::ModId() = modId;
    return api != nullptr && api->apiVersion >= NCS_LOADER_API;
}

inline const NCSHostApi* Host() { return detail::Host(); }

inline void Log(int level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
inline void Log(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (Host() && Host()->log) Host()->log(level, detail::ModId(), buf);
}

#define NCS_LOGI(...) ::NCS::Log(NCS_LOG_INFO, __VA_ARGS__)
#define NCS_LOGW(...) ::NCS::Log(NCS_LOG_WARN, __VA_ARGS__)
#define NCS_LOGE(...) ::NCS::Log(NCS_LOG_ERROR, __VA_ARGS__)

namespace Game {
inline std::string GetVersion() { return Host() ? Host()->gameVersion : "unknown"; }
inline std::string GetUnityVersion() { return Host() ? Host()->unityVersion : "unknown"; }
inline bool IsSafeMode() { return Host() && Host()->safeMode; }
inline std::vector<std::string> GetLoadedMods() {
    std::vector<std::string> out;
    if (!Host()) return out;
    for (int i = 0; i < Host()->getLoadedModCount(); ++i) out.emplace_back(Host()->getLoadedModName(i));
    return out;
}
}  // namespace Game

}  // namespace NCS
