/*
 * Mimics the Android load order (libmain.so first, libunity.so later, then libil2cpp.so + init),
 * then plays Unity's player loop: fake_run_frame() calls the per-frame managed methods through
 * MethodInfo slot 0 at ~72 Hz. Finally calls NCMK_Shutdown() and checks the slots were restored.
 *
 * Env: HOST_FRAME_TARGETS  per-frame methods the "engine" calls (default "EventSystem,OVRManager",
 *                          so ExecuteTasks never ticks and the dispatcher must fall back)
 *      HOST_GAME_SECONDS   how long frames run (default 9)
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

static void* must_open(const char* name) {
    void* h = dlopen(name, RTLD_NOW | RTLD_GLOBAL);
    if (!h) { fprintf(stderr, "[host_game] dlopen %s failed: %s\n", name, dlerror()); _exit(2); }
    fprintf(stderr, "[host_game] loaded %s\n", name);
    return h;
}

static double now_s(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

int main(void) {
    must_open("libmain.so");
    usleep(300 * 1000);
    must_open("libunity.so");
    usleep(300 * 1000);
    void* il2cpp = must_open("libil2cpp.so");
    usleep(300 * 1000);
    void (*init)(const char*) = (void (*)(const char*))dlsym(il2cpp, "il2cpp_init");
    init("IL2CPP Root Domain");
    fprintf(stderr, "[host_game] il2cpp_init done\n");

    void (*run_frame)(const char*) = (void (*)(const char*))dlsym(il2cpp, "fake_run_frame");
    int (*slots_original)(int) = (int (*)(int))dlsym(il2cpp, "fake_slots_original");
    int (*slot1_original)(int) = (int (*)(int))dlsym(il2cpp, "fake_slot1_original");
    long (*original_calls)(int) = (long (*)(int))dlsym(il2cpp, "fake_original_calls");
    int (*frame)(void) = (int (*)(void))dlsym(il2cpp, "fake_frame");

    const char* targets = getenv("HOST_FRAME_TARGETS");
    if (!targets) targets = "EventSystem,OVRManager";
    double seconds = getenv("HOST_GAME_SECONDS") ? atof(getenv("HOST_GAME_SECONDS")) : 9.0;
    fprintf(stderr, "[host_game] player loop: '%s' for %.1f s\n", targets, seconds);
    double end = now_s() + seconds;
    while (now_s() < end) {
        run_frame(targets);
        usleep(13889); /* 72 Hz */
    }
    fprintf(stderr, "[host_game] frames=%d originals ExecuteTasks=%ld EventSystem=%ld OVRManager=%ld\n", frame(),
            original_calls(0), original_calls(1), original_calls(2));
    fprintf(stderr, "[host_game] before shutdown: original slots ExecuteTasks=%d EventSystem=%d OVRManager=%d, OVRManager slot1=%d\n",
            slots_original(0), slots_original(1), slots_original(2), slot1_original(2));

    void* loader = dlopen("libncmkloader.so", RTLD_NOW | RTLD_NOLOAD);
    void (*shutdown)(void) = loader ? (void (*)(void))dlsym(loader, "NCMK_Shutdown") : NULL;
    if (shutdown) shutdown();
    else fprintf(stderr, "[host_game] NCMK_Shutdown not found\n");

    fprintf(stderr, "[host_game] after shutdown: original slots ExecuteTasks=%d EventSystem=%d OVRManager=%d\n",
            slots_original(0), slots_original(1), slots_original(2));
    /* The engine keeps running after unload: calls must reach the originals directly. */
    long before = original_calls(1);
    run_frame(targets);
    fprintf(stderr, "[host_game] post-unload frame ok: %s\n", original_calls(1) > before || original_calls(2) > 0 ? "yes" : "no");
    fprintf(stderr, "[host_game] exiting\n");
    return 0;
}
