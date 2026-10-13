# NGO 1.x over LiteNetLib — wire notes for the voice relay

Derived entirely from public MIT sources: `Unity-Technologies/com.unity.netcode.gameobjects`
(tag `ngo/1.14.1`, cross-checked against `ngo/1.6.0`) and
`Unity-Technologies/multiplayer-community-contributions`
(`Transports/com.community.netcode.transport.litenetlib`). No compatibility logic here is
taken from a decompiled client; the client-side observations are listed separately as
open questions to be settled by black-box capture.

## 1. Transport layer

LiteNetLib, **ProtocolId 13** — the `1.x` line. This is confirmed directly from a live
capture: the client's `ConnectRequest` carries `protocolId = 13` at bytes `[1..4]`.

Note this is **not** the LiteNetLib copy vendored inside the community transport package,
which is still on the `0.9.5.x` / ProtocolId 11 line. A server built against the package's
bundled sources will have every connect dropped in `NetManager` before any callback fires.
Build against LiteNetLib 1.x.

`ConnectRequest` layout (LNL 1.x, `NetConnectRequestPacket`, header 18 bytes):

```
[0]      property (ConnectRequest = 5) | connectionNumber << 5
[1..4]   protocolId          = 13
[5..12]  connectionTime      (long)
[13..16] peerId              (int)
[17]     addrSize            (16 for IPv4, 28 for IPv6)
[18..]   target SocketAddress, then connect data
```

Community transport behaviour the server must mirror:

| aspect | value |
|---|---|
| connect payload | **3 bytes, observed `01 00 11`** — see below |
| connection requests | accepted unconditionally (`request.Accept()`) |
| clientId | `peer.Id`, `+1` on the server |
| server clientId | `0` |
| delivery mapping | Unreliable→`Unreliable`, …Sequenced→`Sequenced`, Reliable*→`ReliableOrdered` |
| channel | always 0; LNL encodes `channel*4 + deliveryMethod` in its own header |

The observed connect payload does **not** parse as LiteNetLib's `Put(string)` — an empty key
writes `00 00`, and reading `01 00` as a length prefix (`size + 1 == 1`) leaves the trailing
`11` unaccounted for. So the client is not using `Connect(address, port, key)` with a plain
string; it passes a custom 3-byte blob. Because the transport accepts connection requests
unconditionally, our server does not need to interpret it — but it must not reject on it.

## 2. Batch framing

Every datagram payload is one batch:

```
offset 0  ushort  Magic     = 0x1160
offset 2  ushort  BatchCount
offset 4  int     BatchSize    (== padded total length)
offset 8  ulong   BatchHash    (XXHash64, seed 0)
offset 16 ...     messages
          pad to a multiple of 8 bytes
```

`BatchHash` covers bytes `[16 .. BatchSize)` — i.e. **including the tail padding**.
The receiver hashes `receivedLength - 16`, so sender and receiver agree only if the
datagram arrives byte-exact.

## 3. Message framing

```
bitpacked uint  MessageType   (index into the negotiated id space)
bitpacked uint  MessageSize   (payload bytes, excluding this header)
byte[]          payload
```

### Bit-packed integers

32-bit: low 3 bits of the first byte hold the byte count `n`.
If the value needs ≥30 bits, the first byte is literal `5` followed by a raw LE `uint32`.
Otherwise `v <<= 3`, `n = usedByteCount(v)`, write `v | n` in `n` LE bytes.

64-bit: identical with 4 bits and a `9` escape; `v <<= 4`.

Signed values are zig-zag encoded first (`(v << 1) ^ (v >> 31)`).

Arrays written with `WriteValueSafe` are a **raw LE `int` length** followed by raw elements —
not bit-packed.

## 4. The id space (the important part)

Message ids are **not** a fixed table. Each side builds a local list of its message types,
sorted ordinal by `Type.FullName`, then moves `ConnectionApprovedMessage` and
`ConnectionRequestMessage` to the front (`PrioritizeMessageOrder`). Because
`…ConnectionApprovedMessage` sorts before `…ConnectionRequestMessage`:

> **Index 0 is always ConnectionApproved and index 1 always ConnectionRequest**, for any
> build, no matter which other messages were trimmed out.

That is what lets the server parse the client's very first message without knowing anything
about the client's message set.

Then `ConnectionApprovedMessage` carries the server's hash list, and the client calls
`SetServerMessageOrder` with it — **the server's ordering becomes the id space for the rest
of the session**. The server therefore *chooses* the numbering and never has to discover
Rec Room's internal ordering.

Message identity is `XXHash32(Type.FullName)` over UTF-8, seed 0:

| index | hash | type |
|---|---|---|
| 0 | `0xa5e29a7d` | `Unity.Netcode.ConnectionApprovedMessage` |
| 1 | `0xb31736b2` | `Unity.Netcode.ConnectionRequestMessage` |
| 2 | `0xb81cf6d3` | `Unity.Netcode.DisconnectReasonMessage` |
| 3 | `0x978922a6` | `Unity.Netcode.NamedMessage` |
| 4 | `0x179751e9` | `Unity.Netcode.ServerLogMessage` |
| 5 | `0xc6887f42` | `Unity.Netcode.TimeSyncMessage` |
| 6 | `0x46f5a397` | `Unity.Netcode.UnnamedMessage` |

