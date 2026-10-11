#include "environment.h"

#include <dirent.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "../Logging/ncmk_log.h"

#if defined(__ANDROID__)
#include <sys/system_properties.h>
#endif

namespace ncmk {

namespace {
bool StartsWith(const std::string& s, const char* p) { return s.rfind(p, 0) == 0; }
bool EndsWith(const std::string& s, const char* suffix) {
    size_t n = strlen(suffix);
    return s.size() >= n && s.compare(s.size() - n, n, suffix) == 0;
}
// libncmkmod_<name>.so, or the NightClubModKit-era libncsmod_<name>.so (still loaded).
bool IsModName(const std::string& n) {
    return (StartsWith(n, "libncmkmod_") || StartsWith(n, "libncsmod_")) && EndsWith(n, ".so");
}

std::string ReadFile(const std::string& path) {
    std::string out;
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return out;
    char buf[4096];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0 && out.size() < (1 << 20)) out.append(buf, n);
    fclose(f);
    return out;
}

// Finds `"key"` followed by ':' and returns the position after ':' (whitespace skipped), or npos.
size_t FindValue(const std::string& json, const std::string& key, size_t from = 0) {
    std::string q = "\"" + key + "\"";
    size_t p = json.find(q, from);
    while (p != std::string::npos) {
        size_t c = p + q.size();
        while (c < json.size() && isspace(static_cast<unsigned char>(json[c]))) ++c;
        if (c < json.size() && json[c] == ':') {
            ++c;
            while (c < json.size() && isspace(static_cast<unsigned char>(json[c]))) ++c;
            return c;
        }
        p = json.find(q, p + 1);
    }
    return std::string::npos;
}

bool ReadBool(const std::string& json, const std::string& key, bool def, size_t from = 0) {
    size_t v = FindValue(json, key, from);
    if (v == std::string::npos) return def;
    if (json.compare(v, 4, "true") == 0) return true;
    if (json.compare(v, 5, "false") == 0) return false;
    return def;
}

int ReadInt(const std::string& json, const std::string& key, int def, size_t from = 0) {
    size_t v = FindValue(json, key, from);
    if (v == std::string::npos) return def;
    char* end = nullptr;
    long n = strtol(json.c_str() + v, &end, 10);
    return end == json.c_str() + v ? def : static_cast<int>(n);
}

struct ZipEntry {
    std::string name;
    uint16_t method = 0;
    uint32_t compressedSize = 0;
    uint32_t localOffset = 0;
};

// Central directory of a ZIP/APK (no ZIP64; the patcher refuses ZIP64 APKs anyway).
std::vector<ZipEntry> ReadCentralDirectory(FILE* f) {
    std::vector<ZipEntry> out;
    auto rd16 = [](const unsigned char* p) { return static_cast<uint32_t>(p[0] | (p[1] << 8)); };
    auto rd32 = [](const unsigned char* p) {
        return static_cast<uint32_t>(p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<uint32_t>(p[3]) << 24));
    };
    fseeko(f, 0, SEEK_END);
    off_t size = ftello(f);
    off_t tail = std::min<off_t>(size, 65557);
    std::vector<unsigned char> buf(static_cast<size_t>(tail));
    fseeko(f, size - tail, SEEK_SET);
    if (fread(buf.data(), 1, buf.size(), f) != buf.size()) return out;
    for (off_t i = tail - 22; i >= 0; --i) {
        const unsigned char* e = buf.data() + i;
        if (rd32(e) != 0x06054b50) continue;
        uint32_t count = rd16(e + 10), cdSize = rd32(e + 12), cdOff = rd32(e + 16);
        std::vector<unsigned char> cd(cdSize);
        fseeko(f, cdOff, SEEK_SET);
        if (fread(cd.data(), 1, cd.size(), f) != cd.size()) break;
        size_t p = 0;
        for (uint32_t k = 0; k < count && p + 46 <= cd.size(); ++k) {
            const unsigned char* h = cd.data() + p;
            if (rd32(h) != 0x02014b50) break;
            uint32_t nl = rd16(h + 28), xl = rd16(h + 30), cl = rd16(h + 32);
            if (p + 46 + nl > cd.size()) break;
            ZipEntry z;
            z.name.assign(reinterpret_cast<const char*>(h + 46), nl);
            z.method = static_cast<uint16_t>(rd16(h + 10));
            z.compressedSize = rd32(h + 20);
            z.localOffset = rd32(h + 42);
            out.push_back(std::move(z));
            p += 46 + nl + xl + cl;
        }
        break;
    }
    return out;
}
}  // namespace

namespace {
std::string Prop(const char* name, const char* hostEnv) {
#if defined(__ANDROID__)
    (void)hostEnv;
    char value[PROP_VALUE_MAX] = {0};
    __system_property_get(name, value);
    return value;
#else
    (void)name;
    const char* v = getenv(hostEnv);
    return v ? v : "";
#endif
}
}  // namespace

