#pragma once

#include <string>

// NCS::Unity - engine queries built on NCS::IL2CPP (name-based lookups only).
// Only thread-safe getters are exposed in API 1; scene graph helpers (FindGameObject,
// GetMainCamera, GetTransform, GetScene, GetComponent, CreateObject) need a main-thread
// dispatcher from the Hooks module and are intentionally not implemented yet.
namespace NCS {
namespace Unity {

std::string GetUnityVersion();      // UnityEngine.Application.unityVersion
std::string GetApplicationVersion(); // UnityEngine.Application.version
std::string GetProductName();        // UnityEngine.Application.productName
int GetFrameCount();                 // UnityEngine.Time.frameCount, -1 if unavailable

}  // namespace Unity
}  // namespace NCS
