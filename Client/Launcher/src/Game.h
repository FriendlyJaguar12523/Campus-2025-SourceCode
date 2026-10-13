#pragma once
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

// Finding the game, checking its files and starting it with the Campus patch.
//
// Start = the old launcher's flow: run Recroom_Release.exe from its own folder, wait for
// GameAssembly.dll + Referee.dll to load, then load the Campus patch into it. The patch is NOT kept
// on disk: the launcher downloads it from the server for every Play (signed in only) to a random
// name in <game>\CampusPatch\Patch\ (the patch finds its Assets and Logs relative to itself), and
// deletes it as soon as the game closes. A copy left by a crash is deleted at the next start.
// "+forcemode:screen" / "+forcemode:vr" picks desktop or VR (the patch reads it); desktop also
// gets Unity's own window switches (-screen-fullscreen, -window-mode, -screen-width/-height).

constexpr const char* kGameExe = "Recroom_Release.exe";

struct Resolution { const char* label; int w, h; };
extern const Resolution kResolutions[];
extern const int kResolutionCount;

// Is this the game folder (Recroom_Release.exe in it)?
bool IsGameDir(const std::string& dir);
// Look for the game: launcher.ini GamePath, the launcher's own folder and up to 5 parents (and a
// "Campus" subfolder of each), then every drive 3 folders deep. "" = not found. Can take seconds.
std::string FindGameDir(const std::string& launcherDir);
// Files the game needs that aren't there (relative names; empty = all present).
std::vector<std::string> MissingFiles(const std::string& gameDir);
// Folder picker (the Windows one). "" = cancelled.
std::string PickFolder(void* owner, const std::string& start);
// The same picker with its own title (the 2026 client's folder).
std::string PickFolderTitled(void* owner, const std::string& start, const wchar_t* title);

struct LaunchOptions
{
    std::string gameDir;
    bool vr = false;
    int windowMode = 0;           // 0 windowed, 1 fullscreen, 2 borderless
    int resolution = 0;           // kResolutions index, 0 = the game's own choice
    std::string patchDll;         // the downloaded patch (deleted when the game closes)
    // Launcher-launch token "accountId|steamId|ts|hmac" (LauncherKey.h). Passed to the game as
    // the CAMPUS_LAUNCH_TOKEN env var so the patch can forward it to the server. Empty = a
    // test launch that won't satisfy the server's launcher-launch gate.
    std::string launchToken;
    // The Dev release channel (developer accounts): the game is pointed at the developer's own server. Passed as the
    // CAMPUS_NS_HOST ("localhost:44334") and CAMPUS_PHOTON_HOST ("127.0.0.1") environment variables the patch reads. Empty = Campus.
    std::string nsHost, photonHost;
};

// <game>\PATCH - where the patch goes (the folder to exclude in an antivirus).
std::string PatchFolder(const std::string& gameDir);
// Create <game>\PATCH with the patch's menu art in PATCH\Assets (embedded in the launcher).
void EnsurePatchFolder(const std::string& gameDir);

// Where to save the downloaded patch for this launch: <game>\PATCH\cp_<random>.dll. (withAssets: also lay out PATCH\Assets, the 2025
// patch's menu art - the 2026 patch doesn't use it.)
std::string PatchCopyPath(const std::string& gameDir, bool withAssets = true);
// Delete patch copies left behind (a crash, the PC turned off) and the old shipped CampusPatch.dll.
// Copies still loaded in a running game stay until it closes.
void CleanPatchCopies(const std::string& gameDir);

// One launch, run on its own thread and polled by the UI.
struct LaunchJob
{
    std::atomic<bool> done{ false }, ok{ false };
    std::atomic<bool> exited{ false };      // the game has closed and the patch copy is gone
    std::atomic<unsigned long> pid{ 0 };
    std::mutex mtx;
    std::string status;           // what it's doing / why it failed
    std::vector<std::string> log;
    void Say(const std::string& s);      // status + log
    void Note(const std::string& s);     // log only
    std::string Status();
};

void StartGame(const LaunchOptions& o, std::shared_ptr<LaunchJob> job);
// Is the game running? (any Recroom_Release.exe)
bool GameRunning();
