/*
 * NCMK - native mod ABI (C, stable). See docs/MODDING.md.
 *
 * Every mod is an ARM64 shared library named libncmkmod_<name>.so that exports:
 *
 *   const NCMKModInfo* NCMKMod_GetInfo(void);               required
 *   int                NCMKMod_Load(const NCMKHostApi* api); required, return 0 on success
 *   void               NCMKMod_Unload(void);                 optional
 *
 *   API 2 - Unity main thread (needs the main-thread dispatcher; never called in Safe Mode):
 *   void NCMKMod_OnUpdate(float deltaTime);                  optional, once per rendered frame
 *   void NCMKMod_OnLateUpdate(float deltaTime);              optional, after every mod's OnUpdate of the frame
 *   void NCMKMod_OnFixedUpdate(void);                        optional, once per physics step Unity ran since
 *                                                            the previous frame (called after the frame's
 *                                                            Update, not interleaved with physics)
 *   void NCMKMod_OnSceneLoaded(const char* sceneName);       optional, when the active scene changes
 *
 *   Loader worker thread (attached to IL2CPP, NOT Unity's main thread):
 *   void NCMKMod_OnBackgroundTick(float deltaSeconds);       optional, ~10 Hz
 *
 * THREADING
 *   NCMKMod_Load, NCMKMod_OnBackgroundTick and NCMKMod_Unload run on the loader worker thread.
 *   Scene-graph / UI / Input work (GameObject, Transform, Canvas, OVRInput...) belongs in the main
 *   thread callbacks, or in a function queued with api->runOnMainThread().
 *
 * COMPATIBILITY
 *   NCMK_LOADER_API is 2. A mod built for API 1 (requiredApi = 1) still loads; its old
 *   NCMKMod_Update / NCSMod_Update export is called as the background tick. Mods from the
 *   NightClubModKit era (libncsmod_*.so exporting NCSMod_*) are loaded the same way: the
 *   NCSHostApi / NCSModInfo layouts are identical to the API 1 part of the structs below.
 *
 *   NCMKHostApi only grows by appending fields. Before using a field added after API 1 check it
 *   with NCMK_HOST_HAS(api, field): an older loader passes a smaller struct.
 */
#ifndef NCMK_MOD_API_H
#define NCMK_MOD_API_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define NCMK_LOADER_API 2

#if defined(_WIN32)
#define NCMK_EXPORT __declspec(dllexport)
#else
#define NCMK_EXPORT __attribute__((visibility("default")))
#endif

enum NCMKLogLevel {
    NCMK_LOG_DEBUG = 0,
    NCMK_LOG_INFO = 1,
    NCMK_LOG_WARN = 2,
    NCMK_LOG_ERROR = 3,
};

typedef struct NCMKModInfo {
    uint32_t structSize;   /* sizeof(NCMKModInfo) */
    const char* id;        /* "ncmk.helloworld" - also names the config file Config/<id>.cfg */
    const char* name;      /* "HelloWorld" */
    const char* author;
    const char* version;   /* "1.0.0" */
    uint32_t requiredApi;  /* NCMK_LOADER_API the mod was built against */
} NCMKModInfo;

/* Opaque IL2CPP handles. */
typedef void NCMKClass;
typedef void NCMKMethod;
typedef void NCMKField;
typedef void NCMKObject;
typedef void NCMKString;
typedef void NCMKImage;

typedef void (*NCMKMainThreadFn)(void* user);

