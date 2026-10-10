/* Mimics the Android load order: libmain.so first, libunity.so later, then libil2cpp.so + init. */
#include <dlfcn.h>
#include <stdio.h>
#include <unistd.h>

static void* must_open(const char* name) {
    void* h = dlopen(name, RTLD_NOW | RTLD_GLOBAL);
    if (!h) { fprintf(stderr, "[host_game] dlopen %s failed: %s\n", name, dlerror()); _exit(2); }
    fprintf(stderr, "[host_game] loaded %s\n", name);
    return h;
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
    sleep(7); /* let the loader finish and DebugOverlay print once */
    fprintf(stderr, "[host_game] exiting\n");
    return 0;
}
