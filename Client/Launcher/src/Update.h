#pragma once
#include <string>

// The launcher keeps itself up to date (the same updater Campus Studio uses). The check runs at start,
// again every 5 minutes while the launcher is open and the game is not running, and again when PLAY is
// pressed if the last answer is older than 30 s (App::Poll / App::Play). At start it asks the
// server which version is newest (/api/launcher/v1/app: version, build, sha256, size). When that
// build is higher than this exe's LV_VERSION_BUILD it downloads the exe (/api/launcher/v1/app/<sha256>)
// to %LOCALAPPDATA%\CampusLauncher\update\, checks the SHA-256, renames the running exe to
// <name>.old.exe (Windows lets a running exe be renamed, not replaced), puts the new one in its
// place and starts it. The next start deletes the .old.exe.
// Releases are made by tools\build_release.bat: it raises the version by one (1.01 -> 1.02) and
// stages the exe in Deploy\launcher-app for UploadUpdate.bat.

std::string ExePath();
// Delete <name>.old.exe files left by an update (false while one is still in use).
bool RemoveOldExe();
// SHA-256 of a file as lower-case hex ("" when it can't be read).
std::string Sha256File(const std::string& path);
// Swap the running exe for `newExe` (already checked) and start it with the same command line.
// On failure the running exe is put back and `error` says why.
bool ApplyUpdate(const std::string& newExe, std::string& error);
