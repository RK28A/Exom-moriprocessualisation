#include "input_pump.h"

#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncmk_log.h"

namespace ncmk {
namespace input {

namespace IL = NCMK::IL2CPP;

namespace {
enum class Mode { Unknown, NoOvrInput, GameUpdates, Pumping, Failed };

struct State {
    bool resolved = false;
    const MethodInfo* ovrUpdate = nullptr;    // static void OVRInput.Update()
    const MethodInfo* getInstance = nullptr;  // static OVRManager OVRManager.get_instance()
    Mode mode = Mode::Unknown;
    long frame = 0;
};

State& S() {
    static State* s = new State();
    return *s;
}

void SetMode(State& s, Mode m) {
    if (m == s.mode) return;
    s.mode = m;
    switch (m) {
        case Mode::NoOvrInput:
            NCMK_LOGI("[NCMK] Input: OVRInput not found (Oculus.VR) - mods fall back to UnityEngine.XR.InputDevices");
            break;
        case Mode::GameUpdates:
            NCMK_LOGI("[NCMK] Input: OVRManager instance present - OVRInput is updated by the game");
            break;
        case Mode::Pumping:
            NCMK_LOGI("[NCMK] Input: no OVRManager instance - NCMK calls OVRInput.Update() once per frame");
            break;
        case Mode::Failed:
            NCMK_LOGW("[NCMK] Input: OVRInput.Update() threw - pumping stopped (mods fall back to XR InputDevices)");
            break;
        default:
            break;
    }
}
}  // namespace

void PumpFrame() {
    State& s = S();
    if (!s.resolved) {
        s.resolved = true;
        Il2CppClass* input = IL::FindClass("Oculus.VR", "", "OVRInput");
        s.ovrUpdate = IL::FindMethod(input, "Update", 0);
        s.getInstance = IL::FindMethod(IL::FindClass("Oculus.VR", "", "OVRManager"), "get_instance", 0);
        if (!s.ovrUpdate) SetMode(s, Mode::NoOvrInput);
    }
    if (s.mode == Mode::NoOvrInput || s.mode == Mode::Failed) return;

    if (s.frame++ % 72 == 0) {
        Il2CppObject* exc = nullptr;
        Il2CppObject* instance = s.getInstance ? IL::Invoke(s.getInstance, nullptr, nullptr, &exc) : nullptr;
        SetMode(s, instance && !exc ? Mode::GameUpdates : Mode::Pumping);
    }
    if (s.mode != Mode::Pumping) return;
    Il2CppObject* exc = nullptr;
    IL::Invoke(s.ovrUpdate, nullptr, nullptr, &exc);
    if (exc) SetMode(s, Mode::Failed);
}

}  // namespace input
}  // namespace ncmk
