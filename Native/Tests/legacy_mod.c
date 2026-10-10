/* libncsmod_legacytest.so - a mod as built for NightClubModKit (API 1, before the NCMK rename):
 * NCSMod_* exports, NCSModInfo / NCSHostApi layouts (identical to the API 1 part of NCMK's).
 * The NCMK loader must still load it and call NCSMod_Update as the background tick. */
#include <stdint.h>
#include <stdio.h>

typedef struct {
    uint32_t structSize;
    const char* id;
    const char* name;
    const char* author;
    const char* version;
    uint32_t requiredApi;
} NCSModInfo;

typedef struct {
    uint32_t structSize;
    uint32_t apiVersion;
    const char* loaderVersion;
    const char* packageName;
    const char* gameVersion;
    const char* unityVersion;
    const char* modsRoot;
    int safeMode;
    void (*log)(int level, const char* modId, const char* message);
} NCSHostApiPrefix;

static const NCSModInfo kInfo = {sizeof(NCSModInfo), "ncs.legacytest", "LegacyTest", "old", "0.9.0", 1};
static const NCSHostApiPrefix* g_api;
static int g_ticks;

__attribute__((visibility("default"))) const NCSModInfo* NCSMod_GetInfo(void) { return &kInfo; }

__attribute__((visibility("default"))) int NCSMod_Load(const NCSHostApiPrefix* api) {
    g_api = api;
    api->log(1, kInfo.id, "legacy NCSMod_Load called");
    return 0;
}

__attribute__((visibility("default"))) void NCSMod_Update(float dt) {
    (void)dt;
    if (++g_ticks == 3) g_api->log(1, kInfo.id, "legacy NCSMod_Update ticking on the background thread");
}
