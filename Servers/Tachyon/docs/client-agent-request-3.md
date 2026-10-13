# Request #3: the forked NGO batch/message framing and ConnectionApproved

The LiteNetLib layer is solved and the client now completes the transport handshake against
our server and sends its NGO ConnectionRequest. We decoded it and confirmed the Tachyon JSON
(`AI/AT/VB/CKA/CIA/CPK`). But the fork changed NGO's batch and message framing, so we cannot
send a ConnectionApproved the client will accept without the exact serialization.

## What we already worked out from the wire (for your cross-check)

Decoded from a real 2041-byte ConnectionRequest batch:

```
[0:2]    magic  u16 = 0x1160
[2:4]    count  u16 = 0                     (stock BatchCount, sent as 0)
[4:8]    size   u32 = 2041                  (total datagram length)
[8:16]   hash   u64 = 0x3faa53cee8c5e338    (= XXHash64 over bytes [24:2041], VERIFIED)
[16:24]  u64        = 1                      (fork field; equals message count here)
[24]     bitpacked  = 1                      (message type: ConnectionRequest, client-local idx)
[25:27]  bitpacked  = 2014                   (message size)
[27]     0x81                                (version-header preamble byte)
[28:63]  7 × { u32 hash ; 1 byte version=0 }
[63:68]  00 00 00 00 01                      (5 bytes, not in stock NGO)
[68:76]  u64 ConfigHash
[76:80]  u32 = 1961  (ConnData length)
[80:2041] Tachyon JSON
```

So the batch hash covers `[24:end]` (message stream), NOT stock's `[16:alignedLength]`, and
the batch is NOT padded to 8 bytes. Two message-body bytes are unexplained: the `0x81`
preamble, and the `00 00 00 00 01`.

## Questions (in priority order)

**Q1 — forked batch header & hash, in `NetworkMessageManager`.**
Confirm the header layout above. Specifically:
- Is the `u64` at `[16:24]` the message count, a tick, or something else? What writes it?
- Confirm `BatchHash = XXHash64(buffer + 24, size - 24)` (or give the real range/seed).
- **Does the RECEIVER validate `BatchHash` and reject on mismatch, or log-and-continue?**
  This decides whether our outbound hash must be exact.
- Does the receiver iterate messages by the `u64` count, by `BatchCount`, or by position?

**Q2 — the version-header preamble.**
In the forked `ConnectionRequestMessage.Serialize` (and the shared version-header code), what
is written before the version table? Stock writes `BytePacker.WriteValueBitPacked(N)` (N=7 ->
`0x39`). We see a single `0x81`. And what is the `00 00 00 00 01` written AFTER the table,
before `ConfigHash`? Give the exact field sequence of the forbidden segment as this build
serializes it.

**Q3 — `ConnectionApprovedMessage` serialization (the thing we must send).**
Full `Serialize` (server→client) for this build. We need every field and its encoding, in
order: the version header (same preamble as Q2?), `OwnerClientId`, `NetworkTick`, any
`ConnectedClientIds`, `sceneObjectCount`, and anything the fork added. Also the message
**type index** the client expects for ConnectionApproved in the pre-approval (local) space,
and how `Deserialize` reads it — because the client's `Deserialize` is what accepts or rejects
our packet.

**Q4 — how the approval reaches Tachyon.**
Earlier finding: `NetworkManager.RecRoom_ConnectionInfo` is a fork addition, hypothesised to
carry the approval JSON (`PMFAAFNFMAL`, the `"Carrot"` / `SessionKeyB` blob). Confirm whether
the approval JSON rides inside `ConnectionApprovedMessage`'s ConnectionData, in
`RecRoom_ConnectionInfo`, or elsewhere — i.e. where in the server→client bytes we must place
`{"DR":"Carrot",...}`.

**Q5 — key-null path for the session.**
Does the client require a non-null `SessionKeyB` to proceed, or will it accept an approval with
`"Carrot"` and no key and stay plaintext (marker `0x11`)? This decides whether we can defer the
RSA keypair patch and run voice unencrypted.

## Output

Decompiled `Serialize`/`Deserialize` bodies where possible, else exact field order + encodings
+ byte offsets. Distinguish read from inferred. If a field genuinely is not present, say so —
do not fill it from stock NGO; the fork has already diverged in three places above.
