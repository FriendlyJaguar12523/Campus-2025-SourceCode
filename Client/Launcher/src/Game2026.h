#pragma once
#include "Game.h"

// The "2026" release channel (developer accounts): the Rec Room 2026 client (20260323.14, RecRoom.exe) started with the Campus 2026 patch
// (CampusPatch2025\2026Patch -> Deploy\launcher-patch\2026), connected to the live Campus servers.
//
// A different game from the 2025 one, so it has its own folder (Settings.gameDir2026, found in the Steam libraries when blank) and its
// own start: this file is the community Injector.exe folded into the launcher. Start = check the three client files against the build the
// patch's RVAs were measured on, run RecRoom.exe, hand Referee its local arena before its first use (the stock service that normally
// supplies it is gone), then load the patch DLL. Everything else (patch download, launch token, deleting the copy when the game closes)
// is the same as the 2025 flow.

constexpr const char* kGameExe2026 = "RecRoom.exe";

// Is this the 2026 game folder (RecRoom.exe + the three modules the patch checks)?
bool IsGameDir2026(const std::string& dir);
// Where the 2026 client is: the Steam libraries' steamapps\common\RecRoom. "" = not found.
std::string FindGameDir2026();
// Required files that aren't there (empty = all present).
std::vector<std::string> Missing2026Files(const std::string& dir);
// Folder picker titled for the 2026 client. "" = cancelled.
std::string PickFolder2026(void* owner, const std::string& start);
// Is RecRoom.exe running?
bool GameRunning2026();
// "" when the three client files are the supported 20260323.14 build, otherwise what is wrong (hashes the files: a few seconds).
std::string CheckBuild2026(const std::string& dir);

// Run RecRoom.exe with the patch (o.patchDll) and the launch token. Same job/polling contract as StartGame.
void StartGame2026(const LaunchOptions& o, std::shared_ptr<LaunchJob> job);