## 5. Handshake

**Client → server, `ConnectionRequestMessage`** (id 1, version 0 in every 1.x release):

```
bitpacked int   count
count ×       { raw uint Hash ; bitpacked int Version }
raw ulong       ConfigHash
[ byte[] ConnectionData ]      only when the server has ConnectionApproval enabled
```

`ConfigHash` is `XXHash64` over, in order: `ProtocolVersion` (ushort, game-set),
the literal `NetworkConstants.PROTOCOL_VERSION`, sorted prefab hashes (only when
`ForceSamePrefabs`), `TickRate` (uint), then bools `ConnectionApproval`,
`ForceSamePrefabs`, `EnableSceneManagement`, `EnsureNetworkVariableLengthSafety`, and
`RpcHashSize`. A mismatch disconnects the client.

`PROTOCOL_VERSION` is `"15.0.0"` across **all** of NGO 1.6 → 2.0, so the config hash does
*not* identify the NGO version. It only constrains the game's own `ProtocolVersion`,
tick rate and flags — a small enough tuple to brute-force from one captured hash.

**Server → client, `ConnectionApprovedMessage`** (id 0):

```
bitpacked int   count                  ← "forbidden segment", must come first
count ×       { raw uint Hash ; bitpacked int Version }
bitpacked ulong OwnerClientId
bitpacked int   NetworkTick
raw int len + len × raw ulong          ← ConnectedClientIds, only if targetVersion >= 1
raw uint        sceneObjectCount       ← always 0 for us
```

Version discipline: serialize using the version *the client advertised* for that message.
`ConnectionApprovedMessage.Version` is `0` in NGO 1.6–1.7 and `1`
(`k_VersionAddClientIds`) from 1.8.0 onward — implement both branches and switch on the
negotiated value rather than pinning a release. Every other message in our set is
version `0` across the whole 1.x line.

## 6. Session messages

`TimeSyncMessage` — `bitpacked int Tick`, sent unreliable at tick rate.

`NamedMessage` — `raw ulong Hash` then **opaque bytes to the end of the message**.
Dissonance's own protocol rides inside those bytes; a proximity relay forwards them
without parsing. `UnnamedMessage` is the same minus the leading hash.

## 7c. Forked NGO batch format (from a decoded ConnectionRequest)

The client's NGO batching is **not stock**. Decoded from a real 2041-byte ConnectionRequest
(`docs/client-agent-request-3.md` has the capture). Layout:

```
[0:2]    magic  u16  = 0x1160
[2:4]    count  u16  = 0            (stock BatchCount field, unused/zero)
[4:8]    size   u32  = total datagram length
[8:16]   hash   u64  = XXHash64 over [24:end]   <-- NOTE: from 24, not 16
[16:24]  u64         = 1            (fork field; == message count in this sample)
[24:]    message stream
```

