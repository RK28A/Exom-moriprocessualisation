// libncsmod_helloworld.so - the smallest possible NightClubModKit mod.
// Expected log:  [NCS] Mod loaded: HelloWorld 1.0.0
#include "../../SDK/Core/ncs_sdk.hpp"
#include "../../SDK/Il2Cpp/ncs_il2cpp.hpp"

static const NCSModInfo kInfo = {
    sizeof(NCSModInfo), "ncs.helloworld", "HelloWorld", "RK", "1.0.0", NCS_LOADER_API,
};

extern "C" NCS_EXPORT const NCSModInfo* NCSMod_GetInfo(void) { return &kInfo; }

extern "C" NCS_EXPORT int NCSMod_Load(const NCSHostApi* api) {
    if (!NCS::Init(api, kInfo.id)) return 1;
    NCS_LOGI("Hello from HelloWorld %s (loader %s, game %s, Unity %s)", kInfo.version, api->loaderVersion,
             api->gameVersion, api->unityVersion);

    auto assemblies = NCS::IL2CPP::GetAssemblies();
    NCS_LOGI("IL2CPP reports %zu assemblies", assemblies.size());

    // Read-only lookup of a game class (no modification).
    if (NCS::IL2CPP::FindClass("Assembly-CSharp", "", "GameManager"))
        NCS_LOGI("Found Assembly-CSharp::GameManager");
    return 0;
}

extern "C" NCS_EXPORT void NCSMod_Unload(void) { NCS_LOGI("HelloWorld unloaded"); }
