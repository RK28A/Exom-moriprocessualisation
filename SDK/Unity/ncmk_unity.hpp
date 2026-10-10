// NCMK SDK - Unity helpers built on the IL2CPP layer.
//
// Scene-graph helpers (GameObject, Transform, Camera, Scene...) must run on Unity's main thread:
// call them from NCMKMod_OnUpdate / OnLateUpdate / OnSceneLoaded or from a function queued with
// NCMK::RunOnMainThread. Debug builds check this and log once per helper.
//
// Handles (GameObject, Transform...) are plain pointers to managed objects. Keep one across frames
// with IL2CPP::Pinned, or look it up again each time.
#pragma once

#include <cmath>
#include <string>

#include "../Il2Cpp/ncmk_il2cpp.hpp"

namespace NCMK {
namespace Unity {

// ============================================================================ value types

struct Vec2 {
    float x = 0, y = 0;
    Vec2() = default;
    Vec2(float x_, float y_) : x(x_), y(y_) {}
    Vec2 operator+(Vec2 o) const { return {x + o.x, y + o.y}; }
    Vec2 operator-(Vec2 o) const { return {x - o.x, y - o.y}; }
    Vec2 operator*(float s) const { return {x * s, y * s}; }
};

struct Vec3 {
    float x = 0, y = 0, z = 0;
    Vec3() = default;
    Vec3(float x_, float y_, float z_) : x(x_), y(y_), z(z_) {}
    Vec3 operator+(Vec3 o) const { return {x + o.x, y + o.y, z + o.z}; }
    Vec3 operator-(Vec3 o) const { return {x - o.x, y - o.y, z - o.z}; }
    Vec3 operator*(float s) const { return {x * s, y * s, z * s}; }
    float Dot(Vec3 o) const { return x * o.x + y * o.y + z * o.z; }
    Vec3 Cross(Vec3 o) const { return {y * o.z - z * o.y, z * o.x - x * o.z, x * o.y - y * o.x}; }
    float Length() const { return std::sqrt(Dot(*this)); }
    Vec3 Normalized() const {
        float l = Length();
        return l > 1e-6f ? *this * (1.f / l) : Vec3{};
    }
    static Vec3 Forward() { return {0, 0, 1}; }
    static Vec3 Up() { return {0, 1, 0}; }
    static Vec3 Right() { return {1, 0, 0}; }
};

struct Vec4 {
    float x = 0, y = 0, z = 0, w = 0;
};

struct Quat {
    float x = 0, y = 0, z = 0, w = 1;
    Quat() = default;
    Quat(float x_, float y_, float z_, float w_) : x(x_), y(y_), z(z_), w(w_) {}
    static Quat Identity() { return {}; }
    Quat operator*(Quat q) const {
        return {w * q.x + x * q.w + y * q.z - z * q.y, w * q.y + y * q.w + z * q.x - x * q.z,
                w * q.z + z * q.w + x * q.y - y * q.x, w * q.w - x * q.x - y * q.y - z * q.z};
    }
    Vec3 operator*(Vec3 v) const {
        Vec3 u{x, y, z};
        Vec3 t = u.Cross(v) * 2.f;
        return v + t * w + u.Cross(t);
    }
    /// Unity's convention: Z, then X, then Y (degrees).
    static Quat Euler(float xDeg, float yDeg, float zDeg) {
        const float k = 3.14159265358979f / 360.f;
        float cx = std::cos(xDeg * k), sx = std::sin(xDeg * k);
        float cy = std::cos(yDeg * k), sy = std::sin(yDeg * k);
        float cz = std::cos(zDeg * k), sz = std::sin(zDeg * k);
        Quat qx{sx, 0, 0, cx}, qy{0, sy, 0, cy}, qz{0, 0, sz, cz};
        return qy * qx * qz;
    }
    static Quat LookRotation(Vec3 forward, Vec3 up = Vec3::Up()) {
        Vec3 f = forward.Normalized(), r = up.Cross(f).Normalized(), u = f.Cross(r);
        float trace = r.x + u.y + f.z;
        Quat q;
        if (trace > 0.f) {
            float s = std::sqrt(trace + 1.f) * 2.f;
            q = {(u.z - f.y) / s, (f.x - r.z) / s, (r.y - u.x) / s, 0.25f * s};
        } else if (r.x > u.y && r.x > f.z) {
            float s = std::sqrt(1.f + r.x - u.y - f.z) * 2.f;
            q = {0.25f * s, (u.x + r.y) / s, (f.x + r.z) / s, (u.z - f.y) / s};
        } else if (u.y > f.z) {
            float s = std::sqrt(1.f + u.y - r.x - f.z) * 2.f;
            q = {(u.x + r.y) / s, 0.25f * s, (f.y + u.z) / s, (f.x - r.z) / s};
        } else {
            float s = std::sqrt(1.f + f.z - r.x - u.y) * 2.f;
            q = {(f.x + r.z) / s, (f.y + u.z) / s, 0.25f * s, (r.y - u.x) / s};
        }
        return q;
    }
};

struct Color {
    float r = 1, g = 1, b = 1, a = 1;
    Color() = default;
    Color(float r_, float g_, float b_, float a_ = 1.f) : r(r_), g(g_), b(b_), a(a_) {}
};

struct Rect {
    float x = 0, y = 0, width = 0, height = 0;
};

// ============================================================================ internals

namespace detail {
using IL2CPP::ClassRef;
using IL2CPP::StructCodec;

inline ClassRef& K(const char* name) {
    // One ClassRef per UnityEngine.CoreModule class name (string literal identity).
    struct Entry { const char* name; ClassRef ref; };
    static Entry entries[32];
    static int count = 0;
    for (int i = 0; i < count; ++i)
        if (std::strcmp(entries[i].name, name) == 0) return entries[i].ref;
    if (count == 32) {
        static ClassRef none{"UnityEngine.CoreModule", "UnityEngine", "?"};
        return none;
    }
    const char* ns = (std::strcmp(name, "SceneManager") == 0 || std::strcmp(name, "Scene") == 0) ? "UnityEngine.SceneManagement"
                                                                                                    : "UnityEngine";
    entries[count] = {name, ClassRef{"UnityEngine.CoreModule", ns, name}};
    return entries[count++].ref;
}

inline StructCodec<2>& V2() { static StructCodec<2> c(K("Vector2"), {"x", "y"}); return c; }
inline StructCodec<3>& V3() { static StructCodec<3> c(K("Vector3"), {"x", "y", "z"}); return c; }
inline StructCodec<4>& V4() { static StructCodec<4> c(K("Vector4"), {"x", "y", "z", "w"}); return c; }
inline StructCodec<4>& Q() { static StructCodec<4> c(K("Quaternion"), {"x", "y", "z", "w"}); return c; }
inline StructCodec<4>& C() { static StructCodec<4> c(K("Color"), {"r", "g", "b", "a"}); return c; }
inline StructCodec<4>& R() { static StructCodec<4> c(K("Rect"), {"m_XMin", "m_YMin", "m_Width", "m_Height"}); return c; }

inline void* Arg(const Vec2& v, int slot = 0) { return V2().Arg(&v.x, slot); }
inline void* Arg(const Vec3& v, int slot = 0) { return V3().Arg(&v.x, slot); }
inline void* Arg(const Vec4& v, int slot = 0) { return V4().Arg(&v.x, slot); }
inline void* Arg(const Quat& v, int slot = 0) { return Q().Arg(&v.x, slot); }
inline void* Arg(const Color& v, int slot = 0) { return C().Arg(&v.r, slot); }
inline void* Arg(const Rect& v, int slot = 0) { return R().Arg(&v.x, slot); }

template <typename T> struct CodecFor;
template <> struct CodecFor<Vec2> { static StructCodec<2>& Get() { return V2(); } };
template <> struct CodecFor<Vec3> { static StructCodec<3>& Get() { return V3(); } };
template <> struct CodecFor<Vec4> { static StructCodec<4>& Get() { return V4(); } };
template <> struct CodecFor<Quat> { static StructCodec<4>& Get() { return Q(); } };
template <> struct CodecFor<Color> { static StructCodec<4>& Get() { return C(); } };
template <> struct CodecFor<Rect> { static StructCodec<4>& Get() { return R(); } };

/// Value type from a boxed return value (fields read by name).
template <typename T>
inline T Read(NCMKObject* boxed, T fallback = T{}) {
    T v = fallback;
    if (!CodecFor<T>::Get().Read(boxed, reinterpret_cast<float*>(&v))) return fallback;
    return v;
}

/// Lazily resolved method; resolution is attempted once.
struct MethodRef {
    NCMKMethod* m = nullptr;
    bool tried = false;
    template <typename F>
    NCMKMethod* Get(F resolve) {
        if (!tried && IL2CPP::IsReady()) {
            tried = true;
            m = resolve();
        }
        return m;
    }
};
}  // namespace detail

/// Method of `klass` resolved once per call site (by name + argument count / by parameter types).
#define NCMK_METHOD(klass, name, argc)                                                                    \
    ([](NCMKClass* k_) -> NCMKMethod* {                                                                    \
        static ::NCMK::Unity::detail::MethodRef ref;                                                       \
        return ref.Get([k_] { return ::NCMK::IL2CPP::FindMethod(k_, (name), (argc)); });                   \
    }(klass))
#define NCMK_METHOD_SIG(klass, name, ...)                                                                 \
    ([](NCMKClass* k_) -> NCMKMethod* {                                                                    \
        static ::NCMK::Unity::detail::MethodRef ref;                                                       \
        return ref.Get([k_] { return ::NCMK::IL2CPP::FindMethod(k_, (name), {__VA_ARGS__}); });           \
    }(klass))

// ============================================================================ objects

class Transform;
class GameObject;

/// Any UnityEngine.Object.
class Object {
public:
    Object() = default;
    explicit Object(NCMKObject* p) : ptr_(p) {}
    NCMKObject* Ptr() const { return ptr_; }
    explicit operator bool() const { return ptr_ != nullptr; }

