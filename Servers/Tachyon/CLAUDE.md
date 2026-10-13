# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Tachyon** is a clean-room, protocol-compatible **voice server** for the Rec Room client (build
20250718.01) — in-room proximity voice over Dissonance → NGO → a RecRoom encrypt layer → LiteNetLib.
It is its own protocol (not Photon; unrelated to the sibling LuxonServer beyond shared clean-room
posture). Two real clients can hear each other through it, plaintext, with no client patch.

Compatibility logic is derived only from public MIT sources (NGO, the community LiteNetLib transport,
LiteNetLib) and black-box traffic observation. Client static analysis is done by a **separate agent**
on a dumped image; questions to it live in `docs/client-agent-request-*.md`. `docs/ngo-litenetlib-wire.md`
and `docs/tachyon-findings.md` are the authoritative wire spec, tagged `[PROVEN]`/`[UNVERIFIED]` — read
them before touching protocol code, and keep them in sync when the wire understanding changes.

## The implementation

One server: `src/` → **`tachyon`** (C++23). Transport is the `vendor/LiteNetLibPP` submodule;
hashing is the official single-header xxHash in `third_party/`; the recflare room lookup uses
libcurl. (A C#/.NET reference implementation existed earlier and proved the protocol; it has been
removed — the wire knowledge now lives in `docs/`. Old commits still have it if ever needed.)

## Build & run

```sh
git submodule update --init --recursive        # LiteNetLibPP is a pinned submodule
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
./build/tachyon --port 7777                     # fork + dissonance + 0x11 layer + rooms are defaults
```
Configure applies `patches/0001-tachyon-packet-layer-hook.patch` to the submodule working tree
idempotently, so the submodule shows as "dirty" afterward — **that is expected**, not a mistake
(reset with `git -C vendor/LiteNetLibPP checkout .`). Needs CMake ≥3.16, a C++23 compiler,
**`libcurl-devel`** (room lookup) and **`openssl-devel`** (blob decryption).

Settings come from a `key = value` **config file** (`--config <path>`, see `tachyon.conf.example`);
CLI flags override it. Flags: `--port` / `--no-fork` / `--no-dissonance` / `--no-encrypt` /
`--no-rooms` / `--room-url <u>` / `--room-timeout <ms>` / `--private-key <xml-file>` /
`--ds-count-width N`; logging `-v`/`-q`/`--log-level error|warn|info|debug` (default info;
per-message/relay spam is debug-only). Runs as a service: logs to stdout, exits 0 on SIGINT/SIGTERM.

### Testing
`./build/tachyon --selftest [batch.hex]` is the offline test (no network, no curl): it checks
bit-packing round-trips, the message-id hashes, batch build↔parse with hash verification (fork +
stock), and the Dissonance primitives; an optional hex file is fed through the batch parser. Exit 0
= pass, 1 = failure (CI-friendly). Beyond that, validation is against the real client — `tachyon -v`
prints per-relay/id diagnostics, and `tools/decode_lnl.py <capture>` decodes LiteNetLib hex dumps.

## Architecture — the protocol stack (bottom-up)

Each layer wraps the one below.

1. **LiteNetLib 1.x transport, ProtocolId 13.** Reliable/unreliable UDP via LiteNetLibPP. Connect
   handshake, ping/pong, MTU, fragmentation are the library's job.
2. **RecRoom encrypt layer** — a trailing-**marker** byte, below LiteNetLib framing. Plaintext appends
   `0x11` outbound and strips it inbound; `0x2A` (encrypted) is dropped (Burst cipher unidentified).
   The whole handshake runs key-null, so plaintext is sufficient. LiteNetLibPP has no packet-layer
   hook, so `patches/` adds generic `outbound_layer`/`inbound_layer` `std::function`s to `net_manager`
   at the socket boundary; `main.cpp` installs the marker through them.
3. **NGO forked batch.** Rec Room forks NGO's batch header to **24 bytes** (BatchCount@16); `BatchHash`
   is **XXHash64 over bytes [24:end]**. Messages are bit-packed `type` + `size` + body. The session's
   **message-id space is defined by the version list the server echoes in ConnectionApproved**
   (`MESSAGE_ORDER`) — the client rebuilds its mapping from that, so the echo must be verbatim.
