#pragma once

#include <string>

// Loader-side engine queries built on NCMK::IL2CPP (name-based lookups only), used on the worker
// thread at startup. Mods use the scene-graph helpers of the SDK (SDK/Unity/ncmk_unity.hpp) from
// the main-thread callbacks instead.
namespace NCMK {
namespace Unity {

std::string GetUnityVersion();      // UnityEngine.Application.unityVersion
std::string GetApplicationVersion(); // UnityEngine.Application.version
std::string GetProductName();        // UnityEngine.Application.productName
int GetFrameCount();                 // UnityEngine.Time.frameCount, -1 if unavailable

}  // namespace Unity
}  // namespace NCMK
