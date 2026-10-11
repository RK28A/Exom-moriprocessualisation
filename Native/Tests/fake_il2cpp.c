/*
 * Fake of the IL2CPP export surface used by libncmkloader.so (host tests only).
 *
 * MethodInfo starts with the two slots the dispatcher swaps (methodPointer, virtualMethodPointer),
 * like the real runtime. fake_run_frame() plays the role of Unity's player loop: it calls the
 * per-frame methods THROUGH slot 0, so a swapped slot routes the call into the loader.
 */
#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { const char* name; } Image;
typedef struct { const char* name; } Assembly;
typedef struct { const char* name; int type; } Type;
typedef struct Class {
    const Image* image;
    const char* ns;
    const char* name;
    const struct Class* parent;
    const struct Class* declaring;
    Type self;
    const Type* enumBase;
} Class;
typedef struct MethodInfo MethodInfo;
typedef void* (*InvokeFn)(void* obj, void** args);
struct MethodInfo {
    void* methodPointer;         /* slot 0 */
    void* virtualMethodPointer;  /* slot 1 */
    void* invoker;
    const char* name;
    const Class* klass;
    int argc;
    uint32_t flags;
    const Type* params[4];
    InvokeFn invoke; /* used by il2cpp_runtime_invoke */
};
typedef struct { const Class* klass; const char* name; int offset; int size; int isLiteral; int64_t literal; } Field;
typedef struct { int32_t length; uint16_t chars[128]; } String;
typedef struct { void* klass; void* monitor; int32_t value; } BoxedInt;
typedef struct { void* klass; void* monitor; int32_t handle; } BoxedScene;

static int g_initialized = 0;
static int g_domain = 1;
static int g_frame = 0;
static int g_last_original_frame = -1;
static long g_original_calls[3];
/* Like the real Unity 6 runtime, GC handles are pointer-sized values; the fake hands out values
 * above 4 GB so any 32-bit truncation in the loader or SDK frees an unknown handle and aborts. */
#define FAKE_MAX_HANDLES 256
static uintptr_t g_handles[FAKE_MAX_HANDLES];
static int g_handle_count = 0;
static int g_live_handles = 0;

enum { IMG_CORLIB, IMG_CORE, IMG_CSHARP, IMG_UI, IMG_OVR, IMG_XR, IMG_COUNT };
static const Image kImages[] = {
    {"mscorlib.dll"}, {"UnityEngine.CoreModule.dll"}, {"Assembly-CSharp.dll"}, {"UnityEngine.UI.dll"}, {"Oculus.VR.dll"},
    {"UnityEngine.XRModule.dll"},
};
static const Assembly kAssemblies[] = {
    {"mscorlib"}, {"UnityEngine.CoreModule"}, {"Assembly-CSharp"}, {"UnityEngine.UI"}, {"Oculus.VR"}, {"UnityEngine.XRModule"},
};
static const Assembly* kAssemblyPtrs[] = {
    &kAssemblies[0], &kAssemblies[1], &kAssemblies[2], &kAssemblies[3], &kAssemblies[4], &kAssemblies[5],
};

static const Type kTypeI4 = {"System.Int32", 0x08};
static const Type kTypeString = {"System.String", 0x0e};
static const Type kTypeSingle = {"System.Single", 0x0c};
static const Type kTypeU8 = {"System.UInt64", 0x0b};
static const Type kTypeBoolRef = {"System.Boolean&", 0x02};
static const Type kTypeSingleRef = {"System.Single&", 0x0c};
static const Type kTypeVector3Ref = {"UnityEngine.Vector3&", 0x11};
static const Type kTypeQuaternionRef = {"UnityEngine.Quaternion&", 0x11};

