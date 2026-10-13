# Tachyon — voice server for an NGO/LiteNetLib client

A clean-room, protocol-compatible **voice server** (project name **Tachyon**) for the Rec Room
client's in-room proximity voice. Same clean-room posture as the sibling LuxonServer, but a
different, unrelated protocol: compatibility logic is derived from public MIT sources (NGO, the
community LiteNetLib transport, LiteNetLib itself) and from black-box observation of network
traffic. The stack, bottom-up, is Dissonance over NGO over the RecRoom encrypt layer over LiteNetLib.

Two real clients hear each other through it, in plaintext, with no client patch — and voice is
scoped to rooms server-side.

## Layout

- `src/` + `CMakeLists.txt` — **Tachyon** (C++23). Dependency-light: the LiteNetLib transport comes
  from the `vendor/LiteNetLibPP` submodule, hashing from the official single-header xxHash in
  `third_party/`, and the recflare room lookup from libcurl.
- `patches/` — `0001-tachyon-packet-layer-hook.patch`, applied to the LiteNetLibPP submodule at
  configure time (adds the datagram-layer hook the RecRoom `0x11` marker needs). See `patches/README.md`.
- `docs/ngo-litenetlib-wire.md` — the wire specification. Read this first.
- `docs/tachyon-findings.md` — client static-analysis findings (the full Tachyon stack).
- `docs/client-agent-request-*.md` — questions to the client-side static-analysis agent.
- `tools/decode_lnl.py` — decoder for hex-dump captures of LiteNetLib traffic.
- `traffic.txt` — an early capture of the client failing to connect (see spec §7).
- `vendor/litenetlib`, `vendor/ngo`, `vendor/mcc` — upstream reference clones, git-ignored and not
  built against; re-create if you need to re-check the spec:

  ```sh
  mkdir -p vendor && cd vendor
  git clone --filter=blob:none https://github.com/Unity-Technologies/com.unity.netcode.gameobjects.git ngo
  git clone --filter=blob:none https://github.com/Unity-Technologies/multiplayer-community-contributions.git mcc
  git clone --filter=blob:none https://github.com/RevenantX/LiteNetLib.git litenetlib
  ```

(A C#/.NET reference implementation lived in `reference/` earlier and proved the protocol end to end;
it has been removed now that the C++ server is the product. Old commits still have it.)

## Build & run

Needs CMake, a C++23 compiler, `libcurl-devel` (room lookup) and `openssl-devel` (blob decryption)
— e.g. `sudo dnf install libcurl-devel openssl-devel`. `vendor/LiteNetLibPP` is a submodule, so
clone with it:

```sh
git clone --recurse-submodules <repo>        # or: git submodule update --init --recursive
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
./build/tachyon --config tachyon.conf         # or all-flags; fork+dissonance+0x11+rooms are defaults
```

Settings load from a `key = value` config file (`--config`, see `tachyon.conf.example`); CLI flags
override it. The RSA private key that decrypts the client's wire blobs (`AT`/`VB`/`CKA`/`CIA`/`CPK`)
is set via `private_key` in the config or `--private-key <file>` — a `.NET RSAKeyValue` XML file.

Offline self-test (no network): `./build/tachyon --selftest [batch.hex]` — checks packing, batch
build↔parse + hashes (fork & stock), and Dissonance primitives; exit 0 = pass. An optional captured
NGO batch (hex) is fed through the parser.

Flags: `--no-fork` / `--no-dissonance` / `--no-encrypt` / `--no-rooms` / `--room-url <u>` /
`--room-timeout <ms>` / `--ds-count-width N`, plus logging: `-v`/`--verbose` (debug: per-message +
per-relay), `-q`/`--quiet` (warnings/errors only), `--log-level error|warn|info|debug`. The default
is **info** — a startup banner plus connect/disconnect/handshake lifecycle and all warnings, quiet
enough to run as a service. It logs to stdout (line-buffered) and shuts down cleanly on SIGINT/SIGTERM
(exit 0), so a minimal systemd unit is just:

```ini
[Service]
ExecStart=/path/to/tachyon --port 7777 --quiet
Restart=on-failure
```

With `--verbose`, a connection walks through, in order: LiteNetLib connect accepted →
`ConnectionRequest` parsed → `ConnectionApproved` sent → room resolved (`account=… -> room=…`) →
Dissonance `HandshakeResponse` → `ClientState` roster relays → `[relay] … delivered=1/1` when
same-room clients talk.

## Current status

Works end to end against the real client (build 20250718.01): LiteNetLib (ProtocolId 13) + RecRoom
`0x11` marker + forked NGO batch + the Tachyon `"Carrot"` approval + the Dissonance session/relay,
all plaintext. Same-room clients hear each other; cross-room is silent; room membership follows
players live as they switch rooms (resolved per handshake from recflare).

## Scope

Connection handshake, time sync, the Dissonance session handshake, voice relay (ServerRelay unwrap +
per-client fan-out), and **server-side room scoping**. Rooms come from recflare
(`GET <room-url>?id=<AccountId>` → room instance id — there is no room id on the wire); each room
gets its own Dissonance session, and the relay/roster are scoped to same-room peers, with a
lookup-failure fail-safe that isolates the client. There is no object replication, no NetworkVariable
sync and no scene management — the client build does not contain those message types.
