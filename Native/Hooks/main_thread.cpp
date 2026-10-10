// Main-thread dispatcher. See main_thread.h.
//
// ---------------------------------------------------------------------------------------------
// THE ONLY IL2CPP MEMORY-LAYOUT ASSUMPTION IN NCMK
//
// Since Unity 2021.2 (IL2CPP metadata v29+, including Unity 6000.x / metadata v31 used by the
// game) the runtime MethodInfo struct starts with two function pointers:
//
//     struct MethodInfo {
//         Il2CppMethodPointer methodPointer;         // slot 0: what invokers / scripting calls use
//         Il2CppMethodPointer virtualMethodPointer;  // slot 1: virtual dispatch / adjustor thunk
//         ...
//     };
//
// We read and write ONLY these two slots. Slot 0 must be non-null before anything is swapped,
// and slot 1 is swapped only when it holds the same pointer as slot 0 (otherwise it is an
// adjustor thunk or a different entry and is left alone). Every other access goes through
// exported il2cpp_* functions resolved by name. MethodInfo memory is runtime-allocated and
// writable; slot writes are single pointer-sized atomic stores.
//
// Calling convention of the swapped entry (IL2CPP, Unity 2021.2+): every managed method takes a
// trailing `const MethodInfo*`:   static void M()  -> void(const MethodInfo*)
//                                instance void M() -> void(Il2CppObject* this, const MethodInfo*)
// ---------------------------------------------------------------------------------------------

#include "main_thread.h"

#include <sys/syscall.h>
#include <unistd.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <thread>
#include <utility>

#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncmk_log.h"

namespace ncmk {
namespace hooks {

namespace IL = NCMK::IL2CPP;

std::string Target::Label() const {
    std::string s = assembly;
    s += " ";
    if (nameSpace && *nameSpace) s += std::string(nameSpace) + ".";
    s += std::string(klass) + "." + method;
    s += isStatic ? " (static)" : " (instance)";
    return s;
}

const std::vector<Target>& DefaultTargets() {
    static const std::vector<Target>* targets = new std::vector<Target>{
        {"UnityEngine.CoreModule", "UnityEngine", "UnitySynchronizationContext", "ExecuteTasks", true},
        {"UnityEngine.UI", "UnityEngine.EventSystems", "EventSystem", "Update", false},
        {"Oculus.VR", "", "OVRManager", "Update", false},
    };
    return *targets;
}

namespace {

constexpr int kMaxHooks = 8;

// Plain static storage (zero-initialised, no dynamic initializer): entry points may still run
// after Restore() on a thread that already loaded the swapped pointer, so records are never freed.
struct Hook {
    const MethodInfo* method;
    void* original;  // slot 0 before the swap
    void* entry;     // our entry point written into the slots
    bool slot1Swapped;
    bool installed;
    long ticks;      // __atomic
    char label[160];
};
Hook g_hooks[kMaxHooks];

enum State { kPending = 0, kProbing = 1, kActive = 2, kFailed = 3, kStopped = 4 };

struct Getter {
    bool resolved = false;
    void* icall = nullptr;
    const MethodInfo* method = nullptr;
};

struct Runtime {
    std::atomic<int> state{kPending};
    std::atomic<int> winner{-1};
    std::atomic<long> mainTid{0};
    std::atomic<long> dispatched{0};
    FrameHandler handler = nullptr;

    // Main thread only.
    bool inDispatch = false;
    bool foreignThreadLogged = false;
    bool noFrameCountLogged = false;
    int lastFrame = -1;
    double lastFixedTime = -1.0;
    long sceneHandle = -1;
    bool sceneProbeFailed = false;
    std::string sceneName;
    Getter frameCount, deltaTime, unscaledDeltaTime, fixedTime, fixedDeltaTime;
    const MethodInfo* getActiveScene = nullptr;
    FieldInfo* sceneHandleField = nullptr;
    const MethodInfo* sceneGetName = nullptr;
    bool sceneResolved = false;