enum {
    C_APPLICATION, C_TIME, C_GAMEMANAGER, C_SYNCCTX, C_SCENEMANAGER, C_SCENE, C_UIBEHAVIOUR, C_EVENTSYSTEM,
    C_OVRMANAGER, C_OVRBASE, C_OVRINPUT, C_BUTTON, C_CONTROLLER, C_AXIS1D,
    C_VECTOR3, C_QUATERNION, C_XRDEVICES, C_XRDEVICE, C_XRNODE, C_COUNT
};
static const Class kClasses[C_COUNT] = {
    [C_APPLICATION] = {&kImages[IMG_CORE], "UnityEngine", "Application", 0, 0, {"UnityEngine.Application", 0x12}, 0},
    [C_TIME] = {&kImages[IMG_CORE], "UnityEngine", "Time", 0, 0, {"UnityEngine.Time", 0x12}, 0},
    [C_GAMEMANAGER] = {&kImages[IMG_CSHARP], "", "GameManager", 0, 0, {"GameManager", 0x12}, 0},
    [C_SYNCCTX] = {&kImages[IMG_CORE], "UnityEngine", "UnitySynchronizationContext", 0, 0,
                   {"UnityEngine.UnitySynchronizationContext", 0x12}, 0},
    [C_SCENEMANAGER] = {&kImages[IMG_CORE], "UnityEngine.SceneManagement", "SceneManager", 0, 0,
                        {"UnityEngine.SceneManagement.SceneManager", 0x12}, 0},
    [C_SCENE] = {&kImages[IMG_CORE], "UnityEngine.SceneManagement", "Scene", 0, 0, {"UnityEngine.SceneManagement.Scene", 0x11}, 0},
    [C_UIBEHAVIOUR] = {&kImages[IMG_UI], "UnityEngine.EventSystems", "UIBehaviour", 0, 0,
                       {"UnityEngine.EventSystems.UIBehaviour", 0x12}, 0},
    [C_EVENTSYSTEM] = {&kImages[IMG_UI], "UnityEngine.EventSystems", "EventSystem", &kClasses[C_UIBEHAVIOUR], 0,
                       {"UnityEngine.EventSystems.EventSystem", 0x12}, 0},
    [C_OVRMANAGER] = {&kImages[IMG_OVR], "", "OVRManager", 0, 0, {"OVRManager", 0x12}, 0},
    [C_OVRBASE] = {&kImages[IMG_OVR], "", "OVRBase", 0, 0, {"OVRBase", 0x12}, 0},
    [C_OVRINPUT] = {&kImages[IMG_OVR], "", "OVRInput", &kClasses[C_OVRBASE], 0, {"OVRInput", 0x12}, 0},
    /* IL2CPP prints nested types with '+' or '/'; the loader must accept both. */
    [C_BUTTON] = {&kImages[IMG_OVR], "", "Button", 0, &kClasses[C_OVRINPUT], {"OVRInput+Button", 0x11}, &kTypeI4},
    [C_CONTROLLER] = {&kImages[IMG_OVR], "", "Controller", 0, &kClasses[C_OVRINPUT], {"OVRInput/Controller", 0x11}, &kTypeI4},
    [C_AXIS1D] = {&kImages[IMG_OVR], "", "Axis1D", 0, &kClasses[C_OVRINPUT], {"OVRInput+Axis1D", 0x11}, &kTypeI4},
    [C_VECTOR3] = {&kImages[IMG_CORE], "UnityEngine", "Vector3", 0, 0, {"UnityEngine.Vector3", 0x11}, 0},
    [C_QUATERNION] = {&kImages[IMG_CORE], "UnityEngine", "Quaternion", 0, 0, {"UnityEngine.Quaternion", 0x11}, 0},
    /* XR fallback for NCMK::Input (OVRInput below never reports a connected controller). */
    [C_XRDEVICES] = {&kImages[IMG_XR], "UnityEngine.XR", "InputDevices", 0, 0, {"UnityEngine.XR.InputDevices", 0x12}, 0},
    [C_XRDEVICE] = {&kImages[IMG_XR], "UnityEngine.XR", "InputDevice", 0, 0, {"UnityEngine.XR.InputDevice", 0x11}, 0},
    [C_XRNODE] = {&kImages[IMG_XR], "UnityEngine.XR", "XRNode", 0, 0, {"UnityEngine.XR.XRNode", 0x11}, &kTypeI4},
};

