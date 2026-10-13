#pragma once
//
// Resolves a Rec Room AccountId to its room instance id via recflare's matchmaking endpoint
// (GET <base_url>?id=<accountId> -> a bare numeric instance id). The lookup runs once per handshake
// on the connection path (synchronous, short timeout). It is deliberately NOT cached: a player's
// room changes when they switch rooms, and a reconnect after a switch must see the new room.
//
#include <cstdint>
#include <optional>
#include <string>

class room_lookup {
public:
    // base_url e.g. "https://match.recflare.net/tachyon"; timeout in milliseconds.
    room_lookup(std::string base_url, long timeout_ms);

    // Returns the numeric instance id for an account, or nullopt on failure / non-numeric input.
    // A returned 0 is passed through verbatim; the server decides what 0 means (isolate).
    std::optional<uint64_t> resolve(const std::string& account_id);

private:
    std::string m_base_url;
    long m_timeout_ms;
};
