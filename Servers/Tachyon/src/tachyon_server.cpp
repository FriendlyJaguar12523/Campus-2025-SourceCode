#include "tachyon_server.h"
#include "wire.h"
#include "dissonance.h"
#include "log.h"

#include <lnl/net_data_reader.h>

#include <cctype>
#include <cstdio>
#include <algorithm>
#include <string>

using lnl::DELIVERY_METHOD;

// ------------------------------------------------------------------ helpers

static std::string to_hex(const std::vector<uint8_t>& b) {
    static const char* h = "0123456789abcdef";
    std::string s;
    s.reserve(b.size() * 2);
    for (uint8_t c : b) { s.push_back(h[c >> 4]); s.push_back(h[c & 0xF]); }
    return s;
}

// Printable-ASCII rendering with non-printables shown as '.', for eyeballing decrypted blobs.
static std::string to_ascii(const std::vector<uint8_t>& b) {
    std::string s;
    s.reserve(b.size());
    for (uint8_t c : b) s.push_back((c >= 0x20 && c < 0x7f) ? (char) c : '.');
    return s;
}

static std::string extract_json_string(const std::string& json, const char* field) {
    std::string key = std::string("\"") + field + "\":\"";
    auto i = json.find(key);
    if (i == std::string::npos) return {};
    i += key.size();
    auto j = json.find('"', i);
    if (j == std::string::npos) return {};
    return json.substr(i, j - i);
}

tachyon_server::tachyon_server() {
    for (size_t i = 0; i < MESSAGE_ORDER.size(); i++)
        m_hashes[i] = wire::message_hash(MESSAGE_ORDER[i]);
    m_start = std::chrono::steady_clock::now();
}

int tachyon_server::index_of(const char* full_name) const {
    for (size_t i = 0; i < MESSAGE_ORDER.size(); i++)
        if (std::string(MESSAGE_ORDER[i]) == full_name) return (int) i;
    return -1;
}

int tachyon_server::local_version(const char* full_name) const {
    return std::string(full_name) == "Unity.Netcode.ConnectionApprovedMessage" ? 1 : 0;
}

int tachyon_server::current_tick() const {
    auto secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - m_start).count();
    return (int) (secs * TICK_RATE);
}

tachyon_server::client_state* tachyon_server::find(lnl::net_peer* p) {
    auto it = m_clients.find(p);
    return it == m_clients.end() ? nullptr : &it->second;
}

uint32_t tachyon_server::session_for_room(uint64_t room) {
    auto it = m_session_by_room.find(room);
    if (it != m_session_by_room.end()) return it->second;
    uint32_t s = (uint32_t) (std::chrono::steady_clock::now().time_since_epoch().count() & 0x7fffffff);
    if (s == 0) s = 1;
    m_session_by_room[room] = s;
    return s;
}

// Resolve this client's room (via room_resolver) and pick the room's session. A failed lookup or a
// 0 result isolates the client in a unique room of its own, so it can neither hear nor be heard -
// fail-safe. With no resolver configured, everyone shares room 1 (the original single-room mode).
void tachyon_server::assign_room(client_state& st) {
    if (st.room != 0) return;   // already resolved
    if (room_resolver && !st.account_id.empty()) {
        auto r = room_resolver(st.account_id);
        if (r && *r != 0) {
            st.room = *r;
            LOGI("[room] client %llu account=%s -> room %llu\n",
                 (unsigned long long) st.client_id, st.account_id.c_str(), (unsigned long long) st.room);
        } else {
            st.room = ++m_next_isolated_room;
            LOGW("[room] client %llu account=%s has no room (lookup %s) - isolating\n",
                 (unsigned long long) st.client_id, st.account_id.c_str(), r ? "returned 0" : "failed");
        }
    } else {
        st.room = 1;   // single shared room
    }
    st.session = session_for_room(st.room);
}

// ------------------------------------------------------------------ listener

void tachyon_server::on_connection_request(std::shared_ptr<lnl::net_connection_request>& request) {
    // The community transport accepts unconditionally with an empty connect key.
    LOGD("[lnl ] connect request -> accept\n");
    request->accept();
}