    /// False once Unity destroyed the native object (UnityEngine.Object's bool operator).
    bool IsAlive() const {
        NCMK_REQUIRE_MAIN_THREAD(false);
        if (!ptr_) return false;
        void* args[] = {ptr_};
        return IL2CPP::Unbox<bool>(IL2CPP::Call("Object.op_Implicit",
                                                NCMK_METHOD_SIG(detail::K("Object").Get(), "op_Implicit", "UnityEngine.Object"),
                                                nullptr, args));
    }

    std::string Name() const {
        NCMK_REQUIRE_MAIN_THREAD(std::string());
        return IL2CPP::ToString(IL2CPP::Call("Object.get_name", NCMK_METHOD(detail::K("Object").Get(), "get_name", 0), ptr_));
    }

    void SetName(const char* name) const {
        NCMK_REQUIRE_MAIN_THREAD();
        void* args[] = {IL2CPP::NewString(name)};
        IL2CPP::Call("Object.set_name", NCMK_METHOD_SIG(detail::K("Object").Get(), "set_name", "System.String"), ptr_, args);
    }

    /// UnityEngine.Object.Destroy(obj).
    static void Destroy(Object o) {
        NCMK_REQUIRE_MAIN_THREAD();
        if (!o) return;
        void* args[] = {o.ptr_};
        IL2CPP::Call("Object.Destroy", NCMK_METHOD_SIG(detail::K("Object").Get(), "Destroy", "UnityEngine.Object"), nullptr, args);
    }

