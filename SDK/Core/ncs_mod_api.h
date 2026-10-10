/*
 * NightClubModKit - native mod ABI (C, stable).
 *
 * Every mod is an ARM64 shared library named libncsmod_<name>.so that exports:
 *
 *   const NCSModInfo* NCSMod_GetInfo(void);              required
 *   int               NCSMod_Load(const NCSHostApi* api); required, return 0 on success
 *   void              NCSMod_Update(float deltaSeconds);  optional
 *   void              NCSMod_Unload(void);                optional
 *   void              NCSMod_OnSceneLoaded(const char*);  optional (reserved, called once scene hooks exist)
 *   void              NCSMod_OnFixedUpdate(void);         optional (reserved)
 *   void              NCSMod_OnGUI(void);                 optional (reserved)
 *
 * THREADING (API 1): NCSMod_Load and NCSMod_Update run on the loader worker thread, which is
 * attached to the IL2CPP domain but is NOT Unity's main thread. Do not call Unity APIs that
 * require the main thread (scene graph, GameObject, Transform...). Main-thread callbacks arrive
 * with the Hooks module in a later API version.
 */
#ifndef NCS_MOD_API_H
#define NCS_MOD_API_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define NCS_LOADER_API 1

#if defined(_WIN32)
#define NCS_EXPORT __declspec(dllexport)
#else
#define NCS_EXPORT __attribute__((visibility("default")))
#endif

enum NCSLogLevel {
    NCS_LOG_DEBUG = 0,
    NCS_LOG_INFO = 1,
    NCS_LOG_WARN = 2,
    NCS_LOG_ERROR = 3,
};

typedef struct NCSModInfo {
    uint32_t structSize;   /* sizeof(NCSModInfo) */
    const char* id;        /* "ncs.helloworld" */
    const char* name;      /* "HelloWorld" */
    const char* author;
    const char* version;   /* "1.0.0" */
    uint32_t requiredApi;  /* NCS_LOADER_API the mod was built against */
} NCSModInfo;

/* Opaque IL2CPP handles. */
typedef void NCSClass;
typedef void NCSMethod;
typedef void NCSField;
typedef void NCSObject;
typedef void NCSString;
typedef void NCSImage;

typedef struct NCSHostApi {
    uint32_t structSize;
    uint32_t apiVersion;           /* == NCS_LOADER_API of the running loader */

    const char* loaderVersion;
    const char* packageName;
    const char* gameVersion;       /* Application.version once IL2CPP is ready, else "unknown" */
    const char* unityVersion;      /* Application.unityVersion once IL2CPP is ready, else "unknown" */
    const char* modsRoot;          /* e.g. /sdcard/NightClubMods */
    int safeMode;

    /* Logging: goes to logcat (tag NCSLoader) and NightClubMods/Logs/latest.log */
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
    NCSImage* (*findImage)(const char* assemblyName);                   /* "Assembly-CSharp" or "Assembly-CSharp.dll" */
    NCSClass* (*findClass)(const char* assembly, const char* nameSpace, const char* name);
    NCSMethod* (*findMethod)(NCSClass* klass, const char* name, int argCount);
    NCSField* (*findField)(NCSClass* klass, const char* name);
    NCSObject* (*invoke)(NCSMethod* method, void* instance, void** args, NCSObject** exception);
    NCSString* (*newString)(const char* utf8);
    int (*stringToUtf8)(NCSString* str, char* buffer, int bufferSize);  /* returns length or -1 */
    void (*getField)(NCSObject* obj, NCSField* field, void* outValue);
    void (*setField)(NCSObject* obj, NCSField* field, void* value);
    void (*getStaticField)(NCSField* field, void* outValue);
    void (*setStaticField)(NCSField* field, void* value);
    void* (*unbox)(NCSObject* boxed);
    int (*attachCurrentThread)(void);

    /* ---- appended in loader 0.2.0 (still API 1: check structSize before use) ---- */
    const char* deviceName;      /* "Meta Quest 3S" */
    const char* deviceCodename;  /* ro.product.device: hollywood / seacliff / eureka / panther */
    const char* deviceModel;     /* ro.product.model:  "Quest 2" / "Quest Pro" / "Quest 3" / "Quest 3S" */
} NCSHostApi;

/* True when the running loader provides the field (older loaders have a smaller struct). */
#define NCS_HOST_HAS(api, field) \
    ((api) && (api)->structSize >= offsetof(NCSHostApi, field) + sizeof(((NCSHostApi*)0)->field))

typedef const NCSModInfo* (*NCSMod_GetInfo_t)(void);
typedef int (*NCSMod_Load_t)(const NCSHostApi* api);
typedef void (*NCSMod_Update_t)(float deltaSeconds);
typedef void (*NCSMod_Unload_t)(void);
typedef void (*NCSMod_OnSceneLoaded_t)(const char* sceneName);
typedef void (*NCSMod_OnFixedUpdate_t)(void);
typedef void (*NCSMod_OnGUI_t)(void);

#ifdef __cplusplus
}
#endif

#endif /* NCS_MOD_API_H */
