#pragma once

namespace ncmk {
namespace input {

/// Unity main thread, once per dispatched frame (before the mods' callbacks).
///
/// OVRInput only refreshes its controller state when OVRInput.Update() runs, which normally
/// happens inside OVRManager.Update(). This game drives its hands with HurricaneVR, so an
/// OVRManager instance is not guaranteed. When OVRInput exists but OVRManager.instance is null,
/// the loader calls OVRInput.Update() itself, once per frame. OVRManager is re-checked every
/// 72 frames, so pumping stops as soon as the game creates one (never double-updates).
void PumpFrame();

}  // namespace input
}  // namespace ncmk
