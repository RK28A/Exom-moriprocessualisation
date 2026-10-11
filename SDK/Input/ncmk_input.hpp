// NCMK SDK - Quest controller input: OVRInput (Oculus.VR), with a UnityEngine.XR.InputDevices
// fallback.
//
// OVRInput only refreshes when OVRInput.Update() runs. Games normally do that through OVRManager;
// this one drives its hands with HurricaneVR, so the loader calls OVRInput.Update() once per frame
// whenever OVRManager.instance is null. If OVRInput still shows no sign of life during the first
// ~90 frames, or is missing, the SDK switches to UnityEngine.XR.InputDevices. The active source is
// logged once ("NCMK::Input source: ...") and available as NCMK::Input::ActiveSource().
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
    NCMKMethod* isConnected = nullptr; // bool IsControllerConnected(Controller)
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
        o.isConnected = IL2CPP::FindMethod(o.input, "IsControllerConnected", {"OVRInput.Controller"});
        o.ctrl[0] = static_cast<int32_t>(IL2CPP::EnumValue(o.controller, "LTouch", 1));
        o.ctrl[1] = static_cast<int32_t>(IL2CPP::EnumValue(o.controller, "RTouch", 2));
        o.ok = o.input && o.button && o.controller;
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

// ------------------------------------------------------------------ XR fallback (UnityEngine.XR)
// UnityEngine.XR.InputDevices works with any XR provider (Oculus / OpenXR) whether or not the game
// runs an OVRManager. Its static TryGetFeatureValue* methods take a device id and a usage name, so
// everything is called statically and resolved by signature.
struct Xr {
    NCMKMethod* deviceAtNode = nullptr;  // InputDevice GetDeviceAtXRNode(XRNode)
    NCMKField* deviceId = nullptr;       // InputDevice.m_DeviceId (UInt64)
    NCMKMethod* tryBool = nullptr;       // bool TryGetFeatureValue(UInt64, String, ref bool)
    NCMKMethod* tryFloat = nullptr;      // bool TryGetFeatureValue_float(UInt64, String, ref float)
    NCMKMethod* tryVec2 = nullptr;       // bool TryGetFeatureValue_Vector2f(UInt64, String, ref Vector2)
    NCMKMethod* tryVec3 = nullptr;       // bool TryGetFeatureValue_Vector3f(UInt64, String, ref Vector3)
    NCMKMethod* tryQuat = nullptr;       // bool TryGetFeatureValue_Quaternionf(UInt64, String, ref Quaternion)
    int32_t node[2] = {4, 5};            // XRNode.LeftHand / RightHand (re-read by name)
    bool ok = false;
};

inline Xr& X() {
    static Xr x;
    static bool resolved = false;
    if (!resolved && IL2CPP::IsReady()) {
        resolved = true;
        NCMKClass* devices = IL2CPP::FindClass("UnityEngine.XRModule", "UnityEngine.XR", "InputDevices");
        NCMKClass* device = IL2CPP::FindClass("UnityEngine.XRModule", "UnityEngine.XR", "InputDevice");
        NCMKClass* node = IL2CPP::FindClass("UnityEngine.XRModule", "UnityEngine.XR", "XRNode");
        x.deviceAtNode = IL2CPP::FindMethod(devices, "GetDeviceAtXRNode", {"UnityEngine.XR.XRNode"});
        x.deviceId = IL2CPP::FindField(device, "m_DeviceId");
        x.tryBool = IL2CPP::FindMethod(devices, "TryGetFeatureValue", {"System.UInt64", "System.String", "System.Boolean&"});
        x.tryFloat = IL2CPP::FindMethod(devices, "TryGetFeatureValue_float", {"System.UInt64", "System.String", "System.Single&"});
        x.tryVec2 = IL2CPP::FindMethod(devices, "TryGetFeatureValue_Vector2f", {"System.UInt64", "System.String", "UnityEngine.Vector2&"});
        x.tryVec3 = IL2CPP::FindMethod(devices, "TryGetFeatureValue_Vector3f", {"System.UInt64", "System.String", "UnityEngine.Vector3&"});
        x.tryQuat = IL2CPP::FindMethod(devices, "TryGetFeatureValue_Quaternionf",
                                       {"System.UInt64", "System.String", "UnityEngine.Quaternion&"});
        x.node[0] = static_cast<int32_t>(IL2CPP::EnumValue(node, "LeftHand", 4));
        x.node[1] = static_cast<int32_t>(IL2CPP::EnumValue(node, "RightHand", 5));
        x.ok = x.deviceAtNode && x.deviceId && x.tryBool;
    }
    return x;
}

