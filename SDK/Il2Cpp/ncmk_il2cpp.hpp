// NCMK SDK - IL2CPP helpers for mods. Name-based lookups only, no hardcoded offsets: struct
// fields are read and written by name through il2cpp, enums by member name, overloads by
// parameter type names.
#pragma once

#include <cstdint>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

#include "../Core/ncmk_sdk.hpp"

namespace NCMK {
namespace IL2CPP {

inline bool IsReady() { return Host() && Host()->isIl2CppReady(); }

inline std::vector<std::string> GetAssemblies() {
    std::vector<std::string> out;
    if (!IsReady()) return out;
    for (int i = 0; i < Host()->getAssemblyCount(); ++i) out.emplace_back(Host()->getAssemblyName(i));
    return out;
}

inline NCMKImage* FindAssembly(const char* name) { return IsReady() ? Host()->findImage(name) : nullptr; }

inline NCMKClass* FindClass(const char* assembly, const char* nameSpace, const char* name) {
    return IsReady() ? Host()->findClass(assembly, nameSpace, name) : nullptr;
}

inline NCMKMethod* FindMethod(NCMKClass* klass, const char* name, int argc) {
    return klass ? Host()->findMethod(klass, name, argc) : nullptr;
}

/// Overload-safe lookup by parameter type names: FindMethod(k, "Get", {"OVRInput.Button", "OVRInput.Controller"}).
inline NCMKMethod* FindMethod(NCMKClass* klass, const char* name, std::initializer_list<const char*> paramTypes) {
    if (!klass || !HasApi2()) return nullptr;
    std::vector<const char*> v(paramTypes);
    return Host()->findMethodBySignature(klass, name, v.data(), static_cast<int>(v.size()));
}

inline NCMKClass* FindNestedClass(NCMKClass* outer, const char* name) {
    return outer && HasApi2() ? Host()->findNestedClass(outer, name) : nullptr;
}

inline NCMKField* FindField(NCMKClass* klass, const char* name) { return klass ? Host()->findField(klass, name) : nullptr; }

/// Enum member value by name (fallback when the class or member is missing).
inline int64_t EnumValue(NCMKClass* enumClass, const char* name, int64_t fallback = -1) {
    return enumClass && HasApi2() ? Host()->getEnumValue(enumClass, name, fallback) : fallback;
}

inline NCMKObject* TypeOf(NCMKClass* klass) { return klass && HasApi2() ? Host()->typeOf(klass) : nullptr; }
inline NCMKObject* NewObject(NCMKClass* klass) { return klass && HasApi2() ? Host()->newObject(klass) : nullptr; }

/// Invokes a method. Returns nullptr and sets *threw when a managed exception was raised.
inline NCMKObject* Invoke(NCMKMethod* m, void* instance = nullptr, void** args = nullptr, bool* threw = nullptr) {
    if (threw) *threw = false;
    if (!m) return nullptr;
    NCMKObject* exc = nullptr;
    NCMKObject* r = Host()->invoke(m, instance, args, &exc);
    if (threw) *threw = exc != nullptr;
    return exc ? nullptr : r;
}

/// Invoke that logs (once per `what`) a missing method or a managed exception.
inline NCMKObject* Call(const char* what, NCMKMethod* m, void* instance = nullptr, void** args = nullptr, bool* ok = nullptr) {
    if (ok) *ok = false;
    if (!m) {
        FailOnce(what, "%s: method not found in this game build", what);
        return nullptr;
    }
    bool threw = false;
    NCMKObject* r = Invoke(m, instance, args, &threw);
    if (threw) {
        FailOnce(what, "%s threw a managed exception", what);
        return nullptr;
    }
    if (ok) *ok = true;
    return r;
}

template <typename T>
inline T GetField(NCMKObject* obj, NCMKField* f) {
    T v{};
    if (obj && f) Host()->getField(obj, f, &v);
    return v;
}

template <typename T>
inline void SetField(NCMKObject* obj, NCMKField* f, T v) {
    if (obj && f) Host()->setField(obj, f, &v);
}

template <typename T>
inline T GetStatic(NCMKField* f) {
    T v{};
    if (f) Host()->getStaticField(f, &v);
    return v;
}

/// Primitive (int, float, bool...) from a boxed return value.
template <typename T>
inline T Unbox(NCMKObject* boxed, T fallback = T{}) {
    if (!boxed) return fallback;
    T v{};
    std::memcpy(&v, Host()->unbox(boxed), sizeof(T));
    return v;
}

inline std::string ToString(NCMKString* s) {
    if (!s) return {};
    std::vector<char> buf(4096);
    int n = Host()->stringToUtf8(s, buf.data(), static_cast<int>(buf.size()));
    return n < 0 ? std::string() : std::string(buf.data(), static_cast<size_t>(n));
}

inline NCMKString* NewString(const char* utf8) { return Host()->newString(utf8 ? utf8 : ""); }

/// Calls a static, parameterless method returning System.String.
inline std::string CallStaticString(const char* assembly, const char* ns, const char* klass, const char* method) {
    auto* m = FindMethod(FindClass(assembly, ns, klass), method, 0);
    bool threw = false;
    auto* r = Invoke(m, nullptr, nullptr, &threw);
    return threw ? std::string() : ToString(r);
}

/// Raw il2cpp export (cached per name by the caller): Export<Il2CppObject*(*)(void*, uintptr_t)>("il2cpp_array_new").
template <typename Fn>
inline Fn Export(const char* name) {
    return Host() ? reinterpret_cast<Fn>(Host()->il2cppResolve(name)) : nullptr;
}

/// Keeps a managed object alive while native code stores it across frames (GC handle, pinned).
/// Move-only; releases the handle when destroyed. Reset() long-lived ones in NCMKMod_Unload rather
/// than relying on static destructors at process exit.
class Pinned {
public:
    Pinned() = default;
    explicit Pinned(NCMKObject* obj) : obj_(obj), handle_(obj && HasApi2() ? Host()->pin(obj) : 0) {}
    Pinned(const Pinned&) = delete;
    Pinned& operator=(const Pinned&) = delete;
    Pinned(Pinned&& o) noexcept : obj_(o.obj_), handle_(o.handle_) { o.obj_ = nullptr; o.handle_ = 0; }
    Pinned& operator=(Pinned&& o) noexcept {
        if (this != &o) {
            Reset();
            std::swap(obj_, o.obj_);
            std::swap(handle_, o.handle_);
        }
        return *this;
    }
    ~Pinned() { Reset(); }
    void Reset() {
        if (handle_ && Host()) Host()->unpin(handle_);
        handle_ = 0;
        obj_ = nullptr;
    }
    NCMKObject* Get() const { return obj_; }
    explicit operator bool() const { return obj_ != nullptr; }

private:
    NCMKObject* obj_ = nullptr;
    uint32_t handle_ = 0;
};

/// Lazily resolved class: static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "Transform"}; k.Get().
struct ClassRef {
    const char* assembly;
    const char* nameSpace;
    const char* name;
    NCMKClass* cached = nullptr;
    bool tried = false;
    NCMKClass* Get() {
        if (!tried && IsReady()) {
            tried = true;
            cached = FindClass(assembly, nameSpace, name);
            if (!cached) FailOnce((std::string("class:") + nameSpace + "." + name).c_str(), "class %s.%s not found in %s", nameSpace, name, assembly);
        }
        return cached;
    }
};

/// Value-type marshalling by FIELD NAME. Arguments are written into a scratch boxed instance
/// (il2cpp_object_new + il2cpp_field_set_value) and passed as its unboxed pointer; return values
/// are read back with il2cpp_field_get_value. Field offsets always come from il2cpp.
/// Main thread only (the scratch boxes are shared).
template <int N>
class StructCodec {
public:
    StructCodec(ClassRef klass, std::initializer_list<const char*> fields) : klass_(klass) {
        int i = 0;
        for (const char* f : fields)
            if (i < N) names_[i++] = f;
    }

