# Deploying Tachyon voice with Campus

Tachyon is a standalone C++ UDP voice server that runs **alongside** the Campus C# server on
the Linux VPS. Voice is proximity/room-scoped: Tachyon relays Dissonance voice and asks the C#
server which room each speaker is in. **No client patch is needed** (plaintext `0x11` path).

The C# server changes are already in the repo (see "What the C# side does" below) and are
**config-gated OFF by default** — until you set `TachyonVoiceEndpoint`, clients keep using the
existing Photon Voice P2P path, so nothing about voice changes until you flip it on.

---

## 1. Build Tachyon on the VPS

Needs: `git`, CMake ≥ 3.16, a C++23 compiler (gcc 13+/clang 16+), `libcurl-devel`,
`openssl-devel`. (Debian/Ubuntu: `libcurl4-openssl-dev libssl-dev`.)

```sh
sudo mkdir -p /opt/tachyon && sudo chown "$USER" /opt/tachyon
git clone <this repo> /opt/tachyon
cd /opt/tachyon
git submodule update --init --recursive        # LiteNetLibPP is a pinned submodule
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

The configure step applies `patches/0001-tachyon-packet-layer-hook.patch` to the submodule, so
`vendor/LiteNetLibPP` shows as "dirty" afterward — **that is expected**.

Smoke-test the binary offline (no network):

```sh
./build/tachyon --selftest        # exit 0 = pass
```

## 2. Config

`tachyon.conf` (repo root) is ready to use. Key line:

```
room_url = https://camprec.net/tachyon
```

Tachyon calls `GET <room_url>?id=<AccountId>` and expects a bare `roomInstanceId`. If Tachyon
and the C# server are on the same box and you know Kestrel's local port, you can change this to
`http://127.0.0.1:<port>/tachyon` to skip the public round-trip. Copy the file to `/opt/tachyon`
if you cloned elsewhere.

## 3. Firewall — open UDP 7777

Clients connect over **UDP**, not TCP. Whatever the C# server's HTTPS uses, voice needs 7777/udp
open to the internet:

```sh
sudo ufw allow 7777/udp            # or the cloud provider's security-group equivalent
```

If the VPS is behind a NAT/provider firewall, forward/allow 7777/udp to this host too.

## 4. Run as a service

```sh
sudo cp deploy/tachyon.service /etc/systemd/system/tachyon.service
# edit paths in the unit if you did not use /opt/tachyon
sudo systemctl daemon-reload
sudo systemctl enable --now tachyon
journalctl -u tachyon -f           # watch it; -v in the conf for per-relay detail
```

## 5. Flip voice over to Tachyon (C# side)

In `ConfigCampus.cs` set the endpoint the CLIENT will dial (public IP or DDNS host + Tachyon's
port — **not** 127.0.0.1, the client is remote):

```csharp
public static string TachyonVoiceEndpoint = "camprec.net:7777";
```

Rebuild + redeploy the C# server. From then on `/Matchmaking/player/connection-info` hands the
client `voiceConnectionInfo = "camprec.net:7777"` and a non-empty `voiceServerId`, so it
routes voice through Tachyon. To roll back to Photon P2P instantly, set it back to `""`.

## 6. Verify

- `journalctl -u tachyon -f` shows a handshake and `account=… senderId=…` relay lines when two
  players are in the same room and talking.
- Two clients in the **same** room hear each other; two in **different** rooms do not (room
  scoping via `/tachyon`). A lookup failure isolates a client (fail-safe: they hear no one).
- `curl "https://camprec.net/tachyon?id=<someOnlineAccountId>"` returns that player's
  numeric room instance id (or `0` if they're nowhere).

---

## What the C# side does (already committed)

| File | Change |
|------|--------|
| `Controllers/TachyonController.cs` | **New.** `GET /tachyon?id=<AccountId>` → bare `roomInstanceId` (text/plain), via `HeartbeatDB.PeekPresence`. `0` when unknown/offline (fail-safe). |
| `Controllers/MatchmakingController.cs` | `player/connection-info` now sets `voiceConnectionInfo`/`voiceServerId` from config (empty ⇒ Photon fallback). |
| `api/server/ConfigCampus.cs` | `TachyonVoiceEndpoint` (default `""`) + `TachyonVoiceServerId`. |
| `Middleware/PatchGateMiddleware.cs` | `/tachyon` added to the no-patch-header open list (server-to-server). |

## Notes / caveats

- Plaintext voice only (`0x11`); no `private_key` is set and none is needed. The encrypted
  `0x2A` path would need `KT` minting + the transport cipher confirmed first — out of scope.
- The room lookup is synchronous on Tachyon's handshake path (blocks up to `room_timeout_ms`).
  Fine for normal joins; revisit only if join storms matter.
- `voiceServerId` is just a stable client-side label — Tachyon's protocol has no room/server id
  on the wire; room scoping is entirely via the `/tachyon` lookup.
