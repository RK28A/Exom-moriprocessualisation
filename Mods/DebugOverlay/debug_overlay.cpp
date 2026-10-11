// libncmkmod_debugoverlay.so - in-headset debug panel (NCMK API 2).
//
// Shows scene name, FPS and loaded mods on a world-space canvas that follows the head.
// Left thumbstick click toggles it. Options in NCMKMods/Config/ncmk.debugoverlay.cfg.
// If the UI cannot be built (classes missing, no camera...), it falls back to a status block in
// logcat / latest.log every few seconds.
#include <cstdio>
#include <string>

#include "../../SDK/Config/ncmk_config.hpp"
#include "../../SDK/Core/ncmk_sdk.hpp"
#include "../../SDK/Input/ncmk_input.hpp"
#include "../../SDK/UI/ncmk_ui.hpp"
#include "../../SDK/Unity/ncmk_unity.hpp"

static const NCMKModInfo kInfo = {
    sizeof(NCMKModInfo), "ncmk.debugoverlay", "DebugOverlay", "RK", "0.3.0", NCMK_LOADER_API,
};

namespace {
using namespace NCMK;

struct Options {
    bool enabled = true;
    float distance = 1.2f;   // meters in front of the head
    float height = -0.25f;   // meters relative to eye level
    float refresh = 0.5f;    // seconds between text updates
    float logInterval = 5.f; // fallback mode
} g_opt;

enum class Mode { Starting, Panel, LogOnly };
Mode g_mode = Mode::Starting;
bool g_visible = true;

IL2CPP::Pinned g_canvasObject;  // keeps the canvas GameObject wrapper alive across frames
IL2CPP::Pinned g_textObject;
UI::Canvas g_canvas;
UI::Text g_text;

std::string g_scene = "?";
float g_textTimer = 0.f, g_logTimer = 0.f;
float g_fpsTime = 0.f;
int g_fpsFrames = 0;
float g_fps = 0.f;

std::string ModList() {
    std::string mods;
    for (const auto& m : Game::GetLoadedMods()) mods += "\n  " + m;
    return mods;
}

std::string StatusText() {
    char buf[256];
    snprintf(buf, sizeof(buf), "Loader %s - game %s\nScene: %s\nFPS: %.0f\nDevice: %s\nMods:", Host()->loaderVersion,
             Game::GetVersion().c_str(), g_scene.c_str(), g_fps, Game::GetDeviceName().c_str());
    return buf + ModList();
}

bool BuildPanel() {
    g_canvas = UI::Canvas::CreateWorldSpace("NCMK.DebugOverlay", {520, 360});
    if (!g_canvas) return false;
    g_canvas.SetSortingOrder(500);
    // Soft purple halo, then a rounded 9-sliced background (both decorative: no raycasts).
    UI::Element glow = UI::CreateElement(g_canvas.rect, "Glow");
    if (glow) {
        glow.rect.Stretch(-24.f);
        UI::Image img = UI::AddImage(glow.gameObject, {0.75f, 0.35f, 1.f, 0.35f});
        img.SetSliced(UI::CreateGlowSprite(96, 18, 24));
        img.SetRaycastTarget(false);
    }
    UI::Element bg = UI::CreateElement(g_canvas.rect, "Background");
    if (bg) {
        bg.rect.Stretch();
        UI::Image img = UI::AddImage(bg.gameObject, {0.05f, 0.03f, 0.10f, 0.85f});
        img.SetSliced(UI::CreateRoundedSprite(64, 18));
        img.SetRaycastTarget(false);
    }
    UI::Element title = UI::CreateElement(g_canvas.rect, "Title");
    if (title) {
        title.rect.PlaceTopLeft({18, 12}, {484, 40});
        UI::Text t = UI::AddText(title.gameObject, "NCMK Debug Overlay", 28, {0.85f, 0.6f, 1.f, 1.f});
        t.SetBold(true);
        t.SetRaycastTarget(false);
    }
    UI::Element body = UI::CreateElement(g_canvas.rect, "Status");
    if (body) {
        body.rect.PlaceTopLeft({18, 58}, {484, 290});
        g_text = UI::AddText(body.gameObject, "NCMK", 24, {1, 1, 1, 1});
    }
    if (!g_text) return false;
    g_text.SetOverflow(true, true);
    g_text.SetRaycastTarget(false);
    g_canvasObject = IL2CPP::Pinned(g_canvas.gameObject.Ptr());
    g_textObject = IL2CPP::Pinned(g_text.Ptr());
    return true;
}

void FollowHead() {
    Unity::Camera cam = Unity::Camera::Main();
    if (!cam) return;
    Unity::Transform head = cam.transform();
    Unity::Vec3 fwd = head.forward();
    fwd.y = 0;
    fwd = fwd.Length() > 1e-3f ? fwd.Normalized() : Unity::Vec3::Forward();
    Unity::Vec3 pos = head.position() + fwd * g_opt.distance + Unity::Vec3{0, g_opt.height, 0};
    g_canvas.rect.SetPosition(pos);
    g_canvas.rect.SetRotation(Unity::Quat::LookRotation(fwd));
}

void LogStatus() {
    NCMK_LOGI("---- NCMK Debug Overlay ----");
    NCMK_LOGI("Loader   : %s (API %u)", Host()->loaderVersion, Host()->apiVersion);
    NCMK_LOGI("Game     : %s %s", Host()->packageName, Host()->gameVersion);
    NCMK_LOGI("Device   : %s", Game::GetDeviceName().c_str());
    NCMK_LOGI("Scene    : %s", g_scene.c_str());
    NCMK_LOGI("FPS      : %.1f", g_fps);
    NCMK_LOGI("Mods     :%s", ModList().c_str());
}
}  // namespace

