#pragma once
#include <cstdint>
#include <string>

// Linking the Campus account to the player's Steam account.
//
// The server only links a Steam id Steam itself vouched for: the launcher asks Steam for an auth
// session ticket - through the GAME's own steam_api64.dll (Recroom_Release_Data\Plugins\x86_64),
// as app 471710, exactly like the game's login - and the server checks Steam's signature on it
// (SteamTicket.cs). A SteamID on its own proves nothing: anyone can look one up.

// The SteamID64 signed in to the Steam client on this PC (from the registry), or 0 when Steam
// isn't running / nobody is signed in. Cheap - used to decide whether a link is needed at all.
uint64_t LocalSteamId();

// Make sure the game has a usable steam_api64.dll (Recroom_Release_Data\Plugins\x86_64). It goes missing
// (antivirus, a half-finished unzip) or comes back cut short, and then both the Steam link here and the
// game's own Steam login fail asking for it. When it is missing or obviously broken the launcher writes
// back the copy it carries (the one the game zip ships). `note` says what it did ("" = nothing to do);
// false = it needed to and couldn't (game running, read-only folder).
bool EnsureSteamApi(const std::string& gameDir, std::string& note);

// Ask Steam for a ticket (hex). Loads the game's steam_api64.dll, starts the Steam API as the
// game's app, takes the ticket and shuts it down again (Steam shows "playing" for a second).
// Blocking - call it on a worker thread. false + `error` when Steam isn't available.
bool GetSteamTicket(const std::string& gameDir, std::string& hex, uint64_t& steamId, std::string& error);
