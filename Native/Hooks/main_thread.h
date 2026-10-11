// Main-thread dispatcher: runs NCMK code on Unity's main thread once per frame.
//
// No inline hook and no code patching. A per-frame managed method is resolved BY NAME and the
// two function-pointer slots at the start of its MethodInfo are swapped for an NCMK entry point
// that calls the original first, then dispatches. See main_thread.cpp for the layout note.
#pragma once

#include <chrono>
#include <string>
#include <vector>

#include "../../SDK/Core/ncmk_mod_api.h"

namespace ncmk {
namespace hooks {

struct Target {
    const char* assembly;
    const char* nameSpace;
    const char* klass;
    const char* method;
    bool isStatic;
    std::string Label() const;
};

/// Tried in order:
///   1. UnityEngine.CoreModule  UnityEngine.UnitySynchronizationContext.ExecuteTasks (static)
///   2. UnityEngine.UI          UnityEngine.EventSystems.EventSystem.Update          (instance)
///   3. Oculus.VR               OVRManager.Update                                    (instance)
const std::vector<Target>& DefaultTargets();

struct Frame {
    int frameCount = -1;           // Time.frameCount (-1 when unavailable)
    float deltaTime = 0.f;         // Time.deltaTime
    float unscaledDeltaTime = 0.f; // Time.unscaledDeltaTime
    int fixedSteps = 0;            // physics steps since the previous dispatched frame
    const char* sceneLoaded = nullptr;  // non-null on the frame the active scene changed
};

using FrameHandler = void (*)(const Frame& frame);

/// Runs on the loader worker thread. Installs the targets one after another; each gets
/// `perTarget` to produce a tick before the next one is also installed. The first target that
/// ticks wins and every other swapped slot is restored. Returns the winning index, or -1 when no
/// target ticked (all slots restored). Logs exactly one "STAGE 09" line either way.
int Install(const std::vector<Target>& targets, FrameHandler handler,
            std::chrono::milliseconds perTarget = std::chrono::milliseconds(5000));

/// The game's original method pointer when `entry` is one of the dispatcher's entry points, else null.
void* OriginalFor(const void* entry);

/// Restores every swapped MethodInfo slot (idempotent). Called on unload.
void Restore();

/// Never installs (Safe Mode, ...): main-thread requests are refused from now on.
void Disable(const char* reason);

bool Active();
bool IsMainThread();

/// Queues fn(user) for the next dispatched frame. False when the dispatcher is not active.
bool Post(NCMKMainThreadFn fn, void* user);

/// Number of dispatched frames so far (for diagnostics/tests).
long DispatchedFrames();

}  // namespace hooks
}  // namespace ncmk
