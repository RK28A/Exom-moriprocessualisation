// Remote commands from the PC patcher, over an abstract UNIX socket "@ncmk" in the game process.
// The PC reaches it with `adb forward tcp:0 localabstract:ncmk` (the same mechanism Chrome's
// remote devtools use), so no file has to go through shared storage.
//
// Protocol (text lines, '\n'):
//   server: "NCMK 1 <loaderVersion> <package> <gameVersion>"
//   client: "PING"  -> "PONG"
//   client: "DUMP"  -> "P <done> <total> <assembly>"   progress, repeated
//                      "D <n>" + n raw bytes            dump text, repeated
//                      "E OK <bytes> <classes> <methods>" or "E ERR <message>"
// One client at a time; read-only commands only.
#pragma once

#include <functional>
#include <string>

namespace ncmk {
namespace remote {

constexpr const char* kSocketName = "ncmk";

struct ServerInfo {
    std::string package, loaderVersion, gameVersion, unityVersion;
    std::function<bool()> ready;  // IL2CPP usable
};

/// Starts the listener thread (idempotent). False when the socket cannot be bound (logged).
bool Start(const ServerInfo& info);
void Stop();

}  // namespace remote
}  // namespace ncmk
