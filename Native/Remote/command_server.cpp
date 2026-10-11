// See command_server.h.

#include "command_server.h"

#include <poll.h>
#include <pthread.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstddef>
#include <cstdio>
#include <cstring>

#include "../Diagnostics/il2cpp_dump.h"
#include "../Logging/ncmk_log.h"

namespace ncmk {
namespace remote {

namespace {

std::atomic<bool> g_started{false};
std::atomic<bool> g_stop{false};
std::atomic<int> g_listenFd{-1};
ServerInfo* g_info = nullptr;  // never freed: the thread may outlive Stop()

bool SendAll(int fd, const char* data, size_t n) {
    while (n > 0) {
        ssize_t w = send(fd, data, n, MSG_NOSIGNAL);
        if (w < 0 && errno == EINTR) continue;
        if (w <= 0) return false;
        data += w;
        n -= static_cast<size_t>(w);
    }
    return true;
}

bool SendLine(int fd, const std::string& line) {
    std::string s = line + "\n";
    return SendAll(fd, s.data(), s.size());
}

// Reads one '\n'-terminated line (max 256 chars) within timeoutMs.
bool ReadLine(int fd, std::string& out, int timeoutMs) {
    out.clear();
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (out.size() < 256) {
        int left = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count());
        if (left <= 0) return false;
        pollfd p{fd, POLLIN, 0};
        if (poll(&p, 1, left) <= 0) return false;
        char c;
        ssize_t r = recv(fd, &c, 1, 0);
        if (r <= 0) return false;
        if (c == '\n') return true;
        if (c != '\r') out += c;
    }
    return false;
}

class SocketSink : public dump::Sink {
public:
    explicit SocketSink(int fd) : fd_(fd) {}
    bool Write(const char* data, size_t size) override {
        char head[32];
        int n = snprintf(head, sizeof(head), "D %zu\n", size);
        return ok_ = ok_ && SendAll(fd_, head, static_cast<size_t>(n)) && SendAll(fd_, data, size);
    }
    void Progress(size_t done, size_t total, const char* what) override {
        auto now = std::chrono::steady_clock::now();
        if (done != total && now - last_ < std::chrono::milliseconds(250)) return;
        last_ = now;
        char line[256];
        snprintf(line, sizeof(line), "P %zu %zu %s", done, total, what ? what : "");
        ok_ = ok_ && SendLine(fd_, line);
    }

private:
    int fd_;
    bool ok_ = true;
    std::chrono::steady_clock::time_point last_{};
};

void HandleClient(int fd) {
    const ServerInfo& info = *g_info;
    if (!SendLine(fd, "NCMK 1 " + info.loaderVersion + " " + info.package + " " + info.gameVersion)) return;
    std::string cmd;
    if (!ReadLine(fd, cmd, 30000)) return;
    if (cmd == "PING") {
        SendLine(fd, "PONG");
    } else if (cmd == "DUMP") {
        if (!info.ready || !info.ready()) {
            SendLine(fd, "E ERR IL2CPP runtime not ready yet - wait for the game to finish starting");
            return;
        }
        NCMK_LOGI("[NCMK][Dump] dump requested by the PC");
        auto t0 = std::chrono::steady_clock::now();
        SocketSink sink(fd);
        dump::Result r = dump::DumpAll(sink, {info.package, info.gameVersion, info.unityVersion, info.loaderVersion});
        double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        if (r.ok) {
            NCMK_LOGI("[NCMK][Dump] done: %zu assemblies, %zu classes, %zu fields, %zu methods, %zu bytes in %.1f s", r.assemblies,
                      r.classes, r.fields, r.methods, r.bytes, secs);
            SendLine(fd, "E OK " + std::to_string(r.bytes) + " " + std::to_string(r.classes) + " " + std::to_string(r.methods));
        } else {
            NCMK_LOGW("[NCMK][Dump] stopped: %s", r.error.c_str());
            SendLine(fd, "E ERR " + r.error);
        }
    } else {
        SendLine(fd, "E ERR unknown command");
    }
}

void* ServerMain(void*) {
    int lfd = g_listenFd.load();
    while (!g_stop.load()) {
        pollfd p{lfd, POLLIN, 0};
        int pr = poll(&p, 1, 500);
        if (pr <= 0) continue;
        int fd = accept4(lfd, nullptr, nullptr, SOCK_CLOEXEC);
        if (fd < 0) continue;
        HandleClient(fd);
        close(fd);
    }
    close(lfd);
    g_listenFd.store(-1);
    return nullptr;
}
}  // namespace

bool Start(const ServerInfo& info) {
    if (g_started.exchange(true)) return true;
    g_info = new ServerInfo(info);
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        NCMK_LOGW("[NCMK][Remote] socket() failed: %s - PC commands (dump) unavailable", strerror(errno));
        return false;
    }
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    size_t len = strlen(kSocketName);
    memcpy(addr.sun_path + 1, kSocketName, len);  // abstract namespace: leading NUL, not a file
    socklen_t alen = static_cast<socklen_t>(offsetof(sockaddr_un, sun_path) + 1 + len);
    if (bind(fd, reinterpret_cast<sockaddr*>(&addr), alen) != 0 || listen(fd, 2) != 0) {
        NCMK_LOGW("[NCMK][Remote] cannot listen on @%s: %s - PC commands (dump) unavailable", kSocketName, strerror(errno));
        close(fd);
        return false;
    }
    g_listenFd.store(fd);
    pthread_t t;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 1 << 20);
    bool ok = pthread_create(&t, &attr, ServerMain, nullptr) == 0;
    pthread_attr_destroy(&attr);
    if (!ok) {
        close(fd);
        g_listenFd.store(-1);
        NCMK_LOGW("[NCMK][Remote] thread start failed");
        return false;
    }
    NCMK_LOGI("[NCMK][Remote] listening on @%s (PC: adb forward tcp:0 localabstract:%s)", kSocketName, kSocketName);
    return true;
}

void Stop() { g_stop.store(true); }

}  // namespace remote
}  // namespace ncmk