void tachyon_server::on_peer_connected(std::shared_ptr<lnl::net_peer>& peer) {
    client_state st;
    st.peer = peer;
    st.client_id = m_next_client_id++;
    LOGI("[lnl ] peer connected -> clientId %llu\n", (unsigned long long) st.client_id);
    m_clients[peer.get()] = std::move(st);
}

void tachyon_server::on_peer_disconnected(std::shared_ptr<lnl::net_peer>& peer, lnl::disconnect_info&) {
    if (auto* st = find(peer.get())) {
        if (dissonance) send_remove_client(*st);
        LOGI("[lnl ] peer disconnected (clientId %llu)\n", (unsigned long long) st->client_id);
        m_clients.erase(peer.get());
    }
}

void tachyon_server::on_network_receive(std::shared_ptr<lnl::net_peer>& peer, lnl::net_data_reader& reader,
                                        uint8_t, DELIVERY_METHOD) {
    if (auto* st = find(peer.get()))
        (void) handle_batch(*st, reader.data() + reader.position(), reader.remaining());
}

// ------------------------------------------------------------------ receive

bool tachyon_server::handle_batch(client_state& st, const uint8_t* data, size_t len) {
    size_t min_header = fork ? 24 : (size_t) wire::BATCH_HEADER_SIZE;
    if (len < min_header) {
        LOGW("[warn] runt packet (%zu bytes, need %zu), ignoring\n", len, min_header);
        return false;
    }

    size_t p = 0;
    uint16_t magic;
    uint32_t count;
    int32_t size;
    uint64_t hash;
    size_t header_size;

    if (fork) {
        // 24-byte header: Magic u16 @0, pad @2, BatchSize u32 @4, BatchHash u64 @8,
        // BatchCount u16 @16, pad @18..23. Hash covers [24:end].
        magic = wire::get_u16(data, p);
        wire::get_u16(data, p);              // padding [2:4]
        size = wire::get_i32(data, p);       // [4:8]
        hash = wire::get_u64(data, p);       // [8:16]
        count = wire::get_u16(data, p);      // BatchCount [16:18]
        p += 6;                              // padding [18:24]
        header_size = 24;
    } else {
        magic = wire::get_u16(data, p);
        count = wire::get_u16(data, p);
        size = wire::get_i32(data, p);
        hash = wire::get_u64(data, p);
        header_size = wire::BATCH_HEADER_SIZE;
    }

    if (magic != wire::BATCH_MAGIC) {
        LOGW("[warn] bad batch magic 0x%04x (want 0x%04x) - wrong framing or truncated\n",
               magic, wire::BATCH_MAGIC);
        return false;
    }

    uint64_t computed = wire::hash64(data, header_size, len - header_size);
    if (computed != hash) {
        // A mismatch means corrupt or hostile framing - drop, do not parse further.
        LOGW("[warn] batch hash mismatch: got 0x%016llx computed 0x%016llx (len %zu, declared %d) - dropping\n",
               (unsigned long long) hash, (unsigned long long) computed, len, size);
        return false;
    }
    LOGD("[recv] batch count=%u size=%d len=%zu hashOk=1\n", count, size, len);

    for (uint32_t i = 0; i < count && p < len; i++) {
        uint32_t type, msg_size;
        if (!wire::try_get_packed_u32(data, p, len, type) ||
            !wire::try_get_packed_u32(data, p, len, msg_size)) {
            LOGW("[warn] truncated message header in batch (message %u)\n", i);
            return false;
        }
        if (msg_size > len - p) {
            LOGW("[warn] message %u claims %u bytes, only %zu left\n", i, msg_size, len - p);
            return false;
        }
        handle_message(st, type, data, p, msg_size);
        p += msg_size;
    }
    return true;
}