static String* make_string(const char* s) {
    String* r = calloc(1, sizeof(String));
    size_t n = strlen(s);
    if (n > 127) n = 127;
    r->length = (int32_t)n;
    for (size_t i = 0; i < n; ++i) r->chars[i] = (uint16_t)s[i];
    return r;
}
static BoxedInt* box_int(int32_t v) {
    BoxedInt* b = calloc(1, sizeof(BoxedInt));
    b->value = v;
    return b;
}
/* "MainMenu" for the first 30 frames after the first query, then "Club". */
static int g_first_scene_query = -1;
static void* box_true(void) { return box_int(1); }
static void* box_false(void) { return box_int(0); }
static int scene_handle(void) {
    if (g_first_scene_query < 0) g_first_scene_query = g_frame;
    return g_frame < g_first_scene_query + 30 ? 1 : 2;
}

static void* unity_version(void* o, void** a) { (void)o; (void)a; return make_string("6000.2.2f1"); }
static void* app_version(void* o, void** a) { (void)o; (void)a; return make_string("1.1.0"); }
static void* frame_count(void* o, void** a) { (void)o; (void)a; return box_int(g_frame); }
static void* get_active_scene(void* o, void** a) {
    (void)o; (void)a;
    BoxedScene* s = calloc(1, sizeof(BoxedScene));
    s->klass = (void*)&kClasses[C_SCENE];
    s->handle = scene_handle();
    return s;
}
static void* scene_get_name(void* o, void** a) {
    (void)o;
    return make_string(*(int32_t*)a[0] == 1 ? "MainMenu" : "Club");
}
static void* returns_null(void* o, void** a) { (void)o; (void)a; return NULL; }

/* OVRInput.Update(): counted; OVRManager.instance: null (the game drives hands with HurricaneVR). */
static long g_ovr_input_updates = 0;
static void* ovr_input_update(void* o, void** a) { (void)o; (void)a; ++g_ovr_input_updates; return NULL; }

/* UnityEngine.XR.InputDevices: right hand (node 5) at (0.25, 1.1, 0.4) with PrimaryButton held,
 * left hand (node 4) at (-0.25, 1.1, 0.4). Device ids are node + 100. */
typedef struct { void* klass; void* monitor; uint64_t id; } BoxedDevice;
static void* xr_device_at_node(void* o, void** a) {
    (void)o;
    BoxedDevice* d = calloc(1, sizeof(BoxedDevice));
    d->klass = (void*)&kClasses[C_XRDEVICE];
    d->id = (uint64_t)(*(int32_t*)a[0]) + 100;
    return d;
}
static const char* utf8_of(void* s) {
    static char buf[128];
    String* str = (String*)s;
    int n = str->length < 127 ? str->length : 127;
    for (int i = 0; i < n; ++i) buf[i] = (char)str->chars[i];
    buf[n] = 0;
    return buf;
}
static void* xr_try_bool(void* o, void** a) {
    (void)o;
    uint64_t id = *(uint64_t*)a[0];
    int known = id == 104 || id == 105;
    *(uint8_t*)a[2] = known && id == 105 && !strcmp(utf8_of(a[1]), "PrimaryButton");
    return known ? box_true() : box_false();
}
static void* xr_try_vec3(void* o, void** a) {
    (void)o;
    uint64_t id = *(uint64_t*)a[0];
    if (strcmp(utf8_of(a[1]), "DevicePosition") || (id != 104 && id != 105)) return box_false();
    float* v = (float*)a[2];
    v[0] = id == 105 ? 0.25f : -0.25f; v[1] = 1.1f; v[2] = 0.4f;
    return box_true();
}
static void* xr_try_quat(void* o, void** a) {
    (void)o;
    float* q = (float*)a[2];
    q[0] = q[1] = q[2] = 0.f; q[3] = 1.f;
    (void)a; return box_true();
}

