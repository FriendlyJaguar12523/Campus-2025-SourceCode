#pragma once
#include <cstdint>
#include <string>
#include <vector>

// %LOCALAPPDATA%\CampusLauncher (created), or the --data folder (testing).
std::string AppDataDir();
void SetAppDataDir(const std::string& dir);

// The launcher's choices, saved in %LOCALAPPDATA%\CampusLauncher\settings.json.
struct Settings
{
    std::string server = "https://camprec.net";   // --server <url> overrides it (testing)
    std::string gameDir;          // folder holding Recroom_Release.exe ("" = look for it)
    int mode = 0;                 // 0 desktop, 1 VR
    int windowMode = 0;           // 0 windowed, 1 fullscreen, 2 borderless (desktop only)
    int resolution = 0;           // index into Game.h kResolutions (0 = the game's own choice)
    bool closeOnPlay = false;     // close the launcher once the game is running
    std::string gameSha;          // the game zip the launcher installed (UPDATE when the server's differs; "" = installed by hand)
    std::vector<float> uiGradient;   // the last in-game gradient seen (4 x RGB), so the launcher opens in it; empty = Campus
    // Developer accounts only (the server's /me says dev:true): which patch PLAY downloads. "public" (everyone), "beta", "dev" or "2026".
    // "dev" also starts the game against the developer's OWN server (devServer, https) instead of Campus.
    // "2026" plays the Rec Room 2026 client (gameDir2026, not gameDir) with the Campus 2026 patch, on the live Campus servers.
    std::string channel = "public";
    std::string gameDir2026;      // folder holding RecRoom.exe of the 2026 client ("" = look in the Steam libraries)
    std::string devServer = "localhost:44334";
    std::string devServerDir, devNetDir;     // the folders of the developer's own Campus server / CampusNet ("" = the Campus2025Server-2025 repo next to the launcher)
    int lastSeenBuild = 0;        // LV_VERSION_BUILD of the exe the user last ran - mismatch means we just self-updated

    void Load();
    void Save() const;
    std::string Api(const std::string& path) const { return server + path; }
};

// The signed-in account, saved in %LOCALAPPDATA%\CampusLauncher\session.json. Only the 30-day
// launcher token is kept (never the password); it works on the launcher's own routes and nowhere
// else - it can't play, post or reach the admin panel.
struct Session
{
    std::string token, username;
    uint64_t id = 0;
    int64_t expires = 0;          // unix seconds

    bool Valid() const;
    bool Load();
    void Save() const;
    void Clear();
};