void tachyon_server::handle_message(client_state& st, uint32_t type, const uint8_t* data,
                                    size_t off, size_t len) {
    // Before approval the client uses its own local ordering, which always puts ConnectionApproved
    // at 0 and ConnectionRequest at 1 regardless of the trimmed message set.
    std::string name;
    if (st.approved)
        name = type < MESSAGE_ORDER.size() ? MESSAGE_ORDER[type] : "<unknown>";
    else if (type == 0) name = "Unity.Netcode.ConnectionApprovedMessage";
    else if (type == 1) name = "Unity.Netcode.ConnectionRequestMessage";
    else name = "<pre-approval unknown>";

    LOGD("[recv]   [%u] %s (%zu bytes)\n", type, name.c_str(), len);

    if (name == "Unity.Netcode.ConnectionRequestMessage")
        handle_connection_request(st, data, off, len);
    else if (name == "Unity.Netcode.NamedMessage")
        handle_named_message(st, data, off, len);
    else if (name == "Unity.Netcode.ServerLogMessage")
        LOGD("[clog] client %llu sent a ServerLogMessage (%zu bytes)\n",
               (unsigned long long) st.client_id, len);
    else
        LOGD("[recv] %s from client %llu (%zu bytes)\n", name.c_str(),
             (unsigned long long) st.client_id, len);
}

void tachyon_server::handle_connection_request(client_state& st, const uint8_t* data,
                                               size_t off, size_t len) {
    size_t p = off;
    size_t end = off + len;
    int n;
    if (!wire::try_get_packed_i32(data, p, end, n) || n < 0 || (size_t) n > end - p) {
        // Each entry is >=5 bytes, so a count exceeding the remaining bytes is malformed/hostile.
        LOGW("[warn] ConnectionRequest from client %llu: bad version count\n",
               (unsigned long long) st.client_id);
        return;
    }
    LOGD("[hand] ConnectionRequest from client %llu: %d message versions advertised\n",
           (unsigned long long) st.client_id, n);

    st.client_version_list.clear();
    st.remote_versions.clear();
    for (int i = 0; i < n; i++) {
        uint32_t h;
        int v;
        if (!wire::try_get_u32(data, p, end, h) || !wire::try_get_packed_i32(data, p, end, v)) {
            LOGW("[warn] truncated version entry %d from client %llu\n", i, (unsigned long long) st.client_id);
            return;
        }
        st.remote_versions[h] = v;
        st.client_version_list.emplace_back(h, v);
    }

    // ConnectionApproval is off in the trimmed build, so only ConfigHash follows.
    if (p + 8 <= off + len) wire::get_u64(data, p);   // ConfigHash (skip)

    // Tachyon connection data: a length-prefixed byte[] holding the handshake JSON.
    if (p + 4 <= off + len) {
        int conn_len = wire::get_i32(data, p);
        if (conn_len > 0 && p + (size_t) conn_len <= off + len) {
            std::string json((const char*) (data + p), (size_t) conn_len);
            st.account_id = extract_json_string(json, "AI");
            std::string cpk = extract_json_string(json, "CPK");
            LOGD("[hand] Tachyon ConnectionData: %d bytes JSON, CPK=%zu b64chars, AccountId=%s\n",
                   conn_len, cpk.size(), st.account_id.empty() ? "?" : st.account_id.c_str());
            // Full value of each key, debug-only (these include live auth tokens - don't share).
            // For the RSA-encrypted blobs, decrypt with the private key when one is loaded.
            int key_bytes = (priv_key && priv_key->valid()) ? priv_key->bits() / 8 : 0;
            for (const char* key : {"AI", "AT", "VB", "CKA", "CIA", "CPK"}) {
                std::string v = extract_json_string(json, key);
                LOGD("[hand]   %-3s len=%-4zu %s\n", key, v.size(), v.c_str());
                if (key_bytes == 0 || std::string(key) == "AI") continue;

                auto raw = base64_decode(v);
                if ((int) raw.size() == key_bytes) {
                    // Exactly one RSA block -> decrypt directly.
                    auto dec = priv_key->decrypt(raw.data(), raw.size());
                    if (dec) LOGD("[hand]       %s -> %zu bytes: %s | \"%s\"\n", key, dec->size(),
                                  to_hex(*dec).c_str(), to_ascii(*dec).c_str());
                    else LOGD("[hand]       %s decrypt FAILED (wrong key or padding)\n", key);
                } else {
                    LOGD("[hand]       %s = %zu bytes, not one %d-bit RSA block (hybrid/chunked)\n",
                         key, raw.size(), priv_key->bits());
                }
            }
        }
    }

    assign_room(st);   // AccountId -> room + session (recflare), before the Dissonance handshake
    send_connection_approved(st);
    st.approved = true;
    LOGI("[hand] sent ConnectionApproved to client %llu\n", (unsigned long long) st.client_id);
}