    /// UnityEngine.Object.DontDestroyOnLoad(obj): keeps it across scene loads.
    static void DontDestroyOnLoad(Object o) {
        NCMK_REQUIRE_MAIN_THREAD();
        if (!o) return;
        void* args[] = {o.ptr_};
        IL2CPP::Call("Object.DontDestroyOnLoad",
                     NCMK_METHOD_SIG(detail::K("Object").Get(), "DontDestroyOnLoad", "UnityEngine.Object"), nullptr, args);
    }

protected:
    NCMKObject* ptr_ = nullptr;
};

inline void DontDestroyOnLoad(Object o) { Object::DontDestroyOnLoad(o); }

/// UnityEngine.Component (and any MonoBehaviour).
class Component : public Object {
public:
    using Object::Object;
    GameObject gameObject() const;
    Transform transform() const;
};

class Transform : public Component {
public:
    using Component::Component;

#define NCMK_TRANSFORM_PROP(Type, getter, setter, prop, typeName)                                                     \
    Type getter() const {                                                                                           \
        NCMK_REQUIRE_MAIN_THREAD(Type{});                                                                           \
        return detail::Read<Type>(                                                                                  \
            IL2CPP::Call("Transform.get_" prop, NCMK_METHOD(detail::K("Transform").Get(), "get_" prop, 0), ptr_)); \
    }                                                                                                               \
    void setter(const Type& v) const {                                                                              \
        NCMK_REQUIRE_MAIN_THREAD();                                                                                 \
        void* args[] = {detail::Arg(v)};                                                                            \
        if (!args[0]) return;                                                                                       \
        IL2CPP::Call("Transform.set_" prop, NCMK_METHOD_SIG(detail::K("Transform").Get(), "set_" prop, typeName), ptr_, args); \
    }

