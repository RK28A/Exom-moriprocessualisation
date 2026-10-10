// libncmkmod_frametest.so - host-test-only mod proving the API 2 contract against the fake IL2CPP:
// OnUpdate fires on the main thread once per frame (deduplicated), after the original method,
// OnLateUpdate follows OnUpdate, OnFixedUpdate/OnSceneLoaded fire, runOnMainThread works, and the
// new host API lookups behave.
#include <cstdio>
#include <cstring>
#include <string>

#include "../../SDK/Core/ncmk_sdk.hpp"

static const NCMKModInfo kInfo = {sizeof(NCMKModInfo), "ncmk.test.frametest", "FrameTest", "NCMK", "1.0.0", NCMK_LOADER_API};

namespace {
const NCMKHostApi* g_api = nullptr;
int (*g_frame)() = nullptr;
int (*g_originalRan)() = nullptr;
int g_updates = 0, g_late = 0, g_fixed = 0, g_dupFrames = 0, g_notMain = 0, g_origNotFirst = 0, g_lateOutOfOrder = 0;
int g_lastFrame = -1, g_lastUpdateFrame = -1;
int g_posted = 0, g_postedOnMain = 0;
std::string g_scenes;
std::string g_failures;

void Check(bool ok, const char* what) {
    if (!ok) g_failures += std::string(g_failures.empty() ? "" : ", ") + what;
}

void Posted(void* user) {
    ++g_posted;
    if (g_api->isMainThread()) ++g_postedOnMain;
    *static_cast<int*>(user) = 1;
}
int g_postedFlag = 0;
}  // namespace

extern "C" NCMK_EXPORT const NCMKModInfo* NCMKMod_GetInfo(void) { return &kInfo; }

extern "C" NCMK_EXPORT int NCMKMod_Load(const NCMKHostApi* api) {
    if (!NCMK::Init(api, kInfo.id)) return 1;
    g_api = api;
    g_frame = reinterpret_cast<int (*)()>(api->il2cppResolve("fake_frame"));
    g_originalRan = reinterpret_cast<int (*)()>(api->il2cppResolve("fake_original_ran_this_frame"));

    Check(NCMK_HOST_HAS(api, getEnumValue), "structSize");
    Check(api->apiVersion == 2, "apiVersion");
    Check(!api->isMainThread(), "isMainThread on worker");

    NCMKClass* input = api->findClass("Oculus.VR", "", "OVRInput");
    NCMKClass* button = api->findNestedClass(input, "Button");
    NCMKClass* controller = api->findNestedClass(input, "Controller");
    Check(input && button && controller, "findNestedClass");
    Check(!api->findNestedClass(input, "Nope"), "findNestedClass missing");
    Check(api->getEnumValue(button, "One", -9) == 1, "getEnumValue One");
    Check(api->getEnumValue(button, "Any", 0) == -1, "getEnumValue negative");
    Check(api->getEnumValue(controller, "RTouch", 0) == 2, "getEnumValue RTouch");
    Check(api->getEnumValue(button, "Missing", 42) == 42, "getEnumValue fallback");

    const char* buttonSig[] = {"OVRInput.Button", "OVRInput.Controller"};
    const char* axisSig[] = {"Axis1D", "Controller"};
    const char* wrongSig[] = {"OVRInput.Button", "Int32"};
    const char* partialSig[] = {"utton", "Controller"};
    const char* stringSig[] = {"String"};
    NCMKMethod* getButton = api->findMethodBySignature(input, "Get", buttonSig, 2);
    NCMKMethod* getAxis = api->findMethodBySignature(input, "Get", axisSig, 2);
    Check(getButton && getAxis && getButton != getAxis, "findMethodBySignature overloads");
    Check(!api->findMethodBySignature(input, "Get", wrongSig, 2), "findMethodBySignature wrong types");
    Check(!api->findMethodBySignature(input, "Get", partialSig, 2), "findMethodBySignature name boundary");
    Check(api->findMethodBySignature(input, "Describe", stringSig, 1) != nullptr, "findMethodBySignature parent class");

    Check(api->typeOf(input) != nullptr, "typeOf");
    NCMKObject* obj = api->newObject(input);
    Check(obj != nullptr, "newObject");
    uint32_t pin = api->pin(obj);
    Check(pin != 0, "pin");
    api->unpin(pin);

    Check(api->runOnMainThread(Posted, &g_postedFlag) == 1, "runOnMainThread accepted");

    if (g_failures.empty()) NCMK_LOGI("[frametest] API 2 host checks: PASS");
    else NCMK_LOGE("[frametest] API 2 host checks: FAIL (%s)", g_failures.c_str());
    return 0;
}

extern "C" NCMK_EXPORT void NCMKMod_OnUpdate(float dt) {
    int frame = g_frame ? g_frame() : -1;
    if (frame == g_lastFrame) ++g_dupFrames;
    g_lastFrame = frame;
    g_lastUpdateFrame = frame;
    if (!g_api->isMainThread()) ++g_notMain;
    if (g_originalRan && !g_originalRan()) ++g_origNotFirst;
    if (++g_updates == 1) NCMK_LOGI("[frametest] first OnUpdate (frame %d, dt %.4f)", frame, dt);
}

extern "C" NCMK_EXPORT void NCMKMod_OnLateUpdate(float) {
    ++g_late;
    if ((g_frame ? g_frame() : -1) != g_lastUpdateFrame) ++g_lateOutOfOrder;
}

extern "C" NCMK_EXPORT void NCMKMod_OnFixedUpdate(void) { ++g_fixed; }

extern "C" NCMK_EXPORT void NCMKMod_OnSceneLoaded(const char* name) {
    g_scenes += std::string(g_scenes.empty() ? "" : ",") + name;
    NCMK_LOGI("[frametest] OnSceneLoaded(%s)", name);
}

extern "C" NCMK_EXPORT void NCMKMod_Unload(void) {
    NCMK_LOGI("[frametest] updates=%d late=%d fixed=%d duplicateFrames=%d notMainThread=%d originalNotFirst=%d "
              "lateOutOfOrder=%d posted=%d postedOnMain=%d scenes=%s",
              g_updates, g_late, g_fixed, g_dupFrames, g_notMain, g_origNotFirst, g_lateOutOfOrder, g_posted, g_postedOnMain,
              g_scenes.c_str());
    bool ok = g_updates >= 50 && g_late == g_updates && g_fixed > 0 && g_dupFrames == 0 && g_notMain == 0 &&
              g_origNotFirst == 0 && g_lateOutOfOrder == 0 && g_posted == 1 && g_postedOnMain == 1 &&
              g_scenes == "MainMenu,Club";
    NCMK_LOGI("[frametest] RESULT %s", ok ? "PASS" : "FAIL");
}