// NamedMessage body = [u16 NamedMessageType LE][i32 length LE][Dissonance packet].
void tachyon_server::handle_named_message(client_state& st, const uint8_t* data, size_t off, size_t len) {
    if (len < 6) { LOGW("[warn] runt NamedMessage (%zu bytes)\n", len); return; }
    size_t p = off;
    uint16_t named_type = wire::get_u16(data, p);
    int diss_len = wire::get_i32(data, p);
    if (diss_len < 0 || p + (size_t) diss_len > off + len) {
        LOGW("[warn] NamedMessage nType=%u bad length %d\n", named_type, diss_len);
        return;
    }
    std::vector<uint8_t> diss(data + p, data + p + diss_len);

    if (named_type == dissonance::NAMED_TO_SERVER)
        handle_dissonance_to_server(st, diss);
    else
        LOGW("[warn] NamedMessage from client %llu with nType=%u (expected 1=ToServer)\n",
               (unsigned long long) st.client_id, named_type);
}

void tachyon_server::handle_dissonance_to_server(client_state& st, const std::vector<uint8_t>& diss) {
    if (diss.size() < 3) return;
    size_t p = 0;
    uint16_t magic = dissonance::get_be_u16(diss.data(), p);
    if (magic != dissonance::MAGIC) {
        LOGW("[warn] Dissonance magic 0x%04x != 0x8bc7\n", magic);
        return;
    }
    auto type = (dissonance::MsgType) diss[p++];
    LOGD("[diss] client %llu: %s (%zu bytes)\n", (unsigned long long) st.client_id,
           dissonance::type_name(type), diss.size());

    if (!dissonance) return;

    switch (type) {
        case dissonance::MsgType::HandshakeRequest:
            send_handshake_response(st);
            break;

        case dissonance::MsgType::ClientState: {
            // Roster discovery: remember this client's state, tell everyone else about it, and
            // replay everyone else's state to this client so both directions learn peers.
            st.last_client_state = diss;
            int peers = 0, replayed = 0;
            for (auto& [ptr, other] : m_clients) {
                if (!other.approved || other.client_id == st.client_id) continue;
                if (other.room != st.room) continue;   // room-scoped roster: no cross-room discovery
                send_dissonance_to_client(other, diss, DELIVERY_METHOD::RELIABLE_ORDERED);
                if (!other.last_client_state.empty()) {
                    send_dissonance_to_client(st, other.last_client_state, DELIVERY_METHOD::RELIABLE_ORDERED);
                    replayed++;
                }
                peers++;
            }
            LOGD("[diss] ClientState from ds#%u: relayed to %d peer(s), replayed %d peer state(s) back\n",
                   st.ds_client_id, peers, replayed);
            break;
        }

        case dissonance::MsgType::ServerRelayReliable:
            handle_server_relay(st, diss, DELIVERY_METHOD::RELIABLE_ORDERED);
            break;
        case dissonance::MsgType::ServerRelayUnreliable:
            handle_server_relay(st, diss, DELIVERY_METHOD::UNRELIABLE);
            break;

        default:
            relay_dissonance(st, diss, DELIVERY_METHOD::RELIABLE_ORDERED);
            break;
    }
}