    bool Ready() {
        if (!resolved_) {
            resolved_ = true;
            NCMKClass* k = klass_.Get();
            ok_ = k != nullptr && HasApi2();
            for (int i = 0; i < N && ok_; ++i) ok_ = (fields_[i] = FindField(k, names_[i])) != nullptr;
            if (!ok_) FailOnce((std::string("struct:") + klass_.name).c_str(), "value type %s: fields not found, helpers using it are disabled", klass_.name);
        }
        return ok_;
    }

    /// Writes `values` into scratch box `slot` (0..3) and returns the pointer to pass in args[].
    void* Arg(const float* values, int slot = 0) {
        if (!Ready()) return nullptr;
        NCMKObject* box = Scratch(slot & 3);
        if (!box) return nullptr;
        for (int i = 0; i < N; ++i) Host()->setField(box, fields_[i], const_cast<float*>(&values[i]));
        return Host()->unbox(box);
    }

    /// Reads a boxed return value; false when null or unresolved.
    bool Read(NCMKObject* boxed, float* out) {
        if (!boxed || !Ready()) return false;
        for (int i = 0; i < N; ++i) Host()->getField(boxed, fields_[i], &out[i]);
        return true;
    }

private:
    // Pinned once and intentionally never released (a few bytes per value type): releasing GC
    // handles from static destructors during process exit is not safe.
    NCMKObject* Scratch(int slot) {
        if (!scratch_[slot]) {
            scratch_[slot] = NewObject(klass_.Get());
            if (scratch_[slot]) Host()->pin(scratch_[slot]);
        }
        return scratch_[slot];
    }

    ClassRef klass_;
    const char* names_[N] = {};
    NCMKField* fields_[N] = {};
    NCMKObject* scratch_[4] = {};
    bool resolved_ = false;
    bool ok_ = false;
};

/// System.Byte[] filled from native memory with Marshal.Copy(IntPtr, byte[], int, int).
/// No array-layout assumption: il2cpp_array_new allocates, Marshal.Copy (by signature) fills.
inline NCMKObject* NewByteArray(const void* data, int length) {
    static auto arrayNew = Export<NCMKObject* (*)(NCMKClass*, uintptr_t)>("il2cpp_array_new");
    static ClassRef byteClass{"mscorlib", "System", "Byte"};
    static ClassRef marshal{"mscorlib", "System.Runtime.InteropServices", "Marshal"};
    static NCMKMethod* copy = nullptr;
    if (!copy) copy = FindMethod(marshal.Get(), "Copy", {"System.IntPtr", "System.Byte[]", "System.Int32", "System.Int32"});
    if (!arrayNew || !byteClass.Get() || length < 0) {
        FailOnce("NewByteArray", "NewByteArray: il2cpp_array_new or System.Byte unavailable");
        return nullptr;
    }
    NCMKObject* arr = arrayNew(byteClass.Get(), static_cast<uintptr_t>(length));
    if (!arr || length == 0) return arr;
    intptr_t src = reinterpret_cast<intptr_t>(data);
    int32_t start = 0, len = length;
    void* args[] = {&src, arr, &start, &len};
    bool ok = false;
    Call("Marshal.Copy(IntPtr, byte[], int, int)", copy, nullptr, args, &ok);
    return ok ? arr : nullptr;
}

}  // namespace IL2CPP
}  // namespace NCMK