inline uint64_t XrDevice(Hand h) {
    auto& x = X();
    int32_t node = x.node[h == Hand::Left ? 0 : 1];
    void* args[] = {&node};
    NCMKObject* dev = IL2CPP::Call("InputDevices.GetDeviceAtXRNode", x.deviceAtNode, nullptr, args);
    return dev ? IL2CPP::GetField<uint64_t>(dev, x.deviceId) : 0;
}

inline bool XrBool(Hand h, const char* usage) {
    uint64_t id = XrDevice(h);
    bool value = false;
    void* args[] = {&id, IL2CPP::NewString(usage), &value};
    bool found = IL2CPP::Unbox<bool>(IL2CPP::Call("InputDevices.TryGetFeatureValue(bool)", X().tryBool, nullptr, args));
    return found && value;
}

inline float XrFloat(Hand h, const char* usage) {
    uint64_t id = XrDevice(h);
    float value = 0.f;
    void* args[] = {&id, IL2CPP::NewString(usage), &value};
    bool found = IL2CPP::Unbox<bool>(IL2CPP::Call("InputDevices.TryGetFeatureValue_float", X().tryFloat, nullptr, args));
    return found ? value : 0.f;
}

/// ref Vector2/Vector3/Quaternion: the SDK scratch box receives the value, read back by field name.
template <typename T>
inline bool XrStruct(Hand h, const char* what, NCMKMethod* m, const char* usage, T& out) {
    auto& codec = Unity::detail::CodecFor<T>::Get();
    T zero = out;
    void* ref = codec.Arg(reinterpret_cast<const float*>(&zero), 3);
    if (!ref || !m) return false;
    uint64_t id = XrDevice(h);
    void* args[] = {&id, IL2CPP::NewString(usage), ref};
    if (!IL2CPP::Unbox<bool>(IL2CPP::Call(what, m, nullptr, args))) return false;
    return codec.ReadArg(3, reinterpret_cast<float*>(&out));
}

/// Pressed / released edges for the XR source (InputDevices has no "down this frame").
/// Poll every frame for reliable edges.
struct Edge {
    int frame = -1;
    bool cur = false, prev = false;
};

inline const char* XrUsage(Button b) {
    static const char* names[] = {"PrimaryButton", "SecondaryButton", "Primary2DAxisClick", "TriggerButton", "GripButton", "MenuButton"};
    return names[static_cast<int>(b)];
}

inline Edge& XrEdge(Hand h, Button b) {
    static Edge edges[2][6];
    Edge& e = edges[h == Hand::Left ? 0 : 1][static_cast<int>(b)];
    int frame = Unity::Time::FrameCount();
    if (frame != e.frame || frame < 0) {
        e.prev = e.cur;
        e.cur = XrBool(h, XrUsage(b));
        e.frame = frame;
    }
    return e;
}

// ------------------------------------------------------------------ source selection
enum class Source { Undecided, OvrInput, Xr, None };

