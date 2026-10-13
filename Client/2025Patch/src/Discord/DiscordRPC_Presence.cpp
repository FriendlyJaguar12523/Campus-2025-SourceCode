#include "DiscordRPC_Presence.h"
#include "../RR/Patching/Console.h"
#include "../RR/Patching/ServerConfiguration.h"
#include <thread>
#include <atomic>
#include <iostream>
#include <ctime>
#include <chrono>
#include <cstring>
#include <string>
#include <regex>
#include <sstream>
#include <mutex>
#include <windows.h>
#include <winhttp.h>
#include "json.hpp"
#include "../Utils/obf.h"
#include "../Discord/discord-rpc-win/discord-rpc/win64-dynamic/include/discord_rpc.h"

using json = nlohmann::json;  // Add this line

#pragma comment(lib, "winhttp.lib")

namespace DiscordRPC {

    static std::thread g_thread;
    static std::atomic<bool> g_running{ false };
    static std::atomic<bool> g_initialized{ false };
    static std::atomic<bool> g_discordReady{ false };
    static std::string g_currentRoom = ""; // empty = no room shown yet
    static std::mutex g_roomMutex;

    // Presence mode decides what the "no room label" cases read as:
    //   0 = LOGIN SCREEN  - online but not in any room yet (the very start, before loading in)
    //   1 = HIDDEN        - in a room, but "Appear Online To" isn't All, so the room is withheld
    //   2 = IN A ROOM     - g_currentRoom holds the label to show
    // Starts at login screen; the very first presence push therefore reads "In Login Screen".
    static std::atomic<int> g_presenceMode{ 0 };

    // Set once the player has been in ANY room. "In Login Screen" is a STARTUP-only state (before
    // loading in), so after this is true a transient "not in a room" reads as generic "Playing
    // Campus" instead of flipping back to the login screen.
    static std::atomic<bool> g_everInRoom{ false };

    // Store presence strings to keep them alive
    static std::string g_cachedDetails;
    static std::string g_cachedState;
    static std::mutex g_presenceMutex;

    // Discord Application ID
    static const char* CLIENT_ID = "1535967316729266206";

    // Extract room ID from URL
    static std::string ExtractRoomIdFromUrl(const std::string& url) {
        std::smatch match;
        std::regex roomIdRegex("/Room_server/rooms/([0-9]+)");
        if (std::regex_search(url, match, roomIdRegex) && match.size() > 1) {
            return match[1].str();
        }
        return "";
    }

    // The player's OWN presence, from /api/presence/self/{accountId} - the SAME source the admin
    // panel uses (their heartbeat): current room (name + isPrivate instance flag), that room's
    // accessibility, and whether they appear online to EVERYONE. Works for dorms and private rooms.
    struct RoomInfo {
        std::string name;
        bool isPrivate = false;         // private INSTANCE (dorm / private session)
        int  accessibility = -1;        // room accessibility: 1 Public, 2 Unlisted, 3/4 Dev, -1 unpublished/unknown
        bool appearOnlineToAll = true;  // "Appear Online To" == All
        bool found = false;             // they're in a room
        bool ok = false;                // the HTTP fetch + parse succeeded
    };

    // Fetch the caller's presence from the server (admin-panel data source).
    static RoomInfo FetchSelf(uint64_t accountId) {
        RoomInfo info;
        if (accountId == 0) return info;

        std::wstring host = CampusWiden(CAMPUS_API_HOST);
        std::wstring path = L"/api/presence/self/" + std::to_wstring(accountId);

        HINTERNET hSession = WinHttpOpen(L"DiscordRPC/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, NULL, NULL, 0);
        if (!hSession) return info;

        HINTERNET hConnect = WinHttpConnect(hSession, CampusHostW(host).c_str(), CampusPortOf(host), 0);
        if (!hConnect) {
            WinHttpCloseHandle(hSession);
            return info;
        }

        HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"GET", path.c_str(), NULL, NULL, NULL, WINHTTP_FLAG_SECURE);
        if (!hRequest) {
            WinHttpCloseHandle(hConnect);
            WinHttpCloseHandle(hSession);
            return info;
        }

        // Disable certificate verification for simplicity
        DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID |
            SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
        WinHttpSetOption(hRequest, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));

