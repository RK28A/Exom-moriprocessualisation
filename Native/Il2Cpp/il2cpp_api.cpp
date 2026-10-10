#include "il2cpp_api.h"

#include <dlfcn.h>

#include <atomic>
#include <cstring>

#include "../Logging/ncs_log.h"

namespace NCS {
namespace IL2CPP {

Api api;

namespace {
void* g_handle = nullptr;
std::atomic<bool> g_ready{false};
thread_local bool t_attached = false;

std::string StripDll(const char* name) {
    std::string s = name ? name : "";
    if (s.size() > 4 && s.compare(s.size() - 4, 4, ".dll") == 0) s.resize(s.size() - 4);
    return s;
}
}  // namespace

void* ResolveExport(const char* name) { return g_handle ? dlsym(g_handle, name) : nullptr; }

bool ResolveExports(void* handle) {
    g_handle = handle;
    bool ok = true;
    int missingOptional = 0;
#define NCS_RESOLVE_REQ(ret, name, args)                                       \
    api.name = reinterpret_cast<ret(*) args>(dlsym(handle, #name));           \
    if (!api.name) {                                                           \
        NCS_LOGE("[NCS] Missing required IL2CPP export: %s", #name);            \
        ok = false;                                                            \
    }
#define NCS_RESOLVE_OPT(ret, name, args)                                       \
    api.name = reinterpret_cast<ret(*) args>(dlsym(handle, #name));           \
    if (!api.name) {                                                           \
        NCS_LOGW("[NCS] Optional IL2CPP export not found: %s", #name);          \
        ++missingOptional;                                                     \
    }
    NCS_IL2CPP_REQUIRED(NCS_RESOLVE_REQ)
    NCS_IL2CPP_OPTIONAL(NCS_RESOLVE_OPT)
#undef NCS_RESOLVE_REQ
#undef NCS_RESOLVE_OPT
    if (ok) NCS_LOGI("[NCS] IL2CPP exports resolved (%d optional missing)", missingOptional);
    return ok;
}

bool RuntimeInitialized() {
    if (!api.il2cpp_get_corlib || !api.il2cpp_get_corlib()) return false;
    Il2CppDomain* domain = api.il2cpp_domain_get();
    if (!domain) return false;
    size_t count = 0;
    api.il2cpp_domain_get_assemblies(domain, &count);
    return count > 0;
}

bool AttachCurrentThread() {
    if (t_attached) return true;
    if (!api.il2cpp_thread_attach || !api.il2cpp_domain_get) return false;
    Il2CppDomain* domain = api.il2cpp_domain_get();
    if (!domain) return false;
    t_attached = api.il2cpp_thread_attach(domain) != nullptr;
    return t_attached;
}

bool IsReady() { return g_ready.load(); }
void SetReady(bool ready) { g_ready.store(ready); }

std::vector<std::string> ListAssemblies() {
    std::vector<std::string> out;
    if (!api.il2cpp_domain_get) return out;
    size_t count = 0;
    const Il2CppAssembly** list = api.il2cpp_domain_get_assemblies(api.il2cpp_domain_get(), &count);
    for (size_t i = 0; list && i < count; ++i) {
        const Il2CppImage* image = api.il2cpp_assembly_get_image(list[i]);
        const char* name = image ? api.il2cpp_image_get_name(image) : nullptr;
        out.emplace_back(name ? StripDll(name) : "?");
    }
    return out;
}

const Il2CppImage* FindAssembly(const char* name) {
    if (!api.il2cpp_domain_get || !name) return nullptr;
    const std::string wanted = StripDll(name);
    size_t count = 0;
    const Il2CppAssembly** list = api.il2cpp_domain_get_assemblies(api.il2cpp_domain_get(), &count);
    for (size_t i = 0; list && i < count; ++i) {
        const Il2CppImage* image = api.il2cpp_assembly_get_image(list[i]);
        if (image && StripDll(api.il2cpp_image_get_name(image)) == wanted) return image;
    }
    return nullptr;
}

Il2CppClass* FindClass(const char* assembly, const char* nameSpace, const char* name) {
    const Il2CppImage* image = FindAssembly(assembly);
    if (!image || !api.il2cpp_class_from_name) return nullptr;
    return api.il2cpp_class_from_name(image, nameSpace ? nameSpace : "", name);
}

const MethodInfo* FindMethod(Il2CppClass* klass, const char* name, int argc) {
    return klass && api.il2cpp_class_get_method_from_name ? api.il2cpp_class_get_method_from_name(klass, name, argc) : nullptr;
}

FieldInfo* FindField(Il2CppClass* klass, const char* name) {
    return klass && api.il2cpp_class_get_field_from_name ? api.il2cpp_class_get_field_from_name(klass, name) : nullptr;
}

Il2CppObject* Invoke(const MethodInfo* method, void* instance, void** args, Il2CppObject** exception) {
    if (exception) *exception = nullptr;
    if (!method || !api.il2cpp_runtime_invoke) return nullptr;
    AttachCurrentThread();
    Il2CppObject* exc = nullptr;
    Il2CppObject* r = api.il2cpp_runtime_invoke(method, instance, args, &exc);
    if (exception) *exception = exc;
    return exc ? nullptr : r;
}

void GetField(Il2CppObject* obj, FieldInfo* field, void* out) {
    if (obj && field && api.il2cpp_field_get_value) api.il2cpp_field_get_value(obj, field, out);
}

void SetField(Il2CppObject* obj, FieldInfo* field, void* value) {
    if (obj && field && api.il2cpp_field_set_value) api.il2cpp_field_set_value(obj, field, value);
}

void GetStaticField(FieldInfo* field, void* out) {
    if (field && api.il2cpp_field_static_get_value) api.il2cpp_field_static_get_value(field, out);
}

void SetStaticField(FieldInfo* field, void* value) {
    if (field && api.il2cpp_field_static_set_value) api.il2cpp_field_static_set_value(field, value);
}

Il2CppString* NewString(const char* utf8) { return api.il2cpp_string_new ? api.il2cpp_string_new(utf8) : nullptr; }

std::string ToUtf8(Il2CppString* str) {
    std::string out;
    if (!str || !api.il2cpp_string_chars || !api.il2cpp_string_length) return out;
    const Il2CppChar* c = api.il2cpp_string_chars(str);
    int32_t len = api.il2cpp_string_length(str);
    for (int32_t i = 0; i < len; ++i) {
        uint32_t cp = c[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < len && c[i + 1] >= 0xDC00 && c[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (c[i + 1] - 0xDC00);
            ++i;
        }
        if (cp < 0x80) {
            out += static_cast<char>(cp);
        } else if (cp < 0x800) {
            out += static_cast<char>(0xC0 | (cp >> 6));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (cp >> 18));
            out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        }
    }
    return out;
}

size_t ClassCount(const Il2CppImage* image) {
    return image && api.il2cpp_image_get_class_count ? api.il2cpp_image_get_class_count(image) : 0;
}

std::string CallStaticString(const char* assembly, const char* ns, const char* klass, const char* method) {
    const MethodInfo* m = FindMethod(FindClass(assembly, ns, klass), method, 0);
    Il2CppObject* exc = nullptr;
    Il2CppObject* r = Invoke(m, nullptr, nullptr, &exc);
    if (exc || !r) return {};
    return ToUtf8(reinterpret_cast<Il2CppString*>(r));
}

}  // namespace IL2CPP
}  // namespace NCS
