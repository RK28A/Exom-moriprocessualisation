// Read-only IL2CPP inspection helpers for the diagnostics modules: metadata dumps, safe value
// formatting and "probes" around engine-invoked methods. Name-based lookups only.
#pragma once

#include <string>
#include <vector>

#include "../Il2Cpp/il2cpp_api.h"

namespace ncmk {
namespace diag {

/// "System.Int32", "bl_SceneLoaderManager", ... ("?" when unknown).
std::string TypeName(const Il2CppType* type);
std::string ClassName(Il2CppClass* klass);  // "Namespace.Name" or "Name"

/// Finds a class by simple name: first in `preferredAssembly`, then in every assembly.
Il2CppClass* FindClassByName(const char* preferredAssembly, const char* name);

/// Class names of `assembly` starting with `prefix`.
std::vector<Il2CppClass*> FindClassesByPrefix(const char* assembly, const char* prefix);

bool IsReferenceType(const Il2CppType* type);
bool IsUnityObjectClass(Il2CppClass* klass);  // derives from UnityEngine.Object

/// Logs namespace, parents, fields (type, static/instance/const), properties and methods
/// (parameter types, return type, static, MethodInfo* and method pointer) with `tag`.
void DumpMetadata(const char* tag, Il2CppClass* klass);

/// Value of a field as text: "NULL", "0x... (Class) [alive|DESTROYED/MISSING Unity object]",
/// primitives, short strings. `obj` null = static field. Main thread only.
std::string FieldValue(Il2CppObject* obj, FieldInfo* field, bool* isNullReference = nullptr);

/// Logs every static field (or instance field of obj) with its value; returns the NULL reference fields.
std::vector<std::string> DumpFieldValues(const char* tag, Il2CppClass* klass, Il2CppObject* obj);

/// "System.NullReferenceException: Object reference not set ..." for a managed exception object.
std::string ExceptionText(Il2CppObject* exception);

/// Unity's "== null" for a UnityEngine.Object (false for destroyed / missing objects).
bool UnityObjectAlive(Il2CppObject* obj);

/// Resources.FindObjectsOfTypeAll(typeof(klass)) (loaded instances, assets included). Main thread.
std::vector<Il2CppObject*> FindLoadedObjects(Il2CppClass* klass, size_t max = 4);

// ---------------------------------------------------------------- probes

/// Called on the main thread around a probed method. `threw` is set on the "after" call when the
/// original raised a managed exception (which is re-thrown unchanged after the callback).
using ProbeCallback = void (*)(int probeId, Il2CppObject* self, bool before, bool threw);

/// Wraps a `void M()` / `void M(this)` method by swapping the two MethodInfo slots (the same
/// documented layout assumption as the main-thread dispatcher; slot 1 only when equal to slot 0).
/// Only calls that go through the MethodInfo (engine messages such as Awake/Start, SendMessage,
/// Invoke, runtime_invoke) are seen; direct calls compiled into game code are not.
/// Returns the probe id, or -1 (not installed, reason logged).
int InstallProbe(const char* tag, const MethodInfo* method, ProbeCallback callback);

/// Tag given to InstallProbe (for log lines in callbacks).
const char* ProbeTag(int probeId);

/// Restores every probed MethodInfo (idempotent).
void RestoreProbes();

}  // namespace diag
}  // namespace ncmk