// ServerRelay = magic(2) type(1) session(4) recipientCount(1) recipientIds(u16 BE each)
//               innerLength(u16 BE) innerPacket. Deliver the inner packet to each named
//               Dissonance client id - NOT forward the wrapper.
void tachyon_server::handle_server_relay(client_state& st, const std::vector<uint8_t>& diss,
                                         DELIVERY_METHOD delivery) {
    const uint8_t* d = diss.data();
    size_t p = 7;   // skip magic(2) + type(1) + session(4)
    if (p >= diss.size()) return;
    int rcount = d[p++];
    std::vector<uint16_t> recipients;
    for (int i = 0; i < rcount && p + 2 <= diss.size(); i++)
        recipients.push_back(dissonance::get_be_u16(d, p));
    if (p + 2 > diss.size()) return;
    int inner_len = dissonance::get_be_u16(d, p);
    if (inner_len <= 0 || p + (size_t) inner_len > diss.size()) return;
    std::vector<uint8_t> inner(d + p, d + p + inner_len);

    int delivered = 0, cross_room = 0;
    for (uint16_t id : recipients) {
        // Deliver to the current owner of the id, never a superseded/stale peer sharing it.
        auto owner = m_ds_owner.find(id);
        if (owner == m_ds_owner.end()) continue;
        auto* target = find(owner->second);
        if (!target || !target->approved) continue;
        if (target->room != st.room) { cross_room++; continue; }   // enforce room isolation
        send_dissonance_to_client(*target, inner, delivery);
        delivered++;
    }
    if (cross_room)
        LOGW("[room] dropped %d cross-room recipient(s) from account=%s (room %llu)\n",
             cross_room, st.account_id.empty() ? "?" : st.account_id.c_str(), (unsigned long long) st.room);
    if (tlog::enabled(LogLevel::Debug)) {
        auto inner_type = inner.size() >= 3 ? (dissonance::MsgType) inner[2] : (dissonance::MsgType) 255;
        // Show the recipient ids and the inner VoiceData sender id (u16 BE right after
        // magic+type+session, offset 7) so they can be compared against AccountIds directly.
        std::string rlist;
        for (uint16_t r : recipients) rlist += (rlist.empty() ? "" : ",") + std::to_string(r);
        int inner_sender = inner.size() >= 9 ? ((inner[7] << 8) | inner[8]) : -1;
        LOGD("[relay] from ds#%u (account=%s) recipients=[%s] inner=%s senderId=%d innerLen=%d delivered=%d/%zu\n",
               st.ds_client_id, st.account_id.empty() ? "?" : st.account_id.c_str(),
               rlist.c_str(), dissonance::type_name(inner_type), inner_sender, inner_len,
               delivered, recipients.size());
    }
}

// ------------------------------------------------------------------ send

void tachyon_server::send_remove_client(client_state& leaving) {
    if (leaving.ds_client_id == 0 || leaving.session == 0) return;
    // Only the current owner's disconnect removes the id. A superseded ghost (reconnect-before-
    // timeout, its id reused by a newer peer) stays silent.
    auto owner = m_ds_owner.find(leaving.ds_client_id);
    if (owner == m_ds_owner.end() || owner->second != leaving.peer.get()) return;
    m_ds_owner.erase(owner);
    auto pkt = dissonance::build_remove_client(leaving.session, leaving.ds_client_id);
    for (auto& [ptr, other] : m_clients) {
        if (other.approved && other.client_id != leaving.client_id && other.room == leaving.room)
            send_dissonance_to_client(other, pkt, DELIVERY_METHOD::RELIABLE_ORDERED);
    }
}

void tachyon_server::send_handshake_response(client_state& st) {
    if (st.session == 0) assign_room(st);   // ensure room + per-room session are resolved
    if (st.ds_client_id == 0) {
        // Reuse this player's id across reconnects so a dropped-and-rejoined client does not leave a
        // ghost id in the other clients' rosters (the churn that breaks addressing).
        if (!st.account_id.empty()) {
            auto it = m_ds_id_by_account.find(st.account_id);
            if (it != m_ds_id_by_account.end()) st.ds_client_id = it->second;
        }
        if (st.ds_client_id == 0) {
            st.ds_client_id = m_next_ds_client_id++;
            if (!st.account_id.empty()) m_ds_id_by_account[st.account_id] = st.ds_client_id;
        }
    }
    m_ds_owner[st.ds_client_id] = st.peer.get();   // newest handshake owns the id
    auto resp = dissonance::build_handshake_response(st.session, st.ds_client_id, ds_count_width);
    send_dissonance_to_client(st, resp, DELIVERY_METHOD::RELIABLE_ORDERED);
    LOGI("[diss] -> client %llu: HandshakeResponse session=%u assignedId=%u account=%s room=%llu (%zu bytes)\n",
           (unsigned long long) st.client_id, st.session, st.ds_client_id,
           st.account_id.empty() ? "?" : st.account_id.c_str(), (unsigned long long) st.room, resp.size());
}

