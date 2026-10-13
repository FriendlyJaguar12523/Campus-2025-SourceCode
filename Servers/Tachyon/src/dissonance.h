#pragma once
//
// The Dissonance session layer (Placeholder Software, stock v8.x).
// Dissonance rides inside an NGO NamedMessage:
//
//   NamedMessage body = [u16 NamedMessageType LE][i32 length LE][Dissonance packet]
//     NamedMessageType: 1 = DissonanceToServer (client->us), 2 = DissonanceToClient (us->client)
//
//   Dissonance packet = [u16 magic 0x8bc7 BE][u8 MessageType][u32 session BE (most types)]...
//     Dissonance's PacketWriter/Reader is BIG-ENDIAN.
//
// Field encodings INSIDE HandshakeResponse/ClientState (list widths, string encoding) are
// reconstructed from the protocol docs, not byte-verified; empty lists were proven sufficient
// for the basic multi-client case. See docs/client-agent-request-5.md.
//
#include <cstdint>
#include <cstddef>
#include <vector>

namespace dissonance {

    inline constexpr uint16_t MAGIC = 0x8bc7;
    inline constexpr uint16_t NAMED_TO_SERVER = 1;
    inline constexpr uint16_t NAMED_TO_CLIENT = 2;

    // Numbered as the Rec Room 2025 client (20250718.01) reads them. Its receive switch takes 1..11
    // only - anything else is "Ignoring a packet with an unknown header". RemoveClient used to be 0
    // here, so no client ever removed a departed player: the stale entry kept its old speech timing
    // and a returning player's voice was discarded ("missed target activation time ... discarding")
    // until the listener rejoined (2026-09-27). Case 10 is CheckSessionId + one roster call - the
    // same shape as ClientState - and 11 is the peer-to-peer handshake.
    enum class MsgType : uint8_t {
        ClientState = 1, VoiceData = 2, TextData = 3,
        HandshakeRequest = 4, HandshakeResponse = 5, ErrorWrongSession = 6,
        ServerRelayReliable = 7, ServerRelayUnreliable = 8, DeltaChannelState = 9,
        RemoveClient = 10, HandshakeP2P = 11
    };

    const char* type_name(MsgType t);

    // ---- big-endian primitives (Dissonance PacketWriter/Reader) ----
    inline void put_be_u16(std::vector<uint8_t>& b, uint16_t v) {
        b.push_back((uint8_t) (v >> 8));
        b.push_back((uint8_t) v);
    }
    inline void put_be_u32(std::vector<uint8_t>& b, uint32_t v) {
        b.push_back((uint8_t) (v >> 24));
        b.push_back((uint8_t) (v >> 16));
        b.push_back((uint8_t) (v >> 8));
        b.push_back((uint8_t) v);
    }
    inline uint16_t get_be_u16(const uint8_t* d, size_t& p) {
        uint16_t v = (uint16_t) ((d[p] << 8) | d[p + 1]);
        p += 2;
        return v;
    }
    inline uint32_t get_be_u32(const uint8_t* d, size_t& p) {
        uint32_t v = ((uint32_t) d[p] << 24) | ((uint32_t) d[p + 1] << 16) |
                     ((uint32_t) d[p + 2] << 8) | (uint32_t) d[p + 3];
        p += 4;
        return v;
    }

    // Build a HandshakeResponse for a client joining an (initially empty) session: session id,
    // client id, then three empty lists (clients, rooms, listeners). count_width: 1=byte else u16 BE.
    std::vector<uint8_t> build_handshake_response(uint32_t session, uint16_t client_id, int count_width);

    // Build a RemoveClient broadcast for a departing client.
    std::vector<uint8_t> build_remove_client(uint32_t session, uint16_t client_id);

} // namespace dissonance
