// See il2cpp_inspect.h. Compiled with -fexceptions/-funwind-tables: a probe entry sits between
// IL2CPP frames and must let a managed exception (a C++ exception in IL2CPP) pass through it.

#include "il2cpp_inspect.h"

#include <cinttypes>
#include <cstdio>
#include <cstring>

#include "../Logging/ncmk_log.h"

namespace ncmk {
namespace diag {

namespace IL = NCMK::IL2CPP;
using IL::api;

namespace {
// IL2CPP type enum (Il2CppTypeEnum, ECMA-335 element types).
enum : int {
    kVoid = 0x01, kBoolean = 0x02, kChar = 0x03, kI1 = 0x04, kU1 = 0x05, kI2 = 0x06, kU2 = 0x07, kI4 = 0x08,
    kU4 = 0x09, kI8 = 0x0a, kU8 = 0x0b, kR4 = 0x0c, kR8 = 0x0d, kString = 0x0e, kPtr = 0x0f, kByRef = 0x10,
    kValueType = 0x11, kClass = 0x12, kVar = 0x13, kArray = 0x14, kGenericInst = 0x15, kI = 0x18, kU = 0x19,
    kObject = 0x1c, kSzArray = 0x1d, kMVar = 0x1e,
};
constexpr int kFieldStatic = 0x0010;
constexpr int kFieldLiteral = 0x0040;

std::string Hex(const void* p) {
    char b[32];
    snprintf(b, sizeof(b), "0x%" PRIxPTR, reinterpret_cast<uintptr_t>(p));
    return b;
}

Il2CppClass* UnityObjectClass() {
    static Il2CppClass* k = IL::FindClass("UnityEngine.CoreModule", "UnityEngine", "Object");
    return k;
}
}  // namespace

std::string TypeName(const Il2CppType* type) {
    if (!type || !api.il2cpp_type_get_name) return "?";
    char* n = api.il2cpp_type_get_name(type);
    if (!n) return "?";
    std::string s = n;
    if (api.il2cpp_free) api.il2cpp_free(n);
    return s;
}

std::string ClassName(Il2CppClass* klass) {
    if (!klass || !api.il2cpp_class_get_name) return "?";
    const char* ns = api.il2cpp_class_get_namespace ? api.il2cpp_class_get_namespace(klass) : "";
    std::string s = ns && *ns ? std::string(ns) + "." : std::string();
    return s + api.il2cpp_class_get_name(klass);
}

std::vector<Il2CppClass*> FindClassesByPrefix(const char* assembly, const char* prefix) {
    std::vector<Il2CppClass*> out;
    const Il2CppImage* img = IL::FindAssembly(assembly);
    if (!img || !api.il2cpp_image_get_class || !api.il2cpp_class_get_name) return out;
    size_t n = IL::ClassCount(img);
    for (size_t i = 0; i < n; ++i) {
        auto* k = const_cast<Il2CppClass*>(api.il2cpp_image_get_class(img, i));
        const char* name = k ? api.il2cpp_class_get_name(k) : nullptr;
        if (name && strncmp(name, prefix, strlen(prefix)) == 0) out.push_back(k);
    }
    return out;
}

Il2CppClass* FindClassByName(const char* preferredAssembly, const char* name) {
    for (Il2CppClass* k : FindClassesByPrefix(preferredAssembly, name))
        if (strcmp(api.il2cpp_class_get_name(k), name) == 0) return k;
    for (const auto& a : IL::ListAssemblies()) {
        if (a == preferredAssembly) continue;
        for (Il2CppClass* k : FindClassesByPrefix(a.c_str(), name))
            if (strcmp(api.il2cpp_class_get_name(k), name) == 0) return k;
    }
    return nullptr;
}

bool IsReferenceType(const Il2CppType* type) {
    if (!type || !api.il2cpp_type_get_type) return false;
    switch (api.il2cpp_type_get_type(type)) {
        case kString: case kClass: case kArray: case kObject: case kSzArray: return true;
        case kGenericInst: {
            Il2CppClass* k = api.il2cpp_class_from_type ? api.il2cpp_class_from_type(type) : nullptr;
            return k && api.il2cpp_class_is_valuetype && !api.il2cpp_class_is_valuetype(k);
        }
        default: return false;
    }
}

bool IsUnityObjectClass(Il2CppClass* klass) {
    Il2CppClass* uo = UnityObjectClass();
    for (Il2CppClass* k = klass; k && uo; k = api.il2cpp_class_get_parent ? api.il2cpp_class_get_parent(k) : nullptr)
        if (k == uo) return true;
    return false;
}

bool UnityObjectAlive(Il2CppObject* obj) {
    static const MethodInfo* implicitOp = nullptr;
    static bool resolved = false;
    if (!resolved) {
        resolved = true;
        const char* sig[] = {"UnityEngine.Object"};
        implicitOp = IL::FindMethodBySignature(UnityObjectClass(), "op_Implicit", sig, 1);
    }
    if (!obj) return false;
    if (!implicitOp || !api.il2cpp_object_unbox) return true;
    void* args[] = {obj};
    Il2CppObject* exc = nullptr;
    Il2CppObject* r = IL::Invoke(implicitOp, nullptr, args, &exc);
    return !exc && r && *static_cast<bool*>(api.il2cpp_object_unbox(r));
}

std::string ExceptionText(Il2CppObject* exception) {
    if (!exception) return "(none)";
    std::string type = api.il2cpp_object_get_class ? ClassName(api.il2cpp_object_get_class(exception)) : "exception";
    static const MethodInfo* getMessage =
        IL::FindMethod(IL::FindClass("mscorlib", "System", "Exception"), "get_Message", 0);
    std::string msg;
    if (getMessage) {
        Il2CppObject* exc = nullptr;
        Il2CppObject* s = IL::Invoke(getMessage, exception, nullptr, &exc);
        if (!exc && s) msg = IL::ToUtf8(reinterpret_cast<Il2CppString*>(s));
    }
    return msg.empty() ? type : type + ": " + msg;
}

std::string FieldValue(Il2CppObject* obj, FieldInfo* field, bool* isNullReference) {
    if (isNullReference) *isNullReference = false;
    if (!field || !api.il2cpp_field_get_type) return "?";
    const Il2CppType* t = api.il2cpp_field_get_type(field);
    int flags = api.il2cpp_field_get_flags ? api.il2cpp_field_get_flags(field) : 0;
    bool isStatic = (flags & kFieldStatic) != 0;
    if (!isStatic && !obj) return "(instance field)";
    alignas(8) unsigned char buf[16] = {0};
    int kind = api.il2cpp_type_get_type ? api.il2cpp_type_get_type(t) : 0;
    char out[160];

    if (IsReferenceType(t)) {
        Il2CppObject* v = nullptr;
        if (isStatic) IL::GetStaticField(field, &v);
        else IL::GetField(obj, field, &v);
        if (!v) {
            if (isNullReference) *isNullReference = true;
            return "NULL";
        }
        if (kind == kString) {
            std::string str = IL::ToUtf8(reinterpret_cast<Il2CppString*>(v));
            if (str.size() > 80) str = str.substr(0, 80) + "...";
            return "\"" + str + "\"";
        }
        Il2CppClass* k = api.il2cpp_object_get_class ? api.il2cpp_object_get_class(v) : nullptr;
        std::string s = Hex(v) + " (" + ClassName(k) + ")";
        if (k && IsUnityObjectClass(k) && !UnityObjectAlive(v)) {
            if (isNullReference) *isNullReference = true;
            s += " [DESTROYED/MISSING Unity object: == null in C#]";
        }
        return s;
    }

    switch (kind) {
        case kBoolean: case kI1: case kU1: case kI2: case kU2: case kChar: case kI4: case kU4: case kI8: case kU8:
        case kR4: case kR8: case kI: case kU:
            if (isStatic) IL::GetStaticField(field, buf);
            else IL::GetField(obj, field, buf);
            break;
        default:
            return "(value type " + TypeName(t) + ")";
    }
    switch (kind) {
        case kBoolean: return buf[0] ? "true" : "false";
        case kI1: snprintf(out, sizeof(out), "%d", *reinterpret_cast<int8_t*>(buf)); break;
        case kU1: snprintf(out, sizeof(out), "%u", buf[0]); break;
        case kI2: snprintf(out, sizeof(out), "%d", *reinterpret_cast<int16_t*>(buf)); break;
        case kU2: case kChar: snprintf(out, sizeof(out), "%u", *reinterpret_cast<uint16_t*>(buf)); break;
        case kI4: snprintf(out, sizeof(out), "%d", *reinterpret_cast<int32_t*>(buf)); break;
        case kU4: snprintf(out, sizeof(out), "%u", *reinterpret_cast<uint32_t*>(buf)); break;
        case kR4: snprintf(out, sizeof(out), "%g", static_cast<double>(*reinterpret_cast<float*>(buf))); break;
        case kR8: snprintf(out, sizeof(out), "%g", *reinterpret_cast<double*>(buf)); break;
        default: snprintf(out, sizeof(out), "%" PRId64, *reinterpret_cast<int64_t*>(buf)); break;
    }
    return out;
}

void DumpMetadata(const char* tag, Il2CppClass* klass) {
    if (!klass) return;
    const char* ns = api.il2cpp_class_get_namespace ? api.il2cpp_class_get_namespace(klass) : "";
    NCMK_LOGI("%s namespace = '%s', class = %s", tag, ns ? ns : "", api.il2cpp_class_get_name(klass));
    std::string chain;
    for (Il2CppClass* p = api.il2cpp_class_get_parent ? api.il2cpp_class_get_parent(klass) : nullptr; p;
         p = api.il2cpp_class_get_parent(p))
        chain += (chain.empty() ? "" : " <- ") + ClassName(p);
    NCMK_LOGI("%s parents: %s%s", tag, chain.empty() ? "(none)" : chain.c_str(),
              IsUnityObjectClass(klass) ? "  [UnityEngine.Object]" : "");

    if (api.il2cpp_class_get_fields && api.il2cpp_field_get_name) {
        void* it = nullptr;
        int n = 0;
        while (FieldInfo* f = api.il2cpp_class_get_fields(klass, &it)) {
            int flags = api.il2cpp_field_get_flags ? api.il2cpp_field_get_flags(f) : 0;
            const Il2CppType* t = api.il2cpp_field_get_type ? api.il2cpp_field_get_type(f) : nullptr;
            NCMK_LOGI("%s field %-32s %-48s %s%s", tag, api.il2cpp_field_get_name(f), TypeName(t).c_str(),
                      flags & kFieldLiteral ? "const" : flags & kFieldStatic ? "static" : "instance",
                      IsReferenceType(t) ? " (reference)" : "");
            ++n;
        }
        NCMK_LOGI("%s %d field(s)", tag, n);
    } else {
        NCMK_LOGW("%s field enumeration unavailable in this libil2cpp", tag);
    }

    if (api.il2cpp_class_get_properties && api.il2cpp_property_get_name) {
        void* it = nullptr;
        while (const PropertyInfo* p = api.il2cpp_class_get_properties(klass, &it)) {
            const MethodInfo* g = api.il2cpp_property_get_get_method ? api.il2cpp_property_get_get_method(p) : nullptr;
            NCMK_LOGI("%s property %-32s %s%s", tag, api.il2cpp_property_get_name(p),
                      g && api.il2cpp_method_get_return_type ? TypeName(api.il2cpp_method_get_return_type(g)).c_str() : "?",
                      g ? (IL::IsStatic(g) ? " static get" : " get") : " (no getter)");
        }
    }

    if (api.il2cpp_class_get_methods && api.il2cpp_method_get_name) {
        void* it = nullptr;
        while (const MethodInfo* m = api.il2cpp_class_get_methods(klass, &it)) {
            std::string params;
            uint32_t pc = api.il2cpp_method_get_param_count ? api.il2cpp_method_get_param_count(m) : 0;
            for (uint32_t i = 0; i < pc; ++i)
                params += (i ? ", " : "") + TypeName(api.il2cpp_method_get_param ? api.il2cpp_method_get_param(m, i) : nullptr);
            void* slot0 = *reinterpret_cast<void* const*>(m);  // methodPointer (documented slot 0)
            NCMK_LOGI("%s method %s%s %s(%s)  MethodInfo=%s ptr=%s", tag, IL::IsStatic(m) ? "static " : "",
                      api.il2cpp_method_get_return_type ? TypeName(api.il2cpp_method_get_return_type(m)).c_str() : "?",
                      api.il2cpp_method_get_name(m), params.c_str(), Hex(m).c_str(), Hex(slot0).c_str());
        }
    }
}

std::vector<std::string> DumpFieldValues(const char* tag, Il2CppClass* klass, Il2CppObject* obj) {
    std::vector<std::string> nulls;
    if (!klass || !api.il2cpp_class_get_fields || !api.il2cpp_field_get_name) return nulls;
    void* it = nullptr;
    while (FieldInfo* f = api.il2cpp_class_get_fields(klass, &it)) {
        int flags = api.il2cpp_field_get_flags ? api.il2cpp_field_get_flags(f) : 0;
        bool isStatic = (flags & kFieldStatic) != 0;
        if (isStatic == (obj != nullptr)) continue;  // statics without obj, instance fields with obj
        const Il2CppType* t = api.il2cpp_field_get_type ? api.il2cpp_field_get_type(f) : nullptr;
        bool isNull = false;
        std::string v = FieldValue(obj, f, &isNull);
        const char* name = api.il2cpp_field_get_name(f);
        if (isNull) {
            NCMK_LOGW("%s NULL FIELD: %s%s (%s) = %s", tag, isStatic ? "static " : "", name, TypeName(t).c_str(), v.c_str());
            nulls.push_back(name);
        } else {
            NCMK_LOGI("%s field %s%s (%s) = %s", tag, isStatic ? "static " : "", name, TypeName(t).c_str(), v.c_str());
        }
    }
    return nulls;
}

std::vector<Il2CppObject*> FindLoadedObjects(Il2CppClass* klass, size_t max) {
    std::vector<Il2CppObject*> out;
    static const MethodInfo* findAll = nullptr;
    static const MethodInfo* getLength = nullptr;
    static const MethodInfo* getValue = nullptr;
    static bool resolved = false;
    if (!resolved) {
        resolved = true;
        const char* sig[] = {"System.Type"};
        findAll = IL::FindMethodBySignature(IL::FindClass("UnityEngine.CoreModule", "UnityEngine", "Resources"),
                                            "FindObjectsOfTypeAll", sig, 1);
        Il2CppClass* array = IL::FindClass("mscorlib", "System", "Array");
        getLength = IL::FindMethod(array, "get_Length", 0);
        const char* idx[] = {"System.Int32"};
        getValue = IL::FindMethodBySignature(array, "GetValue", idx, 1);
    }
    Il2CppObject* type = IL::TypeOf(klass);
    if (!findAll || !getLength || !getValue || !type || !api.il2cpp_object_unbox) return out;
    void* args[] = {type};
    Il2CppObject* exc = nullptr;
    Il2CppObject* arr = IL::Invoke(findAll, nullptr, args, &exc);
    if (exc || !arr) return out;
    Il2CppObject* len = IL::Invoke(getLength, arr, nullptr, &exc);
    if (exc || !len) return out;
    int32_t n = *static_cast<int32_t*>(api.il2cpp_object_unbox(len));
    for (int32_t i = 0; i < n && out.size() < max; ++i) {
        void* a[] = {&i};
        Il2CppObject* o = IL::Invoke(getValue, arr, a, &exc);
        if (!exc && o) out.push_back(o);
    }
    if (n > static_cast<int32_t>(max)) NCMK_LOGI("[NCMK][Diag] %d loaded %s objects (first %zu inspected)", n, ClassName(klass).c_str(), max);
    return out;
}

// ---------------------------------------------------------------- probes

namespace {
constexpr int kMaxProbes = 8;
struct Probe {
    const MethodInfo* method;
    void* original;
    void* entry;
    bool slot1;
    bool installed;
    bool isStatic;
    ProbeCallback callback;
    char tag[96];
};
Probe g_probes[kMaxProbes];  // static storage: an entry may still run after RestoreProbes()
int g_probeCount = 0;

void** Slots(const MethodInfo* m) { return reinterpret_cast<void**>(const_cast<MethodInfo*>(m)); }

template <int I>
void InstanceEntry(Il2CppObject* self, const MethodInfo* m) {
    Probe& p = g_probes[I];
    if (p.callback) p.callback(I, self, true, false);
    try {
        reinterpret_cast<void (*)(Il2CppObject*, const MethodInfo*)>(p.original)(self, m);
    } catch (...) {
        if (p.callback) p.callback(I, self, false, true);
        throw;  // the managed exception continues exactly as before
    }
    if (p.callback) p.callback(I, self, false, false);
}

template <int I>
void StaticEntry(const MethodInfo* m) {
    Probe& p = g_probes[I];
    if (p.callback) p.callback(I, nullptr, true, false);
    try {
        reinterpret_cast<void (*)(const MethodInfo*)>(p.original)(m);
    } catch (...) {
        if (p.callback) p.callback(I, nullptr, false, true);
        throw;
    }
    if (p.callback) p.callback(I, nullptr, false, false);
}

template <int... I>
struct Entries {
    static void* Instance(int i) {
        static void* const t[] = {reinterpret_cast<void*>(&InstanceEntry<I>)...};
        return t[i];
    }
    static void* Static(int i) {
        static void* const t[] = {reinterpret_cast<void*>(&StaticEntry<I>)...};
        return t[i];
    }
};
using AllEntries = Entries<0, 1, 2, 3, 4, 5, 6, 7>;
}  // namespace

int InstallProbe(const char* tag, const MethodInfo* method, ProbeCallback callback) {
    if (!method) return -1;
    if (g_probeCount >= kMaxProbes) {
        NCMK_LOGW("%s probe not installed: too many probes", tag);
        return -1;
    }
    uint32_t argc = api.il2cpp_method_get_param_count ? api.il2cpp_method_get_param_count(method) : 1;
    std::string ret = api.il2cpp_method_get_return_type ? TypeName(api.il2cpp_method_get_return_type(method)) : "?";
    if (argc != 0 || ret != "System.Void") {
        NCMK_LOGW("%s probe not installed: only void M() is supported (this one returns %s, %u parameter(s))", tag, ret.c_str(), argc);
        return -1;
    }
    void** slots = Slots(method);
    void* slot0 = __atomic_load_n(&slots[0], __ATOMIC_ACQUIRE);
    void* slot1 = __atomic_load_n(&slots[1], __ATOMIC_ACQUIRE);
    if (!slot0) {
        NCMK_LOGW("%s probe not installed: method pointer is null (not compiled / stripped)", tag);
        return -1;
    }
    int i = g_probeCount++;
    Probe& p = g_probes[i];
    p.method = method;
    p.original = slot0;
    p.isStatic = IL::IsStatic(method);
    p.entry = p.isStatic ? AllEntries::Static(i) : AllEntries::Instance(i);
    p.callback = callback;
    snprintf(p.tag, sizeof(p.tag), "%s", tag);
    p.isStatic = IL::IsStatic(method);
    __atomic_store_n(&slots[0], p.entry, __ATOMIC_RELEASE);
    p.slot1 = slot1 == slot0;
    if (p.slot1) __atomic_store_n(&slots[1], p.entry, __ATOMIC_RELEASE);
    p.installed = true;
    NCMK_LOGI("%s probe installed (MethodInfo %s, original %s%s)", tag, Hex(method).c_str(), Hex(slot0).c_str(),
              p.slot1 ? ", slots 0+1" : ", slot 0 only");
    return i;
}

void* ProbeOriginalFor(const void* entry) {
    if (!entry) return nullptr;
    for (int i = 0; i < g_probeCount; ++i)
        if (g_probes[i].entry == entry) return g_probes[i].original;
    return nullptr;
}

const char* ProbeTag(int probeId) {
    return probeId >= 0 && probeId < g_probeCount ? g_probes[probeId].tag : "?";
}

void RestoreProbes() {
    for (int i = 0; i < g_probeCount; ++i) {
        Probe& p = g_probes[i];
        if (!p.installed) continue;
        void** slots = Slots(p.method);
        // Only restore what we wrote (another tool may have changed the slot since).
        void* expected = p.entry;
        __atomic_compare_exchange_n(&slots[0], &expected, p.original, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
        if (p.slot1) {
            expected = p.entry;
            __atomic_compare_exchange_n(&slots[1], &expected, p.original, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
        }
        p.installed = false;
    }
}

}  // namespace diag
}  // namespace ncmk
