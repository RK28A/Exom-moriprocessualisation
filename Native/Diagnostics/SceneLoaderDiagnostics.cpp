// See SceneLoaderDiagnostics.h.

#include "SceneLoaderDiagnostics.h"

#include <atomic>
#include <cstring>
#include <string>
#include <vector>

#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncmk_log.h"
#include "il2cpp_inspect.h"

namespace ncmk {
namespace diag {

namespace IL = NCMK::IL2CPP;
using IL::api;

namespace {
constexpr const char* kTag = "[NCMK][SceneLoader]";
constexpr const char* kManager = "bl_SceneLoaderManager";

struct State {
    SceneLoaderOptions opt;
    bool enabled = false;                 // module alive (reflection worked)
    Il2CppClass* manager = nullptr;
    const MethodInfo* isGlobalLoadingScreen = nullptr;
    const MethodInfo* loadSceneById = nullptr;

    // main thread
    bool firstFrameDone = false;
    bool dumpRequested = false;
    std::string scene;
    float sceneTime = 0.f;
    bool recoveryAttempted = false;
    bool recoveryInProgress = false;      // g_sceneRecoveryInProgress
    float recoveryWatch = 0.f;
    std::string recoveryFromScene;
};
State& S() {
    static State* s = new State();
    return *s;
}
std::atomic<bool> g_confirmedFailure{false};
std::atomic<int> g_failures{0};

// ---------------------------------------------------------------- state dump (main thread)

/// Static fields, loaded instances, singleton-like static getters, IsGlobalLoadingScreen().
void DumpState(const char* why) {
    State& s = S();
    NCMK_LOGI("%s ---- state dump (%s) ----", kTag, why);
    std::vector<std::string> nulls = DumpFieldValues(kTag, s.manager, nullptr);

    // Static getters returning the manager type or a UnityEngine.Object (decided from metadata).
    if (api.il2cpp_class_get_properties && api.il2cpp_property_get_get_method && api.il2cpp_method_get_return_type &&
        api.il2cpp_class_from_type) {
        void* it = nullptr;
        while (const PropertyInfo* p = api.il2cpp_class_get_properties(s.manager, &it)) {
            const MethodInfo* g = api.il2cpp_property_get_get_method(p);
            if (!g || !IL::IsStatic(g) || (api.il2cpp_method_get_param_count && api.il2cpp_method_get_param_count(g) != 0)) continue;
            Il2CppClass* rk = api.il2cpp_class_from_type(api.il2cpp_method_get_return_type(g));
            if (rk != s.manager && !IsUnityObjectClass(rk)) continue;
            Il2CppObject* exc = nullptr;
            Il2CppObject* v = IL::Invoke(g, nullptr, nullptr, &exc);
            const char* name = api.il2cpp_property_get_name(p);
            if (exc) NCMK_LOGW("%s static property %s threw %s", kTag, name, ExceptionText(exc).c_str());
            else if (!v) { NCMK_LOGW("%s NULL PROPERTY: static %s (%s) = NULL", kTag, name, ClassName(rk).c_str()); nulls.push_back(name); }
            else NCMK_LOGI("%s static property %s = %p (%s)%s", kTag, name, static_cast<void*>(v),
                           ClassName(api.il2cpp_object_get_class ? api.il2cpp_object_get_class(v) : rk).c_str(),
                           IsUnityObjectClass(rk) && !UnityObjectAlive(v) ? " [DESTROYED/MISSING]" : "");
            if (v && !exc && rk == s.manager) {
                NCMK_LOGI("%s instance = %p (from static %s)", kTag, static_cast<void*>(v), name);
                for (auto& n : DumpFieldValues(kTag, s.manager, v)) nulls.push_back(n);
            }
        }
    }

    // Loaded instances (MonoBehaviour / ScriptableObject managers).
    if (IsUnityObjectClass(s.manager)) {
        auto objs = FindLoadedObjects(s.manager, 2);
        NCMK_LOGI("%s loaded %s objects: %zu", kTag, kManager, objs.size());
        for (Il2CppObject* o : objs) {
            NCMK_LOGI("%s instance = %p%s", kTag, static_cast<void*>(o), UnityObjectAlive(o) ? "" : " [DESTROYED]");
            for (auto& n : DumpFieldValues(kTag, s.manager, o)) nulls.push_back(n);
        }
    }

    // Reproduce the reported failure without side effects: IsGlobalLoadingScreen() is a query.
    if (s.isGlobalLoadingScreen && IL::IsStatic(s.isGlobalLoadingScreen) &&
        (!api.il2cpp_method_get_param_count || api.il2cpp_method_get_param_count(s.isGlobalLoadingScreen) == 0)) {
        Il2CppObject* exc = nullptr;
        Il2CppObject* r = IL::Invoke(s.isGlobalLoadingScreen, nullptr, nullptr, &exc);
        if (exc) {
            NCMK_LOGW("%s IsGlobalLoadingScreen() threw %s", kTag, ExceptionText(exc).c_str());
            g_confirmedFailure.store(true);
        } else {
            NCMK_LOGI("%s IsGlobalLoadingScreen() = %s", kTag,
                      r && api.il2cpp_object_unbox ? (*static_cast<bool*>(api.il2cpp_object_unbox(r)) ? "true" : "false") : "?");
        }
    }
    if (nulls.empty()) NCMK_LOGI("%s no NULL reference found in the inspected fields", kTag);
    else {
        std::string all;
        for (auto& n : nulls) all += (all.empty() ? "" : ", ") + n;
        NCMK_LOGW("%s NULL references: %s", kTag, all.c_str());
    }
    NCMK_LOGI("%s ---- end of state dump ----", kTag);
}

// ---------------------------------------------------------------- probes (main thread)

void OnProbe(int id, Il2CppObject* self, bool before, bool threw) {
    const char* name = ProbeTag(id);
    if (before) {
        NCMK_LOGI("%s begins (this=%p)", name, static_cast<void*>(self));
        return;
    }
    if (!threw) {
        NCMK_LOGI("%s completed", name);
        return;
    }
    g_confirmedFailure.store(true);
    g_failures.fetch_add(1);
    NCMK_LOGE("%s normal loader failed: threw a managed exception (see the Unity log line just after)", name);
    S().dumpRequested = true;  // dumped on the next dispatched frame, outside the exception path
}

// ---------------------------------------------------------------- scene helpers (main thread)

struct SceneApi {
    bool resolved = false;
    const MethodInfo* getActiveScene = nullptr;
    FieldInfo* handle = nullptr;
    const MethodInfo* buildIndex = nullptr;    // Scene.GetBuildIndexInternal(int)
    const MethodInfo* sceneCount = nullptr;    // SceneManager.get_sceneCountInBuildSettings
    const MethodInfo* scenePath = nullptr;     // SceneUtility.GetScenePathByBuildIndex(int)
    const MethodInfo* loadAsync = nullptr;     // SceneManager.LoadSceneAsync(int)
};
SceneApi& Scenes() {
    static SceneApi a;
    if (!a.resolved) {
        a.resolved = true;
        Il2CppClass* sm = IL::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "SceneManager");
        Il2CppClass* sc = IL::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "Scene");
        Il2CppClass* su = IL::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "SceneUtility");
        const char* i4[] = {"System.Int32"};
        a.getActiveScene = IL::FindMethod(sm, "GetActiveScene", 0);
        a.handle = IL::FindField(sc, "m_Handle");
        a.buildIndex = IL::FindMethodBySignature(sc, "GetBuildIndexInternal", i4, 1);
        a.sceneCount = IL::FindMethod(sm, "get_sceneCountInBuildSettings", 0);
        a.scenePath = IL::FindMethodBySignature(su, "GetScenePathByBuildIndex", i4, 1);
        a.loadAsync = IL::FindMethodBySignature(sm, "LoadSceneAsync", i4, 1);
    }
    return a;
}

