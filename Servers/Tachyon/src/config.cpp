#include "config.h"

#include <fstream>
#include <sstream>

namespace {
    std::string trim(const std::string& s) {
        size_t a = s.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) return "";
        size_t b = s.find_last_not_of(" \t\r\n");
        return s.substr(a, b - a + 1);
    }

    bool parse_bool(const std::string& v, bool& out) {
        if (v == "true" || v == "1" || v == "yes" || v == "on") { out = true; return true; }
        if (v == "false" || v == "0" || v == "no" || v == "off") { out = false; return true; }
        return false;
    }
}

bool load_config(const std::string& path, config& cfg, std::string& err) {
    std::ifstream f(path);
    if (!f) { err = "cannot open config file '" + path + "'"; return false; }

    std::string line;
    int lineno = 0;
    while (std::getline(f, line)) {
        lineno++;
        std::string s = trim(line);
        if (s.empty() || s[0] == '#') continue;

        auto eq = s.find('=');
        if (eq == std::string::npos) {
            err = "line " + std::to_string(lineno) + ": expected key = value";
            return false;
        }
        std::string key = trim(s.substr(0, eq));
        std::string val = trim(s.substr(eq + 1));

        auto bad = [&](const char* why) {
            err = "line " + std::to_string(lineno) + " (" + key + "): " + why;
            return false;
        };

        if (key == "port") cfg.port = (uint16_t) std::stoi(val);
        else if (key == "room_url") cfg.room_url = val;
        else if (key == "room_timeout_ms") cfg.room_timeout_ms = std::stol(val);
        else if (key == "private_key") cfg.private_key_path = val;
        else if (key == "ds_count_width") cfg.ds_count_width = std::stoi(val);
        else if (key == "encrypt") { if (!parse_bool(val, cfg.encrypt)) return bad("expected a boolean"); }
        else if (key == "fork") { if (!parse_bool(val, cfg.fork)) return bad("expected a boolean"); }
        else if (key == "dissonance") { if (!parse_bool(val, cfg.dissonance)) return bad("expected a boolean"); }
        else if (key == "rooms") { if (!parse_bool(val, cfg.rooms)) return bad("expected a boolean"); }
        else if (key == "log_level") {
            if (val == "error") cfg.log_level = LogLevel::Error;
            else if (val == "warn") cfg.log_level = LogLevel::Warn;
            else if (val == "info") cfg.log_level = LogLevel::Info;
            else if (val == "debug") cfg.log_level = LogLevel::Debug;
            else return bad("expected error|warn|info|debug");
        }
        else return bad("unknown config key");
    }
    return true;
}
