// NightClubModKit native loader (libncsloader.so).
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
//
// No hooks are installed and no game code is modified.

#include <dlfcn.h>
#include <link.h>
#include <pthread.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "../../SDK/Core/ncs_mod_api.h"
#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncs_log.h"
#include "../Plugins/plugin_manager.h"
#include "../Unity/unity_runtime.h"
#include "environment.h"

namespace {

using namespace std::chrono_literals;
namespace IL = NCS::IL2CPP;

struct LoaderState {
    std::string package;
    std::string modsRoot;
    std::string selfPath;
    std::string gameVersion = "unknown";
    std::string unityVersion = "unknown";
    ncs::DeviceInfo device;
    bool safeMode = false;
    ncs::LoaderConfig config;
    ncs::PluginManager plugins;
    std::vector<std::string> assemblyNames;
    std::vector<std::string> modNames;
};

LoaderState g;
NCSHostApi g_host{};
std::atomic<bool> g_started{false};

void Stage(int n, const char* what) { NCS_LOGI("[NCS] STAGE %02d %s", n, what); }

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
            NCS_LOGE("[NCS] Timed out waiting for %s", what);
            return false;
        }
        if (now > nextReport) {
            NCS_LOGI("[NCS] Still waiting for %s...", what);
            nextReport = now + 10s;
        }
        std::this_thread::sleep_for(step);
    }
    return true;
}

// ---------------------------------------------------------------------------- host API

void HostLog(int level, const char* modId, const char* message) {
    ncs::log::Write(static_cast<ncs::log::Level>(level < 0 ? 0 : level > 3 ? 3 : level), "[%s] %s", modId ? modId : "mod",
                    message ? message : "");
}
int HostIl2CppReady() { return IL::IsReady() ? 1 : 0; }
int HostModCount() { return static_cast<int>(g.modNames.size()); }
const char* HostModName(int i) { return i >= 0 && i < static_cast<int>(g.modNames.size()) ? g.modNames[i].c_str() : ""; }
void* HostResolve(const char* name) { return IL::ResolveExport(name); }
int HostAssemblyCount() { return static_cast<int>(g.assemblyNames.size()); }
const char* HostAssemblyName(int i) {
    return i >= 0 && i < static_cast<int>(g.assemblyNames.size()) ? g.assemblyNames[i].c_str() : "";
}
NCSImage* HostFindImage(const char* n) { return const_cast<Il2CppImage*>(IL::FindAssembly(n)); }
NCSClass* HostFindClass(const char* a, const char* ns, const char* n) { return IL::FindClass(a, ns, n); }
NCSMethod* HostFindMethod(NCSClass* k, const char* n, int argc) {
    return const_cast<MethodInfo*>(IL::FindMethod(static_cast<Il2CppClass*>(k), n, argc));
}
NCSField* HostFindField(NCSClass* k, const char* n) { return IL::FindField(static_cast<Il2CppClass*>(k), n); }
NCSObject* HostInvoke(NCSMethod* m, void* inst, void** args, NCSObject** exc) {
    Il2CppObject* e = nullptr;
    Il2CppObject* r = IL::Invoke(static_cast<const MethodInfo*>(m), inst, args, &e);
    if (exc) *exc = e;
    return r;
}
NCSString* HostNewString(const char* s) { return IL::NewString(s); }
int HostStringToUtf8(NCSString* s, char* buf, int size) {
    if (!buf || size <= 0) return -1;
    std::string u = IL::ToUtf8(static_cast<Il2CppString*>(s));
    int n = static_cast<int>(u.size() < static_cast<size_t>(size - 1) ? u.size() : static_cast<size_t>(size - 1));
    memcpy(buf, u.data(), n);
    buf[n] = '\0';
    return n;
}
void HostGetField(NCSObject* o, NCSField* f, void* out) { IL::GetField(static_cast<Il2CppObject*>(o), static_cast<FieldInfo*>(f), out); }
void HostSetField(NCSObject* o, NCSField* f, void* v) { IL::SetField(static_cast<Il2CppObject*>(o), static_cast<FieldInfo*>(f), v); }
void HostGetStatic(NCSField* f, void* out) { IL::GetStaticField(static_cast<FieldInfo*>(f), out); }
void HostSetStatic(NCSField* f, void* v) { IL::SetStaticField(static_cast<FieldInfo*>(f), v); }
void* HostUnbox(NCSObject* o) { return o && IL::api.il2cpp_object_unbox ? IL::api.il2cpp_object_unbox(static_cast<Il2CppObject*>(o)) : nullptr; }
int HostAttach() { return IL::AttachCurrentThread() ? 1 : 0; }

