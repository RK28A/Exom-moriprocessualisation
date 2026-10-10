// NCMK native loader (libncmkloader.so).
//
// Injected as a DT_NEEDED dependency of Unity's libmain.so, so the dynamic linker runs our
// constructor right before libmain.so is initialised - before libunity.so / libil2cpp.so exist.
// The constructor therefore only logs and starts a worker thread; everything else waits.
//
// Startup stages (all logged, to make crash diagnosis easy):
//   STAGE 01 Loader constructor
//   STAGE 02 Worker thread
//   STAGE 03 libunity detected
//   STAGE 04 libil2cpp detected
//   STAGE 05 IL2CPP exports resolved
//   STAGE 06 Runtime initialized
//   STAGE 07 Plugin manager initialized
//   STAGE 08 Mods loaded
//   STAGE 09 Main-thread dispatcher (which per-frame target was hooked, or that none worked)
//
// No game code is modified. The only runtime change is the main-thread dispatcher, which swaps
// two MethodInfo function-pointer slots of one per-frame managed method (see Hooks/main_thread.cpp)
// and restores them on unload. It is never installed in Safe Mode.

#include <dlfcn.h>
#include <link.h>
#include <pthread.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "../../SDK/Core/ncmk_mod_api.h"
#include "../Hooks/main_thread.h"
#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncmk_log.h"
#include "../Plugins/plugin_manager.h"
#include "../Unity/unity_runtime.h"
#include "environment.h"

