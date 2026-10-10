#include "ncs_log.h"

#include <cstdarg>
#include <cstdio>
#include <ctime>
#include <mutex>
#include <vector>

#if defined(__ANDROID__)
#include <android/log.h>
#endif

namespace ncs {
namespace log {
namespace {

constexpr const char* kTag = "NCSLoader";

// Only constant-initialized globals here: logging is used from the loader constructor, which
// may run before this file's dynamic initializers. Non-trivial state lives behind accessors.
std::mutex g_mutex;           // constexpr constructor
FILE* g_file = nullptr;
bool g_fileEnabled = true;

std::string& PathRef() {
    static std::string* p = new std::string();
    return *p;
}

std::vector<std::string>& Early() {  // lines logged before the file was opened
    static std::vector<std::string>* v = new std::vector<std::string>();
    return *v;
}

const char* LevelName(Level l) {
    switch (l) {
        case Debug: return "D";
        case Info: return "I";
        case Warn: return "W";
        default: return "E";
    }
}

void Emit(Level level, const char* msg) {
#if defined(__ANDROID__)
    int prio = level == Debug ? ANDROID_LOG_DEBUG
             : level == Info  ? ANDROID_LOG_INFO
             : level == Warn  ? ANDROID_LOG_WARN
                              : ANDROID_LOG_ERROR;
    __android_log_write(prio, kTag, msg);
#else
    fprintf(stderr, "%s/%s: %s\n", LevelName(level), kTag, msg);
#endif

    char stamp[32];
    time_t now = time(nullptr);
    struct tm tmv;
    localtime_r(&now, &tmv);
    strftime(stamp, sizeof(stamp), "%H:%M:%S", &tmv);

    std::lock_guard<std::mutex> lock(g_mutex);
    std::string line = std::string(stamp) + " " + LevelName(level) + " " + msg + "\n";
    if (g_file) {
        fputs(line.c_str(), g_file);
        fflush(g_file);
    } else if (g_fileEnabled && Early().size() < 512) {
        Early().push_back(line);
    }
}

}  // namespace

void WriteRaw(Level level, const char* message) { Emit(level, message); }

void Write(Level level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    Emit(level, buf);
}

bool OpenFile(const std::string& path) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_fileEnabled) return false;
    FILE* f = fopen(path.c_str(), "w");
    if (!f) return false;
    if (g_file) fclose(g_file);
    g_file = f;
    PathRef() = path;
    for (auto& l : Early()) fputs(l.c_str(), g_file);
    Early().clear();
    fflush(g_file);
    return true;
}

void SetFileEnabled(bool enabled) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_fileEnabled = enabled;
    if (!enabled) {
        Early().clear();
        if (g_file) {
            fclose(g_file);
            g_file = nullptr;
        }
    }
}

const std::string& FilePath() { return PathRef(); }

}  // namespace log
}  // namespace ncs
