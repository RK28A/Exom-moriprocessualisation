// libncmkmod_helloworld.so - the smallest possible NCMK mod.
// Expected log:  [NCMK] Mod loaded: HelloWorld 1.0.0
#include "../../SDK/Core/ncmk_sdk.hpp"
#include "../../SDK/Il2Cpp/ncmk_il2cpp.hpp"

static const NCMKModInfo kInfo = {
    sizeof(NCMKModInfo), "ncmk.helloworld", "HelloWorld", "RK", "1.0.0", NCMK_LOADER_API,
};

extern "C" NCMK_EXPORT const NCMKModInfo* NCMKMod_GetInfo(void) { return &kInfo; }

extern "C" NCMK_EXPORT int NCMKMod_Load(const NCMKHostApi* api) {
    if (!NCMK::Init(api, kInfo.id)) return 1;
    NCMK_LOGI("Hello from HelloWorld %s (loader %s, game %s, Unity %s)", kInfo.version, api->loaderVersion,
             api->gameVersion, api->unityVersion);

    auto assemblies = NCMK::IL2CPP::GetAssemblies();
    NCMK_LOGI("IL2CPP reports %zu assemblies", assemblies.size());

    // Read-only lookup of a game class (no modification).
    if (NCMK::IL2CPP::FindClass("Assembly-CSharp", "", "GameManager"))
        NCMK_LOGI("Found Assembly-CSharp::GameManager");
    return 0;
}

extern "C" NCMK_EXPORT void NCMKMod_Unload(void) { NCMK_LOGI("HelloWorld unloaded"); }