/* Per-frame originals (what Unity's player loop would run). */
static void execute_tasks_original(const MethodInfo* m) { (void)m; g_original_calls[0]++; g_last_original_frame = g_frame; }
static void event_system_update_original(void* self, const MethodInfo* m) {
    (void)self; (void)m; g_original_calls[1]++; g_last_original_frame = g_frame;
}
static void ovr_manager_update_original(void* self, const MethodInfo* m) {
    (void)self; (void)m; g_original_calls[2]++; g_last_original_frame = g_frame;
}
/* Different slot 1 (like an adjustor thunk): must NOT be swapped. */
static void ovr_manager_update_thunk(void* self, const MethodInfo* m) { ovr_manager_update_original(self, m); }

#define STATIC 0x0010u
#define T(c) (&kClasses[c].self)
enum { M_UNITYVER, M_APPVER, M_FRAMECOUNT, M_EXECUTETASKS, M_GETACTIVESCENE, M_SCENENAME, M_ESUPDATE, M_OVRUPDATE,
       M_GET_BUTTON, M_GET_AXIS, M_GETDOWN, M_DESCRIBE, M_OVRINPUT_UPDATE, M_OVRMANAGER_INSTANCE,
       M_XR_DEVICE_AT_NODE, M_XR_TRY_BOOL, M_XR_TRY_VEC3, M_XR_TRY_QUAT, M_COUNT };
static MethodInfo kMethods[M_COUNT] = {
    [M_UNITYVER] = {0, 0, 0, "get_unityVersion", &kClasses[C_APPLICATION], 0, STATIC, {0}, unity_version},
    [M_APPVER] = {0, 0, 0, "get_version", &kClasses[C_APPLICATION], 0, STATIC, {0}, app_version},
    [M_FRAMECOUNT] = {0, 0, 0, "get_frameCount", &kClasses[C_TIME], 0, STATIC, {0}, frame_count},
    [M_EXECUTETASKS] = {(void*)execute_tasks_original, (void*)execute_tasks_original, 0, "ExecuteTasks",
                        &kClasses[C_SYNCCTX], 0, STATIC, {0}, returns_null},
    [M_GETACTIVESCENE] = {0, 0, 0, "GetActiveScene", &kClasses[C_SCENEMANAGER], 0, STATIC, {0}, get_active_scene},
    [M_SCENENAME] = {0, 0, 0, "GetNameInternal", &kClasses[C_SCENE], 1, STATIC, {&kTypeI4}, scene_get_name},
    [M_ESUPDATE] = {(void*)event_system_update_original, (void*)event_system_update_original, 0, "Update",
                    &kClasses[C_EVENTSYSTEM], 0, 0, {0}, returns_null},
    [M_OVRUPDATE] = {(void*)ovr_manager_update_original, (void*)ovr_manager_update_thunk, 0, "Update",
                     &kClasses[C_OVRMANAGER], 0, 0, {0}, returns_null},
    /* Overloads that only findMethodBySignature can tell apart. */
    [M_GET_BUTTON] = {0, 0, 0, "Get", &kClasses[C_OVRINPUT], 2, STATIC, {T(C_BUTTON), T(C_CONTROLLER)}, returns_null},
    [M_GET_AXIS] = {0, 0, 0, "Get", &kClasses[C_OVRINPUT], 2, STATIC, {T(C_AXIS1D), T(C_CONTROLLER)}, returns_null},
    [M_GETDOWN] = {0, 0, 0, "GetDown", &kClasses[C_OVRINPUT], 2, STATIC, {T(C_BUTTON), T(C_CONTROLLER)}, returns_null},
    [M_DESCRIBE] = {0, 0, 0, "Describe", &kClasses[C_OVRBASE], 1, STATIC, {&kTypeString}, returns_null},
    [M_OVRINPUT_UPDATE] = {0, 0, 0, "Update", &kClasses[C_OVRINPUT], 0, STATIC, {0}, ovr_input_update},
    [M_OVRMANAGER_INSTANCE] = {0, 0, 0, "get_instance", &kClasses[C_OVRMANAGER], 0, STATIC, {0}, returns_null},
    [M_XR_DEVICE_AT_NODE] = {0, 0, 0, "GetDeviceAtXRNode", &kClasses[C_XRDEVICES], 1, STATIC, {T(C_XRNODE)}, xr_device_at_node},
    [M_XR_TRY_BOOL] = {0, 0, 0, "TryGetFeatureValue", &kClasses[C_XRDEVICES], 3, STATIC,
                       {&kTypeU8, &kTypeString, &kTypeBoolRef}, xr_try_bool},
    [M_XR_TRY_VEC3] = {0, 0, 0, "TryGetFeatureValue_Vector3f", &kClasses[C_XRDEVICES], 3, STATIC,
                       {&kTypeU8, &kTypeString, &kTypeVector3Ref}, xr_try_vec3},
    [M_XR_TRY_QUAT] = {0, 0, 0, "TryGetFeatureValue_Quaternionf", &kClasses[C_XRDEVICES], 3, STATIC,
                       {&kTypeU8, &kTypeString, &kTypeQuaternionRef}, xr_try_quat},
};
static const void* kOriginalSlots[3][2] = {
    {(void*)execute_tasks_original, (void*)execute_tasks_original},
    {(void*)event_system_update_original, (void*)event_system_update_original},
    {(void*)ovr_manager_update_original, (void*)ovr_manager_update_thunk},
};
static MethodInfo* PerFrame(int i) { return &kMethods[i == 0 ? M_EXECUTETASKS : i == 1 ? M_ESUPDATE : M_OVRUPDATE]; }

