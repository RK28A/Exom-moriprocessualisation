// NCMK SDK - C++ convenience layer for mods. Header-only; wraps NCMKHostApi.
//
//   #include "SDK/Core/ncmk_sdk.hpp"      Init, logging, game info, main-thread helpers
//   #include "SDK/Il2Cpp/ncmk_il2cpp.hpp" name-based IL2CPP access
//   #include "SDK/Unity/ncmk_unity.hpp"   GameObject, Transform, Camera, Time, Scene, value types
//   #include "SDK/Input/ncmk_input.hpp"   Quest controllers (OVRInput)
//   #include "SDK/UI/ncmk_ui.hpp"         world-space Canvas, Image, Text, textures, sprites
//   #include "SDK/Config/ncmk_config.hpp" per-mod key/value config file
#pragma once

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>
#include <vector>

#include "ncmk_mod_api.h"

namespace NCMK {

namespace detail {
inline const NCMKHostApi*& Host() {
    static const NCMKHostApi* host = nullptr;
    return host;
}
inline const char*& ModId() {
    static const char* id = "mod";
    return id;
}
}  // namespace detail

/// Call first thing in NCMKMod_Load. Fails when the loader is older than the API 1 baseline.
inline bool Init(const NCMKHostApi* api, const char* modId) {
    detail::Host() = api;
    detail::ModId() = modId;
    return api != nullptr && api->apiVersion >= 1;
}

inline const NCMKHostApi* Host() { return detail::Host(); }
inline const char* ModId() { return detail::ModId(); }

/// True when the running loader implements API 2 (main-thread callbacks and helpers).
inline bool HasApi2() { return NCMK_HOST_HAS(Host(), getEnumValue); }

inline void LogV(int level, const char* fmt, va_list ap) {
    char buf[1024];
    vsnprintf(buf, sizeof(buf), fmt, ap);
    if (Host() && Host()->log) Host()->log(level, detail::ModId(), buf);
}

inline void Log(int level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
inline void Log(int level, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    LogV(level, fmt, ap);
    va_end(ap);
}

#define NCMK_LOGI(...) ::NCMK::Log(NCMK_LOG_INFO, __VA_ARGS__)
#define NCMK_LOGW(...) ::NCMK::Log(NCMK_LOG_WARN, __VA_ARGS__)
#define NCMK_LOGE(...) ::NCMK::Log(NCMK_LOG_ERROR, __VA_ARGS__)

namespace detail {
/// Remembers keys (string literals / function names) that already logged, so a failing helper
/// called every frame logs once instead of flooding logcat.
inline bool FirstTime(const char* key) {
    static std::mutex lock;
    static std::vector<std::string> seen;
    std::lock_guard<std::mutex> g(lock);
    for (const auto& s : seen)
        if (s == key) return false;
    seen.emplace_back(key);
    return true;
}
}  // namespace detail

/// Logs a warning the first time `key` fails; silent afterwards.
inline void FailOnce(const char* key, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
inline void FailOnce(const char* key, const char* fmt, ...) {
    if (!detail::FirstTime(key)) return;
    va_list ap;
    va_start(ap, fmt);
    LogV(NCMK_LOG_WARN, fmt, ap);
    va_end(ap);
}

/// 1 on Unity's main thread (API 2 loaders, after the dispatcher's first frame).
inline bool IsMainThread() { return HasApi2() && Host()->isMainThread(); }

/// Queues fn(user) for the main thread (next frame). False if no dispatcher is running.
inline bool RunOnMainThread(NCMKMainThreadFn fn, void* user) {
    return HasApi2() && Host()->runOnMainThread(fn, user) != 0;
}

namespace detail {
/// Debug builds: every Unity/UI/Input helper checks it runs on the main thread and logs once per
/// helper when it does not (the helper then returns an empty/default value). Release builds skip
/// the check.
inline bool CheckMainThread(const char* helper) {
#ifndef NDEBUG
    if (!IsMainThread()) {
        if (FirstTime(helper))
            Log(NCMK_LOG_ERROR, "%s called outside Unity's main thread - use NCMKMod_OnUpdate or NCMK::RunOnMainThread", helper);
        return false;
    }
#else
    (void)helper;
#endif
    return true;
}
}  // namespace detail

#define NCMK_REQUIRE_MAIN_THREAD(ret)                                   \
    do {                                                                \
        if (!::NCMK::detail::CheckMainThread(__PRETTY_FUNCTION__)) return ret; \
    } while (0)

namespace Game {
inline std::string GetVersion() { return Host() ? Host()->gameVersion : "unknown"; }
inline std::string GetUnityVersion() { return Host() ? Host()->unityVersion : "unknown"; }
inline bool IsSafeMode() { return Host() && Host()->safeMode; }
inline std::string GetModsRoot() { return Host() && Host()->modsRoot ? Host()->modsRoot : ""; }
/// "Meta Quest 3S", or "unknown" with loaders older than 0.2.0.
inline std::string GetDeviceName() { return NCMK_HOST_HAS(Host(), deviceName) ? Host()->deviceName : "unknown"; }
inline std::string GetDeviceCodename() { return NCMK_HOST_HAS(Host(), deviceCodename) ? Host()->deviceCodename : ""; }
inline std::vector<std::string> GetLoadedMods() {
    std::vector<std::string> out;
    if (!Host()) return out;
    for (int i = 0; i < Host()->getLoadedModCount(); ++i) out.emplace_back(Host()->getLoadedModName(i));
    return out;
}
}  // namespace Game

}  // namespace NCMK
