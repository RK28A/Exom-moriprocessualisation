#pragma once

#include <string>

namespace ncs {
namespace log {

enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 };

// Writes to logcat (tag "NCSLoader") and, once OpenFile() succeeded, to latest.log.
// Lines written before OpenFile() are buffered and flushed into the file.
void Write(Level level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
void WriteRaw(Level level, const char* message);

bool OpenFile(const std::string& path);
void SetFileEnabled(bool enabled);
const std::string& FilePath();

}  // namespace log
}  // namespace ncs

#define NCS_LOGD(...) ::ncs::log::Write(::ncs::log::Debug, __VA_ARGS__)
#define NCS_LOGI(...) ::ncs::log::Write(::ncs::log::Info, __VA_ARGS__)
#define NCS_LOGW(...) ::ncs::log::Write(::ncs::log::Warn, __VA_ARGS__)
#define NCS_LOGE(...) ::ncs::log::Write(::ncs::log::Error, __VA_ARGS__)