void tachyon_server::relay_dissonance(client_state& st, const std::vector<uint8_t>& diss,
                                      DELIVERY_METHOD delivery) {
    for (auto& [ptr, other] : m_clients) {
        if (other.approved && other.client_id != st.client_id && other.room == st.room)
            send_dissonance_to_client(other, diss, delivery);
    }
}

void tachyon_server::send_dissonance_to_client(client_state& st, const std::vector<uint8_t>& diss,
                                               DELIVERY_METHOD delivery) {
    auto& body = m_named_scratch;   // reused; distinct from m_wire_scratch used by send_message
    body.clear();
    wire::put_u16(body, dissonance::NAMED_TO_CLIENT);   // u16 LE
    wire::put_i32(body, (int32_t) diss.size());         // i32 LE (WriteValueSafe prefix)
    body.insert(body.end(), diss.begin(), diss.end());
    send_message(st, "Unity.Netcode.NamedMessage", body, delivery);
}

void tachyon_server::send_connection_approved(client_state& st) {
    std::vector<uint8_t> body;

    // Version header first. Echo the client's exact list (verbatim) so SetServerMessageOrder
    // reproduces its own mapping.
    if (!st.client_version_list.empty()) {
        wire::put_packed_i32(body, (int32_t) st.client_version_list.size());
        for (auto& [h, v] : st.client_version_list) {
            wire::put_u32(body, h);
            wire::put_packed_i32(body, v);
        }
    } else {
        wire::put_packed_i32(body, (int32_t) MESSAGE_ORDER.size());
        for (size_t i = 0; i < MESSAGE_ORDER.size(); i++) {
            wire::put_u32(body, m_hashes[i]);
            wire::put_packed_i32(body, local_version(MESSAGE_ORDER[i]));
        }
    }

    wire::put_packed(body, st.client_id);              // OwnerClientId (bitpacked ulong)
    wire::put_packed_i32(body, current_tick());        // NetworkTick (bitpacked int)

    // Rec Room's ConnectionApproved carries the Tachyon approval payload as a length-prefixed
    // byte[]. Deserialize does no validation; "DR":"Carrot" is required-present. KT is omitted
    // (RSA to an opaque CPK we cannot mint), leaving the client key-null -> plaintext voice.
    static const char* json = "{\"DR\":\"Carrot\"}";
    size_t json_len = 15;   // strlen({"DR":"Carrot"})
    wire::put_i32(body, (int32_t) json_len);
    body.insert(body.end(), json, json + json_len);
    LOGD("[hand] approval payload: %s (%zu bytes; KT omitted)\n", json, json_len);

    send_message(st, "Unity.Netcode.ConnectionApprovedMessage", body, DELIVERY_METHOD::RELIABLE_ORDERED);
}

void tachyon_server::send_time_sync(client_state& st, int tick) {
    std::vector<uint8_t> body;
    wire::put_packed_i32(body, tick);
    send_message(st, "Unity.Netcode.TimeSyncMessage", body, DELIVERY_METHOD::UNRELIABLE);
}

// Fill the batch header in place (identical bytes to the old put_u16/put_i32/put_u64 sequence).
// Fork: Magic u16 @0, pad @2, BatchSize u32 @4, BatchHash u64 @8, BatchCount u16 @16, pad @18..23.
// Stock: Magic u16 @0, BatchCount u16 @2, BatchSize u32 @4, BatchHash u64 @8.
void tachyon_server::write_batch_header(uint8_t* d, size_t size, uint64_t hash) const {
    d[0] = (uint8_t) wire::BATCH_MAGIC;
    d[1] = (uint8_t) (wire::BATCH_MAGIC >> 8);
    d[2] = fork ? 0 : 1;                 // fork: pad; stock: BatchCount low byte
    d[3] = 0;
    d[4] = (uint8_t) size;   d[5] = (uint8_t) (size >> 8);
    d[6] = (uint8_t) (size >> 16); d[7] = (uint8_t) (size >> 24);
    for (int i = 0; i < 8; i++) d[8 + i] = (uint8_t) (hash >> (i * 8));
    if (fork) { d[16] = 1; d[17] = 0; } // BatchCount (stock already wrote it at [2:4])
}