void BuildHostApi() {
    g_host.structSize = sizeof(NCSHostApi);
    g_host.apiVersion = NCS_LOADER_API;
    g_host.loaderVersion = ncs::kLoaderVersion;
    g_host.packageName = g.package.c_str();
    g_host.gameVersion = g.gameVersion.c_str();
    g_host.unityVersion = g.unityVersion.c_str();
    g_host.modsRoot = g.modsRoot.c_str();
    g_host.safeMode = g.safeMode ? 1 : 0;
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
    g_host.deviceName = g.device.displayName.c_str();
    g_host.deviceCodename = g.device.codename.c_str();
    g_host.deviceModel = g.device.model.c_str();
}

// ---------------------------------------------------------------------------- mod discovery

std::vector<ncs::ModCandidate> DiscoverMods() {
    std::vector<ncs::ModCandidate> out;

    // 1) Mods embedded in the patched APK (lib/arm64-v8a/libncsmod_*.so).
    const std::string& self = g.selfPath;
    size_t bang = self.find("!/");
    if (bang != std::string::npos) {
        // Loaded directly from the APK (extractNativeLibs=false): the linker resolves sonames itself.
        for (const auto& name : ncs::ListEmbeddedMods(self.substr(0, bang))) out.push_back({name, "apk"});
    } else if (!self.empty()) {
        std::string dir = self.substr(0, self.rfind('/'));
        for (const auto& name : ncs::ListModFilesInDir(dir)) out.push_back({dir + "/" + name, "apk-extracted"});
    }

    // 2) External mods from NightClubMods/Mods. Shared storage is mounted noexec, so each file is
    //    copied into the app's private directory before dlopen.
    if (!g.modsRoot.empty()) {
        std::string ext = g.modsRoot + "/Mods";
        auto files = ncs::ListModFilesInDir(ext);
        std::string priv = ncs::AppDataDir();
#if defined(NCS_HOST_TEST)
        if (priv.empty()) priv = g.modsRoot + "/Cache";
#endif
        if (!files.empty() && priv.empty()) NCS_LOGW("[NCS] App data dir not writable; external mods skipped (embed them in the APK).");
        for (const auto& name : files) {
            if (priv.empty()) break;
            bool dup = false;
            for (const auto& c : out)
                if (c.dlopenName.size() >= name.size() &&
                    c.dlopenName.compare(c.dlopenName.size() - name.size(), name.size(), name) == 0)
                    dup = true;
            if (dup) {
                NCS_LOGW("[NCS] %s exists both embedded and in Mods/ - using the embedded copy", name.c_str());
                continue;
            }
            std::string cacheDir = priv + "/ncs_mods";
            ncs::MakeDirs(cacheDir);
            std::string dst = cacheDir + "/" + name;
            if (ncs::CopyFile(ext + "/" + name, dst)) out.push_back({dst, "external"});
            else NCS_LOGE("[NCS] Could not copy %s to %s", name.c_str(), dst.c_str());
        }
    }
    return out;
}

// ---------------------------------------------------------------------------- worker