static const Field kFields[] = {
    {&kClasses[C_XRDEVICE], "m_DeviceId", (int)offsetof(BoxedDevice, id), 8, 0, 0},
    {&kClasses[C_VECTOR3], "x", 16, 4, 0, 0},
    {&kClasses[C_VECTOR3], "y", 20, 4, 0, 0},
    {&kClasses[C_VECTOR3], "z", 24, 4, 0, 0},
    {&kClasses[C_QUATERNION], "x", 16, 4, 0, 0},
    {&kClasses[C_QUATERNION], "y", 20, 4, 0, 0},
    {&kClasses[C_QUATERNION], "z", 24, 4, 0, 0},
    {&kClasses[C_QUATERNION], "w", 28, 4, 0, 0},
    {&kClasses[C_XRNODE], "LeftHand", 0, 4, 1, 4},
    {&kClasses[C_XRNODE], "RightHand", 0, 4, 1, 5},
    {&kClasses[C_SCENE], "m_Handle", (int)offsetof(BoxedScene, handle), 4, 0, 0},
    {&kClasses[C_BUTTON], "None", 0, 4, 1, 0},
    {&kClasses[C_BUTTON], "One", 0, 4, 1, 1},
    {&kClasses[C_BUTTON], "Two", 0, 4, 1, 2},
    {&kClasses[C_BUTTON], "Any", 0, 4, 1, -1},
    {&kClasses[C_CONTROLLER], "LTouch", 0, 4, 1, 1},
    {&kClasses[C_CONTROLLER], "RTouch", 0, 4, 1, 2},
};

/* ---- time icalls ---- */
static int32_t icall_frame_count(void) { return g_frame; }
static float icall_delta_time(void) { return 1.0f / 72.0f; }
static float icall_fixed_delta_time(void) { return 0.02f; }
static float icall_fixed_time(void) { return (float)(floor((g_frame / 72.0) / 0.02) * 0.02); }