        // Stamp the patch signature - this WinHTTP call is separate from the game's own requests,
        // so without the header the server's per-request patch gate 403s it and the presence can
        // never read a room (everything falls back to a generic "Playing Campus").
        std::wstring patchHeader = std::wstring(L"X-Campus-Patch: ") + (const wchar_t*)skCrypt(L"CAMPUS-2025-2h7Fc20FKokap9Ah3t") + L"\r\n";
        if (!WinHttpSendRequest(hRequest, patchHeader.c_str(), (DWORD)-1L, NULL, 0, 0, 0)) {
            WinHttpCloseHandle(hRequest);
            WinHttpCloseHandle(hConnect);
            WinHttpCloseHandle(hSession);
            return info;
        }

        if (!WinHttpReceiveResponse(hRequest, NULL)) {
            WinHttpCloseHandle(hRequest);
            WinHttpCloseHandle(hConnect);
            WinHttpCloseHandle(hSession);
            return info;
        }

        std::string response;
        DWORD bytesRead = 0;
        char buffer[4096];
        while (WinHttpReadData(hRequest, buffer, sizeof(buffer) - 1, &bytesRead) && bytesRead > 0) {
            buffer[bytesRead] = '\0';
            response += buffer;
        }

        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);

        // Parse JSON: { found, name, isPrivate, accessibility, appearOnlineToAll }.
        try {
            auto jsonData = json::parse(response);
            info.ok = true;
            info.found = jsonData.value("found", false);
            info.name = jsonData.value("name", std::string(""));
            info.isPrivate = jsonData.value("isPrivate", false);
            info.accessibility = jsonData.value("accessibility", -1);
            info.appearOnlineToAll = jsonData.value("appearOnlineToAll", true);
        }
        catch (const std::exception& e) {
            std::cout << TAG_DISCORD() << "JSON parse error: " << e.what() << "\n";
        }

        return info;
    }

    bool IsRunning() { return g_running.load(); }

    // The local player's account id, captured from the Notifications hub URL (?id=<accountId>) the
    // client opens after login. 0 while still on the login screen.
    static std::atomic<uint64_t> g_localAccountId{ 0 };

    void SetLocalAccountId(uint64_t id) {
        if (id == 0) return;
        uint64_t prev = g_localAccountId.exchange(id);
        if (prev != id) RefreshPresence(); // refresh immediately once we know who we are
    }

    // Set the presence mode + room label, then push. mode: 0 login-screen, 1 hidden, 2 in-room.
    static void SetPresence(int mode, const std::string& room) {
        g_presenceMode = mode;
        {
            std::lock_guard<std::mutex> lock(g_roomMutex);
            g_currentRoom = room;
        }
        if (g_initialized && g_discordReady) UpdatePresence();
    }

    // Recompute the Discord presence from the server (the admin-panel data source). Called on room
    // changes AND on a timer, so it "constantly" reflects the current room AND the live "Appear
    // Online To" setting. Runs on a detached/worker thread; guarded like SetRoomFromUrl.
    void RefreshPresence() {
        if (!g_running.load()) return;
        uint64_t acc = g_localAccountId.load();
        if (acc == 0) return; // still on the login screen - nothing to look up yet
        try {
            RoomInfo info = FetchSelf(acc);
            if (!g_running.load()) return; // may have shut down during the HTTPS wait
            std::cout << TAG_DISCORD() << "presence self acc=" << acc
                << " ok=" << info.ok << " found=" << info.found
                << " priv=" << info.isPrivate << " access=" << info.accessibility
                << " all=" << info.appearOnlineToAll << " name=" << info.name << "\n";
            if (!info.ok) return;          // fetch/parse failed (e.g. server missing the endpoint) - leave as-is

            // NOT in a room: at the very start (never been in a room) that's the login/loading
            // screen; after they've loaded in once it's just the menu/idle -> generic, NOT a flip
            // back to "In Login Screen" (which was overwriting the real room on later polls).
            if (!info.found) { SetPresence(g_everInRoom.load() ? 1 : 0, ""); return; }

            // We're in a room now - past the login screen for good.
            g_everInRoom = true;

            // In a room, but "Appear Online To" isn't All: withhold the room (generic, not a lie
            // about being on the login screen).
            if (!info.appearOnlineToAll) { SetPresence(1, ""); return; }

            // Dev-only / dev-unlisted / unpublished (unknown) maps: just "[PRIVATE]", name masked.
            bool devOrUnpublished = (info.accessibility == 3 || info.accessibility == 4 || info.accessibility < 0);
            if (devOrUnpublished) { SetPresence(2, "[PRIVATE]"); return; }

            // A private INSTANCE of a normal (published) room: "[PRIVATE] <name>".
            if (info.isPrivate) {
                SetPresence(2, info.name.empty() ? "[PRIVATE]" : ("[PRIVATE] " + info.name));
                return;
            }

            // Public instance of a normal room: show the name (single "^", names may already carry one).
            if (info.name.empty())
                SetPresence(0, "");                                  // no name -> treat as not-really-in-a-room
            else if (info.name[0] == '^')
                SetPresence(2, info.name);
            else
                SetPresence(2, "^" + info.name);
        }
        catch (...) {
            // Swallow -- detached thread; an escaping exception becomes std::terminate.
        }
    }

    // Kept for the SendRequest hook (fires on room fetches): just refresh from the server, which
    // uses the captured account id rather than the room id in the URL.
    void SetRoomFromUrl(const std::string& /*url*/) {
        RefreshPresence();
    }

    void SetCurrentRoom(const std::string& roomName) {
        if (roomName.find("visited") != std::string::npos ||
            roomName.find("by") != std::string::npos ||
            roomName.find("unknown") != std::string::npos ||
            roomName.find("null") != std::string::npos ||
            roomName.find("undefined") != std::string::npos ||
            roomName.find("skip") != std::string::npos ||
            roomName.find("take") != std::string::npos ||
            roomName.find("includeCCUs") != std::string::npos ||
            roomName.empty() ||
            roomName.length() > 50 ||
            roomName.find("?") != std::string::npos) {
            return;
        }

        {
            std::lock_guard<std::mutex> lock(g_roomMutex);
            g_currentRoom = roomName;
        }
        // Was: called UpdatePresence() while g_roomMutex was held. UpdatePresence
        // ALSO locks g_roomMutex -> std::mutex is non-recursive -> the second lock
        // hangs the calling thread forever. That thread was usually a detached
        // WinHTTP fetch thread from SendRequest.h, so a leaked thread + a leaked
        // WinHTTP session per room change. Release the lock first, then update.
        if (g_initialized && g_discordReady) {
            UpdatePresence();
        }
    }

    // Privacy-aware room setter (see SetRoomFromUrl). Unlike SetCurrentRoom this ALLOWS the two
    // special values the privacy rules need: "[PRIVATE]" (mask) and "" (hide the room entirely).
    // A normal public label still gets a light guard against query-string junk.
    void SetRoomLabel(const std::string& label) {
        if (!label.empty() && label != "[PRIVATE]") {
            if (label.length() > 50 ||
                label.find('?') != std::string::npos ||
                label.find("null") != std::string::npos ||
                label.find("undefined") != std::string::npos) {
                return;
            }
        }
        {
            std::lock_guard<std::mutex> lock(g_roomMutex);
            g_currentRoom = label;
        }
        if (g_initialized && g_discordReady) {
            UpdatePresence();
        }
    }

    void UpdatePresence() {
        if (!g_initialized || !g_discordReady) {
            return;
        }

        std::lock_guard<std::mutex> roomLock(g_roomMutex);
        std::lock_guard<std::mutex> presenceLock(g_presenceMutex);

        // Details depend on the presence mode: login screen (not in a room), hidden (in a room but
        // withheld by "Appear Online To"), or in a room.
        int mode = g_presenceMode.load();
        if (mode == 0) {            // login / loading screen - online, not in a room yet
            g_cachedDetails = "In Login Screen";
            g_cachedState = "";
        }
        else if (mode == 1) {       // in a room but hidden by the appear-online setting
            g_cachedDetails = "Playing Campus";
            g_cachedState = "";
        }
        else {                      // in a room
            g_cachedDetails = "In " + g_currentRoom;
            g_cachedState = "Playing Campus";
        }

        // Discord's "elapsed" timer is driven by startTimestamp. This used to be set to
        // time(nullptr) on EVERY push, and UpdatePresence runs on the ~10s refresh timer (and on
        // every room fetch), so the clock reset every ~10 seconds and never counted up. Keep the
        // start FIXED for as long as the shown presence (mode + room) is unchanged, and only reset
        // it when the room actually changes - so it counts the whole time you're in a room, then
        // restarts on the next room. Guarded by g_presenceMutex, which is held here.
        static time_t s_presenceStart = 0;
        static std::string s_presenceKey;
        std::string presenceKey = std::to_string(mode) + "|" + g_currentRoom;
        if (s_presenceStart == 0 || presenceKey != s_presenceKey) {
            s_presenceStart = time(nullptr);
            s_presenceKey = presenceKey;
        }

        DiscordRichPresence discordPresence{};
        memset(&discordPresence, 0, sizeof(discordPresence));
        discordPresence.details = g_cachedDetails.c_str();
        discordPresence.state = g_cachedState.c_str();
        discordPresence.startTimestamp = s_presenceStart;
        discordPresence.largeImageKey = "campus";
        discordPresence.largeImageText = "Campus";
        discordPresence.smallImageText = "Campus";
        discordPresence.partyId = "ae488379-351d-4a4f-ad32-2b9b01c91657";
        discordPresence.joinSecret = "MTI4NzM0OjFpMmhuZToxMjMxMjM= ";

        Discord_UpdatePresence(&discordPresence);
    }

    void ParseRoomFromResponse(const std::string& responseBody) {
        // Try to parse room data directly from response
        try {
            auto jsonData = json::parse(responseBody);
            if (jsonData.contains("RoomId") && jsonData["RoomId"].is_number()) {
                std::string roomId = std::to_string(jsonData["RoomId"].get<int>());
                std::string roomName = jsonData.contains("Name") ? jsonData["Name"].get<std::string>() : "";
                if (!roomName.empty()) {
                    SetCurrentRoom("^" + roomName);
                }
                else {
                    SetCurrentRoom("Room " + roomId);
                }
                return;
            }
        }
        catch (...) {
            // Not JSON or missing fields, continue with regex parsing
        }

        // Fallback to regex parsing
        std::smatch match;

        std::regex roomNamePattern("\"roomName\"\\s*:\\s*\"([^\"]+)\"");
        if (std::regex_search(responseBody, match, roomNamePattern) && match.size() > 1) {
            std::string room = match[1].str();
            if (!room.empty()) {
                SetCurrentRoom("^" + room);
                return;
            }
        }

        std::regex roomIdPattern("\"roomId\"\\s*:\\s*([0-9]+)");
        if (std::regex_search(responseBody, match, roomIdPattern) && match.size() > 1) {
            std::string roomId = match[1].str();
            if (!roomId.empty()) {
                SetCurrentRoom("Room " + roomId);
                return;
            }
        }

        std::regex namePattern("\"name\"\\s*:\\s*\"([^\"]+)\"");
        if (std::regex_search(responseBody, match, namePattern) && match.size() > 1) {
            std::string name = match[1].str();
            if (!name.empty() && name.find("visited") == std::string::npos) {
                SetCurrentRoom("^" + name);
                return;
            }
        }
    }

    void CallbackThread() {
        int tick = 0;
        while (g_running) {
            if (g_initialized) {
                Discord_RunCallbacks();
            }
            // Every ~10s re-check the server: keeps the room current AND continuously honors the
            // live "Appear Online To" setting (hide the room the moment it's set to not-All).
            if (++tick >= 100) {
                tick = 0;
                RefreshPresence();
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
    }

    void Start() {
        if (g_initialized) {
            return;
        }

        // Use static handlers that persist
        static DiscordEventHandlers handlers{};
        static bool handlersSet = false;

        if (!handlersSet) {
            handlers.ready = [](const DiscordUser* user) {
                g_discordReady = true;
                UpdatePresence();
                };
            handlers.disconnected = [](int errorCode, const char* message) {
                g_discordReady = false;
                };
            handlers.errored = [](int errorCode, const char* message) {
                g_discordReady = false;
                };
            handlersSet = true;
        }

        Discord_Initialize(CLIENT_ID, &handlers, 1, nullptr);
        g_initialized = true;

        g_running = true;
        g_thread = std::thread(CallbackThread);

        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        Discord_RunCallbacks();
    }

    void Shutdown() {
        g_running = false;
        if (g_thread.joinable()) {
            g_thread.join();
        }

        Discord_Shutdown();
        g_initialized = false;
        g_discordReady = false;
    }

} // namespace DiscordRPC