namespace {

using namespace std::chrono_literals;
namespace IL = NCMK::IL2CPP;

struct LoaderState {
    std::string package;
    std::string modsRoot;
    std::string selfPath;
    std::string gameVersion = "unknown";
    std::string unityVersion = "unknown";
    ncmk::DeviceInfo device;
    bool safeMode = false;
    ncmk::LoaderConfig config;
    ncmk::PluginManager plugins;
    std::vector<std::string> assemblyNames;
    std::vector<std::string> modNames;
};

// Heap-allocated on first use instead of a global object: the loader constructor can run before
// this library's C++ static initializers, and the worker thread must never race a constructor.
LoaderState& G() {
    static LoaderState* state = new LoaderState();  // thread-safe, never destroyed
    return *state;
}
NCMKHostApi g_host{};  // POD: zero-initialized at load time, no dynamic initializer
std::atomic<bool> g_started{false};
std::atomic<bool> g_stopping{false};
std::atomic<bool> g_loopRunning{false};

void Stage(int n, const char* what) { NCMK_LOGI("[NCMK] STAGE %02d %s", n, what); }

// ---------------------------------------------------------------------------- lib detection

struct FindCtx {
    const char* suffix;
    std::string path;
};

int FindLibCallback(struct dl_phdr_info* info, size_t, void* data) {
    auto* ctx = static_cast<FindCtx*>(data);
    if (!info->dlpi_name) return 0;
    size_t n = strlen(info->dlpi_name), m = strlen(ctx->suffix);
    if (n >= m && strcmp(info->dlpi_name + n - m, ctx->suffix) == 0) {
        ctx->path = info->dlpi_name;
        return 1;
    }
    return 0;
}

/// Returns a handle to an already-loaded library without loading it ourselves.
void* LoadedLibrary(const char* soname) {
    if (void* h = dlopen(soname, RTLD_NOW | RTLD_NOLOAD)) return h;
    std::string suffix = std::string("/") + soname;
    FindCtx ctx{suffix.c_str(), {}};
    dl_iterate_phdr(FindLibCallback, &ctx);
    if (!ctx.path.empty()) return dlopen(ctx.path.c_str(), RTLD_NOW | RTLD_NOLOAD);
    return nullptr;
}

template <typename Pred>
bool WaitFor(Pred pred, std::chrono::milliseconds timeout, std::chrono::milliseconds step, const char* what) {
    auto start = std::chrono::steady_clock::now();
    auto nextReport = start + 10s;
    while (!pred()) {
        auto now = std::chrono::steady_clock::now();
        if (now - start > timeout) {
            NCMK_LOGE("[NCMK] Timed out waiting for %s", what);
            return false;
        }
        if (now > nextReport) {
            NCMK_LOGI("[NCMK] Still waiting for %s...", what);
            nextReport = now + 10s;
        }
        std::this_thread::sleep_for(step);
    }
    return true;
}

// ---------------------------------------------------------------------------- host API

void HostLog(int level, const char* modId, const char* message) {
    ncmk::log::Write(static_cast<ncmk::log::Level>(level < 0 ? 0 : level > 3 ? 3 : level), "[%s] %s", modId ? modId : "mod",
                    message ? message : "");
}
int HostIl2CppReady() { return IL::IsReady() ? 1 : 0; }
int HostModCount() { return static_cast<int>(G().modNames.size()); }
const char* HostModName(int i) { return i >= 0 && i < static_cast<int>(G().modNames.size()) ? G().modNames[i].c_str() : ""; }
void* HostResolve(const char* name) { return IL::ResolveExport(name); }
int HostAssemblyCount() { return static_cast<int>(G().assemblyNames.size()); }
const char* HostAssemblyName(int i) {
    return i >= 0 && i < static_cast<int>(G().assemblyNames.size()) ? G().assemblyNames[i].c_str() : "";
}
NCMKImage* HostFindImage(const char* n) { return const_cast<Il2CppImage*>(IL::FindAssembly(n)); }
NCMKClass* HostFindClass(const char* a, const char* ns, const char* n) { return IL::FindClass(a, ns, n); }
NCMKMethod* HostFindMethod(NCMKClass* k, const char* n, int argc) {
    return const_cast<MethodInfo*>(IL::FindMethod(static_cast<Il2CppClass*>(k), n, argc));
}
NCMKField* HostFindField(NCMKClass* k, const char* n) { return IL::FindField(static_cast<Il2CppClass*>(k), n); }
NCMKObject* HostInvoke(NCMKMethod* m, void* inst, void** args, NCMKObject** exc) {
    Il2CppObject* e = nullptr;
    Il2CppObject* r = IL::Invoke(static_cast<const MethodInfo*>(m), inst, args, &e);
    if (exc) *exc = e;
    return r;
}
NCMKString* HostNewString(const char* s) { return IL::NewString(s); }
int HostStringToUtf8(NCMKString* s, char* buf, int size) {
    if (!buf || size <= 0) return -1;
    std::string u = IL::ToUtf8(static_cast<Il2CppString*>(s));
    int n = static_cast<int>(u.size() < static_cast<size_t>(size - 1) ? u.size() : static_cast<size_t>(size - 1));
    memcpy(buf, u.data(), n);
    buf[n] = '\0';
    return n;
}
void HostGetField(NCMKObject* o, NCMKField* f, void* out) { IL::GetField(static_cast<Il2CppObject*>(o), static_cast<FieldInfo*>(f), out); }
void HostSetField(NCMKObject* o, NCMKField* f, void* v) { IL::SetField(static_cast<Il2CppObject*>(o), static_cast<FieldInfo*>(f), v); }
void HostGetStatic(NCMKField* f, void* out) { IL::GetStaticField(static_cast<FieldInfo*>(f), out); }
void HostSetStatic(NCMKField* f, void* v) { IL::SetStaticField(static_cast<FieldInfo*>(f), v); }
void* HostUnbox(NCMKObject* o) { return o && IL::api.il2cpp_object_unbox ? IL::api.il2cpp_object_unbox(static_cast<Il2CppObject*>(o)) : nullptr; }
int HostAttach() { return IL::AttachCurrentThread() ? 1 : 0; }

// API 2
int HostIsMainThread() { return ncmk::hooks::IsMainThread() ? 1 : 0; }
int HostRunOnMainThread(NCMKMainThreadFn fn, void* user) { return ncmk::hooks::Post(fn, user) ? 1 : 0; }
NCMKMethod* HostFindMethodBySignature(NCMKClass* k, const char* n, const char* const* suffixes, int count) {
    return const_cast<MethodInfo*>(IL::FindMethodBySignature(static_cast<Il2CppClass*>(k), n, suffixes, count));
}
NCMKClass* HostFindNestedClass(NCMKClass* outer, const char* n) { return IL::FindNestedClass(static_cast<Il2CppClass*>(outer), n); }
NCMKObject* HostTypeOf(NCMKClass* k) { return IL::TypeOf(static_cast<Il2CppClass*>(k)); }
NCMKObject* HostNewObject(NCMKClass* k) { return IL::NewObject(static_cast<Il2CppClass*>(k)); }
uint32_t HostPin(NCMKObject* o) { return IL::Pin(static_cast<Il2CppObject*>(o)); }
void HostUnpin(uint32_t h) { IL::Unpin(h); }
int64_t HostGetEnumValue(NCMKClass* k, const char* n, int64_t fallback) {
    return IL::GetEnumValue(static_cast<Il2CppClass*>(k), n, fallback);
}

void BuildHostApi() {
    g_host.structSize = sizeof(NCMKHostApi);
    g_host.apiVersion = NCMK_LOADER_API;
    g_host.loaderVersion = ncmk::kLoaderVersion;
    g_host.packageName = G().package.c_str();
    g_host.gameVersion = G().gameVersion.c_str();
    g_host.unityVersion = G().unityVersion.c_str();
    g_host.modsRoot = G().modsRoot.c_str();
    g_host.safeMode = G().safeMode ? 1 : 0;
    g_host.log = HostLog;
    g_host.isIl2CppReady = HostIl2CppReady;
    g_host.getLoadedModCount = HostModCount;
    g_host.getLoadedModName = HostModName;
    g_host.il2cppResolve = HostResolve;
    g_host.getAssemblyCount = HostAssemblyCount;
    g_host.getAssemblyName = HostAssemblyName;
    g_host.findImage = HostFindImage;
    g_host.findClass = HostFindClass;
    g_host.findMethod = HostFindMethod;
    g_host.findField = HostFindField;
    g_host.invoke = HostInvoke;
    g_host.newString = HostNewString;
    g_host.stringToUtf8 = HostStringToUtf8;
    g_host.getField = HostGetField;
    g_host.setField = HostSetField;
    g_host.getStaticField = HostGetStatic;
    g_host.setStaticField = HostSetStatic;
    g_host.unbox = HostUnbox;
    g_host.attachCurrentThread = HostAttach;
    g_host.deviceName = G().device.displayName.c_str();
    g_host.deviceCodename = G().device.codename.c_str();
    g_host.deviceModel = G().device.model.c_str();
    g_host.isMainThread = HostIsMainThread;
    g_host.runOnMainThread = HostRunOnMainThread;
    g_host.findMethodBySignature = HostFindMethodBySignature;
    g_host.findNestedClass = HostFindNestedClass;
    g_host.typeOf = HostTypeOf;
    g_host.newObject = HostNewObject;
    g_host.pin = HostPin;
    g_host.unpin = HostUnpin;
    g_host.getEnumValue = HostGetEnumValue;
}

// Unity main thread, once per frame (called by the dispatcher).
void OnFrame(const ncmk::hooks::Frame& f) {
    if (g_stopping.load()) return;
    G().plugins.FrameAll(f.deltaTime, f.fixedSteps, f.sceneLoaded);
}

// ---------------------------------------------------------------------------- mod discovery

std::vector<ncmk::ModCandidate> DiscoverMods() {
    std::vector<ncmk::ModCandidate> out;

    // 1) Mods embedded in the patched APK (lib/arm64-v8a/libncmkmod_*.so, legacy libncsmod_*.so).
    const std::string& self = G().selfPath;
    size_t bang = self.find("!/");
    if (bang != std::string::npos) {
        // Loaded directly from the APK (extractNativeLibs=false): the linker resolves sonames itself.
        for (const auto& name : ncmk::ListEmbeddedMods(self.substr(0, bang))) out.push_back({name, "apk"});
    } else if (!self.empty()) {
        std::string dir = self.substr(0, self.rfind('/'));
        for (const auto& name : ncmk::ListModFilesInDir(dir)) out.push_back({dir + "/" + name, "apk-extracted"});
    }

    // 2) External mods from NCMKMods/Mods. Shared storage is mounted noexec, so each file is
    //    copied into the app's private directory before dlopen.
    if (!G().modsRoot.empty()) {
        std::string ext = G().modsRoot + "/Mods";
        auto files = ncmk::ListModFilesInDir(ext);
        std::string priv = ncmk::AppDataDir();
#if defined(NCMK_HOST_TEST)
        if (priv.empty()) priv = G().modsRoot + "/Cache";
#endif
        if (!files.empty() && priv.empty()) NCMK_LOGW("[NCMK] App data dir not writable; external mods skipped (embed them in the APK).");
        for (const auto& name : files) {
            if (priv.empty()) break;
            bool dup = false;
            for (const auto& c : out)
                if (c.dlopenName.size() >= name.size() &&
                    c.dlopenName.compare(c.dlopenName.size() - name.size(), name.size(), name) == 0)
                    dup = true;
            if (dup) {
                NCMK_LOGW("[NCMK] %s exists both embedded and in Mods/ - using the embedded copy", name.c_str());
                continue;
            }
            std::string cacheDir = priv + "/ncmk_mods";
            ncmk::MakeDirs(cacheDir);
            std::string dst = cacheDir + "/" + name;
            if (ncmk::CopyFile(ext + "/" + name, dst)) out.push_back({dst, "external"});
            else NCMK_LOGE("[NCMK] Could not copy %s to %s", name.c_str(), dst.c_str());
        }
    }
    return out;
}

// ---------------------------------------------------------------------------- worker

void* WorkerMain(void*) {
    Stage(2, "Worker thread");

    G().package = ncmk::CurrentPackageName();
    if (G().package != ncmk::kTargetPackage) {
        NCMK_LOGE("[NCMK] Package '%s' is not %s - loader stays idle", G().package.c_str(), ncmk::kTargetPackage);
        return nullptr;
    }
    NCMK_LOGI("[NCMK] Package verified: %s", G().package.c_str());
    G().device = ncmk::DetectDevice();
    NCMK_LOGI("[NCMK] Device: %s (%s / %s), Android %s (SDK %s)", G().device.displayName.c_str(), G().device.model.c_str(),
             G().device.codename.c_str(), G().device.androidRelease.c_str(), G().device.sdk.c_str());
    if (!G().device.known) NCMK_LOGW("[NCMK] Unrecognised headset - continuing (all Quests share the arm64 build)");
    else if (!G().device.supported) NCMK_LOGW("[NCMK] %s is not a supported headset", G().device.displayName.c_str());
    G().selfPath = ncmk::SelfLibraryPath();
    NCMK_LOGI("[NCMK] Loader path: %s", G().selfPath.c_str());

    G().modsRoot = ncmk::ResolveModsRoot(G().package);
    if (G().modsRoot.empty()) {
        NCMK_LOGW("[NCMK] No writable NCMKMods folder (storage permission?) - file logging and external mods disabled");
    } else {
        G().config = ncmk::LoadConfig(G().modsRoot);
        ncmk::log::SetFileEnabled(G().config.logging);
        if (G().config.logging && ncmk::log::OpenFile(G().modsRoot + "/Logs/latest.log"))
            NCMK_LOGI("[NCMK] Log file: %s", ncmk::log::FilePath().c_str());
        NCMK_LOGI("[NCMK] Mods root: %s", G().modsRoot.c_str());
    }
    G().safeMode = G().config.safeMode || ncmk::SafeModeFlagPresent(G().modsRoot);
    if (G().safeMode) NCMK_LOGW("[NCMK] SAFE MODE: no third-party mods will be loaded");

    if (!WaitFor([] { return LoadedLibrary("libunity.so") != nullptr; }, 120s, 50ms, "libunity.so")) return nullptr;
    Stage(3, "libunity detected");

    NCMK_LOGI("[NCMK] Waiting for libil2cpp");
    void* il2cpp = nullptr;
    if (!WaitFor([&] { return (il2cpp = LoadedLibrary("libil2cpp.so")) != nullptr; }, 180s, 50ms, "libil2cpp.so")) return nullptr;
    Stage(4, "libil2cpp detected");
    NCMK_LOGI("[NCMK] libil2cpp.so found");

    if (!IL::ResolveExports(il2cpp)) {
        NCMK_LOGE("[NCMK] IL2CPP API incomplete - mods disabled for this session");
        return nullptr;
    }
    Stage(5, "IL2CPP exports resolved");
    NCMK_LOGI("[NCMK] IL2CPP API ready");

    if (!WaitFor([] { return IL::RuntimeInitialized(); }, 300s, 100ms, "IL2CPP runtime initialization")) return nullptr;
    if (!IL::AttachCurrentThread()) {
        NCMK_LOGE("[NCMK] il2cpp_thread_attach failed");
        return nullptr;
    }
    IL::SetReady(true);
    Stage(6, "Runtime initialized");
    NCMK_LOGI("[NCMK] IL2CPP initialized");

    G().assemblyNames = IL::ListAssemblies();
    NCMK_LOGI("[NCMK] %zu assemblies:", G().assemblyNames.size());
    for (const auto& a : G().assemblyNames) {
        size_t classes = IL::ClassCount(IL::FindAssembly(a.c_str()));
        NCMK_LOGI("[NCMK]   %s (%zu classes)", a.c_str(), classes);
    }

    std::string uv = NCMK::Unity::GetUnityVersion();
    std::string gv = NCMK::Unity::GetApplicationVersion();
    if (!uv.empty()) G().unityVersion = uv;
    if (!gv.empty()) G().gameVersion = gv;
    NCMK_LOGI("[NCMK] Unity %s, game version %s", G().unityVersion.c_str(), G().gameVersion.c_str());

    // FIFTH TEST: find a known class without changing anything.
    if (IL::FindClass("UnityEngine.CoreModule", "UnityEngine", "Application"))
        NCMK_LOGI("[NCMK] Class found: UnityEngine.Application");
    else
        NCMK_LOGW("[NCMK] UnityEngine.Application not found");

    BuildHostApi();
    G().plugins.SetDisabledIds(G().config.disabledMods);
    Stage(7, "Plugin manager initialized");

    if (G().safeMode) {
        NCMK_LOGW("[NCMK] Safe mode active - skipping mods");
    } else {
        NCMK_LOGI("[NCMK] Loading mods");
        G().plugins.LoadAll(DiscoverMods(), &g_host);
        for (const auto& m : G().plugins.Mods()) G().modNames.push_back(m.displayName);
    }
    Stage(8, "Mods loaded");
    NCMK_LOGI("[NCMK] NCMK started successfully");

    if (G().safeMode) {
        ncmk::hooks::Disable("Safe Mode");
        NCMK_LOGI("[NCMK] STAGE 09 Main-thread dispatcher: not installed (Safe Mode)");
        return nullptr;
    }
    if (G().plugins.Mods().empty()) {
        ncmk::hooks::Disable("no mods loaded");
        NCMK_LOGI("[NCMK] STAGE 09 Main-thread dispatcher: not installed (no mods loaded)");
        return nullptr;
    }
    std::chrono::milliseconds perTarget = 5000ms;
#if defined(NCMK_HOST_TEST)
    if (const char* t = getenv("NCMK_HOOK_TIMEOUT_MS")) perTarget = std::chrono::milliseconds(atoi(t));
#endif
    ncmk::hooks::Install(ncmk::hooks::DefaultTargets(), OnFrame, perTarget);

    if (!G().plugins.AnyBackgroundTick()) return nullptr;
    // OnBackgroundTick (and the API 1 Update): loader worker thread, ~10 Hz. Not Unity's main thread.
    g_loopRunning.store(true);
    auto last = std::chrono::steady_clock::now();
    while (!g_stopping.load()) {
        std::this_thread::sleep_for(100ms);
        auto now = std::chrono::steady_clock::now();
        float dt = std::chrono::duration<float>(now - last).count();
        last = now;
        if (!g_stopping.load()) G().plugins.BackgroundTickAll(dt);
    }
    g_loopRunning.store(false);
    return nullptr;
}

}  // namespace