/* ==================================================================== test-only exports */
void fake_run_frame(const char* targets) {
    ++g_frame;
    /* Like Unity: always call through slot 0 with the trailing MethodInfo*. */
    if (targets && strstr(targets, "ExecuteTasks"))
        ((void (*)(const MethodInfo*))kMethods[M_EXECUTETASKS].methodPointer)(&kMethods[M_EXECUTETASKS]);
    if (targets && strstr(targets, "EventSystem")) {
        static int es1, es2; /* two EventSystem instances: two calls per frame */
        ((void (*)(void*, const MethodInfo*))kMethods[M_ESUPDATE].methodPointer)(&es1, &kMethods[M_ESUPDATE]);
        ((void (*)(void*, const MethodInfo*))kMethods[M_ESUPDATE].methodPointer)(&es2, &kMethods[M_ESUPDATE]);
    }
    if (targets && strstr(targets, "OVRManager")) {
        static int ovr;
        ((void (*)(void*, const MethodInfo*))kMethods[M_OVRUPDATE].methodPointer)(&ovr, &kMethods[M_OVRUPDATE]);
    }
}
int fake_frame(void) { return g_frame; }
int fake_original_ran_this_frame(void) { return g_last_original_frame == g_frame; }
long fake_original_calls(int i) { return g_original_calls[i]; }
int fake_live_gchandles(void) { return g_live_handles; }
long fake_ovr_input_updates(void) { return g_ovr_input_updates; }
/* 1 when both slots hold the original pointers. */
int fake_slots_original(int i) {
    return PerFrame(i)->methodPointer == kOriginalSlots[i][0] && PerFrame(i)->virtualMethodPointer == kOriginalSlots[i][1];
}
int fake_slot1_original(int i) { return PerFrame(i)->virtualMethodPointer == kOriginalSlots[i][1]; }

/* ==================================================================== IL2CPP API */
void il2cpp_init(const char* domain) { (void)domain; g_initialized = 1; }

void* il2cpp_get_corlib(void) { return g_initialized ? (void*)&kImages[0] : NULL; }
void* il2cpp_domain_get(void) { return &g_domain; }
const Assembly** il2cpp_domain_get_assemblies(const void* d, size_t* n) {
    (void)d;
    *n = g_initialized ? IMG_COUNT : 0;
    return kAssemblyPtrs;
}
const Image* il2cpp_assembly_get_image(const Assembly* a) { return &kImages[a - kAssemblies]; }
const char* il2cpp_image_get_name(const Image* i) { return i->name; }
size_t il2cpp_image_get_class_count(const Image* i) {
    size_t n = 0;
    for (size_t k = 0; k < C_COUNT; ++k) n += kClasses[k].image == i;
    return n;
}
const Class* il2cpp_class_from_name(const Image* img, const char* ns, const char* name) {
    for (size_t k = 0; k < C_COUNT; ++k)
        if (kClasses[k].image == img && !kClasses[k].declaring && !strcmp(kClasses[k].ns, ns) && !strcmp(kClasses[k].name, name))
            return &kClasses[k];
    return NULL;
}
const char* il2cpp_class_get_name(const Class* c) { return c->name; }
const char* il2cpp_class_get_namespace(const Class* c) { return c->ns; }
const Class* il2cpp_class_get_parent(const Class* c) { return c->parent; }
const Class* il2cpp_class_get_nested_types(const Class* c, void** iter) {
    size_t i = (size_t)*iter;
    for (; i < C_COUNT; ++i)
        if (kClasses[i].declaring == c) { *iter = (void*)(i + 1); return &kClasses[i]; }
    *iter = (void*)C_COUNT;
    return NULL;
}
const Type* il2cpp_class_get_type(const Class* c) { return &c->self; }
const Type* il2cpp_class_enum_basetype(const Class* c) { return c->enumBase; }
void* il2cpp_type_get_object(const Type* t) { return (void*)t; } /* stand-in System.Type */
char* il2cpp_type_get_name(const Type* t) { return t ? strdup(t->name) : NULL; }
int il2cpp_type_get_type(const Type* t) { return t->type; }
void il2cpp_free(void* p) { free(p); }

