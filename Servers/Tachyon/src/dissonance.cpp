#include "dissonance.h"

namespace dissonance {

    const char* type_name(MsgType t) {
        switch (t) {
            case MsgType::RemoveClient: return "RemoveClient";
            case MsgType::ClientState: return "ClientState";
            case MsgType::VoiceData: return "VoiceData";
            case MsgType::TextData: return "TextData";
            case MsgType::HandshakeRequest: return "HandshakeRequest";
            case MsgType::HandshakeResponse: return "HandshakeResponse";
            case MsgType::ErrorWrongSession: return "ErrorWrongSession";
            case MsgType::ServerRelayReliable: return "ServerRelayReliable";
            case MsgType::ServerRelayUnreliable: return "ServerRelayUnreliable";
            case MsgType::DeltaChannelState: return "DeltaChannelState";
            case MsgType::HandshakeP2P: return "HandshakeP2P";
            default: return "<unknown>";
        }
    }

    static void write_count(std::vector<uint8_t>& b, int count, int width) {
        if (width == 1) b.push_back((uint8_t) count);
        else put_be_u16(b, (uint16_t) count);
    }

    std::vector<uint8_t> build_handshake_response(uint32_t session, uint16_t client_id, int count_width) {
        std::vector<uint8_t> p;
        put_be_u16(p, MAGIC);
        p.push_back((uint8_t) MsgType::HandshakeResponse);
        put_be_u32(p, session);
        put_be_u16(p, client_id);
        write_count(p, 0, count_width);   // client list (others): empty
        write_count(p, 0, count_width);   // room list: empty
        write_count(p, 0, count_width);   // listeners list: empty
        return p;
    }

    std::vector<uint8_t> build_remove_client(uint32_t session, uint16_t client_id) {
        std::vector<uint8_t> p;
        put_be_u16(p, MAGIC);
        p.push_back((uint8_t) MsgType::RemoveClient);
        put_be_u32(p, session);
        put_be_u16(p, client_id);
        return p;
    }

} // namespace dissonance