    NCMK_TRANSFORM_PROP(Vec3, position, SetPosition, "position", "UnityEngine.Vector3")
    NCMK_TRANSFORM_PROP(Vec3, localPosition, SetLocalPosition, "localPosition", "UnityEngine.Vector3")
    NCMK_TRANSFORM_PROP(Quat, rotation, SetRotation, "rotation", "UnityEngine.Quaternion")
    NCMK_TRANSFORM_PROP(Quat, localRotation, SetLocalRotation, "localRotation", "UnityEngine.Quaternion")
    NCMK_TRANSFORM_PROP(Vec3, localScale, SetLocalScale, "localScale", "UnityEngine.Vector3")
#undef NCMK_TRANSFORM_PROP

    /// Alias of localScale (Unity has no settable world scale).
    Vec3 scale() const { return localScale(); }
    void SetScale(const Vec3& v) const { SetLocalScale(v); }

    Vec3 forward() const { return rotation() * Vec3::Forward(); }

    Transform parent() const {
        NCMK_REQUIRE_MAIN_THREAD(Transform());
        return Transform(IL2CPP::Call("Transform.get_parent", NCMK_METHOD(detail::K("Transform").Get(), "get_parent", 0), ptr_));
    }

    /// SetParent(parent, worldPositionStays). A null parent moves the object to the scene root.
    void SetParent(const Transform& parent, bool worldPositionStays = false) const {
        NCMK_REQUIRE_MAIN_THREAD();
        bool stays = worldPositionStays;
        void* args[] = {parent.Ptr(), &stays};
        IL2CPP::Call("Transform.SetParent",
                     NCMK_METHOD_SIG(detail::K("Transform").Get(), "SetParent", "UnityEngine.Transform", "System.Boolean"), ptr_, args);
    }

    /// Local point -> world space.
    Vec3 TransformPoint(const Vec3& local) const {
        NCMK_REQUIRE_MAIN_THREAD(Vec3{});
        void* args[] = {detail::Arg(local)};
        if (!args[0]) return {};
        return detail::Read<Vec3>(IL2CPP::Call(
            "Transform.TransformPoint", NCMK_METHOD_SIG(detail::K("Transform").Get(), "TransformPoint", "UnityEngine.Vector3"), ptr_, args));
    }
};

class GameObject : public Object {
public:
    using Object::Object;

