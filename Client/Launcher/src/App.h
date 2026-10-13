#pragma once
#include "Game.h"
#include "Game2026.h"
#include "LocalServers.h"
#include "Install.h"
#include "Http.h"
#include "Settings.h"
#include "Tex.h"
#include "json.hpp"
#include <atomic>
#include <memory>
#include <string>
#include <thread>
#include <vector>

void ReleaseSingleInstance();   // main.cpp

// The in-game UI gradient as the launcher edits it (the server keeps it per account:
// /api/launcher/v1/theme; the patch reads the same thing in game).
struct UiTheme
{
    bool enabled = true;
    std::string preset = "campus";     // a preset id, or "custom"
    float stops[4][3]{};
    float accent[3]{};
    bool operator==(const UiTheme& o) const;
};

struct UiPreset
{
    std::string id, name;
    float stops[4][3]{};
    float accent[3]{};
};

// The launcher window: a top bar (logo, Play / Customize / Settings, the account) over one page.
// The window has no Windows frame: the top bar is the title bar (main.cpp asks IsCaption).
class App
{
public:
    void Init(void* hwnd, const std::string& serverOverride);
    void DebugPreset(const std::string& list);   // Debug build: --dbg offline,connecting,dns,updating,nogame,gameupdate,installing,running,ban,permban,signedout,nopanel
    void Frame();
    void Shutdown();
    bool WantsQuit() const { return quit_; }
    bool WantsHidden() const { return hideWindow_; }
    // Window-relative pixel: is it the top bar's empty space (drag the window by it)?
    bool IsCaption(float x, float y) const;
    void ShowPage(const std::string& name);       // --page (testing): play / customize / settings
    // Testing: --nofind (don't search the drives for the game), --install-dir DIR (INSTALL goes there, no folder picker).
    void SetTestOptions(bool noFind, const std::string& installDir) { noFind_ = noFind; testInstallDir_ = installDir; }

private:
    using json = nlohmann::json;
    enum class Page { Play, Customize, Settings, Debug };   // Debug: the DEBUG tab, only reachable in the Debug build

    // ---- pieces of the window
    void DrawTopBar(float h);
    void DrawPlay();
    void DrawSignIn();
    void DrawOffline();
    void DrawDebug();                   // the DEBUG tab (Debug build only - an empty stub in Release)
    void DrawCustomize();
    void DrawSettings();
    void DrawLog();
    void DrawUpdating();

    // ---- work
    void Poll();
    void CheckForUpdate();
    void SignIn();
    void SignOut(const std::string& why = "");
    void RefreshMe();
    void LoadTheme();
    void LinkSteam();
    void CopyPlayerLog(bool previous);   // Settings > Game log: Rec Room's Player.log -> next to the launcher
    void FetchGameInfo();
    void InstallGame(bool update);
    void SaveTheme();
    void FetchImage(Tex& tex, std::string& loadedUrl, std::shared_ptr<NetJob>& job, const std::string& path, int width);
    void Play();
    void FindGame();
    void CheckFiles();
    void ChooseFolder();
    void Log(const std::string& s);
    std::string Url(const std::string& path) const { return S.Api(path); }
    bool Busy() const;
    std::string Ellipsize(const std::string& s, float width) const;
    std::string DisplayName() const;

    void* hwnd_ = nullptr;
    bool quit_ = false, hideWindow_ = false;
    Settings S;
    Session session_;
    std::string launcherDir_;
    Page page_ = Page::Play;

    // top bar: the clickable bits (everything else in it drags the window)
    float barH_ = 0;
    std::vector<std::pair<float, float>> hotX_;   // x ranges of buttons in the bar

    // brand art (embedded)
    Tex hero_;
    std::vector<uint8_t> heroPx_;       // the art as shipped (Campus colours), recoloured into hero_
    std::vector<uint8_t> heroMask_;     // HeroMask.png: how much of each pixel is backdrop / logo (R)
    int heroW_ = 0, heroH_ = 0;
    void PaintHero(const float (*stops)[3]);
    void EnsureHero();
    std::vector<float> heroIn_;         // the gradient hero_ was last painted in
    bool heroPainted_ = false;
    std::vector<float> drawnIn_;        // the gradient the launcher is drawn in now (empty = Campus)
    void FollowGradient();

