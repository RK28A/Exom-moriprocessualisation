// libncsmod_debugoverlay.so - first "visible" mod.
//
// API 1 has no main-thread / rendering hook yet, so the overlay is emitted as a periodic status
// block in logcat and NightClubMods/Logs/latest.log. Rendering in the headset arrives with the
// Hooks module (API 2); the data collection below stays the same.
#include <string>

#include "../../SDK/Core/ncs_sdk.hpp"
#include "../../SDK/Il2Cpp/ncs_il2cpp.hpp"
#include "../../SDK/Unity/ncs_unity.hpp"

static const NCSModInfo kInfo = {
    sizeof(NCSModInfo), "ncs.debugoverlay", "DebugOverlay", "RK", "0.1.0", NCS_LOADER_API,
};

namespace {
float g_accum = 0.f;
int g_lastFrame = -1;
float g_sinceFrame = 0.f;
bool g_sceneUnavailableLogged = false;
constexpr float kInterval = 5.f;
}  // namespace

extern "C" NCS_EXPORT const NCSModInfo* NCSMod_GetInfo(void) { return &kInfo; }

extern "C" NCS_EXPORT int NCSMod_Load(const NCSHostApi* api) {
    if (!NCS::Init(api, kInfo.id)) return 1;
    NCS_LOGI("DebugOverlay ready (status every %.0fs)", kInterval);
    return 0;
}

extern "C" NCS_EXPORT void NCSMod_Update(float dt) {
    g_accum += dt;
    g_sinceFrame += dt;
    if (g_accum < kInterval) return;
    g_accum = 0.f;

    const NCSHostApi* h = NCS::Host();
    int frame = NCS::Unity::GetFrameCount();
    float fps = (frame >= 0 && g_lastFrame >= 0 && g_sinceFrame > 0.f) ? (frame - g_lastFrame) / g_sinceFrame : -1.f;
    g_lastFrame = frame;
    g_sinceFrame = 0.f;

    std::string scene = NCS::Game::GetCurrentScene();
    if (scene.empty()) {
        scene = "n/a (main thread required)";
        if (!g_sceneUnavailableLogged) {
            NCS_LOGI("Scene name needs the main-thread hook (API 2); showing n/a");
            g_sceneUnavailableLogged = true;
        }
    }

    std::string mods;
    for (const auto& m : NCS::Game::GetLoadedMods()) mods += (mods.empty() ? "" : ", ") + m;

    NCS_LOGI("---- NCS Debug Overlay ----");
    NCS_LOGI("Loader   : %s (API %u)", h->loaderVersion, h->apiVersion);
    NCS_LOGI("Game     : %s %s", h->packageName, h->gameVersion);
    NCS_LOGI("Device   : %s", NCS::Game::GetDeviceName().c_str());
    NCS_LOGI("Unity    : %s", h->unityVersion);
    if (fps >= 0.f) NCS_LOGI("FPS      : %.1f (frame %d)", fps, frame);
    else NCS_LOGI("FPS      : measuring (frame %d)", frame);
    NCS_LOGI("Scene    : %s", scene.c_str());
    NCS_LOGI("Mods     : %s", mods.c_str());
    NCS_LOGI("SafeMode : %s", h->safeMode ? "ON" : "off");
    NCS_LOGI("IL2CPP   : %s", h->isIl2CppReady() ? "ready" : "not ready");
}

extern "C" NCS_EXPORT void NCSMod_Unload(void) {}
