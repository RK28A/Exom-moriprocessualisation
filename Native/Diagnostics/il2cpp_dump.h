// Full IL2CPP metadata dump (Il2CppDumper "dump.cs" style) built at runtime from the il2cpp API:
// every assembly, class (modifiers, parent, interfaces), field (offset / const value), property and
// method (parameters, RVA, file offset in libil2cpp.so, VA). Name-based only; the method address
// is read from the documented MethodInfo slot 0.
//
// Runs on a background thread attached to IL2CPP, a few classes at a time with short pauses so the
// game keeps running smoothly. Output goes to a Sink (the remote command server streams it to the PC).
#pragma once

#include <cstddef>
#include <string>

namespace ncmk {
namespace dump {

class Sink {
public:
    virtual ~Sink() = default;
    /// Returns false to abort (client gone).
    virtual bool Write(const char* data, size_t size) = 0;
    virtual void Progress(size_t done, size_t total, const char* what) = 0;
};

struct Header {
    std::string package, gameVersion, unityVersion, loaderVersion;
};

struct Result {
    bool ok = false;
    size_t assemblies = 0, classes = 0, fields = 0, methods = 0, bytes = 0;
    std::string error;
};

struct Pacing {
    int classesPerSlice = 16;   // classes dumped between two pauses
    int pauseMicros = 1000;     // pause length
};

Result DumpAll(Sink& sink, const Header& header, Pacing pacing = {});

}  // namespace dump
}  // namespace ncmk
