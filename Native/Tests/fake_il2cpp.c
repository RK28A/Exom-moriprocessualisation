/* Minimal fake of the IL2CPP export surface used by libncsloader.so (host tests only). */
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct { const char* name; } Image;
typedef struct { const char* name; } Assembly;
typedef struct { const Image* image; const char* ns; const char* name; } Class;
typedef void* (*Fn)(void);
typedef struct { const Class* klass; const char* name; int argc; Fn fn; } Method;
typedef struct { int32_t length; uint16_t chars[128]; } String;
typedef struct { void* klass; void* monitor; int32_t value; } BoxedInt;

static int g_initialized = 0;
static int g_domain = 1;
static int g_frame = 0;

static const Image kImages[] = { {"mscorlib.dll"}, {"UnityEngine.CoreModule.dll"}, {"Assembly-CSharp.dll"} };
static const Assembly kAssemblies[] = { {"mscorlib"}, {"UnityEngine.CoreModule"}, {"Assembly-CSharp"} };
static const Assembly* kAssemblyPtrs[] = { &kAssemblies[0], &kAssemblies[1], &kAssemblies[2] };

static const Class kClasses[] = {
    { &kImages[1], "UnityEngine", "Application" },
    { &kImages[1], "UnityEngine", "Time" },
    { &kImages[2], "", "GameManager" },
};

static String* make_string(const char* s) {
    String* r = calloc(1, sizeof(String));
    size_t n = strlen(s);
    if (n > 127) n = 127;
    r->length = (int32_t)n;
    for (size_t i = 0; i < n; ++i) r->chars[i] = (uint16_t)s[i];
    return r;
}

static void* unity_version(void) { return make_string("6000.2.2f1"); }
static void* app_version(void) { return make_string("1.1.0"); }
static void* frame_count(void) {
    BoxedInt* b = calloc(1, sizeof(BoxedInt));
    b->value = (g_frame += 72);
    return b;
}

static const Method kMethods[] = {
    { &kClasses[0], "get_unityVersion", 0, unity_version },
    { &kClasses[0], "get_version", 0, app_version },
    { &kClasses[1], "get_frameCount", 0, frame_count },
};

void il2cpp_init(const char* domain) { (void)domain; g_initialized = 1; }

void* il2cpp_get_corlib(void) { return g_initialized ? (void*)&kImages[0] : NULL; }
void* il2cpp_domain_get(void) { return &g_domain; }
const Assembly** il2cpp_domain_get_assemblies(const void* d, size_t* n) {
    (void)d;
    *n = g_initialized ? 3 : 0;
    return kAssemblyPtrs;
}
const Image* il2cpp_assembly_get_image(const Assembly* a) { return &kImages[a - kAssemblies]; }
const char* il2cpp_image_get_name(const Image* i) { return i->name; }
size_t il2cpp_image_get_class_count(const Image* i) {
    size_t n = 0;
    for (size_t k = 0; k < sizeof(kClasses) / sizeof(kClasses[0]); ++k) n += kClasses[k].image == i;
    return n;
}
const Class* il2cpp_class_from_name(const Image* img, const char* ns, const char* name) {
    for (size_t k = 0; k < sizeof(kClasses) / sizeof(kClasses[0]); ++k)
        if (kClasses[k].image == img && !strcmp(kClasses[k].ns, ns) && !strcmp(kClasses[k].name, name)) return &kClasses[k];
    return NULL;
}
const Method* il2cpp_class_get_method_from_name(const Class* c, const char* name, int argc) {
    for (size_t k = 0; k < sizeof(kMethods) / sizeof(kMethods[0]); ++k)
        if (kMethods[k].klass == c && !strcmp(kMethods[k].name, name) && kMethods[k].argc == argc) return &kMethods[k];
    return NULL;
}
void* il2cpp_runtime_invoke(const Method* m, void* obj, void** args, void** exc) {
    (void)obj; (void)args;
    if (exc) *exc = NULL;
    return m->fn();
}
void* il2cpp_thread_attach(void* d) { (void)d; return &g_domain; }
void* il2cpp_object_unbox(void* o) { return &((BoxedInt*)o)->value; }
void* il2cpp_string_new(const char* s) { return make_string(s); }
uint16_t* il2cpp_string_chars(String* s) { return s->chars; }
int32_t il2cpp_string_length(String* s) { return s->length; }
