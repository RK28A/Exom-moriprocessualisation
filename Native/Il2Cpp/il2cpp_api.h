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
struct Il2CppType;
typedef uint16_t Il2CppChar;

// Exported IL2CPP API, resolved with dlsym (never by offset).
#define NCMK_IL2CPP_REQUIRED(X)                                                                         \
    X(Il2CppDomain*, il2cpp_domain_get, (void))                                                        \
    X(const Il2CppAssembly**, il2cpp_domain_get_assemblies, (const Il2CppDomain*, size_t*))           \
    X(const Il2CppImage*, il2cpp_assembly_get_image, (const Il2CppAssembly*))                          \
    X(const char*, il2cpp_image_get_name, (const Il2CppImage*))                                        \
    X(Il2CppClass*, il2cpp_class_from_name, (const Il2CppImage*, const char*, const char*))           \
    X(const MethodInfo*, il2cpp_class_get_method_from_name, (Il2CppClass*, const char*, int))         \
    X(Il2CppObject*, il2cpp_runtime_invoke, (const MethodInfo*, void*, void**, Il2CppObject**))       \
    X(Il2CppThread*, il2cpp_thread_attach, (Il2CppDomain*))                                            \
    X(const Il2CppImage*, il2cpp_get_corlib, (void))

#define NCMK_IL2CPP_OPTIONAL(X)                                                                         \
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
    X(Il2CppThread*, il2cpp_thread_current, (void))                                                   \
    X(const MethodInfo*, il2cpp_class_get_methods, (Il2CppClass*, void**))                            \
    X(const char*, il2cpp_method_get_name, (const MethodInfo*))                                       \
    X(uint32_t, il2cpp_method_get_param_count, (const MethodInfo*))                                   \
    X(const Il2CppType*, il2cpp_method_get_param, (const MethodInfo*, uint32_t))                      \
    X(uint32_t, il2cpp_method_get_flags, (const MethodInfo*, uint32_t*))                              \
    X(char*, il2cpp_type_get_name, (const Il2CppType*))                                               \
    X(int, il2cpp_type_get_type, (const Il2CppType*))                                                 \
    X(void, il2cpp_free, (void*))                                                                     \
    X(Il2CppClass*, il2cpp_class_get_parent, (Il2CppClass*))                                          \
    X(Il2CppClass*, il2cpp_class_get_nested_types, (Il2CppClass*, void**))                            \
    X(const Il2CppType*, il2cpp_class_get_type, (Il2CppClass*))                                       \
    X(const Il2CppType*, il2cpp_class_enum_basetype, (Il2CppClass*))                                  \
    X(Il2CppObject*, il2cpp_type_get_object, (const Il2CppType*))                                     \
    X(uint32_t, il2cpp_gchandle_new, (Il2CppObject*, bool))                                           \
    X(void, il2cpp_gchandle_free, (uint32_t))                                                         \
    X(void*, il2cpp_resolve_icall, (const char*))

namespace NCMK {
namespace IL2CPP {

struct Api {
#define NCMK_DECLARE_FN(ret, name, args) ret(*name) args = nullptr;
    NCMK_IL2CPP_REQUIRED(NCMK_DECLARE_FN)
    NCMK_IL2CPP_OPTIONAL(NCMK_DECLARE_FN)
#undef NCMK_DECLARE_FN
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

/// Method `name` whose parameter type names end with `suffixes` ('+' and '/' read as '.'),
/// searching parent classes too. Name-based only.
const MethodInfo* FindMethodBySignature(Il2CppClass* klass, const char* name, const char* const* suffixes, int count);
Il2CppClass* FindNestedClass(Il2CppClass* outer, const char* name);
Il2CppObject* TypeOf(Il2CppClass* klass);
Il2CppObject* NewObject(Il2CppClass* klass);
uint32_t Pin(Il2CppObject* obj);
void Unpin(uint32_t handle);
int64_t GetEnumValue(Il2CppClass* enumClass, const char* name, int64_t fallback);
bool IsStatic(const MethodInfo* method);

/// Invokes a static parameterless method returning System.String; empty on failure.
std::string CallStaticString(const char* assembly, const char* ns, const char* klass, const char* method);

}  // namespace IL2CPP
}  // namespace NCMK
