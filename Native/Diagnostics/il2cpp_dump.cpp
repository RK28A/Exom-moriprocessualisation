// See il2cpp_dump.h.

#include "il2cpp_dump.h"

#include <dlfcn.h>
#include <link.h>
#include <unistd.h>

#include <cinttypes>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <vector>

#include "../Il2Cpp/il2cpp_api.h"
#include "../Logging/ncmk_log.h"
#include "il2cpp_inspect.h"

namespace ncmk {
namespace dump {

namespace IL = NCMK::IL2CPP;
using IL::api;
using diag::TypeName;

namespace {

// ---------------------------------------------------------------- libil2cpp.so layout (RVA / file offset)

struct Segment {
    uintptr_t vaddr, memsz, offset;
};
struct Module {
    uintptr_t base = 0;
    std::vector<Segment> segments;
    std::string path;
};

int FindIl2Cpp(dl_phdr_info* info, size_t, void* data) {
    auto* m = static_cast<Module*>(data);
    const char* name = info->dlpi_name;
    if (!name || !strstr(name, "libil2cpp.so")) return 0;
    m->base = info->dlpi_addr;
    m->path = name;
    for (int i = 0; i < info->dlpi_phnum; ++i) {
        const auto& ph = info->dlpi_phdr[i];
        if (ph.p_type == PT_LOAD) m->segments.push_back({ph.p_vaddr, ph.p_memsz, ph.p_offset});
    }
    return 1;
}

Module& Il2CppModule() {
    static Module m;
    static bool done = false;
    if (!done) {
        done = true;
        dl_iterate_phdr(FindIl2Cpp, &m);
    }
    return m;
}

// "// RVA: 0x1234 Offset: 0x1234 VA: 0x7d97..." for a method pointer.
std::string Address(const void* p) {
    char b[160];
    if (!p) return "// RVA: -1 Offset: -1";
    auto va = reinterpret_cast<uintptr_t>(p);
    Module& m = Il2CppModule();
    if (m.base && va >= m.base) {
        uintptr_t rva = va - m.base;
        for (const auto& s : m.segments)
            if (rva >= s.vaddr && rva < s.vaddr + s.memsz) {
                snprintf(b, sizeof(b), "// RVA: 0x%" PRIxPTR " Offset: 0x%" PRIxPTR " VA: 0x%" PRIxPTR, rva, rva - s.vaddr + s.offset, va);
                return b;
            }
    }
    Dl_info info{};
    if (dladdr(p, &info) && info.dli_fname) {
        const char* slash = strrchr(info.dli_fname, '/');
        snprintf(b, sizeof(b), "// VA: 0x%" PRIxPTR " (%s+0x%" PRIxPTR ")", va, slash ? slash + 1 : info.dli_fname,
                 va - reinterpret_cast<uintptr_t>(info.dli_fbase));
        return b;
    }
    snprintf(b, sizeof(b), "// VA: 0x%" PRIxPTR, va);
    return b;
}

// ---------------------------------------------------------------- modifiers (ECMA-335 attribute bits)

const char* Access(uint32_t flags) {  // FIELD/METHOD_ATTRIBUTE_*_ACCESS_MASK
    switch (flags & 0x7) {
        case 1: return "private ";
        case 2: return "private protected ";
        case 3: return "internal ";
        case 4: return "protected ";
        case 5: return "protected internal ";
        case 6: return "public ";
        default: return "";
    }
}

std::string ClassModifiers(Il2CppClass* k, int flags, bool isInterface, bool isEnum, bool isValueType) {
    std::string s;
    switch (flags & 0x7) {  // TYPE_ATTRIBUTE_VISIBILITY_MASK
        case 1: case 2: s = "public "; break;
        case 3: s = "private "; break;
        case 4: s = "protected "; break;
        case 6: s = "private protected "; break;
        case 7: s = "protected internal "; break;
        default: s = "internal "; break;
    }
    bool abstractF = flags & 0x80, sealedF = flags & 0x100;
    if (isInterface) return s + "interface ";
    if (isEnum) return s + "enum ";
    if (isValueType) return s + "struct ";
    if (abstractF && sealedF) s += "static ";
    else if (abstractF) s += "abstract ";
    else if (sealedF) s += "sealed ";
    (void)k;
    return s + "class ";
}

bool IsIntegral(int kind) { return kind >= 0x02 && kind <= 0x0b; }  // bool .. u8

// ---------------------------------------------------------------- buffered writer

class Writer {
public:
    explicit Writer(Sink& sink) : sink_(sink) { buf_.reserve(kChunk + 4096); }
    bool ok() const { return ok_; }
    size_t bytes() const { return bytes_; }
    void Put(const std::string& s) { Put(s.data(), s.size()); }
    void Put(const char* s) { Put(s, strlen(s)); }
    void Put(const char* s, size_t n) {
        if (!ok_) return;
        buf_.append(s, n);
        if (buf_.size() >= kChunk) Flush();
    }
    void Printf(const char* fmt, ...) __attribute__((format(printf, 2, 3))) {
        char b[1024];
        va_list ap;
        va_start(ap, fmt);
        int n = vsnprintf(b, sizeof(b), fmt, ap);
        va_end(ap);
        if (n > 0) Put(b, static_cast<size_t>(n) < sizeof(b) ? static_cast<size_t>(n) : sizeof(b) - 1);
    }
    void Flush() {
        if (!ok_ || buf_.empty()) return;
        ok_ = sink_.Write(buf_.data(), buf_.size());
        bytes_ += buf_.size();
        buf_.clear();
    }

private:
    static constexpr size_t kChunk = 64 * 1024;
    Sink& sink_;
    std::string buf_;
    size_t bytes_ = 0;
    bool ok_ = true;
};

// ---------------------------------------------------------------- one class

void DumpClass(Writer& w, Il2CppClass* k, Result& r) {
    const char* ns = api.il2cpp_class_get_namespace ? api.il2cpp_class_get_namespace(k) : "";
    int flags = api.il2cpp_class_get_flags ? api.il2cpp_class_get_flags(k) : 1;
    bool isEnum = api.il2cpp_class_is_enum && api.il2cpp_class_is_enum(k);
    bool isInterface = api.il2cpp_class_is_interface && api.il2cpp_class_is_interface(k);
    bool isValueType = api.il2cpp_class_is_valuetype && api.il2cpp_class_is_valuetype(k);

    std::string name = api.il2cpp_class_get_type ? TypeName(api.il2cpp_class_get_type(k)) : api.il2cpp_class_get_name(k);
    if (ns && *ns && name.compare(0, strlen(ns) + 1, std::string(ns) + ".") == 0) name = name.substr(strlen(ns) + 1);

    w.Printf("\n// Namespace: %s\n", ns ? ns : "");
    w.Put(ClassModifiers(k, flags, isInterface, isEnum, isValueType));
    w.Put(name);
    std::vector<std::string> bases;
    if (Il2CppClass* parent = api.il2cpp_class_get_parent ? api.il2cpp_class_get_parent(k) : nullptr) {
        std::string pn = diag::ClassName(parent);
        if (!isEnum && !isValueType && pn != "System.Object") bases.push_back(api.il2cpp_class_get_type ? TypeName(api.il2cpp_class_get_type(parent)) : pn);
    }
    if (api.il2cpp_class_get_interfaces) {
        void* it = nullptr;
        while (Il2CppClass* i = api.il2cpp_class_get_interfaces(k, &it))
            bases.push_back(api.il2cpp_class_get_type ? TypeName(api.il2cpp_class_get_type(i)) : diag::ClassName(i));
    }
    for (size_t i = 0; i < bases.size(); ++i) w.Put(i == 0 ? " : " : ", "), w.Put(bases[i]);
    w.Put("\n{\n");

    // Fields
    if (api.il2cpp_class_get_fields && api.il2cpp_field_get_name) {
        void* it = nullptr;
        bool header = false;
        while (FieldInfo* f = api.il2cpp_class_get_fields(k, &it)) {
            if (!header) { w.Put("\t// Fields\n"); header = true; }
            int ff = api.il2cpp_field_get_flags ? api.il2cpp_field_get_flags(f) : 0;
            const Il2CppType* t = api.il2cpp_field_get_type ? api.il2cpp_field_get_type(f) : nullptr;
            bool isConst = ff & 0x40, isStatic = ff & 0x10, isReadonly = ff & 0x20;
            w.Put("\t");
            w.Put(Access(static_cast<uint32_t>(ff)));
            if (isConst) w.Put("const ");
            else {
                if (isStatic) w.Put("static ");
                if (isReadonly) w.Put("readonly ");
            }
            w.Put(TypeName(t));
            w.Put(" ");
            w.Put(api.il2cpp_field_get_name(f));
            if (isConst) {
                // Integral constants (enum members included): read the literal; other constants are skipped.
                int kind = t && api.il2cpp_type_get_type ? api.il2cpp_type_get_type(t) : 0;
                bool enumMember = isEnum && t && api.il2cpp_class_from_type && api.il2cpp_class_from_type(t) == k;
                if ((IsIntegral(kind) || enumMember) && api.il2cpp_field_static_get_value) {
                    int64_t v = 0;
                    api.il2cpp_field_static_get_value(f, &v);
                    if (kind == 0x02) w.Put(v & 0xff ? " = true" : " = false");
                    else w.Printf(" = %" PRId64, enumMember || kind == 0x08 ? static_cast<int64_t>(static_cast<int32_t>(v)) : v);
                }
                w.Put(";\n");
            } else if (api.il2cpp_field_get_offset) {
                w.Printf("; // 0x%zx\n", api.il2cpp_field_get_offset(f));
            } else {
                w.Put(";\n");
            }
            ++r.fields;
        }
    }

    // Properties
    if (api.il2cpp_class_get_properties && api.il2cpp_property_get_name) {
        void* it = nullptr;
        bool header = false;
        while (const PropertyInfo* p = api.il2cpp_class_get_properties(k, &it)) {
            if (!header) { w.Put("\n\t// Properties\n"); header = true; }
            const MethodInfo* g = api.il2cpp_property_get_get_method ? api.il2cpp_property_get_get_method(p) : nullptr;
            const MethodInfo* s = api.il2cpp_property_get_set_method ? api.il2cpp_property_get_set_method(p) : nullptr;
            const MethodInfo* any = g ? g : s;
            uint32_t mf = any && api.il2cpp_method_get_flags ? api.il2cpp_method_get_flags(any, nullptr) : 0;
            std::string type = g && api.il2cpp_method_get_return_type ? TypeName(api.il2cpp_method_get_return_type(g))
                             : s && api.il2cpp_method_get_param ? TypeName(api.il2cpp_method_get_param(s, 0)) : "?";
            w.Put("\t");
            w.Put(Access(mf));
            if (mf & 0x10) w.Put("static ");
            w.Put(type);
            w.Put(" ");
            w.Put(api.il2cpp_property_get_name(p));
            w.Put(" { ");
            if (g) w.Put("get; ");
            if (s) w.Put("set; ");
            w.Put("}\n");
        }
    }

    // Methods
    if (api.il2cpp_class_get_methods && api.il2cpp_method_get_name) {
        void* it = nullptr;
        bool header = false;
        while (const MethodInfo* m = api.il2cpp_class_get_methods(k, &it)) {
            if (!header) { w.Put("\n\t// Methods\n"); header = true; }
            uint32_t mf = api.il2cpp_method_get_flags ? api.il2cpp_method_get_flags(m, nullptr) : 0;
            void* ptr = *reinterpret_cast<void* const*>(m);  // methodPointer: documented MethodInfo slot 0
            w.Put("\n\t");
            w.Put(Address(ptr));
            w.Put("\n\t");
            w.Put(Access(mf));
            if (mf & 0x10) w.Put("static ");
            if (mf & 0x400) w.Put("abstract ");
            else if ((mf & 0x40) && !(mf & 0x10)) w.Put(mf & 0x100 ? "virtual " : (mf & 0x20 ? "sealed override " : "override "));
            w.Put(api.il2cpp_method_get_return_type ? TypeName(api.il2cpp_method_get_return_type(m)).c_str() : "?");
            w.Put(" ");
            w.Put(api.il2cpp_method_get_name(m));
            w.Put("(");
            uint32_t pc = api.il2cpp_method_get_param_count ? api.il2cpp_method_get_param_count(m) : 0;
            for (uint32_t i = 0; i < pc; ++i) {
                if (i) w.Put(", ");
                w.Put(api.il2cpp_method_get_param ? TypeName(api.il2cpp_method_get_param(m, i)).c_str() : "?");
                const char* pn = api.il2cpp_method_get_param_name ? api.il2cpp_method_get_param_name(m, i) : nullptr;
                w.Put(" ");
                if (pn && *pn) w.Put(pn);
                else w.Printf("p%u", i);
            }
            w.Put(") { }\n");
            ++r.methods;
        }
    }
    w.Put("}\n");
}

}  // namespace

Result DumpAll(Sink& sink, const Header& h, Pacing pacing) {
    Result r;
    if (!IL::IsReady() || !api.il2cpp_image_get_class || !api.il2cpp_class_get_name) {
        r.error = "IL2CPP is not ready";
        return r;
    }
    IL::AttachCurrentThread();
    Writer w(sink);
    Module& mod = Il2CppModule();

    std::vector<std::string> assemblies = IL::ListAssemblies();
    size_t total = 0;
    for (const auto& a : assemblies) total += IL::ClassCount(IL::FindAssembly(a.c_str()));

    w.Printf("// NCMK IL2CPP dump - %s %s (Unity %s), NCMK loader %s\n", h.package.c_str(), h.gameVersion.c_str(),
             h.unityVersion.c_str(), h.loaderVersion.c_str());
    w.Printf("// libil2cpp.so: %s, loaded at 0x%" PRIxPTR "\n", mod.path.empty() ? "?" : mod.path.c_str(), mod.base);
    w.Put("// RVA = address - load base; Offset = position in libil2cpp.so; VA = address in this process.\n");
    w.Put("// Field comments are instance offsets (static fields: offset in the class static data).\n");
    w.Printf("// %zu assemblies, %zu classes\n", assemblies.size(), total);
    for (size_t i = 0; i < assemblies.size(); ++i)
        w.Printf("// Image %zu: %s.dll - %zu classes\n", i, assemblies[i].c_str(), IL::ClassCount(IL::FindAssembly(assemblies[i].c_str())));

    size_t done = 0;
    int slice = 0;
    for (const auto& a : assemblies) {
        const Il2CppImage* img = IL::FindAssembly(a.c_str());
        size_t n = IL::ClassCount(img);
        w.Printf("\n// ===================================================== %s.dll\n", a.c_str());
        sink.Progress(done, total, a.c_str());
        for (size_t i = 0; i < n && w.ok(); ++i) {
            auto* k = const_cast<Il2CppClass*>(api.il2cpp_image_get_class(img, i));
            if (k) DumpClass(w, k, r), ++r.classes;
            ++done;
            if (++slice >= pacing.classesPerSlice) {
                slice = 0;
                w.Flush();
                sink.Progress(done, total, a.c_str());
                if (pacing.pauseMicros > 0) usleep(static_cast<useconds_t>(pacing.pauseMicros));  // let the game breathe
            }
        }
        ++r.assemblies;
        if (!w.ok()) break;
    }
    w.Flush();
    sink.Progress(done, total, "done");
    r.bytes = w.bytes();
    r.ok = w.ok();
    if (!r.ok) r.error = "the PC stopped reading";
    return r;
}

}  // namespace dump
}  // namespace ncmk