const MethodInfo* il2cpp_class_get_method_from_name(const Class* c, const char* name, int argc) {
    for (size_t k = 0; k < M_COUNT; ++k)
        if (kMethods[k].klass == c && !strcmp(kMethods[k].name, name) && (argc < 0 || kMethods[k].argc == argc)) return &kMethods[k];
    return NULL;
}
const MethodInfo* il2cpp_class_get_methods(const Class* c, void** iter) {
    size_t i = (size_t)*iter;
    for (; i < M_COUNT; ++i)
        if (kMethods[i].klass == c) { *iter = (void*)(i + 1); return &kMethods[i]; }
    *iter = (void*)M_COUNT;
    return NULL;
}
const char* il2cpp_method_get_name(const MethodInfo* m) { return m->name; }
uint32_t il2cpp_method_get_param_count(const MethodInfo* m) { return (uint32_t)m->argc; }
const Type* il2cpp_method_get_param(const MethodInfo* m, uint32_t i) { return i < 4 ? m->params[i] : NULL; }
uint32_t il2cpp_method_get_flags(const MethodInfo* m, uint32_t* iflags) {
    if (iflags) *iflags = 0;
    return m->flags;
}

const Field* il2cpp_class_get_field_from_name(const Class* c, const char* name) {
    for (size_t k = 0; k < sizeof(kFields) / sizeof(kFields[0]); ++k)
        if (kFields[k].klass == c && !strcmp(kFields[k].name, name)) return &kFields[k];
    return NULL;
}
void il2cpp_field_get_value(void* obj, const Field* f, void* out) { memcpy(out, (char*)obj + f->offset, (size_t)f->size); }
void il2cpp_field_set_value(void* obj, const Field* f, void* value) { memcpy((char*)obj + f->offset, value, (size_t)f->size); }
void il2cpp_field_static_get_value(const Field* f, void* out) {
    if (f->isLiteral) memcpy(out, &f->literal, (size_t)f->size); /* little-endian: low bytes */
}

void* il2cpp_resolve_icall(const char* name) {
    if (!strcmp(name, "UnityEngine.Time::get_frameCount")) return (void*)icall_frame_count;
    if (!strcmp(name, "UnityEngine.Time::get_deltaTime")) return (void*)icall_delta_time;
    if (!strcmp(name, "UnityEngine.Time::get_unscaledDeltaTime")) return (void*)icall_delta_time;
    if (!strcmp(name, "UnityEngine.Time::get_fixedDeltaTime")) return (void*)icall_fixed_delta_time;
    if (!strcmp(name, "UnityEngine.Time::get_fixedTime")) return (void*)icall_fixed_time;
    return NULL;
}

void* il2cpp_runtime_invoke(const MethodInfo* m, void* obj, void** args, void** exc) {
    if (exc) *exc = NULL;
    return m->invoke ? m->invoke(obj, args) : NULL;
}
void* il2cpp_object_new(const Class* c) {
    void** o = calloc(1, 64);
    o[0] = (void*)c;
    return o;
}
uintptr_t il2cpp_gchandle_new(void* obj, int pinned) {
    (void)obj; (void)pinned;
    if (g_handle_count == FAKE_MAX_HANDLES) abort();
    uintptr_t h = (uintptr_t)0x7a5c00000000ull + (uintptr_t)(g_handle_count + 1) * 0x2000u;
    g_handles[g_handle_count++] = h;
    ++g_live_handles;
    return h;
}
void il2cpp_gchandle_free(uintptr_t h) {
    for (int i = 0; i < g_handle_count; ++i)
        if (g_handles[i] == h) {
            g_handles[i] = 0;
            --g_live_handles;
            return;
        }
    fprintf(stderr, "[fake il2cpp] FATAL: il2cpp_gchandle_free(0x%llx): unknown handle (truncated?)\n", (unsigned long long)h);
    abort();
}
void* il2cpp_thread_attach(void* d) { (void)d; return &g_domain; }
void* il2cpp_object_unbox(void* o) { return &((BoxedInt*)o)->value; }
void* il2cpp_string_new(const char* s) { return make_string(s); }
uint16_t* il2cpp_string_chars(String* s) { return s->chars; }
int32_t il2cpp_string_length(String* s) { return s->length; }