/// OVRInput is used while it shows signs of life (a controller reported connected, or a pose that
/// moves) during the first ~90 polled frames. If it stays silent (no OVRManager and no pump, or a
/// rig that bypasses OVR) the XR InputDevices fallback takes over. Logged once.
inline Source ActiveSource() {
    static Source source = Source::Undecided;
    static int frames = 0, lastFrame = -1;
    static Vec3 firstPos[2];
    static bool alive = false;
    if (source != Source::Undecided) return source;

    auto& o = O();
    auto decide = [](Source s, const char* why) {
        source = s;
        const char* name = s == Source::OvrInput ? "OVRInput" : s == Source::Xr ? "UnityEngine.XR.InputDevices" : "none";
        Log(s == Source::None ? NCMK_LOG_WARN : NCMK_LOG_INFO, "NCMK::Input source: %s (%s)", name, why);
        return source;
    };
    if (!o.ok) return X().ok ? decide(Source::Xr, "OVRInput not found") : decide(Source::None, "neither OVRInput nor XR InputDevices found");

    int frame = Unity::Time::FrameCount();
    if (frame != lastFrame) {
        lastFrame = frame;
        for (int i = 0; i < 2 && !alive; ++i) {
            int32_t controller = o.ctrl[i];
            void* args[] = {&controller};
            if (o.isConnected && IL2CPP::Unbox<bool>(IL2CPP::Invoke(o.isConnected, nullptr, args))) alive = true;
            Vec3 p = Unity::detail::Read<Vec3>(IL2CPP::Invoke(o.localPos, nullptr, args));
            if (frames == 0) firstPos[i] = p;
            else if (p.x != firstPos[i].x || p.y != firstPos[i].y || p.z != firstPos[i].z) alive = true;
        }
        ++frames;
    }
    if (alive) return decide(Source::OvrInput, "controller state is updating");
    if (frames >= 90)
        return X().ok ? decide(Source::Xr, "OVRInput stayed silent for 90 frames")
                      : decide(Source::OvrInput, "OVRInput silent but no XR InputDevices to fall back to");
    return Source::OvrInput;  // still observing
}

inline bool UseXr() { return ActiveSource() == Source::Xr; }

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

/// Which backend answers: OVRInput or the UnityEngine.XR fallback (decided on the first frames).
using detail::Source;
inline Source ActiveSource() { return detail::ActiveSource(); }

/// True while the button is held.
inline bool IsDown(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    if (detail::UseXr()) return detail::XrEdge(h, b).cur;
    return detail::CallButton("OVRInput.Get(Button)", detail::O().get, h, b);
}
/// True on the frame the button was pressed.
inline bool WasPressed(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    if (detail::UseXr()) {
        auto& e = detail::XrEdge(h, b);
        return e.cur && !e.prev;
    }
    return detail::CallButton("OVRInput.GetDown(Button)", detail::O().getDown, h, b);
}
/// True on the frame the button was released.
inline bool WasReleased(Hand h, Button b) {
    NCMK_REQUIRE_MAIN_THREAD(false);
    if (detail::UseXr()) {
        auto& e = detail::XrEdge(h, b);
        return !e.cur && e.prev;
    }
    return detail::CallButton("OVRInput.GetUp(Button)", detail::O().getUp, h, b);
}

/// Index trigger, 0..1.
inline float Trigger(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    if (detail::UseXr()) return detail::XrFloat(h, "Trigger");
    static int32_t v;
    static bool done;
    return detail::CallAxis1D("PrimaryIndexTrigger", v, done, h);
}
/// Hand (grip) trigger, 0..1.
inline float Grip(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    if (detail::UseXr()) return detail::XrFloat(h, "Grip");
    static int32_t v;
    static bool done;
    return detail::CallAxis1D("PrimaryHandTrigger", v, done, h);
}
/// Thumbstick, -1..1 on both axes.
inline Vec2 Thumbstick(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(Vec2{});
    if (detail::UseXr()) {
        Vec2 v;
        detail::XrStruct(h, "InputDevices.TryGetFeatureValue_Vector2f", detail::X().tryVec2, "Primary2DAxis", v);
        return v;
    }
    auto& o = detail::O();
    if (!o.ok) return {};
    static int32_t axisCache;
    static bool axisDone;
    int32_t axis = detail::AxisValue(o.axis2D, "PrimaryThumbstick", axisCache, axisDone), controller = detail::Ctrl(h);
    void* args[] = {&axis, &controller};
    return Unity::detail::Read<Vec2>(IL2CPP::Call("OVRInput.Get(Axis2D)", o.get2D, nullptr, args));
}

/// Controller pose in tracking space (OVRInput.GetLocalController*, or XR DevicePosition/Rotation).
inline Pose LocalPose(Hand h) {
    NCMK_REQUIRE_MAIN_THREAD(Pose{});
    if (detail::UseXr()) {
        Pose p;
        p.rotation = Quat::Identity();
        bool okPos = detail::XrStruct(h, "InputDevices.TryGetFeatureValue_Vector3f", detail::X().tryVec3, "DevicePosition", p.position);
        bool okRot = detail::XrStruct(h, "InputDevices.TryGetFeatureValue_Quaternionf", detail::X().tryQuat, "DeviceRotation", p.rotation);
        p.valid = okPos && okRot;
        return p;
    }
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
