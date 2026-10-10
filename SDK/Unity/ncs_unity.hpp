// NightClubSDK - Unity helpers built on the IL2CPP layer.
//
// API 1 only exposes thread-safe engine queries. Scene graph access (Find, GetMainCamera,
// GetTransform, GetComponent, CreateObject) requires main-thread execution and will be enabled
// once the Hooks module provides a main-thread dispatcher.
#pragma once

#include <string>

#include "../Il2Cpp/ncs_il2cpp.hpp"

namespace NCS {
namespace Unity {

inline std::string GetUnityVersion() {
    return IL2CPP::CallStaticString("UnityEngine.CoreModule", "UnityEngine", "Application", "get_unityVersion");
}

inline std::string GetApplicationVersion() {
    return IL2CPP::CallStaticString("UnityEngine.CoreModule", "UnityEngine", "Application", "get_version");
}

/// Time.frameCount (thread-safe getter). Returns -1 when unavailable.
inline int GetFrameCount() {
    auto* m = IL2CPP::FindMethod(IL2CPP::FindClass("UnityEngine.CoreModule", "UnityEngine", "Time"), "get_frameCount", 0);
    bool threw = false;
    auto* r = IL2CPP::Invoke(m, nullptr, nullptr, &threw);
    return (threw || !r) ? -1 : IL2CPP::Unbox<int>(r);
}

/// SceneManager.GetActiveScene().name - requires the main thread; returns "" when called elsewhere.
inline std::string GetCurrentSceneName() {
    auto* sm = IL2CPP::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "SceneManager");
    auto* get = IL2CPP::FindMethod(sm, "GetActiveScene", 0);
    bool threw = false;
    auto* boxed = IL2CPP::Invoke(get, nullptr, nullptr, &threw);
    if (threw || !boxed) return {};
    auto* sceneClass = IL2CPP::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "Scene");
    auto* getName = IL2CPP::FindMethod(sceneClass, "get_name", 0);
    void* scene = Host()->unbox(boxed);
    auto* r = IL2CPP::Invoke(getName, scene, nullptr, &threw);
    return threw ? std::string() : IL2CPP::ToString(r);
}

}  // namespace Unity

namespace Game {
inline std::string GetCurrentScene() { return Unity::GetCurrentSceneName(); }
}  // namespace Game

}  // namespace NCS
