# Tachyon voice server — findings from client static analysis

Source: static analysis of Rec Room build `20250718.01`. Every RVA and obfuscated name below
is valid only for that build and must be re-derived by shape for any other. Facts are tagged
**[PROVEN]** (read from the image) or **[UNVERIFIED]** (hypothesis — confirm before building on it).

This file is the reference; the wire mechanics we have implemented/verified live in
`ngo-litenetlib-wire.md`. Open reads are tracked in `client-agent-request-2.md`.

## Architecture

**[PROVEN]** Dedicated server, star topology, server relays audio. The client can only ever be
a client: `NetworkManager.StartHost`/`StartServer` have zero call sites;
`LiteNetLibTransport.StartServer` is dead code; `StartClient` binds an ephemeral port and only
connects outbound. Dissonance role selection always resolves to client.

## Protocol stack

```
UDP
 └─ RecRoomClientEncryptLayer   +9 bytes/datagram, always present   [PROVEN]
     └─ LiteNetLib 1.x          ProtocolId/HeaderSize UNVERIFIED
         └─ NGO 1.6+ (trimmed)  7 message types, no objects/vars/scenes  [PROVEN]
             └─ Tachyon handshake  JSON in the NGO connection payload
                 └─ Dissonance 8.x  over NGO named/unnamed messages
```

## LiteNetLib layer (task doc section 4)

**[PROVEN]** Bundled inside `Netcode.Transports.LiteNetLib.dll`, name-obfuscated except
`LiteNetLibTransport`. `NetConnectAcceptPacket` matches stock exactly: `Size == 15`,
connectionNumber at `[9]` (reject `>= 4`), isReused at `[10]` (reject `> 1`), peerId at
`[11..14]` (reject negative). `ProcessConnectAccept` matches stock: requires state `Outgoing`
and `ConnectionTime == peer.ConnectTime`.

**[UNVERIFIED]** `NetConstants.ProtocolId` and `NetConnectRequestPacket.HeaderSize` — live in
metadata `fieldDefaultValues`, not yet walked. Our capture pins ProtocolId to **13** from the
wire, but that is not yet confirmed from the image. The 3 connect-data bytes `01 00 11` were
not traced to their writer.

## Encrypt layer (task doc section 3) — SOLVED

`RecRoomClientEncryptLayer` (`MGHAOMEMAHF`), always installed. **[PROVEN, read from image]** It
is a variable-length **trailing-marker** scheme — `ExtraPacketSizeForLayer = 9` is the
worst-case reservation, not the bytes added. The marker is always the LAST byte.

Method map (the task doc had inbound/outbound swapped — corrected):

| RVA | method |
|---|---|
| `0x735DC20` | `ProcessOutBoundPacket` |
| `0x735D760` | `ProcessInboundPacket` |
| `0x735DF90` | `SetKey` (copies exactly 32 bytes, AES-256 sized) |
| `0x735E140` | `ClearKey` (nulls key on disconnect) |

Outbound: `key == null` → append one byte `0x11`, `length += 1`. `key != null` → append 8-byte
RNG nonce + marker `0x2A` (`length += 9`) and encrypt the payload. No branch on packet property.

Inbound: read last byte. `0x11` → strip 1 (no key needed). `0x2A` → decrypt, needs key,
requires `length > 9`, strip 9. Any other marker → **drop** (this is why our earlier
`--accept-extra 00` sweep silently failed — `0x00` is an unknown marker).

**The whole LiteNetLib + NGO handshake runs key == null, so plaintext `0x11` is all that is
needed to reach NGO.** Implemented and verified end-to-end in
`src/main.cpp` (marker lambdas + `patches/`) (`--encrypt-layer`): the ConnectAccept goes out as 16
bytes (15 + `0x11`), the client accepts, and marker-framed NGO data flows.

**[UNVERIFIED]** The encrypted-path cipher. The transform (`0x73653E0`) is Burst-compiled and
dispatches through function pointers; the 32-byte key does not by itself prove AES. Not needed
for the handshake. It only matters if the session actually sets a key — which the Tachyon
approval flow controls, so keeping the client key-null (plaintext `0x11` throughout) is the
path worth checking before touching the cipher.

**[PROVEN deduction]** The connect-data bytes we saw as `01 00 11` are really `01 00` (2 bytes)
plus the layer's trailing `0x11` marker.

## NGO layer (task doc section 5)

**[PROVEN]** Trimmed to 7 message types, no NetworkObject/Behaviour/Variable/spawn/scene. We do
not reimplement NGO — just handshake, time sync, and a named/unnamed relay. NGO type names are
not obfuscated. Rec Room forked `NetworkManager` with `RecRoom_ConnectionInfo` get/set.

**[UNVERIFIED]** Hypothesis: `RecRoom_ConnectionInfo` carries the approval JSON to the client,
since stock `ConnectionApprovedMessage` has no arbitrary payload. Accessors are shared stubs —
call-xref is unreliable here.

## Tachyon handshake (task doc section 6)

Session wrapper `BAOFAOBLAMJ` = `TachyonClient`, `RecRoom.Networking.PhotonImpl.Runtime`.

**[PROVEN] Client to server** (NGO connection-request payload), `HCNIOOINPCA` `0x82786B0`,
JSON type `PAFCNOLGBNM`, all keys `[JsonProperty]` `Required.Always`, `byte[]` as base64:

| key | member | type |
|---|---|---|
| `AI` | AccountId | string |
| `AT` | AccessToken | byte[] |
| `VB` | VerificationBlob | byte[] |
| `CKA` | ClientKeyA | byte[] |
| `CIA` | ClientIVA | byte[] |
| `CPK` | ClientPublicKey | byte[] (RSA CspBlob) |