    /// new GameObject(name), active in the current scene.
    static GameObject Create(const char* name) {
        NCMK_REQUIRE_MAIN_THREAD(GameObject());
        NCMKClass* k = detail::K("GameObject").Get();
        NCMKMethod* ctor = NCMK_METHOD_SIG(detail::K("GameObject").Get(), ".ctor", "System.String");
        NCMKObject* obj = ctor ? IL2CPP::NewObject(k) : nullptr;
        if (!obj) {
            FailOnce("GameObject.Create", "GameObject.Create: GameObject(string) not available");
            return GameObject();
        }
        void* args[] = {IL2CPP::NewString(name)};
        bool ok = false;
        IL2CPP::Call("GameObject..ctor(string)", ctor, obj, args, &ok);
        return ok ? GameObject(obj) : GameObject();
    }

    /// GameObject.Find(name): active objects only, "/" separated paths allowed.
    static GameObject Find(const char* name) {
        NCMK_REQUIRE_MAIN_THREAD(GameObject());
        void* args[] = {IL2CPP::NewString(name)};
        return GameObject(IL2CPP::Call("GameObject.Find", NCMK_METHOD_SIG(detail::K("GameObject").Get(), "Find", "System.String"),
                                       nullptr, args));
    }

    void Destroy() const { Object::Destroy(*this); }

    void SetActive(bool active) const {
        NCMK_REQUIRE_MAIN_THREAD();
        void* args[] = {&active};
        IL2CPP::Call("GameObject.SetActive", NCMK_METHOD_SIG(detail::K("GameObject").Get(), "SetActive", "System.Boolean"), ptr_, args);
    }

    bool activeSelf() const {
        NCMK_REQUIRE_MAIN_THREAD(false);
        return IL2CPP::Unbox<bool>(
            IL2CPP::Call("GameObject.get_activeSelf", NCMK_METHOD(detail::K("GameObject").Get(), "get_activeSelf", 0), ptr_));
    }

    Transform transform() const {
        NCMK_REQUIRE_MAIN_THREAD(Transform());
        return Transform(
            IL2CPP::Call("GameObject.get_transform", NCMK_METHOD(detail::K("GameObject").Get(), "get_transform", 0), ptr_));
    }

    /// AddComponent(typeof(klass)); klass from IL2CPP::FindClass / ClassRef.
    Component AddComponent(NCMKClass* klass) const {
        NCMK_REQUIRE_MAIN_THREAD(Component());
        NCMKObject* type = IL2CPP::TypeOf(klass);
        if (!type) return Component();
        void* args[] = {type};
        return Component(IL2CPP::Call("GameObject.AddComponent",
                                      NCMK_METHOD_SIG(detail::K("GameObject").Get(), "AddComponent", "System.Type"), ptr_, args));
    }

    Component GetComponent(NCMKClass* klass) const {
        NCMK_REQUIRE_MAIN_THREAD(Component());
        NCMKObject* type = IL2CPP::TypeOf(klass);
        if (!type) return Component();
        void* args[] = {type};
        return Component(IL2CPP::Call("GameObject.GetComponent",
                                      NCMK_METHOD_SIG(detail::K("GameObject").Get(), "GetComponent", "System.Type"), ptr_, args));
    }