void tachyon_server::build_batch(const char* full_name, const std::vector<uint8_t>& body,
                                 std::vector<uint8_t>& out) {
    uint32_t type_id = (uint32_t) index_of(full_name);
    int hdr = fork ? 24 : wire::BATCH_HEADER_SIZE;

    out.assign(hdr, 0);              // header placeholder (zeroed)
    wire::put_packed(out, type_id);
    wire::put_packed(out, (uint32_t) body.size());
    out.insert(out.end(), body.begin(), body.end());
    if (!fork)
        while (out.size() % 8 != 0) out.push_back(0);   // stock NGO pads to 8; fork does not

    uint64_t hash = wire::hash64(out.data(), hdr, out.size() - hdr);
    write_batch_header(out.data(), out.size(), hash);
}

void tachyon_server::send_message(client_state& st, const char* full_name,
                                  const std::vector<uint8_t>& body, DELIVERY_METHOD delivery) {
    build_batch(full_name, body, m_wire_scratch);   // reused buffer - no per-packet allocation
    st.peer->send(m_wire_scratch, delivery);
    LOGD("[send] %s -> client %llu (%zu bytes on wire)\n", full_name,
         (unsigned long long) st.client_id, m_wire_scratch.size());
}

void tachyon_server::pump_time_sync() {
    int tick = current_tick();
    if (tick != m_last_tick && tick % TICK_RATE == 0) {
        m_last_tick = tick;
        for (auto& [ptr, c] : m_clients)
            if (c.approved) send_time_sync(c, tick);
    }
}

// ------------------------------------------------------------------ self-test

namespace {
    int g_fails = 0;
    void check(const char* name, bool ok) {
        printf("[selftest] %-42s %s\n", name, ok ? "PASS" : "FAIL");
        if (!ok) g_fails++;
    }

    // Parse a hex-dump file (whitespace-insensitive) into bytes. Returns false if not readable.
    bool read_hex_file(const char* path, std::vector<uint8_t>& out) {
        FILE* f = fopen(path, "rb");
        if (!f) return false;
        std::string hex;
        int c;
        while ((c = fgetc(f)) != EOF)
            if (std::isxdigit(c)) hex.push_back((char) c);
        fclose(f);
        for (size_t i = 0; i + 1 < hex.size(); i += 2)
            out.push_back((uint8_t) std::stoi(hex.substr(i, 2), nullptr, 16));
        return true;
    }
}