DeviceInfo DetectDevice() {
    DeviceInfo d;
    d.model = Prop("ro.product.model", "NCMK_HOST_MODEL");
    d.codename = Prop("ro.product.device", "NCMK_HOST_DEVICE");
    d.androidRelease = Prop("ro.build.version.release", "NCMK_HOST_ANDROID");
    d.sdk = Prop("ro.build.version.sdk", "NCMK_HOST_SDK");

    struct Known { const char* codename; const char* model; const char* name; bool supported; };
    static const Known kKnown[] = {
        {"monterey", "Quest", "Meta Quest (1st gen)", false},
        {"hollywood", "Quest 2", "Meta Quest 2", true},
        {"seacliff", "Quest Pro", "Meta Quest Pro", true},
        {"eureka", "Quest 3", "Meta Quest 3", true},
        {"panther", "Quest 3S", "Meta Quest 3S", true},
    };
    // Codename first: "Quest 3" is a prefix of "Quest 3S", so exact matches only.
    for (const auto& k : kKnown)
        if (d.codename == k.codename) { d.displayName = k.name; d.known = true; d.supported = k.supported; return d; }
    for (const auto& k : kKnown)
        if (d.model == k.model) { d.displayName = k.name; d.known = true; d.supported = k.supported; return d; }
    d.displayName = d.model.empty() ? "Unknown device" : "Meta " + d.model;
    return d;
}

std::string CurrentPackageName() {
#if defined(NCMK_HOST_TEST)
    if (const char* p = getenv("NCMK_HOST_PACKAGE")) return p;
#endif
    std::string s = ReadFile("/proc/self/cmdline");
    size_t z = s.find('\0');
    if (z != std::string::npos) s.resize(z);
    size_t colon = s.find(':');  // "pkg:process"
    if (colon != std::string::npos) s.resize(colon);
    return s;
}

std::string SelfLibraryPath() {
    Dl_info info{};
    if (dladdr(reinterpret_cast<void*>(&SelfLibraryPath), &info) && info.dli_fname) return info.dli_fname;
    return {};
}

std::string AppDataDir() {
    std::string pkg = CurrentPackageName();
    for (const std::string& base : {std::string("/data/user/0/"), std::string("/data/data/")}) {
        std::string d = base + pkg;
        if (access(d.c_str(), W_OK) == 0) return d;
    }
    return {};
}

bool MakeDirs(const std::string& path) {
    std::string cur;
    for (size_t i = 0; i < path.size(); ++i) {
        cur += path[i];
        if ((path[i] == '/' && i > 0) || i + 1 == path.size()) mkdir(cur.c_str(), 0775);
    }
    struct stat st{};
    return stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
}

bool FileExists(const std::string& path) { return access(path.c_str(), F_OK) == 0; }

bool CopyFile(const std::string& from, const std::string& to) {
    int in = open(from.c_str(), O_RDONLY | O_CLOEXEC);
    if (in < 0) return false;
    std::string tmp = to + ".tmp";
    int out = open(tmp.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0700);
    if (out < 0) {
        close(in);
        return false;
    }
    char buf[65536];
    ssize_t n;
    bool ok = true;
    while ((n = read(in, buf, sizeof(buf))) > 0)
        if (write(out, buf, n) != n) { ok = false; break; }
    close(in);
    close(out);
    return ok && rename(tmp.c_str(), to.c_str()) == 0;
}

std::string ResolveModsRoot(const std::string& package) {
    std::vector<std::string> candidates;
#if defined(NCMK_HOST_TEST)
    if (const char* r = getenv("NCMK_MODS_ROOT")) candidates.emplace_back(r);
#endif
    // The game's manifest has no storage permission: its own external files folder is the only
    // shared-storage location it can always use (and the one the patcher pushes to over adb).
    candidates.emplace_back("/sdcard/Android/data/" + package + "/files/NCMKMods");
    candidates.emplace_back("/storage/emulated/0/Android/data/" + package + "/files/NCMKMods");

    for (const auto& root : candidates) {
        if (!MakeDirs(root + "/Logs")) continue;
        std::string probe = root + "/Logs/.probe";
        FILE* f = fopen(probe.c_str(), "w");
        if (!f) continue;
        fclose(f);
        unlink(probe.c_str());
        for (const char* sub : {"/Mods", "/Config", "/Cache", "/Overrides/Textures", "/Overrides/Audio", "/Overrides/Bundles"})
            MakeDirs(root + sub);
        return root;
    }
    return {};
}