**[PROVEN] Server to client** (approval payload), `CDJMICBEALM` =
`NetworkManager_OnClientConnectedCallback` `0x8277880`, JSON type `PMFAAFNFMAL`:

| key | member | type | note |
|---|---|---|---|
| `DR` | Reason | string | `Required.Always`; **must equal `"Carrot"`** |
| `KT` | SessionKeyB | byte[] | `Required.Default`; RSA-encrypted to the client's `CPK` |

Split-key: transport key = f(client `CKA`/`CIA`, server `SessionKeyB`). **[UNVERIFIED]** the
combination function — read `0x8277880`.

**[PROVEN] The RSA blocker.** `DMIBBCKIGCG` (`.cctor` `0x827A820`) holds a hardcoded 1024-bit
RSA **public** key; the client encrypts `AT`/`VB`/`CKA`/`CIA` to it. A working server needs the
matching **private** key, which is Rec Room's and not present in the client. Recommended route:
generate our own 1024-bit keypair and **patch the embedded modulus literal** in the client
(plain string in `.data`), coordinated with the client patcher so both ship the same keypair.
Key material and the RSA modulus are NOT committed to this repo — supplied at runtime.

## CPK is opaque — the RSA blocker still stands

The client-agent's "you can mint KT without Rec Room's private key" does **not** hold against
the capture. The `CPK` in the request JSON is 160 bytes of high-entropy data: no
`PUBLICKEYBLOB` header (`0x06 0x02`), no `RSA1` magic, and an even low byte — so it is not a
plaintext RSA modulus or CspBlob. **CPK is itself RSA-encrypted** (to Rec Room's key, like
AT/VB/CKA/CIA). A third-party server cannot read it, so it cannot encrypt a `KT` the client
will decrypt. The `KT`-minting path is therefore not viable without Rec Room's private key.

Consequence: to run voice, the client-patch route is the real option — neuter the connected
callback's encryption setup / the layer's `SetKey` so the transport key stays null and the
layer keeps emitting plaintext `0x11`. This is independent of reaching NGO "Connected", which
does not need a valid `KT` at all (ConnectionApproved.Deserialize does no validation).

## NGO handshake: implemented

The server now speaks the fork batch format (`--fork`) and sends a ConnectionApproved:
24-byte header (`BatchCount` at offset 16, hash `XXHash64` over `[24:]`), message type 0
(ConnectionApproved in the client's pre-approval local order), the client's 8-entry version
header echoed verbatim, `OwnerClientId`, `NetworkTick`, and a length-prefixed
`{"DR":"Carrot"}` payload. Verified offline against the real captured request: the fork parser
confirms the batch hash and our reply round-trips. `KT` is omitted (CPK opaque), so the
client's post-connect callback will throw on `RSA.Decrypt` *after* the Connected event — which
is the expected boundary of what a patch-free server can reach.

## Dissonance (task doc section 7)

**[PROVEN — live]** After NGO Connected, the real client sends a Dissonance handshake as an NGO
`NamedMessage` (our message index 3), plaintext (marker `0x11`), and **repeats it every ~4s
because the server does not answer**:

```
NamedMessage hash = 0xc78b0000000f0001
payload (13 bytes) = 04 01 00 00 03 c0 00 00 bb 80 00 02 32
```

`0x04` looks like a Dissonance message-type byte; `03c0` = 960 and `bb80` = 48000 decode as the
Opus frame size and sample rate, so this is the client advertising its audio config and waiting
for a server handshake reply. This is the next layer to implement — see
`docs/client-agent-request-4.md`. For a proximity relay the *voice* packets can still be
forwarded opaque; only the session handshake needs server logic.

**[PROVEN]** `NfgoCommsNetwork` present, role from NGO client flags; integration rewired onto
raw named/unnamed messages (no NetworkBehaviour). Derive the handshake reply from
`NfgoCommsNetwork` methods, `DissonanceNetworkManagerInjector` `0x826A370`, and `DissonanceVoip.dll`.

## MILESTONE (2026-08-21): full handshake works, plaintext, no client patch

The server (`reference`, `--encrypt-layer --fork`) completes the entire stack against the
real client: LiteNetLib + encrypt layer + forked NGO + Tachyon `"Carrot"` approval. The game
**connects, does not crash, and reports "connected to voice."** Traffic is plaintext `0x11` —
omitting `KT` left the client key-null (its post-connect RSA callback is caught, non-fatal), so
**no client patch is needed** for plaintext voice. What remains is the Dissonance session
protocol above, then multi-client relay.

## Matchmaking DTO (task doc section 8)

**[PROVEN]** `GET https://match.<host>/player/connection-info?roomInstanceId={long}` returns a
bare 9-key DTO including `VoiceConnectionInfo` (`"<ip>:<port>"`) and `VoiceServerId`. Client
advertises `VoiceServerVersion` in the `matchmake/v2/room` body; server returns the allocated
voice server here. Empty strings for both voice fields = "no voice", definitely accepted — so
voice can ship dark while the handshake is brought up.

## Order of work (from the task doc)

1. Read the encrypt-layer 9-byte format + null-key path (`client-agent-request-2.md`). **now.**
2. Pin ProtocolId / HeaderSize from `fieldDefaultValues`.
3. Confirm how the approval JSON reaches the client (`RecRoom_ConnectionInfo`?).
4. Read the key-combination function at `0x8277880`.
5. Client to `Connected`: LiteNetLib + NGO ConnectionRequest/Approved + `"Carrot"`.
6. Time sync, then the Dissonance relay.

Steps 1–4 are each a bounded read that silently invalidates the implementation if guessed —
land them as written findings before server code.
