#pragma once
#include <string>

namespace DiscordRPC {
    // True between Start() and Shutdown(). SendRequest.h consults this before
    // firing a detached WinHTTP thread so we don't spawn work into an object
    // graph that's being torn down (Shutdown -> Discord_Shutdown -> static
    // destructors would otherwise race the detached thread's std::string /
    // std::mutex use and crash the process on exit).
    bool IsRunning();
    void Start();
    void Shutdown();
    void UpdatePresence();
    void SetCurrentRoom(const std::string& roomName);
    // Set the presence room label directly, bypassing the name-heuristics: "^Name" for a public
    // room, "[PRIVATE]" to mask a private one, or "" to show no room at all (privacy / hidden).
    void SetRoomLabel(const std::string& label);
    void ParseRoomFromResponse(const std::string& responseBody);
    void SetRoomFromUrl(const std::string& url);
    // Capture the local player's account id (from the Notifications hub URL) - lets the presence
    // look up the player's own room + "Appear Online To" setting.
    void SetLocalAccountId(uint64_t id);
    // Recompute presence from the server (room + privacy + appear-online). Safe to call often.
    void RefreshPresence();
}