int UnboxInt(Il2CppObject* o, int fallback) {
    return o && api.il2cpp_object_unbox ? *static_cast<int32_t*>(api.il2cpp_object_unbox(o)) : fallback;
}

int ActiveSceneIndex() {
    SceneApi& a = Scenes();
    if (!a.getActiveScene || !a.handle || !a.buildIndex) return -1;
    Il2CppObject* exc = nullptr;
    Il2CppObject* boxed = IL::Invoke(a.getActiveScene, nullptr, nullptr, &exc);
    if (exc || !boxed) return -1;
    int32_t h = 0;
    IL::GetField(boxed, a.handle, &h);
    void* args[] = {&h};
    return UnboxInt(IL::Invoke(a.buildIndex, nullptr, args, &exc), -1);
}

// ---------------------------------------------------------------- recovery (main thread)

void TryRecovery() {
    State& s = S();
    if (s.recoveryInProgress || s.recoveryAttempted) return;  // recursion / single shot
    s.recoveryAttempted = true;
    s.recoveryInProgress = true;
    SceneApi& a = Scenes();
    Il2CppObject* exc = nullptr;

    int active = ActiveSceneIndex();
    int count = a.sceneCount ? UnboxInt(IL::Invoke(a.sceneCount, nullptr, nullptr, &exc), -1) : -1;
    int target = s.opt.recoveryScene >= 0 ? s.opt.recoveryScene : (active >= 0 ? active + 1 : -1);
    NCMK_LOGW("%s attempting SceneManager fallback: active scene '%s' (index %d), requested sceneID unknown "
              "(LoadSceneByID is called directly by game code), target index %d (%s), sceneCountInBuildSettings=%d",
              kTag, s.scene.c_str(), active, target, s.opt.recoveryScene >= 0 ? "config" : "active + 1", count);

    std::string path;
    if (a.scenePath && target >= 0) {
        void* args[] = {&target};
        Il2CppObject* p = IL::Invoke(a.scenePath, nullptr, args, &exc);
        if (!exc && p) path = IL::ToUtf8(reinterpret_cast<Il2CppString*>(p));
    }
    if (target < 0 || (count >= 0 && target >= count) || target == active || (a.scenePath && path.empty())) {
        NCMK_LOGW("%s Recovery refused: invalid sceneID %d (count %d, active %d, path '%s')", kTag, target, count, active, path.c_str());
        s.recoveryInProgress = false;
        return;
    }
    if (!a.loadAsync) {
        NCMK_LOGW("%s Recovery refused: SceneManager.LoadSceneAsync(int) not found", kTag);
        s.recoveryInProgress = false;
        return;
    }
    void* args[] = {&target};
    Il2CppObject* op = IL::Invoke(a.loadAsync, nullptr, args, &exc);
    if (exc) {
        NCMK_LOGE("%s fallback failed: LoadSceneAsync(%d) threw %s", kTag, target, ExceptionText(exc).c_str());
        s.recoveryInProgress = false;
        return;
    }
    NCMK_LOGW("%s LoadSceneAsync(%d) '%s' started (AsyncOperation %p) - waiting for the scene change", kTag, target, path.c_str(),
              static_cast<void*>(op));
    s.recoveryFromScene = s.scene;
    s.recoveryWatch = 0.f;
}
}  // namespace