typedef struct NCMKHostApi {
    /* ================================ API 1 ================================ */
    uint32_t structSize;
    uint32_t apiVersion;           /* == NCMK_LOADER_API of the running loader */

    const char* loaderVersion;
    const char* packageName;
    const char* gameVersion;       /* Application.version once IL2CPP is ready, else "unknown" */
    const char* unityVersion;      /* Application.unityVersion once IL2CPP is ready, else "unknown" */
    const char* modsRoot;          /* /sdcard/Android/data/<package>/files/NCMKMods */
    int safeMode;

    /* Logging: goes to logcat (tag NCMK) and NCMKMods/Logs/latest.log */
    void (*log)(int level, const char* modId, const char* message);

    /* Loader state */
    int (*isIl2CppReady)(void);
    int (*getLoadedModCount)(void);
    const char* (*getLoadedModName)(int index);  /* "Name version" */

    /* Raw IL2CPP export lookup (dlsym on libil2cpp.so). Prefer the helpers below. */
    void* (*il2cppResolve)(const char* exportName);

    /* IL2CPP helpers - name based, never offset based. */
    int (*getAssemblyCount)(void);
    const char* (*getAssemblyName)(int index);
    NCMKImage* (*findImage)(const char* assemblyName);                   /* "Assembly-CSharp" or "Assembly-CSharp.dll" */
    NCMKClass* (*findClass)(const char* assembly, const char* nameSpace, const char* name);
    NCMKMethod* (*findMethod)(NCMKClass* klass, const char* name, int argCount);
    NCMKField* (*findField)(NCMKClass* klass, const char* name);
    NCMKObject* (*invoke)(NCMKMethod* method, void* instance, void** args, NCMKObject** exception);
    NCMKString* (*newString)(const char* utf8);
    int (*stringToUtf8)(NCMKString* str, char* buffer, int bufferSize);  /* returns length or -1 */
    void (*getField)(NCMKObject* obj, NCMKField* field, void* outValue);
    void (*setField)(NCMKObject* obj, NCMKField* field, void* value);
    void (*getStaticField)(NCMKField* field, void* outValue);
    void (*setStaticField)(NCMKField* field, void* value);
    void* (*unbox)(NCMKObject* boxed);
    int (*attachCurrentThread)(void);

    /* appended in loader 0.2.0 */
    const char* deviceName;      /* "Meta Quest 3S" */
    const char* deviceCodename;  /* ro.product.device: hollywood / seacliff / eureka / panther */
    const char* deviceModel;     /* ro.product.model:  "Quest 2" / "Quest Pro" / "Quest 3" / "Quest 3S" */

    /* ================================ API 2 ================================ */

    /* 1 when called on Unity's main thread (known after the dispatcher's first frame). */
    int (*isMainThread)(void);

    /* Queues fn(user) to run on the main thread at the start of the next dispatched frame.
     * Callable from any thread. Returns 0 when no main-thread dispatcher is running (Safe Mode,
     * or no per-frame target could be hooked) - fn is then never called. */
    int (*runOnMainThread)(NCMKMainThreadFn fn, void* user);

    /* Overload-safe lookup: a method named `name` whose parameter types end with the given
     * suffixes, e.g. {"Single"}, {"UnityEngine.Vector3", "Boolean"}, {"OVRInput.Button", "OVRInput.Controller"}.
     * Nested types match with '.', '+' or '/'. Searches parent classes too. */
    NCMKMethod* (*findMethodBySignature)(NCMKClass* klass, const char* name, const char* const* paramTypeSuffixes,
                                         int paramCount);

    /* Nested type by simple name: findNestedClass(OVRInput, "Button"). */
    NCMKClass* (*findNestedClass)(NCMKClass* outer, const char* name);

    /* System.Type object for a class (for GetComponent(Type), AddComponent(Type)...). */
    NCMKObject* (*typeOf)(NCMKClass* klass);

    /* il2cpp_object_new: allocates an instance; the constructor is NOT run (invoke ".ctor" yourself). */
    NCMKObject* (*newObject)(NCMKClass* klass);

    /* Keeps a managed object alive while native code holds it: il2cpp_gchandle_new(obj, pinned=true).
     * Returns a handle (0 on failure) to release with unpin. */
    uint32_t (*pin)(NCMKObject* obj);
    void (*unpin)(uint32_t handle);

    /* Value of an enum member by name, e.g. getEnumValue(OVRInput.Button, "One", -1). */
    int64_t (*getEnumValue)(NCMKClass* enumClass, const char* name, int64_t fallback);
} NCMKHostApi;

/* True when the running loader provides the field (older loaders have a smaller struct). */
#define NCMK_HOST_HAS(api, field) \
    ((api) && (api)->structSize >= offsetof(NCMKHostApi, field) + sizeof(((NCMKHostApi*)0)->field))

typedef const NCMKModInfo* (*NCMKMod_GetInfo_t)(void);
typedef int (*NCMKMod_Load_t)(const NCMKHostApi* api);
typedef void (*NCMKMod_Unload_t)(void);
typedef void (*NCMKMod_OnUpdate_t)(float deltaTime);
typedef void (*NCMKMod_OnLateUpdate_t)(float deltaTime);
typedef void (*NCMKMod_OnFixedUpdate_t)(void);
typedef void (*NCMKMod_OnSceneLoaded_t)(const char* sceneName);
typedef void (*NCMKMod_OnBackgroundTick_t)(float deltaSeconds);

#ifdef __cplusplus
}
#endif

#endif /* NCMK_MOD_API_H */
