#pragma once
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

// Installing (or updating) the game from the server: GET /api/launcher/v1/game/<sha256> is the
// whole game as one zip (Campus.zip, staged by tools\release_app.py game). The launcher downloads
// it next to the install folder (resuming a broken download), checks it against the server's
// SHA-256, unpacks it (the zip's own top folder, e.g. Campus2025\, becomes the game folder) and
// deletes the zip.
struct InstallJob
{
    std::atomic<int> phase{ 0 };                  // 0 downloading, 1 checking, 2 unpacking
    std::atomic<uint64_t> got{ 0 }, total{ 0 };   // bytes of the current phase
    std::atomic<bool> cancel{ false }, done{ false }, ok{ false };
    std::mutex mtx;
    std::string error;                            // why it stopped ("" when ok or cancelled)
    std::string gameDir;                          // the installed game folder (ok)
};

struct InstallOptions
{
    std::string url, token;      // the zip's download address + the launcher token
    std::string sha;             // its SHA-256 (from /api/launcher/v1/game)
    uint64_t size = 0;           // its size
    std::string parentDir;       // where to put it: <parentDir>\<zip's top folder>
};

void StartInstall(const InstallOptions& o, std::shared_ptr<InstallJob> job);

// Free bytes on the drive holding `dir` (0 when unknown).
uint64_t FreeBytes(const std::string& dir);