    /// GetComponent, or AddComponent when missing.
    Component GetOrAddComponent(NCMKClass* klass) const {
        Component c = GetComponent(klass);
        return c ? c : AddComponent(klass);
    }
};

inline GameObject Component::gameObject() const {
    NCMK_REQUIRE_MAIN_THREAD(GameObject());
    return GameObject(IL2CPP::Call("Component.get_gameObject", NCMK_METHOD(detail::K("Component").Get(), "get_gameObject", 0), ptr_));
}

inline Transform Component::transform() const {
    NCMK_REQUIRE_MAIN_THREAD(Transform());
    return Transform(IL2CPP::Call("Component.get_transform", NCMK_METHOD(detail::K("Component").Get(), "get_transform", 0), ptr_));
}

class Camera : public Component {
public:
    using Component::Component;
    /// Camera.main (first enabled camera tagged MainCamera), or an empty handle.
    static Camera Main() {
        NCMK_REQUIRE_MAIN_THREAD(Camera());
        return Camera(IL2CPP::Call("Camera.get_main", NCMK_METHOD(detail::K("Camera").Get(), "get_main", 0), nullptr));
    }
};

// ============================================================================ time / scene

namespace Time {
namespace detail_ {
template <typename T>
inline T Get(const char* getter, NCMKMethod* m, T fallback) {
    NCMKObject* r = IL2CPP::Call(getter, m);
    return r ? IL2CPP::Unbox<T>(r) : fallback;
}
}  // namespace detail_
/// Time.deltaTime (scaled seconds since last frame).
inline float Delta() {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    return detail_::Get<float>("Time.get_deltaTime", NCMK_METHOD(detail::K("Time").Get(), "get_deltaTime", 0), 0.f);
}
/// Time.unscaledDeltaTime.
inline float Unscaled() {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    return detail_::Get<float>("Time.get_unscaledDeltaTime", NCMK_METHOD(detail::K("Time").Get(), "get_unscaledDeltaTime", 0), 0.f);
}
/// Time.time (scaled seconds since start).
inline float Now() {
    NCMK_REQUIRE_MAIN_THREAD(0.f);
    return detail_::Get<float>("Time.get_time", NCMK_METHOD(detail::K("Time").Get(), "get_time", 0), 0.f);
}
/// Time.frameCount, -1 when unavailable. Also usable off the main thread (thread-safe getter).
inline int FrameCount() {
    return detail_::Get<int32_t>("Time.get_frameCount", NCMK_METHOD(detail::K("Time").Get(), "get_frameCount", 0), -1);
}
}  // namespace Time

namespace Scene {
/// Name of the active scene ("" when unavailable).
inline std::string CurrentName() {
    NCMK_REQUIRE_MAIN_THREAD(std::string());
    NCMKObject* boxed = IL2CPP::Call("SceneManager.GetActiveScene", NCMK_METHOD(detail::K("SceneManager").Get(), "GetActiveScene", 0));
    static NCMKField* handleField = nullptr;
    if (!handleField) handleField = IL2CPP::FindField(detail::K("Scene").Get(), "m_Handle");
    if (!boxed || !handleField) return {};
    int32_t handle = IL2CPP::GetField<int32_t>(boxed, handleField);
    void* args[] = {&handle};
    return IL2CPP::ToString(IL2CPP::Call("Scene.GetNameInternal",
                                         NCMK_METHOD_SIG(detail::K("Scene").Get(), "GetNameInternal", "System.Int32"), nullptr, args));
}
}  // namespace Scene

// ============================================================================ API 1 helpers (kept)

inline std::string GetUnityVersion() {
    return IL2CPP::CallStaticString("UnityEngine.CoreModule", "UnityEngine", "Application", "get_unityVersion");
}

inline std::string GetApplicationVersion() {
    return IL2CPP::CallStaticString("UnityEngine.CoreModule", "UnityEngine", "Application", "get_version");
}

inline int GetFrameCount() { return Time::FrameCount(); }

/// Deprecated alias of Scene::CurrentName() (main thread).
inline std::string GetCurrentSceneName() { return Scene::CurrentName(); }

}  // namespace Unity

namespace Game {
inline std::string GetCurrentScene() { return Unity::Scene::CurrentName(); }
}  // namespace Game

}  // namespace NCMK
