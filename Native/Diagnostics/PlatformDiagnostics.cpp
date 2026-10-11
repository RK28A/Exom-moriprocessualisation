// See PlatformDiagnostics.h.

#include "PlatformDiagnostics.h"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cerrno>
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
constexpr const char* kStorage = "[NCMK][Storage]";
constexpr const char* kMeta = "[NCMK][MetaPlatform]";

void CheckDir(const std::string& dir, bool listFiles) {
    struct stat st{};
    if (stat(dir.c_str(), &st) != 0) {
        NCMK_LOGW("%s %s: %s", kStorage, dir.c_str(), strerror(errno));
        return;
    }
    NCMK_LOGI("%s %s: exists (mode %o, uid %u, gid %u)", kStorage, dir.c_str(), st.st_mode & 07777, st.st_uid, st.st_gid);
    DIR* d = opendir(dir.c_str());
    if (!d) {
        NCMK_LOGW("%s %s: cannot list (%s)", kStorage, dir.c_str(), strerror(errno));
        return;
    }
    int n = 0;
    while (dirent* e = readdir(d)) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        ++n;
        if (!listFiles || n > 12) continue;
        std::string path = dir + "/" + e->d_name;
        struct stat fs{};
        bool hasStat = stat(path.c_str(), &fs) == 0;
        int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
        int err = errno;
        if (fd >= 0) close(fd);
        NCMK_LOGI("%s   %s  %lld bytes  %s", kStorage, e->d_name, hasStat ? static_cast<long long>(fs.st_size) : -1LL,
                  fd >= 0 ? "readable" : (std::string("NOT readable: ") + strerror(err)).c_str());
    }
    closedir(d);
    NCMK_LOGI("%s %s: %d entr%s", kStorage, dir.c_str(), n, n == 1 ? "y" : "ies");
}

struct Meta {
    bool enabled = false;
    Il2CppClass* settings = nullptr;
    Il2CppClass* core = nullptr;
    bool firstDone = false;
    float time = 0.f;
    bool secondDone = false;
};
Meta& M() {
    static Meta* m = new Meta();
    return *m;
}

void OnInitPlatform(int id, Il2CppObject*, bool before, bool threw) {
    if (before) NCMK_LOGI("%s begins", ProbeTag(id));
    else if (threw) NCMK_LOGW("%s failed: threw a managed exception (logged by Unity just after)", ProbeTag(id));
    else NCMK_LOGI("%s completed without exception", ProbeTag(id));
}

std::string StaticString(Il2CppClass* k, const char* getter, bool* threw) {
    *threw = false;
    const MethodInfo* m = IL::FindMethod(k, getter, 0);
    if (!m) return "<no " + std::string(getter) + ">";
    Il2CppObject* exc = nullptr;
    Il2CppObject* s = IL::Invoke(m, nullptr, nullptr, &exc);
    if (exc) {
        *threw = true;
        return ExceptionText(exc);
    }
    return s ? IL::ToUtf8(reinterpret_cast<Il2CppString*>(s)) : std::string();
}

void DescribeAppId(const char* name, const std::string& v, bool threw) {
    if (threw) NCMK_LOGW("%s PlatformSettings.%s threw %s", kMeta, name, v.c_str());
    else if (v.empty()) NCMK_LOGW("%s PlatformSettings.%s is EMPTY (the settings asset is missing or has no app id)", kMeta, name);
    else if (v[0] == '<') NCMK_LOGI("%s PlatformSettings: %s", kMeta, v.c_str());
    else NCMK_LOGI("%s PlatformSettings.%s is set (%zu characters)", kMeta, name, v.size());
}

void Report(const char* why) {
    Meta& m = M();
    NCMK_LOGI("%s ---- %s ----", kMeta, why);
    if (m.settings) {
        auto loaded = FindLoadedObjects(m.settings, 2);
        NCMK_LOGI("%s loaded PlatformSettings assets: %zu", kMeta, loaded.size());
        bool threw = false;
        std::string v = StaticString(m.settings, "get_MobileAppID", &threw);
        DescribeAppId("MobileAppID", v, threw);
        v = StaticString(m.settings, "get_AppID", &threw);
        DescribeAppId("AppID", v, threw);
    }
    if (m.core) {
        const MethodInfo* init = IL::FindMethod(m.core, "IsInitialized", 0);
        Il2CppObject* exc = nullptr;
        Il2CppObject* r = init ? IL::Invoke(init, nullptr, nullptr, &exc) : nullptr;
        if (!init) NCMK_LOGI("%s Core.IsInitialized() not found", kMeta);
        else if (exc) NCMK_LOGW("%s Core.IsInitialized() threw %s", kMeta, ExceptionText(exc).c_str());
        else NCMK_LOGI("%s Core.IsInitialized() = %s", kMeta,
                       r && api.il2cpp_object_unbox && *static_cast<bool*>(api.il2cpp_object_unbox(r)) ? "true" : "false");
    }
    // Where Unity reads its data from: the APK, or the OBB when it was mounted.
    for (const char* g : {"get_dataPath", "get_streamingAssetsPath", "get_persistentDataPath"})
        NCMK_LOGI("[NCMK][Storage] Application.%s = %s", g + 4,
                  IL::CallStaticString("UnityEngine.CoreModule", "UnityEngine", "Application", g).c_str());
}
}  // namespace

void StorageDiagnostics(const std::string& package) {
    NCMK_LOGI("%s process uid %u, gid %u", kStorage, getuid(), getgid());
    CheckDir("/sdcard/Android/obb/" + package, true);
    CheckDir("/storage/emulated/0/Android/obb/" + package, false);
    CheckDir("/sdcard/Android/data/" + package, false);
    CheckDir("/sdcard/Android/data/" + package + "/files", true);
}

void MetaPlatformInit() {
    Meta& m = M();
    m.settings = IL::FindClass("Oculus.Platform", "Oculus.Platform", "PlatformSettings");
    m.core = IL::FindClass("Oculus.Platform", "Oculus.Platform", "Core");
    NCMK_LOGI("%s Oculus.Platform.PlatformSettings %s, Oculus.Platform.Core %s (read-only checks; no entitlement or account "
              "check is bypassed)", kMeta, m.settings ? "found" : "not found", m.core ? "found" : "not found");
    if (Il2CppClass* ach = FindClassByName("Assembly-CSharp", "MetaAchievements")) {
        if (const MethodInfo* init = IL::FindMethod(ach, "InitPlatform", 0))
            InstallProbe("[NCMK][MetaPlatform] MetaAchievements.InitPlatform", init, OnInitPlatform);
    }
    m.enabled = m.settings || m.core;
}

void MetaPlatformOnFrame(const char* sceneLoaded, float dt) {
    (void)sceneLoaded;
    Meta& m = M();
    if (!m.enabled) return;
    if (!m.firstDone) {
        m.firstDone = true;
        Report("first dispatched frame");
        return;
    }
    m.time += dt > 0.f && dt < 1.f ? dt : 0.f;
    if (!m.secondDone && m.time > 15.f) {
        m.secondDone = true;
        Report("15 s later");
    }
}

void MetaPlatformRestore() { RestoreProbes(); }

}  // namespace diag
}  // namespace ncmk