void ApplyLoaderJson(LoaderConfig& cfg, const std::string& json) {
    size_t loader = json.find("\"loader\"");
    size_t from = loader == std::string::npos ? 0 : loader;
    cfg.logging = ReadBool(json, "logging", cfg.logging, from);
    cfg.safeMode = ReadBool(json, "safeMode", cfg.safeMode, from);
    cfg.sceneLoaderDiagnostics = ReadBool(json, "sceneLoaderDiagnostics", cfg.sceneLoaderDiagnostics, from);
    cfg.sceneLoaderRecovery = ReadBool(json, "sceneLoaderRecovery", cfg.sceneLoaderRecovery, from);
    cfg.sceneLoaderRecoveryScene = ReadInt(json, "sceneLoaderRecoveryScene", cfg.sceneLoaderRecoveryScene, from);
    cfg.sceneLoaderRecoveryDelay = ReadInt(json, "sceneLoaderRecoveryDelaySeconds", cfg.sceneLoaderRecoveryDelay, from);
    cfg.metaPlatformDiagnostics = ReadBool(json, "metaPlatformDiagnostics", cfg.metaPlatformDiagnostics, from);
    cfg.storageDiagnostics = ReadBool(json, "storageDiagnostics", cfg.storageDiagnostics, from);
}

std::string ApkPathFromLibrary(const std::string& libPath) {
    size_t bang = libPath.find("!/");
    if (bang != std::string::npos) return libPath.substr(0, bang);
    size_t lib = libPath.rfind("/lib/");
    return lib == std::string::npos ? std::string() : libPath.substr(0, lib) + "/base.apk";
}

bool LoadEmbeddedConfig(LoaderConfig& cfg, const std::string& apkPath) {
    if (apkPath.empty()) return false;
    FILE* f = fopen(apkPath.c_str(), "rb");
    if (!f) return false;
    bool ok = false;
    for (const auto& e : ReadCentralDirectory(f)) {
        if (e.name != "assets/NCMK/loader.json") continue;
        if (e.method != 0 || e.compressedSize == 0 || e.compressedSize > 65536) break;  // the patcher writes it STORED
        unsigned char lh[30];
        fseeko(f, e.localOffset, SEEK_SET);
        if (fread(lh, 1, 30, f) != 30 || lh[0] != 'P' || lh[1] != 'K') break;
        uint32_t nl = static_cast<uint32_t>(lh[26] | (lh[27] << 8)), xl = static_cast<uint32_t>(lh[28] | (lh[29] << 8));
        std::string json(e.compressedSize, '\0');
        fseeko(f, static_cast<off_t>(e.localOffset) + 30 + nl + xl, SEEK_SET);
        if (fread(&json[0], 1, json.size(), f) != json.size()) break;
        ApplyLoaderJson(cfg, json);
        cfg.source = "APK assets/NCMK/loader.json";
        ok = true;
        break;
    }
    fclose(f);
    return ok;
}

LoaderConfig LoadConfig(const std::string& modsRoot, LoaderConfig cfg) {
    if (modsRoot.empty()) return cfg;
    std::string path = modsRoot + "/Config/config.json";
    std::string json = ReadFile(path);
    if (json.empty()) {
        FILE* f = fopen(path.c_str(), "w");
        if (f) {
            fputs("{\n  \"loader\": {\n    \"logging\": true,\n    \"safeMode\": false\n  },\n\n  \"mods\": {\n  }\n}\n", f);
            fclose(f);
        }
        return cfg;
    }
    ApplyLoaderJson(cfg, json);
    cfg.source = (cfg.source == "defaults" ? std::string() : cfg.source + " + ") + path;

    size_t mods = FindValue(json, "mods");
    if (mods != std::string::npos && json[mods] == '{') {
        size_t end = json.find('}', mods);
        std::string obj = json.substr(mods + 1, end == std::string::npos ? std::string::npos : end - mods - 1);
        size_t p = 0;
        while ((p = obj.find('"', p)) != std::string::npos) {
            size_t q = obj.find('"', p + 1);
            if (q == std::string::npos) break;
            std::string id = obj.substr(p + 1, q - p - 1);
            if (!ReadBool(obj, id, true, p)) cfg.disabledMods.push_back(id);
            p = obj.find_first_of(",}", q);
            if (p == std::string::npos) break;
        }
    }
    return cfg;
}

bool SafeModeFlagPresent(const std::string& modsRoot) {
    return !modsRoot.empty() && FileExists(modsRoot + "/disable_mods");
}

std::vector<std::string> ListModFilesInDir(const std::string& dir) {
    std::vector<std::string> out;
    DIR* d = opendir(dir.c_str());
    if (!d) return out;
    while (dirent* e = readdir(d)) {
        std::string n = e->d_name;
        if (IsModName(n)) out.push_back(n);
    }
    closedir(d);
    std::sort(out.begin(), out.end());
    return out;
}

std::vector<std::string> ListEmbeddedMods(const std::string& apkPath) {
    std::vector<std::string> out;
    FILE* f = fopen(apkPath.c_str(), "rb");
    if (!f) return out;
    const std::string prefix = "lib/arm64-v8a/";
    for (const auto& e : ReadCentralDirectory(f)) {
        if (!StartsWith(e.name, prefix.c_str())) continue;
        std::string file = e.name.substr(prefix.size());
        if (file.find('/') == std::string::npos && IsModName(file)) out.push_back(file);
    }
    fclose(f);
    std::sort(out.begin(), out.end());
    return out;
}

}  // namespace ncmk