4. **Tachyon handshake.** JSON in the NGO ConnectionData: `AI` (AccountId, plaintext) plus
   `AT`/`VB`/`CKA`/`CIA`/`CPK`, all RSA-encrypted to a 1024-bit public key hardcoded in the client
   (`VB`/`CKA`/`CIA` are single 128-byte blocks; `AT` is larger — hybrid/chunked). With the original
   Rec Room key these are opaque, but with a **patched client** (its embedded modulus swapped for our
   own keypair) the server decrypts them using the matching private key (`src/crypto.cpp`,
   `RSAKeyValue` XML via `--private-key`/config; `rsa_key::decrypt` tries PKCS#1 then OAEP). The
   server's ConnectionApproved payload must contain `{"DR":"Carrot"}`; `KT` (a session key) is
   currently **omitted**, leaving the client key-null → plaintext voice (`0x11`). Minting `KT` would
   move the client to the encrypted path (`0x2A`), which needs the transport cipher confirmed first.
5. **Dissonance session** (Placeholder Software stock v8.x). Rides in an NGO NamedMessage:
   `[u16 NamedMessageType LE][i32 len LE][Dissonance packet]`. **Dissonance is BIG-ENDIAN** — a
   deliberate exception; everything else on the wire is little-endian. Magic `0x8bc7`. The server
   answers `HandshakeRequest`→`HandshakeResponse` (assigns a client id), relays `ClientState` for
   roster discovery, and handles voice via **ServerRelay unwrap**.

### Relay model (the core design)

Star topology: the server is a **dumb relay** and clients decide who hears them. A speaking client
sends `ServerRelayUnreliable` = `session + recipientCount + recipientIds(u16 BE) + innerLen + inner
VoiceData`. The server **unwraps** it and delivers the inner packet to each named Dissonance client id
— it must NOT forward the wrapper. This is the whole reason voice works.

**Room enforcement is server-side, keyed on recflare (implemented).** There is no room id anywhere
on the wire — the ConnectionData JSON is only `AI`/`AT`/`VB`/`CKA`/`CIA`/`CPK` (`AI` = AccountId; the
rest are RSA-encrypted auth/crypto blobs, opaque). So the server resolves the room from the AccountId:
at handshake, `GET <room-url>?id=<AI>` → a bare numeric `roomInstanceId` (`src/room_lookup.cpp`,
libcurl; **not cached** — a player's room changes when they switch rooms, and a reconnect must see
the new one). Each room gets its **own Dissonance session id** (`m_session_by_room`), and the relay
scopes `ClientState` roster, `ServerRelay` delivery, `RemoveClient`, and broadcast to the same
`client_state.room`. A lookup failure or a `0` result **isolates** the client in a unique room of its
own (fail-safe: it hears no one). `--no-rooms` reverts to one shared session for the whole port.

Key proven fact behind this: the client routes voice by **the ds id the server assigns in
HandshakeResponse** (a relay showed `account=205 senderId=1`), and it honors both that ds client id
and the u32 session we hand it — which is why per-room sessions work as an enforcement layer (a
cross-room packet is also dropped client-side as wrong-session). Matchmaking separately points a
room's clients at a voice server via `VoiceServerId`.

### Reconnect-stable ids — an invariant to preserve

Rec Room clients drop and reconnect frequently. Two mechanisms keep rosters consistent; **do not
regress them**:
- Dissonance client ids are **stable per player**, keyed on the Tachyon AccountId (`AI`), so a
  reconnect reuses its id (`m_ds_id_by_account`) instead of orphaning a ghost.
- An **owner-per-id** map (`m_ds_owner`): the newest handshake owns an id, so a superseded old peer's
  delayed disconnect does not broadcast `RemoveClient` for an id now held by the reconnected client,
  and relay delivers only to the current owner.
- `RemoveClient` is Dissonance message type **10** (2026-09-27, read from the 2025 client's receive
  switch, which accepts 1..11 only). It was 0 here until then: every "player left" was ignored as an
  unknown header, rosters never shrank, and a returning player's speech was discarded against the
  stale entry's timing ("missed target activation time ... discarding") until the listener
  rejoined. Symptom in Player.log: endless "Failed to find player for voice playback: N".
  Deployed by `UploadUpdate.bat` via `Deploy/update-tachyon.sh` (rebuilds only when `src/` changed).

Untrusted-input paths (batch loop, version list) are bounds-checked (`wire::try_get_*`); keep new
parsing of client data guarded — the server is exposed on UDP.

### Not yet implemented / known caveats

- The room lookup is **synchronous** on the handshake path (blocks up to `--room-timeout`); make it
  async if join storms matter.
- No object replication / NetworkVariable / scene management (the client build omits those types).