Message: `bitpacked type` (1 = ConnectionRequest in the client's local index),
`bitpacked size`, then body. The hash covers the message stream `[24:end]` exactly.

ConnectionRequest body (`[27:end]`, size 2014) vs stock:
```
0x81                      <- 1 byte where stock writes bitpacked version-count (7 -> 0x39)
7 × { u32 hash ; 1 byte version=0 }   version table (our 7 message hashes, all v0)
00 00 00 00 01            <- 5 bytes not present in stock, before ConfigHash
u64 ConfigHash
u32 ConnData length (1961)
ConnData = Tachyon JSON (AI/AT/VB/CKA/CIA/CPK)
```

The `0x81` preamble and the 5-byte field are in the shared "version header" (forbidden
segment), so ConnectionApproved must reproduce them. Their meaning, and the exact
ConnectionApproved serialization, are the last unknowns — see `docs/client-agent-request-3.md`.

## 7b. Live progress (real client)

- **LiteNetLib handshake: SOLVED against the real client.** With the trailing-marker layer,
  the client reaches `Connected` and holds it — Ping/Pong and MTU discovery both flow, no
  timeout. The client only disconnects (`RemoteConnectionClose`) after ~10s of no NGO approval.
- **NGO ConnectionRequest: reached.** The client sends a fragmented Channeled batch (3 LNL
  fragments, ~2041 bytes reassembled) carrying the Tachyon handshake JSON (base64 `CKA`/`CIA`/
  `CPK`, ends `=="}`).
- **Open anomaly:** that batch parses as `magic=0x1160, count=0, size=2041`, and the batch hash
  does not match a stock XXHash64 over `[16..len]`. A count of 0 with a full message payload
  means the batch header layout and/or hash range differs from stock NGO 1.6–1.14, OR the fork
  frames the connection payload non-standardly. This must be resolved before ConnectionApproved,
  because a wrong batch header means the client rejects our approval too. Use `--dump-batches`
  to capture the full bytes for offline decode.

## 7. State of the evidence

Confirmed from the capture in `traffic.txt` (decode with `tools/decode_lnl.py`):

- ProtocolId 13 → LiteNetLib 1.x.
- A 3-byte connect payload `01 00 11`, not the stock empty key.
- Target `66.228.47.217:4499`.
- MTU probing at 1024, consistent with the LNL 1.x `PossibleMtu` table.

The capture's failure mode is unambiguous and is **entirely below NGO**: 11 `ConnectRequest`s
all carrying the *same* `connectionTime` (one connect attempt, resent), each answered by a
`ConnectAccept`, pings going out with **no Pong and no MtuOk in reply**, and the ping
sequence resetting 1‑4 → 1‑4 → 1‑4 as the peer times out at `DisconnectTimeout` (~5s) and
retries. **Not one Channeled or Unreliable data packet appears**, so nothing in sections 2–6
has yet been exercised against the real client.

Whatever answered that capture accepts the connection and then stops speaking LiteNetLib.
Using a real LiteNetLib library (LiteNetLibPP) in the server — rather than hand-rolling the handshake —
makes Ping/Pong, MTU discovery and keepalive correct for free, which is exactly the failure
that capture shows.

Still open:

1. **`ConfigHash` inputs.** Capture one `ConnectionRequestMessage` and brute-force the tuple
   in §5 to recover `ProtocolVersion`, `TickRate` and the flags exactly. Nothing in the
   current capture reaches this point.
2. **Client message set and versions.** The advertised hash/version table in that request is
   a direct readout of the client's message set; the server logs it.
3. **The truncated byte.** Upstream `LiteNetLibTransport.OnNetworkReceive` hands NGO
   `UserDataSize - 1`, with the comment "the last byte sent is used to indicate the channel"
   — but the matching `Send` stopped appending that byte in commit `b32ffdd`. LNL carries the
   channel in its own header, so upstream HEAD drops a real payload byte and the batch hash
   cannot verify. Rec Room's build must diverge somewhere. The server tests both hypotheses
   per packet and reports which one matches.
4. **What the 3 connect-data bytes mean.** Not blocking, since the server accepts
   unconditionally, but more captures would settle it.

## 8. Transport-level encryption (Rec Room fork) — CORRECTED

An earlier revision of this doc argued from wire bytes that there was **no datagram-level
packet layer**. That was wrong. Client static analysis (`LiteNetLibTransport.Initialize`)
shows a `RecRoomClientEncryptLayer` is **always** installed, with
`ExtraPacketSizeForLayer = 9`. It adds 9 bytes to every outbound datagram and strips 9 from
every inbound one, **below** LiteNetLib — so it wraps the handshake, not just NGO payloads.
The clean-looking captures were app-level packet logs taken below the layer, not raw wire.

### Why this is the handshake failure

- Client strips 9 bytes off every inbound datagram before LiteNetLib parses it.
- A stock 15-byte ConnectAccept becomes 6 bytes → `NetConnectAcceptPacket.FromData` fails
  `Size != 15` → returns null → **silent ignore**. The client stays in `Outgoing`, resends
  ConnectRequest with the same `connectionTime`, and times out at ~5s. Exactly the observed loop.
- A stock server, lacking the layer, still *accepts* the client's over-long ConnectRequest
  because the 9 extra bytes fold into variable-length connect data — which is why the server
  saw "peer connected" while the client never did.
- Fix magnitude: a correct ConnectAccept is `15 + 9 = 24` bytes on the wire.

Verified in the server: a `PacketLayerBase` with `ExtraPacketSizeForLayer = 9` on both ends
completes the handshake; on the server only (stock client) it fails identically to the game.
See `src/main.cpp` (marker lambdas + `patches/`) and `--encrypt-layer`.

### The format is a trailing marker byte — SOLVED

Read from the image (see `docs/tachyon-findings.md`). The layer is variable-length; the marker
is the **last** byte of every datagram. Key-null (the whole handshake): outbound appends one
byte `0x11`; inbound strips it. Key-set: append 8-byte nonce + marker `0x2A` and encrypt.
Any other trailing marker is dropped. So a ConnectAccept must be `15 + 1 = 16` bytes on the
wire with a trailing `0x11`. Implemented in `src/main.cpp` (marker lambdas + `patches/`)
(`--encrypt-layer`) and verified end-to-end.

### Payload encryption (post-handshake)

Once `RecRoom_SetEncryptionInfo(clientId, key)` runs, the same layer encrypts NGO payloads
with a split-key AES scheme: client contributes `CKA`/`CIA`, server contributes `SessionKeyB`
(RSA-encrypted to the client's `CPK`), combined in a function at `0x8277880` (not yet read).
The handshake to establish that key is JSON in the NGO connection payload — the Tachyon
handshake, see `docs/tachyon-findings.md`.

**Key material is deliberately not committed.** It is supplied at runtime.