void SceneLoaderInit(const SceneLoaderOptions& options) {
    State& s = S();
    s.opt = options;
    if (!options.diagnostics && !options.recovery) {
        NCMK_LOGI("%s diagnostics off", kTag);
        return;
    }
    s.manager = FindClassByName("Assembly-CSharp", kManager);
    if (!s.manager) {
        NCMK_LOGW("%s %s not found in this game build - module idle", kTag, kManager);
        return;
    }
    s.enabled = true;
    NCMK_LOGI("%s %s found", kTag, kManager);
    DumpMetadata(kTag, s.manager);

    s.loadSceneById = IL::FindMethod(s.manager, "LoadSceneByID", 1);
    s.isGlobalLoadingScreen = IL::FindMethod(s.manager, "IsGlobalLoadingScreen", 0);
    if (s.loadSceneById)
        NCMK_LOGI("%s LoadSceneByID resolved (MethodInfo %p, ptr %p, %s)", kTag, static_cast<const void*>(s.loadSceneById),
                  *reinterpret_cast<void* const*>(s.loadSceneById), IL::IsStatic(s.loadSceneById) ? "static" : "instance");
    else NCMK_LOGW("%s LoadSceneByID(int) not found", kTag);
    if (s.isGlobalLoadingScreen)
        NCMK_LOGI("%s IsGlobalLoadingScreen resolved (MethodInfo %p, ptr %p, %s)", kTag,
                  static_cast<const void*>(s.isGlobalLoadingScreen), *reinterpret_cast<void* const*>(s.isGlobalLoadingScreen),
                  IL::IsStatic(s.isGlobalLoadingScreen) ? "static" : "instance");
    else NCMK_LOGW("%s IsGlobalLoadingScreen() not found", kTag);
    NCMK_LOGI("%s note: LoadSceneByID / IsGlobalLoadingScreen are called directly by compiled game code, so NCMK "
              "cannot run code right before them; the engine-invoked Awake() of their callers is probed instead.", kTag);

    // Callers seen in the device log: bl_SceneLoader.Awake, Lovatto.SceneLoader.bl_SceneLoaderCanvas.Awake.
    for (Il2CppClass* k : FindClassesByPrefix("Assembly-CSharp", "bl_SceneLoader")) {
        if (k == s.manager) continue;
        DumpMetadata(kTag, k);
        const MethodInfo* awake = IL::FindMethod(k, "Awake", 0);
        if (!awake) continue;
        InstallProbe((std::string(kTag) + " " + ClassName(k) + ".Awake").c_str(), awake, OnProbe);
    }
    NCMK_LOGI("%s recovery %s (delay %d s, target %s)", kTag, options.recovery ? "ON" : "off", options.recoveryDelay,
              options.recoveryScene >= 0 ? std::to_string(options.recoveryScene).c_str() : "active index + 1");
}

