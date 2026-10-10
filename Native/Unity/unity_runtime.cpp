#include "unity_runtime.h"

#include "../Il2Cpp/il2cpp_api.h"

namespace NCMK {
namespace Unity {

namespace {
constexpr const char* kCore = "UnityEngine.CoreModule";
}

std::string GetUnityVersion() { return IL2CPP::CallStaticString(kCore, "UnityEngine", "Application", "get_unityVersion"); }

std::string GetApplicationVersion() { return IL2CPP::CallStaticString(kCore, "UnityEngine", "Application", "get_version"); }

std::string GetProductName() { return IL2CPP::CallStaticString(kCore, "UnityEngine", "Application", "get_productName"); }

int GetFrameCount() {
    const MethodInfo* m = IL2CPP::FindMethod(IL2CPP::FindClass(kCore, "UnityEngine", "Time"), "get_frameCount", 0);
    Il2CppObject* exc = nullptr;
    Il2CppObject* boxed = IL2CPP::Invoke(m, nullptr, nullptr, &exc);
    if (exc || !boxed || !IL2CPP::api.il2cpp_object_unbox) return -1;
    return *static_cast<int*>(IL2CPP::api.il2cpp_object_unbox(boxed));
}

}  // namespace Unity
}  // namespace NCMK