    // update: 0 not checked, 1 checking, 2 downloading, 3 done
    int updPhase_ = 0;
    std::shared_ptr<NetJob> updJob_;
    std::string updVersion_, updError_;
    bool updLatest_ = false;
    unsigned long long oldExeAt_ = 0;
    // ---- DEBUG panel overrides. In a Release build nothing ever sets these, so every check below is false;
    // in the Debug build the panel (F12) sets them to show screens that are hard to reach for real
    // (no internet, a launcher / game update, a ban ...) without actually having them.
    struct DebugOverrides
    {
        bool panelOpen = true;
        bool offline = false;           // the "Can't connect to Campus" screen
        bool connecting = false;        // the "Connecting to Campus..." screen
        bool launcherUpdating = false;  // the "Updating the launcher" progress box
        bool noGame = false;            // game not installed: DOWNLOAD
        bool gameUpdate = false;        // a newer game build: UPDATE
        bool installing = false;        // a FAKE download / check / unpack (nothing is downloaded)
        double installStart = 0;
        bool running = false;           // "Campus is running"
        int ban = 0;                    // 0 none, 1 timed ban, 2 permanent ban
        bool signedOut = false;         // the sign-in screen
        bool dnsBanner = false;         // the "Can't reach Campus - Fix connection" strip
        bool devAccount = false;        // show the release-channel drop-down (a developer account)
    };
    DebugOverrides dbg_;
    int UpdPhase() const { return dbg_.launcherUpdating ? 2 : updPhase_; }
    std::string UpdVersionShown() const { return dbg_.launcherUpdating ? "9.99" : updVersion_; }
    void DebugInstallState(int& phase, uint64_t& got, uint64_t& total);
    // Staying current: the server is asked at start, again every 5 minutes while the launcher is open
    // (not while the game runs), and again when PLAY is pressed if the last answer is older than 30 s.
    // Nothing in the launcher loads until the Campus server has answered once: false = the "connecting" /
    // "can't connect" screen instead of the pages. The check retries every few seconds until it does.
    bool online_ = false;
    unsigned long long updCheckedAt_ = 0;      // GetTickCount64 when the last check finished
    unsigned long long updNextCheckAt_ = 0;    // when the next background check is due
    bool playAfterUpdateCheck_ = false;        // PLAY was pressed with a stale answer: re-ask first, then play
    // Just-updated flow: when this exe's LV_VERSION_BUILD is higher than the one the last run
    // wrote to Settings.lastSeenBuild, the launcher starts with justUpdatedFromVer_ set to the
    // old version (e.g. "1.10") and shows a green "Updated to 1.11" chip on PLAY for a bit.
    // Settings.lastSeenBuild is rewritten to this build right away so the chip only shows once.
    std::string justUpdatedFromVer_;
    unsigned long long justUpdatedAt_ = 0;      // when the chip became visible (GetTickCount64)

    // game folder search
    std::shared_ptr<std::atomic<int>> findState_;   // 0 searching, 1 done
    std::shared_ptr<std::string> findResult_;
    std::vector<std::string> missing_;
    bool cleaned_ = false;

    // account
    std::string loginUser_, loginPass_, loginError_;
    std::shared_ptr<NetJob> loginJob_, meJob_;
    json me_;
    unsigned long long meAt_ = 0;
    Tex avatar_;
    std::string avatarUrl_;
    std::shared_ptr<NetJob> avatarJob_;

    // the game download (Install.h): the server's zip, and an install / update in progress
    std::shared_ptr<NetJob> gameInfoJob_;
    std::string gameSha_;
    uint64_t gameSize_ = 0, gameUnpacked_ = 0;
    std::shared_ptr<InstallJob> install_;
    bool noFind_ = false;
    std::string testInstallDir_;
    bool installUpdate_ = false;
    double speedAt_ = 0, speed_ = 0;
    uint64_t speedGot_ = 0;