int tachyon_server::run_selftest(const char* hexfile) {
    g_fails = 0;
    LogLevel saved_level = tlog::level;
    tlog::level = LogLevel::Error;   // silence the expected warnings from the negative tests
    printf("[selftest] Tachyon wire self-test\n");

    // 1. Bit-packing round-trips (BytePacker/ByteUnpacker), incl. the 5/9-byte escape boundaries.
    {
        bool ok = true;
        for (uint32_t v : {0u, 1u, 7u, 8u, 255u, 256u, 65535u, 0x1FFFFFFFu, 0x20000000u, 0xFFFFFFFFu}) {
            std::vector<uint8_t> b; size_t p = 0;
            wire::put_packed(b, v);
            ok &= (wire::get_packed_u32(b.data(), p) == v) && (p == b.size());
        }
        for (int32_t v : {0, 1, -1, 63, -64, 1000, -1000, 2000000000, -2000000000}) {
            std::vector<uint8_t> b; size_t p = 0;
            wire::put_packed_i32(b, v);
            ok &= (wire::get_packed_i32(b.data(), p) == v) && (p == b.size());
        }
        for (uint64_t v : {0ull, 1ull, 15ull, 16ull, 0x0FFFFFFFFFFFFFFFull, 0xFFFFFFFFFFFFFFFFull}) {
            std::vector<uint8_t> b; size_t p = 0;
            wire::put_packed(b, v);
            ok &= (wire::get_packed_u64(b.data(), p) == v) && (p == b.size());
        }
        check("bit-packing round-trip (u32/i32/u64)", ok);
    }

    // 2. Message-id hashes match the values proven against the real client.
    {
        static const std::pair<const char*, uint32_t> known[] = {
            {"Unity.Netcode.ConnectionApprovedMessage", 0xa5e29a7d},
            {"Unity.Netcode.ConnectionRequestMessage", 0xb31736b2},
            {"Unity.Netcode.NamedMessage", 0x978922a6},
            {"Unity.Netcode.TimeSyncMessage", 0xc6887f42},
        };
        bool ok = true;
        for (auto& [name, h] : known) ok &= (wire::message_hash(name) == h);
        check("message-id hashes match the client", ok);
    }

    // 3. Batch build <-> parse round-trip, both fork (24-byte) and stock (16-byte) headers. Build a
    //    TimeSync message, re-parse the header/body by hand, and confirm handle_batch accepts it;
    //    then corrupt a byte and confirm handle_batch rejects it (hash mismatch).
    bool saved_fork = fork;
    for (bool test_fork : {true, false}) {
        fork = test_fork;
        std::vector<uint8_t> body;
        wire::put_packed_i32(body, 12345);   // a TimeSync-shaped body
        std::vector<uint8_t> batch;
        build_batch("Unity.Netcode.TimeSyncMessage", body, batch);

        // Manual re-parse: header, then packed type + packed size + body.
        size_t hdr = fork ? 24 : 16, p = 0;
        uint16_t magic = wire::get_u16(batch.data(), p);
        uint64_t declared = 0;
        { size_t hp = 8; declared = wire::get_u64(batch.data(), hp); }
        uint64_t recomputed = wire::hash64(batch.data(), hdr, batch.size() - hdr);
        p = hdr;
        uint32_t type = wire::get_packed_u32(batch.data(), p);
        uint32_t sz = wire::get_packed_u32(batch.data(), p);
        bool body_ok = (sz == body.size()) &&
                       std::equal(body.begin(), body.end(), batch.begin() + p);

        bool ok = (magic == wire::BATCH_MAGIC) && (declared == recomputed) &&
                  (type == (uint32_t) index_of("Unity.Netcode.TimeSyncMessage")) && body_ok;
        check(test_fork ? "batch round-trip (fork header)" : "batch round-trip (stock header)", ok);

        client_state fake;
        fake.approved = true;   // no peer: TimeSync dispatch does not send
        check(test_fork ? "handle_batch accepts valid (fork)" : "handle_batch accepts valid (stock)",
              handle_batch(fake, batch.data(), batch.size()));

        batch[batch.size() - 1] ^= 0xFF;   // corrupt the body -> hash must fail
        check(test_fork ? "handle_batch rejects corrupt (fork)" : "handle_batch rejects corrupt (stock)",
              !handle_batch(fake, batch.data(), batch.size()));
    }
    fork = saved_fork;

    // 4. Dissonance big-endian primitives + HandshakeResponse structure.
    {
        std::vector<uint8_t> b;
        dissonance::put_be_u16(b, 0x8bc7);
        dissonance::put_be_u32(b, 0x11223344);
        size_t p = 0;
        bool ok = (dissonance::get_be_u16(b.data(), p) == 0x8bc7) &&
                  (dissonance::get_be_u32(b.data(), p) == 0x11223344) && (p == b.size());
        auto resp = dissonance::build_handshake_response(0xdeadbeef, 42, 2);
        ok &= (resp.size() >= 9) && (resp[0] == 0x8b) && (resp[1] == 0xc7) &&
              (resp[2] == (uint8_t) dissonance::MsgType::HandshakeResponse);
        check("Dissonance BE primitives + HandshakeResponse", ok);
    }

    // 5. Optional: feed a captured NGO batch through the parser.
    if (hexfile) {
        std::vector<uint8_t> cap;
        if (!read_hex_file(hexfile, cap)) {
            printf("[selftest] could not read hexfile '%s'\n", hexfile);
            g_fails++;
        } else {
            client_state fake;
            fake.approved = true;
            printf("[selftest] parsing captured %zu-byte batch from %s\n", cap.size(), hexfile);
            check("captured batch parses (hash ok)", handle_batch(fake, cap.data(), cap.size()));
        }
    }

    printf("[selftest] %s (%d failure%s)\n", g_fails == 0 ? "ALL PASS" : "FAILURES",
           g_fails, g_fails == 1 ? "" : "s");
    tlog::level = saved_level;
    return g_fails == 0 ? 0 : 1;
}