void SceneLoaderOnFrame(const char* sceneLoaded, float dt) {
    State& s = S();
    if (!s.enabled) return;
    if (sceneLoaded) {
        if (s.recoveryInProgress && s.scene != sceneLoaded) {
            NCMK_LOGI("%s fallback succeeded: '%s' -> '%s'", kTag, s.scene.c_str(), sceneLoaded);
            s.recoveryInProgress = false;
        }
        s.scene = sceneLoaded;
        s.sceneTime = 0.f;
        if (s.firstFrameDone) DumpState((std::string("scene loaded: ") + sceneLoaded).c_str());
    } else {
        s.sceneTime += dt > 0.f && dt < 1.f ? dt : 0.f;
    }
    if (!s.firstFrameDone) {
        s.firstFrameDone = true;
        DumpState("first dispatched frame");
    } else if (s.dumpRequested) {
        s.dumpRequested = false;
        DumpState("after a failed loader call");
    }

    if (s.recoveryInProgress) {
        s.recoveryWatch += dt > 0.f && dt < 1.f ? dt : 0.f;
        if (s.recoveryWatch > 60.f) {
            NCMK_LOGW("%s fallback did not change the scene within 60 s (still '%s') - recovery stays off", kTag, s.scene.c_str());
            s.recoveryInProgress = false;
        }
        return;
    }
    if (s.opt.recovery && !s.recoveryAttempted && g_confirmedFailure.load() && s.sceneTime >= static_cast<float>(s.opt.recoveryDelay)) {
        NCMK_LOGW("%s stuck in '%s' for %.0f s after a confirmed scene loader failure", kTag, s.scene.c_str(), s.sceneTime);
        TryRecovery();
    }
}

void SceneLoaderRestore() { RestoreProbes(); }

}  // namespace diag
}  // namespace ncmk
