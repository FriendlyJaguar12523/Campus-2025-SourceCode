// Tachyon voice server - C++ entry point.
//
// Mirrors the working C# reference-server invocation `--encrypt-layer --fork --dissonance`, which are the
// defaults here. The transport is LiteNetLibPP; the RecRoom trailing-marker encrypt layer is
// installed through net_manager's outbound/inbound hooks (the small vendored patch).
#include "tachyon_server.h"
#include "wire.h"
#include "log.h"
#include "room_lookup.h"
#include "config.h"
#include "crypto.h"

#include <lnl/net_manager.h>
#include <curl/curl.h>

#include <atomic>
#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace {
    constexpr uint8_t PLAINTEXT_MARKER = 0x11;
    constexpr uint8_t ENCRYPTED_MARKER = 0x2A;

    // Set from a signal handler so the main loop exits cleanly (systemd sends SIGTERM to stop).
    std::atomic<bool> g_stop{false};
    void on_signal(int) { g_stop.store(true); }

    bool parse_level(const std::string& s, LogLevel& out) {
        if (s == "error") out = LogLevel::Error;
        else if (s == "warn") out = LogLevel::Warn;
        else if (s == "info") out = LogLevel::Info;
        else if (s == "debug") out = LogLevel::Debug;
        else return false;
        return true;
    }
}

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IOLBF, 0);   // line-buffered so logs appear live even when piped
    config cfg;                            // defaults; a --config file loads over them; CLI wins

    // Pass 1: load the config file first, so CLI flags can override it.
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        if (a == "--config" && i + 1 < argc) {
            std::string err;
            if (!load_config(argv[i + 1], cfg, err)) {
                LOGE("[fatal] config: %s\n", err.c_str());
                return 2;
            }
        }
    }
    tlog::level = cfg.log_level;

    // Pass 2: CLI flags override the config.
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        if (a == "--config") { ++i; }   // already handled
        else if (a == "--port" && i + 1 < argc) cfg.port = (uint16_t) std::stoi(argv[++i]);
        else if (a == "--no-encrypt") cfg.encrypt = false;
        else if (a == "--no-fork") cfg.fork = false;
        else if (a == "--no-dissonance") cfg.dissonance = false;
        else if (a == "--no-rooms") cfg.rooms = false;
        else if (a == "--room-url" && i + 1 < argc) cfg.room_url = argv[++i];
        else if (a == "--room-timeout" && i + 1 < argc) cfg.room_timeout_ms = std::stol(argv[++i]);
        else if (a == "--private-key" && i + 1 < argc) cfg.private_key_path = argv[++i];
        else if (a == "--verbose" || a == "-v") cfg.log_level = tlog::level = LogLevel::Debug;
        else if (a == "--quiet" || a == "-q") cfg.log_level = tlog::level = LogLevel::Warn;
        else if (a == "--log-level" && i + 1 < argc) {
            if (!parse_level(argv[++i], cfg.log_level)) {
                LOGE("[fatal] unknown --log-level '%s' (use error|warn|info|debug)\n", argv[i]);
                return 2;
            }
            tlog::level = cfg.log_level;
        }
        else if (a == "--ds-count-width" && i + 1 < argc) cfg.ds_count_width = std::stoi(argv[++i]);
        else if (a == "--selftest") {
            const char* hexfile = (i + 1 < argc && argv[i + 1][0] != '-') ? argv[++i] : nullptr;
            tachyon_server st; st.fork = cfg.fork; st.ds_count_width = cfg.ds_count_width;
            return st.run_selftest(hexfile);   // offline, no network / no curl
        }
        else {
            LOGE("[fatal] unknown argument '%s'\n", a.c_str());
            return 2;
        }
    }

    tachyon_server server;
    server.fork = cfg.fork;
    server.dissonance = cfg.dissonance;
    server.ds_count_width = cfg.ds_count_width;
    bool encrypt = cfg.encrypt;

    // Load the RSA private key (decrypts the client's AT/VB/CKA/CIA/CPK blobs at handshake).
    rsa_key key;
    if (!cfg.private_key_path.empty()) {
        std::ifstream kf(cfg.private_key_path);
        if (!kf) { LOGE("[fatal] cannot read private key '%s'\n", cfg.private_key_path.c_str()); return 2; }
        std::stringstream ss; ss << kf.rdbuf();
        std::string err;
        if (!key.load_xml(ss.str(), err)) { LOGE("[fatal] private key: %s\n", err.c_str()); return 2; }
        server.priv_key = &key;
    }

    curl_global_init(CURL_GLOBAL_DEFAULT);
    room_lookup rooms_lookup(cfg.room_url, cfg.room_timeout_ms);
    if (cfg.rooms)
        server.room_resolver = [&](const std::string& account) { return rooms_lookup.resolve(account); };

    // Startup banner at Info (a --quiet service suppresses it); the id-space table is Debug detail.
    LOGI("Tachyon voice server (C++ / LiteNetLibPP)\n");
    LOGI("  LiteNetLib ProtocolId : 13\n");
    LOGI("  listening             : udp/%u\n", cfg.port);
    LOGI("  fork header           : %s\n", server.fork ? "ON (24-byte)" : "off (stock 16-byte)");
    LOGI("  dissonance session    : %s\n", server.dissonance ? "ON" : "off (parse+log only)");
    if (cfg.rooms) LOGI("  room scoping          : ON via %s (per-room session)\n", cfg.room_url.c_str());
    else           LOGI("  room scoping          : off (single shared session)\n");
    LOGI("  encrypt layer         : %s\n", encrypt ? "ON, RecRoom trailing-marker (plaintext 0x11)" : "off");
    if (server.priv_key) LOGI("  RSA private key       : loaded (%d-bit) - decrypting client blobs\n", key.bits());
    else                 LOGI("  RSA private key       : none (client blobs left encrypted)\n");
    LOGD("  message id space:\n");
    for (size_t i = 0; i < tachyon_server::MESSAGE_ORDER.size(); i++)
        LOGD("    [%zu] 0x%08x  %s\n", i, wire::message_hash(tachyon_server::MESSAGE_ORDER[i]),
             tachyon_server::MESSAGE_ORDER[i]);

    lnl::net_manager manager(&server);

    if (encrypt) {
        // Server-side RecRoomClientEncryptLayer. Plaintext path only (key == null): append one
        // 0x11 marker outbound, strip the trailing marker inbound. 0x2A (encrypted) is dropped -
        // the Burst cipher is unidentified and the whole handshake runs key-null anyway.
        manager.outbound_layer = [](const uint8_t* data, size_t length, std::vector<uint8_t>& out) -> size_t {
            out.assign(data, data + length);
            out.push_back(PLAINTEXT_MARKER);
            return out.size();
        };
        manager.inbound_layer = [](uint8_t* data, size_t length) -> size_t {
            if (length < 1) return 0;
            uint8_t marker = data[length - 1];
            if (marker == PLAINTEXT_MARKER) return length - 1;
            // Dropped datagrams are Debug: a hostile peer could otherwise flood the log.
            if (marker == ENCRYPTED_MARKER)
                LOGD("[enc ] <- ENCRYPTED packet (marker 0x2A, len %zu) - cipher not implemented, dropping\n", length);
            else
                LOGD("[enc ] <- unknown marker 0x%02x (len %zu), dropping\n", marker, length);
            return 0;
        };
    }

    if (!manager.start(cfg.port)) {
        LOGE("[fatal] failed to bind udp/%u\n", cfg.port);
        return 1;
    }

    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);

    while (manager.is_running() && !g_stop.load()) {
        manager.poll_events();
        server.pump_time_sync();
        // Poll responsively while clients are connected (voice needs low relay latency), but back
        // right off when the server is empty so an idle instance barely registers on the CPU.
        std::this_thread::sleep_for(std::chrono::milliseconds(server.has_clients() ? 5 : 50));
    }

    LOGI("shutting down\n");
    curl_global_cleanup();
    return 0;
}
