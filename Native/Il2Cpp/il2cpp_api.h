#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

// Opaque IL2CPP runtime types.
struct Il2CppDomain;
struct Il2CppAssembly;
struct Il2CppImage;
struct Il2CppClass;
struct MethodInfo;
struct FieldInfo;
struct Il2CppObject;
struct Il2CppString;
struct Il2CppThread;
typedef uint16_t Il2CppChar;

// Exported IL2CPP API, resolved with dlsym (never by offset).
#define NCS_IL2CPP_REQUIRED(X)                                                                         \
    X(Il2CppDomain*, il2cpp_domain_get, (void))                                                        \
    X(const Il2CppAssembly**, il2cpp_domain_get_assemblies, (const Il2CppDomain*, size_t*))           \
    X(const Il2CppImage*, il2cpp_assembly_get_image, (const Il2CppAssembly*))                          \
    X(const char*, il2cpp_image_get_name, (const Il2CppImage*))                                        \
    X(Il2CppClass*, il2cpp_class_from_name, (const Il2CppImage*, const char*, const char*))           \
    X(const MethodInfo*, il2cpp_class_get_method_from_name, (Il2CppClass*, const char*, int))         \
    X(Il2CppObject*, il2cpp_runtime_invoke, (const MethodInfo*, void*, void**, Il2CppObject**))       \
    X(Il2CppThread*, il2cpp_thread_attach, (Il2CppDomain*))                                            \
    X(const Il2CppImage*, il2cpp_get_corlib, (void))

#define NCS_IL2CPP_OPTIONAL(X)                                                                         \
    X(FieldInfo*, il2cpp_class_get_field_from_name, (Il2CppClass*, const char*))                      \
    X(FieldInfo*, il2cpp_class_get_fields, (Il2CppClass*, void**))                                    \
    X(const char*, il2cpp_field_get_name, (FieldInfo*))                                               \
    X(void, il2cpp_field_get_value, (Il2CppObject*, FieldInfo*, void*))                               \
    X(void, il2cpp_field_set_value, (Il2CppObject*, FieldInfo*, void*))                               \
    X(void, il2cpp_field_static_get_value, (FieldInfo*, void*))                                       \
    X(void, il2cpp_field_static_set_value, (FieldInfo*, void*))                                       \
    X(Il2CppObject*, il2cpp_object_new, (const Il2CppClass*))                                         \
    X(void*, il2cpp_object_unbox, (Il2CppObject*))                                                    \
    X(Il2CppString*, il2cpp_string_new, (const char*))                                                \
    X(Il2CppChar*, il2cpp_string_chars, (Il2CppString*))                                              \
    X(int32_t, il2cpp_string_length, (Il2CppString*))                                                 \
    X(size_t, il2cpp_image_get_class_count, (const Il2CppImage*))                                     \
    X(const Il2CppClass*, il2cpp_image_get_class, (const Il2CppImage*, size_t))                       \
    X(const char*, il2cpp_class_get_name, (Il2CppClass*))                                             \
    X(const char*, il2cpp_class_get_namespace, (Il2CppClass*))                                        \
    X(bool, il2cpp_is_vm_thread, (Il2CppThread*))                                                     \
    X(Il2CppThread*, il2cpp_thread_current, (void))

namespace NCS {
namespace IL2CPP {

struct Api {
#define NCS_DECLARE_FN(ret, name, args) ret(*name) args = nullptr;
    NCS_IL2CPP_REQUIRED(NCS_DECLARE_FN)
    NCS_IL2CPP_OPTIONAL(NCS_DECLARE_FN)
#undef NCS_DECLARE_FN
};

extern Api api;

/// dlsym every export. Returns false if a required one is missing (missing names are logged).
bool ResolveExports(void* libil2cppHandle);
void* ResolveExport(const char* name);

/// True once il2cpp_init has completed (corlib loaded and assemblies registered).
bool RuntimeInitialized();

/// Attaches the calling native thread to the IL2CPP domain (idempotent per thread).
bool AttachCurrentThread();

bool IsReady();
void SetReady(bool ready);

// Name-based helpers --------------------------------------------------------------------
std::vector<std::string> ListAssemblies();
const Il2CppImage* FindAssembly(const char* name);  // "Assembly-CSharp" or "Assembly-CSharp.dll"
Il2CppClass* FindClass(const char* assembly, const char* nameSpace, const char* name);
const MethodInfo* FindMethod(Il2CppClass* klass, const char* name, int argc);
FieldInfo* FindField(Il2CppClass* klass, const char* name);
Il2CppObject* Invoke(const MethodInfo* method, void* instance, void** args, Il2CppObject** exception);
void GetField(Il2CppObject* obj, FieldInfo* field, void* out);
void SetField(Il2CppObject* obj, FieldInfo* field, void* value);
void GetStaticField(FieldInfo* field, void* out);
void SetStaticField(FieldInfo* field, void* value);
Il2CppString* NewString(const char* utf8);
std::string ToUtf8(Il2CppString* str);
size_t ClassCount(const Il2CppImage* image);

/// Invokes a static parameterless method returning System.String; empty on failure.
std::string CallStaticString(const char* assembly, const char* ns, const char* klass, const char* method);

}  // namespace IL2CPP
}  // namespace NCS
