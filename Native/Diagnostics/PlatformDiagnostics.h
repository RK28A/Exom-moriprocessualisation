// Read-only diagnostics for what the game needs outside its own code:
//
//   Storage (worker thread, no Unity calls): can the game process see and read its OBB folder
//   (/sdcard/Android/obb/<pkg>) and its external files folder? Unity logs "No permission to read
//   external storage. Skipping OBB loading." when it cannot, and every asset that lives in the OBB
//   (Resources, later scenes) is then missing.
//
//   Meta platform (main thread): Oculus.Platform.PlatformSettings app id (set or empty),
//   Core.IsInitialized(), and a probe around MetaAchievements.InitPlatform when the engine invokes it.
//   Nothing is bypassed, spoofed or changed: entitlement and account checks run exactly as shipped.
#pragma once

#include <string>

namespace ncmk {
namespace diag {

/// Worker thread. Logs OBB / external storage visibility from the game process.
void StorageDiagnostics(const std::string& package);

void MetaPlatformInit();                                   // worker thread: class lookup + probe
void MetaPlatformOnFrame(const char* sceneLoaded, float dt); // main thread
void MetaPlatformRestore();

}  // namespace diag
}  // namespace ncmk
