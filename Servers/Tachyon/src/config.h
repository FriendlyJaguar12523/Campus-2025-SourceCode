#pragma once
//
// Runtime configuration, loaded from a simple `key = value` file (no dependency). CLI flags still
// win over the file. Unknown keys are an error so typos surface. See tachyon.conf.example.
//
#include "log.h"

#include <cstdint>
#include <string>

struct config {
    uint16_t port = 7777;
    bool encrypt = true;
    bool fork = true;
    bool dissonance = true;
    bool rooms = true;
    int ds_count_width = 2;
    LogLevel log_level = LogLevel::Info;

    std::string room_url = "https://match.recflare.net/tachyon";
    long room_timeout_ms = 2000;

    // Path to a file holding the RSA private key as .NET RSAKeyValue XML. Its public half is the
    // key the client encrypts AT/VB/CKA/CIA/CPK to; the server decrypts those with this key.
    std::string private_key_path;
};

// Load `path` over the defaults already in `cfg`. Returns false and sets `err` on any problem
// (missing file, bad line, unknown key, bad value).
bool load_config(const std::string& path, config& cfg, std::string& err);