extern "C" NCMK_EXPORT const NCMKHostApi* NCMK_GetHostApi(void) { return IL::IsReady() ? &g_host : nullptr; }

/// Unload: restores the dispatcher's MethodInfo pointers, stops the background tick and unloads
/// every mod (NCMKMod_Unload). Call it from the Unity main thread or at shutdown.
extern "C" NCMK_EXPORT void NCMK_Shutdown(void) {
    if (g_stopping.exchange(true)) return;
    ncmk::hooks::Restore();
    for (int i = 0; i < 50 && g_loopRunning.load(); ++i) std::this_thread::sleep_for(20ms);
    G().plugins.UnloadAll();
    NCMK_LOGI("[NCMK] Shutdown complete");
}

__attribute__((destructor)) static void loader_fini() {
    // Process exit or dlclose: never leave a MethodInfo pointing into an unmapped library.
    ncmk::hooks::Restore();
}

__attribute__((constructor)) static void loader_init() {
    if (g_started.exchange(true)) return;
    G();  // construct the state before the worker thread exists
    Stage(1, "Loader constructor");
    NCMK_LOGI("[NCMK] Hello from NCMK");
    NCMK_LOGI("[NCMK] Loader %s (API %d)", ncmk::kLoaderVersion, NCMK_LOADER_API);

    pthread_t thread;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 1 << 20);
    if (pthread_create(&thread, &attr, WorkerMain, nullptr) != 0) NCMK_LOGE("[NCMK] Failed to start worker thread");
    pthread_attr_destroy(&attr);
}
