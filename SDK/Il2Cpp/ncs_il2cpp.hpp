// NightClubSDK - IL2CPP helpers for mods (name-based lookups, no hardcoded offsets).
#pragma once

#include <string>
#include <vector>

#include "../Core/ncs_sdk.hpp"

namespace NCS {
namespace IL2CPP {

inline bool IsReady() { return Host() && Host()->isIl2CppReady(); }

inline std::vector<std::string> GetAssemblies() {
    std::vector<std::string> out;
    if (!IsReady()) return out;
    for (int i = 0; i < Host()->getAssemblyCount(); ++i) out.emplace_back(Host()->getAssemblyName(i));
    return out;
}

inline NCSImage* FindAssembly(const char* name) { return IsReady() ? Host()->findImage(name) : nullptr; }

inline NCSClass* FindClass(const char* assembly, const char* nameSpace, const char* name) {
    return IsReady() ? Host()->findClass(assembly, nameSpace, name) : nullptr;
}

inline NCSMethod* FindMethod(NCSClass* klass, const char* name, int argc) {
    return klass ? Host()->findMethod(klass, name, argc) : nullptr;
}

inline NCSField* FindField(NCSClass* klass, const char* name) { return klass ? Host()->findField(klass, name) : nullptr; }

/// Invokes a method. Returns nullptr and sets *threw when a managed exception was raised.
inline NCSObject* Invoke(NCSMethod* m, void* instance = nullptr, void** args = nullptr, bool* threw = nullptr) {
    if (!m) return nullptr;
    NCSObject* exc = nullptr;
    NCSObject* r = Host()->invoke(m, instance, args, &exc);
    if (threw) *threw = exc != nullptr;
    return exc ? nullptr : r;
}

template <typename T>
inline T GetField(NCSObject* obj, NCSField* f) {
    T v{};
    if (f) Host()->getField(obj, f, &v);
    return v;
}

template <typename T>
inline void SetField(NCSObject* obj, NCSField* f, T v) {
    if (f) Host()->setField(obj, f, &v);
}

template <typename T>
inline T GetStatic(NCSField* f) {
    T v{};
    if (f) Host()->getStaticField(f, &v);
    return v;
}

template <typename T>
inline T Unbox(NCSObject* boxed) {
    return boxed ? *static_cast<T*>(Host()->unbox(boxed)) : T{};
}

inline std::string ToString(NCSString* s) {
    if (!s) return {};
    char buf[1024];
    int n = Host()->stringToUtf8(s, buf, sizeof(buf));
    return n < 0 ? std::string() : std::string(buf, static_cast<size_t>(n));
}

/// Calls a static, parameterless method returning System.String.
inline std::string CallStaticString(const char* assembly, const char* ns, const char* klass, const char* method) {
    auto* m = FindMethod(FindClass(assembly, ns, klass), method, 0);
    bool threw = false;
    auto* r = Invoke(m, nullptr, nullptr, &threw);
    return threw ? std::string() : ToString(r);
}

}  // namespace IL2CPP
}  // namespace NCS