void* WorkerMain(void*) {
    Stage(2, "Worker thread");

    g.package = ncs::CurrentPackageName();
    if (g.package != ncs::kTargetPackage) {
        NCS_LOGE("[NCS] Package '%s' is not %s - loader stays idle", g.package.c_str(), ncs::kTargetPackage);
        return nullptr;
    }
    NCS_LOGI("[NCS] Package verified: %s", g.package.c_str());
    g.device = ncs::DetectDevice();
    NCS_LOGI("[NCS] Device: %s (%s / %s), Android %s (SDK %s)", g.device.displayName.c_str(), g.device.model.c_str(),
             g.device.codename.c_str(), g.device.androidRelease.c_str(), g.device.sdk.c_str());
    if (!g.device.known) NCS_LOGW("[NCS] Unrecognised headset - continuing (all Quests share the arm64 build)");
    else if (!g.device.supported) NCS_LOGW("[NCS] %s is not a supported headset", g.device.displayName.c_str());
    g.selfPath = ncs::SelfLibraryPath();
    NCS_LOGI("[NCS] Loader path: %s", g.selfPath.c_str());

    g.modsRoot = ncs::ResolveModsRoot(g.package);
    if (g.modsRoot.empty()) {
        NCS_LOGW("[NCS] No writable NightClubMods folder (storage permission?) - file logging and external mods disabled");
    } else {
        g.config = ncs::LoadConfig(g.modsRoot);
        ncs::log::SetFileEnabled(g.config.logging);
        if (g.config.logging && ncs::log::OpenFile(g.modsRoot + "/Logs/latest.log"))
            NCS_LOGI("[NCS] Log file: %s", ncs::log::FilePath().c_str());
        NCS_LOGI("[NCS] Mods root: %s", g.modsRoot.c_str());
    }
    g.safeMode = g.config.safeMode || ncs::SafeModeFlagPresent(g.modsRoot);
    if (g.safeMode) NCS_LOGW("[NCS] SAFE MODE: no third-party mods will be loaded");

    if (!WaitFor([] { return LoadedLibrary("libunity.so") != nullptr; }, 120s, 50ms, "libunity.so")) return nullptr;
    Stage(3, "libunity detected");

    NCS_LOGI("[NCS] Waiting for libil2cpp");
    void* il2cpp = nullptr;
    if (!WaitFor([&] { return (il2cpp = LoadedLibrary("libil2cpp.so")) != nullptr; }, 180s, 50ms, "libil2cpp.so")) return nullptr;
    Stage(4, "libil2cpp detected");
    NCS_LOGI("[NCSLoader] libil2cpp.so found");

    if (!IL::ResolveExports(il2cpp)) {
        NCS_LOGE("[NCS] IL2CPP API incomplete - mods disabled for this session");
        return nullptr;
    }
    Stage(5, "IL2CPP exports resolved");
    NCS_LOGI("[NCSLoader] IL2CPP API ready");

    if (!WaitFor([] { return IL::RuntimeInitialized(); }, 300s, 100ms, "IL2CPP runtime initialization")) return nullptr;
    if (!IL::AttachCurrentThread()) {
        NCS_LOGE("[NCS] il2cpp_thread_attach failed");
        return nullptr;
    }
    IL::SetReady(true);
    Stage(6, "Runtime initialized");
    NCS_LOGI("[NCS] IL2CPP initialized");

    g.assemblyNames = IL::ListAssemblies();
    NCS_LOGI("[NCS] %zu assemblies:", g.assemblyNames.size());
    for (const auto& a : g.assemblyNames) {
        size_t classes = IL::ClassCount(IL::FindAssembly(a.c_str()));
        NCS_LOGI("[NCS]   %s (%zu classes)", a.c_str(), classes);
    }

    std::string uv = NCS::Unity::GetUnityVersion();
    std::string gv = NCS::Unity::GetApplicationVersion();
    if (!uv.empty()) g.unityVersion = uv;
    if (!gv.empty()) g.gameVersion = gv;
    NCS_LOGI("[NCS] Unity %s, game version %s", g.unityVersion.c_str(), g.gameVersion.c_str());

    // FIFTH TEST: find a known class without changing anything.
    if (IL::FindClass("UnityEngine.CoreModule", "UnityEngine", "Application"))
        NCS_LOGI("[NCSLoader] Class found: UnityEngine.Application");
    else
        NCS_LOGW("[NCS] UnityEngine.Application not found");

    BuildHostApi();
    g.plugins.SetDisabledIds(g.config.disabledMods);
    Stage(7, "Plugin manager initialized");

    if (g.safeMode) {
        NCS_LOGW("[NCS] Safe mode active - skipping mods");
    } else {
        NCS_LOGI("[NCS] Loading mods");
        g.plugins.LoadAll(DiscoverMods(), &g_host);
        for (const auto& m : g.plugins.Mods()) g.modNames.push_back(m.displayName);
    }
    Stage(8, "Mods loaded");
    NCS_LOGI("[NCS] NCSLoader started successfully");

    if (g.plugins.Mods().empty()) return nullptr;

    // API 1 update tick: background thread, ~10 Hz. Not Unity's main thread.
    auto last = std::chrono::steady_clock::now();
    for (;;) {
        std::this_thread::sleep_for(100ms);
        auto now = std::chrono::steady_clock::now();
        float dt = std::chrono::duration<float>(now - last).count();
        last = now;
        g.plugins.UpdateAll(dt);
    }
    return nullptr;
}

}  // namespace

extern "C" NCS_EXPORT const NCSHostApi* NCS_GetHostApi(void) { return IL::IsReady() ? &g_host : nullptr; }

__attribute__((constructor)) static void loader_init() {
    if (g_started.exchange(true)) return;
    Stage(1, "Loader constructor");
    NCS_LOGI("[NCSLoader] Hello from NightClubModKit");
    NCS_LOGI("[NCS] Loader %s (API %d)", ncs::kLoaderVersion, NCS_LOADER_API);

    pthread_t thread;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 1 << 20);
    if (pthread_create(&thread, &attr, WorkerMain, nullptr) != 0) NCS_LOGE("[NCS] Failed to start worker thread");
    pthread_attr_destroy(&attr);
}