    // Steam link (Steam.h): tried once per sign-in, again from Settings
    std::shared_ptr<NetJob> steamJob_;
    bool steamTried_ = false, steamBad_ = false;
    bool playAfterSteam_ = false;       // PLAY waits for this PC's Steam to be whitelisted first
    bool steamCheckedForPlay_ = false;
    bool SteamWhitelisted() const;      // is this PC's Steam on the server's normal-player whitelist (via /me)?
    bool AccountBanned() const;         // the signed-in account has an active ban/timeout (via /me)
    bool IsDev() const;                 // a developer / owner account (via /me "dev"): gets the release-channel drop-down
    std::string Channel() const;
    bool Is2026() const { return Channel() == "2026"; }
    bool cleaned2026_ = false;          // the 2026 PATCH folder was tidied this run
    std::string GameDirNow() const { return Is2026() ? S.gameDir2026 : S.gameDir; }   // the folder PLAY uses on the current channel     // the 2026 channel: another game (RecRoom.exe, S.gameDir2026), the live servers
    void Find2026();                    // look for the 2026 client in the Steam libraries
    void Choose2026Folder();
    void LaunchGame();                  // start the game with the patch that was just downloaded
    LocalServerOptions LocalOpts() const;   // the Dev channel's own servers (LocalServers.h)
    std::shared_ptr<NetJob> localJob_, stopJob_;  // starting the Dev channel's local servers (playPhase_ 4)
    std::string localMsg_;              // Settings > Developer: what the last "Stop them" did        // "public", or "beta" / "dev" when this is a developer account that chose it
    std::string ChannelQuery() const { std::string c = Channel(); return c == "public" ? std::string() : "?channel=" + c; }
    std::string BanLine() const;        // human-friendly "This account is banned [permanently | for 2 h 15 min]. Reason: ..."
    // The HMAC-signed launch token the launcher mints at Play time: "accountId|steamId|ts|hmac",
    // signed with CAMPUS_LAUNCHER_LAUNCH_KEY. Passed to the game through the CAMPUS_LAUNCH_TOKEN
    // env var; the patch forwards it to the server on /api/patch/proof so the login endpoint can
    // tell "launched by the current launcher" from "ran outside the launcher with a copy of the
    // current patch DLL". The server yanks a Steam id from the normal-player whitelist on the
    // second case. Returns "" when there is no signed-in account.
    std::string MintLaunchToken() const;
    // DNS fallback (HostsFix.h): some ISPs null-route camprec.net / gameauth.ddns.net and the game fails to
    // connect with "error code: summer". The launcher checks DNS at startup and if it fails,
    // offers a one-click hosts-file write (needs admin once). dnsBad_ is set by a background
    // thread so a hung resolver can't stall the UI; dnsFixing_ is up while the UAC prompt runs.
    std::atomic<int> dnsState_{ 0 };    // 0 = checking, 1 = ok, 2 = failed
    std::atomic<bool> dnsFixing_{ false };
    std::string dnsFixResult_;          // "" normally, else the latest attempt's result message
    void CheckDns();                    // background thread; sets dnsState_
    void FixDns();                      // elevate + write hosts + re-check (runs on background thread)
    std::string steamMsg_;
    std::string playerLogMsg_, playerLogFile_;   // result of the last "copy the game log" (and the file it made)
    bool playerLogBad_ = false;

    // the in-game UI gradient
    std::vector<UiPreset> presets_;
    std::shared_ptr<NetJob> presetsJob_, themeJob_, saveJob_;
    UiTheme theme_, themeSaved_, saving_;
    UiTheme custom_;                    // the player's own colours, kept while they try presets
    double themeChangedAt_ = 0;         // saved by itself a moment after the last change
    bool themeLoaded_ = false;
    std::string themeMsg_;
    bool themeMsgBad_ = false;
    int editSlot_ = -1;                 // the colour being picked: 0-3 the stops, 4 the accent
    float swatchY_ = 0;                 // screen y of the swatch row's middle (the wheel opens beside it)

    // play: 0 idle, 1 asking for the patch, 2 downloading it, 3 game starting/running, 4 starting the Dev channel's local servers
    int playPhase_ = 0;
    std::shared_ptr<NetJob> patchJob_;
    std::string patchSha_, patchPath_;
    std::shared_ptr<LaunchJob> launch_;
    size_t launchLogSeen_ = 0;
    std::string status_ = "Ready to play.";
    bool statusBad_ = false;

    bool showLog_ = false;
    std::vector<std::string> log_;
};
