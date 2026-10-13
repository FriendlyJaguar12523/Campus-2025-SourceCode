# Photon wire findings — Rec Room PC client 20250718.01 (captured 2026-09-15)

Source: pktmon capture of a real login + dorm join against Photon Cloud
(`scratchpad/cap/photon.pcapng`, decoders `enet.py` / `gp16.py` / `session.py` / `summary.py`).
Everything below is **observed**, not recalled.

## Transport (ENet-style UDP, all big-endian)

Packet header, 12 bytes: `peerId:int16, crc:byte(0), commandCount:byte, sentTime:int32, challenge:int32`.
Client → server header peerId = the id assigned in VerifyConnect (e.g. 0xF1AA); server → client header
peerId = 0.

Command header, 12 bytes: `type:byte, channel:byte, flags:byte (1 reliable, 2 unsequenced), reserved:byte(4),
length:int32 (incl. header), reliableSeq:int32`.

| type | name | payload |
|---|---|---|
| 1 | ACK | `receivedReliableSeq:int32, receivedSentTime:int32` (flags 0, seq 0, channel = acked cmd's channel) |
| 2 | CONNECT | 32 bytes: `00 00 04 b0 00 00 80 00 00 00 00 02 00 00 00 00 00 00 00 00 00 00 13 88 00 00 00 02 00 00 00 02` (mtu 1200, 2 channels) — channel 255, reliable, seq 1, header peerId -1 |
| 3 | VERIFYCONNECT | 32 bytes: assigned `peerId:int16` then `04 b0 00 00 80 00 00 00 00 02` + zeros — channel 255, seq 1 |
| 4 | DISCONNECT | none (flags 2) |
| 5 | PING | none, reliable, channel 255 — both directions, ~1/s |
| 6 | SEND_RELIABLE | message |
| 7 | SEND_UNRELIABLE | `unreliableSeq:int32` + message |
| 8 | SEND_FRAGMENT | `startSeq:int32, fragCount:int32, fragNumber:int32, totalLength:int32, fragOffset:int32` + data; reassembled = one reliable message with seq = startSeq |
| 12 | SERVERTIME | client → server only, reliable ch 255, no payload. Server just ACKs it; the client reads the server clock from the ack packet's header `sentTime`. Server never sends type 12. |

Game/master traffic uses channel 0 only. Reliable seq numbers are per channel (255 has its own).

## Message layer (inside commands 6/7/8)

`0xF3, msgType` then body. msgType `& 0x80` = encrypted (AES of the body). Types seen:
0 Init (client), 1 InitResponse (server: `f3 01 00`), 2 OperationRequest, 3 OperationResponse,
4 Event, 6 InternalOperationRequest, 7 InternalOperationResponse.

Init body: `protoMajor:byte protoMinor:byte 1e 41 06 19 00` + app name padded to 32 bytes.
- PUN (Realtime) connections send **`01 06` = GpBinary 1.6** with app name = the Realtime app id
  (or `NameServer` on the name server).
- Photon Voice/Chat connections send `01 08` (1.8). Not needed for CampusNet (voice is Tachyon).

### GpBinary 1.6 encoding (what PUN uses)
OperationRequest: `opCode:byte, paramCount:int16, (key:byte, value)*`
OperationResponse: `opCode:byte, returnCode:int16, debugMessage:value(usually '*'), paramCount:int16, params`
Event: `eventCode:byte, paramCount:int16, params`
Values: type byte then data — `*` null, `b` byte, `o` bool, `k` int16, `i` int32, `l` int64, `f` float,
`d` double, `s` string (int16 len, utf8), `x` byte[] (int32 len), `n` int[] (int32 count),
`a` string[] (int16 count), `h` Hashtable (int16 count, typed key + typed value), `D` Dictionary
(keyType:byte, valType:byte (0 = per-entry typed), int16 count), `z` object[] (int16 count, typed),
`y` typed array (int16 count, elemType:byte, elements untyped; elemType `c` = custom: `code:byte` then
per element `len:int16 bytes`), `c` custom (`code:byte, len:int16, bytes`).

## Encryption
InternalOperation 0 (InitEncryption): client sends param 1 = 96-byte DH public key; server answers
param 1 = 96-byte key (768-bit Oakley group). Client then sends the NameServer Authenticate
**encrypted** (msgType 0x82). On master/game the Authenticate REQUEST is plaintext (only the secret
token inside), Photon's responses to Authenticate/Join are encrypted — the client decrypts only when
the flag is set, so a plaintext response is fine from our own server.

## Operations / events observed (codes = standard Photon LoadBalancing)
NameServer: encrypted Authenticate (230) — response gives master address (230) + secret (221).
Master: Init(app id) → InitEncryption → Authenticate 230 {221 secret} → resp rc 0 (encrypted) →
event 226 AppStats {227 MasterPeerCount, 228 PeerCount, 229 GameCount} (unreliable) →
JoinGame 226 {255 GameId, 215 JoinMode=1 CreateIfNotExists} → resp (encrypted: game address + secret) → Disconnect.
Game: Init → InitEncryption → Authenticate 230 {221} → resp rc 0 (plaintext, no params) →
JoinGame 226 {255 GameId, 215 1, 249 PlayerProps{PlatformId, HardwareType, DeviceClass, S, prev_activity,
rm_isopinprogress, 255 NickName}, 250 Broadcast, 248 GameProps{253 IsOpen, 254 IsVisible, 250 PropsListedInLobby string[], 'BUG':1},
241, 232, 239 (bools), 191 RoomFlags=59} → resp (encrypted) + event 255 Join (encrypted).
Then: SetProperties 252 {251 props, 250 broadcast, 254 actorNr (player props), 231 expected (CAS)} →
resp 252 rc 0 + event 253 {251 props, 253 targetActor (0 = room), 254 sender};
RaiseEvent 253 {244 code, 245 data, 247 cache, 246 receiverGroup} — cache 4/5/6 used (AddToRoomCache,
AddToRoomCacheGlobal, RemoveFromRoomCache); ReceiverGroup 1 = All (server echoes to sender), absent =
Others. Event to receivers: {245 data, 254 sender actor}. Game event codes used: 22,23,24,26,27,29,31,32,33,34,36.
Room props carry the anti-cheat hash checks (`32.hshchk_gr`, `P1.hshchk_gr`, `39.tmhash`) — client-side values the server only stores.
Custom auth token = the Campus server's `photonAuthToken` (HS256 JWT, sub = account id, aud = photon room id).

## Event cache and master client (2026-09-23)

Rec Room leans on Photon's room cache: most game state goes out as `AddToRoomCacheGlobal` (5)
and is later cleared with `RemoveFromRoomCache` (6) - ev 22/29/31/33/36 in the traces. Three
server-side mistakes made late joiners (Dodgeball mid-round) replay stale round/team/spawn state:

- A global event was stored with sender **0**. Photon keeps the real sender ("global" only means
  it outlives them): it is replayed as coming from them, and a removal's actor filter matches it.
- `RemoveFromRoomCache` from a non-master could only remove its own events, so a clear run by
  anyone but the master was silently ignored. Photon lets any actor remove matching events.
  Event code 0 on a removal means "any code".
- A removal that carried a data filter was also **delivered** to the room as a normal event.
  On a removal the data is only the filter; Photon never sends it. Rec Room's clears were being
  re-run by everyone as new RPCs (thousands a day).

Master hand-off: PUN's `SetMasterClient` writes room property 248 with the current master as the
expected value. 248 is live (lowest active actor unless overridden), never stored, so the plain
CAS always refused it. The server now honours it (`Room.MasterOverride`) and broadcasts 248.

## CleanupCacheOnLeave defaults OFF (2026-09-26)

PUN only sends op param 241 / game prop 249 when RoomOptions.CleanupCacheOnLeave is TRUE, so a room created without them wants its cache kept. Rec Room creates rooms without them. CampusNet used to default to true, which deleted the room creator's cached events (the BufferedEventHelper RoomLoad state) when they left: the next joiner got 3 cached events instead of ~40 and sat on the loading screen at 25% forever ("Waiting to receive buffered message"). Default is now false; Rec Room clears departed players' events itself with RemoveFromRoomCacheForActorsLeft.

## RemoveFromRoomCacheForActorsLeft keeps global events (2026-09-27)

Global events keep their real sender (see above) so replays and RemoveFromRoomCache filters match them. That made
`RemoveFromRoomCacheForActorsLeft` (cache op 7) delete them too. Rec Room's new master sends it after a player
leaves, so when a room's creator left, the BufferedEventHelper RoomLoad slices were wiped. Those slices are all
global (cache=5).

The next joiner then replayed a handful of events instead of about 50 and hung at 25% ("Waiting to receive buffered
message"). Seen in every stuck-load case on 2026-09-26/27: 51→3, 56→4, 52→7, 51→22.

In Photon a global event belongs to the room (actor 0), so op 7 never touches it. CampusNet now removes only
non-global events of departed actors, and logs each cleanup ("cleanup for departed players: removed X of Y, kept Z
global").

## Departed senders' global events replay as actor 0 (2026-09-27)

Keeping global events after their sender left (the previous section) was not enough. They were replayed under the departed sender's actor number, and Rec Room's BufferedEventHelper only accepts buffered room-load messages from the current master or from actor 0.

Client check (RVA 0x8293b40): sender == 0 or sender == master, else "Received buffered message from non-master client [1] Current Master is [2]. Discarding...". So a joiner still hung at 25% even with all 53 cached events replayed (room-2-2-461102059, 11:11).

The join replay now sends a global event whose sender is no longer in the room as actor 0, the room, as Photon does. Events of present actors keep their real sender, so RemoveFromRoomCache sender filters still match.

## Every global event replays as actor 0 (2026-09-27, later)

Departed senders were not the only case. When a player drops, the master can hand master on to someone else while the original sender stays in the room (room-2-2-49039038: a1 -> a2). The joiner then got the room-load slices from a1, who was no longer master, and discarded all 54 ("Received buffered message from non-master client [1] Current Master is [2]"). Stuck at 25% again.

The replay now sends EVERY global (cache=5) event as actor 0, as Photon's AddToRoomCacheGlobal defines ("cached for actor 0"). The cache still stores the real sender, so RemoveFromRoomCache sender filters keep matching. Non-global (cache=4) events, such as instantiates, keep their real sender.


## A new session never inherits an old actor (2026-09-27)

Staff connections are kept through 10 min of silence (AfkExempt), so a hard-quit or restarted game leaves its old
connection in the room. The new game's normal join (JoinMode 0/1) used to take over that actor, cache and all,
so it was replayed its OWN earlier spawns: a second Maker Pen with the same view id ("NetworkView ID duplicate
found: 100029"), whose teardown threw in `MakerPen.ShowConfigFlow` on every leave. The client never finished
leaving the dorm ("Timed out waiting for stable state for action: Leave Room", FailedToSpawnPlayer 8712), so
every other room hung at "PREPARING 0%".

Only JoinMode 3 (RejoinOnly, PUN's ReconnectAndRejoin) resumes an actor now. Any other join from a user who
still has an actor removes that ghost as departed: its non-global cached events go, others get a Leave event,
and the newcomer is a new actor. If the ghost was alone, the room is reset as if it had closed.