    std::mutex queueLock;
    std::vector<std::pair<NCMKMainThreadFn, void*>> queue;
};

Runtime& R() {
    static Runtime* r = new Runtime();  // never destroyed: entries can run during process exit
    return *r;
}

long CurrentTid() { return static_cast<long>(syscall(SYS_gettid)); }

void** Slots(const MethodInfo* m) { return reinterpret_cast<void**>(const_cast<MethodInfo*>(m)); }

// ---------------------------------------------------------------- engine queries (main thread)

template <typename T>
T ReadTime(Getter& g, const char* icallName, const char* methodName, T fallback) {
    if (!g.resolved) {
        g.resolved = true;
        if (IL::api.il2cpp_resolve_icall) g.icall = IL::api.il2cpp_resolve_icall(icallName);
        if (!g.icall) g.method = IL::FindMethod(IL::FindClass("UnityEngine.CoreModule", "UnityEngine", "Time"), methodName, 0);
    }
    if (g.icall) return reinterpret_cast<T (*)()>(g.icall)();
    if (!g.method) return fallback;
    Il2CppObject* exc = nullptr;
    Il2CppObject* boxed = IL::Invoke(g.method, nullptr, nullptr, &exc);
    if (exc || !boxed || !IL::api.il2cpp_object_unbox) return fallback;
    T v{};
    memcpy(&v, IL::api.il2cpp_object_unbox(boxed), sizeof(T));
    return v;
}

void ResolveScene(Runtime& r) {
    r.sceneResolved = true;
    Il2CppClass* sm = IL::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "SceneManager");
    Il2CppClass* scene = IL::FindClass("UnityEngine.CoreModule", "UnityEngine.SceneManagement", "Scene");
    r.getActiveScene = IL::FindMethod(sm, "GetActiveScene", 0);
    r.sceneHandleField = IL::FindField(scene, "m_Handle");
    r.sceneGetName = IL::FindMethod(scene, "GetNameInternal", 1);
    if (!r.getActiveScene || !r.sceneHandleField)
        NCMK_LOGW("[NCMK] Scene tracking unavailable (SceneManager.GetActiveScene / Scene.m_Handle not found): OnSceneLoaded disabled");
}

// Returns true when the active scene changed since the last poll.
bool PollScene(Runtime& r) {
    if (!r.sceneResolved) ResolveScene(r);
    if (!r.getActiveScene || !r.sceneHandleField || r.sceneProbeFailed) return false;
    Il2CppObject* exc = nullptr;
    Il2CppObject* boxed = IL::Invoke(r.getActiveScene, nullptr, nullptr, &exc);
    if (exc || !boxed) {
        r.sceneProbeFailed = true;
        NCMK_LOGW("[NCMK] SceneManager.GetActiveScene failed: OnSceneLoaded disabled");
        return false;
    }
    int32_t handle = 0;
    IL::GetField(boxed, r.sceneHandleField, &handle);  // boxed struct: field offsets come from il2cpp
    if (handle == r.sceneHandle) return false;
    r.sceneHandle = handle;

    std::string name;
    if (r.sceneGetName) {
        void* args[] = {&handle};
        Il2CppObject* s = IL::Invoke(r.sceneGetName, nullptr, args, &exc);
        if (!exc && s) name = IL::ToUtf8(reinterpret_cast<Il2CppString*>(s));
    }
    if (name.empty()) name = "scene#" + std::to_string(handle);
    r.sceneName = name;
    return true;
}

void Dispatch(Runtime& r) {
    int fc = ReadTime<int32_t>(r.frameCount, "UnityEngine.Time::get_frameCount", "get_frameCount", -1);
    if (fc >= 0) {
        if (fc == r.lastFrame) return;  // already dispatched this frame
        r.lastFrame = fc;
    } else if (!r.noFrameCountLogged) {
        r.noFrameCountLogged = true;
        NCMK_LOGW("[NCMK] Time.frameCount unavailable: per-frame deduplication disabled");
    }

    Frame f;
    f.frameCount = fc;
    f.deltaTime = ReadTime<float>(r.deltaTime, "UnityEngine.Time::get_deltaTime", "get_deltaTime", 0.f);
    f.unscaledDeltaTime =
        ReadTime<float>(r.unscaledDeltaTime, "UnityEngine.Time::get_unscaledDeltaTime", "get_unscaledDeltaTime", f.deltaTime);
    float fixedDt = ReadTime<float>(r.fixedDeltaTime, "UnityEngine.Time::get_fixedDeltaTime", "get_fixedDeltaTime", 0.f);
    float fixedTime = ReadTime<float>(r.fixedTime, "UnityEngine.Time::get_fixedTime", "get_fixedTime", -1.f);
    if (fixedDt > 0.f && fixedTime >= 0.f) {
        if (r.lastFixedTime >= 0.0) {
            long steps = std::lround((fixedTime - r.lastFixedTime) / fixedDt);
            f.fixedSteps = static_cast<int>(steps < 0 ? 0 : steps > 8 ? 8 : steps);
        }
        r.lastFixedTime = fixedTime;
    }

    // Scene changes: polled on the first frame and every 10th frame after it.
    if (r.dispatched.load() % 10 == 0 && PollScene(r)) f.sceneLoaded = r.sceneName.c_str();

    std::vector<std::pair<NCMKMainThreadFn, void*>> work;
    {
        std::lock_guard<std::mutex> lock(r.queueLock);
        work.swap(r.queue);
    }
    for (auto& w : work) w.first(w.second);

    if (r.handler) r.handler(f);
    r.dispatched.fetch_add(1);
}

void OnTick(int index) {
    __atomic_fetch_add(&g_hooks[index].ticks, 1, __ATOMIC_RELAXED);
    Runtime& r = R();
    int state = r.state.load();
    if (state != kProbing && state != kActive) return;

    int expected = -1;
    if (!r.winner.compare_exchange_strong(expected, index) && expected != index) return;  // not the winner
    long tid = CurrentTid();
    long none = 0;
    if (r.mainTid.compare_exchange_strong(none, tid)) {
        r.state.store(kActive);
    } else if (r.mainTid.load() != tid) {
        if (!r.foreignThreadLogged) {
            r.foreignThreadLogged = true;
            NCMK_LOGW("[NCMK] Per-frame target called from a second thread (tid %ld) - ignored", tid);
        }
        return;
    }
    if (r.inDispatch) return;  // a mod re-entered the hooked method
    r.inDispatch = true;
    Dispatch(r);
    r.inDispatch = false;
}

template <int I>
void StaticEntry(const MethodInfo* m) {
    reinterpret_cast<void (*)(const MethodInfo*)>(g_hooks[I].original)(m);
    OnTick(I);
}

template <int I>
void InstanceEntry(void* self, const MethodInfo* m) {
    reinterpret_cast<void (*)(void*, const MethodInfo*)>(g_hooks[I].original)(self, m);
    OnTick(I);
}

void* const kStaticEntries[kMaxHooks] = {
    reinterpret_cast<void*>(&StaticEntry<0>), reinterpret_cast<void*>(&StaticEntry<1>),
    reinterpret_cast<void*>(&StaticEntry<2>), reinterpret_cast<void*>(&StaticEntry<3>),
    reinterpret_cast<void*>(&StaticEntry<4>), reinterpret_cast<void*>(&StaticEntry<5>),
    reinterpret_cast<void*>(&StaticEntry<6>), reinterpret_cast<void*>(&StaticEntry<7>),
};
void* const kInstanceEntries[kMaxHooks] = {
    reinterpret_cast<void*>(&InstanceEntry<0>), reinterpret_cast<void*>(&InstanceEntry<1>),
    reinterpret_cast<void*>(&InstanceEntry<2>), reinterpret_cast<void*>(&InstanceEntry<3>),
    reinterpret_cast<void*>(&InstanceEntry<4>), reinterpret_cast<void*>(&InstanceEntry<5>),
    reinterpret_cast<void*>(&InstanceEntry<6>), reinterpret_cast<void*>(&InstanceEntry<7>),
};

bool Swap(int i, const Target& t) {
    Hook& h = g_hooks[i];
    snprintf(h.label, sizeof(h.label), "%s", t.Label().c_str());
    const MethodInfo* m = IL::FindMethod(IL::FindClass(t.assembly, t.nameSpace, t.klass), t.method, 0);
    if (!m) {
        NCMK_LOGW("[NCMK] Dispatcher: %s not found", h.label);
        return false;
    }
    if (IL::api.il2cpp_method_get_flags && IL::IsStatic(m) != t.isStatic) {
        NCMK_LOGW("[NCMK] Dispatcher: %s is %s, expected %s - skipped", h.label, IL::IsStatic(m) ? "static" : "an instance method",
                  t.isStatic ? "static" : "an instance method");
        return false;
    }
    void** slots = Slots(m);
    void* slot0 = __atomic_load_n(&slots[0], __ATOMIC_ACQUIRE);
    if (!slot0) {
        NCMK_LOGW("[NCMK] Dispatcher: %s has a null methodPointer - skipped", h.label);
        return false;
    }
    void* entry = t.isStatic ? kStaticEntries[i] : kInstanceEntries[i];
    if (slot0 == entry) return false;  // already ours
    h.method = m;
    h.original = slot0;
    h.entry = entry;
    __atomic_store_n(&h.ticks, 0, __ATOMIC_RELAXED);
    h.slot1Swapped = false;
    // Original must be published before the entry becomes reachable.
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    __atomic_store_n(&slots[0], entry, __ATOMIC_RELEASE);
    void* expected = slot0;
    if (__atomic_compare_exchange_n(&slots[1], &expected, entry, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) h.slot1Swapped = true;
    h.installed = true;
    NCMK_LOGI("[NCMK] Dispatcher: trying %s (slot 0%s swapped)", h.label, h.slot1Swapped ? " + slot 1" : "");
    return true;
}

void Unswap(Hook& h) {
    if (!h.installed) return;
    void** slots = Slots(h.method);
    void* expected = h.entry;
    __atomic_compare_exchange_n(&slots[0], &expected, h.original, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    if (h.slot1Swapped) {
        expected = h.entry;
        __atomic_compare_exchange_n(&slots[1], &expected, h.original, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    }
    h.installed = false;
}

void DropQueue(Runtime& r) {
    std::lock_guard<std::mutex> lock(r.queueLock);
    if (!r.queue.empty()) NCMK_LOGW("[NCMK] %zu queued main-thread call(s) dropped (no dispatcher)", r.queue.size());
    r.queue.clear();
}

}  // namespace

int Install(const std::vector<Target>& targets, FrameHandler handler, std::chrono::milliseconds perTarget) {
    Runtime& r = R();
    r.handler = handler;
    int expected = kPending;
    if (!r.state.compare_exchange_strong(expected, kProbing)) return -1;  // disabled or shut down meanwhile
    auto stopped = [&r] { return r.state.load() == kStopped; };
    std::string tried;
    int n = static_cast<int>(targets.size() < kMaxHooks ? targets.size() : kMaxHooks);
    for (int i = 0; i < n && r.winner.load() < 0 && !stopped(); ++i) {
        tried += (tried.empty() ? "" : ", ") + std::string(targets[i].klass) + "." + targets[i].method;
        if (!Swap(i, targets[i])) continue;
        auto deadline = std::chrono::steady_clock::now() + perTarget;
        while (r.winner.load() < 0 && !stopped() && std::chrono::steady_clock::now() < deadline)
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        if (r.winner.load() < 0 && !stopped())
            NCMK_LOGW("[NCMK] Dispatcher: no tick from %s after %lld ms - trying the next target", g_hooks[i].label,
                      static_cast<long long>(perTarget.count()));
    }

    if (stopped()) {  // NCMK_Shutdown ran while probing: leave nothing swapped
        for (auto& h : g_hooks) Unswap(h);
        NCMK_LOGI("[NCMK] STAGE 09 Main-thread dispatcher: stopped during startup");
        return -1;
    }
    int w = r.winner.load();
    for (int i = 0; i < kMaxHooks; ++i)
        if (i != w) Unswap(g_hooks[i]);

    if (w < 0) {
        r.state.store(kFailed);
        DropQueue(r);
        NCMK_LOGE("[NCMK] STAGE 09 Main-thread dispatcher: no per-frame target worked (tried %s) - main-thread callbacks disabled",
                  tried.c_str());
        return -1;
    }
    NCMK_LOGI("[NCMK] STAGE 09 Main-thread dispatcher: hooked %s (main thread tid %ld)", g_hooks[w].label, r.mainTid.load());
    return w;
}

void Restore() {
    Runtime& r = R();
    int prev = r.state.exchange(kStopped);
    bool any = false;
    for (auto& h : g_hooks) {
        any = any || h.installed;
        Unswap(h);
    }
    DropQueue(r);
    if (any || prev == kActive) NCMK_LOGI("[NCMK] Dispatcher stopped: MethodInfo pointers restored");
}

void Disable(const char* reason) {
    Runtime& r = R();
    int expected = kPending;
    if (r.state.compare_exchange_strong(expected, kFailed)) {
        DropQueue(r);
        NCMK_LOGI("[NCMK] Main-thread dispatcher not installed: %s", reason);
    }
}

bool Active() { return R().state.load() == kActive; }

bool IsMainThread() {
    long tid = R().mainTid.load();
    return tid != 0 && tid == CurrentTid();
}

bool Post(NCMKMainThreadFn fn, void* user) {
    Runtime& r = R();
    int state = r.state.load();
    if (!fn || state == kFailed || state == kStopped) return false;
    std::lock_guard<std::mutex> lock(r.queueLock);
    r.queue.emplace_back(fn, user);
    return true;
}

long DispatchedFrames() { return R().dispatched.load(); }

}  // namespace hooks
}  // namespace ncmk