extern "C" NCMK_EXPORT const NCMKModInfo* NCMKMod_GetInfo(void) { return &kInfo; }

extern "C" NCMK_EXPORT int NCMKMod_Load(const NCMKHostApi* api) {
    if (!NCMK::Init(api, kInfo.id)) return 1;
    if (!NCMK::HasApi2()) {
        NCMK_LOGE("DebugOverlay needs NCMK loader API 2 (this loader: API %u)", api->apiVersion);
        return 2;
    }
    Config cfg;
    cfg.Load();
    g_opt.enabled = cfg.GetBool("enabled", g_opt.enabled);
    g_opt.distance = cfg.GetFloat("distance", g_opt.distance);
    g_opt.height = cfg.GetFloat("height", g_opt.height);
    g_opt.refresh = cfg.GetFloat("refreshSeconds", g_opt.refresh);
    g_opt.logInterval = cfg.GetFloat("logIntervalSeconds", g_opt.logInterval);
    if (cfg.Dirty()) cfg.Save();
    NCMK_LOGI("DebugOverlay ready (%s)", g_opt.enabled ? "enabled" : "disabled in config");
    return 0;
}

extern "C" NCMK_EXPORT void NCMKMod_OnSceneLoaded(const char* scene) { g_scene = scene ? scene : "?"; }

extern "C" NCMK_EXPORT void NCMKMod_OnUpdate(float dt) {
    if (!g_opt.enabled) return;
    float unscaled = Unity::Time::Unscaled();
    if (unscaled <= 0.f) unscaled = dt;
    g_fpsTime += unscaled;
    ++g_fpsFrames;
    if (g_fpsTime >= 1.f) {
        g_fps = g_fpsFrames / g_fpsTime;
        g_fpsTime = 0.f;
        g_fpsFrames = 0;
    }

    if (g_mode == Mode::Starting) {
        if (g_scene == "?") g_scene = Unity::Scene::CurrentName().empty() ? "?" : Unity::Scene::CurrentName();
        g_mode = BuildPanel() ? Mode::Panel : Mode::LogOnly;
        if (g_mode == Mode::Panel) NCMK_LOGI("In-headset panel created (left thumbstick click toggles it)");
        else {
            NCMK_LOGW("UI unavailable - status goes to the log every %.0f s instead", g_opt.logInterval);
            LogStatus();
        }
    }

    if (g_mode == Mode::LogOnly) {
        g_logTimer += unscaled;
        if (g_logTimer >= g_opt.logInterval) {
            g_logTimer = 0.f;
            LogStatus();
        }
        return;
    }

    if (Input::WasPressed(Input::Hand::Left, Input::Button::Thumbstick)) {
        g_visible = !g_visible;
        g_canvas.gameObject.SetActive(g_visible);
    }
    if (!g_visible) return;
    FollowHead();
    g_textTimer += unscaled;
    if (g_textTimer >= g_opt.refresh) {
        g_textTimer = 0.f;
        g_text.SetText(StatusText().c_str());
    }
}

extern "C" NCMK_EXPORT void NCMKMod_Unload(void) {
    if (g_mode == Mode::Panel && NCMK::IsMainThread()) Unity::Object::Destroy(g_canvas.gameObject);
    g_textObject.Reset();
    g_canvasObject.Reset();
}
