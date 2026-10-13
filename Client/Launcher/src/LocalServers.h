#pragma once
#include <string>

// The Dev release channel (developer accounts): PLAY starts the developer's OWN Campus server and CampusNet (the Photon game server)
// on this PC - if they are not already running - and waits for the server before the game starts. When the game closes the launcher
// stops what IT started (a server that was already running is left alone).
//
// Both are started from their built output (bin\Release\net8.0\CampusServer.exe / campusnet.exe), building first when there is none
// or the source is newer. The server runs with CAMPUS_LOCAL_DEV=1 (its login gates off) on https://localhost:<port> from the
// dev server address, plus a plain http port that CampusNet calls back on. Their output goes to <launcher data>\localserver.log /
// localnet.log.
struct LocalServerOptions
{
    std::string serverDir;        // the Campus server project (CampusServer.csproj) - "" = look for Campus2025Server-2025 next to the launcher
    std::string netDir;           // the CampusNet project (CampusNet.csproj)         - "" = <server repo>\CampusNet
    std::string devServer;        // host:port the game will talk to, e.g. localhost:44334
    std::string launcherDir;      // where the launcher runs from (to find the repo next to it)
    std::string logDir;           // where the logs go
};

// Blocks (run it on a worker thread). "" = the servers are up; otherwise why not (shown to the player).
std::string StartLocalServers(const LocalServerOptions& o);
// What StartLocalServers is doing right now (any thread).
std::string LocalServersStatus();
// Stop what StartLocalServers started (not a server the developer had already running): the Campus server is asked to shut down
// cleanly (then ended if it doesn't), CampusNet is ended. How many were stopped. Blocks up to ~12 s - run it on a worker thread.
int StopLocalServersWeStarted(const LocalServerOptions& o);
bool LocalServersStartedByUs();
// Stop the Campus server and CampusNet that run from those folders (whoever started them). How many processes were stopped.
int StopLocalServers(const LocalServerOptions& o);
// The folders that will be used (the settings, or what was found next to the launcher).
std::string ResolveServerDir(const LocalServerOptions& o);
std::string ResolveNetDir(const LocalServerOptions& o);
