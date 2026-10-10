// NCMK SDK - per-mod configuration file: <modsRoot>/Config/<modId>.cfg
//
//   # comments start with '#' or ';'
//   overlay.enabled = true
//   overlay.distance = 1.2
//   greeting = Hello club
//
// Usage (any thread; file I/O only, no Unity calls):
//
//   NCMK::Config cfg;                         // uses the id passed to NCMK::Init
//   cfg.Load();
//   bool on   = cfg.GetBool("overlay.enabled", true);
//   float d   = cfg.GetFloat("overlay.distance", 1.2f);
//   cfg.Save();                               // writes defaults for keys that were missing
//
// Getters record the default of a missing key, so the first Save() produces a complete,
// documented file the player can edit (with adb or the patcher's DeviceMods/Config folder).
#pragma once

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "../Core/ncmk_sdk.hpp"

namespace NCMK {

class Config {
public:
    /// modId defaults to the id given to NCMK::Init.
    explicit Config(std::string modId = "") : modId_(modId.empty() ? ModId() : std::move(modId)) {}

    /// <modsRoot>/Config/<modId>.cfg ("" when the loader has no mods root).
    std::string Path() const {
        std::string root = Game::GetModsRoot();
        return root.empty() ? std::string() : root + "/Config/" + modId_ + ".cfg";
    }

    /// Reads the file (missing file = empty config). Returns false only on a read error.
    bool Load() {
        std::lock_guard<std::mutex> g(lock_);
        entries_.clear();
        std::string path = Path();
        if (path.empty()) return false;
        FILE* f = fopen(path.c_str(), "rb");
        if (!f) return errno == ENOENT;
        char line[2048];
        while (fgets(line, sizeof(line), f)) {
            std::string s = Trim(line);
            if (s.empty() || s[0] == '#' || s[0] == ';') continue;
            size_t eq = s.find('=');
            if (eq == std::string::npos) continue;
            std::string key = Trim(s.substr(0, eq)), value = Trim(s.substr(eq + 1));
            if (!key.empty()) Put(key, value);
        }
        fclose(f);
        dirty_ = false;
        return true;
    }

    /// Writes every key (sorted by first appearance). Atomic: temp file + rename.
    bool Save() {
        std::lock_guard<std::mutex> g(lock_);
        std::string path = Path();
        if (path.empty()) return false;
        std::string tmp = path + ".tmp";
        FILE* f = fopen(tmp.c_str(), "wb");
        if (!f) return false;
        fprintf(f, "# %s configuration (NCMK). Edit values, keep the 'key = value' format.\n", modId_.c_str());
        for (const auto& e : entries_) fprintf(f, "%s = %s\n", e.first.c_str(), e.second.c_str());
        bool ok = fclose(f) == 0 && rename(tmp.c_str(), path.c_str()) == 0;
        if (ok) dirty_ = false;
        return ok;
    }

    /// True when a getter added a default or a setter changed a value since Load/Save.
    bool Dirty() const { return dirty_; }

    bool Has(const std::string& key) const {
        std::lock_guard<std::mutex> g(lock_);
        return Find(key) != nullptr;
    }

    std::string GetString(const std::string& key, const std::string& def) {
        std::lock_guard<std::mutex> g(lock_);
        if (const std::string* v = Find(key)) return *v;
        Put(key, def);
        dirty_ = true;
        return def;
    }

    long GetInt(const std::string& key, long def) {
        std::string v = GetString(key, std::to_string(def));
        char* end = nullptr;
        long r = strtol(v.c_str(), &end, 0);
        return end && end != v.c_str() ? r : def;
    }

    float GetFloat(const std::string& key, float def) {
        char buf[64];
        snprintf(buf, sizeof(buf), "%g", def);
        std::string v = GetString(key, buf);
        char* end = nullptr;
        float r = strtof(v.c_str(), &end);
        return end && end != v.c_str() ? r : def;
    }

    bool GetBool(const std::string& key, bool def) {
        std::string v = GetString(key, def ? "true" : "false");
        if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
        if (v == "false" || v == "0" || v == "no" || v == "off") return false;
        return def;
    }

    void Set(const std::string& key, const std::string& value) {
        std::lock_guard<std::mutex> g(lock_);
        const std::string* cur = Find(key);
        if (cur && *cur == value) return;
        Put(key, value);
        dirty_ = true;
    }
    void Set(const std::string& key, long v) { Set(key, std::to_string(v)); }
    void Set(const std::string& key, int v) { Set(key, std::to_string(v)); }
    void Set(const std::string& key, float v) {
        char buf[64];
        snprintf(buf, sizeof(buf), "%g", v);
        Set(key, std::string(buf));
    }
    void Set(const std::string& key, bool v) { Set(key, std::string(v ? "true" : "false")); }
    void Set(const std::string& key, const char* v) { Set(key, std::string(v ? v : "")); }

private:
    static std::string Trim(const std::string& s) {
        size_t a = s.find_first_not_of(" \t\r\n"), b = s.find_last_not_of(" \t\r\n");
        return a == std::string::npos ? std::string() : s.substr(a, b - a + 1);
    }
    const std::string* Find(const std::string& key) const {
        for (const auto& e : entries_)
            if (e.first == key) return &e.second;
        return nullptr;
    }
    void Put(const std::string& key, const std::string& value) {
        for (auto& e : entries_)
            if (e.first == key) {
                e.second = value;
                return;
            }
        entries_.emplace_back(key, value);
    }

    std::string modId_;
    std::vector<std::pair<std::string, std::string>> entries_;
    mutable std::mutex lock_;
    bool dirty_ = false;
};

}  // namespace NCMK
