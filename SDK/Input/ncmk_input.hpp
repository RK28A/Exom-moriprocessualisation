// NCMK SDK - Quest controller input, built on OVRInput (Oculus.VR assembly).
//
// Every OVRInput method is resolved by name AND parameter types (OVRInput has many overloads of
// Get/GetDown/GetUp), and enum values (Button.One, Controller.RTouch...) by member name.
// Main thread only. Poll from NCMKMod_OnUpdate.
//
// World-space pose: OVRInput reports controller poses in tracking space. The tracking-space
// transform is OVRCameraRig.trackingSpace when the scene has an OVRCameraRig, otherwise the parent
// of Camera.main (the usual XR rig layout). Needs on-device verification for this game's
// HurricaneVR rig.
#pragma once

#include "../Unity/ncmk_unity.hpp"

namespace NCMK {
namespace Input {

using Unity::Quat;
using Unity::Vec2;
using Unity::Vec3;

enum class Hand { Left, Right };

enum class Button {
    Primary,     // A (right) / X (left)      OVRInput.Button.One
    Secondary,   // B (right) / Y (left)      OVRInput.Button.Two
    Thumbstick,  // thumbstick click          OVRInput.Button.PrimaryThumbstick
    Trigger,     // index trigger as button   OVRInput.Button.PrimaryIndexTrigger
    Grip,        // hand trigger as button    OVRInput.Button.PrimaryHandTrigger
    Menu,        // left menu button          OVRInput.Button.Start
};

struct Pose {
    Vec3 position;
    Quat rotation;
    bool valid = false;
};

namespace detail {
struct Ovr {
    NCMKClass* input = nullptr;
    NCMKClass* button = nullptr;
    NCMKClass* controller = nullptr;
    NCMKClass* axis1D = nullptr;
    NCMKClass* axis2D = nullptr;
    NCMKMethod* get = nullptr;      // bool  Get(Button, Controller)
    NCMKMethod* getDown = nullptr;  // bool  GetDown(Button, Controller)
    NCMKMethod* getUp = nullptr;    // bool  GetUp(Button, Controller)
    NCMKMethod* get1D = nullptr;    // float Get(Axis1D, Controller)
    NCMKMethod* get2D = nullptr;    // Vector2 Get(Axis2D, Controller)
    NCMKMethod* localPos = nullptr; // Vector3 GetLocalControllerPosition(Controller)
    NCMKMethod* localRot = nullptr; // Quaternion GetLocalControllerRotation(Controller)
    int32_t ctrl[2] = {1, 2};       // LTouch, RTouch (re-read by name)
    bool ok = false;
};

inline Ovr& O() {
    static Ovr o;
    static bool resolved = false;
    if (!resolved && IL2CPP::IsReady()) {
        resolved = true;
        o.input = IL2CPP::FindClass("Oculus.VR", "", "OVRInput");
        o.button = IL2CPP::FindNestedClass(o.input, "Button");
        o.controller = IL2CPP::FindNestedClass(o.input, "Controller");
        o.axis1D = IL2CPP::FindNestedClass(o.input, "Axis1D");
        o.axis2D = IL2CPP::FindNestedClass(o.input, "Axis2D");
        o.get = IL2CPP::FindMethod(o.input, "Get", {"OVRInput.Button", "OVRInput.Controller"});
        o.getDown = IL2CPP::FindMethod(o.input, "GetDown", {"OVRInput.Button", "OVRInput.Controller"});
        o.getUp = IL2CPP::FindMethod(o.input, "GetUp", {"OVRInput.Button", "OVRInput.Controller"});
        o.get1D = IL2CPP::FindMethod(o.input, "Get", {"OVRInput.Axis1D", "OVRInput.Controller"});
        o.get2D = IL2CPP::FindMethod(o.input, "Get", {"OVRInput.Axis2D", "OVRInput.Controller"});
        o.localPos = IL2CPP::FindMethod(o.input, "GetLocalControllerPosition", {"OVRInput.Controller"});
        o.localRot = IL2CPP::FindMethod(o.input, "GetLocalControllerRotation", {"OVRInput.Controller"});
        o.ctrl[0] = static_cast<int32_t>(IL2CPP::EnumValue(o.controller, "LTouch", 1));
        o.ctrl[1] = static_cast<int32_t>(IL2CPP::EnumValue(o.controller, "RTouch", 2));
        o.ok = o.input && o.button && o.controller;
        if (!o.ok) FailOnce("NCMK::Input", "NCMK::Input: OVRInput not found (Oculus.VR) - controller input disabled");
    }
    return o;
}

inline int32_t Ctrl(Hand h) { return O().ctrl[h == Hand::Left ? 0 : 1]; }

inline int32_t ButtonValue(Button b) {
    static int32_t cache[6];
    static bool done[6];
    const char* names[] = {"One", "Two", "PrimaryThumbstick", "PrimaryIndexTrigger", "PrimaryHandTrigger", "Start"};
    int i = static_cast<int>(b);
    if (!done[i]) {
        done[i] = true;
        cache[i] = static_cast<int32_t>(IL2CPP::EnumValue(O().button, names[i], 0));
    }
    return cache[i];
}

inline int32_t AxisValue(NCMKClass* axisEnum, const char* name, int32_t& cache, bool& done) {
    if (!done) {
        done = true;
        cache = static_cast<int32_t>(IL2CPP::EnumValue(axisEnum, name, 0));
    }
    return cache;
}

inline bool CallButton(const char* what, NCMKMethod* m, Hand h, Button b) {
    if (!O().ok) return false;
    int32_t button = ButtonValue(b), controller = Ctrl(h);
    void* args[] = {&button, &controller};
    return IL2CPP::Unbox<bool>(IL2CPP::Call(what, m, nullptr, args));
}

inline float CallAxis1D(const char* name, int32_t& cache, bool& done, Hand h) {
    if (!O().ok) return 0.f;
    int32_t axis = AxisValue(O().axis1D, name, cache, done), controller = Ctrl(h);
    void* args[] = {&axis, &controller};
    return IL2CPP::Unbox<float>(IL2CPP::Call("OVRInput.Get(Axis1D)", O().get1D, nullptr, args));
}

inline Unity::Transform FindTrackingSpace() {
    static IL2CPP::ClassRef rigClass{"Oculus.VR", "", "OVRCameraRig"};
    NCMKClass* rig = rigClass.Get();
    if (rig) {
        NCMKMethod* find = NCMK_METHOD_SIG(Unity::detail::K("Object").Get(), "FindFirstObjectByType", "System.Type");
        if (!find) find = NCMK_METHOD_SIG(Unity::detail::K("Object").Get(), "FindObjectOfType", "System.Type");
        void* args[] = {IL2CPP::TypeOf(rig)};
        NCMKObject* inst = args[0] ? IL2CPP::Call("Object.FindObjectOfType(OVRCameraRig)", find, nullptr, args) : nullptr;
        if (inst) {
            NCMKObject* ts = IL2CPP::Call("OVRCameraRig.get_trackingSpace", NCMK_METHOD(rig, "get_trackingSpace", 0), inst);
            if (ts) return Unity::Transform(ts);
        }
    }
    Unity::Camera cam = Unity::Camera::Main();
    return cam ? cam.transform().parent() : Unity::Transform();
}

/// Tracking-space transform: OVRCameraRig.trackingSpace, else Camera.main's parent. Looked up at
/// most once per frame (the rig can change with the scene).
inline Unity::Transform TrackingSpace() {
    static int frame = -2;
    static NCMKObject* cached = nullptr;
    int now = Unity::Time::FrameCount();
    if (now != frame || now < 0) {
        frame = now;
        cached = FindTrackingSpace().Ptr();
    }
    return Unity::Transform(cached);
}
}  // namespace detail

/// True while the button is held.
inline bool IsDown(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    return detail::CallButton("OVRInput.Get(Button)", detail::O().get, h, b);
}
/// True on the frame the button was pressed.
inline bool WasPressed(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    return detail::CallButton("OVRInput.GetDown(Button)", detail::O().getDown, h, b);
}
/// True on the frame the button was released.
inline bool WasReleased(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    return detail::CallButton("OVRInput.GetUp(Button)", detail::O().getUp, h, b);
}

/// Index trigger, 0..1.
inline float Trigger(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    static int32_t v;
    static bool done;
    return detail::CallAxis1D("PrimaryIndexTrigger", v, done, h);
}
/// Hand (grip) trigger, 0..1.
inline float Grip(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    static int32_t v;
    static bool done;
    return detail::CallAxis1D("PrimaryHandTrigger", v, done, h);
}
/// Thumbstick, -1..1 on both axes.
inline Vec2 Thumbstick(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(Vec2{});
    auto& o = detail::O();
    if (!o.ok) return {};
    static int32_t axisCache;
    static bool axisDone;
    int32_t axis = detail::AxisValue(o.axis2D, "PrimaryThumbstick", axisCache, axisDone), controller = detail::Ctrl(h);
    void* args[] = {&axis, &controller};
    return Unity::detail::Read<Vec2>(IL2CPP::Call("OVRInput.Get(Axis2D)", o.get2D, nullptr, args));
}

/// Controller pose in tracking space (OVRInput.GetLocalController*).
inline Pose LocalPose(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(Pose{});
    auto& o = detail::O();
    if (!o.ok) return {};
    int32_t controller = detail::Ctrl(h);
    void* args[] = {&controller};
    bool okPos = false, okRot = false;
    NCMKObject* p = IL2CPP::Call("OVRInput.GetLocalControllerPosition", o.localPos, nullptr, args, &okPos);
    Vec3 pos = Unity::detail::Read<Vec3>(p);
    NCMKObject* r = IL2CPP::Call("OVRInput.GetLocalControllerRotation", o.localRot, nullptr, args, &okRot);
    Quat rot = Unity::detail::Read<Quat>(r, Quat::Identity());
    return {pos, rot, okPos && okRot};
}

/// Controller pose in world space (tracking-space pose transformed by the rig).
inline Pose WorldPose(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(Pose{});
    Pose local = LocalPose(h);
    if (!local.valid) return local;
    Unity::Transform space = detail::TrackingSpace();
    if (!space) {
        FailOnce("NCMK::Input::WorldPose", "NCMK::Input::WorldPose: no tracking space found - returning the tracking-space pose");
        return local;
    }
    return {space.TransformPoint(local.position), space.rotation() * local.rotation, true};
}

}  // namespace Input
}  // namespace NCMK
