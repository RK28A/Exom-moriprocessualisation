#pragma once

#include <string>

namespace ncmk {
namespace log {

enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 };

// Writes to logcat (tag "NCMK") and, once OpenFile() succeeded, to latest.log.
// Lines written before OpenFile() are buffered and flushed into the file.
void Write(Level level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
void WriteRaw(Level level, const char* message);

bool OpenFile(const std::string& path);
void SetFileEnabled(bool enabled);
const std::string& FilePath();

}  // namespace log
}  // namespace ncmk

#define NCMK_LOGD(...) ::ncmk::log::Write(::ncmk::log::Debug, __VA_ARGS__)
#define NCMK_LOGI(...) ::ncmk::log::Write(::ncmk::log::Info, __VA_ARGS__)
#define NCMK_LOGW(...) ::ncmk::log::Write(::ncmk::log::Warn, __VA_ARGS__)
#define NCMK_LOGE(...) ::ncmk::log::Write(::ncmk::log::Error, __VA_ARGS__)
