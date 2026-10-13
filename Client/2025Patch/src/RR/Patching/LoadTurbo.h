#pragma once
// ================================================================
// LOAD TURBO - lift the frame cap while a room's bundle downloads
// ================================================================
// The game takes a room's baked asset bundle (25-125 MB) in small pieces, ONE PIECE PER RENDERED FRAME: three
// downloads in one session moved 13.9 / 14.1 / 13.9 KB per frame (84.6 MB over 5,990 frames, 118.5 MB over 8,517,
// 107.7 MB over 7,771) at exactly the monitor's 59 Hz - while the same file arrives over the same network in 7
// seconds for anything else on the PC. So with vsync on, a 118 MB bundle takes ~2 minutes; the faster the loading
// screen renders, the faster it downloads.
//
// So: when the game asks for a room bundle (GET .../cdn/room/<name>.assetbundle) the Present hook presents with
// SyncInterval 0 (no vsync) until the game reports how the join went (POST .../roominstance/<id>/reportjoinresult,
// sent when the load finished or failed) - or 5 minutes, whichever comes first. Everything else keeps vsync.
// (SendRequest.h calls OnRequest for every URL; main.cpp's Present hooks call Active().)
#include <atomic>
#include <chrono>
#include <iostream>
#include <string>

namespace LoadTurbo {

    inline std::atomic<long long> g_until{ 0 };          // steady_clock ms; 0 = off

    inline long long NowMs() {
        return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    inline bool Active() {
        long long u = g_until.load(std::memory_order_relaxed);
        return u != 0 && NowMs() < u;
    }

    inline void OnRequest(const std::string& url) {
        if (url.find("/cdn/room/") != std::string::npos && url.find(".assetbundle") != std::string::npos) {
            bool was = Active();
            g_until.store(NowMs() + 5 * 60 * 1000);
            if (!was) std::cout << "[LoadTurbo] room bundle download started - vsync off until the join result is reported\n";
        }
        else if (url.find("/reportjoinresult") != std::string::npos) {
            if (Active()) std::cout << "[LoadTurbo] join result reported - vsync back on\n";
            g_until.store(0);
        }
    }
}
